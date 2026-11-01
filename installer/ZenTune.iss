; Inno Setup script for ZenTune. Built by byggsetup.bat (installer\bygg-setup.ps1), which passes /DAppVersion.
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif

[Setup]
; Never change AppId: it is how Windows knows a new Setup.exe is an update of the same app
AppId={{858D956A-3C63-4D0F-AE40-5A8313B6E28C}
AppName=ZenTune
AppVersion={#AppVersion}
AppPublisher=ZenTune
DefaultDirName={autopf}\ZenTune
DefaultGroupName=ZenTune
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ZenTune-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\src\ZenTune.App\Assets\ZenTune-graph.ico
; Installs for the current user without an admin prompt; the wizard lets the user choose "for all users" instead
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\ZenTune.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\out\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ZenTune"; Filename: "{app}\ZenTune.exe"
Name: "{autodesktop}\ZenTune"; Filename: "{app}\ZenTune.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ZenTune.exe"; Description: "Start ZenTune"; Flags: nowait postinstall skipifsilent
