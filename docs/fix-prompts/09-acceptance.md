# P0 — Финальная сверка и релизный gate

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 08; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Сверь результат со скрином до правок и **третьим Ambient Accent референсом** по геометрии (глобальные размеры, равные gutters, alignment), плотности (empty vs fit), icon grid (fixed gaps), визуальной иерархии, текстам, render reliability. Фокус на desktop usability, не требуй точного копирования фейковых иконок/обоев. Запусти unit/integration tests, WPF smoke на Windows 10/11, PerMonitorV2 100→150% DPI, negative origin, monitor unplug/replug, WorkerW restart, native fallback, drag/resize cancel, restore snapshots, 200 icons. Замерь startup, UI hangs, allocation baseline. Проверь backward compatibility и отсутствие неожиданных изменений после первого запуска. Составь defect list P0/P1/P2; при P0/P1 возврати конкретный промпт исполнителю, затем повтори gate. Вмешательства в storage/native beyond necessary оформлять отдельным ADR.

## Выходные артефакты
`docs/REVIEWS/workspace-grid-final.md` с сравнительной матрицей, командами/tests, снимками before/after, списком дефектов, итоговым release decision и rollback.

## Definition of Done
Все P0/P1 исправлены и повторно проверены; выполнены тесты из каждой стадии, manual matrix реально выполнена на Windows (либо выпуск помечен blocked); все артефакты ADR/миграции обновлены; архив изменений не маскирует невыполненные проверки.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу release gate.
