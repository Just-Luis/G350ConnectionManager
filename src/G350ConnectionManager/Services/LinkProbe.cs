using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Services;

/// <summary>
/// Read-only diagnostics. Every method here observes the system; none of them
/// change adapter configuration, routes, services or sharing state.
///
/// Probing is staged so the UI can show *where* it failed:
///   1. USB adapter present
///   2. Local link (carrier) up
///   3. Console answers ICMP on either candidate IP
///   4. TCP/22 listening  (= console-side services running)
///   5. Internet reachable (only meaningful once sharing is on)
/// </summary>
public sealed class LinkProbe
{
    /// <summary>
    /// The console answers on 192.168.137.2 when Windows ICS is enabled, and
    /// 192.168.7.2 when it is not. Both are tried, matching the existing .bat tools.
    /// </summary>
    public static readonly string[] ConsoleCandidates = { "192.168.137.2", "192.168.7.2" };

    public const int SshPort = 22;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromMilliseconds(900);

    // ---------------------------------------------------------------- probes

    /// <summary>ICMP echo. Returns the round-trip time in ms, or null on failure.</summary>
    public static async Task<int?> PingAsync(string host, int timeoutMs = 900, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(ct);
            if (reply.Status == IPStatus.Success)
                return (int)reply.RoundtripTime;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when something accepts a TCP connection on host:port.</summary>
    public static async Task<bool> TcpOpenAsync(string host, int port, int timeoutMs = 900, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------ full sweep

    /// <summary>
    /// Runs the full staged diagnostic sweep and returns an immutable snapshot.
    /// Never throws: failures become Failed probe results.
    /// </summary>
    public static async Task<LinkSnapshot> RunAsync(CancellationToken ct = default)
    {
        var stages = new List<ProbeResult>(6);
        var adapters = AdapterEnumerator.All();
        var adapter = AdapterEnumerator.FindConsoleAdapter(adapters);

        // --- Stage 1: USB adapter present -------------------------------------
        if (adapter is null)
        {
            stages.Add(new ProbeResult(ProbeState.Failed,
                "No USB Ethernet adapter found — is the console connected and in PC mode?"));
        }
        else
        {
            stages.Add(new ProbeResult(ProbeState.Ok,
                $"{adapter.DisplayName} ({adapter.Name})"));
        }

        // --- Stage 2: link up -------------------------------------------------
        var linkUp = adapter?.IsUp == true;
        if (adapter is null)
            stages.Add(ProbeResult.Skipped);
        else if (linkUp)
            stages.Add(new ProbeResult(ProbeState.Ok, "Cable link up"));
        else
            stages.Add(new ProbeResult(ProbeState.Failed, "Adapter present but link is down"));

        // --- Stage 3: console answers ping -----------------------------------
        string? consoleIp = null;
        int? pingMs = null;

        if (linkUp)
        {
            foreach (var candidate in ConsoleCandidates)
            {
                if (ct.IsCancellationRequested) break;
                var ms = await PingAsync(candidate, (int)PingTimeout.TotalMilliseconds, ct);
                if (ms is not null)
                {
                    consoleIp = candidate;
                    pingMs = ms;
                    break;
                }
            }
        }

        if (pingMs is not null)
            stages.Add(new ProbeResult(ProbeState.Ok, $"Console answers at {consoleIp}", pingMs));
        else if (linkUp)
            stages.Add(new ProbeResult(ProbeState.Failed,
                "No response on " + string.Join(" or ", ConsoleCandidates)
                + " — start services on the console"));
        else
            stages.Add(ProbeResult.Skipped);

        // --- Stage 4: SSH listening ------------------------------------------
        var sshOk = false;
        if (consoleIp is not null)
        {
            sshOk = await TcpOpenAsync(consoleIp, SshPort, (int)TcpTimeout.TotalMilliseconds, ct);
            stages.Add(sshOk
                ? new ProbeResult(ProbeState.Ok, $"SSH / SFTP reachable on port {SshPort}")
                : new ProbeResult(ProbeState.Warning,
                    $"Console responds but port {SshPort} is closed — press [A] on the console"));
        }
        else
        {
            stages.Add(ProbeResult.Skipped);
        }

        // --- Stage 5: internet -------------------------------------------------
        //
        // Asks whether THIS PC can reach the internet, not whether the console
        // adapter has a gateway. Under ICS that adapter is the sharing *host* and
        // deliberately has no gateway of its own, so keying off adapter.Gateway
        // reported "sharing is off" precisely when sharing was on.
        var internetOk = await PingAsync("8.8.8.8", 1200, ct) is not null;

        stages.Add(internetOk
            ? new ProbeResult(ProbeState.Ok, "This PC has internet")
            : new ProbeResult(ProbeState.Warning,
                "This PC has no internet — check Wi-Fi or Ethernet"));

        var gateway = adapter?.Gateway;

        return new LinkSnapshot
        {
            ConsoleIp = consoleIp,
            PingOk = pingMs is not null,
            PingMs = pingMs,
            SshOk = sshOk,
            Adapter = adapter,
            LinkUp = linkUp,
            GatewayAddress = gateway,
            InternetSharingActive = adapter?.Ipv4?.StartsWith("192.168.137.") == true,
            InternetReachable = internetOk,
            Stages = stages,
            Timestamp = DateTime.Now
        };
    }
}
