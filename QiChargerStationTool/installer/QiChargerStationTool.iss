; QiChargerStationTool 安装包脚本（Inno Setup 6）
; 前置：先执行 publish.ps1 生成 publish\QiChargerStationTool-Portable
; 编译：用 Inno Setup Compiler 打开本文件 → Compile
; 产出：publish\QiChargerStationTool-Setup.exe

#define MyAppName "QiChargerStationTool"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Qi Charger"
#define MyAppExeName "QiChargerStationTool.exe"

[Setup]
AppId={{A7C3E2F1-9B4D-4E6A-8C2F-1D5E9A0B7C41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\publish
OutputBaseFilename=QiChargerStationTool-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
DisableProgramGroupPage=yes
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"

[Files]
; publish.ps1 输出的绿色目录
Source: "..\publish\QiChargerStationTool-Portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 可选：检测 .NET 8 Desktop Runtime（self-contained 包不需要）
// function InitializeSetup(): Boolean;
// begin
//   Result := True;
// end;
