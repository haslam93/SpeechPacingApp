; PaceCoach Inno Setup installer script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)

#define AppName      "Pace Coach"
#define AppExeName   "PaceApp.App.exe"
#define AppVersion   "1.0.0"
#define AppPublisher "Hammad Aslam"
#define AppURL       "https://github.com/hammadaslam/PaceApp"

[Setup]
AppId={{B7A4D2F1-3C8E-4F6A-9D12-E5A8C7F4B602}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\PaceCoach
DefaultGroupName={#AppName}
OutputDir=Output
OutputBaseFilename=PaceCoach-Setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\src\PaceApp.App\Assets\PaceCoach.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupentry"; Description: "Start Pace Coach with Windows"; GroupDescription: "Startup:"

[Files]
Source: "..\published\PaceCoach-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; Tasks: startupentry

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
