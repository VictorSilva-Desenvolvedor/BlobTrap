<#
.SYNOPSIS
    Acompanha o log do BlobTrap ao vivo, com cor por nivel.

.DESCRIPTION
    O log fica em %LOCALAPPDATA%\BlobTrap\logs, um arquivo por dia. Este script mostra o
    arquivo de hoje e continua imprimindo conforme o app escreve - o jeito de conferir, durante
    um download, se a qualidade escolhida foi a melhor e se o arquivo vai ter audio.

    Amarelo = WARN (algo ameaca "melhor qualidade, com som"), vermelho = ERROR.
    Linhas "SEM AUDIO" e "sem ffmpeg" ganham destaque proprio.

.PARAMETER Tail
    Quantas linhas anteriores mostrar ao abrir. Padrao: 40.

.PARAMETER SoAvisos
    Mostra apenas WARN e ERROR.

.EXAMPLE
    pwsh tools\watch-log.ps1
.EXAMPLE
    powershell -File tools\watch-log.ps1 -SoAvisos
#>
[CmdletBinding()]
param(
    [int]$Tail = 40,
    [switch]$SoAvisos
)

$ErrorActionPreference = 'Stop'

$directory = Join-Path $env:LOCALAPPDATA 'BlobTrap\logs'
$file = Join-Path $directory ("blobtrap-{0:yyyy-MM-dd}.log" -f (Get-Date))

# O app cria a pasta e o arquivo na primeira linha escrita. Esperar aqui permite abrir o
# monitor antes do app.
if (-not (Test-Path -LiteralPath $file)) {
    Write-Host "Aguardando $file ..." -ForegroundColor DarkGray
    while (-not (Test-Path -LiteralPath $file)) { Start-Sleep -Milliseconds 500 }
}

function Write-LogLine([string]$line) {
    # Formato: "HH:mm:ss.fff  LEVEL  fonte  mensagem". Linhas de continuacao (excecao) nao
    # tem nivel e herdam a cor da anterior.
    if ($line -match '^\d{2}:\d{2}:\d{2}\.\d{3}\s+(DEBUG|INFO|WARN|ERROR)') {
        $script:level = $Matches[1]
    }

    if ($SoAvisos -and $script:level -notin 'WARN', 'ERROR') { return }

    $color = switch ($script:level) {
        'ERROR' { 'Red' }
        'WARN' { 'Yellow' }
        'DEBUG' { 'DarkGray' }
        default { 'Gray' }
    }

    if ($line -match 'SEM AUDIO|sem audio|DOIS arquivos') { $color = 'Magenta' }
    elseif ($line -match 'arquivo final:|concluido') { $color = 'Green' }

    Write-Host $line -ForegroundColor $color
}

$script:level = 'INFO'
Write-Host "Acompanhando $file  (Ctrl+C para sair)" -ForegroundColor Cyan

# Get-Content -Wait reabre o arquivo a cada gravacao; o app escreve em UTF-8 e o BOM so
# aparece na primeira linha.
Get-Content -LiteralPath $file -Tail $Tail -Wait -Encoding UTF8 | ForEach-Object { Write-LogLine $_ }
