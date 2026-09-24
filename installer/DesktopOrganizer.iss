; Inno Setup script for DesktopOrganizer.
; Build with Inno Setup 6 (https://jrsoftware.org/isdl.php) — either open this
; file in "Inno Setup Compiler" and press Build, or from the command line:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DesktopOrganizer.iss
;
; Prerequisite: run installer\build.ps1 first so that installer\publish\win-x64
; contains a fresh self-contained build (build.ps1 runs this compiler too —
; running it directly is only needed for a manual/offline re-package).

#define MyAppName "DesktopOrganizer"
#define MyAppPublisher "khagen"
#define MyAppExeName "DesktopOrganizer.UI.exe"
#define MyAppExePath SourcePath + "publish\win-x64\" + MyAppExeName
; Version comes straight from the published exe's file-version resource instead
; of a hand-typed literal — that literal is exactly what drifted out of sync
; with Directory.Build.props's <Version> (installer kept saying 0.2.1 after the
; app moved on to 0.3.0). GetVersionNumbersString reads the native FILEVERSION
; resource, e.g. "0.3.0.0" — always 4 parts, since .NET pads a 3-part <Version>
; with a trailing zero. Trimmed back to 3 parts to match the csproj/changelog.
; Requires installer\publish\win-x64\{#MyAppExeName} to already exist and be
; fresh, i.e. this script must be compiled via build.ps1 (publish, then ISCC),
; not by opening it in the IDE and hitting Build on a stale/missing publish.
#define MyAppVersionRaw GetVersionNumbersString(MyAppExePath)
#define MyAppVersion Copy(MyAppVersionRaw, 1, Len(MyAppVersionRaw) - 2)
; Generated once for this app — do not change between releases, or Windows
; will treat upgrades as a different application (no clean upgrade/uninstall).
; No braces here: the {{ ... } escaping is done directly on the AppId= line below,
; since a bare "{GUID}" macro value would make Inno's preprocessor emit an
; unescaped "{" that the compiler then tries (and fails) to parse as a constant.
#define MyAppId "A1BAA770-E3C5-4487-9E33-CEABF984557D"
; Per-user autostart contract shared with the app. The app keeps NO copy of the
; "start with Windows" flag in its JSON state: AutostartService
; (src/DesktopOrganizer.UI/Services/AutostartService.cs) reads/writes exactly this
; HKCU Run value, so the checkbox on the installer's Finished page and the one in
; the app's Settings window are literally the same setting. Both literals below
; must match AutostartService.RunKeyPath / AutostartService.ValueName — enforced
; by InstallerAutostartContractTests.
#define AutostartRunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define AutostartValueName "DesktopOrganizer"
; NB: AutostartValueName is the *Release* build's value name (AutostartService.InstallerValueName).
; Debug builds use "DesktopOrganizer.Dev" and a separate data root %LocalAppData%\DesktopOrganizer.Dev
; (AppStorageRoot) so development never touches the installed application — nothing here refers to them.

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
; open for its whole lifetime) and prompt the user to close it before
; overwriting files — otherwise the locked .exe would abort the update. This is
; the installer-detection mutex only (ADR-023 D-1) — separate from the app's
; own per-session single-instance guard mutex. The literal below must always
; match SingleInstanceNaming.InstallerDetectionMutexName
; (src/DesktopOrganizer.Services/SingleInstance/SingleInstanceNaming.cs); do not
; rename/reprefix it here without updating that constant in the same change —
; enforced by InstallerMutexContractTests.
AppMutex=DesktopOrganizerSingleInstanceMutex
; Belt-and-braces alongside AppMutex: Restart Manager checks every file matching
; this mask for open handles (not just the main exe) and can close/restart the
; owning process itself when the user allows it.
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.StartWithWindows=Запускать при старте Windows
english.StartWithWindows=Start with Windows

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
; "Launch now" checkbox on the Finished page. The "Start with Windows" checkbox
; that sits right under it is not a [Run]/[Tasks] entry — it is added in [Code]
; (see AutostartCheckBox) so that it can live on the same final page and mirror
; the app's live registry state instead of Inno's remembered task selection.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Belt-and-braces: remove anything the app may have written under its own
; install folder (it shouldn't write there, but just in case).
Type: filesandordirs; Name: "{app}"

[Code]
{ ---------------------------------------------------------------------------
  "Start with Windows" on the Finished page.

  Design:
   * Single source of truth is the HKCU Run value (AutostartRunKey /
     AutostartValueName) — the same one the app's AutostartService toggles.
   * Initial state: fresh install → checked (Fences-like default); upgrade →
     whatever the app currently has (value present ⟺ checked), so a user who
     turned autostart off inside the app is not silently re-opted-in.
   * On Finish: checked → write the value (always with the *new* exe path, which
     also heals a stale path left by a previous install location);
     unchecked → delete the value, i.e. it really disables autostart, exactly
     like unchecking the box in the app.
   * Silent install (/SILENT, /VERYSILENT): the Finished page is skipped, so the
     user's existing choice is preserved as-is; an existing value is only
     re-pointed at the freshly installed exe.
   * Uninstall: the value is removed so no dangling autostart entry survives.
  --------------------------------------------------------------------------- }

const
  LB_GETITEMHEIGHT = $01A1;

var
  AutostartCheckBox: TNewCheckBox;
  IsUpgradeInstall: Boolean;

function AutostartCommand(): String;
begin
  { Same format the app writes: the full exe path wrapped in double quotes. }
  Result := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '"';
end;

function AutostartCurrentlyEnabled(): Boolean;
begin
  Result := RegValueExists(HKEY_CURRENT_USER, '{#AutostartRunKey}', '{#AutostartValueName}');
end;

procedure WriteAutostart(Enabled: Boolean);
begin
  if Enabled then
  begin
    if not RegWriteStringValue(HKEY_CURRENT_USER, '{#AutostartRunKey}', '{#AutostartValueName}', AutostartCommand()) then
      Log('Autostart: failed to write HKCU\{#AutostartRunKey}\{#AutostartValueName}');
  end
  else
  begin
    if AutostartCurrentlyEnabled() then
      if not RegDeleteValue(HKEY_CURRENT_USER, '{#AutostartRunKey}', '{#AutostartValueName}') then
        Log('Autostart: failed to delete HKCU\{#AutostartRunKey}\{#AutostartValueName}');
  end;
end;

function IsUpgrade(): Boolean;
var
  UninstallKey: String;
begin
  { Inno writes "<AppId>_is1" under the Uninstall branch; AppId here includes the braces.
    PrivilegesRequired=lowest puts it in HKCU, but check HKLM too in case an older
    per-machine install is being upgraded. }
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + '{' + '{#MyAppId}' + '}_is1';
  Result := RegKeyExists(HKEY_CURRENT_USER, UninstallKey) or RegKeyExists(HKEY_LOCAL_MACHINE, UninstallKey);
end;

procedure InitializeWizard();
begin
  { Evaluated before installation writes its own Uninstall key. }
  IsUpgradeInstall := IsUpgrade();

  AutostartCheckBox := TNewCheckBox.Create(WizardForm);
  AutostartCheckBox.Parent := WizardForm.FinishedPage;
  AutostartCheckBox.Caption := CustomMessage('StartWithWindows');
  AutostartCheckBox.Height := ScaleY(17);
  { Position is finalised in CurPageChanged(wpFinished), once RunList is populated. }
end;

procedure LayoutFinishedPage();
var
  ItemHeight: Integer;
begin
  { Shrink RunList to exactly its items so our checkbox sits directly under
    "Launch DesktopOrganizer" and looks like part of the same list. }
  ItemHeight := SendMessage(WizardForm.RunList.Handle, LB_GETITEMHEIGHT, 0, 0);
  if ItemHeight <= 0 then
    ItemHeight := ScaleY(22);

  if WizardForm.RunList.Items.Count > 0 then
  begin
    WizardForm.RunList.Height := ItemHeight * WizardForm.RunList.Items.Count + ScaleY(2);
    AutostartCheckBox.Top := WizardForm.RunList.Top + WizardForm.RunList.Height + ScaleY(2);
  end
  else
    AutostartCheckBox.Top := WizardForm.RunList.Top;

  AutostartCheckBox.Left := WizardForm.RunList.Left + WizardForm.RunList.Offset;
  AutostartCheckBox.Width := WizardForm.RunList.Width - WizardForm.RunList.Offset;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    LayoutFinishedPage();
    if IsUpgradeInstall then
      AutostartCheckBox.Checked := AutostartCurrentlyEnabled()
    else
      AutostartCheckBox.Checked := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  { On the Finished page "Next" is the Finish button; apply the choice before
    Inno processes the postinstall [Run] entries so the app, if launched, already
    sees the final state. }
  if CurPageID = wpFinished then
    WriteAutostart(AutostartCheckBox.Checked);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Files are in place: if autostart is already on, make sure it points at the
    exe we just installed (covers silent upgrades and a changed install dir). }
  if (CurStep = ssPostInstall) and AutostartCurrentlyEnabled() then
    WriteAutostart(True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    WriteAutostart(False);
end;
