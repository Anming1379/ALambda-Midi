; ALambda Midi 安装脚本（framework-dependent 版）
; .NET 10 桌面运行时不会打包进安装目录，安装阶段由内置脚本检测并自动安装。

#define MyAppName "ALambda Midi"
#define MyAppVersion "2.3"
#define MyAppPublisher "Anming_cat"
#define MyAppURL "https://anmingcat.pages.dev/"
#define MyAppExeName "ALambdaMidi.exe"

[Setup]
AppId={{B8A3D2E1-4F5C-4A7B-9E2D-1A3F5C7B9D2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=ALambdaMidi_Setup_v{#MyAppVersion}
SetupIconFile=Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion=2.3.0.0
MinVersion=10.0.17763
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
; framework-dependent 发布目录：exe、dll、deps.json、runtimeconfig.json 和 Assets\Midi 等
Source: "dist\publish-framework\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; .NET 运行时安装助手（安装时使用，也保留在安装目录方便用户手动运行）
Source: "Assets\install-dotnet\install-dotnet.cmd"; DestDir: "{app}\install-dotnet"; Flags: ignoreversion
Source: "Assets\install-dotnet\install-dotnet.ps1"; DestDir: "{app}\install-dotnet"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 先运行 .NET 助手；助手会自行检测，已安装时直接跳过，缺失时自动下载安装
Filename: "{app}\install-dotnet\install-dotnet.cmd"; Parameters: "/quiet"; StatusMsg: "正在检测 .NET 10 桌面运行时（缺失时会自动下载安装）..."; Flags: runhidden waituntilterminated
; 确认运行时可用后再启动程序
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Classes\ALambdaMidi.Document"; ValueType: string; ValueName: ""; ValueData: "MIDI 文件"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\ALambdaMidi.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\ALambdaMidi.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.mid\OpenWithProgids"; ValueType: none; ValueName: "ALambdaMidi.Document"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.midi\OpenWithProgids"; ValueType: none; ValueName: "ALambdaMidi.Document"; Flags: uninsdeletevalue

[Code]
function HasNet10Under(const BaseDir: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(BaseDir) then Exit;

  if FindFirst(AddBackslash(BaseDir) + '10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and 16) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsDotNet10DesktopInstalled: Boolean;
begin
  Result :=
    HasNet10Under(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasNet10Under(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function NeedsDotNet: Boolean;
begin
  Result := not IsDotNet10DesktopInstalled;
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  if NeedsDotNet then
  begin
    MsgBox(
      '本安装包不包含 .NET 运行时，需要系统已安装 .NET 10 桌面运行时。' + #13#10 + #13#10 +
      '安装程序会在复制文件后自动检测，并调用内置脚本下载安装（约 60 MB，需要联网）。' + #13#10 +
      '安装 .NET 时需要管理员权限；如果失败，脚本会打开微软官方下载页面。',
      mbInformation, MB_OK);
  end;
end;
