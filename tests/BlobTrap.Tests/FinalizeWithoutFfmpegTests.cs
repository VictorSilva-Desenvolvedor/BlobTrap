using System.IO;
using BlobTrap.Core.Download;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Covers what happens when the tracks are downloaded but no muxer is installed.
///
/// This branch decides whether the user keeps the bytes they just waited for or loses them:
/// the original code threw here, and the temp folder cleanup then deleted both tracks. Passing
/// the runner in as null is what makes it testable at all - resolving ffmpeg inside the method
/// would tie the result to whatever happens to be installed on the machine running the suite.
/// </summary>
public sealed class FinalizeWithoutFfmpegTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "blobtrap-tests", Guid.NewGuid().ToString("N")[..8]);

    private readonly string _work;
    private readonly string _output;

    public FinalizeWithoutFfmpegTests()
    {
        _work = Path.Combine(_root, "work");
        _output = Path.Combine(_root, "out");

        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_output);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp folder is not worth failing a green run over */ }
    }

    private string WriteTrack(string name, string content)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, content);
        return path;
    }

    private DownloadPlan BuildPlan(string fileName, TrackKind track)
    {
        var variant = new MediaVariant
        {
            Id = "v",
            Url = new Uri("https://cdn.example.com/master.m3u8"),
            Track = track,
            Delivery = DeliveryMode.HlsSegments,
            Height = 1080,
        };

        var source = new MediaSource
        {
            Id = "s",
            Url = new Uri("https://cdn.example.com/master.m3u8"),
            Kind = MediaKind.HlsPlaylist,
            Request = RequestContext.Default,
            Title = "amostra",
            Variants = new[] { variant },
        };

        return new DownloadPlan
        {
            Source = source,
            Video = variant,
            OutputPath = Path.Combine(_output, fileName),
        };
    }

    private static Task<string?> Finalize(DownloadPlan plan, string videoPath, string? audioPath) =>
        DownloadExecutor.FinalizeAsync(
            plan,
            ffmpeg: null,
            videoPath,
            audioPath,
            duration: 120,
            new Progress<DownloadProgress>(),
            CancellationToken.None);

    [Fact]
    public async Task SeparateTracksAreBothDeliveredInsteadOfDiscarded()
    {
        var plan = BuildPlan("Filme [1080p].mp4", TrackKind.VideoOnly);
        var video = WriteTrack("video.ts", "conteudo de video");
        var audio = WriteTrack("audio.m4a", "conteudo de audio");

        var warning = await Finalize(plan, video, audio);

        var videoOut = Path.Combine(_output, "Filme [1080p] (video).ts");
        var audioOut = Path.Combine(_output, "Filme [1080p] (audio).m4a");

        Assert.True(File.Exists(videoOut), $"video nao chegou em {videoOut}");
        Assert.True(File.Exists(audioOut), $"audio nao chegou em {audioOut}");

        // The bytes have to survive the move, not just the file names.
        Assert.Equal("conteudo de video", File.ReadAllText(videoOut));
        Assert.Equal("conteudo de audio", File.ReadAllText(audioOut));

        // Nothing is left behind in the work folder for the cleanup to delete.
        Assert.False(File.Exists(video));
        Assert.False(File.Exists(audio));

        Assert.NotNull(warning);
        Assert.Contains("separados", warning!);
    }

    [Fact]
    public async Task ASingleTrackKeepsItsOwnContainerAndWarnsAboutNothing()
    {
        var plan = BuildPlan("Filme [1080p].mp4", TrackKind.Muxed);
        var video = WriteTrack("video.ts", "stream bruto");

        var warning = await Finalize(plan, video, audioPath: null);

        // Renaming a raw transport stream to .mp4 would produce a file players reject, so the
        // source extension is kept instead.
        var expected = Path.Combine(_output, "Filme [1080p].ts");

        Assert.True(File.Exists(expected), $"arquivo nao chegou em {expected}");
        Assert.Equal("stream bruto", File.ReadAllText(expected));
        Assert.False(File.Exists(Path.Combine(_output, "Filme [1080p].mp4")));
        Assert.Null(warning);
    }

    [Fact]
    public async Task AnExistingFileIsNotOverwrittenBySeparateTracks()
    {
        var plan = BuildPlan("Filme [1080p].mp4", TrackKind.VideoOnly);
        var video = WriteTrack("video.ts", "novo video");
        var audio = WriteTrack("audio.m4a", "novo audio");

        var previous = Path.Combine(_output, "Filme [1080p] (video).ts");
        File.WriteAllText(previous, "download anterior");

        await Finalize(plan, video, audio);

        Assert.Equal("download anterior", File.ReadAllText(previous));
        Assert.True(File.Exists(Path.Combine(_output, "Filme [1080p] (video) (2).ts")));
    }
}
