using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BlobTrap.Core.Download;
using BlobTrap.Core.Net;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A prova que os testes de estado sozinhos não dão: que retomar produz <em>exatamente</em>
/// os mesmos bytes que baixar de uma vez só.
///
/// Um servidor local de verdade em vez de um mock do HttpClient, porque o que está sendo
/// verificado é a costura entre HTTP, escrita ordenada em disco e o ponto de retomada — e um
/// mock testaria justamente a parte que não é interessante.
/// </summary>
public class SegmentResumeEndToEndTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "blobtrap-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    private HttpListener? _listener;
    private string _prefix = string.Empty;
    private CancellationTokenSource? _serving;

    /// <summary>Segmento N é preenchido com o byte N — assim a emenda errada é detectável.</summary>
    private const int SegmentCount = 60;
    private const int SegmentSize = 512;

    /// <summary>Falha toda requisição a partir deste índice, quando ligado.</summary>
    private int _failFrom = int.MaxValue;

    /// <summary>Quais índices foram pedidos. É o que prova que a retomada pulou trabalho.</summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<int> _requested = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        // Porta 0 deixa o SO escolher uma livre; HttpListener nao aceita 0, entao a porta sai
        // de um socket temporario.
        var port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();

        _serving = new CancellationTokenSource();
        _ = ServeAsync(_listener, _serving.Token);

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

    [Fact]
    public async Task RetomarProduzOsMesmosBytesQueBaixarDeUmaVez()
    {
        var parts = Parts();

        // 1. Referencia: baixa tudo de uma vez.
        var expected = Path.Combine(_root, "inteiro.ts");
        using (var http = new MediaHttpClient())
            await new SegmentDownloader(http) { Parallelism = 4 }
                .DownloadAsync(parts, expected, RequestContext.Default, null, CancellationToken.None);

        var reference = await File.ReadAllBytesAsync(expected);
        Assert.Equal(SegmentCount * SegmentSize, reference.Length);

        // 2. Agora com interrupcao no meio.
        var resumed = Path.Combine(_root, "retomado.ts");
        _failFrom = 35;

        using (var http = new MediaHttpClient { MaxRetries = 0 })
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new SegmentDownloader(http) { Parallelism = 4 }
                    .DownloadAsync(parts, resumed, RequestContext.Default, null, CancellationToken.None));
        }

        // O parcial e o sidecar tem que ter sobrevivido - e' isso que torna a retomada possivel.
        Assert.True(File.Exists(resumed + ".part"), "o .part deveria ter sobrevivido a falha");
        Assert.True(File.Exists(resumed + ".progress"), "o sidecar deveria ter sobrevivido a falha");

        var partial = new FileInfo(resumed + ".part").Length;
        Assert.True(partial > 0, "a retomada nao economiza nada se nada foi preservado");

        // 3. Servidor volta ao normal; retoma. A partir daqui, so' o que faltava pode ser pedido.
        _failFrom = int.MaxValue;
        _requested.Clear();

        using (var http = new MediaHttpClient())
            await new SegmentDownloader(http) { Parallelism = 4 }
                .DownloadAsync(parts, resumed, RequestContext.Default, null, CancellationToken.None);

        Assert.Equal(reference, await File.ReadAllBytesAsync(resumed));

        // A prova de que a retomada serve para alguma coisa: os segmentos que ja estavam no
        // disco nao voltaram para a rede. Sem isto o teste passaria mesmo com o resume
        // desligado, porque rebaixar tudo tambem produz os bytes certos.
        var pedidosNaRetomada = _requested.ToList();
        Assert.NotEmpty(pedidosNaRetomada);
        Assert.True(
            pedidosNaRetomada.Count < SegmentCount,
            $"a retomada pediu {pedidosNaRetomada.Count} de {SegmentCount} segmentos - nao pulou nada");

        var jaGravados = (int)(partial / SegmentSize);
        Assert.All(pedidosNaRetomada, i => Assert.True(
            i >= jaGravados,
            $"o segmento {i} ja estava no disco e foi pedido de novo"));

        // Terminou: nao pode sobrar rastro de trabalho pela metade.
        Assert.False(File.Exists(resumed + ".part"));
        Assert.False(File.Exists(resumed + ".progress"));
    }

    [Fact]
    public async Task DownloadCompleto_NaoDeixaParcialNemSidecar()
    {
        var output = Path.Combine(_root, "limpo.ts");

        using var http = new MediaHttpClient();
        await new SegmentDownloader(http)
            .DownloadAsync(Parts(), output, RequestContext.Default, null, CancellationToken.None);

        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".part"));
        Assert.False(File.Exists(output + ".progress"));
    }

    // ----- apoio -----

    private IReadOnlyList<MediaPart> Parts() =>
        Enumerable.Range(0, SegmentCount)
            .Select(i => new MediaPart { Uri = new Uri($"{_prefix}seg/{i}"), DurationSeconds = 4 })
            .ToList();

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();

        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();

        return port;
    }

    private async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                var index = int.Parse(context.Request.Url!.Segments[^1]);

                if (index >= _failFrom)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                    continue;
                }

                _requested.Add(index);

                var body = new byte[SegmentSize];
                Array.Fill(body, (byte)(index & 0xFF));

                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, cancellationToken);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or FormatException or ObjectDisposedException)
            {
                // Cliente desistiu no meio, que e' exatamente o que o teste provoca.
            }
        }
    }
}
