using BlobTrap.Core.Net;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Models;

/// <summary>
/// A media URL observed on the wire. This is the sniffer's output - it says "something
/// playable lives here", not yet which qualities exist. Resolving turns it into a
/// <see cref="MediaSource"/> with selectable variants.
/// </summary>
public sealed class MediaCandidate
{
    public MediaCandidate(Uri url, MediaKind kind, RequestContext request)
    {
        Url = url;
        Kind = kind;
        Request = request;
        Id = Naming.StableId(url.AbsoluteUri);
        FirstSeen = DateTimeOffset.Now;
        LastSeen = FirstSeen;
    }

    public string Id { get; }
    public Uri Url { get; }
    public MediaKind Kind { get; }
    public RequestContext Request { get; set; }

    public string? MimeType { get; set; }
    public long? ContentLength { get; set; }
    public Uri? PageUrl { get; set; }
    public string? PageTitle { get; set; }

    public DateTimeOffset FirstSeen { get; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>How many times this exact URL was requested. High counts mark a live or looping stream.</summary>
    public int HitCount { get; set; } = 1;

    /// <summary>Set once a manifest is parsed and found to advertise DRM.</summary>
    public bool IsProtected { get; set; }

    public string? ProtectionSystem { get; set; }

    /// <summary>Human label for the candidate list.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(PageTitle) && Kind.IsStreaming()
            ? PageTitle!
            : Naming.NameFromUrl(Url);

    public string ShortUrl
    {
        get
        {
            var text = Url.AbsoluteUri;
            return text.Length <= 90 ? text : text[..60] + "..." + text[^25..];
        }
    }

    public override string ToString() => $"{Kind}: {Url}";
}
