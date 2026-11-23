#ifndef AppVersion
  #define AppVersion "6.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir SourcePath + "..\artifacts\WristMusic-win-x64"
#endif
#ifndef ReleaseDir
  #define ReleaseDir SourcePath + "..\artifacts\release"
#endif
#define AppName "腕间音乐"
#define AppExe "WristMusic.exe"

[Setup]
AppId=cn.xingmengyu.wristmusic
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=星梦屿工作室
AppComments=作者:awa星野厨のMY · 交流群:1049685082 · 本程序完全免费
DefaultDirName={localappdata}\Programs\XingMengYu\WristMusic
DefaultGroupName={#AppName}
DisableDirPage=no
DisableWelcomePage=no
DisableProgramGroupPage=no
PrivilegesRequired=lowest
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ReleaseDir}
OutputBaseFilename=WristMusic-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\WristMusic\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
WizardStyle=modern light windows11
WizardSizePercent=110,110
ShowLanguageDialog=no
Compression=lzma2/normal
SolidCompression=yes
LZMAUseSeparateProcess=yes
AppMutex=Local\XingMengYu.WristMusic.Desktop
SetupMutex=Local\XingMengYu.WristMusic.Setup
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany=星梦屿工作室
VersionInfoDescription={#AppName} 安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
WelcomeLabel1=欢迎安装 [name]
WelcomeLabel2=安装向导将把 [name/ver] 安装到你的电脑。%n%n星梦屿工作室%n作者:awa星野厨のMY%n交流群:1049685082%n本程序完全免费·o(*≧▽≦)ツ┏━┓%n%n单击“下一步”继续。
BeveledLabel=星梦屿工作室

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "开始使用.txt,使用说明.md,validation-results.json,netease-bridge-validation.json,pico-connect-driver-reference.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\开始使用.txt"
Type: files; Name: "{app}\使用说明.md"
Type: files; Name: "{app}\validation-results.json"
Type: files; Name: "{app}\netease-bridge-validation.json"
Type: files; Name: "{app}\pico-connect-driver-reference.json"
Type: files; Name: "{app}\WristMusic.pdb"

[INI]
Filename: "{app}\WristMusic.install.ini"; Section: "Installation"; Key: "Channel"; String: "setup"; Flags: uninsdeleteentry uninsdeletesectionifempty

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\卸载{#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "运行{#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedSteamVrStartup"

[UninstallDelete]
Type: files; Name: "{app}\WristMusic.install.ini"
Type: dirifempty; Name: "{app}"
