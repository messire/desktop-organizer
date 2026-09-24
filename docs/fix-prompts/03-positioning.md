# P0 — Единый drag/snap/collision и координаты мониторов

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 02; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Подключи workspace-grid к реальным путям panel move (`PanelDragMoveBehavior`, `GridSnap`, `PanelCollision`, `ChildWindow`), restoring snapshots, monitor changes, display/work-area changes и виртуальным рабочим столам. Поиск фактических путей обязательный: имена проверять по коду. Snap должен быть привязан к origin конкретного монитора, не к `(0,0)` глобального desktop; gap между панелями должен быть одинаковым при adjacency и не приводить к перекрытию. Определить явный приоритет grid alignment vs magnetic snap vs anti-overlap и поведение модификатора свободного движения. Не ломай desktop host/WorkerW, invisible border и NOACTIVATE. Миграция legacy bounds только по явной операции «Align to workspace grid» или безопасному opt-in режиму.

## Выходные артефакты
Adapter для рабочей сетки, обновлённые movement/snap handlers, integration tests движений/мониторов и реальный чек-лист multi-monitor.

## Definition of Done
Независимо созданные панели с одинаковым span совпадают по краям; соседние панели имеют равные gaps; restore не переносит панель на другой монитор при доступном исходном; нет jump на first launch; позиция стабильна после drag-release и рестарта; клавиатурный/доступный fallback не деградирует.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 04.
