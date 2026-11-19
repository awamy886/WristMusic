# Run with Windows PowerShell 5.1 (the same host as the updater).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $repo 'src/WristMusic/Services/ApplyUpdate.ps1'
$testDir = Join-Path $repo ('artifacts/validation/update-' + [guid]::NewGuid().ToString('N'))
$rootDir = [IO.Directory]::CreateDirectory((Join-Path $testDir 'portable app')).FullName
$source = [IO.Directory]::CreateDirectory((Join-Path $testDir 'src/WristMusic-win-x64')).FullName
$exe = Join-Path $source 'WristMusic.exe'
# Stub records its startup arguments instead of opening an app or installing anything.
Add-Type -OutputAssembly $exe -OutputType WindowsApplication -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public static class Fixture {
    public static void Main(string[] args) {
        if (args.Length == 2 && args[0] == "wait") {
            using (var ev = EventWaitHandle.OpenExisting(args[1])) ev.WaitOne(30000);
        } else {
            File.WriteAllLines(System.Reflection.Assembly.GetExecutingAssembly().Location + ".args", args);
        }
    }
}
'@
foreach ($name in 'WristMusic.dll','WristMusic.deps.json','coreclr.dll') {
    [IO.File]::WriteAllText((Join-Path $source $name), 'new')
    [IO.File]::WriteAllText((Join-Path $rootDir $name), 'old')
}
[IO.File]::Copy($exe, (Join-Path $rootDir 'WristMusic.exe'))
[IO.File]::WriteAllText((Join-Path $rootDir 'keep.txt'), 'personal')
$zip = Join-Path $testDir 'update.zip'
$setup = Join-Path $testDir 'setup.exe'
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $source), $zip)
[IO.File]::Copy($exe, $setup)

function Run-Worker($asset, $digest, [switch]$installed) {
    $parentSignal = 'Local\WristTest.' + [guid]::NewGuid()
    $readyName = 'Local\WristTest.' + [guid]::NewGuid()
    $finish = [Threading.EventWaitHandle]::new($false, 'ManualReset', $parentSignal)
    $readyEvent = [Threading.EventWaitHandle]::new($false, 'ManualReset', $readyName)
    $parent = Start-Process -FilePath $exe -ArgumentList "wait $parentSignal" -WindowStyle Hidden -PassThru
    $argsLine = '-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "{0}" -Root "{1}" -Package "{2}" -Hash {3} -ParentId {4} -Ready {5} -SteamVr' -f $helper,$rootDir,$asset,$digest,$parent.Id,$readyName
    if ($installed) { $argsLine += ' -Setup' }
    [IO.File]::WriteAllText((Join-Path $testDir 'pending.json'), '{}')
    $worker = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $argsLine -WindowStyle Hidden -PassThru
    try {
        if (!$readyEvent.WaitOne(15000)) { throw 'Update handoff failed' }
        [void]$finish.Set()
        if (!$worker.WaitForExit(30000)) { throw 'Update worker timeout' }
        if (Test-Path (Join-Path $testDir 'pending.json')) { throw 'Pending marker was not consumed' }
    } finally { [void]$finish.Set(); $finish.Dispose(); $readyEvent.Dispose(); $parent.Dispose(); $worker.Dispose() }
}
Run-Worker $zip ('0' * 64)
$dll = Join-Path $rootDir 'WristMusic.dll'
if ([IO.File]::ReadAllText($dll) -ne 'old' -or !(Test-Path (Join-Path $testDir 'error.txt'))) { throw 'Invalid checksum accepted' }
$locked = [IO.File]::Open($dll, 'Open', 'Read', 'None')
try { Run-Worker $zip (Get-FileHash $zip).Hash } finally { $locked.Dispose() }
foreach ($name in 'WristMusic.dll','WristMusic.deps.json','coreclr.dll') {
    if ([IO.File]::ReadAllText((Join-Path $rootDir $name)) -ne 'old') { throw 'Rollback failed' }
}
Run-Worker $zip (Get-FileHash $zip).Hash
if ([IO.File]::ReadAllText($dll) -ne 'new' -or (Test-Path $zip) -or
    [IO.File]::ReadAllText((Join-Path $rootDir 'keep.txt')) -ne 'personal') { throw 'Portable update failed' }
Run-Worker $setup (Get-FileHash $setup).Hash -installed
$received = [IO.File]::ReadAllLines($setup + '.args')
if ($received -notcontains "/DIR=$rootDir" -or $received -notcontains '/VERYSILENT' -or
    $received -notcontains '/CURRENTUSER') { throw 'Incorrect installer arguments' }
if ((Get-Content (Join-Path $rootDir 'WristMusic.exe.args')) -ne '--steamvr-startup') { throw 'Lost SteamVR startup mode' }
'Update worker checks passed: checksum, rollback, portable replacement, installer handoff and relaunch.'
