using System.Collections.Generic;
using BlobTrap.App.Browser;
using BlobTrap.App.Settings;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Detecção medida contra tráfego REAL, não inventado.
///
/// Cada caso aqui foi capturado de verdade em 04/09/2026: a URL saiu do
/// <c>yt-dlp --dump-json</c> da página, e os cabeçalhos vieram de uma requisição
/// <c>Range: bytes=0-1023</c> feita à mesma URL. É exatamente o par (URL, resposta) que o
/// sniffer CDP vê quando o player toca.
///
/// Motivo de existir: o app mostrou "Nenhuma mídia detectada" numa página do YouTube com o
/// vídeo tocando. Nenhum teste pegava isso porque todos os anteriores testavam
/// <see cref="MediaClassifier"/> isolado — e o classificador estava certo. O que falhava era
/// a costura entre ler os cabeçalhos e decidir guardar o candidato.
/// </summary>
public class DeteccaoRealTests
{
    /// <summary>Um caso real: o que veio do fio, e o que o app tem que concluir.</summary>
    public sealed record Caso(
        string Origem,
        string Url,
        string ContentType,
        /// <summary>`Content-Length` da resposta — numa 206 é o pedaço, não o arquivo.</summary>
        string? ContentLength,
        /// <summary>`Content-Range`, quando a resposta é 206. Traz o tamanho real do ativo.</summary>
        string? ContentRange,
        MediaKind Esperado)
    {
        public override string ToString() => Origem;
    }

    /// <summary>
    /// O corpus. URLs do YouTube estão com os parâmetros de assinatura redigidos — o formato
    /// é o que importa, e um token de CDN não entra em arquivo versionado.
    /// </summary>
    public static IEnumerable<object[]> Corpus() => new[]
    {
        // --- YouTube: o caso que motivou tudo ---
        new object[] { new Caso(
            "youtube/videoplayback video",
            "https://rr7---sn-jucj-0qpz.googlevideo.com/videoplayback?expire=1788552631&ei=REDIGIDO"
                + "&id=o-AKssCpCMNysxmyzptA8jWggfhEWqT16&itag=160&source=youtube&requiressl=yes"
                + "&mime=video%2Fmp4&gir=yes&clen=14282860&sig=REDIGIDO",
            "video/mp4", "65536", "bytes 0-65535/14282860", MediaKind.ProgressiveVideo) },

        new object[] { new Caso(
            "youtube/videoplayback audio m4a",
            "https://rr7---sn-jucj-0qpz.googlevideo.com/videoplayback?expire=1788552631&ei=REDIGIDO"
                + "&itag=140&source=youtube&mime=audio%2Fmp4&gir=yes&clen=25196483&sig=REDIGIDO",
            "audio/mp4", "65536", "bytes 0-65535/25196483", MediaKind.ProgressiveAudio) },

        new object[] { new Caso(
            "youtube/videoplayback audio webm",
            "https://rr7---sn-jucj-0qpz.googlevideo.com/videoplayback?expire=1788552631&ei=REDIGIDO"
                + "&itag=251&source=youtube&mime=audio%2Fwebm&gir=yes&clen=24118745&sig=REDIGIDO",
            "audio/webm", "65536", "bytes 0-65535/24118745", MediaKind.ProgressiveAudio) },

        // O manifesto HLS do YouTube nao tem .m3u8 em lugar nenhum: os parametros vao no
        // proprio caminho, separados por barra. So o Content-Type denuncia o que e'.
        new object[] { new Caso(
            "youtube/manifesto HLS sem extensao",
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1788552631/ei/REDIGIDO"
                + "/id/740450c44c8f91db/itag/233/source/youtube/requiressl/yes/ratebypass/yes",
            "application/vnd.apple.mpegurl", "1024", "bytes 0-1023/8421", MediaKind.HlsPlaylist) },

        // --- HLS de outras origens ---
        new object[] { new Caso(
            "apple/master.m3u8",
            "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8",
            "application/x-mpegURL", "1024", "bytes 0-1023/6291", MediaKind.HlsPlaylist) },

        // Unified Streaming publica como ".ism/.m3u8" - a extensao existe mas vem depois de
        // um segmento que ja tem ponto, que e' onde Path.GetExtension costuma tropecar.
        new object[] { new Caso(
            "unified/tears-of-steel.ism/.m3u8",
            "https://demo.unified-streaming.com/k8s/features/stable/video/tears-of-steel/tears-of-steel.ism/.m3u8",
            "application/vnd.apple.mpegurl", "1024", "bytes 0-1023/1097", MediaKind.HlsPlaylist) },

        // --- DASH ---
        new object[] { new Caso(
            "unified/tears-of-steel.ism/.mpd",
            "https://demo.unified-streaming.com/k8s/features/stable/video/tears-of-steel/tears-of-steel.ism/.mpd",
            "application/dash+xml", "1024", "bytes 0-1023/8332", MediaKind.DashManifest) },

        new object[] { new Caso(
            "akamai/SNE_DASH_SD_CASE1A_REVISED.mpd",
            "https://dash.akamaized.net/dash264/TestCases/1a/sony/SNE_DASH_SD_CASE1A_REVISED.mpd",
            "application/dash+xml", "2230", null, MediaKind.DashManifest) },

        new object[] { new Caso(
            "akamai/bbb_30fps.mpd",
            "https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd",
            "application/dash+xml", "3927", null, MediaKind.DashManifest) },

        // --- Arquivo direto ---
        new object[] { new Caso(
            "archive.org/big_buck_bunny_720p_surround.mp4",
            "https://archive.org/download/BigBuckBunny_124/Content/big_buck_bunny_720p_surround.mp4",
            "video/mp4", "1024", "bytes 0-1023/355856562", MediaKind.ProgressiveVideo) },

        new object[] { new Caso(
            "w3schools/mov_bbb.webm",
            "https://www.w3schools.com/html/mov_bbb.webm",
            "video/webm", "1024", "bytes 0-1023/482282", MediaKind.ProgressiveVideo) },

        new object[] { new Caso(
            "w3schools/horse.mp3",
            "https://www.w3schools.com/html/horse.mp3",
            "audio/mpeg", "1024", "bytes 0-1023/28915", MediaKind.ProgressiveAudio) },

        new object[] { new Caso(
            "streamable/cdn-cf-west.mp4",
            "https://cdn-cf-west.streamable.com/video/f6441ae0c84311e4af010bc47400a0a4.mp4",
            "video/mp4", "1024", "bytes 0-1023/3044857", MediaKind.ProgressiveVideo) },

        // A CDN do TikTok nao poe extensao no caminho: e' /video/tos/<regiao>/<hash>. Nada na
        // URL denuncia midia, e o arquivo tem 170 KB - conteudo real que o limiar antigo de
        // 512 KB descartaria mesmo se a URL fosse reconhecida.
        new object[] { new Caso(
            "tiktok/audio sem extensao no caminho",
            "https://v58.tiktokcdn.com/video/tos/useast2a/tos-useast2a-v-27dcd7/fdec9588d6684e7d97da29739b9d",
            "audio/mpeg", "1024", "bytes 0-1023/173696", MediaKind.ProgressiveAudio) },
    };

    /// <summary>O classificador sozinho: dado (URL, mime), que tipo de mídia é.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Classificador_ReconheceOTipo(Caso caso)
    {
        var kind = MediaClassifier.Classify(new Uri(caso.Url), caso.ContentType);

        Assert.Equal(caso.Esperado, kind);
    }

    /// <summary>
    /// O caminho completo, com as opções que o app usa por padrão — incluindo
    /// "ocultar arquivos pequenos", que é onde a detecção morria.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Sniffer_GuardaOCandidato(Caso caso)
    {
        var registry = new MediaRegistry();

        // Os defaults reais de AppSettings, lidos dele e nao copiados: o bug so aparece com o
        // filtro de tamanho ligado, que e' como o app sai de fabrica, e um numero repetido
        // aqui deixaria de acompanhar a mudanca do default no dia em que ele mudasse.
        var padrao = new AppSettings();
        registry.Options.MinProgressiveBytes = padrao.HideSmallFiles ? padrao.SmallFileThresholdBytes : 0;
        registry.Options.IncludeAudio = padrao.IncludeAudioOnly;
        registry.Options.IncludeSubtitles = padrao.IncludeSubtitles;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = caso.ContentType,
        };
        if (caso.ContentLength is not null) headers["content-length"] = caso.ContentLength;
        if (caso.ContentRange is not null) headers["content-range"] = caso.ContentRange;

        var tamanho = CdpSniffer.ReadContentLength(headers);

        var candidato = registry.Observe(
            new Uri(caso.Url),
            caso.ContentType,
            RequestContext.Default,
            contentLength: tamanho);

        Assert.True(candidato is not null, $"{caso.Origem}: a mídia não foi detectada (tamanho lido: {tamanho})");
        Assert.Equal(caso.Esperado, candidato!.Kind);
    }

    /// <summary>
    /// Numa resposta 206, `Content-Length` é o tamanho do PEDAÇO e `Content-Range` traz o do
    /// arquivo. Ler o primeiro faz um filme de 14 MB parecer ter 64 KB — e todo player moderno
    /// busca mídia por range, então isso não é um caso de canto: é o caso comum.
    /// </summary>
    [Fact]
    public void ContentRange_TemPrecedenciaSobreContentLength()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = "65536",
            ["content-range"] = "bytes 0-65535/14282860",
        };

        Assert.Equal(14282860, CdpSniffer.ReadContentLength(headers));
    }

    [Fact]
    public void SemContentRange_ContentLengthEhOTamanho()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = "355856562",
        };

        Assert.Equal(355856562, CdpSniffer.ReadContentLength(headers));
    }

    [Theory]
    // Range aberto: o servidor nao sabe o total ainda (live, chunked).
    [InlineData("bytes 0-65535/*", "65536", 65536L)]
    // Content-Range malformado nao pode derrubar a leitura; sobra o content-length.
    [InlineData("bytes lixo", "2048", 2048L)]
    public void ContentRangeSemTotal_CaiParaOContentLength(string range, string length, long esperado)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = length,
            ["content-range"] = range,
        };

        Assert.Equal(esperado, CdpSniffer.ReadContentLength(headers));
    }

    /// <summary>
    /// O último recurso: a CDN não põe extensão no caminho E o servidor não manda um
    /// Content-Type útil. Sobra o ResourceType do CDP — o Chrome dizendo que entregou aquilo
    /// a um elemento de mídia. Sem isso, esta família de casos é invisível.
    /// </summary>
    [Theory]
    [InlineData("application/octet-stream", MediaKind.ProgressiveVideo)]
    [InlineData("binary/octet-stream", MediaKind.ProgressiveVideo)]
    [InlineData("audio/mpeg", MediaKind.ProgressiveAudio)]
    [InlineData(null, MediaKind.ProgressiveVideo)]
    public void SemExtensaoESemMimeUtil_OChromeDecide(string? mime, MediaKind esperado)
    {
        var registry = new MediaRegistry();

        var candidato = registry.Observe(
            new Uri("https://cdn.exemplo.com/stream/a7f3c2e9b1"),
            mime,
            RequestContext.Default,
            contentLength: 8_400_000,
            resourceType: "Media");

        Assert.NotNull(candidato);
        Assert.Equal(esperado, candidato!.Kind);
    }

    /// <summary>
    /// A contrapartida: "Media" so' entra quando o resto falhou, e nenhum outro ResourceType
    /// vira midia. Sem essa trava, todo XHR de uma pagina viraria candidato.
    /// </summary>
    [Theory]
    [InlineData("XHR")]
    [InlineData("Fetch")]
    [InlineData("Document")]
    [InlineData("Script")]
    [InlineData("Image")]
    [InlineData(null)]
    public void OutroResourceType_NaoViraMidia(string? tipo)
    {
        var registry = new MediaRegistry();

        var candidato = registry.Observe(
            new Uri("https://api.exemplo.com/v1/dados"),
            "application/octet-stream",
            RequestContext.Default,
            contentLength: 8_400_000,
            resourceType: tipo);

        Assert.Null(candidato);
    }

    /// <summary>
    /// O ResourceType nao sobrescreve o que o caminho ja disse. Um .m3u8 marcado como "Media"
    /// continua sendo playlist, e nao um arquivo progressivo - trocar isso mandaria o
    /// resolvedor baixar o texto do manifesto como se fosse video.
    /// </summary>
    [Fact]
    public void ResourceType_NaoSobrescreveOQueOCaminhoJaDisse()
    {
        var registry = new MediaRegistry();

        var candidato = registry.Observe(
            new Uri("https://cdn.exemplo.com/hls/master.m3u8"),
            "application/vnd.apple.mpegurl",
            RequestContext.Default,
            contentLength: 4096,
            resourceType: "Media");

        Assert.NotNull(candidato);
        Assert.Equal(MediaKind.HlsPlaylist, candidato!.Kind);
    }

    /// <summary>Audio nao entra no corte por tamanho: e' legitimamente pequeno.</summary>
    [Fact]
    public void AudioPequeno_NaoEhDescartadoPeloLimiarDeVideo()
    {
        var registry = new MediaRegistry();
        registry.Options.MinProgressiveBytes = 64 * 1024;

        var audio = registry.Observe(
            new Uri("https://cdn.exemplo.com/recado.mp3"), "audio/mpeg",
            RequestContext.Default, contentLength: 12_000);

        var video = registry.Observe(
            new Uri("https://cdn.exemplo.com/bumper.mp4"), "video/mp4",
            RequestContext.Default, contentLength: 12_000);

        Assert.NotNull(audio);
        Assert.Null(video);
    }
}
