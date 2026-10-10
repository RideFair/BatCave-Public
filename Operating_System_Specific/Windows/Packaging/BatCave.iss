#ifndef PublishDirectory
  #error PublishDirectory is required
#endif
#ifndef PackageDirectory
  #define PackageDirectory "."
#endif
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{7132C2B3-A038-4A5A-B164-5CF1395B9BD3}
AppName=BatCave
AppVersion={#AppVersion}
AppPublisher=RideFair
DefaultDirName={localappdata}\Programs\BatCave Desktop
DefaultGroupName=BatCave Desktop
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#PackageDirectory}
OutputBaseFilename=BatCave-{#AppVersion}-Windows-x64-Setup
SetupIconFile=..\batcave_icon.ico
UninstallDisplayIcon={app}\BatCave.WindowsAgent.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Messages]
ErrorCloseApplications=Windows could not close an application using BatCave's files.%n%nOlder BatCave versions may still be running in the system tray after their window closes. Choose Quit BatCave from its tray menu, then select Try again.

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.log,*.pfx,*.p12,*.pem,*.key,*.batcave-peer.json,identity.json,approved-peers.json,desktop-settings.json,topology.secure.json,monitor-layouts.json,*.json.new"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BatCave"; Filename: "{app}\BatCave.WindowsAgent.exe"; Parameters: "--desktop"

[Run]
Filename: "{app}\BatCave.WindowsAgent.exe"; Parameters: "--desktop"; Description: "Open BatCave"; Flags: nowait postinstall skipifsilent

[Registry]
; Remove only this app's opt-in login entry. Preserve device identity and
; pairing data during uninstall, so reinstall cannot silently replace identity.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "BatCaveSecure"; Flags: uninsdeletevalue
