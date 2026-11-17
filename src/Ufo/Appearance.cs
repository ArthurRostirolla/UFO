using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Ufo;

/// <summary>A background family: page colour plus the hairline and orbit-ring tones that sit on it.</summary>
public sealed record AppTheme(string Name, Color Background, Color Line, Color Ring);

/// <summary>App preferences from the settings screen; applied right away and kept in %AppData%\UFO.</summary>
public sealed record AppPreferences(string AccentHex, string ThemeName, bool RunInBackground, bool ShowAdvanced = false, Ufo.Clips.ClipSettings? Clips = null)
{
    public static AppPreferences Default => new("#FF5A1F", "Grafite", RunInBackground: false);
}

/// <summary>
/// Colour engine: every colour in the UI is a dynamic resource, so replacing the brushes in the
/// application resources recolours the whole app (saucer included) while it runs.
/// </summary>
public static class Appearance
{
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Laranja", "#FF5A1F"), ("Ciano", "#2FD8C8"), ("Lima", "#B8F03C"),
        ("Rosa", "#FF6FC8"), ("Violeta", "#8C7BFF"), ("Âmbar", "#FFB020"),
    ];

    public static readonly AppTheme[] Themes =
    [
        new("Grafite", Rgb("#0B0B0C"), Rgb("#232322"), Rgb("#34342F")),
        new("Espaço", Rgb("#0A0E18"), Rgb("#1B2233"), Rgb("#2A3550")),
        new("Ameixa", Rgb("#120B18"), Rgb("#2A1D33"), Rgb("#3B2A47")),
    ];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UFO", "preferences.json");

    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? AppPreferences.Default
                : AppPreferences.Default;
        }
        catch (JsonException)
        {
            return AppPreferences.Default;
        }
    }

    public static void Save(AppPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static AppTheme ThemeNamed(string name) => Themes.FirstOrDefault(theme => theme.Name == name) ?? Themes[0];

    public static void Apply(AppPreferences preferences)
    {
        ResourceDictionary resources = Application.Current.Resources;
        Color accent = TryRgb(preferences.AccentHex) ?? Rgb(AppPreferences.Default.AccentHex);
        AppTheme theme = ThemeNamed(preferences.ThemeName);
        resources["AccentBrush"] = Frozen(accent);
        resources["AccentWashBrush"] = Frozen(Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        resources["UfoLightBrush"] = Frozen(accent);
        resources["BgBrush"] = Frozen(theme.Background);
        resources["LineBrush"] = Frozen(theme.Line);
        resources["RingBrush"] = Frozen(theme.Ring);
        // Derived from the background so each theme gets matching raised surfaces and control borders.
        Color ink = Rgb("#EDEDEA");
        resources["SurfaceBrush"] = Frozen(Blend(theme.Background, ink, 0.07));
        resources["ControlBorderBrush"] = Frozen(Blend(theme.Background, ink, 0.20));
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    public static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color? TryRgb(string hex)
    {
        try
        {
            return Rgb(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
