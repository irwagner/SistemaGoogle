param(
    [Parameter(Mandatory)][string]$VersaoInicial,
    [Parameter(Mandatory)][string]$VersaoNova,
    [Parameter(Mandatory)][string]$Destino
)

$ErrorActionPreference = 'Stop'
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destino)
if (Test-Path -LiteralPath $root) { throw 'Use uma pasta nova para o ensaio.' }
$null = New-Item -ItemType Directory -Path $root
$program = Join-Path $root 'Programa com espaços e acentos'
$data = Join-Path $root 'Dados do ensaio'
$initial = Join-Path $root 'pacote-inicial'
$update = Join-Path $root 'pacote-novo'
$previousData = $env:CENTRAL_DATA
$env:CENTRAL_DATA = $data

function Expand-Verified([string]$Archive, [string]$PathValue) {
    $zipPath = (Resolve-Path -LiteralPath $Archive).Path
    $expected = (Get-Content -LiteralPath "$zipPath.sha256" -Raw).Split(' ')[0]
    if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expected) {
        throw 'Hash do ZIP divergente.'
    }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $PathValue
}

function Check-Version([string]$Version, [int]$Schema) {
    $name = (Get-Content -LiteralPath (Join-Path $program 'atual.txt') -Raw).Trim()
    if ($name -ne $Version) { throw 'Versão ativa divergente.' }
    $exe = Join-Path $program "releases/$name/Central.exe"
    $diagnosis = & $exe diagnostico
    if ($LASTEXITCODE -or $diagnosis -notcontains "Central $Version" -or $diagnosis -notcontains "Schema $Schema") {
        throw "Diagnóstico do binário instalado falhou: $diagnosis"
    }
    $diagnosis
}

try {
    Expand-Verified $VersaoInicial $initial
    Expand-Verified $VersaoNova $update
    $baseManifest = Get-Content -LiteralPath (Join-Path $initial 'release.json') -Raw | ConvertFrom-Json
    $newManifest = Get-Content -LiteralPath (Join-Path $update 'release.json') -Raw | ConvertFrom-Json
    $baseVersion = $baseManifest.version
    $newVersion = $newManifest.version
    $baseSchema = if ($baseManifest.schema) { $baseManifest.schema } else { 4 }
    $newSchema = if ($newManifest.schema) { $newManifest.schema } else { 4 }
    if ($baseVersion -eq $newVersion) { throw 'Use duas versões diferentes.' }
    & (Join-Path $initial 'Instalar.ps1') -Programa $program
    Check-Version $baseVersion $baseSchema
    $preUpgrade = Join-Path $root 'Dados antes da atualização'
    & (Join-Path $program "releases/$baseVersion/Central.exe") backup $preUpgrade
    if ($LASTEXITCODE) { throw 'Backup anterior falhou.' }
    & (Join-Path $update 'Instalar.ps1') -Programa $program
    Check-Version $newVersion $newSchema
    & (Join-Path $update 'Instalar.ps1') -Programa $program -Rollback
    if ($newSchema -gt $baseSchema) {
        $env:CENTRAL_DATA = Join-Path $root 'Dados compatíveis com a versão anterior'
        & (Join-Path $program "releases/$baseVersion/Central.exe") restore $preUpgrade
        if ($LASTEXITCODE) { throw 'Restauração para rollback falhou.' }
    }
    Check-Version $baseVersion $baseSchema
    if (!(Test-Path -LiteralPath (Join-Path $data 'central.sqlite'))) { throw 'Dados não sobreviveram ao rollback.' }
    if (!(Test-Path -LiteralPath (Join-Path $program 'Abrir-Central.cmd'))) { throw 'Launcher ausente.' }
    'OK Windows x64: ZIP verificado, instalação, atualização lado a lado, rollback e banco preservado.'
} finally { $env:CENTRAL_DATA = $previousData }
