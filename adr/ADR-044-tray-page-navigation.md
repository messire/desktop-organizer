# ADR-044: Навигация по страницам из трея

**Статус:** Accepted (2026-09-29) — Amended 2026-09-30 (ADR-047: `TrayMenuRenderer`/`SetMenuPalette` красят меню трея по токенам темы Windows; MAJOR-1 — `ContextMenuStrip` по-прежнему не пересоздаётся — сохранён и перепроверен тестом)

---

Источник — бэклог 1.x, пункты «Двойной клик по иконке трея — round-robin переключение страниц» и
«Отдельный пункт „Страницы“ в контекстном меню иконки трея» (`docs/ROADMAP.md`), мастер-план
[docs/PLANS/backlog-1x-master-plan.md](../docs/PLANS/backlog-1x-master-plan.md) §1–§2. Опирается на
контракты ADR-043 (`PageDescriptors`, `PagesChanged`, `PageDisplayNames.Resolve`), не пересматривает их.
Ветка `rc/0.8.0`. Реализация: `dotnet-services-engineer` → `tdd-test-engineer` → `code-quality-reviewer` ∥
`fences-ux-parity-reviewer` → этот документ. Коммиты `f039db9` (round-robin, wave 1),
`4e7268b`/`904ef4d` (подменю «Страницы», review-фиксы wave 2/3).

## Контекст

До этой задачи двойной клик по иконке трея показывал/скрывал окно настроек
(`ITrayIconManager.OnToggleMainWindowVisibilityRequested`, ADR-002), а меню трея содержало только
`Settings`/`Edit layout`/`Lock layout`/`Show desktop icons`/`Exit`. Переключение между страницами
панелей было возможно только через `Ctrl+PgUp/PgDn` в окне настроек или swipe-жест — не было ни одного
пути «из трея», хотя трей — основная точка входа в приложение, когда окно настроек скрыто (обычный
рабочий режим).

Слой `Services` (где живёт `TrayIconManager`, WinForms `NotifyIcon`/`ContextMenuStrip`) не видит
`IPageManager` — тот определён и реализован в `DesktopOrganizer.UI`, выше `Services` в слоистости
проекта (инвариант CLAUDE.md «Services не зависит от UI»).

## Решение

### 1. Двойной клик — round-robin, событие наружу, решение в UI-слое

`TrayIconManager.Initialize`'s `DoubleClick` поднимает новое `ITrayIconManager.OnNextPageRequested`
(`EventHandler`) **вместо** `ToggleMainWindowVisibility()` — тот же приём «Services поднимает
намерение, UI решает», что уже используют `OnEditLayoutToggleRequested`/`ToggleDesktopIconsRequested`.
`TrayIconManager` по-прежнему ничего не знает о страницах.

Решение о следующей странице принимает новый DI-singleton `TrayPagesAdapter` (`UI/Lifecycle`,
`IDisposable`), запускаемый из `App.InitTrayIcon` сразу после `ITrayIconManager.Initialize()`: подписан
на `OnNextPageRequested`, берёт `IPageManager.Pages`/`CurrentPage`, вычисляет следующую страницу чистой
функцией **`PageCycling.Next(IReadOnlyList<string> pages, string current) → string?`** (`UI/Services`,
internal static) и вызывает `IPageManager.SwitchTo`. Правила `PageCycling.Next`: следующая по порядку,
после последней — первая; `pages.Count <= 1` → `null` (no-op, как в ROADMAP); `current` отсутствует в
списке → первая страница.

`IPageManager` **не расширяется** методом навигации (`SwitchToNext()` и т.п.) — вся логика цикла живёт
вне менеджера, в чистой, независимо тестируемой функции; это оставляет дверь открытой для отдельного
рефакторинга, если такой метод понадобится другому вызывающему коду.

**Переключение отложено через `Dispatcher.BeginInvoke(DispatcherPriority.Normal)`**, а не вызывается
синхронно внутри обработчика `DoubleClick`: колбэк `NotifyIcon` приходит на тот же UI-поток (WinForms
message loop его разделяет), но откладывание даёт диспетчеризации самого WinForms-события полностью
развернуться, прежде чем `SwitchTo` начнёт показывать/скрывать панели — тот же приём, что уже применён
в `App.ShowSettingsWindow`.

Мёртвый код удалён: `ITrayIconManager.OnToggleMainWindowVisibilityRequested`,
`TrayIconManager.ToggleMainWindowVisibility`, `App.ToggleSettingsWindow`, `IWindowManager.StartMainWindow`
с реализацией в `WindowManager` — других вызывающих не было (проверено Grep на момент реализации).

**Amendment [ADR-002](ADR-002-main-window-as-settings.md):** двойной клик по трею больше **не**
показывает и не скрывает окно настроек. Открытие окна настроек остаётся доступным исключительно через
пункт меню трея «Settings» — тот путь ADR-002 не менялся.

### 2. Подменю «Страницы» — первый пункт меню, с разделителем

Новый примитив без UI-типов в Services.Abstractions:
`public sealed record TrayPageEntry(string Id, string DisplayName, bool IsCurrent);`

`ITrayIconManager` получает:
- `void SetPages(IReadOnlyList<TrayPageEntry> pages)` — вызывающий передаёт готовый снимок, как
  `UpdateContextMenu`/`SetDesktopIconsVisible` уже делают для других разделов меню;
- `event EventHandler<string>? OnPageSelected` — клик по странице, аргумент — id.

`TrayIconManager.EnsureContextMenuBuilt` один раз создаёт `_pagesItem` («Pages») как **первый** пункт
меню, с разделителем после него; внутри — `_nextPageItem` («Next page», поднимает то же
`OnNextPageRequested`, что и двойной клик — единый путь), разделитель, затем N пунктов страниц.
**Инвариант «верхний `ContextMenuStrip` никогда не пересобирается» (существующий MAJOR-1 из
предшествующих фаз) сохранён** — `SetPages` меняет только `_pagesItem.DropDownItems`: если
последовательность (Id, DisplayName) не изменилась, у существующих пунктов меняется только `Checked`
(без пересоздания экземпляров); иначе старые пункты страниц явно `Dispose()`-ятся и пересоздаются
(`_nextPageItem` и разделитель остаются на месте). `_nextPageItem.Enabled = pages.Count > 1`.

`TrayPagesAdapter` подписывается также на `IPageManager.PageChanged`/`PagesChanged` и
`ILocalizationService.CultureChanged` (у unnamed `default` меняется локализованное имя при смене языка)
и на каждое из них перестраивает снимок через `PushPages()`: `PageDescriptors` → `PageDisplayNames.Resolve`
(ADR-043) → `TrayPageEntry`. Снимок передаётся в `SetPages` через **`Dispatcher.BeginInvoke(DispatcherPriority.Background)`** —
приоритет намеренно ниже, чем у самого переключения страницы (`Normal`): цепочка клика по пункту меню —
`Click → OnPageSelected → (Normal) SwitchTo → PageChanged → PushPages` — не должна пересобирать то самое
подменю, чей пункт всё ещё технически диспетчеризует свой `Click`, даже спустя один тик диспетчера; это
тот же класс гонки, от которого защищает существующий MAJOR-1.

`OnPageSelected(id)` → `SwitchTo(id)`, только если `id` всё ещё присутствует в `Pages` — устаревший пункт
(страница удалена между открытием меню и кликом) молча игнорируется, а не передаётся менеджеру с
неизвестным id.

Переименование страницы **из трея не реализовано** (WinForms `ContextMenuStrip` не даёт inline-ввода) —
за этим пользователь отправляется в раздел «Страницы» окна настроек (ADR-043).

### 3. Осознанное расхождение с Fences

В Stardock Fences нет списка страниц в трее вообще, а двойной клик по трею открывает настройки. Это
приложение выбирает другое поведение по двум причинам: (1) у Fences есть on-desktop индикатор текущей
страницы/переключатель прямо на рабочем столе, которого в этом приложении нет — трей-подменю частично
компенсирует его отсутствие; (2) окно настроек уже доступно отдельным пунктом меню, так что двойной клик
не теряет функциональность — он получает новую. Зафиксировано как осознанное отклонение, не как
неполный паритет.

## Последствия

### Позитивные
- Быстрое переключение страниц без открытия окна настроек — из трея, который всегда доступен.
- Единая точка входа для round-robin (двойной клик и пункт «Next page» подменю поднимают одно и то же
  событие) — нет двух независимых реализаций одной и той же логики цикла.
- Ноль изменений в `Services` относительно `IPageManager` — слоистость проекта не нарушена.

### Известные ограничения
- Нет визуального отклика на рабочем столе при переключении страницы двойным кликом по трею (в отличие,
  например, от анимации/уведомления) — по умолчанию его нет; открытый вопрос для будущего бэклога, не
  блокер этой задачи.
- Длинные имена страниц (до 40 символов, ADR-043) могут растягивать меню трея — принято как допустимое
  (аналогично поведению Проводника), без принудительного усечения данных.

## Manual verify (Win10/Win11) — не блокер

- Двойной клик по трею с 3 страницами: 1→2→3→1 по кругу; с одной страницей — ничего не происходит;
  пункт «Settings» по-прежнему открывает окно.
- «Pages ▸»: имена (включая переименованные и unnamed `default` на обоих языках), галочка на текущей,
  «Next page» неактивен при одной странице.
- Добавление/удаление/переименование страницы при закрытом меню → при следующем открытии список
  актуален; переключение страницы кликом в подменю несколько раз подряд не падает и не оставляет
  «залипших» пунктов.
- Смена языка UI → «Pages»/«Next page» и имя unnamed `default` перерисованы без перезапуска.

---

**Источник:** [docs/PLANS/backlog-1x-master-plan.md](../docs/PLANS/backlog-1x-master-plan.md) §1–2 —
план считается выполненным и остаётся историческим артефактом.

**Расширяет:** [ADR-043](ADR-043-page-display-names.md) (`PageDescriptors`/`PagesChanged`/
`PageDisplayNames.Resolve`). **Amends:** [ADR-002](ADR-002-main-window-as-settings.md) (двойной клик
трея).

---

## Amendments

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-30 | [ADR-047](ADR-047-unified-design-system.md) (D-10) | Трей получил `TrayMenuRenderer`/`SetMenuPalette`: меню (`ContextMenuStrip`) красится по токенам темы Windows (`SystemUsesLightTheme`); верхний `ContextMenuStrip` по-прежнему не пересоздаётся при смене палитры (MAJOR-1 сохранён, перепроверен тестом) | — (инвариант «верхний `ContextMenuStrip` никогда не пересобирается» из раздела 2 в силе) |
