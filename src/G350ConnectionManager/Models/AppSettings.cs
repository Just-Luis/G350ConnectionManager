using System.IO;
using System.Text.Json;
using G350ConnectionManager.Services;

namespace G350ConnectionManager.Models;

/// <summary>
/// User-visible preferences. Stored under %APPDATA%\G350ConnectionManager\settings.json.
/// Fields are additive: unknown keys are ignored and missing keys take defaults,
/// so the file survives version upgrades without a migration step.
/// </summary>
public sealed class AppSettings
{
    public bool DarkTheme { get; set; }
    public string AccentHex { get; set; } = ThemeService.DefaultAccent;
    public bool StartCollapsed { get; set; }
    public bool SidebarCollapsed { get; set; }
    public string ScreenshotFolder { get; set; } = DefaultCaptureFolder();
    public string RecordingFolder { get; set; } = DefaultCaptureFolder();

    private static string DefaultCaptureFolder()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pictures, "G350 Captures");
    }

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "G350ConnectionManager");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings must never block startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Non-fatal: the app keeps working with in-memory state.
        }
    }
}
