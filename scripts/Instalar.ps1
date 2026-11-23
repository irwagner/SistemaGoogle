param(
    [string]$Programa = (Join-Path $env:LOCALAPPDATA 'CentralProducao/programa'),
    [switch]$Rollback
)

$ErrorActionPreference = 'Stop'
if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Esta edição requer Windows x64.' }
$installRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Programa)
if ($installRoot.StartsWith('\\')) { throw 'Use uma pasta local.' }

function Assert-LocalPath([string]$PathValue) {
    for ($folder = $PathValue; $folder; $folder = Split-Path -Parent $folder) {
        if ((Test-Path -LiteralPath $folder) -and ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Ponto de reanálise no caminho da instalação: $folder"
        }
    }
}

function Assert-Release([string]$AppPath, $Manifest) {
    if ($Manifest.target -ne 'win-x64' -or $Manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Manifesto inválido.' }
    $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Manifest.files) {
        if ($file.path -notmatch '^[A-Za-z0-9._/-]+$' -or $file.path.Split('/') -contains '..' -or $file.path.StartsWith('/') -or !$paths.Add($file.path)) {
            throw 'Caminho inválido ou duplicado no manifesto.'
        }
        $resolved = Join-Path $AppPath $file.path
        Assert-LocalPath $resolved
        if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $file.sha256) { throw "Hash divergente: $($file.path)" }
    }
    $actual = @(Get-ChildItem -LiteralPath $AppPath -Recurse -File -Force)
    if (!$paths.Contains('Central.exe') -or $actual.Count -ne $paths.Count) { throw 'Release incompleto.' }
}

function Set-Pointer([string]$Pointer, [string]$Version) {
    $temp = "$Pointer.$([Guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $temp -Value $Version -Encoding ascii
    if (Test-Path -LiteralPath $Pointer) {
        $backup = "$temp.anterior"
        [IO.File]::Replace($temp, $Pointer, $backup)
        [IO.File]::Delete($backup)
    } else {
        [IO.File]::Move($temp, $Pointer)
    }
}

Assert-LocalPath $installRoot
$releases = Join-Path $installRoot 'releases'
Assert-LocalPath $releases
$null = New-Item -ItemType Directory -Path $releases -Force
$current = Join-Path $installRoot 'atual.txt'
$previous = Join-Path $installRoot 'anterior.txt'
Assert-LocalPath $current
Assert-LocalPath $previous
$old = if (Test-Path -LiteralPath $current) { (Get-Content -LiteralPath $current -Raw).Trim() } else { $null }
if ($old -and $old -notmatch '^\d+\.\d+\.\d+$') { throw 'Ponteiro atual inválido.' }
if ($Rollback) {
    $version = (Get-Content -LiteralPath $previous -Raw).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão anterior inválida.' }
    $app = Join-Path $releases $version
    $manifest = Get-Content -LiteralPath (Join-Path $releases "$version.json") -Raw | ConvertFrom-Json
    if ($manifest.version -ne $version) { throw 'Manifesto anterior divergente.' }
    Assert-Release $app $manifest
} else {
    $manifestFile = Join-Path $PSScriptRoot 'release.json'
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $app = Join-Path $PSScriptRoot 'app'
    Assert-Release $app $manifest
    $version = $manifest.version
    $target = Join-Path $releases $version
    if (Test-Path -LiteralPath $target) {
        Assert-Release $target $manifest
    } else {
        $stage = Join-Path $releases ".staging-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $app -Destination $stage -Recurse
        Assert-Release $stage $manifest
        [IO.Directory]::Move($stage, $target)
    }
    Copy-Item -LiteralPath $manifestFile -Destination (Join-Path $releases "$version.json") -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Abrir-Central.cmd') -Destination $installRoot -Force
}
if ($old -and $old -ne $version) { Set-Pointer $previous $old }
Set-Pointer $current $version
Write-Output "Versão $version ativa. Launcher em $installRoot\Abrir-Central.cmd"
