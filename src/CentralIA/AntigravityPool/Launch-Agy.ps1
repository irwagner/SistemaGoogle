param(
    [Parameter(Mandatory)][ValidateSet('Login','Project')][string]$Mode,
    [int]$Port,
    [switch]$ContinueConversation
)

$ErrorActionPreference = 'Stop'
$agyPath = $env:AGY_POOL_AGY_PATH
if (-not $agyPath -or -not (Test-Path -LiteralPath $agyPath -PathType Leaf)) {
    throw 'Instale o Antigravity CLI oficial antes de usar o pool.'
}

if ($Mode -eq 'Login') {
    $loginPath = Join-Path $env:CENTRALIA_POOL_DATA 'login'
    New-Item -ItemType Directory -Path $loginPath -Force | Out-Null
    Set-Location -LiteralPath $loginPath
    Remove-Item Env:CLOUD_CODE_URL -ErrorAction SilentlyContinue
    Write-Host 'Entre com a conta Google que deseja adicionar na Central IA.'
    & $agyPath
    exit $LASTEXITCODE
}

Set-Location -LiteralPath $env:CENTRALIA_AGY_WORKSPACE
$env:CLOUD_CODE_URL = "http://127.0.0.1:$Port"
if ($ContinueConversation) { & $agyPath --continue }
else { & $agyPath }
exit $LASTEXITCODE
