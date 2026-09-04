using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Xunit;

namespace BlobTrap.Tests;

public class MediaRegistryTests
{
    private static readonly RequestContext Context = RequestContext.Default;

    [Fact]
    public void Observe_AddsDownloadableMediaOnce()
    {
        var registry = new MediaRegistry();
        var url = new Uri("https://cdn.example.com/master.m3u8");

        var added = 0;
        registry.CandidateAdded += (_, _) => added++;

        registry.Observe(url, "application/vnd.apple.mpegurl", Context);
        registry.Observe(url, "application/vnd.apple.mpegurl", Context);

        Assert.Equal(1, added);

        var candidate = Assert.Single(registry.Snapshot());
        Assert.Equal(2, candidate.HitCount);
    }

    [Fact]
    public void Observe_TreatsVolatileQueryParametersAsTheSameAsset()
    {
        var registry = new MediaRegistry();

        registry.Observe(new Uri("https://cdn.example.com/movie.mp4?expires=100&sig=aaa"), "video/mp4", Context);
        registry.Observe(new Uri("https://cdn.example.com/movie.mp4?expires=200&sig=bbb"), "video/mp4", Context);

        // A refreshed CDN token points at the same file, not a second one.
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Observe_KeepsDistinctAssetsApart()
    {
        var registry = new MediaRegistry();

        registry.Observe(new Uri("https://cdn.example.com/a.mp4"), "video/mp4", Context);
        registry.Observe(new Uri("https://cdn.example.com/b.mp4"), "video/mp4", Context);

        Assert.Equal(2, registry.Snapshot().Count);
    }

    [Fact]
    public void Observe_GroupsSegmentsInsteadOfListingThem()
    {
        var registry = new MediaRegistry();

        for (var i = 0; i < 50; i++)
            registry.Observe(new Uri($"https://cdn.example.com/v/720/seg-{i}.ts"), "video/mp2t", Context, contentLength: 1000);

        Assert.Empty(registry.Snapshot());

        var family = Assert.Single(registry.SegmentFamilies());
        Assert.Equal(50, family.Count);
        Assert.Equal(50_000, family.TotalBytes);
    }

    [Fact]
    public void Observe_HonoursTheMinimumSizeFilter()
    {
        var registry = new MediaRegistry();
        registry.Options.MinProgressiveBytes = 512 * 1024;

        registry.Observe(new Uri("https://cdn.example.com/preview.mp4"), "video/mp4", Context, contentLength: 10_000);
        registry.Observe(new Uri("https://cdn.example.com/feature.mp4"), "video/mp4", Context, contentLength: 900_000);

        var candidate = Assert.Single(registry.Snapshot());
        Assert.Equal("/feature.mp4", candidate.Url.AbsolutePath);
    }

    [Fact]
    public void Observe_FilterDoesNotHideStreamsOfUnknownSize()
    {
        var registry = new MediaRegistry();
        registry.Options.MinProgressiveBytes = 512 * 1024;

        // A manifest is tiny by nature; the size filter must not touch it.
        registry.Observe(new Uri("https://cdn.example.com/master.m3u8"), null, Context, contentLength: 800);

        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Observe_CanExcludeAudioAndSubtitles()
    {
        var registry = new MediaRegistry();
        registry.Options.IncludeAudio = false;
        registry.Options.IncludeSubtitles = false;

        registry.Observe(new Uri("https://cdn.example.com/song.mp3"), null, Context);
        registry.Observe(new Uri("https://cdn.example.com/subs.vtt"), null, Context);
        registry.Observe(new Uri("https://cdn.example.com/movie.mp4"), null, Context);

        var candidate = Assert.Single(registry.Snapshot());
        Assert.Equal(MediaKind.ProgressiveVideo, candidate.Kind);
    }

    [Fact]
    public void Snapshot_PutsStreamsBeforeFiles()
    {
        var registry = new MediaRegistry();

        registry.Observe(new Uri("https://cdn.example.com/movie.mp4"), null, Context, contentLength: 5_000_000);
        registry.Observe(new Uri("https://cdn.example.com/master.m3u8"), null, Context);

        Assert.Equal(MediaKind.HlsPlaylist, registry.Snapshot()[0].Kind);
    }

    [Fact]
    public void AddManual_AcceptsAPageUrlAsAnExtractorTarget()
    {
        var registry = new MediaRegistry();

        var candidate = registry.AddManual(new Uri("https://www.example.com/watch/123"), Context);

        Assert.Equal(MediaKind.PageEmbed, candidate.Kind);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Clear_EmptiesCandidatesAndFamilies()
    {
        var registry = new MediaRegistry();
        registry.Observe(new Uri("https://cdn.example.com/master.m3u8"), null, Context);
        registry.Observe(new Uri("https://cdn.example.com/v/seg-1.ts"), null, Context);

        registry.Clear();

        Assert.Empty(registry.Snapshot());
        Assert.Empty(registry.SegmentFamilies());
    }
}
