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
    private readonly IcsService _ics = new();
    private readonly ConsoleNetworkService _console = new();
    private bool _subscribed;
    private bool _busy;
    private bool _icsBusy;
    private IcsSnapshot? _icsSnapshot;

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
        _subscribed = true;
        Render(App.Link.Current);
        _ = RefreshCurrentConfigAsync();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        App.Link.Updated -= OnLinkUpdated;
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
                : $"{adapter.Ipv4}/{PrefixOf(adapter)}" +
                  (string.IsNullOrEmpty(adapter.Gateway) ? "" : $" · gateway {adapter.Gateway}");

        // Applying only makes sense once the adapter is actually present.
        ApplyButton.IsEnabled = !_busy && adapter is not null;
        RestoreButton.IsEnabled = !_busy && adapter is not null && _net.HasSnapshot(adapter.Name);
        TestButton.IsEnabled = !_busy;

        UpdateDiffPreview();
        UpdateActivityLog();
    }

    private static string PrefixOf(AdapterInfo a)
    {
        // AdapterInfo carries a dotted IPv4 string; the prefix is read live from netsh
        // when needed, so show /24 as the console-link default here.
        return "24";
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
        if (ok) await RefreshConsoleNetworkAsync();
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
        if (ok) await RefreshConsoleNetworkAsync();
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
        IcsStatusText.Text = "Checking internet sharing…";
        IcsDot.Fill = (Brush)FindResource("StatusIdleBrush");

        var snapshot = await _ics.ReadAsync();
        _icsSnapshot = snapshot;

        _icsBusy = false;
        IcsCheckButton.IsEnabled = true;
        IcsCheckButton.Content = "Refresh";

        RenderIcs(snapshot);
        UpdateActivityLog();
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
        var console = s.ConsoleConnection;
        var canShare = console is not null && s.Connections.Count > 1;

        IcsEnableButton.Visibility = s.IsActive || !canShare
            ? Visibility.Collapsed : Visibility.Visible;
        IcsDisableButton.Visibility = s.IsActive
            ? Visibility.Visible : Visibility.Collapsed;

        IcsEnableButton.IsEnabled = !_icsBusy && canShare;
        IcsDisableButton.IsEnabled = !_icsBusy;

        IcsActions.Visibility = canShare || s.IsActive
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------- ics toggle

    private async void IcsEnable_Click(object sender, RoutedEventArgs e)
        => await ToggleSharingAsync(enable: true);

    private async void IcsDisable_Click(object sender, RoutedEventArgs e)
        => await ToggleSharingAsync(enable: false);

    /// <summary>
    /// Enables or disables sharing, then realigns the console's default route.
    ///
    /// The second step is not optional. ICS moves the PC adapter to
    /// 192.168.137.1, but the console keeps a static default route via
    /// 192.168.7.1 — a gateway that no longer exists. SSH keeps working (same
    /// link, both addresses live on usb0) while internet silently fails, which
    /// is exactly the kind of half-working state that is hard to diagnose.
    /// </summary>
    private async Task ToggleSharingAsync(bool enable)
    {
        if (_icsBusy) return;

        var snapshot = _icsSnapshot;
        var console = snapshot?.ConsoleConnection;
        if (snapshot is null || console is null)
        {
            NetworkHint.Text = "Check internet sharing first.";
            return;
        }

        // The source is whatever is currently carrying the internet; when
        // enabling, prefer the connection that already has a gateway.
        var source = enable
            ? (snapshot.PublicConnection ?? PickInternetSource(snapshot, console))
            : snapshot.PublicConnection;

        if (enable && source is null)
        {
            NetworkHint.Text = "No internet connection was found to share. "
                             + "Connect this PC to Wi-Fi or Ethernet first.";
            return;
        }

        _icsBusy = true;
        RenderIcsActions(snapshot);
        IcsCheckButton.IsEnabled = false;

        try
        {
            NetworkHint.Text = enable
                ? "Turning on sharing… approve the Windows prompt if it appears."
                : "Turning off sharing… approve the Windows prompt if it appears.";

            var (ok, message, after) = await _ics.SetSharingAsync(
                enable,
                source?.Name ?? snapshot.PublicConnection?.Name ?? string.Empty,
                console.Name);

            // Use the state the elevated helper captured. Re-reading here would
            // need a second elevation prompt for data we already have, and the
            // stale copy is what left the buttons stuck.
            if (after is not null)
            {
                _icsSnapshot = after;
                RenderIcs(after);
                UpdateFieldState();
            }

            if (!ok)
            {
                NetworkHint.Text = message;
                return;
            }

            NetworkHint.Text = message;
            await App.Link.RefreshAsync();
            Render(App.Link.Current);
            // The PC address just changed, so the console's gateway is stale.
            // Always attempt the update: after ICS is turned off the PC leaves
            // the .137.x subnet, so a probe may find no console address at all
            // even though the device is perfectly reachable on the other one.
            // RefreshNetworkAsync tries both addresses itself.
            await RefreshConsoleNetworkAsync();
        }
        finally
        {
            // Always release the buttons, including on an exception or an early
            // return. They were disabled while _icsBusy was true, and nothing
            // re-enabled them afterwards — which is why a Refresh used to be
            // needed before the panel could be used again.
            _icsBusy = false;
            IcsCheckButton.IsEnabled = true;

            if (_icsSnapshot is not null)
                RenderIcsActions(_icsSnapshot);
        }
    }

    /// <summary>
    /// Best guess at the adapter carrying the internet. The console adapter is
    /// never a candidate: making it the source is the classic misconfiguration
    /// this app exists to prevent.
    /// </summary>
    private static IcsConnection? PickInternetSource(IcsSnapshot s, IcsConnection console)
    {
        var candidates = s.Connections
            .Where(c => c.IsConnected && !c.LooksLikeConsole && c.Name != console.Name)
            .ToList();

        // Prefer adapters whose driver description says Wi-Fi; adapter *names*
        // are user-renamable ("WiFi" can become anything), descriptions are not.
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

    private async Task RefreshIcsAsync(bool quiet = false)
    {
        if (!quiet)
        {
            IcsStatusText.Text = "Checking internet sharing…";
            IcsDot.Fill = (Brush)FindResource("StatusIdleBrush");
        }
        else
        {
            // The console-update line belongs to the toggle that produced it.
            // Keeping it after a fresh read showed stale text ("gateway
            // 192.168.7.1") long after the gateway had become 192.168.137.1.
            IcsRestartBox.Visibility = Visibility.Collapsed;
        }

        var snapshot = await _ics.ReadAsync();
        _icsSnapshot = snapshot;
        RenderIcs(snapshot);

        // Sharing state decides what "automatic" means, so refresh step 1 too.
        UpdateFieldState();
    }

    // ------------------------------------------------ console network refresh

    /// <summary>
    /// Tells the console to re-apply its network settings after the PC's address
    /// changed, so the user does not have to walk over and press Start Services.
    ///
    /// The console's USB_Network_Manager.sh performs the address + route step
    /// itself (it adds both .7.2 and .137.2 to usb0 and probes for .137.1). The
    /// app only triggers it via --refresh-net, which is non-destructive: it does
    /// not unload g_ether or touch sshd, so it cannot cut the session used to
    /// ask for it.
    /// </summary>
    private async Task RefreshConsoleNetworkAsync()
    {
        // Re-probe first: the address the app holds may be the one that just
        // stopped being valid. The service also falls back to the other
        // candidate address, so a stale value here is not fatal.
        await App.Link.RefreshAsync();
        var host = App.Link.Current.ConsoleIp ?? string.Empty;
        IcsRestartText.Text = "Updating the console's network…";
        IcsRestartBox.Visibility = Visibility.Visible;
        IcsRestartDot.Fill = (Brush)FindResource("StatusIdleBrush");

        var (ok, gateway, message) = await _console.RefreshNetworkAsync(host);

        // Verify rather than trust: the console reporting a gateway does not
        // prove packets flow.
        // Give it a few seconds: the default route was just replaced and the
        // first ICMP can fail while the kernel settles. A single immediate check
        // reported "no internet" on a console that was in fact already online.
        var online = false;
        if (ok)
        {
            // Two tries, ~1s apart. HasInternetAsync already walks both console
            // addresses, so a short retry is enough; a long one just leaves the
            // panel saying "Updating..." after the work is actually done.
            online = await _console.HasInternetAsync(host);
            if (!online)
            {
                await Task.Delay(1000);
                online = await _console.HasInternetAsync(host);
            }
        }

        IcsRestartDot.Fill = (Brush)FindResource(
            online ? "StatusOkBrush"
            : ok ? "StatusWarnBrush"
            : "StatusErrorBrush");

        IcsRestartText.Text = online
            ? $"Console updated (gateway {gateway}), the console has internet."
            : ok
                ? $"Console updated (gateway {gateway}), the console has no internet."
                : message;
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
