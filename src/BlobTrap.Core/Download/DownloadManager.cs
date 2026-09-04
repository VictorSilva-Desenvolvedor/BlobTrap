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
    private readonly DownloadExecutor _executor;
    private readonly ConcurrentQueue<DownloadJob> _pending = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly object _sync = new();
    private SemaphoreSlim _slots;
    private int _maxConcurrent = 2;

    public DownloadManager(DownloadExecutor executor)
    {
        _executor = executor;
        _slots = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
    }

    public event EventHandler<DownloadJob>? JobAdded;
    public event EventHandler<DownloadJob>? JobChanged;

    /// <summary>How many downloads run at the same time. Changing it takes effect for new jobs.</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            var clamped = Math.Clamp(value, 1, 8);
            lock (_sync)
            {
                if (clamped == _maxConcurrent) return;
                _maxConcurrent = clamped;
                // Replacing the semaphore is safe: running jobs release the instance they took.
                _slots = new SemaphoreSlim(clamped, clamped);
            }
        }
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

        _ = Task.Run(() => PumpAsync());
        return job;
    }

    private async Task PumpAsync()
    {
        if (!_pending.TryDequeue(out var job)) return;

        var slots = _slots;
        await slots.WaitAsync().ConfigureAwait(false);

        try
        {
            await RunAsync(job).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
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

        var progress = new Progress<DownloadProgress>(p =>
        {
            job.Progress = p;
            if (job.State != DownloadState.Downloading && p.Stage != "Finalizando")
                job.State = DownloadState.Downloading;
            else if (p.Stage == "Finalizando")
                job.State = DownloadState.Muxing;

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

    public void Dispose()
    {
        CancelAll();
        _slots.Dispose();
    }
}
