namespace BlobTrap.Core.Download;

/// <summary>Falta de espaço detectada antes de baixar, e não depois de encher o disco.</summary>
public sealed class InsufficientDiskSpaceException : Exception
{
    public InsufficientDiskSpaceException(string message, long requiredBytes, long availableBytes)
        : base(message)
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public long RequiredBytes { get; }

    public long AvailableBytes { get; }
}

/// <summary>
/// Confere se cabe antes de começar.
///
/// Sem isto, um HLS de 4 GB num disco com 1 GB livre baixa por vinte minutos e morre com uma
/// <c>IOException</c> genérica — que chega ao usuário como "Falhou" e mais nada, sem dizer que
/// o problema é espaço nem quanto falta. Pior: o trabalho parcial fica ocupando o disco que já
/// estava cheio.
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// Folga sobre o tamanho estimado.
    ///
    /// Existem duas cópias no disco ao mesmo tempo: os segmentos concatenados na pasta de
    /// trabalho e o arquivo final que o ffmpeg escreve a partir deles. Some-se a isso a
    /// estimativa do manifesto, que erra para baixo com frequência - <c>BANDWIDTH</c> é média
    /// declarada, não tamanho medido.
    /// </summary>
    public const double SafetyFactor = 2.2;

    /// <summary>Piso absoluto, para o caso do tamanho ser desconhecido ou ridiculamente pequeno.</summary>
    public const long MinimumFreeBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Espaço livre no volume que contém o caminho, ou null quando não dá para saber (rede,
    /// volume removido, permissão negada). Null significa "não sei", nunca "não tem".
    /// </summary>
    public static long? AvailableFor(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Quanto o download precisa ter livre, incluindo a folga.</summary>
    public static long RequiredFor(long? estimatedBytes)
    {
        if (estimatedBytes is not > 0) return MinimumFreeBytes;

        var withHeadroom = (double)estimatedBytes.Value * SafetyFactor;

        // Estimativa absurda nao pode virar um required negativo por estouro de long.
        return withHeadroom >= long.MaxValue
            ? long.MaxValue
            : Math.Max(MinimumFreeBytes, (long)withHeadroom);
    }

    /// <summary>
    /// Estoura quando o volume claramente não comporta o download.
    ///
    /// Quando o espaço livre não pode ser lido, deixa passar. Um palpite errado que bloqueia um
    /// download possível é pior do que a falha que esta checagem evita: o usuário fica sem o
    /// vídeo e sem explicação, e não há nada que ele possa fazer a respeito.
    /// </summary>
    public static void EnsureRoomFor(string outputPath, long? estimatedBytes)
    {
        var available = AvailableFor(outputPath);
        if (available is null) return;

        var required = RequiredFor(estimatedBytes);
        if (available.Value >= required) return;

        throw new InsufficientDiskSpaceException(
            $"Espaço insuficiente em disco: precisa de cerca de {Util.Naming.FormatBytes(required)} " +
            $"e há {Util.Naming.FormatBytes(available.Value)} livres.",
            required,
            available.Value);
    }
}
