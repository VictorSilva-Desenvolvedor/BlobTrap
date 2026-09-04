using System.IO;
using System.Runtime.CompilerServices;
using BlobTrap.Core.Diagnostics;
using Xunit;

// Parte da suite mexe em estado global de processo: Log.Directory e Log.MinimumLevel sao
// estaticos, porque um logger e' um logger. Com classes rodando em paralelo, um teste do
// DownloadManager escrevia no log enquanto LogTests apagava a pasta - e o resultado era uma
// falha que depende de quem chegou primeiro.
//
// A suite inteira roda em menos de meio segundo, entao o paralelismo nao estava comprando
// nada em troca desse risco.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BlobTrap.Tests;

internal static class IsolamentoDeLog
{
    /// <summary>
    /// Manda o log da suíte para uma pasta temporária, antes que qualquer teste rode.
    ///
    /// <see cref="Log.Directory"/> cai em %LOCALAPPDATA%\BlobTrap\logs por padrão, que é o log
    /// REAL do usuário. Rodar `dotnet test` despejava centenas de linhas de download falso lá
    /// dentro — "segmento 412", caminhos em Temp, jobs que nunca existiram. Num arquivo de mil
    /// linhas, quase todas eram lixo de teste, e o log deixava de servir para diagnosticar o
    /// app de verdade, que era a única razão de ele existir.
    ///
    /// ModuleInitializer, e não um fixture: precisa valer para toda a suíte, inclusive para os
    /// testes que só tocam no log de raspão através do DownloadManager.
    /// </summary>
    [ModuleInitializer]
    internal static void Redirecionar()
    {
        Log.Directory = Path.Combine(
            Path.GetTempPath(), "blobtrap-testes-" + Guid.NewGuid().ToString("N")[..8], "logs");
    }
}
