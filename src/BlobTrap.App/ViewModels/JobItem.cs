using System.Windows.Threading;
using BlobTrap.Core.Models;
using BlobTrap.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlobTrap.App.ViewModels;

/// <summary>One row in the downloads list, mirroring a <see cref="DownloadJob"/> onto the UI thread.</summary>
public sealed partial class JobItem : ObservableObject
{
    private readonly Dispatcher _dispatcher;

    public JobItem(DownloadJob job, Dispatcher dispatcher)
    {
        Job = job;
        _dispatcher = dispatcher;
        job.Changed += OnJobChanged;
    }

    public DownloadJob Job { get; }

    public string Title => Job.Title;

    public string OutputPath => Job.OutputPath;

    public string SourceLabel => Job.Plan.Source.Title;

    public string QualityLabel => Job.Plan.Video.Label;

    public double ProgressPercent => (Job.Progress.Fraction ?? 0) * 100;

    public bool IsIndeterminate => Job.Progress.Fraction is null && Job.State is DownloadState.Downloading or DownloadState.Preparing;

    public string StateLabel => Job.State switch
    {
        DownloadState.Queued => "Na fila",
        DownloadState.Preparing => "Preparando",
        DownloadState.Downloading => Job.Progress.Stage ?? "Baixando",
        DownloadState.Muxing => "Finalizando",
        DownloadState.Completed => "Concluido",
        DownloadState.Failed => "Falhou",
        DownloadState.Canceled => "Cancelado",
        _ => Job.State.ToString(),
    };

    public string DetailLabel
    {
        get
        {
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

    public bool CanCancel => !Job.IsFinished;

    public bool IsCompleted => Job.State == DownloadState.Completed;

    public bool IsFailed => Job.State == DownloadState.Failed;

    [RelayCommand]
    private void Cancel() => Job.Cancel();

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
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Detach() => Job.Changed -= OnJobChanged;
}
