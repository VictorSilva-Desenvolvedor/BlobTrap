# Builds the BlobTrap installer end to end: publish, then compile the Inno Setup script.
#
#   pwsh installer\build.ps1
#   pwsh installer\build.ps1 -Version 1.1.0
#
# The app is published self-contained, so the machine running the installer needs no .NET
# runtime. WebView2 is still required and the installer checks for it.

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish
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
