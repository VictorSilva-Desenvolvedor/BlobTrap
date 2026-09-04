using System.Text;
using System.Text.RegularExpressions;

namespace BlobTrap.Core.Diagnostics;

/// <summary>
/// Tira segredo de texto antes de ele chegar ao disco.
///
/// Isto existe por causa do log. Sem log, os segredos que o BlobTrap manipula — Cookie de
/// sessão, Authorization, token assinado na query string da CDN — só passavam por memória e
/// pela linha de comando do yt-dlp. Um arquivo de log muda isso: ele fica no disco, sobrevive
/// ao processo, e é justamente o arquivo que o usuário anexa num relato de bug.
///
/// A regra é conservadora de propósito. Redigir demais custa uma linha de log menos útil;
/// redigir de menos vaza a sessão do usuário para dentro de um anexo de issue.
/// </summary>
public static class Redaction
{
    public const string Placeholder = "[redigido]";

    /// <summary>
    /// Nomes de parâmetro que carregam credencial. A comparação é por "contém", não por
    /// igualdade: CDNs usam <c>hdnts</c>, <c>__token__</c>, <c>x-amz-signature</c>,
    /// <c>Policy</c>, <c>Key-Pair-Id</c> - não há um nome canônico a casar.
    /// </summary>
    private static readonly string[] SecretParameterMarkers =
    {
        "token", "key", "sig", "auth", "password", "passwd", "secret",
        "hmac", "policy", "credential", "session", "expires", "hdnts", "nonce",
    };

    /// <summary>Cabeçalhos cujo valor nunca deve aparecer inteiro.</summary>
    private static readonly string[] SecretHeaders =
    {
        "cookie", "set-cookie", "authorization", "proxy-authorization", "x-auth-token", "x-api-key",
    };

    private static readonly Regex UserProfilePattern = new(
        @"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Cabeçalho de credencial em texto solto, no formato "Cookie: sessao=abc" que ffmpeg e
    /// yt-dlp usam ao ecoar o que receberam.
    ///
    /// Mira só nos nomes sensíveis, em vez de casar "nome: valor" genérico. A versão genérica
    /// tinha um furo silencioso: numa linha com URL, "https" casava como nome e o valor comia
    /// o resto da linha — incluindo o Cookie que vinha depois, que então nunca era redigido.
    /// O lookbehind impede que o nome case no meio de outra palavra ou de um caminho.
    /// </summary>
    private static readonly Regex SecretHeaderLinePattern = new(
        @"(?<![\w/])(?<name>cookie|set-cookie|authorization|proxy-authorization|x-auth-token|x-api-key)\s*:\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Passa um texto qualquer pela redação completa: URLs, cabeçalhos e caminho do usuário.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = ScrubUrlsIn(text);
        result = ScrubHeaderLines(result);
        return ScrubUserPath(result);
    }

    /// <summary>
    /// Mantém a URL legível — host e caminho dizem qual CDN e qual segmento falhou — e troca
    /// só o valor dos parâmetros que carregam credencial.
    /// </summary>
    public static string ScrubUrl(Uri? url)
    {
        if (url is null) return string.Empty;
        if (!url.IsAbsoluteUri) return ScrubUserPath(url.OriginalString);

        var query = url.Query;
        if (query.Length <= 1) return url.GetLeftPart(UriPartial.Path);

        var builder = new StringBuilder(url.GetLeftPart(UriPartial.Path)).Append('?');
        var first = true;

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0) continue;
            if (!first) builder.Append('&');
            first = false;

            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                builder.Append(pair);
                continue;
            }

            var name = pair[..separator];
            builder.Append(name).Append('=')
                   .Append(IsSecretParameter(name) ? Placeholder : pair[(separator + 1)..]);
        }

        return builder.ToString();
    }

    /// <summary>Valor de cabeçalho, redigido inteiro quando o nome é de credencial.</summary>
    public static string ScrubHeader(string name, string value) =>
        IsSecretHeader(name) ? Placeholder : ScrubUserPath(ScrubUrlsIn(value));

    /// <summary>
    /// Troca <c>C:\Users\fulano</c> por <c>%USERPROFILE%</c>. O nome de usuário não ajuda a
    /// diagnosticar nada e costuma ser o nome real da pessoa.
    /// </summary>
    public static string ScrubUserPath(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : UserProfilePattern.Replace(text, "%USERPROFILE%");

    public static bool IsSecretParameter(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        foreach (var marker in SecretParameterMarkers)
            if (lower.Contains(marker, StringComparison.Ordinal)) return true;

        return false;
    }

    public static bool IsSecretHeader(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        foreach (var header in SecretHeaders)
            if (lower == header) return true;

        return false;
    }

    /// <summary>Acha URLs no meio de um texto solto (stderr do ffmpeg, mensagem de excecao).</summary>
    private static string ScrubUrlsIn(string text)
    {
        if (!text.Contains("://", StringComparison.Ordinal)) return text;

        return Regex.Replace(
            text,
            @"[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""'<>\\]+",
            match => Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? ScrubUrl(uri)
                : match.Value,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
    }

    private static string ScrubHeaderLines(string text)
    {
        if (!text.Contains(':', StringComparison.Ordinal)) return text;

        // Preserva a grafia original do nome: quem le o log reconhece o cabecalho que enviou.
        return SecretHeaderLinePattern.Replace(text, match => match.Groups["name"].Value + ": " + Placeholder);
    }
}
