# Phase 6 — Edit Layout UX

> **Мастер-план фазы: [`docs/PLANS/phase-6-edit-layout-ux.md`](../../PLANS/phase-6-edit-layout-ux.md)**
> (7 пунктов P6-1…P6-7, решения D-1…D-13, риски R-1…R-13, ручная матрица V-1…V-19).
> Архитектурное решение: [ADR-026](../../../adr/ADR-026-edit-layout-mode.md) (Proposed → Accepted в P6-7).
> Смежные инварианты, которые фаза обязана сохранить: ADR-021 (gesture lifecycle), ADR-022
> (`NativePanelSizing`), ADR-017 (anti-overlap), ADR-011 (magnetic grid snap), ADR-025 (visual states).

## Цель

Убрать случайные move/resize в обычном режиме и сделать layout editing предсказуемым.

## Режимы

- Normal
- Edit Layout
- Drag Item
- Rename Item

Panel interaction state остаётся локальным визуальным состоянием.

> **Уточнение плана (D-1):** глобальных режимов ровно два — `Normal` и `EditLayout`
> (`LayoutInteractionMode`, app-global singleton `ILayoutEditModeService`). `Drag Item` и `Rename Item` —
> это уже существующие panel-local состояния `PanelInteractionState.Dragging`/`Editing` (ADR-021) и
> `PanelVisualState` (ADR-025); второго, конкурирующего с трекером enum не заводим.

## В Edit Layout

- resize handles/hit zones активны;
- panel drag активен;
- guides/snap visualization видимы;
- lock layout доступен;
- accent border усиливается;
- можно показывать grid spacing.

## В Normal

- move/resize можно отключить полностью либо оставить modifier-based;
- controls приглушены;
- content interaction имеет приоритет.

> **Решение плана (D-2):** политика per-gesture, настройка `NormalModeLayoutGestures`
> (`MoveAndResize | MoveOnly | None`, default **`MoveOnly`**). Move с заголовка остаётся доступен
> (заголовок — однозначная ручка), но получает системный порог начала жеста; resize в Normal выключен —
> именно 6-пиксельные невидимые thumb'ы и есть источник случайных геометрических изменений.

## Артефакты

- `ADR/ADR-026-edit-layout-mode.md`
- interaction state diagram
- UX acceptance scenarios

Полный список новых/изменяемых файлов — §2 мастер-плана.

## Порядок работ

`P6-1 → (P6-2 ∥ P6-3) → (P6-4 ∥ P6-5) → P6-6 → P6-7` (§3 мастер-плана).
P6-6 (оверлей направляющих) — **отбрасываемый** пункт с заранее спроектированным fallback (D-10).

## DoD

Статус: `[~]` — код + автотесты завершены (P6-1..P6-5, ADR-026 Accepted), manual verify не выполнена в
этой среде. Полная трассировка — §4 мастер-плана.

- [x] **DoD-1.** Случайный drag иконки не двигает панель. — `LayoutGesturePolicy` (12-кейсовая таблица
      истинности) запрещает resize в Normal (default `MoveOnly`); move стартует только с заголовка и
      только после системного порога. Unit-закрыто; live-подтверждение — V-3/V-4 (manual).
- [x] **DoD-2.** Rename / context menu не запускают resize/move. — `canMove`-гейт проверяется до
      `CaptureMouse()`, после `_onDragGestureStarting()` и после ветки `ClickCount==2`;
      `ResizeAffordanceGate` выключает resize-thumb'ы в Normal на обеих ветках. Unit-закрыто; V-5/V-6
      (manual).
- [x] **DoD-3.** Edit mode включается/выключается одним действием (трей / меню панели / окно настроек).
      — три независимые точки (D-9 №1–3), `Toggle()` идемпотентен. Unit-закрыто; V-1/V-2 (manual).
- [x] **DoD-4.** Exit edit mode гарантированно завершает активные жесты (`AbortResize` → `AbortDrag` →
      `ForceIdle`, строго в этом порядке). Unit-закрыто; V-7 (manual).
- [x] **DoD-5.** State persists only if product decision explicitly says so: `Mode` — не персистится;
      `IsLayoutLocked` / `NormalModeLayoutGestures` — персистятся в `IAppSettings`. Unit-закрыто
      («после `Load()` приложение всегда в `Normal`»); V-12 (manual).
- [~] **DoD-6.** В Edit Layout активны resize handles, panel drag, guides; доступен lock layout; усилен
      accent border. — resize handles/drag/lock/accent border реализованы и unit-закрыты
      (`LayoutEditVisualsTests`). **Guides — только магнитное притяжение (`SnapGuideCalculator`,
      unit-закрыто), без визуального оверлея направляющих: P6-6 не реализован в этой итерации** (см.
      ADR-026 «Известные ограничения», DO-018). V-8/V-9/V-10 (manual, guide-визуал не проверяем —
      его нет).
- [x] **DoD-7.** В Normal контролы приглушены, приоритет у контента (значения Фазы 5 не сдвинулись). —
      регресс-тест `isLayoutEditing:false` ⇒ точное совпадение с `InteractionVisualState.*`. V-11
      (manual).
- [x] **DoD-8.** Нулевая регрессия move/resize/drop/rename/view mode; файлы из §0 мастер-плана — 0
      изменённых строк. — грep-гейт зелёный, полный `dotnet test` зелёный (1925/1925). V-13..V-16
      (manual).
- [x] **DoD-9.** `ChildWindowViewModel.cs` — 0 изменённых строк (DO-002). — грep-гейт по диффу.
- [x] **DoD-10.** Ни одного нового hex-литерала в `ChildWindow.xaml` / `MainWindow.xaml` (гейт Фазы 5). —
      `rg -n "#[0-9A-Fa-f]{3,8}"` → 0.
- [x] **DoD-11.** Решение отражено в ADR-026 (Proposed → Accepted). — этот close-out (P6-7).
- [x] **DoD-12.** Схема JSON не изменена (`StateEnvelope.CurrentSchemaVersion` остаётся 4), апгрейд user
      state не требуется; `GoldenMigrationTests`/`SchemaContractTests` зелёные без правок.

Три полных раунда `code-quality-reviewer` ∥ `fences-ux-parity-reviewer` с фиксами между ними + финальный
вердикт **APPROVE** (0 CRITICAL, 0 MAJOR, 5 MINOR не блокирующих — M-1/M-2 задокументированы в ADR-026 и
`docs/Refactoring/BACKLOG.md` DO-019/DO-020).

## Manual verify (Win10/Win11)

Фаза целиком про ввод — ни один из V-1…V-19+ не воспроизводим в среде разработки
(`dotnet build`/`dotnet test`). Матрица: `docs/Refactoring/Testing/edit-layout-manual-matrix.md`,
источник — §7.2 мастер-плана плюс пункты, добавленные ревьюерами в ходе раундов ревью. Не выполнено
в этой среде разработки (нет живого Windows GUI).
