using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlobTrap.Core.Download;

/// <summary>
/// O que um download segmentado interrompido deixa gravado ao lado do <c>.part</c> para poder
/// continuar de onde parou.
///
/// Existe porque cancelar um HLS de 4 GB em 95% jogava tudo fora: <see cref="SegmentDownloader"/>
/// abria o arquivo em <c>FileMode.Create</c> e concatenava do zero, sem nenhum registro de
/// quantas partes já tinham entrado.
///
/// O risco desta abordagem é preciso e vale nomear: se o <c>.part</c> e este arquivo
/// divergirem, retomar produziria um vídeo corrompido <em>em silêncio</em> — que é pior do que
/// perder o download, porque o usuário só descobre ao assistir. Por isso
/// <see cref="Matches"/> confere tudo que pode conferir, e qualquer divergência que não seja
/// recuperável faz o download recomeçar em vez de arriscar.
/// </summary>
public sealed record SegmentResumeState
{
    /// <summary>Muda quando o formato do arquivo muda, para não ler um sidecar antigo errado.</summary>
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>Quantas partes da lista já foram escritas, em ordem, no <c>.part</c>.</summary>
    [JsonPropertyName("partesEscritas")]
    public int WrittenParts { get; init; }

    /// <summary>
    /// Tamanho que o <c>.part</c> tinha quando estas partes foram contadas.
    ///
    /// É a única defesa contra a divergência: se o arquivo cresceu além disto, o processo
    /// morreu no meio de uma parte e o excedente é lixo.
    /// </summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>Total de partes que o manifesto tinha. Se mudou, o stream não é mais o mesmo.</summary>
    [JsonPropertyName("totalDePartes")]
    public int TotalParts { get; init; }

    /// <summary>
    /// Identifica o stream. Um manifesto ao vivo re-resolvido pode devolver outras URLs para o
    /// mesmo índice; retomar por cima disso emendaria dois vídeos diferentes.
    /// </summary>
    [JsonPropertyName("identidade")]
    public string Identity { get; init; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>
    /// Constrói a identidade do stream a partir do que não pode mudar entre duas tentativas
    /// da mesma mídia: quantas partes são, e qual é a primeira e a última.
    /// </summary>
    public static string BuildIdentity(IReadOnlyList<MediaPart> parts)
    {
        if (parts.Count == 0) return string.Empty;

        return $"{parts.Count}|{parts[0].Uri.AbsoluteUri}|{parts[^1].Uri.AbsoluteUri}"
             + $"|{parts[0].Range}|{parts[^1].Range}";
    }

    /// <summary>
    /// Diz se este estado descreve o mesmo download que está sendo pedido agora. Não olha o
    /// arquivo — isso é <see cref="SegmentDownloader"/> quem faz, porque só ele sabe o tamanho.
    /// </summary>
    public bool Matches(IReadOnlyList<MediaPart> parts) =>
        Schema == CurrentSchema
        && WrittenParts > 0
        && WrittenParts <= parts.Count
        && TotalParts == parts.Count
        && Identity == BuildIdentity(parts);

    /// <summary>Lê o sidecar. Qualquer problema devolve null, que significa "comece do zero".</summary>
    public static SegmentResumeState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<SegmentResumeState>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Sidecar ilegivel nao e' motivo para falhar: e' motivo para recomecar.
            return null;
        }
    }

    /// <summary>
    /// Grava o sidecar. Falhar aqui custa o resume, não o download — o <c>.part</c> continua
    /// crescendo do mesmo jeito, e a próxima tentativa apenas começa do zero.
    /// </summary>
    public bool TryWrite(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sidecar orfao nao atrapalha: sem o .part ao lado, Matches nunca aprova.
        }
    }
}
