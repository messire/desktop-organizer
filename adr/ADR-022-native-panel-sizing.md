# ADR-022: Native Panel Sizing via Win32 Message Loop

**Статус:** Accepted (Phase 2, `feat/native-panel-sizing`, commits C1–C7; see
`docs/PLANS/phase-2-native-panel-sizing.md` for the implementation plan this ADR now reflects) — Amended 2026-09-22 ×2 (ADR-028: native-resize P04 интеграция workspace-grid spans); Amended 2026-09-24 (`ResizeConstraints.SizeGridStep`: resize honors `GridSize`); Amended 2026-09-27 (ADR-037: D-6 System-DPI-awareness закрыт, PerMonitorV2; D-3 — `NativeSizingCoordinateMapper` больше не единственная точка конверсии); Amended 2026-10-09 (ADR-audit final pass: `NativeSizingCoordinateMapper` не имеет production-вызовов — только тесты и комментарии; класс сохранён); Amended 2026-10-10 (решение пользователя: native-путь — единственный; Фаза 0 выполнена — дефолт `NativePanelSizing = True`, карточка скрыта, закрыты пробелы G-1…G-6); Amended 2026-10-10 (**Фаза 1 выполнена**, коммит `bc60c8e`, после гейта «native проверен»: WPF-путь через `Thumb` и ключ `NativePanelSizing` удалены — флага и второго пути больше нет, `ChildWindow` строит `NativePanelResizeController` безусловно; Rollout/C8 выполнен; golden легаси/native → regression-golden против `ExpectedPipelineOracle`; Фаза 2 — документация, этот проход)

> **Состояние на 2026-10-10 (Фаза 1 выполнена).** Тексты Context, Decision (D-1…D-6), Alternatives, Rollout, Consequences и
> Invariants ниже написаны, когда два пути жили параллельно (WPF `Thumb` по умолчанию, native за флагом
> `NativePanelSizing`). Сейчас **существует только native-путь**: флага `NativePanelSizing`, `NativeSizingSwitch`
> (env/CLI), `PanelResizeController`, `ResizeOverlay` с `Thumb`'ами нет. Фразы «feature flag», «legacy path»,
> «два контроллера», «rollback — один флаг» читать как историю; действующее состояние — в разделе
> «Amendment 2026-10-10: Фаза 1» и в сводной таблице в конце.

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
rollback stays available. *(Superseded 2026-10-10: the flag defaulted to `True` in Phase 0 and was removed
together with the `Thumb` path in Phase 1 — see the Phase 1 amendment.)*

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
*(2026-10-10: the legacy `Accumulate`-chain path is gone, so "legacy vs native" is no longer comparable. The
equivalence suite became a regression-golden against the pure pipeline `ExpectedPipelineOracle`
(`Accumulate → Apply → SnapBounds`) in `NativePanelResizeControllerTests`/`NativePanelResizeControllerGridAlignedTests`;
the calculator-level property "`Apply` does not depend on how `raw` was obtained" stays pinned by
`ResizeGeometryCalculatorApplyEquivalenceTests`.)*

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

> **Частично устарело (2026-10-10, G-4).** Утверждения «`endReason` всегда `Completed`» и «`canceled` всегда `false`» верны
> только если rect окна изменился до ESC (или если порядок `WM_EXITSIZEMOVE`/восстановления rect окажется неблагоприятным —
> риск MV-7). ESC при rect, равном стартовому, даёт `ResizeEndReason.Canceled`/`canceled:true` без персиста и без демоции
> `AutoContent → Manual` (см. Amendment 2026-10-10, G-4). Исходный текст сохранён ниже как история.

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

**Выполнен (C8, 2026-10-10).** Дефолт флага стал `True` (Фаза 0, коммиты `e0b7fe8`/`36aece3`), затем, после гейта «native
проверен» пользователем, Фаза 1 (коммит `bc60c8e`) удалила `ResizeOverlay`, `PanelResizeController`, `NativeSizingSwitch` и
сам флаг. Текст ниже — исходный план rollout'а, сохранён как история.

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
- rollback is a single setting flip with no code path removed. *(No longer true since 2026-10-10: the flag and
  the legacy path are removed; rollback is the previous release candidate.)*

Negative:
- two parallel resize controllers (`PanelResizeController`, `NativePanelResizeController`) must be
  kept in sync manually until C8 removes the legacy one; `ResizeConstraintsFactory` was extracted
  specifically to avoid a second copy of constraint-building logic; *(removed in Phase 1, 2026-10-10 — one controller
  remains)*;
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
- ~~`NativePanelSizing` defaults to `False`; flipping it to `True` must not require a code change,
  only a setting/env/CLI value (Rollout).~~ **Снят 2026-10-10:** ключа нет (Фаза 1). Новый инвариант:
  `ChildWindow` строит `NativePanelResizeController` безусловно; повторное введение второго пути ресайза или
  настройки-переключателя требует нового ADR.

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

## Amendment 2026-09-27 (ADR-037): PerMonitorV2 и конверсия координат

[ADR-037](ADR-037-permonitor-v2-dual-mode-desktop-scale.md) закрывает D-6 («DPI-awareness приложения не
меняется в этой фазе»): приложение PerMonitorV2 (манифест), единая шкала desktop-слоя — `DesktopScaleModel`/
`IDesktopScaleProvider`. Следствия для этого ADR (сверено с `NativePanelResizeController`):

- **D-3.** `NativeSizingCoordinateMapper` **больше не единственная точка конверсии**: `WM_SIZING`-прямоугольники
  конвертируются в desktop-DIP через `IDesktopScaleProvider.Current.PxToDip(Rect)` и обратно через
  `DipToPx` с независимым `Math.Round` каждого края (`ToDesktopDip`/`ToNativePx`), а bounds окна читаются/пишутся
  через `PanelWindowCoordinates`. Инвариант «`INativePanelSizingClient` никогда не получает DIP — только пиксели»
  сохранён (конверсия остаётся в UI-контроллере, не в сервисе).
  **Уточнение 2026-10-09 (ADR-audit final pass, сверено поиском по `src/`):** после этого перехода у
  `NativeSizingCoordinateMapper` (`UI/Interaction/Native/`) **нет ни одного production-вызова** — `ToDip`/`ToPx` используются
  только в тестах (в т. ч. `NativeSizingCoordinateMapperTests`, `…GridAlignedTests`, `ResizeGeometryCalculatorApplyEquivalenceTests`) и
  упоминаются в комментариях (`NativePanelResizeController.ToDesktopDip`/`ToNativePx` повторяют контракт его округления,
  `WorkspaceLayoutPolicy`, `PanelSizingGridSupport`). Это не «одна точка конверсии» и не вторая политика DPI: класс — мёртвый
  в production эталон округления, оставленный как тестируемый контракт; удаление — отдельная чистка (не сделана в этом проходе).
- **D-6.** Источник правды о смене DPI/монитора — по-прежнему `IMonitorService.MonitorsChanged`; прежняя
  формулировка «приложение остаётся System-DPI» не действует.
- D-2/D-4 (один калькулятор, нет второго `Accumulate`) не затронуты.

## Amendment 2026-10-10: native-путь — единственный (Фаза 0)

**Решение пользователя (2026-10-10)** — пункт 3 «Решений, ожидающих пользователя» в `docs/ROADMAP.md`: оставить
**native**-путь (Win32 sizing loop), удалить WPF-путь через `Thumb` (`PanelResizeController` + `ResizeOverlay`).
План и инвентаризация — [`docs/PLANS/native-resize-single-path.md`](../docs/PLANS/native-resize-single-path.md). Работа
разбита на фазы; **выполнена Фаза 0** (коммиты `e0b7fe8` — флип дефолта и грипы, `36aece3` — пробелы native-пути).
Фаза 1 (удаление Thumb-пути и ключа) была обусловлена явным «native проверен» пользователя по ручному списку MV-0…MV-13
ниже; **гейт пройден, Фаза 1 выполнена 2026-10-10 — см. следующий amendment.** Текст этого раздела описывает состояние
после Фазы 0 (когда оба пути ещё присутствовали в коде); Фаза 0 была сборкой для проверки, не релизом.

**Что сделано в Фазе 0 (сверено с кодом):**

- **Дефолт `NativePanelSizing = True`** (`Settings.settings` и `Settings.Designer.cs` синхронно) — явное разрешение
  пользователя на этот ключ замороженного контура; сам ключ остаётся до Фазы 1 (откат без перекомпиляции:
  `--no-native-sizing` / `DESKTOPORGANIZER_NATIVESIZING=0`). Карточка «Native resize (experimental)» в
  `GeneralSection.xaml` — `Visibility="Collapsed"` (ресурсные ключи остаются ссылаемыми). **Известное ограничение:**
  сохранённое в `user.config` значение `False` по-прежнему перекрывает новый дефолт — такой пользователь остаётся на
  Thumb-пути при скрытом тумблере (семантику загрузки настроек не меняли); для проверки см. MV-0.
- **G-1 — `WM_DPICHANGED` больше не обрывает жест** (amends [ADR-037](ADR-037-permonitor-v2-dual-mode-desktop-scale.md)):
  `NativePanelResizeController.DpiChanged` не трогает сессию; шкала desktop-DIP фиксируется снимком
  `IDesktopScaleProvider.Current` на `SizeMoveEntered` (`_gestureScale`) и используется всеми конверсиями жеста, поэтому
  снап продолжается, а `WM_EXITSIZEMOVE` персистит результат один раз. `ChildWindow.OnDpiChanged` по-прежнему пропускает
  отложенное повторное применение, пока трекер не `Idle`. Риск: WPF отвечает на необработанный `WM_DPICHANGED` suggested
  rect, который следующий `WM_SIZING` перекрывает (до одного кадра скачка) — при заметном дрожании fallback: оборвать
  жест **и** прервать системный цикл (механизм G-2).
- **G-2 — прерывание системного цикла.** Новый член `INativeApi.CancelSizingLoop(hwnd)` (`GetCapture` → `WM_CANCELMODE` →
  `ReleaseCapture`, P/Invoke остаётся только в `Tools/NativeApi.cs`), `INativePanelSizingService.CancelActiveSizing(hwnd)`.
  `NativePanelResizeController.AbortResize` при активной сессии отменяет цикл и завершает жест; сохранение отложено (M-3)
  **кроме teardown** (`Closing`/`Unloaded`) — там сохранение синхронное, потому что отложенный колбэк пережил бы окно.
  Это обоснованное отклонение от плана, где откладывались все причины.
- **G-3 — визуальные грипы Edit Layout** (amends [ADR-026](ADR-026-edit-layout-mode.md) D-6): `ResizeGripVisuals` — Grid из
  8 `Border` (`IsHitTestVisible="False"`) в `ChildWindow.xaml`; видимость следует за тем же флагом гейта через
  `NativeSizingAffordance(UIElement? gripVisuals = null)`. Hit-test по-прежнему делает `WM_NCHITTEST` (D-5).
- **G-4 — ESC (частично снимает R-8; amends [ADR-021](ADR-021-interaction-state-and-gesture-lifecycle.md)):** если на
  `SizeMoveExited` итоговый rect равен стартовому, жест завершается как `ResizeEndReason.Canceled` **без** персиста и без
  демоции `AutoContent → Manual` (`canceled: true` в диагностике). Дополнение (коммит `550a096`, ревью): тот же guard
  применяется и к отложенному персисту после `AbortResize` → `CancelSizingLoop` — если окно вернулось в стартовый rect
  (`INativePanelSizingService.TryGetWindowRect`), персист и `NotifyHeightResizeCommitted` пропускаются; синхронный
  teardown-персист (`Closing`/`Unloaded`) не затронут. Раздел «R-8 deviation» выше остаётся верным для случая,
  когда rect изменился до ESC: система восстанавливает исходный rect, но отличить такой ESC от обычного окончания по
  сообщениям нельзя.
- **G-5 — `WM_MOUSEACTIVATE`:** `MA_NOACTIVATE` только если `LOWORD(lParam)` — sizing hit-code (`HTLEFT`…`HTBOTTOMRIGHT`,
  10–17); иначе `handled = false` (поведение как без хука). Прежняя формулировка «всегда `MA_NOACTIVATE`» не действует.
- **G-6 — `WM_NCLBUTTONDBLCLK` на sizing-краях проглатывается** (`handled = true`): двойной клик по верхнему/нижнему краю
  не запускает системный «вертикальный maximize». Snap перетаскиванием верхнего края к верху экрана — только ручная
  проверка (MV-11).
- **ADR-049 проверен — конфликта нет:** оба хука на одном `HwndSource`, ни один не трогает чужие сообщения; кадры цикла идут
  с `SWP_NOZORDER` (горячий путь), режим E не затронут. Остаётся пунктом ручной проверки (MV-9).
- `ResizeGeometryCalculator.cs` не менялся (инвариант); схема, миграции, DTO не затронуты.

**Rollout (раздел выше) и Invariants — уточнение:** «`NativePanelSizing` defaults to `False`» и «Legacy path … is the
default» **больше не действуют** (дефолт `True`); «Removing `ResizeOverlay` is out of scope» относилось к Фазе 1 (выполнена).

**Ручная проверка на Windows 11 (гейт Фазы 1, список MV-0…MV-13 из плана; гейт пройден 2026-10-10, MV-7 остаётся известным риском):**

| # | Сценарий |
|---|---|
| MV-0 | native активен: в `user.config` нет `NativePanelSizing=False` (или запуск с `--native-sizing`); в логе ресайз = `SizeMoveEntered` + `captureOwner=NativeSizingLoop`; записать режим слоя E/T |
| MV-1 | ресайз за каждый из 8 краёв/углов, курсоры верные (G-9), противоположный край неподвижен |
| MV-2 | снап к icon-grid, `GridSize` > 1, workspace-grid (`GridAligned`) — шаги как раньше, нет «доскока» |
| MV-3 | Alt во время ресайза (`GridAligned` и `GridSize` > 1) — свободный ресайз, нормализация на отпускании (G-7) |
| MV-4 | свёрнутую панель нельзя ресайзить, после разворота можно |
| MV-5 | anti-overlap (ADR-017) для `GridAligned`: упор в слот соседа |
| MV-6 | Normal (`MoveOnly`): края не ресайзят; Edit Layout: ресайз + видимые грипы (G-3); Lock: ресайза нет; смена Normal-жестов без рестарта |
| MV-7 | ESC внутри цикла (G-4/R-8): исходный размер, AutoContent не становится Manual, после рестарта размер исходный |
| MV-8 | mixed-DPI (100 % + 150 %): ресайз через границу мониторов (G-1) — жест не обрывается, без скачков/дрожания, размер сохраняется |
| MV-9 | ADR-049 (режим T): во время и после ресайза панель под окнами приложений; Win+D не ломается |
| MV-10 | активация/клавиатура (G-5): F2, Delete, Enter, Esc, закрытие меню кликом мимо работают; фокус активного приложения при ресайзе за край не отнимается |
| MV-11 | двойной клик по верхнему/нижнему краю; верхний край к верху экрана (G-6) — нет вертикального maximize |
| MV-12 | hover у края (G-8), правый клик по краю (нет системного меню) |
| MV-13 | рестарт приложения, перезапуск Explorer, смена страницы, clone панели, Folder Portal — затем ресайз |

**Открытый риск MV-7 (порядок `WM_EXITSIZEMOVE` и восстановления rect).** Реализация G-4 опирается на допущение, что при
ESC на `WM_EXITSIZEMOVE` окно уже имеет исходный rect, поэтому `finalRect == _startWindowRectPx`. Если система сначала
доставляет `WM_EXITSIZEMOVE`, а исходный rect восстанавливает позже, сравнение не сработает: жест завершится как
`Completed` и (как до Фазы 0) демотирует `AutoContent` — ровно поведение R-8. Проверить можно только на живой Windows
(MV-7); контрмера по результату — `win32-interop-specialist`.

**Остаётся принятым/проверяемым вручную:** G-7 (Alt читается через `Keyboard.Modifiers` внутри вложенного цикла при
неактивном окне; fallback — `GetAsyncKeyState` через `INativeApi`), G-8 (hover на крае — non-client, `IsMouseOver` = false),
G-9 (курсоры), G-10 (`onResizeGestureStarting` вызывается до `TryBegin`), G-11 (после Фазы 1 нет запасного пути при сбое
`Attach` — лог `AttachFailed`). Проверяется только режим слоя (E или T), доступный на машине пользователя; код
режимо-независим, остаток — известное ограничение.

**Фаза 1 (после «native OK») — выполнена, см. следующий amendment.** Был запланирован удалить `PanelResizeController`,
`ThumbOverlayAffordance`, `NativeSizingSwitch`, `ResizeOverlay` с Thumb'ами и `ResizeHandleStyle`,
`IGestureDiagnostics.CaptureGot/CaptureLost`, ключ `NativePanelSizing`, перевести golden в regression-golden и переписать
геометрические тесты на `ResizeGripVisuals`; затем Фаза 2 — доработка этого ADR и матриц `docs/Refactoring/Testing/`.

## Amendment 2026-10-10: Фаза 1 выполнена — единственный путь ресайза

Гейт Фазы 1 («native проверен» пользователем на Windows 11, список MV-0…MV-13) пройден; Фаза 1 плана
[`native-resize-single-path.md`](../docs/PLANS/native-resize-single-path.md) выполнена коммитом `bc60c8e`
(по отчёту коммита — порядка −1,5 k строк нетто). Сверено с кодом на HEAD `f8c88c8`.

**Удалено:**

- `PanelResizeController`, `ThumbOverlayAffordance`, `NativeSizingSwitch` (env `DESKTOPORGANIZER_NATIVESIZING`, CLI `--native-sizing`/
  `--no-native-sizing`), `ResizeOverlay` с 8 `Thumb` и их обработчиками, `ResizeHandleStyle`.
- Диагностика: `IGestureDiagnostics.CaptureGot`/`CaptureLost` и вся категория `capture` (в `src/` не осталось ни одного
  вызова `Write("capture", …)`). `PanelClosing(panelId, thumbIsDragging)` → `PanelClosing(panelId, resizeActive)`,
  значение — `_interactionTracker.State == PanelInteractionState.Resizing` (`ChildWindow.OnClosing`). `ResizeStarted` теперь
  пишется только из `WM_ENTERSIZEMOVE`-обработчика native-контроллера (`captureOwner` = константа `NativeSizingLoop`).
- Ключ `NativePanelSizing` — явное разрешение пользователя на этот ключ замороженного контура (2026-10-10; остальные
  ключи, `schemaVersion`, миграции, DTO, `JsonApplicationStateStore`, `UserConfigFolderMigration` не затронуты): из
  `IAppSettings`/`AppSettings`/`Settings.settings`/`Settings.Designer.cs`, обеих VM (`GlobalDefaultsViewModel`,
  `MainWindowViewModel`), скрытой карточки «Native resize (experimental)» в `GeneralSection.xaml` и resx
  `Settings_General_NativeResize_*` (en+ru). В `src/`, `installer/`, `.github/` нет ни одного упоминания `NativePanelSizing`/
  `native-sizing`/`NATIVESIZING`.

**Единственный путь.** `ChildWindow` собирает `NativeSizingAffordance(ResizeGripVisuals)` → `ResizeAffordanceGate` →
`RollUpBehavior` → `PanelDragMoveBehavior` → `NativePanelResizeController` без ветвления: одно поле
`_resizeController : NativePanelResizeController` (cleanup `70c3d13` после ревью: интерфейс `IPanelResizeController` с
единственной реализацией и второе поле удалены). Грипы Edit Layout — только визуальные
(`ResizeGripVisuals`, `IsHitTestVisible="False"`, стили `ResizeGripVisualStyle`/`ResizeGripVisualEdgeStyle`); hit-test
целиком у `WM_NCHITTEST` (`NativeHitTestMapper`). D-1…D-6 остаются в силе; из них только формулировки про флаг и «легаси-путь»
стали историческими (см. баннер в начале документа).

**Старый `user.config` с ключом.** `<setting name="NativePanelSizing">` в уже записанном `user.config` игнорируется молча:
`LocalFileSettingsProvider` читает только объявленные `SettingsProperty`, а при `Save()` не трогает чужие элементы секции —
«осиротевший» ключ остаётся в файле безвредно и **сознательно не чистится** (чистка = изменение семантики сохранения
замороженного контура; `UserConfigFolderMigration` копирует файл как есть). Автотест закрепляет объявленную поверхность
(`NativePanelSizingKeyRemovedTests`: ключ не объявлен в `Settings.Default.Properties`, нет в `IAppSettings`, нет в
`GeneralSection`); сам разбор старого файла на живой Windows — пункт ручной проверки (план §4).

**Golden → regression-golden.** `LegacyApply` (Accumulate-цепочка «как у Thumb») заменён на `ExpectedPipelineOracle` в
`NativePanelResizeControllerTests` (чистый конвейер `Accumulate → Apply → SnapBounds`, кейсы и ненулевой origin-offset
сохранены) и в `NativePanelResizeControllerGridAlignedTests`; `ResizeGeometryCalculatorApplyEquivalenceTests` оставлен
(свойство самого калькулятора). Удалены `NativeSizingSwitchTests`; `ResizeOverlayGeometryTests` → `ResizeGripVisualsTests`.
`ResizeGeometryCalculator.cs` не менялся (инвариант D-2).

**Мёртвый код, оставленный сознательно:**

- `ResizeEdgeParser.TryParse` — после Фазы 1 остался без production-вызовов (единственные вызывающие были в
  `PanelResizeController`) и был удалён cleanup'ом `70c3d13`; остальные члены `ResizeEdgeParser`
  (`AffectsWidth`/`AffectsHeight`/…) используются.
- `NativeSizingCoordinateMapper` — по-прежнему не имеет production-вызовов (см. уточнение 2026-10-09 выше); используется
  только тестами (`NativeSizingCoordinateMapperTests`, `…GridAlignedTests`, `NativePanelResizeControllerTests`,
  `NativePanelResizeControllerGridAlignedTests`, `PanelSizingGridSupportCommitTests`) и комментариями как эталон округления
  `ToDesktopDip`/`ToNativePx`.
- `NativePanelResizeController.Detach()` (`=> Dispose()`) — нет ни одного вызывающего (ни в `src/`, ни в тестах); teardown идёт
  через `Dispose()`.
  Решение «удалить или оставить» для двух последних — открытый **DO-050** (ROADMAP, `docs/Refactoring/BACKLOG.md`).

**Обоснования, перенесённые из удалённого `PanelResizeController`.** Комментарии вида «see `PanelResizeController`'s matching
remark» потеряли адресата; ключевые правила, общие для обоих (теперь одного) `SnapWidthToGrid()`-путей, сверены с текущим
`NativePanelResizeController` и ADR-011/018/029:

- **F-2 (workspace-grid, §3.7).** Ширину `GridAligned`-панели определяет рабочая сетка (ADR-028), а не размер значка:
  `SnapWidthToGrid` для неё не работает (`PanelSizingGridSupport.IsGridAligned` → `IsIconGridSnapActive` = `false`).
- **Приоритет `GridSize` (ADR-011 Amendment 2026-10-09, ADR-audit WP-C / C3).** При `GridSize > 1` snap к сетке значков не
  действует нигде — ни в жесте (`BuildConstraints`), ни в `SnapWidthToGrid`; оба берут один предикат
  `ResizeConstraintsFactory.IsIconGridSnapActive` и `sizeGridStep` из `ResolveSizeGridStep`. Раньше предикат дублировался в двух
  контроллерах; теперь контроллер один, предикат по-прежнему общий с жестом.
- **M-2 (ADR-029 Решение 4): резерв скроллбара.** Ширина контента для snap'а = ширина окна − `GridMetrics.ContentHorizontalChrome(
  policy, SystemParameters.VerticalScrollBarWidth)` (для `FixedGap` — отступ + резерв системного скроллбара, для `LegacyStretch`
  резерва нет); минимум — `GridMetrics.MinPanelWidth(...)`.
- **M-1 (finalization): `Math.Ceiling`.** Приведённая к сетке ширина округляется **вверх** до целого пикселя, никогда вниз:
  округление вниз могло дать ширину на 1 px меньше целевой, и следующий проход snap'а видел бы другое значение (дрейф).
- **`ResizeSnapEdgePadding(cell)`** = `AdaptiveGridLayout.EdgePadding(cell, DefaultEdgePaddingRatio)` — горизонтальный отступ у
  краёв, который адаптивная раскладка (ADR-018) держит по бокам ряда ячеек; передаётся в `AdaptiveGridLayout.SnapExtent`.
- **P06 MAJOR-4 (ADR-029, «Fix-цикл P06»).** После завершённого жеста, затрагивающего ВЫСОТУ, `PersistAfterGesture` в конце
  (после `SaveWindowsState`) вызывает `PanelSizingGridSupport.NotifyHeightResizeCommitted(host, committedEdge)`: `AutoContent`
  понижается до `Manual` («manual wins»), событие `SizingModeChanged(reason="HeightResizeCommitted")`. Край жеста передаётся
  значением (`committedEdge`), потому что отложенный персист может выполниться уже после старта нового жеста. Для native-ESC с
  неизменным rect (G-4) этот шаг пропускается вместе со всем персистом.

Полный исторический текст: `git show bc60c8e^:src/DesktopOrganizer.UI/Behaviors/PanelResizeController.cs`.

**Остаточные риски и известные ограничения.**

- **MV-7** (порядок `WM_EXITSIZEMOVE` и восстановления исходного rect при ESC) остаётся известным открытым риском; контрмера
  по результату проверки — `win32-interop-specialist`.
- **G-11:** запасного пути нет. Если `Attach` не удался, панель остаётся без ресайза; сбой логируется как
  `NativePanelSizingService`/`AttachFailed` (`IAppLog`). Откат — предыдущий release candidate.
- **Откат на старую сборку.** Старая сборка с ключом прочитает осиротевшее значение либо свой дефолт `False` и уйдёт на Thumb-путь;
  судьба состояния панелей определяется ADR-024 (после перехода на схему v7 сборки ≤0.8.0 открывают файл как пустую read-only
  сессию — Upgrade notes в `CHANGELOG.md`), а не этим ADR.
- **Только мышь (mouse-only).** Ресайз проверялся мышью; touch/pen-ресайз через системный sizing loop не проверен —
  принятое ограничение (DO-054).
- **G-8 (known).** Над полосой ресайза (край — non-client) `PanelRoot.IsMouseOver` = `false`: хром/граница панели «гаснут» при
  наведении на край в Normal+MoveAndResize и в Edit Layout. Косметика; фикс — по результату ручной проверки (DO-052).
- **G-9 (known).** Курсоры краёв системные (`DefWindowProc` `WM_SETCURSOR` по HT-кодам), собственного `Cursor` у WPF-элементов
  больше нет; 8 курсоров проверить вручную, фикс — по результату (DO-053).
  Оценка (ревью `dfe193a`): панель в режиме E остаётся `WS_POPUP` после `SetParent` (без `WS_CHILD`), поэтому
  `DefWindowProc` в норме не пересылает `WM_SETCURSOR` родителю, но поведение popup с не-desktop родителем не
  документировано; в режиме T (Win11 24H2+) риска нет. Дешёвый фикс, если курсор окажется стрелкой: обработать
  `WM_SETCURSOR` в том же hook для `wParam == hwnd` и HT 10–17 → `SetCursor(IDC_SIZEWE|SIZENS|SIZENWSE|SIZENESW)` через
  два новых члена `INativeApi` (`LoadSystemCursor`, `SetCursor`), `handled=true`.
- **Контракт `WM_NCHITTEST` (коммит `dfe193a`, ревью Фазы 1).** Порядок в hook: сначала дешёвый `IsHitTestEnabled`
  клиента (гейт), затем один `GetWindowRect`; при выключенном гейте точка внутри окна получает **явный** `HTCLIENT`
  (`handled=true`), снаружи — unhandled; при включённом гейте — DPI + `HitTest`, край → HT 10–17, `None` внутри окна →
  тоже явный `HTCLIENT`. Это снимает зависимость гейта от стилей окна (`ResizeMode` по умолчанию `CanResize`, и
  `WS_THICKFRAME` у окна вполне может быть): `DefWindowProc` больше не может ответить `HT*` в обход гейта и запустить
  цикл с `edge=None` без снапа и персиста. При выключенном гейте вместо двух native-вызовов на каждый `WM_NCHITTEST`
  остаётся один (`GetWindowRect`, он нужен для «внутри/снаружи»).
- G-7 (Alt через `Keyboard.Modifiers` внутри вложенного цикла) и G-10 (`onResizeGestureStarting` до `TryBegin`) остаются
  принятыми/проверяемыми вручную, как описано выше.

**Ручная проверка после Фазы 1 (добавлено в manual verify):** (а) положить в `user.config` (`%LOCALAPPDATA%\khagendev\...`)
элемент `<setting name="NativePanelSizing">` со значением `False` и запустить сборку: приложение стартует без ошибок и
предупреждений, остальные настройки (`GridSize`, `Language`, …) на месте, ресайз идёт нативным путём (в `--diag`-логе
`SizeMoveEntered` + `captureOwner=NativeSizingLoop`), после выхода элемент по-прежнему лежит в файле нетронутым;
(б) MV-7 (ESC посреди цикла); (в) DO-052/DO-053 — hover и курсоры на краях.

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-22 | [ADR-028](ADR-028-workspace-grid.md) | `GridAligned` в resize-конвейере (`SnapBounds`/`ClampResizeToSlots` в `ResizeGestureSession.TryUpdate`); span персистится вместе с геометрией; golden-тесты расширены | Формулировка первого amendment «`SnapCellWidth/Height` берутся из `UnitSize`» (заменена вторым) |
| 2026-09-24 | ADR-011 amendment | `ResizeConstraints.SizeGridStep`, `setBounds`-делегат, `NormalizeCommittedSize` | — |
| 2026-09-27 | [ADR-037](ADR-037-permonitor-v2-dual-mode-desktop-scale.md) | PerMonitorV2; конверсия через `DesktopScaleModel` | D-3: «одна точка конверсии `NativeSizingCoordinateMapper`»; D-6: «DPI-awareness не меняется / System-DPI» |
| 2026-10-09 | ADR-audit final pass | Констатация: `NativeSizingCoordinateMapper` без production-вызовов (только тесты/комментарии) | Формулировка D-3 «`NativeSizingCoordinateMapper` — конверсия native-пути» как описание живого кода |
| 2026-10-10 | Решение пользователя (коммиты `e0b7fe8`, `36aece3`, [план](../docs/PLANS/native-resize-single-path.md)) | Native — единственный путь; Фаза 0: дефолт `NativePanelSizing = True`, карточка скрыта; G-1 `WM_DPICHANGED` не обрывает жест (amends ADR-037); G-2 `CancelSizingLoop`/`CancelActiveSizing`, `AbortResize` отменяет системный цикл; G-3 `ResizeGripVisuals` (amends ADR-026 D-6); G-4 ESC с неизменным rect → `Canceled` без save (amends ADR-021, частично R-8); G-5 `MA_NOACTIVATE` только на HT 10–17; G-6 `WM_NCLBUTTONDBLCLK` на краях проглочен | Rollout: «`NativePanelSizing` default `False`» и «legacy — по умолчанию»; Invariants: «defaults to `False`»; R-8: «`canceled` всегда `false`» (теперь `Canceled` приходит при неизменном rect); «`MA_NOACTIVATE` для любого клика» (G-5) |
| 2026-10-10 | Решение пользователя (коммит `bc60c8e`, Фаза 1, [план](../docs/PLANS/native-resize-single-path.md)) | **Фаза 1:** `PanelResizeController`, `ThumbOverlayAffordance`, `NativeSizingSwitch` (env/CLI), `ResizeOverlay` + 8 `Thumb`, `ResizeHandleStyle`, `CaptureGot`/`CaptureLost` и категория `capture` удалены; ключ `NativePanelSizing` удалён из `IAppSettings`/`AppSettings`/`Settings.settings`/Designer/VM/карточки/resx (явное разрешение пользователя); `ChildWindow` строит `NativePanelResizeController` безусловно; `PanelClosing.thumbIsDragging` → `resizeActive`; golden `LegacyApply` → regression-golden `ExpectedPipelineOracle`; старый `user.config` с ключом игнорируется молча | Rollout/C8 (исполнен); Invariants: «`NativePanelSizing` defaults to `False`»; D-1…D-6 в части «feature flag»/«legacy Thumb path»/«два пути»; Consequences: «rollback — один флаг», «два контроллера синхронизируются вручную»; D-4: «bit-for-bit golden легаси/native» (теперь regression-golden против чистого конвейера); Phase 0: «сохранённое `False` по-прежнему действует», «откат `--no-native-sizing`/`DESKTOPORGANIZER_NATIVESIZING=0`» |

**Состояние (2026-10-10, после Фазы 1):** единственный путь ресайза — native; флага и legacy-пути нет; `ResizeEdgeParser.TryParse` и
`NativeSizingCoordinateMapper` оставлены сознательно (DO-050 открыт); открытый риск — MV-7. Решение п. 3 «Решений, ожидающих
пользователя» (`docs/ROADMAP.md`) исполнено полностью (Фазы 0, 1, 2).

**Manual verify.** `docs/ROADMAP.md` (строки 6–8) признаёт manual verify по Фазам 0–10 выполненной на
Windows 11 24H2 2026-09-27 и достаточной. Ссылка выше «see the manual repro matrix rows 20–31»
(`docs/Refactoring/Testing/resize-repro-matrix.md`) остаётся историческим чек-листом. Гейт Фазы 1 (проверка native-пути
пользователем по списку MV-0…MV-13) пройден 2026-10-10 — по сообщению пользователя; результаты по строкам в репозитории не
записаны. Остаются вручную: MV-7 (ESC) и разбор старого `user.config` с осиротевшим ключом `NativePanelSizing` сборкой после
Фазы 1 (план §4).
