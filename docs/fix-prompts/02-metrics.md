# P0 — Чистая геометрия и тесты

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 01; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
На основе утверждённого ADR реализуй чистый тестируемый `WorkspaceGridMetrics`/`WorkspaceLayoutPolicy` без WPF dependencies, где это разумно. API для SnapPosition, SnapBounds с side/corner anchors, Bounds ↔ spans, preferred size, safe clamp, monitor-specific origin, rounding idempotency. Не подмешивай `GridMetrics.CellSize` и `AdaptiveGridLayout.SnapExtent` в workspace width/height. Параметры U, panel gap, preset spans должны быть конфигурируемыми и валидируемыми. Изменение IconSize не вызывает изменение существующих Bounds. Добавь property-based/parameterized tests на отрицательные координаты, DPI 100/125/150/200%, Min/Max, repeated snap, непрерывную drag траекторию и resize from each edge.

## Выходные артефакты
Код геометрии в подходящем `src/` модуле, unit tests `tests/DesktopOrganizer.Tests/Unit/...`, публичный контракт и комментарии с единицами измерения, changelog.

## Definition of Done
`Snap(Snap(x))==Snap(x)` (с допуском), несколько окон одного span имеют одинаковую геометрию на одном workspace; icon size не входит в зависимости window sizing; все тесты geometry зелёные, а старые тесты не удалены ради прохождения.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 03.
