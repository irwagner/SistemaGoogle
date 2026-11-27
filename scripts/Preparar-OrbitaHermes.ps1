param([switch]$PularDependencias, [switch]$SomenteInterface)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$integration = Join-Path $root 'src\Orbita.Hermes'
$lock = Get-Content -LiteralPath (Join-Path $integration 'upstream.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root '.orbita-hermes'
$checkout = Join-Path $cache 'upstream'
if (!(Test-Path -LiteralPath $checkout)) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    & git -c core.longpaths=true clone --depth 1 --branch $lock.tag $lock.repository $checkout
    if ($LASTEXITCODE) { throw 'O checkout do Hermes falhou.' }
}
$actual = & git -C $checkout rev-parse HEAD
if ($LASTEXITCODE -or $actual -ne $lock.commit) { throw 'O checkout do Hermes não corresponde ao upstream.json.' }

$desktop = Join-Path $checkout 'apps\desktop'
if (!$PularDependencias) {
    Push-Location -LiteralPath $checkout
    try {
        & npx.cmd --yes "pnpm@$($lock.pnpm)" --filter 'hermes...' install --frozen-lockfile --trust-lockfile --ignore-scripts
        if ($LASTEXITCODE) { throw 'A instalação das dependências do Hermes falhou.' }
    } finally { Pop-Location }
}
$renderer = Join-Path $integration 'renderer'
$source = Join-Path $desktop 'src\orbita'
New-Item -ItemType Directory -Force -Path $source | Out-Null
Copy-Item -LiteralPath (Join-Path $renderer 'orbita.html'),(Join-Path $renderer 'orbita.vite.mts') -Destination $desktop
Copy-Item -LiteralPath (Join-Path $renderer 'orbita\main.tsx'),(Join-Path $renderer 'orbita\orbita.css') -Destination $source
Push-Location -LiteralPath $desktop
try {
    & node 'node_modules/vite/bin/vite.js' build --config 'orbita.vite.mts'
    if ($LASTEXITCODE) { throw 'A compilação da interface do Orbita falhou.' }
    if ($SomenteInterface) { return }
    $electron = Join-Path $desktop 'node_modules\electron'
    $runtime = Join-Path $electron 'dist'
    if (!(Test-Path -LiteralPath (Join-Path $runtime 'electron.exe'))) {
        & node (Join-Path $electron 'install.js')
        if ($LASTEXITCODE) { throw 'O runtime Electron não pôde ser instalado.' }
    }
} finally { Pop-Location }

$output = Join-Path $cache ('windows-x64-' + [guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $runtime -Destination $output -Recurse
$app = Join-Path $output 'resources\app'
New-Item -ItemType Directory -Force -Path $app | Out-Null
Copy-Item -LiteralPath (Join-Path $integration 'native\main.cjs'),(Join-Path $integration 'native\preload.cjs'),(Join-Path $integration 'native\orbita-api.cjs') -Destination $app
Copy-Item -LiteralPath (Join-Path $desktop 'dist-orbita') -Destination (Join-Path $app 'renderer') -Recurse
Copy-Item -LiteralPath (Join-Path $checkout 'LICENSE') -Destination (Join-Path $output 'LICENSE-Hermes.txt')
Copy-Item -LiteralPath (Join-Path $integration 'upstream.json') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\ORBITA_CLIENTE_HERMES.md') -Destination (Join-Path $output 'Leia-me.md')
'{"name":"orbita-studio-preview","version":"0.4.0-preview.1","main":"main.cjs"}' | Set-Content -LiteralPath (Join-Path $app 'package.json') -Encoding ascii
Rename-Item -LiteralPath (Join-Path $output 'electron.exe') -NewName 'Orbita.Studio.Preview.exe'
$output | Set-Content -LiteralPath (Join-Path $cache 'ultimo-build.txt') -Encoding utf8
Write-Output $output
