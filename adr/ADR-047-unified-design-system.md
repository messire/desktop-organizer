# ADR-047: Unified Design System (Palette T)

**Статус:** Accepted (2026-09-30) — Amended 2026-10-09 (D-8: «Ещё» `DropDownButton` в Panels заменён контекстным
меню строки; фиксы шаблонов `Theme.ComboBox`/`Theme.ListViewItem`/`Theme.ListBoxItem` — см. «Amendment
(2026-10-09)» в конце раздела «Amendments»)

---

**Источник:** бэклог 1.x, пункт 6 «Унифицировать интерфейс: меню, настройки, диалоги в одном стиле»
(`docs/ROADMAP.md`), рамки и решения мастер-плана
[docs/PLANS/backlog-1x-master-plan.md §6](../docs/PLANS/backlog-1x-master-plan.md), дочерний план
[docs/PLANS/design-system-1x.md](../docs/PLANS/design-system-1x.md) (`fences-feature-planner`, 2026-09-29,
инвентаризация §1, целевая система §2, фазы 6.0–6.5 §3). Ветка `feature/backlog-1x`. Фазы прошли конвейер
исполнитель → `tdd-test-engineer` → (`code-quality-reviewer` ∥ `fences-ux-parity-reviewer`) волнами;
коммиты волн — `6582af2` (6.0–6.2a), `eb4af36` (6.2b/6.2c), `6f563d6` (6.2b/6.2c/6.4: окно настроек,
меню панели, тематизированное меню трея), `f98da21` (6.3/6.5: `AppDialogWindow`, `InfoBar`/
`SettingsStatusNotifier`, включение тёмной темы, фиксы четвёртой волны ревью) + короткий заключительный
фикс `wpf-ui-builder` в этой же волне (перекомпоновка заголовка `AppDialogWindow` на `Grid` вместо
`StackPanel` для длинных заголовков, `HelpText`/`LabeledBy` для доступности диалога, порядок владельца
диалога «активное chrome-окно → видимое `MainWindow` → без владельца», severity `Warning` для
безвозвратных операций вида «Apply to all panels», цветной глиф severity и подсвеченный заголовок в
`InfoBar` + `AutomationProperties.LiveSetting=Assertive` для `Critical`, кисть выделения `TextBox` на
токенах вместо системной). `dotnet build`/`dotnet test` — 0 errors, 0 warnings на каждой волне.

## Контекст

Инвентаризация фазы 6.0 (`ChromeContrastAuditTests`, `docs/PLANS/design-system-1x.md` §1.1) зафиксировала,
что к 2026-09-28 в приложении сосуществовало три независимых визуальных языка: окно настроек в стиле
Windows 11 Personalization (ADR-031/042, всегда светлое), тёмное меню заголовка панели поверх Ambient
Accent (ADR-025), и WinForms `ContextMenuStrip` трея. Помимо стилистического разнобоя измерение нашло
конкретные нарушения читаемости:

- подменю меню заголовка панели («Переместить на страницу ▸» и т. п.) рисовались светлым Aero2-попапом
  со светлым текстом `#DDDDDD` — контраст **≈ 1.2:1** (гипотеза H-1, подтверждена STA-тестом фазы 6.0);
- выбранный пункт сайдбара окна настроек отличался только заливкой `#FFFFFF` на `#F0F0F0` — **1.14:1**,
  ниже порога WCAG 1.4.11 (≥ 3:1) для индикатора состояния;
- трек выключенного toggle — `#9A9A9A` на белом, **2.81:1**;
- предупреждающий текст (`Brush.Settings.Warning`, страницы/системные иконки) — `#B8860B` на белом,
  **3.25:1**, ниже 4.5:1 для обычного текста;
- 9 из 26 поверхностей (§1.1 плана, столбец «Типографика») использовали 9–11 px там, где
  `SystemFonts.MessageFontSize` («M») равен 12 px при 100 % масштабе.

Задача — не «перекрасить», а свести всё, что относится к хрому приложения (не к самим панелям), к одной
системе токенов с измеримой читаемостью, оставив нативные поверхности (shell-меню, системные диалоги)
явно задокументированными исключениями, а не молча забытыми.

## Решения

### D-1. Две палитры: Palette P (панели, не трогается) и Palette T (хром, новая)

Главное архитектурное разделение. **Palette P** — существующая Ambient Accent (ADR-025/030/042):
`Colors.xaml`/`Brushes.xaml`, `PanelTheme.*`, `SurfaceComposer`, `PanelChromeForegroundPolicy`. Красит
`ChildWindow` (хром, заголовок, значки, скроллбар), `SystemIconsPanelWindow`, `PanelPreviewCard` — данные
пользователя (per-panel цвет), frozen `StaticResource`-цепочка не меняется, HC-оверлей «только при
старте» (D-12 ADR-025) остаётся как было. **Palette T** — новая, theme-aware: `Theme.*`, красит окно
настроек (`MainWindow` + 8 секций), `PanelCustomizeWindow`, `PanelColorEditor`/`SteppedValueEditor`/новый
`SurfaceOptionsEditor` (как контролы хрома, не как данные панели), все WPF-меню (меню заголовка панели,
меню «на страницу» в Settings), тултипы, хинт-попапы панели, `AppDialogWindow`, `InfoBar`, трей (через
renderer). Старые `Color.Settings.*`/`Brush.Settings.*` (и их HC-строки) удалены из `Colors.xaml`/
`Brushes.xaml`/`Colors.HighContrast.xaml` после того, как последний потребитель перешёл на `Theme.*`.

### D-2. Источник темы — Windows, без новой настройки (CN-1 = A)

Тема окна настроек/диалогов/WPF-меню следует **`AppsUseLightTheme`** («режим приложений» Windows); трей —
**`SystemUsesLightTheme`** («режим Windows», как рисуются собственные меню Explorer/панели задач), с
фолбэком на `AppsUseLightTheme` → Light при отсутствии значения. High Contrast — отдельная ветка,
построенная из `SystemColors`, живо переключается для Palette T; Palette P по-прежнему подхватывает HC
только при старте (нерассмотренное расхождение, зафиксировано ниже как известное ограничение, R-8 плана).
Явного переключателя «Светлая/Тёмная/Системная» в настройках **нет** — CN-1 решён вариантом A ещё на
этапе планирования (persistence заморожен, ни одна фаза не добавляет поле). Заголовок каждого окна семьи
хрома получает `DWMWA_USE_IMMERSIVE_DARK_MODE` (атрибут `20` на Win10 20H1+/Win11, `19` на Win10
1809–1909) через новый `IWindowFrameTheming`/`INativeApi.SetDwmWindowAttribute`, вызываемый при
`SourceInitialized` и при каждом `ThemeChanged` (общий attached-behavior `ThemedWindowBehavior`).

### D-3. C#-источник правды, `DynamicResource`

`UI/Theming/ThemePalette.cs` (`record`, `Light`/`Dark`, по прецеденту `AccentPalette`) — единственный
источник значений; `ThemeResourceBuilder` строит из него (или из `SystemColors` для HC)
`ResourceDictionary` с уже `Freeze()`-нутыми кистями и `double`/`Thickness`/`CornerRadius` для геометрии и
типографики. `ThemeKeys` — const-строки имён ресурсов. `AppThemeService` (singleton, `Current`,
`event ThemeChanged`) подписан на `ISystemThemeProvider.Changed`, `SystemParameters.StaticPropertyChanged`
(High Contrast) и пересчитывает `TypeRamp` при смене системного шрифта; маршалит события на UI-поток.
Смена темы — замена целиком одного merged-словаря в `Application.Resources`, поэтому все
`{DynamicResource Theme.*}`-потребители перерисовываются без перезапуска; `StaticResource Theme.*`
запрещён guard-тестом (`ThemeKeysCoverageTests`, T-2). Причины C#, а не статических XAML-словарей: трей
живёт в Services и не может парсить WPF XAML, тест контраста (T-1) — чистый C# без STA, а HC-вариант
строится тем же билдером — три независимых потребителя одной таблицы значений.

`ISystemThemeProvider`/`SystemThemeProvider` (Services/Tools) читает `HKCU\...\Themes\Personalize`
(`AppsUseLightTheme`/`SystemUsesLightTheme`) через `INativeApi`, плюс `SystemParametersInfo`
(`IsHighContrastOn`) и снимок системных цветов/шрифта (`SystemColorsFingerprint`, `MessageFontSize`) —
последние два нужны, чтобы отличить переключение между двумя HC-темами (`WM_SYSCOLORCHANGE` без смены
флага) и смену «Размера текста» от «пустого» `WM_SETTINGCHANGE`. Слушает через собственное скрытое
top-level окно (`WS_POPUP`, не message-only — не получает broadcast) с дебаунсом 100 мс на `WM_TIMER`
(Windows шлёт серию сообщений на одно переключение); событие `Changed` поднимается только когда снимок
реально изменился. Оконная процедура обёрнута в try/catch и всегда проваливается в `DefWindowProc`.

### D-4. Типографика — `TypeRamp` от `SystemFonts.MessageFontSize`

База **M** = `SystemFonts.MessageFontSize` (12 DIP при 100 % / «Размер текста» = 100 %, растёт вместе с
Windows-настройкой «Специальные возможности → Размер текста»). Токены `Font.Size.Caption` (M),
`Font.Size.Body` (M+2), `Font.Size.BodyStrong` (M+2, SemiBold), `Font.Size.Subtitle` (M+6, SemiBold),
`Font.Size.Title` (M+8, SemiBold) — `sys:Double` в словаре темы, пересчитываются вживую при смене
системного шрифта. Правило: ни один стиль Palette T не задаёт `FontSize` меньше M (закрывает 9–11 px из
инвентаризации); исключения — глифы (не текст), `PanelPreviewCard` (масштабная копия панели,
`AccessibilityView=Raw`) и геометрия Palette P (`GridMetrics.LabelHeight` у `SystemIconsPanelWindow`) —
зафиксированы allowlist-ом храповика T-4/T-3 с причиной на каждую запись.

### D-5. Отступы и геометрия

Шкала хрома — **4/8/12/16/24** DIP (`Theme.Card.Padding`, `Theme.Card.Gap`, `Theme.Group.Gap`,
`Theme.Page.Padding`, `Theme.Control.MinHeight=32`, `Theme.MenuItem.MinHeight=30`). Новые ключи
`Theme.Radius.Control/Popup/Card` (4/8/6) — отдельно от `Radius.Control/Popup/Panel` Palette P (3/4/8),
чтобы изменение радиуса хрома не сдвинуло геометрию панелей. Существующие `Space.6`/`Space.10` остаются
только для Palette P.

### D-6. Правила состояний и измеримая читаемость

hover — только заливка (текст не меняется); pressed — заливка `*.Pressed` + `Text.Secondary` для
secondary/subtle кнопок; disabled — токены `Text.Disabled`/`Control.Fill.Disabled`/`Accent.Fill.Disabled`,
**не** `Opacity` на весь контрол (было 0.45/0.5 — непредсказуемый контраст; исключение — кнопки заголовка
панели, Palette P, вне scope); focus — единый `Theme.FocusVisual` (двойное кольцо `Stroke.Focus`/
`Stroke.FocusInner`, только клавиатурный фокус); selected — индикатор `Selection.Indicator` (≥ 3:1) +
`Selection.Fill`, цвет никогда не единственный носитель состояния. Метрики читаемости, закреплённые
автотестами: основной/вторичный текст ≥ 4.5:1 на каждой поверхности, где может оказаться (включая
hover/selected) — `ThemePaletteContrastTests` (T-1); крупный текст/глифы/границы/фокус/выбор ≥ 3:1;
disabled ≥ 3:1 (сверх обязательного WCAG-минимума, ради различимости); «стены текста» — описания карточек
≤ 140 символов en / ≤ 170 ru, тексты диалогов ≤ 200/240, тултипы ≤ 160/190 — `ResxTextLengthGuardTests`
(T-5, храповик «только уменьшается», allowlist с причиной на каждое текущее нарушение).

**Найденная в реализации точечная правка палитры (T-1 fix, зафиксирована в `ThemePalette.cs`):** буквальное
значение `#3183CA` из планового черновика §2.2 для `Theme.Accent.Pressed` (Light) давало контраст
`Text.OnAccent` (белый) только 4.02:1 — ниже требуемых 4.5:1 для той же строки таблицы («OnAccent ≥ 4.5 на
Fill/Hover/Pressed»). Затемнено до **`#2C75B5`** — по-прежнему самое светлое из трёх Accent-состояний
визуально, но с контрастом ≥ 4.5:1; план явно допускал такую правку исполнителем без эскалации.

### D-7. Один стиль для всех WPF-меню, метрики Win11

Меню заголовка панели, меню «Переместить/Копировать на страницу» в Settings и все будущие WPF-меню
получили общий явный стиль `Theme.ContextMenu`/`Theme.MenuItem`/`Theme.Separator` (полный шаблон
`MenuItem` для всех 4 ролей — `TopLevelHeader`/`Item`/`SubmenuHeader`/`Item` — закрывает H-1), следующий
теме Windows, а не палитре конкретной панели: рядом на столе открывается нативное shell-меню значка,
которое рисует система, и WPF-меню должно выглядеть его соседом. Метрики — радиус попапа 8 DIP, пункт
30 DIP, отступ 12,6, подсветка — скруглённый прямоугольник с инсетом 4, Caption (соответствует 9 pt
нативного меню), разделитель 1 px с отступами 4. Радио-группы меню заголовка панели («Показывать» — 4
пункта фильтра, «Упорядочить» — Вручную/По имени) переведены из плоского списка disabled-заголовков в
подменю (тот же приём, что уже использовали «Макет значков»/«Размер панели») — 20 пунктов сжаты до 16, без
псевдо-заголовков вида `IsEnabled=False`; `fences-ux-parity-reviewer` подтвердил паритет с группировкой
контекстного меню ограждения в самом Fences. «Переименовать» в меню сохранил акселератор `F2` (ADR-013).

### D-8. Кнопки — командная строка вместо ряда из 5, `DropDownButton`

> **Пересмотрено 2026-10-09:** вариант A в разделе «Панели» (`DropDownButton «Ещё»`) заменён контекстным
> меню строки списка — см. «Amendment (2026-10-09)» ниже. Правила D-8 в целом и `DropDownButton` в
> «Страницах»/«Backup» не затронуты.

Правила: не более одной primary-кнопки на вид/группу, не более 3 видимых кнопок в командной строке,
деструктивное действие — не рядом с primary, а последним пунктом меню с подтверждением; кнопка, открывающая
меню — всегда новый контрол `DropDownButton` (`Controls/DropDownButton.cs`, `ToggleButton` + `Menu :
ContextMenu`, `IsChecked` ↔ `Menu.IsOpen`, Space/Enter/Alt+↓ открывают и переводят фокус на первый пункт,
Esc закрывает и возвращает фокус, UIA Toggle-паттерн) с шевроном-глифом, не текстовым «▾». Раздел «Панели»
окна настроек (было: `[Добавить] [Настроить] [Переместить ▾] [Копировать ▾] [Удалить]` — переносился на 2
строки в RU) сведён к варианту A: `[Добавить панель]` (primary) · `[Настроить…]` · `DropDownButton «Ещё»`
с подменю «Переместить на страницу ▸» / «Копировать на страницу ▸» / — / «Удалить панель…» — 3 кнопки,
одна строка. Тот же язык — в «Страницах» (`[Сделать текущей]` subtle + «⋯» → Переименовать (F2)/Удалить…)
и в «Backup» (`Восстановить` subtle + «⋯» → Удалить…). About-ссылки переведены на `Theme.Button.Link`.
`SplitButton` в этой итерации **не вводится** (рассмотрен для «Переместить ▾» и отклонён — у действия нет
осмысленного действия по умолчанию).

### D-9. Диалоги и уведомления — единый `AppDialogWindow` + `InfoBar`/`IStatusNotifier`

Матрица «вызов → механизм» (`docs/PLANS/design-system-1x.md` §2.8):

| Было | Стало |
|---|---|
| `IConfirmationService.Confirm` (Remove panel/page, Align all, Apply to all panels ×2) → `MessageBox` Yes/No | `AppDialogWindow` (модальный WPF, тема T); сигнатура `IConfirmationService` не изменилась |
| `Notify(Warning/Error)` (copy/recycle failed) | `AppDialogWindow`, одна кнопка «OK» — остаётся модальным (fail-visibly, ADR-035) |
| `Notify(Information)` («Панель перемещена/скопирована на страницу…») | `InfoBar` в окне настроек через новый `IStatusNotifier`/`SettingsStatusNotifier` — неблокирующий, автоскрытие Informational/Success, `Caution`/`Critical` — до закрытия пользователем |
| `IInputDialogService.PromptText` («Сохранить снапшот как…») | `AppDialogWindow` в режиме ввода (TextBox + inline-ошибка); `Views/InputDialog.xaml(.cs)` удалён |
| Крэш / старт / CLI restore (`App.xaml.cs`, `Program.cs`) | остаются `MessageBox` — осознанно, минимум зависимостей до/после WPF-UI (ADR-033) |

`AppDialogWindow` (новый, `Views/Dialogs/`) скрыт за `internal IDialogPresenter`/`DialogPresenter` —
`ConfirmationService`/`InputDialogService` зависят только от интерфейса, их собственные unit-тесты не
поднимают реальное окно. Заголовок — `Subtitle` (HeadingLevel1), глиф severity (`Segoe Fluent Icons`,
цветной — часть заключительной правки волны 6.3/6.5, изначальный вариант красил глиф `Text.Primary`),
кнопки — визуальный primary (`Theme.Button.Primary`) всегда представляет действие («Да»/«OK»), а кнопка
по умолчанию для Enter выбирается по `MessageBoxImage`: `Question` → «Да», **`Warning` → «Нет»** — осознанно
небезопасный-по-умолчанию Enter для необратимых операций (удаление панели/страницы, «Apply to all panels»)
инвертирован в безопасную сторону; Esc всегда эквивалентен «Нет»/отмене. Это изменение поведения относительно
`MessageBox` (тот не назначал явного дефолта на Warning), принято сознательно и подтверждено
`fences-ux-parity-reviewer` (риск R-12 плана). Владелец и активация: **активное top-level окно приложения →
видимое `MainWindow` → без владельца** (порядок уточнён последней короткой правкой волны 6.3/6.5;
первоначальная реализация проверяла только «активное окно» — этого недостаточно для вызовов из
`ChildWindow.HeaderContextMenu`, который сам никогда не активен, `WS_EX_NOACTIVATE`, ADR-001/013); без
владельца — `CenterScreen`, `ShowInTaskbar=True`, `Topmost` на время показа + явный `Activate()` (R-6).
`AppDialogWindow` **никогда** не бывает владельцем/дочерним `ChildWindow`. `IStatusNotifier.Show` несёт
`Severity`/`Title`/`Message`; `InfoBar` (новый контрол, `Controls/InfoBar.cs`) стоит в `MainWindow` над
`ScrollViewer` контента, показывает цветной глиф и подсвеченный заголовок по severity (заключительная
правка волны — исходная версия красила только текст), кнопку закрытия, `AutomationProperties.LiveSetting`
— `Polite` для Informational/Success/Caution, **`Assertive`** для `Critical` (тоже часть заключительной
правки — изначально всегда `Polite`).

### D-10. Трей — вариант A (WinForms `ContextMenuStrip` + собственный renderer)

Сравнение (план §2.9): вариант A (остаться на WinForms, добавить `ToolStripRenderer` по токенам) против B
(WPF `ContextMenu` через скрытое host-окно — больше нативного риска: закрытие по клику мимо,
`SetForegroundWindow`-семантика, DPI попапа из невидимого окна — ради чисто визуального выигрыша,
отклонён) и C (нативный Win32 `HMENU` — переписал бы весь `TrayIconManager`, не выбран для самого трея, но
его opt-in часть отдельно оценена для shell-меню значков, см. D-11). Реализовано: `TrayMenuPalette`
(Services.Abstractions, `sealed record` из `uint` ARGB — Services не знает о WPF), `TrayMenuPaletteFactory`
(`UI/Theming` — палитра для трея строится в UI-слое из `ThemePalette` по `SystemUsesLightTheme`, затем
передаётся в Services через контракт), `ITrayIconManager.SetMenuPalette`, `TrayMenuRenderer :
ToolStripRenderer` (Services/Providers — фон/рамка/подсветка/текст/галочка-глиф вместо бокса/стрелка
подменю/разделитель, без градиентной колонки картинок старого «Office»-рендера), `TrayThemeAdapter`
(`UI/Lifecycle`, подписан на `IAppThemeService.ThemeChanged`, вызывает `SetMenuPalette` через
`Dispatcher.BeginInvoke(Background)`). Renderer назначается и корневому `ContextMenuStrip`, и каждому
`ToolStripDropDown` (подменю «Страницы» — WinForms не наследует `Renderer` у выпадающих списков) —
переприменяется после `SetPages`-пересборки. **MAJOR-1 (ADR-044) сохранён**: смена палитры не пересоздаёт
strip (`SetContextMenu` вызывается ровно один раз за время жизни трея), только меняет renderer и
`Invalidate()`; если меню открыто в момент смены темы — палитра применяется к следующему показу
(`Opening`), не обрывая уже открытое меню.

### D-11. Нативные поверхности — фиксированные ограничения и одно opt-in-решение (6.4b)

Не стилизуются в этой задаче (и не могут быть без непропорционального риска): shell-меню значков панели
(ADR-040, меню Explorer-расширений), `ColorDialog` (WinForms, «Свой цвет…», ADR-042), `FolderBrowserDialog`
(«Обзор…», Folder Portal), крэш/старт/CLI `MessageBox` (D-9). **Фаза 6.4b (оценка, не обязательство по
плану) закрыта решением «да»:** `win32-interop-specialist` реализовал `INativeMenuTheming`/
`NativeMenuTheming` (Services/Tools) — opt-in тёмных классических Win32-меню процесса через
недокументированные ординалы `uxtheme.dll` **135** (`SetPreferredAppMode`, режим `ForceDark = 2`, не
`AllowDark` — вызывающий уже разрешил тему приложения (HC/gate/режим Windows), меню должны следовать этому
разрешению, а не заново читать системную настройку) и **136** (`FlushMenuThemes` — без него уже открытое
однажды меню держит старую тему в кэше). Обвязан со всех сторон: `NativeMenuTheming.FeatureEnabled` —
константа кода (не настройка), гейт по билду ≥ 18362 (Windows 10 1903 — первая сборка, где ординал 135 это
именно `SetPreferredAppMode`, а не ABI-совместимый, но семантически другой `AllowDarkModeForApp(BOOL)`
1809-й эры), экспорт резолвится `GetProcAddress` из системного `uxtheme.dll` и вызывается только если
найден, светлая тема — Windows-дефолт для любого процесса, поэтому запрос светлой темы до первого запроса
тёмной вообще не делает нативного вызова (пользователь, ни разу не включавший тёмную тему приложения,
никогда не достигает недокументированного API). Затрагивает только классические Win32-меню **этого
процесса** (`HMENU`-попапы — контекстное меню значка и системное меню окна); WPF/WinForms-меню рисует само
приложение и не затронуты. Пункты, которые сам shell-расширения рисуют owner-draw, могут остаться
светлыми — то же ограничение, что у обычного меню Проводника. Manual verify Windows 10 22H2 / Windows 11
24H2 (M-6.16, не блокер).

### D-12. Отклонённые альтернативы

- **WPF Fluent `ThemeMode`** (.NET 9/10) — требует смены TFM с `net8.0-windows`, экспериментальная
  диагностика `WPF0001` потребовала бы подавления (запрещено правилом «0 warnings»), и перекрасила бы
  заодно Palette P (панели), что прямо противоречит D-1.
- **Сторонние библиотеки** (WPF-UI, ModernWpf, MahApps) — новая зависимость + глобальный рестайл;
  прецедент ADR-031 D-2 «без сторонних библиотек навигации».
- **Статические XAML-словари тем** (`Light.xaml`/`Dark.xaml`) — дублировали бы значения с треем (Services
  не читает XAML) и с тестом контраста; выбран C#-источник (D-3).
- **Явная настройка «Светлая/Тёмная/Системная»** — CN-1 = A, без новой настройки (persistence заморожен).

## Реализация

Фазы 6.0 (аудит + три храповика: `ChromeXamlLiteralGuardTests`, `ResxTextLengthGuardTests`,
`MessageBoxUsageGuardTests`) → 6.1 (токены + `SystemThemeProvider`/`IWindowFrameTheming`, тёмная тема за
код-гейтом `AppThemeService.DefaultDarkThemeEnabled`) → 6.2a (библиотека стилей/контролов:
`Theme.Controls.xaml`, `Theme.Implicit.xaml`, `DropDownButton`, `SettingsCard`, `InfoBar` как контролы, ещё
без темы включена) → 6.2b ∥ 6.2c (перенос окна настроек и меню панели/Customize на стили из 6.2a) → 6.3
(диалоги/InfoBar) ∥ 6.4 (трей + 6.4b) → 6.5 (сквозной аудит, фиксы ≤ 2 итераций, этот документ).

**Отклонение от плана, зафиксированное сознательно:** план (§3, фаза 6.2a) предполагал включить
`DarkThemeEnabled = true` последним коммитом 6.2a, сразу после готовности библиотеки стилей. По факту
реализации гейт остаётся выключенным на протяжении 6.2/6.4 и включается только в волне 6.3/6.5
(коммит `f98da21`, «dark theme live»): диалоги (`ConfirmationService`/`InputDialogService`) и уведомления
(`Notify(Information)`) до фазы 6.3 всё ещё шли через `MessageBox`/`Views/InputDialog.xaml` — стоковые,
всегда светлые окна. Включение тёмной темы сразу после 6.2a означало бы, что пользователь мог получить
тёмное окно настроек, из которого «Удалить панель…» открывает светлый системный `MessageBox` — тот же
класс регрессии («промежуточное состояние», риск R-3 плана), который план явно называл причиной держать
гейт закрытым между 6.1 и 6.2, только продлённый до конца 6.3. Решение принято `wpf-ui-builder` на месте и
подтверждено ревью без эскалации к оркестратору — область риска (R-3) та же, что план уже предвидел, разница
только в точке, где риск объективно закрывается.

**Долг T-5, принятый для релиза 1.0.** Храповик «стен текста» (`ResxTextLengthGuardTests`) не дошёл до
нуля исключений: на конец 6.5 allowlist содержит **17 ключей en / 26 ru** длиннее лимитов §2.12 (было 19
en / 30 ru на входе в 6.0) — сокращены самые длинные (`Recycle_Failed_Message_Format`,
`Settings_Confirm_AlignAll_Message_Format`, `Page_Confirm_Remove_ManagedFiles_Message` и др.), но полная
редакторская проходка по всем оставшимся строкам `design:ux-copy` признана отдельной, некритичной для
паритета задачей — вынесена в follow-up ниже, а не блокирует Accepted-статус этого ADR.

**Инвентаризация HC:** `Theme.Accent.Disabled` в режиме High Contrast разрешается в `SystemColors.Highlight`
(не в отдельный `GrayText`-токен, как обсуждалось на этапе планирования) — в HC WPF `SystemColors` не даёт
отдельного «disabled accent», а сам accent в HC уже обязан быть системным `Highlight`; отдельного визуального
состояния «disabled, но акцентный» в HC не вводится, различимость disabled в HC несёт `Theme.Text.Disabled`
→ `SystemColors.GrayText` (там, где это текст, а не заливка).

## Известные ограничения

- **`LayoutSnappingSection`** — часть карточек оставлена без перевода на `SettingsCard` (follow-up
  `DO-026` из плана, не устранён полностью в этой задаче — только большинство карточек Settings).
- **`Typography.IconLabel`** (подписи значков `SystemIconsPanelWindow`) остаётся в Palette P — геометрия
  привязана к `GridMetrics.LabelHeight`, вне scope этой задачи по определению границ D-1.
- **Повторный вход через трей при открытом модальном `AppDialogWindow`** — не адресовано отдельно в этой
  задаче (унаследованный класс риска R-5/R-6 плана); тот же порядок «disable всех окон потока», что и у
  `MessageBox`, но `ShowDialog` отключает **все** top-level окна потока, а не только владельца.
- **`PanelPreviewCard`** сознательно остаётся вне Palette T — это масштабная превью-копия самой панели
  (Palette P), а не элемент хрома, даже когда физически лежит внутри карточки Palette T
  (`PanelCustomizeWindow`/`NewPanelDefaultsSection`).
- **Живая смена High Contrast для Palette P** — по-прежнему подхватывается только при следующем запуске
  приложения (D-12 ADR-025, не пересмотрено этой задачей — R-8 плана).
- **T-5 (RESX «стены текста»)** — 17 en / 26 ru ключей остаются длиннее целевого лимита; принято как долг
  1.0, а не блокер (см. «Реализация» выше).

## Amendments

- **[ADR-025](ADR-025-ambient-accent-visual-language.md)** — `Color.Settings.*`/`Brush.Settings.*` выведены
  из Ambient Accent в новую Palette T и удалены оттуда; `Control.ContextMenu`/`Control.MenuItem`/
  `Control.Popup.Chrome` заменены theme-aware `Theme.ContextMenu`/`Theme.MenuItem`/`Theme.Popup.Chrome`;
  HC «только при старте» для Palette P (D-12 ADR-025) остаётся неизменным и явно не распространяется на
  Palette T (D-2 этого документа).
- **[ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md)** — карточки Settings переведены
  на новый контрол `SettingsCard` (закрывает DO-026 для большинства карточек, не всех — см. «Известные
  ограничения»); тема окна настроек следует теме Windows; ряд кнопок Panels заменён командной строкой
  (D-8); Customize — заголовки групп через `Settings.GroupHeader`, `Auto-size` — `Theme.ToggleSwitch`
  вместо обычного `CheckBox`; ширина сайдбара/окна настроек увеличена (см. amendment ADR-002 ниже).
- **[ADR-042](ADR-042-color-presets-rgb-picker-stepped-size.md)** — известный HC-пробел («кольцо выбора
  свотча/сегмента не связано с `HighlightBrush`») закрыт токенами `Theme.Selection.Indicator`/
  `Theme.Selection.Fill`; Icon layout/Panel geometry (ранее `ComboBox` на 2 значения) переведены на
  `Settings.SegmentedGroup`.
- **[ADR-040](ADR-040-native-shell-context-menu.md)** — 6.4b: opt-in тёмных классических Win32-меню
  процесса (D-11) реализован за `INativeApi`/`INativeMenuTheming`; сам механизм показа shell-меню значка
  (helper-окно, `IContextMenu2`/`IContextMenu3`) не изменён — меню теперь просто наследует тёмную/светлую
  тему процесса через `SetPreferredAppMode`.
- **[ADR-044](ADR-044-tray-page-navigation.md)** — трей получил `TrayMenuRenderer`/`SetMenuPalette`
  (D-10); MAJOR-1 («смена палитры не пересоздаёт `ContextMenuStrip`») явно сохранён и перепроверен тестом.
- **[ADR-013](ADR-013-panel-item-selection-and-rename.md)/[ADR-001](ADR-001-desktop-integration.md)** —
  диалоги, вызываемые из контекста панели (`WS_EX_NOACTIVATE`), никогда не используют `ChildWindow` как
  владельца/родителя; порядок разрешения владельца `AppDialogWindow` — см. D-9.
- **[ADR-033](ADR-033-crash-safety-and-app-log.md)** — крэш-диалог/стартовые/CLI-сообщения остаются
  `MessageBox` намеренно (D-9), не переведены на `AppDialogWindow`.
- **[ADR-002](ADR-002-main-window-as-settings.md)** — размер окна настроек увеличен (сайдбар и общая
  ширина — под RU-текст командной строки Panels и типографику M+2), значение задано только в XAML и
  по-прежнему не персистится (согласуется с исходным решением ADR-002 не хранить размер окна).

### Amendment (2026-10-09): D-8 Panels и фиксы шаблонов Palette T

Фикс-пак на `rc/0.8.0` (build 0 warnings, 6087 тестов зелёные). Новых ADR не заводилось.

**1. D-8, раздел «Панели»: «Ещё» заменён контекстным меню строки.** `DropDownButton «Ещё»` удалён из
`PanelsSection.xaml`. «Настроить…», «Переместить на страницу ▸», «Копировать на страницу ▸», разделитель и
«Удалить панель…» теперь в `ContextMenu` (`Theme.ContextMenu`) на каждой строке `ListBox`
(`ListBox.ItemContainerStyle`). Команды берутся через мост `Tag` (строка → `MainWindowViewModel`; у самого
`ContextMenu` другой `DataContext`). `EventSetter ContextMenuOpening` → `PanelRow_ContextMenuOpening`
(`PanelsSection.xaml.cs`) выделяет строку до открытия, поэтому открытие с клавиатуры (Shift+F10 / клавиша
Menu) действует на нужную панель, а не на ранее выбранную: команды работают с `SelectedPanel`, а не со
строкой. Находимость обеспечивает описание карточки `Settings_Panels_OnThisPage_Description` («Правый клик
по панели: …»); тот же приём, что у `HeaderContextMenu` панели на рабочем столе. Командная строка теперь
**Добавить панель** (primary) + **Настроить…** — две кнопки, правило «≤3 кнопок» D-8 соблюдено
(`SettingsWindowLayoutWidthTests` скорректирован). Ключи RESX `Common_MoreActions_Button` и
`Settings_Panels_More_A11y` удалены; `Settings_Pages_More_A11y_Format` / `Settings_Backup_More_A11y_Format`
остаются (там «⋯» сохранён). Компромисс: действие над строкой без правого клика не видно визуально
(находимость — только текстом карточки и клавиатурой); принят ради более простой командной строки.

**2. Фиксы шаблонов в `Resources/Styles/Theme.Controls.xaml`** (регрессионные тесты —
`tests/DesktopOrganizer.Tests/Unit/Views/ThemeItemContainerTemplateTests.cs`):

- `Theme.ComboBox`: `ContentSite` привязан к `ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"`
  и `ContentStringFormat` — у `ComboBox` с `DisplayMemberPath` в закрытом состоянии показывалось имя типа
  элемента, а не его член.
- Новый `Theme.ListViewItem` с `GridViewRowPresenter` (а не `ContentPresenter`, который игнорирует
  `GridView.Columns`); подключён как `ItemContainerStyle` в `Theme.ListView`. Без него строки снапшотов в
  `BackupSection` показывали «DesktopOrganizer.Core.LayoutSnapshot».
- `Theme.ListBoxItem` и `Theme.ListViewItem`: выбранное состояние добавляет акцентную полосу `Indicator`
  шириной 3 DIP (`Theme.Selection.Indicator`) + `Theme.FocusVisual` — та же форма, что у
  `Settings.SidebarListBoxItem`. Причина та же, что в D-2/D-6: `Theme.Selection.Fill` в Light — белое на
  белом, одной заливкой выделение не читается.

**Прочее.** Палитры, токены и контракты D-1..D-7, D-9..D-12 не затронуты. Ранее закреплённый ADR-031
amendment этого же дня (переключатель «Show desktop icons» в Settings → General) добавил карточку General
в уже существующем стиле `SettingsCard`/`Theme.ToggleSwitch` — отдельных решений Palette T не потребовалось.

## Manual verify

Не блокер. Полный чек-лист по фазам — `docs/PLANS/design-system-1x.md` §10 (M-0.1/M-0.2, M-6.1…M-6.16).
Сводный список manual verify по всему бэклогу 1.x (включая пункт 6) передан пользователю отдельно
оркестратором вместе с этим циклом документирования.
