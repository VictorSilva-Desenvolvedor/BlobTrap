# BlobTrap

Baixador de vídeos para Windows escrito em C#. Você navega dentro do próprio app, o BlobTrap
observa a rede do navegador embutido e lista tudo que for mídia — arquivo direto, HLS, DASH,
legenda — com as qualidades disponíveis para escolher.

O nome vem do problema que ele resolve: players modernos usam `blob:` (MSE), uma URL que não
dá para baixar. O que dá para baixar são os segmentos por trás dela, e é isso que o BlobTrap
captura.

## O que ele faz

- **Navegador embutido (WebView2)** com sniffer via Chrome DevTools Protocol. Vê todas as
  requisições — inclusive as que o pipeline de mídia faz sozinho, que é justamente o caso do
  `blob:`. Sem proxy, sem certificado, sem extensão.
- **Formatos**: HLS (`.m3u8`), DASH (`.mpd`), arquivos progressivos (mp4, webm, mkv, mov, flv…),
  áudio (mp3, m4a, opus…) e legendas (vtt, srt, ttml).
- **Motor próprio em C#** para HLS e DASH: parser de manifesto, download paralelo de segmentos
  com escrita ordenada, decriptação AES-128, e ffmpeg só para remuxar/juntar (`-c copy`, sem
  recodificar).
- **yt-dlp como fallback** para sites cuja mídia nunca aparece como manifesto simples, e para o
  botão "Baixar esta página".
- **Replay de contexto HTTP**: cookies, `Referer`, `Origin`, `User-Agent` e `Authorization`
  capturados do request original são reenviados, senão a CDN devolve 403.
- **Faixas separadas**: quando o vídeo vem sem áudio (o normal em DASH e em HLS com
  `EXT-X-MEDIA`), o BlobTrap baixa as duas e junta com ffmpeg.

## O que ele não faz

Não remove DRM. Streams com Widevine, PlayReady, FairPlay ou ClearKey são identificados no
manifesto e recusados com uma mensagem explícita. Isso é uma decisão de projeto, não uma
limitação a contornar.

Você é responsável por ter o direito de baixar o que baixar.

## Como rodar

Requisitos: .NET 8 SDK e o **Microsoft Edge WebView2 Runtime** (já vem no Windows 11).

```bash
dotnet build
dotnet run --project src/BlobTrap.App
```

Na primeira execução, abra **Ferramentas** e instale ffmpeg e yt-dlp — o app baixa os dois
para `%LOCALAPPDATA%\BlobTrap\bin`. Sem ffmpeg, streams saem como `.ts` bruto e faixas
separadas não podem ser juntadas.

## Como usar

1. Navegue até a página do vídeo na barra de endereço.
2. Dê play. É o play que faz o player buscar o manifesto.
3. As mídias aparecem no painel direito. Clique em **Baixar**.
4. Escolha a qualidade, a faixa de áudio e onde salvar.

Se nada aparecer, use **Baixar esta página** — passa a URL para o yt-dlp, que conhece as regras
de extração de mais de mil sites.

## Estrutura

```
src/BlobTrap.Core/          # tudo que não é UI
  Sniffing/                 # classificação de URL e registro de candidatos
  Hls/                      # parser de playlist M3U8 (RFC 8216)
  Dash/                     # parser de MPD e expansão de templates de segmento
  Resolving/                # manifesto -> lista de qualidades selecionáveis
  Download/                 # download paralelo, AES-128, fila
  Tools/                    # ffmpeg, yt-dlp, instalador
  Net/                      # HttpClient com retry e replay de contexto
src/BlobTrap.App/           # WPF
  Browser/                  # sniffer CDP e sonda de DOM
  ViewModels/  Views/
tests/BlobTrap.Tests/       # 99 testes de parser, classificação e cripto
```

## Testes

```bash
dotnet test
```

Cobrem o que quebra silenciosamente: parsing de HLS (atributos com vírgula dentro de aspas,
byte ranges encadeados, IV derivado do media sequence), DASH (templates `$Number%05d$`,
`SegmentTimeline` com `r=`, herança de `BaseURL` e de duração de período), classificação de
URL e o round-trip do AES-128.
