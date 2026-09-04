using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BlobTrap.App.Settings;
using BlobTrap.Core.Download;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Resolving;
using BlobTrap.Core.Sniffing;
using BlobTrap.Core.Tools;
using BlobTrap.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlobTrap.App.ViewModels;

/// <summary>Lets the view own the quality dialog while the view model owns the flow.</summary>
public interface IMediaPicker
{
    Task<DownloadPlan?> PickAsync(MediaSource source, string downloadDirectory);

    void ShowMessage(string title, string message);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MediaHttpClient _http;
    private readonly MediaResolver _resolver;
    private readonly DownloadExecutor _executor;
    private readonly Dictionary<string, CandidateItem> _candidateIndex = new(StringComparer.Ordinal);

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        Settings = AppSettings.Load();
        _http = new MediaHttpClient();

        Registry = new MediaRegistry();
        ApplySnifferOptions();

        _resolver = new MediaResolver(_http);
        _executor = new DownloadExecutor(_http, _resolver) { SegmentParallelism = Settings.SegmentParallelism };

        Downloads = new DownloadManager(_executor) { MaxConcurrent = Settings.MaxConcurrentDownloads };

        Registry.CandidateAdded += OnCandidateAdded;
        Registry.CandidateUpdated += OnCandidateUpdated;
        Registry.Cleared += OnRegistryCleared;
        Downloads.JobAdded += OnJobAdded;

        AddressText = Settings.HomePage;
        RefreshToolState();
    }

    public AppSettings Settings { get; }

    public MediaRegistry Registry { get; }

    public DownloadManager Downloads { get; }

    public ObservableCollection<CandidateItem> Candidates { get; } = new();

    public ObservableCollection<JobItem> Jobs { get; } = new();

    /// <summary>Set by the window so the view model can ask for a quality choice.</summary>
    public IMediaPicker? Picker { get; set; }

    [ObservableProperty]
    private string _addressText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Pronto.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasFfmpeg;

    [ObservableProperty]
    private bool _hasYtDlp;

    [ObservableProperty]
    private string? _currentPageTitle;

    /// <summary>Raised when the view model wants the browser to navigate somewhere.</summary>
    public event EventHandler<Uri>? NavigationRequested;

    /// <summary>Raised by Ctrl+L so the view can put the caret in the address bar.</summary>
    public event EventHandler? FocusAddressRequested;

    [RelayCommand]
    private void FocusAddress() => FocusAddressRequested?.Invoke(this, EventArgs.Empty);

    public string ToolStatus => (HasFfmpeg, HasYtDlp) switch
    {
        (true, true) => "ffmpeg + yt-dlp prontos",
        (true, false) => "ffmpeg pronto - yt-dlp ausente",
        (false, true) => "yt-dlp pronto - ffmpeg ausente",
        _ => "ffmpeg e yt-dlp ausentes",
    };

    public void RefreshToolState()
    {
        HasFfmpeg = ToolLocator.IsAvailable(ExternalTool.Ffmpeg);
        HasYtDlp = ToolLocator.IsAvailable(ExternalTool.YtDlp);
        _resolver.YtDlp = YtDlpRunner.TryCreate();
        OnPropertyChanged(nameof(ToolStatus));
    }

    public void ApplySnifferOptions()
    {
        Registry.Options.MinProgressiveBytes = Settings.HideSmallFiles ? Settings.SmallFileThresholdBytes : 0;
        Registry.Options.IncludeAudio = Settings.IncludeAudioOnly;
        Registry.Options.IncludeSubtitles = Settings.IncludeSubtitles;
    }

    [RelayCommand]
    private void Navigate()
    {
        var target = NormalizeAddress(AddressText);
        if (target is null)
        {
            StatusText = "Endereco invalido.";
            return;
        }

        AddressText = target.AbsoluteUri;
        NavigationRequested?.Invoke(this, target);
    }

    /// <summary>Accepts a URL, a bare host, or a search phrase.</summary>
    internal static Uri? NormalizeAddress(string text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https" or "file")
            return absolute;

        var looksLikeHost = !trimmed.Contains(' ') && trimmed.Contains('.');
        if (looksLikeHost && Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var guessed))
            return guessed;

        return new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(trimmed));
    }

    [RelayCommand]
    private void ClearCandidates()
    {
        Registry.Clear();
        StatusText = "Lista limpa.";
    }

    /// <summary>Resolves a detected candidate, asks the user which quality, and queues it.</summary>
    [RelayCommand]
    private async Task DownloadCandidateAsync(CandidateItem? item)
    {
        if (item is null) return;
        await ResolveAndQueueAsync(item.Candidate);
    }

    /// <summary>Hands the current page to the extractor, which covers sites with no plain manifest.</summary>
    [RelayCommand]
    private async Task DownloadCurrentPageAsync()
    {
        var target = NormalizeAddress(AddressText);
        if (target is null) return;

        if (!HasYtDlp)
        {
            Picker?.ShowMessage("yt-dlp necessario",
                "Baixar a partir da pagina usa o yt-dlp. Instale-o pelo botao Ferramentas.");
            return;
        }

        var candidate = Registry.AddManual(target, RequestContext.Default with { Referer = target.AbsoluteUri },
            MediaKind.PageEmbed);

        await ResolveAndQueueAsync(candidate);
    }

    private async Task ResolveAndQueueAsync(MediaCandidate candidate)
    {
        if (Picker is null) return;

        IsBusy = true;
        StatusText = $"Analisando {candidate.Url.Host}...";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var source = await _resolver.ResolveAsync(candidate, timeout.Token);

            if (source.IsProtected)
            {
                Picker.ShowMessage("Conteudo protegido",
                    $"Esta midia usa DRM ({source.ProtectionSystem}). O BlobTrap nao baixa conteudo protegido por DRM.");
                StatusText = "Midia protegida por DRM.";
                return;
            }

            if (source.Variants.Count == 0)
            {
                StatusText = "Nenhum formato encontrado.";
                return;
            }

            Directory.CreateDirectory(Settings.DownloadDirectory);
            var plan = await Picker.PickAsync(source, Settings.DownloadDirectory);
            if (plan is null)
            {
                StatusText = "Download cancelado.";
                return;
            }

            Downloads.Enqueue(plan);
            StatusText = $"Na fila: {Path.GetFileName(plan.OutputPath)}";
        }
        catch (DrmProtectedException ex)
        {
            Picker.ShowMessage("Conteudo protegido", ex.Message);
            StatusText = "Midia protegida por DRM.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analise cancelada (tempo esgotado).";
        }
        catch (Exception ex)
        {
            Picker.ShowMessage("Nao foi possivel analisar", ex.Message);
            StatusText = "Falha ao analisar a midia.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.DownloadDirectory);
            Process.Start(new ProcessStartInfo(Settings.DownloadDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Nao foi possivel abrir a pasta: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenJobFile(JobItem? item)
    {
        if (item is null || !File.Exists(item.OutputPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(item.OutputPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Nao foi possivel abrir o arquivo: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearFinishedJobs()
    {
        Downloads.ClearFinished();

        foreach (var item in Jobs.Where(j => j.Job.IsFinished).ToList())
        {
            item.Detach();
            Jobs.Remove(item);
        }
    }

    /// <summary>Downloads ffmpeg and yt-dlp into the app's own bin folder.</summary>
    public async Task InstallToolsAsync(bool ffmpeg, bool ytDlp, IProgress<ToolInstallProgress> progress, CancellationToken cancellationToken)
    {
        var installer = new ToolInstaller(_http);

        if (ffmpeg) await installer.InstallFfmpegAsync(progress, cancellationToken);
        if (ytDlp) await installer.InstallYtDlpAsync(progress, cancellationToken);

        RefreshToolState();
    }

    public void ApplySettings()
    {
        ApplySnifferOptions();
        Downloads.MaxConcurrent = Settings.MaxConcurrentDownloads;
        _executor.SegmentParallelism = Settings.SegmentParallelism;
        Settings.Save();
    }

    private void OnCandidateAdded(object? sender, MediaCandidate candidate) =>
        RunOnUi(() =>
        {
            if (_candidateIndex.ContainsKey(candidate.Id)) return;

            var item = new CandidateItem(candidate);
            _candidateIndex[candidate.Id] = item;

            // Streams are what people are usually after, so they go to the top.
            if (item.IsStream) Candidates.Insert(0, item);
            else Candidates.Add(item);

            StatusText = $"{Candidates.Count} midia(s) detectada(s).";
        });

    private void OnCandidateUpdated(object? sender, MediaCandidate candidate) =>
        RunOnUi(() =>
        {
            if (_candidateIndex.TryGetValue(candidate.Id, out var item)) item.Refresh();
        });

    private void OnRegistryCleared(object? sender, EventArgs e) =>
        RunOnUi(() =>
        {
            Candidates.Clear();
            _candidateIndex.Clear();
        });

    private void OnJobAdded(object? sender, DownloadJob job) =>
        RunOnUi(() => Jobs.Insert(0, new JobItem(job, _dispatcher)));

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    /// <summary>Builds the default output path for a picked variant.</summary>
    public string BuildOutputPath(MediaSource source, MediaVariant variant, bool audioOnly)
    {
        var stem = Naming.SanitizeFileName(source.Title, "video");

        var quality = variant.Track == TrackKind.AudioOnly || audioOnly
            ? "audio"
            : variant.ResolutionLabel;

        var extension = audioOnly
            ? (variant.Container is "webm" or "opus" ? "opus" : "m4a")
            : ChooseContainer(variant);

        var fileName = $"{stem} [{quality}].{extension}";
        return Naming.EnsureUniquePath(Path.Combine(Settings.DownloadDirectory, fileName));
    }

    private static string ChooseContainer(MediaVariant variant)
    {
        if (variant.Delivery is DeliveryMode.HlsSegments or DeliveryMode.DashSegments)
            return variant.Container == "webm" ? "webm" : "mp4";

        return string.IsNullOrWhiteSpace(variant.Container) ? "mp4" : variant.Container;
    }

    public void Dispose()
    {
        Settings.Save();
        Downloads.Dispose();
        _http.Dispose();
    }
}
