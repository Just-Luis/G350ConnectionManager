using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;

namespace G350ConnectionManager.Views;

public partial class NetworkView : UserControl
{
    /// <summary>
    /// True while InitializeComponent() is still running. The AutoMode radio has
    /// IsChecked="True" in XAML, so its Checked event fires before the named
    /// fields below it exist — touching them there would throw.
    /// </summary>
    private bool _loading = true;

    private readonly NetworkConfigService _net = new();
    private SharingCoordinator Sharing => App.Sharing;
    private bool _subscribed;
    private bool _busy;
    private bool _icsBusy;
    private IcsSnapshot? _icsSnapshot;

    /// <summary>
    /// Suppresses <see cref="IcsShare_Changed"/> while the switch is being moved
    /// to mirror the real state, so a repaint is never mistaken for a click.
    /// </summary>
    private bool _syncingToggle;


    public NetworkView()
    {
        InitializeComponent();
        _loading = false;

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();

        UpdateFieldState();
        Render(App.Link.Current);
    }

    // ------------------------------------------------------------- lifecycle

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Link.Updated += OnLinkUpdated;
        Sharing.Changed += OnSharingChanged;
        _subscribed = true;
        Render(App.Link.Current);
        RenderSharedState();
        _ = RefreshCurrentConfigAsync();
    }

    /// <summary>
    /// Another page changed the sharing state. Adopt it rather than keeping the
    /// private copy that used to make the two pages disagree.
    /// </summary>
    private void OnSharingChanged(object? sender, IcsSnapshot snapshot)
    {
        _icsSnapshot = snapshot;
        RenderIcs(snapshot);
        UpdateFieldState();
    }

    /// <summary>
    /// Paints whatever is already known, so returning to this tab shows real
    /// state instead of "not checked" and a disabled button.
    /// </summary>
    private void RenderSharedState()
    {
        if (Sharing.Last is { } known)
        {
            _icsSnapshot = known;
            RenderIcs(known);
        }
        else
        {
            // Nothing read yet. The switch stays usable and either direction
            // reads the state before acting, so Check is never a prerequisite.
            IcsActions.Visibility = Visibility.Visible;
            IcsShareToggle.IsEnabled = !_icsBusy;
        }
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        App.Link.Updated -= OnLinkUpdated;
        Sharing.Changed -= OnSharingChanged;
        _subscribed = false;
    }

    private void OnLinkUpdated(object? sender, LinkSnapshot snapshot) => Render(snapshot);

    // ------------------------------------------------------------------ render

    private void Render(LinkSnapshot s)
    {
        var adapter = s.Adapter;

        CurrentDot.Fill = (Brush)FindResource(
            adapter is null ? "StatusIdleBrush"
            : s.IsReady ? "StatusOkBrush"
            : "StatusWarnBrush");

        CurrentAdapterText.Text = adapter is null
            ? "Console not connected"
            : $"{adapter.DisplayName} · {adapter.Name}";

        CurrentConfigText.Text = adapter is null
            ? "Plug in the USB cable and start services on the console."
            : adapter.Ipv4 is null
                ? "No IPv4 address assigned yet."
                // AdapterInfo carries the address without its prefix; the real
                // prefix comes from netsh in RefreshCurrentConfigAsync, which
                // overwrites this line once it has run. The console link is
                // always /24, so that is the honest interim value.
                : $"{adapter.Ipv4}/24" +
                  (string.IsNullOrEmpty(adapter.Gateway) ? "" : $" · gateway {adapter.Gateway}");

        // Applying only makes sense once the adapter is actually present.
        ApplyButton.IsEnabled = !_busy && adapter is not null;
        RestoreButton.IsEnabled = !_busy && adapter is not null && _net.HasSnapshot(adapter.Name);
        TestButton.IsEnabled = !_busy;

        UpdateDiffPreview();
        UpdateActivityLog();
    }

    private void UpdateActivityLog()
    {
        ActivityText.Text = _net.Activity.Count == 0
            ? "No changes yet."
            : string.Join("\n", _net.Activity);
    }

    // ------------------------------------------------------------ ip mode ui

    private void IpMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateFieldState();
    }

    /// <summary>
    /// Automatic mode pins the values the app will apply; Manual lets the user
    /// type them.
    /// </summary>
    private void UpdateFieldState()
    {
        if (IpAddressBox is null || SubnetBox is null || GatewayBox is null) return;

        var manual = ManualMode?.IsChecked == true;

        IpAddressBox.IsEnabled = manual;
        SubnetBox.IsEnabled = manual;
        GatewayBox.IsEnabled = manual;

        if (!manual)
        {
            // Automatic has to match what Windows is actually doing. While ICS is
            // on it owns the adapter at 192.168.137.1 and would overwrite .7.1,
            // so suggesting that here would be actively wrong.
            var sharing = _icsSnapshot?.IsActive == true;

            IpAddressBox.Text = sharing
                ? NetworkConfigService.IcsAutoAddress
                : NetworkConfigService.AutoAddress;
            SubnetBox.Text = NetworkConfigService.AutoMask;
            GatewayBox.Text = string.Empty;

            ModeHint.Text = sharing
                ? "Internet sharing sets 192.168.137.1, so the console appears at 192.168.137.2."
                : "Assigns 192.168.7.1 so the console appears at 192.168.7.2.";
        }
        else
        {
            ModeHint.Text = "Choose any address you like — keep the console on the same subnet.";
        }

        UpdateDiffPreview();
    }

    private void UpdateDiffPreview()
    {
        if (DiffBox is null || DiffBefore is null || DiffAfter is null) return;

        var adapter = App.Link.Current.Adapter;
        if (adapter is null || _busy)
        {
            DiffBox.Visibility = Visibility.Collapsed;
            return;
        }

        var before = adapter.Ipv4 is null
            ? "no address"
            : $"{adapter.Ipv4}/24" + (string.IsNullOrEmpty(adapter.Gateway) ? "" : $"\ngw {adapter.Gateway}");

        var gw = GatewayBox.Text.Trim();
        var after = $"{IpAddressBox.Text.Trim()}/{MaskToPrefixText(SubnetBox.Text.Trim())}"
                    + (gw.Length == 0 ? "" : $"\ngw {gw}");

        DiffBefore.Text = before;
        DiffAfter.Text = after;
        DiffBox.Visibility = Visibility.Visible;
    }

    private static string MaskToPrefixText(string mask)
    {
        var p = Ipv4Config.MaskToPrefix(mask);
        return p?.ToString() ?? "?";
    }

    // --------------------------------------------------------------- actions

    private async Task RefreshCurrentConfigAsync()
    {
        var adapter = App.Link.Current.Adapter;
        if (adapter is null) return;

        var cfg = await _net.ReadAsync(adapter.Name);
        if (cfg is null) return;

        CurrentConfigText.Text = $"{cfg.Address ?? "no address"}/{cfg.PrefixLength}" +
                                 (cfg.Gateway is null ? "" : $" · gateway {cfg.Gateway}") +
                                 $"  ({cfg.Mode})";

        RestoreButton.IsEnabled = !_busy && _net.HasSnapshot(adapter.Name);
        UpdateDiffPreview();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var adapter = App.Link.Current.Adapter;
        if (adapter is null)
        {
            NetworkHint.Text = "Connect the console first.";
            return;
        }

        var address = IpAddressBox.Text.Trim();
        var mask = SubnetBox.Text.Trim();
        var gateway = GatewayBox.Text.Trim();

        _busy = true;
        NetworkHint.Text = "Applying… approve the Windows prompt if it appears.";
        Render(App.Link.Current);
        await Task.Yield();

        var (ok, message) = await _net.ApplyStaticAsync(adapter.Name, address, mask,
            gateway.Length == 0 ? null : gateway);

        _busy = false;
        NetworkHint.Text = message;

        // Re-probe so the banner reflects the new addressing immediately.
        await App.Link.RefreshAsync();
        await RefreshCurrentConfigAsync();
        Render(App.Link.Current);

        // Any enforced address change invalidates the console's gateway, so the
        // console must re-apply its own networking. Without this, Apply left the
        // console pointing at a gateway that no longer existed.
        if (ok) await RefreshConsoleAfterAddressChangeAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var adapter = App.Link.Current.Adapter;
        if (adapter is null)
        {
            NetworkHint.Text = "Connect the console first, then restore.";
            return;
        }

        _busy = true;
        NetworkHint.Text = "Restoring… approve the Windows prompt if it appears.";
        Render(App.Link.Current);
        await Task.Yield();

        var (ok, message) = await _net.RestoreAsync(adapter.Name);

        _busy = false;
        NetworkHint.Text = message;

        await App.Link.RefreshAsync();
        await RefreshCurrentConfigAsync();
        Render(App.Link.Current);

        // Restoring also moves the PC address, so the console needs the same
        // treatment as Apply.
        if (ok) await RefreshConsoleAfterAddressChangeAsync();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        NetworkHint.Text = "Testing…";
        var snapshot = await App.Link.RefreshAsync();

        NetworkHint.Text = snapshot.IsReady
            ? $"Console reachable at {snapshot.ConsoleIp} · {snapshot.PingMs} ms"
            : snapshot.AdapterFound
                ? "Adapter up, but the console did not answer. Start services on the console."
                : "No USB adapter found. Check the cable and that the console is in PC mode.";
    }

    // ----------------------------------------------------------- ics status

    /// <summary>
    /// Reading ICS requires elevation, so this is never automatic: a UAC prompt
    /// on page load would be hostile. The user asks for it explicitly.
    /// </summary>
    private async void IcsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_icsBusy) return;

        _icsBusy = true;
        IcsCheckButton.IsEnabled = false;
        IcsCheckButton.Content = "Checking…";
        try
        {
            await RefreshIcsAsync();
            UpdateActivityLog();
        }
        finally
        {
            _icsBusy = false;
            IcsCheckButton.IsEnabled = true;
            IcsCheckButton.Content = "Refresh";
        }
    }

    private void RenderIcs(IcsSnapshot s)
    {
        if (!s.Ok)
        {
            IcsStatusText.Text = "Internet sharing — could not be read";
            IcsDot.Fill = (Brush)FindResource("StatusWarnBrush");
            IcsDetail.Visibility = Visibility.Collapsed;

            // Surface the reason in the same hint area as other network errors.
            NetworkHint.Text = s.Message;
            return;
        }

        // A previous failure left its explanation in the step-1 hint. Clear it,
        // otherwise a successful read still shows "approval is required".
        if (IcsStatusText.Text.StartsWith("Internet sharing — could not be read", StringComparison.Ordinal))
            NetworkHint.Text = string.Empty;

        var pub = s.PublicConnection;
        var priv = s.PrivateConnection;
        var console = s.ConsoleConnection;

        if (s.IsActive)
        {
            IcsStatusText.Text = $"Internet sharing is on ({pub!.Name} → {priv?.Name ?? "?"})";
            IcsDot.Fill = (Brush)FindResource("StatusOkBrush");
        }
        else if (s.ServiceRunning)
        {
            IcsStatusText.Text = "Internet sharing is off (service idle)";
            IcsDot.Fill = (Brush)FindResource("StatusIdleBrush");
        }
        else
        {
            IcsStatusText.Text = "Internet sharing is off";
            IcsDot.Fill = (Brush)FindResource("StatusIdleBrush");
        }

        IcsDetail.Visibility = Visibility.Visible;

        IcsSourceText.Text = pub is null
            ? "nothing is being shared"
            : $"{pub.Name}  ·  {pub.DeviceName}";

        IcsTargetText.Text = priv is null
            ? "—"
            : $"{priv.Name}  ·  {priv.DeviceName}";

        IcsScopeText.Text = s.ScopeAddress is null
            ? "unknown"
            : $"{s.ScopeAddress}  (ICS always uses this subnet)";

        RenderIcsConflict(s, priv, console);
        RenderIcsConnectionList(s);
        RenderIcsActions(s);
    }

    /// <summary>
    /// Shows only the action that makes sense for the current state, so the user
    /// is never offered "turn on" while it is already on.
    /// </summary>
    private void RenderIcsActions(IcsSnapshot s)
    {
        // The switch is always usable. It used to require a detected console
        // connection before either button could be pressed, which greyed out the
        // controls exactly after the status read the user had just asked for.
        // The operation itself reports a missing adapter if there is one.
        IcsActions.Visibility = Visibility.Visible;
        IcsShareToggle.IsEnabled = !_icsBusy;

        MirrorToggle(s.IsActive);
    }

    /// <summary>
    /// Moves the switch without raising the click handler.
    /// </summary>
    private void MirrorToggle(bool on)
    {
        _syncingToggle = true;
        try
        {
            IcsShareToggle.IsChecked = on;
        }
        finally
        {
            _syncingToggle = false;
        }
    }


    // ------------------------------------------------------------- ics toggle

    /// <summary>
    /// Raised by the switch in either direction. Shares the Home page's pattern:
    /// the knob is only moved by code when mirroring real state, never in
    /// response to a user click, so a programmatic sync cannot start a second
    /// share operation.
    /// </summary>
    private async void IcsShare_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;

        var enable = IcsShareToggle.IsChecked == true;

        await ToggleSharingAsync(enable);
    }

    /// <summary>
    /// Enables or disables sharing and realigns the console's default route. The
    /// sequence itself lives in <see cref="SharingCoordinator"/> so the Home page
    /// runs exactly the same code rather than a second copy of it.
    /// </summary>
    private async Task ToggleSharingAsync(bool enable)
    {
        if (_icsBusy) return;

        _icsBusy = true;
        if (_icsSnapshot is not null) RenderIcsActions(_icsSnapshot);
        IcsCheckButton.IsEnabled = false;

        try
        {
            NetworkHint.Text = enable
                ? "Turning on sharing… approve the Windows prompt if it appears."
                : "Turning off sharing… approve the Windows prompt if it appears.";

            var result = await Sharing.SetSharingAsync(enable, _icsSnapshot);

            // Use the state the elevated helper captured; re-reading here would
            // need a second elevation prompt for data we already have.
            if (result.Snapshot is not null)
            {
                _icsSnapshot = result.Snapshot;
                RenderIcs(result.Snapshot);
                UpdateFieldState();
            }

            if (!result.Ok)
            {
                NetworkHint.Text = result.Message;
                return;
            }

            NetworkHint.Text = result.Message;
            await App.Link.RefreshAsync();
            Render(App.Link.Current);
            RenderConsoleRefresh(result);
        }
        finally
        {
            // Always release the buttons, including on an exception or an early
            // return, so the panel is usable without pressing Refresh first.
            _icsBusy = false;
            IcsCheckButton.IsEnabled = true;

            if (_icsSnapshot is not null)
                RenderIcsActions(_icsSnapshot);
        }
    }

    /// <summary>
    /// Renders what happened when the console was told to re-apply its network
    /// settings after the PC's address changed.
    /// </summary>
    private void RenderConsoleRefresh(SharingCoordinator.Result result)
    {
        IcsRestartBox.Visibility = Visibility.Visible;

        IcsRestartDot.Fill = (Brush)FindResource(
            result.ConsoleHasInternet ? "StatusOkBrush"
            : result.ConsoleRefreshed ? "StatusWarnBrush"
            : "StatusErrorBrush");

        IcsRestartText.Text = result.ConsoleHasInternet
            ? $"Console updated (gateway {result.Gateway}), the console has internet."
            : result.ConsoleRefreshed
                ? $"Console updated (gateway {result.Gateway}), the console has no internet."
                : "The console could not be reached to update its network settings.";
    }

    /// <summary>
    /// Reads internet sharing and repaints the panel. The read is elevated, so
    /// this is only ever called from a button the user pressed.
    /// </summary>
    private async Task RefreshIcsAsync()
    {
        IcsStatusText.Text = "Checking internet sharing…";
        IcsDot.Fill = (Brush)FindResource("StatusIdleBrush");

        // The console-update line belongs to the toggle that produced it. A fresh
        // read supersedes it; keeping it showed a stale gateway long after the
        // console had moved to a different one.
        IcsRestartBox.Visibility = Visibility.Collapsed;

        var snapshot = await Sharing.ReadAsync();
        _icsSnapshot = snapshot;
        RenderIcs(snapshot);

        // Sharing state decides what "automatic" means, so refresh step 1 too.
        UpdateFieldState();
    }

    /// <summary>
    /// Apply and Restore also move the PC's address, so the console's default
    /// route becomes stale in exactly the same way it does after a toggle.
    /// </summary>
    private async Task RefreshConsoleAfterAddressChangeAsync()
    {
        var result = await Sharing.RefreshConsoleAsync();
        await App.Link.RefreshAsync();
        Render(App.Link.Current);
        RenderConsoleRefresh(result);
    }

    /// <summary>
    /// Flags genuinely broken configurations: sharing aimed at the wrong adapter,
    /// or the console adapter accidentally made the internet *source*.
    ///
    /// The normal ICS state (console adapter at 192.168.137.1) is deliberately not
    /// reported — it is the expected, working arrangement, and describing it only
    /// made a healthy setup look like a problem.
    /// </summary>
    private void RenderIcsConflict(IcsSnapshot s, IcsConnection? priv, IcsConnection? console)
    {
        var notes = new List<string>();

        if (s.IsActive && priv is not null && !priv.LooksLikeConsole && console is not null)
        {
            notes.Add(
                $"Sharing is on, but it targets {priv.Name}, not the console adapter "
                + $"({console.Name}). The G350 will not get internet access until sharing "
                + "is moved to it.");
        }

        if (s.IsActive && console is not null && console.IsPublic)
        {
            notes.Add(
                $"The console adapter ({console.Name}) is currently the internet *source*. "
                + "That is almost certainly backwards — the G350 should be the destination.");
        }

        if (notes.Count == 0)
        {
            IcsConflictBox.Visibility = Visibility.Collapsed;
            return;
        }

        IcsConflictText.Text = string.Join("\n\n", notes);
        IcsConflictBox.Visibility = Visibility.Visible;
    }

    private void RenderIcsConnectionList(IcsSnapshot s)
    {
        if (s.Connections.Count == 0)
        {
            IcsConnectionsText.Text = "Windows reported no connections.";
            return;
        }

        var lines = s.Connections.Select(c =>
        {
            var mark = c.SharingEnabled
                ? (c.SharingType == 0 ? "▲ source" : "▼ dest  ")
                : "        ";
            var up = c.IsConnected ? "up     " : "down   ";
            return $"{mark} {up} {c.Name}";
        });

        IcsConnectionsText.Text = string.Join("\n", lines);
    }
}
