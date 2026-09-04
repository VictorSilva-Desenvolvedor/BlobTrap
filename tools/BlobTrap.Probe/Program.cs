using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using BlobTrap.Core.Diagnostics;

namespace BlobTrap.Probe;

/// <summary>
/// Roda o autoteste de detecção contra as páginas da lista e escreve o relatório.
///
/// <code>
/// dotnet run --project tools\BlobTrap.Probe -- --out relatorio.md
/// dotnet run --project tools\BlobTrap.Probe -- --targets meus-alvos.json --only youtube
/// </code>
///
/// Código de saída: 0 quando todo alvo passou, 1 quando algum falhou ou deu erro — para
/// poder encadear num script sem ler o texto.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions TargetsJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var exitCode = 0;

        // WPF por causa do WebView2, que precisa de uma janela de verdade para desenhar - e
        // um player que não desenha não busca mídia, que é o que a ferramenta veio medir.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.Startup += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(args);
            }
            catch (Exception ex)
            {
                // Última rede: sem isto uma falha aqui viraria caixa de diálogo de crash do
                // WPF, que numa ferramenta de linha de comando trava esperando um clique.
                Console.Error.WriteLine($"falhou: {ex}");
                exitCode = 2;
            }
            finally
            {
                app.Shutdown();
            }
        };

        app.Run();
        return exitCode;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var outputPath = Value(args, "--out") ?? $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.md";
        var only = Value(args, "--only");

        var options = new ProbeOptions
        {
            Budget = TimeSpan.FromSeconds(Number(args, "--budget", 45)),
            Quiet = TimeSpan.FromSeconds(Number(args, "--quiet", 6)),
            MaxResolve = (int)Number(args, "--max-resolve", 3),
            ShowWindow = !args.Contains("--hidden"),
            Trace = Value(args, "--trace"),
        };

        // O log da ferramenta fica junto do relatório, não no log do usuário: quem roda isto
        // está depurando o BlobTrap, e misturar as duas coisas suja o arquivo que a pessoa
        // anexa num relato de bug.
        Log.Directory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".", "probe-logs");

        var targets = LoadTargets(Value(args, "--targets"));

        if (only is not null)
            targets = targets
                .Where(t => t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)
                         || t.Url.Contains(only, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("nenhum alvo a rodar.");
            return 2;
        }

        Console.WriteLine($"BlobTrap {AppVersion.Current} - {targets.Count} alvo(s)\n");

        using var runner = new ProbeRunner(options) { Report = Console.WriteLine };
        await runner.StartAsync();

        var results = new List<DetectionResult>();
        foreach (var target in targets) results.Add(await runner.RunAsync(target));

        var report = DetectionCheck.Render(results, AppVersion.Current, DateTimeOffset.Now);
        await File.WriteAllTextAsync(outputPath, report, Encoding.UTF8);

        var passed = results.Count(r => r.Outcome == DetectionOutcome.Passou);
        Console.WriteLine($"\n{passed}/{results.Count} passaram. Relatório: {Path.GetFullPath(outputPath)}");

        return passed == results.Count ? 0 : 1;
    }

    /// <summary>
    /// Lê a lista de alvos de um JSON, ou usa a embutida.
    ///
    /// Um arquivo ilegível para a ferramenta de propósito, em vez de cair no padrão: quem
    /// passou <c>--targets</c> quer medir aqueles alvos, e rodar outros calado devolveria um
    /// relatório que parece certo e responde a pergunta errada.
    /// </summary>
    private static List<DetectionTarget> LoadTargets(string? path)
    {
        if (path is null) return DetectionTarget.Defaults().ToList();

        var json = File.ReadAllText(path);
        var targets = JsonSerializer.Deserialize<List<DetectionTarget>>(json, TargetsJson)
            ?? throw new InvalidDataException($"{path} não contém uma lista de alvos.");

        return targets;
    }

    private static void PrintUsage() => Console.WriteLine("""
        BlobTrap.Probe - autoteste de detecção contra páginas reais.

          --out <arquivo>       onde gravar o relatório (padrão: probe-<data>.md)
          --targets <arquivo>   lista de alvos em JSON [{ "name", "url", "expectation" }]
                                expectation: QualquerMidia | Stream | Arquivo | RecusaPorDrm
          --only <texto>        roda só os alvos cujo nome ou URL contenha o texto
          --budget <segundos>   teto por alvo (padrão: 45)
          --quiet <segundos>    silêncio do sniffer que encerra a coleta (padrão: 6)
          --max-resolve <n>     candidatos resolvidos por alvo (padrão: 3)
          --hidden              tira a janela do caminho (o navegador continua rodando)
          --trace <texto>       imprime toda resposta cuja URL contenha o texto, com o que o
                                classificador concluiu sobre ela

        Saída: 0 se todos passaram, 1 se algum falhou, 2 se a ferramenta não rodou.
        """);

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double Number(string[] args, string name, double fallback) =>
        double.TryParse(Value(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
