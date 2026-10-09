# ADR-021: Explicit Panel Interaction State and Gesture Lifecycle

**Статус:** Accepted (Phase 1, `fix/resize-lifecycle`, commits C1–C9; see
`docs/PLANS/phase-1-resize-stabilization.md` for the implementation plan this ADR now reflects) — Amended 2026-09-24 (ADR-011 amendment: `ResizeConstraints.SizeGridStep`, resize honors `GridSize`); Amended 2026-09-22 (ADR-028: `GridAligned` входит в resize-конвейер через `ResizeConstraintsFactory` и `ResizeGestureSession.TryUpdate`, сам `ResizeGeometryCalculator` не меняется); Amended 2026-10-10 (ADR-022: `ResizeEndReason.Canceled` теперь приходит и с native-пути — ESC с неизменным rect); Amended 2026-10-10 (ADR-022 Фаза 1: `Thumb`-путь удалён — семантика `Thumb`-capture/`CancelDrag`/`RecoverStaleDrag` и `PanelResizeController` исторические; жизненный цикл жеста обслуживает только `NativePanelResizeController`, `CaptureLost` = `SizeMoveAborted`, `Canceled` приходит от native ESC)

> **Историческая оговорка (2026-10-10).** Разделы ниже про `Thumb.DragCompleted`/`CancelDrag`/`LostMouseCapture`,
> `PanelResizeController` и стадию-регрессию H4 описывают удалённый WPF-путь. Не изменились и продолжают действовать
> `PanelInteractionTracker`, `ResizeGestureSession`, `ResizeGeometryCalculator`, `ResizeEndReason` (все члены) и правило
> `ShouldPersist`; `Canceled` теперь порождает native-контроллер (ESC с неизменным rect), а событие `CaptureLost` в
> диагностике больше не пишется (категория `capture` удалена вместе с Thumb-путём).

## Context

Move, resize, rename, icon reorder, selection and context-menu gestures share the same WPF visual
tree. Resize relied on WPF `Thumb` events without a first-class cancellation lifecycle:
`OnResizeDragCompleted` ignored `DragCompletedEventArgs.Canceled`, there was no reaction to
`LostMouseCapture`/`Unloaded`/window disposal, and `SaveWindowsState()` was invoked unconditionally
regardless of whether any geometry had actually been applied.

Phase 1 implemented the fix end-to-end: `PanelInteractionState`/`PanelInteractionTracker`,
`ResizeEdge`, `ResizeConstraints`, `ResizeGeometryCalculator` (pure math extracted from
`PanelResizeController`), and `ResizeGestureSession` (explicit gesture state machine). 1084/1084
tests are green, `dotnet build` is clean. `code-quality-reviewer` (1 CRITICAL + 6 MAJOR, all fixed
with added regression tests) and `fences-ux-parity-reviewer` (snap-to-grid semantics confirmed
bit-for-bit identical; no regressions; self-healing after Alt+Tab is new functionality, not a bug)
both signed off.

### Real bug found and fixed during implementation: H3 (`RollUpBehavior` height animation hold)

`RollUpBehavior.ApplyRollUp` builds a `DoubleAnimation` targeting `Window.HeightProperty` without
setting `FillBehavior`, which defaults to `FillBehavior.HoldEnd`. WPF's animation system keeps a
`HoldEnd` animation's clock attached to the property *after* `Completed` fires, so it continues to
hold the animated value with **higher priority than any later plain local-value assignment**. Every
vertical resize delta writes `_window.Height = x` as a plain local value — once a panel has been
rolled up and back down even once, every subsequent vertical resize silently has no visual effect,
because the stale animation still wins property-value precedence.

This is the leading candidate explanation for the "resize appears to hang" symptom class that
motivated Phase 1's diagnostics work in Phase 0: it looks like a lifecycle/capture problem
(the initial hypothesis this ADR was written to address) but is actually a WPF animation-priority
interaction with no lifecycle bug involved. Fixed (commit C7) by detaching the animation
(`_window.BeginAnimation(Window.HeightProperty, null)`) and re-asserting the target height as a
plain local value in the `Completed` handler, immediately after the animation's own visual effect
finishes. `FillBehavior.Stop` was considered and rejected — it snaps back to the pre-animation value
for one frame before the handler runs, producing a visible flicker. A related race (M-6: rapid
double-toggle superseding an in-flight animation before its `Completed` handler runs) was fixed with
a monotonic generation counter that lets a stale handler detect it has been superseded and bail out.

**Architectural takeaway:** an explicit gesture lifecycle (this ADR's core decision) does not, by
itself, guard against WPF animation system side effects on the same dependency property a gesture
writes to. Any future gesture that writes to an animatable property already used by a `Storyboard`/
`DoubleAnimation` elsewhere in the same window must audit `FillBehavior` and animation lifetime, not
just capture/state transitions.

## Decision

Introduce explicit UI-only interaction state and idempotent begin/update/end lifecycle for panel
gestures.

`PanelInteractionState` stays in UI layer and must not enter Domain/Core.

### `PanelInteractionState` transition matrix

Implemented in `PanelInteractionTracker` (`src/DesktopOrganizer.UI/Interaction/`), enforced and
tested (`PanelInteractionTrackerTests`, `[Theory]` over all reachable transitions):

| Из \ В | Idle | Hover | Editing | Dragging | Resizing |
|---|---|---|---|---|---|
| Idle | — | ✔ (Ph6) | ✔ (Ph6) | ✔ | ✔ |
| Hover | ✔ | — | ✔ (Ph6) | ✔ | ✔ |
| Editing | ✔ | ✖ | — | ✖ | ✖ |
| Dragging | ✔ | ✖ | ✖ | — | ✖ |
| Resizing | ✔ | ✖ | ✖ | ✖ | — |

`Hover` and `Editing` are **reserved for Phase 6** (Edit Layout UX / inline rename). In Phase 1, no
code path sets either value — `TryEnter(Hover)`/`TryEnter(Editing)` are never called anywhere in the
Phase 1 diff. This is deliberately pinned by a regression test (`T-TRK-08`, "no Phase 1 transition
ever sets Hover/Editing") specifically to guard against the "dead state" risk named in Consequences/
Negative below: introducing enum values with zero producers and zero consumers is itself a design
smell if left unverified, so the absence of any path into these states is treated as a tested
invariant rather than an implicit assumption.

`ForceIdle()` is UI-thread-affine and must only be called from the WPF input pipeline
(`Unloaded`/`Closing`/disposal handlers) — calling it from a finalizer or background thread is out of
contract.

### `ResizeEndReason` and `ShouldPersist` semantics

`ResizeGestureSession.End(ResizeEndReason)` (`src/DesktopOrganizer.UI/Interaction/`) closes exactly
one resize gesture, idempotently, and reports a `ResizeEndOutcome`:

```csharp
public enum ResizeEndReason { Completed, Canceled, CaptureLost, SourceChanged, Unloaded, Closing, Failed }
```

**Persistence rule:** `ShouldPersist = AppliedDeltaCount > 0`, **independent of `Reason`**, with one
exception — `Failed` always forces `ShouldPersist = false`.

Rationale: when WPF reports a `DragCompleted` with `Canceled = true` (e.g. Escape, or the platform
otherwise aborting the gesture), it does **not** roll back any `Window.Width`/`Height`/`Left`/`Top`
values already written to the screen during preceding `DragDelta` events. The panel's on-screen
geometry has already diverged from disk. Since WPF gives no rollback mechanism, the only geometry
that can be truthfully persisted is whatever is actually on screen — so any gesture that applied at
least one delta persists that geometry on end, regardless of whether the end reason was a clean
completion, a cancellation, a lost-capture recovery, an unexpected source change, an `Unloaded`, or a
`Closing`. A gesture that never applied a delta (e.g. a bare click on a resize thumb immediately
interrupted, with zero `DragDelta` events) has nothing to persist and correctly does not call
`SaveWindowsState()` at all.

`Failed` is the sole exception: an exception thrown mid-delta means the geometry may have been left
half-applied (partially written `Window` properties before the throw), so persisting it would risk
writing corrupt state to disk; `ShouldPersist = false` in that case is a safety choice, not a
consequence of the "screen already changed" rationale above.

`End` is idempotent (`WasActive = false` on any call after the first) and re-entrancy-safe (an
internal `_ending` guard tolerates `Thumb.CancelDrag()` synchronously re-raising `DragCompleted` from
inside the same `End` call). `tracker.Exit(Resizing)` is invoked in a `finally` block so that even an
exception mid-cleanup still returns `PanelInteractionState` to `Idle` — this is the mechanism behind
the DoD requirement "`LostMouseCapture` always leads to `Idle`."

### Accepted deviation from strict bit-for-bit behavior: `ResizeConstraints` computed once per gesture

Documented in `PanelResizeController`'s class-level `<remarks>` and confirmed by
`fences-ux-parity-reviewer`: prior to commit C4, `MinWidth`/`MinHeight`/snap cell sizes were
re-derived from the live `ChildWindowModel` on **every single `DragDelta`**. As of C4,
`ResizeConstraints` is computed exactly once, at `ResizeGestureSession.TryBegin`, and held fixed for
the gesture's entire duration.

This is a deliberate simplification that ships with introducing an explicit session — no
characterization test (commit C1) exercises changing `IconSize`/`ViewMode` mid-gesture, and no
normal UI interaction can do so either: the mouse is captured by the resize thumb for the whole
gesture, so there is no input surface through which a user could change the panel's icon size or
view mode while a resize drag is in progress. `fences-ux-parity-reviewer` confirmed this is
practically unreachable through the UI and does not constitute an observable behavior regression.
It is, however, a **formal** difference from the pre-Phase-1 code and is recorded here so it is not
mistaken for an oversight: it is explicitly **not** part of the bit-for-bit contract that commit C2
(pure geometry extraction) established and guarantees.

### `ResizeGeometryCalculator.Apply` — deliberate Phase 2 compatibility

`ResizeGeometryCalculator.Apply(Rect current, ResizeEdge edge, ResizeRawExtent raw, double
anchorRight, double anchorBottom, ResizeConstraints constraints)` was designed to accept a "current
rect + accumulated raw extent + opposite-edge anchors" input shape specifically because this maps
directly onto what a native `WM_SIZING` handler can supply in Phase 2 (native resize/host): the
`WM_SIZING` message delivers a proposed window rect, which converts to the same raw-extent shape
this calculator already consumes, without needing a second parallel implementation of the
snap/clamp/reposition math.

This is recorded here as an explicit design intent (R6, `docs/PLANS/phase-1-resize-stabilization.md`
§6) so that Phase 2 planning starts from "call this calculator" rather than re-deriving the same
geometry math against a different WPF input path. `ResizeGeometryCalculator` has zero WPF `Thumb`/
`Window` dependencies — it operates only on `Rect`/`Vector`/`ResizeConstraints` — which is what makes
it directly reusable from a native message handler with no adaptation beyond translating `WM_SIZING`'s
`RECT` into the same raw-extent/anchor shape.

## Consequences

Positive:
- deterministic cleanup — `Exit`/`End` guaranteed via `finally`, idempotent, re-entrancy-safe;
- visual states can bind to one source of truth (`PanelInteractionTracker.StateChanged`);
- resize/move can later share native lifecycle events (Phase 2, via `ResizeGeometryCalculator`'s
  WPF-independent API, see above);
- an unrelated real bug (H3, roll-up animation hold) was found and fixed as a direct result of
  building out the explicit lifecycle and its regression suite — see Context;
- `LostMouseCapture`/stale-drag recovery (`RecoverStaleDrag`, closes hypothesis H4) makes the gesture
  session self-healing after native capture theft (Alt+Tab, Explorer restart) without requiring the
  platform to reliably deliver `LostMouseCapture` to the affected `Thumb`.

Negative:
- more state transitions to test (~53 new tests across `ResizeGeometryCalculatorTests`,
  `PanelInteractionTrackerTests`, `ResizeGestureSessionTests`);
- incorrect transition design can itself create dead states — mitigated for Phase 1 by `T-TRK-08`
  pinning that `Hover`/`Editing` have zero producers until Phase 6 claims them;
- `ResizeConstraints` computed once per gesture is a formal (if practically unreachable) deviation
  from prior per-delta re-read behavior — see above; must be revisited if a future feature allows
  changing `IconSize`/`ViewMode` while a resize gesture is in flight without releasing capture.

## Invariants

- only one panel gesture owns capture at a time (`PanelInteractionTracker.TryEnter` enforces mutual
  exclusion between `Dragging`/`Resizing`; `Editing`/`Hover` reserved, not yet wired);
- every Begin has exactly one logical End, even if platform events are duplicated/missing
  (`ResizeGestureSession` idempotency + `_ending` re-entrancy guard);
- End is idempotent;
- window disposal forces state to Idle (`ForceIdle()` on `Unloaded`; `AbortResize(Closing)` on
  `OnClosing`).

## Amendment 2026-09-24 — `ResizeConstraints.SizeGridStep` (ADR-011 Amendment: resize also honors `GridSize`)

`ResizeConstraints` gained an eighth field, `SizeGridStep` (default `0d`, so every pre-amendment caller/
test keeps producing bit-for-bit identical output). `ResizeConstraintsFactory.BuildConstraints` resolves
it from `IAppSettings.GridSize` (new optional `gridSize` parameter, default `1` = "no grid"); when
positive it takes priority over the icon-grid snap (`SnapCellWidth`/`SnapCellHeight` — see ADR-015/
ADR-018) inside `ResizeGeometryCalculator.Apply`, which reuses `Models.GridSizeNormalizer.NormalizeExtent`
(the same round-half-up helper ADR-011's own restore-time normalization already uses) directly on the raw
window `Width`/`Height`, in place of the existing icon-grid-snap/free branch — `Apply`'s "point 7"
per-axis/anchor contract (untouched axis returned as-is, anchor edge never moved) is unchanged; only how
`newWidth`/`newHeight` are computed gained a third branch.

`ResizeGestureSession.IsFreeResizeFrame` (the `Alt` free-resize gate, previously GridAligned-only) now
also returns `true` whenever `SizeGridStep > 0`, regardless of `PanelGeometryMode` — holding `Alt` skips
GridSize quantisation for that frame only, for a `LegacyFreeform` gesture exactly as it already did for a
`GridAligned` one. `PanelSizingGridSupport.NormalizeCommittedSize` is the new idempotent commit-time
safety net (called by both `PanelResizeController.EndResize`/`NativePanelResizeController.
PersistAfterGesture`, right after `PersistSpans`, before `_snapToGrid()`/`SaveWindowsState()`) that
guarantees a gesture never comes to rest off the `GridSize` lattice even if `Alt` suppressed every live
frame. None of this changes `ResizeGeometryCalculator`'s zero-WPF-dependency shape (§ above) — the native
`WM_SIZING` path (`NativePanelResizeController`, ADR-022) reuses the identical `ResizeConstraintsFactory`/
`ResizeGeometryCalculator`/`ResizeGestureSession`/`PanelSizingGridSupport` code, so the D-4 golden
equivalence this ADR/ADR-022 already establish extends to `SizeGridStep` gestures without a second
implementation — see `ResizeGeometryCalculatorSizeGridStepTests`, `ResizeGestureSessionSizeGridStepTests`,
`ResizeConstraintsFactorySizeGridStepTests`, `PanelSizingGridSupportSizeGridStepTests`, and
`NativePanelResizeControllerTests.RealController_GridSizeMode_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset`.

## Amendment 2026-09-22 (ADR-028): `GridAligned` в resize-конвейере

[ADR-028](ADR-028-workspace-grid.md) подключает режим `GridAligned` без правки `ResizeGeometryCalculator.Apply`
(инвариант D-2/R6 этого ADR сохранён): правила кадра задаёт `ResizeConstraintsFactory.BuildConstraints(...,
PanelGridState)`, а квантование (`SnapBounds` → `ClampResizeToWorkArea` → `ClampResizeToSlots`) выполняет
`ResizeGestureSession.TryUpdate` вторым шагом того же кадра; `Alt` пропускает квантование кадра (читается
свежим каждый кадр), commit всегда квантует.

## Amendment 2026-10-10 (ADR-022, native-путь единственный): `Canceled` и персист на native-пути

Правило персиста «`ShouldPersist = AppliedDeltaCount > 0` независимо от `Reason`» (раздел выше) описывает Thumb-путь, где
WPF не откатывает записанную геометрию. На native-пути (единственном после решения пользователя 2026-10-10,
[ADR-022](ADR-022-native-panel-sizing.md) Amendment 2026-10-10, G-4) система при ESC восстанавливает исходный rect, поэтому
`NativePanelResizeController` на `SizeMoveExited` с `finalRect == _startWindowRectPx` завершает жест как
`ResizeEndReason.Canceled` и вызывает `EndResize(Canceled, persist: false)` — **без** `PersistAfterGesture` и без демоции
`AutoContent → Manual`. `Canceled` — значит, снова используемый член enum (раньше его порождал только `Thumb`-путь, удалённый
в Фазе 1 ADR-022, 2026-10-10). Семантика `Thumb`-capture в этом ADR — историческая. Открытый риск порядка сообщений — MV-7 в ADR-022.

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-22 | [ADR-028](ADR-028-workspace-grid.md) | `GridAligned`: квантование в `ResizeGestureSession.TryUpdate`, `Alt`-пропуск кадра, commit-квантование | — (Apply не менялся); `ShouldPersist = AppliedDeltaCount > 0` по-прежнему определяет, есть ли что персистить |
| 2026-09-24 | ADR-011 amendment | `ResizeConstraints.SizeGridStep` (восьмое поле, по умолчанию `0d`); `IsFreeResizeFrame` теперь `true` и при `SizeGridStep > 0`; `PanelSizingGridSupport.NormalizeCommittedSize` | Утверждение «`ResizeConstraints` — семь полей» (до амендмента) |
| 2026-10-10 | [ADR-022](ADR-022-native-panel-sizing.md) | Native ESC с неизменным rect → `Canceled`, `persist: false`, без демоции `AutoContent` | Допущение «любой жест с `AppliedDeltaCount > 0` персистит» — для native-`Canceled`; «`Canceled` приходит только из Thumb `DragCompleted`» |
| 2026-10-10 | [ADR-022](ADR-022-native-panel-sizing.md) Фаза 1 | `Thumb`-путь, `PanelResizeController`, `CaptureGot`/`CaptureLost` удалены; единственный источник жеста — `NativePanelResizeController`; `PanelClosing.thumbIsDragging` → `resizeActive` | Описание `Thumb`-capture/`CancelDrag`/`RecoverStaleDrag`/`PanelResizeController` как действующего механизма (стало историческим); диагностический сигнал «`CaptureLost` без `ResizeCompleted` = утечка capture» |
