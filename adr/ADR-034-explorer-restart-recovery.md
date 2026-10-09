# ADR-034: Explorer Restart Recovery

**Статус:** Accepted (2026-09-27) — amends ADR-001, ADR-019/ADR-020; Amended 2026-10-09 (ADR-031 amendment:
`IDesktopIconsService.VisibilityChanged`, tray checkmark is now event-driven — see the end of this document);
Amended 2026-10-09 (ADR-audit WP-D: top-level fence panels are held in the desktop band — `WM_WINDOWPOSCHANGING`
filter + `KeepInDesktopBand`; corrects the former "kept back by the ADR-020 timer" claim; **the mechanism is now an
ADR of its own: [ADR-049](ADR-049-desktop-band-top-level-fence-panels.md)**)

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
re-parented at all, they are **top-level** windows, not `WorkerW` children. (Correction 2026-10-09, ADR-audit
WP-D: this paragraph used to claim they were kept at the back of the Z-order by "the existing
`SendToBackOfZOrder` timer (ADR-020)" — that timer only ever served the system icons sidebar; fence panels
were pushed back once, during recovery, and otherwise not at all. What actually holds them in the desktop band
now is described in the amendment "2026-10-09: top-level fence panels are held in the desktop band" at the end
of this document.) Recovery must produce a good outcome in both layouts without knowing in advance which one it
is running on.

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
panels are, and remain, top-level windows — this is not a bug in this recovery package, it is the documented
behavior of the current OS layout. They are held in the desktop band (above wallpaper/icons, below application
windows) by the event-driven mechanism of the 2026-10-09 amendment below — **not** by ADR-020's sidebar timer,
as an earlier revision of this paragraph claimed. See ADR-037 for the full two-mode DPI consequence.

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
window open. See `docs/PLANS/release-hardening-1.0.md` §4 DoD for the full checklist. Status of the manual
verification (Windows 11 24H2, 2026-09-27; Windows 10 is not checked separately) — `docs/ROADMAP.md`,
«Сквозные задачи».

## Related

- ADR-001 (Desktop Integration) — amended: the `WorkerW`-child assumption only holds on Windows 10/≤23H2.
- ADR-019/ADR-020 (sidebar top-level/Z-order) — `ReassertAfterShellRestart` reuses the same Z-order anchor
  logic these ADRs established.
- ADR-033 (Crash Safety and App Log) — `IAppLog` is what this package logs recovery outcomes through.
- ADR-037 (PerMonitorV2 + Dual-Mode Desktop Scale) — `IDesktopScaleProvider.Refresh()`/`BeginBatch()`,
  consumed at the end of `RecoverDesktopLayer`; the top-level-mode outcome this ADR documents is the same
  mode ADR-037 calls "T".

## Amendment (2026-10-09): desktop-icons visibility is now an event (ADR-031 amendment)

`IDesktopIconsService.VisibilityChanged` is raised by `SetIconsVisible`/`Toggle` and by a **successful**
`ReapplyDesiredVisibility()` (the "hidden" intent re-applied to the new `SHELLDLL_DefView`, step 3 above;
not raised when it returns `false` and will be retried). Subscribers keep the tray checkmark
(`App.xaml.cs`, guarded by `Dispatcher.CheckAccess()`) and the Settings → General toggle
(`MainWindowViewModel.DesktopIconsVisible`) in sync, so the explicit tray resync in step 2 is now a safety
net rather than the only path. The recovery algorithm, the session-only `DesiredVisible` intent and "zero
new persisted state" are unchanged (ADR-034 intent preserved). Changes made by Explorer itself are still
not observed.

## Amendment (2026-10-09): top-level fence panels are held in the desktop band (ADR-audit WP-D)

> The decision below was later extracted into [ADR-049](ADR-049-desktop-band-top-level-fence-panels.md), which is now the
> primary description of the mechanism (definition of the band, rejected alternatives, diagnostics, manual verify). The
> text here is kept as the original amendment; if the two ever differ, ADR-049 wins.

**Defect.** The ADR audit found that in mode T (Windows 11 24H2+, no `WorkerW`, `EmbedInDesktop` →
`NoDesktopLayer`) nothing kept fence panels at the back of the Z-order: `SendToBackOfZOrder`'s 300 ms timer
exists only in `SystemIconsPanelManager` (sidebar, ADR-020), and `ChildWindow` called `SendToBackOfZOrder` once,
only from shell-restart recovery. A new, re-created or page-switched panel (a fresh HWND is linked at the top of
the Z-order; `Show()` keeps it there) appeared **above** the user's application windows and sank only as other
windows were activated.

**Decision — event-driven, no timer, no global hook** (`ChildWindow.ZOrder.cs`, attached by `WindowManager`
next to `AttachShellRecoveryTracking`, before the first `Show()`):

1. **`WM_WINDOWPOSCHANGING` filter** on the panel's own `HwndSource` →
   `IDesktopIntegrationService.ConstrainWindowPosChangingToDesktopBand`. When the panel itself is about to change
   Z-order (or be shown — `SWP_SHOWWINDOW`), the proposed `hwndInsertAfter` in `WINDOWPOS` is rewritten in place
   to the top of the desktop band (plus `SWP_NOOWNERZORDER`, ADR-020), or the Z-order half is vetoed with
   `SWP_NOZORDER` when the panel already is inside the band. Move/resize frames (`SWP_NOZORDER`) and hide
   requests return after one structure read. Covers `ShowWindow`'s raise, activation during inline rename and
   anything else that raises the panel — before it happens, so there is no flash.
2. **Deferred re-assert after every transition to visible** (`IsVisibleChanged` → `Dispatcher.BeginInvoke` at
   `Loaded`) → `IDesktopIntegrationService.KeepInDesktopBand`: the safety net for a freshly created window (linked
   at the top without any `WM_WINDOWPOSCHANGING`) and for a show the filter could not redirect. Idempotent: zero
   `SetWindowPos` calls when the panel already is in the band.
3. Shell-restart recovery (`ChildWindow.RecoverAfterShellRestart`, `NoDesktopLayer` branch) now calls
   `KeepInDesktopBand` instead of `SendToBackOfZOrder`.

**Desktop band** = the contiguous run of windows directly above `Progman` (walked with `GW_HWNDPREV`) made of
Explorer's `WorkerW` siblings and **this process's** windows carrying `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`
(fence panels — including hidden ones of other pages — and the sidebar). A panel inside the run stays put; a
panel outside it is inserted directly below the first window above the run (or at `HWND_TOP` when the run
reaches the always-on-top windows / the top of the list). Our Settings window and dialogs lack
`WS_EX_NOACTIVATE` and end the run, so a panel never rises above them. Walk cap `MaxPanelBandWalk = 512`; no
`Progman` or cap exceeded → "unknown" → the Z-order is left alone. All reads are message-free (`FindWindow`,
`GetWindow`, `GetWindowLongPtr`, `GetClassName`, `GetWindowThreadProcessId`) and therefore safe inside a window
procedure; every native call stays behind `INativeApi` (new: `GetWindowProcessId`, `TryReadWindowPosZOrder`,
`WriteWindowPosZOrder`).

**Applicability is a fact, not an OS version**: every call first checks `GetAncestor(GA_ROOT) == hwnd`. A
`WorkerW` child (mode E, Windows 10/≤23H2) has Explorer's window as its root and is never touched; because the
check is made at message time, `SetParent`'s own internal repositioning (made after the new parent is linked) is
ignored as well.

**Rejected alternatives.**
- *Reuse the sidebar's 300 ms timer for N panels* — `SendToBackOfZOrder`'s single "one slot above `Progman`"
  target makes N windows (and the sidebar) take that slot from each other on every tick: endless Z-order churn,
  N × 3.3 `SetWindowPos`/s, and a periodic re-assert could also fight Win+D while the user looks at the desktop.
- *`HWND_BOTTOM` for everyone* — ADR-001 "Variant 2" / ADR-020: "below everything" is not "above the wallpaper".
- *`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`* — a second global hook next to `WH_MOUSE_LL` (ADR-020 Variant B);
  unnecessary anyway: another window being activated moves only that window, never the panel.
- *Re-assert only on `Show()`/`WM_ACTIVATE`* (alone) — leaves a one-frame flash on top and misses raises that are
  not activations; kept only as the safety net (part 2).

**Interaction with the sidebar (ADR-020).** The sidebar keeps its timer and its slot directly above `Progman`;
fence panels land at the top of the band, above it. The sidebar's tick finds itself in place (zero writes) and
the band walk treats the sidebar as a member — the two mechanisms never swap places.

**Win+D.** Unchanged: a panel reacts only to its own Z-order changes, never to other windows (or `Progman`)
being raised.

**Walk cap and diagnostics (ADR-audit review cycle 1).** `MaxPanelBandWalk = 512` bounds the `GW_HWNDPREV`
walk; it is generous because the run legitimately holds every fence panel of every page plus the sidebar, and
exists only so a corrupted/racing sibling list (Explorer restarting mid-walk) can never loop unboundedly inside a
window procedure. The "no decision" outcomes are now distinct (`DesktopBandPlacementKind.ProgmanMissing`,
`WalkCapExceeded`; `Unknown` only for the window being `Progman` itself) so that each is visible in the app log
— **once per session**, as `DesktopBandProgmanMissing` / `DesktopBandWalkCapExceeded`; the Z-order is still left
alone. Every previously empty `catch` of the mechanism now writes one Warn per session per kind
(`DesktopBandKeepFailed`, `DesktopBandFilterFailed`, `SendToBackFailed` in the service; `ZOrderHookInstallFailed`,
`ZOrderFilterFailed`, `ZOrderReassertFailed`, `ZOrderHookRemoveFailed` in `ChildWindow.ZOrder.cs`), carrying only
the exception type. Nothing is ever written from inside the `WM_WINDOWPOSCHANGING` hook: the service's filter
only ORs a flag (and keeps the last exception), which the next `KeepInDesktopBand` — UI thread, outside any
window procedure — writes; `ChildWindow`'s own hook boundary posts its single entry to the dispatcher at
`Background` priority the first time and does one interlocked exchange per failure after that. The `Progman`
lookup is not cached between calls: `FindWindow` on a class name is an in-kernel lookup, a cache would need a
dispatcher-frame notion in the Services layer, and a stale handle after an Explorer restart is exactly the case
this ADR exists for. `INativeApi.TryReadWindowPosZOrder`/`WriteWindowPosZOrder` no longer pretend to catch an
access violation (the .NET 8 runtime never delivers one to a managed `catch`): the `lParam` is valid by the
`WM_WINDOWPOSCHANGING` contract.

**Manual verify (Windows 11 24H2+).** New panel / page switch / re-created panel appears behind the active
application windows without a flash; clicking a panel still works and neither raises nor activates it; inline
rename still gets keyboard focus; several panels and the sidebar do not visibly reorder; the Settings window is
never covered by a panel; Win+D and Explorer restart behave as before; Windows 10 (mode E) unchanged.

## Amendments

| Date | Source | What changed | Which sections/decisions no longer apply |
|---|---|---|---|
| 2026-10-09 | [ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md) amendment | `IDesktopIconsService.VisibilityChanged`; tray checkmark and Settings toggle are event-driven, the recovery step-2 resync is a safety net | "the explicit tray resync is the only path" |
| 2026-10-09 | ADR-audit WP-D + review cycle 1, [ADR-049](ADR-049-desktop-band-top-level-fence-panels.md) | Top-level fence panels (mode T) are held in the desktop band by a `WM_WINDOWPOSCHANGING` filter + deferred `KeepInDesktopBand`; shell-restart recovery calls `KeepInDesktopBand` instead of `SendToBackOfZOrder`; diagnostics and the walk cap | The claim that mode-T panels are "kept at the back of the Z-order by the existing `SendToBackOfZOrder` timer (ADR-020)" — that timer serves only the sidebar |
