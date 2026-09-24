# ADR-022: Native Panel Sizing via Win32 Message Loop

Status: Accepted (Phase 2, `feat/native-panel-sizing`, commits C1–C7; see
`docs/PLANS/phase-2-native-panel-sizing.md` for the implementation plan this ADR now reflects)

## Context

Current sizing uses eight transparent WPF `Thumb`s. This gives full control over snapping but
depends on WPF drag/capture behavior and has reported intermittent lock-up (addressed for the
Thumb path in Phase 1, ADR-021).

Phase 2 implemented an alternative resize path driven by the Win32 sizing loop
(`WM_NCHITTEST`/`WM_ENTERSIZEMOVE`/`WM_SIZING`/`WM_EXITSIZEMOVE`/`WM_CAPTURECHANGED`/
`WM_MOUSEACTIVATE`/`WM_DPICHANGED`/`WM_DPICHANGED_AFTERPARENT`), gated behind the
`NativePanelSizing` setting (default `False`). 1287/1287 tests are green, `dotnet build` is clean.
`code-quality-reviewer` (1 CRITICAL + 4 MAJOR found and fixed, including a real coordinate-symmetry
bug in the `WM_SIZING` origin offset — see D-4) and `fences-ux-parity-reviewer` (default-behavior
parity confirmed while the flag is `False`; no regressions found) both signed off.

## Decision

Move panel resize to Win32 non-client sizing semantics using `WM_NCHITTEST`, `WM_SIZING`,
`WM_ENTERSIZEMOVE`, `WM_EXITSIZEMOVE`, `WM_CAPTURECHANGED`, `WM_MOUSEACTIVATE`, and
`WM_DPICHANGED`/`WM_DPICHANGED_AFTERPARENT`. Keep grid/snap calculations managed and pure, and
reuse Phase 1's `ResizeGeometryCalculator`/`ResizeGestureSession` unmodified. Ship behind a
feature flag, default `False`, so the legacy `Thumb` path remains the default and a one-release
rollback stays available.

### D-1. No C++ module is introduced; P/Invoke extends the existing `INativeApi`

The `PHASE-2 "C++ criterion"` is not met: exactly one window hook and 6–8 messages are needed,
marshaling is limited to two `RECT`s and a `POINT`, and there is no measurable performance case for
native code. The project already has a centralized P/Invoke adapter
(`src/DesktopOrganizer.Services/Interfaces/INativeApi.cs` + `Tools/NativeApi.cs`). The CLAUDE.md
invariant ("P/Invoke only behind `INativeApi`") is preserved: the new primitives
(`GetWindowRect`/`ReadRect`/`WriteRect`/`GetDpiScaleForWindow`) were added to `INativeApi`; no new
`[DllImport]` appears anywhere else.

The drafts in `docs/Refactoring/Native/*.cs` were used only as an API shape reference. Their
namespace (`DesktopOrganizer.Native`) and signature (`Attach(nint, Func<Rect>, Action<Rect>)`) were
**not** carried into production — there is no such assembly, and `Func<Rect>/Action<Rect>` cannot
distinguish edge/DPI/coordinate-space information that the real client interface needs. The drafts
remain in place as refactoring-package artifacts and were not deleted. A dedicated
`DesktopOrganizer.Native` assembly (ROADMAP "Architectural target") remains out of scope for this
phase — candidate for Phase 8.

### D-2. Dependency direction is "callback downward": policy lives in UI, message decoding in Services

`INativeApi` is `internal` to `DesktopOrganizer.Services` (there is an
`InternalsVisibleTo("DesktopOrganizer.UI")`, added for a single DI test, but it is not a sanctioned
back door for new features). `ResizeGeometryCalculator` lives in `DesktopOrganizer.UI.Interaction`
and depends on `UI.Models` (`GridMetrics`/`AdaptiveGridLayout`) — it cannot move into
Services/Core without dragging half the UI model layer with it and breaking the Phase 1 commit C2
bit-for-bit contract.

Therefore the dependency direction is **callback downward**: the service knows nothing about
snap/grid — it only decodes Win32 messages, converts coordinates, and asks the client.

```
ChildWindow (UI)
  +-- NativePanelResizeController (UI)        <- all policy: session, calculator, diagnostics
        |-- ResizeGestureSession (Ph1)        <- reused as-is
        |-- ResizeGeometryCalculator (Ph1)    <- reused as-is, NOT copied
        +-- implements INativePanelSizingClient
              ^
              | (Services.Abstractions)
        INativePanelSizingService.Attach(hwnd, client) -> IDisposable
              |
        NativePanelSizingService (Services/Tools)   <- HwndSource.AddHook + message translation
              +-- INativeApi (internal)             <- the only P/Invoke surface
```

`NativeSizingEdge` (Abstractions) is a deliberately separate enum from `UI.Interaction.ResizeEdge` —
Abstractions must not depend on UI. Mapping between the two lives in `NativeSizingEdgeMap` (UI).

### D-3. Pixels outside, DIP inside — exactly one conversion point

`INativePanelSizingClient` receives **physical screen pixels** plus the current `dpiScale`;
conversion to DIP happens in a single pure `NativeSizingCoordinateMapper` (UI). Converting inside
the service would create a second DPI policy alongside `MonitorService.ToDipRect` (system DPI
scale) and risk drifting from it. The mapper is a pure function, tested at 1.0/1.25/1.5/2.0 scale
and at negative screen coordinates (monitor to the left of the primary), with no GUI required.

### D-4. `Accumulate` is not used on the native path — only `Apply`, with a self-correcting synthetic delta

`WM_SIZING` delivers an already-complete proposed rect (gesture origin + cursor offset) — i.e.
exactly the value that `Accumulate` reconstructs from a sequence of deltas on the Thumb path:
`raw == (startExtent +/- sum(delta)) == proposedExtent`. The native path therefore builds a
`ResizeRawExtent(proposed.Width, proposed.Height)` directly from each `WM_SIZING` message and calls
the **same** `Apply` with the same `anchorRight`/`anchorBottom` from `ResizeGestureSession.TryBegin`
— recomputed from `_session.Raw` on every message, with no accumulation drift, since the native path
always starts from the gesture's original extent rather than accumulating deltas across messages.
This equivalence is pinned by a golden test (`NativeResizeEquivalenceTests`, T-EQ-01..08): for every
edge and both view modes, running the same total cursor displacement through (a) the legacy
`Accumulate`-chain-plus-`Apply` and (b) the native single-`Apply`-per-message path produces
bit-for-bit identical results. `ResizeGeometryCalculator.cs` itself was not modified (verified by
diff) — this is the DoD criterion "grid snap matches the current implementation."

**Real bug found and fixed during implementation: origin-offset symmetry.** `WM_SIZING`'s
coordinate space (child-reparented-into-`WorkerW` vs. top-level) is not documented, so
`NativeSizingOriginResolver` self-calibrates an offset from the first `WM_SIZING` of each gesture
(`GetWindowRect` is always screen-relative, so `offset = GetWindowRect - firstSizingRect`).
`code-quality-reviewer` found a CRITICAL defect where this offset was applied on the way *in*
(decoding the proposed rect) but not applied symmetrically on the way *out* (writing the corrected
rect back via `WriteRect`), which silently shifted the panel's on-screen position by the offset on
every resize when running inside `WorkerW`. Fixed in commit `646e04c`. The architectural lesson,
recorded here so future coordinate-mapping code doesn't repeat it: any coordinate calibration
applied to `ProposeBounds`'s input **must** be applied — inverted — to its output before the rect is
written back to Win32; testing only the "recognize an offset" direction and not the "undo the offset"
direction leaves exactly this class of bug uncaught.

### D-5. Hit-testing never returns `HTCAPTION`

Panel drag-move remains a WPF gesture (`PanelDragMoveBehavior`: anti-overlap ADR-017 + magnetic snap
ADR-011). Returning `HTCAPTION` from `WM_NCHITTEST` would hand move over to the system modal loop
and disable both features. Consequently `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` on panels only ever
fire for resize — which is convenient: `SizeMoveEntered.edge` is always a real sizing edge, never a
move.

### D-6. Application DPI-awareness is unchanged in this phase

`src/DesktopOrganizer.UI/DesktopOrganizer.UI.csproj` still has no `app.manifest` (no `dpiAware`/
`dpiAwareness` declaration). Switching the whole application to Per-Monitor-V2 is a global change to
every window, the WPF layout system, and `MonitorService`, and stays out of scope for this phase.
Consequences the implementation had to account for:
- `WM_DPICHANGED` (0x02E0) is only delivered to top-level PMv2-aware windows. The panel is
  `SetParent`-ed into `WorkerW` (`DesktopIntegrationService.EmbedInDesktop`), i.e. it is a child
  window — the message may never arrive. A handler exists (per spec), but the **source of truth for
  DPI/geometry changes is `IMonitorService.MonitorsChanged`** (`MonitorService` has its own top-level
  message window and already reacts to `WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE`).
- `WM_DPICHANGED_AFTERPARENT` (0x02E3) — the child-window variant — is also handled, for the case
  where awareness is changed in the future; today it most likely never fires. This branch is marked
  `MANUAL VERIFY` and must not break anything if it is simply never invoked.

### R-8 deviation: ESC during the system sizing loop is indistinguishable from a normal end

`WM_EXITSIZEMOVE` fires identically whether the user released the mouse normally or pressed ESC —
`DefWindowProc`'s sizing loop gives no signal to distinguish cancellation from completion.
Consequently, on the native path `endReason` is always `Completed`, `canceled` is always `false`,
even when the gesture was in fact cancelled via ESC. This is a **documented deviation**, not a bug:
the triage contract (`CaptureLost` without a following `ResizeCompleted` signals a capture leak)
remains valid, since ESC still reaches `WM_EXITSIZEMOVE` and is logged as such. Recorded in
`docs/Refactoring/Testing/diagnostics-log-events.md` and in the manual repro matrix
(`docs/Refactoring/Testing/resize-repro-matrix.md`, row R-8 note) so a manual tester does not
mistake it for a regression.

## Alternatives rejected

1. Keep Thumb path forever: too fragile around capture/lifecycle (motivated Phase 1, but does not
   remove the underlying WPF drag/capture dependency).
2. Full native rewrite of panel window: unnecessary scope; no requirement crosses the C++ criterion
   bar (D-1).
3. Pixel-only system resize without snap: loses existing product behavior (grid snap, anchor
   semantics) — rejected because D-4 shows the existing pure calculator can be reused as-is instead.

## Rollout

Feature flag `NativePanelSizing` (`IAppSettings`), default `False`, override priority
env `DESKTOPORGANIZER_NATIVESIZING` > CLI `--native-sizing`/`--no-native-sizing` > persisted
setting (`NativeSizingSwitch`, mirrors `DiagnosticsSwitch`). Legacy path (`PanelResizeController` +
`ResizeOverlay` Thumbs) remains fully intact and is the default; both paths sit behind the shared
`IPanelResizeController`/`IResizeAffordance` seams so `ChildWindow`/`RollUpBehavior` do not need to
know which path is active. Removing `ResizeOverlay` is explicitly out of scope for this phase (C8,
next release) — it only happens once the manual repro matrix below has been walked by a human on
live Windows and the flag's default is flipped to `True`.

## Consequences

Positive:
- resize gains a code path independent of WPF `Thumb`/capture semantics, addressing the
  Fences-parity requirement for native-feeling resize without discarding any existing grid/snap
  logic;
- `ResizeGeometryCalculator`/`ResizeGestureSession` (Phase 1) are validated as genuinely
  path-independent — Phase 1's stated design intent (ADR-021, "`ResizeGeometryCalculator.Apply` —
  deliberate Phase 2 compatibility") is now proven, not just planned;
- a real coordinate-symmetry bug (D-4) was found and fixed as a direct result of building the
  golden-equivalence test suite honestly (through real `ProposeBounds` + full controller lifecycle,
  not a shortcut mock) — see commit `7a5e8bb`;
- rollback is a single setting flip with no code path removed.

Negative:
- two parallel resize controllers (`PanelResizeController`, `NativePanelResizeController`) must be
  kept in sync manually until C8 removes the legacy one; `ResizeConstraintsFactory` was extracted
  specifically to avoid a second copy of constraint-building logic;
- R-1/R-2/R-3/R-4/R-5/R-6/R-7 are all only verifiable on live Windows (layered-window artifacts,
  activation stealing, `WM_SIZING` coordinate space, min-size cursor detachment, nested message loop
  timer interactions, roll-up interaction, DPI/multi-monitor) — none of this is exercised by
  `dotnet test` in this environment; see the manual repro matrix rows 20–31;
- R-8 is a permanent, not transitional, limitation of the Win32 sizing loop API surface — it cannot
  be fixed by more code, only documented.

## Invariants

- no new `[DllImport]` outside `Tools/NativeApi.cs` (D-1);
- `ResizeGeometryCalculator.cs` is never modified by native-path work — a second copy of snap/clamp
  math is an automatic review failure (D-2, ADR-021 R6);
- `INativePanelSizingClient` never receives/returns DIP — pixels only, conversion is exclusively
  `NativeSizingCoordinateMapper`'s job (D-3);
- `WM_NCHITTEST` on a panel never returns `HTCAPTION` (D-5);
- `NativePanelSizing` defaults to `False`; flipping it to `True` must not require a code change,
  only a setting/env/CLI value (Rollout).

---

## Amendment 2026-09-22 (ADR-028)

*(Раздел на русском по требованию текущего проходного задания; остальной документ ADR-022 сохранён
на языке оригинала.)*

[ADR-028](ADR-028-workspace-grid.md) добавляет `PanelGeometryMode.GridAligned` — новую ветку
квантования размера (`SnapCellWidth`/`SnapCellHeight` берутся из `WorkspaceGridMetrics.UnitSize`, а не из
`GridMetrics.CellSize(IconSize)`) внутри уже существующего `ResizeConstraintsFactory`. Это **не меняет и
не дублирует** `ResizeGeometryCalculator.Apply` — инвариант D-2/D-4 этого ADR («второй экземпляр
snap/clamp математики — автоматический провал ревью») остаётся в силе буквально: `Apply` остаётся общим
для `LegacyFreeform`/`GridAligned`, для `List`/`Grid` view mode и для обоих путей resize (legacy `Thumb` и
native `WM_SIZING`) — единственная новая точка входа `GridAligned` в конвейер — выбор источника констант
в `ResizeConstraintsFactory`, ниже по потоку от `Apply`, не внутри него.

**Обязательное следствие для золотого теста.** `NativeResizeEquivalenceTests` (D-4) обязаны быть
расширены `GridAligned`-кейсами при реализации, а не заменены — bit-for-bit эквивалентность legacy/native
путей должна быть подтверждена для нового режима геометрии так же, как она уже подтверждена для
`LegacyFreeform`. Это работа стадии реализации (вне scope architecture-only прохода, зафиксировавшего
этот amendment), но сам факт обязательности пере-baseline'а — часть контракта ADR-028.

**DPI-awareness (D-6) наследуется без изменений.** ADR-028 явно не меняет статус «System DPI, не
PerMonitorV2» — origin-per-monitor (новая часть ADR-028) вычисляется в System-DPI координатах монитора,
как и всё остальное в этом ADR; источник истины на смену DPI/монитора остаётся `IMonitorService.
MonitorsChanged`, не `WM_DPICHANGED`. Переход на PerMonitorV2 остаётся отдельным, отложенным решением.

---

## Amendment 2026-09-22 — реализация P04: уточнения к этому ADR

Реализация ADR-028 в native-пути (этап P04) уточняет предыдущий amendment в трёх местах. Полное
описание — в [ADR-028, «Amendment — Реализация P04»](ADR-028-workspace-grid.md).

**1. Точек входа `GridAligned` оказалось две, и D-2 по-прежнему соблюдён.** Предыдущий amendment
предполагал, что единственная новая точка — выбор констант в `ResizeConstraintsFactory`. Фактически
`GridAligned` квантуется **не** через `SnapCellWidth/SnapCellHeight` (они для этого режима равны `0`,
то есть выбрана СВОБОДНАЯ ветка `Apply`), а отдельным вторым шагом того же кадра —
`WorkspaceLayoutPolicy.SnapBounds`/`ClampResizeToSlots` внутри `ResizeGestureSession.TryUpdate`, ПОСЛЕ
`Apply`. `ResizeGeometryCalculator.cs` не изменён ни на строку; второго экземпляра snap/clamp-математики
не появилось — `TryUpdate` общая для Thumb- и native-пути, так что копии по определению не существует.
Формулировка прошлого amendment («`SnapCellWidth`/`SnapCellHeight` берутся из
`WorkspaceGridMetrics.UnitSize`») оказалась неточной и заменяется этой.

**2. Golden-эквивалентность пере-baseline'ена, как и требовалось.** `ResizeGeometryCalculatorApplyEquivalenceTests`
и «honest golden equivalence» блок `NativePanelResizeControllerTests` не изменены; `GridAligned`-кейсы
добавлены рядом (`NativePanelResizeControllerGridAlignedTests`): реальный native-контроллер прогоняется
через `WM_ENTERSIZEMOVE`/`WM_SIZING`×2/`WM_EXITSIZEMOVE` с ненулевым origin-offset на 8 сторонах и
масштабах 100/125/150 % и сверяется бит-в-бит с чистым конвейером `Apply → SnapBounds`.

**3. R-8 подтверждён и теперь распространяется на span.** ESC внутри системного цикла по-прежнему
неотличим от обычного завершения, поэтому такой жест персистится — начиная с P04 вместе с
`WidthSpanUnits`/`HeightSpanUnits`. Это прямое следствие уже задокументированного обоснования R-8
(панель визуально уже в новой геометрии), а не новое решение. Запись span живёт внутри
`PersistAfterGesture`, то есть полностью подчиняется `deferPersist` (M-3): из модального цикла на диск
ничего не уходит inline.

**4. `ToPx` не изменён (D-3 в силе).** Раздельное округление краёв принято как унаследованное
ограничение; граница ошибки (≤ 1 физический px, не накапливается, не персистится) зафиксирована тестом
на 100/110/125/150/175 % — см. ADR-028 п. 8 того же amendment.

## Amendment 2026-09-24 — `ResizeConstraints.SizeGridStep` (ADR-011 Amendment: resize honors `GridSize`)

D-2 («один калькулятор на оба пути») и D-4 («native reuses `Apply`, no second `Accumulate`») остаются в
силе без единого исключения: `ResizeConstraintsFactory.BuildConstraints` (уже общая точка входа для
Thumb- и native-контроллеров) получила новый необязательный параметр `gridSize` (по умолчанию `1` —
«сетка выключена», бит-в-бит воспроизводит прежний вывод для каждого существующего вызова), из которого
резолвится новое поле `ResizeConstraints.SizeGridStep`; `NativePanelResizeController.SizeMoveEntered`
передаёт `IAppSettings.GridSize` в этот параметр — ровно тем же способом, каким `PanelResizeController.
OnResizeDragStarted` уже передаёт `_appSettings.SnapResizeToIconGrid`. `ResizeGeometryCalculator.Apply`
по-прежнему не тронут ни строкой WPF/P-Invoke-кода — весь новый код (`SizeGridStep`-ветка) остаётся тем
же чистым `Rect`/`Vector`/`ResizeConstraints`-конвейером.

Единственное изменение, специфичное для native-пути: `NativePanelResizeController` получила четвёртый
testability-seam делегат, `setBounds` (пишет `Left`/`Top`/`Width`/`Height` разом), нужный
`PersistAfterGesture`'s новому commit-time вызову `PanelSizingGridSupport.NormalizeCommittedSize` —
единственная операция во всём native-пути, которой действительно может понадобиться переставить оба угла
окна одним актом (существовавший `setWidth` пишет только ширину, чего было достаточно для drop-time
`SnapWidthToGrid`, но недостаточно для anchor-aware пере-квантования размера). Production-путь
(`ChildWindow`) не тронут — новый делегат собирается публичным конструктором из того же `Window`
(`SetBoundsDelegate`), как и три существовавших раньше.

Golden-эквивалентность снова пере-baseline'ена без изменения самого механизма: новый теоретический кейс
`NativePanelResizeControllerTests.RealController_GridSizeMode_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset`
(GridSize=48, 8 краёв, ненулевой origin-offset) сравнивает реальный native-контроллер с
Accumulate-chain-путём (`LegacyApply`) бит-в-бит, тем же способом, что и все существующие «Honest golden
equivalence» кейсы в этом файле.
