using BlobTrap.Core.Models;
using BlobTrap.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlobTrap.App.ViewModels;

/// <summary>One row in the "detected media" list.</summary>
public sealed partial class CandidateItem : ObservableObject
{
    public CandidateItem(MediaCandidate candidate) => Candidate = candidate;

    public MediaCandidate Candidate { get; }

    public string Id => Candidate.Id;

    public string Title => Candidate.DisplayName;

    public string Url => Candidate.Url.AbsoluteUri;

    public string ShortUrl => Candidate.ShortUrl;

    public string KindLabel => Candidate.Kind.ToDisplayString();

    /// <summary>
    /// Short form for the badge. The full names ("Arquivo de vídeo") tower over "HLS" and
    /// "DASH" beside them, so the badges stop reading as one set of labels.
    /// </summary>
    public string BadgeLabel => Candidate.Kind switch
    {
        MediaKind.HlsPlaylist => "HLS",
        MediaKind.DashManifest => "DASH",
        MediaKind.SmoothManifest => "SMOOTH",
        MediaKind.ProgressiveVideo => "VÍDEO",
        MediaKind.ProgressiveAudio => "ÁUDIO",
        MediaKind.Subtitle => "LEGENDA",
        MediaKind.PageEmbed => "PÁGINA",
        _ => "MÍDIA",
    };

    public string Host => Candidate.Url.Host;

    /// <summary>
    /// A plain file shows its size. A stream cannot: the size is only known once the manifest
    /// is parsed. Showing a request count there put two different units in the same column,
    /// so the sizes could not be compared - and "req" was our word, not the user's.
    /// </summary>
    public string SizeLabel => Candidate.Kind.IsStreaming()
        ? "manifesto"
        : Naming.FormatBytes(Candidate.ContentLength);

    public bool IsStream => Candidate.Kind.IsStreaming();

    /// <summary>Drives the accent colour of the kind badge.</summary>
    public string BadgeKey => Candidate.Kind switch
    {
        MediaKind.HlsPlaylist => "Hls",
        MediaKind.DashManifest => "Dash",
        MediaKind.ProgressiveVideo => "File",
        MediaKind.ProgressiveAudio => "Áudio",
        MediaKind.Subtitle => "Subtitle",
        MediaKind.PageEmbed => "Page",
        _ => "Other",
    };

    /// <summary>Called when the registry sees the same URL again.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(Title));
    }
}
