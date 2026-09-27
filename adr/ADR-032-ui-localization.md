# ADR-032: UI Localization (ru/en) with Hot Language Switch

Status: Accepted (2026-09-27)

Ветка `feature/localization` (от `rc/0.6.1`, база `d5312ec`). Источник решения — бриф
`docs/prompts/localization.md` (решения пользователя D-1..D-6, инвентаризация 177 XAML-литералов /
~98 C#-литералов) и мастер-план `docs/PLANS/localization.md` (`fences-feature-planner`, решения плана
L-1..L-10, контракты §2, глоссарий/ключи §3, 9 пакетов работ §4). Конвейер реализации:
`dotnet-services-engineer` → WP-0S ядро локализации (`72d8dd4`) → `wpf-ui-builder` → WP-0U WPF-плумбинг
(`c410a40`) → пять параллельных пакетов этапа 1, слитые в порядке `merge …-wp2` (`8b54b50`) →
`…-wp4b` (`854338f`) → `…-wp5` (`1dc1e5f`) → `…-wp3` (`b184a48`) → `…-wp4a` (`a8bc99f`) →
`…-wp1` (`99b5356`) → `tdd-test-engineer` WP-6 гейты (`711cfb4`) → два цикла ревью и фикса:
FIX-B/FIX-A (`373b636`/`d230b78`) → `d5cedff` (усиление гейтов) → `c7a81e5` (полировка копии и гейтов,
ревью-итерация 2). Итог на HEAD `c7a81e5`: `dotnet build`/`dotnet test` — 0 errors, 0 warnings, 3855
тестов. Раздел «Реализация» ниже фиксирует фактические расхождения кода с планом.

## Контекст

До этой фичи в приложении не было инфраструктуры локализации: 0 `.resx`, `NeutralResourcesLanguage` не
задан, `CurrentUICulture` не трогается, `DynamicResource` — 0 использований. Инвентаризация
(`docs/prompts/localization.md`) нашла 177 XAML-литералов (136 уникальных) в 13 из 24 файлов и ~98
C#-литералов (~90 уникальных) в 22 файлах, включая plural-формы («N panel(s)»), enum-`ComboBox`,
показывающие сырые имена членов (`List`/`Wrap`, `Manual`/`Name`), и трей (WinForms `ContextMenuStrip`).

Ранние решения проекта явно фиксировали «только английский»:

- [ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md) D-8: «UI-строки нового кода —
  только английский, как во всём проекте… не вводится инфраструктура локализации ради этой фичи».
- [ADR-023](ADR-023-single-instance-and-ipc.md), строка 281: «The two new user-facing messages are
  English-only; localization is a separate cross-cutting task.»

Обе записи явно называли локализацию «отдельной сквозной задачей» — эта задача её закрывает. Пользователь
зафиксировал решения D-1..D-6 (см. «Решения» ниже) и явно разрешил единственное расширение замороженного
контура persistence (CLAUDE.md «Настройки и persistence — ЗАМОРОЖЕНЫ») — новый скаляр `Language`.

## Рассмотренные варианты

### A. Классический RESX + `x:Static` доступ, смена языка через перезапуск приложения

`ResourceManager.GetString` за статическими `x:Static`-обращениями в XAML, без markup extension и без
события смены. Простейший вариант, минимум нового кода.

Отклонён: не даёт горячую смену (D-3 брифа — явное требование пользователя). Перезапуск ради смены языка
хуже, чем перезапуск ради Fences-паритета в любой другой настройке приложения — все остальные настройки
(тема, geometry-режим, sizing) применяются на лету.

### B. RESX + собственный `ILocalizationService` + `{loc:Loc}`/`{loc:LocBinding}` (выбран)

RESX-словари в отдельном проекте без зависимостей (`DesktopOrganizer.Localization`), сервис с
индексатором + `INotifyPropertyChanged` + событием `CultureChanged`, две markup-extension —
`{loc:Loc Key}` (простой биндинг на индексатор) и `{loc:LocBinding}` (multi-binding, пропускающий
исходное значение через существующий `IValueConverter` с живой культурой). Все существующие конвертеры
(`EnumDisplayNameConverter`, `PanelSubtitleConverter` и др.) остаются `IValueConverter` с прежними именами
и параметрами (L-9) — переоценку при смене языка даёт сама markup extension, а не переписывание каждого
конвертера в `IMultiValueConverter`.

Даёт горячую смену без перезагрузки окон: WPF подписывается на `INotifyPropertyChanged` слабо
(`PropertyChangedEventManager`), поэтому утечек подписок на биндинги нет; единственная зона внимания —
явные C#-подписки VM/сервисов на `CultureChanged` (см. R-11 плана, закрыто `IDisposable`-отпиской и
правилом «per-panel VM не подписывается»).

### C. JSON-словари вместо RESX

Собственный формат `{ "Key": "value" }` на язык, без `ResourceManager`/satellite-ассамблей.

Отклонён: теряет весь готовый .NET-конвейер (`SatelliteResourceLanguages`, `NeutralResourcesLanguage`,
инсталлятор publish уже умеет отбирать сателлиты по культуре) и обязывает писать собственный fallback,
кэширование и paritet-проверку с нуля — RESX это даёт бесплатно, а paritet-тесты (`ResxConsistencyTests`)
всё равно нужны в любом варианте.

### D. XAML `ResourceDictionary` на язык + `DynamicResource`

Отдельный `ResourceDictionary` (`en.xaml`/`ru.xaml`) со строковыми ресурсами, подмена в
`Application.Resources.MergedDictionaries` при смене языка, `DynamicResource` в местах использования.

Отклонён (L-4 плана): нужен собственный словарь ресурсов на язык плюс подмена в `MergedDictionaries`, что
ломает существующий порядок словарей `App.LoadResourceDictionaries()` (комментарий `App.xaml.cs:267-269`
требует конкретного порядка merge) и не работает для C#-конвертеров/VM без дополнительного слоя. `Binding`
на индексатор сервиса (вариант B) решает то же самое единым механизмом для XAML и C#.

## Решения

### D-1. Persistence: единственный новый скаляр `Language`

`IAppSettings.Language` (`string`, `""`/`"en"`/`"ru"`, дефолт `""` = «как в Windows») — единственное
расширение замороженного контура CLAUDE.md, сделанное с явным разрешением пользователя (бриф
`docs/prompts/localization.md` D-1). JSON-схема `application-state.json` (`schemaVersion = 5`,
[ADR-024](ADR-024-versioned-json-persistence.md)) не меняется, миграций нет, DTO не трогаются. Diff
контура persistence ограничен четырьмя файлами: `Properties/Settings.settings`,
`Properties/Settings.Designer.cs`, `Services/IAppSettings.cs`, `Services/AppSettings.cs` — подтверждено
`SchemaContractTests`/`PanelStateMapperTests`/`LayoutSnapshotServiceTests`/`StateRestoreCommandTests`,
прошедшими без правок (G-1 плана). См. amendment к ADR-024 D-10 ниже — `Language` пополняет прецедентный
список аддитивных скаляров `IAppSettings`, введённых после ADR-024 (`DefaultSurfaceStyle`,
`IsLayoutLocked`, `NormalModeLayoutGestures`, `DefaultPanelGeometryMode`, `DefaultIconLayoutPolicy`).

Откат на старую сборку не знает ключа `Language` и молча его игнорирует (`LocalFileSettingsProvider`
читает только известные свойства) — прецедент того же класса риска, что инцидент 2026-09-24 в JSON-схеме,
здесь безопасен именно потому, что `user.config` — per-key, не единый документ с версией.

### D-2. Формат словарей — RESX в отдельном проекте без зависимостей

`src/DesktopOrganizer.Localization/DesktopOrganizer.Localization.csproj` (`Microsoft.NET.Sdk`, без
`ProjectReference`), `Strings.resx` (нейтральный = английский) + `Strings.ru.resx`.
`Directory.Build.props`: `<NeutralLanguage>en</NeutralLanguage>` (генерирует
`NeutralResourcesLanguageAttribute`), `<SatelliteResourceLanguages>en;ru</SatelliteResourceLanguages>`.
Ссылаются `Services` и `UI` (и `Tests`); `Core` и `Services.Abstractions` — не ссылаются, чтобы `Core`
оставался чистой моделью и интерфейс `ILocalizationService` использовал только BCL-типы (L-1 плана).
Культуры — нейтральные `CultureInfo.GetCultureInfo("en")`/`("ru")`, не `en-US`/`ru-RU`: поиск satellite
идёт прямо в `ru\`, формат чисел/дат — базовый для языка.

`LocalizationResources` (`DesktopOrganizer.Localization`): статический, потокобезопасный,
никогда не бросает — отсутствующий ключ возвращает сам ключ (видимо в UI, greppable, покрыто
`XamlKeyCoverageTests`/`CodeKeyCoverageTests`, G-L2). `PluralRules.Select(culture, n)` — категории CLDR
`One/Few/Many/Other`; для ru: `n%10==1 && n%100!=11 → One`, `n%10 in 2..4 && n%100 not in 12..14 → Few`,
иначе `Many`; для en и любого другого языка: `n==1 → One`, иначе `Other`. `SupportedLanguages.Resolve`
разрешает `""`/`null`/неизвестный тег → системная UI-культура, если её `TwoLetterISOLanguageName == "ru"`,
иначе `en` (D-4). `SystemCulture.OriginalUiCulture` захватывается при первом обращении — `Program.Main`
обращается первой строкой, до любого переопределения культуры потока (находка плана: иначе «как в
Windows» после ручного выбора разрешалось бы в уже переопределённую культуру).

### D-3. Горячая смена без перезапуска

`ILocalizationService` (`Services.Abstractions`): индексатор `this[key]`, `INotifyPropertyChanged`,
`CurrentCulture`, `LanguagePreference`, `SupportedCultures`, событие `CultureChanged`
(поднимается ПОСЛЕ `PropertyChanged("Item[]"/"CurrentCulture")`, только когда изменилась именно
эффективная культура, не просто предпочтение — например «Как в Windows» → `"ru"` на уже русской Windows
поднимает только `PropertyChanged("LanguagePreference")`, без `CultureChanged`). `ApplyLanguage`
разрешает предпочтение и применяет его, но **не** сохраняет в `IAppSettings` — это делает вызывающий
UI-код. `LocalizationService` (`Services/Localization/LocalizationService.cs`) реализует контракт;
исключения из подписчиков `CultureChanged` изолируются индивидуальным try/catch на каждый
delegate из `GetInvocationList()` — падение одного подписчика не останавливает остальных и не
пробрасывается наружу; исключение логируется в `IDiagnosticsLog` категорией `localization`, событие
`CultureChangedSubscriberFailed`, только `exceptionType` (никогда `ex.Message`, инвариант приватности
ADR-027).

WPF-инфраструктура (`src/DesktopOrganizer.UI/Localization/`, xmlns `loc`):

- `LocalizationSource` — статический `Current`; по умолчанию (до `Initialize`) — ленивый
  `new LocalizationService(en)`, так что дизайнер и тесты без явной инициализации всегда видят
  английский. `Initialize` вызывается один раз из `App.StartApp`, до разбора первого XAML; повторный
  вызов с другим экземпляром бросает.
- `LocExtension` → `{loc:Loc Key}`, `LocBindingExtension` → `{loc:LocBinding Path, Converter,
  ConverterParameter, FormatKey, …}` (строит `MultiBinding` из исходного значения и `CurrentCulture`,
  `Converter = LocalizedValueConverter`), `CultureBindingExtension` → `{loc:CultureBinding}` (для
  существующих `MultiBinding`, напр. `PanelsSection.xaml`). Общая логика построения `Binding`/выбора
  «вернуть результат `ProvideValue` для DP-цели vs вернуть сам `Binding` для `Setter`/шаблона» вынесена в
  internal `LocBindingFactory` (не было явно поимённо в плане — добавлено реализатором как общий примитив
  для трёх markup extensions и тестов, без изменения публичного контракта §2.4).
- `LocalizedValueConverter` (internal `IMultiValueConverter`), `LocKeyConverter` (ключ-как-данные →
  строка), `PageDisplayNameConverter` (`"default"` → `Page_Default_DisplayName`, иначе — как есть, D-5).
- `CultureSync` (internal static, идемпотентный `Attach`/`ResetForTests`): один раз на процесс
  переопределяет `FrameworkElement.LanguageProperty`'s default metadata и регистрирует class handler на
  `Window.LoadedEvent`; на каждый `CultureChanged` обновляет `CultureInfo.DefaultThreadCurrentUICulture`/
  `CurrentUICulture` и `Language` каждого открытого окна. `Attach`, вызванный повторно (в т.ч. в тестах),
  сначала отписывается от предыдущего `ILocalizationService`, а не накапливает подписки — фикс review
  round 2 (см. «Реализация»).

Трей (`TrayIconManager`, `Services/Providers/`) **не пересобирается**, а переименовывается на месте: пять
пунктов меню хранятся в полях со стабильными `Name` (`Tray.Settings`, `Tray.EditLayout`, `Tray.LockLayout`,
`Tray.ShowDesktopIcons`, `Tray.Exit`), `ApplyTexts()` переустанавливает `Text`/`ToolTipText` в ответ на
`CultureChanged`. Полная пересборка `ContextMenuStrip` была источником утечки HWND и гонки с `Click` в
Фазе 6 (MAJOR-1) — этот путь сознательно не повторён.

VM пересчитывают привязанные строки по `CultureChanged` явным списком свойств (`RaiseLocalizedProperties`
по духу, не `string.Empty`), `IDisposable`-VM отписываются в `Dispose`; per-panel `ChildWindowViewModel` на
`CultureChanged` не подписывается — весь текст панели идёт через `{loc:…}` в XAML (это защищает от утечки
подписки на N открытых панелей). `SystemCulture.OriginalUiCulture` захватывается первой строкой
`Program.Main`.

### D-4. Языки — en/ru, дефолт «Как в Windows»

Первые поддерживаемые языки — английский (нейтральный нейтральный/дефолтный) и русский. Дефолт —
следовать за UI-культурой Windows (`SupportedLanguages.Resolve`); любая культура, кроме русской,
разрешается в английский. Нормализация регистра: `LanguagePreference` нормализует введённый/сохранённый
тег к канонической нижнерегистровой форме (`"RU"` в `user.config` читается как `"ru"`) — фикс review
round 1 (NIT), чтобы точное сравнение тега в пикере всегда совпадало с эффективной культурой.

### D-5. Данные vs UI

«New Panel», имя снапшота по умолчанию (`Snapshot_DefaultName_Format`, `Layout {0:dd.MM HH:mm}`) —
локализуются в момент создания и далее хранятся как данные, не переводятся при смене языка задним числом
(нет новых полей в JSON). ID страниц (`"default"`, `"Page N"`) остаются идентификаторами; `"default"`
показывается через `PageDisplayNameConverter` (`Page_Default_DisplayName`), остальные («Page 2») — как
есть (CN-2 варианта A, буквально по D-5 брифа).

### D-6. Кнопки нативных `MessageBox` — язык ОС

Текст сообщений `MessageBox` локализуется через `LocalizationResources`, но кнопки (Да/Нет/ОК) рисует ОС
на языке системы — ожидаемое расхождение (R-14 плана), не устраняется, если язык приложения ≠ язык
Windows.

### CN-1..CN-3 (неблокирующие, дефолт из плана)

- **CN-1** (термин «layout»): вариант **A** — «расположение» («Изменить расположение», «Закрепить
  расположение», «Расположение и привязка», «снимок расположения»).
- **CN-2** (показ ID страниц `"Page N"` в русском UI): вариант **A** — буквально по D-5, как есть
  («Page 2»), без display-конвертера для не-`"default"` страниц.
- **CN-3** (культура форматирования чисел/дат, L-6): вариант **A** — язык интерфейса определяет формат
  (`Window.Language`/`CultureSync`), региональный `CurrentCulture` Windows не трогается.

### Соглашение об именовании ключей RESX (§2.6 плана)

Формат `Area_Sub_Element[_Part]`, PascalCase-токены через `_`
(`^[A-Z][A-Za-z0-9]*(_[A-Z0-9][A-Za-z0-9]*)+$`). Области: `Common`, `App`, `Startup`, `Restore`, `Tray`,
`Language`, `Settings`, `Customize`, `Panel`, `SystemIcon`, `Snapshot`, `Page`, `Hint`, `Enum`, `Plural`,
`Dialog`. Суффиксы: `_Header`/`_Description`/`_DisabledReason`/`_Tooltip`/`_A11y`/`_Title`/`_Message`/
`_Format` (только ключи с `{n}`)/`_Button`. Enum: `Enum_{EnumType}_{Member}`. Plural: `Plural_{Noun}_{One,
Few,Many,Other}`, `{0}` = число, все 4 формы обязательны в обоих языках (у en `Few`/`Many` = `Other`, у ru
`Other` = `Many`) — так наборы ключей en/ru совпадают, что упрощает `ResxConsistencyTests` до простого
равенства множеств. RESX организован регионами-маркерами `<!-- ==== BEGIN <owner> ==== -->`/`END` — один
на пакет работ, чтобы шесть параллельных «переводчиков» не конфликтовали в одном файле (находка плана
№1); общий SHARED-регион (`Common_`, `Enum_`, `Plural_`, `Language_`, `Page_`, `Panel_NewTitle`,
`Snapshot_`, `SystemIcon_`, `Startup_`, `Restore_`) засеян заранее пакетом WP-0S.

## Гейты (тесты, `tests/DesktopOrganizer.Tests/Unit/Localization/`)

- **G-L1** `XamlLiteralGuardTests` — в `src/DesktopOrganizer.UI/**/*.xaml` нет текстовых литералов в
  `Text|Header|Content|ToolTip|Title|AutomationProperties.Name|AutomationProperties.HelpText`, включая
  `StringFormat`-шаблоны (усилено `d5cedff` — исходный скан пропускал `StringFormat="..."`); allowlist —
  глифы, «DesktopOrganizer», `d:`-атрибуты дизайнера, пустые значения.
- **G-L2** `XamlKeyCoverageTests` + `CodeKeyCoverageTests` — каждый ключ, на который ссылается
  `{loc:Loc}`/`FormatKey=`/литерал в C#, существует в neutral RESX (прямое направление, «ссылка →
  реальный ключ»); **`KeyUsageCoverageTests`** — обратное направление: каждый ключ в `Strings.resx`
  достижим хотя бы из одного реального call site (литерал, или один из трёх динамически строящихся
  семейств — `Enum_{Type}_{Member}`, `SystemIcon_{Kind}`, `Plural_{Base}_{Category}`); падает на мёртвых
  ключах (заменил предыдущий предупреждающий, не блокирующий отчёт `UnusedResxKeys_…`, добавлено
  `d5cedff`).
- **G-L3** `ResxConsistencyTests` — паритет ключей en/ru (одинаковые множества), у `_Format`-ключей и
  plural-баз совпадают индексы плейсхолдеров, у не-`_Format`-ключей нет `{\d`, нет пустых значений, у
  всех plural-баз — все 4 CLDR-формы, имена ключей соответствуют regex §2.6, нет ведущих/хвостовых
  пробелов (trim-правило усилено `d5cedff`).
- `HotSwitchIntegrationTests` (коллекция `GlobalCulture`, `DisableParallelization = true`) — через
  `XamlReader` (не живые окна, см. «Реализация») проверяет, что `ApplyLanguage` меняет тексты нескольких
  элементов, `Window.Language` и культуру потока за один проход, с восстановлением в `finally`.
- `InstallerLocalizationContractTests` — проверяет состав publish-каталога инсталлятора (только `ru\`
  сателлит рядом с приложением).

## Известные ограничения (сознательно приняты)

- **`DefaultThreadCurrentUICulture`** влияет на язык текста исключений WPF в диагностических логах — эти
  тексты остаются на языке, действовавшем в момент исключения, не переводятся задним числом; это не
  UI-текст приложения (правило брифа: диагностика/консоль остаются английскими для разработчиков).
- **Кэшированные тексты стандартного контекстного меню `TextBox`** (Copy/Paste/Undo — рисует сам WPF из
  системных ресурсов) не гарантированно переживают горячую смену языка в рамках одной открытой сессии
  меню — известное ограничение WPF, не устраняется этой фичей.
- **`StateRestoreCommand`** (Services/Storage) не тронут — входит в замороженный контур восстановления
  ADR-024 §7; локализованный текст `MessageBox` для его результатов собирает `Program.cs` из
  `StateRestoreResult.Outcome` и путей, английский `Message`-текст остаётся для консоли и как «Details».
- **Консольный вывод** (`Console.Error`, диагностические логи `IDiagnosticsLog`) — английский, не
  переводится: читают скрипты и разработчики, не конечный пользователь UI.
- **`SystemIconDescriptor.DefaultCaption`** (`Core`) — английский fallback-текст, используемый только если
  ключ `SystemIcon_{Kind}` не резолвится; `Core` не получает зависимости на `Localization`, поэтому не
  может сам знать о текущей культуре.
- **Данные пользователя (D-5)**: «New Panel», имена снапшотов, ID страниц `"Page N"` в уже сохранённых
  панелях остаются на языке, действовавшем в момент создания — не переводятся при смене языка задним
  числом.
- **`MessageBox`-кнопки (D-6)** — язык ОС, может не совпадать с языком приложения.

## Реализация (2026-09-27)

Фиксирует фактически внедрённое к HEAD `c7a81e5` (не намерения плана). Полная трассировка инвентаризации
брифа → пакеты — план `docs/PLANS/localization.md` §8.

### Расхождения с планом

| Пункт плана | План | Факт | Оценка |
|---|---|---|---|
| Ключи Common (§3.2) | `Common_Ok_Button`/`Common_Cancel_Button`/`Common_LockedReason` — три ключа. | Регион SHARED фактически содержит 11 `Common_*`-ключей: помимо трёх плановых — `Common_EditLayout`, `Common_IconLayout_Header`, `Common_IconSize_Header`, `Common_LockLayout`, `Common_Remove_Button`, `Common_Rename`, `Common_SurfaceStyle_Header`, `Common_ValuePx_Format`. | Не блокирует Accepted: это именно то, что §3.3 плана предвидело как механизм — пакет, которому нужен переиспользуемый ключ вне своего домена, кладёт его в SHARED с префиксом `Common_` вместо дублирования в своём регионе. Согласуется с духом плана (уменьшение дублирования между WP-3/WP-4a/WP-4b), не нарушает регионную дисциплину §3.3. |
| `Settings_Pages_CurrentBadge` (план §2.8: `Settings_Pages_CurrentBadge_Format`) | Ключ с суффиксом `_Format` (несёт `{0}`/плейсхолдер бейджа «(current)»). | Ключ без `_Format`: `Settings_Pages_CurrentBadge` — статичный текст без плейсхолдера. | Минорное отклонение от именования §2.6 (правило «`_Format` только если есть `{n}»` — здесь плейсхолдера и не оказалось нужно, поэтому суффикс закономерно опущен реализатором). Не влияет на `ResxConsistencyTests` (нет `{\d` в значении — проверка проходит). |
| `Settings_Panels_Subtitle_View_Format` (ru) | Локализованный текст вида «{0} вид» аналогично en. | ru-значение — `"{0}"` (без слова «вид»), тождественно en, только с идентичным плейсхолдером. | Follow-up: не блокирует Accepted (текст функционален, паритет плейсхолдеров есть), но не полностью раскрывает глоссарий — кандидат на точечную правку копии, не архитектурное расхождение. |
| Дубль `EnumDisplayName x:Key` (§2.4: «переносится в пост-мерж cleanup») | Одно объявление `<conv:EnumDisplayNameConverter x:Key="EnumDisplayName"/>` вместо двух в `Settings.xaml`/`PanelCustomizeWindow.xaml`. | Дубль **оставлен** намеренно — оба файла явно документируют причину (комментарии `PanelCustomizeWindow.xaml:24-40`): единый merge словаря `Settings.xaml` в `App.LoadResourceDictionaries()` уже был источником `XamlParseException` в ADR-031 («Cannot find resource named 'Settings.PageHeader'» — window-локальные словари не видны шаблонам, парсящимся раньше), и узкая тестовая fixture (`SettingsSectionsInitializeTests`-подобная) ловит `XamlParseException: Cannot find resource named 'EnumDisplayName'`, если дубль убрать без более широкого рефакторинга видимости ресурсов. | Follow-up-кандидат (см. ниже), не блокирует Accepted — идентичен по духу follow-up'у `SettingsCard` из ADR-031: явно распознанный, документированный технический долг, не архитектурное решение. |
| `LocBindingFactory` | Не выделен явным контрактом §2.4 — три markup extensions описаны как самостоятельно строящие `Binding`/`MultiBinding`. | Добавлен internal статический класс `LocBindingFactory` (`CreateKeyBinding`, `CreateCultureBinding`, `ProvideValueOrBinding`) как общий примитив трёх extensions и тестов (WP-0U). | Не расхождение по существу — реализация того же публичного контракта §2.4 (`{loc:Loc}`/`{loc:LocBinding}`/`{loc:CultureBinding}`), просто с меньшим дублированием кода между тремя классами. |
| `TargetNullValue` (LocBindingExtension, §2.4) | Свойство перечислено в контракте `LocBindingExtension` наряду с `FallbackValue`. | Удалено в ходе реализации — конвейер `LocalizedValueConverter` покрывает `null`-значение через `FallbackValue`/`UnsetValue` без отдельного `TargetNullValue`. | Минорное сужение публичного API markup extension относительно §2.4; не найдено ни одного реального usage, требовавшего `TargetNullValue` отдельно от `FallbackValue` — не блокирует Accepted. |
| `HotSwitchIntegrationTests` (план §4.8: `MainWindow` + `ChildWindow` на STA) | Реальные окна `MainWindow`/`ChildWindow`, открытые на STA-потоке, смена языка проверяется по видимым `TextBlock`/`MenuItem.Header`. | Тест построен через `XamlReader` на облегчённом дереве элементов с `{loc:…}`-биндингами, а не через реальные окна приложения (избегает тяжёлой инициализации `MainWindow`/`ChildWindow`/DI в тестовом хосте). | Follow-up: покрывает контракт markup extensions и `CultureSync` за пределами реальных Views/VM-графов приложения; интеграция «реальное окно End-to-end» остаётся manual verify (MV-L2/MV-L3), не автотестом. |
| Порядок слияния этапа 1 (план §5.3: WP-2 → WP-3 → WP-4b → WP-5 → WP-4a → WP-1) | Фиксированный порядок из шести merge. | Фактический порядок реального слияния (`.git/logs/HEAD`): `…-wp2` → `…-wp4b` → `…-wp5` → `…-wp3` → `…-wp4a` → `…-wp1`. | Перестановка WP-3/WP-4b в порядке слияния относительно плана; итоговый набор файлов и конфликтов не изменился (регионная дисциплина RESX сделала порядок некритичным) — не архитектурное расхождение. |

### Follow-up-кандидаты (не блокируют Accepted)

- **Дубль `EnumDisplayName x:Key`** (`Resources/Styles/Settings.xaml:21`, `Views/PanelCustomizeWindow.xaml:40`)
  — требует того же класса рефакторинга видимости ресурсов, что уже задокументирован в ADR-031 (перенос
  window-локальных словарей в `App.LoadResourceDictionaries()`), не выполнен в этой итерации.
- **`Settings_Panels_Subtitle_View_Format` (ru = `"{0}"`)** — точечная правка копии, раскрывающая
  глоссарий §3.1 полнее (например, «Вид: {0}» или аналог, согласованный с `fences-ux-parity-reviewer`).
- **Второй язык добавляется одним файлом**: `Strings.<tag>.resx` + запись в `SupportedLanguages.All` —
  инфраструктура (RESX-регионы, гейты G-L1..G-L3, `LocalizationResources`) уже рассчитана на N языков, не
  только на два.
- **«Glass»-подсказка дублируется двумя ключами** (surface-style описание всплывает в двух разных картах
  настроек с почти идентичным текстом) — кандидат на объединение в один `Common_*`-ключ при следующей
  правке копии.

Контракты §2.1/§2.2 (`ILocalizationService`, `LocalizationResources`, `SupportedLanguages`,
`SystemCulture`, `PluralRules`), C-5 (`LanguageOptions`/`SelectedLanguageOption`/`LanguageOption`) и
D-10-класса межпакетные контракты (`Tray.*`-имена пунктов, `PageDisplayNameConverter`, `LocKeyConverter`)
реализованы без переименования.

## Amendment к ADR-031 (D-8 «только английский»)

[ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md) D-8 («UI-строки нового кода — только
английский… не вводится инфраструктура локализации ради этой фичи») — **отменяется этим ADR**. Вся
поверхность, добавленная ADR-031 (боковая навигация `MainWindow`, `PanelCustomizeWindow`, карточки
`Settings.*`), локализована в рамках WP-4a/WP-4b этой фичи. Остальные решения ADR-031 (D-1..D-7, D-9,
D-10) не пересматриваются.

## Amendment к ADR-023 (строка 281, «English-only»)

[ADR-023](ADR-023-single-instance-and-ipc.md), строка 281 («The two new user-facing messages are
English-only; localization is a separate cross-cutting task») — **отменяется этим ADR**. Оба сообщения
(`Startup_OtherSession_Message`/`Startup_ActivationFailed_Message`, см. §2.8 плана) локализованы в
`Program.cs` пакетом WP-0S. Остальные решения ADR-023 (D-1..D-10) не пересматриваются.

## Amendment к ADR-024 (аддитивный скаляр `Language`)

[ADR-024](ADR-024-versioned-json-persistence.md) вводит версионированный JSON-документ для структурного
состояния и явно оставляет `ApplicationSettingsBase`/`user.config` для «оставшихся простых скалярных
настроек» (Context, «`ApplicationSettingsBase` is preserved for the… remaining simple scalar
preferences»). CLAUDE.md формализовало эту границу как заморозку контура persistence, требующую явного
разрешения пользователя на любое расширение — в том числе на новые скаляры `IAppSettings`/`user.config`.

Этим ADR фиксируется: `IAppSettings.Language` (D-1 выше) — **дополнительный аддитивный скаляр**,
введённый с явного разрешения пользователя (`docs/prompts/localization.md` D-1), продолжающий тот же
прецедентный ряд, что и `DefaultSurfaceStyle`, `IsLayoutLocked`, `NormalModeLayoutGestures`,
`DefaultPanelGeometryMode`, `DefaultIconLayoutPolicy` (все — аддитивные `IAppSettings`-скаляры, введённые
после ADR-024 соответствующими фичами, без изменения `schemaVersion`/JSON-схемы). Это не открывает общее
разрешение на будущие скаляры без отдельного явного «да» — каждый новый скаляр CLAUDE.md продолжает
требовать собственного разрешения в самой задаче. Инварианты D-1..D-13 ADR-024 не затронуты: JSON-схема,
миграции, DTO не менялись.

**Предлагаемая правка CLAUDE.md** (раздел «Настройки и persistence — ЗАМОРОЖЕНЫ», применяется только по
отдельному подтверждению пользователя — этот ADR лишь предлагает формулировку, не редактирует CLAUDE.md):

> Разрешённое исключение (дано пользователем 2026-09-26, см. ADR-032 D-1, `docs/prompts/localization.md`):
> аддитивный скаляр `IAppSettings.Language` (`""`/`"en"`/`"ru"`, дефолт `""`) — язык интерфейса. Новые
> аналогичные аддитивные скаляры всё равно требуют отдельного явного разрешения в самой задаче.

## Manual verify (Win10/Win11)

Полная матрица — `docs/PLANS/localization.md` §7.3, MV-L1..MV-L15 (обязательные: MV-L2, MV-L4, MV-L6,
MV-L7, MV-L12). Дополнительно из ревью:

- Перенос заголовков в окне Settings (720×560) при масштабах 100/125/150/175% DPI на русском UI — нет
  обрезаний без переноса/тултипа, нет горизонтального скролла.
- Трей-меню у края экрана при 175% DPI на русском UI (более длинный текст пунктов).
- Narrator на `SystemIconsPanelWindow` в русском UI — озвучиваются русские `AutomationProperties.Name`.
- Язык стандартного контекстного меню `TextBox` (Copy/Paste/Undo) после горячей смены языка приложения —
  известное ограничение (см. «Известные ограничения»), подтвердить фактическое поведение вживую.
- GDI-объекты (Task Manager) за 20 переключений языка через трей — не должны монотонно расти (подтвердить
  отсутствие утечки при `ApplyTexts()` на месте, без пересборки `ContextMenuStrip`).
- Инсталлятор: содержимое `{app}` после установки — только каталог `ru\` рядом с
  `DesktopOrganizer.Localization.resources.dll`, других папок-культур нет.

## Последствия

### Позитивные

- Закрыта сквозная задача, явно отложенная ADR-023/ADR-031 («localization is a separate cross-cutting
  task» / «не вводится инфраструктура… ради этой фичи»).
- Горячая смена языка без перезапуска для всех открытых окон/панелей/трея/панели системных значков —
  ни одна другая настройка приложения, применяемая на лету, не потеряла этот UX-паритет.
- Закрыт бэклог ROADMAP «общий `EnumDisplayConverter`» (`docs/ROADMAP.md:1229-1232`) — попутный эффект
  инфраструктуры локализации, а не отдельная фича: `EnumDisplayNameConverter` теперь резолвит текст по
  конвенции `Enum_{Type}_{Member}` вместо `switch`, что и устраняет сырые имена enum-значений.
- Единственное расширение замороженного контура persistence — один скаляр, с явным разрешением
  пользователя и без касания JSON-схемы/DTO/миграций.

### Негативные / риски

- Русский текст длиннее английского на 20–35% — риск обрезки в узких картах настроек/трее/CM на высоких
  DPI; частично митигировано `TextWrapping`/`TextTrimming`+тултипами, требует manual verify (MV-L4).
- Глобальное состояние (`LocalizationSource.Current`, `FrameworkElement.LanguageProperty.OverrideMetadata`,
  культура потока UI) — источник потенциальных проблем в параллельных тестах; изолировано коллекцией
  `GlobalCulture` (`DisableParallelization = true`) и восстановлением в `finally`.
- Известные ограничения (см. выше) — WPF-кэш меню `TextBox`, тексты исключений WPF, `SystemIconDescriptor.
  DefaultCaption` fallback — остаются, документированы, а не устранены.
- Данные, созданные до смены языка («New Panel», имена снапшотов, ID страниц), остаются на языке создания
  (D-5) — ожидаемое, но заметное пользователю расхождение при смешанном использовании языков в одной
  сессии данных.

## Не сделано в этой фиче (сознательно)

- Языки, кроме en/ru; RTL; pseudo-localization-сборка (D-4 брифа) — второй/третий язык не в объёме,
  инфраструктура рассчитана на добавление одним `.resx`-файлом (см. follow-up).
- Локализация метки системных значков из shell (`SHGetFileInfo`) — натив, отдельная задача;
  `SystemIcon_{Kind}`-ключи покрывают только собственные ярлыки приложения (This PC, Recycle Bin и т.п.).
- Перевод уже сохранённых данных пользователя (D-5) — не реализовывался и не планировался.
- Изменение `CultureInfo.CurrentCulture` (региональный формат/сортировка Windows) — L-6/CN-3, сознательно
  не тронуто; `NaturalStringComparer` не меняется.
