# Phase 0 — Baseline, Diagnostics, Safety Net

## Цель

До изменения resize/WorkerW зафиксировать текущее поведение и получить воспроизводимость проблем вместо лечения по ощущениям.

## Работы

1. Создать минимальный repro checklist для зависающего resize:
   - resize за каждую грань/угол;
   - выход курсора за окно;
   - быстрый Alt+Tab;
   - переключение virtual desktop;
   - display/DPI change;
   - sleep/wake monitor;
   - Explorer restart;
   - context menu/open popup во время drag;
   - mouse button release вне окна.
2. Добавить structured debug logging для gesture lifecycle:
   - `ResizeStarted`;
   - `ResizeDelta` sampled/throttled;
   - `ResizeCompleted`;
   - `LostMouseCapture`;
   - `CaptureChanged`;
   - `WM_ENTERSIZEMOVE`, `WM_EXITSIZEMOVE` после native-перехода.
3. Зафиксировать baseline CPU/RAM/startup при 3/5/10 панелях и 100/500/1000 items.
4. Зафиксировать current UI screenshots: idle, hover, expanded, rolled-up, grid/list.
5. Добавить/обновить issue list из `docs/REFACTORING_PLAN.md`:
   - real single-instance отсутствует;
   - `ChildWindowViewModel` всё ещё крупный;
   - hardcoded UI colors;
   - native services без полного test coverage.

## Артефакты

- `Testing/resize-repro-matrix.md`
- `Testing/performance-baseline-template.md`
- structured log event list в этой фазе
- issue backlog в проекте

## DoD

- Есть воспроизводимый checklist минимум из 10 сценариев.
- Для каждого зависания можно определить последний lifecycle event по логу.
- Есть baseline startup/memory/CPU numbers на одной фиксированной машине.
- Есть screenshot baseline текущего UI.
- Все известные P0/P1 issues заведены как отдельные work items.

## Статус

**`[~]` Код завершён и code-complete; замеры baseline на живом Windows GUI ожидают ручного прогона.**

### Реализовано (code-complete, 983/983 тестов зелёные, `dotnet build` чистый)

- Structured JSONL diagnostics logging для gesture lifecycle: `IDiagnosticsLog`
  (`DesktopOrganizer.Services.Abstractions`) + `JsonLinesDiagnosticsLog`/`NullDiagnosticsLog`
  (`DesktopOrganizer.Services`), non-blocking producer/bounded queue/drop-on-full backpressure,
  фоновый writer-поток, privacy-инвариант (никаких заголовков/путей в payload) — см. ADR-027.
- `IGestureDiagnostics` (`DesktopOrganizer.UI.Diagnostics`) — типизированная фасадная обёртка,
  инструментирует resize/drag lifecycle и mouse capture в `PanelResizeController`/
  `PanelDragMoveBehavior`/`ChildWindow` (log-only, поведение не изменено — подтверждено
  `fences-ux-parity-reviewer` byte-for-byte до/после).
- Session header (`SessionStarted`/`SessionExiting`) + flush при выходе из приложения.
- DI-регистрация обоих сервисов, enable-переключатель (`--diag` / `DESKTOPORGANIZER_DIAG=1`,
  on by default в Debug, off by default в Release).
- Схема событий зафиксирована и задокументирована:
  `docs/Refactoring/Testing/diagnostics-log-events.md` (включая зарезервированные, но ещё не
  реализованные `SizeMoveEntered`/`SizeMoveExited` для Фазы 2).
- `resize-repro-matrix.md` обновлена с процедурой захвата/триажа лога.
- Issue backlog заведён: `docs/Refactoring/BACKLOG.md` (10 пунктов, DO-001..DO-010, из
  `REFACTORING_PLAN.md` §5.2).
- Регрессионные тесты на shutdown-гонки, ротацию файлов, backpressure под нагрузкой (commit
  `399c23d`) — закрывают 2 CRITICAL + 6 MAJOR находки `code-quality-reviewer` (C-1, C-2, M-1..M-5,
  MINOR-4..6).
- ADR-027 (Structured JSONL Diagnostics Logging), статус `Proposed`.

### НЕ выполнено в этой среде — manual verify (Win10/Win11)

Среда разработки ограничена `dotnet build`/`dotnet test`, нет живого Windows GUI:

- **Baseline CPU/RAM/startup** при 3/5/10 панелях и 100/500/1000 items — не измерено. Процедура и
  шаблон готовы (`Testing/performance-baseline-procedure.md`,
  `Testing/performance-baseline-template.md`), инструменты готовы
  (`tools/perf/New-PerfFixture.ps1`, `tools/perf/Measure-Startup.ps1`), числа пока не заполнены.
- **Screenshot baseline** (idle/hover/expanded/rolled-up/grid/list, light+dark, 100%/150% DPI) — не
  снято. Чек-лист готов (`Testing/screenshot-baseline-checklist.md`), `Screenshots/` — placeholder.
- **15 сценариев `resize-repro-matrix.md`** — не воспроизведены живьём (таблица готова к заполнению
  колонок «Last logged event»/«Log file» человеком на Windows).

Фаза **не** закрывается как `[x]` до тех пор, пока эти три пункта не будут выполнены вручную на
Win10/Win11 и не будут вписаны реальные числа/скриншоты/repro-результаты.
