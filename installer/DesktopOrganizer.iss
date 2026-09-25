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
;
; Single-instance / graceful-shutdown contract shared with the app (see [Code]'s EnsureAppClosed below).
; Both literals must always match SingleInstanceNaming.InstallerDetectionMutexName /
; .InstallerShutdownRequestEventName (src/DesktopOrganizer.Services/SingleInstance/SingleInstanceNaming.cs)
; — do not rename/reprefix either one here without updating those constants in the same change; enforced
; by InstallerMutexContractTests.
; * AppRunningMutexName: existence-only detection mutex the app holds open for its whole lifetime — its
;   sole job is "exists <=> the app process is alive" (ADR-023 D-1). Used both by IsAppRunning() below
;   (CheckForMutexes) and, no longer, by [Setup]'s own AppMutex= (see the comment there for why).
; * AppShutdownEventName: named event the app listens on (IShutdownRequestListener) to trigger its own
;   normal exit path (state save, desktop icon restore, single-instance teardown) on request.
#define AppRunningMutexName "DesktopOrganizerSingleInstanceMutex"
#define AppShutdownEventName "DesktopOrganizerShutdownRequestEvent"

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
; NB: deliberately NOT using [Setup]'s own AppMutex= directive. AppMutex makes Setup block with its own
; hardcoded, non-closable "close the application and click OK to continue" prompt and gives [Code] no
; hook to intervene — exactly the wall this feature replaces with EnsureAppClosed's own
; ask-then-close-it-ourselves-then-retry flow (PrepareToInstall/InitializeUninstall below), which offers
; to close the app gracefully via AppShutdownEventName instead of just refusing to proceed. IsAppRunning
; (CheckForMutexes against AppRunningMutexName) is what [Code] uses instead to detect the same condition.
;
; Belt-and-braces alongside that: Restart Manager checks every file matching this mask for open handles
; (not just the main exe) and can close/restart the owning process itself when the user allows it — kept
; as a second line of defense in case some other handle (not the main .exe) is what's actually locking a
; file.
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.StartWithWindows=Запускать при старте Windows
english.StartWithWindows=Start with Windows

; EnsureAppClosed's user-facing strings ([Code], below). %1 is always {#MyAppName} via FmtMessage.
russian.AppRunningClosePrompt=%1 сейчас запущен. Закрыть его и продолжить установку?
english.AppRunningClosePrompt=%1 is currently running. Close it and continue with the installation?
russian.AppRunningUninstallPrompt=%1 сейчас запущен. Закрыть его и продолжить удаление?
english.AppRunningUninstallPrompt=%1 is currently running. Close it and continue with the uninstallation?
russian.AppRunningWaiting=Закрываю %1…
english.AppRunningWaiting=Closing %1…
russian.AppRunningCloseFailedRetry=Не удалось закрыть %1 автоматически (возможно, установлена старая версия). Закройте его вручную: значок в трее → Выход, затем нажмите «Повторить».
english.AppRunningCloseFailedRetry=Could not close %1 automatically (you may have an older version installed). Please close it manually: tray icon -> Exit, then click Retry.
russian.AppRunningCancelled=Установка отменена: %1 должен быть закрыт перед обновлением.
english.AppRunningCancelled=Setup cancelled: %1 must be closed before the installation can continue.

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
  Graceful-close-then-continue instead of AppMutex's hard block.

  Design:
   * IsAppRunning() probes AppRunningMutexName for existence only (CheckForMutexes) — same detection
     the old [Setup] AppMutex= directive relied on, just driven from this section instead so it can be
     retried in a loop instead of being a single non-closable prompt.
   * RequestGracefulShutdown() signals AppShutdownEventName via SetEvent. The app's
     IShutdownRequestListener wakes up and runs its normal Shutdown() path (state save, desktop icon
     restore, single-instance teardown) — the SAME path the tray "Exit" command uses. OpenEventW
     returning a null handle means either no app is running right now, or a pre-this-feature app version
     without a listener (EVENT_MODIFY_STATE is enough to call SetEvent; no wider access requested).
   * WaitForAppExit() polls IsAppRunning() rather than waiting on the app's process handle directly —
     this script never has an actual process handle, only the mutex-existence contract IsAppRunning()
     already wraps. Once the mutex is gone it additionally waits for {#MyAppExeName} to become writable
     (IsAppExeUnlocked) — see that function's own remarks for why those two are not quite the same
     instant.
   * EnsureAppClosed() is the shared ask/close/retry loop used by BOTH install (PrepareToInstall) and
     uninstall (InitializeUninstall): ask -> (if yes) request+wait -> (if that failed) offer retry/cancel
     -> loop. SuppressibleMsgBox degrades correctly for unattended runs ONLY when /SUPPRESSMSGBOXES is
     passed alongside /SILENT or /VERYSILENT (that combination is what makes it return the *default*
     button — IDYES / IDCANCEL below — without showing anything; Inno Setup's own documented contract). A
     bare /VERYSILENT with no /SUPPRESSMSGBOXES still shows these boxes. A fully unattended run (e.g. an
     auto-update script) needs /VERYSILENT /SUPPRESSMSGBOXES to get the auto-close-then-continue behavior
     with no UI at all — see installer/README.md.
  --------------------------------------------------------------------------- }

const
  EVENT_MODIFY_STATE = $0002;
  GENERIC_WRITE = $40000000;
  OPEN_EXISTING = 3;
  INVALID_HANDLE_VALUE = $FFFFFFFF; // THandle is unsigned here; this is (THandle)(-1).

function OpenEventW(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function CreateFileW(lpFileName: String; dwDesiredAccess, dwShareMode: DWORD; lpSecurityAttributes: Cardinal; dwCreationDisposition, dwFlagsAndAttributes: DWORD; hTemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

var
  { O-6: set by EnsureAppClosed whenever it actually closed a running instance (as opposed to no instance
    having been running at all). Consumed by CurStepChanged below to relaunch the app after a silent
    upgrade that would otherwise leave the user with nothing running post-install. }
  AppWasClosedByInstaller: Boolean;

{ Pumps the wizard's message queue and advances the marquee animation (when ProgressPage is non-nil —
  see EnsureAppClosed) before each polling Sleep, so a 15-second wait never makes the wizard look hung
  ("Not Responding") the way one long, uninterrupted Sleep() would. }
procedure ShutdownWaitTick(ProgressPage: TOutputMarqueeProgressWizardPage);
begin
  if ProgressPage <> nil then
    ProgressPage.SetProgress(0, 0); // a marquee page has no real position/max — this call's job is the message pump, not a percentage
  Sleep(250);
end;

function IsAppRunning(): Boolean;
begin
  { Existence-only probe — never waits on, or takes, ownership (same contract as the app's own
    installer-detection mutex, ADR-023 D-1). }
  Result := CheckForMutexes('{#AppRunningMutexName}');
end;

{ R-1: True when {#MyAppExeName} either does not exist yet (clean install — nothing to be locked) or can
  be opened for writing right now. False means some process — almost always the app itself, mid-exit —
  still holds it open. AppRunningMutexName (what IsAppRunning probes) is released inside
  _serviceProvider.Dispose(), at the very end of App.OnExit; the process's hold on its own .exe file can
  outlive that by a short window, so the mutex disappearing is a necessary but not sufficient signal that
  the file itself is free to overwrite. }
function IsAppExeUnlocked(): Boolean;
var
  ExePath: String;
  FileHandle: THandle;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ExePath) then
  begin
    Result := True;
    exit;
  end;

  FileHandle := CreateFileW(ExePath, GENERIC_WRITE, 0, 0, OPEN_EXISTING, 0, 0);
  if FileHandle = INVALID_HANDLE_VALUE then
  begin
    Result := False;
  end
  else
  begin
    CloseHandle(FileHandle);
    Result := True;
  end;
end;

function TryOpenShutdownEvent(): THandle;
begin
  Result := OpenEventW(EVENT_MODIFY_STATE, False, '{#AppShutdownEventName}');
end;

function RequestGracefulShutdown(): Boolean;
var
  EventHandle: THandle;
  Elapsed: Integer;
begin
  { O-2(b): InstallerShutdownRequestListener.Start() runs from App.StartApp (InitTrayIcon), a short but
    nonzero time after Program.Main already created AppRunningMutexName — the very mutex IsAppRunning()
    just saw. Retry OpenEventW for a few seconds so a just-launched app is not immediately treated the
    same as a pre-this-feature version that never listens at all. Bails out early if the app exits on its
    own while retrying (IsAppRunning turns False) — nothing left to signal at that point. }
  EventHandle := TryOpenShutdownEvent();
  Elapsed := 0;
  while (EventHandle = 0) and IsAppRunning() and (Elapsed < 3000) do
  begin
    Sleep(250);
    Elapsed := Elapsed + 250;
    EventHandle := TryOpenShutdownEvent();
  end;

  if EventHandle = 0 then
  begin
    { No listener to signal — either the app isn't running any more, or it's an older build that predates
      this feature (no IShutdownRequestListener). Either way there is nothing more this function can do. }
    Log('RequestGracefulShutdown: OpenEventW failed (app not running, or an older version without the shutdown listener).');
    Result := False;
    exit;
  end;

  Result := SetEvent(EventHandle);
  if not Result then
    Log('RequestGracefulShutdown: SetEvent failed.');
  CloseHandle(EventHandle);
end;

function WaitForAppExit(TimeoutMs: Integer; ProgressPage: TOutputMarqueeProgressWizardPage): Boolean;
var
  Elapsed: Integer;
begin
  Elapsed := 0;
  while IsAppRunning() and (Elapsed < TimeoutMs) do
  begin
    ShutdownWaitTick(ProgressPage);
    Elapsed := Elapsed + 250;
  end;

  if IsAppRunning() then
  begin
    Result := False;
    exit;
  end;

  { R-1: see IsAppExeUnlocked's remarks — give the process up to another 5s to actually release its file
    lock now that its detection mutex is gone. }
  Elapsed := 0;
  while (not IsAppExeUnlocked()) and (Elapsed < 5000) do
  begin
    ShutdownWaitTick(ProgressPage);
    Elapsed := Elapsed + 250;
  end;

  Result := IsAppExeUnlocked();
end;

{ Shared by PrepareToInstall (install/upgrade) and InitializeUninstall (uninstall) — see file-header
  comment above. PromptMsg names the CustomMessage to ask with (the two callers differ only in that).
  ShowProgress is True only for the install path, which has a live wizard to put a busy page on
  (R-2, CreateOutputMarqueeProgressPage); the uninstall path has no wizard at that point, so it passes
  False and this function never touches the progress page at all. }
function EnsureAppClosed(const PromptMsg: String; ShowProgress: Boolean): Boolean;
var
  ProgressPage: TOutputMarqueeProgressWizardPage;
begin
  ProgressPage := nil;

  while IsAppRunning() do
  begin
    if SuppressibleMsgBox(FmtMessage(CustomMessage(PromptMsg), ['{#MyAppName}']), mbConfirmation, MB_YESNO, IDYES) <> IDYES then
    begin
      Result := False;
      exit;
    end;

    if ShowProgress then
    begin
      // Created at most once per EnsureAppClosed call (i.e. at most once per install run), reused across
      // retry-loop iterations via Show()/Hide() rather than recreated each time.
      if ProgressPage = nil then
        ProgressPage := CreateOutputMarqueeProgressPage(
          SetupMessage(msgWizardPreparing),
          FmtMessage(CustomMessage('AppRunningWaiting'), ['{#MyAppName}']));
      ProgressPage.Show();
    end;

    try
      if RequestGracefulShutdown() and WaitForAppExit(15000, ProgressPage) then
      begin
        AppWasClosedByInstaller := True;
        break;
      end;
    finally
      if ShowProgress then
        ProgressPage.Hide();
    end;

    { O-2(a): the app may have exited on its own (or the user closed it by hand) between the IsAppRunning
      check that started this loop iteration and here — re-check before bothering the user with the
      "could not close it automatically" dialog. }
    if not IsAppRunning() then
    begin
      AppWasClosedByInstaller := True;
      break;
    end;

    if SuppressibleMsgBox(FmtMessage(CustomMessage('AppRunningCloseFailedRetry'), ['{#MyAppName}']), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      exit;
    end;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if EnsureAppClosed('AppRunningClosePrompt', True) then
    Result := ''
  else
    Result := FmtMessage(CustomMessage('AppRunningCancelled'), ['{#MyAppName}']);
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureAppClosed('AppRunningUninstallPrompt', False);
end;

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
var
  ResultCode: Integer;
begin
  { Files are in place: if autostart is already on, make sure it points at the
    exe we just installed (covers silent upgrades and a changed install dir). }
  if (CurStep = ssPostInstall) and AutostartCurrentlyEnabled() then
    WriteAutostart(True);

  { O-6: the postinstall entry in the [Run] section above (the one launching the freshly installed exe,
    flagged "nowait postinstall skipifsilent") never fires for a silent run — skipifsilent means exactly
    that. That is fine for an ordinary silent install (nothing was running, nothing needs relaunching),
    but if EnsureAppClosed (PrepareToInstall) closed a running instance itself, a silent upgrade would
    otherwise leave the user with the app gone from their tray after what looked, from the outside, like a
    background update. Relaunch it ourselves in that one specific case. ExecAsOriginalUser (rather than
    Exec) launches as the interactively logged-on user, which matters when Setup itself is elevated, and
    behaves exactly like Exec when it isn't (this installer's own PrivilegesRequired=lowest case).
    ewNoWait mirrors the [Run] entry's own "nowait" flag: Setup does not block waiting for the app to
    exit. }
  if (CurStep = ssPostInstall) and WizardSilent() and AppWasClosedByInstaller then
  begin
    if not ExecAsOriginalUser(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log('CurStepChanged: failed to relaunch the app after a silent installer-initiated close.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    WriteAutostart(False);
end;
