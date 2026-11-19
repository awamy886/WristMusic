$projectRoot = Split-Path -Parent $PSScriptRoot
$portable = Join-Path $projectRoot 'artifacts\WristMusic-win-x64\WristMusic.exe'
$arguments = @()
if (Test-Path -LiteralPath $portable) { & $portable @arguments; exit }
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$binary = Join-Path $projectRoot 'src\WristMusic\bin\Release\net10.0-windows10.0.19041.0\WristMusic.dll'
if (-not (Test-Path -LiteralPath $binary)) { throw '请先运行 scripts\build.ps1' }
& $sdk $binary @arguments
