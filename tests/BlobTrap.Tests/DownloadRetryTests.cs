using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlobTrap.Core.Download;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Resolving;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Tentar de novo é o caminho que decide se o usuário perde ou não o trabalho já feito
/// quando a CDN devolve 403 tarde. Ele mexe em estado que já foi dado como final, então
/// tudo que pode voltar para trás é testado explicitamente.
/// </summary>
public class DownloadRetryTests
{
    [Fact]
    public async Task JobQueFalhou_VoltaARodarComOMesmoPlano()
    {
        var runner = new FailFirstRunner();
        using var manager = new DownloadManager(runner);

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => job.IsFinished);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.True(job.CanRetry);

        Assert.True(manager.Retry(job));
        await WaitUntil(() => job.State == DownloadState.Completed);

        Assert.Equal(2, runner.Runs);
        Assert.Equal(2, job.Attempt);
        Assert.Null(job.ErrorMessage);
    }

    [Fact]
    public async Task Retry_LimpaOErroEOProgressoDaTentativaAnterior()
    {
        var runner = new FailFirstRunner
        {
            Work = (_, progress) =>
            {
                progress.Report(new DownloadProgress { BytesReceived = 900, TotalBytes = 1000, Stage = "Baixando" });
                return Task.CompletedTask;
            },
        };

        using var manager = new DownloadManager(runner);
        var job = manager.Enqueue(Plan());
        await WaitUntil(() => job.IsFinished);

        Assert.NotNull(job.ErrorMessage);

        // Antes de sair da fila o job precisa estar zerado: um erro antigo grudado numa nova
        // tentativa e' pior do que nao ter botao nenhum, porque diz que falhou o que ainda
        // nem rodou.
        var observedWhileQueued = new List<DownloadState>();
        job.Changed += (_, _) => observedWhileQueued.Add(job.State);

        manager.Retry(job);
        await WaitUntil(() => job.State == DownloadState.Completed);

        Assert.Contains(DownloadState.Queued, observedWhileQueued);
    }

    [Fact]
    public async Task JobConcluido_NaoPodeSerRefeito()
    {
        using var manager = new DownloadManager(new AlwaysOkRunner());

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => job.IsFinished);

        // O arquivo ja esta no disco. Refazer por cima seria destruir o resultado sem pedir.
        Assert.False(job.CanRetry);
        Assert.False(manager.Retry(job));
    }

    [Fact]
    public async Task JobCancelado_PodeSerRefeito()
    {
        var runner = new AlwaysOkRunner { Gate = new TaskCompletionSource() };
        using var manager = new DownloadManager(runner);

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => runner.Runs == 1);

        job.Cancel();
        await WaitUntil(() => job.IsFinished);
        Assert.Equal(DownloadState.Canceled, job.State);

        runner.Gate = null;
        Assert.True(manager.Retry(job));
        await WaitUntil(() => job.State == DownloadState.Completed);
    }

    /// <summary>
    /// O token e' recriado a cada tentativa. Sem isso a nova tentativa herdaria o token ja
    /// cancelado e morreria antes de emitir um unico byte - falhando de um jeito que parece
    /// erro de rede.
    /// </summary>
    [Fact]
    public async Task RetryDepoisDeCancelar_NaoHerdaOTokenCancelado()
    {
        var runner = new AlwaysOkRunner { Gate = new TaskCompletionSource() };
        using var manager = new DownloadManager(runner);

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => runner.Runs == 1);

        job.Cancel();
        await WaitUntil(() => job.IsFinished);
        Assert.True(job.CancellationToken.IsCancellationRequested);

        runner.Gate = null;
        manager.Retry(job);

        Assert.False(job.CancellationToken.IsCancellationRequested);
        await WaitUntil(() => job.State == DownloadState.Completed);
    }

    [Fact]
    public async Task FalhaPorDrm_NaoOferecTentarDeNovo()
    {
        var runner = new AlwaysOkRunner { Fail = new DrmProtectedException("Widevine") };
        using var manager = new DownloadManager(runner);

        var job = manager.Enqueue(Plan());
        await WaitUntil(() => job.IsFinished);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.True(job.IsPermanentFailure);
        Assert.False(job.CanRetry);
        Assert.False(manager.Retry(job));
    }

    [Fact]
    public async Task RetryRespeitaOLimiteDeConcorrencia()
    {
        var runner = new FailFirstRunner { FailCount = 3, Gate = new TaskCompletionSource() };
        using var manager = new DownloadManager(runner) { MaxConcurrent = 1 };

        var jobs = new List<DownloadJob>();
        for (var i = 0; i < 3; i++) jobs.Add(manager.Enqueue(Plan()));

        runner.Gate.SetResult();
        await WaitUntil(() => jobs.All(j => j.IsFinished));
        Assert.All(jobs, j => Assert.Equal(DownloadState.Failed, j.State));

        runner.Gate = new TaskCompletionSource();
        foreach (var job in jobs) Assert.True(manager.Retry(job));

        await WaitUntil(() => runner.Running == 1);
        await Task.Delay(60);

        Assert.Equal(1, runner.Running);

        runner.Gate.SetResult();
        await WaitUntil(() => jobs.All(j => j.State == DownloadState.Completed));
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
        OutputPath = Path.Combine(Path.GetTempPath(), "blobtrap-retry.mp4"),
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

    private class AlwaysOkRunner : IDownloadRunner
    {
        private int _running;

        public int Runs;

        public Exception? Fail { get; init; }

        public Func<DownloadJob, IProgress<DownloadProgress>, Task>? Work { get; init; }

        public TaskCompletionSource? Gate { get; set; }

        public int Running => Volatile.Read(ref _running);

        public virtual async Task ExecuteAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            Interlocked.Increment(ref _running);

            try
            {
                var gate = Gate;
                if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
                if (Fail is not null) throw Fail;
                if (Work is not null) await Work(job, progress);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>Falha nas primeiras N execuções e passa depois, como uma CDN instável faria.</summary>
    private sealed class FailFirstRunner : AlwaysOkRunner
    {
        public int FailCount { get; init; } = 1;

        public override async Task ExecuteAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            var attempt = Runs;
            await base.ExecuteAsync(job, progress, cancellationToken);

            if (attempt < FailCount)
                throw new HttpRequestException("Falha ao buscar segmento 412 apos 5 tentativas.");
        }
    }
}
