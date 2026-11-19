param([string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$project = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\WristMusic\WristMusic.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
if (-not $InstallerPath) { $InstallerPath = Join-Path $projectRoot "artifacts\release\WristMusic-$version-win-x64-setup.exe" }
if (-not (Test-Path -LiteralPath $InstallerPath)) { throw 'Build the installer first.' }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\cn.xingmengyu.wristmusic_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'A real installation already exists; validation will not overwrite it.' }
try { $running = [Threading.Mutex]::OpenExisting('Local\XingMengYu.WristMusic.Desktop'); $running.Dispose(); throw '请先关闭腕间音乐，再执行安装包验证。' } catch [Threading.WaitHandleCannotBeOpenedException] { }

$testRoot = Join-Path $projectRoot ('.tools\installer-validation\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$target = [IO.Path]::GetFullPath((Join-Path $testRoot 'app'))
if (-not $target.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid validation target' }
$testGroup = 'XingMengYu.Setup.Validation.' + [Guid]::NewGuid().ToString('N')
$groupPath = Join-Path ([Environment]::GetFolderPath('Programs')) $testGroup
$settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WristMusic\settings.json'
$settingsHashBefore = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } else { $null }
$payloadPath = Join-Path $projectRoot 'artifacts\WristMusic-win-x64'
$uninstaller = Join-Path $target 'unins000.exe'
$report = [ordered]@{ version=$version; target=$target; success=$false }

function Invoke-ValidationBinary([string]$binary, [string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($binary)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $testRoot
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(1000)) {
            if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill($true); throw "Validation process timed out: $binary" }
        }
        if ($process.ExitCode -ne 0) { throw "Validation process failed with code $($process.ExitCode): $binary" }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Assert-InstalledLocation {
    $entry = Get-ItemProperty -LiteralPath $uninstallKey
    if ([IO.Path]::GetFullPath($entry.InstallLocation).TrimEnd('\') -ne $target.TrimEnd('\')) { throw 'Installer recorded an unexpected location' }
    if (-not $target.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall target escaped the workspace' }
    return $entry
}

try {
    Write-Output 'Installing into the workspace validation directory...'
    $installArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/TASKS=',('/DIR=' + $target),('/GROUP=' + $testGroup),('/LOG=' + (Join-Path $testRoot 'install.log')))
    $report.installExitCode = Invoke-ValidationBinary $InstallerPath $installArguments
    $entry = Assert-InstalledLocation
    if ($entry.DisplayVersion -ne $version -or $entry.Publisher -ne '星梦屿工作室') { throw 'Installed version or publisher does not match' }
    $report.registeredVersion = $entry.DisplayVersion
    $files = Get-ChildItem -LiteralPath $payloadPath -Recurse -File
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payloadPath, $file.FullName)
        $installed = Join-Path $target $relative
        if (-not (Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) { throw "Installed payload mismatch: $relative" }
    }
    $report.matchingPayloadFiles = $files.Count
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path $groupPath '腕间音乐.lnk'
    if (-not (Test-Path -LiteralPath $shortcutPath)) { throw "Start menu shortcut was not created in the requested group: $shortcutPath" }
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ($shortcut.TargetPath -ne (Join-Path $target 'WristMusic.exe') -or $shortcut.WorkingDirectory.TrimEnd('\') -ne $target.TrimEnd('\')) { throw 'Start menu shortcut target is incorrect' }
    $report.startMenuShortcutVerified = $true
    Write-Output 'Running the installed executable self-test...'
    $report.installedAppExitCode = Invoke-ValidationBinary (Join-Path $target 'WristMusic.exe') @('--self-test')
    $appReport = Get-Content -LiteralPath (Join-Path $testRoot 'artifacts\validation\results.json') -Raw | ConvertFrom-Json
    if (-not $appReport.success) { throw $appReport.error }
    $report.installedAppPassedChecks = @($appReport.results | Where-Object { $_.passed }).Count

    Write-Output 'Checking repair installation and user-file preservation...'
    $note = Join-Path $target 'validation-user-note.txt'
    [IO.File]::WriteAllText($note, 'This user-created file must survive repair and uninstall.')
    [IO.File]::WriteAllText((Join-Path $target 'WristMusic.dll'), 'repair validation')
    $report.repairExitCode = Invoke-ValidationBinary $InstallerPath ($installArguments | ForEach-Object { if ($_ -like '/LOG=*') { '/LOG=' + (Join-Path $testRoot 'repair.log') } else { $_ } })
    Assert-InstalledLocation | Out-Null
    if ((Get-FileHash -LiteralPath (Join-Path $target 'WristMusic.dll')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $payloadPath 'WristMusic.dll')).Hash -or -not (Test-Path -LiteralPath $note)) { throw 'Repair did not restore the payload or preserve the user file' }
    $report.repairVerified = $true

    Write-Output 'Uninstalling the validation copy...'
    Assert-InstalledLocation | Out-Null
    $report.uninstallExitCode = Invoke-ValidationBinary $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $testRoot 'uninstall.log')))
    $report.uninstallEntryRemoved = -not (Test-Path -LiteralPath $uninstallKey)
    $report.programRemoved = -not (Test-Path -LiteralPath (Join-Path $target 'WristMusic.exe'))
    $report.shortcutsRemoved = -not (Test-Path -LiteralPath (Join-Path $groupPath '腕间音乐.lnk'))
    $report.userFilePreserved = Test-Path -LiteralPath $note
    $settingsHashAfter = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } else { $null }
    $report.preferencesUnchanged = $settingsHashBefore -eq $settingsHashAfter
    if (-not ($report.uninstallEntryRemoved -and $report.programRemoved -and $report.shortcutsRemoved -and $report.userFilePreserved -and $report.preferencesUnchanged)) { throw 'Uninstall validation did not satisfy the cleanup or preservation checks' }
    $report.success = $true
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    if (Test-Path -LiteralPath $uninstallKey) {
        $remaining = Get-ItemProperty -LiteralPath $uninstallKey
        if ([IO.Path]::GetFullPath($remaining.InstallLocation).TrimEnd('\') -eq $target.TrimEnd('\') -and (Test-Path -LiteralPath $uninstaller)) {
            Invoke-ValidationBinary $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $testRoot 'cleanup.log'))) | Out-Null
        }
    }
    $reportPath = Join-Path $projectRoot 'artifacts\release\installer-validation.json'
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $report | ConvertTo-Json -Depth 5 | Write-Output
}
