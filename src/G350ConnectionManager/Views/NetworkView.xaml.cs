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
            IpAddressBox.Text = NetworkConfigService.AutoAddress;
            SubnetBox.Text = NetworkConfigService.AutoMask;
            GatewayBox.Text = string.Empty;
            ModeHint.Text = "Assigns 192.168.7.1 so the console appears at 192.168.7.2.";
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
    }

    /// <summary>
    /// The .7.x vs .137.x conflict is the single most confusing part of this
    /// feature, so it is stated explicitly instead of left to be discovered.
    /// </summary>
    private void RenderIcsConflict(IcsSnapshot s, IcsConnection? priv, IcsConnection? console)
    {
        var notes = new List<string>();

        if (s.IsActive && priv is not null && priv.LooksLikeConsole)
        {
            notes.Add(
                $"The console adapter ({priv.Name}) is the shared side, so Windows "
                + $"has assigned it {s.ScopeAddress ?? "192.168.137.1"} and will keep "
                + "it there. While sharing is on, the console sits at .137.2, not .7.2 — "
                + "the address in step 1 above does not apply.");
        }

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
