param([int]$Porta = 7784, [string]$Perfil, [switch]$PrepararSomente, [switch]$IniciarComWindows)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root '.orbita-hermes'
$upstream = Join-Path $cache 'upstream'
$desktop = Join-Path $upstream 'apps\desktop'
$runtime = Join-Path $cache 'runtime-venv\Scripts'
if ([string]::IsNullOrWhiteSpace($Perfil)) { $Perfil = Join-Path $cache 'perfil-orbita' }
$homeDir = [IO.Path]::GetFullPath($Perfil)
if (!(Test-Path -LiteralPath (Join-Path $desktop 'dist\electron-main.mjs'))) { throw 'Prepare o cliente Hermes nativo antes de abrir.' }
if ($Porta -lt 1024 -or $Porta -gt 65535) { throw 'Porta do Orbita inválida.' }

if ($IniciarComWindows) {
    if ($Porta -ne 7784 -or $homeDir -ine (Join-Path $cache 'perfil-orbita')) { throw 'Use o perfil principal para configurar a inicialização do Windows.' }
    $startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if ([string]::IsNullOrWhiteSpace($startup)) { throw 'O Windows não informou a pasta de inicialização do usuário.' }
    $null = New-Item -ItemType Directory -Path $startup -Force
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startup 'Orbita Studio.lnk'))
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shortcut.Arguments = '-NoLogo -NoProfile -WindowStyle Hidden -File "{0}"' -f $PSCommandPath
    $shortcut.WorkingDirectory = $root
    $shortcut.Description = 'Abrir o Orbita Studio ao entrar no Windows'
    $shortcut.Save()
    $trayPreferences = Join-Path $homeDir 'desktop'
    $null = New-Item -ItemType Directory -Path $trayPreferences -Force
    [IO.File]::WriteAllText((Join-Path $trayPreferences 'minimize-to-tray.json'), '{"enabled":true}')
}

function Test-OrbitaLocal {
    try {
        $build = Invoke-RestMethod -Uri "http://127.0.0.1:$Porta/build" -TimeoutSec 2
        return $build -match '^\d+\.\d+\.\d+'
    } catch { return $false }
}

$newServer = Join-Path $root 'entrega\Windows\OrbitaStudio-0.3.17-win-x64\studio\Orbita.Studio.exe'
$oldServer = Join-Path $root 'entrega\Windows\OrbitaStudio-0.3.16-win-x64\studio\Orbita.Studio.exe'

function Update-IdleOrbita {
    if (!(Test-Path -LiteralPath $newServer) -or !(Test-OrbitaLocal)) { return }
    $address = "http://127.0.0.1:$Porta"
    if ((Invoke-RestMethod -Uri "$address/build" -TimeoutSec 2) -ne '0.3.16') { return }
    $listener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Porta -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$listener) { return }
    $previous = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    if (!$previous -or $previous.Path -ine $oldServer) { return }
    $page = Invoke-WebRequest -UseBasicParsing -Uri $address -TimeoutSec 3
    $match = [regex]::Match($page.Content, 'name="orbita-token" content="([a-f0-9]{64})"')
    if (!$match.Success) { return }
    $auth = @{ 'X-Orbita-Token' = $match.Groups[1].Value }
    $pool = Invoke-RestMethod -Uri "$address/api/pool" -Headers $auth -TimeoutSec 3
    $studio = Invoke-RestMethod -Uri "$address/api/state" -Headers $auth -TimeoutSec 3
    if ($pool.active -or $pool.enrollment -or $studio.missions.Where({ $_.state -in @('conversando', 'executando') })) {
        Write-Output 'A versão atual está em uso. A atualização pode entrar ao reabrir o Orbita com o backend livre.'
        return
    }
    Stop-Process -InputObject $previous
    $null = $previous.WaitForExit(5000)
}

if (!$PrepararSomente -and $Porta -eq 7784) {
    try { Update-IdleOrbita } catch { Write-Output 'O Orbita manteve o servidor atual disponível.' }
}

if (!$PrepararSomente -and $Porta -eq 7784 -and !(Test-OrbitaLocal)) {
    $server = if (Test-Path -LiteralPath $newServer) { $newServer } else { $oldServer }
    if (!(Test-Path -LiteralPath $server)) { throw 'Abra o servidor do Orbita antes de iniciar o Hermes Desktop.' }
    $env:ORBITA_PORT = [string]$Porta
    Start-Process -FilePath $server -WorkingDirectory (Split-Path -Parent $server) -ArgumentList '--sem-navegador' -WindowStyle Hidden
    for ($attempt = 0; $attempt -lt 15 -and !(Test-OrbitaLocal); $attempt++) { Start-Sleep -Milliseconds 300 }
}

New-Item -ItemType Directory -Path $homeDir -Force | Out-Null
$config = Join-Path $homeDir 'config.yaml'
if (!(Test-Path -LiteralPath $config)) {
    @'
model:
  default: antigravity
  provider: custom
  base_url: http://127.0.0.1:1/v1
  context_length: 64000
auxiliary:
  title_generation:
    enabled: false
onboarding:
  run: false
stt:
  enabled: false
terminal:
  backend: local
display:
  language: pt-BR
'@ | Set-Content -LiteralPath $config -Encoding utf8
    'OPENAI_API_KEY=orbita-desktop-local' | Set-Content -LiteralPath (Join-Path $homeDir '.env') -Encoding utf8
}

[IO.File]::WriteAllText((Join-Path $homeDir 'orbita-local-port'), [string]$Porta)
$env:HERMES_HOME = $homeDir
$env:HERMES_DESKTOP_USER_DATA_DIR = Join-Path $homeDir 'desktop'
$env:HERMES_DESKTOP_HERMES_ROOT = $upstream
$env:HERMES_DESKTOP_HERMES = Join-Path $runtime 'hermes.exe'
$env:HERMES_DESKTOP_APP_NAME = 'Orbita Studio'
$env:ORBITA_STUDIO_DESKTOP = '1'
$env:ORBITA_MANAGE_MESSAGING = '1'
$env:ORBITA_SIGNAL_SETUP = Join-Path $root 'Vincular-SignalOrbita.cmd'
if (!$env:DISCORD_REQUIRE_MENTION) { $env:DISCORD_REQUIRE_MENTION = 'false' }
if (!$env:DISCORD_AUTO_THREAD) { $env:DISCORD_AUTO_THREAD = 'false' }
if (!$env:DISCORD_HISTORY_BACKFILL) { $env:DISCORD_HISTORY_BACKFILL = 'false' }
if (!$env:ORBITA_CENTRAL_EXE) {
    $env:ORBITA_CENTRAL_EXE = Join-Path $root 'entrega\Windows\OrbitaStudio-0.3.10-win-x64\producao\Central.exe'
}
$env:HERMES_DESKTOP_ISOLATED_BACKEND = '1'
$env:HERMES_DESKTOP_IGNORE_EXISTING = '1'
$env:HERMES_DESKTOP_SKIP_PROTOCOL_REGISTRATION = '1'
$env:OPENAI_API_KEY = 'orbita-desktop-local'
$env:PATH = "$runtime;$env:PATH"
Push-Location -LiteralPath $upstream
try {
    & (Join-Path $runtime 'python.exe') -m tui_gateway.orbita_bridge
    if ($LASTEXITCODE) { throw 'O cliente não conseguiu carregar as missões do Orbita.' }
    if ($PrepararSomente) { return }
    try { & (Join-Path $PSScriptRoot 'Signal-Orbita.ps1') } catch { Write-Output $_.Exception.Message }
    $electron = Join-Path $desktop 'node_modules\electron\dist\electron.exe'
    Start-Process -FilePath $electron -WorkingDirectory $desktop -ArgumentList ('"' + $desktop + '"')
} finally { Pop-Location }
