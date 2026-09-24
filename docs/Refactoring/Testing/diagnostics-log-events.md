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

`%LOCALAPPDATA%\DesktopOrganizer\Logs\gestures-{yyyyMMdd-HHmmss}-{pid}.jsonl`.

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
| `category` | string | `session` \| `gesture` \| `capture` \| `panel` \| `state` (Фаза 4) \| `layout` (Фаза 6) |
| `name` | string | Имя события (см. таблицу ниже) |
| `scope` | string \| null | Первые 8 символов `Guid` панели (`N`-формат), `null` для session-событий |
| `payload` | object | Поля события, см. ниже |

## Privacy-инвариант

**Категорически запрещено** писать в `payload`: заголовки панелей, имена/пути файлов, содержимое
`ContentFilter`, любые пользовательские строки произвольной длины. Разрешено: `scope` (guid-префикс),
геометрия (double), enum-имена (string, из закрытого набора), bool, count/duration (int/long).
Причина: тестировщики будут присылать эти файлы разработчикам для диагностики зависаний — лог не должен
содержать личных данных. Инвариант закреплён unit-тестами (`GestureDiagnosticsTests` для `gesture`/
`capture`/`panel`, `SingleInstanceDiagnosticsTests` для single-instance событий категории `session`)
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
| `ResizeStarted` | 0 | `direction`, `boundsX/Y/Width/Height`, `viewMode`, `iconSize`, `snapEnabled`, `snapCellWidth/Height`, `captureOwner` |
| `ResizeDelta` (throttled, ≤1/50мс) | 0 | `direction`, `hChange`, `vChange`, `rawWidth/Height`, `appliedX/Y/Width/Height` |
| `ResizeCompleted` | 0 (payload расширен в Фазе 1, commit C8) | `direction`, `canceled` (из `DragCompletedEventArgs.Canceled`, сохранён для обратной совместимости со схемой Фазы 0), `boundsX/Y/Width/Height`, `lastRawWidth/Height`, `lastAppliedX/Y/Width/Height`, `deltaCount`, `emittedCount`, `durationMs`, `endReason` (`Interaction.ResizeEndReason.ToString()` — закрытый набор: `Completed`/`Canceled`/`CaptureLost`/`SourceChanged`/`Unloaded`/`Closing`/`Failed`), `exceptionType` (только когда `endReason == Failed`: имя типа исключения, `Exception.GetType().Name` — НИКОГДА `Exception.Message`; `null` для остальных `endReason`) |
| `DragStarted` | 0 | `startCursorX/Y`, `boundsX/Y/Width/Height` |
| `DragDelta` (throttled, ≤1/50мс) | 0 | `appliedX/Y/Width/Height` |
| `DragCompleted` | 0 | `boundsX/Y/Width/Height`, `lastAppliedX/Y/Width/Height`, `deltaCount`, `emittedCount`, `durationMs` |
| `SizeMoveEntered` (`WM_ENTERSIZEMOVE`) | **2 — реализовано** (commit C4, `NativePanelResizeController`) | `hwnd`, `edge` (`Services.Abstractions.NativeSizingEdge.ToString()`) |
| `SizeMoveExited` (`WM_EXITSIZEMOVE`) | **2 — реализовано** (commit C4, `NativePanelResizeController`) | `hwnd`, `finalX/Y/Width/Height` (DIP, `GetWindowRect` converted via `NativeSizingCoordinateMapper.ToDip`) |

### `capture` (scope = panel id prefix)

| Событие | Фаза | Payload |
|---|---|---|
| `CaptureGot` | 0 | `sourceName` (тип `e.OriginalSource`), `captureOwner` (`Mouse.Captured?.GetType().Name`) |
| `CaptureLost` | 0 | `sourceName`, `captureOwner`, `thumbIsDragging` (`Thumb.IsDragging` на момент потери capture — ключевой сигнал для диагностики зависания resize, см. ADR-021) |

### `panel` (scope = panel id prefix)

| Событие | Фаза | Payload |
|---|---|---|
| `PanelClosing` | 0 | `thumbIsDragging` |
| `ExistenceRefreshFailed` | **P08 fix cycle 1, 2026-09-22 (UX P2)** — `GestureDiagnostics.ExistenceRefreshFailed`, вызывается из `ChildWindowViewModel.RefreshExistence`'s `catch` | `exceptionType` (`Exception.GetType().Name`) — **никогда** `Exception.Message`. Заменяет `Console.Error.WriteLine`, который в `WinExe`-процессе никто не читает (тот же класс дефекта, что и `icon`-категория ниже) |
| `AlignToWorkspaceGridRefused` | **P03 fix cycle 1 final verification, 2026-09-22** — `GestureDiagnostics.AlignToWorkspaceGridRefused`, вызывается из `ChildWindow.WorkspaceGrid.cs` при отказе `WorkspaceGridPlacement.TryAlign` | `reason` — код (`NoMonitor` / `Unmeasured`), **никогда** title/path панели. Заменяет `Console.Error.WriteLine` |

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

### `icon` (scope = null) — P08, docs/DIAGNOSTICS/icon-loading-findings.md

| Событие | Источник | Payload |
|---|---|---|
| `IconHandleFailed` | `IconHandler.GetIconHandle` — `SHGetFileInfoW` returned a null handle | `extension` (string, lower-cased, ≤16 chars — see privacy note below), `attributes` (`Services.Enums.FileAttribute.ToString()`, closed set: `Normal`/`Directory` in practice) |
| `IconDestroyFailed` | `IconHandler.DestroyIconHandle` — `DestroyIcon` returned `false` | — (no path available at this call site to begin with) |
| `IconRetrievalFailed` | `IconProvider.TryRetrieveIcon` — any exception during shell lookup/bitmap creation | `extension`, `errorType` (`Exception.GetType().Name`) |
| `IconFreshnessCheckFailed` | `IconProvider.TryGetLastWriteTimeUtc` — `File`/`Directory.GetLastWriteTimeUtc` threw | `extension`, `errorType` |

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
helper, что и остальные события категорий `gesture`/`capture`/`panel`), пишется ПОСЛЕ мутации
`ChildWindowModel.SizingMode` (`AutoContent → Manual`), чисто наблюдательное — не влияет на сам переход.
Единственный сегодняшний вызывающий путь — завершённый пользовательский resize-жест (Thumb ИЛИ нативный
`WM_SIZING`), затронувший ВЫСОТУ панели, для панели, находившейся в `PanelSizingMode.AutoContent`
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

## Использование при диагностике зависания

Для каждого сценария из `resize-repro-matrix.md`: воспроизвести с `--diag`, найти в
`%LOCALAPPDATA%\DesktopOrganizer\Logs\gestures-*.jsonl` последнюю запись для нужного `scope` —
это последнее зафиксированное состояние gesture lifecycle непосредственно перед зависанием.
Особое внимание — есть ли `CaptureLost` без последующего `ResizeCompleted` (сигнатура утечки capture,
которую Фаза 1 должна устранить).
