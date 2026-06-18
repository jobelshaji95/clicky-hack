; Inno Setup script for Clicky (Windows, fully offline build).
; Produces a single ClickySetup.exe that installs the self-contained .NET app,
; the bundled offline engines (llama.cpp server, Piper, Whisper model), and the
; small Piper voice. The large vision model is downloaded on first run.
;
; Build steps (run from the `windows` directory):
;   1. dotnet publish src\Clicky\Clicky.csproj -c Release -r win-x64 ^
;          --self-contained -o publish
;   2. powershell -ExecutionPolicy Bypass -File scripts\fetch-runtime.ps1
;   3. Copy stage\tools and stage\models into publish\
;   4. Compile this script with Inno Setup (iscc installer\Clicky.iss)

#define AppName "Clicky"
#define AppVersion "1.0.0"
#define AppPublisher "Clicky"
#define AppExeName "Clicky.exe"

[Setup]
AppId={{B1C0A7E2-CL1C-4B00-9F00-000000000001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ClickySetup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}

[Tasks]
Name: "startupicon"; Description: "Start Clicky automatically when I sign in"; GroupDescription: "Startup:"

[Files]
; Everything published into `publish` (app + tools + models) is shipped recursively.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
Description: "Launch Clicky"; Filename: "{app}\{#AppExeName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the first-run-downloaded vision model so uninstall leaves nothing behind.
Type: filesandordirs; Name: "{app}\models"
