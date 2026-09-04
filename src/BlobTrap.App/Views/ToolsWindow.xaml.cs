using System.Windows;
using BlobTrap.App.ViewModels;
using BlobTrap.Core.Tools;
using Microsoft.Win32;

namespace BlobTrap.App.Views;

/// <summary>Installs the external engines and edits the preferences that affect downloads.</summary>
public partial class ToolsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource? _install;

    public ToolsWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;

        FolderBox.Text = viewModel.Settings.DownloadDirectory;
        HideSmallCheck.IsChecked = viewModel.Settings.HideSmallFiles;
        IncludeAudioCheck.IsChecked = viewModel.Settings.IncludeAudioOnly;
        IncludeSubtitlesCheck.IsChecked = viewModel.Settings.IncludeSubtitles;

        ConcurrencyCombo.ItemsSource = new[] { 1, 2, 3, 4, 6, 8 };
        ConcurrencyCombo.SelectedItem = viewModel.Settings.MaxConcurrentDownloads;

        SegmentCombo.ItemsSource = new[] { 2, 4, 6, 8, 12, 16 };
        SegmentCombo.SelectedItem = viewModel.Settings.SegmentParallelism;

        RefreshToolLabels();
        Closed += (_, _) => _install?.Cancel();
    }

    private void RefreshToolLabels()
    {
        var ffmpeg = ToolLocator.Find(ExternalTool.Ffmpeg);
        FfmpegStatus.Text = ffmpeg ?? "Nao instalado - junta video e audio e converte containers.";
        InstallFfmpegButton.Content = ffmpeg is null ? "Instalar" : "Reinstalar";

        var ytDlp = ToolLocator.Find(ExternalTool.YtDlp);
        YtDlpStatus.Text = ytDlp ?? "Nao instalado - extrai video de sites que nao expoem o manifesto.";
        InstallYtDlpButton.Content = ytDlp is null ? "Instalar" : "Reinstalar";
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
            InstallStatus.Text = "Instalacao concluida.";
        }
        catch (OperationCanceledException)
        {
            InstallStatus.Text = "Instalacao cancelada.";
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
