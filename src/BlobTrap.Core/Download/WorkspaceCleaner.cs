using BlobTrap.Core.Tools;

namespace BlobTrap.Core.Download;

/// <summary>
/// Varre as pastas de trabalho que downloads anteriores deixaram para trás.
///
/// <see cref="DownloadExecutor"/> apaga a sua no <c>finally</c>, o que cobre o caminho normal
/// e a falha. Não cobre o que não passa por <c>finally</c>: queda de energia, Gerenciador de
/// Tarefas, atualização do Windows no meio do download. Cada um desses deixa
/// <c>%LOCALAPPDATA%\BlobTrap\temp\&lt;id&gt;</c> com os segmentos já baixados dentro - e nada,
/// até agora, olhava para lá de novo. Num app cujo trabalho é justamente escrever gigabytes,
/// isso cresce sem teto e em silêncio.
/// </summary>
public static class WorkspaceCleaner
{
    /// <summary>Raiz das pastas de trabalho, uma subpasta por job.</summary>
    public static string TempRoot { get; } = Path.Combine(ToolLocator.AppDataDirectory, "temp");

    /// <summary>
    /// Idade mínima para uma pasta ser considerada órfã.
    ///
    /// Sem essa folga, uma segunda instância do BlobTrap apagaria a pasta de um download que a
    /// primeira ainda está usando. A janela não precisa ser exata - só maior do que o intervalo
    /// entre criar a pasta e escrever o primeiro segmento nela.
    /// </summary>
    public static TimeSpan MinimumAge { get; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Apaga o que sobrou de execuções anteriores e devolve quantos bytes foram liberados.
    ///
    /// Nunca lança: isto roda na inicialização, e um disco ocupado ou uma permissão negada não
    /// podem impedir o app de abrir. O que não der para apagar agora fica para a próxima.
    /// </summary>
    public static long SweepOrphans(DateTimeOffset now, string? root = null)
    {
        var directory = root ?? TempRoot;
        if (!Directory.Exists(directory)) return 0;

        long freed = 0;

        foreach (var candidate in EnumerateSafely(directory))
        {
            try
            {
                var info = new DirectoryInfo(candidate);

                // LastWriteTime, e nao CreationTime: uma pasta criada ha dias mas escrita ha um
                // minuto pertence a um download que esta vivo agora.
                var idleSince = now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (idleSince < MinimumAge) continue;

                var size = MeasureSafely(info);
                info.Delete(recursive: true);
                freed += size;
            }
            catch (IOException)
            {
                // Provavelmente em uso por outra instancia. Fica para a proxima varredura.
            }
            catch (UnauthorizedAccessException)
            {
                // Idem - e abrir o app importa mais do que recuperar espaco agora.
            }
        }

        return freed;
    }

    private static IReadOnlyList<string> EnumerateSafely(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Tamanho aproximado, só para relatar quanto foi liberado. Um arquivo que sumiu no meio da
    /// contagem não invalida a limpeza, então o erro vira zero em vez de exceção.
    /// </summary>
    private static long MeasureSafely(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
