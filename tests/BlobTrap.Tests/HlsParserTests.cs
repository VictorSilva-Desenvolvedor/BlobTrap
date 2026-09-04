using BlobTrap.Core.Hls;
using Xunit;

namespace BlobTrap.Tests;

public class HlsParserTests
{
    private static readonly Uri BaseUri = new("https://cdn.example.com/video/master.m3u8");

    [Fact]
    public void IsMaster_DistinguishesMasterFromMedia()
    {
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=1280x720
            720p.m3u8
            """;

        const string media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:9.009,
            seg1.ts
            """;

        Assert.True(HlsParser.IsMaster(master));
        Assert.False(HlsParser.IsMaster(media));
    }

    [Fact]
    public void ParseMaster_ReadsVariantsAndResolvesRelativeUris()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,AVERAGE-BANDWIDTH=1800000,RESOLUTION=1920x1080,FRAME-RATE=59.94,CODECS="avc1.640028,mp4a.40.2"
            1080/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=854x480,CODECS="avc1.4d401f"
            https://other.example.com/480/index.m3u8
            """;

        var master = HlsParser.ParseMaster(playlist, BaseUri);

        Assert.Equal(2, master.Variants.Count);

        var best = master.Variants[0];
        Assert.Equal(1920, best.Width);
        Assert.Equal(1080, best.Height);
        Assert.Equal(1_800_000, best.AverageBandwidth);
        Assert.Equal(59.94, best.FrameRate!.Value, 2);
        Assert.Equal("https://cdn.example.com/video/1080/index.m3u8", best.Uri.AbsoluteUri);

        // A quoted CODECS value contains a comma, which naive splitting would break on.
        Assert.Equal("avc1.640028,mp4a.40.2", best.Codecs);

        Assert.Equal("https://other.example.com/480/index.m3u8", master.Variants[1].Uri.AbsoluteUri);
    }

    [Fact]
    public void ParseMaster_ReadsAudioRenditionsAndMarksVariantVideoOnly()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud1",NAME="Portugues",LANGUAGE="pt-BR",DEFAULT=YES,CHANNELS="2",URI="audio/pt.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="sub1",NAME="Portugues",LANGUAGE="pt",URI="subs/pt.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080,AUDIO="aud1",SUBTITLES="sub1"
            video/1080.m3u8
            """;

        var master = HlsParser.ParseMaster(playlist, BaseUri);

        var variant = Assert.Single(master.Variants);
        Assert.True(variant.IsVideoOnly);
        Assert.Equal("aud1", variant.AudioGroupId);

        var audio = Assert.Single(master.AudioRenditions);
        Assert.Equal("pt-BR", audio.Language);
        Assert.True(audio.IsDefault);
        Assert.Equal("https://cdn.example.com/video/audio/pt.m3u8", audio.Uri!.AbsoluteUri);

        Assert.Single(master.SubtitleRenditions);
    }

    [Fact]
    public void ParseMaster_SkipsIFrameOnlyVariantsButKeepsTheirEntry()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080
            main.m3u8
            #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100000,RESOLUTION=1920x1080,URI="iframe.m3u8"
            """;

        var master = HlsParser.ParseMaster(playlist, BaseUri);

        Assert.Equal(2, master.Variants.Count);
        Assert.Single(master.Variants, v => v.IsIFrameOnly);
    }

    [Fact]
    public void ParseMedia_ReadsSegmentsAndDuration()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:0
            #EXTINF:9.009,
            seg0.ts
            #EXTINF:9.009,
            seg1.ts
            #EXTINF:3.003,
            seg2.ts
            #EXT-X-ENDLIST
            """;

        var media = HlsParser.ParseMedia(playlist, new Uri("https://cdn.example.com/video/index.m3u8"));

        Assert.Equal(3, media.Segments.Count);
        Assert.True(media.HasEndList);
        Assert.False(media.IsLive);
        Assert.Equal(21.021, media.TotalDuration, 3);
        Assert.Equal("https://cdn.example.com/video/seg0.ts", media.Segments[0].Uri.AbsoluteUri);
    }

    [Fact]
    public void ParseMedia_WithoutEndListIsLive()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:1450
            #EXTINF:6.0,
            seg1450.ts
            """;

        var media = HlsParser.ParseMedia(playlist, BaseUri);

        Assert.True(media.IsLive);
        Assert.Equal(1450, media.Segments[0].MediaSequence);
    }

    [Fact]
    public void ParseMedia_DerivesIvFromMediaSequenceWhenKeyOmitsIt()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:5
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXTINF:10,
            seg5.ts
            #EXTINF:10,
            seg6.ts
            #EXT-X-ENDLIST
            """;

        var media = HlsParser.ParseMedia(playlist, BaseUri);

        var first = media.Segments[0].Key!;
        Assert.True(first.IsAes128);
        Assert.Equal("https://cdn.example.com/video/key.bin", first.Uri!.AbsoluteUri);

        // The IV is the sequence number as a 128-bit big-endian integer.
        Assert.Equal(5, first.Iv![15]);
        Assert.All(first.Iv.Take(15), b => Assert.Equal(0, b));

        Assert.Equal(6, media.Segments[1].Key!.Iv![15]);
    }

    [Fact]
    public void ParseMedia_UsesExplicitIvWhenPresent()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:10,
            seg0.ts
            #EXT-X-ENDLIST
            """;

        var media = HlsParser.ParseMedia(playlist, BaseUri);

        var iv = media.Segments[0].Key!.Iv!;
        Assert.Equal(16, iv.Length);
        Assert.Equal(0x0F, iv[15]);
        Assert.Equal(0x00, iv[0]);
    }

    [Fact]
    public void ParseMedia_ChainsByteRangesWhenOffsetIsOmitted()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXT-X-BYTERANGE:1000@0
            #EXTINF:10,
            all.ts
            #EXT-X-BYTERANGE:500
            #EXTINF:10,
            all.ts
            #EXT-X-ENDLIST
            """;

        var media = HlsParser.ParseMedia(playlist, BaseUri);

        Assert.Equal("bytes=0-999", media.Segments[0].RangeHeader);

        // An omitted offset continues where the previous range of the same file ended.
        Assert.Equal("bytes=1000-1499", media.Segments[1].RangeHeader);
    }

    [Fact]
    public void ParseMedia_AttachesInitSegmentFromMap()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4,
            seg1.m4s
            #EXT-X-ENDLIST
            """;

        var media = HlsParser.ParseMedia(playlist, BaseUri);

        Assert.Equal("https://cdn.example.com/video/init.mp4", media.Segments[0].Map!.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("SAMPLE-AES", "com.apple.streamingkeydelivery", true, "FairPlay")]
    [InlineData("SAMPLE-AES-CTR", "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", true, "Widevine")]
    [InlineData("AES-128", null, false, null)]
    [InlineData("NONE", null, false, null)]
    public void HlsKey_IdentifiesDrmSystems(string method, string? keyFormat, bool expectedDrm, string? expectedName)
    {
        var key = new HlsKey { Method = method, KeyFormat = keyFormat };

        Assert.Equal(expectedDrm, key.IsDrm);
        Assert.Equal(expectedName, key.DrmName);
    }
}
