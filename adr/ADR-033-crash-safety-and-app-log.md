# ADR-033: Crash Safety and an Always-On Application Log

**Статус:** Accepted (2026-09-27) — Amended 2026-09-30 (ADR-047: крэш/старт/CLI `MessageBox` остаются намеренно вне единой дизайн-системы — минимум зависимостей до/после WPF-UI); Amended 2026-10-09 (ADR-audit WP-C + review cycle 1: операционные Warn/Error конвейера иконок дублируются в `IAppLog`, повторяющиеся события ограничены `AppLogEventThrottle`)

## Context

Before this branch the project had exactly one logging abstraction — `IDiagnosticsLog`/`IGestureDiagnostics`
(ADR-027), off by default in Release, on only under `--diag`/`DESKTOPORGANIZER_DIAG=1`, and scoped
narrowly to gesture-lifecycle tracing. There was no crash handler at all: an unhandled exception on the
UI thread crashed straight through WPF's default handling, desktop icons could be left hidden forever
(the toggle lives entirely in the shell's own `SHELLDLL_DefView` state, never persisted — session-only by
design), and nothing was ever written to disk describing what happened. A tester hitting a crash had no
artifact to attach to a bug report beyond "it closed".

The 1.0 release-hardening plan (`docs/PLANS/release-hardening-1.0.md` §1–§2) bundled two waves under one
architectural umbrella because the crash handler's own log write depends on the first wave's log
infrastructure existing: package B built an always-on (Release-default) app log, and package A built the
crash sequence on top of it.

## Decision

### Two diagnostics levels, one sink

`DiagnosticsLevel { Standard, Verbose }` (`DesktopOrganizer.Services.Diagnostics.DiagnosticsSwitch`):
`Standard` is the Release default (`DiagnosticsDefaults.FileLoggingEnabled = true`, a compile-time
constant — there is deliberately no user-facing setting to turn the always-on log off in a Release build);
`Verbose` is the Debug default, or `Standard` overridden by `--diag`/`DESKTOPORGANIZER_DIAG=1`. The
existing `JsonLinesDiagnosticsLog` (ADR-027's sink — rotation, retention, non-blocking producer contract
all unchanged) is now the **one** sink for both concerns: `IDiagnosticsLog`'s own semantics are untouched
(resolves to `NullDiagnosticsLog` in `Standard`, so gesture-lifecycle tracing still only runs under
`Verbose`, exactly as ADR-027 specified), while a new facade always writes to the same sink regardless of
level.

### `IAppLog` facade and ambient access

`IAppLog` (`Services.Abstractions`): `Info`/`Warn(string source, string evt, params KeyValuePair<string,
object?>[] data)`, `Error(string source, string evt, Exception? ex, params …)`. `DiagnosticsAppLog` is the
production implementation — writes to the shared sink under category `app`, after
`LogScrubber.Scrub(string)` has replaced rooted/UNC paths with `<path:.ext>` and the user profile/username
with `%USERPROFILE%` (same privacy invariant ADR-027 already established for the `payload` fields: no
titles, file names, or paths ever reach the log verbatim).

Not every call site is DI-resolvable — static helpers, `Program.Main` before the container exists, and a
handful of files inside the frozen persistence contour (ADR-024) that only ever swap a bare
`Console.Error.WriteLine` for a log call, with no other change to control flow. For those, `AppLog.Current`
(`Services/Diagnostics/AppLog.cs`) is a lazily-initialized ambient static — `NullAppLog` until
`AppLog.Initialize(IAppLog)` is called exactly once from `Program`, matching the same "no eager sink
materialization" discipline ADR-023 R-12 established for the diagnostics log. DI-resolvable classes outside
the frozen contour take `IAppLog? log = null` in their constructor and fall back to `AppLog.Current` lazily
— existing tests that construct a type without a log keep working unchanged.

### Crash sequence (`CrashCoordinator`)

Installed as early as possible:

- `Program.Main`, before `Application` exists: `AppDomain.CurrentDomain.UnhandledException` (background
  threads — only logs `Error` and calls `SetObserved()`; the process still terminates, .NET 8's own
  default), `TaskScheduler.UnobservedTaskException` (same treatment, non-fatal), and
  `WinFormsExceptionBridge.Install` — switches WinForms into `UnhandledExceptionMode.CatchException` and
  subscribes `Application.ThreadException`, so an exception thrown from the tray icon's `NotifyIcon`
  callback (the only WinForms surface this WPF app has) reaches this application's own crash handling
  instead of WinForms' own `ThreadExceptionDialog`.
- `App`'s constructor: `DispatcherUnhandledException` → `e.Handled = true` → routes into the same
  `CrashCoordinator.HandleFatal`.

`CrashCoordinator.HandleFatal(Exception, CrashOrigin)` (`UI/Lifecycle/CrashCoordinator.cs`) is a single
`Interlocked`-guarded, idempotent sequence — every dependency is a plain delegate/interface captured by the
caller ahead of time (never a fresh DI resolve during the crash itself), and every step runs inside its own
try/catch (`TryRun`) so a failure in one step can never skip the rest:

1. `IDesktopIconsService.SetIconsVisible(true)` — the user gets their desktop back unconditionally.
2. `CrashReportWriter` writes `Logs\crash-yyyyMMdd-HHmmss-<pid>.log` (UTC time, `AppVersionInfo.Full`, OS
   version/build, runtime description, UI culture, origin, scrubbed exception chain — type/HResult/
   message/stack, retention 10 files, direct `File.WriteAllText`, never through the JSONL queue) plus an
   `IAppLog.Error` event, then flushes the log so neither is lost if the process is about to die anyway.
3. **State is only saved for a UI-thread origin** (plan Q1 default, no escalation needed — this default
   adds no new save point): `CrashOrigin.Dispatcher`/`WinForms` run the existing idempotent
   `ShutdownSequence` (which already calls `ApplicationState.SaveNow()`); `CrashOrigin.Background` means
   `AppDomain.UnhandledException` already caught this on a genuine background thread with the CLR about to
   terminate the process regardless — attempting a save there would race the process's own teardown for no
   benefit.
4. A short localized dialog (`Crash_Dialog_Title`/`Crash_Dialog_Message`) offering to open the logs folder.
5. `Environment.Exit`-equivalent with `ExitCodes.Crashed = 5` (documented on `Program.Main`'s exit-code
   comment alongside the pre-existing 0–4).

### One shutdown sequence, three triggers

`ShutdownSequence` (`UI/Lifecycle/ShutdownSequence.cs`) wraps the exact steps `App.OnExit` used to run
inline into one `Interlocked`-guarded, run-at-most-once sequence, with each step in its own try/catch (a
step's own "never throws" contract is a design intent, not a guarantee the shutdown path may rely on).
`App.OnExit`, `Application.SessionEnding` (WPF's own signal) and `Microsoft.Win32.SystemEvents.SessionEnding`
(the OS-level insurance fallback, marshalled onto the UI thread) all call the same `RunShutdownSequence()` —
whichever reaches it first does the real work; the others are silent no-ops (plan Q2 default: SessionEnding
runs the existing sequence earlier, no new persisted state, no new record ever written twice).

### Startup self-heal

Icon visibility is session-only by design (never persisted) — if the previous process was killed while
icons were hidden (`taskkill /f`, a power loss, a crash origin that skipped the normal exit path), nothing
in this application's own state says so. `StartupIconsSelfHeal.Apply` (`UI/Lifecycle/StartupIconsSelfHeal.cs`)
acts only on positive evidence: `SHELLDLL_DefView` was actually found and is
`DesktopIconsVisibility.Hidden`. It never overrides Explorer's own "Show desktop icons" being off (that's
the user's own Explorer-level choice, left alone), and it does nothing when `SHELLDLL_DefView` does not
exist yet (autostart at logon, before Explorer has built the desktop).

## Consequences

Positive:
- Every crash, on either thread, leaves a scrubbed, attachable artifact (`crash-*.log`) plus an `app`-log
  entry, restores the user's desktop icons, and exits with a documented code — closing the "it just closed"
  gap this ADR's Context describes.
- The always-on Release log (`Standard` level) means an ordinary error/warning no longer requires
  `--diag` to be reproduced from a tester's machine — only gesture-lifecycle tracing (ADR-027) still does.
- `ShutdownSequence`'s single idempotent instance closes a class of bugs before they could exist: no matter
  which of `OnExit`/`SessionEnding`/a UI-thread crash reaches it first, the save/icons/dispose steps run
  exactly once.
- Zero new persisted state: icon-visibility intent, diagnostics level, and the crash sequence's own guard
  flag are all process-memory-only (ADR-024 freeze respected in full).

Negative:
- A background-thread crash (`CrashOrigin.Background`) still terminates the process with .NET 8's own
  default behavior and exit code — `CrashCoordinator` only gets to run its icons/report/log steps, never
  the save step, and never gets to call `Shutdown` itself.
- Two logging facades now coexist (`IDiagnosticsLog` for verbose gesture tracing, `IAppLog` for always-on
  app events) sharing one physical sink — a future consumer must pick the right one rather than there being
  a single obvious logging call.
- `AppLog.Current`'s ambient-static seam is required for the 4 frozen-persistence-contour files (Q3 default:
  yes, swap `Console.Error` for a log call with no control-flow change) and for `Program` before DI exists —
  a small but real exception to "always inject", scoped and documented at each use.

## Related

- ADR-027 (Structured JSONL Diagnostics Logging) — the sink and non-blocking producer contract this ADR's
  always-on log reuses unchanged; `IDiagnosticsLog`'s own gesture-tracing semantics are untouched.
- ADR-023 (Single Instance and Activation IPC) — exit code 5 is documented alongside the pre-existing 0–4
  codes on `Program.Main`; R-12's "no eager sink materialization for a Secondary instance" discipline is
  reused for `AppLog.Current`.
- ADR-034 (Explorer Restart Recovery) — `IAppLog` is the log every new package from this point on writes
  to; `ShellRecoveryCoordinator` logs through it.
- `docs/PLANS/release-hardening-1.0.md` §1 (package B), §2 (package A).

## Amendments

| Date | Source | What changed | Which sections/decisions no longer apply |
|---|---|---|---|
| 2026-09-30 | [ADR-047](ADR-047-unified-design-system.md) (D-9) | The crash dialog, startup and CLI messages deliberately stay plain `MessageBox` — they are **not** moved to `AppDialogWindow`/`InfoBar`, to keep the dependency surface minimal before and after the WPF UI exists | — (the "dialog" step of the crash sequence is unchanged; informational `MessageBox`es elsewhere in the app were replaced by `AppDialogWindow`/`InfoBar`) |
| 2026-10-09 | ADR-audit WP-C + review cycle 1 ([ADR-046](ADR-046-background-icon-loading.md)) | Operational Warn/Error events of the icon pipeline (`StaIconLoadQueue`, `BackgroundItemProbeQueue`) are duplicated into `IAppLog`, so they survive without `--diag`; a per-event, per-session budget (`AppLogEventThrottle`, K = 5, then one `LogEventSuppressed` Warn naming the event and the limit, then counting only) keeps a failure that repeats per icon / probe / window message from filling the always-on log; the same class with K = 1 and no notice backs the once-per-session Z-order diagnostics ([ADR-049](ADR-049-desktop-band-top-level-fence-panels.md)) | — (additive; for throttled Error sources the always-on log now holds the first K occurrences per event name, not every occurrence) |

## Amendment (2026-10-09): icon-pipeline events in the app log, bounded by `AppLogEventThrottle`

Source: the ADR audit (WP-C) and its review cycle 1 (G4). Behaviour only — no change to the sink, the level model, the
scrubber or `IAppLog`'s members.

- **What is duplicated.** The icon pipeline's operational events used to reach only the diagnostics trace (`--diag`).
  `StaIconLoadQueue` now also writes its Warn-level events (`IconWorkerAbandoned`, `IconLaneDisabled`,
  `RemoteRootQuarantined`, …) into `IAppLog` under source `StaIconLoadQueue`; `BackgroundItemProbeQueue` writes
  `ItemProbeFailed`. Payloads stay closed-set (lane, root kind, extension; error type) — the privacy invariant of this
  ADR is unchanged.
- **Merged event.** `IconJobTimedOut` and `IconWorkerAbandoned` are one incident (a stuck job ⇒ its worker is abandoned).
  The diagnostics trace keeps both events (schema `docs/Refactoring/Testing/diagnostics-log-events.md`); the app log gets
  **one** `IconWorkerAbandoned` Warn carrying `lane`, `extension` and `abandoned`.
- **Throttle.** Error-level events (Freeze-invariant violation, sink/watchdog exception, probe failure) pass through
  `AppLogEventThrottle` (`Services/Diagnostics/AppLogEventThrottle.cs`): the first `K = 5` (`DefaultLimitPerEvent`)
  occurrences of an event name are written, the next one is replaced by a single `LogEventSuppressed` Warn
  (`event`, `limit`), every later one is only counted (`SuppressedCount`). The budget lives as long as the owning
  instance (a DI singleton or process-wide static ⇒ the session). Thread-safe, never throws, **not** for window procedures
  or `WH_MOUSE_LL` callbacks (an admitted entry is written synchronously). The Warn-level icon events (`IconWorkerAbandoned`,
  `IconLaneDisabled`, `RemoteRootQuarantined`) are written directly, not through the throttle.
- **Other users.** `DesktopIntegrationService` and `DesktopZOrderDiagnostics` use the same class with
  `limitPerEvent: 1, announceSuppression: false` for once-per-session diagnostics (ADR-049).
