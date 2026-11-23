# Run with Windows PowerShell 5.1 after a Release build (no network required).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $repo 'src/WristMusic/obj/Release/helper/WristUpdater.exe'
$testDir = Join-Path $repo ('artifacts/validation/update-' + [guid]::NewGuid().ToString('N'))
$rootDir = [IO.Directory]::CreateDirectory((Join-Path $testDir ('portable app ' + [char]0x661f))).FullName
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
foreach ($name in 'WristMusic.deps.json','coreclr.dll') {
    [IO.File]::WriteAllText((Join-Path $source $name), 'new')
    [IO.File]::WriteAllText((Join-Path $rootDir $name), 'old')
}
$fixtureVersion = '5.0.1'
Add-Type -OutputAssembly (Join-Path $source 'WristMusic.dll') -TypeDefinition '[assembly:System.Reflection.AssemblyVersion("5.0.1.0")] public class Payload { }'
[IO.File]::WriteAllText((Join-Path $rootDir 'WristMusic.dll'), 'old')
[IO.File]::Copy($exe, (Join-Path $rootDir 'WristMusic.exe'))
[IO.File]::WriteAllText((Join-Path $rootDir 'keep.txt'), 'personal')
$zip = Join-Path $testDir 'update.zip'
$setup = Join-Path $testDir 'setup.exe'
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $source), $zip)
[IO.File]::Copy($exe, $setup)

function Run-Worker($asset, $digest, [switch]$installed, [switch]$failure, $version = $fixtureVersion) {
    $parentSignal = 'Local\WristTest.' + [guid]::NewGuid()
    $readyName = 'Local\WristTest.' + [guid]::NewGuid()
    $finish = [Threading.EventWaitHandle]::new($false, 'ManualReset', $parentSignal)
    $readyEvent = [Threading.EventWaitHandle]::new($false, 'ManualReset', $readyName)
    $proceed = [Threading.EventWaitHandle]::new($false, 'ManualReset', $readyName + '.proceed')
    $parent = Start-Process -FilePath $exe -ArgumentList "wait $parentSignal" -WindowStyle Hidden -PassThru
    $kind = if ($installed) { 'setup' } else { 'portable' }
    $argsLine = '"{0}" "{1}" {2} {3} {4} {5} {6} steamvr' -f $rootDir,$asset,$digest,$parent.Id,$readyName,$version,$kind
    $receipt = Join-Path $rootDir 'WristMusic.exe.args'
    [IO.File]::Delete($receipt)
    [IO.File]::WriteAllText((Join-Path $testDir 'pending.json'), '{}')
    $worker = Start-Process -FilePath $helper -ArgumentList $argsLine -WindowStyle Hidden -PassThru
    try {
        if (!$readyEvent.WaitOne(15000)) { throw 'Update handoff failed' }
        [void]$proceed.Set()
        [void]$finish.Set()
        if (!$worker.WaitForExit(30000)) { throw 'Update worker timeout' }
        if ((Test-Path (Join-Path $testDir 'pending.json')) -ne $failure.IsPresent) { throw 'Unexpected pending marker after update' }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $receipt) -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 50 }
        $flags = [IO.File]::ReadAllLines($receipt)
        if ($flags -notcontains '--steamvr-startup' -or ($flags -contains '--update-retry-later') -ne $failure.IsPresent) { throw 'Unexpected relaunch mode' }
    } finally { [void]$finish.Set(); $finish.Dispose(); $readyEvent.Dispose(); $proceed.Dispose(); $parent.Dispose(); $worker.Dispose() }
}
$badZip = Join-Path $testDir 'damaged.zip'
[IO.File]::Copy($zip, $badZip)
Run-Worker $badZip ('0' * 64) -failure
if (Test-Path $badZip) { throw 'Invalid package retained' }
$dll = Join-Path $rootDir 'WristMusic.dll'
if ([IO.File]::ReadAllText($dll) -ne 'old' -or !(Test-Path (Join-Path $testDir 'error.txt'))) { throw 'Invalid checksum accepted' }
$locked = [IO.File]::Open($dll, 'Open', 'Read', 'None')
try { Run-Worker $zip (Get-FileHash $zip).Hash -failure } finally { $locked.Dispose() }
if (!(Test-Path $zip)) { throw 'Valid package lost after install failure' }
foreach ($name in 'WristMusic.dll','WristMusic.deps.json','coreclr.dll') {
    if ([IO.File]::ReadAllText((Join-Path $rootDir $name)) -ne 'old') { throw 'Rollback failed' }
}
Run-Worker $zip (Get-FileHash $zip).Hash
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString(3) -ne $fixtureVersion -or (Test-Path $zip) -or
    [IO.File]::ReadAllText((Join-Path $rootDir 'keep.txt')) -ne 'personal') { throw 'Portable update failed' }
# Even a zero installer exit code must not consume the package with the old DLL in place.
Run-Worker $setup (Get-FileHash $setup).Hash -installed -version '5.0.2' -failure
if (!(Test-Path $setup)) { throw 'Package lost after a no-op installer' }
Run-Worker $setup (Get-FileHash $setup).Hash -installed
$received = [IO.File]::ReadAllLines($setup + '.args')
if ($received -notcontains "/DIR=$rootDir" -or $received -notcontains '/VERYSILENT' -or
    $received -notcontains '/CURRENTUSER') { throw 'Incorrect installer arguments' }
if ((Get-Content (Join-Path $rootDir 'WristMusic.exe.args')) -ne '--steamvr-startup') { throw 'Lost SteamVR startup mode' }
'Update worker checks passed: checksum, rollback, portable replacement, installer handoff and relaunch.'
