# Публикация в Microsoft Store: технический план реализации

Дата: 2026-10-10. Ветка: `feature/store-publication`. Основание: [store-first-publication.md](store-first-publication.md)
(A1–A4, B2–B9, §7, §8). Scope в целом: **L**, по пакетам: P1 — M, P2 — S, P3 — S–M, P4 — M–L.

**Обязательные решения владельца:** D-1=A (Store-сборка с package identity хранит **весь** корень в
`%USERPROFILE%\DesktopOrganizer`, Inno-корень `%LOCALAPPDATA%\DesktopOrganizer` не меняется; это разрешение
касается **только пути корня**: новых persisted-полей, миграций и смены `schemaVersion`/`schemaRevision` нет);
D-2 (`<Version>1.0.0`); D-5 (`startupTask Enabled="false"`); код закрытый.

**Общий DoD для каждого пакета:** `dotnet build DesktopOrganizer.sln -c Release -p:TreatWarningsAsErrors=true`
и Debug-сборка — 0 errors / 0 warnings; `dotnet test` зелёный (база 6426 + новые тесты); без `#pragma`/
`[SuppressMessage]`/`NoWarn`; паритет resx en/ru сохранён (`ResxConsistencyTests`, `KeyUsageCoverageTests`,
`ResxTextLengthGuardTests`).

---

## Пакет 1 — A1: корень хранилища + identity-probe (M)

### Дизайн

1. **Probe (нативный слой).** P/Invoke-объявление остаётся в `NativeApi.cs` (вложенный `NativeMethods`):
   `[LibraryImport("kernel32.dll")] static partial int GetCurrentPackageFullName(ref uint length, IntPtr buffer)`.
   Вызов с `length = 0`, `buffer = IntPtr.Zero`, без `unsafe`. Код возврата интерпретирует чистая функция
   `internal static bool NativeApi.InterpretPackageProbe(int rc)`: `15700` (APPMODEL_ERROR_NO_PACKAGE) → `false`,
   `122` (ERROR_INSUFFICIENT_BUFFER) или `0` → `true`, любой другой код → `false`. Результат кешируется в
   статическом `Lazy<bool>` (`ExecutionAndPublication`), доступном как `internal static bool NativeApi.HasPackageIdentity`.
   `DllNotFoundException`/`EntryPointNotFoundException` → `false`. До DI логировать нечем, поэтому сырой код
   сохраняется в `internal static int NativeApi.PackageProbeResultCode`. После `AppLog.Initialize`
   `Program.Main` один раз пишет `Info("Program", "PackageIdentity", packaged, rc)`.
2. **Обёртки.** `INativeApi.IsPackagedProcess { get; }` возвращает статическое значение. Публичная абстракция
   для UI и тестов — `IAppPackageContext { bool IsPackaged { get; } }` в `Services.Abstractions/Interfaces`.
   Реализация `AppPackageContext(INativeApi)` лежит в `Services`, регистрируется singleton в
   `Services/Configuration/DiConfig.cs`. UI не видит `INativeApi`: интерфейс internal, а `AboutViewModel`
   публичный, так что внутренний тип в его конструкторе нарушает доступность.
3. **Задокументированное исключение из инварианта «P/Invoke только за INativeApi».** Единственный статический
   потребитель `NativeApi.HasPackageIdentity` вне `NativeApi.cs` — `AppStorageRoot`, потому что он вызывается до DI
   (`Program.cs:367`, `App.CrashLogDirectory`, `DiConfig.cs:293`, `StateStorePaths.cs:37`,
   `PanelItemStorageService.cs:123`, `PanelItemsUninstallRestorer.cs:24`, `AboutViewModel.cs:97`,
   `MainWindowViewModel.cs:521`). Сами объявления P/Invoke из `NativeApi.cs` никуда не уходят. Исключение
   фиксируется в remarks `AppStorageRoot` и в ADR-050, а guard-тест его закрепляет.
4. **Резолвер.** `internal static string AppStorageRoot.Resolve(bool isPackaged, bool isDebug, Func<Environment.SpecialFolder, string> getFolder)`:

   | identity | Debug | Корень |
   |---|---|---|
   | нет | нет | `%LOCALAPPDATA%\DesktopOrganizer` (как сейчас) |
   | нет | да | `%LOCALAPPDATA%\DesktopOrganizer.Dev` (как сейчас) |
   | есть | нет | `%USERPROFILE%\DesktopOrganizer` |
   | есть | да | `%USERPROFILE%\DesktopOrganizer.Dev` |

   Если при identity `UserProfile` пуст, используется fallback `LocalApplicationData`: корень с пустым
   префиксом хуже. Fallback покрыт тестом и описан в remarks как деградация. `Default` вычисляется один раз
   (`Lazy<string>`) из `Resolve(NativeApi.HasPackageIdentity, IsDebugBuild, Environment.GetFolderPath)`.
   Новые члены: `internal const bool IsDebugBuild`, `public static bool IsPackagedLocation`,
   `public static string PanelItemsDirectory`, `public static string LogsDirectory`. Они убирают пять копий
   литералов `"PanelItems"`/`"Logs"` в перечисленных call-sites (поведение то же). Константы
   `ReleaseFolderName`/`DebugFolderName`/`FolderName` не меняются.
5. **README.txt в корне.** Класс `internal static class StorageRootReadme` (`Services/Storage`):
   `FileName = "README.txt"`, `ShouldWrite(bool isPackaged, StateLoadStatus status, bool isWritable)` →
   `isPackaged && status == FirstRun && isWritable`, и `TryWrite(string root, IAppLog log)` с
   `FileMode.CreateNew`: файл никогда не перезаписывается, `IOException`/`UnauthorizedAccessException` пишутся в
   лог как `Warn` и проглатываются. Метод вызывает `Program.Main` сразу после `applicationState.Load()`. Признак
   `FirstRun` уже существует, нового флага нет. Inno-корень не трогается, так как `isPackaged == false`. Текст
   двуязычный en + ru и лежит embedded-ресурсом `Services/Storage/StorageRootReadme.txt`: файл пишется до выбора
   языка UI, поэтому resx не подходит. Содержание: что это за папка, что `PanelItems\` — это перемещённые файлы
   пользователя, что удаление или сброс приложения папку **не** удаляют, как вернуть файлы (перетащить элемент с
   панели на рабочий стол или в папку, ADR-036; «Вернуть» в Settings → Backup → «Files without a panel»; либо
   скопировать из `PanelItems\` вручную; Delete на элементе возвращает файл и сразу отправляет его в Корзину,
   поэтому «удалить элемент» как способ вернуть файл не рекомендуется), просьба не удалять папку при
   установленном приложении.
   Это не persisted-состояние: файл никто не читает, его нет в схеме и в бэкапах, удалять его безопасно.
6. **Константа TaskId для Пакетов 2 и 4** создаётся здесь (одна строка), чтобы Пакет 4 мог идти параллельно:
   `internal const string AutostartService.StartupTaskId = "DesktopOrganizerStartup"`. Точки в TaskId не
   ставим ради консервативной совместимости со схемой манифеста.

### Файлы

- Изменить: `src/DesktopOrganizer.Services/Tools/NativeApi.cs`, `.../Interfaces/INativeApi.cs`,
  `.../Storage/AppStorageRoot.cs`, `.../Storage/StateStorePaths.cs` (только doc),
  `.../Storage/StorageFileLock.cs` (doc), `.../Tools/PanelItemStorageService.cs`,
  `.../Tools/PanelItemsUninstallRestorer.cs`, `.../Configuration/DiConfig.cs`,
  `.../DesktopOrganizer.Services.csproj` (EmbeddedResource),
  `src/DesktopOrganizer.Services.Abstractions/Storage/IStorageLock.cs` (doc),
  `.../Interfaces/IPanelItemsUninstallRestorer.cs` (doc), `src/DesktopOrganizer.UI/Program.cs`,
  `App.xaml.cs`, `Configuration/DiConfig.cs` (doc), `ViewModels/AboutViewModel.cs`, `MainWindowViewModel.cs`,
  `ChildWindowViewModel.cs` (doc), `Services/WindowManager.cs` (комментарий), `Services/AutostartService.cs`
  (константа).
- Создать: `Services.Abstractions/Interfaces/IAppPackageContext.cs`, `Services/Tools/AppPackageContext.cs`,
  `Services/Storage/StorageRootReadme.cs`, `Services/Storage/StorageRootReadme.txt`.
- XML-doc в `src` с упоминанием `%LOCALAPPDATA%\DesktopOrganizer` (≈18 мест, найдены Grep) правит
  **реализующий агент этого пакета**: формулировка «корень хранилища (`AppStorageRoot.Default`: Inno —
  `%LOCALAPPDATA%\…`, Store — `%USERPROFILE%\…`)».

### Тесты

- `Unit/Storage/AppStorageRootTests.cs`: тест `Default_IsFolderNameUnderLocalApplicationData` (стр. 54–64)
  заменяется на `Default_EqualsResolveForThisProcess` (тестовый хост без identity, так что ожидается
  LocalAppData). Новые `[Theory]` по таблице резолвера (4 кейса, `getFolder` — фейк со значениями `L:\`/`U:\`),
  `Resolve_PackagedWithEmptyUserProfile_FallsBackToLocalAppData`, `Default_IsStableAcrossCalls`,
  `PanelItemsDirectory/LogsDirectory_DeriveFromDefault`. Тесты `StateStorePaths`/`PanelItemStorageService`
  (стр. 71–93) остаются без изменений.
- `Unit/Native/PackageIdentityProbeTests.cs`: `[Theory]` для `InterpretPackageProbe` (15700, 122, 0, 5, -1),
  `HasPackageIdentity_IsFalseInTestHost`, `IsPackagedProcess_MatchesStatic`.
- `Unit/Services/AppPackageContextTests.cs`: делегирует в мок `INativeApi`.
- `Unit/Storage/StorageRootReadmeTests.cs`: `ShouldWrite` — полная таблица (packaged × 7 значений `StateLoadStatus` ×
  writable), `TryWrite` создаёт файл с маркерами en и ru и словом `PanelItems`, не перезаписывает существующий,
  при занятом файле не бросает и пишет `Warn` (мок `IAppLog`).
- `Unit/Native/PackageProbeUsageGuardTests.cs` (source-scan по `src/`): `GetCurrentPackageFullName` встречается
  только в `NativeApi.cs`, а `NativeApi.HasPackageIdentity` — только в `NativeApi.cs` и `AppStorageRoot.cs`.
- `Integration/Services/DiConfigTests.cs`: `IAppPackageContext` резолвится как singleton.

### DoD

Общий DoD; все потребители корня берут путь из `AppStorageRoot`, литералов `"PanelItems"`/`"Logs"` вне него
нет; под Inno поведение побайтно прежнее; B7 закрывается сам («Открыть папку», crash-диалог, Orphans).

### Риски

- **Нативное, вне Windows не проверяется:** под identity probe возвращает `true`, а запись в `%USERPROFILE%` не
  виртуализируется. Проверяется только sideload-ом (manual verify).
- В профиле пользователя уже есть посторонняя папка `DesktopOrganizer`. Mitigation: README и подпапки, данные
  пишутся только в `State\`/`PanelItems\`/`Logs\`.
- Sparse/external-location пакеты тоже имеют identity, но не виртуализируются; выбор корня `%USERPROFILE%` для
  них корректен.
- Если стоят Inno и Store одновременно, у них два разных корня (C2, roadmap).

---

## Пакет 2 — A2: автозапуск под identity (S)

### Дизайн

- `IAutostartService` получает `bool IsManagedBySystem { get; }` и `void OpenSystemStartupSettings()`. Отдельный
  enum `Mode` не нужен, состояний только два.
- `AutostartService(IAppPackageContext packageContext, IExternalLinkLauncher linkLauncher, IAppLog? appLog = null)`.
  Под identity `IsManagedBySystem = true`: getter `IsEnabled` возвращает `false` **без обращения к реестру**,
  setter ничего не делает и пишет `Warn("AutostartService", "WriteIgnoredPackaged")`. Без identity поведение и
  контракт с `.iss` прежние (`InstallerAutostartContractTests` не меняются).
- `IExternalLinkLauncher.OpenStartupAppsSettings()` — отдельный метод без параметров:
  `private const string StartupAppsSettingsUri = "ms-settings:startupapps"`, тот же `LaunchViaShell`.
  `OpenUrl` по-прежнему пропускает только https. Произвольный `ms-settings:` через публичный API открыть нельзя:
  whitelist получается самой конструкцией метода.
- UI: в `GeneralSection.xaml` две карточки с взаимоисключающей `Visibility`. Toggle остаётся при
  `!AutostartManagedBySystem`. Вторая карточка (тот же Header) — описание
  `Settings_General_Autostart_Managed_Description` и кнопка-ссылка (`Theme.Button.Link`)
  `Settings_General_Autostart_OpenSystemSettings_Button`. `MainWindowViewModel` получает
  `AutostartManagedBySystem => _autostart.IsManagedBySystem` и `OpenStartupSettingsCommand`;
  **конструктор VM не меняется**, всё идёт через уже внедрённый `IAutostartService`.
- Тексты en/ru: «Windows управляет автозапуском приложений из Microsoft Store. Включите DesktopOrganizer в
  Параметры → Приложения → Автозагрузка.» / кнопка «Открыть параметры автозагрузки».

### Файлы

Изменить: `UI/Services/IAutostartService.cs`, `AutostartService.cs`, `IExternalLinkLauncher.cs`,
`ExternalLinkLauncher.cs`, `ViewModels/MainWindowViewModel.cs`, `Views/Settings/GeneralSection.xaml`,
`Localization/Strings.resx`, `Strings.ru.resx`. Проверить `UI/Configuration/DiConfig.cs` (DI сам подхватит
новый конструктор).

### Тесты

- `AutostartServiceTests` (новый или расширенный): под identity `IsManagedBySystem`, `IsEnabled == false`,
  set не создаёт значение в HKCU (проверка на изолированном value name в Debug, как в существующих тестах) и
  логирует `Warn`; без identity поведение прежнее; `OpenSystemStartupSettings` вызывает
  `OpenStartupAppsSettings` ровно один раз.
- `ExternalLinkLauncherTests`: `OpenUrl("ms-settings:startupapps")` по-прежнему отклоняется (регрессия M4);
  константа URI равна `ms-settings:startupapps`.
- `MainWindowViewModelTests`: `AutostartManagedBySystem` проксирует сервис; команда вызывает сервис.
- `AppStorageRootTests.AutostartValueNames…` не меняется.

### DoD

Общий DoD; под identity нет ни одной записи в HKCU\Run и нет toggle-а; resx en/ru в паритете.

### Риски

`ms-settings:startupapps` есть на Win10 1903+ и Win11 (MinVersion 19041 это покрывает). Задача регистрируется
только после первого запуска приложения, поэтому до него в списке «Автозагрузка» её нет. Это ограничение
платформы, оно попадает в Notes for certification.

---

## Пакет 3 — A4, B2, B4, B5, B6, B9 (S–M)

### Дизайн

- **A4.** `AboutViewModel(…, IAppPackageContext packageContext, IAppLog? appLog = null)`, новое свойство
  `IsUpdateCheckAvailable => !packageContext.IsPackaged`. В `AboutSection.xaml` на `Border` карточки Updates
  ставится `Visibility="{Binding About.IsUpdateCheckAvailable, Converter={StaticResource BooleanToVisibility}}"`.
  `CheckForUpdatesAsync` под identity сразу выходит (defense in depth: сетевого вызова нет в принципе).
- **B2.** Новый член `ITrayIconManager.ShowInfoBalloon(string title, string text)`, реализация в `TrayIconManager`
  через `_notifyIconAdapter.ShowBalloonTip(10000, …, ToolTipIcon.Info)`. Политика живёт в UI:
  `UI/Services/FirstRunTrayNotice.cs` по образцу `ReadOnlySessionNotice`. Условие:
  `LastLoadStatus == StateLoadStatus.FirstRun && stateStore.IsWritable`, показ не чаще раза за сессию, сам факт
  показа **никуда не сохраняется**. Вызов из `App.StartApp` сразу после `ReadOnlySessionNotice.ShowIfReadOnly()`.
  Работает в обоих каналах. Ключи `FirstRun_TrayBalloon_Title` и `FirstRun_TrayBalloon_Message`: «DesktopOrganizer
  работает в области уведомлений. Щёлкните значок правой кнопкой: Настройки и Выход.»
- **B4.** `Hint_Move_Message` становится форматом с `{0}`, куда подставляется `AppStorageRoot.PanelItemsDirectory`:
  en «The file was moved from the desktop into the panel. It is kept in {0}.», ru — аналогично. Новый ключ
  `Hint_Move_PackagedRetention` («It stays there even if you uninstall the app.») показывается **только** при
  `AppStorageRoot.IsPackagedLocation`: Inno-деинсталлятор возвращает файлы на рабочий стол, и там эта фраза
  была бы ложью. Сборку текста выносим в чистую функцию `MoveHintText.Compose(ILocalizationSource, string folder, bool isPackaged)`.
  `HintPopupController.ShowMoveHint()` использует её, папку и флаг получает в конструкторе из
  `ChildWindow.xaml.cs` через статический `AppStorageRoot`. В `ChildWindow.xaml:733` статический
  `Text="{loc:Loc Hint_Move_Message}"` убираем, иначе в разметке останется `{0}`. Однократность по-прежнему
  обеспечивает существующий `HasShownMoveHint`.
- **B5.** `Settings_About_Diagnostics_Message` переписываем без CLI и env (одна формулировка для обоих каналов):
  «Log files help diagnose problems. Open the logs folder below and attach the latest file to your report.»
  `Settings_General_ResetHints_Description`: «Shows the one-time tip about moving files again the next time you
  drop a file into a panel.» Русские версии делаем аналогично, слов «Move-semantics» и «--diag» в тексте нет.
- **B6 (ждёт URL владельца).** В `UpdateEndpoints` добавляем `public const string PublicSiteUrl =
  "https://github.com/messire"` с пометкой `// OWNER: replace with AP2 public page`, `SupportUrl = PublicSiteUrl`
  и `PrivacyPolicyUrl = PublicSiteUrl` (последняя — для листинга и будущих ссылок). Получается
  `ProjectPageUrl => PublicSiteUrl`, а «Report a problem» ведёт на `SupportUrl` вместо `IssuesUrl` приватного
  репозитория (сейчас 404). Когда URL станет известен, меняется одна константа. `IssuesUrl`/`ReleasesUrl`/
  `TrustedPathPrefix` остаются для Inno-проверки обновлений.
- **B9.** `CHANGELOG.md`: раздел `[Unreleased]` со статьями Added/Changed (Store-канал, корень хранилища, автозапуск
  через Параметры, balloon, тексты) и **Known limitations**. В limitations идут: P1 (сброс настроек `user.config`
  при каждом обновлении из Store), скрытые иконки после удаления пакета при работающем приложении (лечится
  перезапуском Explorer или входом в систему), одновременная установка Inno и Store (C2), автозапуск только
  через Параметры (C1). На релизе раздел переименовывается в `[1.0.0]` по RC-конвенции.

### Файлы

Изменить: `UI/ViewModels/AboutViewModel.cs`, `Views/Settings/AboutSection.xaml`,
`Services.Abstractions/Interfaces/ITrayIconManager.cs`, `Services/Providers/TrayIconManager.cs`,
`UI/App.xaml.cs`, `UI/Configuration/DiConfig.cs` (регистрация `FirstRunTrayNotice`),
`UI/Behaviors/HintPopupController.cs`, `UI/Views/ChildWindow.xaml`, `ChildWindow.xaml.cs`,
`Services/Updates/UpdateEndpoints.cs`, `Localization/Strings.resx`, `Strings.ru.resx`, `CHANGELOG.md`.
Создать: `UI/Services/FirstRunTrayNotice.cs`, `UI/Behaviors/MoveHintText.cs`. Тестовые фейки
`ITrayIconManager`, если они есть, дополняются новым членом.

### Тесты

- `AboutViewModelTests`: `IsUpdateCheckAvailable` для true/false; под identity `CheckForUpdatesAsync` не
  вызывает `IUpdateCheckService` (`Times.Never`); `ReportProblemCommand` открывает `SupportUrl`.
- `FirstRunTrayNoticeTests`: показ при `FirstRun` и writable; нет показа для Loaded, NewerSchema, Quarantined,
  RecoveredFromBackup и не-writable; повторный вызов ничего не делает; используются локализованные ключи.
- `TrayIconManagerTests`: `ShowInfoBalloon` пробрасывает параметры в `INotifyIconAdapter.ShowBalloonTip`.
- `MoveHintTextTests`: путь подставлен; фраза о сохранности после удаления есть только при packaged; en и ru.
- `UpdateEndpointsTests`: `PublicSiteUrl`/`SupportUrl`/`PrivacyPolicyUrl` — абсолютные https;
  `ProjectPageUrl == PublicSiteUrl`.
- `ResxConsistencyTests` проверяет паритет плейсхолдеров `{0}` в en/ru; source-scan на отсутствие `--diag` и
  `Move-semantics` в значениях resx (en и ru).

### DoD

Общий DoD; под identity карточка Updates скрыта и сетевых вызовов нет; balloon появляется ровно при `FirstRun`;
новых persisted-полей и настроек `IAppSettings` нет; CHANGELOG содержит Known limitations.

### Риски

На Win11 balloon показывается как toast, Focus Assist может его подавить. Это допустимо, но AP3 на balloon не
опирается. Длинный путь в popup шириной 260 px переносится по строкам; при необходимости увеличить длительность
показа. URL для B6 пока нет, текущие значения временные (не 404).

---

## Пакет 4 — A3: упаковка MSIX без Visual Studio (M–L)

### Дизайн

- **Без `.wapproj`, в `DesktopOrganizer.sln` ничего не добавляется.** Используется ручной манифест +
  `makepri.exe` + `makeappx.exe`.
- **`installer/msix/AppxManifest.xml`** — шаблон, валидный XML с токенами `$IDENTITY_NAME$`, `$PUBLISHER$`,
  `$PUBLISHER_DISPLAY_NAME$`, `$VERSION$` в атрибутах. Содержимое:
  - `Identity ProcessorArchitecture="x64"`;
  - `TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0"`;
  - `Resources`: `en-US`, `ru-RU`;
  - `Application Id="DesktopOrganizer" Executable="DesktopOrganizer.UI.exe" EntryPoint="Windows.FullTrustApplication"`.
    Выбран классический Desktop Bridge вариант. Альтернатива `uap10:RuntimeBehavior="packagedClassicApp"
    uap10:TrustLevel="mediumIL"` эквивалентна, но реже встречается в документации к MakeAppx. Используем одно из
    двух, не оба;
  - `uap:VisualElements` с `DisplayName`, `Description`, `BackgroundColor="transparent"`, `Square150x150Logo`,
    `Square44x44Logo`, `uap:DefaultTile Wide310x150Logo`; `Properties/Logo = Assets\StoreLogo.png`;
  - `desktop:Extension Category="windows.startupTask" Executable="DesktopOrganizer.UI.exe"
    EntryPoint="Windows.FullTrustApplication"` → `desktop:StartupTask TaskId="DesktopOrganizerStartup"
    Enabled="false" DisplayName="DesktopOrganizer"`;
  - `Capabilities`: только `rescap:runFullTrust`.
- **Identity.** Файл `installer/msix/identity.dev.psd1` (Name `DesktopOrganizer.Dev`, Publisher
  `CN=DesktopOrganizer Dev`) используется для локального sideload. Файл `installer/msix/identity.store.psd1`
  владелец заполнит после AP1, посимвольно из Partner Center; это не секрет. Скрипт выбирает файл параметром
  `-Identity Dev|Store`.
- **`installer/build-msix.ps1`**:
  1. `param([ValidateSet("Release")] $Configuration = "Release", [ValidateSet("Dev","Store")] $Identity = "Dev", [switch]$Sign, [switch]$RunWack, [string]$SdkBinDir)`.
  2. Вызов `& "$PSScriptRoot\build.ps1" -Configuration Release -SkipInstaller`: публикация self-contained win-x64
     с проверкой сателлитов переиспользуется, а не дублируется.
  3. Layout `installer/msix/layout/`: копия `installer/publish/win-x64` **без `*.pdb`** плюс `Assets\`.
  4. Версия берётся из `(Get-Item layout\DesktopOrganizer.UI.exe).VersionInfo.FileVersion`, как в `.iss`.
     Проверка `^[1-9]\d*\.\d+\.\d+\.0$`, иначе `throw`.
  5. Манифест собирается через `[xml]` DOM (`SetAttribute`), а не строковой заменой: `Publisher` с запятыми и
     кавычками экранируется автоматически. В выходном манифесте не должно остаться `$…$`.
  6. SDK: автопоиск новейшего `Windows Kits\10\bin\10.0.*\x64` с `makeappx.exe` и `makepri.exe`; локально это
     10.0.26100.0. Номер не хардкодится (на CI образ может отличаться).
  7. PRI: `makepri createconfig /cf … /dq en-US /pv 10.0.0`, затем `makepri new` по **staging-папке только с
     `AppxManifest.xml` + `Assets\`**. Папка `ru\` с сателлитами .NET не должна индексироваться как языковой
     квалификатор. Получившийся `resources.pri` копируется в layout. PRI обязателен: без него варианты
     `scale-*`/`targetsize-*` не резолвятся.
  8. `makeappx pack /d layout /p installer/msix/Output/DesktopOrganizer_<ver>_x64.msix /o`, проверка `$LASTEXITCODE`.
  9. `-Sign` (только с `-Identity Dev`): создаётся или переиспользуется self-signed сертификат с
     `Subject == Publisher` (EKU 1.3.6.1.5.5.7.3.3) в `Cert:\CurrentUser\My`, затем
     `signtool sign /fd SHA256 /sha1 <thumb>`. Импорт в `LocalMachine\TrustedPeople` требует прав
     администратора: скрипт печатает инструкцию и сам не импортирует. Store-пакет не подписывается, это делает Store.
  10. `-RunWack`: `appcert.exe test -appxpackagepath … -reportoutputpath Output\wack-report.xml` (нужен админ).
      Если appcert не найден — предупреждение, не ошибка.
  11. В Partner Center загружается `.msix` напрямую; `.msixupload` не нужен.
- **Ассеты.** Скрипт `installer/msix/New-MsixAssets.ps1` запускается один раз, результат коммитится. Он
  читает `src/DesktopOrganizer.UI/Resources/app.ico` разбором ICONDIR и берёт PNG-кадр 256×256, без
  `Icon.ToBitmap`. Ресайз через System.Drawing с HighQualityBicubic в 32bppArgb на прозрачном фоне. Наборы:
  - `StoreLogo` — scale-100/125/150/200/400 (50/63/75/100/200 px, полный кадр);
  - `Square150x150Logo` — scale-100/125/150/200/400 (150…600 px, иконка 66 % по центру);
  - `Square44x44Logo` — scale-100…400 (44…176 px), а также `targetsize-16/24/32/48/256` и те же
    `targetsize-N_altform-unplated` (полный кадр);
  - `Wide310x150Logo` — scale-100/200 (310×150 и 620×300, иконка на 66 % высоты по центру).

  Каждый файл меньше 200 КБ. Результат в `installer/msix/Assets/`.
- **Метаданные (D-2, закрытый код):** в `Directory.Build.props` ставится `<Version>1.0.0</Version>`, из
  `<Description>` убирается «open-source» (остаётся «A free Windows desktop organizer…»). В `README.md:3`
  тоже «A free Windows desktop organizer…». `LICENSE` остаётся.
- **CI.** Новый job `msix` в `.github/workflows/ci.yml`: `needs: build-and-test`,
  `if: github.event_name != 'pull_request'`, `runs-on: windows-latest`, setup-dotnet 8,
  `pwsh ./installer/build-msix.ps1 -Identity Dev` без `-Sign`, затем `upload-artifact`
  `installer/msix/Output/*.msix` (retention 30 дней). `permissions: contents: read`.
- **`.gitignore`:** добавить `installer/msix/layout/`, `installer/msix/Output/`, `installer/msix/staging/`.
  Правило `*.msix` уже есть.

### Файлы

Создать: `installer/msix/AppxManifest.xml`, `identity.dev.psd1`, `identity.store.psd1` (с плейсхолдерами
`TODO-AP1`), `New-MsixAssets.ps1`, `Assets/*.png` (≈36 файлов), `installer/build-msix.ps1`.
Изменить: `Directory.Build.props`, `README.md`, `.gitignore`, `.github/workflows/ci.yml`.

### Тесты (контракты, `tests/…/Unit/Installer/MsixManifestContractTests.cs`; поиск корня — как `FindRepoRoot` в `InstallerMetadataContractTests`)

1. Шаблон парсится как XML, корневой namespace — foundation/windows10; `Identity/@Version == "$VERSION$"` (не захардкожен).
2. `StartupTask/@TaskId == AutostartService.StartupTaskId`, `@Enabled == "false"`; `Executable` у Application
   и у Extension равен `typeof(App).Assembly.GetName().Name + ".exe"`.
3. Capabilities — ровно `runFullTrust`: нет `unvirtualizedResources` и других rescap.
4. `TargetDeviceFamily` — `Windows.Desktop`, `MinVersion` 10.0.19041.0; `ProcessorArchitecture="x64"`.
5. Языки `Resources` соответствуют `<SatelliteResourceLanguages>en;ru` (en-US, ru-RU).
6. Версия: `FileVersionInfo` сборки UI соответствует `^[1-9]\d*\.\d+\.\d+\.0$`, то есть revision 0 и major ≥ 1 (D-2).
7. Ассеты: на каждое лого из манифеста есть минимум вариант scale-100; полный набор targetsize, включая
   altform-unplated; каждый PNG (сигнатура) меньше 200 КБ; размеры из IHDR совпадают с ожидаемыми, без System.Drawing.
8. Отсутствие Debug-артефактов: в тексте `build-msix.ps1` есть `ValidateSet("Release")` и исключение `*.pdb`;
   `DesktopOrganizer.sln` не ссылается на `*.wapproj`.

### DoD

Общий DoD; `installer/build-msix.ps1 -Identity Dev -Sign` локально собирает `.msix` (`makeappx` возвращает 0);
CI job выкладывает артефакт; WACK (`-RunWack`, по возможности) без FAILED. Store-identity подставляется после AP1.

### Риски

- **Нативное и непроверяемое в CI:** установка, запуск под identity, startupTask, WACK.
- `makepri /dq` с двумя языками: синтаксис и влияние на список языков в листинге проверить через `makepri dump`
  и WACK; fallback — `en-US`.
- Апскейл 256 → 396 px для `Square150x150Logo.scale-400` даёт мягкость. Допустимо; в roadmap — растеризация `app.svg`.
- Набор Windows SDK на `windows-latest` может отличаться: работает автопоиск, при отсутствии `makepri` —
  явная ошибка.
- `build.ps1` подписывает exe/dll, если задан `DESKTOPORGANIZER_SIGN_CERT`. Это безвредно; в CI-job `msix` переменная не задаётся.

---

## Порядок и параллелизм

1. **Пакет 1** — первым и последовательно: `win32-interop-specialist` (probe в `NativeApi`), затем
   `dotnet-services-engineer` (резолвер, `IAppPackageContext`, README, DI, XML-doc).
2. После merge Пакета 1 — **параллельно**:
   - **Пакеты 2 и 3** — один агент `wpf-ui-builder`, последовательно: P2, затем P3. Оба правят
     `Strings.resx`/`Strings.ru.resx` и `AboutViewModel`/DI.
   - **Пакет 4** — `dotnet-services-engineer` (скрипты, ассеты, CI). Пересечение по файлам с P2/P3 отсутствует.
     Константа TaskId уже есть из P1.
   - Параллельные агенты работают в отдельных git worktree, иначе чужая красная сборка блокирует сборку
     соседнего агента.
3. `tdd-test-engineer` — тесты каждого пакета (TDD до реализации там, где есть чистые функции: `Resolve`,
   `InterpretPackageProbe`, `ShouldWrite`, `MoveHintText`, `FirstRunTrayNotice`).
4. Ревью: `code-quality-reviewer` ∥ `fences-ux-parity-reviewer`.
5. `architecture-documenter`:
   - **ADR-050** «Канал Microsoft Store (MSIX)»: probe и исключение из инварианта, таблица корней, README,
     автозапуск через Параметры, скрытие Updates, упаковка без `.wapproj`, TaskId.
   - **Amendments:**
     - ADR-024 — корень по identity, без изменения схемы, со ссылкой на D-1;
     - ADR-009 — расположение `PanelItems` и сохранность при uninstall/Reset;
     - ADR-041 — в MSIX `--restore-panel-items` нет, данные остаются в `%USERPROFILE%`;
     - ADR-027 — путь `Logs`;
     - ADR-038 — MSIX-канал, CI job, `1.0.0`.
   - **Документы:** README (≈10 упоминаний путей, Store-раздел — минимально), `installer/README.md` (MSIX),
     `docs/Refactoring/Testing/diagnostics-log-events.md` (событие `PackageIdentity`), ROADMAP, ARTIFACT_INDEX.
   - **CLAUDE.md:** строку «путей в `%LOCALAPPDATA%\DesktopOrganizer`» оркестратор предлагает обновить пользователю
     и не правит сам.

## Ручная проверка на Windows 10/11 (владелец)

Удалить Inno-сборку. Выполнить `build-msix.ps1 -Identity Dev -Sign`, импортировать сертификат в TrustedPeople,
`Add-AppxPackage`. Затем:

1. Первый запуск: появляется balloon, в `%USERPROFILE%\DesktopOrganizer` лежит `README.txt`.
2. Перетащить файл в панель: hint с путём; файл виден в Explorer и вытаскивается обратно.
3. Uninstall и Settings → Apps → Reset не трогают папку.
4. Автозапуск: карточка со ссылкой открывает «Автозагрузку»; после включения приложение стартует при входе.
5. Карточка Updates скрыта.
6. «Открыть папку логов/хранилища», crash-диалог и Orphans открывают `%USERPROFILE%\…`.
7. Сценарий AP3 пройден.

---

### Recommended Next Actions

1. CRITICAL: `win32-interop-specialist` — Пакет 1, probe `GetCurrentPackageFullName` в `NativeApi` и guard-тест.
2. CRITICAL: `dotnet-services-engineer` — Пакет 1: резолвер, `IAppPackageContext`, `StorageRootReadme`, DI,
   XML-doc. После Пакета 1 — Пакет 4 (параллельно с P2/P3).
3. CRITICAL: `wpf-ui-builder` — Пакеты 2, затем 3 (после Пакета 1).
4. RECOMMENDED: `tdd-test-engineer` — тесты по спискам выше, TDD для чистых функций.
5. RECOMMENDED: `architecture-documenter` — ADR-050 и amendments ADR-009/024/027/038/041, документы.
6. OPTIONAL: `fences-ux-parity-reviewer` — тексты hint, balloon и карточки автозапуска.
