#ifndef AppVersion
  #define AppVersion "0.5.3"
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

[Icons]
#ifndef SmokeTest
Name: "{autoprograms}\宿舍之音"; Filename: "{app}\DormCast.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\宿舍之音"; Filename: "{app}\DormCast.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\Drivers"; Description: "打开虚拟声卡驱动原包（首次需要手动安装）"; Flags: postinstall shellexec skipifsilent
Filename: "{app}\DormCast.exe"; Description: "启动宿舍之音"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.tmp"

[Code]
procedure InitializeWizard;
var
  Page: TWizardPage;
  Guide: TNewStaticText;
begin
  Page := CreateCustomPage(wpSelectDir, '首次使用：准备虚拟声卡', '程序运行依赖已内置；虚拟声卡需要手动安装一次。');
  Guide := TNewStaticText.Create(Page);
  Guide.Parent := Page.Surface;
  Guide.Left := 0;
  Guide.Top := 8;
  Guide.Width := Page.SurfaceWidth;
  Guide.Height := Page.SurfaceHeight - 16;
  Guide.AutoSize := False;
  Guide.WordWrap := True;
  Guide.Caption := '安装完成后，请打开 Drivers 文件夹：' + #13#10#13#10 +
    '1. 解压 VBCABLE_Driver_Pack46.zip。' + #13#10#13#10 +
    '2. 右键 VBCABLE_Setup_x64.exe，选择“以管理员身份运行”，点击 Install Driver。' + #13#10#13#10 +
    '3. 按官方提示重启，再打开宿舍之音。程序会选择虚拟声卡采集、实际扬声器播放。' + #13#10#13#10 +
    '已经安装 VB-CABLE 的用户可跳过。官方驱动原包保持完整，本安装程序不会自动运行驱动安装器。';
end;
