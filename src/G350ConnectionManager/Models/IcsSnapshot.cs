namespace G350ConnectionManager.Models;

/// <summary>
/// One connection as Windows Internet Connection Sharing sees it.
/// </summary>
/// <param name="Name">Adapter name, e.g. "WiFi" or "Ethernet 2".</param>
/// <param name="DeviceName">Driver description, e.g. "Intel(R) Wi-Fi 6 AX201".</param>
/// <param name="Guid">Adapter GUID — stable across renames, unlike the name.</param>
/// <param name="IsConnected">True when Windows reports the connection up.</param>
/// <param name="SharingEnabled">True when this connection is currently shared.</param>
/// <param name="SharingType">0 = public (source), 1 = private (destination).</param>
public sealed record IcsConnection(
    string Name,
    string DeviceName,
    string Guid,
    bool IsConnected,
    bool SharingEnabled,
    int SharingType)
{
    public bool IsPublic => SharingEnabled && SharingType == 0;
    public bool IsPrivate => SharingEnabled && SharingType == 1;

    /// <summary>Short state word for the UI.</summary>
    public string StateText => !SharingEnabled
        ? "not shared"
        : SharingType == 0 ? "shared · source" : "shared · destination";

    public bool LooksLikeConsole => Services.AdapterEnumerator
        .LooksLikeGadget(Name, DeviceName);
}

/// <summary>
/// A full read-only snapshot of ICS state, plus the raw facts the UI needs to
/// explain what Windows is doing.
/// </summary>
public sealed record IcsSnapshot(
    bool Ok,
    string Message,
    bool SharingInstalled,
    bool ServiceRunning,
    string? ScopeAddress,
    IReadOnlyList<IcsConnection> Connections)
{
    public static IcsSnapshot Failed(string message) =>
        new(false, message, false, false, null, Array.Empty<IcsConnection>());

    public IcsConnection? PublicConnection =>
        Connections.FirstOrDefault(c => c.IsPublic);

    public IcsConnection? PrivateConnection =>
        Connections.FirstOrDefault(c => c.IsPrivate);

    public IcsConnection? ConsoleConnection =>
        Connections.FirstOrDefault(c => c.LooksLikeConsole);

    /// <summary>True when ICS is actually forwarding something.</summary>
    public bool IsActive => PublicConnection is not null;
}
