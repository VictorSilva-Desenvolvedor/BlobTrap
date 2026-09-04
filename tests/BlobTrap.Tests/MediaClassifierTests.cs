using BlobTrap.Core.Models;
using BlobTrap.Core.Sniffing;
using Xunit;

namespace BlobTrap.Tests;

public class MediaClassifierTests
{
    [Theory]
    [InlineData("https://cdn.example.com/master.m3u8", MediaKind.HlsPlaylist)]
    [InlineData("https://cdn.example.com/stream/index.m3u8?token=abc", MediaKind.HlsPlaylist)]
    [InlineData("https://cdn.example.com/manifest.mpd", MediaKind.DashManifest)]
    [InlineData("https://cdn.example.com/movie.mp4", MediaKind.ProgressiveVideo)]
    [InlineData("https://cdn.example.com/clip.webm", MediaKind.ProgressiveVideo)]
    [InlineData("https://cdn.example.com/song.mp3", MediaKind.ProgressiveAudio)]
    [InlineData("https://cdn.example.com/legenda.vtt", MediaKind.Subtitle)]
    [InlineData("https://cdn.example.com/video/seg-00012.ts", MediaKind.MediaSegment)]
    [InlineData("https://cdn.example.com/video/chunk-5.m4s", MediaKind.MediaSegment)]
    [InlineData("https://example.com/page.html", MediaKind.Unknown)]
    [InlineData("https://example.com/logo.png", MediaKind.Unknown)]
    public void Classify_ByPath(string url, MediaKind expected)
    {
        Assert.Equal(expected, MediaClassifier.Classify(new Uri(url), null));
    }

    [Theory]
    [InlineData("https://cdn.example.com/video/init.mp4")]
    [InlineData("https://cdn.example.com/video/init-720p.mp4")]
    [InlineData("https://cdn.example.com/video/00042.mp4")]
    [InlineData("https://cdn.example.com/video/seg-9.mp4")]
    public void Classify_TreatsFragmentNamedMp4AsSegment(string url)
    {
        // Listing every chunk of a DASH stream as a downloadable file would bury the manifest.
        Assert.Equal(MediaKind.MediaSegment, MediaClassifier.Classify(new Uri(url), null));
    }

    [Fact]
    public void Classify_PrefersPathOverGenericMimeType()
    {
        // Plenty of CDNs serve playlists as text/plain or octet-stream.
        var url = new Uri("https://cdn.example.com/live/index.m3u8");

        Assert.Equal(MediaKind.HlsPlaylist, MediaClassifier.Classify(url, "text/plain"));
        Assert.Equal(MediaKind.HlsPlaylist, MediaClassifier.Classify(url, "application/octet-stream"));
    }

    [Fact]
    public void Classify_UsesMimeWhenThePathHasNoExtension()
    {
        var url = new Uri("https://api.example.com/v1/playback/12345");

        Assert.Equal(MediaKind.HlsPlaylist, MediaClassifier.Classify(url, "application/vnd.apple.mpegurl"));
        Assert.Equal(MediaKind.DashManifest, MediaClassifier.Classify(url, "application/dash+xml"));
        Assert.Equal(MediaKind.ProgressiveVideo, MediaClassifier.Classify(url, "video/mp4"));
    }

    [Fact]
    public void Classify_MimeMarksTransportStreamAsSegment()
    {
        var url = new Uri("https://cdn.example.com/live/part");

        Assert.Equal(MediaKind.MediaSegment, MediaClassifier.Classify(url, "video/mp2t"));
    }

    [Fact]
    public void Classify_FindsExtensionHiddenInQueryString()
    {
        var url = new Uri("https://cdn.example.com/download?file=filme-completo.mp4&token=xyz");

        Assert.Equal(MediaKind.ProgressiveVideo, MediaClassifier.Classify(url, null));
    }

    [Theory]
    [InlineData("https://pubads.g.doubleclick.net/gampad/ads?video.mp4")]
    [InlineData("https://cdn.example.com/ads/preroll.mp4")]
    public void Classify_DropsAdvertisingHostsAndPaths(string url)
    {
        Assert.True(MediaClassifier.IsNoise(new Uri(url)));
        Assert.Equal(MediaKind.Unknown, MediaClassifier.Classify(new Uri(url), "video/mp4"));
    }

    [Fact]
    public void SegmentFamilyKey_GroupsChunksFromTheSameStream()
    {
        var a = MediaClassifier.SegmentFamilyKey(new Uri("https://cdn.example.com/v/720/seg-1.ts"));
        var b = MediaClassifier.SegmentFamilyKey(new Uri("https://cdn.example.com/v/720/seg-2.ts"));
        var other = MediaClassifier.SegmentFamilyKey(new Uri("https://cdn.example.com/v/1080/seg-1.ts"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
    }

    [Fact]
    public void Classify_IgnoresNonHttpSchemes()
    {
        Assert.Equal(MediaKind.Unknown, MediaClassifier.Classify(new Uri("blob:https://example.com/abc-123"), null));
        Assert.Equal(MediaKind.Unknown, MediaClassifier.Classify(new Uri("data:video/mp4;base64,AAAA"), "video/mp4"));
    }
}
