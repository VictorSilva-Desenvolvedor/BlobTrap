using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Tools;
using BlobTrap.Core.Util;
using Xunit;

namespace BlobTrap.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("Video: o retorno", "Video o retorno")]
    [InlineData("a/b\\c*d?e", "a b c d e")]
    [InlineData("   espacos   demais   ", "espacos demais")]
    [InlineData("nome.", "nome")]
    public void SanitizeFileName_StripsWhatWindowsRejects(string input, string expected)
    {
        Assert.Equal(expected, Naming.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_EscapesReservedDeviceNames()
    {
        Assert.Equal("CON_", Naming.SanitizeFileName("CON"));
        Assert.Equal("video", Naming.SanitizeFileName("   "));
    }

    [Fact]
    public void SanitizeFileName_TruncatesLongTitles()
    {
        var result = Naming.SanitizeFileName(new string('a', 400));

        Assert.Equal(120, result.Length);
    }

    [Theory]
    [InlineData("https://cdn.example.com/videos/meu-filme.mp4", "meu-filme")]
    [InlineData("https://cdn.example.com/videos/", "videos")]
    [InlineData("https://cdn.example.com/", "cdn.example.com")]
    public void NameFromUrl_UsesTheLastMeaningfulSegment(string url, string expected)
    {
        Assert.Equal(expected, Naming.NameFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0L, "0 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void FormatBytes_IsReadable(long? bytes, string expected)
    {
        Assert.Equal(expected, Naming.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(65d, "1:05")]
    [InlineData(3725d, "1:02:05")]
    [InlineData(null, "-")]
    public void FormatDuration_HandlesHours(double? seconds, string expected)
    {
        Assert.Equal(expected, Naming.FormatDuration(seconds));
    }

    [Fact]
    public void StableId_IsDeterministicPerUrl()
    {
        var a = Naming.StableId("https://example.com/a");
        var b = Naming.StableId("https://example.com/a");
        var c = Naming.StableId("https://example.com/b");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}

public class RequestContextTests
{
    [Fact]
    public void FromHeaders_KeepsTheHeadersThatAuthenticateARequest()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "TestAgent/1.0",
            ["Referer"] = "https://example.com/watch",
            ["Cookie"] = "session=abc",
            ["Authorization"] = "Bearer xyz",
            ["X-Custom-Token"] = "tok",
            ["Accept-Encoding"] = "gzip",
            [":method"] = "GET",
        };

        var context = RequestContext.FromHeaders(headers);

        Assert.Equal("TestAgent/1.0", context.UserAgent);
        Assert.Equal("https://example.com/watch", context.Referer);
        Assert.Equal("session=abc", context.Cookie);
        Assert.True(context.ExtraHeaders.ContainsKey("Authorization"));
        Assert.True(context.ExtraHeaders.ContainsKey("X-Custom-Token"));

        // HttpClient sets these itself; replaying them causes protocol errors.
        Assert.False(context.ExtraHeaders.ContainsKey("Accept-Encoding"));
        Assert.False(context.ExtraHeaders.ContainsKey(":method"));
    }

    [Fact]
    public void FromHeaders_FallsBackToThePageUrlForRefererAndOrigin()
    {
        var context = RequestContext.FromHeaders(
            new Dictionary<string, string>(),
            new Uri("https://example.com/watch/42?x=1"));

        Assert.Equal("https://example.com/watch/42?x=1", context.Referer);
        Assert.Equal("https://example.com", context.Origin);
    }

    [Fact]
    public void ApplyTo_DoesNotThrowOnMalformedCapturedHeaders()
    {
        var context = RequestContext.Default with
        {
            Cookie = "weird=value",
            ExtraHeaders = new Dictionary<string, string> { ["X-Bad"] = "line\nbreak" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        var exception = Record.Exception(() => context.ApplyTo(request));

        Assert.Null(exception);
    }
}

public class MediaVariantTests
{
    [Fact]
    public void Label_DescribesAVideoTrack()
    {
        var variant = new MediaVariant
        {
            Id = "v", Url = new Uri("https://e.com/v.m3u8"),
            Track = TrackKind.VideoOnly, Delivery = DeliveryMode.HlsSegments,
            Height = 1080, Bandwidth = 4_500_000, FrameRate = 60, Codecs = "avc1.640028",
        };

        Assert.Contains("1080p", variant.Label);
        Assert.Contains("60fps", variant.Label);
        Assert.Contains("4.5 Mbps", variant.Label);
        Assert.Contains("H.264", variant.Label);
        Assert.Contains("sem audio", variant.Label);
    }

    [Fact]
    public void EstimatedBytes_FallsBackToBitrateTimesDuration()
    {
        var variant = new MediaVariant
        {
            Id = "v", Url = new Uri("https://e.com/v.m3u8"),
            Track = TrackKind.Muxed, Delivery = DeliveryMode.HlsSegments,
            Bandwidth = 8_000_000, DurationSeconds = 60,
        };

        // 8 Mbps for 60s is 60 MB.
        Assert.Equal(60_000_000, variant.EstimatedBytes);
    }

    [Fact]
    public void BestAudioFor_PrefersTheGroupTheVideoDeclares()
    {
        var video = new MediaVariant
        {
            Id = "v", Url = new Uri("https://e.com/v.m3u8"),
            Track = TrackKind.VideoOnly, Delivery = DeliveryMode.HlsSegments,
            AudioGroupId = "aud-hi",
        };

        var wrongGroup = new MediaVariant
        {
            Id = "a1", Url = new Uri("https://e.com/a1.m3u8"),
            Track = TrackKind.AudioOnly, Delivery = DeliveryMode.HlsSegments,
            AudioGroupId = "aud-lo", Bandwidth = 256_000,
        };

        var rightGroup = new MediaVariant
        {
            Id = "a2", Url = new Uri("https://e.com/a2.m3u8"),
            Track = TrackKind.AudioOnly, Delivery = DeliveryMode.HlsSegments,
            AudioGroupId = "aud-hi", Bandwidth = 128_000,
        };

        var source = new MediaSource
        {
            Id = "s", Url = new Uri("https://e.com/master.m3u8"),
            Kind = MediaKind.HlsPlaylist, Request = RequestContext.Default,
            Variants = new[] { video, wrongGroup, rightGroup },
        };

        // The higher bitrate track loses because it belongs to another group.
        Assert.Same(rightGroup, source.BestAudioFor(video));
    }

    [Fact]
    public void BestAudioFor_ReturnsNothingForAMuxedTrack()
    {
        var muxed = new MediaVariant
        {
            Id = "v", Url = new Uri("https://e.com/v.mp4"),
            Track = TrackKind.Muxed, Delivery = DeliveryMode.Progressive,
        };

        var source = new MediaSource
        {
            Id = "s", Url = new Uri("https://e.com/v.mp4"),
            Kind = MediaKind.ProgressiveVideo, Request = RequestContext.Default,
            Variants = new[] { muxed },
        };

        Assert.Null(source.BestAudioFor(muxed));
    }

    [Fact]
    public void BestVideo_PicksTheHighestResolution()
    {
        MediaVariant Make(int height, long bandwidth) => new()
        {
            Id = $"v{height}", Url = new Uri($"https://e.com/{height}.m3u8"),
            Track = TrackKind.VideoOnly, Delivery = DeliveryMode.HlsSegments,
            Height = height, Bandwidth = bandwidth,
        };

        var source = new MediaSource
        {
            Id = "s", Url = new Uri("https://e.com/master.m3u8"),
            Kind = MediaKind.HlsPlaylist, Request = RequestContext.Default,
            Variants = new[] { Make(480, 800_000), Make(1080, 4_000_000), Make(720, 2_000_000) },
        };

        Assert.Equal(1080, source.BestVideo!.Height);
    }
}

public class DownloadProgressTests
{
    [Fact]
    public void Fraction_PrefersSegmentCountsOverBytes()
    {
        var progress = new DownloadProgress { SegmentsDone = 25, SegmentsTotal = 100, BytesReceived = 1, TotalBytes = 1000 };

        Assert.Equal(0.25, progress.Fraction);
    }

    [Fact]
    public void Fraction_IsNullWhenNothingIsKnown()
    {
        Assert.Null(new DownloadProgress { BytesReceived = 500 }.Fraction);
    }

    [Fact]
    public void Eta_IsDerivedFromSpeedAndRemainingBytes()
    {
        var progress = new DownloadProgress { BytesReceived = 500, TotalBytes = 1500, BytesPerSecond = 100 };

        Assert.Equal(TimeSpan.FromSeconds(10), progress.Eta);
    }
}

public class YtDlpProgressTests
{
    [Fact]
    public void ParseProgressLine_ReadsTheMachineReadableTemplate()
    {
        var progress = YtDlpRunner.ParseProgressLine("BTPROG 1048576 10485760 10485760 524288.0");

        Assert.NotNull(progress);
        Assert.Equal(1_048_576, progress!.BytesReceived);
        Assert.Equal(10_485_760, progress.TotalBytes);
        Assert.Equal(524_288, progress.BytesPerSecond);
    }

    [Fact]
    public void ParseProgressLine_FallsBackToTheEstimateWhenTheTotalIsUnknown()
    {
        var progress = YtDlpRunner.ParseProgressLine("BTPROG 2048 NA 999999 1024.0");

        Assert.Equal(999_999, progress!.TotalBytes);
    }

    [Fact]
    public void ParseProgressLine_RejectsUnrelatedOutput()
    {
        Assert.Null(YtDlpRunner.ParseProgressLine("[download] Destination: video.mp4"));
    }
}

public class FfmpegTimestampTests
{
    [Theory]
    [InlineData("00:00:12.34", 12.34)]
    [InlineData("01:02:03.50", 3723.5)]
    public void ParseTimestamp_ReadsFfmpegClock(string value, double expectedSeconds)
    {
        var parsed = FfmpegRunner.ParseTimestamp(value);

        Assert.NotNull(parsed);
        Assert.Equal(expectedSeconds, parsed!.Value.TotalSeconds, 2);
    }

    [Fact]
    public void ParseTimestamp_RejectsGarbage()
    {
        Assert.Null(FfmpegRunner.ParseTimestamp("not a time"));
    }
}
