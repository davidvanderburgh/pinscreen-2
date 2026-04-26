; Pinscreen 2 -- Inno Setup script
; Compile with build.ps1, which passes /DAppVersion and /DPublishDir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef ProjectDir
  #define ProjectDir ".."
#endif

#ifndef PublishDir
  #define PublishDir "..\Pinscreen2.App\bin\Release\net9.0\win-x64\publish"
#endif

[Setup]
AppId={{6F2C8B73-1A4D-4F3E-9C5A-7E2B1D8F0A4E}
AppName=Pinscreen 2
AppVersion={#AppVersion}
AppVerName=Pinscreen 2 v{#AppVersion}
AppPublisher=David Vanderburgh
AppPublisherURL=https://github.com/davidvanderburgh/pinscreen-2
AppSupportURL=https://github.com/davidvanderburgh/pinscreen-2/issues
AppUpdatesURL=https://github.com/davidvanderburgh/pinscreen-2/releases
DefaultDirName={autopf}\Pinscreen2
DefaultGroupName=Pinscreen 2
OutputBaseFilename=Pinscreen2_Setup_v{#AppVersion}
SetupIconFile={#ProjectDir}\Pinscreen2.App\Assets\icon.ico
UninstallDisplayIcon={app}\Pinscreen2.App.exe
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Install for all users into Program Files. The app is updated by re-running a
; new installer .exe -- there is no in-app self-update flow.
PrivilegesRequired=admin
WizardStyle=modern
DisableProgramGroupPage=auto
VersionInfoVersion={#AppVersion}.0
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "startupicon"; Description: "Launch Pinscreen 2 when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Everything from the published self-contained app folder
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Pinscreen 2"; Filename: "{app}\Pinscreen2.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Pinscreen2.App.exe"
Name: "{group}\{cm:UninstallProgram,Pinscreen 2}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Pinscreen 2"; Filename: "{app}\Pinscreen2.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Pinscreen2.App.exe"; Tasks: desktopicon
Name: "{userstartup}\Pinscreen 2"; Filename: "{app}\Pinscreen2.App.exe"; WorkingDir: "{app}"; Tasks: startupicon

[Run]
Filename: "{app}\Pinscreen2.App.exe"; Description: "Launch Pinscreen 2"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
