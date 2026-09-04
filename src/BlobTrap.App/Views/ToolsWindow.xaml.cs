using System.IO;
using System.Windows;
using BlobTrap.App.Theming;
using BlobTrap.App.ViewModels;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Tools;
using Microsoft.Win32;

namespace BlobTrap.App.Views;

/// <summary>Installs the external engines and edits the preferences that affect downloads.</summary>
public partial class ToolsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource? _install;
    private CancellationTokenSource? _versionProbe;

    public ToolsWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        WindowEffects.Attach(this, Backdrop.Acrylic);

        _viewModel = viewModel;

        FolderBox.Text = viewModel.Settings.DownloadDirectory;
        HideSmallCheck.IsChecked = viewModel.Settings.HideSmallFiles;
        IncludeAudioCheck.IsChecked = viewModel.Settings.IncludeAudioOnly;
        IncludeSubtitlesCheck.IsChecked = viewModel.Settings.IncludeSubtitles;

        ConcurrencyCombo.ItemsSource = new[] { 1, 2, 3, 4, 6, 8 };
        ConcurrencyCombo.SelectedItem = viewModel.Settings.MaxConcurrentDownloads;

        SegmentCombo.ItemsSource = new[] { 2, 4, 6, 8, 12, 16 };
        SegmentCombo.SelectedItem = viewModel.Settings.SegmentParallelism;

        AppVersionText.Text = $"BlobTrap {AppVersion.Current}";

        RefreshToolLabels();
        Closed += (_, _) => { _versionProbe?.Cancel(); _install?.Cancel(); };
    }

    private void RefreshToolLabels()
    {
        var ffmpeg = ToolLocator.Find(ExternalTool.Ffmpeg);
        FfmpegStatus.Text = ffmpeg ?? "Não instalado - junta vídeo e áudio e converte containers.";
        InstallFfmpegButton.Content = ffmpeg is null ? "Instalar" : "Reinstalar";

        var ytDlp = ToolLocator.Find(ExternalTool.YtDlp);
        YtDlpStatus.Text = ytDlp ?? "Não instalado - extrai vídeo de sites que não expõem o manifesto.";
        InstallYtDlpButton.Content = ytDlp is null ? "Instalar" : "Reinstalar";

        _ = ShowVersionsAsync();
    }

    /// <summary>
    /// Pergunta a versão a cada binário e a mostra ao lado do caminho.
    ///
    /// Importa mais para o yt-dlp do que parece: ele é o caminho para os sites que mudam de
    /// extração toda semana, e uma cópia de seis meses atrás falha em silêncio - o download
    /// simplesmente não acha formato, e nada na tela sugeria que a causa fosse idade.
    ///
    /// Cada binário custa um processo, então isto roda fora da construção da janela; a tela
    /// abre com o caminho e a versão chega depois.
    /// </summary>
    private async Task ShowVersionsAsync()
    {
        _versionProbe?.Cancel();
        _versionProbe?.Dispose();
        _versionProbe = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var token = _versionProbe.Token;

        await Task.WhenAll(
            AppendVersionAsync(FfmpegRunner.TryCreate()?.GetVersionAsync(token), FfmpegStatus, token),
            AppendVersionAsync(YtDlpRunner.TryCreate()?.GetVersionAsync(token), YtDlpStatus, token));
    }

    private static async Task AppendVersionAsync(Task<string?>? probe, System.Windows.Controls.TextBlock target, CancellationToken token)
    {
        if (probe is null) return;

        string? version;

        try
        {
            version = await probe;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            // Binario corrompido, ou a janela fechou antes da resposta. Fica so o caminho -
            // saber a versao nunca vale um erro na cara de quem so queria instalar algo.
            Log.Warn("ferramentas", "nao foi possivel ler a versao de um binario", ex);
            return;
        }

        if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(version)) return;

        target.Text = $"{version}  -  {target.Text}";
    }

    private async void OnInstallFfmpegClick(object sender, RoutedEventArgs e) => await InstallAsync(ffmpeg: true, ytDlp: false);

    private async void OnInstallYtDlpClick(object sender, RoutedEventArgs e) => await InstallAsync(ffmpeg: false, ytDlp: true);

    private async Task InstallAsync(bool ffmpeg, bool ytDlp)
    {
        if (_install is not null) return;

        _install = new CancellationTokenSource();

        InstallFfmpegButton.IsEnabled = false;
        InstallYtDlpButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;

        var progress = new Progress<ToolInstallProgress>(p =>
        {
            InstallStatus.Text = p.Stage;

            InstallProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } fraction) InstallProgress.Value = fraction * 100;
        });

        try
        {
            await _viewModel.InstallToolsAsync(ffmpeg, ytDlp, progress, _install.Token);
            InstallStatus.Text = "Instalação concluída.";
        }
        catch (OperationCanceledException)
        {
            InstallStatus.Text = "Instalação cancelada.";
        }
        catch (Exception ex)
        {
            InstallStatus.Text = $"Falhou: {ex.Message}";
        }
        finally
        {
            _install.Dispose();
            _install = null;

            InstallProgress.Visibility = Visibility.Collapsed;
            InstallFfmpegButton.IsEnabled = true;
            InstallYtDlpButton.IsEnabled = true;

            RefreshToolLabels();
        }
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = FolderBox.Text,
            Title = "Escolher pasta de downloads",
        };

        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.Settings;

        if (!string.IsNullOrWhiteSpace(FolderBox.Text)) settings.DownloadDirectory = FolderBox.Text.Trim();

        settings.HideSmallFiles = HideSmallCheck.IsChecked == true;
        settings.IncludeAudioOnly = IncludeAudioCheck.IsChecked == true;
        settings.IncludeSubtitles = IncludeSubtitlesCheck.IsChecked == true;

        if (ConcurrencyCombo.SelectedItem is int concurrency) settings.MaxConcurrentDownloads = concurrency;
        if (SegmentCombo.SelectedItem is int segments) settings.SegmentParallelism = segments;

        _viewModel.ApplySettings();
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
