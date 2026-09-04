using BlobTrap.Core.Util;
using Xunit;

namespace BlobTrap.Tests;

public class CodecInfoTests
{
    [Theory]
    [InlineData("avc1.640028,mp4a.40.2", true, true)]
    [InlineData("avc1.4d401f", true, false)]
    [InlineData("mp4a.40.2", false, true)]
    [InlineData("hvc1.2.4.L120.B0,ec-3", true, true)]
    [InlineData("vp09.00.10.08,opus", true, true)]
    [InlineData("av01.0.05M.08", true, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void DetectsVideoAndAudioCodecs(string? codecs, bool expectedVideo, bool expectedAudio)
    {
        Assert.Equal(expectedVideo, CodecInfo.HasVideo(codecs));
        Assert.Equal(expectedAudio, CodecInfo.HasAudio(codecs));
    }

    [Theory]
    [InlineData("mp4a.40.2", true)]
    [InlineData("ec-3", true)]
    [InlineData("avc1.640028,mp4a.40.2", false)]
    [InlineData("avc1.640028", false)]
    [InlineData(null, false)]
    public void IsAudioOnly_RequiresAudioAndNoVideo(string? codecs, bool expected)
    {
        Assert.Equal(expected, CodecInfo.IsAudioOnly(codecs));
    }

    [Fact]
    public void HandlesQuotedAndSpacedLists()
    {
        Assert.True(CodecInfo.HasVideo("\"avc1.640028\", \"mp4a.40.2\""));
        Assert.True(CodecInfo.HasAudio(" mp4a.40.5 "));
    }
}
