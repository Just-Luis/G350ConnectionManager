using System.Windows;
using G350ConnectionManager.Models;
using G350ConnectionManager.Services;

namespace G350ConnectionManager;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static ThemeService Theme { get; private set; } = null!;
    public static LinkMonitor Link { get; private set; } = null!;

    /// <summary>
    /// Shared internet-sharing state. Both the Home page and the Network page
    /// read and write through this instance, so a toggle in one is reflected in
    /// the other instead of each page keeping a private copy.
    /// </summary>
    public static SharingCoordinator Sharing { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ------------------------------------------------------ helper mode
        //
        // The app re-launches its own binary elevated with --ics-probe to read
        // Windows Internet Connection Sharing. That path must run before any UI
        // setup: no window, no theme, no settings file, no link polling.
        if (e.Args.Length > 0 && e.Args[0] == IcsService.ProbeArgument)
        {
            var outPath = e.Args.Length > 1 ? e.Args[1] : string.Empty;
            var code = string.IsNullOrEmpty(outPath)
                ? 2
                : IcsService.RunProbe(outPath);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == IcsService.ToggleArgument)
        {
            // --ics-toggle <enable|disable> <source> <target> <out.json> [targetGuid]
            var outPath = e.Args.Length > 4 ? e.Args[4] : string.Empty;
            var code = string.IsNullOrEmpty(outPath)
                ? 2
                : IcsService.RunToggle(e.Args, outPath);
            Shutdown(code);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == IcsService.DiagArgument)
        {
            // --ics-diag <out.txt>
            var outPath = e.Args.Length > 1 ? e.Args[1] : string.Empty;
            var code = string.IsNullOrEmpty(outPath)
                ? 2
                : IcsService.RunDiag(outPath);
            Shutdown(code);
            return;
        }

        Settings = AppSettings.Load();

        Theme = new ThemeService(this);
        Theme.Initialize(
            Settings.DarkTheme ? ThemeService.AppTheme.Dark : ThemeService.AppTheme.Light,
            Settings.AccentHex);

        Link = new LinkMonitor();
        Sharing = new SharingCoordinator();

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
