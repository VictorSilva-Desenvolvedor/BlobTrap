using BlobTrap.Core.Dash;
using Xunit;

namespace BlobTrap.Tests;

public class DashParserTests
{
    private static readonly Uri ManifestUri = new("https://cdn.example.com/media/manifest.mpd");

    [Fact]
    public void Parse_ReadsRepresentationsAndSplitsTracksByContentType()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10M">
              <Period id="0">
                <AdaptationSet contentType="video" mimeType="video/mp4" frameRate="30000/1001">
                  <SegmentTemplate initialization="init-$RepresentationID$.mp4" media="seg-$RepresentationID$-$Number$.m4s" startNumber="1" timescale="90000" duration="180000"/>
                  <Representation id="v1080" bandwidth="4000000" width="1920" height="1080" codecs="avc1.640028"/>
                  <Representation id="v720" bandwidth="2000000" width="1280" height="720" codecs="avc1.64001f"/>
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="pt">
                  <SegmentTemplate initialization="init-$RepresentationID$.mp4" media="seg-$RepresentationID$-$Number$.m4s" startNumber="1" timescale="48000" duration="96000"/>
                  <Representation id="a128" bandwidth="128000" codecs="mp4a.40.2"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);

        Assert.False(manifest.IsDynamic);
        Assert.Equal(TimeSpan.FromMinutes(10), manifest.Duration);

        var representations = manifest.AllRepresentations.ToList();
        Assert.Equal(3, representations.Count);

        var video = representations.First(r => r.Id == "v1080");
        Assert.True(video.IsVideo);
        Assert.Equal(1920, video.Width);
        Assert.Equal(29.97, video.FrameRate!.Value, 2);

        var audio = representations.First(r => r.Id == "a128");
        Assert.True(audio.IsAudio);
        Assert.Equal("pt", audio.Language);
    }

    [Fact]
    public void SegmentTemplate_WithFixedDuration_GeneratesTheRightNumberOfSegments()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT20S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate initialization="init.mp4" media="seg-$Number%04d$.m4s" startNumber="1" timescale="1000" duration="4000"/>
                  <Representation id="v" bandwidth="1000000" width="640" height="360"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);
        var representation = manifest.AllRepresentations.Single();

        var segments = representation.Segments.BuildSegments(representation, TimeSpan.FromSeconds(20));

        // One init plus 20s / 4s = 5 media segments.
        Assert.Equal(6, segments.Count);
        Assert.True(segments[0].IsInitialization);
        Assert.Equal("https://cdn.example.com/media/init.mp4", segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/media/seg-0001.m4s", segments[1].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/media/seg-0005.m4s", segments[5].Uri.AbsoluteUri);
    }

    [Fact]
    public void SegmentTemplate_WithTimeline_ExpandsRepeatsAndUsesTime()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT12S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate initialization="init.mp4" media="$Time$.m4s" timescale="1000">
                    <SegmentTimeline>
                      <S t="0" d="4000" r="2"/>
                    </SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v" bandwidth="1000000"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);
        var representation = manifest.AllRepresentations.Single();

        var segments = representation.Segments.BuildSegments(representation, TimeSpan.FromSeconds(12));

        // r="2" means two *additional* repeats, so three segments in total.
        Assert.Equal(4, segments.Count);
        Assert.Equal("https://cdn.example.com/media/0.m4s", segments[1].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/media/4000.m4s", segments[2].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/media/8000.m4s", segments[3].Uri.AbsoluteUri);
    }

    [Fact]
    public void BaseUrl_IsInheritedAndOverriddenPerLevel()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <BaseURL>https://cdn.example.com/root/</BaseURL>
              <Period>
                <BaseURL>period/</BaseURL>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate initialization="init.mp4" media="s-$Number$.m4s" startNumber="1" timescale="1000" duration="4000"/>
                  <Representation id="v" bandwidth="1000000">
                    <BaseURL>rep/</BaseURL>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);
        var representation = manifest.AllRepresentations.Single();

        var segments = representation.Segments.BuildSegments(representation, TimeSpan.FromSeconds(8));

        Assert.Equal("https://cdn.example.com/root/period/rep/init.mp4", segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/root/period/rep/s-1.m4s", segments[1].Uri.AbsoluteUri);
    }

    [Fact]
    public void SegmentList_ProducesInitPlusEveryListedUrl()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v" bandwidth="1000000">
                    <SegmentList timescale="1000" duration="4000">
                      <Initialization sourceURL="init.mp4"/>
                      <SegmentURL media="a.m4s"/>
                      <SegmentURL media="b.m4s"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);
        var representation = manifest.AllRepresentations.Single();

        var segments = representation.Segments.BuildSegments(representation, TimeSpan.FromSeconds(8));

        Assert.Equal(3, segments.Count);
        Assert.True(segments[0].IsInitialization);
        Assert.Equal("https://cdn.example.com/media/b.m4s", segments[2].Uri.AbsoluteUri);
    }

    [Fact]
    public void Parse_FlagsWidevineProtection()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <ContentProtection schemeIdUri="urn:uuid:EDEF8BA9-79D6-4ACE-A3C8-27DCD51D21ED"/>
                  <SegmentTemplate initialization="init.mp4" media="s-$Number$.m4s" startNumber="1" timescale="1000" duration="4000"/>
                  <Representation id="v" bandwidth="1000000"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);

        Assert.True(manifest.IsProtected);
        Assert.Equal("Widevine", manifest.ProtectionSystem);
    }

    [Fact]
    public void Period_WithoutDuration_InheritsFromTheNextPeriodStart()
    {
        const string mpd = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT30S">
              <Period id="a" start="PT0S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v1" bandwidth="1000"/>
                </AdaptationSet>
              </Period>
              <Period id="b" start="PT10S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v2" bandwidth="1000"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);

        Assert.Equal(TimeSpan.FromSeconds(10), manifest.Periods[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(20), manifest.Periods[1].Duration);
    }

    [Theory]
    [InlineData("seg-$Number$.m4s", 7, "seg-7.m4s")]
    [InlineData("seg-$Number%05d$.m4s", 42, "seg-00042.m4s")]
    [InlineData("$RepresentationID$/$Number$.m4s", 3, "v1/3.m4s")]
    [InlineData("cost$$-$Number$.m4s", 1, "cost$-1.m4s")]
    public void Template_ExpandsPlaceholders(string template, long number, string expected)
    {
        var result = DashTemplate.Expand(template, "v1", bandwidth: 128000, number: number, time: null);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Template_ExpandsBandwidthAndTime()
    {
        var result = DashTemplate.Expand("$Bandwidth$/$Time$.m4s", "v1", 128000, null, 9000);

        Assert.Equal("128000/9000.m4s", result);
    }
}
