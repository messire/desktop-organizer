# Мастер-план: Release hardening 1.0 (пакеты A–G)

Ветка: `feature/release-hardening` (от `rc/0.6.1`). **Версия остаётся 0.6.1** — бампа нет, в плане его нет.
Статус: **Implemented (2026-09-27) — все пакеты A/B/C/D/E/F/G1/G2 реализованы, `dotnet build`/`dotnet test`
0 errors/0 warnings, 4379 тестов; ждёт manual verify (Win10/Win11).** Источник фактов: code inspection
оркестратора + сверка планировщиком. Архитектурные решения зафиксированы в
[ADR-033](../../adr/ADR-033-crash-safety-and-app-log.md)…[ADR-038](../../adr/ADR-038-release-engineering.md)
(§9 ниже), статусы — в [docs/ROADMAP.md](../ROADMAP.md) «Фаза 10». Этот документ сохраняется как
исторический план — расхождения реализации с ним отмечены пометками «Реализовано» под каждым пакетом, без
переписывания самого плана.

### Реализовано — отклонения от плана (кратко, детали — в ADR/CHANGELOG)

- **Пакет F (DPI):** гейт F0 сработал и потребовал перепланинга (см. `release-hardening-1.0-f0-results.md`
  §6) — реализован вариант A (двухрежимный, ADR-037), не буквальный «единый desktop-layer scale» из §6
  этого документа; детальный план — `release-hardening-1.0-F-dpi.md`. E6-гипотеза якорения (QF-1) осталась
  **PENDING** — нет стенда с 2 мониторами.
- **Пакет C:** `ShellRecoveryCoordinator.IDesktopScaleProvider.BeginBatch()` — добавлено ревью-находкой M10,
  не было в исходном плане §4.
- **Пакет G1:** `MyAppPublisher` остался `"khagen"` (Q4 вариант B — MIT/messire как copyright holder,
  паблишер инсталлятора не переименован); подпись для ISCC всегда по SHA1-отпечатку сертификата (никогда
  `/f <pfx> /p <password>`), см. `installer/build.ps1`, `M5/L8`.
- **Пакет G2:** `AboutViewModel` — `OpenReleaseCommand` (последний найденный релиз) отделён от
  `OpenProjectPageCommand` (`UpdateEndpoints.ProjectPageUrl`), вместо единого `OpenReleasesCommand` из
  плана §7 G2.
- **Q1–Q6 (§10):** реализованы все дефолты «A» без эскалации к пользователю (сохранение состояния при
  краше только на UI-потоке; SessionEnding через существующую последовательность; замена `Console.Error` в
  4 файлах контура разрешена; LICENSE MIT/messire, паблишер не переименован; единый масштаб WorkerW принят
  как известное ограничение 1.0; при сбое Корзины панель остаётся).

---

## 0. Общие правила для всех пакетов

### 0.1 Заморозка persistence (CLAUDE.md, ADR-024) — жёсткий гейт
Ни один пакет **не** трогает: `schemaVersion`/`StateEnvelope`, `Storage/Migrations/*`, DTO/`PanelStateMapper`,
`IAppSettings`/`AppSettings`/`Settings.settings`/`Settings.Designer.cs`/`user.config`, `LayoutSnapshotService`
(семантику), пути `%LOCALAPPDATA%\DesktopOrganizer\State`. Всё, что «нужно запомнить», — **только память процесса
(session-only)** или **compile-time константа**. Проверка перед закрытием каждого пакета:
`git diff --name-only` не содержит перечисленных файлов (исключение — см. Q3: пакет B, только замена
`Console.Error.WriteLine` на лог-вызов в 4 файлах контура, без изменения control flow).

### 0.2 Чистая сборка
`dotnet build` + `dotnet test` по solution: **0 errors, 0 warnings**, без `#pragma`/`[SuppressMessage]`/`NoWarn`.
Гейты локализации ADR-032 должны оставаться зелёными: `ResxConsistencyTests` (паритет en/ru, плейсхолдеры),
`KeyUsageCoverageTests` (каждый ключ используется), `XamlLiteralGuardTests` (нет литералов в XAML).

### 0.3 Локализация — раздельные префиксы и якоря (mergeable-правки resx)
`src/DesktopOrganizer.Localization/Strings.resx` и `Strings.ru.resx` — общий горячий файл. Правило: каждый пакет
добавляет ключи **одним непрерывным блоком** со своим префиксом, вставленным **сразу после своего якорного ключа**
(один и тот же якорь в обоих resx); перед каждой правкой — перечитать файл.

| Пакет | Префикс новых ключей | Якорь (вставлять после) | Правки существующих значений |
|---|---|---|---|
| A | `Crash_*` | `Startup_Failed_Format` | — |
| B | — (ключей нет, логи на английском) | — | — |
| C | — (UI-текста нет) | — | — |
| D | `Recycle_*` | `Settings_Confirm_RemovePanel_Title` | значения `Settings_Confirm_RemovePanel_ManagedFiles_Message`, `Page_Confirm_Remove_ManagedFiles_Message` |
| E | `DragOut_*` (если понадобится) | `Panel_FileMenu_RemoveFromPanel_Tooltip` (**ключ и весь якорь удалены 2026-09-27, release polish 1.0** — собственное `FileContextMenu` убрано целиком, элемент панели показывает нативное shell-меню; см. [ADR-040](../../adr/ADR-040-native-shell-context-menu.md)) | — |
| G2 | `Settings_About_Update_*`, `Settings_About_Links_*` | `Settings_About_Version_Format` | значение `Settings_About_Diagnostics_Message` |

### 0.4 Версия приложения
`AssemblyVersion` зафиксирован `1.0.0.0` (Directory.Build.props, ради пути `user.config`) — **нельзя** брать
версию из `AssemblyName.Version` (баг: `App.GetAppVersion()` сейчас отдаёт `1.0.0.0`). Единый источник —
`AssemblyInformationalVersionAttribute` без суффикса `+<sha>`. Вводится в пакете B: `AppVersionInfo`
(`Services/Diagnostics/AppVersionInfo.cs`, public static: `Display` = `0.6.1`, `Full` = с `+sha`). A и G2 его используют.

### 0.5 Горячие файлы (кто трогает)

| Файл | B | A | D | C | E | F | G2 |
|---|---|---|---|---|---|---|---|
| `UI/App.xaml.cs` | x | x | | x | | x | |
| `UI/Program.cs` | x | x | | | | | |
| `UI/Services/WindowManager.cs` | x | | x | x | | x | |
| `Services/Tools/MonitorService.cs` | x | | | | | x | |
| `Services/Tools/PanelItemStorageService.cs` | x | | x | | x | | |
| `Services/Interfaces/INativeApi.cs` + `Tools/NativeApi.cs` | x | | x | x | | x | |
| `UI/ViewModels/MainWindowViewModel.cs` | x | | x | | | | x |
| `UI/ViewModels/ChildWindowViewModel.cs` | x | | x | | x | | |
| `UI/Views/MainWindow.xaml` | | | | | | | (нет — About вынесен в `Views/Settings/AboutSection.xaml`) |
| `Strings.resx` / `Strings.ru.resx` | | x | x | | x | | x |
| `Services/Configuration/DiConfig.cs` | x | | | x | | | x |
| `UI/Configuration/DiConfig.cs` | | | | | x | | x |
| `UI/Services/SystemIconsPanelManager.cs` | | | | x | | x | |
| `UI/DesktopOrganizer.UI.csproj` | | | | | | x | |

---

## 1. Пакет B — Diagnostics (фундамент, волна 1)

**Scope: L** (кросс-слойный, механический, ~20 файлов). Агент: **dotnet-services-engineer**.

### Решение (ADR-033, часть 1)
- **Два уровня:** `Standard` (Release по умолчанию — compile-time константа `DiagnosticsDefaults.FileLoggingEnabled = true`)
  и `Verbose` (Debug, `--diag`, `DESKTOPORGANIZER_DIAG=1`). Отключить файловый лог в Release без перекомпиляции нельзя — осознанно (никаких новых настроек).
- **Один sink** — существующий `JsonLinesDiagnosticsLog` (ротация 10 MB × 10, каталог `Logs`, паттерн файлов не меняется).
  Регистрируется как внутренний singleton. `IDiagnosticsLog` сохраняет **прежнюю семантику «verbose-трассировка»**:
  в `Standard` резолвится в `NullDiagnosticsLog` (жестовые события не пишутся), в `Verbose` — в sink. Так ни один
  существующий вызов `IsEnabled`-гейтинга не меняет поведение.
- **Новый фасад `IAppLog`** (Services.Abstractions/Interfaces): `Info/Warn(string source, string evt, params KeyValuePair<string, object?>[] data)`,
  `Error(string source, string evt, Exception? ex, params …)`. Реализация `DiagnosticsAppLog` пишет в sink всегда
  (категория `app`, поля `level`, `exType`, `hresult`, `msg` — после `LogScrubber`, `stack` — только у `Error`).
- **Приватность:** `LogScrubber.Scrub(string)` заменяет rooted/UNC-пути на `<path:.ext>` и имя пользователя/профиль на
  `%USERPROFILE%`. Все сообщения — английский, без путей/названий панелей (инвариант `DiagnosticEvent`).
- **Классы без DI-доступа → ambient `AppLog.Current`** (`Services/Diagnostics/AppLog.cs`, public static; по умолчанию
  `NullAppLog`; `Initialize(IAppLog)` ровно один раз из `Program`, lazy-обёртка над провайдером — sink материализуется при
  первой записи, ADR-023 R-12 сохранён). Правило выбора:
  1. DI-резолвимые классы вне контура persistence — ctor-инъекция `IAppLog? log = null` с ленивым fallback
     `_log ?? AppLog.Current` (не ломает тесты, конструирующие класс без лога).
  2. Статические хелперы, `Program` до DI, и **4 файла замороженного контура** — только `AppLog.Current`
     (ноль изменений ctor/DI в контуре).
- `Program.cs`: CLI-строки `--restore-state-backup` остаются в консоли **и** дублируются в лог (терминальный вывод — контракт).

### Артефакты
Новые: `Services.Abstractions/Interfaces/IAppLog.cs`; `Services/Diagnostics/{DiagnosticsAppLog,NullAppLog,AppLog,LogScrubber,DiagnosticsLevel,DiagnosticsDefaults,AppVersionInfo}.cs`.
Изменяемые: `Services/Diagnostics/DiagnosticsSwitch.cs` (+`Resolve(args, env) → DiagnosticsLevel`, `IsEnabled` → обёртка),
`Services/Configuration/DiConfig.cs` (регистрация sink/`IDiagnosticsLog`/`IAppLog`), `UI/Program.cs` (`AppLog.Initialize`),
`UI/App.xaml.cs` (`GetAppVersion` → `AppVersionInfo`).
Замена `Console.Error.WriteLine` (67 шт.):
- Services: `Tools/PanelItemStorageService.cs` (21), `Tools/NativePanelSizingService.cs` (7), `Tools/MonitorService.cs` (4),
  `Tools/DesktopGestureService.cs` (3), `Tools/NativeApi.cs` (3), `Tools/FileLauncher.cs` (1), `Tools/FileItemRenameService.cs` (1),
  `Providers/IconProvider.cs` (1), `Diagnostics/IconDiagnostics.cs` (1);
  контур (Q3, только ambient, без смены control flow): `Storage/AtomicJsonFileWriter.cs` (5), `Storage/JsonApplicationStateStore.cs` (4), `Storage/StateRestoreCommand.cs` (1).
- UI: `Program.cs` (3, дублировать), `App.xaml.cs` (1), `Services/WindowManager.cs` (3), `ViewModels/ChildWindowViewModel.cs` (2),
  `Services/AutostartService.cs` (2), `ViewModels/MainWindowViewModel.cs` (1); контур (Q3): `State/ApplicationStateService.cs` (2).
  `Diagnostics/IGestureDiagnostics.cs` — только комментарии, не трогать.

### Тесты
`LogScrubberTests` (диск/UNC/профиль/несколько путей/без путей — без изменений), `DiagnosticsAppLogTests` (Moq sink: категория/level/scrub/stack только у Error, никогда не бросает),
`DiagnosticsSwitchTests` (обновить: Release→Standard, флаг/env→Verbose, Debug→Verbose), `AppLogAmbientTests` (Null по умолчанию, повторный Initialize — no-op),
`DiConfigTests` (`IAppLog` резолвится; в Standard `IDiagnosticsLog` — Null; R-12: резолв+`Start()` координатора в роли Secondary не создаёт файлов в temp-каталоге логов),
`AppVersionInfoTests` (срез `+sha`), **`ConsoleErrorGuardTests`** (скан `src/**/*.cs`: 0 вхождений `Console.Error.WriteLine`, allowlist — 3 строки `Program.cs` с маркером `// cli-output`).

### DoD
1. 0 вызовов `Console.Error.WriteLine` вне allowlist (гейт-тест). 2. Release-сборка без флагов пишет `app`-события в `Logs\*.jsonl`; жестовые события — только с `--diag`.
3. Ни одно сообщение лога не содержит абсолютного пути (scrubber + тест). 4. Secondary-запуск не создаёт/не ротирует лог-файлы, если не было ошибок.
5. `IDiagnosticsLog`-вызовы существующего кода не изменены. 6. 0 warnings.

### Риски
- Объём логов в Release: только ошибки/warn + редкие lifecycle-info; ротация уже есть. — Логи в горячих путях (resize/drag) запрещены на уровне `Info`.
- Q3: правка 4 файлов контура — если пользователь не подтвердит, B пропускает их (12 вызовов), guard-тест получает их в allowlist.

### Manual verify (Win10/Win11)
Release-установка без `--diag`: вызвать ошибку (заблокировать файл в панели и переименовать) → запись в `Logs`, путь замаскирован.

---

## 2. Пакет A — Crash safety (волна 2)

**Scope: L.** Агент: **dotnet-services-engineer** (lifecycle), консультативно win32-interop-specialist не нужен (используется готовый `IDesktopIconsService`).

### Решение (ADR-033, часть 2)
- **Обработчики** (устанавливаются как можно раньше):
  - `Program.Main` (до DI): `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`
    (только лог `Error` + `SetObserved()`, **не** фатально — поведение .NET 8 по умолчанию сохраняется),
    WinForms-мост `Services/Diagnostics/WinFormsExceptionBridge.Install(handler)` (`Application.SetUnhandledExceptionMode(CatchException)` +
    `Application.ThreadException` — иначе исключение в колбэке `NotifyIcon` покажет WinForms `ThreadExceptionDialog`; WinForms есть только в Services).
  - `App` ctor: `DispatcherUnhandledException` → `e.Handled = true` → фатальная последовательность → `Shutdown(ExitCodes.Crashed = 5)`.
- **Фатальная последовательность** `CrashCoordinator.HandleFatal(Exception, CrashOrigin)` (однократно, `Interlocked`-guard, каждый шаг в своём try/catch):
  1. восстановить иконки рабочего стола (`IDesktopIconsService.SetIconsVisible(true)`, ссылка захвачена заранее, без DI-резолва во время краша);
  2. синхронно записать `Logs\crash-yyyyMMdd-HHmmss-<pid>.log` (`CrashReportWriter`: UTC-время, `AppVersionInfo.Full`, `Environment.OSVersion`
     + build, `RuntimeInformation.FrameworkDescription`, UI-культура, origin, цепочка исключений `type/HResult/scrubbed message/stack`,
     без путей пользователя; retention 10 файлов; прямой `File.WriteAllText`, не через очередь JSONL) + `IAppLog.Error` + `Flush()`;
  3. сохранение состояния — **по Q1** (по умолчанию: Dispatcher-краш → штатный `OnExit` (включая существующий `ApplicationState.SaveNow()`
     за write-barrier `MarkConsumersReady`); краш фонового потока → **без** сохранения);
  4. короткий локализованный диалог (`MessageBox`, `Crash_Dialog_Title`, `Crash_Dialog_Message` — «иконки восстановлены, отчёт сохранён; открыть папку логов?» Yes/No);
  5. выход с кодом 5 (Dispatcher: `Shutdown(5)`; AppDomain: процесс завершает CLR, код документируется как «неуправляемый»).
- **Startup self-heal:** видимость иконок — session-only (не персистится), поэтому в `StartApp` при `AreIconsVisible == false` иконки
  принудительно показываются (значит, прошлый процесс был убит/упал). Покрывает taskkill/выключение питания.
- **SessionEnding** (Q2): `Application.SessionEnding` + `Microsoft.Win32.SystemEvents.SessionEnding` (страховка, маршалинг на UI через
  `UiDispatch`) → запускают **тот же** существующий shutdown-метод, что `OnExit` (вынести тело `OnExit` в идемпотентный `RunShutdownSequence()`),
  повторный вызов из `OnExit` — no-op. Новых записей нет — те же, раньше по времени.
- `Program.Main` doc-комментарий кодов выхода: добавить `5 — fatal unhandled exception`.

### Артефакты
Новые: `UI/Lifecycle/CrashCoordinator.cs` (логика, тестируемая — зависимости делегатами/интерфейсами), `UI/Lifecycle/ExitCodes.cs`,
`Services/Diagnostics/CrashReportWriter.cs` (public), `Services/Diagnostics/WinFormsExceptionBridge.cs`.
Изменяемые: `UI/App.xaml.cs` (обработчик Dispatcher, `RunShutdownSequence`, self-heal, SessionEnding), `UI/Program.cs`, resx (`Crash_Dialog_Title`, `Crash_Dialog_Message`).

### Тесты
`CrashReportWriterTests` (содержимое, отсутствие путей после scrub, имя файла, retention=10, IO-ошибка не бросает), `CrashCoordinatorTests`
(порядок: иконки → лог → диалог → shutdown(5) через `MockSequence`; исключение в шаге 1 не отменяет шаги 2–5; повторный вызов — no-op;
фоновый origin не вызывает save), `ShutdownSequenceIdempotencyTests` (SessionEnding + OnExit → одна последовательность, `SaveNow` один раз),
`StartupIconsSelfHealTests` (Mock `IDesktopIconsService`: false → `SetIconsVisible(true)`), `WinFormsExceptionBridgeTests` (handler вызывается).

### DoD
1. Любое необработанное исключение UI-потока → иконки видимы, есть `crash-*.log`, диалог, код 5. 2. Фоновый поток → иконки + лог.
3. `UnobservedTaskException` логируется, процесс жив. 4. Рестарт после kill → иконки видимы. 5. Выход из сеанса → иконки + состояние (Q2). 6. 0 warnings.

### Риски
- Диалог на умирающем фоновом потоке держит процесс до закрытия — приемлемо. Реентерабельность: диалог Dispatcher-краша не должен пампить сообщения, которые вызовут второй краш — guard.
- Q1/Q2 затрагивают «когда пишется» (заморозка) — нужен явный ответ пользователя; до ответа реализуется дефолт, который **не добавляет новых точек записи** (Dispatcher-краш идёт через штатный `OnExit`).

### Manual verify (Win10/Win11)
Скрыть иконки → инжектировать исключение (debug-команда только в Debug-сборке, `#if DEBUG`, пункт трея) на UI и фоновом потоке → иконки вернулись, лог, диалог (en/ru);
`taskkill /f` при скрытых иконках → перезапуск показывает иконки; выход из системы/перезагрузка при скрытых иконках.

---

## 3. Пакет D — Удаление панели → Корзина (волна 2)

**Scope: M/L.** Агент: **dotnet-services-engineer** (+ **win32-interop-specialist** для нового метода `INativeApi`).

### Решение (ADR-035, amends ADR-010)
- **Никогда не hard-delete пользовательских данных.** Нет fallback на `File.Delete`/`Directory.Delete` содержимого (удаляются только
  пустые GUID-каталоги-обёртки). При сбое — **fail visibly**: панель (страница) **не удаляется**.
- `INativeApi.RecycleItems(string[] paths, IntPtr owner) → bool`: `FO_DELETE | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI`
  (`WANTNUKEWARNING` — shell спросит, если элемент не помещается в Корзину, вместо тихого безвозвратного удаления; существующий
  `SendToRecycleBin` не трогаем — его семантика используется SystemIconsPanel). Owner = `IntPtr.Zero`. Только UI (STA) поток.
- `IPanelItemStorageService.ReleaseToRecycleBin(IReadOnlyList<string> storedPaths) → RecycleOutcome(Recycled, Remaining, Cancelled)`:
  фильтр `IsManaged`, **один** batch-вызов shell, итог определяется **проверкой существования каждого пути после вызова** (устойчиво к
  частичному выполнению); манифест чистится только для `Recycled`; `Release(string)` удаляется из интерфейса (последний потребитель —
  `ChildWindowViewModel.RemoveFileItem` legacy-ветка `OriginalPathUnknown` — переводится на `ReleaseToRecycleBin([path])`).
- `WindowManager.RemoveChildWindow(..., releaseManagedItems: true, ...)` → возвращает `PanelRemovalResult` (`Removed | Blocked(remaining)`):
  Sync → recycle → при `Remaining.Count > 0`: из модели/VM убираются только реально ушедшие пути, окно **не** закрывается, сохранение
  debounced (как при обычном удалении элемента); иначе — прежний путь. `IWindowManager` сигнатура меняется (bool/результат).
- `PageManagerService.RemovePage`: собрать managed-пути всех панелей страницы → **один** recycle → если что-то осталось, **страница и все
  панели сохраняются** (ушедшие элементы убраны), сообщение; иначе удаление панелей с `releaseManagedItems: false` (уже в Корзине) + страницы.
- Сообщение: `IConfirmationService.Notify(string message, string title, MessageBoxImage icon)` (новый член) — `Recycle_Failed_Message_Format` ({0} = число элементов).
- Тексты подтверждения (en+ru) — значения существующих ключей: «…файлы, перемещённые в неё, будут перемещены в Корзину».

### Артефакты
`Services/Interfaces/INativeApi.cs`, `Services/Tools/NativeApi.cs` (+`FofWantNukeWarning = 0x4000`), `Services.Abstractions/Interfaces/IPanelItemStorageService.cs`
(+`RecycleOutcome` record), `Services/Tools/PanelItemStorageService.cs` (ctor: `INativeApi`, internal test-ctor `(storageRoot, INativeApi)`),
`UI/Services/{IWindowManager,WindowManager,PageManagerService,IConfirmationService,ConfirmationService}.cs`,
`UI/ViewModels/MainWindowViewModel.cs` (только тело `RemovePanel`), `UI/ViewModels/ChildWindowViewModel.cs` (только `RemoveFileItem`),
resx (`Recycle_Failed_Title`, `Recycle_Failed_Message_Format` + 2 значения).

### Тесты
`PanelItemStorageServiceTests`: вызывает `RecycleItems` только с managed-путями (Moq `INativeApi`, temp-каталог); mock удаляет файлы → манифест
очищен, пустой GUID-каталог удалён; mock вернул false/ничего не удалил → **файлы на месте**, манифест цел, `Remaining` полон; частичный
успех; не-managed путь игнорируется. `WindowManager` (статический хелпер `ReleaseManagedItems` → результат), `PageManagerServiceTests`
(Blocked → страница не удалена), `MainWindowViewModelTests` (Blocked → `Notify`), `ChildWindowViewModelTests` (legacy-ветка → recycle).

### DoD
1. В коде нет пути, удаляющего содержимое managed-элемента мимо Корзины (grep-тест на `File.Delete`/`Directory.Delete(…, true)` в
   `Release*`-методах). 2. Удалённая панель → файлы в Корзине, восстанавливаются «Восстановить». 3. Сбой → панель на месте + сообщение.
4. Тексты en/ru обновлены. 5. 0 warnings.

### Риски
- `FOF_WANTNUKEWARNING` показывает системный диалог — единственное допустимое UI shell-а. — Длинные пути >MAX_PATH: SHFileOperation не
  поддерживает `\\?\` — такие элементы окажутся в `Remaining` (видимый отказ, данные целы). — Объём Корзины / диск без Корзины → тот же путь.

### Manual verify (Win10/Win11)
Удалить панель с файлами и папкой → Корзина → «Восстановить» возвращает в `PanelItems\<guid>\` (осиротевший элемент — ожидаемо, запись в ADR);
элемент > размера Корзины → диалог shell, «Нет» → панель остаётся; удалить страницу с 3 панелями; залоченный файл → Blocked.

---

## 4. Пакет C — Перезапуск Explorer (волна 3)

**Scope: L (на границе XL).** Агент: **win32-interop-specialist** (ведущий) + **wpf-ui-builder** (WindowManager/ChildWindow recovery).

### Решение (ADR-034)
- **Детекция:** новый `IExplorerRestartMonitor` (Services.Abstractions) / `ExplorerRestartMonitor` (Services/Tools) — **собственное** скрытое
  top-level окно через `INativeApi.CreateMessageWindow` (не `MonitorService` — развязка с F); `INativeApi.RegisterWindowMessage("TaskbarCreated")`;
  страховочный триггер — смена HWND `Progman` (опрос `FindWindow` раз в 2 с). Событие `ExplorerRestarted` на UI-потоке. WndProc — минимум работы + try/catch.
- **Устойчивость к всплескам:** чистая политика `DesktopLayerRecoveryPolicy` (UI/Models): коалесинг событий в окне 1500 мс, счётчик поколений
  (новое событие отменяет незавершённую попытку), ретраи при `WorkerW == 0`: 500 мс × до 10 попыток; каждая попытка идемпотентна.
- **Восстановление** (`WindowManager.RecoverDesktopLayer()`, вызывает `App` по событию):
  1. для каждой панели с созданным HWND: `IsWindow(hwnd)` → `IDesktopIntegrationService.EmbedInDesktop(hwnd)` (стили + `SetParent` в новый
     WorkerW; пропуск `SetParent`, если родитель уже новый WorkerW) → принудительно переприменить Left/Top/Width/Height из модели;
  2. HWND уничтожен (родитель-WorkerW разрушен вместе с детьми) → убрать запись **без** сохранения/релиза, корректно dispose VM,
     `CreateChildWindow(model)` заново (модель — единственный источник правды); guard, чтобы `Closed` от внешнего `WM_DESTROY` не считался удалением;
  3. `SystemIconsPanelManager.ReassertAfterShellRestart()`: `ApplyDesktopWindowStyles` + `SendToBackOfZOrder` + повтор anchor виртуального стола;
  4. `IVirtualDesktopService.Reset()` (новый: сброс кеша COM `IVirtualDesktopManager` — старый RCW указывает на мёртвый explorer);
  5. иконки: `IDesktopIconsService` получает session-only намерение `DesiredVisible` (в памяти) + `ReapplyDesiredVisibility()` — новый
     `SHELLDLL_DefView` видим по умолчанию; затем синхронизация галки трея;
  6. трей: WinForms `NotifyIcon` сам обрабатывает `TaskbarCreated` — только проверить.
- Lasso-хук `WH_MOUSE_LL` процесс-локален — переустанавливать не нужно (проверить вручную).

### Артефакты
Новые: `Services.Abstractions/Interfaces/IExplorerRestartMonitor.cs`, `Services/Tools/ExplorerRestartMonitor.cs`, `UI/Models/DesktopLayerRecoveryPolicy.cs`.
Изменяемые: `Services/Interfaces/INativeApi.cs` + `Tools/NativeApi.cs` (`RegisterWindowMessage`, `IsWindow`), `Services/Tools/DesktopIntegrationService.cs`,
`Services.Abstractions/Interfaces/{IVirtualDesktopService,IDesktopIconsService}.cs`, `Services/Tools/{VirtualDesktopService,DesktopIconsService}.cs`,
`Services/Configuration/DiConfig.cs`, `UI/Services/{IWindowManager,WindowManager,ISystemIconsPanelManager,SystemIconsPanelManager}.cs`,
`UI/Views/ChildWindow.xaml.cs` (guard внешнего destroy), `UI/App.xaml.cs` (подписка + `Start()`/dispose).

### Тесты
`ExplorerRestartMonitorTests` (Moq `INativeApi`: сообщение `TaskbarCreated` → событие; прочие — нет; сбой создания окна → деградация + лог;
смена HWND Progman → событие), `DesktopLayerRecoveryPolicyTests` (5 событий за 1 с → 1 восстановление; ретраи/лимит; новое поколение отменяет старое),
`DesktopIntegrationServiceTests` (повторный embed при том же родителе не вызывает `SetParent`), `VirtualDesktopServiceTests` (`Reset` → повторная инициализация),
`DesktopIconsServiceTests` (reapply намерения).

### DoD
1. После `taskkill /f /im explorer.exe && start explorer` все видимые панели снова на рабочем столе, в своих позициях, за окнами.
2. Скрытые иконки остаются скрытыми, галка трея корректна. 3. Боковая панель на месте, «на всех рабочих столах» работает. 4. Три рестарта
подряд — нет дублей окон/утечек, одно восстановление на всплеск. 5. Состояние на диске не меняется от восстановления. 6. 0 warnings.

### Риски
- **Неизвестно, уничтожаются ли cross-process дочерние HWND** при гибели WorkerW — план покрывает обе ветки (re-embed / recreate).
- `TaskbarCreated` может прийти до готовности Progman/WorkerW — ретраи. UIPI: при запуске с повышенными правами сообщение фильтруется — страховка Progman-опросом.
- Win11 24H2 изменил слой рабочего стола (WorkerW внутри Progman) — `GetOrCreateWorkerW` уже есть; проверить на обеих ОС.

### Manual verify (Win10/Win11)
Перезапуск Explorer (graceful из Диспетчера задач и `taskkill /f`) ×3 подряд; с панелями на 2 мониторах; с roll-up панелью; со скрытыми иконками;
с включённым «Show on all virtual desktops»; во время drag панели; с открытым окном настроек.

---

## 5. Пакет E — Drag-out элементов панели (волна 3)

**Scope: L.** Агент: **wpf-ui-builder** (ведущий) + **dotnet-services-engineer** (storage-метод).

### Решение (ADR-036, amends ADR-009/ADR-014)
- **Один OLE-жест вместо двух.** Существующий `ListReorderGestureController.DoDragDrop` получает `DataObject` с **тремя** форматами:
  приватный reorder-формат + `DataFormats.FileDrop` (путь элемента) + `"Preferred DropEffect"` = `DROPEFFECT_MOVE`. Семантику определяет
  **цель**, отдельное «вышел за границы панели» не нужно: своя панель → reorder (как сейчас), другая панель приложения → перенос,
  Explorer/рабочий стол → перемещение файла самим shell. `allowedEffects = Move`.
- **Координация** — `IPanelItemDragOutSession` (UI/Services, singleton, только UI-поток): `Begin(sourcePanelId, path)`,
  `TryClaim(targetPanelId, paths) → bool`, `End() → DragOutOutcome {Reordered | TransferredTo(id) | External}`.
- **После возврата `DoDragDrop`** (колбэк `onDragCompleted(item, effect)` в контроллере):
  - `TransferredTo` → источник убирает элемент из списка **без** `Restore` (`ChildWindowViewModel.RemoveTransferredItem`), `FilesChanged`;
  - `Reordered` → ничего;
  - `External` → **не доверять возвращённому effect** (Explorer при optimized move часто отдаёт `None`, async-drop может завершиться позже):
    проверка существования пути в T+0 / T+500 мс / T+2000 мс (`IDebounceTimer`); путь исчез → `RemoveMovedOutItem` →
    `IPanelItemStorageService.ForgetMovedOut(path)` (удалить запись манифеста и **пустой** GUID-каталог; no-op, если путь существует);
    путь на месте → элемент остаётся (копирование/отмена). Файл никогда не удаляется приложением.
- **Цель — другая панель:** `AddDroppedFiles` для `IsManaged(path)` **не** вызывает `MoveIn` (managed-хранилище общее, ADR-013: `PanelItems\<guid>\`),
  а добавляет ссылку и `TryClaim`; managed-путь без активной сессии (перетащен из Explorer из `PanelItems`) — отклоняется (иначе дубль ссылок).
- **Та же панель, пустая область** → `OnDragEnter/OnDrop`: при `session.Source == this` → `Effects = None`, no-op.
- Порталы: drag-кандидат уже `null` (read-only) — drag-out запрещён. Во время inline-rename — запрещён (существующий гейт). Selection — Single.
- Persistence: только существующий путь `FilesChanged` → debounced save `FilePaths` (как при «Remove from panel»). Схема не меняется.

### Артефакты
Новые: `UI/Services/{IPanelItemDragOutSession,PanelItemDragOutSession}.cs`, `UI/Behaviors/PanelItemDragDataFactory.cs` (сборка `DataObject`, чистая),
`UI/Behaviors/MovedOutVerifier.cs` (расписание проверок).
Изменяемые: `UI/Behaviors/ListReorderGestureController.cs` (опц. `createDataObject`, `onDragCompleted`; `SystemIconsPanelWindow` не затронут — параметры опциональны),
`UI/Views/ChildWindow.xaml.cs`, `UI/ViewModels/ChildWindowViewModel.cs` (`AddDroppedFiles`, `RemoveTransferredItem`, `RemoveMovedOutItem`),
`Services.Abstractions/Interfaces/IPanelItemStorageService.cs` + `Services/Tools/PanelItemStorageService.cs` (`ForgetMovedOut`), `UI/Configuration/DiConfig.cs`.

### Тесты
`PanelItemDragOutSessionTests` (begin/claim/end; claim источником = Reordered; несовпадающие пути → false; End сбрасывает), `PanelItemDragDataFactoryTests`
(3 формата, `Preferred DropEffect` = 2; портал/отсутствующий путь → null), `MovedOutVerifierTests` (fake `IDebounceTimer`: исчез на 2-й проверке → remove; не исчез → остаётся),
`ChildWindowViewModelTests` (managed + сессия → без `MoveIn`, ссылка добавлена; managed без сессии → отклонён; `RemoveTransferredItem` не вызывает `Restore`),
`PanelItemStorageServiceTests.ForgetMovedOut` (путь существует → no-op; отсутствует → манифест/пустой каталог очищены), `ListReorderGestureController` — регрессия reorder.

### DoD
1. Элемент → рабочий стол/Explorer: файл перемещён shell-ом, исчез из панели, манифест без записи. 2. Элемент → другая панель: перенос без
копирования/перемещения файла, исчез из источника. 3. Reorder в своей панели работает как прежде. 4. Портал — drag-out невозможен. 5. Отмена
(Esc)/копирование — элемент остаётся. 6. 0 warnings.

### Риски
- Панели — дети WorkerW с `WS_EX_NOACTIVATE`: OLE-drag из такого окна и drop на `SHELLDLL_DefView` — только ручная проверка.
- Optimized move vs async drop — закрыто проверкой существования; кросс-томовый drop (другой диск) — shell делает copy+delete сам.
- Drop на рабочий стол, где лежит панель, — цель — сама панель (не десктоп); ожидаемо.
- `WH_MOUSE_LL`-лассо не должно стартовать: mouse-down на панели, не на десктопе — проверить.

### Manual verify (Win10/Win11)
Файл и папка → рабочий стол, → Explorer (тот же диск, другой диск, сетевой), → другая панель, → та же панель (пустое место/reorder); Esc посреди drag;
Ctrl во время drag (copy — элемент остаётся); элемент, не-managed (ссылка на оригинал); портал.

---

## 6. Пакет F — Per-monitor DPI (волна 1: спайк F0 — выполнен; волна 4: реализация F1–F4)

**Перепланирован по итогам F0** ([результаты](release-hardening-1.0-f0-results.md)): на Win11 24H2+ панели top-level (WorkerW не
найден, `SetParent` не выполняется), на Win10 / Win11 ≤ 23H2 — дети WorkerW. Решение оркестратора — **вариант A**: оба режима в 1.0.

**Детальный под-план: [release-hardening-1.0-F-dpi.md](release-hardening-1.0-F-dpi.md).** Он полностью заменяет прежние F1–F4,
инвентаризацию px↔DIP, тесты, DoD, риски и manual verify этого раздела (прежний «ключевой архитектурный факт» верен только для режима E).

Кратко: **Scope XL.** Манифест PerMonitorV2 (только UI); одна абстракция `IDesktopScaleProvider` / `DesktopScaleModel`
(режим E — единый масштаб DPI корня WorkerW, режим T — effective DPI монитора; режим пересчитывается при рестарте Explorer и смене
дисплеев); все px↔DIP — через неё; persistence заморожена (числа неизменны на односкейловых конфигурациях, без миграции).
F1 win32-interop-specialist → (F2 dotnet-services-engineer ∥ F3 wpf-ui-builder) → F4 tdd-test-engineer. Волна 4 после C и E;
пересечение с G2 по обоим `DiConfig.cs` — порядок в под-плане §9.

---

## 7. Пакет G — Release engineering

Разбит на **G1** (без кода, волна 1) и **G2** (update check + About, волна 4). ADR-038.

### G1 — инсталлятор, документы, CI (Scope M). Агент: **dotnet-services-engineer**
- `installer/DesktopOrganizer.iss`: `#define MyAppURL "https://github.com/messire"`; `AppPublisherURL={#MyAppURL}`, `AppSupportURL={#MyAppURL}`
  (заменить плейсхолдер); `MyAppPublisher` — Q4. Хук подписи: `#ifdef SignEnabled` → `SignTool=dosign` + `SignedUninstaller=yes`.
- `installer/build.ps1`: если заданы `DESKTOPORGANIZER_SIGN_CERT` (+`DESKTOPORGANIZER_SIGN_PASSWORD`, опц. `DESKTOPORGANIZER_SIGN_TIMESTAMP`,
  `SIGNTOOL_PATH`) — `signtool sign /fd sha256 /tr … /td sha256` для `DesktopOrganizer*.exe/dll` в publish, затем ISCC с
  `/DSignEnabled "/Sdosign=signtool … $f"`; иначе — сообщение «подпись пропущена» и прежнее поведение. Секреты не печатаются.
  Добавить `-p:ContinuousIntegrationBuild=true` (детерминированные `/_/`-пути в стеках — поддерживает приватность crash-логов A).
- `README.md` (корень): en-раздел + ru-раздел: что это, возможности, установка/удаление, где данные и логи (`%LOCALAPPDATA%\DesktopOrganizer`),
  `--diag`, `--restore-state-backup`, проверка обновлений (сеть только по кнопке), известные ограничения (смешанный DPI, Win11 24H2), лицензия, ссылка поддержки.
- `LICENSE` — MIT, `Copyright (c) 2026 messire` — **Q4, требует подтверждения пользователя**.
- `CHANGELOG.md` — Keep a Changelog; 0.1.0…0.6.1 из тел squash-коммитов `vX.Y.Z` (`git log --format=%B`) / тегов; отсутствующие ранние версии пометить.
- `.github/workflows/ci.yml`: `windows-latest`, `actions/setup-dotnet` 8.0.x, restore, `dotnet build -c Release -p:TreatWarningsAsErrors=true`
  (гейт 0 warnings), `dotnet test` с фильтром `Category!=RequiresDesktop` (тесты, требующие Explorer/WorkerW, пометить `[Trait]`), на `push tags v*`:
  `choco install innosetup` → `installer/build.ps1` → `upload-artifact` `installer/Output/*.exe`. Подпись в CI — только если заданы secrets.
- Тесты: `InstallerMetadataContractTests` (URL-ы = константа, `SignTool` только под `#ifdef`); существующие `Installer*ContractTests` — зелёные.
- DoD: инсталлятор собирается без env подписи; с тестовым self-signed сертификатом — подписаны exe/установщик/деинсталлятор; CI зелёный; README/LICENSE/CHANGELOG в корне.
- Manual verify: «Программы и компоненты» показывают ссылки; SmartScreen с подписью/без.

### G2 — проверка обновлений и ссылки в About (Scope M). Агент: **wpf-ui-builder** + **dotnet-services-engineer** (HTTP-сервис)
- `Core/Versioning/SemVersion.cs` — парсер/компаратор SemVer 2.0 (срез `v`, `+build`; prerelease < release).
- `Services.Abstractions/Interfaces/IUpdateCheckService.cs` + `UpdateCheckResult { Status: UpToDate|UpdateAvailable|NoReleases|Failed, Latest, ReleaseUrl, FailureKind }`.
- `Services/Updates/GitHubReleaseUpdateChecker.cs` + `Services/Updates/UpdateEndpoints.cs` (константы: `Repo = "messire/DesktopOrganizer"`,
  API `https://api.github.com/repos/messire/DesktopOrganizer/releases/latest`, `ReleasesUrl`, `IssuesUrl = …/issues/new`). Один `HttpClient`
  (владелец — сервис, dispose с контейнером), таймаут 10 с, `User-Agent: DesktopOrganizer/<AppVersionInfo.Display>`, `Accept: application/vnd.github+json`,
  лимит тела 256 KB, `System.Text.Json`; 404 → `NoReleases`; 403/429 → `Failed(RateLimited)`; `html_url` открывается, только если начинается с
  `https://github.com/messire/`, иначе `ReleasesUrl`. Internal ctor с `HttpMessageHandler` для тестов. **Ничего не скачивает и не запускает.**
- **Без persisted state:** нет «последней проверки», нет автопроверки при старте — только кнопка; результат живёт в памяти VM.
- `UI/ViewModels/Settings/AboutViewModel.cs`: `CheckForUpdatesCommand` (async, `IsChecking` блокирует кнопку), статус (локализованные ключи + аргументы,
  горячая смена языка через `LocBinding`), `OpenReleasesCommand`, `ReportProblemCommand` (открывает `IssuesUrl`, без данных пользователя),
  `OpenLogsFolderCommand` (`Directory.CreateDirectory(Logs)` → Explorer). URL-ы открываются через `Process.Start(UseShellExecute)` в тонком
  `IExternalLinkLauncher` (UI/Services) — тестируемо.
- `UI/Views/Settings/AboutSection.xaml`: кнопка «Проверить обновления», inline-статус, ссылки «Страница релизов», «Сообщить о проблеме», «Открыть папку логов»;
  значение `Settings_About_Diagnostics_Message` → «Журналы: папка логов; подробный режим — запуск с --diag».
- `UI/ViewModels/MainWindowViewModel.cs` (ctor-параметр + свойство `About`), `UI/Configuration/DiConfig.cs`, `Services/Configuration/DiConfig.cs` (регистрация checker).
- Тесты: `SemVersionTests`; `GitHubReleaseUpdateCheckerTests` (fake handler: новее/равно/старее/prerelease/404/403/таймаут/битый JSON/огромное тело/чужой `html_url`;
  без реальной сети); `AboutViewModelTests` (IsChecking, ключи статуса, команды открывают константные URL, `Mock<IAppSettings>` — `VerifyNoOtherCalls`).
- DoD: кнопка показывает «актуальная версия / доступна X.Y.Z (ссылка) / не удалось проверить» (en/ru); офлайн — понятная ошибка, UI не зависает;
  ни одного нового файла/ключа настроек; 0 warnings.
- Риски: rate limit GitHub (60/ч без токена) — достаточно для ручной кнопки; прокси/корпоративный TLS → `Failed`; репозиторий ещё не опубликован → `NoReleases`/404.
- Manual verify: онлайн/офлайн; смена языка при показанном статусе; ссылки открываются в браузере по умолчанию.

---

## 8. Карта параллелизации

Правило: в одной волне **нет двух пакетов, правящих один и тот же файл** (по таблице 0.5); ≤3 агента на волну; resx — только по правилу 0.3.

| Волна | Пакеты (агенты) | Почему не конфликтуют |
|---|---|---|
| **1** | **B** (dotnet-services-engineer) ∥ **G1** (dotnet-services-engineer) ∥ **F0** (win32-interop-specialist) | B — единственный в волне, кто трогает `src/`; G1 — только `installer/`, корень, `.github/`; F0 — только `tools/dpi-probe/` и новый doc |
| **2** | **A** (dotnet-services-engineer) ∥ **D** (dotnet-services-engineer + win32-interop-specialist) | A: `App.xaml.cs`, `Program.cs`, новые `UI/Lifecycle/*`, `Services/Diagnostics/{CrashReportWriter,WinFormsExceptionBridge}`; D: `PanelItemStorageService`, `INativeApi/NativeApi`, `WindowManager`, `PageManagerService`, `MainWindowViewModel`, `ChildWindowViewModel`, `ConfirmationService`. Общий только resx — разные якоря (A после `Startup_Failed_Format`, D после `Settings_Confirm_RemovePanel_Title`) |
| **3** | **C** (win32-interop-specialist + wpf-ui-builder) ∥ **E** (wpf-ui-builder + dotnet-services-engineer) | C: `App.xaml.cs`, `WindowManager`, `INativeApi/NativeApi`, `DesktopIntegrationService`, `SystemIconsPanelManager`, `VirtualDesktopService`, `DesktopIconsService`, `ChildWindow.xaml.cs` (**только** guard внешнего destroy в `OnClosed`-области), Services `DiConfig`; E: `ChildWindow.xaml.cs` (drag/drop-область), `ChildWindowViewModel`, `ListReorderGestureController`, `PanelItemStorageService`, UI `DiConfig`, resx. **Единственное пересечение — `ChildWindow.xaml.cs`** (разные регионы; перечитывать перед правкой). Если оркестратор требует строгого нуля — C-правку `ChildWindow.xaml.cs` вынести в `ChildWindow.ShellRecovery.cs` (partial, новый файл) |
| **4** | **F1–F4** (win32-interop-specialist + wpf-ui-builder) ∥ **G2** (wpf-ui-builder + dotnet-services-engineer) | F: `app.manifest`, UI csproj, `MonitorService`, `IMonitorService`, `MonitorInfo`, `INativeApi/NativeApi`, `App.xaml.cs`, `LassoPanelGeometry`, `SystemIconsPanelManager`; G2: `AboutSection.xaml`, `AboutViewModel`, `MainWindowViewModel`, оба `DiConfig`, `Core/Versioning`, `Services/Updates`, resx. Пересечений нет |

Критический путь: B → (A, D) → (C, E) → F. G1/F0 — вне критического пути. После каждого пакета — конвейер CLAUDE.md
(tdd-test-engineer → [debug-healer] → code-quality-reviewer ∥ fences-ux-parity-reviewer → architecture-documenter) **до** старта следующей волны,
потому что следующая волна правит те же горячие файлы.

Почему такой порядок: B первым — фундамент (`IAppLog`, `AppVersionInfo`, `LogScrubber`) для A/C/D/E/G2 и самый «широкий» по файлам;
D до E — оба правят `PanelItemStorageService`/`IPanelItemStorageService`/`ChildWindowViewModel`; C до F — F обязан перепроверить восстановление
после рестарта Explorer под PMv2; F последним — наибольший риск регрессий геометрии, изолирован от остальных изменений.

---

## 9. ADR (для architecture-documenter)
- ADR-033 — Crash safety + always-on app log (A+B); amends ADR-027 (уровни Standard/Verbose, фасад `IAppLog`, ambient `AppLog`), ADR-023 (код выхода 5).
- ADR-034 — Explorer restart recovery (C); amends ADR-001, ADR-019/020.
- ADR-035 — Удаление панели через Корзину, fail-visibly (D); amends ADR-010.
- ADR-036 — Drag-out элементов (E); amends ADR-009, ADR-014.
- ADR-037 — PerMonitorV2 + единый desktop-layer scale (F); amends ADR-004, ADR-022 D-6, ADR-028 (DPI-awareness).
- ADR-038 — Release engineering + ручная проверка обновлений (G); первая сетевая функция приложения — приватность/безопасность.

## 10. Открытые вопросы (CLARIFICATION, с дефолтами)
- **Q1 (A, заморозка «когда пишется»):** сохранение при краше. A (дефолт) — Dispatcher-краш идёт через штатный `OnExit` с существующим `SaveNow`
  за write-barrier, фоновый краш — без записи; B — никогда не сохранять при краше (флаг в `OnExit`); C — пытаться всегда (маршалинг на UI с таймаутом 2 с).
- **Q2 (A):** SessionEnding запускает существующую shutdown-последовательность раньше (те же записи). A (дефолт) — да; B — только иконки, запись оставить `OnExit`.
- **Q3 (B):** разрешить замену `Console.Error` на лог-вызов (без смены control flow) в 4 файлах контура persistence. A (дефолт) — да; B — не трогать, allowlist.
- **Q4 (G1):** LICENSE MIT на «messire»? `MyAppPublisher` «khagen» → «messire»? A — MIT/messire/messire; B — MIT/messire, publisher без изменений; C — другая лицензия.
- **Q5 (F):** принять единый масштаб WorkerW-панелей на смешанном DPI (визуально не per-monitor) как известное ограничение 1.0. A (дефолт) — да, по итогам F0; B — перепланировать панели в top-level окна (вне пакета).
- **Q6 (D):** при сбое Корзины — A (дефолт) панель остаётся; B — панель удаляется, файлы остаются в `PanelItems` + сообщение с путём.

## 11. Сводный manual verify (Win10/Win11)
См. разделы пакетов; обязательный минимум перед 1.0: краш UI/фон + kill при скрытых иконках (A); логи Release без флагов (B); тройной рестарт Explorer
на 2 мониторах (C); удаление панели/страницы → Корзина → восстановление, отказ Корзины (D); drag-out на десктоп/Explorer/другую панель/другой диск (E);
смешанный DPI 100/150 с монитором слева, смена масштаба на лету (F); установщик со ссылками, подпись, кнопка обновлений онлайн/офлайн (G).
