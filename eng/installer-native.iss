#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifdef PackagingTest
  #define ProductId "TaskbarLyrics.PackagingTest"
  #define ProductName "TaskbarLyrics Packaging Test"
  #ifndef TestDataDir
    #error TestDataDir is required for isolated tests
  #endif
  #define DataDir TestDataDir
#else
  #define ProductId "TaskbarLyrics.Native"
  #define ProductName "TaskbarLyrics"
  #define DataDir "{localappdata}\TaskbarLyricsNative"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
DefaultDirName={localappdata}\Programs\{#ProductId}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
WizardStyle=modern
#ifdef PackagingTest
Compression=none
SolidCompression=no
#else
Compression=lzma2/ultra64
SolidCompression=yes
#endif
OutputDir={#OutputDir}
OutputBaseFilename=TaskbarLyrics-Native-v{#AppVersion}-win-x64-setup
SetupIconFile=..\native\assets\app.ico
UninstallDisplayIcon={app}\TaskbarLyrics.Native.exe
CloseApplications=yes
RestartApplications=no
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,portable.flag,Data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\TaskbarLyrics.Native.exe"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\TaskbarLyrics.Native.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TaskbarLyrics.Native.exe"; Description: "启动 TaskbarLyrics"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function StopInstalledCopy: Boolean;
var Code: Integer; Exe: String;
begin
  Exe := ExpandConstant('{app}\TaskbarLyrics.Native.exe');
  Result := True;
  if FileExists(Exe) then
    Result := Exec(Exe, '--shutdown', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopInstalledCopy then
    Result := '请先从托盘退出 TaskbarLyrics，保存设置后重试。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    if FileExists(ExpandConstant('{app}\portable.flag')) then begin
      MsgBox('此目录是便携版目录，请选择其他安装位置。', mbError, MB_OK);
      Result := False;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Existing: String;
begin
  if CurStep = ssPostInstall then
    if RegQueryStringValue(HKCU, RunKey, '{#ProductId}', Existing) and (Existing <> '') then
      RegWriteStringValue(HKCU, RunKey, '{#ProductId}', '"' + ExpandConstant('{app}\TaskbarLyrics.Native.exe') + '" --background');
end;

function InitializeUninstall: Boolean;
begin
  Result := StopInstalledCopy;
  if not Result then
    MsgBox('程序尚未退出，请先保存设置并从托盘退出，再卸载。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Purge: Boolean; Existing, DataPath: String;
begin
  if CurUninstallStep = usUninstall then begin
    Purge := False;
    if UninstallSilent then
      Purge := ExpandConstant('{param:PURGEDATA|0}') = '1'
    else
      Purge := MsgBox('是否同时删除个人设置、歌词缓存和日志？' + #13#10 +
        '选择“否”可在以后重新安装时继续使用这些数据。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if RegQueryStringValue(HKCU, RunKey, '{#ProductId}', Existing) then
      if (CompareText(Existing, '"' + ExpandConstant('{app}\TaskbarLyrics.Native.exe') + '"') = 0) or (CompareText(Existing, '"' + ExpandConstant('{app}\TaskbarLyrics.Native.exe') + '" --background') = 0) then
        RegDeleteValue(HKCU, RunKey, '{#ProductId}');
    if Purge then begin
#ifndef PackagingTest
      if CheckForMutexes('Local\TaskbarLyrics.Native.SingleInstance') then begin
        MsgBox('另一份 TaskbarLyrics 仍在运行，已保留个人数据。退出所有副本后再清理。', mbInformation, MB_OK);
        Exit;
      end;
#endif
      DataPath := ExpandConstant('{#DataDir}');
      if DirExists(DataPath) then
        if not DelTree(DataPath, True, True, True) then
          MsgBox('部分数据正在使用，未能全部删除：' + DataPath, mbInformation, MB_OK);
    end;
  end;
end;


