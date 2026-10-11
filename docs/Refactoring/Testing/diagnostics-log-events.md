# Diagnostics Log — Event Schema

Канонический артефакт Фазы 0 ("structured log event list", PHASE-0 §2). Описывает формат JSONL-лога
gesture lifecycle, полный список событий и privacy-инвариант. Реализация: `IDiagnosticsLog` (Services.Abstractions),
`JsonLinesDiagnosticsLog`/`NullDiagnosticsLog` (Services), `IGestureDiagnostics`/`GestureDiagnostics` (UI),
`SingleInstanceCoordinator` (Services — session-события жизненного цикла процесса, Фаза 3),
`JsonApplicationStateStore`/`StateRestoreCommand`/`Program.cs`/`ApplicationStateService` (Services/UI —
категория `state`, Фаза 4, ADR-024).

## Как включить

- Debug-сборка: включено по умолчанию.
- Release-сборка: выключено по умолчанию. Включить: флаг `--diag` в command line ИЛИ переменная окружения
  `DESKTOPORGANIZER_DIAG=1`.
- Реализация — `DiagnosticsSwitch.IsEnabled(...)` (`src/DesktopOrganizer.Services/Diagnostics/DiagnosticsSwitch.cs`).

## Где лежит лог

`<корень хранилища>\Logs\app-{yyyyMMdd-HHmmss}-{pid}.jsonl`, где корень (`AppStorageRoot.LogsDirectory`) зависит от канала
(ADR-050, ADR-024 Amendment 2026-10-10): Inno-сборка — `%LOCALAPPDATA%\DesktopOrganizer`, Store-сборка (package identity) —
`%USERPROFILE%\DesktopOrganizer`; Debug-сборка добавляет суффикс `.Dev`. Везде ниже в этом документе
`%LOCALAPPDATA%\DesktopOrganizer\Logs` читается как «`Logs\` под корнем канала».

- Ротация: файл сессии переключается на `-1`/`-2` суффиксы при превышении 10 МБ; хранится не более
  10 файлов сессий (старые удаляются при старте).
- Формат: JSON Lines, UTF-8 без BOM, один JSON-объект на строку.
- Запись асинхронная (фоновый поток-писатель), вызывающий (UI) поток никогда не делает файловый I/O —
  см. `JsonLinesDiagnosticsLog`.

## Форма строки

```json
{"timestamp":"2026-09-17T10:15:00.1234567+03:00","seq":42,"category":"gesture","name":"ResizeStarted","scope":"a1b2c3d4","payload":{"direction":"BottomRight","boundsX":100.0,"boundsY":100.0,"...":"..."}}
```

| Поле | Тип | Описание |
|---|---|---|
| `timestamp` | ISO 8601 с офсетом | `DateTimeOffset.Now` на момент `Write` |
| `seq` | integer | Монотонный счётчик per-process (не переживает перезапуск) |
| `category` | string | `session` \| `gesture` \| `panel` \| `state` (Фаза 4) \| `layout` (Фаза 6) \| `icon` (P08, extended ADR-046) \| `liveness` (ADR-046) |
| `name` | string | Имя события (см. таблицу ниже) |
| `scope` | string \| null | Первые 8 символов `Guid` панели (`N`-формат), `null` для session-событий |
| `payload` | object | Поля события, см. ниже |

## Privacy-инвариант

**Категорически запрещено** писать в `payload`: заголовки панелей, имена/пути файлов, содержимое
`ContentFilter`, любые пользовательские строки произвольной длины. Разрешено: `scope` (guid-префикс),
геометрия (double), enum-имена (string, из закрытого набора), bool, count/duration (int/long).
Причина: тестировщики будут присылать эти файлы разработчикам для диагностики зависаний — лог не должен
содержать личных данных. Инвариант закреплён unit-тестами (`GestureDiagnosticsTests` для `gesture`/
`panel`, `SingleInstanceDiagnosticsTests` для single-instance событий категории `session`)
и в XML-doc `DiagnosticEvent`.

Отдельно для Фазы 3: `{sidHash}`/`userKey` (дериват SID пользователя, из которого собираются имена
guard-мьютекса и активационного pipe'а) запрещено писать в payload — см. «Кто пишет single-instance
события» ниже.

## Полный список событий

### `session` (scope = null)

| Событие | Фаза | Payload |
|---|---|---|
| `SessionStarted` | 0 | `appVersion`, `osVersion`, `dpiScale`, `monitorCount`, `panelCount` |
| `SessionExiting` | 0 | — |
| `DiagnosticsDropped` | 0 | `count` — синтетическая метка, пишется писателем при переполнении очереди |
| `SingleInstancePrimaryAcquired` | **3 — реализовано** (commit C6, `SingleInstanceCoordinator.Start`) | `attempts` (int), `elapsedMs` (long), `wasAbandoned` (bool — предыдущий primary был убит, mutex достался через `AbandonedMutexException`), `serverStarted` (bool) |
| `SingleInstanceSecondaryDetected` | **3 — реализовано** (commit C6, `SingleInstanceCoordinator.Start`) | `attempts` (int), `elapsedMs` (long), `sendResult` (`Services.Abstractions.ActivationSendResult.ToString()` — закрытый набор: `Delivered`/`PrimaryShuttingDown`/`OtherSession`/`NoListener`/`Timeout`/`ProtocolError`/`Faulted`; литерал `None`, если отправки не было). **Пишется только на degraded-пути D-4** — см. «Кто пишет» ниже |
| `ActivationRequestReceived` | **3 — реализовано** (commit C6, `SingleInstanceCoordinator`, поток pipe-сервера) | `command` (`ActivationCommand.ToString()` — в `DO/1` всегда `ActivateSettings`), `protocolVersion` (int), `sameSession` (bool — `ActivationRequest.SessionId` совпал с сессией primary; `false` = FUS), `bufferedAtArrival` (bool — пришло до готовности UI и было отложено `ActivationRequestRelay`) |
| `ActivationServerUnavailable` | **3 — реализовано** (commit C6, `SingleInstanceCoordinator.TryBeginServing`) | `reason` (закрытый набор: `PipeBusy` — единственный инстанс pipe'а занят другой сессией, ADR-023 D-4; `AclFailed` — ACL канала не построить, сервер сознательно не стартует без ACL, R-14; `Faulted` — прочее) |
| `SingleInstanceShutdownSequence` | **3 — реализовано** (commit C6, `SingleInstanceCoordinator.Dispose`) | `stopServerMs` (long), `serverStopTimedOut` (bool — bounded join 2 с не уложился), `mutexReleased` (bool) |

#### Кто пишет single-instance события (ADR-023 R-12)

Все пять пишутся **только в primary-процессе**. Secondary (обычный второй запуск — тот, что не получил
guard-мьютекс) **не резолвит `IDiagnosticsLog` вообще**: в Debug-сборке диагностика включена по
умолчанию, и 20 быстрых запусков (`Invoke-SingleInstanceStress.ps1`, M-1/M-12) создали бы 20 файлов
сессий и вымели бы лог самого primary по retention (10 файлов). Поэтому:

- `SingleInstanceSecondaryDetected` появляется в логе **только** на degraded-пути D-4 (процесс захватил
  guard, но pipe-сервер занят другой сессией → guard освобождён, процесс всё-таки secondary) — то есть
  ровно там, где `IDiagnosticsLog` уже был материализован соседним `ActivationServerUnavailable`;
- про обычный secondary в логе primary остаётся строка `ActivationRequestReceived`.

**`{sidHash}`** (16 hex-символов SHA-256 от SID, из которых собраны имена guard-мьютекса и pipe'а)
и `userKey` **не пишутся никогда, ни в одном поле** — это дериват идентификатора пользователя.
Инвариант закреплён `SingleInstanceDiagnosticsTests.Payload_AcrossFullPrimaryLifecycle_…`, который
дополнительно сверяет каждое строковое значение payload с закрытым allow-list имён enum'ов.

### `gesture` (scope = panel id prefix)

| Событие | Фаза | Payload |
|---|---|---|
| `ResizeStarted` | 0; с ADR-022 Фаза 1 (2026-10-10) пишется **только** из native `WM_ENTERSIZEMOVE` (`NativePanelResizeController.SizeMoveEntered` после `TryBegin`) | `direction`, `boundsX/Y/Width/Height`, `viewMode`, `iconSize`, `snapEnabled`, `snapCellWidth/Height`, `captureOwner` (всегда константа `"NativeSizingLoop"`; прежнее имя типа элемента-владельца capture — `Thumb` — ушло вместе с Thumb-путём) |
| `ResizeDelta` (throttled, ≤1/50мс) | 0 | `direction`, `hChange`, `vChange`, `rawWidth/Height`, `appliedX/Y/Width/Height` |
| `ResizeCompleted` | 0 (payload расширен в Фазе 1, commit C8) | `direction`, `canceled` (`true` только для `endReason == Canceled`, т.е. native ESC при rect, равном стартовому, ADR-022 G-4; сохранён для обратной совместимости со схемой Фазы 0, раньше брался из `DragCompletedEventArgs.Canceled`), `boundsX/Y/Width/Height`, `lastRawWidth/Height`, `lastAppliedX/Y/Width/Height`, `deltaCount`, `emittedCount`, `durationMs`, `endReason` (`Interaction.ResizeEndReason.ToString()` — закрытый набор: `Completed`/`Canceled`/`CaptureLost`/`SourceChanged`/`Unloaded`/`Closing`/`Failed`), `exceptionType` (только когда `endReason == Failed`: имя типа исключения, `Exception.GetType().Name` — НИКОГДА `Exception.Message`; `null` для остальных `endReason`) |
| `DragStarted` | 0 | `startCursorX/Y`, `boundsX/Y/Width/Height` |
| `DragDelta` (throttled, ≤1/50мс) | 0 | `appliedX/Y/Width/Height` |
| `DragCompleted` | 0 | `boundsX/Y/Width/Height`, `lastAppliedX/Y/Width/Height`, `deltaCount`, `emittedCount`, `durationMs` |
| `SizeMoveEntered` (`WM_ENTERSIZEMOVE`) | **2 — реализовано** (commit C4, `NativePanelResizeController`) | `hwnd`, `edge` (`Services.Abstractions.NativeSizingEdge.ToString()`) |
| `SizeMoveExited` (`WM_EXITSIZEMOVE`) | **2 — реализовано** (commit C4, `NativePanelResizeController`) | `hwnd`, `finalX/Y/Width/Height` (DIP, `GetWindowRect` конвертирован `ToDesktopDip` по снимку `IDesktopScaleProvider.Current`; `NativeSizingCoordinateMapper` с ADR-037 не используется) |

### Категория `capture` — удалена (ADR-022 Фаза 1, 2026-10-10)

События `CaptureGot` и `CaptureLost` (и их поля `sourceName`, `captureOwner`, `thumbIsDragging`) писались только обработчиками
`Mouse.GotMouseCapture`/`LostMouseCapture` на `ResizeOverlay` Thumb-пути. Путь удалён, `IGestureDiagnostics.CaptureGot`/
`CaptureLost` удалены, в `src/` нет ни одного `Write("capture", …)`; в логах старых сборок категория встречаться может.
Эквивалент потери capture на native-пути — `ResizeCompleted.endReason == "CaptureLost"` (системный цикл прерван:
`NativePanelResizeController.SizeMoveAborted`, `WM_CAPTURECHANGED`).

### `panel` (scope = panel id prefix)

| Событие | Фаза | Payload |
|---|---|---|
| `PanelClosing` | 0; поле переименовано в ADR-022 Фазе 1 (2026-10-10) | `resizeActive` (bool — панель закрывается во время активного resize-жеста: `PanelInteractionTracker.State == Resizing`); прежнее `thumbIsDragging` (`(Mouse.Captured as Thumb)?.IsDragging`) удалено |
| `ExistenceRefreshFailed` | **P08 fix cycle 1, 2026-09-22 (UX P2)** — `GestureDiagnostics.ExistenceRefreshFailed`, вызывается из `ChildWindowViewModel.RefreshExistence`'s `catch` | `exceptionType` (`Exception.GetType().Name`) — **никогда** `Exception.Message`. Заменяет `Console.Error.WriteLine`, который в `WinExe`-процессе никто не читает (тот же класс дефекта, что и `icon`-категория ниже) |
| `AlignToWorkspaceGridRefused` | **P03 fix cycle 1 final verification, 2026-09-22** — `GestureDiagnostics.AlignToWorkspaceGridRefused`, вызывается из `ChildWindow.WorkspaceGrid.cs` при отказе `WorkspaceGridPlacement.TryAlign` | `reason` — код (`NoMonitor` / `Unmeasured`), **никогда** title/path панели. Заменяет `Console.Error.WriteLine` |
| `PanelItemsBuilt` | **ADR-046, backlog 1.x item 7** — `GestureDiagnostics.PanelItemsBuilt`, `Stopwatch` вокруг `BaseViewModel.AddFilesCore` + непосредственно следующего фильтра/сортировки в `LoadFromModel`/`ReplaceAll`/Folder-Portal-ресинке. Ровно та часть метрики ROADMAP «не дольше одного кадра», которую можно измерить без Windows-GUI — построение `FileItem`'ов без fs/shell (D-5) плюс проход фильтра/сортировки; загрузка иконок и liveness-проба асинхронны и не блокируют этот проход (генерация контейнеров невиртуализированной `AdaptiveIconGridPanel`, ADR-018, — отдельная, не покрываемая этим событием стоимость, см. `docs/PLANS/background-icon-loading.md` §8.4) | `count` (int, число элементов после перестройки), `elapsedMicroseconds` (long) |
| `IconPipelineSynchronousFallbackUsed` | **ADR-046 D-13, ревью iteration 2 (L-4)** — `GestureDiagnostics.IconPipelineSynchronousFallbackUsed`, вызывается когда `BaseViewModel`/`ChildWindowViewModel` получили реальные `IIconLoadQueue`/`IItemProbeQueue`, но у конструирующего потока не было живого `SynchronizationContext` для доставки — VM молча деградировал до синхронного легаси-пайплайна (`SynchronousIconLoadQueue`/`SynchronousItemProbeQueue`, UI/Services). Ожидаемо для headless/тестового вызывающего; в продакшене обычно означает, что панель-ViewModel сконструирована до установки UI-диспетчерского `SynchronizationContext` | `component` — закрытый набор: `"iconLoadQueue"` \| `"itemProbeQueue"` |

### `state` (scope = null) — Фаза 4, ADR-024

Источники: `JsonApplicationStateStore`, `AtomicJsonFileWriter` (через `AtomicWriteFailedException`),
`StateRestoreCommand` (`--restore-state-backup`, C7), `Program.cs` (storage lock, C7),
`ApplicationStateService` (`ApplicationStateService.cs` — write-barrier half, post-review M-1).
Полный список имён — из ADR-024 §4 мастер-плана. Ниже — **фактическое** состояние реализации на
конец пост-ревью фиксов (не запланированное): что реально пишет событие, с каким payload, и что из
спецификации §4 плана **не** реализовано и почему.

| Событие | Статус | Payload (фактический) |
|---|---|---|
| `StateLoaded` | **реализовано** (`JsonApplicationStateStore.Load`) | `status` (`StateLoadStatus.ToString()`), `schemaVersionOnDisk` (int), `migrationsApplied` (int) |
| `StateMigrated` | **не реализовано** | — (см. «Что не реализовано» ниже) |
| `StateMigrationFailed` | **реализовано (M-6, пост-ревью)** (`JsonApplicationStateStore.HandleMigrationFailure`, вызывается из `TryMigrate` при необработанном исключении внутри `ISettingsMigration.Migrate`) | `fromVersion` (int, версия документа, для которого миграция упала), `toVersion` (int, `StateEnvelope.CurrentSchemaVersion`), `errorType` (`Exception.GetType().Name`) — **никогда** `ex.Message`. Документ после этого события уходит в карантин (`QuarantineFile`), процесс стартует с пустым записываемым состоянием вместо падения на каждом следующем запуске |
| `StateLegacyImported` | **реализовано, частично** (`JsonApplicationStateStore.LoadCore`, rung 5) | `migrationsApplied` (int) |
| `StateSaved` | **реализовано, частично** (`JsonApplicationStateStore.Save`) | `schemaVersion` (int), `trigger` — см. «Что не реализовано» ниже |
| `StateSaveFailed` | **реализовано** (`JsonApplicationStateStore.Save` и `PersistAfterLoad`) | `stage` (`StateWriteStage.ToString()`), `errorType` (`Exception.GetType().Name`) |
| `StateRecoveredFromBackup` | **реализовано (C7)** — два независимых писателя | `reason`: `"PrimaryMissing"`/`"PrimaryCorrupt"` (автоматическая лестница восстановления, D-7 rungs 2/3, `JsonApplicationStateStore.LoadCore`), либо `"ManualRestoreCommand"` (`StateRestoreCommand.Restore`, `--restore-state-backup`, только при **успешном** восстановлении — см. `StateRestoreFailed` ниже для неудачи) |
| `StateRestoreFailed` | **реализовано (пост-ревью, заменяет вводивший в заблуждение `StateRecoveredFromBackup` при неудаче — MINOR-4)** (`StateRestoreCommand.Restore`, ветка `catch` вокруг записи нового primary) | `errorType` (`Exception.GetType().Name`). Пишется, когда запись восстановленного кандидата на диск не удалась; предыдущий primary-файл в этом случае гарантированно не тронут (M-4 — запись нового primary теперь происходит **до** карантина старого, см. ADR-024 D-11-соседний раздел «Bugs found and fixed») |
| `StateQuarantined` | **реализовано** (`JsonApplicationStateStore.QuarantineFile` — карантин при парсинге/миграции; `JsonApplicationStateStore.QuarantineLoadedDocument` — карантин при частичной загрузке, C-1, см. ниже; `StateRestoreCommand.QuarantinePreviousPrimaryAfterWrite`) | `bytes` (long, размер закарантиненного файла); дополнительно `reason` (string) **только** когда событие пишется из `QuarantineLoadedDocument` — на сегодня единственное фактическое значение `"PartialPanelLoad"` (C-1, см. ниже). Вызовы из `QuarantineFile`/`QuarantinePreviousPrimaryAfterWrite` поле `reason` не передают |
| `StateStoreLockUnavailable` | **реализовано (C7)** (`Program.RunSingleInstanceHandshakeAndApp`, только на нормальном пути запуска — не на пути `--restore-state-backup`, который вместо этого просто выходит с кодом `2`, см. `Storage/rollback.md` сценарий C) | `attempts` (int) — константа `3`, совпадающая с продакшен-дефолтом `StorageFileLock()`; `IStorageLock.TryAcquire()` не возвращает фактическое число попыток, поэтому это не измеренное значение |
| `StateWriteBarrierViolation` | **реализовано** — теперь пишется из **двух** независимых мест (пост-ревью M-1) | `caller`: закрытый набор из **двух** значений: `"Save"` (`JsonApplicationStateStore.Save`, барьер «до `Load()`») и `"PersistNow/ConsumersNotReady"` (`ApplicationStateService.PersistNow`, барьер «`Load()` прошёл, но не все 4 потребителя ещё прочитали свой срез документа» — второй, симметричный барьер, ADR-024 D-11) |

#### `QuarantineLoadedDocument` (C-1, пост-ревью CRITICAL-фикс) — контрактный метод, не отдельное событие

`IApplicationStateStore.QuarantineLoadedDocument(string reason)` — новый метод контракта (не был в
`docs/Refactoring/Storage/IApplicationStateStore.cs` черновике C1/до-ревью). Копирует (не
перемещает — primary остаётся текущим committed-состоянием) текущий primary-файл в timestamped
карантинный файл **до** того, как следующий `Save()` может перезаписать его усечённым документом.
Идемпотентен на один `Load()`: реально копирует только первый вызов после данного `Load()`, дальше —
no-op. Вызывается из `IApplicationStateService.ReportPartialLoad(string reason)`, которую, в свою
очередь, вызывает `WindowManager.LoadWindowsState()`, когда `CreateEachIsolated` сообщает
`skipped > 0` (хотя бы одна сохранённая панель не смогла сконструироваться и была пропущена).
Единственное фактическое значение `reason` на сегодня — `"PartialPanelLoad"`. Само событие, которое
это в итоге пишет на диск, — `StateQuarantined` (см. таблицу выше, поле `reason`), не отдельное имя
события.

Зачем: до перехода на JSON store `WindowManager.BackupPendingCorruptStateIfAny` карантинировала
**полный** payload при частичной загрузке панелей. У store-вого карантина (`QuarantineFile`) внутри
`JsonApplicationStateStore` этой сети безопасности не было — он срабатывает только на ошибку
*парсинга/миграции* документа, а не на то, что вызывающий код успешно распарсил документ и затем сам,
на уровне UI, тихо потерял часть панелей. `QuarantineLoadedDocument` — это ровно недостающий сигнал.

#### Что не реализовано и почему (честный статус на конец пост-ревью фиксов)

- **`StateMigrated`** — по одному событию на каждый примененный шаг миграции (`fromVersion`,
  `toVersion`, `durationMs`) не пишется вовсе. Сегодня агрегат `migrationsApplied` (сколько шагов
  всего применено) уже есть внутри `StateLoaded`/`StateLegacyImported`, но по-шаговой детализации нет.
  Добавление потребовало бы прокинуть `Func<IDiagnosticsLog>` в `StateMigrationRunner`, который
  сегодня конструируется в трёх разных местах (`JsonApplicationStateStore`, `StateRestoreCommand`,
  `DiConfig`) и имеет отдельный, независимый набор тестов (`StateMigrationRunnerTests`,
  `GoldenMigrationTests`) — риск/объём этой правки выходит за рамки C7 («recovery switch + docs»).
  Оставлено как открытый пункт для следующей итерации Storage-слоя.
- **`StateLoaded`/`StateLegacyImported` payload неполный** — план (§4) хочет также `panelCount`,
  `pageCount`, `snapshotCount`, `durationMs` (и `hadSystemIconsPanel` для `StateLegacyImported`). Эти
  величины принципиально не видны на уровне `JsonApplicationStateStore` (D-3: этот слой работает
  только с `JsonObject`, ничего не знает про панели/страницы) — их знает только UI-слой
  (`ApplicationStateService`). Начиная с пост-ревью фикса M-1 `ApplicationStateService.cs` **уже
  пишет** диагностику (`DiagWriteBarrierViolation` → `StateWriteBarrierViolation` с
  `caller: "PersistNow/ConsumersNotReady"`, см. выше), но только для write-barrier-нарушений — счётчики
  `panelCount`/`pageCount`/`snapshotCount`/`durationMs`/`hadSystemIconsPanel` в `StateLoaded`/
  `StateSaved`/`StateLegacyImported` по-прежнему не добавлены. Закрытие этого пункта — отдельная,
  более крупная правка на границе Services/UI, а не разовое добавление полей; не сделано.
- **`StateSaved.trigger`** — план ожидает `"debounced"` либо `"immediate"` в зависимости от того,
  пришёл ли вызов через `ApplicationStateService.RequestSave()` (debounce 500 мс) или `SaveNow()`
  (синхронно). Фактически `JsonApplicationStateStore.Save` не знает, через какой путь его вызвали —
  `SaveScheduler` (в `ApplicationStateService`) стирает это различие к моменту вызова `Save()` — и
  сегодня **всегда** пишет литерал `"immediate"`, независимо от реального пути. Поле присутствует, но
  недостоверно; не пофикшено в C7 по той же причине, что и предыдущий пункт (нужна правка на границе
  Services/UI). **`bytes`/`durationMs`** тоже отсутствуют — `Save` не измеряет ни размер сериализованного
  документа, ни длительность записи.
- **`StateSaveFailed.attempt`** — план упоминает номер попытки retry (`AtomicJsonFileWriter.ReplaceOrMove`
  делает до 3 попыток при `IOException`). Фактически `AtomicWriteFailedException`/`StateWriteStage` не
  несут номер попытки — событие сообщает, на какой **стадии** протокола записи (D-6) произошёл отказ,
  но не на какой попытке retry внутри стадии `BeforeReplace`. Не реализовано.

#### Privacy-инвариант для `state` (проверено, не предположение)

`StateSaveFailed` — **проверено по коду**: оба места, где он пишется
(`JsonApplicationStateStore.Save` и `PersistAfterLoad`), передают исключительно
`(ex.InnerException ?? ex).GetType().Name` в поле `errorType`. То же самое проверено для двух
пост-ревью событий: `StateMigrationFailed` (`HandleMigrationFailure`) передаёт `failure.GetType().Name`,
`StateRestoreFailed` (`StateRestoreCommand.Restore`) передаёт `ex.GetType().Name` — ни то, ни другое
`ex.Message` не трогает. Нигде в категории `state` не встречается `Exception.Message` или файловый
путь — единственные пути, которые попадают в payload, это `stage`/`status`/`reason` (закрытые наборы
enum-имён/строковых токенов) и `bytes`/`attempts`/`migrationsApplied`/`fromVersion`/`toVersion`
(числа). Закреплено тестами `JsonApplicationStateStoreTests.Save_WriteFails_DiagnosticsCarryOnlyExceptionTypeName_NeverMessageOrPath`
и (для `StateRecoveredFromBackup`/`StateQuarantined`/`StateMigrationFailed`/`StateRestoreFailed`)
`JsonApplicationStateStoreTests`/`StateRestoreCommandTests` диагностика-ассерты — отдельного файла
`StateDiagnosticsTests.cs` (упомянутого в §5.1 мастер-плана как план на будущее) в этой итерации не
заведено, поскольку существующие ассерты уже покрывают privacy-инвариант точечно там, где событие
пишется.

### `icon` (scope = null) — P08, docs/DIAGNOSTICS/icon-loading-findings.md; extended release polish 1.0 (ADR-039); extended ADR-046 (background STA icon loading, backlog 1.x item 7)

| Событие | Источник | Payload |
|---|---|---|
| `IconHandleFailed` | `IconHandler.GetIconHandle` — `SHGetFileInfoW` returned a null handle | `extension` (string, lower-cased, ≤16 chars — see privacy note below), `attributes` (`Services.Enums.FileAttribute.ToString()`, closed set: `Normal`/`Directory` in practice) |
| `IconDestroyFailed` | `IconHandler.DestroyIconHandle` — `DestroyIcon` returned `false` | — (no path available at this call site to begin with) |
| `ShellItemImageFailed` (**release polish 1.0**, ADR-039 stage 1) | `IconHandler.GetShellItemBitmap` — `IShellItemImageFactory::GetImage` returned a failure HRESULT or a null bitmap handle | `extension`, `hresult` (string, `"0x{hr:X8}"` — a closed, numeric failure code, never a message) |
| `IconBitmapReadFailed` (**release polish 1.0**, ADR-039 stage 1) | `IconHandler.TryReadBitmapPixels` — copying the shell-returned `HBITMAP`'s pixels failed | — (no path available at this call site) |
| `IconBitmapDestroyFailed` (**release polish 1.0**, ADR-039 stage 1) | `IconHandler.DestroyBitmapHandle` — `DeleteObject`/the bitmap-delete call returned `false` | — |
| `SystemIconIndexFailed` (**release polish 1.0**, ADR-039 stage 2) | `IconHandler.GetSystemImageListIconHandle` — `SHGetFileInfo(SHGFI_SYSICONINDEX)` returned a null image-list handle | `extension` |
| `ImageListIconFailed` (**release polish 1.0**, ADR-039 stage 2) | `IconHandler.GetSystemImageListIconHandle` — `IImageList::GetIcon` returned a null icon handle for a resolved image-list handle | `extension`, `imageList` (int — the `SHIL_*` constant actually used, e.g. `IconHandler.ShilExtraLarge`) |
| `IconRetrievalFailed` | `IconProvider`'s per-stage `catch` (`TryShellItemImage`/`TrySystemImageList`/`TryRetrieveIcon`) — any exception during shell lookup/bitmap creation for the corresponding fallback stage | `extension`, `errorType` (`Exception.GetType().Name`), `stage` (**release polish 1.0**, ADR-039 — closed set: `"shellItem"` / `"imageList"` / `"fileAttributes"`, one of `IconProvider`'s private stage-name constants) |
| `IconFreshnessCheckFailed` | `IconProvider.TryGetLastWriteTimeUtc` — `File`/`Directory.GetLastWriteTimeUtc` threw | `extension`, `errorType` |
| `IconQueueStarted` (**ADR-046**) | `StaIconLoadQueue.EnsureLaneRunning` — a lane's worker starts (or resumes after being idle) draining its queue | `lane` (closed set: `"local"` \| `"remote"`) |
| `IconQueueDrained` (**ADR-046**) | `StaIconLoadQueue` — a lane's queue emptied out (also the third trigger, alongside batch-size/flush-interval, for flushing every group's result buffer — D-9) | `lane`, `jobs` (int, jobs processed this drain), `elapsedMs` (long) |
| `IconJobTimedOut` (**ADR-046 D-4**) | `StaIconLoadQueue`'s watchdog pass — a job/type-job has been executing longer than `IconLoadQueueOptions.StuckJobTimeout` (default 5 s) | `lane`, `extension` (the stuck job's `IconDiagnostics.SafeExtension`, or its type-key for a type-job) |
| `IconWorkerAbandoned` (**ADR-046 D-4**) | `StaIconLoadQueue`'s watchdog pass — the stuck worker is detached (`IStaWorker.Abandon()`) and a replacement is created for the lane | `lane`, `abandoned` (int, the lane's running count of abandoned-but-not-yet-returned workers) |
| `IconLaneDisabled` (**ADR-046 D-4**) | `StaIconLoadQueue` — a lane exceeded `IconLoadQueueOptions.MaxAbandonedWorkersPerLane` (default 2) simultaneously-abandoned workers and disabled itself for the rest of the session; its jobs now resolve `Unavailable` immediately | `lane` |
| `RemoteRootQuarantined` (**ADR-046 D-3**) | `StaIconLoadQueue`'s watchdog pass, via `IRemoteRootCircuitBreaker.ReportStuck` — a Remote-lane job's own network root is opened (quarantined) after a stuck-job timeout | `rootKind` (closed set: `"unc"` \| `"mappedDrive"` \| `"shortcutTarget"` — never the root path itself) |
| `IconQueueBackpressureDropped` (**ADR-046 D-10**) | `StaIconLoadQueue.Submit` — a pending `Background`-priority job with no live waiters was evicted because `IconLoadQueueOptions.MaxPendingJobs` (default 20 000) was reached | `count` (int, always `1` — one event per eviction) |
| `IconQueueSinkThrew` (**ADR-046 D-9**) | `StaIconLoadQueue` — a group's `onResults` delivery callback threw; the exception is swallowed here so it never reaches `DispatcherUnhandledException` (which would otherwise terminate the process via `CrashCoordinator`) | `errorType` (`Exception.GetType().Name`) |
| `IconResultNotFrozen` (**ADR-046 C-4**) | `StaIconLoadQueue` — a result's `ImageSource` failed the explicit runtime `IsFrozen` check before crossing the worker→UI boundary (a rare mock/provider defect, not the production `IconProvider`); the result is downgraded to `Outcome = Failed` and the icon is not delivered | `stage` (closed set: `"typeIcon"` for a type-job, or the lane name — `"local"`/`"remote"` — for a regular icon-fetch job) |
| `IconQueueWatchdogPassFailed` (**ADR-046**) | `StaIconLoadQueue`'s watchdog timer callback — an unexpected exception during a watchdog pass, caught so the timer keeps running | `errorType` |

Replaces four `Console.Error.WriteLine` call sites the P00 baseline audit found silently discarded by
the `WinExe` process (nothing reads process stderr) and absent from the JSONL log entirely (0 of 698
records in the audited log mentioned an icon failure, despite real failures occurring — see the
baseline's part C). Guarded by `IDiagnosticsLog.IsEnabled` per the zero-cost seam convention; no new
payload shape beyond the privacy invariant below.

#### Privacy note: `extension`

The general privacy invariant (above) allows only `scope`/geometry/enum-names/bool/count-duration —
`icon` events add one narrow exception: a lower-cased file extension (e.g. `.exe`, `.lnk`, or
`string.Empty` for none/for a directory). This is not a title, file name, or path — it carries no more
information than "what kind of file" and is exactly the granularity needed to tell "a shortcut's icon
failed" from "a folder's icon failed" in an attached log. Enforced by `IconProviderTests`/`IconHandlerTests`,
which assert the payload never contains the test's own full path or the exception's raw message (both
of which routinely embed paths) — only `extension`/`errorType`/`attributes`.

**Amendment (2026-09-22, P08 fix cycle 1, M2 finding).** `Path.GetExtension` has no notion of a "real"
registered file extension — for a path like `C:\Users\ivan.petrov` (a DIRECTORY whose own trailing name
segment contains a dot — e.g. a Windows user-profile folder), it happily returns `.petrov`, which can
carry a surname or other personal-data fragment straight into the payload. `IconDiagnostics.SafeExtension`
now applies two guards before the value above is ever written, closing two different (not fully
overlapping) slices of the risk:

- A directory always yields `string.Empty` — its "extension" (if any) is a directory-name artifact,
  never a real file-type signal. This is what closes the exact `ivan.petrov` example above. **NIT fix
  (P08 fix cycle 2, 2026-09-22):** `Path.GetExtension` is checked FIRST, and `Directory.Exists(path)`
  only when it actually returned something non-empty — an extension-less path knowably yields
  `string.Empty` either way, so the disk I/O in `Directory.Exists` is skipped entirely for that (common)
  case instead of running on every single icon-pipeline diagnostics event. Output is unchanged either
  way; only the order (and the resulting I/O cost) changed.
- Whatever `Path.GetExtension` returns for a FILE (not a directory) is only accepted verbatim if it
  matches `^\.[a-z0-9]{1,8}$` (case-insensitive, checked against the already-lower-cased value) —
  closing the unicode/punctuation/implausibly-long class of fragment (e.g. `.日本語`, `.a-b`) by
  degrading to the closed literal `"other"` (`IconDiagnostics.OtherExtension`).

**Known, accepted residual limitation:** a short, alphanumeric FILE-name fragment that happens to look
exactly like a plausible extension (e.g. a file literally named `report.smith`) is structurally
indistinguishable from a real one and is NOT filtered by the regex above — there is no way to tell "an
obscure but real extension" from "a surname that happens to be short and alphabetic" without
maintaining an explicit allow-list of known real extensions, which was considered and rejected (see
ADR-027 Amendment, "Alternatives considered" — an unbounded list to maintain for a gain the directory
guard already covers for the actual reported scenario).

The previous length-cap-only guard (≤16 chars, still leaking `.petrov`-shaped values under that bound)
is superseded by this allow-list. Enforced by `IconDiagnosticsTests.SafeExtension_UnrecognizedDottedFragment_DoesNotLeakItVerbatim_ReturnsOtherBucket`,
`IconDiagnosticsTests.SafeExtension_DirectoryWithDotInOwnName_ReturnsEmpty_NeverTheNameFragment`, and
`IconDiagnosticsTests.SafeExtension_ShortAlphanumericFileNameFragment_IsAcceptedAsIfItWereARealExtension`
(documents the residual limitation explicitly, not silently). See ADR-027 Amendment (2026-09-22) for the
corresponding decision record.

### `liveness` (scope = null) — ADR-046, backlog 1.x item 7

| Событие | Источник | Payload |
|---|---|---|
| `ItemProbeFailed` | `BackgroundItemProbeQueue`'s per-path `catch` around `IPathProbe.Probe` — an unexpected exception while probing one path of a liveness batch on the thread pool; the batch's `Task` does not fault, the path's own result degrades to `Exists = false, IsKnown = false` (legacy `File.Exists` semantics) | `errorType` (`Exception.GetType().Name`) |

Liveness (7c, ADR-046) moved `ChildWindowViewModel`'s background existence-liveness poll off the UI thread
onto `BackgroundItemProbeQueue` (thread pool, ≤2 concurrent batches). `ItemProbeFailed` is this category's
only event today — the pass-level outcomes (timeout, generation mismatch, disposed VM) are not logged, only
applied/discarded per the invariants in `docs/PLANS/background-icon-loading.md` §7.1/§7.2 (M-A/M-C).

### `layout` (scope = null для `LayoutModeChanged`, panel id prefix для `SizingModeChanged`) — Фаза 6/ADR-026, workspace-grid/ADR-029

| Событие | Фаза | Payload |
|---|---|---|
| `LayoutModeChanged` | **6 — реализовано** (`LayoutEditModeService.SetMode`, вызывается из `Services/LayoutEditModeService.cs`, обёртка `IGestureDiagnostics.LayoutModeChanged` реализована в `Diagnostics/GestureDiagnostics.cs`) | `from` (`LayoutInteractionMode.ToString()` — `Normal`/`EditLayout`), `to` (то же), `source` (`LayoutModeChangeSource.ToString()` — закрытый набор: `Tray`/`PanelMenu`/`SettingsWindow`/`Keyboard`/`Programmatic`), `isLocked` (bool, текущее значение `IAppSettings.IsLayoutLocked` на момент перехода) |
| `SizingModeChanged` | **workspace-grid P06, fix-цикл MAJOR-4, 2026-09-22** (`GestureDiagnostics.SizingModeChanged`, `Diagnostics/GestureDiagnostics.cs:400-411`; вызывается из `Views/ChildWindow.ContentSizing.cs`'s `OnUserHeightResizeCommitted`, см. ADR-029 «Fix-цикл P06» MAJOR-4) | `from` (`PanelSizingMode.ToString()`), `to` (то же), `reason` (`string` — не enum по типу сигнатуры `IGestureDiagnostics.SizingModeChanged`, но на сегодня единственный вызывающий код передаёт ровно один литерал, `"HeightResizeCommitted"`, — де-факто закрытый набор из одного значения; не персональные данные, риск privacy-инварианта не возникает, пока вызывающие ограничиваются короткими кодовыми именами, а не свободным текстом) |

`LayoutModeChanged` пишется строго на **реальный** переход режима (`from != to`) — повторный вход в уже
активный режим (no-op re-entry, например повторный `Toggle()` на уже открытом Edit Layout через другую
точку входа) не порождает второй записи. `scope = null`: режим глобальный для приложения, не привязан к
конкретной панели (D-1 плана Фазы 6). Порядок относительно остальных эффектов смены режима: сначала
`LayoutInteractionMode.Mode` обновляется, затем поднимается `ILayoutEditModeService.ModeChanged`
(на которое подписаны все живые `ChildWindow` для абортирования активных жестов, D-4), и только
последним — `IGestureDiagnostics.LayoutModeChanged` (чисто наблюдательное событие, ни на что не влияет).

`SizingModeChanged` — per-panel (`scope` = 8-символьный префикс `Guid` панели, тот же `Scope(panelId)`
helper, что и остальные события категорий `gesture`/`panel`), пишется ПОСЛЕ мутации
`ChildWindowModel.SizingMode` (`AutoContent → Manual`), чисто наблюдательное — не влияет на сам переход.
Единственный сегодняшний вызывающий путь — завершённый пользовательский resize-жест (нативный
`WM_SIZING`; Thumb-путь удалён в ADR-022 Фазе 1), затронувший ВЫСОТУ панели, для панели, находившейся в `PanelSizingMode.AutoContent`
(«manual wins» правило ADR-029 Решение 7, Fences-подобное поведение). Событие НЕ пишется при демотации
через любой другой путь (Manual изначально, переключение через UI и т.д. не порождают эту запись —
только именно это, конкретное, автоматическое понижение режима).

## Источник геометрии (`Width`/`Height`, не `ActualWidth`/`ActualHeight`)

Все `boundsX/Y/Width/Height`/`appliedX/Y/Width/Height`/`lastApplied*` в `gesture`-событиях читаются из
`Window.Left/Top/Width/Height`, а не `ActualWidth`/`ActualHeight` — это то же самое, что
`ChildWindow.OnRenderSizeChanged` реально сохраняет в `ChildWindowModel.Size` (персистентное состояние
на диске), так что лог отражает именно закоммиченную/сохранённую геометрию. `ResizeCompleted`/
`DragCompleted` читают эти поля **после** `SnapWidthToGrid`/`PanelDragMoveBehavior.SnapToGrid` и
`IWindowManager.SaveWindowsState()`, а не до — иначе в логе никогда не было бы пост-snap геометрии
(finalization M-5). Единственное место, где `ActualWidth`/`ActualHeight` остаются в силе — реальная
(не диагностическая) анти-overlap математика в `PanelDragMoveBehavior.OnTitleBarMouseMove`, которая эту
доку не касается.

## Throttling-контракт (`ResizeDelta`/`DragDelta`)

- Первая дельта жеста всегда пишется.
- Последующие — не чаще раза в 50 мс (`GestureDiagnostics.DefaultDeltaSampleIntervalMs`).
- Последняя дельта (даже подавленная троттлингом) сохраняется и попадает в `ResizeCompleted`/`DragCompleted`
  как `lastRaw*`/`lastApplied*` — финальная геометрия перед зависанием/отменой никогда не теряется.

## `IAppLog` events added by release polish 1.0 (a DIFFERENT sink — `app-*.jsonl`, ADR-033)

Everything above this section is `IDiagnosticsLog` (ADR-027) — the gesture-lifecycle JSONL log described by
this whole document. The events below are written through `IAppLog`/`AppLog.Current` (`Services/Diagnostics`,
ADR-033) instead: the always-on, Release-default structured application log at
`%LOCALAPPDATA%\DesktopOrganizer\Logs\app-*.jsonl`, a separate file/sink with its own schema
(`source`/`name`/`level`/exception fields, not `category`/`scope`/`payload`). They are listed here, not in a
duplicate document, because they were added by the same release-polish 1.0 round as the `icon` events above
and share the exact same privacy discipline (extension/error-type/closed-set tokens, never a path or
`Exception.Message`) — but they do **not** appear in `%LOCALAPPDATA%\DesktopOrganizer\Logs\app-*.jsonl`'s
gesture-diagnostics sibling file, and the throttling/scope conventions described elsewhere in this document
do not apply to them.

| Событие | Источник | Payload (`IAppLog` fields) |
|---|---|---|
| `ShellContextMenuFailed` (`NativeApi`) | `NativeApi.LogShellMenuFailure`, called from every stage of `ShowShellContextMenuCore` (`BindToParent`/`GetUIObjectOf`/`CreatePopupMenu`/`QueryContextMenu`/`TrackPopupMenu`/`GetCommandString`/`InvokeCommand`) | `stage` (string, one of the stage names above), `extension` (string, or the literal `"<virtual>"` for a `SystemIconsPanel` item with no file extension), `hresult` (`"0x{hr:X8}"`) |
| `ShellContextMenuFailed` (`FileLauncher`) | `FileLauncher.ShowContextMenu`'s own `catch` — belt-and-suspenders around `INativeApi.ShowShellContextMenuForPath`, which already degrades silently on its own | `exception` (`Exception.GetType().Name`), `extension` (`Path.GetExtension(path)`) — note: no `stage` field on this second call site, distinguishable by `source: "FileLauncher"` vs. `source: "NativeApi"` |
| `RecycleManagedFromStorage` | `PanelItemStorageService.RecycleManagedItem` — the item could not first be restored to a visible location (legacy item with no recorded original path, the original directory could not be recreated, or the restore move itself failed), so it is recycled directly from inside managed storage instead (the pre-ADR-040 behavior, kept as this method's own fallback) | `restoreOutcome` (`RestoreOutcome.ToString()`) |
| `RecycleManagedDeclined` | `PanelItemStorageService.RecycleManagedItem` — the shell did not actually recycle the item (the user declined the oversized-item warning, the file is locked) after it had already been restored to its original/desktop location; the item is re-adopted back into managed storage so the panel does not lose it | `readopted` (bool — whether re-adoption into managed storage succeeded) |
| `MoveItemFailed` | `PanelItemsUninstallRestorer.TryMoveItem` — an exception while moving a single item to its resolved target during `--restore-panel-items` | `source` (path), `target` (path) — **exception**: unlike every other event in this document, these two carry the actual file paths, not just an extension; accepted because this event only ever reaches the local diagnostics log of the same machine the uninstall is running on, at the exact moment those paths are being reported as *not yet successfully moved* — the operator reading the log needs the real paths to find and move the file by hand |
| `VerifiedCopySourceDeleteFailed` | `PanelItemsUninstallRestorer.DeleteVerifiedSourceOrWarn` — a cross-volume source could not be deleted after its copy was already verified byte-for-byte (still counted as restored, not failed) | `source` (path), `target` (path), `exception` (`ex.Message`, not just the type name — same accepted exception as `MoveItemFailed` above) |
| `EnumerateStorageRootFailed` / `EnumerateItemDirectoryFailed` / `ManifestReadFailed` / `ManifestTempLeftoverDeleteFailed` / `GuidDirectoryCleanupFailed` / `StorageRootCleanupFailed` | `PanelItemsUninstallRestorer` — best-effort enumeration/cleanup steps during `--restore-panel-items`, logged via `Log.Error(source, name, ex, ...)` | `path` (the folder/file involved — same accepted-path exception as above); the underlying `Exception` is passed to `IAppLog.Error` itself, which is responsible for its own message/type formatting |
| `PanelCloned` (backlog 1.x item 8, ADR-045) | `WindowManager.ClonePanelToPage` — logged via `Log.Info` right after the clone is registered, logged, and persisted, just before `OnChildWindowAdded` fires | `panelId` (the SOURCE panel's opaque id — never a title), `skippedMissingItems` (int — count of managed items that no longer existed on disk and were silently skipped rather than copied; never surfaced to the user, only logged here) |
| `PanelCloneCreateFailed` (backlog 1.x item 8, ADR-045) | `WindowManager.ClonePanelToPage`'s `catch` — any exception after item copying succeeded but before the clone finished registering (e.g. `Window.Show()` itself threw); logged via `Log.Error` with the exception | `panelId` (the SOURCE panel's opaque id) |
| `PanelCloneCloseFailed` (backlog 1.x item 8, ADR-045) | `WindowManager.ClonePanelToPage`'s rollback path — `Window.Close()` on the half-created clone window itself threw while unwinding a failed clone; logged via `Log.Error` with the exception, isolated in its own `try/catch` so it can never prevent the rest of the rollback (discarding the fresh managed-item copies) from completing | `panelId` (the SOURCE panel's opaque id) |
| `SystemThemeInitial` (backlog 1.x item 6, ADR-047) | `SystemThemeProvider.Start` — the theme/high-contrast/font-size snapshot read once at startup, before the listener window exists | `appsUseLightTheme`, `systemUsesLightTheme`, `highContrast` (bool), `systemColorsFingerprint` (`"X8"`-formatted hash of `GetSysColor` values — order-sensitive, deterministic, never randomised per process), `messageFontSize` (double) |
| `SystemThemeChanged` (backlog 1.x item 6, ADR-047) | `SystemThemeProvider.Refresh` — the debounced (100 ms) re-read after a `WM_SETTINGCHANGE`/`WM_SYSCOLORCHANGE`/`WM_THEMECHANGED` broadcast actually changed the snapshot; an unrelated broadcast (or one that round-trips back to the same values) logs nothing | same fields as `SystemThemeInitial` |
| `ListenerWindowCreateFailed` (backlog 1.x item 6, ADR-047) | `SystemThemeProvider.Start` — the provider's own hidden `WS_POPUP` listener window could not be created; the provider degrades to "theme read once at startup", never throws | `win32Error` (int, `GetLastError()`) |
| `StartFailed` / `ReadFailed` / `WndProcError` / `ChangedHandlerFailed` / `ReleaseFailed` (backlog 1.x item 6, ADR-047) | `SystemThemeProvider` — best-effort `catch` blocks around, respectively: `Start()` itself, a registry/`SystemParametersInfo` read, the window procedure, a `Changed` subscriber's own handler, and window teardown in `Dispose()` | `errorType` (`Exception.GetType().Name`) via `Log.Error(source, name, ex)` — never `ex.Message` |
| `ImmersiveDarkModeUnsupported` (backlog 1.x item 6, ADR-047) | `WindowFrameTheming.LogUnsupportedOnce` — the running Windows build predates the immersive dark-mode caption attribute (older than Windows 10 1809); logged once per process, not once per window | `windowsBuild` (int) |
| `ImmersiveDarkModeFailed` (backlog 1.x item 6, ADR-047) | `WindowFrameTheming.TryApply` — `DwmSetWindowAttribute` either threw or returned a failing `HRESULT`; rate-limited to one entry per window handle per minute (a window that keeps failing does not spam the log every `ThemeChanged`) | `attribute` (20 or 19), `dark` (bool, failing-HRESULT case only), `hresult` (`"0x{hr:X8}"`, failing-HRESULT case only), `suppressedSinceLast` (int, how many further failures for the same window were rate-limited away since the last entry) |
| `NativeMenuModeApplied` (backlog 1.x item 6, ADR-047, phase 6.4b) | `NativeMenuTheming.TryApply` — the process's classic Win32 menus (a panel item's shell context menu, a window's system menu) were switched to `ForceDark`/`Default` via the undocumented `SetPreferredAppMode` uxtheme ordinal; idempotent (a repeat call for the same `dark` value logs nothing) | `dark` (bool) |
| `SetPreferredAppModeUnavailable` / `FlushMenuThemesUnavailable` (backlog 1.x item 6, ADR-047, phase 6.4b) | `NativeMenuTheming.TryApply` — the corresponding `uxtheme.dll` ordinal (135/136) could not be resolved or the call itself failed; logged once per process (not once per request), the feature then silently no-ops for the rest of the session | `windowsBuild` (int) |
| `PackageIdentity` (Microsoft Store channel, ADR-050) | `Program.Main` — `Info`, один раз за запуск сразу после `applicationState.Load()`, только в Primary (Secondary не создаёт писателя лога); записывает канал распространения и сырой результат пробы | `packaged` (bool — `IAppPackageContext.IsPackaged`), `underUserProfile` (bool — `AppStorageRoot.IsPackagedLocation`; `false` у Inno и у упакованного процесса, откатившегося на `%LOCALAPPDATA%` из-за пустого профиля), `probeResultCode` (int — код `GetCurrentPackageFullName`: `0`/`122` — упакован, `15700` — нет, `-1` — вызов невозможен), `storageRoot` (путь корня; `LogScrubber` заменяет его на `<path>`, имя учётной записи — на `%USERPROFILE%`) |
| `WriteIgnoredPackaged` (`AutostartService`, ADR-050) | Сеттер `IAutostartService.IsEnabled` под package identity — `Warn`; запись в `HKCU\Run` не выполняется (автозапуск Store-сборки — манифест `startupTask`, меняется только в Параметрах Windows); в штатной работе недостижим (UI показывает ссылку на «Автозагрузку» вместо переключателя) | — |
| `WriteFailed` (`StorageRootReadme`, ADR-050) | `StorageRootReadme.TryWrite` — `Warn`; `README.txt` в корне Store-хранилища не создан из-за `IOException`/`UnauthorizedAccessException`; файл — любезность, приложение продолжает работу | `root` (путь корня), `error` (`Exception.GetType().Name`), `message` (`Exception.Message`) — оба строковых поля проходят `LogScrubber` на записи; исключение из «никогда не `Exception.Message`», принято: событие редкое, а сообщение нужно, чтобы понять причину отказа записи в профиль |
| `ResourceMissing` (`StorageRootReadme`, ADR-050) | `StorageRootReadme.TryWrite` — `Warn`; embedded-ресурс `DesktopOrganizer.Services.Storage.StorageRootReadme.txt` отсутствует в сборке (ошибка упаковки), README не создан | `resource` (имя ресурса) |
| `ShowInfoBalloonFailed` (`TrayIconManager`, ADR-050) | `TrayIconManager.ShowInfoBalloon` — `Error`; `NotifyIcon.ShowBalloonTip` бросил исключение; баннер первого запуска (`FirstRunTrayNotice`) fire-and-forget, наружу не пробрасывается | исключение передаётся в `IAppLog.Error` |

## Использование при диагностике зависания

Для каждого сценария из `resize-repro-matrix.md`: воспроизвести с `--diag`, найти в
`%LOCALAPPDATA%\DesktopOrganizer\Logs\app-*.jsonl` последнюю запись для нужного `scope` —
это последнее зафиксированное состояние gesture lifecycle непосредственно перед зависанием.
Особое внимание — есть ли `SizeMoveEntered`/`ResizeStarted` без последующих `SizeMoveExited` и `ResizeCompleted` (сигнатура
зависшего системного sizing-цикла; прежняя сигнатура «`CaptureLost` без `ResizeCompleted`» относилась к удалённому Thumb-пути).
Для native-пути `endReason == "CaptureLost"` в `ResizeCompleted` — это прерванный системой цикл (`SizeMoveAborted`), а не утечка.
