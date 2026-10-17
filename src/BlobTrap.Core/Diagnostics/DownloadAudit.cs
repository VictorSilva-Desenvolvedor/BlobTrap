using BlobTrap.Core.Models;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Diagnostics;

/// <summary>Uma linha do log com o nível que ela merece.</summary>
public readonly record struct AuditLine(LogLevel Level, string Message);

/// <summary>
/// Monta o que o log precisa dizer para quem quer conferir "baixei na melhor qualidade e com
/// som?": o que a origem oferecia, o que foi escolhido, e onde as duas coisas divergem.
///
/// São funções puras de propósito. Quem escreve no log é o chamador; aqui só se decide o texto
/// e a gravidade, o que permite testar sem disco, sem rede e sem ffmpeg.
/// </summary>
public static class DownloadAudit
{
    /// <summary>Quantas opções de cada tipo entram no resumo da origem. O resto é ruído.</summary>
    private const int TopCount = 3;

    /// <summary>Resumo do que o manifesto ou o extrator ofereceu, melhores opções primeiro.</summary>
    public static string DescribeSource(MediaSource source)
    {
        var videos = source.VideoVariants.ToList();
        var audios = source.AudioVariants.ToList();

        var text = $"origem {source.ResolvedBy}/{source.Kind} em {source.Url.Host}: "
            + $"{videos.Count} video(s), {audios.Count} audio(s), {source.SubtitleVariants.Count()} legenda(s)";

        if (source.DurationSeconds is > 0)
            text += $", duracao {TimeSpan.FromSeconds(source.DurationSeconds.Value):hh\\:mm\\:ss}";

        if (source.IsLive) text += ", AO VIVO";

        if (videos.Count > 0) text += " | melhores videos: " + Join(videos);
        if (audios.Count > 0) text += " | melhores audios: " + Join(audios);

        return text;
    }

    /// <summary>
    /// O plano escolhido, seguido dos avisos que ele merece. A primeira linha é sempre Info;
    /// as seguintes só existem quando algo ameaça o objetivo "melhor qualidade, com som".
    /// </summary>
    /// <param name="ffmpegAvailable">
    /// Parâmetro, e não consulta interna, para o resultado não depender do que a máquina que
    /// roda o teste tem instalado.
    /// </param>
    public static IReadOnlyList<AuditLine> DescribePlan(DownloadPlan plan, bool ffmpegAvailable)
    {
        var lines = new List<AuditLine>();

        var audio = plan.Audio is null ? "nenhum" : Describe(plan.Audio);
        var size = plan.EstimatedBytes is > 0 ? $", ~{Naming.FormatBytes(plan.EstimatedBytes)}" : string.Empty;

        lines.Add(new AuditLine(LogLevel.Info,
            $"plano: video {Describe(plan.Video)} | audio {audio}"
            + (plan.AudioOnly ? " | SOMENTE AUDIO" : string.Empty)
            + (plan.NeedsMerge ? " | juntar faixas" : string.Empty)
            + size));

        // Mudo: a faixa de video nao carrega som e nada foi pareado para ela.
        if (!plan.AudioOnly && plan.Video.Track == TrackKind.VideoOnly && plan.Audio is null)
        {
            lines.Add(new AuditLine(LogLevel.Warn,
                "SEM AUDIO: a faixa de video escolhida nao tem som proprio e nenhuma faixa de audio "
                + "foi pareada; o arquivo vai sair mudo"));
        }

        if (!plan.AudioOnly && plan.Source.BestVideo is { } bestVideo
            && bestVideo.QualityScore > plan.Video.QualityScore)
        {
            lines.Add(new AuditLine(LogLevel.Warn,
                $"qualidade abaixo da melhor oferecida: escolhida {plan.Video.Label}, "
                + $"melhor disponivel {bestVideo.Label}"));
        }

        if (plan.Audio is { } chosenAudio
            && plan.Source.BestAudioFor(plan.Video) is { } bestAudio
            && (bestAudio.Bandwidth ?? 0) > (chosenAudio.Bandwidth ?? 0))
        {
            lines.Add(new AuditLine(LogLevel.Warn,
                $"audio abaixo do melhor oferecido: escolhido {Describe(chosenAudio)}, "
                + $"melhor disponivel {Describe(bestAudio)}"));
        }

        if (!ffmpegAvailable) lines.AddRange(MissingFfmpegWarnings(plan));

        return lines;
    }

    private static IEnumerable<AuditLine> MissingFfmpegWarnings(DownloadPlan plan)
    {
        if (plan.Video.Delivery == DeliveryMode.External && plan.Audio is not null && plan.NeedsMerge)
        {
            yield return new AuditLine(LogLevel.Warn,
                "ffmpeg ausente: o yt-dlp precisa dele para juntar video e audio");
        }
        else if (plan.NeedsMerge)
        {
            yield return new AuditLine(LogLevel.Warn,
                "ffmpeg ausente: video e audio vao ficar em DOIS arquivos separados, sem juntar");
        }
        else if (plan.Video.Delivery is DeliveryMode.HlsSegments or DeliveryMode.DashSegments)
        {
            yield return new AuditLine(LogLevel.Warn,
                "ffmpeg ausente: o arquivo vai sair como stream bruto (.ts), sem remux");
        }
    }

    private static string Join(IReadOnlyList<MediaVariant> variants) =>
        string.Join("; ", variants.Take(TopCount).Select(Describe))
        + (variants.Count > TopCount ? $"; +{variants.Count - TopCount}" : string.Empty);

    private static string Describe(MediaVariant variant)
    {
        var text = $"{variant.Label} [{variant.Delivery}";
        if (variant.Language is not null) text += $", {variant.Language}";
        return text + "]";
    }
}
