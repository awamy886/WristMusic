param([string]$Sdk, [string]$Compiler = 'C:\msys64\mingw64\bin\g++.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Official Spout2 revision used for the bundled x64 recorder.
$revision = '2bd42d21cd1825b2fd61224d914c555e59545ab5'
if (!$Sdk) { $Sdk = Join-Path $root '.tools/spout-sdk' }
if (!(Test-Path (Join-Path $Sdk 'SPOUTSDK'))) { throw "Provide -Sdk pointing to leadedge/Spout2 at $revision." }
$gl = Join-Path $Sdk 'SPOUTSDK/SpoutGL'
$dx = Join-Path $Sdk 'SPOUTSDK/SpoutDirectX/SpoutDX'
$obj = New-Item -ItemType Directory -Force (Join-Path $root '.tools/camera-objects')
$env:PATH = (Split-Path -Parent $Compiler) + ';' + $env:PATH
$flags = @('-std=c++17','-O2','-msse4.1','-DSPOUT_BUILD_STATIC','-DNOMINMAX',"-I$gl","-I$dx")
$files = @(Join-Path $dx 'SpoutDX.cpp')
foreach ($name in 'SpoutCopy','SpoutDirectX','SpoutFrameCount','SpoutSenderNames','SpoutSharedMemory','SpoutUtils') {
    $files += Join-Path $gl ($name + '.cpp')
}
$objects = foreach ($file in $files) {
    $output = Join-Path $obj ([IO.Path]::GetFileNameWithoutExtension($file) + '.o')
    & $Compiler @flags -c $file -o $output
    if ($LASTEXITCODE) { throw "Compilation failed: $file" }
    $output
}
$target = Join-Path $root 'src/WristMusic/ThirdParty/WristCamera.exe'
& $Compiler @flags -municode -static -s (Join-Path $root 'src/CameraRecorder/Recorder.cpp') @objects -o $target -ld3d11 -ldxgi -ld3dcompiler -lmmdevapi -lmfreadwrite -lmfplat -lmfuuid -lole32 -luuid -lwinmm -lpsapi -lversion -lcomctl32 -lgdi32 -loleaut32 -lopengl32 -lshlwapi -ladvapi32
if ($LASTEXITCODE) { throw 'Recorder link failed' }
