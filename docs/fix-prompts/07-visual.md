# P1 — Довести Ambient Accent до референса

**Роль:** senior .NET/WPF engineer, Windows shell integration. Следуй `AGENT_CONTRACT.md`. **Вход:** текущий репозиторий, текущий скрин и третий Ambient Accent референс; не переносить размеры с картинки как доказанные константы.

**Зависимость:** результаты этапа 06; не приступай до его DoD. **Вне рамок:** рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание UI на другом фреймворке, полная замена WorkerW/хранилища.

## Задание
Работай с существующими `PanelChrome`, `src/DesktopOrganizer.UI/Resources/` и `ChildWindow.xaml`, а не создавай параллельный визуальный framework. Сопоставь screenshot baseline с третьим Ambient Accent: расширить border radius после проверки clipping/hit test; баланс слабого tint поверхности, тонкого accent edge, слабого accent border, различимых glass/ambient/solid без потери contrast. Header 32–36 DIP как гипотеза, категория/icon слева (если данные есть), title primary, controls muted idle/clear on hover и доступные keyboard tooltips. Дай читаемые primary/secondary labels, 2 строки + ellipsis + tooltip; отсутствие картинки не должно сжимать слот; frame item hover/selected, consistent padding, не превращай каждый значок в тяжёлую карточку. Не вставляй «solid/glass/ambient» в реальное имя категории. Validate translucent contrast на светлых и тёмных wallpaper, high contrast OS, reduced motion. Не меняй layout-grid геометрию здесь.

## Выходные артефакты
Изменённые XAML resources/design tokens/styles, review screenshots 100/150% DPI light/dark wallpapers, usability checklist, список точных token values с обоснованием.

## Definition of Done
В референс-сценариях панели визуально из одного семейства; текст и кнопки читаемы поверх двух контрастных обоев; заголовки и подписи не режутся, collapsed panel понятна, нет overlapping hot zones; внешний вид не нарушает performance/WorkerW.

## Передача на следующий этап
Представь patch/diff, перечень затронутых файлов, test log, Windows manual results, неразрешённые вопросы. Если DoD не подтверждён, остановись и передай доработку; не переходи к этапу 08.
