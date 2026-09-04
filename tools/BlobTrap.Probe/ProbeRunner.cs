using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using BlobTrap.App.Browser;
using BlobTrap.App.Settings;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Resolving;
using BlobTrap.Core.Sniffing;
using BlobTrap.Core.Tools;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BlobTrap.Probe;

public sealed class ProbeOptions
{
    /// <summary>Teto por alvo. Estourou, o veredito é Erro, não "não detectou".</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Silêncio do sniffer que conta como "a página já mostrou o que tinha".</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Espera máxima pelo fim da navegação.
    ///
    /// Separado do orçamento total porque há página que nunca termina de carregar: métrica em
    /// long-polling, WebSocket aberto, anúncio que nunca resolve. Esperar o fim dessas é
    /// esperar para sempre, e o sniffer já está capturando desde o primeiro byte — o carregar
    /// completo nunca foi condição para medir.
    /// </summary>
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Quantos candidatos resolver por alvo. Resolver custa rede; os primeiros bastam.</summary>
    public int MaxResolve { get; init; } = 3;

    /// <summary>Mostra a janela do navegador. Ligado ajuda a ver por que um alvo falhou.</summary>
    public bool ShowWindow { get; init; } = true;

    /// <summary>
    /// Quando definido, imprime toda resposta cuja URL contenha este texto, com o que o CDP
    /// entregou e o que o classificador concluiu.
    ///
    /// É o que separa as três causas possíveis de um "não detectou", que de fora são
    /// idênticas: o request não chegou ao sniffer, chegou e o classificador devolveu
    /// Unknown, ou foi classificado e o registro descartou por filtro.
    /// </summary>
    public string? Trace { get; init; }
}

/// <summary>
/// Dirige o BlobTrap de verdade contra páginas de verdade.
///
/// A ferramenta existe porque a suíte não alcança este caminho. <c>DeteccaoRealTests</c> mede
/// o classificador contra tráfego real capturado à mão, o que é reprodutível mas congela o
/// dia da captura; o que ela não cobre é a costura viva — CDP anexado, player tocando,
/// manifesto resolvido contra a CDN de hoje. Foi exatamente aí que o app já mostrou
/// "Nenhuma mídia detectada" com o vídeo rodando na frente do usuário.
///
/// Por isso o runner usa o <see cref="CdpSniffer"/> e o <see cref="MediaResolver"/> do
/// produto, sem cópia paralela: uma reimplementação aqui testaria a reimplementação.
/// </summary>
public sealed class ProbeRunner : IDisposable
{
    private readonly ProbeOptions _options;
    private readonly MediaHttpClient _http = new();
    private readonly MediaResolver _resolver;

    private Window? _window;
    private WebView2? _browser;
    private CdpSniffer? _sniffer;
    private readonly MediaRegistry _registry = new();

    public ProbeRunner(ProbeOptions options)
    {
        _options = options;
        // Registry ligado de propósito: é assim que o app roda, e é o que faz um manifesto
        // protegido contaminar os arquivos cifrados do mesmo pacote. Sem esta linha a sonda
        // mede um BlobTrap que não existe — e o defeito nº 3 continuaria invisível para ela.
        _resolver = new MediaResolver(_http)
        {
            YtDlp = YtDlpRunner.TryCreate(),
            Registry = _registry,
        };
    }

    /// <summary>Escrita de progresso; o Program manda para o console.</summary>
    public Action<string> Report { get; init; } = _ => { };

    public async Task StartAsync()
    {
        // Perfil próprio, longe do `webview` que o app usa.
        //
        // Duas razões, e as duas importam: a ferramenta não pode herdar sessão nem cookie de
        // quem a roda — isso mediria a conta da pessoa, não o BlobTrap — e a execução tem que
        // valer o mesmo na segunda vez que na primeira (regra 5), o que um cache acumulado de
        // outra sessão não garante.
        var profile = Path.Combine(ToolLocator.AppDataDirectory, "probe", "webview");
        Directory.CreateDirectory(profile);

        _browser = new WebView2();
        _window = new Window
        {
            Title = "BlobTrap Probe",
            Width = 1280,
            Height = 800,
            Content = _browser,
            // Uma janela escondida faz o Chromium tratar a página como ocluída e suspender o
            // pipeline de mídia - que é justamente o que precisa rodar para haver o que ver.
            ShowInTaskbar = _options.ShowWindow,
            WindowStyle = _options.ShowWindow ? WindowStyle.SingleBorderWindow : WindowStyle.ToolWindow,
        };

        _window.Show();
        if (!_options.ShowWindow) _window.Left = -10000;

        var environment = await CoreWebView2Environment.CreateAsync(null, profile);
        await _browser.EnsureCoreWebView2Async(environment);

        var core = _browser.CoreWebView2;

        _sniffer = new CdpSniffer(_registry);
        _sniffer.Warning += (_, message) => Report($"  aviso: {message}");
        await _sniffer.AttachAsync(core);

        var probe = new PageMediaProbe(_registry);
        await probe.AttachAsync(core);

        if (_options.Trace is { } filtro) AttachTrace(core, filtro);

        core.SourceChanged += (_, _) => _sniffer.UpdatePage(SafeUri(core.Source), core.DocumentTitle);
        core.DocumentTitleChanged += (_, _) => _sniffer.UpdatePage(SafeUri(core.Source), core.DocumentTitle);
    }

    /// <summary>
    /// Escuta as mesmas respostas que o sniffer escuta e imprime o veredito do classificador
    /// para cada uma. Segundo ouvinte de propósito: instrumentar o <see cref="CdpSniffer"/>
    /// mudaria código de produto para enxergar código de produto.
    /// </summary>
    private void AttachTrace(CoreWebView2 core, string filtro)
    {
        var receiver = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");

        receiver.DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("response", out var response)) return;
                if (response.GetProperty("url").GetString() is not { } url) return;
                if (!url.Contains(filtro, StringComparison.OrdinalIgnoreCase)) return;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

                var mime = response.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
                var tipo = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                var kind = MediaClassifier.Classify(uri, mime);
                if (kind == MediaKind.Unknown) kind = MediaClassifier.FromResourceType(tipo, mime);

                Report($"    trace {kind,-18} ruido={MediaClassifier.IsNoise(uri),-5} type={tipo,-10} "
                     + $"mime={mime} {Redaction.ScrubUrl(uri)}");
            }
            catch (JsonException)
            {
                // Payload malformado do CDP nao vale interromper a medicao em curso.
            }
        };
    }

    public async Task<DetectionResult> RunAsync(DetectionTarget target)
    {
        if (_browser?.CoreWebView2 is not { } core) throw new InvalidOperationException("Chame StartAsync antes.");

        Report($"[{target.Name}] {target.Url}");

        var stopwatch = Stopwatch.StartNew();

        // Lista zerada por alvo: um candidato vazado da página anterior faria um alvo passar
        // pelo trabalho do outro, e um falso "passou" é pior do que não ter a ferramenta.
        // É a mesma limpeza que o app faz a cada navegação de topo.
        ApplyShippedDefaults(_registry.Options);
        _registry.Clear();
        _sniffer!.ResetPageState();

        try
        {
            using var budget = new CancellationTokenSource(_options.Budget);

            // Navegação problemática não interrompe a medição, porque ela não é o que se
            // está medindo: o sniffer captura desde o primeiro byte, e o veredito sai do que
            // ele viu. Dois casos reais aparecem aqui, e nenhum é defeito do BlobTrap:
            //
            //  - a página nunca termina de carregar (métrica em long-polling, socket aberto);
            //  - a URL é o próprio manifesto, e o navegador a trata como download em vez de
            //    navegação, o que chega como ConnectionAborted depois de a resposta ter
            //    passado inteira pelo CDP.
            //
            // O motivo fica guardado: se nada for detectado, aí sim ele vira o veredito, e a
            // pessoa lê "não carregou" em vez de "não detectou".
            string? falhaDeNavegacao = null;

            using (var navegacao = CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            {
                navegacao.CancelAfter(_options.NavigationTimeout);

                try
                {
                    await NavigateAsync(core, target.Url, navegacao.Token);
                }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested)
                {
                    falhaDeNavegacao = $"a página não terminou de carregar em {_options.NavigationTimeout.TotalSeconds:0}s";
                    Report($"  aviso: {falhaDeNavegacao}; medindo assim mesmo");
                }
                catch (InvalidOperationException ex)
                {
                    falhaDeNavegacao = ex.Message;
                    Report($"  aviso: {falhaDeNavegacao}; medindo assim mesmo");
                }
            }

            await TryStartPlaybackAsync(core);
            await WaitForQuietAsync(budget.Token);

            var playback = await ReadPlaybackAsync(core);
            Report($"  player: {playback}");

            var observations = await ObserveAsync(budget.Token);

            var result = observations.Count == 0 && falhaDeNavegacao is not null
                ? DetectionCheck.Judge(target, observations, stopwatch.Elapsed, falhaDeNavegacao)
                : DetectionCheck.Judge(target, observations, stopwatch.Elapsed);

            // Sem play não há tráfego de mídia, e aí um veredito negativo não fala do
            // BlobTrap: fala do autoplay que o Chromium bloqueou. Só rebaixa o que deu
            // negativo — evidência positiva continua valendo, porque um manifesto detectado
            // foi detectado, e há página que entrega a mídia sem elemento <video> nenhum.
            if (result.Outcome == DetectionOutcome.Falhou && !playback.Started && falhaDeNavegacao is null)
                result = DetectionCheck.Judge(target, observations, stopwatch.Elapsed,
                    $"o player não chegou a tocar ({playback}) - nada a concluir sobre a detecção");
            Report($"  {result.Outcome}: {result.Summary}");
            return result;
        }
        catch (OperationCanceledException)
        {
            var expired = $"tempo esgotado ({_options.Budget.TotalSeconds:0}s)";
            Report($"  Erro: {expired}");
            return DetectionCheck.Judge(target, Array.Empty<DetectionObservation>(), stopwatch.Elapsed, expired);
        }
        catch (Exception ex)
        {
            // Um alvo que explode não pode levar os outros junto: o valor da ferramenta está
            // em rodar a lista inteira e comparar. A falha vira o veredito desse alvo.
            var message = $"{ex.GetType().Name}: {ex.Message}";
            Report($"  Erro: {message}");
            return DetectionCheck.Judge(target, Array.Empty<DetectionObservation>(), stopwatch.Elapsed, message);
        }
    }

    /// <summary>
    /// Os filtros do app recém-instalado, não os do usuário.
    ///
    /// <see cref="AppSettings.Load"/> leria o settings.json de quem rodou, e aí "não detectou"
    /// poderia ser só uma preferência ligada — a ferramenta mede o padrão que sai de fábrica.
    /// </summary>
    private static void ApplyShippedDefaults(SnifferOptions options)
    {
        var defaults = new AppSettings();

        options.MinProgressiveBytes = defaults.HideSmallFiles ? defaults.SmallFileThresholdBytes : 0;
        options.IncludeAudio = defaults.IncludeAudioOnly;
        options.IncludeSubtitles = defaults.IncludeSubtitles;
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url, CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnCompleted;

            if (e.IsSuccess) arrived.TrySetResult();
            else arrived.TrySetException(new InvalidOperationException($"navegação falhou ({e.WebErrorStatus})"));
        }

        core.NavigationCompleted += OnCompleted;
        core.Navigate(url);

        using var registration = cancellationToken.Register(() =>
        {
            core.NavigationCompleted -= OnCompleted;
            arrived.TrySetCanceled(cancellationToken);
        });

        await arrived.Task;
    }

    /// <summary>
    /// Sem play não há manifesto: o player só busca a mídia quando alguém manda tocar, e é
    /// disso que o app depende. Muta antes de tocar porque o Chromium bloqueia autoplay com
    /// som, e um bloqueio silencioso apareceria como "o BlobTrap não detectou nada".
    /// </summary>
    private static async Task TryStartPlaybackAsync(CoreWebView2 core)
    {
        const string Script = """
            (async function () {
              const seletor = [
                'button[aria-label*="lay" i]', 'button[title*="lay" i]',
                '[class*="play" i][role="button"]', '.ytp-large-play-button', '.vjs-big-play-button',
              ].join(',');

              for (const botao of document.querySelectorAll(seletor)) {
                try { botao.click(); } catch (e) { /* controle que sumiu entre achar e clicar */ }
              }

              for (const midia of document.querySelectorAll('video, audio')) {
                try { midia.muted = true; await midia.play(); }
                catch (e) { /* autoplay recusado; o clique acima ainda pode ter pegado */ }
              }

            })();
            """;

        await core.ExecuteScriptAsync(Script);
    }

    /// <summary>
    /// Pergunta à página se algum player de fato andou.
    ///
    /// <c>currentTime &gt; 0</c> é a prova: um elemento que existe mas está parado em zero não
    /// pediu byte nenhum à rede, e nesse caso "não detectou" fala do autoplay bloqueado, não
    /// do sniffer.
    /// </summary>
    private static async Task<PlaybackState> ReadPlaybackAsync(CoreWebView2 core)
    {
        const string Script = """
            (function () {
              const midias = Array.from(document.querySelectorAll('video, audio'));
              const avanco = midias.reduce((maior, m) => Math.max(maior, m.currentTime || 0), 0);
              const blob = midias.some(m => (m.currentSrc || m.src || '').startsWith('blob:'));

              return JSON.stringify({ elementos: midias.length, avanco: avanco, blob: blob });
            })();
            """;

        var json = await core.ExecuteScriptAsync(Script);

        // ExecuteScriptAsync devolve JSON: a string do script vem como string JSON escapada.
        var inner = JsonSerializer.Deserialize<string>(json);
        if (inner is null) return new PlaybackState(0, 0, false);

        using var document = JsonDocument.Parse(inner);
        var root = document.RootElement;

        return new PlaybackState(
            root.GetProperty("elementos").GetInt32(),
            root.GetProperty("avanco").GetDouble(),
            root.GetProperty("blob").GetBoolean());
    }

    /// <summary>Espera o sniffer sossegar: nada novo por <see cref="ProbeOptions.Quiet"/>.</summary>
    private async Task WaitForQuietAsync(CancellationToken cancellationToken)
    {
        var last = -1;
        var stable = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(500);

        while (stable < _options.Quiet)
        {
            await Task.Delay(step, cancellationToken);

            var count = _registry.Snapshot().Count;

            // Página que descobre mídia sem parar nunca sossega, e a espera consumiria o
            // orçamento inteiro para terminar em "tempo esgotado" - um erro que fala da
            // paciência da ferramenta, não da detecção. Cada achado novo é relatado, então
            // quem lê o console vê a diferença entre "travou" e "ainda está achando coisa".
            if (count != last) Report($"  ...{count} candidato(s)");

            stable = count == last ? stable + step : TimeSpan.Zero;
            last = count;
        }
    }

    /// <summary>
    /// Resolve os primeiros candidatos. Detectar é metade do trabalho: quantas qualidades
    /// existem, e se há DRM, só aparece depois de buscar e ler o manifesto na CDN.
    /// </summary>
    private async Task<IReadOnlyList<DetectionObservation>> ObserveAsync(CancellationToken cancellationToken)
    {
        var observations = new List<DetectionObservation>();

        var candidates = _registry.Snapshot()
            .Where(c => c.Kind.IsDownloadable())
            .Take(_options.MaxResolve)
            .ToList();

        foreach (var candidate in candidates)
            observations.Add(await ObserveOneAsync(candidate, cancellationToken));

        return observations;
    }

    private async Task<DetectionObservation> ObserveOneAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var source = await _resolver.ResolveAsync(candidate, cancellationToken);

            return new DetectionObservation(
                candidate.Kind, candidate.Url, candidate.ContentLength,
                IsProtected: source.IsProtected,
                ProtectionSystem: source.ProtectionSystem,
                VariantCount: source.Variants.Count);
        }
        catch (DrmProtectedException ex)
        {
            // Recusar é o comportamento correto, então isto é resultado, não erro.
            return new DetectionObservation(
                candidate.Kind, candidate.Url, candidate.ContentLength,
                IsProtected: true, ProtectionSystem: ex.System);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Resolver falhou, mas detectar funcionou - e a distinção é o que interessa medir.
            return new DetectionObservation(
                candidate.Kind, candidate.Url, candidate.ContentLength,
                ResolveError: $"não resolveu: {ex.Message}");
        }
    }

    /// <summary>O que a página relata sobre os próprios players, depois da tentativa de play.</summary>
    private sealed record PlaybackState(int Elements, double Position, bool UsesBlob)
    {
        /// <summary>Tocou de verdade: o relógio do player saiu do zero.</summary>
        public bool Started => Position > 0;

        public override string ToString() =>
            Elements == 0
                ? "nenhum elemento de mídia na página"
                : $"{Elements} elemento(s), {Position.ToString("0.0", CultureInfo.InvariantCulture)}s tocados"
                  + (UsesBlob ? ", via blob:" : string.Empty);
    }

    private static Uri? SafeUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    public void Dispose()
    {
        _sniffer?.Dispose();
        _http.Dispose();
        _window?.Close();
    }
}
