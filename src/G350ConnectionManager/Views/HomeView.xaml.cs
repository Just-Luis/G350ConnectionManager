using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Views;

public partial class HomeView : UserControl
{
    private bool _subscribed;

    public HomeView()
    {
        InitializeComponent();

        // Views are cached by MainWindow and re-attached when re-selected, so
        // subscribing in the constructor is not enough: Unloaded fires when the
        // view is navigated away from, and the cached instance is never
        // reconstructed on return. Bind to Loaded/Unloaded instead.
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();

        Render(App.Link.Current);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Link.Updated += OnLinkUpdated;
        _subscribed = true;
        Render(App.Link.Current);
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        App.Link.Updated -= OnLinkUpdated;
        _subscribed = false;
    }

    private void OnLinkUpdated(object? sender, LinkSnapshot snapshot) => Render(snapshot);

    private void Test_Click(object sender, RoutedEventArgs e) => _ = App.Link.RefreshAsync();

    private void Render(LinkSnapshot s)
    {
        BannerDot.Fill = (Brush)FindResource(
            s.IsReady ? "StatusOkBrush"
            : s.AdapterFound ? "StatusWarnBrush"
            : "StatusIdleBrush");

        BannerHeadline.Text = s.Headline;
        BannerDetail.Text = s.IsReady ? $"{s.Detail} · {s.PingMs} ms" : s.Detail;

        // The card is labelled "Network" and follows step 1 of the setup flow,
        // which is about the console's address — so this reports the console IP,
        // not the PC adapter's.
        Step1Text.Text = s.IsReady
            ? $"IP set to {s.ConsoleIp}"
            : s.AdapterFound
                ? "Adapter up, console silent"
                : "Waiting for console";

        Step2Text.Text = s.InternetReachable
            ? "Online — PortMaster ready"
            : s.InternetSharingActive
                ? "Sharing on, no internet"
                : "Off";
    }
}
