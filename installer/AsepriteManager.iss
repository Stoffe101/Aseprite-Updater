#define MyAppName "Aseprite Manager"
#define MyAppVersion "0.7.0"
#define MyAppExeName "AsepriteManager.exe"

[Setup]
AppId={{9A0FB176-24A1-46D0-99AC-5949CD3257D5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Stoffe101
DefaultDirName={localappdata}\Programs\Aseprite Manager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=AsepriteManager-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Aseprite Manager"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Aseprite Manager"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Aseprite Manager"; Flags: nowait postinstall skipifsilent
