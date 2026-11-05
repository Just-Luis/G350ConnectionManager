using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        App.Link.Updated += OnLinkUpdated;
        Render(App.Link.Current);
        Unloaded += (_, _) => App.Link.Updated -= OnLinkUpdated;
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
