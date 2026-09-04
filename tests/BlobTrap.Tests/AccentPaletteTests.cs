using System.Windows.Media;
using BlobTrap.App.Theming;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// The palette's channel order is decided at runtime by matching entry 3 against DWM's
/// AccentColor. These tests use an asymmetric colour on purpose: Windows' default blue
/// (#0078D4) reads differently as RGB and as BGR, so a swapped decode cannot hide. A colour
/// where red equals blue - a green or a grey accent - would pass either way and prove nothing.
/// </summary>
public class AccentPaletteTests
{
    private static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>Eight four-byte entries, with <paramref name="reversed"/> writing B,G,R.</summary>
    private static byte[] BuildPalette(bool reversed)
    {
        var shades = new[]
        {
            Color.FromRgb(0x99, 0xEB, 0xFF), // Light3
            Color.FromRgb(0x4C, 0xC2, 0xFF), // Light2
            Color.FromRgb(0x00, 0x91, 0xF8), // Light1
            Accent,                          // Base
            Color.FromRgb(0x00, 0x6C, 0xBE), // Dark1
            Color.FromRgb(0x00, 0x4A, 0x83), // Dark2
            Color.FromRgb(0x00, 0x30, 0x56), // Dark3
            Color.FromRgb(0x4C, 0x4A, 0x48), // neutral
        };

        var bytes = new byte[32];
        for (var i = 0; i < shades.Length; i++)
        {
            var c = shades[i];
            bytes[i * 4] = reversed ? c.B : c.R;
            bytes[i * 4 + 1] = c.G;
            bytes[i * 4 + 2] = reversed ? c.R : c.B;
            bytes[i * 4 + 3] = 0xFF;
        }

        return bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesTheSamePaletteWhicheverWayTheBytesRun(bool reversed)
    {
        var palette = AccentPalette.FromBytes(BuildPalette(reversed), Accent);
        Assert.NotNull(palette);

        Assert.Equal(Accent, palette!.Base);
        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), palette.Light2);
        Assert.Equal(Color.FromRgb(0x00, 0x6C, 0xBE), palette.Dark1);
        Assert.Equal(Color.FromRgb(0x99, 0xEB, 0xFF), palette.Light3);
    }

    [Fact]
    public void ADarkThemeTakesTheLightenedAccentWithBlackOnTop()
    {
        var palette = AccentPalette.FromBytes(BuildPalette(reversed: false), Accent);
        Assert.NotNull(palette);

        // WinUI fills accent controls with Light2 on dark surfaces, and writes black on them.
        Assert.Equal(palette!.Light2, palette.FillFor(AppTheme.Dark));
        Assert.Equal(Colors.Black, palette.OnAccentFor(AppTheme.Dark));
    }

    [Fact]
    public void ALightThemeTakesTheDarkenedAccentWithWhiteOnTop()
    {
        var palette = AccentPalette.FromBytes(BuildPalette(reversed: false), Accent);
        Assert.NotNull(palette);

        Assert.Equal(palette!.Dark1, palette.FillFor(AppTheme.Light));
        Assert.Equal(Colors.White, palette.OnAccentFor(AppTheme.Light));
    }

    [Fact]
    public void AnAccentMatchingNeitherOrderIsRefusedRatherThanGuessed()
    {
        // Happens when DWM's AccentColor is missing and the fallback blue stands in for it:
        // the palette is real but the reference colour is not, so the order cannot be read.
        // Returning null sends the caller to the single-colour path instead of guessing.
        var bytes = BuildPalette(reversed: false);
        var unrelated = Color.FromRgb(0x11, 0x22, 0x33);

        Assert.Null(AccentPalette.FromBytes(bytes, unrelated));
    }

    [Fact]
    public void ASymmetricAccentIsReadAsWrittenSoOtherShadesSurvive()
    {
        // This machine's green accent (#107C10) has red equal to blue, so entry 3 matches both
        // orders. Only entry 3 is symmetric, so a tie broken towards BGR would swap the
        // channels of every other shade - checking Base alone would miss exactly that.
        var green = Color.FromRgb(0x10, 0x7C, 0x10);
        var lightest = Color.FromRgb(0x95, 0xEF, 0x81);

        var bytes = new byte[32];
        bytes[0] = lightest.R;
        bytes[1] = lightest.G;
        bytes[2] = lightest.B;
        bytes[12] = green.R;
        bytes[13] = green.G;
        bytes[14] = green.B;

        var palette = AccentPalette.FromBytes(bytes, green);

        Assert.NotNull(palette);
        Assert.Equal(green, palette!.Base);
        Assert.Equal(lightest, palette.Light3);
    }
}
