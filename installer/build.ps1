# Builds the BlobTrap installer end to end: publish, then compile the Inno Setup script.
#
#   pwsh installer\build.ps1
#   pwsh installer\build.ps1 -Version 1.1.0
#   pwsh installer\build.ps1 -Portable      # so' o .exe, sem instalador
#
# The app is published self-contained, so the machine running the installer needs no .NET
# runtime. WebView2 is still required and the installer checks for it.
#
# -Portable produz um executavel unico em dist\, para quem quer rodar sem instalar (pendrive,
# maquina de terceiro, teste rapido). E' o mesmo binario, empacotado diferente: nao cria
# atalho, nao aparece em Adicionar/Remover Programas e nao checa o WebView2 antes de abrir -
# sem o runtime ele abre e avisa na propria janela.
#
# Os dados continuam em %LOCALAPPDATA%\BlobTrap nos dois modos, entao o portatil NAO e'
# isolado: ele compartilha ferramentas, preferencias e perfil do navegador com o instalado.

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish,
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\BlobTrap.App\BlobTrap.App.csproj'
$publishDir = Join-Path $repoRoot 'publish'
$distDir = Join-Path $repoRoot 'dist'
$script = Join-Path $PSScriptRoot 'BlobTrap.iss'

function Find-Iscc {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw "ISCC.exe nao encontrado. Instale com: winget install JRSoftware.InnoSetup"
}

if (-not $Version) {
    # Single source of truth: the version in the app project file.
    [xml]$csproj = Get-Content $appProject
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "Nao consegui ler <Version> de $appProject" }
}

Write-Host "BlobTrap $Version ($Runtime)" -ForegroundColor Cyan

if ($Portable) {
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $portableDir = Join-Path $repoRoot 'publish-portable'
    if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }

    Write-Host 'Publicando executavel unico...' -ForegroundColor Cyan

    # Single-file sim, trimming NAO. Sao coisas diferentes e so' a segunda e' perigosa aqui:
    # o WPF resolve tipos por reflexao e o trimmer remove em silencio o que o XAML precisa em
    # runtime. Empacotar tudo num arquivo nao mexe em quais tipos existem.
    dotnet publish $appProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $portableDir `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)" }

    $produced = Join-Path $portableDir 'BlobTrap.exe'
    if (-not (Test-Path $produced)) { throw "Publish nao produziu BlobTrap.exe em $portableDir" }

    # A promessa do portatil e' "um arquivo so'". Se sobrou qualquer outra coisa ao lado, ela
    # foi perdida no caminho ate o usuario - melhor falhar aqui do que entregar quebrado.
    $extras = Get-ChildItem $portableDir -Recurse -File | Where-Object { $_.Name -ne 'BlobTrap.exe' }
    if ($extras) { throw "O publish portatil deixou arquivos soltos: $($extras.Name -join ', ')" }

    $target = Join-Path $distDir "BlobTrap-$Version-portable.exe"
    Move-Item $produced $target -Force
    Remove-Item $portableDir -Recurse -Force

    $mb = (Get-Item $target).Length / 1MB
    Write-Host ("Pronto: {0} ({1:N1} MB)" -f $target, $mb) -ForegroundColor Green
    return
}

if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Host 'Publicando...' -ForegroundColor Cyan
    # Not single-file and not trimmed: WPF resolves types by reflection, and trimming
    # silently removes what XAML needs at runtime.
    dotnet publish $appProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $publishDir `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $publishDir 'BlobTrap.exe'))) {
    throw "Publish nao produziu BlobTrap.exe em $publishDir"
}

$publishSize = (Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("Publicado: {0:N0} arquivos, {1:N1} MB" -f `
    (Get-ChildItem $publishDir -Recurse -File).Count, ($publishSize / 1MB))

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$iscc = Find-Iscc
Write-Host "Compilando instalador com $iscc" -ForegroundColor Cyan

& $iscc "/DAppVersion=$Version" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou ($LASTEXITCODE)" }

$setup = Join-Path $distDir "BlobTrap-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Instalador nao encontrado em $setup" }

$size = (Get-Item $setup).Length
Write-Host ("Pronto: {0} ({1:N1} MB)" -f $setup, ($size / 1MB)) -ForegroundColor Green
