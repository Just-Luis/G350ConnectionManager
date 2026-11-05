using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using G350ConnectionManager.Services;
using G350ConnectionManager.Views;

namespace G350ConnectionManager;

public partial class MainWindow : Window
{
    private const double ExpandedSidebarWidth = 230;
    private const double CollapsedSidebarWidth = 72;

    private bool _collapsed;
    private bool _initialising = true;

    private readonly Dictionary<string, Func<FrameworkElement>> _viewFactories = new()
    {
        ["Home"] = () => new HomeView(),
        ["Network"] = () => new NetworkView(),
        ["Screen"] = () => new ScreenView(),
        ["Files"] = () => new FilesView(),
        ["Settings"] = () => new SettingsView(),
    };

    private readonly Dictionary<string, FrameworkElement> _viewCache = new();

    public MainWindow()
    {
        InitializeComponent();

        _collapsed = App.Settings.SidebarCollapsed;
        ApplyCollapsedState(_collapsed);
        Navigate("Home");

        _initialising = false;
    }

    // ---------------------------------------------------------------- nav

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_initialising) return;
        if (sender is RadioButton { Tag: string tag })
            Navigate(tag);
    }

    private void Navigate(string tag)
    {
        if (!_viewFactories.TryGetValue(tag, out var factory)) return;

        if (!_viewCache.TryGetValue(tag, out var view))
        {
            view = factory();
            _viewCache[tag] = view;
        }
        ContentHost.Content = view;
    }

    // ---------------------------------------------------------- sidebar

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplyCollapsedState(_collapsed);

        App.Settings.SidebarCollapsed = _collapsed;
        App.Settings.Save();
    }

    private void ApplyCollapsedState(bool collapsed)
    {
        SidebarColumn.Width = new GridLength(collapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth);

        var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = visibility;
        CollapseText.Visibility = visibility;
        ConnectionPill.Visibility = visibility;

        CollapseIcon.Data = (Geometry)FindResource(collapsed ? "IconChevronRight" : "IconChevronLeft");
        CollapseButton.HorizontalAlignment = collapsed
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;
        CollapseButton.ToolTip = collapsed ? "Expand sidebar" : "Collapse sidebar";

        // Icon-only mode: drop the horizontal padding, centre the 22px glyph and
        // hide the labels. Padding lives on the template's Border via
        // TemplateBinding, so setting it on the RadioButton is enough.
        foreach (var nav in new[] { NavHome, NavNetwork, NavScreen, NavFiles, NavSettings })
        {
            nav.Padding = collapsed ? new Thickness(0) : new Thickness(14, 0, 14, 0);
            nav.HorizontalContentAlignment = collapsed
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
            nav.ToolTip = collapsed ? nav.Tag as string : null;

            if (nav.Content is not StackPanel panel) continue;

            foreach (var child in panel.Children)
            {
                if (child is TextBlock label)
                {
                    label.Visibility = visibility;
                    label.Margin = collapsed ? new Thickness(0) : new Thickness(12, 0, 0, 0);
                }
            }
        }
    }

    // -------------------------------------------------------- status pill

    /// <summary>
    /// Stage 1 has no probing yet, so the pill shows an idle state. Stage 2's
    /// LinkMonitor will drive this through the same method.
    /// </summary>
    public void SetConnectionStatus(bool connected, string headline, string? detail = null)
    {
        StatusDot.Fill = (Brush)FindResource(connected ? "StatusOkBrush" : "StatusIdleBrush");
        StatusText.Text = headline;
        StatusDetail.Text = detail ?? (connected ? string.Empty : "No console detected");
    }
}
