using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BlobTrap.App.Theming;
using BlobTrap.App.ViewModels;
using BlobTrap.Core.Download;
using BlobTrap.Core.Models;
using BlobTrap.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace BlobTrap.App.Views;

/// <summary>A subtitle track with its checkbox state.</summary>
public sealed partial class SubtitleOption : ObservableObject
{
    public SubtitleOption(MediaVariant variant) => Variant = variant;

    public MediaVariant Variant { get; }

    public string Label => Variant.Name ?? Variant.Language ?? "Legenda";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Lets the user pick a quality, an audio track and where the file lands.</summary>
public partial class QualityWindow : Window
{
    private readonly MediaSource _source;
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<SubtitleOption> _subtitles = new();

    public QualityWindow(MediaSource source, MainViewModel viewModel)
    {
        InitializeComponent();

        // Windows 11 gives dialogs the transient acrylic material rather than Mica.
        WindowEffects.Attach(this, Backdrop.Acrylic);

        _source = source;
        _viewModel = viewModel;

        TitleText.Text = source.Title;
        SubtitleText.Text = BuildSubtitleLine(source);

        var videoVariants = source.VideoVariants.ToList();
        VideoList.ItemsSource = videoVariants;
        VideoList.SelectedItem = videoVariants.FirstOrDefault();
        VideoList.SelectionChanged += (_, _) => OnSelectionChanged();

        var audioVariants = source.AudioVariants.ToList();
        AudioCombo.ItemsSource = audioVariants;
        AudioRow.Visibility = audioVariants.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var subtitle in source.SubtitleVariants) _subtitles.Add(new SubtitleOption(subtitle));
        SubtitleList.ItemsSource = _subtitles;
        SubtitleRow.Visibility = _subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        OnSelectionChanged();
    }

    /// <summary>The plan the user confirmed, or null when they cancelled.</summary>
    public DownloadPlan? Result { get; private set; }

    private static string BuildSubtitleLine(MediaSource source)
    {
        var parts = new List<string> { source.Kind.ToDisplayString() };

        if (source.DurationSeconds is > 0) parts.Add(Naming.FormatDuration(source.DurationSeconds));
        if (source.IsLive) parts.Add("transmissão ao vivo");
        parts.Add(source.Variants.Count == 1 ? "1 faixa" : $"{source.Variants.Count} faixas");
        parts.Add($"via {source.ResolvedBy}");

        return string.Join("  -  ", parts);
    }

    /// <summary>Keeps the audio pairing, the file name and the warning line in step with the choice.</summary>
    private void OnSelectionChanged()
    {
        if (VideoList.SelectedItem is not MediaVariant video) return;

        var suggested = _source.BestAudioFor(video);
        if (suggested is not null && AudioCombo.Items.Count > 0) AudioCombo.SelectedItem = suggested;
        else if (AudioCombo.SelectedItem is null && AudioCombo.Items.Count > 0) AudioCombo.SelectedIndex = 0;

        OutputBox.Text = _viewModel.BuildOutputPath(_source, video, AudioOnlyCheck.IsChecked == true);

        var warning = BuildWarning(video);
        WarningText.Text = warning;
        WarningPanel.Visibility = warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private string BuildWarning(MediaVariant video)
    {
        // Espaco vem antes das demais: as outras descrevem um resultado pior, esta descreve
        // um download que nao vai terminar. O dialogo avisa, mas nao bloqueia - quem confirmar
        // recebe o mesmo erro do executor, so que agora dizendo o que faltou.
        var space = DescribeSpace(video);
        if (space.Length > 0) return space;

        if (video.IsLive)
            return "Transmissão ao vivo: o download captura o que já está no manifesto.";

        // Describes what actually happens rather than sounding like a block: the download
        // still runs, it just cannot merge the two tracks at the end.
        if (video.Track == TrackKind.VideoOnly && !_viewModel.HasFfmpeg)
            return "Sem ffmpeg, o vídeo e o áudio serão salvos como dois arquivos separados. "
                 + "Instale-o em Ferramentas para receber um só.";

        if (video.Delivery is DeliveryMode.HlsSegments or DeliveryMode.DashSegments && !_viewModel.HasFfmpeg)
            return "Sem ffmpeg o arquivo sai como stream bruto (.ts), reproduzível no VLC.";

        return string.Empty;
    }

    /// <summary>
    /// Avisa quando o volume de destino nao comporta a escolha. Silencioso quando o espaco
    /// livre nao pode ser lido: "nao sei" nao e' motivo para alarmar ninguem.
    /// </summary>
    private string DescribeSpace(MediaVariant video)
    {
        var estimate = video.EstimatedBytes;
        if (estimate is not > 0) return string.Empty;

        var available = DiskSpace.AvailableFor(OutputBox.Text);
        if (available is null) return string.Empty;

        var required = DiskSpace.RequiredFor(estimate);
        if (available.Value >= required) return string.Empty;

        return $"Espaco insuficiente no destino: esta qualidade precisa de cerca de "
             + $"{Naming.FormatBytes(required)} e ha {Naming.FormatBytes(available.Value)} livres.";
    }

    private void OnAudioOnlyChanged(object sender, RoutedEventArgs e) => OnSelectionChanged();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var current = OutputBox.Text;

        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(current),
            InitialDirectory = Path.GetDirectoryName(current) ?? _viewModel.Settings.DownloadDirectory,
            Filter = "Video MP4|*.mp4|Video WebM|*.webm|Audio M4A|*.m4a|Audio MP3|*.mp3|Todos os arquivos|*.*",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FileName;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (VideoList.SelectedItem is not MediaVariant video)
        {
            MessageBox.Show(this, "Escolha uma qualidade.", "BlobTrap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputPath = OutputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show(this, "Informe o arquivo de destino.", "BlobTrap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Pasta de destino inválida: {ex.Message}", "BlobTrap",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var audioOnly = AudioOnlyCheck.IsChecked == true;

        // Audio only, or a video-only track that has to be merged, both need the audio selection.
        var needsAudio = audioOnly || video.Track == TrackKind.VideoOnly;
        var audio = needsAudio ? AudioCombo.SelectedItem as MediaVariant ?? _source.BestAudioFor(video) : null;

        Result = new DownloadPlan
        {
            Source = _source,
            Video = video,
            Audio = audio,
            Subtitles = _subtitles.Where(s => s.IsSelected).Select(s => s.Variant).ToList(),
            OutputPath = Path.GetFullPath(outputPath),
            AudioOnly = audioOnly,
        };

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
