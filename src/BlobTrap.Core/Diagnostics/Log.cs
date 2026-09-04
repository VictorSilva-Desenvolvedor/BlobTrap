using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using BlobTrap.Core.Tools;

namespace BlobTrap.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Log em arquivo, um por dia, em <c>%LOCALAPPDATA%\BlobTrap\logs</c>.
///
/// Existe por um motivo concreto: quando um download falha na máquina do usuário, o único
/// artefato era uma linha de texto na interface. O stderr do ffmpeg e do yt-dlp — que diz
/// exatamente o que aconteceu — era descartado fora do caso de exceção, e nada registrava
/// qual variante foi escolhida, quantos segmentos entraram ou qual CDN devolveu 403.
///
/// Tudo passa por <see cref="Redaction"/> antes de tocar o disco. Este arquivo é justamente o
/// que o usuário anexa num relato de bug, e o BlobTrap manipula o Cookie de sessão dele.
/// </summary>
public static class Log
{
    /// <summary>Dias de log mantidos. Depois disso o arquivo é apagado na próxima abertura.</summary>
    public const int RetentionDays = 7;

    private static readonly ConcurrentQueue<string> Buffer = new();
    private static readonly object WriteLock = new();

    /// <summary>Falhas seguidas de escrita antes de desistir da sessão inteira.</summary>
    private const int MaxConsecutiveFailures = 5;

    private static string? _directory;
    private static bool _enabled = true;
    private static int _consecutiveFailures;

    /// <summary>Onde os arquivos ficam. Trocável só para teste.</summary>
    public static string Directory
    {
        get => _directory ??= Path.Combine(ToolLocator.AppDataDirectory, "logs");
        set => _directory = value;
    }

    /// <summary>
    /// Nível mínimo escrito. Debug fica de fora por padrão: ele registra cada segmento, e num
    /// filme são dezenas de milhares de linhas para um proveito quase nulo.
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string CurrentFile =>
        Path.Combine(Directory, $"blobtrap-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message, null);

    public static void Info(string source, string message) => Write(LogLevel.Info, source, message, null);

    public static void Warn(string source, string message, Exception? error = null) =>
        Write(LogLevel.Warn, source, message, error);

    public static void Error(string source, string message, Exception? error = null) =>
        Write(LogLevel.Error, source, message, error);

    /// <summary>
    /// Apaga os logs vencidos. Chamado na inicialização, junto da varredura de temporários.
    /// Nunca lança: um log que não pôde ser apagado não é motivo para o app não abrir.
    /// </summary>
    public static void TrimOldFiles(DateTimeOffset now)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            var cutoff = now.AddDays(-RetentionDays);

            foreach (var file in System.IO.Directory.GetFiles(Directory, "blobtrap-*.log"))
            {
                try
                {
                    if (new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) < cutoff)
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Em uso, ou sem permissao. Fica para a proxima abertura.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Idem - abrir o app importa mais.
        }
    }

    private static void Write(LogLevel level, string source, string message, Exception? error)
    {
        if (!_enabled || level < MinimumLevel) return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("  ")
            .Append(level.ToString().ToUpperInvariant().PadRight(5))
            .Append("  ")
            .Append(source.PadRight(18))
            .Append("  ")
            .Append(Redaction.Scrub(message));

        if (error is not null)
        {
            line.Append(Environment.NewLine)
                .Append("                                        ")
                .Append(Redaction.Scrub(error.GetType().Name + ": " + error.Message));
        }

        Append(line.ToString());
    }

    private static void Append(string line)
    {
        Buffer.Enqueue(line);

        lock (WriteLock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var pending = new StringBuilder();
                while (Buffer.TryDequeue(out var queued)) pending.AppendLine(queued);

                if (pending.Length > 0) File.AppendAllText(CurrentFile, pending.ToString(), Encoding.UTF8);

                _consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Buffer.Clear();

                // Log que nao escreve nao pode derrubar o que estava sendo registrado.
                //
                // Mas desligar na primeira falha era demais: um antivirus varrendo o arquivo,
                // ou a pasta sumindo por um instante, matava o log pelo resto da sessao - e
                // com ele o registro do problema que viesse depois, que e' o que interessava.
                // Um disco cheio de verdade falha sempre e chega ao limite em poucas linhas;
                // uma trava momentanea nao chega.
                if (++_consecutiveFailures >= MaxConsecutiveFailures) _enabled = false;
            }
        }
    }
}
