param(
    [Parameter(Mandatory)][ValidateSet('Login','Project')][string]$Mode,
    [string]$ProjectPath,
    [int]$Port = 18454,
    [switch]$ContinueConversation
)

$ErrorActionPreference = 'Stop'
$pwshPath = Join-Path $PSHOME 'pwsh.exe'
$launchScript = Join-Path $PSScriptRoot 'Launch-Agy.ps1'
$argsForWindow = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', ('"' + $launchScript + '"'), '-Mode', $Mode
)
if ($Mode -eq 'Project') {
    $env:CENTRALIA_AGY_WORKSPACE = $ProjectPath
    $argsForWindow += @('-Port', [string]$Port)
    if ($ContinueConversation) { $argsForWindow += '-ContinueConversation' }
}
$workingDirectory = if ($Mode -eq 'Project') { $ProjectPath } else { $PSScriptRoot }
$process = Start-Process -FilePath $pwshPath -ArgumentList $argsForWindow `
    -WorkingDirectory $workingDirectory -WindowStyle Normal -PassThru
Write-Output $process.Id
