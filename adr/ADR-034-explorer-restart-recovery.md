# ADR-034: Explorer Restart Recovery

Status: Accepted (2026-09-27) — amends ADR-001, ADR-019/ADR-020

## Context

Panels are embedded into the desktop layer by re-parenting their `HWND` under Explorer's own `WorkerW`
(ADR-001). Nothing about that embedding survives Explorer itself being restarted (crash, `taskkill /f`,
"Restart" from Task Manager): the old `WorkerW`/`Progman` tree is torn down with the new one built fresh,
and until this package, the application had no way to notice — panels stayed either destroyed (if their
parent HWND died with it) or visually orphaned, and the sidebar/tray/virtual-desktop state could all drift
out of sync with the new shell instance. The README's "Known limitations" section used to read "panels may
need to be reopened manually" — this package replaces that gap with automatic recovery.

The release-hardening F0 DPI spike (`docs/PLANS/release-hardening-1.0-f0-results.md`) measured a second,
independent fact that this ADR's recovery must tolerate rather than assume away: on Windows 11 24H2+
(measured on build 26200), `DesktopIntegrationService`'s `WorkerW` search returns `0` — panels are never
re-parented at all, they are **top-level** windows kept at the back of the Z-order by the existing
`SendToBackOfZOrder` timer (ADR-020), not by window-tree membership. Recovery must produce a good outcome
in both layouts without knowing in advance which one it is running on.

## Decision

### Detection: a dedicated listener, decoupled from `MonitorService`

`IExplorerRestartMonitor`/`ExplorerRestartMonitor` (`Services.Abstractions`/`Services/Tools`) owns its
**own** hidden top-level `WS_POPUP` window (message-only windows do not receive broadcasts such as
`TaskbarCreated`), deliberately separate from `MonitorService`'s window — so package F's per-monitor-DPI
work can change one without touching the other. Two independent signals feed the same event, both handled
inside the one window procedure on the thread that called `Start()`:

- `RegisterWindowMessage("TaskbarCreated")` — the shell's documented broadcast. `AllowWindowMessage` is
  called for it (UIPI: an elevated process would otherwise silently drop the broadcast from a
  non-elevated Explorer).
- A `WM_TIMER`-driven poll of `FindWindow("Progman", null)` every 2 seconds — insurance against a missed or
  filtered broadcast; a changed, non-zero handle counts as "Explorer came back".

The window procedure does the minimum work (compare a message id, read one handle, raise an event) and is
wrapped in `try`/`catch`, always falling through to `DefWindowProc`, so a misbehaving subscriber can never
break message processing.

### Coalescing and bounded retries: `DesktopLayerRecoveryPolicy`

A pure, clock-free policy (`UI/Models/DesktopLayerRecoveryPolicy.cs`): every restart signal starts a new
generation and arms a 1.5 s trailing coalescing window (`CoalesceWindow`) — both detection paths firing for
the same restart, or several restarts in a row, still cost exactly one recovery. Once the window elapses,
attempts run every 500 ms (`RetryInterval`) up to `MaxAttempts = 6` per generation (≈2.5 s of retrying after
the first attempt, ≈4 s after the original signal) — bounded so a shell that genuinely never gets a
`WorkerW` back (the Windows 11 24H2+ layout) does not retry forever. A newer signal always supersedes
whatever generation is still pending or mid-retry. The caller (not the policy) owns time — it asks for a
delay, schedules the attempt itself, and reports back through `TryBeginAttempt`/`OnAttemptCompleted` —
keeping the policy deterministic and unit-testable without a dispatcher.

### Orchestration: `ShellRecoveryCoordinator`

`UI/Lifecycle/ShellRecoveryCoordinator.cs` turns the raw signal into the actual recovery, per attempt:

1. `IWindowManager.RecoverDesktopLayer()` — for every panel with a live `HWND`: re-embed
   (`IDesktopIntegrationService.EmbedInDesktop`, skipping `SetParent` if the parent is already the new
   `WorkerW`) and force-reapply Left/Top/Width/Height from the model (the single source of truth); for a
   panel whose `HWND` was destroyed along with the old parent, remove it without saving/releasing anything
   and recreate it fresh from the model. Returns a `DesktopLayerRecoveryResult` (embedded/topLevel/
   recreated/skipped/failed counts) that the coordinator uses to decide whether to keep retrying.
2. Only once the policy reports the recovery finished (`Completed` or `Exhausted`, never per-attempt):
   `IVirtualDesktopService.Reset()` (the old `IVirtualDesktopManager` RCW points at the dead Explorer
   instance and must be rebuilt against the new one before anything else talks to virtual desktops),
   `ISystemIconsPanelManager.ReassertAfterShellRestart()` (styles, Z-order, anchor), and a tray checkmark
   resync (`ITrayIconManager.SetDesktopIconsVisible`).
3. Every attempt also calls `IDesktopIconsService.ReapplyDesiredVisibility()` (the session-only "hidden"
   intent, since a fresh `SHELLDLL_DefView` is visible by default) and is isolated so an exception in one
   step never aborts the rest.

**M10 (release-hardening review finding).** `RecoverDesktopLayer`'s per-panel loop each calls
`IDesktopScaleProvider.ReportPanelWindow` (package F) — without batching, every one of those calls could
immediately rebuild the DPI snapshot and raise `Changed` mid-loop, letting a subscriber (a live panel
window further along in the very same loop, already destroyed but not yet rebuilt) write geometry against a
half-recovered window. `IDesktopScaleProvider.BeginBatch()` wraps the whole attempt so the provider defers
every dirty-marking call to a single rebuild (and at most one `Changed`) once every panel in that attempt
has already been re-embedded or recreated.

The `WH_MOUSE_LL` gesture hook needs no refresh: it is process-thread-local, not tied to Explorer, and
`DesktopGestureService` resolves the shell's windows afresh on every event rather than caching them. The
WinForms tray icon re-adds itself on `TaskbarCreated` on its own — only verified, not touched.

### Top-level mode is a documented outcome, not an error

When the retry budget is exhausted with `WorkerW` still not found (Windows 11 24H2+), the coordinator logs
`ShellRecoveryTopLevelMode` at `Info`, not `Warn`/`Error` — panels have already had their styles and
Z-order re-asserted by every attempt, and living top-level (with per-monitor DPI under package F, see
ADR-037) is the expected, working state on that shell layout, not a failure of recovery.

### Amendment to ADR-001/ADR-019/ADR-020

The F0 spike's key finding stands as an amendment to the architectural assumption underlying all three:
ADR-001's "Progman/WorkerW approach" and ADR-019/ADR-020's Z-order management assumed panels are always
`WorkerW` children. On Windows 11 24H2+, `DesktopIntegrationService.GetOrCreateWorkerW()` returns `0` and
panels are, and remain, top-level windows kept at the back of the Z-order purely by `SendToBackOfZOrder`
(ADR-020) — this is not a bug in this recovery package, it is the documented behavior of the current OS
layout. See ADR-037 for the full two-mode DPI consequence.

## Consequences

Positive:
- `taskkill /f /im explorer.exe && start explorer` (or a crash/"Restart" from Task Manager) now recovers
  automatically: panels re-embedded (or recreated), sidebar and tray back in sync, virtual-desktop cache
  fresh — closing the README's former "panels may need to be reopened manually" limitation.
- A burst of restart signals (both detection paths firing, or several restarts back to back) costs exactly
  one recovery, not one per signal.
- Zero new persisted state: mode, retry counters, and the coalescing generation are all in-memory only.

Negative:
- The retry budget (6 × 500 ms) is a compile-time constant tuned against the observed F0 timing, not a
  guarantee — an unusually slow shell restart could still exhaust it and fall through to the (harmless,
  logged-as-`Info`) top-level-mode path even on Windows 10/≤23H2, where `WorkerW` genuinely would have
  appeared given more time.
- Recovery is UI-thread-affine and single-instance by design (one `ShellRecoveryCoordinator`, one policy) —
  it does not attempt to recover mid-drag/mid-resize state; a panel gesture in flight during a restart is
  out of scope (manual verify only).

## Manual verify (Win10/Win11)

Graceful restart of Explorer from Task Manager and `taskkill /f` ×3 in a row; with panels on 2 monitors;
with a rolled-up panel; with "Show on all virtual desktops" enabled; during a panel drag; with the Settings
window open. See `docs/PLANS/release-hardening-1.0.md` §4 DoD for the full checklist.

## Related

- ADR-001 (Desktop Integration) — amended: the `WorkerW`-child assumption only holds on Windows 10/≤23H2.
- ADR-019/ADR-020 (sidebar top-level/Z-order) — `ReassertAfterShellRestart` reuses the same Z-order anchor
  logic these ADRs established.
- ADR-033 (Crash Safety and App Log) — `IAppLog` is what this package logs recovery outcomes through.
- ADR-037 (PerMonitorV2 + Dual-Mode Desktop Scale) — `IDesktopScaleProvider.Refresh()`/`BeginBatch()`,
  consumed at the end of `RecoverDesktopLayer`; the top-level-mode outcome this ADR documents is the same
  mode ADR-037 calls "T".
