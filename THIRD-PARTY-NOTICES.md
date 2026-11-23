# Third-party components

## OpenVR

The generated C# binding and Windows x64 native library in `src/WristMusic/ThirdParty` are from Valve's official OpenVR repository at commit `0924064316de3effbcd1acf1e309182a2deb1c05`:

- https://github.com/ValveSoftware/openvr/blob/0924064316de3effbcd1acf1e309182a2deb1c05/headers/openvr_api.cs
- https://github.com/ValveSoftware/openvr/blob/0924064316de3effbcd1acf1e309182a2deb1c05/bin/win64/openvr_api.dll
- https://github.com/ValveSoftware/openvr/blob/0924064316de3effbcd1acf1e309182a2deb1c05/LICENSE

The binding has one local addition: `#nullable disable` and a comment explaining compatibility with nullable-enabled projects. The upstream API code is otherwise unchanged.

Valve's redistribution license is included as `ThirdParty/OpenVR-LICENSE.txt` in source and binary distributions. This project is not affiliated with or endorsed by Valve, Steam, VRChat, or the music applications it can control.

## .NET runtime

Self-contained releases redistribute the Microsoft .NET and Windows Desktop runtime from official NuGet runtime packs. Applicable .NET license and notices:

- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- https://github.com/dotnet/wpf/blob/main/LICENSE.TXT

## UI Automation interop

The read-only NetEase timeline fallback uses Interop.UIAutomationClient 10.19041.0, an interface binding for Windows UI Automation, under the MIT license (Copyright (c) 2019 Roman). Its license is distributed as `licenses/UIAUTOMATION-INTEROP-LICENSE.txt`.

- https://github.com/Roemer/UIAutomation-Interop
- https://www.nuget.org/packages/Interop.UIAutomationClient/10.19041.0

The accessibility fallback is implemented in this project. The feasibility of reading NetEase's displayed time through accessibility was also documented by [now-playing-service](https://github.com/Widdit/now-playing-service/issues/39); no upstream application source is redistributed.

## Installer builder

The EXE installation package is built with Inno Setup 7.1.1 by Jordan Russell and Martijn Laan. The compiler is obtained from its official release, verified by SHA256 and its Pyrsys B.V. Authenticode signature, and is used only as a build tool. Inno Setup's built-in attribution is retained.

- https://jrsoftware.org/
- https://github.com/jrsoftware/issrc/releases/tag/is-7_1_1
- https://github.com/jrsoftware/issrc/blob/is-7_1_1/license.txt

## Optional NetEase bridge

The opt-in bridge setup downloads unmodified BetterNCM 1.3.4 (Windows x64 loader) and InfLink-rs 3.4.0 directly from their authors' GitHub releases after an explicit user action. These binaries are not bundled with WristMusic or loaded into WristMusic. Both projects publish their source and GPL-3.0 license. Existing unrelated loader versions are never overwritten. Download URLs and SHA256 values are pinned in `NeteaseBridgeSetup.cs`.

- BetterNCM source and license: https://github.com/std-microblock/chromatic/tree/1.3.4
- InfLink-rs source and license: https://github.com/apoint123/inflink-rs/tree/v3.4.0
- InfLink-rs release: https://github.com/apoint123/inflink-rs/releases/tag/v3.4.0

## Artwork

The waveform application icon, the vector moon-and-water placeholder, and the light-theme record placeholder are original project assets. Internal test track names are sample data; no song recordings or music-client logos are distributed.

## Spout2

WristCamera uses the SpoutDX receiver from Spout2, revision 2bd42d21cd1825b2fd61224d914c555e59545ab5, under the BSD 2-Clause license. Its license accompanies the binary in ThirdParty/Spout2-LICENSE.txt. Source: https://github.com/leadedge/Spout2
