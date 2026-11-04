; ============================================================
; ClipMate 安装脚本（Inno Setup 6）
; 功能：安装向导 + 桌面快捷方式 + 开机自启选项 + 卸载入口
; ============================================================

#define MyAppName "ClipMate"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ClipMate"
#define MyAppExeName "ClipMate.exe"

[Setup]
AppId={{7A3F9C21-6E4D-4B8A-9C5E-2D1B0A8F4E7C}
AppName={#MyAppName}
; 关键：显式指定 AppVerName 为纯名称，避免控制面板显示成 "ClipMate 版本 1.0.0"
; （Inno Setup 默认 AppVerName = AppName + "版本" + AppVersion）
AppVerName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 安装到常规系统目录 Program Files（默认 C:\Program Files (x86) 或 64 位系统的对应位置）
; 需要管理员权限，因此 PrivilegesRequired 设为 admin，安装时会弹一次 UAC 提权
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\publish\installer
OutputBaseFilename=ClipMate-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 程序自带图标
SetupIconFile=..\assets\ClipMate.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; 中文界面
LicenseFile=..\docs\LICENSE.txt
; 程序单实例互斥量：卸载/安装时若程序仍在运行，会提示先关闭，避免删除失败或数据残留
AppMutex=ClipMate.SingleInstance.8D2E4A1C

[Languages]
; 简体中文（社区维护的 ChineseSimplified.isl，已放到 Inno Setup Languages 目录）
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自启动"; GroupDescription: "附加任务："

[Files]
; 便携版目录里的单文件程序
Source: "..\publish\portable\ClipMate.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 公共桌面：admin 模式下 {autodesktop} 会指向管理员账户桌面，必须用 {commondesktop}
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; 开机自启（HKLM 机器级，配合 admin 安装；admin 模式下写 HKCU 会落到管理员账户，当前用户不生效）
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ClipMate"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清除用户数据目录（配置 config.json + 历史数据库 clips.db），避免重装后配置残留
Type: filesandordirs; Name: "{localappdata}\ClipMate"
