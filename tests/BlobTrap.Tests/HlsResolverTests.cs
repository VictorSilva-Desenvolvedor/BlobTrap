using BlobTrap.Core.Hls;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Resolving;
using Xunit;

namespace BlobTrap.Tests;

public class HlsResolverTests
{
    private static readonly Uri MasterUri = new("https://cdn.example.com/video/master.m3u8");

    private static MediaSource Resolve(string playlist)
    {
        var master = HlsParser.ParseMaster(playlist, MasterUri);
        var candidate = new MediaCandidate(MasterUri, MediaKind.HlsPlaylist, RequestContext.Default);

        return HlsResolver.BuildFromMaster(master, candidate, "teste");
    }

    [Fact]
    public void AudioOnlyStreamInfIsNotOfferedAsAVideoQuality()
    {
        // Apple's own sample playlists list an audio rendition as a full variant stream.
        const string playlist = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="Principal",DEFAULT=YES,URI="audio/main.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=41000,CODECS="mp4a.40.2",AUDIO="aud"
            gear0/prog_index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=264000,RESOLUTION=416x234,CODECS="avc1.4d400d,mp4a.40.2",AUDIO="aud"
            gear1/prog_index.m3u8
            """;

        var source = Resolve(playlist);

        var video = Assert.Single(source.VideoVariants);
        Assert.Equal(234, video.Height);

        // The 41 kbps entry plus the EXT-X-MEDIA rendition, both audio.
        Assert.Equal(2, source.AudioVariants.Count());
        Assert.DoesNotContain(source.VideoVariants, v => v.Url.AbsoluteUri.Contains("gear0"));
    }

    [Fact]
    public void VariantWithAnAudioGroupIsVideoOnlyAndPairsWithItsGroup()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud-hi",NAME="Alta",URI="audio/hi.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud-lo",NAME="Baixa",URI="audio/lo.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,CODECS="avc1.640028",AUDIO="aud-hi"
            video/1080.m3u8
            """;

        var source = Resolve(playlist);

        var video = Assert.Single(source.VideoVariants);
        Assert.Equal(TrackKind.VideoOnly, video.Track);

        var paired = source.BestAudioFor(video);
        Assert.NotNull(paired);
        Assert.Equal("aud-hi", paired!.AudioGroupId);
    }

    [Fact]
    public void VariantWithoutAnAudioGroupIsTreatedAsMuxed()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2"
            720.m3u8
            """;

        var source = Resolve(playlist);

        var video = Assert.Single(source.VideoVariants);
        Assert.Equal(TrackKind.Muxed, video.Track);
        Assert.Null(source.BestAudioFor(video));
    }

    [Fact]
    public void SessionKeyWithDrmMarksTheWholeSourceProtected()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,KEYFORMAT="com.apple.streamingkeydelivery",URI="skd://key"
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS="avc1.64001f"
            720.m3u8
            """;

        var source = Resolve(playlist);

        Assert.True(source.IsProtected);
        Assert.Equal("FairPlay", source.ProtectionSystem);
    }

    [Fact]
    public void IFrameOnlyVariantsAreNotOfferedForDownload()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2"
            720.m3u8
            #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=90000,RESOLUTION=1280x720,URI="iframe.m3u8"
            """;

        var source = Resolve(playlist);

        Assert.Single(source.VideoVariants);
    }

    [Fact]
    public void BestVideoPicksTheHighestResolutionVariant()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=854x480,CODECS="avc1.4d401f,mp4a.40.2"
            480.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            1080.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2"
            720.m3u8
            """;

        var source = Resolve(playlist);

        Assert.Equal(1080, source.BestVideo!.Height);
    }
}
