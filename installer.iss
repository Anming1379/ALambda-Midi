; ALambda Midi 安装脚本

#define MyAppName "ALambda Midi"
#define MyAppVersion "1.0"
#define MyAppPublisher "Anming_cat"
#define MyAppURL "https://anmingcat.pages.dev/"
#define MyAppExeName "ALambdaMidi.exe"

[Setup]
AppId={{B8A3D2E1-4F5C-4A7B-9E2D-1A3F5C7B9D2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=installer
OutputBaseFilename=ALambdaMidi_Setup_v{#MyAppVersion}
SetupIconFile=Assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
Source: "bin\Release\net10.0-windows\win-x64\publish\ALambdaMidi.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Classes\ALambdaMidi.Document"; ValueType: string; ValueName: ""; ValueData: "MIDI 文件"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\ALambdaMidi.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\ALambdaMidi.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.mid\OpenWithProgids"; ValueType: none; ValueName: "ALambdaMidi.Document"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.midi\OpenWithProgids"; ValueType: none; ValueName: "ALambdaMidi.Document"; Flags: uninsdeletevalue

[Code]
function IsDotNet10DesktopInstalled: Boolean;
var
  BaseDir: string;
  I: Integer;
  Version: string;
begin
  Result := False;
  BaseDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BaseDir) then Exit;

  // 硬编码常见版本范围：遍历 10.0 到 10.9
  for I := 0 to 9 do
  begin
    Version := Format('10.%d', [I]);
    if DirExists(BaseDir + '\' + Version + '.0') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 兜底：直接找 10.0.0 这种三段的常见形式
  if DirExists(BaseDir + '\10.0.12') then Result := True;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet10DesktopInstalled then
  begin
    if MsgBox('ALambda Midi 需要 .NET 10 桌面运行时才能运行。' + #13#10 + #13#10 +
              '是否现在打开下载页面？' + #13#10 + #13#10 +
              '安装完成后请重新运行本安装程序。',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    Result := False;
  end;
end;