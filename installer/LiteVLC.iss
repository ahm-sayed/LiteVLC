#define MyAppName "LiteVLC"
#define MyAppVersion "1.6.2"
#define MyAppPublisher "Ahmed Sayed"
#define MyAppURL "https://github.com/ahm-sayed/LiteVLC"
#define MyAppExeName "LiteVLC.exe"

[Setup]
AppId={{8F5E39C2-4D9A-4B18-9F11-6A3C2E8D7B01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
; UsePreviousAppDir=no ensures /DIR="..." from the Auto-Updater ALWAYS overwrites the active folder in-place,
; while GetDefaultDir preserves the user's previous folder during interactive manual installs.
UsePreviousAppDir=no
DefaultDirName={code:GetDefaultDir}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
OutputDir=..\dist
OutputBaseFilename=LiteVLC-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\LiteVLC.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associatevideo"; Description: "Register LiteVLC in 'Open With' for video & audio files (.mp4, .mkv, .avi, .webm, .mov, .mp3, .flac)"; GroupDescription: "File Integration:"

[InstallDelete]
; Clean out any stale legacy artifacts in target folder before copying fresh v1.6.1 binaries
Type: files; Name: "{app}\LiteVLC.pdb"
Type: files; Name: "{app}\LiteVLC.dll"
Type: files; Name: "{app}\LiteVLC.deps.json"
Type: files; Name: "{app}\LiteVLC.runtimeconfig.json"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Clean per-user / system Open-With registration without hijacking existing default player
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName} Media Player"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.avi\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.ts\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.xspf\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo
Root: HKA; Subkey: "Software\Classes\.m3u8\OpenWithList\{#MyAppExeName}"; Flags: uninsdeletekey; Tasks: associatevideo

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallSubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F5E39C2-4D9A-4B18-9F11-6A3C2E8D7B01}_is1';

function GetDefaultDir(Param: String): String;
var
  PrevDir: String;
begin
  if RegQueryStringValue(HKA, UninstallSubKey, 'InstallLocation', PrevDir) and (PrevDir <> '') then
    Result := RemoveBackslashUnlessRoot(PrevDir)
  else if RegQueryStringValue(HKCU, UninstallSubKey, 'InstallLocation', PrevDir) and (PrevDir <> '') then
    Result := RemoveBackslashUnlessRoot(PrevDir)
  else if RegQueryStringValue(HKLM, UninstallSubKey, 'InstallLocation', PrevDir) and (PrevDir <> '') then
    Result := RemoveBackslashUnlessRoot(PrevDir)
  else
    Result := ExpandConstant('{autopf}\{#MyAppName}');
end;

procedure RemoveOldCopyInRoot(RootKey: Integer; NewAppDir: String);
var
  OldDir, UninsExe: String;
  ResultCode: Integer;
begin
  if RegQueryStringValue(RootKey, UninstallSubKey, 'InstallLocation', OldDir) and (OldDir <> '') then
  begin
    OldDir := RemoveBackslashUnlessRoot(OldDir);
    if CompareText(OldDir, NewAppDir) <> 0 then
    begin
      if RegQueryStringValue(RootKey, UninstallSubKey, 'UninstallString', UninsExe) and (UninsExe <> '') then
      begin
        UninsExe := RemoveQuotes(UninsExe);
        if FileExists(UninsExe) then
          Exec(UninsExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Guarantee no lingering older LiteVLC.exe process locks LiteVLC.exe / libmpv-2.dll
  // (also fixes upgrades triggered by older v1.3.0-v1.6.0 updaters!).
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM LiteVLC.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(450);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  NewAppDir: String;
begin
  if CurStep = ssInstall then
  begin
    NewAppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
    // If an older copy of LiteVLC was installed in a DIFFERENT folder (or different HKCU/HKLM scope),
    // cleanly uninstall it so only the latest version exists on the PC!
    RemoveOldCopyInRoot(HKCU, NewAppDir);
    RemoveOldCopyInRoot(HKLM, NewAppDir);
  end;
end;
