namespace BlobTrap.Core.Net;

/// <summary>
/// A read-only stream over a byte range fetched through <see cref="IBrowserFetcher"/> in
/// fixed-size blocks, one block at a time, as the reader asks for them.
///
/// The browser hands bodies back as one message each, so a 500 MB file or a 125 MB parallel
/// chunk cannot come in a single fetch. Blocks keep every round trip small, and reading lazily
/// keeps memory at one block no matter the size of the file.
/// </summary>
public sealed class BrowserRangeStream : Stream
{
    public const int BlockSize = 8 * 1024 * 1024;

    public const long UnknownEnd = long.MaxValue;

    private readonly IBrowserFetcher _fetcher;
    private readonly Uri _url;
    private readonly RequestContext _context;
    private long _lastInclusive;

    private byte[] _block;
    private int _blockOffset;
    private long _nextStart;

    /// <param name="firstBlock">The block already fetched to learn the total size.</param>
    /// <param name="nextStart">Absolute offset of the byte after <paramref name="firstBlock"/>.</param>
    /// <param name="lastInclusive">
    /// Absolute offset of the last byte to deliver, or <see cref="UnknownEnd"/> when the size is
    /// hidden: a cross-origin fetch only sees Content-Range if the CDN exposes it through CORS.
    /// Then the stream reads until a short block or a 416 says the file is over.
    /// </param>
    public BrowserRangeStream(
        IBrowserFetcher fetcher, Uri url, RequestContext context,
        byte[] firstBlock, long nextStart, long lastInclusive)
    {
        _fetcher = fetcher;
        _url = url;
        _context = context;
        _block = firstBlock;
        _nextStart = nextStart;
        _lastInclusive = lastInclusive;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_blockOffset >= _block.Length)
        {
            if (_nextStart > _lastInclusive) return 0;
            await FetchNextBlockAsync(cancellationToken).ConfigureAwait(false);
            if (_blockOffset >= _block.Length) return 0;
        }

        var count = Math.Min(buffer.Length, _block.Length - _blockOffset);
        _block.AsMemory(_blockOffset, count).CopyTo(buffer);
        _blockOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task FetchNextBlockAsync(CancellationToken cancellationToken)
    {
        var end = Math.Min(_nextStart + BlockSize - 1, _lastInclusive);
        var result = await _fetcher.FetchAsync(_url, _context, $"bytes={_nextStart}-{end}", cancellationToken)
            .ConfigureAwait(false);

        var endKnown = _lastInclusive != UnknownEnd;

        // Size unknown and the request started past the end: the previous block was the last.
        if (!endKnown && result?.Status == 416)
        {
            _block = [];
            _blockOffset = 0;
            _lastInclusive = _nextStart - 1;
            return;
        }

        // A block that fails mid-file must fail the download: returning 0 here would end the
        // stream early and the caller would save a truncated file as if it were complete.
        if (result is not { IsSuccess: true } || result.Body.Length == 0)
            throw new HttpRequestException(
                $"O navegador parou de entregar {Diagnostics.Redaction.ScrubUrl(_url)} no byte {_nextStart} " +
                $"(status {result?.Status.ToString() ?? "sem resposta"}).");

        _block = result.Body;
        _blockOffset = 0;
        _nextStart += result.Body.Length;

        // A short block with the size unknown is the tail of the file.
        if (!endKnown && result.Body.Length < end - (_nextStart - result.Body.Length) + 1)
            _lastInclusive = _nextStart - 1;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
