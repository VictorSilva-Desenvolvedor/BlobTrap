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

## Actions da CI ainda declarando Node 20

**Arquivo:** `.github/workflows/ci.yml` — `actions/checkout@v4` e
`actions/setup-dotnet@v4`, nos dois jobs

**Sintoma:** todo run da CI sai com anotação de aviso nos dois jobs:
*"Node.js 20 is deprecated. The following actions target Node.js 20 but are
being forced to run on Node.js 24"*. Nada quebra hoje porque o runner força a
execução no Node 24; quebra no dia em que ele parar de forçar.

O custo enquanto isso é o aviso virar paisagem: quem olha a CI verde com duas
anotações amarelas fixas aprende a não ler anotação nenhuma.

**Hipótese:** não é um problema do workflow em si — é decidir para qual major
subir. Levantado em 2026-09-05, `checkout` está em v7.0.1 e `setup-dotnet` em
v6.0.0; o BlobTrap está três e dois majors atrás. Qualquer major a partir do v5
roda em Node 24 e cala o aviso, então a escolha é de manutenção, não de
correção: subir ao mínimo que resolve, ou ao mais recente e assumir a leitura
das notas de cada major no caminho.

**Recomendação:** ir direto ao mais recente de cada uma (`checkout@v7`,
`setup-dotnet@v6`) num PR só. O workflow usa as duas no feijão com arroz —
checkout raso do commit, instalar o SDK 8 — que é a parte que menos muda entre
majors, e a própria CI é o teste: se restaurar, compilar, testar e publicar
self-contained passarem, o caminho está coberto.
