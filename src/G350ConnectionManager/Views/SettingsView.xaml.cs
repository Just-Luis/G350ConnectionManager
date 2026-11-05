using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        ScreenshotFolderBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            App.Settings.ScreenshotFolder = ScreenshotFolderBox.Text;
            App.Settings.Save();
        };
        RecordingFolderBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            App.Settings.RecordingFolder = RecordingFolderBox.Text;
            App.Settings.Save();
        };
    }
}
