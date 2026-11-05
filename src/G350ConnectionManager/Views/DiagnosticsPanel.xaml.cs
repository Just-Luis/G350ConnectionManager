using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Views;

public partial class DiagnosticsPanel : UserControl
{
    /// <summary>Row model bound by the DataTemplate in XAML.</summary>
    public sealed record StageRow(Brush Brush, string Title, string Detail);

    private static readonly string[] StageTitles =
    {
        "USB adapter",
        "Cable link",
        "Console responds",
        "SSH / SFTP service",
        "Internet"
    };

    public DiagnosticsPanel()
    {
        InitializeComponent();
        App.Link.Updated += OnLinkUpdated;
        Render(App.Link.Current);
    }

    private void OnLinkUpdated(object? sender, LinkSnapshot snapshot) => Render(snapshot);

    private void Test_Click(object sender, RoutedEventArgs e) => _ = App.Link.RefreshAsync();

    private void Render(LinkSnapshot snapshot)
    {
        var rows = new List<StageRow>(StageTitles.Length);

        for (var i = 0; i < StageTitles.Length; i++)
        {
            var result = i < snapshot.Stages.Count ? snapshot.Stages[i] : ProbeResult.Unknown;
            rows.Add(new StageRow(
                BrushFor(result.State),
                StageTitles[i],
                FormatDetail(result)));
        }

        StageList.ItemsSource = rows;

        SubtitleText.Text = snapshot.Timestamp == default
            ? "Checks what the PC can see. Nothing here changes your settings."
            : $"Checks what the PC can see · updated {snapshot.Timestamp:HH:mm:ss}";
    }

    private static string FormatDetail(ProbeResult result) => result.State switch
    {
        ProbeState.Ok when result.LatencyMs is int ms => $"{result.Message} · {ms} ms",
        ProbeState.Ok => result.Message,
        ProbeState.Skipped => "Skipped",
        _ => result.Message
    };

    private Brush BrushFor(ProbeState state) => (Brush)FindResource(state switch
    {
        ProbeState.Ok => "StatusOkBrush",
        ProbeState.Warning => "StatusWarnBrush",
        ProbeState.Failed => "StatusErrorBrush",
        _ => "StatusIdleBrush"
    });

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => App.Link.Updated -= OnLinkUpdated;
}
