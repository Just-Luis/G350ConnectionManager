using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;
using Microsoft.Win32;

namespace G350ConnectionManager.Views;

public partial class ScreenView : UserControl
{
    private readonly ScreenCaptureService _capture = new();
    private readonly List<string> _history = new();

    private CancellationTokenSource? _cts;
    private Task? _pump;

    /// <summary>
    /// True while InitializeComponent() runs: the Medium rate button has
    /// IsChecked="True", so its Checked handler fires before the other named
    /// fields exist.
    /// </summary>
    private bool _loading = true;

    private bool _subscribed;

    public ScreenView()
    {
        InitializeComponent();
        _loading = false;

        SaveToBox.Text = DefaultSaveFolder();

        Unloaded += (_, _) => { Unsubscribe(); StopPreview(); };
        Loaded += (_, _) => Subscribe();

        UpdateRateHint();
        Render(App.Link.Current);
    }

    // ------------------------------------------------------------- lifecycle

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

    private void Render(LinkSnapshot s)
    {
        var connected = s.Adapter is not null && !string.IsNullOrEmpty(s.ConsoleIp);

        ScreenshotButton.IsEnabled = connected;
        PreviewButton.IsEnabled = connected;

        if (!connected && _pump is null)
        {
            PreviewStatus.Text = "Connect the console first.";
        }
        else if (connected && _pump is null)
        {
            PreviewStatus.Text = string.Empty;
        }
    }

    // ----------------------------------------------------------------- rates

    private static readonly Dictionary<string, double> Rates = new(StringComparer.Ordinal)
    {
        ["low"] = 3,
        ["medium"] = 6,
        ["high"] = 12
    };

    private double SelectedFps =>
        RateLow.IsChecked == true ? Rates["low"]
        : RateHigh.IsChecked == true ? Rates["high"]
        : Rates["medium"];

    private void Rate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateRateHint();

        // Applies on the next start; changing mid-stream would mean tearing down
        // the SSH session and re-uploading the grabber.
        if (_pump is not null)
            PreviewStatus.Text = "Rate applies after restarting the preview.";
    }

    private void UpdateRateHint()
    {
        if (RateHint is null) return;

        var fps = SelectedFps;
        RateHint.Text = fps switch
        {
            <= 3 => "Low — lightest on the console, good for reading the menu.",
            <= 6 => "Medium — smooth enough for menus and text screens.",
            _ => "High — best effort. Each frame is a full 1.2 MB capture, so this "
                 + "may not be reached."
        };
    }

    // --------------------------------------------------------------- preview

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_pump is not null)
        {
            StopPreview();
            return;
        }

        var host = App.Link.Current.ConsoleIp;
        if (string.IsNullOrEmpty(host))
        {
            PreviewStatus.Text = "Connect the console first.";
            return;
        }

        PreviewButton.IsEnabled = false;
        PreviewButton.Content = "Starting…";
        PreviewStatus.Text = "Uploading the capture helper…";

        var started = await _capture.StartAsync(host, SelectedFps);

        if (!started)
        {
            PreviewStatus.Text = _capture.LastError ?? "Could not start the preview.";
            PreviewButton.IsEnabled = true;
            PreviewButton.Content = "Start preview";
            return;
        }

        PreviewButton.Content = "Stop preview";
        PreviewButton.IsEnabled = true;

        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpFramesAsync(host, _cts.Token));
    }

    /// <summary>
    /// Reads frames off the SSH stream and hands them to the UI thread.
    /// Runs until the stream ends or the preview is stopped.
    /// </summary>
    private async Task PumpFramesAsync(string host, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var lastReport = TimeSpan.Zero;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = _capture.ReadFrame(token);
                if (frame is null) break;

                var elapsed = DateTime.UtcNow - started;
                var fps = elapsed.TotalSeconds > 0
                    ? _capture.FrameCount / elapsed.TotalSeconds
                    : 0;

                // Do not flood the dispatcher with status updates.
                var report = elapsed - lastReport > TimeSpan.FromSeconds(2);
                if (report) lastReport = elapsed;

                await Dispatcher.InvokeAsync(() =>
                {
                    ShowFrame(frame);
                    if (report)
                    {
                        PreviewStatus.Text =
                            $"{frame.Width}×{frame.Height} · {fps:0.0} fps · "
                            + $"{_capture.FrameCount} frames";
                    }
                });
            }
        }
        catch
        {
            // Stream torn down; handled in the finally block below.
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_pump is not null && !token.IsCancellationRequested)
                {
                    // Ended on its own — tell the user rather than looking frozen.
                    PreviewStatus.Text = "The preview stopped. Press Start preview to resume.";
                    PreviewButton.Content = "Start preview";
                }
            });
        }
    }

    /// <summary>
    /// Displays a captured frame.
    ///
    /// A frozen BitmapSource is created per frame rather than reusing a
    /// WriteableBitmap: the reused-bitmap path rendered as an empty black area
    /// even though the same pixel buffer saved to PNG correctly. At ~6 fps the
    /// extra allocation is irrelevant, and correctness matters more here.
    /// Freezing lets WPF render it on its own render thread.
    /// </summary>
    private void ShowFrame(CapturedFrame frame)
    {
        // Bgr32, not Bgra32: the console's framebuffer stores alpha=0 in every
        // pixel. WPF honours the alpha channel when compositing, so a Bgra32
        // source renders as a fully transparent (black) rectangle. Bgr32 uses
        // the same 4-byte layout but ignores alpha.
        var bitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96,
            PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
        bitmap.Freeze();

        PreviewImage.Source = bitmap;

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
    }

    private void StopPreview()
    {
        try { _cts?.Cancel(); } catch { }

        _capture.Stop();

        var host = App.Link.Current.ConsoleIp;
        if (!string.IsNullOrEmpty(host) && _pump is not null)
        {
            // The grabber is a child of the SSH session; killing it explicitly
            // guarantees nothing is left running on the console.
            var h = host;
            Task.Run(() => ScreenCaptureService.KillRemote(h));
        }

        _cts?.Dispose();
        _cts = null;
        _pump = null;

        PreviewButton.Content = "Start preview";
        PreviewStatus.Text = string.Empty;

        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewImage.Source = null;
    }

    // ------------------------------------------------------------ screenshot

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var host = App.Link.Current.ConsoleIp;
        if (string.IsNullOrEmpty(host))
        {
            PreviewStatus.Text = "Connect the console first.";
            return;
        }

        ScreenshotButton.IsEnabled = false;
        PreviewStatus.Text = "Taking screenshot…";

        var frame = await Task.Run(() => CaptureOnce(host));

        ScreenshotButton.IsEnabled = true;

        if (frame is null)
        {
            PreviewStatus.Text = "The console did not return a frame.";
            return;
        }

        var folder = SaveToBox.Text;
        Directory.CreateDirectory(folder);

        var name = $"g350-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
        var path = Path.Combine(folder, name);

        try
        {
            SavePng(frame, path);
        }
        catch (Exception ex)
        {
            PreviewStatus.Text = $"Could not save the screenshot: {ex.Message}";
            return;
        }

        _history.Insert(0, $"{DateTime.Now:HH:mm:ss}  {name}  ({frame.Width}×{frame.Height})");
        if (_history.Count > 8) _history.RemoveAt(_history.Count - 1);
        HistoryText.Text = string.Join("\n", _history);

        PreviewStatus.Text = $"Saved {name}";

        // Show what was captured even if the live preview is not running.
        ShowFrame(frame);
    }

    /// <summary>Grabs exactly one frame using its own short-lived session.</summary>
    private static CapturedFrame? CaptureOnce(string host)
    {
        using var capture = new ScreenCaptureService();

        if (!capture.StartOnce(host))
            return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var frame = capture.ReadFrame(cts.Token);

        capture.Stop();
        ScreenCaptureService.KillRemote(host);
        return frame;
    }

    // ---------------------------------------------------------------- output

    private static void SavePng(CapturedFrame frame, string path)
    {
        // Match the preview: Bgr32 so the saved PNG has no transparent pixels.
        var bitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96,
            PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string DefaultSaveFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "G350 Captures");

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where should screenshots be saved?",
            InitialDirectory = Directory.Exists(SaveToBox.Text)
                ? SaveToBox.Text
                : DefaultSaveFolder()
        };

        if (dialog.ShowDialog() == true)
            SaveToBox.Text = dialog.FolderName;
    }
}
