using System.IO;
using BlobTrap.Core.Download;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Retomar um download segmentado é a lógica com o pior modo de falha do projeto: emendar
/// bytes no lugar errado não estoura, não avisa e não aparece — entrega um vídeo corrompido
/// que o usuário só descobre ao assistir, horas depois.
///
/// Por isso quase todo teste aqui verifica que o BlobTrap <em>recomeça do zero</em>. Perder
/// o download é o resultado seguro; retomar é a otimização que só pode acontecer quando tudo
/// bate.
/// </summary>
public class SegmentResumeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "blobtrap-resume-" + Guid.NewGuid().ToString("N")[..8]);

    private string PartPath => Path.Combine(_root, "video.ts.part");
    private string StatePath => Path.Combine(_root, "video.ts.progress");

    public SegmentResumeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ----- o caminho feliz -----

    [Fact]
    public void EstadoIntacto_RetomaDeOndeParou()
    {
        var parts = Parts(100);
        WritePart(4096);
        WriteState(parts, written: 40, bytes: 4096);

        Assert.Equal(40, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));
        Assert.Equal(4096, new FileInfo(PartPath).Length);
    }

    /// <summary>
    /// O caso comum de queda: o checkpoint é a cada 10 partes, então o processo morre quase
    /// sempre com bytes a mais no disco do que o sidecar promete. Esse excedente é uma parte
    /// pela metade — cortá-lo salva o resto do trabalho em vez de descartar tudo.
    /// </summary>
    [Fact]
    public void ArquivoMaiorQueOPrometido_CortaOExcedenteERetoma()
    {
        var parts = Parts(100);
        WritePart(5000);
        WriteState(parts, written: 40, bytes: 4096);

        Assert.Equal(40, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));

        // O pedaco de segmento escrito pela metade tem que sumir, ou a emenda sai torta.
        Assert.Equal(4096, new FileInfo(PartPath).Length);
    }

    // ----- tudo que tem que recomecar do zero -----

    [Fact]
    public void ArquivoMenorQueOPrometido_RecomecaDoZero()
    {
        // Truncado por fora. Nao da' para saber onde ele parou de verdade.
        var parts = Parts(100);
        WritePart(2000);
        WriteState(parts, written: 40, bytes: 4096);

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void SemSidecar_RecomecaDoZero()
    {
        WritePart(4096);

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, Parts(100)));
    }

    [Fact]
    public void SemArquivoParcial_RecomecaDoZeroEDescartaOSidecar()
    {
        var parts = Parts(100);
        WriteState(parts, written: 40, bytes: 4096);

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));
        Assert.False(File.Exists(StatePath));
    }

    /// <summary>
    /// A proteção que mais importa. Um manifesto re-resolvido pode devolver outras URLs para
    /// os mesmos índices — num stream ao vivo isso é a regra, não a exceção. Retomar por cima
    /// disso emendaria dois vídeos diferentes no mesmo arquivo.
    /// </summary>
    [Fact]
    public void StreamDiferente_RecomecaDoZero()
    {
        var original = Parts(100);
        WritePart(4096);
        WriteState(original, written: 40, bytes: 4096);

        var outro = Parts(100, host: "outra-cdn.exemplo.com");

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, outro));
    }

    [Fact]
    public void ContagemDePartesMudou_RecomecaDoZero()
    {
        var original = Parts(100);
        WritePart(4096);
        WriteState(original, written: 40, bytes: 4096);

        // Uma playlist ao vivo cresce entre uma tentativa e outra.
        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, Parts(140)));
    }

    [Fact]
    public void SidecarCorrompido_RecomecaDoZero()
    {
        WritePart(4096);
        File.WriteAllText(StatePath, "{ isto nao e json");

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, Parts(100)));
    }

    [Fact]
    public void SidecarDeVersaoAntiga_RecomecaDoZero()
    {
        var parts = Parts(100);
        WritePart(4096);

        var state = new SegmentResumeState
        {
            Schema = SegmentResumeState.CurrentSchema + 1,
            WrittenParts = 40,
            Bytes = 4096,
            TotalParts = parts.Count,
            Identity = SegmentResumeState.BuildIdentity(parts),
        };
        state.TryWrite(StatePath);

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));
    }

    [Fact]
    public void SidecarApontandoAlemDoFim_RecomecaDoZero()
    {
        var parts = Parts(100);
        WritePart(4096);
        WriteState(parts, written: 250, bytes: 4096);

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));
    }

    [Fact]
    public void SidecarComZeroPartes_RecomecaDoZero()
    {
        var parts = Parts(100);
        WritePart(0);
        WriteState(parts, written: 0, bytes: 0);

        Assert.Equal(0, SegmentDownloader.PrepareResume(PartPath, StatePath, parts));
    }

    // ----- o estado em si -----

    [Fact]
    public void Identidade_DistingueStreamsDiferentes()
    {
        Assert.NotEqual(
            SegmentResumeState.BuildIdentity(Parts(100)),
            SegmentResumeState.BuildIdentity(Parts(100, host: "outra.exemplo.com")));
    }

    [Fact]
    public void Identidade_EhEstavelParaOMesmoStream()
    {
        Assert.Equal(
            SegmentResumeState.BuildIdentity(Parts(100)),
            SegmentResumeState.BuildIdentity(Parts(100)));
    }

    [Fact]
    public void Identidade_ConsideraOByteRange()
    {
        // Duas partes podem ter a mesma URL e diferir so' no Range - e' assim que HLS com
        // EXT-X-BYTERANGE fatia um arquivo unico.
        var semRange = Parts(10);
        var comRange = Parts(10, range: "bytes=0-999");

        Assert.NotEqual(
            SegmentResumeState.BuildIdentity(semRange),
            SegmentResumeState.BuildIdentity(comRange));
    }

    [Fact]
    public void EstadoSobreviveAoDiscoIntacto()
    {
        var parts = Parts(100);
        var original = new SegmentResumeState
        {
            WrittenParts = 42,
            Bytes = 123456,
            TotalParts = parts.Count,
            Identity = SegmentResumeState.BuildIdentity(parts),
        };

        Assert.True(original.TryWrite(StatePath));

        var lido = SegmentResumeState.TryRead(StatePath);

        Assert.NotNull(lido);
        Assert.Equal(original, lido);
        Assert.True(lido!.Matches(parts));
    }

    [Fact]
    public void PrepareResume_EhIdempotente()
    {
        // Regra 5. Importa aqui porque uma nova tentativa pode ser cancelada antes de escrever
        // qualquer coisa, e a seguinte tem que encontrar exatamente o mesmo estado.
        var parts = Parts(100);
        WritePart(5000);
        WriteState(parts, written: 40, bytes: 4096);

        var primeira = SegmentDownloader.PrepareResume(PartPath, StatePath, parts);
        var tamanhoApos = new FileInfo(PartPath).Length;
        var segunda = SegmentDownloader.PrepareResume(PartPath, StatePath, parts);

        Assert.Equal(primeira, segunda);
        Assert.Equal(tamanhoApos, new FileInfo(PartPath).Length);
    }

    // ----- apoio -----

    private static IReadOnlyList<MediaPart> Parts(int count, string host = "cdn.exemplo.com", string? range = null) =>
        Enumerable.Range(0, count)
            .Select(i => new MediaPart
            {
                Uri = new Uri($"https://{host}/hls/seg-{i:D5}.ts"),
                Range = range,
                DurationSeconds = 6,
            })
            .ToList();

    private void WritePart(int bytes) => File.WriteAllBytes(PartPath, new byte[bytes]);

    private void WriteState(IReadOnlyList<MediaPart> parts, int written, long bytes) =>
        new SegmentResumeState
        {
            WrittenParts = written,
            Bytes = bytes,
            TotalParts = parts.Count,
            Identity = SegmentResumeState.BuildIdentity(parts),
        }.TryWrite(StatePath);
}
