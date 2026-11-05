using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace G350ConnectionManager.Models;

/// <summary>Result of one diagnostic probe stage.</summary>
public enum ProbeState
{
    Unknown,
    Ok,
    Warning,
    Failed,
    Skipped
}

public sealed record ProbeResult(ProbeState State, string Message, int? LatencyMs = null)
{
    public static readonly ProbeResult Unknown = new(ProbeState.Unknown, "Not tested yet");
    public static readonly ProbeResult Skipped = new(ProbeState.Skipped, "Skipped");
}

/// <summary>
/// A network interface that could be the G350's USB link.
/// </summary>
public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    string? Ipv4,
    string? Gateway,
    bool IsUp,
    bool IsRndis)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;
}

/// <summary>
/// Snapshot of everything the UI needs to render connection status.
/// Immutable: a new instance is produced for each probe cycle.
/// </summary>
public sealed class LinkSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    // Console
    public string? ConsoleIp { get; init; }
    public bool PingOk { get; init; }
    public int? PingMs { get; init; }
    public bool SshOk { get; init; }

    // PC side
    public AdapterInfo? Adapter { get; init; }
    public bool AdapterFound => Adapter is not null;
    public bool LinkUp { get; init; }

    // Sharing
    public string? GatewayAddress { get; init; }
    public bool InternetSharingActive { get; init; }
    public bool InternetReachable { get; init; }

    public IReadOnlyList<ProbeResult> Stages { get; init; } = Array.Empty<ProbeResult>();

    /// <summary>True when the console answers on both ICMP and TCP/22.</summary>
    public bool IsReady => PingOk && SshOk;

    public string Headline => IsReady
        ? "Connected"
        : AdapterFound || ConsoleIp is not null
            ? "Console detected"
            : "Not connected";

    public string Detail
    {
        get
        {
            if (IsReady && ConsoleIp is not null) return ConsoleIp;
            if (ConsoleIp is not null) return $"{ConsoleIp} · SSH not running";
            if (AdapterFound) return "USB adapter up · no console response";
            return "No console detected";
        }
    }
}
