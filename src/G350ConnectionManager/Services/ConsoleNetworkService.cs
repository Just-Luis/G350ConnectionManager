using Renci.SshNet;

namespace G350ConnectionManager.Services;

/// <summary>
/// Repairs the console's default route after ICS moves the PC adapter.
///
/// Measured behaviour, and the reason this type exists:
///
/// The console runs a USB-network script that configures its side statically:
/// <c>usb0</c> keeps 192.168.7.2 and a default route via 192.168.7.1. When ICS
/// is enabled, Windows takes the PC adapter from 192.168.7.1 to 192.168.137.1
/// and hands the console a DHCP lease at 192.168.137.2 — but the console *also*
/// keeps the old 192.168.7.2 address, so its default route still points at a
/// gateway that no longer exists. Internet then fails while SSH keeps working,
/// which is a confusing combination.
///
/// Verified on hardware: with the route at .7.1, ICMP to 1.1.1.1 fails; after
/// repointing it to .137.1, ICMP and HTTPS both succeed. Repointing it back
/// breaks it again, so this is causal rather than coincidental.
///
/// The fix is a single route replace on the console, and it is reverted when
/// sharing is turned off so the console returns to its documented .7.x setup.
/// </summary>
public sealed class ConsoleNetworkService
{
    public const string ConsoleAddress = "192.168.7.2";
    public const string IcsGateway = "192.168.137.1";
    public const string ManualGateway = "192.168.7.1";

    private const string User = "ark";
    private const string Password = "ark";

    /// <summary>Address the console should reach the PC on, given ICS state.</summary>
    public static string ExpectedGateway(bool sharingActive) =>
        sharingActive ? IcsGateway : ManualGateway;

    /// <summary>
    /// Points the console's default route at the gateway matching the current
    /// sharing state. Returns (ok, message).
    /// </summary>
    public Task<(bool Ok, string Message)> AlignDefaultRouteAsync(
        string host, bool sharingActive)
    {
        // SSH.NET is synchronous; run it off the UI thread so a slow console
        // cannot freeze the window.
        return Task.Run(() => AlignDefaultRoute(host, sharingActive));
    }

    private static (bool Ok, string Message) AlignDefaultRoute(string host, bool sharingActive)
    {
        var gateway = ExpectedGateway(sharingActive);

        try
        {
            using var client = new SshClient(host, User, Password);
            client.Connect();

            // Refuse to "fix" a route that is already correct; a needless
            // replace produces console output we would have to explain.
            var current = Run(client, "ip route show default");
            if (current.Contains($"via {gateway} ", StringComparison.Ordinal))
            {
                client.Disconnect();
                return (true, $"Console already routes via {gateway}.");
            }

            var cmd = $"echo {Password} | sudo -S -p '' ip route replace default via {gateway} dev usb0";
            Run(client, cmd);

            var after = Run(client, "ip route show default");
            client.Disconnect();

            return after.Contains($"via {gateway} ", StringComparison.Ordinal)
                ? (true, $"Console now routes via {gateway}.")
                : (false, $"The console did not accept the route change ({(after.Length == 0 ? "no output" : after.Trim())}).");
        }
        catch (Exception ex)
        {
            return (false, $"Could not reach the console: {ex.Message}");
        }
    }

    /// <summary>True when the console can actually reach the internet.</summary>
    public Task<bool> HasInternetAsync(string host) => Task.Run(() => HasInternet(host));

    private static bool HasInternet(string host)
    {
        try
        {
            using var client = new SshClient(host, User, Password);
            client.Connect();
            var result = Run(client, "timeout 5 ping -c1 -W2 1.1.1.1 >/dev/null 2>&1 && echo OK || echo FAIL");
            client.Disconnect();
            return result.Contains("OK", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Run(SshClient client, string command)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(20);
        var output = cmd.Execute();
        return output ?? string.Empty;
    }
}
