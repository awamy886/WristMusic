param([string]$Root, [string]$Package, [string]$Hash, [int]$ParentId,
      [string]$Ready, [switch]$Setup, [switch]$SteamVr)
$ErrorActionPreference = 'Stop'

function Install-Portable([string]$archive, [string]$destination, [string]$work) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $work)
    $payload = Join-Path $work 'WristMusic-win-x64'
    foreach ($required in 'WristMusic.exe','WristMusic.dll','WristMusic.deps.json','coreclr.dll') {
        if (![IO.File]::Exists((Join-Path $payload $required))) { throw 'Incomplete portable package' }
    }
    $changes = [Collections.Generic.List[object]]::new()
    $suffix = '.wm-' + [guid]::NewGuid().ToString('N')
    try {
        foreach ($file in [IO.Directory]::EnumerateFiles($payload, '*', 'AllDirectories')) {
            $relative = $file.Substring($payload.Length + 1)
            $target = Join-Path $destination $relative
            # Do not follow a user-created junction or link when replacing a file.
            for ($node = $target; $node -ne $destination; $node = [IO.Path]::GetDirectoryName($node)) {
                if ((Test-Path -LiteralPath $node) -and
                    ([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked update target' }
            }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $temp = $target + $suffix + '.new'
            $backup = $null
            try {
                [IO.File]::Copy($file, $temp)
                if ([IO.File]::Exists($target)) {
                    $backup = $target + $suffix + '.old'
                    [IO.File]::Replace($temp, $target, $backup)
                } else { [IO.File]::Move($temp, $target) }
                $changes.Add(@{ Target = $target; Backup = $backup })
            } finally { [IO.File]::Delete($temp) }
        }
    } catch {
        for ($i = $changes.Count - 1; $i -ge 0; $i--) {
            $c = $changes[$i]
            if ($c.Backup) { [IO.File]::Copy($c.Backup, $c.Target, $true); [IO.File]::Delete($c.Backup) }
            else { [IO.File]::Delete($c.Target) }
        }
        throw
    }
    foreach ($c in $changes) { if ($c.Backup) { [IO.File]::Delete($c.Backup) } }
}

if ($MyInvocation.InvocationName -eq '.') { return }
$cache = [IO.Path]::GetDirectoryName($Package)
$work = Join-Path $cache ('stage-' + [guid]::NewGuid().ToString('N'))
$owned = $false
$relaunch = $false
$mutex = [Threading.Mutex]::new($false, 'Local\XingMengYu.WristMusic.Updating')
try {
    $owned = $mutex.WaitOne(0)
    if (!$owned) { return }
    $parent = [Diagnostics.Process]::GetProcessById($ParentId)
    $signal = [Threading.EventWaitHandle]::OpenExisting($Ready)
    try { [void]$signal.Set() } finally { $signal.Dispose() }
    if (!$parent.WaitForExit(30000)) { throw 'Application did not exit' }
    $relaunch = $true
    [IO.File]::Delete((Join-Path $cache 'pending.json'))
    if ((Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -ne $Hash) { throw 'Invalid package checksum' }
    if ($Setup) {
        $arguments = '/CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="' + $Root + '"'
        $installer = Start-Process -FilePath $Package -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($installer.ExitCode -ne 0) { throw "Setup exit code $($installer.ExitCode)" }
    } else { Install-Portable $Package $Root $work }
    [IO.File]::Delete($Package)
    [IO.File]::Delete((Join-Path $cache 'error.txt'))
} catch {
    [IO.File]::WriteAllText((Join-Path $cache 'error.txt'), $_.Exception.Message)
} finally {
    # work is a unique extraction directory directly beneath this installation's cache.
    if ([IO.Directory]::Exists($work) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($work)) -eq $cache) {
        try { [IO.Directory]::Delete($work, $true) } catch { }
    }
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
if ($relaunch) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $Root 'WristMusic.exe'))
    $start.WorkingDirectory = $Root
    if ($SteamVr) { $start.Arguments = '--steamvr-startup' }
    [void][Diagnostics.Process]::Start($start)
}
