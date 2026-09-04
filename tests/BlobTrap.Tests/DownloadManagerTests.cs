using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlobTrap.Core.Download;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A fila decide ordem, concorrência e transição de estado. Nenhuma dessas falhas é
/// barulhenta - um estado que regride só aparece como um download concluído que a interface
/// jura estar baixando -, e é por isso que ela é testada sem rede, ffmpeg ou disco.
/// </summary>
public class DownloadManagerTests
{
    [Fact]
    public async Task JobBemSucedido_TerminaEmCompleted()
    {
        var runner = new FakeRunner();
        using var manager = new DownloadManager(runner);

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => job.IsFinished);

        Assert.Equal(DownloadState.Completed, job.State);
        Assert.Null(job.ErrorMessage);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task JobQueFalha_GuardaAMensagemEVaiParaFailed()
    {
        var runner = new FakeRunner { Fail = new InvalidOperationException("CDN devolveu 403") };
        using var manager = new DownloadManager(runner);

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => job.IsFinished);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal("CDN devolveu 403", job.ErrorMessage);
    }

    [Fact]
    public async Task JobCanceladoNaFila_NaoChegaAExecutar()
    {
        var runner = new FakeRunner { Gate = new TaskCompletionSource() };
        using var manager = new DownloadManager(runner) { MaxConcurrent = 1 };

        var blocking = manager.Enqueue(Plan());
        await WaitUntil(() => runner.Started == 1);

        var queued = manager.Enqueue(Plan());
        queued.Cancel();

        runner.Gate.SetResult();
        await WaitUntil(() => blocking.IsFinished && queued.IsFinished);

        Assert.Equal(DownloadState.Completed, blocking.State);
        Assert.Equal(DownloadState.Canceled, queued.State);
        Assert.Equal(1, runner.Started);
    }

    [Fact]
    public async Task MaxConcurrent_LimitaQuantosCorremAoMesmoTempo()
    {
        var runner = new FakeRunner { Gate = new TaskCompletionSource() };
        using var manager = new DownloadManager(runner) { MaxConcurrent = 2 };

        for (var i = 0; i < 6; i++) manager.Enqueue(Plan());

        await WaitUntil(() => runner.Started == 2);
        await Task.Delay(80);

        Assert.Equal(2, runner.Started);

        runner.Gate.SetResult();
        await WaitUntil(() => manager.Jobs.All(j => j.IsFinished));

        Assert.True(runner.Peak <= 2, "pico de " + runner.Peak + " com limite 2");
    }

    [Fact]
    public void MaxConcurrent_EhLimitadoAoIntervaloUtil()
    {
        using var manager = new DownloadManager(new FakeRunner());

        manager.MaxConcurrent = 0;
        Assert.Equal(1, manager.MaxConcurrent);

        manager.MaxConcurrent = 99;
        Assert.Equal(DownloadManager.MaxAllowedConcurrent, manager.MaxConcurrent);
    }

    [Fact]
    public async Task ClearFinished_TiraSoOQueTerminou()
    {
        var runner = new FakeRunner { Gate = new TaskCompletionSource() };
        using var manager = new DownloadManager(runner) { MaxConcurrent = 1 };

        var first = manager.Enqueue(Plan());
        await WaitUntil(() => runner.Started == 1);

        var second = manager.Enqueue(Plan());

        runner.Gate.SetResult();
        await WaitUntil(() => first.IsFinished);

        manager.ClearFinished();

        Assert.DoesNotContain(first, manager.Jobs);
        await WaitUntil(() => second.IsFinished);
    }

    // ----- apoio -----

    private static DownloadPlan Plan() => new()
    {
        Source = new MediaSource
        {
            Id = "s1",
            Url = new Uri("https://exemplo.com/video.m3u8"),
            Kind = MediaKind.HlsPlaylist,
            Request = RequestContext.Default,
            Variants = Array.Empty<MediaVariant>(),
        },
        Video = new MediaVariant
        {
            Id = "v1",
            Url = new Uri("https://exemplo.com/720.m3u8"),
            Track = TrackKind.Muxed,
            Delivery = DeliveryMode.HlsSegments,
        },
        OutputPath = Path.Combine(Path.GetTempPath(), "blobtrap-teste.mp4"),
    };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }

        throw new TimeoutException("A condição não foi satisfeita a tempo.");
    }

    private sealed class FakeRunner : IDownloadRunner
    {
        private int _running;

        public int Started;
        public int Peak;

        public Exception? Fail { get; init; }

        public Func<DownloadJob, IProgress<DownloadProgress>, Task>? Work { get; init; }

        /// <summary>Quando presente, todo job espera aqui - o teste controla quando eles saem.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task ExecuteAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            var now = Interlocked.Increment(ref _running);

            int seen;
            do { seen = Volatile.Read(ref Peak); }
            while (now > seen && Interlocked.CompareExchange(ref Peak, now, seen) != seen);

            try
            {
                if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
                if (Fail is not null) throw Fail;
                if (Work is not null) await Work(job, progress);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
