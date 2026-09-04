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

    public string Host => Candidate.Url.Host;

    /// <summary>Streams get a badge; a plain file shows its size instead.</summary>
    public string SizeLabel => Candidate.Kind.IsStreaming()
        ? (Candidate.HitCount > 1 ? $"{Candidate.HitCount} req" : "stream")
        : Naming.FormatBytes(Candidate.ContentLength);

    public bool IsStream => Candidate.Kind.IsStreaming();

    /// <summary>Drives the accent colour of the kind badge.</summary>
    public string BadgeKey => Candidate.Kind switch
    {
        MediaKind.HlsPlaylist => "Hls",
        MediaKind.DashManifest => "Dash",
        MediaKind.ProgressiveVideo => "File",
        MediaKind.ProgressiveAudio => "Audio",
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
