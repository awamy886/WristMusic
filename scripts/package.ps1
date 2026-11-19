$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\WristMusic\WristMusic.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
$packagePath = Join-Path $projectRoot 'artifacts\WristMusic-win-x64'
$developerFiles = @('开始使用.txt', '使用说明.md', 'validation-results.json', 'netease-bridge-validation.json', 'pico-connect-driver-reference.json', 'WristMusic.pdb')
# A publish into the existing directory can retain files shipped by an older build.
foreach ($name in $developerFiles) {
    $path = Join-Path $packagePath $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
foreach ($required in @('WristMusic.exe', 'openvr_api.dll', 'coreclr.dll', 'PresentationFramework.dll', 'Input/actions.json', 'Input/bindings_oculus_touch.json', 'Input/bindings_pico_controller.json', 'Input/bindings_knuckles.json', 'Input/bindings_vive_controller.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $required))) { throw "Portable build is incomplete: $required" }
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packagePath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $packagePath -Force
$licenseOutput = Join-Path $packagePath 'licenses'
New-Item -ItemType Directory -Force -Path $licenseOutput | Out-Null
$packageCache = Join-Path $projectRoot '.tools\packages'
$runtimePack = Get-ChildItem (Join-Path $packageCache 'microsoft.netcore.app.runtime.win-x64') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$desktopPack = Get-ChildItem (Join-Path $packageCache 'microsoft.windowsdesktop.app.runtime.win-x64') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
Copy-Item -LiteralPath (Join-Path $runtimePack.FullName 'LICENSE.TXT') -Destination (Join-Path $licenseOutput 'DOTNET-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $runtimePack.FullName 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseOutput 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
Copy-Item -LiteralPath (Join-Path $desktopPack.FullName 'LICENSE') -Destination (Join-Path $licenseOutput 'WPF-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $packageCache 'interop.uiautomationclient\10.19041.0\LICENSE.txt') -Destination (Join-Path $licenseOutput 'UIAUTOMATION-INTEROP-LICENSE.txt') -Force
Compress-Archive -Path $packagePath -DestinationPath (Join-Path $projectRoot 'artifacts\WristMusic-win-x64.zip') -Force
Get-FileHash -LiteralPath (Join-Path $projectRoot 'artifacts\WristMusic-win-x64.zip') -Algorithm SHA256 | Select-Object Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts\WristMusic-win-x64.sha256.json') -Encoding utf8
$releasePath = Join-Path $projectRoot 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releasePath | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\WristMusic-win-x64.zip') -Destination (Join-Path $releasePath "WristMusic-$version-win-x64-portable.zip") -Force
