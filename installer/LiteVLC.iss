#define MyAppName "LiteVLC"
#define MyAppVersion "1.2.0"
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
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
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
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associatevideo"; Description: "Register LiteVLC in 'Open With' for video & audio files (.mp4, .mkv, .avi, .webm, .mov, .mp3, .flac)"; GroupDescription: "File Integration:"

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

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
