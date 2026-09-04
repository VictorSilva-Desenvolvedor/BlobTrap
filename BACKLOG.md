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

## Alvo DASH da sonda estoura o tempo limite

**Arquivo:** `tools/BlobTrap.Probe` — alvo "DASH público (dash.js)",
`https://reference.dashif.org/dash.js/latest/samples/advanced/monitoring.html`

**Sintoma:** `Erro: tempo esgotado (45s)`. O alvo nunca chega a reportar candidato
nem player; os outros quatro terminam entre 6 e 10 segundos.

**Hipótese:** é a página, não o BlobTrap. O `monitoring.html` do dash.js carrega
um painel de métricas com dependências externas pesadas, e o dash.if já se provou
instável nesta bateria — dois alvos anteriores dele foram descartados como falso
alarme (o "reference player" exige clique em Load, e `hello-world.html` responde
404). Um terceiro alvo do mesmo host falhando por conta própria é o padrão, não a
exceção.

**Por que não corrigi agora:** o defeito, se existir, é da sonda ou da escolha de
alvo — as duas coisas são da sessão `blobtrap-2a`, que construiu a ferramenta. E
o caminho DASH do produto está coberto por outro alvo que passa (o manifesto
Widevine é DASH e é reconhecido), além dos testes de `DashParser`.

**Próximo passo sugerido:** trocar por um MPD estático e estável — por exemplo
`https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd`, que respondeu
`application/dash+xml` em 04/09/2026 — ou subir o limite só desse alvo e
confirmar se é lentidão ou travamento.
