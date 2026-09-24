# P0 — Решения ADR и контракты режимов

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 00; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Создай новый ADR «Workspace Grid and Panel Geometry» и amend/supersede ADR-011/015/018 там, где текущая семантика конфликтует. Определи 3 независимые системы: physical monitor geometry, workspace panel grid, internal icon layout. Обоснованно выбери DIP-модуль U (например 8 DIP лишь как стартовую гипотезу), origin per monitor/work area, шаги координат, размеры панелей как spans, минимумы, gap, округление при DPI. **Не навязывай единственный размер/запрет свободного resize**: определи `GridAligned` и `LegacyFreeform` / manual override, и как переключаются режимы. Отдельно специфицируй правило «иконки не меняют ширину панели» и поведение недостаточной площади (scroll/clip constraints, не скрывать данные). Определи snap при drag (threshold) и resize (quantization), corner/edge anchoring и min/max, столкновения, доступность всех окон в multimonitor. В ADR-018 явно сформулируй замену stretch-gap на fixed-gap либо optional legacy policy, охарактеризуй UX-компромисс. Предложи план миграции layout snapshots и сохранённых window bounds без скачка при первом запуске. Не меняй runtime-код в этом PR.

## Выходные артефакты
`adr/ADR-028-workspace-grid.md`, `adr/ADR-029-icon-layout-policy.md` (или эквивалентные свободные номера), amendments к связанным ADR, `docs/PLANS/workspace-grid-migration.md`, таблица invariants/API; ADR обновляют реальный индекс.

## Definition of Done
В ADR зафиксированы независимые системы координат, DIP/px и origin, оба режима, backwards compatibility, сохранение snapshots и alternatives; ADR-018 конфликт устранён явно, а не молча; решения согласованы со существующими ADR-021/022/026.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 02.
