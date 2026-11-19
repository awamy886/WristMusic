$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsPath = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
$installerPath = Join-Path $toolsPath 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
& $installerPath -Channel 10.0 -InstallDir (Join-Path $toolsPath 'dotnet') -NoPath
if (-not (Test-Path -LiteralPath (Join-Path $toolsPath 'dotnet\dotnet.exe'))) { throw '.NET SDK download failed' }
