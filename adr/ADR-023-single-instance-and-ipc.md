# ADR-023: Single Instance and Activation IPC

Status: Accepted

Branch `feat/single-instance-ipc` (from `rc/0.4.0`). Implementation master plan:
`docs/PLANS/phase-3-single-instance-ipc.md` (D-0..D-9, risks R-1..R-15). Commits `812743b`(C1)
`1ab6216`(C2) `9c21051`(C3) `63316b6`(C4+C5) `7098c00`(C6) → post-review fixes `c3ac4f3`
(accept-loop lifecycle + response-before-handoff + ACL) `1554282` (shutdown diagnostics +
degraded-primary + top-level guard) `2b7d115` (flaky test fix). 1387/1387 tests green (13 runs in a
row), `dotnet build` clean. Passed `code-quality-reviewer` (2 CRITICAL + 4 MAJOR found and fixed) and
`fences-ux-parity-reviewer` (ordinary/single launch unchanged, no regressions).

## Context

`App.xaml.cs` used to create `Mutex("DesktopOrganizerSingleInstanceMutex", initiallyOwned: false)`
purely so the installer could detect a running process (Inno Setup's `AppMutex=` checks object
*existence*, not ownership). `WaitOne`/ownership were never checked, so nothing stopped a second
process from starting. Combined with `PanelItemStorageService`'s in-memory write-through cache, two
concurrent processes could silently stomp each other's edits — tracked as `DO-001` (P1) in
`docs/Refactoring/BACKLOG.md`.

### R-1 — the key finding that shaped the whole design

The naive fix ("second process reaches `StartApp`, calls `Shutdown()`") does not work, because WPF
still runs `OnExit` on the way down. `App.OnExit` calls `WindowManager.SaveWindowsStateNow()` →
`SaveScheduler.Flush()` → an **unconditional** `_save()`. A process that never called
`LoadWindowsState()` has an empty child-window collection, so this serializes `"[]"`, and since
`_lastSavedJson` is `null` the `json == _lastSavedJson` guard never fires — `Properties.Settings
.Default.ChildWindowsState` is overwritten with `"[]"` and saved. The same happens to
`SystemIconsPanel.Save()` (DTO of an unloaded model) and to `RestoreDesktopIcons()` (unhides icons the
real primary intentionally hid). **Any** second process that reaches `App.OnExit` silently erases the
user's panels and desktop-icon state.

Consequence for the design: a secondary process must **never construct `App` at all** — reaching
`OnExit` is not survivable, only avoidable. This ruled out every design shaped around "guard inside
`App`'s constructor/startup, then call `Shutdown()`" and forced the guard to run in the process's own
entry point, before any `Application` object exists (D-2).

## Decision

DesktopOrganizer runs one writable instance per Windows user (session-scoped, with a machine-global
tie-breaker for Fast User Switching, see D-4/D-9). A named mutex owns process exclusivity for the
current session. Secondary launches send an `ACTIVATE_SETTINGS` activation command to the primary
instance via a machine-global Named Pipe and exit without ever touching writable state.

### D-1. Two separate mutexes — detection contract vs. guard contract

| Object | Name | Semantics | Consumer |
|---|---|---|---|
| detection | `DesktopOrganizerSingleInstanceMutex` (no prefix, verbatim literal) | "exists ⟺ the app process is alive" | `installer/DesktopOrganizer.iss` `[Code]` `IsAppRunning`/`CheckForMutexes` (formerly `[Setup] AppMutex=` — see D-10 addendum) |
| guard | `Local\DesktopOrganizer.SingleInstance.{sidHash}` | "owned ⟺ I am primary" | application only |
| shutdown-request event | `DesktopOrganizerShutdownRequestEvent` (no prefix, verbatim literal) | "signaled ⟺ the installer asks the running Primary to exit gracefully" | `installer/DesktopOrganizer.iss` `[Code]` `RequestGracefulShutdown`/`SetEvent` (signaler) and `IShutdownRequestListener` (consumer, Primary only) — see D-10 addendum |

These are deliberately **not** the same object: the installer's contract is *existence*, the guard's
contract is *ownership*. Merging them would permanently forbid renaming/respecifying the guard without
shipping a new installer. The cost is one extra OS handle per process.

The detection-mutex **name must never change**, including adding an equivalent `Local\` prefix (same
semantic object, zero benefit, non-zero risk) — pinned by `InstallerMutexContractTests`, which parses
`installer/DesktopOrganizer.iss` and compares the literal against
`SingleInstanceNaming.InstallerDetectionMutexName`.

`{sidHash}` is the first 16 hex characters of SHA-256 of `WindowsIdentity.GetCurrent().User.Value`
(falls back to upper-cased `Environment.UserName` if the SID is unavailable). The raw SID is never
embedded in the object name — pipe names are machine-global and visible to every principal on the box.

### D-2. Guard runs in the process's own `Program.Main`, before `Application` is constructed

A secondary process must never construct `App` (R-1). `DesktopOrganizer.UI.csproj` sets
`<StartupObject>DesktopOrganizer.UI.Program</StartupObject>` and removes `App.xaml` as the
`ApplicationDefinition` (kept as a `Page`, compiled by MSBuild instead of the WPF SDK's own
auto-generated `Main`). `Program.Main` builds the DI container, resolves `ISingleInstanceCoordinator`,
drives the handshake, and only constructs `App`/calls `InitializeComponent()`/`Run()` once the
handshake resolves to `RunAsPrimary`. Side benefits: a secondary process never raises a tray icon, never
installs `WH_MOUSE_LL`, never touches `user.config` or `PanelItemStorageService`, and — because it
never resolves `IDiagnosticsLog` — never writes a JSONL session file (see R-12/D-death-by-retention
below). Its lifetime is roughly the handshake's own, tens of milliseconds.

This is the option the plan called the primary path (Option B); the documented fallback ("guard inside
`App`'s constructor, `_isSecondary` flag, early-return in `OnExit`") was **not needed** — the
`StartupObject`/`ApplicationDefinition` swap built cleanly, so it was not exercised in this
implementation.

### D-3. `_role` field checked first in `OnExit` — defense in depth

Even though D-2 means a secondary process today never constructs `App` at all, `App` carries an
`internal App(ServiceProvider, ISingleInstanceCoordinator, SingleInstanceRole role)` constructor and
`OnExit`'s very first statement is an early-return if `_role != SingleInstanceRole.Primary`. Zero cost,
closes R-1 against any future refactor of the entry point that might reintroduce a path where `App` is
constructed for a non-primary process.

### D-4. Named pipe with `maxNumberOfServerInstances: 1` — a machine-global tie-breaker over the mutex

The guard mutex (no prefix / `Local\`) is a **session**-scoped object, not a **user**-scoped one. Fast
User Switching (one user, two sessions) would otherwise let two "primaries" write to the same
`%LOCALAPPDATA%` concurrently. A `Global\` mutex is not an option: creating an object in the Global
namespace requires `SeCreateGlobalPrivilege`, which an unelevated, per-user install
(`PrivilegesRequired=lowest`) does not have.

The named-pipe namespace, by contrast, *is* machine-global. The server is created with
`maxNumberOfServerInstances: 1`; a second session's attempt to create a server of the same name fails
with `ERROR_PIPE_BUSY` → that process treats itself as secondary, releases the guard mutex it just
took, sends the activation, and exits. Arbitration order: mutex (fast, per-session) → pipe server
(authoritative, per-user machine-wide).

Other pipe-server creation failures (ACL, generic faults) do **not** demote the process to secondary —
see the "degraded primary" deviation below.

### D-5. Protocol — one line, version tagged, response mandatory

```
request :  "DO/1 ACTIVATE_SETTINGS s=<sessionId>\n"     (UTF-8, ≤256 bytes)
response:  "DO/1 OK\n" | "DO/1 SHUTTING_DOWN\n" | "DO/1 OTHER_SESSION\n" | "DO/1 UNSUPPORTED\n"
```

`SHUTTING_DOWN` is what removes the "primary exiting + simultaneous launch" race: the secondary gets a
deterministic "retry ownership" signal instead of guessing off a bare timeout. `OTHER_SESSION` covers
D-4/FUS: activating a window in another Windows session is meaningless, so the secondary shows a
message and exits (code 4) instead. `UNSUPPORTED` is the compatibility rule — the server must always
answer, never silently close the connection, so a future client (`DO/2`) can tell "old primary" apart
from "primary hung". v1 has exactly one command, `ACTIVATE_SETTINGS`.

### D-6. Handshake — alternating acquire/send, 3s budget

`SingleInstanceHandshakePolicy` drives up to 6 attempts (connect timeout 300ms each, backoff
0/50/100/200/400/800ms) with no `Thread.Sleep` inside the pure policy itself — time is injected via
`Func<long> nowMs`. `TryAcquire` before every send handles "primary is exiting right now"
(`SHUTTING_DOWN`/no listener); the opposite race ("primary took the mutex but hasn't raised its server
yet") is closed by the primary starting its server **immediately** after acquiring the guard, before any
other startup work, plus the same retries.

### D-7. A failed handshake never starts a second writable instance

Budget exhausted, mutex held, primary unresponsive (hung) → the secondary shows a `MessageBox`
("DesktopOrganizer is already running but is not responding. Close it via Task Manager and try
again.") and exits with code 3. "Storage is never opened by two writable instances" outranks
convenience; there is no `--force-new-instance` escape hatch.

### D-8. Activation only ever calls `IWindowManager.ShowMainWindow()`

`SingleInstanceActivationHandler.Handle()` touches nothing else — no panels, no pages, no
`SystemIconsPanel`, no `DesktopIcons`, no `MonitorService`. Pinned by a Moq test asserting exactly one
`ShowMainWindow()` call plus `VerifyNoOtherCalls()` — the machine-checkable form of "activation does not
change desktop panels". The secondary calls `AllowSetForegroundWindow(ASFW_ANY)` (added to `INativeApi`,
the project's sole P/Invoke seam) immediately before sending, otherwise foreground-lock would reduce
`Window.Activate()` in the primary to a flashing taskbar button.

### D-9. Shutdown ordering — three phases, guard mutex released last

```
App.OnExit:
  1. coordinator.EnterShutdown()          // server stays up, now answers SHUTTING_DOWN
  2. <existing teardown: RestoreDesktopIcons / SystemIconsPanel.Save / SaveWindowsStateNow /
      GestureDiagnostics.SessionExiting>
  3. coordinator.CompleteShutdown()       // stops server (bounded ≤2s), releases guard mutex,
                                           //   writes SingleInstanceShutdownSequence — see deviation below
  4. DiagnosticsLog.Flush()
  5. _serviceProvider.Dispose()           // → coordinator.Dispose(): releases both mutex handles
```

The guard mutex is released only after every write has completed — otherwise a new process could start
writing to `user.config` while `Settings.Save()` is still mid-flight in the old primary.

**Thread affinity (R-5):** `Mutex` is owned by a *thread*. `TryAcquire`/`Release` run strictly on the
main STA thread (`Program.Main` and `App.OnExit` are the same thread); the pipe server's accept thread
never touches the mutex. `Release` is wrapped in `try/catch (ApplicationException)` — a failed release
is not fatal, the OS reclaims the object on process exit and the next instance correctly becomes primary
via `AbandonedMutexException`.

## Deviations from the original plan (`docs/PLANS/phase-3-single-instance-ipc.md`)

### New API member: `ISingleInstanceCoordinator.CompleteShutdown()`

Not present in the plan's §2.1 interface sketch. Found necessary while implementing D-9: writing the
`SingleInstanceShutdownSequence` diagnostics event requires a live `IDiagnosticsLog`, but
`ISingleInstanceCoordinator.Dispose()` runs automatically as part of `ServiceProvider.Dispose()`'s own
disposal chain — by the time `Dispose()` executes, the container is already marked disposed and any
further `GetService<IDiagnosticsLog>()` call throws `ObjectDisposedException` unconditionally, with no
short-circuit for already-materialized singletons. This was found and fixed as **C-2**, one of the two
CRITICAL findings from `code-quality-reviewer` (it reproduced as an `ObjectDisposedException` thrown on
every ordinary app exit). `App.OnExit` now calls `coordinator.CompleteShutdown()` explicitly, while the
DI container is still live, strictly before `_serviceProvider.Dispose()`. `CompleteShutdown()` is
idempotent; `Dispose()` performs the equivalent (diagnostics-free) teardown on its own as a fallback if
`CompleteShutdown()` was never called (e.g. a process that never became primary).

### ACL requires both `CreateNewInstance` and `TakeOwnership`, not `CreateNewInstance` alone

The plan (§2.3) and the initial review both assumed the pipe's `PipeSecurity` needed only
`PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance` to let a legitimately competing
`CreateNamedPipe` (the D-4/D-9 tie-breaker firing from the same user's other session) get far enough to
observe `ERROR_PIPE_BUSY` rather than being rejected outright. Measured experimentally by
`win32-interop-specialist` on Windows 11 (build 26200): granting `ReadWrite | CreateNewInstance` alone
still yields `ERROR_ACCESS_DENIED` for the competing create, because that create also supplies its own
security descriptor, and `PipeAccessRights.TakeOwnership` (`WRITE_OWNER`) turned out to be required for
that access check to pass. Without both rights, the competing create fails the access check *before* the
instance-count check, is misclassified as `ActivationServerUnavailableReason.AclFailed`, and makes
`PipeBusy` — the one outcome the machine-global pipe exists to detect — unreachable. Neither right widens
exposure beyond the plan's intent: the DACL still names exactly one SID (the current user's), the
instance count still caps at 1, and that SID already owns the object regardless. See
`NamedPipeActivationServer.CompetingCreateRights` and its doc comment; pinned by `T-IPC-02`.

## Bugs found and fixed during review (important lessons)

`code-quality-reviewer` found 2 CRITICAL + 4 MAJOR issues, all fixed before merge:

- **CRITICAL — pipe-instance leak → busy-spin, 100% CPU (C-1-class).** An earlier accept-loop shape
  left a `NamedPipeServerStream` instance un-disposed on certain exit paths from the per-connection
  handling; because the server is created with `maxNumberOfServerInstances: 1`, the leaked instance
  permanently occupied the single slot, and the accept loop's own recreate-on-failure logic then spun
  at 100% CPU retrying against an instance it could never reclaim. Confirmed experimentally (not just
  by code reading) before the fix. Fixed by making stream disposal on every code path through the
  accept loop unconditional (finally-based) and adding response-before-handoff sequencing, so the
  stream returns to a disposable state deterministically regardless of how the per-connection work
  ends.
- **CRITICAL — `ObjectDisposedException` on every ordinary exit (C-2).** See the `CompleteShutdown()`
  deviation above.
- **MAJOR — degraded-primary classification (M-2).** `TryBeginServing()` failing for a reason *other*
  than `PipeBusy` (i.e. `AclFailed`/`Faulted`) must not be treated the same as a genuine competing
  primary: the process legitimately holds the guard and must stay primary — just without a working
  activation channel — rather than release a guard it rightfully owns and report a misleading
  "already running" failure to the user. `SingleInstanceCoordinator.Start()`'s loop now only releases
  the guard and falls back to the secondary path when `LastStartFailure == PipeBusy`; any other reason
  keeps `acquired = true` and lets the process run as a (diagnostically-flagged, via
  `ActivationServerUnavailable`) degraded primary.
- **MAJOR — top-level guard around the handshake itself (M-4).** Nothing upstream of `Program.Main`
  catches an exception thrown while building the DI container or driving the handshake (unlike the WPF
  SDK's own auto-generated `Main`, which runs inside `Application`'s own exception machinery). Without a
  `try/catch` in `Main` itself, a failure (e.g. `UnauthorizedAccessException` opening the guard mutex)
  would make the process vanish silently with a CLR-default exit code. Fixed with a top-level
  `try/catch` in `Program.Main` that shows a `MessageBox` and returns exit code 1.
- Two further MAJOR findings (named-pipe server stream lifecycle details, response-before-handoff
  sequencing) — folded into the same accept-loop hardening as the pipe-instance-leak fix above; see
  commit `c3ac4f3`.

`fences-ux-parity-reviewer` found no regressions: ordinary/single-instance launch behavior (tray icon,
panel load, desktop gestures) is unchanged.

## Invariants

- secondary never opens writable state store, never resolves `IDiagnosticsLog`, never constructs `App`;
- IPC message set is tiny (`DO/1`, one command) and versioned; the server always answers, never closes
  silently;
- a process that legitimately holds the guard mutex stays primary even if the activation channel itself
  could not start (degraded primary, D-4 deviation above) — it never releases a guard it rightfully
  owns just because the pipe failed to come up;
- failure to contact a stale/hung primary after the 3s handshake budget never starts a second writable
  instance (D-7) — it fails closed with a user-facing message instead;
- the guard mutex is released last, after the activation server has stopped and all shutdown-path writes
  (`SaveWindowsStateNow`, `SystemIconsPanel.Save`) have completed (D-9).

## Consequences

- One extra OS mutex handle per process for the life of the app (D-1's detection/guard split).
- The process entry point is now `Program.Main`, not WPF's auto-generated one; any future change to
  `DesktopOrganizer.UI.csproj`'s `<StartupObject>`/`ApplicationDefinition` wiring must preserve "a
  secondary process never constructs `App`" (R-1) or this ADR's central invariant is silently undone.
- `installer/DesktopOrganizer.iss`'s `#define AppRunningMutexName "DesktopOrganizerSingleInstanceMutex"`
  and `#define AppShutdownEventName "DesktopOrganizerShutdownRequestEvent"` values (formerly a single
  `[Setup] AppMutex=` directive — see D-10 addendum) are now a cross-repo contract enforced by
  `InstallerMutexContractTests`, not just a comment — renaming
  `SingleInstanceNaming.InstallerDetectionMutexName`/`.InstallerShutdownRequestEventName` without
  updating the installer breaks the test.
- **R-15 — residual risk, deferred to Phase 4.** The pipe tie-breaker (D-4) closes the "two primaries in
  different FUS sessions of the same user" race at the *process* level, but does **not** add a file-level
  lock around `PanelItemStorageService`'s storage root. In the narrow window between a degraded-primary
  process (guard acquired, pipe unavailable — the ACL/Faulted branch of D-4) and a genuinely separate
  primary in another session, both could still open and write the same JSON storage concurrently. Real
  protection is a storage-root file lock, planned for Phase 4 (`ADR-024`, versioned JSON persistence).
  This phase closes `DO-001` at the process-lifecycle level; R-15 is the explicitly accepted remainder.
- Manual verification on live Windows (FUS/multi-session behavior M-9/M-10, installer prompt M-7,
  foreground-activation M-3, abandoned-mutex-after-kill M-5, the full 20-launch stress matrix M-1/M-12)
  is still outstanding — see `docs/Refactoring/Testing/single-instance-manual-matrix.md`.

## Not done in this phase (deliberately)

- No storage-root file lock in `PanelItemStorageService` (Phase 4, `ADR-024`) — see R-15 above.
- Protocol is not extended beyond one command (D-5); no command-line argument pass-through.
- `ShutdownMode`, tray behavior, autostart, and `user.config` format are unchanged.
- `DesktopOrganizer.Native` (Phase 8) is untouched — all new P/Invoke goes through the existing
  `INativeApi`.
- The two new user-facing messages are English-only; localization is a separate cross-cutting task.
  **Superseded by [ADR-032](ADR-032-ui-localization.md)** (2026-09-27) — see the amendment below.

## Amendment (2026-09-22, P08 fix cycle 2): activation wiring's UI marshal is fire-and-forget

**Finding (MAJOR M-3):** D-8's `SingleInstanceActivationHandler.Handle` is invoked on
`NamedPipeActivationServer`'s own accept thread (via `ActivationRequestRelay.Publish`/`AttachSink`,
both of which run the sink while holding their internal lock — see those types' own remarks).
`App.xaml.cs`'s production wiring originally marshalled the resulting `WindowManager.ShowMainWindow()`
call onto the UI thread with the BLOCKING `UiDispatch.TryInvoke`. Two consequences, both timing- rather
than correctness-affecting, and both closed by the same one-line fix:

- **Delayed exit.** D-9's shutdown sequence calls `ISingleInstanceCoordinator.CompleteShutdown()` →
  `NamedPipeActivationServer.Stop()` → a bounded, **2-second** `Thread.Join` on the accept thread — a
  plain `Thread.Join`, which does **not** pump the UI thread's dispatcher. An activation arriving on the
  accept thread while `App.OnExit` is inside that join could not be serviced until the join gave up,
  needlessly delaying application exit by up to its full 2-second budget.
- **A theoretical relay deadlock.** Because `Publish`/`AttachSink` hold `ActivationRequestRelay`'s
  internal gate for the duration of the sink call, a blocking sink held that gate for as long as the UI
  thread took to service it — a hazard for any future change that might need the SAME gate from the UI
  thread's own call stack while a `Publish` is in flight (the single accept thread and the one-time
  `AttachSink` subscription in today's topology do not currently reach this, but the fix removes the
  precondition entirely rather than relying on that staying true).

**Fix:** `App.xaml.cs`'s activation wiring now uses `UiDispatch.TryBeginInvoke` (fire-and-forget)
instead of `UiDispatch.TryInvoke`. This changes nothing about the wire protocol or D-8's invariant — the
`DO/1` response is written, flushed and drained **before** `NamedPipeActivationServer.HandleConnection`
ever hands the request off (D-5/D-8, "answer first, hand off after"), so the accept thread was never
waiting on `ShowMainWindow()` for the client's own round-trip; it only needed to stop waiting on it for
its OWN sake (the next loop iteration / `Stop()`'s join).

**Invariant added (project-wide, not ADR-023-specific):** every background-thread → UI-thread marshal in
this codebase goes through `DesktopOrganizer.UI.Services.UiDispatch`, and defaults to the non-blocking
`UiDispatch.TryBeginInvoke` unless the calling code genuinely needs to observe the UI-thread work
completing before it proceeds. See `docs/Refactoring/Testing/sta-test-conventions.md` rule 4 for the
full rationale (including the unrelated `ChildWindowViewModel.OnFolderChanged`/`FolderWatcher` deadlock
this same review found and fixed) and the regression tests
(`Unit/Services/UiDispatchTests.cs`, `Unit/SingleInstance/ActivationRequestRelayTests.cs`).

## Addendum (2026-09-25): D-10 — installer-initiated graceful shutdown replaces `[Setup] AppMutex=`

**Problem.** `installer/DesktopOrganizer.iss` used to declare `[Setup] AppMutex=DesktopOrganizerSingleInstanceMutex`
so Inno Setup could refuse to proceed while the app was running. That directive gives Inno its own
hardcoded, non-closable "close the application and click OK to continue" prompt with **no `[Code]` hook
to intervene** — the user has to go close the app by hand (tray → Exit) and click OK themselves; a
silent/unattended install/uninstall (`/VERYSILENT`) simply fails outright if the app is running, since
there is nothing `[Code]` can do to close it first.

### D-10. Installer asks, then closes the running instance itself via a named event, then proceeds

`AppMutex=` is removed from `[Setup]`. In its place:

- `#define AppRunningMutexName "DesktopOrganizerSingleInstanceMutex"` — the **same** detection-mutex
  literal D-1 already defined (verbatim, unprefixed, existence-only), now consumed from `[Code]`
  (`IsAppRunning` / `CheckForMutexes`) instead of from `[Setup]`. The object and its contract are
  unchanged; only the consumer moved from Inno's built-in blocking check to a scriptable one.
- `#define AppShutdownEventName "DesktopOrganizerShutdownRequestEvent"` — a new, session-scoped,
  unprefixed named `EventWaitHandle` (`AutoReset`). `[Code]`'s `RequestGracefulShutdown` opens it with
  `EVENT_MODIFY_STATE` and calls `SetEvent`; the running Primary's `IShutdownRequestListener`
  (`ThreadPool.RegisterWaitForSingleObject`, `executeOnlyOnce: true`, started only by Primary from
  `App.InitTrayIcon`, ADR-023 D-2/D-3) wakes up and marshals onto the UI thread via
  `UiDispatch.TryBeginInvoke(dispatcher, Shutdown)` — **the same `Application.Shutdown()` → `OnExit`
  path the tray "Exit" command already uses** (state save, desktop icon restore, single-instance guard
  release per D-9). No new shutdown path was introduced; the installer just gained a way to trigger the
  existing one remotely.
- `EnsureAppClosed` (shared by `PrepareToInstall` for install/upgrade and `InitializeUninstall` for
  uninstall) is the ask → signal → wait loop: prompt (`SuppressibleMsgBox`, degrades to the *default*
  button only under `/SILENT`or`/VERYSILENT` **plus** `/SUPPRESSMSGBOXES`, per Inno's own documented
  contract) → `RequestGracefulShutdown` → `WaitForAppExit` (polls `IsAppRunning`, then additionally waits
  for the `.exe` file itself to become writable, since releasing the detection mutex in
  `_serviceProvider.Dispose()` and releasing the OS's hold on the file handle are not quite the same
  instant) → on success, proceed; on failure, offer Retry/Cancel. A fully unattended run
  (`/VERYSILENT /SUPPRESSMSGBOXES`) closes the running instance with no UI at all, and — new — relaunches
  the app after install if `EnsureAppClosed` is the thing that closed it (`AppWasClosedByInstaller` flag,
  `CurStepChanged`/`ssPostInstall`, `ExecAsOriginalUser` with `ewNoWait`), since the `[Run]` section's own
  postinstall launch entry is `skipifsilent` and would otherwise leave a silently-upgraded install with
  nothing running.
- Restart Manager (`CloseApplicationsFilter=*.exe,*.dll`) is kept as belt-and-braces alongside D-10, not
  replaced by it — it catches any other handle (not the main `.exe`) that might still be locking a file
  DO-10 does not know about.

**Contract enforcement.** `InstallerMutexContractTests` now parses `#define AppRunningMutexName`/
`#define AppShutdownEventName` (previously only the `AppMutex=` literal) against
`SingleInstanceNaming.InstallerDetectionMutexName`/`.InstallerShutdownRequestEventName`, asserts
`[Setup]` no longer contains an `AppMutex=` directive (regression guard against silently reinstating the
old blocking prompt), and asserts no line inside `[Code]` starts with `[` after trimming (guards against
a `[Code]`-comment line being misparsed by Inno's line-based section scanner as a new section header —
a real compile failure hit during this change).

**Accepted risks:**

- **Any process running as the same user, in the same interactive session, can signal
  `DesktopOrganizerShutdownRequestEvent`** — it is an unprefixed, session-scoped, discoverable-by-name
  event, and `SetEvent` only needs `EVENT_MODIFY_STATE`. Effect is limited to "the app closes gracefully
  through its normal Exit path" (a DoS at worst, never data loss — the same shutdown sequence as tray
  Exit runs every time). Accepted because a same-user, same-session process that wants to be disruptive
  already has strictly more powerful options (killing the process outright, or emulating any UI
  interaction); this adds no new capability, only a slightly more polite one.
- **Older installed versions predate `IShutdownRequestListener`.** `OpenEventW` on a pre-D-10 running
  instance returns a null handle (nothing was ever listening), so `RequestGracefulShutdown` fails and
  `EnsureAppClosed` falls through to its Retry/Cancel prompt (`AppRunningCloseFailedRetry`: "close it
  manually via tray → Exit, then click Retry") — i.e. D-10 degrades to exactly the pre-existing
  manual-close UX for anyone upgrading from an older build, never to a hard failure or a silent skip.
- **Restart Manager remains a second, independent line of defense**, not a fallback D-10 depends on —
  it is unaffected by this change and continues to catch file-handle holders D-10's own mutex/event
  contract does not know about.

**Status:** Accepted. No change to ADR-024's persistence contour — `App.Shutdown()` runs the exact
existing `OnExit` sequence; no new state, schema, or settings key was added.

## Amendment (2026-09-27): line 281 "English-only" superseded by ADR-032

[ADR-032](ADR-032-ui-localization.md) introduces a hot-switchable ru/en localization infrastructure and
explicitly revisits the "The two new user-facing messages are English-only; localization is a separate
cross-cutting task" note above (line 281). Both messages
(`Startup_OtherSession_Message`/`Startup_ActivationFailed_Message` — see ADR-032's implementation section
and `docs/PLANS/localization.md` §2.8) are now localized RESX keys resolved against
`CultureInfo.CurrentUICulture` in `Program.cs`, before DI/`ApplicationStateService.Load()` run. No other
decision in this ADR (D-1..D-10) is revisited.
