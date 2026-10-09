# ADR-031: Settings Window — Sidebar Navigation + Panel Customize Dialog

**Статус:** Accepted (2026-09-26) — Amended 2026-09-27 (ADR-032: D-8 «только английский» — superseded);
Amended 2026-09-29 (ADR-042 amendment, бэклог 1.x пункт 5: карточная раскладка `PanelCustomizeWindow`/
`NewPanelDefaultsSection` упрощена до Preview → «Цвет» → «Дополнительно», боковая навигация и немодальность
самого окна не пересмотрены); Amended 2026-09-30 (ADR-047: карточки Settings → `SettingsCard`, ряд кнопок Panels →
командная строка `DropDownButton`, тема окна следует Windows); Amended 2026-10-09 (D-6: переключатель «Show desktop
icons» возвращён в Settings → General по явной просьбе пользователя, добавлено событие
`IDesktopIconsService.VisibilityChanged` — см. «Amendment (2026-10-09)» в конце документа)

Ветка `feature/settings-window-redesign` (от `rc/0.6.0`, база `3628cc4`). Источник решения — бриф
`docs/prompts/settings-window-redesign.md` (инвентарь текущих настроек, решения D-1..D-10, разбивка на
пакеты работ WP-A/WP-B/WP-C) и мастер-план `docs/PLANS/settings-window-redesign.md`
(`fences-feature-planner`). Конвейер реализации: `wpf-ui-builder` на трёх параллельных пакетах —
WP-A shell (`a256689`), WP-B customize dialog (`383c4e1`), WP-C behaviour/fixes (`79f3040`) →
`tdd-test-engineer` (coverage sweep, `9a0cddd`) → два фикс-цикла по итогам ревью: FIX-B
(`a9c8581`/merge `9d0a2dd`, customize dialog — lock gating, live reactivity, close-on-remove, focus
fallback, folder picker) и FIX-A (`3a2c1b7`/merge `6ce7f2f`, settings shell — panel subtitle,
grid-step clamp, selection loss, empty state, disabled reasons, a11y, dead code) →
`code-quality-reviewer` ∥ `fences-ux-parity-reviewer` (верификация на HEAD `6ce7f2f`) → этот документ.
Итог на HEAD `6ce7f2f`: `dotnet build`/`dotnet test` — 0 errors, 0 warnings, 3573 теста. Раздел
«Реализация» ниже фиксирует фактические расхождения кода с планом и остаточные follow-up'ы.

## Контекст

Окно настроек (`MainWindow`, ADR-002) сейчас — один вертикальный скролл из семи раскрытых `Expander`,
плюс полоска вкладок страниц сверху, имитирующая категории навигации. В нём смешаны четыре разных
области действия без визуального разделения:

1. **Приложение целиком** (Start with Windows, Show desktop icons, native resize experimental flag).
2. **Раскладка/сетка** (`GridSize`, `GridSnapDistance`, `SnapResizeToIconGrid`, `IsLayoutLocked`,
   `NormalModeLayoutGestures`).
3. **Одна конкретная панель** («Selected panel settings» — Title/Accent/TextColor/Opacity/SurfaceStyle/
   IconSize/View/Sort), доступная только если панель выбрана в списке.
4. **Разовые команды сессии** — чекбоксы «Edit layout mode» и «Show desktop icons», дублирующие
   трей/контекстное меню/жесты, но живущие внутри окна настроек как будто это персистентная преференция.

Полоска вкладок страниц вверху окна выглядит как категория навигации первого уровня, но на деле управляет
`IPageManager.CurrentPage` — областью видимости содержимого рабочего стола, а не разделом настроек.

Ряд настроек, уже реализованных в модели (ADR-025, ADR-029), не имеет UI вовсе:

- `SourceFolderPath` (Folder Portal, ADR-009/ADR-010 инфраструктура) — команда подключения watcher'а
  существует в `ChildWindowViewModel`, но не привязана ни к одному контролу;
- `DefaultSurfaceStyle` — есть в `GlobalDefaultsViewModel`, нет комбобокса;
- `ContentFilter`, `IconLayoutPolicy`, `PanelSizingMode`, size presets — доступны только из контекстного
  меню панели (ADR-029), нет пути через настройки;
- `PageId` конкретной панели можно сменить только пересозданием панели на нужной странице.

Помимо архитектурного дрейфа, зафиксирован документированный баг: панель, созданная лассо (right-drag,
`App.xaml.cs`), не получает `PageId = IPageManager.CurrentPage`, в отличие от «Add panel» — попадает не
на текущую, а на страницу по умолчанию (D-7 брифа).

## Рассмотренные варианты

### A. Одна страница с табами/карточками (без выделенного окна customize)

Заменить `Expander`'ы на горизонтальные табы верхнего уровня (General / Layout / Panels / Pages / …)
внутри того же `MainWindow`, оставить per-panel настройки инлайн-секцией, которая раскрывается при
выборе панели в списке.

Отклонено. Не решает главную проблему смешения scope: per-panel секция всё равно живёт в том же окне,
что и глобальные настройки, и обязана быть пустой/задизейбленной, когда панель не выбрана — тот же UX-
дефект, что и сейчас, просто под другой навигацией. Также не даёт естественного места для действия
«открыть настройки конкретной панели из её контекстного меню» — путь, которым реально пользуются в Fences
(`Configure this Fence…`).

### B. Боковая навигация (глобальный scope) + отдельный немодальный диалог «Customize panel…» (выбран)

Разделить по природе scope, а не по случайной механике UI: `MainWindow` — исключительно
приложение-широкие настройки (8 разделов боковой навигации), per-panel настройки — отдельное окно
`PanelCustomizeWindow`, открываемое из двух точек входа (контекстное меню панели — по Fences-паттерну
right-click на конкретном fence; раздел Panels — кнопка «Customize…» для выбранной в списке панели).

Даёт естественную модель «Configure Fences» (глобально) vs «right-click → настроить эту fence»
(локально) — прямой аналог эталона (см. «Внешние источники»). Session-команды (Edit layout mode, Show
desktop icons) выводятся из настроек полностью — они не преференции, а переключатели состояния сессии,
и остаются в трее/контекстном меню/жестах, где у них уже есть надёжный путь.

### C. Panel manager master-detail (список слева, детали выбранной панели справа, без отдельного окна)

Одно окно с двухпанельной раскладкой: список всех панелей во всём приложении слева, детали выбранной —
справа, глобальные настройки — отдельная вкладка того же окна.

Отклонено по трём причинам:

1. Не даёт входа из контекстного меню панели без принудительного переключения окна в «панельный» режим —
   контекстное меню либо открывает не то, что видно на экране (окно уже показывает другую панель), либо
   требует программного переключения выделения, что ломает мысленную модель «одно окно — один список».
2. Master-detail на 6+ настроек панели (Title, Accent, TextColor, Opacity, SurfaceStyle, IconSize, View,
   Sort, ContentFilter, IconLayout, Auto-size, Align-to-grid, Size preset, Folder Portal) в узкой правой
   колонке того же окна, где слева ещё дерево страниц/панелей, создаёт тесную вёрстку — ADR-025/029 уже
   ввели достаточно карточек, чтобы это не помещалось без внутреннего скролла в обеих колонках сразу.
3. Диалог, который можно открыть параллельно с самим `MainWindow` (немодально, retargeting на другую
   панель без закрытия), ближе к тому, как реально работает Fences: конфигуратор и точечная настройка
   fence — разные окна, которые можно держать открытыми одновременно.

## Решение

Реализовать вариант B: боковая навигация в `MainWindow` (8 разделов) + немодальный диалог
`PanelCustomizeWindow`. Ниже — решения D-1..D-10 брифа, зафиксированные как решения этого ADR.

### D-1. Persistence не трогаем

Ноль новых ключей `IAppSettings`/`user.config`, ноль новых DTO/миграций JSON-схемы (ADR-024,
`schemaVersion` остаётся текущим значением). Последний открытый раздел окна (`SettingsSection`) **не**
персистится между запусками приложения — держится в памяти singleton `MainWindowViewModel` и сбрасывается
на дефолт при каждом старте. Это сознательное отступление от «типичного» UX (большинство settings-окон
запоминают последнюю вкладку) в пользу инварианта CLAUDE.md: любое «нужно поле в состоянии» — вне объёма
этой фичи и требует отдельного явного разрешения пользователя на изменение persistence-контура ADR-024.

### D-2. Навигация — самодельная, без сторонних библиотек

`ListBox` разделов слева + `ContentControl` с `DataTemplate` по разделу справа, ключ — enum
`SettingsSection { General, LayoutSnapping, Panels, NewPanelDefaults, Pages, SystemIcons, Backup, About }`.
Никаких сторонних NavigationView-пакетов (WPF-UI, ModernWpf и т.п.) — паттерн эквивалентен тому, что уже
используется в проекте для похожих задач (переключение контента по enum-ключу), не вводит новую
зависимость. Каждый раздел — отдельный `UserControl` в `Views/Settings/`. `MainWindow` остаётся тем же
окном настроек, что и в ADR-002 (не новое окно, не смена роли) — ADR-002 не пересматривается, только
детализируется внутренняя структура его содержимого.

Целевое дерево информационной архитектуры:

```
MainWindow (Settings)
├── General
│   ├── Start with Windows
│   └── Advanced (expander)
│       ├── Native resize (experimental)      [удалено 2026-10-10: ADR-022 Фаза 1, единственный путь ресайза]
│       └── Reset one-time hints (HasShownMoveHint)
├── Layout & snapping
│   ├── Snap panel position to grid (toggle; GridSize==1 при выкл.) + шаг (GridSize>1)
│   ├── Grid snap distance (enabled только при GridSize > 1)
│   ├── Snap resize to icon grid
│   ├── Lock layout (IsLayoutLocked)
│   └── Normal-mode layout gestures (радиогруппа из 3: MoveAndResize/MoveOnly/None)
├── Panels
│   ├── селектор страницы (замена полоски вкладок)
│   └── список панелей (view · N items · Portal) + Add · Customize… · Move to page ▾ · Remove · Align all
├── New panel defaults
│   ├── Accent, Text color, Opacity, Icon size, Panel geometry, Icon layout
│   ├── Default surface style (combo — было без UI)
│   └── «Take from selected panel» (копирует набор из выбранной в Panels панели)
├── Pages
│   └── список, Add, Remove (не «default»), «Make current» (rename/reorder — волна 2, см. «Открытые
│       вопросы»)
├── System icons
│   └── без изменений содержимого (Enable, all virtual desktops + warning, size, visible items)
├── Backup
│   └── Save as… (запрос имени), Restore (подтверждение — уже есть), Delete (подтверждение — добавляется,
│       см. ниже), Export/Import — не показывать (бэклог DO-012)
└── About
    └── версия сборки, папка хранения (%LOCALAPPDATA%\DesktopOrganizer, кнопка Open), ссылка на
        диагностику
```

Диалог `PanelCustomizeWindow` (per-panel scope, вне дерева выше):

```
Customize "{Title}"
├── Title, Accent, Text color, Opacity, Surface style, Icon size, View, Sort   (было: SW inline)
├── Content filter, Icon layout, Auto-size, Align to grid, Size preset         (было: только CM)
└── Folder Portal: путь (Browse… / Clear)                                      (было: нет UI вообще)
```

### D-3. `PanelCustomizeWindow` — немодальный, одно окно на приложение, live-редактирование

Обычное активируемое окно (без `WS_EX_NOACTIVATE` — это диалог настроек, а не desktop-панель), немодальное
(`ShowDialog()` не используется). Повторное открытие для другой панели переиспользует уже открытое окно и
переключает его модель, а не плодит второй экземпляр — соответствует D-3 брифа и Fences-паттерну «одно
окно конфигурации на раз». Заголовок — `Customize "{Title}"`. Изменения применяются **вживую** через тот
же `ChildWindowModel` (INPC), что и сама панель на рабочем столе — инвариант CLAUDE.md «единый источник
правды вида панели — `ChildWindowModel`» не нарушается: диалог не создаёт параллельную copy-модель с
последующим OK/Apply, он редактирует ровно тот объект, который уже отображается на десктопе. Из этого
прямо следует отсутствие кнопок OK/Cancel — только «Закрыть»; отменить правку можно только вручную
(нет отдельного «отменённого» состояния, которое нужно откатывать).

Контракт открытия — `IPanelCustomizeService.Show(ChildWindowModel model)`
(`src/DesktopOrganizer.UI/Services/IPanelCustomizeService.cs`, интерфейс уже в репозитории как заглушка на
момент постановки задачи, реализация — пакет WP-B).

### D-4. Folder Portal в диалоге: Browse… / Clear, реактивно на модель

Логика подключения `FileSystemWatcher` к `SourceFolderPath`, сейчас живущая императивно в
`ChildWindowViewModel.SelectSourceFolder`, переносится в реакцию на изменение `Model.SourceFolderPath`
(INPC-подписка), чтобы диалог был чистым потребителем модели — установил/сбросил путь, а не вызывающей
стороной, знающей про watcher. `SelectSourceFolderCommand` — либо переиспользуется диалогом напрямую, либо
удаляется как мёртвый код, если диалог обходится прямым связыванием с моделью через `OpenFolderDialog`.

### D-5. Массовые операции не переносятся в диалог

«Apply to all…» (broadcast icon layout/sizing mode — контекстное меню, ADR-029 P06) остаётся в
контекстном меню панели, не дублируется и не переезжает в `PanelCustomizeWindow` — это операция над
множеством панелей, а не над одной, диалог по конструкции знает только про одну модель. «Align all»
остаётся в разделе Panels и, как раньше, действует на панели **текущей выбранной в разделе страницы**, с
подтверждением.

### D-6. Трей: возвращается «Show desktop icons»

> **Частично отменено 2026-10-09** — удаление переключателя из окна настроек (первое предложение ниже)
> пересмотрено: он возвращён в Settings → General, см. «Amendment (2026-10-09)» в конце документа. Трей-часть
> D-6 (пункт + `ToggleDesktopIconsRequested`/`SetDesktopIconsVisible`) остаётся в силе.

Чекбокс «Show desktop icons» удаляется из окна настроек (сессионная команда, не преференция — п.
«Контекст» выше), но не теряется: в трей возвращается checkable-пункт «Show desktop icons», рядом с
существующими Edit layout/Lock layout. Контракт — событие `ITrayIconManager.ToggleDesktopIconsRequested`
+ метод `SetDesktopIconsVisible(bool)` для обратной синхронизации галки (если видимость иконок меняется
не через трей, например восстановлением снапшота), подписка — в `App.xaml.cs`.

### D-7. Фикс лассо-панели: `PageId` = текущая страница

Панель, созданная правым drag'ом (лассо), должна получать `PageId = IPageManager.CurrentPage` в
`App.xaml.cs` `NewPanelWithDefaults`, аналогично пути «Add panel». Это исправление существующего
поведения, попутное этой фиче (тот же файл и тот же код-путь, что и D-6/панельный список), а не отдельная
фича — фиксируется здесь, потому что вскрыто при инвентаризации per-panel настроек (строка `PageId` в
таблице брифа).

### D-8. Локализация — только английский

UI-строки нового кода (боковая навигация, диалог, карточки) — только английский, как во всём проекте
(см. `docs/PLANS/ui-polishing-1.md`). Не вводится инфраструктура локализации ради этой фичи.

**Superseded by [ADR-032](ADR-032-ui-localization.md)** (2026-09-27) — см. amendment в конце документа.

### D-9. Стиль карточек — SettingsCard-паттерн поверх существующих токенов

Каждая настройка — карточка «заголовок + пояснение + контрол справа» (SettingsCard/SettingsExpander
паттерн, см. «Внешние источники»), вместо текущих тултипов. Отключённый контрол объясняет причину
неактивности прямо в тексте пояснения карточки (например: «Grid snap distance — enabled when grid
snapping is on»), а не полагается на то, что пользователь наведёт курсор на disabled-элемент. Новый
словарь ресурсов `Resources/Styles/Settings.xaml` (подключается в `App.xaml`), переиспользует существующие
токены `Brush.Settings.*`/типографику — не вводит параллельный набор кистей. Toggle on/off — `CheckBox` с
toggle-визуалом (в WPF нет штатного `ToggleSwitch`); радиогруппа — для 2–5 взаимоисключающих значений
(`NormalModeLayoutGestures`, три значения).

### D-10. Контракты для параллельной разработки (WP-A/WP-B/WP-C)

Единые имена зафиксированы до начала параллельной реализации, чтобы три независимых пакета собрались
после слияния без переговоров:

- `IPanelCustomizeService.Show(ChildWindowModel model)` — UI/Services (уже в репозитории).
- `MainWindowViewModel.CustomizePanelCommand` (параметр — `ChildWindowModel`, по умолчанию
  `SelectedPanel`).
- `MainWindowViewModel.MovePanelToPageCommand` (параметр — `string pageId`) →
  `IWindowManager.MovePanelToPage(ChildWindowModel, string)`.
- `SettingsSection` enum: `General, LayoutSnapping, Panels, NewPanelDefaults, Pages, SystemIcons, Backup,
  About`.
- `ITrayIconManager.ToggleDesktopIconsRequested` (event) + `SetDesktopIconsVisible(bool)`.
- `LayoutSnapshotsViewModel.CaptureSnapshotCommand` запрашивает имя через новый
  `IInputDialogService.PromptText(title, prompt, defaultValue) : string?`.

Разбивка по файлам-владельцам (WP-A shell / WP-B customize dialog / WP-C behaviour & fixes) — деталь
исполнения, не архитектурное решение; зафиксирована в брифе, не дублируется здесь. Ожидаемый единственный
конфликт слияния — обе WP-B и WP-C трогают `DiConfig.cs` (WP-B регистрирует `PanelCustomizeService`, WP-C
добавляет одну строку для `IInputDialogService`) — разрешается оркестратором построчно, не архитектурная
проблема.

## Последствия

### Позитивные

- Устранено смешение четырёх scope в одном окне: приложение / раскладка / панель / сессия теперь имеют
  разные, однозначные места.
- Появляется UI для ранее скрытых возможностей модели: Folder Portal, `DefaultSurfaceStyle`,
  `ContentFilter`/`IconLayoutPolicy`/`PanelSizingMode`/size presets, смена `PageId` панели.
- Немодальный `PanelCustomizeWindow` с live-редактированием через общий `ChildWindowModel` соответствует
  инварианту CLAUDE.md (единый источник правды вида панели) без введения промежуточного copy/apply
  состояния.
- Session-команды (Edit layout mode, Show desktop icons) перестают маскироваться под персистентные
  преференции — семантически корректное место для обоих осталось (трей/CM/жест/`Ctrl+E`), просто без
  дублирующего чекбокса в настройках.
- Попутно чинится задокументированный баг лассо-панели без отдельной фичи/ADR.
- Ноль изменений persistence-контура — весь объём укладывается в UI-слой и in-memory состояние
  `MainWindowViewModel`.

### Негативные / риски

- Новое окно (`PanelCustomizeWindow`) требует manual verify активации/фокуса поверх desktop-панелей с
  `WS_EX_NOACTIVATE` — не проверяется автотестами (нет живого Windows GUI в этой среде).
- Немодальность + переиспользование одного окна для разных панелей — источник потенциальных багов
  «застрявшего» состояния диалога при быстром переключении между панелями (retargeting должен полностью
  переинициализировать биндинги, не оставлять visual residue от предыдущей панели).
- Reactive-подписка на `Model.SourceFolderPath` (D-4) меняет момент, когда стартует watcher, относительно
  текущего императивного вызова — требует внимательной проверки, что подключение/отключение watcher'а не
  дублируется и не теряется при повторном открытии диалога для той же панели.
- Три параллельных пакета (WP-A/B/C) в одном feature-branch — при неаккуратном слиянии возможен разъезд
  контрактов D-10 между пакетами, если один из агентов реализации отклонится от зафиксированных сигнатур.
- «Последний открытый раздел не сохраняется» (D-1) — сознательный компромисс, отличающийся от привычного
  UX большинства settings-окон; пользователь каждый раз стартует с `General`.

### Уточнение ADR-002 (не пересмотр)

`MainWindow` остаётся Settings/Config окном ADR-002 — не появляется новое «главное» окно настроек, не
меняется жизненный цикл (трей → показ, `OnClosing` → `Hide()`). Это ADR только детализирует внутреннюю
структуру содержимого `MainWindow` (боковая навигация вместо плоского списка `Expander`) и выносит
per-panel scope в отдельное окно-компаньон — то, что в ADR-002 было пунктом «Настройки конкретной панели:
выбрать панель из списка → изменить заголовок, цвет, вид» внутри того же окна, теперь открывается как
`PanelCustomizeWindow`.

### Amendment к ADR-026 (Edit Layout Mode)

Таблица «точки переключения режима» ADR-026 («Три независимые точки переключения режима — трей…, меню
заголовка панели, окно настроек + `Ctrl+E`») теряет один из трёх путей: чекбокс «Edit layout mode» в
окне настроек удаляется (см. D-6/раздел «Контекст» выше). Клавиатурный жест `Ctrl+E`, работающий пока
фокус находится в `MainWindow`, остаётся без изменений. Трей и меню заголовка панели остаются
гарантированными путями — соответствующая запись добавлена в конец ADR-026 как amendment этого ADR (не
переписывает тело ADR-026).

## Реализация (2026-09-26)

Фиксирует фактически внедрённое к HEAD `6ce7f2f` (не намерения плана). Полная трассировка настроек
«ни одна не потеряна» — план `docs/PLANS/settings-window-redesign.md` §5.1; DoD пакетов и их фактическое
состояние — там же, раздел «Реализация vs план».

### Расхождения с планом

| Пункт плана | План | Факт | Оценка |
|---|---|---|---|
| `SettingsCard` (D-9, план §2.1) | Отдельный lookless `ContentControl` (`Controls/SettingsCard.cs`) с DP `Header`/`Description`/`DisabledReason`. | Не создан. Карточки — ручные `Border`+`Grid`, продублированные в каждом из 8 `*Section.xaml`; `AutomationProperties.Name` проставлен точечно на каждом интерактивном контроле. | Не блокирует Accepted — визуальный контракт D-9 («заголовок + описание + контрол справа») соблюдён по смыслу. Дублирование разметки — follow-up (см. ниже). |
| `SelectedPanelSettingsViewModel` (план §3.1: «Изменить» → «Удалить») | Класс удаляется целиком, логика `Apply` переезжает в `PanelCustomizeViewModel`. | Класс **сохранён** и переиспользован через композицию как `PanelCustomizeViewModel.Appearance`; получил подписку на модель и `IDisposable` для отписки при retarget/`SetPanel(null)`. `MainWindowViewModel`'s forwarding-свойства (`SelectedPanel*`, `SelectedPanelSettings`, `HasSelectedPanel`) удалены, как и планировалось. | Осознанное отклонение реализатора: меньше дублирования Title/Accent/TextColor/Opacity/SurfaceStyle/IconSize/View/Sort между двумя VM. Единый источник правды (`ChildWindowModel`) не нарушен — оба слоя пишут в ту же модель. |
| Size preset / Fit to content / Align to grid в диалоге (план §3.2 P-B3, шов `IPanelLayoutActionsRegistry`) | `ChildWindow : IPanelLayoutActions`, диалог вызывает геометрию живого окна через реестр. | Шов **не построен**. В `PanelCustomizeWindow` этих трёх действий нет — остаются только в `HeaderContextMenu` панели (context-menu-only), как до фичи. | Follow-up: требует live-window geometry (`ChildWindow.Sizing.cs`/`ContentSizing.cs`); осознанно не строился в этом объёме. Паритет с Fences не теряется (действия доступны, только другим путём входа). |
| Позиционирование диалога (план §3.2 P-B1) | `WindowStartupLocation=CenterScreen` при первом показе. | Центрируется **на мониторе панели** (через `DesktopCoordinateSpace`), не на экране/у курсора. `ShowInTaskbar=True` — как в плане. | Точнее плана на мультимониторе (диалог не «улетает» на другой монитор от панели); не влияет на трассировку §5.1. |
| Фокус диалога поверх `WS_EX_NOACTIVATE`-CM (план §3.5, риск R-1) | `Dispatcher.BeginInvoke(DispatcherPriority.Input)` из CM → `Activate()` → фолбэк `Topmost` true→false. | Реализовано как отложенный `Show` через `UiDispatch` + `Topmost`-флип, плюс восстановление из свёрнутого состояния (`WindowState.Minimized → Normal`) перед активацией. Нативных вызовов по-прежнему нет (инвариант `CLAUDE.md` соблюдён). | Эквивалентно по духу R-1, другой конкретный механизм. MV актуальны без изменений (см. план, MV-3/MV-4/MV-28). |
| Фабрика новой панели для D-7 (план §4.1, `NewPanelModelFactory.Create(settings, pageId)`) | Новый класс `Models/NewPanelModelFactory.cs`. | Вместо него — `PanelDefaults.CreateNew(settings, pageId)` (расширение существующего на момент плана «ничейного» `PanelDefaults`, не новый файл). | Тот же контракт и эффект (лассо и «Add panel» дают одинаковый `PageId`), другое имя/владелец. |
| Слияние `Settings.xaml` (план §2.1/§4.1: строка в `App.LoadResourceDictionaries()`) | Мёржится в коде `App.xaml.cs`, после `Controls.xaml` и до `Panels.xaml`. | Первая сборка (`6ce7f2f`) мёржила словарь в `MainWindow.xaml`'s `Window.Resources` → при первом открытии окна `XamlParseException: Cannot find resource named 'Settings.PageHeader'` (секции-`UserControl` создаются через `DataTemplate` и парсятся до попадания в дерево окна, `StaticResource` видит только `Application.Resources`). Исправлено (hotfix после `d69bfdc`): строка в `App.LoadResourceDictionaries()` как и планировалось, локальный merge удалён, `WpfApplicationHost` синхронизирован. | Регрессионный тест `SettingsSectionsInitializeTests` конструирует все 8 секций только с app-ресурсами; ViewModel-тесты этот класс ошибок не ловят. Урок: Window-локальные словари непригодны для контента, создаваемого шаблонами. |
| CN-1 (запоминать шаг сетки между запусками?) | Вариант **A** по умолчанию — в памяти, заморозка persistence не трогается. | Реализован вариант A. | Закрыто без обращения к пользователю — план заранее пометил A неблокирующим дефолтом. |
| CN-2 (что делает Clear у Folder Portal) | Вариант **A** по умолчанию — панель возвращает `Model.FilePaths`. | Реализован вариант A. | Закрыто без обращения к пользователю, аналогично CN-1. |
| `SelectSourceFolderCommand` (план §3.1/§3.6) | Удаляется как мёртвый код (никогда не был привязан к контролу). | Удалён. `Folder Portal` в `PanelCustomizeWindow` теперь единственный путь назначения `SourceFolderPath`. | Ссылки в `docs/ROADMAP.md:101,244` аннотированы этим документом. |
| `IFolderPickerService`, `IInputDialogService`, `PanelSubtitleConverter`, трей «Show desktop icons», лассо `PageId = CurrentPage` | Как в плане. | Реализовано как в плане. | — |

### Follow-up-кандидаты (не блокируют Accepted)

Заведены в `docs/Refactoring/BACKLOG.md` (DO-026..DO-029):

- **`SettingsCard`-контрол** — вынести повторяющуюся разметку карточек 8 разделов в один
  lookless-контрол, как исходно планировал D-9. Косметический рефакторинг, поведение не меняет.
- **Align to grid / Size presets / Fit to content в `PanelCustomizeWindow`** — требует шва
  `IPanelLayoutActionsRegistry` к живой геометрии окна, не построенного в этой итерации. До реализации
  действия остаются доступны только через `HeaderContextMenu` панели.
- **Rename/reorder страниц (волна 2)** — осознанно вне объёма (ADR-004 Решение 1: `PageId` = отображаемое
  имя страницы, переименование требует каскадной миграции ссылок).
- **Export/Import снапшотов (DO-012)** — раздел Backup сознательно не показывает Export/Import,
  без изменений этой фичей.
- **Color picker для Accent/Text color** — остался hex `TextBox` + swatch (план §3.3); открытый вопрос
  плана §3 «Открытые вопросы» не решался, выделенный контрол не введён.

Контракты D-10 (`SettingsSection`, `CustomizePanelCommand`, `MovePanelToPageCommand`,
`IWindowManager.MovePanelToPage`, `ITrayIconManager.ToggleDesktopIconsRequested`/`SetDesktopIconsVisible`)
реализованы без переименования.

### Дополнение: раунд 2 ревью и финальный fix (2026-09-26, `fc64035` / merge `d69bfdc`)

Повторный проход `code-quality-reviewer` на `6ce7f2f` нашёл 1 MAJOR и 6 MINOR; все закрыты последним fix-раундом:

- `ChildWindowModel.FilePaths` стал INPC-свойством (`SetField`) — иначе счётчик элементов в списке Panels
  устаревал на всю сессию (окно настроек — скрытый синглтон). Контракт JSON не менялся (то же имя/тип поля).
- Портал: снимок `Model.FilePaths` перед первым подключением папки; очистка «битого» портала (папка
  исчезла, watcher не подключался) теперь инициирует сохранение.
- `PanelCustomizeWindow.OnClosing` коммитит hex-поля только при `IsDirty`; `PanelCustomizeService.ShowCore`
  не открывает диалог для панели, уже удалённой между отложенным `Show` и колбэком.
- `RefreshPanelsOnCurrentPage` сохраняет порядок (`Move`/`Insert` по индексу), `RefreshTransientState()`
  обновляет «Reset one-time hints» при показе/активации окна; удалён мёртвый `NullToVisibilityConverter`.

Итог на HEAD `d69bfdc`: 0 errors, 0 warnings, **3589** тестов.

## Открытые вопросы

- **Rename/reorder страниц (волна 2).** Сегодня `PageId` страницы равен её отображаемому имени (см.
  ADR-004 Решение 1) — переименование страницы означает смену идентификатора, на который ссылаются все
  `ChildWindowModel.PageId` этой страницы, и требует каскадного обновления. Раздел Pages в этой фиче
  ограничен Add/Remove/Make current; rename и drag-to-reorder осознанно вынесены в отдельную волну работ
  (не в объём D-2 дерева навигации).
- **Export/Import снапшотов (DO-012).** Раздел Backup сознательно не показывает Export/Import — это
  существующий backlog-пункт вне scope этой фичи, не блокирует Save/Restore/Delete.
- **Color picker для Accent/Text color.** Бриф не фиксирует конкретный контрол color picker — использовать
  ли существующий (если есть в проекте) или потребуется собственный, оставлено на усмотрение `wpf-ui-builder`
  в рамках WP-A/WP-B при условии, что визуально это остаётся карточкой «заголовок + пояснение + контрол
  справа» (D-9).

## Внешние источники

- Microsoft Learn — [Guidelines for app settings](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
  (разделение сессионных команд и персистентных преференций, карточный паттерн, прогрессивное раскрытие
  через «Advanced»/expander).
- Windows Community Toolkit — [SettingsCard](https://learn.microsoft.com/en-us/windows/communitytoolkit/controls/settingscard) /
  [SettingsExpander](https://learn.microsoft.com/en-us/windows/communitytoolkit/controls/settingsexpander)
  — источник паттерна «заголовок + пояснение + контрол справа», использованного в D-9 (не подключается
  как зависимость — паттерн воспроизведён вручную поверх существующих токенов, см. D-9/D-2).
- Stardock Help Center — конфигуратор Fences (`Configure Fences`) с навигацией по разделам Home,
  Personalization, Folder Portals, Hotkeys, Quick-Hide, Roll-Up Fences, Desktop Pages, Layout & Snapping,
  Sorting & Organizing, About — прямой референс для дерева боковой навигации (D-2) и для модели
  «глобальный конфигуратор vs правый клик по конкретной fence» (обоснование выбора варианта B).
- Apple Human Interface Guidelines — macOS Settings window guidelines (компактное окно настроек с боковой
  навигацией по разделам, без сессионных команд внутри) — общий референс формы решения, не заимствуется
  дословно.
- Nielsen Norman Group — [Progressive Disclosure](https://www.nngroup.com/articles/progressive-disclosure/)
  — обоснование `Advanced`-expander в разделе General (D-2) для редко используемых переключателей
  (native resize experimental, reset one-time hints).

## Amendment (2026-09-27): D-8 «только английский» отменено ADR-032

[ADR-032](ADR-032-ui-localization.md) вводит горячепереключаемую ru/en локализацию и явно пересматривает
D-8 выше. Вся поверхность, добавленная этим ADR (боковая навигация `MainWindow`, `PanelCustomizeWindow`,
карточки `Settings.*`), локализована пакетами WP-4a/WP-4b фичи локализации. Остальные решения этого ADR
(D-1..D-7, D-9, D-10) не пересматриваются.

## Amendment (2026-10-09): D-6 — «Show desktop icons» возвращён в Settings → General

**Что изменилось.** Переключатель «Show desktop icons» (`Theme.ToggleSwitch` в `SettingsCard`,
`Views/Settings/GeneralSection.xaml`, привязка `MainWindowViewModel.DesktopIconsVisible`, TwoWay) снова
находится в окне настроек — рядом с пунктом трея и жестом двойного клика по рабочему столу. Остаются
действительными: трей-пункт, `ITrayIconManager.ToggleDesktopIconsRequested`/`SetDesktopIconsVisible(bool)`,
остальные решения D-1..D-5, D-7, D-9, D-10. Раздел «Контекст» (п. 4) и «Позитивные последствия» (пункт про
session-команды) в части «Show desktop icons не живёт в настройках» этим amendment'ом устарели; «Edit layout
mode» по-прежнему вне окна настроек (amendment к ADR-026 не пересматривается).

**Причина.** Явная просьба пользователя: переключатель должен быть находим в Settings. Обоснование D-6
«сессионная команда, не преференция» остаётся верным для **персистентности** (состояние по-прежнему не
сохраняется и не попадает в `IAppSettings`/`application-state.json`; описание карточки в RESX прямо
говорит, что при закрытии приложения значки возвращаются), но не для **размещения**: наличие сессионного
переключателя в настройках — осознанное отступление от рекомендации «команды не в настройках».

**Контракт синхронизации (новый).** Раньше галка трея обновлялась в трёх точках (старт, клик, двойной клик),
а внешние/косвенные изменения (R-13 плана `docs/PLANS/settings-window-redesign.md`) не наблюдались. Теперь
источник правды один — `IDesktopIconsService`, и он оповещает всех:

- `IDesktopIconsService.VisibilityChanged` (`EventHandler?`, `Services.Abstractions/Interfaces/IDesktopIconsService.cs`)
  вызывается из `DesktopIconsService.SetIconsVisible`, `Toggle` (через `SetIconsVisible`) и **успешного**
  `ReapplyDesiredVisibility` (восстановление «скрыто» после рестарта Explorer, ADR-034). Событие приходит в
  потоке вызывающего; допускается ложное срабатывание (подписчики сами перечитывают `AreIconsVisible`),
  пропуск — нет.
- Подписчики: `App.xaml.cs` `InitTrayIcon` → `ITrayIconManager.SetDesktopIconsVisible(AreIconsVisible)`
  (галка трея), с защитой `Dispatcher.CheckAccess()` — `CrashCoordinator` может восстанавливать значки из
  фонового потока падающего процесса, там WinForms-пункт трогать нельзя, галка остаётся как есть; и
  `MainWindowViewModel` → `OnPropertyChanged(nameof(DesktopIconsVisible))` (переключатель в настройках).
- Не наблюдается: изменение видимости **вне приложения** (Проводник → Вид → «Отображать значки рабочего
  стола»). Это остаётся известным ограничением; закрыт только R-13 для изменений, инициированных самим
  приложением.

**Persistence не затронута.** Ноль новых ключей `IAppSettings`/`user.config`, ноль изменений схемы и DTO
(ADR-024, `schemaVersion` без изменений). Состояние — только `IDesktopIconsService.DesiredVisible` (in-memory,
намерение ADR-034) и живая видимость `SHELLDLL_DefView`.

**Отклонённая альтернатива** — отдельный ADR для события `VisibilityChanged`: это синхронизационная деталь
уже принятых решений (D-6 и ADR-034), а не самостоятельное архитектурное решение; зафиксировано здесь и в
amendment к [ADR-034](ADR-034-explorer-restart-recovery.md).

## Amendment (2026-09-29): карточная раскладка Customize упрощена (ADR-042)

[ADR-042](ADR-042-color-presets-rgb-picker-stepped-size.md) (amendment бэклога 1.x, пункт 5): раскладка карточек
`PanelCustomizeWindow`/`NewPanelDefaultsSection` упрощена до Preview → «Цвет» → «Дополнительно» (свёрнутый
expander через слот `AdvancedExtraContent`). Боковая навигация из 8 разделов и немодальность самого окна
(D-1…D-3) не пересмотрены; изменился только внутренний состав карточек редактора цвета.

## Amendment (2026-09-30): единая дизайн-система (ADR-047)

[ADR-047](ADR-047-unified-design-system.md): карточки Settings переведены на контрол `SettingsCard` (закрывает
`DO-026` для большинства карточек; `LayoutSnappingSection` — follow-up); тема окна настроек следует теме Windows
(`AppsUseLightTheme`); ряд из 5 кнопок раздела «Панели» заменён командной строкой (D-8; позднее, 2026-10-09,
«Ещё» заменён контекстным меню строки); Customize — заголовки групп через `Settings.GroupHeader`, `Auto-size` —
`Theme.ToggleSwitch`.

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-27 | [ADR-032](ADR-032-ui-localization.md) | Вся поверхность локализована (ru/en) | D-8 «только английский» |
| 2026-09-29 | [ADR-042](ADR-042-color-presets-rgb-picker-stepped-size.md) | Карточки Customize/New-panel-defaults: Preview → «Цвет» → «Дополнительно» | Прежний состав карточек редактора цвета |
| 2026-09-30 | [ADR-047](ADR-047-unified-design-system.md) | `SettingsCard`, командная строка Panels (`DropDownButton`), тема окна = тема Windows | Ряд из 5 кнопок раздела «Панели»; прежняя тема окна настроек, не следовавшая теме Windows |
| 2026-10-09 | (без отдельного ADR) | D-6: «Show desktop icons» возвращён в Settings → General; событие `VisibilityChanged` | Утверждение D-6 «Show desktop icons не живёт в настройках» (Контекст п. 4, «Позитивные последствия») |
