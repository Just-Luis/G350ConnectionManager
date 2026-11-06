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
    // Both start in the same place. Settings is where they are pointed elsewhere.
    public string ScreenshotFolder { get; set; } = DefaultCaptureFolder();
    public string RecordingFolder { get; set; } = DefaultCaptureFolder();

    /// <summary>
    /// Fills in an empty folder setting.
    ///
    /// settings.json is additive and never migrated, so a file written by an
    /// older build can carry a blank value and leave the picker showing nothing.
    /// Only blanks are replaced; a folder the user chose is left alone.
    /// </summary>
    public void NormaliseFolders()
    {
        if (string.IsNullOrWhiteSpace(ScreenshotFolder))
            ScreenshotFolder = DefaultCaptureFolder();
        if (string.IsNullOrWhiteSpace(RecordingFolder))
            RecordingFolder = DefaultCaptureFolder();
    }

    /// <summary>
    /// Screen pipeline quality. Persisted so the chosen trade-off survives a
    /// restart; the sliders are the only way to change it.
    /// </summary>
    public CaptureSettings Capture { get; set; } = CaptureSettings.Default;

    /// <summary>
    /// Pictures\G350 Captures. Both screenshots and recordings default here; the
    /// Settings page is where they are pointed elsewhere.
    /// </summary>
    public static string DefaultCaptureFolder()
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
                if (loaded is not null)
                {
                    loaded.NormaliseFolders();
                    return loaded;
                }
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
