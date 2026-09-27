# Бриф: локализация UI (ru/en) — сквозная задача

Дата: 2026-09-26. Ветка: `feature/localization` (от `rc/0.6.1`). Статус: **реализовано**, см.
[ADR-032](../../adr/ADR-032-ui-localization.md) (Accepted, 2026-09-27; HEAD `c7a81e5`, 0 errors/0
warnings, 3855 тестов). Решения D-1..D-6 ниже зафиксированы пользователем и реализованы без пересмотра;
раздел «Реализация» ADR-032 фиксирует фактические расхождения кода с мастер-планом
(`docs/PLANS/localization.md`).

## Решения пользователя (зафиксированы, не пересматривать в конвейере)

- **D-1. Хранение выбранного языка — новый скаляр `Language` в `user.config` / `IAppSettings`.**
  Пользователь дал **явное разрешение** на это расширение замороженного контура настроек (CLAUDE.md
  «Настройки и persistence — ЗАМОРОЖЕНЫ»). Разрешение ограничено ОДНИМ новым скаляром `Language`
  (строка, IETF-тег `"en"` / `"ru"` или пустая строка = следовать за языком Windows). JSON-схема
  `application-state.json` (`schemaVersion = 5`) **не меняется**, миграций нет, DTO не трогаются.
  Прецеденты аддитивных скаляров после ADR-024: `DefaultSurfaceStyle`, `IsLayoutLocked`,
  `NormalModeLayoutGestures`, `DefaultPanelGeometryMode`, `DefaultIconLayoutPolicy`.
- **D-2. Формат словарей — RESX** (`Strings.resx` нейтральный = английский, `Strings.ru.resx`), в отдельном
  проекте ресурсов, на который ссылаются `Services` и `UI`. `NeutralResourcesLanguage=en`,
  `SatelliteResourceLanguages=en;ru`.
- **D-3. Смена языка без перезапуска** (горячая). `ILocalizationService` (Services.Abstractions) с
  индексатором + INPC + событием `CultureChanged`; markup extension `{loc:Loc Key}` → `Binding` на индексатор;
  C#-строки в VM пересчитываются по событию; трей (WinForms `ContextMenuStrip` в Services) пересобирает
  меню по событию; `FrameworkElement.Language` открытых окон обновляется.
- **D-4. Первые языки — английский (нейтральный) и русский.** Дефолт — следовать за UI-культурой Windows,
  fallback на английский для любой другой культуры.
- **D-5. Локализуемый текст, который является данными пользователя** («New Panel», `Layout dd.MM HH:mm`),
  переводится в момент создания и далее хранится как данные. ID страниц (`"default"`, `"Page N"`) остаются
  идентификаторами; для `"default"` — display-конвертер на локализованное имя, остальные показываются как
  есть. Никаких новых полей в JSON.
- **D-6. Кнопки нативных `MessageBox` (Да/Нет) рисует ОС** — не локализуются приложением, это ожидаемо.

## Инвентаризация (Explore-агент, 2026-09-26)

- Инфраструктуры локализации нет: 0 `.resx`, нет `NeutralResourcesLanguage`, `CurrentUICulture` не
  трогается, `DynamicResource` — 0 использований (387 `StaticResource`, кисти `po:Freeze`).
- XAML: **177 литералов / 136 уникальных** в 13 из 24 файлов. Топ: `Views/ChildWindow.xaml` (36),
  `Views/PanelCustomizeWindow.xaml` (25), `Views/Settings/NewPanelDefaultsSection.xaml` (23),
  `LayoutSnappingSection.xaml` (20), `SystemIconsSection.xaml` (15), `PanelsSection.xaml` (15),
  `GeneralSection.xaml` (13), `BackupSection.xaml` (9), `PagesSection.xaml` (7), `Resources/Templates.xaml` (6),
  `AboutSection.xaml` (5), `InputDialog.xaml` (2), `MainWindow.xaml` (1). 30 из них —
  `AutomationProperties.Name`. Шаблоны в `StringFormat`: `BackupSection.xaml:53,57`, `PagesSection.xaml:59,63`.
  Склейки: `PagesSection.xaml:36` `" (current)"`, `AboutSection.xaml:15` `<Run Text="Version "/>`.
- C#: **~98 литералов / ~90 уникальных** в 22 файлах:
  `UI/Converters/EnumDisplayNameConverter.cs:30-46` (13), `UI/Views/ChildWindow.ContentSizing.cs` (8),
  `UI/Models/SettingsSection.cs:57-67` (8), `UI/ViewModels/PanelCustomizeViewModel.cs` (8),
  `UI/ViewModels/MainWindowViewModel.cs` (10), `UI/ViewModels/LayoutSnapshotsViewModel.cs` (7),
  `Services/Providers/TrayIconManager.cs:98,122-142` (6), `UI/Program.cs:22-47` (5, до DI),
  `Services/Storage/StateRestoreCommand.cs:78-130` (5), `Core/Models/SystemIconDescriptor.cs:23-39` (5),
  `UI/Converters/PanelSubtitleConverter.cs` (5), `UI/Behaviors/HintPopupController.cs:82-85` (4),
  `UI/Services/PageManagerService.cs:146-153` (4), `ItemCountToTextConverter`, `SortModeTooltipConverter`,
  `ViewModeGlyphConverter`, `BoolToGlyphConverter` (по 2), `UI/Services/PanelDefaults.cs:38` ("New Panel"),
  `UI/ViewModels/BaseViewModel.cs:53` (мёртвый код `$"File Count: …"`).
- Plural/составные: `ItemCountToTextConverter.cs:19`, `PanelSubtitleConverter.cs:46-47`,
  `MainWindowViewModel.cs:635-639`, `ChildWindow.ContentSizing.cs:187,217` `"(N panel(s))"`. Русский требует
  3 формы (1 / 2–4 / 5+).
- Enum-отображение: `EnumDisplayNameConverter` (switch, 13 значений; ресурс объявлен дважды —
  `Resources/Styles/Settings.xaml:20` и `PanelCustomizeWindow.xaml:27`); сырой `ToString()` для
  `SystemIconSize` (`SystemIconsSection.xaml:111`) и `PanelSurfaceStyle` (`PanelCustomizeWindow.xaml:76`);
  дубли строк в XAML для `PanelViewMode`/`PanelSortMode` (`PanelCustomizeWindow.xaml:107-154`,
  `ChildWindow.xaml:355,365`), `PanelSizePreset`/`PanelContentFilter`/`IconLayoutPolicy` в контекстном меню
  `ChildWindow.xaml:179-183,216-228,313-343`. Закрывает бэклог ROADMAP «общий EnumDisplayConverter»
  (`docs/ROADMAP.md:1230-1232`).
- Побочные эффекты культуры: `Core/Comparers/NaturalStringComparer.cs:75` — `CurrentCultureIgnoreCase`;
  `StringFormat` (`{0:P0}` в `PanelCustomizeWindow.xaml:73`, `NewPanelDefaultsSection.xaml:61`, дата в
  `BackupSection.xaml:43`) форматируется по en-US, т.к. `xml:lang`/`Language` не заданы.
- Ресурсы собираются в `App.xaml.cs:221-245` `LoadResourceDictionaries()` через `LoadComponent`;
  `App.xaml` без `Application.Resources` (запрещено комментарием). Порядок старта: `Program.cs:218` Load →
  `new App` → `Run` → `StartApp` → `LoadResourceDictionaries`.
- Тесты с assert на текст (~33 места): `EnumDisplayNameConverterTests`, `PanelSubtitleConverterTests`,
  `SortModeTooltipConverterTests`, `ViewModeGlyphConverterTests`, `ItemCountToTextConverterTests`
  (в `AmbientConverterTests.cs`), `RollUpTests.cs:94,108`, `PanelDefaultsTests.cs:133`,
  `PanelCustomizeViewModelTests.cs:214,250,254,465,581`, `PageManagerServiceTests.cs:201,222,282`,
  `MainWindowViewModelTests.cs:326,355,400`, `TrayIconManagerTests.cs` (константы `:23-25`, `:131-132,147`,
  поиск пунктов меню по тексту).
- Окно настроек: раздел `General` есть (`Views/Settings/GeneralSection.xaml`, карточки `Settings.CardBorder`
  /`CardHeader`/`CardDescription`) — место для выбора языка.
- Инсталлятор Inno Setup `installer/DesktopOrganizer.iss` уже ru/en (`[Languages]:92-94`,
  `[CustomMessages]:96-110`); русский `AppRunningCloseFailedRetry:106` ссылается на пункт «Выход», в трее
  он "Exit". `installer/build.ps1` без `SatelliteResourceLanguages` → publish тащит 13 языков фреймворка.
- Прежние решения «только английский»: ADR-031 D-8, ADR-023 §8, `Program.cs:24-37` — теперь отменяются
  этой задачей (зафиксировать amendment в новом ADR).

## Ограничения для конвейера

- 0 errors / 0 warnings (CLAUDE.md). Никаких `#pragma`/`NoWarn`/правок ruleset.
- P/Invoke только за `INativeApi`; `ChildWindowModel` — единый источник правды; битые пути не валят загрузку.
- Нативный/WPF-код не выполняется вне Windows-сессии агента — поведение помечать «manual verify (Win10/Win11)».
