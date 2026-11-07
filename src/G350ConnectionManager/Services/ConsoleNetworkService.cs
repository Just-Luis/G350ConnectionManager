using Renci.SshNet;

using G350ConnectionManager.Models;

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
/// The script exposes <c>--start-services</c>, which does exactly what pressing
/// [A] on the device does: re-address usb0, re-pick the gateway, restart sshd and
/// samba, rewrite resolv.conf. It is safe to run over the SSH session that asked
/// for it because it never unloads the g_ether gadget, so usb0 keeps its
/// addresses throughout -- at worst a running command is interrupted.
///
/// See docs/CONSOLE_CONTROL_INTERFACE.md in the G350Wired repository.
/// </summary>
public sealed class ConsoleNetworkService
{
    private static string User => ConsoleCredentials.User;
    private static string Password => ConsoleCredentials.Password;

    /// <summary>Path the boot TUI actually runs from.</summary>
    private const string ScriptPath = "/opt/system/Tools/USB_Network_Manager.sh";

    /// <summary>Both addresses the console can answer on.</summary>
    private static readonly string[] CandidateHosts = { "192.168.137.2", "192.168.7.2" };

    /// <summary>
    /// Returns the addresses that actually answer right now.
    ///
    /// This exists because SSH.NET's default connect timeout is ~21 seconds, so
    /// one dead candidate made every refresh feel like it had hung. A TCP probe
    /// rejects a dead address in well under a second, and only the live ones are
    /// handed to SSH.
    /// </summary>
    private static List<string> LiveHosts(string? preferred = null)
    {
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) order.Add(preferred);
        foreach (var candidate in CandidateHosts)
            if (!order.Contains(candidate)) order.Add(candidate);

        var live = new List<string>();
        foreach (var host in order)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connect = tcp.ConnectAsync(host, 22);
                if (connect.Wait(TimeSpan.FromMilliseconds(700)) && tcp.Connected)
                    live.Add(host);
            }
            catch
            {
                // Not there; try the next candidate.
            }
        }

        return live;
    }

    private static SshClient Connect(string host)
    {
        var client = new SshClient(host, User, Password)
        {
            // Belt and braces: even a host that passed the TCP probe should not
            // be allowed to stall the whole operation.
            ConnectionInfo = { Timeout = TimeSpan.FromSeconds(5) }
        };
        client.Connect();
        return client;
    }

    /// <summary>
    /// Runs <c>--start-services</c> on the console. Returns (ok, gateway, message);
    /// gateway is null when the console did not report one.
    ///
    /// <paramref name="host"/> is a hint, not a requirement: the port move that
    /// prompted the refresh usually invalidated it, so every candidate address is
    /// tried.
    /// </summary>
    public Task<(bool Ok, string? Gateway, string Message)> RefreshNetworkAsync(string host)
        => Task.Run(() => RefreshNetwork(host));

    private static (bool Ok, string? Gateway, string Message) RefreshNetwork(string host)
    {
        var tried = new List<string>();
        string? lastError = null;

        // Only talk to addresses that answer; a dead candidate would otherwise
        // burn ~21 seconds in the SSH connect.
        var order = LiveHosts(host);
        if (order.Count == 0)
            return (false, null,
                $"Could not reach the console on {string.Join(" or ", CandidateHosts)}. "
                + "Check the cable and that services are running.");

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
            using var client = Connect(host);

            var result = Run(client, $"bash {ScriptPath} --start-services");
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
                "The console did not recognise the command. Update "
                + "USB_Network_Manager.sh on the device to v1.6.0 or later "
                + "(see docs/CONSOLE_CONTROL_INTERFACE.md).");
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
        foreach (var target in LiveHosts(host))
        {
            try
            {
                using var client = Connect(target);
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
