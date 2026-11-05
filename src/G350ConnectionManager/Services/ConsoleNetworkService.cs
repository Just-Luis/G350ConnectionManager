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
    public Task<(bool Ok, string? Gateway, string Message)> RefreshNetworkAsync(string host)
        => Task.Run(() => RefreshNetwork(host));

    private static (bool Ok, string? Gateway, string Message) RefreshNetwork(string host)
    {
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
            return (false, null, $"Could not reach the console to refresh its network: {ex.Message}");
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
        cmd.CommandTimeout = TimeSpan.FromSeconds(25);
        return cmd.Execute() ?? string.Empty;
    }
}
