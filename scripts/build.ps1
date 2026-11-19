param([switch]$Portable, [switch]$Installer, [switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\packages'
    & $sdk restore src\WristMusic\WristMusic.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    & $sdk build src\WristMusic\WristMusic.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($Test) {
        & $sdk src\WristMusic\bin\Release\net10.0-windows10.0.19041.0\WristMusic.dll --self-test --live-check
        if ($LASTEXITCODE -ne 0) { throw 'Self-test failed: artifacts\validation\results.json' }
    }
    if ($Portable -or $Installer) {
        & $sdk publish src\WristMusic\WristMusic.csproj -c Release -r win-x64 --self-contained true -o artifacts\WristMusic-win-x64 -p:PublishSingleFile=false -p:PublishTrimmed=false --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        & (Join-Path $PSScriptRoot 'package.ps1')
        if ($Installer) { & (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipPackaging }
    }
} finally { Pop-Location }
