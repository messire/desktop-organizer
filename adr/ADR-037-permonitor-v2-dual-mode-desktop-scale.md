# ADR-037: PerMonitorV2 and a Dual-Mode Desktop-Layer Scale

Status: Accepted (2026-09-27) — amends ADR-004, ADR-022 D-6, ADR-028; supersedes the "System-DPI-aware"
open question recorded in `docs/ROADMAP.md` "Сквозные задачи"

## Context

The application ran System-DPI-aware (no `app.manifest` at all) from Phase 0 through 0.6.1 — a deliberate,
explicitly-recorded deferral (ADR-004/ADR-022 D-6, ROADMAP "Сквозные задачи": *"полноценная per-monitor
DPI-осведомлённость... переход на Per-Monitor-V2 — отдельное архитектурное решение... требует отдельного
планирования"*). Package F is that planning finally being done.

The release-hardening plan's own F0 spike (`docs/PLANS/release-hardening-1.0-f0-results.md`) found the
gating question was not the one originally being checked ("do child windows get per-monitor DPI") — it was
more fundamental: **on Windows 11 24H2+ (measured on build 26200), panels are never `WorkerW` children at
all.** `DesktopIntegrationService.GetOrCreateWorkerW()` returns `0` on that layout; panels are top-level.
That single fact splits DPI behavior under PerMonitorV2 into two genuinely different regimes that a naive
"one desktop-layer scale" design (the plan's original assumption) could not serve correctly on both:

| Mode | Where (as measured/documented) | What panels are | DPI under PMv2 |
|---|---|---|---|
| **E (Embedded)** | Win10, Win11 ≤23H2 | children of `WorkerW` | one DPI — the top-level ancestor's (`WM_DPICHANGED_BEFOREPARENT`/`_AFTERPARENT` only, per Microsoft's own PMv2 documentation: a per-top-level-window unit) |
| **T (TopLevel)** | Win11 24H2+ (measured: build 26200) | top-level `WS_POPUP` | per-monitor (`WM_DPICHANGED` on the panel itself) |

This ADR records the orchestrator's decision from the plan's Q5/CLARIFICATION_NEEDED (`release-hardening-
1.0-f0-results.md` §6): **variant A, a two-mode implementation covering both regimes in 1.0**, rejecting
variant B (force embedding on 24H2+ — too large a regression risk on the OS this application's users are
actually on today), variant C (defer DPI work past 1.0 entirely), and variant D (top-level everywhere,
abandoning `WorkerW` embedding even on Win10 — out of scope for a hardening release).

## Decision

### One process-wide manifest, one snapshot abstraction

`src/DesktopOrganizer.UI/app.manifest` declares `<dpiAwareness>PerMonitorV2, PerMonitor</dpiAwareness>` +
`<dpiAware>true/pm</dpiAware>` (fallback chain to PerMonitor v1/System for pre-1703 Windows), `asInvoker`,
`supportedOS` Win10+ — wired via `<ApplicationManifest>` in `DesktopOrganizer.UI.csproj` only. All
px↔DIP geometry, everywhere in the codebase, now goes through exactly one snapshot type,
`DesktopScaleModel` (`Services.Abstractions`), served by `IDesktopScaleProvider`
(`Services/Tools/DesktopScaleProvider.cs`, internal singleton, UI-thread affine, never throws — a failed
rebuild keeps the previous snapshot).

### Mode detection is fact-based, not predicted

`DesktopScaleProvider` decides E vs. T from the actual embedding outcome, not from an OS-version guess:
`ReportPanelWindow(hwnd)` (called right after `IDesktopIntegrationService.EmbedInDesktop`) checks
`GetAncestor(hwnd, GA_ROOT)` — root differs from the panel's own HWND → **E** (`LayerScale` = the root's
DPI via `GetDpiForWindow`); root equals the panel's own HWND → **T**. Before any panel exists yet,
`IDesktopIntegrationService.FindDesktopLayerHost()` (a public wrapper over the same `WorkerW` search
package C's recovery uses) is consulted as a prediction; a later fact-based disagreement logs
`DesktopLayerModeMismatch` at `Warn` and the fact always wins.

### `DesktopScaleModel`: uniform fast path is a hard invariant, not an approximation

For a **uniform** snapshot (every monitor at the same scale — includes every single-monitor machine and
every "all monitors identical %" multi-monitor setup) the model uses the exact same formula 0.6.1 always
used: `dip = px / scale`, bit-for-bit, not the general anchored formula evaluated at equal inputs (which
could differ by one ULP). This is what makes the persistence-freeze invariant provable rather than just
plausible: `PersistedGeometryInvarianceTests` asserts a fixture of saved panel geometry, round-tripped
through the new placement/adapter code at `s ∈ {1, 1.25, 1.5, 2}` in both E and T, produces byte-identical
`Position`/`Size`/`MonitorDeviceId`/`Span` values to what 0.6.1 already had on disk — **no migration, no
schema/DTO/key change** (ADR-024 freeze fully respected).

For a genuinely **mixed-DPI** snapshot, mode T anchors each monitor's DIP rectangle to the edge facing the
primary monitor (`QF-1`, the plan's default: "grain to primary, `s_ref` = system DPI") rather than assuming
per-monitor DIP rectangles simply tile edge-to-edge at their physical boundary — a monitor whose scale
differs from the primary's would otherwise leave a gap or overlap in DIP space at that boundary. If
anchoring still produces overlapping DIP rectangles (exotic 3+ monitor layouts), the snapshot degrades to
the uniform formula using `ReferenceScale` (system DPI) — the same behavior 0.6.1 always had — and logs
`DesktopScaleAnchorOverlap` at `Warn`, rather than producing a geometrically inconsistent snapshot.

**E6 anchoring hypothesis — PENDING.** The F1 sub-plan's own experiment to confirm 0.6.1's actual observed
mixed-DPI behavior against this anchoring rule (`docs/PLANS/release-hardening-1.0-F-dpi.md` §6 F1.0) could
not run to a conclusive result: the only machine available during F1 has a single monitor at 100%, where
system-aware virtualization is a no-op and cannot distinguish hypotheses. QF-1's edge-anchoring rule is
therefore implemented as the documented **default**, not a confirmed match to 0.6.1's prior behavior on a
real two-monitor mixed-DPI rig — closing this is manual-verify work (see below), not a code gap.

### `IDesktopScaleProvider.BeginBatch()` — added beyond the original plan

Not in the original F sub-plan's interface sketch: `BeginBatch()` (a nesting-safe, dirty-flag-based batch
scope) was added during the review pass specifically for `ShellRecoveryCoordinator` (ADR-034), whose
`RecoverDesktopLayer` loop calls `ReportPanelWindow` once per panel — without batching, each call could
individually rebuild the snapshot and raise `Changed` mid-loop, letting an already-processed panel react to
geometry computed against a still-mid-recovery world. See ADR-034 for the full scenario; recorded here as
the interface-level consequence.

### `WM_DPICHANGED` and the WPF adapter layer

`TransitionLatch` (`UI/Interaction/TransitionLatch.cs`) guards against a reapply-triggers-another-DpiChanged
loop across `ChildWindow`/`PanelCustomizeWindow`: after a `DpiChanged` notification, geometry is reapplied
from the model **once**, suppressed while a drag/resize gesture or `PanelGeometryWriter.IsApplying` is
active. New thin adapters (`UI/Interaction/PanelWindowCoordinates.cs`, `TopLevelPlacement.cs`,
`DragPositionCalculator.cs`) are the **only** places `window.Left/Top` is read or written for
desktop-layer/top-level windows — all actual px↔DIP math stays inside `DesktopScaleModel` itself, testable
without WPF. `MonitorService` no longer divides by a scale itself; every geometry conversion goes through
`IDesktopScaleProvider.Current`, verified by a golden test against the exact 0.6.1 formula for uniform
snapshots, plus a literal-96/`GetSystemDpiScale` usage guard (`DpiLiteralGuardTests`) scanning `src/**/*.cs`.

### Known limitation, accepted for 1.0

In mode E (Win10, Win11 ≤23H2), panels on a monitor with a different DPI than the embedding layer's own
render at the **layer's** single scale, not their own monitor's — visually not true per-monitor rendering.
This is the same constraint 0.6.1 already had (system-DPI-aware panels were equally not per-monitor before
this package), now precisely characterized and documented (README "Known limitations", this ADR) rather
than left as an open architectural question. Mode T (Win11 24H2+) has no such limitation — panels there are
genuinely per-monitor, PMv2-native top-level windows.

## Consequences

Positive:
- The `app.manifest`/System-DPI-aware open question from ADR-004/ADR-022 D-6 is closed: the process is
  PerMonitorV2-aware on every supported OS, with a documented, tested fallback chain for older ones.
- Fixes ADR-034's own F0 finding (E5): a cross-process `SetParent` of a system-aware window into Explorer's
  PMv2 `WorkerW` used to force-reset the child's own DPI-awareness context irreversibly — a latent source of
  DPI artifacts on Win10 mixed-DPI setups. A PMv2-aware process removes that mismatch entirely.
- The uniform fast-path invariant is provable, not just documented: byte-identical geometry on every
  single-scale configuration, covered by an explicit regression test.
- Mode is determined by observed fact (`GA_ROOT`), so a future OS build that changes the `WorkerW` story
  again is handled by the same detection path, not a hardcoded OS-version branch.

Negative:
- Mixed-DPI geometry in mode T rests on the QF-1 anchoring hypothesis, unconfirmed against 0.6.1's actual
  prior behavior on real hardware (E6 pending — no two-monitor rig was available during development). A
  first real-world mixed-DPI upgrade could show a one-time panel position shift within its own monitor's
  clamp bounds; this is explicitly allowed for and documented (QF-2), not silently risked.
- Embedded mode's single-layer-scale limitation is unchanged from 0.6.1 — this package documents and tests
  it precisely, but does not remove it; genuine per-monitor rendering in embedded mode remains future work
  (variant B/D from the F0 CLARIFICATION_NEEDED).
- `IDesktopScaleProvider` is now a dependency of a wide swath of UI/geometry code (`MonitorService`,
  `WindowManager`, every panel window, the sidebar, `PanelCustomizeWindow`) — a regression here has a large
  blast radius; mitigated by the golden/invariance test suite, not eliminated by it.

## Manual verify (Win10/Win11)

See `docs/PLANS/release-hardening-1.0-F-dpi.md` §10 for the full ten-row matrix (single monitor 100%/150%;
mixed 100%+150% with the secondary right/left/above the primary; live scale change on the primary/secondary;
monitor disconnect/reconnect; upgrade from a real 0.6.1 mixed-DPI profile). Highest priority: rows #3/#4/#10
— comparing panel positions before/after this package on a real two-monitor mixed-DPI rig, which closes the
E6/QF-1 pending item above.

## Related

- ADR-004 (Phase 5 architecture) / ADR-022 D-6 — the System-DPI-aware deferral this ADR finally resolves.
- ADR-028 (Workspace Grid) — `DesktopCoordinateSpace`'s "window space" is now explicitly documented as
  desktop-DIP under this ADR's model; no change to its own geometry math.
- ADR-034 (Explorer Restart Recovery) — `IDesktopScaleProvider.Refresh()`/`BeginBatch()` consumed at the end
  of `RecoverDesktopLayer`; mode T is the same "top-level" outcome ADR-034 documents as expected on 24H2+.
- `docs/PLANS/release-hardening-1.0-F-dpi.md`, `docs/PLANS/release-hardening-1.0-f0-results.md` — the
  detailed sub-plan and spike results this ADR summarizes.
