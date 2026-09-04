using BlobTrap.Core.Net;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Models;

public enum DownloadState
{
    Queued,
    Preparing,
    Downloading,
    Muxing,
    Completed,
    Failed,
    Canceled,
}

/// <summary>What the user selected from a <see cref="MediaSource"/>, ready to execute.</summary>
public sealed class DownloadPlan
{
    public required MediaSource Source { get; init; }
    public required MediaVariant Video { get; init; }
    public MediaVariant? Audio { get; init; }
    public IReadOnlyList<MediaVariant> Subtitles { get; init; } = Array.Empty<MediaVariant>();

    /// <summary>Absolute path of the final file, extension included.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Strip the video stream and keep audio only (ffmpeg re-encodes to mp3 when needed).</summary>
    public bool AudioOnly { get; init; }

    public RequestContext Request => Source.Request;

    /// <summary>True when the two selected tracks have to be merged into one container.</summary>
    public bool NeedsMerge => Audio is not null && Video.Track == TrackKind.VideoOnly;

    public long? EstimatedBytes
    {
        get
        {
            var video = Video.EstimatedBytes;
            var audio = Audio?.EstimatedBytes;
            if (video is null && audio is null) return null;
            return (video ?? 0) + (audio ?? 0);
        }
    }
}

/// <summary>Immutable snapshot of a job's progress, raised on every meaningful change.</summary>
public sealed record DownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public int SegmentsDone { get; init; }
    public int SegmentsTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public string? Stage { get; init; }

    /// <summary>0..1, or null when the total is unknown (live streams, chunked responses).</summary>
    public double? Fraction
    {
        get
        {
            if (SegmentsTotal > 0) return Math.Clamp((double)SegmentsDone / SegmentsTotal, 0, 1);
            if (TotalBytes is > 0) return Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1);
            return null;
        }
    }

    public TimeSpan? Eta
    {
        get
        {
            if (BytesPerSecond <= 1 || TotalBytes is null or <= 0) return null;
            var remaining = TotalBytes.Value - BytesReceived;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }

    public string SpeedLabel => Naming.FormatSpeed(BytesPerSecond);

    public string ProgressLabel
    {
        get
        {
            if (SegmentsTotal > 0) return $"{SegmentsDone}/{SegmentsTotal} segmentos";
            if (TotalBytes is > 0) return $"{Naming.FormatBytes(BytesReceived)} / {Naming.FormatBytes(TotalBytes)}";
            return Naming.FormatBytes(BytesReceived);
        }
    }
}

/// <summary>A queued or running download, with its live state.</summary>
public sealed class DownloadJob
{
    private readonly object _sync = new();
    private CancellationTokenSource _cts = new();

    public DownloadJob(DownloadPlan plan)
    {
        Plan = plan;
        Id = Guid.NewGuid().ToString("N")[..12];
        CreatedAt = DateTimeOffset.Now;
    }

    public string Id { get; }
    public DownloadPlan Plan { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    public string Title => Path.GetFileName(Plan.OutputPath);
    public string OutputPath => Plan.OutputPath;

    public DownloadState State { get; internal set; } = DownloadState.Queued;
    public DownloadProgress Progress { get; internal set; } = new();
    public string? ErrorMessage { get; internal set; }

    /// <summary>
    /// Problems that did not stop the download - a subtitle track that failed, say. The video
    /// is still delivered, but the user is told what was left behind.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = Array.Empty<string>();

    public CancellationToken CancellationToken
    {
        get { lock (_sync) return _cts.Token; }
    }

    public bool IsFinished => State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled;

    /// <summary>
    /// Falha que a repeticao nao resolve: DRM. Tentar de novo devolveria exatamente o mesmo
    /// erro, e oferecer o botao seria mentir sobre o que o BlobTrap faz.
    /// </summary>
    public bool IsPermanentFailure { get; internal set; }

    /// <summary>
    /// Um job que falhou ou foi cancelado pode voltar para a fila. Concluido nao: o arquivo
    /// ja esta la, e refazer por cima seria destruir o resultado sem o usuario pedir.
    /// </summary>
    public bool CanRetry =>
        (State is DownloadState.Failed or DownloadState.Canceled) && !IsPermanentFailure;

    public void Cancel()
    {
        CancellationTokenSource source;
        lock (_sync) source = _cts;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished between the IsFinished check and here, and the manager
            // already disposed the source. There is nothing left to cancel.
        }
    }

    /// <summary>
    /// Zera o job para uma nova tentativa: token novo, progresso, erro e avisos limpos.
    ///
    /// O plano fica intocado - e justamente ele que torna a nova tentativa barata, porque a
    /// qualidade, a faixa de audio e o destino ja foram escolhidos e continuam validos.
    /// </summary>
    internal bool TryPrepareForRetry()
    {
        lock (_sync)
        {
            if (!CanRetry) return false;

            // A fonte antiga so pode ser descartada aqui, e nao no fim do job: ate a nova
            // tentativa a interface ainda le o token para decidir se mostra "Cancelar".
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            State = DownloadState.Queued;
            Progress = new DownloadProgress();
            ErrorMessage = null;
            Warnings = Array.Empty<string>();
            CompletedAt = null;
            Attempt++;

            return true;
        }
    }

    /// <summary>Quantas vezes este job ja rodou. 1 na primeira, 2 na primeira retentativa.</summary>
    public int Attempt { get; private set; } = 1;

    /// <summary>Raised on the thread that made the change; the UI marshals it.</summary>
    public event EventHandler? Changed;

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
