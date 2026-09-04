using System.Collections.Concurrent;
using BlobTrap.Core.Models;
using BlobTrap.Core.Resolving;

namespace BlobTrap.Core.Download;

/// <summary>
/// The download queue. Accepts plans, runs a bounded number at once, and reports state
/// changes through events so a UI can mirror it without polling.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    /// <summary>Teto do ajuste. Acima disto a rede vira o gargalo e o disco começa a serrar.</summary>
    public const int MaxAllowedConcurrent = 8;

    private readonly IDownloadRunner _executor;
    private readonly ConcurrentQueue<DownloadJob> _pending = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly object _sync = new();
    private readonly ConcurrencyGate _slots;

    public DownloadManager(IDownloadRunner executor)
    {
        _executor = executor;
        _slots = new ConcurrencyGate(2);
    }

    public event EventHandler<DownloadJob>? JobAdded;
    public event EventHandler<DownloadJob>? JobChanged;

    /// <summary>
    /// Quantos downloads correm ao mesmo tempo. Reduzir não interrompe o que já está em voo:
    /// o excedente drena conforme cada um termina.
    /// </summary>
    public int MaxConcurrent
    {
        get => _slots.Limit;
        set => _slots.SetLimit(Math.Clamp(value, 1, MaxAllowedConcurrent));
    }

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_sync) return _jobs.ToList(); }
    }

    public DownloadJob Enqueue(DownloadPlan plan)
    {
        var job = new DownloadJob(plan);

        lock (_sync) _jobs.Add(job);

        JobAdded?.Invoke(this, job);
        _pending.Enqueue(job);

        _ = Task.Run(PumpAsync);
        return job;
    }

    private async Task PumpAsync()
    {
        if (!_pending.TryDequeue(out var job)) return;

        await _slots.AcquireAsync().ConfigureAwait(false);

        try
        {
            await RunAsync(job).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task RunAsync(DownloadJob job)
    {
        if (job.CancellationToken.IsCancellationRequested)
        {
            Transition(job, DownloadState.Canceled);
            return;
        }

        Transition(job, DownloadState.Preparing);

        // Progresso aplicado na propria thread que reporta, e nao por Progress<T>.
        //
        // Progress<T> criado fora da thread de UI despacha via ThreadPool, ou seja, de forma
        // assincrona. Um relatorio ainda na fila rodava DEPOIS da transicao para Completed e
        // reescrevia o estado para Downloading - um download terminado que ficava preso em
        // "Baixando" para sempre, sem botao de abrir o arquivo. Aplicando na hora, quando
        // ExecuteAsync retorna nao existe mais relatorio pendente.
        //
        // Nao ha custo de thread de UI aqui: quem marshala e o JobItem, no evento Changed.
        var progress = new InlineProgress<DownloadProgress>(p =>
        {
            // Segunda trava: estado final e final. Sem isto, qualquer caminho futuro que
            // reporte progresso tarde volta a corromper o estado do mesmo jeito.
            if (job.IsFinished) return;

            job.Progress = p;
            job.State = p.Stage == "Finalizando" ? DownloadState.Muxing : DownloadState.Downloading;

            Notify(job);
        });

        try
        {
            await _executor.ExecuteAsync(job, progress, job.CancellationToken).ConfigureAwait(false);

            job.CompletedAt = DateTimeOffset.Now;
            Transition(job, DownloadState.Completed);
        }
        catch (OperationCanceledException)
        {
            Transition(job, DownloadState.Canceled);
        }
        catch (DrmProtectedException ex)
        {
            job.ErrorMessage = ex.Message;
            Transition(job, DownloadState.Failed);
        }
        catch (Exception ex)
        {
            job.ErrorMessage = ex.Message;
            Transition(job, DownloadState.Failed);
        }
        finally
        {
            job.DisposeToken();
        }
    }

    private void Transition(DownloadJob job, DownloadState state)
    {
        job.State = state;
        Notify(job);
    }

    private void Notify(DownloadJob job)
    {
        job.RaiseChanged();
        JobChanged?.Invoke(this, job);
    }

    public void CancelAll()
    {
        foreach (var job in Jobs) job.Cancel();
    }

    /// <summary>Drops finished jobs from the list. Files already written are untouched.</summary>
    public void ClearFinished()
    {
        lock (_sync) _jobs.RemoveAll(j => j.IsFinished);
    }

    public void Dispose() => CancelAll();
}

/// <summary>
/// <see cref="IProgress{T}"/> que roda o handler na thread que chamou Report, em vez de
/// despachar. Ver o comentario em <see cref="DownloadManager"/> para o porque.
/// </summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}
