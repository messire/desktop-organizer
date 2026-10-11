# ADR-050: Канал Microsoft Store (MSIX, package identity)

**Статус:** Accepted (2026-10-10) — реализовано на ветке `feature/store-publication` (пакеты 1–4 [плана реализации](../docs/PLANS/store-publication-implementation.md)); критерий плана — 0 errors / 0 warnings, тесты зелёные (прогон — на оркестраторе), проверка установки и запуска под identity — только вручную на Windows 11 (не блокер). Amends [ADR-009](ADR-009-drag-drop-move-semantics.md), [ADR-024](ADR-024-versioned-json-persistence.md) (только путь корня хранилища), [ADR-027](ADR-027-diagnostics-logging.md), [ADR-038](ADR-038-release-engineering.md), [ADR-041](ADR-041-first-run-panel-and-uninstall-restore.md)
**Дата:** 2026-10-10
**Источник:** анализ готовности [docs/PLANS/store-first-publication.md](../docs/PLANS/store-first-publication.md) (v4, решения владельца D-1…D-6 в §7), технический план [docs/PLANS/store-publication-implementation.md](../docs/PLANS/store-publication-implementation.md), отчёты ревью (учтены в коде).

---

## Контекст

До этого решения приложение распространялось одним способом: Inno-инсталлятор ([ADR-038](ADR-038-release-engineering.md)). Цель — второй канал, Microsoft Store.

### Почему MSIX

Путь «EXE/MSI-инсталлятор в Store» (политика 10.2.9) требует подписи инсталлятора и всех PE-файлов сертификатом из Microsoft Trusted Root Program, и Store такие пакеты **не переподписывает**. Сертификата у проекта нет. MSIX Store подписывает сам, поэтому это единственный реалистичный путь без покупки сертификата.

### Что ломает MSIX-песочница

Приложение запускается как full-trust Desktop Bridge (`runFullTrust`), но файловая система и реестр для него виртуализируются, и две вещи, составляющие суть продукта, перестают работать:

1. **Хранилище.** Файлы, которые пользователь перетащил в панель, **перемещаются** в `PanelItems\` ([ADR-009](ADR-009-drag-drop-move-semantics.md)), пути в состоянии абсолютные. Под `%LOCALAPPDATA%` процесса с identity новые файлы попадают в `%LOCALAPPDATA%\Packages\<PFN>\LocalCache\Local\…`: их не видят Explorer и процессы без identity (drag-out, уже запущенный Word, «Открыть папку», out-of-proc shell-глаголы), а Uninstall и Settings → Apps → Reset стирают LocalCache вместе с файлами пользователя. Единственная защита от потери в Inno-канале — `--restore-panel-items` в `[UninstallRun]` ([ADR-041](ADR-041-first-run-panel-and-uninstall-restore.md)), которой в MSIX нет.
2. **Автозапуск.** Запись `HKCU\…\Run` виртуализируется, Explorer её не видит, а переключатель «Запускать при старте Windows» показывает «включено». Это вводящий в заблуждение UI.

Capability `unvirtualizedResources` (отключение виртуализации) — restricted: по документации Microsoft она предназначена для отдельных игровых сценариев и не рассматривается для других приложений.

## Рассмотренные альтернативы

| Вариант | Итог |
|---|---|
| Хранить корень в `%LOCALAPPDATA%` и в Store-сборке | Отвергнут: потеря данных и невидимость файлов (выше) |
| Корень в `Documents` / `Desktop` | Отвергнут: Controlled Folder Access защищает Documents/Pictures/Videos, OneDrive Known Folder Move переносит Desktop/Documents/Pictures; корень профиля не трогает ни то, ни другое, `CloudPathClassifier` остаётся корректным |
| `unvirtualizedResources` | Отвергнут: restricted capability, не путь для первого релиза |
| Copy вместо Move | Отвергнут: закрыто решением пользователя ([ADR-009](ADR-009-drag-drop-move-semantics.md), Amendment 7) |
| EXE/MSI в Store | Отвергнут: требует Trusted Root-сертификата (см. выше) |
| Упаковка через `.wapproj` | Отвергнут: не собирается `dotnet build` (нужны DesktopBridge-таргеты msbuild/VS), а CI гоняет `dotnet build -p:TreatWarningsAsErrors=true` по всему sln |
| Полная интеграция WinRT `StartupTask` | Отложена (C1): нужен TFM `net8.0-windows10.0.19041.0` на весь solution |

## Решение

### D-1. Identity-probe: `GetCurrentPackageFullName`, статический `Lazy`, одно исключение из инварианта

- P/Invoke объявлен в `NativeApi.cs` (вложенный `NativeMethods`, `LibraryImport("Kernel32.dll")`: `GetCurrentPackageFullName(ref uint, IntPtr)`), вызывается с нулевой длиной и нулевым буфером. Интерпретация — чистая функция `NativeApi.InterpretPackageProbe(int rc)`: коды **0** (`ERROR_SUCCESS`) и **122** (`ERROR_INSUFFICIENT_BUFFER`, ожидаемый ответ для нулевого буфера) ⇒ упакован; **15700** (`APPMODEL_ERROR_NO_PACKAGE`) и **любой иной код** ⇒ не упакован (безопасный дефолт — раскладка Inno, работавшая до канала Store). `DllNotFoundException`/`EntryPointNotFoundException`/любое неожиданное исключение (кроме `OutOfMemoryException`) ⇒ не упакован, код `PackageProbeUnavailableCode = -1`.
- Результат — статический `Lazy<(bool IsPackaged, int ResultCode)>` (`ExecutionAndPublication`), проба выполняется один раз за процесс и никогда не бросает.
- Публичная поверхность для остального кода: `INativeApi.IsPackagedProcess` и `IAppPackageContext.IsPackaged` (`Services.Abstractions`, реализация `AppPackageContext(INativeApi)`, DI — singleton в `Services/Configuration/DiConfig.cs`). UI видит только `IAppPackageContext` (`INativeApi` — internal).

**Задокументированное исключение из инварианта «P/Invoke только за `INativeApi`».** Корень хранилища нужен до построения DI (`Program.Main`: crash/restore-ветки, `App.CrashLogDirectory`, конструкторы по умолчанию хранилищ), а `INativeApi` до этого момента получить нельзя. Поэтому `NativeApi.HasPackageIdentity` (статический кеш пробы) читает **ровно одно место вне `NativeApi.cs`** — `AppStorageRoot`. Тот же класс экспонирует сырой код пробы `AppStorageRoot.PackageProbeResultCode` как pre-DI фасад для единственной диагностической строки. Сами P/Invoke-объявления из `NativeApi.cs` никуда не уходят. Исключение закреплено `PackageProbeUsageGuardTests` (source-scan `src/` без комментариев):

| Символ | Допустим только в |
|---|---|
| `GetCurrentPackageFullName` | `NativeApi.cs` |
| `HasPackageIdentity` | `NativeApi.cs`, `AppStorageRoot.cs` |
| `PackageProbeResultCode` (без префикса `AppStorageRoot.`) | `NativeApi.cs`, `AppStorageRoot.cs` |
| `IsPackagedLocation` | `AppStorageRoot.cs`, `Program.cs`, `HintPopupController.cs`, `ChildWindow.xaml.cs` |

### D-2. Корень хранилища по каналу — `AppStorageRoot.Resolve`

`AppStorageRoot.Default` вычисляется один раз (`Lazy`) из `ResolveLocation(NativeApi.HasPackageIdentity, IsDebugBuild, Environment.GetFolderPath)`. Папка выбирается по конфигурации сборки (как и прежде, [ADR-024](ADR-024-versioned-json-persistence.md) Amendment 2026-09-25), родитель — по каналу:

| Identity | Debug | Корень |
|---|---|---|
| нет (Inno, разработка) | нет | `%LOCALAPPDATA%\DesktopOrganizer` (как раньше) |
| нет | да | `%LOCALAPPDATA%\DesktopOrganizer.Dev` (как раньше) |
| есть (Store) | нет | `%USERPROFILE%\DesktopOrganizer` |
| есть | да | `%USERPROFILE%\DesktopOrganizer.Dev` |

- **Fallback.** Если при identity `UserProfile` пуст или пробелы, используется `%LOCALAPPDATA%`: корень с пустым префиксом (относительный путь) хуже виртуализированного. Тогда `IsPackagedLocation == false`.
- **`IsPackagedLocation`** (`public static bool`) — «корень реально лежит под `%USERPROFILE%`», то есть файл переживёт удаление пакета. Он `false` и для Inno, и для упакованного процесса, откатившегося на `%LOCALAPPDATA%`. Именно он, а не `IsPackaged`, решает, можно ли утверждать «файл остаётся после удаления» (README, Move-hint).
- Новые члены `AppStorageRoot`: `PanelItemsDirectory`, `LogsDirectory` (убрали пять копий литералов `"PanelItems"`/`"Logs"`), `internal const bool IsDebugBuild`, `IsPackagedLocation`, `PackageProbeResultCode`, `internal Resolve`/`ResolveLocation`. Константы `ReleaseFolderName`/`DebugFolderName`/`FolderName` не менялись. Корень закрывает и дефект «Открыть папку хранилища/логов», crash-диалога и Orphans, открывавших пустой виртуальный `%LOCALAPPDATA%`.
- Лестница `State\` / `PanelItems\` / `Logs\` / `.storage-lock` под корнем идентична в обоих каналах; **одна установка использует только один корень**. Миграции данных между каналами нет (Store-пользователей до релиза не существует); Inno-корень не трогается.

**Граница разрешения (замороженный контур, `CLAUDE.md`).** Путь корня входит в замороженный контур persistence ([ADR-024](ADR-024-versioned-json-persistence.md)). Владелец дал явное разрешение 2026-10-10 (решение D-1 = A) **ровно в объёме пути корня для процесса с package identity**. Не менялись: `schemaVersion` (7) и `schemaRevision` (0), `StateEnvelope`, DTO и маппинги, миграции, `JsonApplicationStateStore` (запись, бэкапы, карантин, advisory lock, rung «newer schema → read-only»), `ApplicationStateService`, `IAppSettings`/`AppSettings`/`Settings.settings`/`user.config`, `LayoutSnapshotService`, семантика загрузки/сохранения, round-trip неизвестных полей. Новых persisted-полей нет: баннер первого запуска и README не сохраняют никакого признака (см. D-3, D-5).

### D-3. `README.txt` в корне Store-хранилища

`StorageRootReadme` (`Services/Storage`, internal): `FileName = "README.txt"`, текст — embedded-ресурс `StorageRootReadme.txt` (en + ru в одном файле: запись происходит до выбора языка UI, resx не подходит). `ShouldWrite(isPackaged, status, isWritable) = isPackaged && status == StateLoadStatus.FirstRun && isWritable`. `TryWrite(root, log)` пишет через `FileMode.CreateNew` (UTF-8 с BOM для Блокнота), **никогда не перезаписывает** существующий файл (включая отредактированный пользователем), не бросает: `IOException`/`UnauthorizedAccessException` ⇒ `Warn StorageRootReadme/WriteFailed`, отсутствие ресурса ⇒ `Warn StorageRootReadme/ResourceMissing`. Вызывается из `Program.Main` сразу после `applicationState.Load()` и только если `packaged && AppStorageRoot.IsPackagedLocation` (README утверждает, что папка переживает удаление, — при fallback на `%LOCALAPPDATA%` это было бы ложью). Не persisted-состояние: файл никто не читает, его нет в схеме и бэкапах, удалять безопасно. Содержание: что за папка, что `PanelItems\` — перемещённые файлы пользователя, что удаление/сброс приложения папку **не** удаляют, как вернуть файлы (drag-out на рабочий стол/в папку; Settings → Backup → «Файлы без панели» → «Вернуть»; ручное копирование), предупреждение, что Delete на элементе панели возвращает файл и отправляет его в Корзину.

### D-4. Автозапуск под identity — `startupTask`, а не `HKCU\Run` (решение D-5 владельца: `Enabled="false"`)

- `AutostartService` (UI) получил `IAppPackageContext` и `IExternalLinkLauncher`. Под identity `IAutostartService.IsManagedBySystem == true`; геттер `IsEnabled` возвращает `false` **без обращения к реестру**, сеттер ничего не пишет и логирует `Warn AutostartService/WriteIgnoredPackaged`. Без identity поведение и контракт с `.iss` прежние (`InstallerAutostartContractTests` не менялись).
- Манифест регистрирует `desktop:Extension Category="windows.startupTask"` → `desktop:StartupTask TaskId="DesktopOrganizerStartup" Enabled="false"`. Константа `AutostartService.StartupTaskId = "DesktopOrganizerStartup"` (без точки — консервативная совместимость со схемой манифеста) сверяется с манифестом `MsixManifestContractTests`. `Enabled="false"`: автозапуск не включается без согласия пользователя; отключённую пользователем задачу программно не включить, поэтому единственный путь — Параметры Windows.
- UI: в `GeneralSection.xaml` две карточки с взаимоисключающей `Visibility`: toggle при `!AutostartManagedBySystem`, иначе описание (`Settings_General_Autostart_Managed_Description`) и кнопка-ссылка (`Settings_General_Autostart_OpenSystemSettings_Button`) → `OpenStartupSettingsCommand` (`MainWindowViewModel` проксирует `IAutostartService`, конструктор VM не менялся).
- `IExternalLinkLauncher.OpenStartupAppsSettings()` — **узкий метод без параметров** (`StartupAppsSettingsUri = "ms-settings:startupapps"`, тот же `LaunchViaShell`). `OpenUrl` по-прежнему пропускает только `https://`; произвольный `ms-settings:` через публичный API открыть нельзя — белый список получается самой конструкцией метода (регрессия M4 закреплена тестом).
- Задача `startupTask` регистрируется Windows только после первого запуска приложения, поэтому до него в «Автозагрузке» её нет — ограничение платформы.
- Полная интеграция WinRT `StartupTask` (включение из приложения, `DisabledByUser`) — roadmap C1.

### D-5. Проверка обновлений скрыта, баннер первого запуска, подсказка Move

- **Updates (A4).** `AboutViewModel.IsUpdateCheckAvailable => !IAppPackageContext.IsPackaged`; карточка Updates в `AboutSection.xaml` скрыта по этому свойству, `CheckForUpdatesAsync` под identity выходит сразу (сетевого вызова нет в принципе — обновления идут через Store; GitHub-релизов у закрытого репозитория нет). `GitHubReleaseUpdateChecker` для Inno-канала не менялся. «Report a problem» и «Project page» ведут на `UpdateEndpoints.SupportUrl`/`ProjectPageUrl`, оба равны `PublicSiteUrl` (`https://github.com/messire`, временное значение — заменяется одной константой, когда владелец даст URL публичной страницы политики/поддержки AP2); `PrivacyPolicyUrl` зарезервирован для листинга и не используется кодом.
- **Баннер (B2).** `ITrayIconManager.ShowInfoBalloon(title, text)` (`NotifyIcon.ShowBalloonTip`, 10 с, ошибки — `Error TrayIconManager/ShowInfoBalloonFailed`, наружу не пробрасываются). Политика в UI: `FirstRunTrayNotice.ShowIfFirstRun()` — показывается, если `LastLoadStatus == FirstRun` **и** `IApplicationStateStore.IsWritable`, не чаще одного раза за сессию; вызов из `App.StartApp` после `ReadOnlySessionNotice.ShowIfReadOnly()`. Факт показа **нигде не хранится**: следующий старт грузит существующий документ (`Loaded`). Работает в обоих каналах. Ключи `FirstRun_TrayBalloon_Title`/`FirstRun_TrayBalloon_Message`.
- **Move-hint (B4).** Текст собирает чистая функция `MoveHintText.Compose(ILocalizationService, folder, survivesUninstall)`: `Hint_Move_Message_Format` (`{0}` = `AppStorageRoot.PanelItemsDirectory`) всегда; фраза `Hint_Move_PackagedRetention` («остаётся там, даже если удалить приложение») — **только** при `AppStorageRoot.IsPackagedLocation`: Inno-деинсталлятор возвращает файлы на рабочий стол, и там эта фраза была бы ложью. Однократность прежняя (`IAppSettings.HasShownMoveHint`).
- Формулировки B5 («--diag», «Move-semantics») из UI убраны; новых настроек `IAppSettings` нет.

### D-6. Упаковка: ручной манифест, `build-msix.ps1`, без `.wapproj`

Реализовано в `installer/msix/**` и `installer/build-msix.ps1`; `DesktopOrganizer.sln` не менялся.

- **`installer/msix/AppxManifest.xml`** — шаблон (валидный XML) с токенами `$IDENTITY_NAME$`, `$PUBLISHER$`, `$VERSION$`, `$PUBLISHER_DISPLAY_NAME$`, `$DISPLAY_NAME$`. `Identity ProcessorArchitecture="x64"`; `TargetDeviceFamily Windows.Desktop MinVersion 10.0.19041.0` (MaxVersionTested 10.0.26100.0); `Resources` en-US и ru-RU; `Application` с `EntryPoint="Windows.FullTrustApplication"`, `Executable="DesktopOrganizer.UI.exe"`; `startupTask` (D-4); capabilities — **только** `rescap:runFullTrust` (без `unvirtualizedResources`). Self-contained win-x64 обязателен: Store не ставит .NET Desktop Runtime как зависимость.
- **`installer/build-msix.ps1`**: (1) `installer\build.ps1 -SkipInstaller` — переиспользованная self-contained публикация; (2) layout `installer\msix\layout` без `*.pdb` и XML-документации (`Foo.xml` рядом с `Foo.dll/.exe`) + `Assets\`; (3) версия — `FILEVERSION` опубликованного `DesktopOrganizer.UI.exe` (как в `.iss`), обязана быть `^[1-9]\d*\.\d+\.\d+\.0$`, иначе `throw`; (4) манифест подставляется через XML DOM (`SetAttribute`), `Publisher` с запятыми и кавычками экранируется автоматически; остаток `$TOKEN$` ⇒ ошибка; (5) `resources.pri`: `makepri createconfig /dq en-US_ru-RU` + `makepri new` по staging-папке **только с манифестом и `Assets\`** (папка `ru\` .NET-сателлитов не должна индексироваться как языковой квалификатор), из `priconfig.xml` **удаляется секция `<packaging>`** — иначе `makepri` разбивает тайлы `scale-125/150/200/400` на resource-пакеты, которых нет в layout; наличие `resources.*.pri` ⇒ ошибка; (6) `makeappx pack` → `installer\Output\DesktopOrganizer-<major.minor.build>.msix`; (7) `-SignForSideload` — self-signed сертификат с `Subject == Publisher`, только для placeholder-издателя (с реальным Publisher нужен `-Force`; пакет с самоподписью с реальным Subject никогда не поставляется), импорт в `TrustedPeople` скрипт **не делает**, а печатает инструкцию; (8) `-RunWack` — `appcert.exe`, нужен админ, отсутствие — предупреждение. SDK ищется автоматически (новейший `Windows Kits\10\bin\10.0.*\x64`), `-SdkBinDir` переопределяет. В Partner Center загружается неподписанный `.msix` (Store подпишет сам).
- **Идентичность** (Name/Publisher/PublisherDisplayName/DisplayName) берётся из параметров `-IdentityName`/`-Publisher`/`-PublisherDisplayName`/`-DisplayName` или переменных `DESKTOPORGANIZER_MSIX_IDENTITY_NAME`/`_PUBLISHER`/`_PUBLISHER_DISPLAY_NAME`/`_DISPLAY_NAME` (параметры приоритетнее); значения копируются посимвольно из Partner Center. Дефолт — development-placeholder (`khagendev.DesktopOrganizer.Dev` / `CN=DesktopOrganizer Dev`): такой пакет ставится sideload-ом, но Store его отвергнет; скрипт предупреждает (в GitHub Actions — аннотацией `::warning`).
- **Ассеты.** `installer/msix/Generate-Assets.ps1` читает 256×256 PNG-кадр из `src/DesktopOrganizer.UI/Resources/app.ico` (разбор ICONDIR), ресайз `System.Drawing` (HighQualityBicubic, 32bppArgb) → `installer/msix/Assets/*.png` (32 файла; `StoreLogo`, `Square150x150Logo`, `Square44x44Logo` + `targetsize-16/24/32/48/256` в вариантах plated/`altform-unplated`/`altform-lightunplated`, `Wide310x150Logo`; каждый < 200 КБ). Результат коммитится, скрипт запускается при смене иконки; `build-msix.ps1` и CI только копируют PNG.
- **CI.** Job `msix` в `.github/workflows/ci.yml`: `needs: build-and-test`, `runs-on: windows-latest`, `if: startsWith(github.ref, 'refs/tags/v') || github.event_name == 'workflow_dispatch'`, `setup-dotnet 8`, `pwsh .\installer\build-msix.ps1` (без подписи), `upload-artifact` `DesktopOrganizer-msix` (`installer/Output/*.msix`, 30 дней). Identity подставляется из repo variables `DESKTOPORGANIZER_MSIX_*` (три из четырёх; без них — placeholder + предупреждение). Артефакт предназначен для Partner Center, не для пользователей.
- **Контракты.** `MsixManifestContractTests` (`tests/…/Unit/Installer`) парсит шаблон и сверяет: токены, `TaskId == AutostartService.StartupTaskId` и `Enabled="false"`, `Executable` совпадает с именем сборки UI, capabilities — только `runFullTrust`, `TargetDeviceFamily`/архитектура, языки `Resources` ↔ `SatelliteResourceLanguages`, версия сборки `^[1-9]\d*\.\d+\.\d+\.0$`, наличие и размеры ассетов (по IHDR PNG), `ValidateSet("Release")` и исключение `*.pdb` в скрипте, отсутствие `*.wapproj` в sln.

### D-7. Версия 1.0.0 и закрытый код (решения владельца D-2, D-6)

- `Directory.Build.props`: `<Version>1.0.0</Version>` (версия пакета MSIX обязана иметь первую секцию ≠ 0 и четвёртую = 0 ⇒ минимум 1.0.0.0). `AssemblyVersion` по-прежнему закреплён на `1.0.0.0` (путь `user.config`, комментарий в файле), `FileVersion = $(Version)`. Описание продукта: «A free Windows desktop organizer…» (без «open-source»); то же в `README.md`. Репозиторий остаётся приватным; `LICENSE` (MIT) остаётся, но в листинге License terms не заполняется ссылкой на MIT. Обфускация Store-пакета — вне объёма (C14).

### D-8. События лога

Источник — `IAppLog` (`app-*.jsonl`, [ADR-033](ADR-033-crash-safety-and-app-log.md)). Реестр: [diagnostics-log-events.md](../docs/Refactoring/Testing/diagnostics-log-events.md).

| Событие | Уровень | Где | Поля |
|---|---|---|---|
| `Program` / `PackageIdentity` | Info | `Program.Main` после `Load()`, один раз, только Primary | `packaged`, `underUserProfile`, `probeResultCode`, `storageRoot` |
| `AutostartService` / `WriteIgnoredPackaged` | Warn | сеттер `IsEnabled` под identity | — |
| `StorageRootReadme` / `WriteFailed` | Warn | `TryWrite` | `root`, `error`, `message` |
| `StorageRootReadme` / `ResourceMissing` | Warn | `TryWrite` | `resource` |
| `TrayIconManager` / `ShowInfoBalloonFailed` | Error | `ShowInfoBalloon` | исключение |

`storageRoot`, `root` и `message` содержат пути; `DiagnosticsAppLog` прогоняет каждое строковое поле через `LogScrubber` (корневые пути ⇒ `<path>`, имя учётной записи ⇒ `%USERPROFILE%`), так что файл остаётся безопасным для вложения в отчёт.

## Known limitations

- **P1 — `user.config` сбрасывается при каждом обновлении из Store (DEFER, C13).** Путь `user.config` содержит `_Url_<hash>` от пути exe (нет strong name), а MSIX ставит каждую версию в новую папку `WindowsApps\<Name>_<Version>_x64__<id>`. Теряются все `IAppSettings` (язык, `IsLayoutLocked`, жесты, сетка, одноразовые подсказки, в т.ч. `HasShownMoveHint`); панели и страницы (JSON) **не** затрагиваются. `Upgrade()` не поможет (другой evidence-хэш). Любое исправление (strong-name только Store-сборки, custom `SettingsProvider`, перенос в JSON) меняет контур `IAppSettings`/`user.config` и требует **отдельного явного «да» владельца**; решение D-4 владельца не дано ⇒ действует рекомендация «отложить». Первая публикация проблему не показывает, первое обновление — покажет.
- **Иконки остаются скрытыми, если пакет удалили при работающем приложении.** Приложение не успевает восстановить видимость иконок рабочего стола; лечится перезапуском Explorer или входом в систему (C4: `RegisterApplicationRestart`/self-heal).
- **Side-by-side с Inno (C2).** Имена mutex/pipe общие ([ADR-023](ADR-023-single-instance-and-ipc.md)): второй установленный канал активирует первый экземпляр (открывает его настройки), а корни хранилищ разные. Детекта и миграции данных нет; разработчику перед тестом Store-сборки нужно удалить Inno-сборку. [ADR-023](ADR-023-single-instance-and-ipc.md) этим не меняется — отдельного amendment не требуется.
- **Автозапуск — только через Параметры Windows** (C1); задача появляется в «Автозагрузке» после первого запуска.
- **В Store-канале нет uninstall-restore** (см. [ADR-041](ADR-041-first-run-panel-and-uninstall-restore.md), Amendment): файлы остаются в `%USERPROFILE%\DesktopOrganizer\PanelItems`, пока пользователь не заберёт их сам.
- **Закрытый код (D-6).** Store исходники не запрашивает; публичной должна быть только страница с политикой конфиденциальности и контактом поддержки (AP2). До получения URL ссылки в приложении указывают на `PublicSiteUrl`. Self-contained пакет декомпилируется в читаемый C# (C14).
- **Папка `DesktopOrganizer` в профиле пользователя** может уже существовать (посторонняя): приложение пишет только в `State\`, `PanelItems\`, `Logs\` и `README.txt` с `CreateNew`.

## Последствия

Положительные:
- Store-пользователь не теряет файлы при Uninstall/Reset и видит их в Explorer; drag-out и «Открыть папку» работают.
- Нет лживого переключателя автозапуска; нет сетевого вызова и ссылки на несуществующие GitHub Releases.
- Идентичность определяется одной пробой, а исключение из инварианта «P/Invoke только за `INativeApi`» локализовано одним классом и закреплено тестом.
- Inno-канал побайтно прежний (в т.ч. корень, `Run`-значение, `--restore-panel-items`).

Отрицательные / риски:
- Данные Store-канала лежат в `%USERPROFILE%\DesktopOrganizer` — видимая посторонняя папка в профиле; отсюда README и честная формулировка подсказки.
- Два корня хранилища в одной ОС при одновременной установке обоих каналов (C2).
- Нативное и непроверяемое вне Windows: поведение пробы, установка и запуск под identity, `startupTask`, WACK, рендер тайлов (апскейл 256 → 396 px для `Square150x150Logo.scale-400` даёт мягкость — в roadmap растеризация из SVG). Проверяется только вручную на Windows 11 (список — ниже); блокером не является.
- `makepri /dq en-US_ru-RU`: влияние на список языков листинга проверяется `makepri dump`/WACK; fallback — `en-US`.
- На Win11 balloon показывается как toast, Focus Assist может его подавить; сертификационные заметки на баннер не опираются.

## Manual verify (Windows 11)

Удалить Inno-сборку; `build-msix.ps1 -SignForSideload`, импорт сертификата в `LocalMachine\TrustedPeople` (админ), `Add-AppxPackage`. Затем: первый запуск показывает баннер, в `%USERPROFILE%\DesktopOrganizer` есть `README.txt`; перетаскивание файла на панель показывает подсказку с путём, файл виден в Explorer и вытаскивается обратно; Uninstall и Settings → Apps → Reset не трогают папку; карточка автозапуска открывает «Автозагрузку», после включения приложение стартует при входе; карточка Updates скрыта; «Открыть папку логов/хранилища», crash-диалог и Orphans открывают `%USERPROFILE%\…`; сценарий «Notes for certification» (AP3) пройден; WACK без FAILED.

## Related

- [ADR-009](ADR-009-drag-drop-move-semantics.md) — Move-семантика, из-за которой виртуализация AppData является потерей данных.
- [ADR-024](ADR-024-versioned-json-persistence.md) — замороженный контур; Amendment 2026-10-10 (Store) фиксирует границы разрешения.
- [ADR-027](ADR-027-diagnostics-logging.md), [ADR-033](ADR-033-crash-safety-and-app-log.md) — расположение и приём логов.
- [ADR-038](ADR-038-release-engineering.md) — Inno-канал, CI, версия; Amendment 2026-10-10 (Store).
- [ADR-041](ADR-041-first-run-panel-and-uninstall-restore.md) — uninstall-restore есть только в Inno.
- [ADR-023](ADR-023-single-instance-and-ipc.md) — общие имена mutex/pipe (side-by-side).
- [ADR-048](ADR-048-panel-item-lifecycle-release-rules.md) — свод правил судьбы managed-файлов.
- `docs/PLANS/store-first-publication.md`, `docs/PLANS/store-publication-implementation.md`.

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-10-10 | (создание) | Канал Microsoft Store: identity-probe, корень `%USERPROFILE%` под identity, `startupTask`, упаковка MSIX | — |
