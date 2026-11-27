param([switch]$Preparar, [switch]$Vincular)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $workspace '.orbita-hermes'
$profile = Join-Path $cache 'perfil-orbita'
$root = Join-Path $cache 'signal-orbita'
$device = Join-Path $root 'dispositivo'
$marker = Join-Path $profile 'orbita-signal-account'
$version = '0.14.10'
$package = Join-Path $root "signal-cli-$version"
$libraries = Join-Path $package 'lib\*'
$address = 'http://127.0.0.1:8788'
$accountPattern = '^(?:\+[1-9][0-9]{6,14}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})$'

if (!$Preparar -and !$Vincular -and !(Test-Path -LiteralPath $marker)) { return }
$java = Get-Command java.exe -ErrorAction SilentlyContinue
if (!$java) { throw 'O signal-cli precisa do Java 25 ou mais recente.' }

function Install-Signal {
    $jar = Join-Path $package "lib\signal-cli-$version.jar"
    if (!(Test-Path -LiteralPath $jar)) {
        $null = New-Item -ItemType Directory -Force -Path $root
        $archive = Join-Path $root "signal-cli-$version.tar.gz"
        $sha = '706a57d9faae7525cf732936efdabf210146e3c83f9e8ae7e4e62f5946121c33'
        if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ine $sha) {
            Invoke-WebRequest -UseBasicParsing "https://github.com/AsamK/signal-cli/releases/download/v$version/signal-cli-$version.tar.gz" -OutFile $archive -TimeoutSec 180
        }
        if ((Get-FileHash -LiteralPath $archive).Hash -ine $sha) { throw 'O arquivo do signal-cli falhou na conferência de integridade.' }
        & tar.exe -xzf $archive -C $root
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $jar)) { throw 'O signal-cli não pôde ser extraído.' }
    }
    & $java.Source '--enable-native-access=ALL-UNNAMED' '-cp' $libraries 'org.asamk.signal.Main' '--version'
    if ($LASTEXITCODE) { throw 'O signal-cli não iniciou. Confira a versão do Java.' }
}

function Test-Signal {
    try {
        $query = '{"jsonrpc":"2.0","id":1,"method":"version"}'
        $check = Invoke-RestMethod -UseBasicParsing "$address/api/v1/rpc" -Method Post -Body $query -ContentType 'application/json' -TimeoutSec 2
        return $check.result.version -eq $version
    } catch { return $false }
}

function Start-Signal([string]$account) {
    if ($account -notmatch $accountPattern) { throw 'A conta Signal salva é inválida.' }
    if (Test-Signal) { return }
    if (Get-NetTCPConnection -LocalPort 8788 -State Listen -ErrorAction SilentlyContinue) { throw 'A porta local 8788 está ocupada.' }
    $arguments = @('--enable-native-access=ALL-UNNAMED', '-cp', ('"{0}"' -f $libraries), 'org.asamk.signal.Main',
        '--data-dir', ('"{0}"' -f $device), '--account', $account, 'daemon', '--http', '127.0.0.1:8788', '--no-receive-stdout')
    $server = Start-Process -FilePath $java.Source -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 35 -and !$server.HasExited; $attempt++) {
        if (Test-Signal) { return }
        Start-Sleep -Milliseconds 300
    }
    throw 'O serviço local do Signal não ficou pronto.'
}

if ($Preparar -or $Vincular) { Install-Signal }
if ($Preparar) { return }

if (!$Vincular) {
    Start-Signal ([IO.File]::ReadAllText($marker).Trim())
    return
}

$python = Join-Path $cache 'runtime-venv\Scripts\python.exe'
if (!(Test-Path -LiteralPath $python) -or !(Test-Path -LiteralPath (Join-Path $profile 'config.yaml'))) { throw 'Abra o Orbita Desktop uma vez antes de vincular o Signal.' }
if (Test-Path -LiteralPath $marker) {
    Start-Signal ([IO.File]::ReadAllText($marker).Trim())
    Write-Output 'O Signal já está vinculado. No celular, abra Anotações e mande uma mensagem.'
    return
}

$null = New-Item -ItemType Directory -Force -Path $device
$common = @('--enable-native-access=ALL-UNNAMED', '-cp', $libraries, 'org.asamk.signal.Main', '--data-dir', $device)
$accounts = & $java.Source @common '--output' 'json' 'listAccounts'
if ($LASTEXITCODE) { throw 'Não foi possível consultar o aparelho Signal.' }
$linked = @($accounts | ConvertFrom-Json | Where-Object { $_ -and ($_.number -or $_.aci) })
if ($linked.Count -eq 0) {
    Write-Output 'No celular, abra Signal, Configurações e Dispositivos vinculados para escanear o QR abaixo.'
    & $java.Source @common 'link' '-n' 'Orbita Studio'
    if ($LASTEXITCODE) { throw 'O aparelho Signal ainda não foi vinculado.' }
    $accounts = & $java.Source @common '--output' 'json' 'listAccounts'
    if ($LASTEXITCODE) { throw 'Não foi possível consultar a conta vinculada.' }
    $linked = @($accounts | ConvertFrom-Json | Where-Object { $_ -and ($_.number -or $_.aci) })
}
if ($linked.Count -ne 1) { throw 'Era esperada uma conta Signal neste dispositivo do Orbita.' }
$account = if ($linked[0].number) { $linked[0].number } else { $linked[0].aci }
Start-Signal $account

$env:HERMES_HOME = $profile
$env:ORBITA_SIGNAL_ACCOUNT = $account
$env:ORBITA_SIGNAL_HTTP = $address
@'
import os
from hermes_cli.config import load_env, save_env_value

expected = {
    "SIGNAL_HTTP_URL": os.environ["ORBITA_SIGNAL_HTTP"],
    "SIGNAL_ACCOUNT": os.environ["ORBITA_SIGNAL_ACCOUNT"],
    "SIGNAL_ALLOWED_USERS": os.environ["ORBITA_SIGNAL_ACCOUNT"],
}
for key, value in expected.items():
    save_env_value(key, value)
saved = load_env()
if any(saved.get(key) != value for key, value in expected.items()):
    raise SystemExit(1)
'@ | & $python -
if ($LASTEXITCODE) { throw 'O vínculo existe, mas o Orbita não conseguiu salvar a configuração do Signal.' }
[IO.File]::WriteAllText($marker, $account)
Write-Output 'Signal vinculado. Reabra o Orbita para aplicar a conexão e mande uma mensagem em Anotações.'
