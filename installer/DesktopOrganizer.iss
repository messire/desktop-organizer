; Inno Setup script for DesktopOrganizer.
; Build with Inno Setup 6 (https://jrsoftware.org/isdl.php) — either open this
; file in "Inno Setup Compiler" and press Build, or from the command line:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DesktopOrganizer.iss
;
; Prerequisite: run installer\build.ps1 first so that installer\publish\win-x64
; contains a fresh self-contained build (build.ps1 runs this compiler too —
; running it directly is only needed for a manual/offline re-package).

#define MyAppName "DesktopOrganizer"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "khagen"
#define MyAppExeName "DesktopOrganizer.UI.exe"
; Generated once for this app — do not change between releases, or Windows
; will treat upgrades as a different application (no clean upgrade/uninstall).
; No braces here: the {{ ... } escaping is done directly on the AppId= line below,
; since a bare "{GUID}" macro value would make Inno's preprocessor emit an
; unescaped "{" that the compiler then tries (and fails) to parse as a constant.
#define MyAppId "A1BAA770-E3C5-4487-9E33-CEABF984557D"

[Setup]
AppId={{{#MyAppId}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL=https://github.com/
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app itself only ever touches HKCU (settings, autostart Run key) and the
; user's own desktop, so it doesn't need admin rights. Installing per-user
; under %LocalAppData% avoids the UAC prompt entirely, similar to VS Code.
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=DesktopOrganizer-Setup-{#MyAppVersion}
SetupIconFile=..\src\DesktopOrganizer.UI\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Lets Setup detect a running instance of the app (via the named mutex it holds
; open for its whole lifetime, see App.xaml.cs) and prompt the user to close it
; before overwriting files — otherwise the locked .exe would abort the update.
AppMutex=DesktopOrganizerSingleInstanceMutex
; Belt-and-braces alongside AppMutex: Restart Manager checks every file matching
; this mask for open handles (not just the main exe) and can close/restart the
; owning process itself when the user allows it.
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything from the self-contained publish output (exe, dlls, runtime, app.ico, etc.)
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Belt-and-braces: remove anything the app may have written under its own
; install folder (it shouldn't write there, but just in case).
Type: filesandordirs; Name: "{app}"

