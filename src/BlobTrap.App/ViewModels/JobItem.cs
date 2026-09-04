using System.Windows.Threading;
using BlobTrap.Core.Models;
using BlobTrap.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlobTrap.App.ViewModels;

/// <summary>One row in the downloads list, mirroring a <see cref="DownloadJob"/> onto the UI thread.</summary>
/// <summary>
/// Fixed display values for the design preview. A real job's State and Progress are written by
/// the download engine and cannot be set from outside it, so the preview substitutes just those
/// two while everything else stays a genuine <see cref="DownloadJob"/>.
/// </summary>
public sealed record JobPreview(
    string StateLabel,
    string DetailLabel,
    double ProgressPercent,
    bool IsCompleted = false,
    bool IsFailed = false,
    bool CanCancel = true,
    bool IsIndeterminate = false,
    bool CanRetry = false);

public sealed partial class JobItem : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly JobPreview? _preview;
    private readonly Func<DownloadJob, bool>? _retry;

    public JobItem(DownloadJob job, Dispatcher dispatcher, Func<DownloadJob, bool>? retry = null)
    {
        Job = job;
        _dispatcher = dispatcher;
        _retry = retry;
        job.Changed += OnJobChanged;
    }

    public JobItem(DownloadJob job, Dispatcher dispatcher, JobPreview preview)
        : this(job, dispatcher, retry: null)
        => _preview = preview;

    public DownloadJob Job { get; }

    public string Title => Job.Title;

    public string OutputPath => Job.OutputPath;

    public string SourceLabel => Job.Plan.Source.Title;

    public string QualityLabel => Job.Plan.Video.Label;

    public double ProgressPercent => _preview?.ProgressPercent ?? (Job.Progress.Fraction ?? 0) * 100;

    public bool IsIndeterminate => _preview?.IsIndeterminate
        ?? (Job.Progress.Fraction is null && Job.State is DownloadState.Downloading or DownloadState.Preparing);

    public string StateLabel => _preview?.StateLabel ?? Job.State switch
    {
        DownloadState.Queued => "Na fila",
        DownloadState.Preparing => "Preparando",
        DownloadState.Downloading => Job.Progress.Stage ?? "Baixando",
        DownloadState.Muxing => "Finalizando",
        DownloadState.Completed => "Concluído",
        DownloadState.Failed => "Falhou",
        DownloadState.Canceled => "Cancelado",
        _ => Job.State.ToString(),
    };

    public string DetailLabel
    {
        get
        {
            if (_preview is not null) return _preview.DetailLabel;
            if (Job.State == DownloadState.Failed) return Job.ErrorMessage ?? "Erro desconhecido";

            if (Job.State == DownloadState.Completed)
            {
                // A partial success still says so, instead of reading as a clean finish.
                return Job.Warnings.Count > 0
                    ? $"{Job.OutputPath}  -  {string.Join("; ", Job.Warnings)}"
                    : Job.OutputPath;
            }
            if (Job.State is DownloadState.Queued or DownloadState.Preparing) return QualityLabel;

            var progress = Job.Progress;
            var parts = new List<string> { progress.ProgressLabel };

            if (progress.BytesPerSecond > 1) parts.Add(progress.SpeedLabel);
            if (progress.Eta is { } eta && eta > TimeSpan.Zero) parts.Add($"faltam {Naming.FormatDuration(eta.TotalSeconds)}");

            return string.Join("  -  ", parts);
        }
    }

    public bool CanCancel => _preview?.CanCancel ?? !Job.IsFinished;

    public bool IsCompleted => _preview?.IsCompleted ?? Job.State == DownloadState.Completed;

    public bool IsFailed => _preview?.IsFailed ?? Job.State == DownloadState.Failed;

    /// <summary>Falhou ou foi cancelado, e a fila sabe como reenfileirar.</summary>
    public bool CanRetry => _preview?.CanRetry ?? (_retry is not null && Job.CanRetry);

    [RelayCommand]
    private void Cancel() => Job.Cancel();

    [RelayCommand]
    private void Retry() => _retry?.Invoke(Job);

    private void OnJobChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess()) RaiseAll();
        else _dispatcher.BeginInvoke(RaiseAll);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(CanRetry));
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    public void Detach() => Job.Changed -= OnJobChanged;
}
