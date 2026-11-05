using System.Windows;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;

namespace G350ConnectionManager;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static ThemeService Theme { get; private set; } = null!;
    public static LinkMonitor Link { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();

        Theme = new ThemeService(this);
        Theme.Initialize(
            Settings.DarkTheme ? ThemeService.AppTheme.Dark : ThemeService.AppTheme.Light,
            Settings.AccentHex);

        Link = new LinkMonitor();

        if (Settings.StartCollapsed)
            Settings.SidebarCollapsed = true;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Start polling only after the window exists, so the first snapshot
        // can be delivered straight to the subscribers.
        Link.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Link?.Dispose();
        base.OnExit(e);
    }
}
