using System.Diagnostics;
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

    /// <summary>Live quality settings, shared by preview and screenshots.</summary>
    private CaptureSettings _settings = CaptureSettings.Default;

    /// <summary>Set when a control changes so the pump picks it up next frame.</summary>
    private volatile bool _settingsDirty;

    private int _framesSinceChange;
    private readonly Stopwatch _sinceChange = Stopwatch.StartNew();

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

    // -------------------------------------------------------------- settings

    private int SelectedWidth =>
        ResolutionBox?.SelectedItem is ComboBoxItem { Tag: string tag }
        && int.TryParse(tag, out var w) ? w : 640;

    private void Settings_Changed(object sender, SelectionChangedEventArgs e) => ApplySettings();
    private void Fps_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplySettings();
    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplySettings();
    private void Delay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplySettings();

    /// <summary>
    /// Rebuilds the settings object from the controls.
    ///
    /// Scaling and JPEG encoding both happen on the PC: measured on the console,
    /// ffmpeg needs ~1 s per frame, which would fight the game for its four
    /// cores. Here it costs 1-10 ms, so the device only sends the raw frame it
    /// already captures.
    /// </summary>
    private void ApplySettings()
    {
        if (_loading) return;
        if (FpsSlider is null || QualitySlider is null || DelaySlider is null) return;

        _settings = new CaptureSettings
        {
            Width = SelectedWidth,
            Fps = FpsSlider.Value,
            JpegQuality = (int)QualitySlider.Value,
            DelayMs = (int)DelaySlider.Value
        };

        if (FpsValue is not null) FpsValue.Text = $"{_settings.Fps:0} fps";
        if (QualityValue is not null) QualityValue.Text = $"{_settings.JpegQuality}";
        if (DelayValue is not null) DelayValue.Text = $"{_settings.DelayMs} ms";

        UpdateRateHint();

        // Live controls: the pump reads these on its next frame, so changing a
        // slider takes effect immediately instead of after a restart.
        _settingsDirty = true;

        App.Settings.Capture = _settings;
        App.Settings.Save();
    }

    private void UpdateRateHint()
    {
        if (RateHint is null) return;

        // Rough guide only: the real ceiling is the USB link, not the sliders.
        var budget = _settings.FrameBudgetMs;
        RateHint.Text = budget < 120
            ? "Aggressive: may not be reached on a busy link."
            : budget < 300
                ? "Comfortable for menus and text screens."
                : "Gentle on the console; expect visible lag.";
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

        var started = await _capture.StartAsync(host, _settings.Fps);

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

                    if (_settingsDirty)
                    {
                        // Reset the counter so the reported rate reflects the
                        // current settings rather than a blend of old and new.
                        _settingsDirty = false;
                        _framesSinceChange = 0;
                        _sinceChange.Restart();
                    }

                    _framesSinceChange++;

                    if (report)
                    {
                        var liveFps = _sinceChange.Elapsed.TotalSeconds > 0
                            ? _framesSinceChange / _sinceChange.Elapsed.TotalSeconds
                            : 0;

                        PreviewStatus.Text =
                            $"{_settings.Width}×{_settings.Height} · {liveFps:0.0} fps"
                            + $" · q{_settings.JpegQuality} · {_capture.FrameCount} frames";
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
        // Scaling happens here rather than on the console: it costs ~2 ms on
        // the PC versus ~1 s per frame for ffmpeg on the device's four weak
        // cores. FrameConverter uses Bgr32 because the console stores alpha=0
        // in every pixel and WPF would otherwise render nothing.
        PreviewImage.Source = FrameConverter.ToBitmap(frame, _settings);

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
