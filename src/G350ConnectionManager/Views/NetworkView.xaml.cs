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
    private bool _subscribed;
    private bool _busy;

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
}
