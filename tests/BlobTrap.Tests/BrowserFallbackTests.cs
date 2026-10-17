using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using BlobTrap.Core.Download;
using BlobTrap.Core.Net;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O 403 de 2026-10-17: uma plataforma de curso servia o HLS ao navegador e recusava o mesmo
/// link ao BlobTrap. O fallback pede ao navegador o que o cliente HTTP não conseguiu.
///
/// O servidor aqui recusa tudo, e um navegador falso serve um "arquivo" em memória respeitando
/// Range — é o cenário exato: a CDN só aceita quem está dentro do navegador.
/// </summary>
public sealed class BrowserFallbackTests : IAsyncLifetime
{
    private static readonly RequestContext DaPagina = new() { Referer = "https://portal.exemplo.com/aula" };

    private HttpListener? _listener;
    private string _prefix = string.Empty;
    private CancellationTokenSource? _serving;
    private int _directHits;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "blobtrap-fallback-" + Guid.NewGuid().ToString("N")[..8]);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();

        _prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();

        _serving = new CancellationTokenSource();
        _ = RefuseAllAsync(_listener, _serving.Token);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _serving?.Cancel();
        try { _listener?.Stop(); } catch (ObjectDisposedException) { }
        _listener?.Close();

        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }

        return Task.CompletedTask;
    }

    private async Task RefuseAllAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }

            Interlocked.Increment(ref _directHits);
            context.Response.StatusCode = 403;
            context.Response.Close();
        }
    }

    private Uri Url(string path) => new(_prefix + path);

    [Fact]
    public async Task Segmento_RecusadoNoCliente_VemPeloNavegador()
    {
        var browser = new FakeBrowser(Content(1000));
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var bytes = await http.GetBytesAsync(Url("seg1.ts"), DaPagina, null, CancellationToken.None);

        Assert.Equal(Content(1000), bytes);
    }

    [Fact]
    public async Task Manifesto_RecusadoNoCliente_VemPeloNavegador()
    {
        var browser = new FakeBrowser("#EXTM3U\n#EXT-X-ENDLIST\n"u8.ToArray(), "application/vnd.apple.mpegurl");
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var text = await http.GetStringAsync(Url("playlist.m3u8"), DaPagina, CancellationToken.None);

        Assert.StartsWith("#EXTM3U", text);
    }

    [Fact]
    public async Task PedidoSemReferer_NaoVaiAoNavegador()
    {
        // Download de ferramenta (ffmpeg do GitHub) não veio de página nenhuma: um 403 ali é
        // resposta de verdade, e mandar ao navegador só esconderia o erro.
        var browser = new FakeBrowser(Content(10));
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetBytesAsync(Url("ffmpeg.zip"), RequestContext.Default, null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task NavegadorTambemRecusado_O403OriginalPrevalece()
    {
        // Link expirado ou conta sem acesso: o usuário tem que ver o 403 do servidor, não
        // um erro inventado pelo fallback.
        var browser = new FakeBrowser(Content(10)) { Status = 403 };
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetBytesAsync(Url("seg1.ts"), DaPagina, null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task HostJaConhecido_PulaATentativaDireta()
    {
        // Um HLS de 900 segmentos não pode pagar 900 recusas antes de cada fallback.
        var browser = new FakeBrowser(Content(100));
        using var http = new MediaHttpClient { BrowserFallback = browser };

        await http.GetBytesAsync(Url("seg1.ts"), DaPagina, null, CancellationToken.None);
        var depoisDoPrimeiro = Volatile.Read(ref _directHits);

        await http.GetBytesAsync(Url("seg2.ts"), DaPagina, null, CancellationToken.None);
        await http.GetBytesAsync(Url("seg3.ts"), DaPagina, null, CancellationToken.None);

        Assert.Equal(depoisDoPrimeiro, Volatile.Read(ref _directHits));
    }

    [Theory]
    [InlineData(20 * 1024 * 1024 + 123)] // sequencial: abaixo do corte de download paralelo
    [InlineData(30 * 1024 * 1024 + 7)]   // paralelo: pedaços com Range, cada um em vários blocos
    public async Task ArquivoGrande_PeloNavegador_SaiIgualAoOriginal(int size)
    {
        var original = Content(size);
        var browser = new FakeBrowser(original);
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var output = Path.Combine(_root, $"video-{size}.mp4");
        await new ProgressiveDownloader(http).DownloadAsync(Url("video.mp4"), output, DaPagina, null, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(output));

        // Nenhum pedido ao navegador passou do tamanho de bloco: é o que mantém cada ida e
        // volta pelo CDP pequena, por maior que seja o arquivo.
        Assert.All(browser.Ranges, r => Assert.True(r.End - r.Start + 1 <= BrowserRangeStream.BlockSize));
    }

    [Theory]
    [InlineData(20 * 1024 * 1024 + 123)]        // termina num bloco curto
    [InlineData(2 * BrowserRangeStream.BlockSize)] // termina exatamente num bloco cheio: só o 416 avisa
    public async Task SemContentRange_PorCors_BaixaInteiroMesmoAssim(int size)
    {
        // fetch() de um frame de outra origem só enxerga Content-Range se a CDN o expuser.
        // Sem o total, entregar só o primeiro bloco seria salvar um arquivo truncado.
        var original = Content(size);
        var browser = new FakeBrowser(original) { HideContentRange = true };
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var output = Path.Combine(_root, $"cors-{size}.mp4");
        await new ProgressiveDownloader(http).DownloadAsync(Url("video.mp4"), output, DaPagina, null, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task BlocoQueFalhaNoMeio_FalhaODownload_NaoTrunca()
    {
        // Devolver fim de stream aqui salvaria um arquivo cortado como se estivesse completo.
        var browser = new FakeBrowser(Content(20 * 1024 * 1024)) { FailFromOffset = 9 * 1024 * 1024 };
        using var http = new MediaHttpClient { BrowserFallback = browser };

        var output = Path.Combine(_root, "cortado.mp4");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ProgressiveDownloader(http).DownloadAsync(Url("video.mp4"), output, DaPagina, null, CancellationToken.None));

        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(null, 0L, null)]
    [InlineData("bytes=100-", 100L, null)]
    [InlineData("bytes=100-199", 100L, 199L)]
    public void ParseRange_LeOsDoisFormatos(string? range, long start, long? end)
    {
        Assert.Equal((start, end), MediaHttpClient.ParseRange(range));
    }

    [Theory]
    [InlineData("bytes 0-0/5000", 5000L)]
    [InlineData("bytes 0-0/*", null)]
    [InlineData(null, null)]
    public void TotalFromContentRange_LeOTotal(string? header, long? expected)
    {
        Assert.Equal(expected, MediaHttpClient.TotalFromContentRange(header));
    }

    [Fact]
    public void DescribeSent_DizSeHaviaSessaoSemMostrarACookie()
    {
        var described = MediaHttpClient.DescribeSent(DaPagina with { Cookie = "sessao=segredo" });

        Assert.Contains("com Cookie", described);
        Assert.Contains("Referer", described);
        Assert.DoesNotContain("segredo", described);
    }

    [Theory]
    [InlineData("sessao=1", "com Cookie")]
    [InlineData(null, "sem Cookie")]
    public void DescribeSent_SobreviveARedactionDoLog(string? cookie, string expected)
    {
        // "Cookie: sim" virava "Cookie: [redigido]" no log - e o fato que separa as causas do
        // 403 sumia na única linha que existe para registrá-lo.
        var described = MediaHttpClient.DescribeSent(DaPagina with { Cookie = cookie });

        Assert.Contains(expected, BlobTrap.Core.Diagnostics.Redaction.Scrub(described));
    }

    /// <summary>Byte i vale i mod 251: um bloco no lugar errado muda o arquivo de forma detectável.</summary>
    private static byte[] Content(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private sealed class FakeBrowser(byte[] content, string contentType = "video/mp4") : IBrowserFetcher
    {
        private int _calls;

        public int Status { get; init; } = 200;
        public long FailFromOffset { get; init; } = long.MaxValue;
        public bool HideContentRange { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentBag<(long Start, long End)> Ranges { get; } = new();

        public Task<BrowserFetchResult?> FetchAsync(Uri url, RequestContext context, string? range, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (Status != 200)
                return Task.FromResult<BrowserFetchResult?>(new BrowserFetchResult(Status, null, null, []));

            if (range is null)
                return Task.FromResult<BrowserFetchResult?>(new BrowserFetchResult(200, contentType, null, content));

            var (start, end) = MediaHttpClient.ParseRange(range);
            var last = Math.Min(end ?? content.Length - 1, content.Length - 1);
            Ranges.Add((start, last));

            if (start >= FailFromOffset)
                return Task.FromResult<BrowserFetchResult?>(new BrowserFetchResult(403, null, null, []));

            if (start >= content.Length)
                return Task.FromResult<BrowserFetchResult?>(new BrowserFetchResult(416, null, null, []));

            var body = content[(int)start..((int)last + 1)];
            var contentRange = HideContentRange ? null : $"bytes {start}-{last}/{content.Length}";
            return Task.FromResult<BrowserFetchResult?>(new BrowserFetchResult(206, contentType, contentRange, body));
        }
    }
}
