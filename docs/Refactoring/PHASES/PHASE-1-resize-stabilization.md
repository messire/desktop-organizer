# Phase 1 — Resize Stabilization + Interaction State

## Почему

Текущий resize построен на восьми WPF `Thumb` в `ChildWindow.xaml`. `PanelResizeController` хранит raw geometry, но не имеет явного gesture state/cancellation path. В отличие от title-bar drag, resize path не имеет симметричной обработки `LostMouseCapture`.

## Работы

### 1.1 Ввести явный resize lifecycle

Добавить состояние:

```csharp
public enum PanelInteractionState
{
    Idle,
    Hover,
    Editing,
    Dragging,
    Resizing
}
```

Для resize controller:

- `_isResizing`;
- `_activeThumb` / direction;
- `BeginResize()`;
- `UpdateResize()`;
- `EndResize(bool cancelled)`;
- idempotent cleanup.

### 1.2 Capture hardening

На `DragStarted`:

- explicitly capture mouse/Thumb;
- сохранить стартовую геометрию;
- установить `PanelInteractionState.Resizing`.

На:

- `LostMouseCapture`;
- `Unloaded`;
- unexpected source change;
- cancellation path;

вызвать единый cleanup.

### 1.3 Сохранение состояния

`SaveWindowsState()` только после успешного end gesture, не на каждый delta. При cancellation сохранить последнюю валидную geometry только если она действительно применена.

### 1.4 Tests

Выделить pure geometry часть из `PanelResizeController` в testable calculator:

```text
ResizeGeometryCalculator
  input: start bounds, direction, deltas, constraints, grid
  output: bounds
```

Покрыть corner/edge, min size, snap on/off, list/grid.

## Артефакты

- `ADR/ADR-021-interaction-state-and-gesture-lifecycle.md`
- `UI/PanelInteractionState.cs`
- `UI/ResizeGeometryCalculator.cs`
- обновлённый `PanelResizeController`
- regression tests

## DoD

- 100 последовательных resize операций без зависания по всем 8 handles.
- Release мыши вне окна корректно завершает resize.
- Alt+Tab во время resize не оставляет панель в зависшем состоянии.
- `LostMouseCapture` всегда приводит к `Idle`.
- Geometry calculator имеет unit tests для всех направлений.
- Existing snap-to-grid semantics сохранены.

## Статус

**`[~]` Код + автотесты завершены (1084/1084 тестов зелёные, `dotnet build` чистый); manual verify
на живом Windows GUI (Win10/Win11) ожидает ручного прогона.**

Ветка `fix/resize-lifecycle` (от `rc/0.4.0`). Мастер-план реализации:
`docs/PLANS/phase-1-resize-stabilization.md`. Коммиты C1–C9 (`e0677e7`..`224a0b5`) + пост-ревью
фиксы `4c0ab17`, `df4ea10`, `7bfa688`, `adad643`. Прошли `code-quality-reviewer` (изначально
1 CRITICAL + 6 MAJOR — все исправлены, добавлены регрессионные тесты) и `fences-ux-parity-reviewer`
(snap-to-grid semantics подтверждены bit-for-bit идентичными, регрессий не найдено; self-healing
после Alt+Tab — новая функциональность, не баг). ADR-021: `Proposed` → `Accepted`.

### Реализовано (code-complete)

- Явный gesture lifecycle: `PanelInteractionState`/`PanelInteractionTracker` (Idle/Hover/Editing/
  Dragging/Resizing — `Hover`/`Editing` зарезервированы для Фазы 6, ни один путь Фазы 1 их не
  устанавливает, застраховано тестом) + `ResizeGestureSession` (идемпотентный Begin/Update/End,
  `ResizeEndReason`, `_ending`-guard от реентерантности через `Thumb.CancelDrag()`).
- `ResizeGeometryCalculator` (pure, Window-free) + `ResizeEdge`/`ResizeConstraints`, извлечён из
  `PanelResizeController` бит-в-бит (характеризационные тесты C1 до извлечения, C2 — извлечение без
  правок в золотых значениях); API спроектирован совместимым с Phase 2 `WM_SIZING` (R6).
- Capture hardening: реакция на `DragCompletedEventArgs.Canceled` (ранее игнорировалась — H1),
  try/catch вокруг delta-пути → `End(Failed)` без падения процесса (H2), единый cleanup-путь
  (`OnCaptureLost`/`OnUnloaded`/`AbortResize`).
- Self-healing после потери capture без `LostMouseCapture` на `Thumb` (Alt+Tab, перезапуск Explorer
  — H4): `RecoverStaleDrag` на `PreviewMouseLeftButtonDown` оверлея.
- Условный persist: `SaveWindowsState()` вызывается только если геометрия реально была применена
  (`AppliedDeltaCount > 0`), независимо от причины завершения (кроме `Failed`) — экран не откатывается
  WPF при `Canceled`, значит на диск должна попасть именно применённая геометрия.
- Фикс H3: `RollUpBehavior` — `DoubleAnimation` без `FillBehavior` навсегда удерживала
  `Window.HeightProperty` (`FillBehavior.HoldEnd`), из-за чего вертикальный resize переставал
  работать после первого сворачивания/разворачивания панели; плюс фикс гонки быстрого двойного
  toggle (M-6, generation counter).
- `endReason` (`Completed/Canceled/CaptureLost/SourceChanged/Unloaded/Closing/Failed`) в
  `ResizeCompleted`-событии диагностики (без новых имён событий, схема Фазы 0 остаётся совместимой).
- ~101 новый/обновлённый тест: `ResizeGeometryCalculatorTests` (~25, все 8 edges × Grid/List ×
  snap on/off × min-size × углы), `PanelInteractionTrackerTests` (~10), `ResizeGestureSessionTests`
  (~18, включая 100-итерационный fuzz по всем edge × всем `ResizeEndReason`), обновлённые
  `GestureDiagnosticsTests`, плюс регрессионный набор `resize lifecycle regression suite`
  (stale-drag reuse, true gesture-session reentrancy).
- ADR-021 переведён `Proposed` → `Accepted`.

### НЕ выполнено в этой среде — manual verify (Win10/Win11)

Среда разработки ограничена `dotnet build`/`dotnet test`, нет живого Windows GUI. Остаются
пунктами `docs/Refactoring/Testing/resize-repro-matrix.md`:

- 15 живых сценариев матрицы (100x resize по каждой грани/углу, release мыши вне окна, Alt+Tab,
  контекстное меню, virtual desktop/DPI/монитор, sleep/wake, перезапуск Explorer, resize↔drag
  вплотную, закрытие панели в середине жеста);
- проверка `--diag`-лога: обычный mouse-up должен логироваться как `endReason: "Completed"`, а не
  `"CaptureLost"` (M-1);
- H3 вживую: roll-up → roll-down → вертикальный resize сразу после — должен работать;
- быстрый двойной roll-up toggle (< 150мс) — без визуального рассинхрона (M-6);
- залипшая ручка после потери native capture — первый клик по той же ручке должен начинать новый
  жест, а не быть проглоченным (M-3, H4).

Фаза **не** закрывается как `[x]` до тех пор, пока эти сценарии не будут воспроизведены вручную на
Win10/Win11.
