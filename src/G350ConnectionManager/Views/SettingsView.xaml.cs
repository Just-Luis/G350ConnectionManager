using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;

namespace G350ConnectionManager.Views;

public partial class SettingsView : UserControl
{
    private bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        BuildAccentSwatches();
        LoadFromSettings();
        _loading = false;
    }

    // ------------------------------------------------------------ accent

    private void BuildAccentSwatches()
    {
        foreach (var (name, hex) in ThemeService.Accents)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);

            var swatch = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name,
                Tag = hex
            };

            swatch.MouseLeftButtonUp += (_, _) => SelectAccent(hex);
            AccentPanel.Children.Add(swatch);
        }

        HighlightActiveSwatch(App.Settings.AccentHex);
    }

    private void HighlightActiveSwatch(string hex)
    {
        foreach (var child in AccentPanel.Children)
        {
            if (child is not Border b || b.Tag is not string tag) continue;
            var active = string.Equals(tag, hex, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = active
                ? (Brush)FindResource("TextPrimaryBrush")
                : Brushes.Transparent;
        }
    }

    private void SelectAccent(string hex)
    {
        if (_loading) return;

        App.Settings.AccentHex = hex;
        App.Settings.Save();
        App.Theme.ApplyAccent(hex);
        HighlightActiveSwatch(hex);
    }

    // ------------------------------------------------------------- theme

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var dark = ReferenceEquals(sender, BlackTheme) && BlackTheme.IsChecked == true;
        if (!dark && !ReferenceEquals(sender, WhiteTheme)) return;

        App.Settings.DarkTheme = dark;
        App.Settings.Save();
        App.Theme.ApplyTheme(
            dark ? ThemeService.AppTheme.Dark : ThemeService.AppTheme.Light,
            App.Settings.AccentHex);

        // Theme swap replaces brushes, so re-assert the active swatch outline.
        HighlightActiveSwatch(App.Settings.AccentHex);
    }

    // ----------------------------------------------------------- sidebar

    private void Collapsed_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        App.Settings.StartCollapsed = CollapsedCheck.IsChecked == true;
        App.Settings.Save();
    }

    // ------------------------------------------------------------- load

    private void LoadFromSettings()
    {
        var s = App.Settings;

        if (s.DarkTheme) BlackTheme.IsChecked = true;
        else WhiteTheme.IsChecked = true;

        CollapsedCheck.IsChecked = s.StartCollapsed;
        ScreenshotFolderBox.Text = s.ScreenshotFolder;
        RecordingFolderBox.Text = s.RecordingFolder;
    }

    // ----------------------------------------------------------- folders

    private void ChooseScreenshotFolder_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Where should screenshots be saved?",
                                App.Settings.ScreenshotFolder);
        if (picked is null) return;

        App.Settings.ScreenshotFolder = picked;
        ScreenshotFolderBox.Text = picked;
        App.Settings.Save();
    }

    private void ChooseRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Where should recordings be saved?",
                                App.Settings.RecordingFolder);
        if (picked is null) return;

        App.Settings.RecordingFolder = picked;
        RecordingFolderBox.Text = picked;
        App.Settings.Save();
    }

    /// <summary>Points both outputs at one folder.</summary>
    private void SameFolder_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Use one folder for screenshots and recordings",
                                App.Settings.ScreenshotFolder);
        if (picked is null) return;

        App.Settings.ScreenshotFolder = picked;
        App.Settings.RecordingFolder = picked;
        ScreenshotFolderBox.Text = picked;
        RecordingFolderBox.Text = picked;
        App.Settings.Save();
    }

    private string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(current)
                ? current
                : DefaultCaptureFolder()
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string DefaultCaptureFolder() => AppSettings.DefaultCaptureFolder();
}
