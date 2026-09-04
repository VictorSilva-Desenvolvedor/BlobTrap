using System.Windows.Media;
using Microsoft.Win32;

namespace BlobTrap.App.Theming;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// The Windows personalisation colours, read straight from the registry.
///
/// Windows stores eight accent shades in HKCU\...\Explorer\Accent\AccentPalette, ordered from
/// lightest to darkest with the user's chosen colour at index 3. WinUI picks a different shade
/// depending on the background: a dark surface needs a lighter accent to stay legible, which is
/// why matching "the system accent" means picking from this palette, not using one colour.
/// </summary>
public sealed record AccentPalette
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>Windows' own default blue, used when the registry has nothing to say.</summary>
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    public required Color Light3 { get; init; }
    public required Color Light2 { get; init; }
    public required Color Light1 { get; init; }
    public required Color Base { get; init; }
    public required Color Dark1 { get; init; }
    public required Color Dark2 { get; init; }
    public required Color Dark3 { get; init; }

    /// <summary>The accent fill for buttons and progress, chosen for contrast against the theme.</summary>
    public Color FillFor(AppTheme theme) => theme == AppTheme.Dark ? Light2 : Dark1;

    /// <summary>Accent used as text or a thin stroke, where the fill would be too heavy.</summary>
    public Color TextFor(AppTheme theme) => theme == AppTheme.Dark ? Light2 : Dark2;

    /// <summary>
    /// What to draw on top of <see cref="FillFor"/>. Windows uses black on the lightened accent
    /// in dark mode and white on the darkened one in light mode.
    /// </summary>
    public Color OnAccentFor(AppTheme theme) =>
        theme == AppTheme.Dark ? Colors.Black : Colors.White;

    public static AppTheme ReadTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        var value = key?.GetValue("AppsUseLightTheme");

        // The value is absent on some installs; Windows treats that as light.
        return value is int light && light == 0 ? AppTheme.Dark : AppTheme.Light;
    }

    public static AccentPalette Read()
    {
        var accent = ReadAccentColor();
        var bytes = ReadPaletteBytes();

        // Falls back to the single-colour path whenever the palette cannot be trusted:
        // either it is absent, or its channel order could not be established.
        return (bytes is null ? null : FromBytes(bytes, accent)) ?? Derive(accent);
    }

    private static byte[]? ReadPaletteBytes()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AccentKey);

        // Eight four-byte entries; anything shorter is not a palette we can trust.
        return key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } bytes ? bytes : null;
    }

    /// <summary>
    /// Reads the palette, deciding the channel order by checking it against DWM's AccentColor.
    ///
    /// The two registry values describe the same colour, and AccentColor's layout is known
    /// (ABGR), so entry 3 of the palette identifies which way round the palette bytes run. This
    /// beats hardcoding an order: a wrong guess only shows up on accents where red and blue
    /// differ, so it would sail past any check made on a machine whose accent happens to be
    /// symmetric - green or grey, for instance.
    ///
    /// Returns null when neither order matches, which means the reference colour is not the
    /// one this palette was built from - AccentColor missing and the fallback blue standing in
    /// for it, say. Guessing an order there would be the very thing this method exists to
    /// avoid, so the caller drops to the single-colour path instead.
    /// </summary>
    internal static AccentPalette? FromBytes(byte[] bytes, Color accent)
    {
        var matchesRgb = MatchesRgb(bytes, 3, accent);
        var matchesBgr = MatchesBgr(bytes, 3, accent);

        if (!matchesRgb && !matchesBgr) return null;

        // A symmetric reference colour (red equal to blue) matches both ways. Only entry 3 is
        // symmetric though - the other shades are not - so on a tie the bytes must be read as
        // written, or every remaining shade comes out with its channels swapped.
        var reversed = !matchesRgb;

        Color At(int index)
        {
            var offset = index * 4;
            return reversed
                ? Color.FromRgb(bytes[offset + 2], bytes[offset + 1], bytes[offset])
                : Color.FromRgb(bytes[offset], bytes[offset + 1], bytes[offset + 2]);
        }

        return new AccentPalette
        {
            Light3 = At(0),
            Light2 = At(1),
            Light1 = At(2),
            Base = At(3),
            Dark1 = At(4),
            Dark2 = At(5),
            Dark3 = At(6),
        };
    }

    private static bool MatchesRgb(byte[] bytes, int index, Color color) =>
        bytes[index * 4] == color.R && bytes[index * 4 + 1] == color.G && bytes[index * 4 + 2] == color.B;

    private static bool MatchesBgr(byte[] bytes, int index, Color color) =>
        bytes[index * 4] == color.B && bytes[index * 4 + 1] == color.G && bytes[index * 4 + 2] == color.R;

    private static Color ReadAccentColor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
        if (key?.GetValue("AccentColor") is not int packed) return FallbackAccent;

        // DWM stores the accent as ABGR, not ARGB.
        return Color.FromRgb((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF));
    }

    /// <summary>Builds a palette by lightening and darkening a single colour, for the fallback path.</summary>
    private static AccentPalette Derive(Color accent) => new()
    {
        Light3 = Blend(accent, Colors.White, 0.60),
        Light2 = Blend(accent, Colors.White, 0.40),
        Light1 = Blend(accent, Colors.White, 0.20),
        Base = accent,
        Dark1 = Blend(accent, Colors.Black, 0.15),
        Dark2 = Blend(accent, Colors.Black, 0.30),
        Dark3 = Blend(accent, Colors.Black, 0.45),
    };

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));
}
