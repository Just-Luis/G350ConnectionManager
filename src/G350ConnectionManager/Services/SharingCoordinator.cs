using G350ConnectionManager.Models;

namespace G350ConnectionManager.Services;

/// <summary>
/// One "share my internet with the console" operation, end to end.
///
/// Home and the Network page both need this and both need it to behave
/// identically, so the sequence lives here rather than in either view. A view is
/// responsible only for deciding when to call it and for rendering the result.
///
/// The sequence is deliberately more than "call EnableSharing": ICS moves the PC
/// adapter to 192.168.137.1, which invalidates the console's default route, so
/// the console has to be told to re-apply its networking afterwards. Skipping
/// that leaves SSH working and the internet silently broken.
/// </summary>
public sealed class SharingCoordinator
{
    private readonly IcsService _ics = new();
    private readonly ConsoleNetworkService _console = new();

    /// <summary>True while an operation is running, so callers can disable input.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Reads the current sharing state. Elevated, so only call this from an
    /// explicit user action — never on a timer or on page load.
    /// </summary>
    public Task<IcsSnapshot> ReadAsync() => _ics.ReadAsync();

    /// <summary>Outcome of one toggle, for the caller to render.</summary>
    public sealed record Result(
        bool Ok,
        string Message,
        IcsSnapshot? Snapshot,
        bool ConsoleRefreshed,
        bool ConsoleHasInternet,
        string? Gateway);

    /// <summary>
    /// Enables or disables sharing and realigns the console's default route.
    /// Never throws; every failure comes back in <see cref="Result.Message"/>.
    /// </summary>
    /// <param name="enable">True to share, false to stop sharing.</param>
    /// <param name="known">The last snapshot, or null to read one first.</param>
    public async Task<Result> SetSharingAsync(bool enable, IcsSnapshot? known = null)
    {
        if (IsBusy)
            return new Result(false, "A sharing change is already running.", null, false, false, null);

        IsBusy = true;
        try
        {
            var snapshot = known ?? await _ics.ReadAsync();
            if (!snapshot.Ok)
                return new Result(false, snapshot.Message, snapshot, false, false, null);

            // A stale entry from an adapter that is no longer plugged in still
            // matches the gadget description. Prefer the connection whose GUID is
            // the adapter the link monitor is actually reading.
            var liveGuid = App.Link.Current.Adapter?.Id;
            var console = snapshot.ConsoleFor(liveGuid) ?? snapshot.ConsoleConnection;

            // Turning sharing OFF must not depend on the console being known or
            // even present: the elevated helper clears every shared connection
            // without looking any of them up. Requiring a lookup here is what
            // used to leave Wi-Fi shared after the cable was pulled.
            if (enable && console is null)
                return new Result(false,
                    "The console adapter was not found. Reconnect the console, then try again.",
                    snapshot, false, false, null);

            // The source is whatever carries the internet. Prefer an existing
            // public connection, otherwise look for a plausible one.
            var source = enable
                ? snapshot.PublicConnection ?? PickInternetSource(snapshot, console!)
                : snapshot.PublicConnection;

            if (enable && source is null)
                return new Result(false,
                    "No internet connection was found to share. Connect this PC to "
                    + "Wi-Fi or Ethernet first.",
                    snapshot, false, false, null);

            // The console adapter's GUID is passed so the helper can still find it
            // after a USB-port change has renamed it. Its name alone would not.
            var targetGuid = enable ? (App.Link.Current.Adapter?.Id ?? console?.Guid) : null;

            var (ok, message, after) = await _ics.SetSharingAsync(
                enable,
                source?.Name ?? snapshot.PublicConnection?.Name ?? string.Empty,
                console?.Name ?? string.Empty,
                targetGuid);

            if (!ok)
                return new Result(false, message, after ?? snapshot, false, false, null);

            await App.Link.RefreshAsync();

            // The PC address just changed, so the console's gateway is stale.
            var (refreshed, gateway, online) = await RefreshConsoleNetworkAsync();

            return new Result(true, message, after ?? snapshot, refreshed, online, gateway);
        }
        catch (Exception ex)
        {
            return new Result(false, $"The sharing change failed: {ex.Message}", null, false, false, null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Tells the console to re-apply its networking after the PC's address
    /// changed for a reason other than a sharing toggle - applying or restoring
    /// an adapter address does the same thing to the console's default route.
    ///
    /// Returns the same result shape as a toggle so the caller can render one way.
    /// </summary>
    public async Task<Result> RefreshConsoleAsync()
    {
        if (IsBusy)
            return new Result(false, "A sharing change is already running.", null, false, false, null);

        IsBusy = true;
        try
        {
            var (refreshed, gateway, online) = await RefreshConsoleNetworkAsync();
            return refreshed
                ? new Result(true, $"Console updated (gateway {gateway}).", null, true, online, gateway)
                : new Result(false,
                    "The console could not be reached to update its network settings.",
                    null, false, false, null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Tells the console to re-apply its networking, then checks whether it
    /// actually reached the internet. The second part matters: the console
    /// reporting a gateway does not prove packets flow.
    ///
    /// HasInternetAsync already walks both console addresses, so one short retry
    /// is enough - the default route was just replaced and the first ICMP can
    /// fail while the kernel settles.
    /// </summary>
    private async Task<(bool Refreshed, string? Gateway, bool Online)> RefreshConsoleNetworkAsync()
    {
        await App.Link.RefreshAsync();
        var host = App.Link.Current.ConsoleIp ?? string.Empty;

        var (ok, gateway, _) = await _console.RefreshNetworkAsync(host);
        if (!ok)
            return (false, null, false);

        var online = await _console.HasInternetAsync(host);
        if (!online)
        {
            await Task.Delay(1000);
            online = await _console.HasInternetAsync(host);
        }

        return (true, gateway, online);
    }

    /// <summary>
    /// Best guess at the adapter carrying the internet. The console adapter is
    /// never a candidate: making it the source is the misconfiguration this app
    /// exists to prevent.
    /// </summary>
    private static IcsConnection? PickInternetSource(IcsSnapshot s, IcsConnection console)
    {
        var candidates = s.Connections
            .Where(c => c.IsConnected && !c.LooksLikeConsole && c.Name != console.Name)
            .ToList();

        // Driver descriptions cannot be renamed by the user; adapter names can.
        static bool IsWifi(IcsConnection c) =>
            c.DeviceName.Contains("wi-fi", StringComparison.OrdinalIgnoreCase)
            || c.DeviceName.Contains("wireless", StringComparison.OrdinalIgnoreCase)
            || c.DeviceName.Contains("802.11", StringComparison.OrdinalIgnoreCase)
            || c.Name.Equals("WiFi", StringComparison.OrdinalIgnoreCase)
            || c.Name.StartsWith("Wi-Fi", StringComparison.OrdinalIgnoreCase);

        // Virtual switches are almost never the real internet path.
        static bool IsVirtual(IcsConnection c) =>
            c.DeviceName.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || c.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase);

        return candidates.FirstOrDefault(IsWifi)
               ?? candidates.FirstOrDefault(c => !IsVirtual(c))
               ?? candidates.FirstOrDefault();
    }
}
