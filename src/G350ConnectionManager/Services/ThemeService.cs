using System.Windows;
using System.Windows.Media;

namespace G350ConnectionManager.Services;

/// <summary>
/// Applies light/dark theme dictionaries and computes the accent brushes at
/// runtime, mirroring the mock's {{accent}} / {{onAccent}} / {{soft}} template:
///
///     soft     = accent + "33"                      (20 % alpha overlay)
///     onAccent = #111111 if luminance > 150 else #FFFFFF
///     luminance = 0.299R + 0.587G + 0.114B
/// </summary>
public sealed class ThemeService
{
    public enum AppTheme { Light, Dark }

    /// <summary>Highlight colours offered by the Settings screen, in mock order.</summary>
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Blue",   "#2563EB"),
        ("Teal",   "#0D9488"),
        ("Green",  "#16A34A"),
        ("Orange", "#EA580C"),
        ("Pink",   "#DB2777"),
        ("Purple", "#7C3AED"),
    };

    public const string DefaultAccent = "#2563EB";

    private readonly Application _app;
    private readonly ResourceDictionary _light;
    private readonly ResourceDictionary _dark;
    private ResourceDictionary _current;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Light;
    public string CurrentAccent { get; private set; } = DefaultAccent;

    public ThemeService(Application app)
    {
        _app = app;
        _light = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute)
        };
        _dark = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute)
        };
        _current = _light;
    }

    /// <summary>Raised after a theme or accent change so views can re-render if needed.</summary>
    public event EventHandler? ThemeChanged;

    public void Initialize(AppTheme theme = AppTheme.Light, string? accentHex = null)
    {
        ApplyTheme(theme, accentHex, notify: false);
    }

    public void ApplyTheme(AppTheme theme, string? accentHex = null, bool notify = true)
    {
        CurrentTheme = theme;
        if (!string.IsNullOrWhiteSpace(accentHex))
            CurrentAccent = NormalizeHex(accentHex);

        var target = theme == AppTheme.Dark ? _dark : _light;

        // Swap the theme dictionary in place so DynamicResource lookups refresh.
        var merged = _app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            var idx = merged.IndexOf(_current);
            if (idx >= 0) merged.RemoveAt(idx);
        }
        merged.Insert(0, target);
        _current = target;

        ApplyAccent(CurrentAccent);

        if (notify) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyAccent(string accentHex)
    {
        CurrentAccent = NormalizeHex(accentHex);
        var color = (Color)ColorConverter.ConvertFromString(CurrentAccent);

        _app.Resources["AccentBrush"] = Freeze(new SolidColorBrush(color));
        _app.Resources["SoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)));
        _app.Resources["OnAccentBrush"] = Freeze(new SolidColorBrush(OnAccentFor(color)));
    }

    /// <summary>Same luminance rule the mock's DCLogic uses to pick readable text.</summary>
    public static Color OnAccentFor(Color c)
    {
        var luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return luminance > 150 ? Color.FromRgb(0x11, 0x11, 0x11) : Colors.White;
    }

    private static string NormalizeHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DefaultAccent;
        hex = hex.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        // Expand #RGB shorthand the way the mock does.
        if (hex.Length == 4)
            hex = $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}";
        return hex.Length == 7 ? hex.ToUpperInvariant() : DefaultAccent;
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
