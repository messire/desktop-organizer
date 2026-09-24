# Phase 3 — Single Instance + IPC + Lifecycle Hardening

Источники правды: `docs/Refactoring/PHASES/PHASE-3-single-instance-ipc.md` (спецификация),
`adr/ADR-023-single-instance-and-ipc.md` (Proposed), `docs/Refactoring/BACKLOG.md` DO-001,
`docs/REFACTORING_PLAN.md` §5.2/§5.4, `docs/Refactoring/Templates/SingleInstanceCoordinator.cs`
(черновик формы API — **не** продакшен-код, см. D-0), `installer/DesktopOrganizer.iss:54-57`.

**Scope: L** (Services.Abstractions + Services + UI entry point + installer-контракт + docs).
Не XL: нет нового нативного модуля и нет смены формата persistence (storage-lock — Фаза 4).
Разбит на 7 логических коммитов C1–C7.

---

## 0. Точное текущее поведение (что именно ломается сейчас)

`src/DesktopOrganizer.UI/App.xaml.cs`:

- строка 25 — `private readonly Mutex _installerDetectionMutex = new(initiallyOwned: false, "DesktopOrganizerSingleInstanceMutex");`
  Поле-инициализатор → создаётся в конструкторе `App`, **до** `StartApp`. `WaitOne` не вызывается
  никогда, владение не проверяется. Единственный потребитель имени — `installer/DesktopOrganizer.iss:57`
  (`AppMutex=DesktopOrganizerSingleInstanceMutex`), Inno проверяет **существование** объекта, не владение.
- `StartApp` (Startup-хендлер из `App.xaml`, `ShutdownMode="OnExplicitShutdown"`): `InitTrayIcon()` →
  `MonitorService.Start()` → `LoadPanels()` → диагностика → `InitDesktopGestures()` (ставит
  `WH_MOUSE_LL`) → `SystemIconsPanel.Load()/Apply()`. Ни одна ступень не проверяет, запущен ли уже
  экземпляр.
- `OnExit`: `RestoreDesktopIcons()` → `SystemIconsPanel.Save()` → `WindowManager.SaveWindowsStateNow()`
  → `GestureDiagnostics.SessionExiting()` → `DiagnosticsLog.Flush()` → `_serviceProvider.Dispose()` →
  `_installerDetectionMutex.Dispose()`.

### 0.1. Ключевая находка плана (R-1): «тихий второй процесс» не тихий — он стирает состояние

`WindowManager.SaveWindowsStateNow()` → `SaveScheduler.Flush()` → **безусловный** `_save()` →
`PersistWindowsState()`: сериализует `GetAllChildWindows()`. В процессе, который **не** вызывал
`LoadWindowsState()`, коллекция пуста → `json == "[]"`, `_lastSavedJson == null` → guard
`json == _lastSavedJson` не срабатывает → `Properties.Settings.Default.ChildWindowsState = "[]"` +
`Save()`. То есть **любой** второй процесс, дошедший до `OnExit`, стирает все панели пользователя.
Аналогично `SystemIconsPanel.Save()` (строка 94) пишет DTO незагруженной модели, а
`RestoreDesktopIcons()` возвращает иконки рабочего стола, которые primary намеренно скрыл.

Следствие для дизайна: **нельзя** реализовывать guard как «second instance доходит до `StartApp`,
зовёт `Shutdown()`» — WPF всё равно прогонит `OnExit`. Отсюда решение D-2 (свой `Program.Main`).

---

## 1. Архитектурные решения (ADR-023 delta)

ADR-023 в черновике фиксирует только 3 инварианта. Ниже — решения, которые обязаны попасть в него
при переводе Proposed → Accepted (коммит C7).

### D-0. Черновик `Templates/SingleInstanceCoordinator.cs` — только форма API

Прецедент Фазы 2 (`docs/Refactoring/Native/*.cs`, план Фазы 2 §D-1): namespace
`DesktopOrganizer.UI.Lifecycle` и конструктор `(string name)` в продакшен не переносятся. Причины:
(1) mutex + Named Pipe — ресурсы ОС, их место по конвенции проекта — `DesktopOrganizer.Services`
(реализация) за интерфейсом в `Services.Abstractions`, как `ISystemShellService`/`IMonitorService`;
(2) `TryAcquire()`-only API не покрывает IPC, retry-политику и shutdown-протокол. Черновик остаётся
на месте как артефакт пакета, не удаляется.

### D-1. Два разных mutex'а: detection-контракт и guard-контракт разделены

| Объект | Имя | Семантика | Кто зависит |
|---|---|---|---|
| detection | `DesktopOrganizerSingleInstanceMutex` (**без префикса, verbatim**) | «существует ⟺ процесс приложения жив» | `installer/DesktopOrganizer.iss:57` `AppMutex=` |
| guard | `Local\DesktopOrganizer.SingleInstance.{sidHash}` | «владение ⟺ я primary» | только приложение |

Почему не переиспользовать один объект: контракт инсталлятора — **существование**, контракт
guard'а — **владение**; склеив их, мы навсегда лишаемся права переименовать/переспецифицировать
guard, не выпуская новый инсталлятор. Цена — один лишний хэндл на процесс.

**Имя detection-мьютекса менять запрещено**, включая добавление эквивалентного префикса `Local\`
(семантически тот же объект, но нулевой выигрыш при ненулевом риске). Закрепляется тестом
`InstallerMutexContractTests` (§5, T-INS-01), который парсит `.iss` и сверяет строку с константой.

`{sidHash}` = первые 16 hex-символов SHA-256 от `WindowsIdentity.GetCurrent().User.Value`
(fallback — `Environment.UserName` в верхнем регистре, если SID недоступен). Сырой SID в имя объекта
не кладём: имя pipe'а машинно-глобально и видно всем в системе.

### D-2. Guard выполняется в собственном `Program.Main`, до создания `Application`

Secondary-процесс **не должен создавать `App`** — иначе он проходит `OnExit` (R-1). Реализация:

```xml
<!-- src/DesktopOrganizer.UI/DesktopOrganizer.UI.csproj -->
<PropertyGroup>
  <StartupObject>DesktopOrganizer.UI.Program</StartupObject>
</PropertyGroup>
<ItemGroup>
  <ApplicationDefinition Remove="App.xaml" />
  <Page Include="App.xaml" SubType="Designer" Generator="MSBuild:Compile" />
</ItemGroup>
```

`Program.Main` сам вызывает `app.InitializeComponent(); app.Run();`. Побочные выигрыши:

- secondary не поднимает tray-иконку (нет мигания в трее), не ставит `WH_MOUSE_LL`, не читает
  `user.config`, не трогает `PanelItemStorageService`;
- secondary **не резолвит `IDiagnosticsLog`** → не создаёт файл сессии JSONL. Критично для DoD «20
  быстрых запусков»: retention `JsonLinesDiagnosticsLog` — 10 файлов сессий, 20 secondary-процессов
  в Debug-сборке вымели бы лог primary (см. R-12);
- время жизни secondary ≈ время рукопожатия (десятки мс).

**Fallback (Опция A), если правка `ApplicationDefinition` ломает сборку (CS0017 / проблемы с
`InitializeComponent`):** guard в конструкторе `App` до `BuildServiceProvider()`, флаг `_isSecondary`,
`Shutdown()` в `StartApp`. В этом варианте **обязательны** (не «желательны») early-return в `OnExit`
и вынос тела teardown в тестируемый `AppTeardown` с тестом «при роли Secondary не вызвано ничего».
Решение фиксируется в ADR-023 по факту сборки.

### D-3. Флаг `_isSecondary` в `OnExit` — defense in depth в любом варианте

Даже при D-2 (secondary никогда не создаёт `App`) первым действием `OnExit` остаётся ранний выход,
если роль процесса ≠ Primary. Три строки, ноль стоимости, закрывает R-1 на случай будущего рефакторинга
точки входа.

### D-4. Pipe с `maxNumberOfServerInstances: 1` — машинно-глобальный тай-брейкер поверх mutex'а

Mutex без префикса/с `Local\` — объект **сессии**, не пользователя. Быстрое переключение пользователей
(FUS: один пользователь, две сессии) дало бы два «primary», пишущих в один `%LOCALAPPDATA%`.
`Global\`-мьютекс не вариант: создание объекта в Global-namespace требует `SeCreateGlobalPrivilege`,
которой у обычного (не-elevated) пользователя нет, а приложение ставится per-user
(`PrivilegesRequired=lowest`).

Решение: namespace именованных каналов — машинно-глобальный. Сервер создаётся с
`maxNumberOfServerInstances: 1`; вторая сессия того же пользователя получает при создании
`IOException` (`ERROR_PIPE_BUSY`) → **считает себя secondary**, освобождает guard-мьютекс, отправляет
активацию и выходит. То есть порядок арбитража: `mutex (быстро, per-session)` → `pipe-сервер
(авторитетно, per-user машинно)`.

Следствие/цена: единственный инстанс pipe'а можно «занять» зависшим клиентом → каждое соединение
обслуживается с жёстким таймаутом 500 мс и безусловным `Dispose` (см. §2.4).
Другие ошибки создания сервера (ACL и т.п.) → остаёмся primary в degraded-режиме
(активация не работает, событие `ActivationServerUnavailable` в лог), приложение стартует.

### D-5. Протокол: одна строка, версия в каждом сообщении, ответ обязателен

```
request :  "DO/1 ACTIVATE_SETTINGS s=<sessionId>\n"     (UTF-8, ≤256 байт)
response:  "DO/1 OK\n" | "DO/1 SHUTTING_DOWN\n" | "DO/1 OTHER_SESSION\n" | "DO/1 UNSUPPORTED\n"
```

- `SHUTTING_DOWN` — primary уже в `OnExit`. Именно это убирает гонку «primary выходит + одновременный
  запуск»: secondary получает **детерминированный** сигнал «повтори захват владения» вместо гадания
  по таймауту (см. D-6).
- `OTHER_SESSION` — primary живёт в другой сессии Windows (FUS, см. D-4): активировать окно там
  бессмысленно, secondary показывает сообщение и выходит. `s=<sessionId>` в запросе =
  `Process.GetCurrentProcess().SessionId` (без P/Invoke).
- `UNSUPPORTED` — неизвестная команда/версия. Правило совместимости: сервер обязан отвечать,
  а не закрывать соединение молча, — иначе будущий клиент не отличит «старый primary» от «primary завис».
- v1 содержит ровно одну команду `ACTIVATE_SETTINGS`. Расширение (`OPEN_PANEL`, аргументы командной
  строки) — отдельная версия `DO/2`, не в этой фазе.

Если реализующий агент сочтёт, что поле `s=` раздувает C3, его можно отложить до `DO/1.1`, но код
ответа `OTHER_SESSION` обязан остаться зарезервированным в кодеке и покрыт тестом.

### D-6. Рукопожатие: чередование «захватить владение / отправить команду», бюджет 3 с

Чистая политика (`SingleInstanceHandshakePolicy`), никакого `Thread.Sleep` внутри логики — задержки
возвращаются наружу, время подаётся инжектируемым `Func<long> nowMs`:

| Попытка | Действие | Таймаут connect | Пауза перед следующей |
|---|---|---|---|
| 1 | TryAcquire → (fail) TrySend | 300 мс | 0 |
| 2 | TryAcquire → TrySend | 300 мс | 50 мс |
| 3 | TryAcquire → TrySend | 300 мс | 100 мс |
| 4 | TryAcquire → TrySend | 300 мс | 200 мс |
| 5 | TryAcquire → TrySend | 300 мс | 400 мс |
| 6 | TryAcquire → TrySend | 300 мс | 800 мс |
| — | бюджет исчерпан (3000 мс) → `GiveUp` | | |

`TryAcquire` перед каждой отправкой — это и есть обработка «primary выходит прямо сейчас»
(`SHUTTING_DOWN` или `NoListener`). Обратная гонка «primary захватил mutex, но ещё не поднял сервер»
закрывается тем, что primary поднимает сервер **сразу** после захвата, до всего остального startup
(§2.5), плюс теми же повторами.

### D-7. При неудачном рукопожатии writable-инстанс НЕ запускается

Исчерпан бюджет, mutex занят, primary не отвечает (завис) → secondary показывает `MessageBox`
(«DesktopOrganizer уже запущен, но не отвечает. Завершите процесс в Диспетчере задач.») и выходит с
кодом 3. Инвариант «storage никогда не открыт двумя writable-инстансами» приоритетнее удобства;
никаких `--force-new-instance`.

### D-8. Активация — только `IWindowManager.ShowMainWindow()`

Обработчик не трогает панели, страницы, `SystemIconsPanel`, `DesktopIcons`, `MonitorService`.
Пиннится тестом на Moq-моке `IWindowManager`: ровно один вызов `ShowMainWindow()` + `VerifyNoOtherCalls()`
(это и есть машинно-проверяемая формулировка DoD «без изменения desktop panels»).

`AllowSetForegroundWindow(ASFW_ANY = -1)` вызывается **клиентом** (secondary) непосредственно перед
отправкой — иначе `Window.Activate()` в primary по правилам foreground-lock только мигнёт кнопкой в
панели задач. P/Invoke добавляется в `INativeApi` (инвариант CLAUDE.md: `[DllImport]` больше нигде).

### D-9. Диспозинг: три фазы, mutex освобождается последним

```
App.OnExit:
  1. coordinator.EnterShutdown()        // сервер жив, но отвечает SHUTTING_DOWN
  2. <существующий teardown: RestoreDesktopIcons / SystemIconsPanel.Save / SaveWindowsStateNow / диагностика>
  3. _serviceProvider.Dispose()         // → coordinator.Dispose(): стоп сервера (bounded 2 c),
                                        //   release guard mutex, dispose detection mutex
```

Guard-мьютекс освобождается только после того, как все записи завершены, — иначе новый процесс начнёт
писать в `user.config` поверх ещё идущего `Settings.Save()`.

**Инвариант потоковой привязки (R-5):** `Mutex` принадлежит *потоку*. `TryAcquire`/`ReleaseMutex`
выполняются строго на главном (STA) потоке — `Program.Main` и `App.OnExit` это один и тот же поток.
Поток pipe-сервера обязан не касаться мьютекса ни при каких условиях. `ReleaseMutex` обёрнут в
`try/catch (ApplicationException)`: провал release не фатален — ОС освободит объект при выходе
процесса, следующий экземпляр получит `AbandonedMutexException` и корректно станет primary.

---

## 2. Новые и изменяемые файлы

### 2.1. Новое — `DesktopOrganizer.Services.Abstractions`

| Файл | Содержимое |
|---|---|
| `Interfaces/ISingleInstanceCoordinator.cs` | интерфейс + все публичные enum'ы/record'ы фазы (один файл, прецедент `INativePanelSizingService.cs`) |

```csharp
namespace DesktopOrganizer.Services.Abstractions;

public enum SingleInstanceRole { Primary, Secondary }

/// <summary>Что должна сделать точка входа процесса по итогам рукопожатия.</summary>
public enum SingleInstanceStartupAction { RunAsPrimary, ExitAfterActivation, ExitActivationFailed, ExitOtherSession }

public enum ActivationSendResult { Delivered, PrimaryShuttingDown, OtherSession, NoListener, Timeout, ProtocolError, Faulted }

/// <summary>v1: ровно одна команда. Расширение — новая версия протокола (ADR-023 D-5).</summary>
public enum ActivationCommand { ActivateSettings }

public readonly record struct ActivationRequest(int ProtocolVersion, ActivationCommand Command, int SessionId);

public sealed record SingleInstanceStartupDecision(
    SingleInstanceStartupAction Action,
    SingleInstanceRole Role,
    ActivationSendResult? SendResult,
    int Attempts,
    long ElapsedMs);

public interface ISingleInstanceCoordinator : IDisposable
{
    /// <summary>Выполняет всё рукопожатие (bounded, ≤ бюджета политики). Никогда не бросает.
    /// Вызывается ровно один раз, с главного STA-потока.</summary>
    SingleInstanceStartupDecision Start();

    /// <summary>Поднимает pipe-сервер. Идемпотентно. Только для Primary; при
    /// <c>ERROR_PIPE_BUSY</c> возвращает <see langword="false"/> — вызывающий обязан
    /// деградировать до Secondary (ADR-023 D-4).</summary>
    bool TryBeginServing();

    /// <summary>Поступившие команды активации. Может подняться на фоновом потоке
    /// ДО того, как существует <c>Application.Current</c> — см. ActivationRequestRelay.</summary>
    event EventHandler<ActivationRequest>? ActivationRequested;

    /// <summary>Переводит сервер в режим ответа <c>SHUTTING_DOWN</c>. Идемпотентно, не блокирует.</summary>
    void EnterShutdown();
}
```

### 2.2. Новое — `DesktopOrganizer.Services/SingleInstance/` (чистая часть, 100% тестируемо в CI)

Папка-аналог `Services/Diagnostics/` (там уже соседствуют чистые `DiagnosticsSwitch`/
`DiagnosticEventJsonWriter` и impure `JsonLinesDiagnosticsLog`).

| Файл | Тип | Ключевые члены |
|---|---|---|
| `SingleInstanceNaming.cs` | `public static` | `const string InstallerDetectionMutexName = "DesktopOrganizerSingleInstanceMutex";`<br>`static string GuardMutexName(string userKey)`<br>`static string ActivationPipeName(string userKey)`<br>`static string UserKeyHash(string sidOrUserName)` |
| `ActivationMessageCodec.cs` | `public static` | `const int MaxMessageBytes = 256;`<br>`static string EncodeRequest(ActivationRequest)`<br>`static bool TryDecodeRequest(string line, out ActivationRequest r, out ActivationResponseCode error)`<br>`static string EncodeResponse(ActivationResponseCode)`<br>`static bool TryDecodeResponse(string line, out ActivationResponseCode)`<br>`enum ActivationResponseCode { Ok, ShuttingDown, OtherSession, Unsupported }` |
| `SingleInstanceHandshakePolicy.cs` | `public sealed` | `SingleInstanceHandshakePolicy(int maxAttempts, TimeSpan connectTimeout, TimeSpan budget, IReadOnlyList<TimeSpan> backoff)`<br>`static SingleInstanceHandshakePolicy Default { get; }`<br>`HandshakeStep NextStep(int attempt, long elapsedMs, ActivationSendResult? last, out TimeSpan delay)`<br>`enum HandshakeStep { TryAcquire, TrySend, GiveUp }` |
| `SingleInstanceStartupDecisionTable.cs` | `public static` | `static SingleInstanceStartupDecision Resolve(bool acquired, bool serverStarted, ActivationSendResult? last, int attempts, long elapsedMs)` |
| `ActivationRequestRelay.cs` | `internal sealed` | `void Publish(ActivationRequest)` (потокобезопасно, буферизует)<br>`void AttachSink(Action<ActivationRequest> sink)` (сливает буфер + переключает на прямую доставку)<br>`int BufferedCount { get; }` |

`ActivationRequestRelay` существует потому, что сервер поднимается **до** `new App(...)`: между
`TryBeginServing()` и стартом диспетчера `Application.Current` равен `null`, а активация уже может
прийти (пользователь дважды кликнул по ярлыку). Буфер + поздняя привязка стока — единственное место,
где это состояние живёт, и оно чистое → тестируется без GUI.

### 2.3. Новое — `DesktopOrganizer.Services/SingleInstance/` (часть, трогающая ОС)

| Файл | Тип | Заметки |
|---|---|---|
| `IMutexGate.cs` | `internal interface` | `bool TryAcquire(); void Release(); bool IsOwned { get; }` + `IDisposable` |
| `MutexGate.cs` | `internal sealed` | `WaitOne(0, false)`; `catch (AbandonedMutexException) → owned = true`; `Release` в `try/catch (ApplicationException)` |
| `IActivationChannel.cs` | `internal interfaces` | `IActivationServer { bool TryStart(Action<ActivationRequest> onRequest, Func<ActivationResponseCode> statusProvider); void Stop(); }`,<br>`IActivationClient { ActivationSendResult TrySend(ActivationRequest r, TimeSpan connectTimeout); }` |
| `NamedPipeActivationServer.cs` | `internal sealed` | фоновый поток accept-цикла, `maxNumberOfServerInstances: 1`, `PipeTransmissionMode.Byte`, `PipeOptions.Asynchronous`, ACL только на текущего пользователя |
| `NamedPipeActivationClient.cs` | `internal sealed` | `NamedPipeClientStream.Connect(timeoutMs)`; `TimeoutException → NoListener`; чтение ответа через `ReadAsync(ct)` + `CancellationTokenSource(timeout)` |
| `SingleInstanceCoordinator.cs` | `internal sealed` | оркестрация; ctor: `(IMutexGate guard, IMutexGate detection, IActivationServer server, IActivationClient client, SingleInstanceHandshakePolicy policy, Func<long> nowMs, int sessionId, Func<IDiagnosticsLog> diagnostics)` |

Дисциплина accept-цикла (обязательна, ревьюер проверяет):

1. поток фоновый (`IsBackground = true`) либо `Task.Factory.StartNew(..., LongRunning)`;
2. на соединение — жёсткий бюджет 500 мс на чтение+ответ (`CancellationTokenSource`), затем
   безусловные `Disconnect()`/`Dispose()`;
3. чтение ограничено `ActivationMessageCodec.MaxMessageBytes`; больше → `Unsupported` + разрыв;
4. колбэк наружу — только `relay.Publish(...)` (никакой работы на потоке pipe'а, как в
   `WH_MOUSE_LL`-колбэке);
5. `catch (Exception)` вокруг тела итерации — цикл не умирает от одного плохого клиента;
6. `Stop()` — `cts.Cancel()` + `Dispose()` текущего `NamedPipeServerStream` (страховка от зависания
   `WaitForConnectionAsync`) + **bounded join 2 с**, ровно как `SystemShellService.Dispose`
   (`_recycleBinTimer.Dispose(waitHandle)` → `waitHandle.WaitOne(TimeSpan.FromSeconds(2))`).

ACL: `NamedPipeServerStreamAcl.Create(...)` + `PipeSecurity`, разрешающий только
`WindowsIdentity.GetCurrent().User`. Ожидается доступным «из коробки» на `net8.0-windows`; если сборка
даст `CS0246` — добавить `System.IO.Pipes.AccessControl` в `Directory.Packages.props` (CPM) и
`PackageReference` без версии в `DesktopOrganizer.Services.csproj`. Если ACL создать не удалось —
сервер **не** стартует без ACL, а возвращает `false` (degraded-primary, D-4): безопасность
межпроцессного канала не деградирует молча.

### 2.4. Новое — `DesktopOrganizer.UI`

| Файл | Содержимое |
|---|---|
| `Program.cs` | `public static class Program` + `[STAThread] public static int Main(string[] args)` |
| `Lifecycle/SingleInstanceActivationHandler.cs` | `internal sealed class SingleInstanceActivationHandler(IWindowManager windowManager, Action<Action> uiInvoke)` + `void Handle(ActivationRequest request)` |

`uiInvoke` — делегат-шов (прецедент `NativePanelResizeController`, коммит d7858e4): в проде
`a => Current.Dispatcher.Invoke(a)`, в тестах — синхронный `a => a()`. Благодаря этому D-8
проверяется unit-тестом.

Форма `Main` (псевдокод, не код для копирования):

```
[STAThread]
static int Main(string[] args)
{
    var services = new ServiceCollection();
    services.ConfigureServices();
    using var provider = services.BuildServiceProvider();      // side-effect-free: синглтоны ленивы

    var coordinator = provider.GetRequiredService<ISingleInstanceCoordinator>();
    var decision = coordinator.Start();                        // bounded ≤3 c, не бросает

    switch (decision.Action)
    {
        case ExitAfterActivation:  return 0;
        case ExitOtherSession:     ShowInfo(...);  return 4;
        case ExitActivationFailed: ShowWarn(...);  return 3;
    }

    if (!coordinator.TryBeginServing())                        // D-4: pipe занят → мы всё-таки secondary
        return SecondPass(coordinator);                        // повторная отправка + выход

    var app = new App(provider, coordinator);
    app.InitializeComponent();
    return app.Run();
}
```

Коды выхода: `0` — primary отработал / активация доставлена; `3` — рукопожатие провалено (D-7);
`4` — primary в другой сессии. Используются скриптом стресс-проверки (§6).

### 2.5. Изменяемые файлы

| Файл | Изменение | Риск |
|---|---|---|
| `src/DesktopOrganizer.UI/DesktopOrganizer.UI.csproj` | `<StartupObject>`, `ApplicationDefinition Remove` + `Page Include` для `App.xaml` | средний — единственная правка сборки в фазе (R-11) |
| `src/DesktopOrganizer.UI/App.xaml.cs` | новый ctor `internal App(ServiceProvider, ISingleInstanceCoordinator)`; удалить поле `_installerDetectionMutex` (строка 25) и его `Dispose` (строка 108); в конце `StartApp` — `relay.AttachSink(handler.Handle)` через `coordinator.ActivationRequested`; в начале `OnExit` — `EnterShutdown()` + early-return при роли ≠ Primary | высокий — головной файл фазы |
| `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` | `services.AddSingleton<ISingleInstanceCoordinator>(sp => new SingleInstanceCoordinator(...))` с `Func<IDiagnosticsLog>` (ленивая резолюция, R-12) | низкий |
| `src/DesktopOrganizer.Services/Interfaces/INativeApi.cs` + `Tools/NativeApi.cs` | `bool AllowSetForegroundWindow(int processId)` | низкий, нативно — manual verify |
| `installer/DesktopOrganizer.iss` | **только комментарий** у `AppMutex=` (строки 54-57): ссылка на `SingleInstanceNaming.InstallerDetectionMutexName` и запрет переименования. Значение не меняется | низкий; **файл уже dirty на ветке** — сверить рабочую копию перед правкой |
| `docs/Refactoring/Testing/diagnostics-log-events.md` | секция `session`: 5 новых событий (§4) | низкий |
| `adr/ADR-023-single-instance-and-ipc.md` | Proposed → Accepted + D-1…D-9 | C7 |
| `docs/ROADMAP.md`, `docs/REFACTORING_PLAN.md` §5.2/§5.4, `docs/Refactoring/BACKLOG.md` (DO-001), `docs/Refactoring/ARTIFACT_INDEX.md` | статусы | C7 |

---

## 3. Порядок работ (логические коммиты)

| # | Заголовок | Содержимое | Проверка |
|---|---|---|---|
| **C1** | `feat(lifecycle): pure single-instance handshake core` | §2.1 + §2.2 (интерфейс, naming, кодек, политика, таблица решений, relay) + их тесты. **Ничего не подключено** | build+test зелёные, поведение приложения не изменилось (0 строк в `App.xaml.cs`) |
| **C2** | `feat(lifecycle): mutex gate and named-pipe activation channel` | §2.3 кроме `SingleInstanceCoordinator`; фейки для тестов | unit на фейках + real-pipe интеграционный тест (§5) |
| **C3** | `feat(lifecycle): SingleInstanceCoordinator + DI registration` | оркестрация, регистрация синглтона. Приложение всё ещё **не** вызывает `Start()` | таблица истинности гонок на фейках (T-CO-01…07) |
| **C4** | `feat(ui): real single-instance guard at process entry point` | `Program.cs`, csproj, ctor `App`, перенос detection-мьютекса, `_isSecondary` early-return, `InstallerMutexContractTests` | **самый рискованный**; build+test; ручной запуск обязателен |
| **C5** | `feat(ui): activate existing settings window on second launch` | `SingleInstanceActivationHandler`, привязка relay в `StartApp`, `EnterShutdown()` + порядок в `OnExit` | тест D-8 (`VerifyNoOtherCalls`) |
| **C6** | `feat(diagnostics): single-instance lifecycle events + foreground handoff` | 5 событий, `INativeApi.AllowSetForegroundWindow`, `tools/lifecycle/Invoke-SingleInstanceStress.ps1`, `docs/Refactoring/Testing/single-instance-manual-matrix.md` | тесты приватности payload |
| **C7** | `docs(adr): ADR-023 Proposed→Accepted; close DO-001` | ADR-023, ROADMAP, REFACTORING_PLAN §5.2/§5.4, BACKLOG, ARTIFACT_INDEX | — |

C1 и C2 независимы и могут идти параллельно разными агентами. C4 и C5 — строго последовательно
за C3. C6 можно вести параллельно с C5.

---

## 4. Диагностические события (категория `session`, `scope = null`)

Добавляются в `docs/Refactoring/Testing/diagnostics-log-events.md`. Privacy-инвариант соблюдён: ни
одной пользовательской строки, только enum-имена/числа/bool. `{sidHash}` в payload **не пишется**
(деривация от SID).

| Событие | Payload |
|---|---|
| `SingleInstancePrimaryAcquired` | `attempts`, `elapsedMs`, `wasAbandoned` (bool — предыдущий primary был убит), `serverStarted` (bool) |
| `SingleInstanceSecondaryDetected` | `attempts`, `elapsedMs`, `sendResult` (enum-имя) |
| `ActivationRequestReceived` | `command`, `protocolVersion`, `sameSession` (bool), `bufferedAtArrival` (bool — пришло до готовности UI) |
| `ActivationServerUnavailable` | `reason` (enum: `PipeBusy`/`AclFailed`/`Faulted`) |
| `SingleInstanceShutdownSequence` | `stopServerMs`, `serverStopTimedOut` (bool), `mutexReleased` (bool) |

Пишутся только в primary (в secondary `IDiagnosticsLog` не резолвится — D-2/R-12); строка о
secondary попадает в лог primary через `ActivationRequestReceived`.

---

## 5. Тесты

Директории по существующей конвенции: `tests/DesktopOrganizer.Tests/Unit/SingleInstance/`,
`Unit/Lifecycle/`, `Integration/SingleInstance/`, `Unit/Installer/`.
`[Trait]` — согласовать с `Helpers/TestCategory.cs`/`TestGroup.cs` (DO-008: таксономия уже
несогласована, новые файлы не должны её ухудшать).

### 5.1. Чистая логика (гарантированно зелёные в этой среде)

| ID | Файл | Что пиннит |
|---|---|---|
| T-NAM-01..04 | `SingleInstanceNamingTests` | detection-имя == литерал; guard-имя начинается с `Local\`; hash детерминирован и не содержит сырого SID; разные SID → разные имена |
| T-COD-01..08 | `ActivationMessageCodecTests` | round-trip; неизвестный глагол → `Unsupported`; `DO/2` → `Unsupported`; обрезанная строка без `\n`; >256 байт; пустая строка; мусорные байты; round-trip всех кодов ответа (включая `OtherSession`) |
| T-POL-01..05 | `SingleInstanceHandshakePolicyTests` | порядок шагов; backoff-расписание; остановка по бюджету, а не по числу попыток; `PrimaryShuttingDown` → следующий шаг `TryAcquire`; `Delivered` → `GiveUp` |
| T-DEC-01..06 | `SingleInstanceStartupDecisionTableTests` | таблица истинности `(acquired × serverStarted × sendResult)` → action |
| T-REL-01..04 | `ActivationRequestRelayTests` | буферизация до `AttachSink`; слив в порядке поступления; потокобезопасность (N потоков публикуют, счётчик сходится); публикация после attach доставляется напрямую |

### 5.2. Оркестрация на фейках (`FakeMutexGate`, `InMemoryActivationChannel`) — главный набор

| ID | Сценарий | Ожидание |
|---|---|---|
| T-CO-01 | mutex свободен | `RunAsPrimary`, сервер стартовал, команда никому не отправлялась |
| T-CO-02 | mutex занят, сервер отвечает `Ok` | `ExitAfterActivation`; primary получил ровно один `ActivateSettings` |
| T-CO-03 | mutex занят, первые 2 ответа `ShuttingDown`, затем mutex освободился | `RunAsPrimary` за ≤ бюджета; ровно 3 попытки |
| T-CO-04 | mutex занят, соединения нет вообще (зависший primary) | `ExitActivationFailed`, попыток = `maxAttempts`, `elapsedMs ≤ budget` |
| T-CO-05 | `AbandonedMutexException` при захвате | `RunAsPrimary`, `wasAbandoned == true` |
| T-CO-06 | mutex захвачен, но `TryBeginServing` → `PipeBusy` (D-4, FUS) | guard освобождён, итог — отправка активации; ответ `OtherSession` → `ExitOtherSession` |
| T-CO-07 | `Dispose` без `Start`; двойной `Dispose`; `EnterShutdown` дважды | не бросает, идемпотентно, mutex не «освобождается» без владения |
| T-CO-08 | активация приходит до `AttachSink` | доставлена после привязки, порядок сохранён |

### 5.3. UI

| ID | Файл | Что пиннит |
|---|---|---|
| T-ACT-01 | `SingleInstanceActivationHandlerTests` | ровно `ShowMainWindow()` + `VerifyNoOtherCalls()` на `IWindowManager` (**машинная формулировка DoD «панели не меняются»**) |
| T-ACT-02 | там же | вызов маршалится через `uiInvoke` (фейк считает вызовы), а не выполняется на вызывающем потоке |
| T-ACT-03 | там же | неизвестная команда (будущая `DO/2`) не приводит к вызову `IWindowManager` |

### 5.4. Контракт инсталлятора

`T-INS-01` `InstallerMutexContractTests`: подняться от `AppContext.BaseDirectory` вверх до каталога с
`DesktopOrganizer.sln`, прочитать `installer/DesktopOrganizer.iss`, извлечь `AppMutex=` и сравнить с
`SingleInstanceNaming.InstallerDetectionMutexName`. Корень не найден → тест **падает** (не skip),
иначе он бесполезен.

### 5.5. Реальный канал

`T-IPC-01..03` `NamedPipeActivationIntegrationTests` (`[Trait(Category, Integration)]`, имена pipe/mutex —
уникальный `Guid` на тест): сервер+клиент в одном процессе, доставка команды; второй сервер с тем же
именем → `PipeBusy`; `Stop()` завершается за <2 с при висящем `WaitForConnection`.
Named Pipe и Mutex — объекты ядра, GUI не нужен, поэтому на Windows-хосте это **должно** быть зелёным
под `dotnet test`. Если конкретная среда агента их не запускает — исключать фильтром по `Trait`,
а не удалять; в отчёте фиксировать как непроверенное.

---

## 6. DoD — явное разделение

### 6.1. Достижимо здесь (`dotnet build` / `dotnet test`)

1. Все наборы §5.1–§5.4 зелёные; общий прогон без новых падений относительно baseline ветки.
2. Нет новых предупреждений StyleCop в изменённых/добавленных файлах (DO-007 не ухудшается).
3. **«Второй процесс не продолжает startup»** — формализовано как T-CO-02/T-CO-04/T-CO-06:
   при не-`RunAsPrimary` ни один метод `IWindowManager`/`ITrayIconManager`/`IDesktopGestureService`
   не вызывается (структурно: `App` не создаётся вовсе).
4. **«Активация не меняет панели»** — T-ACT-01.
5. **«Installer detection продолжает работать»** (статическая часть) — T-INS-01 + отсутствие diff
   значения `AppMutex=` в `installer/DesktopOrganizer.iss`.
6. **«Нет гонки при shutdown + одновременный запуск»** (логическая часть) — T-CO-03 на фейках
   + инвариант порядка D-9, зафиксированный в коде и ADR.
7. `Dispose`-корректность — T-CO-07, T-IPC-03 (bounded stop ≤2 с).
8. R-1 структурно закрыт: в `App.xaml.cs` нет пути, на котором процесс с ролью ≠ Primary доходит до
   `SaveWindowsStateNow()`/`SystemIconsPanel.Save()`.

### 6.2. Manual verify (Win10 / Win11) — обязательная матрица

Артефакт: `docs/Refactoring/Testing/single-instance-manual-matrix.md`, помощник —
`tools/lifecycle/Invoke-SingleInstanceStress.ps1` (20 запусков с паузой 100 мс, затем
`Get-Process DesktopOrganizer.UI | Measure-Object`, плюс сверка кодов выхода).

| # | Сценарий | Ожидание |
|---|---|---|
| M-1 | 20 быстрых повторных запусков | ровно 1 процесс; коды выхода вторичных = 0 |
| M-2 | **Регресс R-1:** после M-1 перезапустить primary | все панели на месте; `ChildWindowsState` в `user.config` ≠ `[]` |
| M-3 | Второй запуск при свёрнутом/скрытом MainWindow | существующее окно показано и **в фокусе** (проверка `AllowSetForegroundWindow`) |
| M-4 | Второй запуск: панели, страницы, боковая панель | визуально не изменились; иконки рабочего стола сохранили состояние скрытия |
| M-5 | Kill primary в Диспетчере задач → новый запуск | стартует как primary (abandoned mutex), без диалогов |
| M-6 | «Выход» из трея + немедленный запуск (скрипт) | приложение стартует; запуск не «теряется»; в логе `SHUTTING_DOWN`-повтор |
| M-7 | Установка/обновление инсталлятором при запущенном приложении | Setup предлагает закрыть приложение (AppMutex жив) |
| M-8 | Автозапуск при входе + ручной клик по ярлыку сразу после | ровно 1 процесс |
| M-9 | Два **разных** пользователя Windows одновременно | у каждого свой независимый экземпляр (разные SID → разные имена) |
| M-10 | FUS: **один** пользователь, две сессии | во второй сессии — сообщение «уже запущено в другой сессии», код 4, второй writable-инстанс не поднят |
| M-11 | Зависший primary (искусственно: отладчик/`suspend`) | сообщение D-7 за ≤3 с, приложение не зависает на `WaitOne` |
| M-12 | Debug-сборка, 20 запусков | в `%LOCALAPPDATA%\DesktopOrganizer\Logs` не вымело лог primary (R-12) |
| M-13 | Тайминг холодного старта primary | не вырос заметно (рукопожатие на свободном mutex — один `WaitOne(0)`) |

---

## 7. Риски

| ID | Риск | Митигация | Где проверяется |
|---|---|---|---|
| **R-1** | Secondary, дойдя до `OnExit`, стирает `ChildWindowsState` в `"[]"` и перетирает `SystemIconsPanel` (§0.1) | D-2 (secondary не создаёт `App`) + D-3 (early-return) | §6.1 п.8, M-2 |
| R-2 | Primary убит → mutex abandoned | `catch (AbandonedMutexException) → owned = true` | T-CO-05, M-5 |
| R-3 | Primary жив, но завис: mutex держит, pipe не отвечает | бюджет 3 с, затем D-7 (сообщение, не второй writable-инстанс) | T-CO-04, M-11 |
| R-4 | Deadlock на `WaitOne` | только `WaitOne(0)`; блокирующих ожиданий нет нигде; все ожидания сети — с `CancellationToken` + таймаут | code-review-инвариант; grep на `WaitOne(` без аргумента |
| R-5 | `ReleaseMutex` из чужого потока → `ApplicationException` | acquire/release строго на главном STA-потоке; поток pipe'а не касается mutex; `try/catch` + опора на освобождение при выходе процесса (D-9) | T-CO-07 |
| R-6 | Гонка «primary освободил mutex, сервер уже закрыт» → запуск теряется | ответ `SHUTTING_DOWN` + чередование acquire/send (D-5/D-6); mutex освобождается **последним** (D-9) | T-CO-03, M-6 |
| R-7 | Гонка «primary захватил mutex, сервер ещё не поднят» | `TryBeginServing()` сразу после захвата, до всего startup; плюс повторы | T-CO-01, M-8 |
| R-8 | FUS: два «primary» одного пользователя в разных сессиях | pipe с `maxInstances: 1` как машинно-глобальный тай-брейкер (D-4) | T-CO-06, M-10 |
| R-9 | Переименование/префиксация detection-мьютекса ломает инсталлятор | значение неизменно + T-INS-01 | T-INS-01, M-7 |
| R-10 | Локальный клиент занимает единственный инстанс pipe'а | таймаут 500 мс на соединение, безусловный `Dispose`, `catch` вокруг итерации | T-IPC-03 |
| R-11 | Правка `ApplicationDefinition`/`StartupObject` ломает сборку WPF | Fallback-опция A (D-2) с обязательными доп. гарантиями | сборка в C4 |
| R-12 | 20 secondary-процессов в Debug вымывают retention JSONL-логов (10 сессий) | secondary не резолвит `IDiagnosticsLog` (`Func<IDiagnosticsLog>` в ctor координатора) | M-12 |
| R-13 | `Activate()` не выводит окно на передний план (foreground lock) | `AllowSetForegroundWindow(ASFW_ANY)` в клиенте перед отправкой | M-3, **нативно — только вручную** |
| R-14 | ACL pipe'а недоступен → канал открыт лишним субъектам | без ACL сервер не стартует (degraded-primary), не «тихо без ACL» | code-review |
| R-15 | Остаточный риск: одновременная запись в storage при FUS до Фазы 4 | документируется в ADR-023 «Consequences»; настоящая защита — файловая блокировка storage root в Фазе 4 | — |

**Нативные/непроверяемые вне живой Windows:** `AllowSetForegroundWindow` (R-13), реальное поведение
`AppMutex` в Inno Setup (M-7), FUS/мультисессия (M-9, M-10), abandoned-mutex после kill (M-5),
таймовые гонки M-6/M-8. Всё остальное закрыто тестами.

---

## 8. Что фаза сознательно НЕ делает

- Не вводит файловую блокировку storage root в `PanelItemStorageService` (Фаза 4, `ADR-024`);
  Phase 3 закрывает DO-001 на уровне процесса, остаточный FUS-риск описан в R-15.
- Не расширяет протокол дальше одной команды (D-5), не пробрасывает аргументы командной строки.
- Не меняет `ShutdownMode`, поведение трея, автозапуск и `user.config`-формат.
- Не трогает `DesktopOrganizer.Native` (Фаза 8) — весь P/Invoke идёт через существующий `INativeApi`.
- Не локализует новые пользовательские сообщения (две строки, английский; локализация — отдельная
  сквозная задача).
