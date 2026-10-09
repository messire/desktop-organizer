# STA / WPF test conventions

Rules for any test in `DesktopOrganizer.Tests` that touches a live WPF object
(`Application`, `Dispatcher`, `FrameworkElement`, `ChildWindow`, a real ViewModel).

Written up after an intermittent **`Test Run Aborted`** — a full `dotnet test` run dying
with **zero failing tests**, then passing on the next attempt — was traced to a violation
of rule 1 below. See [Case study](#case-study-the-intermittent-test-run-aborted) for the
whole chain.

---

## The rules

### 1. One owner for `Application.Current`, and the owner never shuts its dispatcher down

`System.Windows.Application` is a process-wide singleton that **can never be
un-constructed**. Whichever test builds it first decides, for the rest of the
`dotnet test` process:

* what `Application.Current.Resources` contains, and
* which thread's `Dispatcher` every later `Application.Current.Dispatcher` marshal targets.

So the thread that constructs it must stay a usable UI thread forever:

* **Do** construct it through `WpfApplicationHost.EnsureApplication()`, which no-ops when one
  already exists and otherwise merges the same resource chain `App.xaml.cs`'s
  `LoadResourceDictionaries()` builds. A bare `new Application()` leaves **empty** resources
  behind and breaks every fixture that expects the real chain (e.g.
  `ChildWindowViewModel.GetTemplate`'s `FindResource("ListViewTemplate")`).
* **Do** keep that thread pumping (`Dispatcher.Run()`), background (`IsBackground = true`) so it
  can never hold the process open, **with an `UnhandledException` absorber attached before it starts
  pumping** (see rule 2). Use `StaTestRunner.Run`/`StaTestRunner.Run<T>` — every fixture that used to
  hand-roll this itself (`ResourceDictionaryMergeTests`, `ChildWindowInitializeTests`,
  `ChildWindowContentSizingTests`, `ChildWindowSizingGridTests`, `ResizeGripVisualsTests` — formerly `ResizeOverlayGeometryTests`, renamed in ADR-022 phase 1) now
  delegates to it (P08 fix cycle 2, M-2) specifically because every one of those hand-rolled copies
  pumped forever WITHOUT the absorber — a real, reproduced abort (see the case study's "Update"
  below).
* **Never** call `Dispatcher.InvokeShutdown()` on the owner's dispatcher, and never let the owner
  thread simply exit. Both leave `Application.Current` non-null with an unusable dispatcher.
* **Never** call `Application.Shutdown()` from a test — it does not make `Application.Current`
  null again, it only makes it worse.

A **non-owner** STA worker is the opposite case and *should* shut its own lazily-created
dispatcher down (that is what releases the hidden message-only `HWND` and the thread message
queue), which is exactly what `StaTestRunner` still does for it.

### 2. A dead dispatcher does not fail quietly — it kills the process

This is the part that makes rule 1 load-bearing rather than cosmetic.

`Dispatcher.InvokeAsyncImpl` refuses to enqueue onto a dispatcher whose
`HasShutdownFinished` is set: it marks the `DispatcherOperation` `Aborted` and cancels its task
source. The **synchronous** overload then calls `DispatcherOperation.Wait`, which surfaces that
cancellation as a `TaskCanceledException` **thrown back at the caller**:

| Marshal | On a shut-down dispatcher |
|---|---|
| `Dispatcher.BeginInvoke` / `InvokeAsync` | operation aborted, silently dropped — safe |
| `Dispatcher.Invoke` | **throws `TaskCanceledException` at the calling thread** |

On the WPF thread an escaping exception at least reaches `Dispatcher.UnhandledException`. On a
thread-pool / timer / `FileSystemWatcher` / named-pipe thread there is no such hook: .NET treats
it as an unhandled exception and **terminates the process**. vstest then reports
`Test Run Aborted` and names whatever test happened to be executing — which is essentially never
the guilty one.

Production code therefore marshals from background threads through
`DesktopOrganizer.UI.Services.UiDispatch`, never a bare `Dispatcher.Invoke`/`Dispatcher.BeginInvoke`.

### 4. Prefer the non-blocking marshal — a blocking one can deadlock, not just throw

`UiDispatch` has two marshals with different guarantees, and picking the wrong one by default is a
second, independent way for a background→UI callback to take the whole process down (or, less
dramatically, delay a clean exit) — found and fixed in the P08 fix cycle 2 `UiDispatch` review:

| Marshal | Blocks the caller? | Safe to call while the caller holds a lock the UI thread might need? |
|---|---|---|
| `UiDispatch.TryInvoke` | **Yes** — waits for the UI thread to service it | **No** — see below |
| `UiDispatch.TryBeginInvoke` | No — queues and returns immediately | Yes |

The concrete failure mode: `ChildWindowViewModel.OnFolderChanged` used to call the blocking
`TryInvoke` from inside `FolderWatcher`'s own debounce callback. If that callback thread is holding
`FolderWatcher`'s debounce lock while `TryInvoke` waits for the UI thread, and the UI thread's very
next action is closing the panel (`DetachWatcher` → `IFolderWatcher.Stop`/`Dispose` → the SAME
lock), the two threads deadlock the whole application — a genuine hang, not merely a crash. The
same shape recurs anywhere a background thread marshalling onto the UI thread might itself be
something the UI thread's OWN next step needs to finish (a lock, a `Thread.Join` a shutdown
sequence is waiting on — see `App`'s single-instance activation wiring, which used to make
`NamedPipeActivationServer.Stop()`'s bounded 2-second `Thread.Join` block the accept thread's queued
activation, needlessly delaying application exit).

**Rule of thumb: default to `UiDispatch.TryBeginInvoke` (fire-and-forget).** Reach for the blocking
`UiDispatch.TryInvoke` only when the calling code genuinely needs to observe the UI-thread work
having completed before it proceeds — and even then, first check that the calling thread cannot
possibly be holding anything the UI thread's next action might need. Every current production call
site (`ChildWindowViewModel.OnFolderChanged`/`RefreshExistence`, `App`'s activation wiring,
`SystemIconsPanelViewModel.OnRecycleBinChanged`) uses `TryBeginInvoke`; there is currently no
call site that needs the blocking form.

A same-thread `Dispatcher.Invoke`/`BeginInvoke` call (the UI thread deferring work to later in its
OWN queue — `App.InitDesktopGestures`'s `WH_MOUSE_LL` hook callbacks, `TrayIconManager`'s tray
message-loop callbacks) is a different case entirely and does not need `UiDispatch` at all: WPF's
own re-entrant fast path (`Dispatcher.CheckAccess() == true`) runs it in place regardless of
shutdown state, so there is no dead-dispatcher hazard for `UiDispatch` to guard against.

### 5. Stop every timer, animation and watcher you start

A `System.Threading.Timer`, `DispatcherTimer`, `DoubleAnimation` or `FileSystemWatcher` created
by a test keeps firing for the **rest of the run**, long after the test that made it has passed —
concurrently with every test that follows.

* Dispose anything `IDisposable` you construct (`ChildWindowViewModel.Dispose()` disposes its
  3-second liveness timer; `SystemShellService.Dispose()` blocks until its Recycle-Bin poll is
  out of the callback before clearing the icon cache).
* Close/clean up real `ChildWindow` instances in the STA harness that created them, so their
  `ContentSizing` debounce `DispatcherTimer` and `RollUpBehavior` animations stop with them.
* Assume any callback you leave running **will** observe torn-down state. Every background
  callback must be written so that it cannot throw — see rule 2 for why "cannot throw" is not a
  style preference here.

---

## Case study: the intermittent `Test Run Aborted`

Reproduced with `dotnet test DesktopOrganizer.sln --no-build --blame-crash --blame-crash-dump-type mini`:
**4 aborts in 22 runs** on the pre-fix tree, **0 in 24** after the fix. All four crashed at the same
place, each naming a different "test running when the crash occurred" (`FolderWatcherTests`,
`AtomicJsonFileWriterTests`, …) — which is the tell that the reported test is a bystander.

**Crash stack:**

```
Unhandled exception. System.Threading.Tasks.TaskCanceledException: A task was canceled.
   at System.Windows.Threading.DispatcherOperation.Wait(TimeSpan timeout)
   at System.Windows.Threading.Dispatcher.InvokeImpl(...)
   at System.Windows.Threading.Dispatcher.Invoke(Action callback)
   at DesktopOrganizer.UI.ViewModels.ChildWindowViewModel.RefreshExistence(Object state)
   at System.Threading.TimerQueueTimer.Fire(Boolean isThreadPool)
   at System.Threading.ThreadPoolWorkQueue.Dispatch()
```

**Chain** (order confirmed from the run's `Sequence_*.xml`):

1. Entry **1069** — `PanelMetricsTests.ResizeCornerThickness_CoversMajorityOfCornerDeadZone` runs
   inside `StaTestRunner` and is the first test in this process to construct `Application`
   (it precedes `ResourceDictionaryMergeTests` at entry 1131, which is the fixture that
   otherwise wins the race and pumps).
2. `StaTestRunner`'s `finally` calls `InvokeShutdown()` on that worker's dispatcher —
   which is now `Application.Current.Dispatcher`. **Rule 1 violated.**
3. Entries **1586 / 1669** — `ChildWindowSizingGridTests` / `ChildWindowContentSizingTests`
   construct real `ChildWindowViewModel`s, each starting a 3-second liveness
   `System.Threading.Timer`. **Rule 3.**
4. One timer period later (around entry 1812, during the deliberately slow `FolderWatcherTests`)
   a tick fires, `Dispatcher.Invoke` rethrows `TaskCanceledException` on the thread-pool thread,
   and the test host dies. **Rule 2.**

Because step 1 depends purely on xUnit's class scheduling, the whole thing was a coin flip —
hence "sometimes aborts, passes on retry".

**Fixes** (both layers, deliberately):

| Layer | Change |
|---|---|
| Production | `UiDispatch.TryInvoke` — probes `HasShutdownStarted`/`HasShutdownFinished`, catches the shutdown-began-mid-enqueue `OperationCanceledException`, and lets a genuine exception from the marshalled work through. Originally used by `ChildWindowViewModel.RefreshExistence`, `ChildWindowViewModel.OnFolderChanged` and `App`'s single-instance activation wiring — all three were switched to the non-blocking `UiDispatch.TryBeginInvoke` in the P08 fix cycle 2 `UiDispatch` review (rule 4 below); `TryInvoke` itself is unchanged and still has regression coverage of its own. |
| Test harness | `StaTestRunner` pumps instead of shutting down when its worker owns `Application.Current`; non-owner workers still shut down. |

**Regression tests:** `Unit/Services/UiDispatchTests.cs` (including
`ShutDownDispatcher_RawInvoke_Throws`, which characterises the WPF behaviour itself so the guard
is not cargo cult) and `Unit/Helpers/StaTestRunnerTests.cs`.

**Update (P08 fix cycle 2) — a second, structurally identical abort, found while adding this
file's own absorbed-exception counter:** a new `StaTestRunnerTests` regression test deliberately
threw a leaked-callback exception on whatever thread happened to own `Application.Current`. On a
run where `ResourceDictionaryMergeTests`/`ChildWindow*Tests`/`ResizeGripVisualsTests` (formerly `ResizeOverlayGeometryTests`; each of
which used to hand-roll its own STA-thread/ownership machinery, duplicated independently per class)
won the race to construct `Application.Current` first, that thread pumped
(`Dispatcher.Run()`) forever exactly per rule 1 — but WITHOUT ever attaching an
`Dispatcher.UnhandledException` absorber, because none of those hand-rolled copies had one. The
test's own exception then landed on that un-absorbed pump and killed the test host — same root
shape as the original case study (an un-guarded pumped dispatcher), reached through a different,
independently-duplicated STA harness instead of `StaTestRunner` itself. **Fix:** every one of those
fixtures now delegates to the new `StaTestRunner.Run<T>` (rule 1 above) instead of maintaining its
own copy, so any of them winning the "first to construct `Application.Current`" race now pumps
WITH the same absorber (and counter) `StaTestRunner`'s own owner path always had. The regression
test itself was also rewritten to not depend on which fixture wins that race at all — it drives
`StaTestRunner`'s pump directly on a dedicated, self-owned STA thread instead of asserting against
whatever `Application.Current` happens to be ambiently at that point in the suite.

---

## Known remaining hazards

Not causes of this abort, but the same class of thing — worth knowing before writing new STA tests:

* **A live but non-pumping owner.** `ChildWindowViewModelTests.EnsureWpfApplication` and
  `ApplicationStateServiceTests` construct `Application` on the plain xUnit thread, which never
  runs a message pump. Nothing crashes (rule 2 only bites on a *shut-down* dispatcher), but a
  cross-thread `Dispatcher.Invoke` onto that owner blocks its caller indefinitely and a
  `DispatcherTimer` bound to it (`ApplicationStateService`'s save debounce) never ticks. Prefer
  `WpfApplicationHost.EnsureApplication()` from inside `StaTestRunner`.
* **`FolderWatcher.OnDebounceElapsed`** used to invoke subscribers from a thread-pool timer callback
  while holding `_debounceLock`, with no `try`/`catch` of its own — a throwing subscriber would
  take the process down and a subscriber that needs that lock on the UI thread would deadlock.
  **Fixed** (P09 release-gate hardening): `OnDebounceElapsed`/`Schedule`'s immediate-fire path now
  capture the pending args under the lock and hand off to a new `RaiseChanged`, which invokes each
  subscriber individually, *outside* `_debounceLock`, each wrapped in its own `try`/`catch`. A
  throwing subscriber is recorded to `IDiagnosticsLog` (category `folderWatcher`, event
  `SubscriberThrew`, payload `errorType` only) and does not stop delivery to the next subscriber or
  stop the watcher itself; `Stop()`/`Dispose()` can always take `_debounceLock` immediately instead of
  blocking behind a subscriber still marshalling onto a busy UI thread. Its only production subscriber
  (`ChildWindowViewModel.OnFolderChanged`, via `UiDispatch.TryInvoke`) already only ever surfaces a
  genuine subscriber bug through this path — `UiDispatch` itself swallows the one
  `OperationCanceledException` its own contract documents, so it never reaches this catch. Regression
  tests: `FolderWatcherTests.Changed_SubscriberThrows_TimerKeepsRunning_NextEventStillDelivered`,
  `FolderWatcherTests.Stop_WhileSubscriberIsStillRunningOnTheTimerThread_DoesNotDeadlock`.
  Дополнение (UiDispatch fix-цикл, C-1): сам подписчик `ChildWindowViewModel.OnFolderChanged` теперь
  маршалит через fire-and-forget `UiDispatch.TryBeginInvoke` (не блокирующий `TryInvoke`) и пропускает
  работу, если view-model уже `_disposed` — обе половины дедлока/гонки закрыты независимо.
* **Leaked ViewModels.** Most fixtures never `Dispose()` the `ChildWindowViewModel`s they build,
  so their liveness timers run for the whole suite. Each tick used to queue a NEW blocking
  `Dispatcher.Invoke` regardless of whether an earlier one had been serviced — see "a second,
  unrelated flake" below, now mitigated by the same P08 fix cycle 2 change (MAJOR M-1: fire-and-forget
  marshal plus a single-flight pending guard, `ChildWindowViewModel.IsExistenceRefreshPending`).

## Wall-clock тесты

**Правило: ни один тест не должен использовать фиксированный `Thread.Sleep`/`Task.Delay` как условие
успеха.** Если тест ждёт, пока фоновый поток что-то сделает — жди сигнал (`ManualResetEventSlim`,
`SemaphoreSlim`, `TaskCompletionSource`) с щедрым верхним пределом (например 10 с) и внятным
диагностическим сообщением при таймауте, а не фиксированную задержку, которая должна "успеть" до
следующего шага теста. Если тест сам инжектирует временный сбой (залоченный файл, read-only атрибут) и
затем снимает его из фонового потока по таймеру — это тот же анти-паттерн с обратным знаком: время
снятия сбоя гоняется наперегонки с фиксированным retry-бюджетом продакшен-кода. Предпочтительно: вынести
задержку/точку снятия сбоя в инжектируемый seam (`Func<int, TimeSpan>` для задержки,
`Action<int>`-хук на "перед следующей попыткой") и снимать сбой **синхронно, на том же потоке**, что
вызывает тестируемый код — тогда обходится не только флейк, но и сам фоновый поток/`Task.Run` целиком.
Единственное законное исключение — тест, где предмет проверки сама скорость (например "не ждём
завершения медленного обработчика"): там, где возможно, замени сравнение с абсолютной константой на
сравнение с сигналом/другим таймингом того же прогона (не ждали ли мы, пока событие уже случилось), а
не расширяй константу до состояния "наверное хватит".

### Найденные и исправленные (P09 release-gate hardening)

Измерено параллельно с abort-расследованием, поэтому раньше было записано здесь как "второй,
несвязанный флейк (пока открыт)". Небольшая доля прогонов падала (не abort) в wall-clock-чувствительных
тестах:

| Test | Symptom | Fix |
|---|---|---|
| `AtomicJsonFileWriterTests.Write_TransientIOException_RetriesAndSucceeds` | `Background lock-holder never signalled it had acquired the handle` — его `Task.Run` не получил поток из пула за **5 s** | Убран `Task.Run`/`Thread.Sleep` целиком: `AtomicJsonFileWriter` получил тест-only seam (`testRetryDelay: Func<int, TimeSpan>`, `testOnRetryScheduled: Action<int>`) — тест снимает лок **синхронно**, на своём же потоке, между первой и второй попыткой писателя, с нулевой задержкой ретрая |
| `AtomicJsonFileWriterTests.Write_TransientUnauthorizedAccessException_RetriesAndSucceeds` | фоновая задача снимала `ReadOnly` через 80 мс, не укладываясь в ретрай-бюджет писателя 50/150 мс | Тот же seam — `testOnRetryScheduled` синхронно снимает `ReadOnly` перед второй попыткой |
| `NamedPipeActivationIntegrationTests.*` | `Faulted` вместо ожидаемого `PrimaryShuttingDown`/`OtherSession`/`Delivered` | Фиксированные `TimeSpan.FromSeconds(2)`-таймауты (`TrySend`/`Wait`) подняты до общего `GenerousWaitBudget = 10s` с диагностическим сообщением при таймауте; `Client_GetsItsResponse_WithoutWaitingForASlowOnRequestHandler` больше не сравнивает `Stopwatch.Elapsed` с фиксированным порогом 800мс — вместо этого проверяет условие (`handlerFinished.IsSet == false` в момент возврата `TrySend`) |

Причина была общей: все они сравнивались с фиксированным wall-clock бюджетом, пока сьют держит сотни
утёкших колбэков `System.Threading.Timer` (см. "Leaked ViewModels" выше), каждый из которых блокирует
поток из пула на `Dispatcher.Invoke`, пока его не обслужит UI-поток. Инъекция новых потоков в пул
дросселируется примерно раз в секунду, поэтому всплеск способен отодвинуть `Task.Run` на несколько
секунд. Ни один из фиксов выше не менял продакшен-таймауты/интервалы (`AtomicJsonFileWriter`'s
50/100/200мс ретрай-расписание, `NamedPipeActivationServer`'s 500мс/2с бюджеты, `FolderWatcher`'s 300мс
debounce) — только сами тесты (и, для `AtomicJsonFileWriter`, добавленный тест-only seam).

**Известный остаточный риск (не в этом фикс-пакете, за рамками "только тесты"):**
`NamedPipeActivationServer`'s собственный `ConnectionBudgetMs = 500` (фиксированный бюджет на
чтение+ответ одного соединения, `HandleConnection`) и есть вероятный подлинный источник наблюдавшегося
`Faulted` — если серверный accept-thread не успевает прочитать/записать в these 500мс из-за задержки
IOCP-колбэка под давлением пула потоков, клиент получает "Server disconnected before a full response
line arrived" независимо от того, насколько терпелив клиентский таймаут. Раздвинуть клиентский бюджет
до 10с (сделано выше) не устраняет этот путь — это фикс продакшен-кода `NamedPipeActivationServer.cs`,
который остаётся вне зоны текущего фикс-пакета (см. `AGENT_CONTRACT.md`/scope этого прохода). Если 10
последовательных `dotnet test --no-build` прогонов после этого фикса всё ещё показывают `Faulted` в
`NamedPipeActivationIntegrationTests`, следующий шаг — поднять `ConnectionBudgetMs` или сделать его
тоже инжектируемым, отдельным, точечным изменением `NamedPipeActivationServer`.

**Обновление (UiDispatch fix-цикл, M-1):** вторая половина причины — блокирующий `Dispatcher.Invoke`
из 3-секундного liveness-таймера `ChildWindowViewModel.RefreshExistence` — тоже закрыта
(`UiDispatch.TryBeginInvoke` + single-flight guard), так что утёкший view-model больше не занимает
поток пула; см. правило 4 выше.
