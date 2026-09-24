# P0 — Panel spans, native resizing и roll-up

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 03; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Встрой `WorkspaceLayoutPolicy` в **уже существующий** `NativePanelResizeController`, `PanelResizeController` fallback и связанные Win32 handlers, не переписывая native sizing с нуля. Отмени зависимость итогового panel extent от `AdaptiveGridLayout.SnapExtent` в `GridAligned`. Определи corner anchoring (`HTLEFT` меняет left + width; `HTTOP` меняет top + height), preview/commit/cancel, DPI conversion, min width for header buttons, min height for 1 icon row и отдельный collapsed header height. Глобальные width/height spans и S/M/L/XL presets должны описывать одну модель (presets = именованные spans), но допускается manual freeform. Согласуй `WM_SIZING`, WPF layout, gesture-state и deferred persistence; не ловить рекурсивные Bounds updates и потери capture. Вынеси snap logic в чистую геометрию, реализуй тесты eight edges и Restore/WorkerW fallback.

## Выходные артефакты
Изменения в existing native + WPF fallback, тесты для edge anchoring/DPI/rollup, обновлённые ADR и ручная матрица resize.

## Definition of Done
Нет состояния зависшего resize в 30 повторных циклах; изменение IconSize не изменяет panel bounds; resize каждой стороны/угла сохраняет противоположный край; collapsed header не становится размером полноценной ячейки; после рестарта bounds/span стабильны, native fallback работает.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 05.
