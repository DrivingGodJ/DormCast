#ifndef AppVersion
  #define AppVersion "0.7.2"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\DormCast-" + AppVersion + "-win-x64"
#endif

[Setup]
#ifdef SmokeTest
AppId={{AD419A2F-126A-41DF-AC7E-D90DD9C98751}
#else
AppId={{E1B13DE4-4D41-4FCB-A84B-14E4C3CD1E72}
#endif
AppName=宿舍之音 DormCast
AppVersion={#AppVersion}
AppPublisher=DormCast
AppCopyright=Copyright (C) 2026 DormCast contributors
DefaultDirName={localappdata}\Programs\DormCast
DefaultGroupName=宿舍之音
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir=..\artifacts
#ifdef SmokeTest
OutputBaseFilename=DormCast-{#AppVersion}-SmokeTest
#else
OutputBaseFilename=DormCast-{#AppVersion}-Setup-x64
#endif
SetupIconFile=..\src\DormCast\Assets\dormcast.ico
UninstallDisplayIcon={app}\DormCast.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
#ifndef SmokeTest
AppMutex=Local\DormCast.Desktop
#endif
SetupLogging=yes
LicenseFile=..\LICENSE

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifndef SmokeTest
Source: "{#PayloadDir}\Drivers\VBCABLE_Driver_Pack46.zip"; DestDir: "{tmp}"; Flags: dontcopy
#endif

[Icons]
#ifndef SmokeTest
Name: "{autoprograms}\宿舍之音"; Filename: "{app}\DormCast.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\宿舍之音"; Filename: "{app}\DormCast.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\DormCast.exe"; Description: "启动宿舍之音"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.tmp"

[Code]
const
  VbCableUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:VBCABLE {87459874-1236-4469}';
  VbCableServiceKey = 'SYSTEM\CurrentControlSet\Services\VBAudioVACMME';

var
  DriverInstalledBySetup: Boolean;

function EscapePowerShellSingleQuotes(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '''', '''''', True);
end;

function IsVirtualCableInstalled(): Boolean;
var
  UninstallExe: String;
begin
  Result :=
    RegKeyExists(HKLM64, VbCableUninstallKey) or
    RegKeyExists(HKLM64, VbCableServiceKey) or
    FileExists(ExpandConstant('{commonpf64}\VB\CABLE\VBCABLE_Setup_x64.exe'));
  if (not Result) and RegQueryStringValue(HKLM64, VbCableUninstallKey, 'UninstallString', UninstallExe) then
    Result := Trim(UninstallExe) <> '';
end;

function GetVbCableUninstallerPath(): String;
var
  UninstallExe: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, VbCableUninstallKey, 'UninstallString', UninstallExe) then
  begin
    UninstallExe := Trim(RemoveQuotes(UninstallExe));
    if (UninstallExe <> '') and FileExists(UninstallExe) then
    begin
      Result := UninstallExe;
      Exit;
    end;
  end;
  UninstallExe := ExpandConstant('{commonpf64}\VB\CABLE\VBCABLE_Setup_x64.exe');
  if FileExists(UninstallExe) then
    Result := UninstallExe;
end;

function ExtractZipArchive(const ZipPath, DestDir: String): Boolean;
var
  PsExe, Params: String;
  ResultCode: Integer;
begin
  Result := False;
  if not FileExists(ZipPath) then Exit;
  if DirExists(DestDir) then DelTree(DestDir, True, True, True);
  ForceDirectories(DestDir);
  PsExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Params := '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' +
    EscapePowerShellSingleQuotes(ZipPath) + ''' -DestinationPath ''' +
    EscapePowerShellSingleQuotes(DestDir) + ''' -Force"';
  if Exec(PsExe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

procedure InitializeWizard;
var
  Page: TWizardPage;
  Guide: TNewStaticText;
begin
  DriverInstalledBySetup := False;
  Page := CreateCustomPage(wpSelectDir, '必需组件：虚拟声卡（VB-CABLE）', '宿舍之音自动使用虚拟声卡采集系统声音并实现多端同步。');
  Guide := TNewStaticText.Create(Page);
  Guide.Parent := Page.Surface;
  Guide.Left := 0;
  Guide.Top := 8;
  Guide.Width := Page.SurfaceWidth;
  Guide.Height := Page.SurfaceHeight - 16;
  Guide.AutoSize := False;
  Guide.WordWrap := True;
  if IsVirtualCableInstalled() then
  begin
    Guide.Caption := '已检测到当前电脑已安装虚拟声卡（VB-CABLE）。' + #13#10#13#10 +
      '本次安装将自动跳过虚拟声卡安装步骤，直接安装或更新宿舍之音本体。' + #13#10#13#10 +
      '提示：进入软件后只需选择本机输出设备（扬声器或耳机），虚拟声卡会由软件自动选择。日后卸载宿舍之音时，虚拟声卡也将一并卸载。';
  end
  else
  begin
    Guide.Caption := '当前电脑尚未安装虚拟声卡（VB-CABLE）。' + #13#10#13#10 +
      '1. 继续安装时，安装程序将强制要求安装 VB-CABLE 虚拟声卡，并弹出 Windows 管理员授权（UAC）提示。' + #13#10#13#10 +
      '2. 若取消授权或未能成功安装虚拟声卡，本次安装将自动中止。' + #13#10#13#10 +
      '3. 安装完成后建议按提示重启电脑；进入软件后只需选择本机输出设备，虚拟声卡由软件自动选择。' + #13#10#13#10 +
      '4. 日后卸载宿舍之音时，虚拟声卡将随软件本体一并卸载。';
  end;
end;

#ifndef SmokeTest
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ZipPath, TempDir, SetupExe: String;
  ErrorCode: Integer;
begin
  Result := '';
  if IsVirtualCableInstalled() then
    Exit;

  ExtractTemporaryFile('VBCABLE_Driver_Pack46.zip');
  ZipPath := ExpandConstant('{tmp}\VBCABLE_Driver_Pack46.zip');
  TempDir := ExpandConstant('{tmp}\vbcable-install');
  if not ExtractZipArchive(ZipPath, TempDir) then
  begin
    Result := '无法解压内置的虚拟声卡安装包（VBCABLE_Driver_Pack46.zip），已中止安装。';
    Exit;
  end;

  SetupExe := TempDir + '\VBCABLE_Setup_x64.exe';
  if not FileExists(SetupExe) then
  begin
    Result := '虚拟声卡安装包中缺少 VBCABLE_Setup_x64.exe，已中止安装。';
    Exit;
  end;

  ShellExec('runas', SetupExe, '-i -h', TempDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode);
  Sleep(1500);

  if (not IsVirtualCableInstalled()) and (not WizardSilent()) then
  begin
    if MsgBox('未能自动完成虚拟声卡（VB-CABLE）安装（可能取消了管理员授权或需要手动确认）。' + #13#10#13#10 +
      '点击“是”打开官方 VB-CABLE 安装窗口并手动点击 Install Driver；点击“否”中止安装。',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('runas', SetupExe, '', TempDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode);
      Sleep(1500);
    end;
  end;

  DelTree(TempDir, True, True, True);

  if not IsVirtualCableInstalled() then
  begin
    Result := '未检测到虚拟声卡（VB-CABLE）安装成功。宿舍之音必须安装虚拟声卡后才能使用，已中止本次安装。';
    Exit;
  end;

  DriverInstalledBySetup := True;
  NeedsRestart := False;
end;

function NeedRestart(): Boolean;
begin
  Result := DriverInstalledBySetup;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SetupExe, SetupDir, ZipPath, TempDir: String;
  ErrorCode: Integer;
  ExtractedTemp: Boolean;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if not IsVirtualCableInstalled() then Exit;

  ExtractedTemp := False;
  TempDir := ExpandConstant('{tmp}\vbcable-uninstall');
  SetupExe := GetVbCableUninstallerPath();
  if (SetupExe = '') or (not FileExists(SetupExe)) then
  begin
    ZipPath := ExpandConstant('{app}\Drivers\VBCABLE_Driver_Pack46.zip');
    if FileExists(ZipPath) and ExtractZipArchive(ZipPath, TempDir) then
    begin
      ExtractedTemp := True;
      SetupExe := TempDir + '\VBCABLE_Setup_x64.exe';
    end;
  end;

  if (SetupExe <> '') and FileExists(SetupExe) then
  begin
    SetupDir := ExtractFileDir(SetupExe);
    ShellExec('runas', SetupExe, '-u -h', SetupDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode);
    Sleep(1500);
    if IsVirtualCableInstalled() and (not UninstallSilent()) then
    begin
      if MsgBox('未能自动静默卸载虚拟声卡（VB-CABLE）。' + #13#10#13#10 +
        '是否打开官方 VB-CABLE 程序手动点击 Remove Driver 完成卸载？',
        mbConfirmation, MB_YESNO) = IDYES then
      begin
        ShellExec('runas', SetupExe, '', SetupDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ErrorCode);
        Sleep(1000);
      end;
    end;
  end;

  if ExtractedTemp then
    DelTree(TempDir, True, True, True);
end;
#endif
