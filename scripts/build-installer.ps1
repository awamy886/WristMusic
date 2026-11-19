param([string]$CompilerPath, [switch]$SkipPackaging)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $CompilerPath) { $CompilerPath = Join-Path $projectRoot '.tools\inno\ISCC.exe' }
if (-not (Test-Path -LiteralPath $CompilerPath)) {
    $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($installedCompiler) { $CompilerPath = $installedCompiler.Source } else { throw '请安装 Inno Setup 并将 ISCC.exe 加入 PATH，或传入 -CompilerPath。' }
}
[xml]$project = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\WristMusic\WristMusic.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
$payloadPath = Join-Path $projectRoot 'artifacts\WristMusic-win-x64'
$assemblyPath = Join-Path $payloadPath 'WristMusic.dll'
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw '请先发布 Windows x64 自包含程序。' }
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).ProductVersion
if ($binaryVersion -ne $version) { throw "发布目录版本 $binaryVersion 与源码版本 $version 不一致，请重新发布。" }
if (-not $SkipPackaging) { & (Join-Path $PSScriptRoot 'package.ps1') }
$releasePath = Join-Path $projectRoot 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releasePath | Out-Null
& $CompilerPath '/Qp' "/DAppVersion=$version" "/DPayloadDir=$payloadPath" "/DReleaseDir=$releasePath" (Join-Path $projectRoot 'installer\WristMusic.iss')
if ($LASTEXITCODE -ne 0) { throw 'EXE installer compilation failed' }
$releaseFiles = @(
    (Join-Path $releasePath "WristMusic-$version-win-x64-portable.zip"),
    (Join-Path $releasePath "WristMusic-$version-win-x64-setup.exe")
)
foreach ($releaseFile in $releaseFiles) { if (-not (Test-Path -LiteralPath $releaseFile)) { throw "Release file missing: $releaseFile" } }
$hashLines = foreach ($releaseFile in $releaseFiles) { (Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($releaseFile) }
[IO.File]::WriteAllLines((Join-Path $releasePath 'SHA256SUMS.txt'), $hashLines, [Text.UTF8Encoding]::new($false))
$releaseFiles | ForEach-Object { Get-Item -LiteralPath $_ } | Select-Object Name,Length,LastWriteTime
