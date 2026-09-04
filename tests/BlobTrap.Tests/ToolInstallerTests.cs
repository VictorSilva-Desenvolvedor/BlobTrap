using BlobTrap.Core.Tools;
using Xunit;

namespace BlobTrap.Tests;

public class ToolInstallerTests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void ParseChecksum_FindsTheMatchingRowInAManifest()
    {
        var manifest = $"""
            0000000000000000000000000000000000000000000000000000000000000000  yt-dlp
            {Hash}  yt-dlp.exe
            1111111111111111111111111111111111111111111111111111111111111111  yt-dlp_macos
            """;

        Assert.Equal(Hash, ToolInstaller.ParseChecksum(manifest, "yt-dlp.exe"));
    }

    [Fact]
    public void ParseChecksum_AcceptsTheBinaryMarkerPrefix()
    {
        // sha256sum writes "*name" for files it read in binary mode.
        Assert.Equal(Hash, ToolInstaller.ParseChecksum($"{Hash} *yt-dlp.exe", "yt-dlp.exe"));
    }

    [Fact]
    public void ParseChecksum_ReadsASingleArtifactFileWithNoNameColumn()
    {
        Assert.Equal(Hash, ToolInstaller.ParseChecksum(Hash + "\n", fileName: null));
    }

    [Fact]
    public void ParseChecksum_ReturnsNullWhenTheFileIsNotListed()
    {
        Assert.Null(ToolInstaller.ParseChecksum($"{Hash}  outra-coisa.exe", "yt-dlp.exe"));
    }

    [Fact]
    public void ParseChecksum_IgnoresGarbageAndShortHashes()
    {
        Assert.Null(ToolInstaller.ParseChecksum("nao e um checksum\nabc123  yt-dlp.exe", "yt-dlp.exe"));
        Assert.Null(ToolInstaller.ParseChecksum(string.Empty, null));
    }
}
