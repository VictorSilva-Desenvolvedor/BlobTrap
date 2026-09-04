# Backlog

Problemas encontrados fora do tópico atual, grandes/arriscados/ambíguos demais
para corrigir na hora (regra 4 do CLAUDE.md). Um item por problema, com arquivo,
linha, sintoma e hipótese.

---

## Instalador sem assinatura de código

**Arquivo:** `installer/BlobTrap.iss` — hook comentado logo antes de `[Files]`

**Sintoma:** o `BlobTrap-Setup-<versão>.exe` não é assinado. Todo usuário leva um
aviso do SmartScreen na primeira execução — *"O Windows protegeu o computador"* —
com o botão de continuar escondido atrás de "Mais informações".

O custo real não é o clique a mais: é que esse aviso é **idêntico** ao que o
Windows daria para um instalador realmente malicioso. Distribuir sem assinatura
treina o usuário a ignorá-lo.

**Hipótese:** não é um problema de código. Depende de adquirir um certificado, e
a escolha tem consequências que não dá para desfazer depois de comprar.

**Caminhos, com o que cada um custa (levantado em 2026-09-04):**

| Caminho | Custo aproximado | Reputação SmartScreen | Observação |
| --- | --- | --- | --- |
| Azure Trusted Signing | ~US$ 10/mês | Herda reputação da Microsoft desde o dia 1 | Exige entidade validada; o mais barato e o mais simples de automatizar em CI |
| Certificado OV | ~US$ 200/ano | Construída do zero, leva semanas de downloads | Desde 2023 exige token físico ou HSM, o que complica CI |
| Certificado EV | ~US$ 400/ano | Imediata | Token físico obrigatório; assinar na CI exige HSM na nuvem |

**Recomendação:** Azure Trusted Signing, por ser o único que resolve a reputação
imediatamente sem token físico — o token é o que inviabiliza assinar no
workflow de CI.

**Quando houver certificado:** descomentar `SignTool` e `SignedUninstaller` no
`.iss`. Nada mais precisa mudar no build.

---

## Cancelamento sem retorno na interface

Quatro problemas do mesmo fluxo — o botão "Cancelar" da linha de download em
`src/BlobTrap.App/Views/MainWindow.xaml:341-344`. Nenhum quebra o cancelamento em
si: o token propaga, o job termina em `Canceled` e a pasta temporária é
preservada para o resume. O que falha é o que a pessoa vê enquanto isso.

**1. Job cancelado continua exibindo velocidade e ETA**

**Arquivo:** `src/BlobTrap.App/ViewModels/JobItem.cs:68-94` (`DetailLabel`)

**Sintoma:** há ramo para `Failed`, `Completed` e `Queued`/`Preparing`, mas não
para `Canceled`. O estado cai no ramo de progresso e a linha congela em algo como
`147/300 segmentos - 3,2 MB/s - faltam 4min`. Lido de relance, isso é um download
travado, não um cancelado.

**Hipótese:** um ramo `Canceled` devolvendo quanto foi baixado antes da parada
resolve, mas o texto certo depende do item 3 abaixo — um job cancelado ainda na
fila não tem progresso nenhum a mostrar.

**2. "Cancelado" sai sem cor**

**Arquivo:** `src/BlobTrap.App/Views/MainWindow.xaml:349-355`

**Sintoma:** os `DataTrigger` do template cobrem só `IsFailed` (`Danger`) e
`IsCompleted` (`Success`). `Cancelado` fica no cinza padrão do `Caption`,
visualmente idêntico a "Na fila" e "Preparando".

**Hipótese:** falta decidir o token — cancelar não é erro, então `Danger` mente;
provavelmente um tom neutro-escuro próprio, e isso é decisão de design.

**3. Cancelar um job que ainda está na fila não muda nada na tela**

**Arquivo:** `src/BlobTrap.Core/Download/DownloadManager.cs:80-96` (`PumpAsync` e
`RunAsync`), com `DownloadJob.Cancel` em `src/BlobTrap.Core/Models/DownloadJob.cs:153`

**Sintoma:** `Cancel()` só sinaliza o token. O job permanece no `_pending` e só
transiciona para `Canceled` quando o `PumpAsync` o desenfileirar — o que, com os
slots ocupados por downloads longos, pode levar minutos. Nesse intervalo a linha
continua dizendo "Na fila" com o botão "Cancelar" ativo. Do ponto de vista de
quem clicou, o clique não fez nada.

**Hipótese:** marcar `Canceled` na hora quando `State == Queued`, e fazer o
`RunAsync` apenas ignorar jobs já finalizados. Ambíguo porque toca a
responsabilidade sobre a transição de estado, hoje concentrada no manager: passar
parte dela para o `DownloadJob` precisa de decisão antes do código.

**4. Não existe estado intermediário "Cancelando…"**

**Arquivo:** `src/BlobTrap.Core/Models/DownloadJob.cs:7-16` (enum `DownloadState`)

**Sintoma:** num job com ffmpeg rodando ou segmento em voo, o cancelamento não é
instantâneo. No intervalo entre o clique e o `OperationCanceledException`, o botão
segue visível (`CanCancel` é `!IsFinished`) e o progresso continua andando. Sem
feedback, a pessoa clica de novo.

**Hipótese:** um valor `Canceling` no enum, propagado até o `StateLabel` e
escondendo o botão. Mexe no Core e no contrato de estados que o `DownloadManager`,
o `JobItem` e os testes assumem — é a mudança mais invasiva das quatro e não deve
entrar junto com as outras.
