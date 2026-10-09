# ADR-046: Фоновая загрузка иконок STA-очередью и асинхронный liveness

**Статус:** Accepted (2026-09-30) — Amended 2026-10-09 (ADR-audit WP-C + review cycle 1, ADR-033: операционные Warn/Error очередей иконок и liveness дублируются в `IAppLog`; `IconJobTimedOut`+`IconWorkerAbandoned` — одна запись; `AppLogEventThrottle` K=5)

---

**Источник:** бэклог 1.x, пункт 7 «Загрузка иконок на UI-потоке: большой портал или сетевые ярлыки
замораживают интерфейс при старте» (`docs/ROADMAP.md`), мастер-план
[docs/PLANS/background-icon-loading.md](../docs/PLANS/background-icon-loading.md) (`fences-feature-planner`,
2026-09-29), follow-up [ADR-039](ADR-039-sized-shell-icons.md) («All icon extraction is still synchronous
on the UI thread»), находка 5 [docs/DIAGNOSTICS/icon-loading-findings.md](../docs/DIAGNOSTICS/icon-loading-findings.md)
(P08, «Отзывчивость при 200+ элементах — задокументировано, не изменено»), отклонённая альтернатива A
[ADR-008](ADR-008-shell-com-sta-threading-and-pidl-marshaling.md), Решение 3
[ADR-012](ADR-012-performance-and-resilience-hardening.md) (`Freeze()`-инвариант). Ветка
`feature/background-icon-loading` в отдельном git worktree от `rc/0.8.0`, смёржена в `a83d430`. Пакеты:
7a-0 контракты, 7a-S планировщик/breaker/probe (`dotnet-services-engineer`), 7a-N STA-воркер/классификатор/
native (`win32-interop-specialist`), 7b интеграция Core/`BaseViewModel`/`ChildWindowViewModel`, 7c liveness
вне UI-потока + `BackgroundItemProbeQueue` (`dotnet-services-engineer`). Тесты — `tdd-test-engineer`. Прошло
**три** раунда `code-quality-reviewer`/`fences-ux-parity-reviewer`: iteration 1 (закрыта до мёржа M1),
iteration 2 — коммит `598ef54` («7b/7c review iteration 2 fixes: UX-CRITICAL, H-1, M-1..M-6, L-1..L-7»),
iteration 3 — коммит `10911a3` («small iteration 3 fixes: H-1/L-3 regression tests, N-2/N-3 liveness
fixes»). Полный список коммитов ветки — `git log a83d430^2 --oneline`.

## Контекст

`IIconProvider.GetIcon(string, int)` (трёхстадийный фолбэк ADR-039) вызывался **синхронно на UI-потоке**
из четырёх мест: `BaseViewModel.AddFilesCore` (добавление файлов), `FileItem`'а конструктор (metadata
round-trip на `Exists`/`IsDirectory`), `RefreshItemsLiveness` (тик 3 с — `FileItem.Refresh()` для каждого
элемента плюс синхронный `GetIcon` при missing→present), `TryRenameItem`/`RefreshAllItemIcons`/
`RetryPlaceholderIcon` (смена размера/DPI, ретрай). UNC/offline-скип ADR-039 закрыл только путь, где сам
элемент лежит на недоступном ресурсе; `.lnk` с целью на недоступной шаре, сетевая буква (`Z:\`), медленное
сторонее shell-расширение и просто Folder Portal на тысячи файлов по-прежнему блокировали старт и
перестройку панели на секунды. Находка P08 №5 измерила это (≈2.8 мс/элемент, 200 некешированных ≈557 мс) и
сознательно оставила без фикса — тиражирование отклонённой ADR-008 альтернативы A (выделенный STA-поток)
на icon loading было явно названо «новым архитектурным решением, требующим отдельного ADR», а не точечной
правкой. Планирование этого пункта дополнительно нашло три места вне исходного мастер-плана бэклога,
делающие тот же класс fs-стопора на UI-потоке: `ChildWindowViewModel.ResolveManagedReplacements` (B-5, до
`ReplaceAll`), `WindowManager.ResolveStalePanelPaths` (B-6, для всех панелей на старте) и
`ReconcileAfterShellCommand` (B-7, `PathExists` для каждого элемента после любой shell-команды, хотя
reconcile действует только на managed-элементы).

## Решение

### D-1. Выделенные STA-потоки с message pump, не `Task.Run`/MTA

Каждый воркер — `Thread(ApartmentState.STA, IsBackground = true)` (`StaWorkerThread`) с `Dispatcher.Run()`
внутри; задания приходят через `Dispatcher.BeginInvoke` на собственный диспетчер воркера
(`IStaWorker.TryPost`). Причина именно pump'а, а не `Task.Run`/MTA или ручного `WaitHandle.WaitOne`: shell
COM (ADR-008 §2) неопределённо ведёт себя вне STA, а сторонним icon-handler'ам иногда нужен собственный
message pump (скрытые окна, таймеры, исходящие COM-вызовы). Воркер нереентерабелен (`_executing`-флаг):
вложенный pump (исходящий COM-вызов handler'а) не запускает следующее задание на том же стеке —
перепощен в конец с `DispatcherPriority.Background`.

### D-2. Две полосы (lanes), по одному воркеру в каждой — Local и Remote

Задание сначала попадает в Local; при старте исполнения `IRemotePathClassifier.GetOwnRemoteRoot`
(UNC/`DRIVE_REMOTE`-буква) или, для `.lnk`, `GetShortcutTargetRemoteRoot` (**сырая** цель ярлыка,
`IShellLinkW::GetPath(SLGP_RAWPATH)`, без `Resolve`, без сети) переносит сетевое задание в Remote-полосу.
Один воркер + только watchdog (как в исходном мастер-плане бэклога) не выполнял бы метрику: N ярлыков на
одну недоступную шару всё равно ждали бы SMB-таймаут последовательно в одной очереди. `MaxStaWorkersPerLane
= 1` (константа); переход на 2 для Local — однострочная правка, если M-7.5 покажет необходимость.

### D-3. Circuit breaker по сетевому корню, с ревью-фиксом семантики short-circuit

`RemoteRootCircuitBreaker`: вызов на корень дольше `SlowThreshold = 2 с` или зависание по watchdog открывают
корень на `RootQuarantine = 60 с` — все задания и пробы этого корня завершаются сразу (`Unavailable`). Через
60 с корень полуоткрыт, пропускается одно пробное задание (`half-open trial`); пока это пробное задание
внутри `BeginCall`, повторный `IsOpen`-опрос того же корня видел бы его как открытый — поэтому Remote-фетч
использует `IPathProbe.ProbeWithinCall`, **не** `Probe` (ревью iteration 2, M-4): вызов уже зарегистрирован
брейкером как in-flight, второй самостоятельный `Probe` закоротил бы сам себя. **Ключевой фикс ревью
iteration 2 (UX-CRITICAL):** breaker-открытие/watchdog-таймаут несут `Exists = null, IsDirectory = null`
(неизвестно), **не** `Exists = false` — короткое замыкание никогда не трогало файловую систему. Изначальная
семантика `Exists = false` красила бы сам локальный `.lnk` серым как «отсутствующий» на секунды каждый раз,
когда его сетевая цель недоступна, хотя файл ярлыка никуда не делся. Тот же принцип применён к 7c-liveness:
`PathProbeResult.IsKnown = false` оставляет элемент нетронутым (`continue` до `ApplyProbe`), а не как
«подтверждённое отсутствие» — иначе элемент на карантинном корне уходил бы в `OnItemsVanished` тем же
способом (закрыто малой правкой iteration 3, N-2).

### D-4. Watchdog

`TimeProvider`-таймер, период 500 мс, взводится **только** пока в системе есть исполняемая единица работы
(включая type-задания — не только icon-задания). Единица работы дольше `StuckJobTimeout = 5 с` →
`IStaWorker.Abandon()` (поток не убивается — `Thread.Abort` в .NET 8 нет; если зависший вызов когда-нибудь
вернётся, поток сам вызовет `InvokeShutdown` на своём диспетчере и выйдет), создаётся новый воркер, очередь
полосы продолжается. Local: путь уходит в карантин на 5 минут. Remote: `breaker.ReportStuck(root)`. Не
больше `MaxAbandonedWorkersPerLane = 2` одновременно висящих брошенных потоков — при превышении полоса
отключается до конца сессии (`IconLaneDisabled`, её задания → `Unavailable`); слот освобождается, когда
брошенный поток фактически возвращается (`AbandonedCount--`).

### D-5. Плейсхолдер → type-иконка (Interim) → реальная (Loaded)

Элемент создаётся сразу: иконка = `TryPeekCachedIcon` ?? `TryPeekTypeIcon` ?? `PlaceholderIcon` — чтение из
существующих словарей под lock, без fs/shell на UI-потоке. В Local-полосе type-задания (стадия 3 по
расширению, отдельный кеш `(ext | "<dir>")`) исполняются **раньше** всех остальных и рассылаются как
`Interim` фан-аутом всем ещё ожидающим запросам того же type-ключа. Серые квадраты живут 1–2 кадра, а не
секунды до реальной иконки.

### D-6. Приоритеты — round-robin по группам внутри уровня

Панель = `IIconLoadGroup`. Уровни `Visible` (видимая страница) и `Background` (скрытая), внутри уровня —
round-robin по группам (портал на 1000 файлов не морит маленькую соседнюю панель), внутри группы FIFO.
`ChildWindowViewModel.SetIconLoadVisibility` (`ChildWindow.IsVisibleChanged`) и `LoadFromModel` (до
`ReplaceAll`) выставляют `IconLoadPriority`; смена страницы переносит ожидающие задания без переприоритизации
каждого запроса отдельно. Приоритет по вьюпорту скролла — явный follow-up, не в этом объёме.

### D-7/D-8. Дедуп и отмена через поколения

Дедуп `(path OrdinalIgnoreCase, Snap(size))` — один fetch, много ожидающих; отмена одного ожидающего не
отменяет fetch, если остались другие. `CancelPending()` инкрементирует `Generation` группы; задание без
живых ожидающих лениво выбрасывается при dequeue, а не отменяется активно. `Dispose()` группы = `CancelPending`
+ флаг, после которого `Post` ничего не доставляет.

### D-9. Доставка пачками

Буфер результатов группы флашится через `SynchronizationContext.Post` (никогда `Send`/`Invoke`/`Wait`) при
`DeliveryBatchSize = 64` накопленных результатах, `DeliveryFlushInterval = 16 мс` с первого результата в
буфере, **или** когда полоса опустела целиком (`IconQueueDrained`, третий триггер флаша). Это и есть
митигация мигания при перестройке 1000-элементной панели. Исключение в `onResults` не долетает до
`DispatcherUnhandledException` (изолировано try/catch на колбэк, `IconQueueSinkThrew`).

### D-10. Back-pressure

`MaxPendingJobs = 20 000`. При переполнении вытесняется самое старое `Background`-задание без живых
ожидающих (`IconQueueBackpressureDropped`); `Dropped` не расходует retry-бюджет M3, VM повторит по своей
политике.

### D-11. Проба существования — одна metadata-операция на воркере

`IPathProbe.Probe`/`ProbeWithinCall` (`File.GetAttributes`) вместо 2–3 `Exists`-вызовов; отсутствие → missing,
`FILE_ATTRIBUTE_DIRECTORY` → `IsDirectory`; любая ошибка → `Exists = false` (совместимо со старой семантикой
`File.Exists`). Внутри `GetIcon` собственная `File.Exists` остаётся (инвариант C-2, ниже) — известный
двойной round-trip **вне UI-потока**, зафиксирован как follow-up, не устранён в этом раунде.

### D-12. Liveness (7c) — отдельная очередь на пуле потоков, не на STA-воркерах

`BackgroundItemProbeQueue` (`IItemProbeQueue`) — чистый fs, без COM, не делит STA-воркеров с иконками (иначе
зависшая `Exists` на шаре останавливала бы загрузку иконок). Не больше `MaxConcurrentProbeBatches = 2`
батчей одновременно (`SemaphoreSlim`, пул не блокируется ожиданием); breaker D-3 общий с иконками.

### D-13. Синхронный fallback для тестов/headless — существующие тесты не переписаны

Если `IIconLoadQueue`/`IItemProbeQueue` не переданы в `BaseViewModel`/`ChildWindowViewModel` (старые тесты,
`new ChildWindowViewModel(...)` без DI) **или** у конструирующего потока нет живого
`SynchronizationContext`, VM молча деградирует до `SynchronousIconLoadQueue`/`SynchronousItemProbeQueue`
(`DesktopOrganizer.UI.Services`) — прежняя синхронная логика inline, доставка результата в том же вызове.
Деградация логируется (`IconPipelineSynchronousFallbackUsed(panelId, component)`, найдено ревью iteration 2,
L-4) — в проде это обычно означает, что панель-ViewModel сконструирована до установки диспетчерского
`SynchronizationContext`. В production DI всегда резолвит настоящие очереди. Весь ранее существовавший набор
тестов (`BaseViewModelCollectionTests`/`ChildWindowViewModel*Tests`/`ContentFilterTests`) остался зелёным без
правок — за исключением `IconProviderTests`, дополненного новыми потокобезопасными кейсами (см. «Известные
ограничения» ниже про B-6/B-7 эквивалентность).

### D-14. Жизненный цикл — ленивый старт, shutdown ≤~1 с

`StaIconLoadQueue`/`BackgroundItemProbeQueue` — DI-синглтоны, `IDisposable`; воркеры стартуют лениво при
первом `Submit` (резолв в тестах и DI-валидация не плодят потоки впустую). `Dispose` (вызывается из
`_serviceProvider.Dispose()` в `ShutdownSequence`, ADR-033): флаг, отмена всех групп, остановка watchdog,
`InvokeShutdown` диспетчеров обоих воркеров, `Join(1 с)` на каждый; брошенные (abandoned) потоки не
дожидаются — они `IsBackground = true`. После `Dispose` ни один `Post` не проходит. Суммарно выход
приложения во время загрузки иконок укладывается в ≈1 с (manual verify M-7.6).

### C-4 (расширение инварианта ADR-012): рантайм-проверка `IsFrozen` на всех путях, не `Debug.Assert`

Каждый `ImageSource`, пересекающий границу воркер→UI, обязан быть заморожен. Проверка — явная, в рантайме
(`!icon.IsFrozen` → `Outcome = Failed` + `IconResultNotFrozen(stage)`), не `Debug.Assert`: в .NET 8
сработавший `Debug.Assert` без отладчика валит процесс так же, как в прецеденте `sta-test-conventions.md`.
Ревью iteration 2 (M-5) нашло, что первая реализация проверяла `IsFrozen` только на пути обычного
icon-задания — проверка добавлена **и** на путь type-задания (`ExecuteTypeJob`, `stage: "typeIcon"`), не
только на `ExecuteIconFetch` (`stage: LaneName(lane.Kind)`).

### Политика повторов (расширение M3, ADR-039)

Missing-путь по-прежнему не кешируется; переход missing→present по liveness → новый `Submit` (асинхронно).
`MaxPlaceholderRetryAttempts = 3` (backoff 1 и 2 тика) срабатывает, если элемент `Exists` **и** (иконка
`IsPlaceholder` **или** последний исход `Unavailable`/`Failed`/`Dropped`) **и** запрос сейчас не в полёте.
Карантин пути/корня (D-3/D-4) отдаёт `Unavailable` без обращения к shell, но всё равно тратит попытку M3 —
зависший путь перестаёт дёргаться после трёх тиков, а не бесконечно.

## Liveness вне UI-потока (7c)

Пайплайн тика (`ChildWindowViewModel.RefreshExistence`) не изменился структурно (single-flight, таймер пула
3 с), но применение результатов стало асинхронным: снимок `(item, path, wasMissing, wasPending)` +
`ItemsGeneration` → `ProbeAsync` с верхней границей `LivenessPassTimeout = 10 с` → применение на UI **только
если** `CanApplyLivenessResults` и `ItemsGeneration` не менялся; иначе проход выбрасывается целиком (частичное
применение потеряло бы переход — инвариант M-A). Single-flight флаг сбрасывается **после** завершения
асинхронного прохода, не после его постановки — иначе на медленной шаре тики наслаивались бы друг на друга.

**M-C под асинхронностью:** `_pendingVanishedManagedItems` стал `Dictionary<FileItem, long observedAtTimestamp>`
(время **снимка**, не время применения) — иначе асинхронное применение около границы тика сократило бы
трёхсекундное окно подтверждения «missing» до долей секунды и сломало бы паттерн editor-save-by-recreate,
который M-C защищает.

**B-5/B-6/B-7 закрыты перестановкой порядка проверок, не изменением результата:** `ResolveManagedReplacements`
и `WindowManager.ResolveStalePanelPaths` сначала ищут managed-кандидата (кеш манифеста в памяти) и только при
ровно одном кандидате делают `Exists` исходного пути — пересечение двух условий не зависит от порядка их
проверки, поэтому результат для любого входа тот же, но сетевой путь без managed-кандидата больше не трогает
сеть. `ReconcileAfterShellCommand` получил ранний `if (!_itemStorage.IsManaged(item.Path)) continue;` перед
`PathExists` — для не-managed `Reconcile` и раньше возвращал `Unchanged`, семантика не изменилась, тест это
пинит.

## Что осталось синхронным на UI-потоке осознанно (§7.4 плана)

`PathExists` по managed-путям (B-8, только `%LOCALAPPDATA%`, не сеть), перечисление портала
(`GetSourceFiles`/`GetSafeEntries`, `Directory.Exists(SourceFolderPath)`), `AddDroppedFiles`/`MoveIn`,
`MovedOutVerifier` (пользовательский жест, локальные пути), `ShowShellContextMenu` (ADR-008/040 — меню
обязано быть на UI-потоке), `SystemIconsPanel` (ADR-007).

## Диагностика

Категория `icon` (`IconDiagnostics.Category`, `IStaIconLoadQueue`/`RemoteRootCircuitBreaker`): `IconQueueStarted
{lane}`, `IconQueueDrained {lane, jobs, elapsedMs}`, `IconJobTimedOut {lane, extension}`, `IconWorkerAbandoned
{lane, abandoned}`, `IconLaneDisabled {lane}`, `RemoteRootQuarantined {rootKind}`,
`IconQueueBackpressureDropped {count}`, `IconQueueSinkThrew {errorType}`, `IconResultNotFrozen {stage}`,
`IconQueueWatchdogPassFailed {errorType}`. Категория `liveness` (`BackgroundItemProbeQueue`): `ItemProbeFailed
{errorType}`. Категория `panel` (`IGestureDiagnostics`/`GestureDiagnostics`, scope = panel id): `PanelItemsBuilt
{count, elapsedMicroseconds}` (Stopwatch вокруг `AddFilesCore`+фильтр/сортировку в `LoadFromModel`/`ReplaceAll` —
метрика M-7.1) и `IconPipelineSynchronousFallbackUsed {component: "iconLoadQueue" | "itemProbeQueue"}` (D-13).
Полный список и privacy-инвариант — `docs/Refactoring/Testing/diagnostics-log-events.md`.

## Последствия

### Позитивные

- Ни один из путей добавления, переименования, смены размера, ретрая и liveness не делает fs/shell-вызов на
  UI-потоке для не-managed путей (строгие моки в тестах, `Times.Never`).
- Зависший shell-handler или недоступная сетевая шара больше не блокируют весь пайплайн иконок — только
  свою полосу/корень, доказано реальным `StaWorkerThread` + сигналами (без wall-clock в CI).
- Кеш `(path, bucket)`, трёхстадийный фолбэк и UNC/offline-скип ADR-039 не изменены: дифф `IconProvider.GetIcon`
  — только комментарий о потоке-вызывающем; существующие `IconProviderTests` на стадии не правились.
- Persistence/`IAppSettings`/RESX без диффа — весь механизм настроек (ADR-024) не тронут; все таймауты/лимиты
  очереди — internal-константы/options-record, не пользовательские настройки.

### Известные ограничения и follow-ups (не блокеры, в бэклог)

- **Панели не виртуализированы** (`AdaptiveIconGridPanel`, ADR-018) — R7-8. Метрика «не дольше одного кадра»
  для 1000 элементов покрывает только UI-время `AddFilesCore`+фильтр/сортировку (`PanelItemsBuilt`, manual
  verify M-7.1); генерация 1000 `ListBoxItem`-контейнеров — отдельная, не относящаяся к этому ADR стоимость
  UI-потока и почти наверняка больше 16 мс сама по себе. Виртуализация — отдельный будущий пункт, не
  расширение этого ADR.
- **Двойной metadata round-trip.** `IPathProbe` делает одну пробу на воркере перед `GetIcon`, но сам `GetIcon`
  внутри (по инварианту C-2, тело не менялось) всё ещё делает собственный `File.Exists`. Оба вызова — вне
  UI-потока, поэтому не влияют на отзывчивость; устранение — follow-up, требует правки `IconProvider.GetIcon`.
- **Перечисление сетевого портала** (`GetSourceFiles`/`GetSafeEntries`, `Directory.Exists(SourceFolderPath)`) —
  вне объёма этого ADR, остаётся синхронным стопором для Folder Portal на недоступной шаре.
- **Не-managed исчезнувший файл остаётся серым дольше, чем managed** — существовавшее (legacy) поведение
  liveness, этот ADR его не менял и не был обязан.
- **`isDirectoryHint`-перегрузка `AddFilesCore`** (портал отдаёт признак каталога вместе с перечислением,
  избегая пересортировки после пробы для 1000 элементов) — опциональный пункт плана (§6.9), не сделана в этом
  раунде: раздула бы дифф без изменения корректности.
- **Приоритет по вьюпорту скролла** (не только видимая/скрытая страница) — explicit follow-up D-6.
- **Существующие тесты `IconProviderTests` дополнены** (потокобезопасность LRU, `TryPeekCachedIcon`/
  `GetTypeIcon`) вопреки общей букве D-13 «существующие тесты не переписываются» — это аддитивное расширение
  того же файла под новый публичный контракт (§3.2 плана), а не правка существующих кейсов; сами кейсы стадий/
  freshness/missing-path не тронуты.

## Amendments

**Amends [ADR-039](ADR-039-sized-shell-icons.md):** закрывает «Known follow-up» — «All icon extraction is
still synchronous on the UI thread». Три стадии фолбэка, снап буккетов, downscale/premultiplied-alpha и
UNC/offline-скип **не изменены**; изменился только вызывающий поток (`GetIcon` теперь вызывается на STA-воркере
очереди, не на UI-потоке — в remarks сигнатуры отражено как «any STA thread»).

**Amends [ADR-012](ADR-012-performance-and-resilience-hardening.md) Решение 3:** инвариант «любой `ImageSource`,
покидающий Services, обязан быть `Freeze()`-нут» распространяется на межпоточную передачу воркер→UI и
конкретизируется явной рантайм-проверкой на границе доставки (см. C-4 выше), а не только на факт вызова
`.Freeze()` в момент создания.

**Amends [ADR-008](ADR-008-shell-com-sta-threading-and-pidl-marshaling.md):** отклонённая альтернатива A
(«выделенный STA-поток для Shell-операций») принимается — но **только** для read-only извлечения иконок
(`IShellItemImageFactory`/`SHGetFileInfo`/системный image list через `IconProvider`/`IconHandler`), выполняемого
на `StaWorkerThread`. Контекстное меню (`ShowShellContextMenu`, ADR-008/ADR-040) остаётся на UI-потоке без
изменений — оно создаёt HWND и гоняет собственный message loop (`TrackPopupMenuEx`), который обязан быть на
том же потоке, что показывает окно.

**Amends [ADR-033](ADR-033-crash-safety-and-app-log.md) (раздел «Диагностика» ниже действует только для `--diag`-трассы),
2026-10-09, ADR-audit WP-C + review cycle 1 (G4):** операционные события очереди (`StaIconLoadQueue`: `IconWorkerAbandoned`,
`IconLaneDisabled`, `RemoteRootQuarantined`; Error-уровень — `IconQueueSinkThrew`, `IconQueueWatchdogPassFailed`,
`IconResultNotFrozen`) и liveness (`BackgroundItemProbeQueue`: `ItemProbeFailed`) теперь **дублируются в always-on
`IAppLog`** и переживают отсутствие `--diag`. `IconJobTimedOut` и `IconWorkerAbandoned` — один инцидент: трасса
диагностики сохраняет оба события (схема `diagnostics-log-events.md` не менялась), в app-лог пишется **одна** запись
`IconWorkerAbandoned` с полями `lane`, `extension`, `abandoned`. Error-события app-лога ограничены
`AppLogEventThrottle` (первые K = 5 на имя события, затем одно `LogEventSuppressed` с `event`/`limit`, далее только
счётчик): сбой, повторяющийся на каждую иконку/пробу, не заполняет лог стек-трейсами. Трасса `--diag` получает каждое
вхождение. Логика очереди, приоритеты, watchdog, circuit breaker и приватность payload (closed-set значения) не менялись.

## Manual verify (Windows 11 24H2) — не блокер

- **M-7.1** Старт с порталом на 1000+ локальных файлов + панель ярлыков на недоступную шару + отключённый
  `Z:\`: панели и меню отзывчивы сразу; серые квадраты — мгновение, затем type-иконки, затем реальные.
  `PanelItemsBuilt.elapsedMicroseconds` ≤ 16 000 для 1000 элементов, иначе — R7-8 (генерация контейнеров).
- **M-7.2** Ярлыки на недоступную шару + элементы на `Z:\`: нет подвисаний каждые ≈3 с (liveness); один
  `RemoteRootQuarantined` на корень за 60 с; при возврате шары элементы получают реальные иконки в пределах
  ~1 минуты.
- **M-7.3** Удаление панели, смена портала, переименование, смена `IconSize`, смена страницы во время
  загрузки: без исключений, без «чужих» иконок, без вспышки в плейсхолдер при ресайзе.
- **M-7.4** Сторонние shell-extension (7-Zip/WinRAR, OneDrive/облачный клиент, Office): иконки корректны,
  online-only файл OneDrive не скачивается.
- **M-7.5** Замер `IconQueueDrained` для портала на 1000 элементов; если > ≈3 с — кандидат на
  `MaxStaWorkersPerLane = 2` для Local (D-2).
- **M-7.6** Выход приложения (трей → Exit) во время загрузки и при зависшем сетевом ярлыке: завершение без
  задержки больше ~1 с, без crash-report.
- **M-7.7** Перезапуск Explorer (ADR-034) во время загрузки иконок: иконки догружаются, падений нет.

## Related

- [ADR-039](ADR-039-sized-shell-icons.md) — трёхстадийный фолбэк и кеш `(path, bucket)`, вызывающий поток
  которого меняет этот ADR.
- [ADR-012](ADR-012-performance-and-resilience-hardening.md) Решение 3 — `Freeze()`-инвариант, расширенный
  рантайм-проверкой.
- [ADR-008](ADR-008-shell-com-sta-threading-and-pidl-marshaling.md) — STA-контракт shell COM, отклонённая
  альтернатива A, принятая здесь для read-only извлечения иконок.
- [docs/DIAGNOSTICS/icon-loading-findings.md](../docs/DIAGNOSTICS/icon-loading-findings.md) — измерение
  (P08, находка 5), которое этот ADR закрывает.
- `docs/Refactoring/Testing/diagnostics-log-events.md` — полный список новых событий категорий `icon`/
  `liveness`/`panel`.
- [docs/PLANS/background-icon-loading.md](../docs/PLANS/background-icon-loading.md) — исходный план,
  считается выполненным и остаётся историческим артефактом.
