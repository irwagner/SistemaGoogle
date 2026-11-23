param(
    [string]$Dotnet = 'dotnet',
    [string]$Destino = (Join-Path $PSScriptRoot '../entrega'),
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Versao = '0.1.0',
    [switch]$SomenteCentral,
    [switch]$AtualizarLocks
)

$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $PSScriptRoot
$previousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$releaseVersion = $Versao
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destino)
$sdkRoot = Split-Path -Parent (Get-Command $Dotnet -ErrorAction Stop).Source
$null = New-Item -ItemType Directory -Force -Path $destination
Push-Location -LiteralPath $base
try {
    if ((& $Dotnet --version) -ne '10.0.404') { throw 'Instale o SDK .NET 10.0.404 ou informe -Dotnet.' }
    $targets = @(
        @{ Name = 'Central'; Project = 'src/Central/Central.csproj' },
        @{ Name = 'Orcamentos'; Project = 'pilotos/Orcamentos/Orcamentos.csproj' }
    )
    if ($SomenteCentral) { $targets = @($targets[0]) }
    foreach ($target in $targets) {
        $name = "$($target.Name)-$releaseVersion-win-x64"
        $package = Join-Path $destination $name
        $archive = "$package.zip"
        if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $archive)) {
            throw "Use um destino novo. Já existe $package ou seu ZIP."
        }
        $mode = if ($AtualizarLocks) { '--force-evaluate' } else { '--locked-mode' }
        & $Dotnet restore $target.Project --runtime win-x64 $mode -p:SelfContained=true
        if ($LASTEXITCODE) { throw 'Restore falhou.' }
        & $Dotnet publish $target.Project -c Release -r win-x64 --self-contained --no-restore "-p:Version=$releaseVersion" -o (Join-Path $package 'app')
        if ($LASTEXITCODE) { throw 'Publish falhou.' }
        $app = Join-Path $package 'app'
        $hashes = Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($app.Length + 1).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        [ordered]@{ version = $releaseVersion; target = 'win-x64'; files = @($hashes) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'release.json') -Encoding utf8
        if ($target.Name -eq 'Central') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Instalar.ps1'),(Join-Path $PSScriptRoot 'Abrir-Central.cmd') -Destination $package
            Copy-Item -LiteralPath (Join-Path $base 'README.md') -Destination $package
            $guides = New-Item -ItemType Directory -Path (Join-Path $package 'docs')
            Copy-Item -LiteralPath (Join-Path $base 'docs/OPERACAO.md'),(Join-Path $base 'docs/MARCOS.md'),(Join-Path $base 'docs/EVIDENCIAS.md') -Destination $guides.FullName
        } else {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Abrir-Orcamentos.cmd') -Destination $package
        }
        Copy-Item -LiteralPath (Join-Path $base 'docs/DEPENDENCIAS.md') -Destination $package
        $licenses = New-Item -ItemType Directory -Path (Join-Path $package 'licencas')
        Copy-Item -LiteralPath (Join-Path $sdkRoot 'LICENSE.txt'),(Join-Path $sdkRoot 'ThirdPartyNotices.txt') -Destination $licenses.FullName
        Copy-Item -LiteralPath (Join-Path $base 'docs/Apache-2.0.txt') -Destination $licenses.FullName
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($item in (Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName)) {
                $relative = $item.FullName.Substring($package.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2026, 11, 23, 0, 0, 0, [TimeSpan]::Zero)
                $inputFile = [IO.File]::OpenRead($item.FullName)
                $zipStream = $entry.Open()
                try { $inputFile.CopyTo($zipStream) }
                finally { $zipStream.Dispose(); $inputFile.Dispose() }
            }
        } finally { $zip.Dispose() }
        "$((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path -Leaf $archive)" |
            Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    }
} finally {
    Pop-Location
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetry
}
