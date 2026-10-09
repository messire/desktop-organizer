# План: аддитивная ревизия схемы (schema v7 + `schemaRevision`)

- **Статус:** план, код не тронут. Ветка-источник: `bugfix/adr-audit-fixes` @ `a8e376e` (0 warnings, 6221 тест).
- **Разрешение пользователя (2026-10-10):** «Аддитивная ревизия схемы» — явное разрешение на замороженный
  контур ADR-024 в объёме §9 «Границы разрешения». Всё, что вне §9, остаётся замороженным.
- **Scope:** **L** (Services.Storage + UI.State + UI.Models + тесты + документация; нативного кода нет).
- **Цель:** любая будущая фича с новым persisted-полем **не ломает** старую сборку той же major-версии и
  **не требует** миграции.

---

## 0. Что сломано сегодня (сверено с кодом)

| # | Где | Поведение сегодня | Следствие для «старой сборки» |
|---|---|---|---|
| F1 | `JsonApplicationStateStore.LoadCore` (rung 7) | `schemaVersion > Current` → `NewerSchema`, пустой payload, read-only | любое новое поле = bump major = read-only откат (инцидент 2026-09-24) |
| F2 | `ApplicationStateService.PersistNow` | документ собирается заново из живых моделей (`PanelStateMapper.ToDto`, `PagesStateMapper.ToDto`, `ToSnapshotDto`, `SystemIconsPanelStateMapper.ToDto`) | все неизвестные ключи теряются при первом save |
| F3 | `StateJson.Options` = `JsonStringEnumConverter(allowIntegerValues:false)` | типизированные enum-свойства DTO (`WindowState`, `SurfaceStyle`, `ViewMode`, `ContentFilter`, `SortMode`, `SystemIconKind`) на незнакомой строке бросают `JsonException` | `ApplicationStateService.Load` → `QuarantineLoadedDocument` + **пустое writable** состояние → следующий save затирает primary |
| F4 | `V4ToV5Migration.Migrate` стр. 80–98 (allow-list 4 top-level ключей) и `MigrateLayoutSnapshots` стр. 133–150 (allow-list `id/name/createdAt/panels`) | неизвестные ключи top-level и снапшота отбрасываются | анти-паттерн для будущих миграций (V5ToV6 уже копирует «всё, кроме») |
| F5 | `StateMigrationRunner.MigrateToCurrent` стр. 145–176 | `StripEnvelope` + обратно цепляются только `schemaVersion/writtenAtUtc/appVersion` | новый envelope-ключ (`schemaRevision`) потеряется в `StateRestoreCommand` |

Прецедент толерантного enum уже есть: `PanelGeometryDto.GeometryMode`/`SizingMode`,
`PanelAppearanceDto.IconLayoutPolicy`, `SystemIconsPanelStateDto.IconSize` — **plain `string` в DTO** + парсер
с фолбэком в маппере (`PanelGeometryModeParser` и др., ADR-028/029). План распространяет этот подход на все enum.

---

## 1. Решение по версионированию (ADR-style)

### 1.1 Решение
- `schemaVersion` остаётся **целым major**: `7`. Major меняется только для несовместимых изменений
  (rename/remove/reshape/смена типа/смена смысла/«must-understand» поле) — с миграцией и rung 7, как сейчас.
- Новый **envelope-ключ** `schemaRevision` (int ≥ 0) — аддитивный minor. `StateEnvelope.CurrentSchemaRevision = 0`.
  Отсутствие ключа ≡ `0`. Некорректное значение (не int / < 0) ≡ `0` + diag, **не** карантин.
- Правила чтения (`JsonApplicationStateStore`, все ветки, где читается документ — rungs 1/2/3):

| На диске | Сборка | Результат |
|---|---|---|
| major < 7 | 7.rN | миграции → v7, premigration-бэкап `…premigration-v{major}.json`, writable (как сейчас) |
| major = 7, revision ≤ rN | 7.rN | `Loaded`, writable |
| major = 7, revision > rN | 7.rN | **`Loaded`, writable**, неизвестные поля сохраняются (§2), diag `StateNewerRevisionOpened {revisionOnDisk, buildRevision}` |
| major > 7 | 7.rN | rung 7 без изменений: `NewerSchema`, read-only, файл не трогается |

- Правило записи: `schemaRevision = max(revisionOnDisk, CurrentSchemaRevision)` — **high-water mark** («файл может
  содержать поля вплоть до rM»), монотонен, не понижается старой сборкой. Последнего писателя по-прежнему
  показывает `appVersion` (информационно, D-5: решения по `appVersion` не принимаются).
- `schemaRevision` — **reserved envelope key** (4-й): `StateEnvelope.ReservedKeys`, `IsReservedKey`, `Wrap(...,
  schemaRevision)`, `TryGetSchemaRevision`. Набор reserved-ключей **заморожен в пределах major** — новый envelope-ключ
  только через bump major (иначе старая сборка того же major сочла бы его payload-ключом).

### 1.2 Почему major-bump до 7 всё-таки нужен (а не «остаться на 6 и добавить revision»)
Рассмотрено: оставить `schemaVersion = 6`, добавить `schemaRevision` как опциональный ключ — 0.8.0 открыла бы такой
файл writable без read-only. **Отвергнуто:** 0.8.0 не умеет ни round-trip неизвестных полей (F2), ни толерантный enum
(F3). Открыв writable файл будущей ревизии, она (а) молча срежет все новые поля при первом save, (б) на новом
значении enum уйдёт в карантин и **перезапишет primary пустым** документом. Major 7 — «черта»: **каждая сборка,
способная писать v7, гарантированно revision-safe**; все revision-unsafe писатели (≤0.8.0) отсекаются rung 7
(громко, без потери данных). Цена — однократный read-only при откате на ≤0.8.0 (§4.3).

### 1.3 Альтернатива «семантическая версия строкой» (`"schemaVersion": "7.0"`) — **отвергнута**
- `StateEnvelope.TryGetSchemaVersion` во всех выпущенных сборках делает `GetValue<int>()`; строка ⇒ `false` ⇒
  документ **corrupt** ⇒ rung 3/4 (карантин, пустое writable состояние). Откат на 0.8.0 перестал бы быть
  read-only и превратился бы в перезапись — ровно класс инцидента 2026-09-24.
- `AtomicJsonFileWriter` stage 3 валидирует `HasValidSchemaVersion` (int) — пришлось бы менять протокол записи D-6.
- Нужен парсер/компаратор строк, а patch-уровень не несёт смысла для схемы. Два целых ключа дают ту же семантику
  без поломки существующего контракта чтения.

---

## 2. Round-trip неизвестных полей

### 2.1 Все DTO (10 типов, `src/DesktopOrganizer.UI/State/Dto/`) — получают
`[JsonExtensionData] public Dictionary<string, JsonElement>? Extras { get; set; }`

| DTO | Носитель extras между load и save |
|---|---|
| `ApplicationStateDocumentDto` (корень payload) | ledger: `Root` |
| `PanelStateDto` (top-level `panels[]` **и** `layoutSnapshots[].panels[]`) | `ChildWindowModel.PersistedExtras.Panel` |
| `PanelGeometryDto` | `…PersistedExtras.Geometry` |
| `PanelAppearanceDto` | `…PersistedExtras.Appearance` |
| `PanelContentDto` | `…PersistedExtras.Content` |
| `PagesStateDto` (секция) | ledger: `PagesSection` |
| `PageStateDto` (`pages.items[]`) | ledger: `PagesById[id]` |
| `LayoutSnapshotStateDto` | ledger: `SnapshotsById[id]` |
| `SystemIconsPanelStateDto` (public) | ledger: `SystemIconsPanel` |
| `SystemIconsPanelItemStateDto` (public) | ledger: элементы с неизвестным `Kind` целиком (DTO-объект) |

### 2.2 Минимальные носители
- **Панели — свойство на модели** `ChildWindowModel.PersistedExtras` типа `PanelPersistedExtras?`
  (новый `sealed` класс в `UI/Models/`, иммутабельный: `IReadOnlyDictionary<string, JsonElement>? Panel/Geometry/
  Appearance/Content` + `IReadOnlyDictionary<string, PreservedEnumValue>? Enums`; `sealed record
  PreservedEnumValue(string Raw, string Fallback)`). Без INPC, `[Browsable(false)]`, **UI его не читает**,
  без `[JsonIgnore]` (обязан сериализоваться).
  Почему на модели, а не side-table по `Id`: снапшоты хранят панели как `LayoutSnapshot.PanelJsonItems`
  (сериализованный `ChildWindowModel`, `LayoutSnapshotService.Capture`/`Restore`, `ApplicationStateService.
  ToLayoutSnapshot/ToSnapshotDto`), а `Restore` выдаёт **новые Guid** — side-table по Id терял бы extras.
  Свойство на модели переживает эти JSON round-trip'ы автоматически, **без диффа в `LayoutSnapshotService`**.
- **`PanelCloneBuilder.Build` — extras сбрасываются** (`dto.Extras = null` на всех 4 уровнях, `Enums` = null):
  клон — новая сущность, а неизвестное поле может быть привязано к идентичности; «поле отсутствует ⇒ дефолт» —
  всегда валидное состояние по правилу аддитивности (§5), копирование — нет. Snapshot restore extras сохраняет
  (это «та же» панель, а не новая).
- **Остальное — `UnknownFieldLedger`** (новый `internal sealed` класс в `UI/State/`), принадлежит
  `ApplicationStateService`: заполняется в `Load()`, читается в `PersistNow()`. Ключи — стабильные id
  (page id никогда не переименовывается — ADR-043; snapshot id — Guid при `Capture`). Удалённая страница/снапшот ⇒
  её extras просто не находятся (корректно). **Не трогаем** `PageDescriptor` (record — extras сломали бы
  value-equality) и `PageManagerService`/`LayoutSnapshotService`/`SystemIconsPanelManager`.

### 2.3 Все точки сборки/разборки документа (обязательный чек-лист реализации)
1. `ApplicationStateService.Load` → `DeserializeDocument` → ledger + `PanelStateMapper.FromDto` (live) +
   `ToLayoutSnapshot` (→ `FromDto` → `PanelJsonItems`) + `PagesStateMapper.FromDto` + DTO `SystemIconsPanel`.
2. `ApplicationStateService.PersistNow` → `PanelStateMapper.ToDto` (live), `PagesStateMapper.ToDto` (+ledger),
   `ToSnapshotDto` (+ledger, панели через `ToDto`), `SystemIconsPanelStateMapper.ToDto` (+ledger, +неизвестные items),
   `ApplicationStateDocumentDto.Extras = ledger.Root`.
3. `PanelStateMapper.ToDto/FromDto` — 4 уровня extras + preserved enums.
4. `PanelCloneBuilder.Build` — сброс (см. выше).
5. `SystemIconsPanelStateMapper.ApplyTo/ToDto` — `Kind` строкой; неизвестный `Kind` → в ledger, на save
   дописывается в конец `items` (порядок задаёт `order`, позиция в массиве не важна).
6. Store: `StateEnvelope.Wrap` (из `Save` и `PersistAfterLoad`), `StateMigrationRunner` (протянуть
   `schemaRevision` как `writtenAtUtc/appVersion`, F5), `StateRestoreCommand` (дифф не нужен — исправляется
   через runner), `WritePreMigrationBackupIfMissing` (verbatim, без изменений).

### 2.4 Сериализация — факты, которые пакет обязан закрепить тестами
- Ключи extension data пишутся **дословно** (ни `PropertyNamingPolicy`, ни `DictionaryKeyPolicy` к ним не
  применяются); сопоставление при чтении case-sensitive ⇒ `"Title"` (Pascal) попадёт в extras и будет записан
  рядом с `"title"` — допустимо (будущие сборки пишут camelCase), фиксируется тестом.
- `WhenWritingNull` касается свойств, не элементов extension data: `"x": null` сохраняется как есть; сам
  `Extras == null` не пишется.
- Дубликатов ключей быть не может: в extras попадают только ключи, не сопоставленные свойству DTO; raw
  неизвестного enum пишется **через само свойство**, не через extras. Тест: результат `PersistNow` парсится
  `JsonNode.Parse` без исключения о дубликатах.
- `JsonElement` в extension data STJ клонирует (`ParseValue`) — время жизни не привязано к исходному документу.
- Порядок ключей меняется (extras после известных) ⇒ первый `PersistNow` после загрузки может пройти no-op guard
  (`_lastCommittedPayloadJson`) и записать файл один раз. Безвредно, фиксируется в ADR как известное.

---

## 3. Толерантный enum

- **DTO:** `PanelGeometryDto.WindowState`, `PanelAppearanceDto.SurfaceStyle`, `PanelContentDto.ViewMode/
  ContentFilter/SortMode`, `SystemIconsPanelItemStateDto.Kind` → `string` с дефолтом-строкой
  (`"Normal"`, `"Ambient"`, `"List"`, `"ShowAll"`, `"Manual"`). **Формат на диске не меняется:** конвертер без
  naming policy и сейчас пишет имя члена как есть (`model.X.ToString()` даёт ту же строку).
- **Парсер:** новый `TolerantEnumParser.Parse<TEnum>(string? raw, TEnum fallback, out bool recognized)` —
  guards как у `PanelGeometryModeParser` (нет `,`, `Enum.TryParse(ignoreCase:true)`, `Enum.IsDefined`) **плюс
  отказ на числовой строке** (`"2"`, `"-1"`), чтобы сохранить смысл `allowIntegerValues:false`. Три существующих
  парсера (`GeometryMode/SizingMode/IconLayoutPolicy`) и `SystemIconsPanel.IconSize` (`Enum.TryParse`
  case-sensitive) **не меняют набор принимаемых значений** (`PersistedGeometryInvarianceTests` и их тесты — без
  изменений); «распознано» для них = «парсер не ушёл в фолбэк».
- **Незнакомое значение** → документированный дефолт члена (= инициализатор DTO/модели) + запись
  `PreservedEnumValue(raw, fallback.ToString())` в `PersistedExtras.Enums["geometry.windowState"]` (и т. п.;
  ключ — фиксированный путь поля; для `systemIconsPanel.iconSize` — в ledger) + **не карантин**.
- **Не перезаписывать дефолтом при следующем save — делаем** (дёшево: 1 helper + 1 строка на поле). В `ToDto`:
  если текущее значение модели == `Fallback` ⇒ писать `Raw`; иначе пользователь поменял значение ⇒ писать
  текущее. Ограничение (документируется): явный выбор пользователем того же значения, что и фолбэк, неотличим
  от «не трогал» ⇒ raw сохраняется (визуально в старой сборке ничего не меняется). Без этого: значение `Mica`
  из будущей ревизии, открытое старой сборкой, превращалось бы в `Ambient` при любом перемещении панели.
- **JSON-число** в enum-поле по-прежнему `JsonException` (тип не совпал со `string`) → карантин, как сегодня:
  будущие сборки никогда не пишут числа, это порча, а не forward-compat.
- `StateJson.Options` **не меняется** (`allowIntegerValues:false` остаётся как страховка для любого будущего
  типизированного enum). Гейт: reflection-тест «ни одно свойство DTO в `UI.State.Dto` не имеет enum-типа».
- **Diag:** `ApplicationStateService.Load` после маппинга агрегирует и пишет **одно** событие
  `StateForwardCompatFieldsPreserved {unknownKeyCount, unknownEnumCount, unknownSystemIconItemCount}` (только если
  хоть один счётчик > 0) — только счётчики (ни имён неизвестных ключей, ни raw-значений: в ручной правке это
  могут быть пользовательские данные; дисциплина `state`-событий ADR-024).

---

## 4. Миграция v6→v7

### 4.1 `V6ToV7Migration` (`Services/Storage/Migrations/`)
- `FromVersion 6 → ToVersion 7`; **payload — тождественный** (deep-clone всех ключей на всех уровнях).
- **Отклонение от брифа (обосновано):** `schemaRevision: 0` миграция **не добавляет** — по контракту
  `ISettingsMigration` миграция видит только payload и не может создавать reserved-ключи
  (`SchemaContractTests.*_NeverProducesAReservedEnvelopeKey`). Ревизию пишет envelope: `PersistAfterLoad`
  (forcePersist после миграции) → `StateEnvelope.Wrap(..., schemaRevision: max(onDisk=0, Current=0))`. Итоговый
  файл после апгрейда: `{"schemaVersion":7,"schemaRevision":0,...}` — как требовалось.
- Идемпотентна по построению (повторное применение = та же копия). Регистрация в `DiConfig` (стр. ~180).
- Новый helper `MigrationJson.CloneAllExcept(JsonObject source, params string[] reshapedKeys)` — используется в
  V6ToV7 и **обязателен для всех будущих миграций** (решение F4: allow-list запрещён, «копировать всё, кроме
  явно перестраиваемого»). `V4ToV5Migration` **не трогаем** (историческая, golden-фикстуры иммутабельны; на
  v4-файлах неизвестных ключей не существовало) — фиксируется в ADR как известный анти-пример.

### 4.2 Бэкап
`WritePreMigrationBackupIfMissing(fromVersion: 6)` ⇒ `application-state.premigration-v6.json` (имя по исходной
версии — существующий механизм, ADR-043 Amendment). Кода не требует, только тест.

### 4.3 Известное последствие — ЕДИНСТВЕННЫЙ раз
Откат на сборку ≤0.8.0 после первого запуска v7-сборки ⇒ rung 7 (`NewerSchema`): read-only, панели не
показываются, файл не перезаписывается, `ReadOnlySessionNotice` предупреждает. Восстановление — вручную из
`application-state.premigration-v6.json` (payload v6 и v7 **совпадают**, т.к. миграция тождественная).
Это последний такой откат: все последующие аддитивные изменения идут через `schemaRevision` без bump major.
Формулировка — в release notes и в ADR-024 Amendment.

---

## 5. Правило аддитивности (критерий «revision или major»)

Изменение допустимо как **revision** (bump `CurrentSchemaRevision`, без миграции) только если выполнено всё:
1. Только **новый опциональный ключ** объекта (или новое значение существующего enum-поля); существующие ключи не
   переименовываются, не удаляются, не меняют тип/форму/смысл.
2. **Отсутствие ключа ⇒ документированный дефолт**, воспроизводящий прежнее поведение байт-в-байт.
3. Поле **не «must-understand»**: игнорирующая его сборка не делает ничего неверного с известными данными (пример
   major: новый «вид панели», который старая сборка отрисовала бы как обычную; перенос данных из известного поля
   в новое).
4. Поле **терпит устаревание** относительно известных соседей (старая сборка правит известные поля и сохраняет
   новое как есть) — новая сборка при чтении валидирует/лечит рассогласование.
5. Не новый envelope-ключ, не новый вид элементов в существующих массивах (кроме `systemIconsPanel.items` с
   неизвестным `kind`, обработанным явно).
6. Новый enum-член: фолбэк старой сборки приемлем для отображения.

Иначе — **major** (миграция + rung 7 + однократный read-only откат) — и тоже только с явного «да».

---

## 6. Тесты и гейты (пакет P3)

Механические правки существующих тестов/фикстур, которые ломает сам пакет (смена `CurrentSchemaVersion`,
enum → `string` в `SchemaContractTests.BuildFullyPopulatedPanel`/`SystemIconsPanelStateMapperTests` и т. п.,
регенерация `expected-current-from-v1..v5.json`), делает **реализующий пакет P1/P2** — сборка и suite после
каждого пакета зелёные. P3 добавляет новые тесты ниже.

| Тест | Файл | Проверяет |
|---|---|---|
| `MigratedV1Document_KeyShape_…` (обновить: цепочка + `V6ToV7`, `applied == 6`) | `SchemaContractTests` | контракт ключей миграции ↔ DTO |
| `V6ToV7Migration_NeverProducesAReservedEnvelopeKey` | `SchemaContractTests` | reserved-ключи (теперь 4, включая `schemaRevision`) |
| `AllDtos_HaveExactlyOneJsonExtensionDataProperty`, `NoDtoProperty_IsEnumTyped` (reflection по `UI.State.Dto`) | `SchemaContractTests` | гейт на будущие DTO |
| `RevisionFixtures_ExistForEveryRevisionUpToCurrent` (`v7-r{0..Current}-full.json`) | `SchemaContractTests` | bump ревизии без golden-фикстуры не пройдёт |
| `Migrations_FromV6Onward_PreserveUnknownKeysAtEveryLevel` (Theory) | `Storage/Migrations/` | F4 не повторяется |
| `V6ToV7Migration_IsIdentity_IsIdempotent`, `…_PreservesUnknownKeys` | `V6ToV7MigrationTests` (новый) | идемпотентность |
| golden: новые `expected-current-from-v6.json`, `v7-r0-full.json`, `v7-unknown-fields.json` (неизвестные ключи на всех 10 уровнях, неизвестные значения во всех enum, неизвестный `kind`, `schemaRevision: 99`) | `GoldenMigrationTests` + `Resources/Storage/golden/README.md` | input-фикстуры v1–v6 **не трогать** (README) |
| `Load_SameMajorHigherRevision_LoadedWritable_SaveKeepsHighWaterRevision` | `JsonApplicationStateStoreTests` | «revision выше, major равен → writable» |
| `Load_PrimaryNewerSchema_…` (существующий, `Current + 1` = 8) | `JsonApplicationStateStoreTests` | «major выше → read-only», без изменения логики |
| `Load_MalformedRevision_TreatedAsZero_NotQuarantined` | `JsonApplicationStateStoreTests` | толерантность envelope |
| `Load_V6Primary_WritesPremigrationV6Backup_AndV7WithRevision0` | `JsonApplicationStateStoreTests` | §4.1/4.2 |
| `MigrateToCurrent_CarriesSchemaRevisionVerbatim` + `Restore_KeepsSchemaRevision` | `StateMigrationRunnerTests`, `StateRestoreCommandTests` | F5 |
| `UnknownFields_LoadThenSave_AllPreserved` (через `ApplicationStateService` с фейковым store, все 10 уровней + снапшот-панели) | `ApplicationStateServiceTests` | главный round-trip |
| `UnknownEnum_FallsBackToDefault_NotQuarantined_RawWrittenBack`; `…_UserChangedValue_WritesNewValue`; `NumericEnum_StillQuarantines`; `NumericString_TreatedAsUnknown` | `ApplicationStateServiceTests`, `PanelStateMapperTests` | §3 |
| `UnknownSystemIconKind_Preserved` | `SystemIconsPanelStateMapperTests` | §2.3 п.5 |
| `PanelJsonItems_RoundTrip_PreservesPersistedExtras` (оба набора опций: `ApplicationStateService` и `LayoutSnapshotService`) | `PanelStateMapperTests` / тесты LayoutSnapshot | снапшоты |
| `PanelCloneBuilder_DropsPersistedExtras` | существующие тесты ADR-045 | §2.2 |
| `Extras_KeysWrittenVerbatim_NullsPreserved_NoDuplicateKeys` | `ApplicationStateServiceTests` | §2.4 |
| `PersistedGeometryInvarianceTests` | — | **без изменений, зелёные** |

Гейт всех пакетов: `dotnet build` и `dotnet test` по solution — **0 errors / 0 warnings** (без `#pragma`/
`SuppressMessage`/`NoWarn`).

---

## 7. Таблица файлов

| Файл | Пакет | Изменение |
|---|---|---|
| `src/DesktopOrganizer.Services/Storage/StateEnvelope.cs` | P1 | `CurrentSchemaVersion = 7`, `CurrentSchemaRevision = 0`, reserved `schemaRevision`, `Wrap(+revision)`, `TryGetSchemaRevision` (толерантный) |
| `src/DesktopOrganizer.Services/Storage/JsonApplicationStateStore.cs` | P1 | чтение revision на rungs 1–3, `_revisionHighWater`, запись `max(...)` в `Save`/`PersistAfterLoad`, diag `StateNewerRevisionOpened`, `StateLoaded` + `schemaRevisionOnDisk` |
| `src/DesktopOrganizer.Services/Storage/StateMigrationRunner.cs` | P1 | протянуть `schemaRevision` verbatim (F5) |
| `src/DesktopOrganizer.Services/Storage/Migrations/V6ToV7Migration.cs` (новый) | P1 | тождественная миграция |
| `src/DesktopOrganizer.Services/Storage/Migrations/MigrationJson.cs` (новый) | P1 | `CloneAllExcept` |
| `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` | P1 | регистрация `V6ToV7Migration` |
| `src/DesktopOrganizer.Services.Abstractions/Storage/IApplicationStateStore.cs` | P1 | только XML-doc (envelope из 4 ключей, revision-правила); сигнатуры не меняются |
| `tests/.../Resources/Storage/golden/expected-current-from-v1..v5.json` | P1 | регенерация на v7 (исключение ADR-025 §6 R-3) |
| `src/DesktopOrganizer.UI/State/Dto/*.cs` (10 DTO в 8 файлах) | P2 | `[JsonExtensionData] Extras`; enum → `string` (6 полей) |
| `src/DesktopOrganizer.UI/Models/PanelPersistedExtras.cs` (новый), `PreservedEnumValue` | P2 | носитель |
| `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` | P2 | свойство `PersistedExtras` (без INPC) |
| `src/DesktopOrganizer.UI/Models/TolerantEnumParser.cs` (новый) | P2 | парсер §3 |
| `src/DesktopOrganizer.UI/State/UnknownFieldLedger.cs` (новый) | P2 | ledger §2.2 |
| `src/DesktopOrganizer.UI/State/PanelStateMapper.cs` | P2 | extras 4 уровня, enums, preserved raw |
| `src/DesktopOrganizer.UI/State/PagesStateMapper.cs` | P2 | перегрузки с ledger (item/section extras) |
| `src/DesktopOrganizer.UI/State/SystemIconsPanelStateMapper.cs` | P2 | `Kind` строкой, неизвестные items, raw `IconSize` |
| `src/DesktopOrganizer.UI/State/ApplicationStateService.cs` | P2 | ledger в `Load`/`PersistNow`/`ToSnapshotDto`, diag-счётчики |
| `src/DesktopOrganizer.UI/State/PanelCloneBuilder.cs` | P2 | сброс extras |
| `tests/.../Unit/Storage/*`, `Unit/State/*`, `Resources/Storage/golden/*` (новые) | P3 | §6 |
| `adr/ADR-024-versioned-json-persistence.md`, `adr/README.md`, `CLAUDE.md`, `docs/ROADMAP.md`, `CHANGELOG.md`, golden `README.md` | P5 | §8 |

**Без диффа (явно):** `AtomicJsonFileWriter`, `StorageFileLock`, `StateStorePaths`, `StateRestoreCommand`,
`LegacySettingsImporter`, `LayoutSnapshotService`, `PageManagerService`, `SystemIconsPanelManager`,
`PageDescriptor`, `LayoutSnapshot` (Core), `StateJson`, миграции V1–V5, `IAppSettings`/`user.config`.

---

## 8. Документация (пакет P5, `architecture-documenter`)

1. **ADR-024 Amendment 2026-10-10 «Аддитивные ревизии (v7)»:** §1 (major+revision, таблица чтения, high-water
   запись, reserved-ключ №4, отвергнутые альтернативы §1.2/§1.3), §2–§3 (round-trip, носители, толерантный enum,
   preserved raw), §4.3 (однократный откат), §5 (правило аддитивности), F4 как анти-пример. Обновить D-5
   («три reserved-ключа» → четыре), D-7 rung 1 (`major == Current`, любая revision), строку сводки Amendments и
   итоговую строку «Текущая версия схемы — 7, ревизия 0».
2. **`adr/README.md`:** строка ADR-024 — v7 + revision.
3. **`CLAUDE.md`, раздел «Настройки и persistence — ЗАМОРОЖЕНЫ»** — предлагаемая формулировка:
   > `application-state.json` (`StateEnvelope`, **schemaVersion = 7**, `schemaRevision` — аддитивный счётчик;
   > v7 и механизм ревизий введены 2026-10-10 с явного разрешения пользователя — ADR-024 Amendment «Аддитивные
   > ревизии»). Сборки v7 сохраняют неизвестные поля и неизвестные значения enum при записи; старая сборка того же
   > major открывает файл более новой ревизии на запись без потерь.
   >
   > Запрещено без отдельного, явного «да» от пользователя в самой задаче:
   > - **любое новое persisted-поле** (даже аддитивное через `schemaRevision`) — на **каждое** поле своё «да»;
   >   аддитивное поле обязано пройти правило аддитивности ADR-024 (§ «Аддитивные ревизии»), идёт через bump
   >   `CurrentSchemaRevision` + golden-фикстуру `v7-rN-full.json`, **без миграции**;
   > - поднимать `schemaVersion` (major), добавлять миграции, переименовывать/удалять/менять тип или смысл
   >   существующих ключей/DTO, добавлять envelope-ключи;
   > - удалять/ослаблять round-trip неизвестных полей (`[JsonExtensionData]` на DTO, `PersistedExtras`,
   >   `UnknownFieldLedger`) или толерантный разбор enum;
   > - (остальные пункты — `IAppSettings`/`user.config`, семантика загрузки/сохранения — без изменений).
   >
   > Любая задача, которой «нужно» поле в состоянии, останавливается с `CLARIFICATION_NEEDED` и вариантами
   > (A — revision-поле по правилу аддитивности; B — major с миграцией; C — не хранить).

   Защита от инцидента сохраняется: разрешение по-прежнему поштучное; меняется только *цена* разрешённого
   аддитивного поля (без миграции и без read-only для старой v7-сборки).
4. **`docs/ROADMAP.md`:** «Решения, ожидающие пользователя» п.1 → закрыт (ссылка на этот план и Amendment);
   новый раздел/пункты `[x]` по пакетам P1–P3.
5. **`CHANGELOG.md`** + release-notes-строка про однократный read-only при откате на ≤0.8.0 (§4.3).
6. Golden `README.md`: v7, ревизионные фикстуры, правило «одна фикстура на ревизию».

---

## 9. Пакеты, порядок, DoD

```
P1 (dotnet-services-engineer: Services.Storage)  ∥  P2 (dotnet-services-engineer: UI.State/Models)
   → P3 (tdd-test-engineer: §6, новые golden)  → [debug-healer при красном]
      → P4 (code-quality-reviewer ∥ fences-ux-parity-reviewer)
         → P5 (architecture-documenter: §8)
```
P1 и P2 не пересекаются по production-файлам — можно параллельно (разные worktree); общий риск конфликта только
в `SchemaContractTests.cs` (P1 — цепочка миграций, P2 — enum → string в фабрике DTO) — мерж тривиальный.

**Границы разрешения пользователя (что P1/P2 вправе трогать):** только файлы §7 «Пакет P1/P2». Никаких новых
persisted-полей с данными, никаких изменений `IAppSettings`/`user.config`, протокола атомарной записи, бэкапов,
карантина, lock, путей. `CurrentSchemaRevision` остаётся **0** — этот план вводит механизм, а не поля.

**DoD:**
1. `StateEnvelope.CurrentSchemaVersion == 7`, `CurrentSchemaRevision == 0`; `schemaRevision` — reserved; v6-файл
   после запуска: `schemaVersion 7`, `schemaRevision 0`, есть `application-state.premigration-v6.json`.
2. Файл `v7` с `schemaRevision > 0` открывается `Loaded`, `IsWritable == true`, после save `schemaRevision`
   не понижен.
3. Файл с `schemaVersion 8` → `NewerSchema`, read-only, файл байт-в-байт не изменён (существующий тест зелёный).
4. Неизвестные ключи на всех 10 DTO-уровнях (включая панели внутри снапшотов) переживают load → save
   (сравнение множеств key-path + значений).
5. Неизвестное строковое значение любого enum-поля → дефолт, документ **не** в карантине, raw записан обратно,
   если пользователь не менял поле; числовое значение — карантин, как раньше.
6. `PanelCloneBuilder` не переносит extras; snapshot restore — переносит.
7. Все тесты §6 зелёные; `PersistedGeometryInvarianceTests` без диффа и зелёные; input golden v1–v6 без диффа.
8. `dotnet build` + `dotnet test` по solution: **0 errors, 0 warnings**.
9. ADR-024 Amendment, `adr/README.md`, `CLAUDE.md`, ROADMAP, CHANGELOG обновлены (§8).

---

## 10. Риски

| # | Риск | Митигация |
|---|---|---|
| R1 | `[JsonExtensionData]` + camelCase: ключи extras не нормализуются, case-sensitive матчинг даёт Pascal-«двойники» | дословная запись — желаемое поведение; тест §2.4 |
| R2 | `WhenWritingNull` vs null внутри extras | тест «null сохраняется» |
| R3 | дубликат ключа (raw enum в extras и в свойстве) | raw только через свойство; тест парсинга без дубликатов |
| R4 | будущая миграция с allow-list (как V4ToV5 стр. 80–98/133–150) срежет неизвестные ключи | `MigrationJson.CloneAllExcept` + Theory-тест для `FromVersion ≥ 6` + правило в ADR |
| R5 | `PanelJsonItems` (две разные `JsonSerializerOptions`) теряет `PersistedExtras` | тест round-trip для обеих опций; без `[JsonIgnore]` |
| R6 | устаревшие сохранённые поля после правок старой сборкой | правило аддитивности п.4: новая сборка лечит при чтении |
| R7 | `PageDescriptor` value-equality | extras страниц только в ledger |
| R8 | lossy-обновление preserved enum при явном выборе того же значения | задокументированное ограничение §3 |
| R9 | однократный read-only при откате на ≤0.8.0 | принято пользователем; `ReadOnlySessionNotice`, premigration-v6, release notes; любые будущие хотфиксы 0.8.x тоже read-only на v7 — не выпускать v6-сборки после v7 |
| R10 | extra-запись файла сразу после загрузки (порядок ключей) | безвредно; задокументировать |
| R11 | соблазн следующей фичей «тихо» поднять revision | `RevisionFixtures_Exist…` гейт + CLAUDE.md поштучное «да» |
| R12 | `user.config`/`IAppSettings` forward-compat **не покрыт** этим планом | вне scope; отдельная задача при необходимости |
| R13 | `SystemIconsPanelStateDto`/`…ItemStateDto` — public: `Kind` enum → string меняет публичную форму типа | потребители только внутри `DesktopOrganizer.UI` (`SystemIconsPanelManager`, маппер, `IApplicationStateService`) — проверено Grep |

**Нативных/непроверяемых вне Windows участков нет** — всё покрывается `dotnet test`.
Для пользователя на Windows 11 (не блокер): апгрейд реального 0.8.0-профиля (панели на месте, есть
`premigration-v6.json`); ручная вставка неизвестного ключа и `"surfaceStyle":"Mica"` → приложение открывается,
панель Ambient, перемещение панели → в файле `Mica` и ключ на месте; откат на 0.8.0 → read-only notice.
