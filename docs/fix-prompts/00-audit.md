# P0 — Аудит фактической геометрии и baseline

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа нет; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Проверь реальную ветку, собери карту зависимостей от `GridSize`, `SnapResizeToIconGrid`, `GridSnap`, `GridMetrics`, `AdaptiveGridLayout`, `AdaptiveIconGridPanel`, `NativePanelResizeController`, `PanelResizeController`, `ChildWindow.xaml(.cs)`, layout snapshots, multi-monitor и DPI. Найди code paths, которые пишут `Left/Top/Width/Height`, включая восстановление, drag, resize, roll-up, virtual desktops и изменение IconSize. Выясни по traces/тестовым сценариям, почему несколько ярлыков теряют изображения на скрине: отсутствие source, lazy load, loading failure, fallback, кеш, opacity, загрузка после старта. Не утверждай первопричину без проверки. Сделай baseline скриншоты на 100% и 150% DPI для 1/2/8 items, пустых панелей, list/grid и collapsed; зафиксируй measured bounds, gutters, actual icon image bounds и item text contrast.

## Выходные артефакты
`docs/PLANS/workspace-grid-00-baseline.md` с dependency graph, текущими ADR-конфликтами, скриншотами (или инструкцией, если нет GUI) и точными исходными измерениями; список текущих тестов и результатов.

## Definition of Done
Каждый writer геометрии и icon loading путь включён в карту; измерения отделены от визуальных предположений; ни одна реализация не менялась; есть репродукция как минимум 3 непропорциональных панелей.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 01.
