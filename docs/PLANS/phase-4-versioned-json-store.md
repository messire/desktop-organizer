# Phase 4 — Versioned JSON Store (перевод структурного состояния из `user.config` в versioned JSON)

Источники правды: `docs/Refactoring/PHASES/PHASE-4-storage-migration.md` (спецификация),
`adr/ADR-024-versioned-json-persistence.md` (Proposed), `docs/Refactoring/Storage/IApplicationStateStore.cs`
и `docs/Refactoring/Storage/migration-plan.md` (черновики формы API — **не** продакшен-код, см. D-0),
`adr/ADR-023-single-instance-and-ipc.md` R-15 (явно передан в эту фазу),
`docs/Refactoring/BACKLOG.md` DO-001, `docs/REFACTORING_PLAN.md` §5.2.

Ветка: `refactor/versioned-json-store` (от `rc/0.4.0`).

**Scope: L** (Services.Abstractions + Services + UI-проводка + тесты + docs).
Не XL: нет нового нативного модуля, нет нового окна/XAML, UI-изменения — только проводка
(`Program.cs`, `App.xaml.cs`, 4 сервиса-потребителя, DI). Разбит на 8 логических коммитов C1–C8.

**Ключевая особенность фазы для этой среды:** почти весь DoD достижим здесь. Это файловый I/O,
а не Win32-хуки: атомарная запись, симулированный краш, recovery из backup, golden-миграции —
всё тестируется реальной временной директорией, как уже делают `JsonLinesDiagnosticsLogTests` и
`PanelItemStorageServiceTests`. Manual verify остаётся только для «реальный апгрейд реальной
пользовательской установки» и FUS (§8).

---

## 0. Точное текущее поведение (что именно меняем)

### 0.1. Четыре независимых писателя в один `user.config`

| Что | Кто пишет | Как сериализуется сегодня | Дисциплина записи |
|---|---|---|---|
| `ChildWindowsState` | `WindowManager.PersistWindowsState` (`WindowManager.cs:392-415`) | `JsonSerializer.Serialize(List<ChildWindowModel>)`, **опции по умолчанию** → PascalCase, **числовые** enum'ы, `Point`/`Size` как `{"X":..,"Y":..,"IsEmpty":..}` | `SaveScheduler`: debounce 500 мс (`SaveWindowsState`) / синхронно (`SaveWindowsStateNow`) |
| `Pages` + `CurrentPage` | `PageManagerService.Save` (`PageManagerService.cs:120-125`) | `JsonSerializer.Serialize(List<string>)` + голая строка | **синхронно, всегда** (`_appSettings.Save()`) |
| `LayoutSnapshots` | `LayoutSnapshotService.Save` (`LayoutSnapshotService.cs:126-138`) | `List<LayoutSnapshot>`, PascalCase; внутри — `PanelJsonItems: List<string>`, т.е. **JSON-строки внутри JSON** | синхронно, всегда |
| `SystemIconsPanel` | `SystemIconsPanelManager.Save` (`SystemIconsPanelManager.cs:205-223`) | `PanelDto` с `JsonStringEnumConverter` + `IgnoreCondition.WhenWritingNull` → PascalCase, **строковые** enum'ы | синхронно, при `App.OnExit` и явных действиях |

Итог: **четыре разных JSON-диалекта** (две несовместимые enum-конвенции, JSON-в-строке) внутри
одного XML-контейнера `user.config`, без версии схемы, без атомарности, без бэкапа, каждый
`Settings.Default.Save()` переписывает весь файл целиком.

### 0.2. Что в `user.config` действительно скалярное

`Properties/Settings.settings` (14 ключей). Разделение по спецификации PHASE-4 §Target:

**Остаются в `ApplicationSettingsBase`** (простые user preferences — 9 ключей):
`GridSize`, `SnapResizeToIconGrid`, `NativePanelSizing`, `GridSnapDistance`, `HasShownMoveHint`,
`DefaultBackgroundColor`, `DefaultTextColor`, `DefaultOpacity`, `DefaultIconSize`.

**Переезжают в versioned JSON** (структурное — 5 ключей):
`ChildWindowsState`, `Pages`, `CurrentPage`, `LayoutSnapshots`, `SystemIconsPanel`.

> `CurrentPage` формально скаляр (`string`), но переезжает вместе с `Pages`: инвариант
> «`CurrentPage ∈ Pages`» обязан обновляться атомарно с самим списком, а сегодня это два
> независимых ключа с двумя независимыми записями.

### 0.3. Почему путь `user.config` сегодня стабилен (и что это значит для миграции)

`Directory.Build.props:15` — `<AssemblyVersion>1.0.0.0</AssemblyVersion>` прибит гвоздями именно
потому, что `user.config` лежит под `%LOCALAPPDATA%\...\<AssemblyVersion>\user.config`. Следствия,
критичные для плана:

1. Legacy-чтение при миграции **надёжно**: путь не меняется между релизами, `Settings.Default.Upgrade()`
   в коде нет и не нужен (проверено: `Upgrade()`/`GetPreviousVersion` не вызываются нигде).
2. Новый JSON-store **не должен** быть version-scoped — кладём рядом с уже существующими
   `%LOCALAPPDATA%\DesktopOrganizer\{PanelItems,Logs}`.
3. `installer/DesktopOrganizer.iss:88-91` удаляет при деинсталляции только `{app}` —
   `%LOCALAPPDATA%\DesktopOrganizer\` переживает и апгрейд, и удаление. Это основа rollback-истории (§7).

### 0.4. Существующая полезная дисциплина, которую нельзя потерять

- `SaveScheduler` (debounce 500 мс vs синхронный flush) — Фаза 7, ADR-012. Сохраняется как есть.
- `_lastSavedJson` no-op guard (`WindowManager.cs:403`) — переезжает внутрь store.
- `BackupPendingCorruptStateIfAny` (`WindowManager.cs:426-447`) — карантин битого payload в
  `%LocalAppData%\DesktopOrganizer\ChildWindowsState.corrupt-{ts}.json`. Обобщается до общего
  карантина store (D-5), исходная реализация удаляется.
- `CreateEachIsolated`/`SyncEachIsolated` — per-panel изоляция при загрузке/синке. Остаётся.
- `App.OnExit` пишет **синхронно** до `_serviceProvider.Dispose()` — новый store обязан
  поддержать синхронную запись на этом пути (см. D-2, отказ от async API).

---

## 1. Архитектурные решения (ADR-024 delta)

ADR-024 в черновике фиксирует 3 абзаца. Ниже D-0…D-12 — то, что обязано попасть в него при
переводе Proposed → Accepted (коммит C8).

### D-0. Черновики `Storage/*.cs` — только форма API

Прецедент Фаз 2 и 3 (ADR-022 D-1, ADR-023 D-0): `docs/Refactoring/Storage/IApplicationStateStore.cs`
остаётся артефактом пакета, в продакшен переносится **не дословно**. Отличия и причины — D-1, D-2.

### D-1. `object State` → `JsonObject`; миграции работают над JSON, а не над CLR-типами

Черновик: `record ApplicationStateEnvelope(int SchemaVersion, DateTimeOffset WrittenAtUtc, string AppVersion, object State)`.
`object State` непригоден: миграция v1→v2 обязана читать документ, записанный кодом, которого
уже нет, — десериализовать его в *сегодняшние* CLR-типы нельзя (это и есть запрещённый
спецификацией «deserialize-and-pray upgrade»).

**Решение:** store и цепочка миграций оперируют `System.Text.Json.Nodes.JsonObject`. Типизация
появляется ровно один раз — на выходе store'а, когда UI десериализует уже мигрированный до
`CurrentSchemaVersion` документ в свои DTO.

**Цена:** имена полей схемы существуют в двух местах — в DTO UI-слоя и в строковых литералах
миграций в Services. Компенсируется обязательным контрактным тестом `SchemaContractTests` (§5).

### D-2. API синхронный, не `Task`-based

Черновик: `Task<ApplicationStateEnvelope> LoadAsync(CancellationToken)`. Отвергнуто:

- `App.OnExit` (`App.xaml.cs:136`) обязан завершить запись **до** `_serviceProvider.Dispose()`;
  async там превращается в `.GetAwaiter().GetResult()` — sync-over-async на пути завершения процесса.
- Объём документа — десятки-сотни КБ; «не морозить UI» уже решено debounce'ом `SaveScheduler`,
  а не асинхронностью.
- Если понадобится фоновая запись — в проекте уже есть образец (`JsonLinesDiagnosticsLog`,
  выделенный writer-поток + bounded-wait `Dispose`). Вводить его **сейчас** — преждевременно:
  фоновый писатель на пути shutdown усложняет ровно то место, где данные теряются.

### D-3. Слои

```
Services.Abstractions/Storage/   контракты, без знания о панелях и без WPF
Services/Storage/                атомарная запись, recovery-лестница, цепочка миграций, lock
UI/State/                        DTO схемы, маппинг ChildWindowModel↔DTO, единая точка загрузки
```

`ChildWindowModel` — UI-тип (тянет `System.Windows.WindowState`, `System.Drawing.Point/Size`),
поэтому store **не может и не должен** его знать. Границу режет `JsonObject` (D-1).

### D-4. Файлы на диске

Корень: `%LOCALAPPDATA%\DesktopOrganizer\State\`.

| Файл | Назначение |
|---|---|
| `application-state.json` | primary |
| `application-state.json.bak` | предыдущий заведомо валидный (создаётся самим `File.Replace`) |
| `application-state.json.tmp-{guid}` | временный на время записи; подметается при старте |
| `application-state.corrupt-{yyyyMMdd-HHmmss}.json` | карантин (наследует конвенцию `WindowManager.cs:439`) |
| `%LOCALAPPDATA%\DesktopOrganizer\.storage-lock` | advisory-лок, D-8 |

Ретенция карантина: не более 5 файлов, старые удаляются при старте (повтор дисциплины
`JsonLinesDiagnosticsLog.EnsureDirectoryAndRetention`).

### D-5. Формат: конверт плоский, три зарезервированных ключа

По спецификации PHASE-4 §Формат конверт **плоский** — служебные поля лежат рядом с payload:

```json
{ "schemaVersion": 3, "writtenAtUtc": "2026-09-19T08:00:00.0000000Z", "appVersion": "0.4.0",
  "panels": [], "pages": {}, "layoutSnapshots": [], "systemIconsPanel": {} }
```

Правило: `schemaVersion` / `writtenAtUtc` / `appVersion` — зарезервированные ключи конверта,
любой другой ключ верхнего уровня принадлежит payload. Правило закрепляется тестом (никакая
миграция не имеет права создать payload-ключ с зарезервированным именем).

`appVersion` — информационный (`InformationalVersion`, т.е. `0.4.0`, а не прибитый
`AssemblyVersion` 1.0.0.0); на решения при чтении **не влияет** — решения принимает только
`schemaVersion`. Это записать в ADR явно, иначе появится соблазн ветвиться по версии приложения.

### D-6. Протокол записи (PHASE-4 §Формат, шаги 1-6)

1. Сериализация payload → конверт (`JsonObject`) → UTF-8 без BOM в `…​.tmp-{guid}` в **той же
   директории** (иначе `File.Replace` не атомарен).
2. `FileStream.Flush(flushToDisk: true)` — настоящий fsync, затем `Dispose`.
3. **Validate parse:** заново открыть temp, `JsonNode.Parse`, проверить наличие и тип
   `schemaVersion`, проверить, что payload-ключи на месте. Провал → temp удаляется, primary не тронут.
4. Атомарная замена: `File.Replace(tmp, primary, backup: primary + ".bak", ignoreMetadataErrors: true)`.
   Единственный вызов, который одновременно даёт атомарность **и** `.bak`.
   Если primary ещё не существует (первый запуск) — `File.Move(tmp, primary)` (`File.Replace`
   требует существующего destination).
   `IOException` (AV/индексатор держит хэндл) → 3 попытки с 50/100/200 мс паузой, затем провал.
5. Только после подтверждённого успеха обновляется in-memory committed-состояние: сериализованная
   строка для no-op guard и `CommittedSchemaVersion`. **Провал записи не имеет права двигать
   committed-состояние** — иначе следующий вызов посчитает «нечего писать» и потеря станет вечной
   (это точная копия класса ошибок, который уже ловили в `PanelItemStorageService`,
   `EnsureManifestCacheUnlocked`/`WriteManifestUnlocked`).

Тестовый шов для симуляции краха — `internal`-конструктор с `Func<StateWriteStage, bool>?`
(`AfterTempWrite` | `AfterFlush` | `AfterValidate` | `BeforeReplace` | `AfterReplace`), прямой
аналог `JsonLinesDiagnosticsLog._testForceReopenFailure` и `PanelItemStorageService.forceCrossVolume`.

### D-7. Протокол чтения (recovery-лестница)

Строгий порядок, каждая ступень — отдельный `StateLoadStatus`:

| # | Ситуация | Действие | Status |
|---|---|---|---|
| 1 | primary парсится, `schemaVersion ≤ Current`, миграции прошли | загрузить | `Loaded` |
| 2 | primary отсутствует, `.bak` валиден | загрузить из `.bak`, **сразу** переписать primary | `RecoveredFromBackup` |
| 3 | primary битый, `.bak` валиден | primary → карантин, загрузить `.bak`, переписать primary | `RecoveredFromBackup` |
| 4 | primary битый, `.bak` битый/нет | primary и `.bak` → карантин, пустое структурное состояние, запись **разрешена** | `Quarantined` |
| 5 | ни primary, ни `.bak` нет, legacy-`user.config` содержит непустое структурное состояние | импорт legacy → v1 → цепочка до Current → запись | `ImportedFromLegacy` |
| 6 | ни того, ни другого, ни legacy | дефолтное пустое состояние (`pages: ["default"]`) | `FirstRun` |
| 7 | primary парсится, но `schemaVersion > Current` | **не мигрировать вниз, не перезаписывать**; пустое структурное состояние + запись **запрещена** на всю сессию | `NewerSchema` |

**Ступень 4 отдельно (сознательный trade-off).** Соблазн — «упасть обратно на legacy-импорт».
Отвергнуто: если JSON когда-то существовал, legacy-блобы заморожены на момент миграции (D-10), и
их воскрешение подменит актуальное состояние устаревшим (панели, удалённые месяц назад, вернутся,
созданные после — нет), причём молча. Вместо этого: карантин + громкий лог + диагностическое
событие + ручное восстановление через `--restore-state-backup` (D-11). Legacy при этом на диске
остаётся и доступен при откате на старый релиз (§7).

**Ступень 7 — это и есть половина rollback-истории (§7).** Правило «документ новее — не трогаем»
обязано присутствовать в ридере **с самого первого релиза**, иначе откат с v3 на релиз,
знающий только v2, затрёт пользователю состояние. Это дороже всего исправлять задним числом.

### D-8. Advisory file lock — **делаем в этой фазе** (закрытие R-15)

Решение по вопросу, вынесенному ADR-023 R-15.

**Делаем.** Обоснование:

1. R-15 явно передан в Фазу 4 и в `ADR-023 §Not done in this phase` («No storage-root file lock …
   Phase 4, ADR-024»). Не закрыть его здесь — значит оставить пункт висеть без владельца.
2. Стоимость мала: один класс ~60 строк + DI + вызов в `Program.Main`.
3. **Полностью тестируется в этой среде**, в отличие от нативных фаз: `FileShare.None`
   применяется per-handle, а не per-process — два `FileStream` в одном тестовом процессе дают
   точно ту же семантику конфликта, что два процесса.
4. Фаза целиком про протокол записи — лок принадлежит тому же reviewable-набору.

**Форма.** `%LOCALAPPDATA%\DesktopOrganizer\.storage-lock`, открывается
`FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None`, держится всё время жизни процесса.
Лок берётся на **корне** хранилища (а не на `State\`), потому что концептуально он покрывает и
`PanelItems\.manifest.json` — вторую разделяемую на запись поверхность, названную в R-15.

**Точка захвата:** `Program.Main`, сразу после `RunAsPrimary`, до `ApplicationStateService.Load()`
и до `new App(...)`. Расписание: 3 попытки × 150 мс (≤450 мс, в духе `SingleInstanceHandshakePolicy`).

**Поведение при провале — degraded read-only, а не выход.** Процесс уже прошёл handshake Фазы 3 и
признан Primary; убивать его из-за подвисшего лок-файла хуже, чем работать без записи. При провале:
загрузка разрешена, **все записи становятся no-op**, пишется `StateStoreLockUnavailable`, один раз
в `Console.Error`. Отдельного модального окна не делаем (ADR-023 §8: локализация — отдельная задача).

**Остаточный риск, который переносим:** `PanelItemStorageService` лок **не консультирует** в этой
фазе — его правки лежат на drag&drop-путях, которые здесь не проверить, а собственная защита
(`EnsureManifestCacheUnlocked` → untrusted → abort) у него уже есть. Записать в BACKLOG как
`DO-011` с владельцем Phase 9.

### D-9. Три версии схемы — все три настоящие, ни одна не синтетическая

DoD требует golden-тесты минимум для 3 версий. Версии придуманы **не** ради счётчика — каждая
убирает конкретный существующий дефект (§0.1):

**v1 — дословная транскрипция legacy.** Ровно те же четыре сериализации, что сегодня, положенные
в конверт бок о бок. Никаких изменений формы. Смысл: шаг legacy→JSON становится тривиально
проверяемым и тривиально обратимым; вся содержательная работа уезжает в v1→v2, где входом служит
уже JSON, а не XML-контейнер.

```json
{ "schemaVersion": 1, "writtenAtUtc": "...", "appVersion": "...",
  "panels": [ /* ChildWindowModel, PascalCase, ЧИСЛОВЫЕ enum'ы, Point/Size как {X,Y,IsEmpty} */ ],
  "pages": { "Ids": ["default"], "CurrentPageId": "default" },
  "layoutSnapshots": [ { "Id":"…", "Name":"…", "CreatedAt":"…", "PanelJsonItems": ["{\"Id\":…}"] } ],
  "systemIconsPanel": { /* PanelDto, PascalCase, СТРОКОВЫЕ enum'ы, null-поля опущены */ } }
```

**v2 — устранение двух реальных дефектов.**
1. `layoutSnapshots[].PanelJsonItems: string[]` → `layoutSnapshots[].panels: object[]` (JSON-строки
   распаковываются в настоящие объекты). Этот костыль существует только потому, что `Core` не
   может ссылаться на UI (`LayoutSnapshot.cs:24-29`); на уровне JSON проблемы нет вовсе.
2. Единый диалект по всему документу: camelCase + **строковые** enum'ы везде.
   Числовые enum'ы в `panels` — тихая мина: любая вставка члена в середину `PanelViewMode`/
   `PanelContentFilter`/`PanelSortMode` молча переинтерпретирует сохранённые панели. Проект уже
   ходил рядом с этим классом ошибок (см. XML-doc `ChildWindowModel.SortMode` про «missing enum
   property → default»).

**v3 — группировка, которую прямо требует PHASE-4 §Target** (panels / geometry / appearance /
pages/layout snapshots / panel content metadata / virtual desktop bindings):

```json
{ "id":"…", "title":"…", "pageId":"default",
  "geometry":   { "x":0,"y":0,"width":300,"height":400,"monitorDeviceId":"\\\\.\\DISPLAY1",
                  "windowState":"Normal","isRolledUp":false,"isVisible":true },
  "appearance": { "backgroundColor":"#1E1E1E","backgroundOpacity":0.8,"textColor":"#DDDDDD","iconSize":48 },
  "content":    { "viewMode":"List","contentFilter":"ShowAll","sortMode":"Manual",
                  "sourceFolderPath":null,"filePaths":[] } }
```

Virtual-desktop-привязки сегодня существуют только у системной панели
(`systemIconsPanel.anchorVirtualDesktopId` / `showOnAllVirtualDesktops`) — в v3 они остаются там,
per-panel-блок **не выдумываем** (точка расширения для Фазы 7, зафиксировать в ADR как
«зарезервировано, не создано»).

`CurrentSchemaVersion = 3`. Писатель всегда пишет 3; v1/v2 существуют исключительно как входы
миграций и вечные golden-фикстуры.

**Линия отреза, если фазу понадобится ужать:** отрезается v3 (группировка), v1+v2 остаются —
v2 несёт оба реальных фикса. Но тогда DoD «golden-тесты для ≥3 версий» закрывается только с явным
waiver'ом в ADR (legacy(v0)+v1+v2). Рекомендация — **не резать**: инфраструктура миграций дешевле
всего доказывается, пока она свежая, а v3 — единственное место, где спецификация просила форму.

### D-10. Legacy не удаляется и не дописывается («заморожен»)

Спецификация: «Legacy data не удаляется минимум один release». Уточнение решения:

- **Не удаляем** (минимум до релиза после того, где JSON стал основным). Удаление — отдельный
  пункт Фазы 9.
- **И не дописываем.** Рассмотренная альтернатива — dual-write структурного состояния в
  `user.config` весь deprecation-релиз (максимальная точность отката) — отвергнута: она
  возвращает `Settings.Default.Save()` (перезапись всего `user.config`) на горячие пути, ради
  устранения которых строился debounce Фазы 7 (ADR-012), и восстанавливает два источника правды —
  ровно тот дефект, который фаза убирает.
- Следствие для отката: старый релиз увидит состояние **на момент миграции**. Это ограничение
  документируется явно (§7), а не замалчивается.

### D-11. Единая точка загрузки + барьер записи

Сегодня структурное состояние читают в четырёх разных местах и в четырёх разных моментах старта:
`PageManagerService` — в конструкторе (то есть в момент первого DI-резолва),
`WindowManager.LoadWindowsState()` — из `App.LoadPanels()`, `SystemIconsPanel.Load()` — после,
`LayoutSnapshotService.Load()` — по требованию. При этом `MonitorService.Start()` вызывается
**до** `LoadPanels()` и способен дёрнуть `OnMonitorsChanged → SaveWindowsState()`.

Сегодня это безвредно случайно (`OnMonitorsChanged` рано выходит при `_childWindows.Count == 0`).
С единым документом «сохранить» означает «переписать весь документ», и такая запись до окончания
загрузки затрёт только что мигрированное состояние. Это главный риск фазы (R-3).

**Решение, двухчастное:**

1. `ApplicationStateService.Load()` вызывается **один раз**, в `Program.Main`, до `new App(...)` —
   то есть до того, как существует хоть один сервис, способный писать. Все четыре потребителя
   читают из уже загруженного in-memory документа, а не с диска.
2. `IApplicationStateStore.Save(...)` **отказывает** (и логирует `StateWriteBarrierViolation`) до
   первого успешного `Load()` в процессе. Барьер — не «на всякий случай», а исполняемый инвариант,
   закрытый тестом; он же страхует от будущих рефакторингов порядка старта.

Порядок в `Program.Main` после изменений:

```
handshake (ADR-023) → RunAsPrimary
  → storageLock.TryAcquire()                (D-8; провал → degraded read-only)
  → applicationState.Load()                 (recovery-лестница D-7 + миграции D-9)
  → new App(provider, coordinator, role)    (дальше StartApp читает из памяти)
```

Миграция, таким образом, выполняется до создания `Application` — там же, где уже стоит
top-level `try/catch` с `StartupFailedMessage` (`Program.cs:55-67`), то есть «миграция упала»
получает пользовательское сообщение бесплатно, без нового UI.

### D-12. Дисциплина записи сохраняется, `SaveScheduler` переезжает

`SaveScheduler` сейчас принадлежит `WindowManager`. С единым документом два писателя (панели через
scheduler, страницы/снапшоты/системная панель — синхронно напрямую) создали бы две конкурирующие
дорожки к одному файлу. Поэтому единственный экземпляр `SaveScheduler` переезжает в
`ApplicationStateService`.

- `RequestSave()` — debounce 500 мс: drag, resize, roll-up, смена фильтра/вида/сортировки,
  `OnMonitorsChanged`.
- `SaveNow()` — синхронно: создание/удаление панели, add/remove/switch страницы, save/delete
  снапшота, restore снапшота, `SystemIconsPanel.Save()`, `App.OnExit`.

Публичный контракт `IWindowManager.SaveWindowsState()/SaveWindowsStateNow()` **не меняется** —
они становятся делегатами на `ApplicationStateService`. Это сознательно ограничивает радиус
поражения: у этих методов много вызывающих, и у каждого в XML-doc уже зафиксировано, почему он
выбрал именно синхронный или отложенный вариант (`WindowManager.cs:198-208`, `App.xaml.cs:264`,
`PageManagerService.cs:96-106`, `LayoutSnapshotService.cs:110-112`).

### D-13. Import/export — **не в этой фазе**, но граница фиксируется сейчас

Спецификация: «Import/export JSON не должен совпадать 1:1 с internal store contract».
Проверено: функции импорта/экспорта в проекте **нет вообще** (ни в UI, ни в сервисах).
Значит, строить второй формат сейчас — строить формат без потребителя.

**Решение:** в этой фазе выполняется только архитектурная часть требования (MUST):
1. DTO схемы и конверт объявляются `internal` (+ уже имеющийся репо-wide `InternalsVisibleTo
   DesktopOrganizer.Tests` в `Directory.Build.props:26`), чтобы внутренний контракт физически не
   мог стать публичным форматом по недосмотру;
2. в ADR-024 §Consequences фиксируется правило: будущая фича export/import обязана иметь
   собственный DTO и собственную версию, `ApplicationStateEnvelope` наружу не отдаётся.

Сама фича export/import — NICE-TO-HAVE, передаётся в Фазу 7 (`PHASE-7-product-features`).
Записать в BACKLOG как `DO-012`.

---

## 2. Новые и изменяемые файлы

### 2.1. Новые — `DesktopOrganizer.Services.Abstractions/Storage/`

| Файл | Содержимое (форма) |
|---|---|
| `IApplicationStateStore.cs` | `StateLoadResult Load();` `StateSaveResult Save(JsonObject payload);` `bool IsWritable { get; }` |
| `StateLoadResult.cs` | `sealed record StateLoadResult(StateLoadStatus Status, JsonObject Payload, int SchemaVersionOnDisk, int MigrationsApplied, string? QuarantinePath)` |
| `StateLoadStatus.cs` | `Loaded, RecoveredFromBackup, Quarantined, ImportedFromLegacy, FirstRun, NewerSchema` |
| `StateSaveResult.cs` | `Saved, SkippedNoChange, SkippedReadOnly, SkippedBeforeLoad, Failed` + `FailureStage?` |
| `ISettingsMigration.cs` | `int FromVersion { get; }` `int ToVersion { get; }` `JsonObject Migrate(JsonObject document);` |
| `ILegacyStateImporter.cs` | `JsonObject? TryImport();` — `null`, если legacy пуст/дефолтен |
| `IStorageLock.cs` | `bool TryAcquire();` `bool IsHeld { get; }` + `IDisposable` |

### 2.2. Новые — `DesktopOrganizer.Services/Storage/`

| Файл | Ответственность |
|---|---|
| `StateStorePaths.cs` | primary/`.bak`/temp/карантин/lock; `internal`-ctor с корнем для тестов (как `PanelItemStorageService`) |
| `AtomicJsonFileWriter.cs` | D-6 целиком: temp → fsync → validate parse → `File.Replace` → retry; подметание `*.tmp-*`; ретенция карантина; `internal` тестовый шов `Func<StateWriteStage,bool>?` |
| `StateWriteStage.cs` | `AfterTempWrite, AfterFlush, AfterValidate, BeforeReplace, AfterReplace` |
| `JsonApplicationStateStore.cs` | recovery-лестница D-7, барьер записи D-11, no-op guard, committed-состояние D-6 п.5, диагностика |
| `StateEnvelope.cs` | чтение/запись зарезервированных ключей, `CurrentSchemaVersion = 3`, правило «остальное — payload» |
| `StateMigrationRunner.cs` | упорядоченное применение цепочки, детекция разрывов, fail-closed, запрет downgrade |
| `Migrations/V1ToV2Migration.cs` | распаковка `PanelJsonItems`, camelCase, числовые→строковые enum'ы |
| `Migrations/V2ToV3Migration.cs` | группировка `geometry`/`appearance`/`content` |
| `StorageFileLock.cs` | D-8, `FileShare.None`, расписание 3×150 мс, bounded dispose |

### 2.3. Новые — `DesktopOrganizer.UI/State/`

| Файл | Ответственность |
|---|---|
| `IApplicationStateService.cs` / `ApplicationStateService.cs` | фасад: `Load()`, типизированный доступ (`Panels`, `Pages`, `Snapshots`, `SystemIconsPanel`), `RequestSave()`/`SaveNow()`, владеет единственным `SaveScheduler` |
| `Dto/ApplicationStateDocumentDto.cs` | корневой payload v3 |
| `Dto/PanelStateDto.cs`, `PanelGeometryDto.cs`, `PanelAppearanceDto.cs`, `PanelContentDto.cs` | v3-форма панели |
| `Dto/PagesStateDto.cs`, `LayoutSnapshotStateDto.cs`, `SystemIconsPanelStateDto.cs` | остальные блоки |
| `PanelStateMapper.cs` | `ChildWindowModel ↔ PanelStateDto` (единственное место, где UI-типы встречаются со схемой) |
| `LegacySettingsImporter.cs` | `ILegacyStateImporter` поверх `IAppSettings`; читает 5 legacy-ключей → v1 `JsonObject` |
| `StateJson.cs` | единственный `JsonSerializerOptions` схемы: camelCase, `JsonStringEnumConverter`, `WhenWritingNull` |

### 2.4. Изменяемые

| Файл | Изменение | Риск |
|---|---|---|
| `src/DesktopOrganizer.UI/Program.cs` | `TryAcquire` лока + `ApplicationStateService.Load()` между `RunAsPrimary` и `new App(...)` (D-11) | средний — порядок старта, уже покрыт top-level `try/catch` |
| `src/DesktopOrganizer.UI/Services/WindowManager.cs` | `LoadWindowsState`/`PersistWindowsState` → через `IApplicationStateService`; `_lastSavedJson`, `_pendingCorruptStateBackup`, `BackupPendingCorruptStateIfAny`, `_saveScheduler` **удаляются** (переехали в store) | **высокий** — самый нагруженный файл фазы |
| `src/DesktopOrganizer.UI/Services/PageManagerService.cs` | чтение в ctor → из in-memory документа; `Save()` → `SaveNow()` | средний |
| `src/DesktopOrganizer.UI/Services/LayoutSnapshotService.cs` | `PanelJsonItems`-строки → типизированные DTO; `Save/Load` через сервис | средний |
| `src/DesktopOrganizer.UI/Services/SystemIconsPanelManager.cs` | `PanelDto`+`JsonOptions` → общий DTO схемы | средний |
| `src/DesktopOrganizer.UI/Services/IAppSettings.cs`, `AppSettings.cs` | удалить 5 структурных свойств, оставить 9 скалярных | низкий |
| `src/DesktopOrganizer.UI/Properties/Settings.settings` | 5 ключей **не удалять** (D-10, legacy заморожен, но читается импортёром) — пометить комментарием `legacy, read-only, см. ADR-024 D-10` | низкий |
| `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` | регистрация store/runner/миграций/лока | низкий |
| `src/DesktopOrganizer.UI/Configuration/DiConfig.cs` | `IApplicationStateService`, `ILegacyStateImporter` | низкий |
| `src/DesktopOrganizer.UI/App.xaml.cs` | `OnExit` — один `SaveNow()` вместо `SystemIconsPanel.Save()` + `SaveWindowsStateNow()` | средний — путь потери данных |
| `docs/Refactoring/Testing/diagnostics-log-events.md` | новая категория `state` (§4) | низкий |
| `tools/perf/New-PerfFixture.ps1` | генерирует фикстуру в `Settings.Default.ChildWindowsState` — переписать на JSON-документ | низкий, но **иначе перф-инструментарий Фазы 0 молча сломается** |

---

## 3. Порядок работ (логические коммиты)

Каждый коммит собирается и оставляет тесты зелёными. C1–C5 — чистое добавление, продакшен-поведение
не меняется до C6.

| # | Название | Содержимое | Проверяемо здесь |
|---|---|---|---|
| **C1** | `feat(storage): contracts` | §2.1 целиком + `StateJson.cs`. Никакой логики | сборка |
| **C2** | `feat(storage): atomic write + recovery ladder` | `StateStorePaths`, `AtomicJsonFileWriter`, `JsonApplicationStateStore` (D-6/D-7), карантин, подметание temp, барьер записи | **да, полностью** |
| **C3** | `feat(storage): migration chain v1→v2→v3` | `StateMigrationRunner`, обе миграции, golden-фикстуры | **да, полностью** |
| **C4** | `feat(storage): legacy user.config importer` | `LegacySettingsImporter` (v0→v1) + тесты на фейковом `IAppSettings` | **да** |
| **C5** | `feat(storage): advisory storage lock` | `StorageFileLock`, degraded read-only, диагностика (D-8, R-15) | **да** |
| **C6** | `refactor(state): wire UI onto the JSON store` | `ApplicationStateService`, DTO+маппер, `Program.Main`, 4 потребителя, `IAppSettings` ужимается, DI; правка существующих тестов | сборка + тесты; поведение окон — manual |
| **C7** | `feat(storage): recovery switch + docs` | `--restore-state-backup`, `docs/Refactoring/Storage/rollback.md`, `Testing/storage-manual-matrix.md`, `diagnostics-log-events.md`, `ARTIFACT_INDEX.md`, `New-PerfFixture.ps1` | да |
| **C8** | `docs(adr): ADR-024 Proposed→Accepted` | ADR-024 D-0…D-13, R-15 → closed, `Refactoring/ROADMAP.md` Phase 4 `[~]`, `BACKLOG.md` DO-011/DO-012, `REFACTORING_PLAN.md` | да |

Параллелить можно C2∥C3∥C4 (независимые файлы, общий только `StateStorePaths` из C2).
C5 независим от всех. C6 — точка схождения, начинать только после C2–C5.

---

## 4. Диагностика (новая категория `state`)

Добавляется в `docs/Refactoring/Testing/diagnostics-log-events.md`, `scope = null`.

| Событие | Payload |
|---|---|
| `StateLoaded` | `status`, `schemaVersionOnDisk`, `migrationsApplied`, `panelCount`, `pageCount`, `snapshotCount`, `durationMs` |
| `StateMigrated` | `fromVersion`, `toVersion`, `durationMs` |
| `StateLegacyImported` | `panelCount`, `pageCount`, `snapshotCount`, `hadSystemIconsPanel` |
| `StateSaved` | `schemaVersion`, `bytes`, `durationMs`, `trigger` (`debounced`\|`immediate`) |
| `StateSaveFailed` | `stage`, `attempt`, `errorType` |
| `StateRecoveredFromBackup` | `reason` |
| `StateQuarantined` | `bytes` |
| `StateStoreLockUnavailable` | `attempts` |
| `StateWriteBarrierViolation` | `caller` (токен из закрытого набора) |

> **Privacy-инвариант (`diagnostics-log-events.md` §Privacy) требует внимания именно здесь.**
> Сообщения исключений файлового I/O почти всегда содержат **полный путь**, а путь может включать
> имя пользователя. Поэтому `StateSaveFailed` пишет `ex.GetType().Name`, и **никогда** `ex.Message`
> и никогда путь. Закрепить тестом в `SingleInstanceDiagnosticsTests`-стиле — новый
> `StateDiagnosticsTests`. Это отличие от существующих `Console.Error.WriteLine($"…: {ex.Message}")`
> в сервисах: там вывод идёт в консоль, а не в файл, который тестировщик пришлёт разработчику.

Диагностика инжектится как `Func<IDiagnosticsLog>` (паттерн Фазы 3, `DiConfig.cs:81-106`,
ADR-023 R-12) — store не имеет права материализовать JSONL-писателя раньше времени.

---

## 5. Тесты

### 5.1. Новые файлы

| Файл | Что доказывает |
|---|---|
| `Unit/Storage/AtomicJsonFileWriterTests.cs` | шаги D-6; **краш на каждой из 5 стадий → primary либо старый, либо новый, всегда валидный**; `.bak` создаётся; temp не течёт; retry при `IOException`; подметание `*.tmp-*` при старте |
| `Unit/Storage/JsonApplicationStateStoreTests.cs` | все 7 ступеней D-7; барьер записи; no-op guard; **провал записи не двигает committed-состояние**; `NewerSchema` → ни одной записи за сессию |
| `Unit/Storage/StateMigrationRunnerTests.cs` | порядок; разрыв в цепочке → fail-closed; исключение миграции → документ не записан; идемпотентность (повторный прогон над Current — no-op); отказ от downgrade |
| `Unit/Storage/Migrations/V1ToV2MigrationTests.cs` | распаковка `PanelJsonItems`; битая JSON-строка внутри снапшота → снапшот пропускается, остальные выживают; числовые enum'ы → строковые по именам, а не по позициям |
| `Unit/Storage/Migrations/V2ToV3MigrationTests.cs` | группировка; отсутствующие поля → дефолты, совпадающие с дефолтами `ChildWindowModel` |
| `Unit/Storage/GoldenMigrationTests.cs` | **DoD-пункт**: `v1-full.json`→Current, `v2-full.json`→Current, `v3-full.json`→Current(identity), `legacy`→v1→Current; сравнение с зафиксированными ожидаемыми документами |
| `Unit/Storage/LegacyStateImporterTests.cs` | все 5 ключей; пустые; дефолтные (`["default"]`, `[]`, `""`) → `null` (это FirstRun, а не импорт); битый один блок → остальные импортируются |
| `Unit/Storage/StorageFileLockTests.cs` | второй хэндл в том же процессе не получает лок; освобождение при `Dispose`; degraded read-only → `Save` возвращает `SkippedReadOnly` и ничего не пишет на диск |
| `Unit/Storage/SchemaContractTests.cs` | **ключевой тест против D-1**: ключи, которые производит `V2ToV3Migration`, посимвольно совпадают с ключами, которые производит сериализация `ApplicationStateDocumentDto`; ни одна миграция не создаёт зарезервированный ключ конверта |
| `Unit/Storage/StateDiagnosticsTests.cs` | privacy: ни в одном `state`-событии нет путей/`ex.Message`/заголовков панелей |
| `Unit/State/PanelStateMapperTests.cs` | round-trip `ChildWindowModel`→DTO→`ChildWindowModel`: **каждое** персистируемое свойство выживает (`FileItems` — `[JsonIgnore]`, не должен) |
| `Unit/State/ApplicationStateServiceTests.cs` | единая точка загрузки; `RequestSave` debounce vs `SaveNow`; запись до `Load()` отклонена |

Golden-фикстуры: `tests/DesktopOrganizer.Tests/Resources/Storage/golden/`
(`v1-full.json`, `v2-full.json`, `v3-full.json`, `expected-current-from-v1.json`,
`expected-current-from-v2.json`, `legacy-settings.json` — снимок 5 legacy-строк).
**Фикстуры неизменяемы после мержа.** Тест, который «починили» правкой golden-файла, перестаёт
быть golden-тестом — зафиксировать это комментарием в шапке каждого файла.

### 5.2. Изменяемые тесты

- `Unit/Services/WindowManagerTests.cs:428-537` — 4 теста, работающих через
  `Settings.Default.ChildWindowsState` (включая `try/finally`-восстановление глобального
  состояния), переводятся на фейковый `IApplicationStateService`. **Побочная выгода:** уходит
  зависимость от процесс-глобального синглтона `Settings.Default`, из-за которой эти тесты
  сегодня нельзя гонять параллельно.
- `Unit/Services/PageManagerServiceTests.cs`, `LayoutSnapshotServiceTests.cs`,
  `SystemIconsPanelManagerTests.cs` — моки `IAppSettings` на структурные ключи → фейковый сервис.
- `Integration/Services/DiConfigTests.cs` — новые регистрации резолвятся; `internal`-оверлоад с
  подменой корня хранилища (по образцу `diagnosticsLogDirectory`), чтобы тесты DI **не трогали
  реальный `%LOCALAPPDATA%\DesktopOrganizer\State`**.

### 5.3. Конвенции

Реальная временная директория + `IDisposable`-фикстура с уборкой (как
`PanelItemStorageServiceTests`/`JsonLinesDiagnosticsLogTests`). Никаких моков файловой системы:
`File.Replace`, `FileShare.None` и fsync — ровно то поведение ОС, которое и надо проверить.
`[Trait]` — существующая таксономия (`Helpers/TestCategory.cs`, `TestGroup.cs`); учитывая DO-008,
новые тесты используют одно согласованное значение и не плодят шестой вариант написания.

---

## 6. Риски

| ID | Риск | Митигация | Проверяемо здесь |
|---|---|---|---|
| **R-1** | Краш/отключение питания посреди записи → усечённый primary | D-6: temp+fsync+validate+`File.Replace`+`.bak`; committed-состояние двигается последним | **да** (инъекция отказа на 5 стадиях) |
| **R-2** | Миграция теряет данные (панели/items/layout) | v1 — дословная транскрипция; golden-тесты; round-trip маппера; `migrationsApplied` в диагностике | **да** |
| **R-3** | **Гонка debounced save × миграция на старте** — отложенная запись, поставленная до `Load()`, затирает мигрированное состояние | D-11: единая точка загрузки в `Program.Main` + исполняемый барьер записи | **да** (тест барьера) |
| **R-4** | Откат на старый релиз после создания JSON | D-10 (legacy заморожен, не удалён) + D-7 ступень 7 (`NewerSchema`, новее — не трогаем) + §7 | частично: правило `NewerSchema` — да; реальный откат инсталлятором — manual |
| **R-5** | Store пишет раньше, чем потребители загрузились → пустой документ поверх полного (класс ошибки R-1 из ADR-023) | барьер записи + `App.OnExit` по-прежнему защищён `_role != Primary` | **да** |
| **R-6** | AV/индексатор держит хэндл → `File.Replace` падает | 3 retry с backoff; провал не двигает committed → следующий debounce попробует снова | **да** (инъекция `IOException`) |
| **R-7** | Два процесса пишут одновременно (FUS, R-15) | D-8 advisory lock + degraded read-only | **да** (`FileShare.None` в одном процессе даёт ту же семантику) |
| **R-8** | Ложный отказ лока (подвисший хэндл) → приложение молча перестаёт сохранять | лок на выделенном `.storage-lock`, которого не касается ничто другое; retry; громкое диагностическое событие | **да** |
| **R-9** | Дрейф имён полей: DTO UI vs литералы миграций (цена D-1) | `SchemaContractTests` | **да** |
| **R-10** | Числовые enum'ы в v1: вставка члена в середину `PanelViewMode`/`ContentFilter`/`SortMode` между релизами сдвигает смысл сохранённых панелей | v1→v2 переводит в строковые **по имени**; golden-фикстура v1 фиксирует текущее соответствие чисел именам навсегда | **да** |
| **R-11** | `LayoutSnapshot.PanelJsonItems` — панели внутри снапшотов мигрируются отдельным путём и могут разойтись с основными | v2 распаковывает их в те же объекты; v3 применяет **ту же** группировку к обоим местам; тест «панель в снапшоте и панель в `panels` имеют идентичную форму» | **да** |
| **R-12** | `ex.Message` файлового I/O с путём попадает в JSONL-лог, который тестировщик отправит разработчику | только `ex.GetType().Name`; `StateDiagnosticsTests` | **да** |
| **R-13** | `New-PerfFixture.ps1` (Фаза 0) пишет в `ChildWindowsState` → после C6 молча генерирует фикстуру в никуда | переписать в C7; иначе перф-базлайн Фазы 9 будет измерять пустое состояние | **да** |
| **R-14** | Рост радиуса: `WindowManager` — файл с наибольшей плотностью инвариантов в проекте | публичный контракт `IWindowManager` не меняется (D-12); удаляются только приватные поля, чья ответственность переехала | сборка+тесты |

---

## 7. Rollback (DoD: «rollback на предыдущий app release задокументирован»)

Отдельный артефакт `docs/Refactoring/Storage/rollback.md` (C7). Содержание:

**Почему откат вообще возможен.** `installer/DesktopOrganizer.iss:88-91` удаляет только `{app}`;
`%LOCALAPPDATA%\DesktopOrganizer\` (State, PanelItems, Logs) и `user.config` переживают и апгрейд,
и деинсталляцию. `AssemblyVersion` прибит (`Directory.Build.props:15`), поэтому путь к
`user.config` не меняется между релизами.

**Сценарий A — откат на релиз до JSON (0.3.x).** Поставить старый инсталлятор поверх. Старый код
читает `user.config`, который с момента миграции заморожен (D-10) и не тронут. Пользователь
получает layout **на момент миграции**; изменения, сделанные после, остаются в
`State\application-state.json` и вернутся при повторной установке нового релиза. Потери данных нет,
есть откат по времени. Ограничение объявляется в release notes — это прямое следствие отказа от
dual-write (D-10) и должно быть названо, а не обнаружено пользователем.

**Сценарий B — откат на релиз, знающий JSON, но более старую схему.** Работает без действий:
ридер видит `schemaVersion > Current`, переходит в `NewerSchema` (D-7 ступень 7), не мигрирует
вниз и **не пишет**. Файл нетронут, повторная установка нового релиза восстанавливает всё.
Цена — этот сеанс старого релиза стартует с пустым структурным состоянием; это сознательно
выбрано вместо «попытаться прочитать как получится».

**Сценарий C — ручное восстановление.** `--restore-state-backup` (C7, из
`Storage/migration-plan.md` п.6): берёт `.bak` либо самый свежий `application-state.corrupt-*.json`,
валидирует, прогоняет цепочку миграций, атомарно ставит primary, текущий primary уводит в карантин.
Никаких деструктивных действий без валидации нового кандидата.

---

## 8. DoD: что закрывается здесь, что — manual verify

### 8.1. Достижимо в этой среде (`dotnet build` / `dotnet test`)

| DoD | Как закрывается |
|---|---|
| Симулированный краш во время записи оставляет старый **или** новый валидный state | `AtomicJsonFileWriterTests`, инъекция отказа на 5 стадиях (R-1) |
| Corrupt primary → recovery from backup | `JsonApplicationStateStoreTests`, ступени 2/3/4 лестницы D-7 |
| Golden migration tests ≥ 3 версий схемы | `GoldenMigrationTests`: legacy→v1, v1→Current, v2→Current, v3→Current |
| Миграция без потери panels/items/layout (*логически*) | golden + `PanelStateMapperTests` round-trip + инвариант счётчиков (`panelCount`/`pageCount`/`snapshotCount` до и после) |
| Гонка debounced save × migration | тест барьера записи (R-3) |
| Файловый лок / R-15 | `StorageFileLockTests` (`FileShare.None` per-handle) |
| Privacy диагностики | `StateDiagnosticsTests` |
| Rollback-правило «новее — не трогаем» | тест `NewerSchema` |
| Нет новых warnings, зелёная сборка/тесты | `dotnet build` + `dotnet test` |

### 8.2. Manual verify (Win10/Win11) — `docs/Refactoring/Testing/storage-manual-matrix.md` (C7)

| ID | Проверка |
|---|---|
| S-1 | **Реальный апгрейд реальной установки**: 0.3.0 с ≥5 панелями, ≥2 страницами, ≥1 снапшотом и включённой системной панелью → установка нового релиза → все панели/items/геометрия/страницы/снапшоты на месте; `State\application-state.json` создан; `user.config` не изменён |
| S-2 | Апгрейд на установке, где панелей нет вообще → `FirstRun`, пустой валидный документ |
| S-3 | Апгрейд на установке с битым `ChildWindowsState` → карантин, приложение стартует |
| S-4 | Второй запуск после миграции → `Loaded`, повторная миграция не выполняется (`migrationsApplied == 0`) |
| S-5 | Откат по сценарию A (§7): установка 0.3.0 поверх → старый layout на месте |
| S-6 | Откат по сценарию B: старый JSON-релиз поверх нового → стартует, файл не изменён |
| S-7 | **FUS**: два сеанса одного пользователя, второй — `StateStoreLockUnavailable`, read-only; primary-файл не повреждён (закрывает R-15 на живой системе) |
| S-8 | Жёсткое выключение питания/`Stop-Process -Force` во время активного drag панели → после старта state валиден (старый или новый) |
| S-9 | `--restore-state-backup` на подложенном битом primary |
| S-10 | Запись при включённом Controlled Folder Access / стороннем AV → degraded-путь, приложение не падает |
| S-11 | Перф: startup-время не хуже базлайна (`tools/perf/Measure-Startup.ps1`) при фикстуре 50 панелей |

---

## 9. Что сознательно **не** делается в этой фазе

- Import/export как фича (D-13) → Фаза 7, BACKLOG `DO-012`.
- Удаление legacy-ключей из `Settings.settings` (D-10) → Фаза 9.
- Консультация лока в `PanelItemStorageService` (D-8) → Фаза 9, BACKLOG `DO-011`.
- Фоновая/асинхронная запись state (D-2).
- Per-panel virtual-desktop-привязки (D-9, зарезервировано) → Фаза 7.
- Шифрование/подпись state-файла: угрозы нет (per-user `%LOCALAPPDATA%`, `PrivilegesRequired=lowest`).
- Изменение `ShutdownMode`, автозапуска, single-instance-протокола (Фаза 3 закрыта).

---

## 10. Открытые вопросы к основному агенту

Ни один не блокирует старт C1–C5; решения нужны к C6/C8.

1. **v3 оставляем или режем до v2?** План рекомендует оставить (D-9). Если резать — нужен явный
   waiver в ADR-024 по DoD-пункту «≥3 версий».
2. **Ветка.** Текущая `refactor/versioned-json-store` создана от `rc/0.4.0` — план исходит из того,
   что вся фаза идёт в неё, PR в `rc/0.4.0` одним набором C1–C8 (симметрично Фазе 3).
3. **`DesktopOrganizer_Refactoring_Package.zip` в корне рабочего дерева** (untracked, `git status`) —
   не входит в объём фазы; если это артефакт доставки пакета, его место — вне репозитория либо
   в `.gitignore`. Отмечено, потому что попадание его в коммит C1 испортит diff.
