; CodecLoom Windows 安装包脚本（Inno Setup 6）
; 编译：ISCC.exe installer\CodecLoom.iss /DAppVersion=1.2.3
; 前置：已按 ci.yml 同款命令把桌面程序发布到 dist\portable\

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "CodecLoom"
#define AppExe "SimdCodecsViewer.exe"

[Setup]
; 固定 AppId：保证升级/卸载识别稳定，与版本号无关
AppId={{8E3F6C2A-4B7D-4E8A-9C1F-2D0CE10CDB01}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=CodecLoom
AppPublisherURL=https://github.com/iamartinlong/CodecLoom
; 用户级安装：无需管理员权限，装到 %LOCALAPPDATA%\Programs\CodecLoom
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=CodecLoom-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayName={#AppName} {#AppVersion}
; 无数字签名证书：不打 SignedUninstaller
LicenseFile=..\LICENSE
SetupIconFile=..\assets\appicon.ico

[Languages]
; 简体中文翻译随仓库分发（Inno 官方不含；来源 kira-96/Inno-Setup-Chinese-Simplified-Translation，6.5.0+）
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 自包含桌面产物整体打包（136 MB，安装后约 400 MB 磁盘占用由 Inno 按实际计算）
Source: "..\dist\portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清理运行期生成的用户数据（设置 JSON 等）
Type: filesandordirs; Name: "{userappdata}\CodecLoom"
