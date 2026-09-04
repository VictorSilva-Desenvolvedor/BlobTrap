using System.Diagnostics;
using System.Reflection;

namespace BlobTrap.Core.Diagnostics;

/// <summary>
/// A versão do BlobTrap em execução.
///
/// Um log sem versão responde "o que aconteceu" mas não "em qual build", que é a primeira
/// pergunta de qualquer relato de bug — e a única que o usuário não consegue responder
/// sozinho, porque até agora a versão não aparecia em lugar nenhum da interface.
/// </summary>
public static class AppVersion
{
    /// <summary>Versão legível ("1.0.0"), ou "desconhecida" quando o assembly não declara uma.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            // InformationalVersion carrega o que <Version> definiu no csproj. Ele pode vir com
            // o hash do commit anexado ("1.0.0+abc1234"), que nao interessa ao usuario.
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus < 0 ? informational : informational[..plus];
            }

            // Environment.ProcessPath, e nao Assembly.Location: num app publicado como arquivo
            // unico o assembly esta embutido no executavel e Location devolve string vazia -
            // este fallback morreria em silencio justamente na distribuicao portatil, que e'
            // onde ele mais importa, porque nao ha .dll ao lado para inspecionar.
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable))
            {
                var info = FileVersionInfo.GetVersionInfo(executable);
                if (!string.IsNullOrWhiteSpace(info.FileVersion)) return info.FileVersion!;
            }

            return assembly.GetName().Version?.ToString(3) ?? "desconhecida";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or BadImageFormatException)
        {
            // Saber a versao nunca vale derrubar o app que a esta registrando.
            return "desconhecida";
        }
    }
}
