namespace BlobTrap.Core.Download;

/// <summary>
/// One fetchable piece of a stream. HLS segments, DASH segments and fMP4 init blobs all
/// reduce to this, so a single downloader serves every streaming format.
/// </summary>
public sealed record MediaPart
{
    public required Uri Uri { get; init; }

    /// <summary>An HTTP Range header value when the part is a slice of a larger file.</summary>
    public string? Range { get; init; }

    /// <summary>Init segments are written first and are never counted as content progress.</summary>
    public bool IsInitialization { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Where to fetch the AES-128 key, when the segment is encrypted.</summary>
    public Uri? KeyUri { get; init; }

    /// <summary>The 16-byte CBC initialisation vector for this segment.</summary>
    public byte[]? Iv { get; init; }

    public bool IsEncrypted => KeyUri is not null;
}

/// <summary>Tracks a moving-average transfer rate without allocating per sample.</summary>
public sealed class SpeedMeter
{
    private readonly object _sync = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private long _windowBytes;
    private long _totalBytes;
    private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
    private double _bytesPerSecond;

    public long TotalBytes { get { lock (_sync) return _totalBytes; } }

    public double BytesPerSecond
    {
        get
        {
            lock (_sync)
            {
                if (_bytesPerSecond > 0) return _bytesPerSecond;

                // A download that finishes before the first window closes still has a rate.
                var elapsed = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
                return elapsed > 0.1 && _totalBytes > 0 ? _totalBytes / elapsed : 0;
            }
        }
    }

    public void Add(long bytes)
    {
        lock (_sync)
        {
            _totalBytes += bytes;
            _windowBytes += bytes;

            var elapsed = (DateTimeOffset.UtcNow - _windowStart).TotalSeconds;
            if (elapsed < 0.5) return;

            var sample = _windowBytes / elapsed;
            // Exponential smoothing keeps the number readable instead of flickering.
            _bytesPerSecond = _bytesPerSecond <= 0 ? sample : _bytesPerSecond * 0.6 + sample * 0.4;

            _windowBytes = 0;
            _windowStart = DateTimeOffset.UtcNow;
        }
    }
}
