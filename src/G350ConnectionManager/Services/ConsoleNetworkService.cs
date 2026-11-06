using Renci.SshNet;

namespace G350ConnectionManager.Services;

/// <summary>
/// Asks the console to re-apply its network settings after the PC's address changes.
///
/// Background: turning Windows ICS on moves the PC adapter from 192.168.7.1 to
/// 192.168.137.1, so the console's default route is left pointing at a gateway
/// that no longer exists. The console's own USB_Network_Manager.sh already knows
/// how to fix this — it adds both 192.168.7.2 and 192.168.137.2 to usb0 and
/// probes for 192.168.137.1 — but that logic only ran when the user pressed
/// [A] Start Services on the device.
///
/// The script now exposes <c>--refresh-net</c>, which performs exactly that
/// address+route step without touching sshd or the g_ether gadget. That matters:
/// the script's normal stop/start path unloads g_ether and flushes usb0, which
/// would destroy the SSH session used to issue the request.
///
/// See docs/CONSOLE_CONTROL_INTERFACE.md in the G350Wired repository.
/// </summary>
public sealed class ConsoleNetworkService
{
    private const string User = "ark";
    private const string Password = "ark";

    /// <summary>Path the boot TUI actually runs from.</summary>
    private const string ScriptPath = "/opt/system/Tools/USB_Network_Manager.sh";

    /// <summary>
    /// Runs <c>--refresh-net</c> on the console. Returns (ok, gateway, message);
    /// gateway is null when the console did not report one.
    /// </summary>
    /// <summary>Both addresses the console can answer on.</summary>
    private static readonly string[] CandidateHosts = { "192.168.137.2", "192.168.7.2" };

    /// <summary>
    /// Runs <c>--refresh-net</c>, trying every address the console may be on.
    ///
    /// A single address is not enough: the whole point of the refresh is that the
    /// PC's subnet just changed, so the address that worked a moment ago is often
    /// the wrong one now. With ICS off the PC leaves 192.168.137.x entirely and
    /// .137.2 stops answering.
    /// </summary>
    public Task<(bool Ok, string? Gateway, string Message)> RefreshNetworkAsync(string host)
        => Task.Run(() => RefreshNetwork(host));

    private static (bool Ok, string? Gateway, string Message) RefreshNetwork(string host)
    {
        var tried = new List<string>();
        string? lastError = null;

        // Prefer the address the app currently believes in, then the other one.
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(host)) order.Add(host);
        foreach (var candidate in CandidateHosts)
            if (!order.Contains(candidate)) order.Add(candidate);

        foreach (var target in order)
        {
            var (ok, gateway, message) = TryRefresh(target, out var unreachable);
            if (ok) return (true, gateway, message);

            tried.Add(target);
            lastError = message;

            // Only fall through to the next address when the console simply was
            // not there. A reachable console that rejected the command is a real
            // failure and must be reported as such.
            if (!unreachable) return (false, null, message);
        }

        return (false, null,
            $"Could not reach the console on {string.Join(" or ", tried)}. "
            + (lastError ?? string.Empty));
    }

    private static (bool Ok, string? Gateway, string Message) TryRefresh(
        string host, out bool unreachable)
    {
        unreachable = false;

        try
        {
            using var client = new SshClient(host, User, Password);
            client.Connect();

            var result = Run(client, $"bash {ScriptPath} --refresh-net");
            client.Disconnect();

            foreach (var line in result.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("gateway=", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Format: gateway=<ip> ip=<iface>
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var gateway = parts[0]["gateway=".Length..];

                if (gateway is "unknown" or "")
                    return (false, null, "The console re-applied its settings but reported no gateway.");

                return (true, gateway, $"Console is now using gateway {gateway}.");
            }

            return (false, null,
                "The console did not recognise the refresh command. "
                + "Update USB_Network_Manager.sh on the device (see docs/CONSOLE_CONTROL_INTERFACE.md).");
        }
        catch (Exception ex)
        {
            // Any connection-level failure means "not there right now"; try the
            // other candidate address before giving up.
            unreachable = true;
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// True when the console can actually reach the internet.
    ///
    /// Tries every candidate address, because the caller's idea of where the
    /// console lives is frequently one step out of date -- that is precisely the
    /// situation a refresh is recovering from. Checking only the stale address
    /// reported "no internet" for a console that was already online.
    /// </summary>
    public Task<bool> HasInternetAsync(string host) => Task.Run(() => HasInternet(host));

    private static bool HasInternet(string host)
    {
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(host)) order.Add(host);
        foreach (var candidate in CandidateHosts)
            if (!order.Contains(candidate)) order.Add(candidate);

        foreach (var target in order)
        {
            try
            {
                using var client = new SshClient(target, User, Password);
                client.Connect();
                var result = Run(client,
                    "timeout 5 ping -c1 -W2 1.1.1.1 >/dev/null 2>&1 && echo OK || echo FAIL");
                client.Disconnect();

                // A reachable console that reports FAIL is a real answer; stop.
                return result.Contains("OK", StringComparison.Ordinal);
            }
            catch
            {
                // Could not connect to this address; try the other one.
            }
        }

        return false;
    }

    private static string Run(SshClient client, string command)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(25);
        return cmd.Execute() ?? string.Empty;
    }
}
