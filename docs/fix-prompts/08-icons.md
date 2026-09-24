# P1 — Диагностика отсутствующих значков и icon pipeline

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 07; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Исследуй отдельно показанный дефект: в dev/tools/soft2/work множество текстовых подписей без иконок, но некоторые значки видны. Проследи `IconProvider`, `IIconProvider`, shell extraction/COM STA, icon cache, models, load cancellation, binding/converter visibility, data source and fallback. Проведи эксперимент на `.exe`, `.lnk`, folder, broken path, URL, startup and after cache clear. Не замещай диагноз общей рекомендацией «починить IconProvider». При необходимости добавь placeholder в фиксированный слот, retry с bounded backoff, file-based invalidation и диагностический event, сохрани no telemetry. C++ допустим только после профилирования и принятия отдельного ADR; не выноси всё в native ради моды.

## Выходные артефакты
`docs/DIAGNOSTICS/icon-loading-findings.md` с evidence/steps; точечный фикс или документированное объяснение контента; тесты extraction/cache/visual fallback.

## Definition of Done
Для валидных путей `.exe`/`.lnk`/folder возвращается реальная иконка или документированно проверенный OS fallback; отсутствующий target не ломает layout; нет silent exception; single-item panel не теряет icon из-за opacity/virtualization; UI остаётся отзывчивым при 200+ items.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 09.
