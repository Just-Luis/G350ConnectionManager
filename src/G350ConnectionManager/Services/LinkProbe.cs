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
///
/// Stage 1 needs a second opinion from the operating system. A network adapter
/// that Windows has administratively disabled does not appear in
/// <see cref="NetworkInterface.GetAllNetworkInterfaces"/> at all - verified on
/// hardware - so enumerating through .NET alone reports "no adapter", which is
/// indistinguishable from "nothing is plugged in". The real answer is usually
/// that the adapter exists and is disabled, so that case is detected separately.
/// </summary>
public sealed class LinkProbe
{
    /// <summary>
    /// The console answers on 192.168.137.2 when Windows ICS is enabled, and
    /// 192.168.7.2 when it is not. Both are tried, matching the existing .bat tools.
    /// </summary>
    private static readonly string[] ConsoleCandidates = { "192.168.137.2", "192.168.7.2" };

    private const int SshPort = 22;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromMilliseconds(900);

    // ---------------------------------------------------------------- probes

    /// <summary>ICMP echo. Returns the round-trip time in ms, or null on failure.</summary>
    private static async Task<int?> PingAsync(string host, int timeoutMs = 900, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(ct);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when something accepts a TCP connection on host:port.</summary>
    private static async Task<bool> TcpOpenAsync(string host, int port, int timeoutMs = 900, CancellationToken ct = default)
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

    /// <summary>
    /// True when Windows knows about a console adapter that is administratively
    /// disabled.
    ///
    /// Such an adapter is invisible to <see cref="NetworkInterface"/>, so without
    /// this check the app reports "no USB adapter found" and sends the user off to
    /// check the cable, the console's power and its USB mode - none of which is
    /// the problem.
    ///
    /// Detection only. Enabling a network adapter changes machine-wide state, so
    /// it stays a decision the user makes, not something a status probe does.
    /// </summary>
    private static bool HasDisabledConsoleAdapter()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command "
                          + "\"@(Get-NetAdapter -ErrorAction SilentlyContinue | "
                          + "Where-Object { $_.InterfaceDescription -like '*Remote NDIS*' "
                          + "-and $_.Status -eq 'Disabled' }).Count\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var text = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(4000);

            return int.TryParse(text, out var count) && count > 0;
        }
        catch
        {
            // A failed probe must not change the diagnostic's verdict.
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
        if (adapter is not null)
        {
            stages.Add(new ProbeResult(ProbeState.Ok,
                $"{adapter.DisplayName} ({adapter.Name})"));
        }
        else if (HasDisabledConsoleAdapter())
        {
            stages.Add(new ProbeResult(ProbeState.Failed,
                "The console's USB adapter is disabled in Windows. Enable it in "
                + "Network Connections (ncpa.cpl) or Device Manager."));
        }
        else
        {
            stages.Add(new ProbeResult(ProbeState.Failed,
                "No USB Ethernet adapter found — is the console connected and in PC mode?"));
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

        return new LinkSnapshot
        {
            ConsoleIp = consoleIp,
            PingOk = pingMs is not null,
            PingMs = pingMs,
            SshOk = sshOk,
            Adapter = adapter,
            InternetSharingActive = adapter?.Ipv4?.StartsWith("192.168.137.") == true,
            InternetReachable = internetOk,
            Stages = stages,
            Timestamp = DateTime.Now
        };
    }
}
