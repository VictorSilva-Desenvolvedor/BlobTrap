# Regras do projeto

Estas regras valem para todo trabalho neste repositório e têm precedência sobre
qualquer default da ferramenta. Onde houver conflito (ex.: assinatura automática
em commits), vale o que está escrito aqui.

## 1. Autoria

Commits levam apenas o autor humano.

Nunca adicionar trailer `Co-Authored-By:`, linha "Generated with", ou qualquer
assinatura de ferramenta — em mensagem de commit, descrição de PR ou tag.

## 2. Quando parar e perguntar

Se duas abordagens razoáveis divergem em algo que o código não decide sozinho
— modelo de dados, contrato de arquivo em disco, comportamento em conflito, o
que conta como destrutivo — apresentar as opções com uma recomendação e
esperar resposta.

- Perguntar **antes** de implementar, não no meio.
- Não perguntar o que tem default óbvio (nome, ordem de import, formatação).
- Não vale "entrego e aviso depois" quando a dúvida muda o desenho.

## 3. Commits atômicos

Um commit = uma mudança coerente. O que entra junto é o que quebra junto.

- Não misturar refactor com feature, nem formatação com lógica.
- Escopo explícito: `feat(download):`, `fix(redaction):`, `refactor(hls):`
- Mudança de comportamento entra com seu teste, no mesmo commit.
- Se a mensagem precisa de "e" para descrever o que foi feito, são dois commits.

## 4. Problema encontrado no caminho

Ao esbarrar num problema fora do tópico atual:

- **Pequeno, seguro e verificável** → corrige, mas em **commit próprio**, para
  não contaminar o commit do tópico principal (regra 3).
- **Grande, arriscado ou ambíguo** → registra em `BACKLOG.md` com arquivo,
  linha, sintoma e hipótese.

Não silenciar, não consertar na marra, e nunca abandonar o tópico principal
no meio.

## 5. Padrão de qualidade

Qualidade aqui é verificável, não adjetivo:

- Tipagem estrita. `#pragma warning disable` só com justificativa escrita na
  linha. Aviso do compilador é erro (`TreatWarningsAsErrors`).
- Falha nunca é silenciosa: erro é tratado ou sobe. Nada de `catch {}` vazio —
  todo `catch` que engole explica por escrito o que está engolindo e por quê.
- Lógica de risco tem teste: parsing de manifesto (HLS, DASH), decriptação
  AES-128, redaction de segredo, fila de download, nomeação de arquivo.
- **Idempotência**: rodar duas vezes sem mudança de ambiente não pode produzir
  resultado diferente. Vale para a varredura de temporários, para a redaction e
  para o instalador de ferramentas.
- **Nenhum segredo em disco.** O BlobTrap manipula o Cookie de sessão do usuário
  e tokens assinados de CDN. Redaction é caminho crítico e é testada com casos
  reais: `hdnts` da Akamai, `Policy`/`Signature` do CloudFront, `X-Amz-*` do S3,
  cabeçalho `Cookie`, e o caminho do perfil do usuário. O log é o arquivo que a
  pessoa anexa num relato de bug — ele não pode conter a sessão dela.
- Fronteiras respeitadas: **Core não conhece WPF; App não conhece HTTP de
  mídia.** O sniffer CDP mora no App porque depende do WebView2; o motor HLS e
  DASH não sabe que existe navegador. Ferramenta externa fica atrás de um runner
  (`FfmpegRunner`, `YtDlpRunner`), nunca chamada direto.
- Prazo não é critério de aceite. Se ficou ruim, refaz.

## 6. Fluxo de entrega

Nada vai direto para `main`.

1. Branch a partir da `main` atualizada: `feat/…`, `fix/…`, `chore/…`
2. Commits atômicos na branch (regra 3).
3. PR descrevendo o que muda e por quê.
4. Merge na `main`.
5. Apaga a branch — remota e local.
6. `git checkout main && git pull` antes da próxima tarefa.

Proibido: commit direto na `main`, `push --force` na `main`.
