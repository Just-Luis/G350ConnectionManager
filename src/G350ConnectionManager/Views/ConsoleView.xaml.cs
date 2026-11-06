using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;
using Microsoft.Win32;

namespace G350ConnectionManager.Views;

public partial class ConsoleView : UserControl
{
    private readonly RemoteConsoleService _console = new();
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _pump;

    private bool _subscribed;

    public ConsoleView()
    {
        InitializeComponent();

        // Output is produced on an SSH thread and drained here, so the UI stays
        // in charge of when its own text is touched.
        _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _pump.Tick += (_, _) => Drain();
        _pump.Start();

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => { Unsubscribe(); _pump.Stop(); _console.Stop(); };

        Render(App.Link.Current);
    }

    // ------------------------------------------------------------- lifecycle

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Link.Updated += OnLinkUpdated;
        _subscribed = true;
        _pump.Start();
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
        var idle = !_console.IsBusy;

        RunButton.IsEnabled = connected && idle;
        StopButton.IsEnabled = !idle;
        CommandBox.IsEnabled = connected;
    }

    private string? Host => App.Link.Current.ConsoleIp;

    // -------------------------------------------------------------- commands

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Run();
        }
        else if (e.Key == Key.Up && _historyIndex > 0)
        {
            // Shell-style history: Up walks back through what was run.
            _historyIndex--;
            CommandBox.Text = _history[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            CommandBox.Text = _history[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
    }

    private readonly List<string> _history = new();
    private int _historyIndex;

    private void QuickCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string command }) return;

        CommandBox.Text = command;
        Run();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => Run();

    private async void Run()
    {
        var host = Host;
        if (string.IsNullOrEmpty(host))
        {
            Append(ConsoleLineKind.Error, "Console not connected.");
            return;
        }

        var command = CommandBox.Text.Trim();
        if (command.Length == 0) return;

        _history.Add(command);
        _historyIndex = _history.Count;
        CommandBox.Clear();

        Render(App.Link.Current);
        await _console.RunAsync(host, command);
        Drain();
        Render(App.Link.Current);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _console.Stop();
        Append(ConsoleLineKind.Notice, "— stopping —");
    }

    // ---------------------------------------------------------------- output

    /// <summary>Moves whatever the service collected into the log.</summary>
    private void Drain()
    {
        var lines = _console.DrainPending();
        if (lines.Count == 0) return;

        foreach (var line in lines) Append(line.Kind, line.Text);
    }

    private void Append(ConsoleLineKind kind, string text)
    {
        // Early builds used "\n" literally; keep the display clean for those.
        text = text.TrimEnd('\n', '\r');
        if (text.Length == 0) return;

        var prefix = kind == ConsoleLineKind.Command ? "$ " : string.Empty;

        if (_log.Length > 0) _log.Append('\n');
        _log.Append(prefix).Append(text);

        // The output pane is a single TextBlock: colouring whole runs would mean
        // rebuilding it per line, which is expensive at this update rate. Only
        // the command echo is marked, via the "$ " prefix the design uses.
        OutputText.Text = _log.ToString();

        OutputScroll.ScrollToEnd();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        OutputText.Text = "Cleared.";
        OutputText.Foreground = (Brush)FindResource("TextMutedBrush");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_log.Length == 0) return;

        try
        {
            Clipboard.SetText(_log.ToString());
            SetStatus("Output copied to the clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy: {ex.Message}");
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        if (_log.Length == 0)
        {
            SetStatus("Nothing to save yet.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save console log",
            FileName = $"g350-console-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt",
            InitialDirectory = App.Settings.ScreenshotFolder,
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _log.ToString());
            SetStatus($"Saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save: {ex.Message}");
        }
    }

    private void SetStatus(string message)
    {
        if (_log.Length > 0) _log.Append('\n');
        _log.Append(message);
        OutputText.Text = _log.ToString();
        OutputScroll.ScrollToEnd();
    }
}
