using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlobTrap.App.Theming;

/// <summary>
/// Builds the app's brush set from the current Windows theme and accent, and swaps it live when
/// the user changes either in Settings.
///
/// Colours are pushed into <see cref="Application.Resources"/> under fixed keys, so XAML binds
/// them with DynamicResource and repaints itself without the window being recreated.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static AppTheme Theme { get; private set; } = AppTheme.Dark;

    public static AccentPalette Accent { get; private set; } = AccentPalette.Read();

    /// <summary>Raised after the resource dictionary has been swapped.</summary>
    public static event EventHandler? Changed;

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // General covers both the light/dark switch and an accent colour change.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;

        var application = Application.Current;
        if (application is null) return;

        application.Dispatcher.BeginInvoke(() =>
        {
            var theme = AccentPalette.ReadTheme();
            var accent = AccentPalette.Read();

            if (theme == Theme && accent == Accent) return;

            Theme = theme;
            Accent = accent;
            Apply();
            Changed?.Invoke(null, EventArgs.Empty);
        });
    }

    public static void Apply()
    {
        var application = Application.Current;
        if (application is null) return;

        Theme = AccentPalette.ReadTheme();
        Accent = AccentPalette.Read();

        var dictionary = Build(Theme, Accent);

        if (_current is not null) application.Resources.MergedDictionaries.Remove(_current);
        application.Resources.MergedDictionaries.Add(dictionary);
        _current = dictionary;
    }

    /// <summary>
    /// The WinUI 3 neutral ramp, transcribed for both themes. These are the same values Windows
    /// itself paints with, which is what makes the app sit next to Explorer without looking off.
    /// </summary>
    private static ResourceDictionary Build(AppTheme theme, AccentPalette accent)
    {
        var dictionary = new ResourceDictionary();

        void Set(string key, Color color) => dictionary[key] = Freeze(new SolidColorBrush(color));
        void SetColor(string key, Color color) => dictionary[key] = color;

        if (theme == AppTheme.Dark)
        {
            SetColor("MicaBaseColor", Rgb(0x20, 0x20, 0x20));

            Set("AppBackground", Rgb(0x20, 0x20, 0x20));
            Set("LayerBackground", Rgb(0x27, 0x27, 0x27));
            Set("CardBackground", Rgb(0x2B, 0x2B, 0x2B));
            Set("CardBackgroundHover", Rgb(0x32, 0x32, 0x32));
            Set("CardBackgroundPressed", Rgb(0x26, 0x26, 0x26));
            Set("ControlBackground", Rgb(0x2D, 0x2D, 0x2D));
            Set("ControlBackgroundHover", Rgb(0x35, 0x35, 0x35));

            Set("StrokeDefault", Rgb(0x38, 0x38, 0x38));
            Set("StrokeSubtle", Rgb(0x2E, 0x2E, 0x2E));
            Set("Divider", Rgb(0x30, 0x30, 0x30));

            Set("TextPrimary", Rgb(0xFF, 0xFF, 0xFF));
            Set("TextSecondary", Rgb(0xC5, 0xC5, 0xC5));
            Set("TextTertiary", Rgb(0x8C, 0x8C, 0x8C));
            Set("TextDisabled", Rgb(0x6B, 0x6B, 0x6B));

            Set("Success", Rgb(0x6C, 0xCB, 0x5F));
            Set("Danger", Rgb(0xFF, 0x99, 0xA4));
            Set("Warning", Rgb(0xFC, 0xE1, 0x00));

            // Badge hues are lightened for dark surfaces and take black text, the same
            // treatment Windows gives the accent, so they stay legible next to it.
            Set("BadgeHls", Rgb(0xB3, 0x92, 0xF0));
            Set("BadgeDash", Rgb(0x6B, 0xB8, 0xF0));
            Set("BadgeFile", Rgb(0x7F, 0xD1, 0xA0));
            Set("BadgeAudio", Rgb(0xF0, 0xB8, 0x6B));
            Set("BadgeSubtitle", Rgb(0x9A, 0xA5, 0xB1));
            Set("BadgeOther", Rgb(0x9E, 0x9E, 0x9E));
        }
        else
        {
            SetColor("MicaBaseColor", Rgb(0xF3, 0xF3, 0xF3));

            Set("AppBackground", Rgb(0xF3, 0xF3, 0xF3));
            Set("LayerBackground", Rgb(0xFB, 0xFB, 0xFB));
            Set("CardBackground", Rgb(0xFF, 0xFF, 0xFF));
            Set("CardBackgroundHover", Rgb(0xF6, 0xF6, 0xF6));
            Set("CardBackgroundPressed", Rgb(0xF0, 0xF0, 0xF0));
            Set("ControlBackground", Rgb(0xFF, 0xFF, 0xFF));
            Set("ControlBackgroundHover", Rgb(0xF9, 0xF9, 0xF9));

            Set("StrokeDefault", Rgb(0xE5, 0xE5, 0xE5));
            Set("StrokeSubtle", Rgb(0xEB, 0xEB, 0xEB));
            Set("Divider", Rgb(0xE0, 0xE0, 0xE0));

            Set("TextPrimary", Rgb(0x1B, 0x1B, 0x1B));
            Set("TextSecondary", Rgb(0x5D, 0x5D, 0x5D));
            Set("TextTertiary", Rgb(0x8A, 0x8A, 0x8A));
            Set("TextDisabled", Rgb(0xA0, 0xA0, 0xA0));

            Set("Success", Rgb(0x0F, 0x7B, 0x0F));
            Set("Danger", Rgb(0xC4, 0x2B, 0x1C));
            Set("Warning", Rgb(0x9D, 0x5D, 0x00));

            // Darkened for light surfaces, where they take white text instead.
            Set("BadgeHls", Rgb(0x6B, 0x4F, 0xBF));
            Set("BadgeDash", Rgb(0x1E, 0x6F, 0xB8));
            Set("BadgeFile", Rgb(0x1B, 0x7F, 0x4B));
            Set("BadgeAudio", Rgb(0x8A, 0x59, 0x10));
            Set("BadgeSubtitle", Rgb(0x4A, 0x55, 0x60));
            Set("BadgeOther", Rgb(0x6B, 0x6B, 0x6B));
        }

        var fill = accent.FillFor(theme);

        Set("AccentFill", fill);
        Set("AccentFillHover", Shade(fill, theme == AppTheme.Dark ? 0.10 : -0.10));
        Set("AccentFillPressed", Shade(fill, theme == AppTheme.Dark ? -0.12 : -0.20));
        Set("AccentText", accent.TextFor(theme));
        Set("OnAccent", accent.OnAccentFor(theme));

        dictionary["IsDarkTheme"] = theme == AppTheme.Dark;

        return dictionary;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Lightens for a positive amount, darkens for a negative one.</summary>
    private static Color Shade(Color color, double amount)
    {
        var target = amount >= 0 ? Colors.White : Colors.Black;
        var strength = Math.Abs(amount);

        return Color.FromRgb(
            (byte)(color.R + (target.R - color.R) * strength),
            (byte)(color.G + (target.G - color.G) * strength),
            (byte)(color.B + (target.B - color.B) * strength));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
