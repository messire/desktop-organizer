# Workspace Grid — миграция layout snapshots и сохранённых window bounds

**Этап:** P01 пакета [`docs/fix-prompts/`](../fix-prompts/README.md) · **Ветка:** `bugfix/workspace-grid`
**Статус:** Proposed (архитектура/план; реализация — отдельная стадия, не этот проход) · **Дата:** 2026-09-22

Опирается на [ADR-028](../../adr/ADR-028-workspace-grid.md) (workspace grid, `PanelGeometryMode`) и на
параллельно готовящийся ADR-029 (`PanelSizingMode`, `IconLayoutPolicy`) — эти два ADR делят один и тот же
schema bump (v4→v5), поэтому план миграции объединяет оба набора аддитивных полей в одну версию, а не
дробит на v5/v6 — дешевле протестировать один переход, чем два подряд с тем же риском (ADR-024 D-9:
«каждая версия фиксирует реальный дефект», здесь — один реальный шаг: подключение workspace-grid +
icon-layout-policy к персистентной схеме).

---

## 1. Версия схемы

- Текущая `StateEnvelope.CurrentSchemaVersion = 4` (сверено по коду:
  `src/DesktopOrganizer.Services/Storage/StateEnvelope.cs:27`; последняя миграция на диске —
  `V3ToV4Migration.cs`, ADR-025 lossy `accentColor`).
- Этот план вводит **`V4ToV5Migration`**, `CurrentSchemaVersion = 5`.
- Место в документе — тот же `panels[].geometry` объект, который уже существует с v3 (`PanelGeometryDto`,
  `X/Y/Width/Height/MonitorDeviceId/WindowState/IsRolledUp/IsVisible`) — новые поля **дописываются** в
  этот же под-объект, не создают новый top-level ключ (нет причин: это geometry-данные по определению
  ADR-024 D-9 v3 группировки).

## 2. Новые поля (аддитивные, все со значением по умолчанию = сегодняшнее поведение)

| Поле (`panels[].geometry`) | Тип (JSON) | Default при отсутствии | Владелец семантики | Комментарий |
|---|---|---|---|---|
| `geometryMode` | string enum (`"legacyFreeform"` \| `"gridAligned"`) | `"legacyFreeform"` | ADR-028 | `PanelGeometryMode`. Отсутствие поля (документ v1..v4, либо любой писатель, который его не проставил) означает `LegacyFreeform` — **никогда** не `GridAligned` по умолчанию. |
| `widthSpanUnits` | int? (nullable) | `null` | ADR-028 | Заполняется только когда `geometryMode == gridAligned`. Игнорируется читателем, если `geometryMode != gridAligned` (см. §5 «избыточность полей»). |
| `heightSpanUnits` | int? (nullable) | `null` | ADR-028 | Симметрично `widthSpanUnits`. |
| `sizingMode` | string enum (`"manual"` \| `"autoContent"`) | `"manual"` | ADR-029 (семантика), этот план (persistence) | `PanelSizingMode`. Default сохраняет сегодняшнее поведение — ручной размер, ничего не меняется автоматически. |

Плюс поле уровня `content`/`appearance` секции (владение ADR-029, только регистрация здесь ради единой
версии схемы — не переопределяю его семантику):

| Поле | Тип | Default | Владелец |
|---|---|---|---|
| `iconLayoutPolicy` | string enum (`"legacyStretch"` \| `"fixedGap"`) | `"legacyStretch"` | ADR-029 |

**Важно про `X/Y/Width/Height`.** Эти четыре существующих поля **не удаляются и не переименовываются**.
Для `LegacyFreeform`-панелей они остаются единственным источником правды позиции/размера — байт-в-байт,
как сегодня. Для `GridAligned`-панелей они **тоже** продолжают писаться (производные от
`widthSpanUnits`/`heightSpanUnits` через `WorkspaceLayoutPolicy.SpansToBounds` + origin монитора на момент
сохранения) — это осознанное дублирование данных, а не альтернатива: любой код, ещё не знающий про
`geometryMode`/spans (включая более старую версию сборки при откате — см. §4 «Rollback»), обязан
продолжать работать, читая только `X/Y/Width/Height`, как будто это обычная `LegacyFreeform`-панель.
`widthSpanUnits`/`heightSpanUnits` — это **дополнительная**, точная информация для кода, который знает
про `GridAligned`; `X/Y/Width/Height` — это «projection» той же геометрии в глобальные DIP, обязанная
всегда быть согласованной на момент записи.

## 3. Порядок применения (write path)

1. `PanelStateMapper.ToDto` (существующий, ADR-024) читает `ChildWindowModel.GeometryMode`
   (новое INPC-свойство модели, добавляется этим же изменением — вне scope architecture-only прохода,
   реализация стадии 02/03) и:
   - если `LegacyFreeform` — пишет `geometryMode: "legacyFreeform"`, `widthSpanUnits`/`heightSpanUnits`:
     `null`, `X/Y/Width/Height` как сегодня (никаких новых вычислений).
   - если `GridAligned` — пишет `geometryMode: "gridAligned"`, `widthSpanUnits`/`heightSpanUnits` из
     модели, **и** `X/Y/Width/Height`, пересчитанные в этот момент через
     `WorkspaceLayoutPolicy.SpansToBounds(span, U, G)` + origin монитора (`MonitorDeviceId` панели) —
     гарантирует, что откат на билд без поддержки `GridAligned` (см. §4) увидит корректные,
     самосогласованные абсолютные координаты, а не устаревший снимок.
2. `sizingMode`/`iconLayoutPolicy` — аналогично, аддитивные, дефолт при отсутствии соответствующего
   INPC-свойства в модели (если стадия реализации ADR-029 ещё не добавила поле к моменту, когда мигрирует
   схема — план должен работать и в промежуточном состоянии, где только часть полей уже осмысленна).

## 4. Rollback: что делает старая сборка с новым файлом

Сверено по коду (`JsonApplicationStateStore.cs`, D-7 recovery ladder, ADR-024): **rung 7** —
`primarySchemaVersion > CurrentSchemaVersion` (старая сборка видит `schemaVersion: 5`, знает только про
`4`) → **никогда не мигрирует вниз, никогда не перезаписывает** (`src/DesktopOrganizer.Services/Storage/JsonApplicationStateStore.cs:253`
проверка `primarySchemaVersion > StateEnvelope.CurrentSchemaVersion`). Практическое следствие:

- Старая сборка, открывшая файл, написанный новой (v5), **не увидит** ни одной панели этой сессии —
  запускается с пустым структурным состоянием (`NewerSchema` статус, D-7 rung 7), **не трогая** файл на
  диске. Это уже существующее, задокументированное поведение ADR-024 («Rollback к JSON-aware, но старой
  схеме — обрабатывается автоматически рангом 7 читающей лестницы — без действий пользователя, без потери
  данных, но эта сессия стартует с пустым состоянием» — ADR-024 «Consequences»). Этот план **не меняет**
  это поведение и не обязан вводить отдельный аварийный путь — оно уже безопасно по конструкции.
- Если пользователь затем **продолжит** работать на старой сборке и создаст/изменит панели — при
  следующем запуске новой (v5-понимающей) сборки поверх этого файла... **этот сценарий невозможен**:
  пока primary — v5, rung 7 запрещает старой сборке писать поверх него (она работает in-memory с пустым
  состоянием, `Save()` на v5-primary файл при активном rung-7-guard — тот же самый файл на диске остаётся
  v5 нетронутым, потому что старая сборка технически способна физически перезаписать файл, **если** она
  вызовет `Save()` — необходимо явно сверить это по коду, см. флаг риска ниже).

**Подтверждено чтением кода (не гипотеза).** `JsonApplicationStateStore.LoadCore` при обнаружении
`primarySchemaVersion > CurrentSchemaVersion` возвращает `StateLoadStatus.NewerSchema` и выставляет
`_newerSchemaLockout = true` (`JsonApplicationStateStore.cs:115`); `IsWritable => _storageLock.IsHeld &&
!_newerSchemaLockout` (`:104`) — то есть запись действительно запрещена для **всей оставшейся сессии**,
не только пропущено чтение. `Save()` явно проверяет `IsWritable` (`:141`) и отказывает без похода на диск.
Откат старой сборки на v5-файл безопасен по конструкции — дополнительная защита в `V4ToV5Migration` не
требуется.

## 5. Идемпотентность

- `V4ToV5Migration.Migrate(JsonObject v4Document) → JsonObject v5Document` — применяется **один раз** на
  документ версии 4 (стандартный контракт `ISettingsMigration`/`StateMigrationRunner`, ADR-024). Повторный
  прогон той же миграции на уже мигрированном (v5) документе не должен случиться в норме
  (`StateMigrationRunner` применяет миграции строго по цепочке от `schemaVersion` документа до
  `CurrentSchemaVersion`, ADR-024 D-9) — но сама миграция обязана быть написана так, чтобы **если**
  документ уже содержит `geometryMode` (например, гипотетический повторный вызов при баге в раннере) —
  не перезаписывать существующее значение поверх дефолта. Это тот же защитный стиль, что уже применяют
  `V1ToV2Migration`/`V2ToV3Migration`/`V3ToV4Migration` — не новый паттерн.
- **Избыточность `X/Y/Width/Height` vs `widthSpanUnits`/`heightSpanUnits` (§2) не создаёт риска
  рассинхронизации при повторном чтении/записи**, потому что при `geometryMode == gridAligned` источником
  правды на **запись** всегда являются spans (см. §3 п.1) — `X/Y/Width/Height` пересчитываются заново на
  каждый `Save()`, никогда не читаются как вход для `GridAligned`-панели. Единственный читатель
  `X/Y/Width/Height` для `GridAligned`-панели — код, который **не знает** про `geometryMode` (старая
  сборка, см. §4) — то есть путь чтения-и-повторной-записи по устаревшим `X/Y/Width/Height` невозможен,
  пока читает код, который умеет `GridAligned` (он всегда предпочитает spans).

## 6. Что делает «Align to workspace grid» с точки зрения persistence

Не новая операция персистентности — обычный `SaveNow()` (существующий синхронный путь `ApplicationStateService`,
ADR-024 D-12, тот же, что уже используют panel create/delete/snapshot-операции) сразу после применения
команды (ADR-028, раздел «Явная команда»): `geometryMode` меняется на `"gridAligned"`,
`widthSpanUnits`/`heightSpanUnits` заполняются впервые, `X/Y/Width/Height` пересчитываются. Никакой
отдельной миграционной логики для этого перехода не требуется — это обычная запись обычного,
уже-мигрированного (v5) документа новыми значениями существующих полей, а не структурная миграция.

## 7. Дробный `IconSize` (F-11) — предложение, не решение

P00 (`workspace-grid-00-baseline.md`, F-11) зафиксировал персистентный дробный `IconSize`
(`DefaultIconSize = 32.0101...`), не совпадающий с тиками слайдера (`TickFrequency=4`), с
**неустановленной причиной**. Это поле принадлежит системе C (icon layout, ADR-029), не системе B — этот
план не владеет им, но обязан предложить persistence-совместимую обработку, раз миграция уже трогает
соседние поля той же панели:

- **Предложение (не решение — открытый вопрос для ADR-029/стадии реализации).** Миграция v4→v5 **не
  трогает** существующее значение `IconSize`/`DefaultIconSize` вообще — ни округление, ни валидация не
  добавляются в саму миграцию. Обоснование: причина дробности не установлена (P00 явно отмечает это как
  открытый вопрос), и молчаливое округление в миграции необратимо потеряло бы точное значение прежде, чем
  причина будет понята — если впоследствии выяснится, что дробность несёт диагностический смысл (например,
  след конкретного старого бага в UI-биндинге), округление в миграции уничтожило бы улику до того, как её
  успели прочитать. Округление к ближайшему тику слайдера, если оно будет решено ADR-029, должно
  происходить в UI-слое при следующем взаимодействии со слайдером (аналогично тому, как
  `IsSnapToTickEnabled=True`, ADR-018 M-1, уже предотвращает **новый** дробный ввод, не трогая старые
  сохранённые значения) — не в миграции схемы.

## 8. Тест-план миграции

Следуя существующему паттерну `V3ToV4MigrationTests`/`GoldenMigrationTests` (ADR-024 D-9):

1. **`V4ToV5MigrationTests`** (новый файл, `tests/DesktopOrganizer.Tests/Unit/Storage/Migrations/`):
   - `Migrate_MissingGeometryFields_DefaultsToLegacyFreeformManualLegacyStretch` — документ v4 без новых
     полей → после миграции `geometryMode="legacyFreeform"`, `sizingMode="manual"`,
     `iconLayoutPolicy="legacyStretch"`, `widthSpanUnits`/`heightSpanUnits` отсутствуют/`null`.
   - `Migrate_PreservesExistingXYWidthHeight` — существующие `X/Y/Width/Height` не меняются побайтово.
   - `Migrate_DoesNotTouchIconSizeOrDefaultIconSize` — регрессия на §7 (дробный `IconSize` не округляется
     миграцией).
   - `Migrate_IsIdempotent` — повторный вызов на уже мигрированном документе не меняет значения (§5).
   - `Migrate_UnknownEnumValue_FallsBackToDefault` — если по какой-то причине документ содержит
     нераспознанное строковое значение `geometryMode`/`sizingMode`/`iconLayoutPolicy` (ручное
     редактирование файла, повреждение), миграция/десериализация не бросает исключение — деградирует к
     дефолту (тот же стиль устойчивости, что весь остальной `StateMigrationRunner`, ADR-024 M-6:
     «уничтожение процесса из-за одного плохого поля недопустимо»).
2. **`GoldenMigrationTests`** — обновить golden-цепочку v1→v2→v3→v4→**v5**, добавить golden-фикстуру v5
   (byte-for-byte ожидаемый документ после полной цепочки), тот же формат, что существующие v1..v4
   фикстуры (`tests/DesktopOrganizer.Tests/Resources/Storage/golden/`).
3. **`SchemaContractTests`** — расширить проверку: ключи, производимые `V4ToV5Migration`, совпадают
   byte-for-byte с ключами, которые производит сериализация обновлённого `PanelGeometryDto`
   (`geometryMode`/`widthSpanUnits`/`heightSpanUnits`/`sizingMode`) — тот же существующий тест-контракт
   (ADR-024 D-1), расширенный на новые поля, не новый тестовый класс.
4. **`PanelStateMapperTests`** — round-trip тест: `ChildWindowModel` с `PanelGeometryMode.GridAligned` →
   DTO → обратно в модель, все новые поля выживают без потерь (тот же паттерн, что уже покрывает все 17
   существующих полей, ADR-024 «`fences-ux-parity-reviewer` подтвердил round-trip»).
5. **`WorkspaceLayoutPolicyTests`** (новый, чистая математика, без persistence) — покрывает
   `SnapPosition`/`BoundsToSpans`/`SpansToBounds`/`SafeClamp`/`PreferredSize` изолированно от миграции;
   владение — стадия 02, не этот план, упомянуто здесь только для полноты картины «что тестируется на
   каком уровне».
6. **Manual verify (Win10/Win11), не автоматизируется:**
   - Реальный апгрейд: файл, сохранённый билдом без ADR-028 (v4), открывается билдом с ADR-028 — ни одна
     панель не двигается/не меняет размер визуально при первом запуске.
   - Реальный откат: файл, сохранённый билдом с ADR-028 (v5, есть хотя бы одна `GridAligned`-панель),
     открывается билдом без ADR-028 (v4-only) — приложение стартует с пустым состоянием (ожидаемо, rung 7),
     не падает, не портит файл на диске (файл остаётся v5, доступен последующему v5-билду).
   - `--restore-state-backup` (существующий инструмент ADR-024 C7) на v5-документе — восстанавливает
     геометрию `GridAligned`-панелей корректно (включая spans, не только `X/Y/Width/Height`).

## 9. Открытые пункты, требующие подтверждения на стадии реализации (не блокируют принятие ADR-028)

- Точное имя нового INPC-свойства `ChildWindowModel.GeometryMode` (сегодня `Position`/`Size` — без INPC,
  P00 находка; новое поле обязано иметь INPC с самого начала, а не наследовать этот долг) — предложение
  этого плана, не финальное имя.

## 10. Реализация (P02b) — persistence-фундамент, ветка `worktree-agent-afbd77086471469f9`

Стадия реализации этого плана (не архитектура — сам план выше писался как Proposed до кода). Все имена
ниже — **финальные**, сверенные компилятором и зелёным `dotnet test`, а не предложения.

### 10.1 Версия схемы

`StateEnvelope.CurrentSchemaVersion = 5` (`src/DesktopOrganizer.Services/Storage/StateEnvelope.cs`).
Новая миграция — `V4ToV5Migration` (`src/DesktopOrganizer.Services/Storage/Migrations/V4ToV5Migration.cs`),
зарегистрирована в `DiConfig.cs` рядом с `V1ToV2Migration`/`V2ToV3Migration`/`V3ToV4Migration`.

### 10.2 Новые поля — точные имена (JSON camelCase / C# PascalCase)

| JSON-путь | DTO-свойство (`internal`, `DesktopOrganizer.UI.State.Dto`) | Тип DTO | `ChildWindowModel`-свойство (INPC) | Default |
|---|---|---|---|---|
| `panels[].geometry.geometryMode` | `PanelGeometryDto.GeometryMode` | `string` | `GeometryMode` (`PanelGeometryMode`) | `"LegacyFreeform"` |
| `panels[].geometry.widthSpanUnits` | `PanelGeometryDto.WidthSpanUnits` | `int?` | `WidthSpanUnits` (`int?`) | `null` (ключ отсутствует в JSON) |
| `panels[].geometry.heightSpanUnits` | `PanelGeometryDto.HeightSpanUnits` | `int?` | `HeightSpanUnits` (`int?`) | `null` (ключ отсутствует в JSON) |
| `panels[].geometry.sizingMode` | `PanelGeometryDto.SizingMode` | `string` | `SizingMode` (`PanelSizingMode`) | `"Manual"` |
| `panels[].appearance.iconLayoutPolicy` | `PanelAppearanceDto.IconLayoutPolicy` | `string` | `IconLayoutPolicy` (`IconLayoutPolicy`) | `"LegacyStretch"` |

**Решение, зафиксированное здесь впервые (план не уточнял): `iconLayoutPolicy` живёт в `appearance`,
не в `content`.** ADR-029 оставлял выбор между двумя существующими под-объектами открытым («рядом с
`viewMode`/`iconSize» — сверить в коде»). Выбран `appearance` (рядом с `IconSize`), а не `content`
(где `ViewMode`) — обоснование: `IconLayoutPolicy` управляет визуальным распределением иконок внутри
уже выбранного `ViewMode`, той же природы, что `IconSize` (оба — визуальные параметры отрисовки
Grid-режима), тогда как `content` описывает *что* показывает панель (фильтр/сортировка/источник), а не
*как* это визуально расположено.

**Почему все пять новых enum-полей DTO — `string`, а не настоящий C#-enum + `JsonStringEnumConverter`
(как `WindowState`/`PanelViewMode`/`PanelSortMode`/`PanelSurfaceStyle` уже сегодня).** С типизированным
enum + `JsonStringEnumConverter` нераспознанное строковое значение (битый/будущий JSON) кидает
`JsonException` прямо из `payload.Deserialize<ApplicationStateDocumentDto>(...)`
(`ApplicationStateService.DeserializeDocument`) — это происходит **после** recovery ladder ADR-024 D-7
(документ уже считается валидным на уровне JSON/миграций), то есть исключение ничем не перехватывается
и уронило бы `Load()`. Через час обсуждения выбран паттерн уже существующих `IAppSettings.DefaultSurfaceStyle`/
`NormalModeLayoutGestures` (`PanelSurfaceStyleParser.ParseOrAmbient`/`NormalModeLayoutGesturesParser.ParseOrMoveOnly`):
DTO хранит `string`, безопасный `Enum.TryParse` с fallback на дефолт живёт в новых статических классах
`PanelGeometryModeParser.ParseOrLegacyFreeform`/`PanelSizingModeParser.ParseOrManual`/
`IconLayoutPolicyParser.ParseOrLegacyStretch` (все три — рядом с соответствующим enum в
`src/DesktopOrganizer.UI/Models/`), вызываемых из `PanelStateMapper.FromDto`. Значения на диске —
PascalCase, совпадающий с именем C#-члена enum (`"LegacyFreeform"`/`"GridAligned"`/`"Manual"`/
`"AutoContent"`/`"LegacyStretch"`/`"FixedGap"`) — **не** lowerCamelCase, как в черновом примере §2 этого
плана: это исправление под уже действующую конвенцию всего остального документа (`"Normal"`,
`"Maximized"`, `"List"`, `"Ambient"`, `"ShowAll"` — все PascalCase, сверено по golden-фикстурам
`tests/DesktopOrganizer.Tests/Resources/Storage/golden/v4-full.json`), а не отступление от неё.

### 10.3 `V4ToV5Migration` — точное поведение

- Purely additive: копирует все существующие ключи `geometry`/`appearance` через `DeepClone`, затем
  дописывает `geometryMode`/`sizingMode` (в `geometry`) и `iconLayoutPolicy` (в `appearance`) **только
  если ключ ещё не существует** — идемпотентно по построению, без отдельного guard-флага.
  `widthSpanUnits`/`heightSpanUnits` никогда не проставляются миграцией (даже как `null`) — их просто
  нет в выходном документе для любой мигрированной (пере-ADR-028) панели.
- Не валидирует и не санитизирует уже присутствующие enum-строки (idempotency важнее — см. тест
  `Migrate_UnknownEnumStringValue_IsPreservedVerbatim_SafeFallbackHappensAtDeserialization`). Безопасный
  fallback к дефолту происходит на один слой выше, в `PanelStateMapper.FromDto` (см. §10.2).
- `appearance.iconSize`/`DefaultIconSize` не трогается вообще (§7 этого плана подтверждён тестом
  `Migrate_DoesNotTouchIconSizeOrDefaultIconSize`, дробное значение `32.0101` проходит через миграцию
  байт-в-байт).

### 10.4 `IAppSettings`/`Settings.settings` — новые ключи

| `IAppSettings`-свойство | `Settings.settings` default | Применяется |
|---|---|---|
| `DefaultPanelGeometryMode` (`string`) | `"GridAligned"` | Только к **новым** панелям (`MainWindowViewModel.AddPanel`, `App.xaml.cs` `NewPanelWithDefaults` → `App.CreateLassoPanel`) |
| `DefaultIconLayoutPolicy` (`string`) | `"FixedGap"` | Аналогично, те же две точки создания |

`GlobalDefaultsViewModel` получил типизированные обёртки `DefaultPanelGeometryMode`
(`PanelGeometryMode`)/`DefaultIconLayoutPolicy` (`IconLayoutPolicy`) поверх `IAppSettings`, тем же
паттерном, что уже `DefaultSurfaceStyle`/`NormalModeLayoutGestures` — плюс `*Options`-свойства
(`PanelGeometryModeOptions`/`IconLayoutPolicyOptions`) для будущего окна настроек (не создавалось в
этом проходе — вне scope).

**Не добавлено (сознательно, не забыто):**
- `IAppSettings.DefaultPanelSizingMode` — ADR-029 не предлагает глобальный дефолт `SizingMode` для
  новых панелей (только явную миграцию существующих в `Manual`); новые панели наследуют
  `ChildWindowModel`'s собственный дефолт поля (`PanelSizingMode.Manual`) без отдельной настройки.
- Настройки `U`/`G` (workspace grid unit/gap) — ADR-028 определяет их как статические константы
  будущего `WorkspaceGridMetrics` (стадия P02, вне зоны этого прохода), не как персистентные
  пользовательские настройки; ADR-028 нигде не запрашивает UI-контрол для их изменения.

### 10.5 Открытый риск для стадии 03/04 (явно передаю дальше, не решаю здесь)

`AddPanel`/`CreateLassoPanel` теперь создают новую панель с `GeometryMode = GridAligned` (дефолт
настройки), но `WidthSpanUnits`/`HeightSpanUnits` остаются `null` — вычисление spans из фактических
`Position`/`Size` в момент создания требует `WorkspaceLayoutPolicy.BoundsToSpans` (собственность P02,
ещё не существует в этом дереве на момент прохода). Сегодня это чисто инертные данные: ни один
runtime-путь (drag/resize/rendering) ещё не читает `GeometryMode`/spans, так что несогласованность
(`GridAligned` без spans) не проявляется поведенчески. **Стадия 03/04 обязана** либо (a) вычислить
spans в тех же двух точках создания панели при подключении `WorkspaceLayoutPolicy`, либо (b) явно
рассматривать `GeometryMode == GridAligned && WidthSpanUnits == null` как «ещё не выровнено» и
вычислять spans лениво при первом обращении. Персистентный контракт (поля/дефолты/миграция) от этого
решения не зависит и менять не потребуется.

### 10.6 Test log

`dotnet build DesktopOrganizer.sln` — 0 Warning(s) в затронутых файлах, 0 Error(s) в решении.
`dotnet test DesktopOrganizer.sln` — **1953/1953 green** (0 failed, 0 skipped), включая:

- `V4ToV5MigrationTests` (новый, 12 тестов) — missing-fields defaults, X/Y/Width/Height verbatim,
  `iconSize` untouched, idempotency (double-migrate byte-equal via `JsonAssert`), unknown-value
  passthrough (no throw), top-level keys passthrough, sparse panel defaults, layout-snapshot parity,
  pages/systemIconsPanel passthrough, empty-panels-array, no reserved-key collision.
- `GoldenMigrationTests` — обновлена цепочка миграций (+`V4ToV5Migration`), счётчики `applied`
  пересчитаны (v1→5: 4, v2→5: 3, v3→5: 2, v4→5: 1); новый `v5-full.json` (включает одну
  `GridAligned`/`AutoContent`/`FixedGap` панель) как identity-фикстура; новый тест
  `V5Full_MigratedTwice_IsByteForByteIdentical_TwoRestoreCyclesAgree` — байт-в-байт равенство после
  двух проходов полной цепочки на уже мигрированном документе (AGENT_CONTRACT.md /
  `docs/fix-prompts/06-fit-content.md` DoD «snapshots после двух циклов restore одинаковы»,
  проверено здесь на уровне схемы; на уровне `LayoutSnapshotService` — см. ниже).
- `SchemaContractTests` — расширена на `V4ToV5Migration`; новый
  `V4ToV5Migration_NeverProducesAReservedEnvelopeKey`; `MigratedV1Document_KeyShape_...` подтверждает
  ключи, которые реально производит полная цепочка v1→v5, совпадают с ключами, которые производит
  DTO-сериализация (новые enum-строки — всегда непустой default, поэтому всегда присутствуют;
  `widthSpanUnits`/`heightSpanUnits` отсутствуют с обеих сторон для `LegacyFreeform`-панели).
- `PanelStateMapperTests` — round-trip расширен на 5 новых полей + отдельные тесты: `null` spans
  остаются `null` при `LegacyFreeform`, и три теста «нераспознанная строка → безопасный дефолт» (по
  одному на `GeometryMode`/`SizingMode`/`IconLayoutPolicy`).
- `JsonApplicationStateStoreTests` — два существующих теста с ручными `FakeMigration`-цепочками
  (`Load_PrimaryAtOlderSchema_...`, `Load_NothingOnDisk_LegacyHasData_...`) расширены до `(4,5)`, счётчики
  `MigrationsApplied` пересчитаны (2→3, 3→4) — без изменения смысла тестов, чисто отражение нового
  целевого номера версии.
- `LayoutSnapshotServiceTests` — два новых теста: одиночный round-trip всех пяти новых полей через
  Capture→Restore (сырая JSON-сериализация `ChildWindowModel`, не DTO-схема), и
  `Restore_TwiceInARow_ProducesIdenticalWorkspaceGridAndIconLayoutFields` — прямая проверка DoD «два
  цикла restore дают одинаковый результат» на уровне сервиса снапшотов (не только схемы).
- `MainWindowViewModelTests` — три новых теста: `AddPanel` сеет `GeometryMode`/`IconLayoutPolicy` из
  `IAppSettings`, и нераспознанное значение настройки не падает, а откатывается к
  `LegacyFreeform`/`LegacyStretch`.

Golden-фикстуры обновлены по правилу README.md (immutable-except-`expected-current-from-*`): новый
`v5-full.json` (input, immutable) и `expected-current-from-v4.json` (новый) добавлены;
`expected-current-from-v1/v2/v3.json` регенерированы под `CurrentSchemaVersion = 5` — это
задокументированное исключение из правила неизменности golden-файлов (README.md, ADR-025 §6 R-3), не
попытка подогнать тест под код.

## 10.7 Fix cycle 1 — code-review findings (2026-09-22, ветка `worktree-agent-a9b6c18e532cb7c2f`)

`code-quality-reviewer` нашёл 1 MAJOR + 5 MINOR + 1 NIT на ветке §10; все закрыты в этом цикле (1 из
макс. 2). MAJOR-1 напрямую превращал открытый риск §10.5 выше из «инертных данных» в реальный
data-integrity баг: §10.5 предполагало, что `GridAligned` без spans безопасно, потому что «ни один
runtime-путь ещё не читает `GeometryMode`/spans» — верно на момент P02b, но сам факт записи такого
документа на диск (dev-машины, реальные v5-файлы) уже был проблемой независимо от того, что стадия 03
следом начинает читать эти поля как источник правды геометрии.

### MAJOR-1 — `GridAligned` без валидных spans

**(a) Защита уже записанных файлов — `PanelStateMapper.FromDto`.** Новый guard: если
`geometryMode == "GridAligned"`, но `widthSpanUnits`/`heightSpanUnits` не оба `> 0` (отсутствуют, `null`,
`0` или отрицательны), панель на загрузке нормализуется в `GeometryMode = LegacyFreeform` со сброшенными
в `null` spans — тот же «никогда не доверяй, всегда валидируй» стиль, что уже применяют
`PanelGeometryModeParser`/`PanelSizingModeParser`/`IconLayoutPolicyParser` рядом. Это единственная точка,
через которую проходит каждая загрузка панели, поэтому она защищает как уже существующие на
dev-машинах битые v5-файлы (написанные до этого фикса), так и любой будущий баг записи.

**(b) Точки создания панели — решение.** Рассмотрены варианты (a) реализовать полное
origin-aware выравнивание уже сейчас, (b) откатить создание к `LegacyFreeform` с TODO(P03). Выбран
**третий, промежуточный вариант**, эквивалентный духу (b), но не отменяющий уже принятое решение ADR-028
«Вопрос 3 = B» (дефолт новых панелей — `GridAligned`): новый общий хелпер
`DesktopOrganizer.UI.Services.PanelDefaults` (см. §10.7.1 ниже) добавляет
`ReconcileGridAlignedSpans(model)`, вызываемый обеими точками создания (`App.xaml.cs`'s
`CreateLassoPanel`, `MainWindowViewModel.AddPanel`) сразу после того, как вызывающий код закончил
присваивать `Position`/`Size` (если вообще присваивает — простое «Add Panel» этого не делает). Метод,
когда `GeometryMode == GridAligned`:
- считает `WidthSpanUnits`/`HeightSpanUnits` через уже смерженный `WorkspaceLayoutPolicy.BoundsToSpans`
  из текущего `Size` (либо из `PanelSizePreset.Medium`, если `Size` ещё вырожденный `(0,0)` — путь
  простого «Add Panel») — **никогда не оставляет spans `null`**, закрывая MAJOR-1(a)'s guard уже на
  этапе записи, а не только защищая чтение;
- пересчитывает `Size` обратно через `WorkspaceLayoutPolicy.SpansToBounds`, чтобы `Size` и spans были
  побайтово согласованы (`Ceiling`, никогда не обрезает контент — тот же принцип, что «Align to
  workspace grid», ADR-028);
- **`Position` осознанно не трогает.** Настоящее выравнивание позиции требует origin монитора
  (`MonitorInfo.WorkArea.TopLeft`, ADR-028 «Origin для системы B»), который сегодня резолвится позже,
  внутри `WindowManager.ResolveMonitorPlacement` (вызывается из `CreateChildWindow`, то есть **после**
  того, как обе точки создания уже закончили строить модель) — а само это резолвление ещё и обусловлено
  открытым Вопросом 2 ADR-028 (недокументированное пространство `Window.Left/Top` после
  `SetParent(WorkerW)` на мониторе с отрицательными координатами, обязательный manual verify перед
  записью origin-based координат). Тянуться в `WindowManager`'s monitor-resolution flow из зоны этого
  fix-цикла (persistence/DTO/mapper/creation-defaults) означало бы либо дублировать эту логику, либо
  предвосхищать ещё не закрытый архитектурный вопрос — оба варианта хуже, чем оставить чёткий
  `TODO(P03)` с точным контрактом (см. XML-doc `PanelDefaults.ReconcileGridAlignedSpans`).

  **Итог:** новая `GridAligned`-панель теперь всегда имеет самосогласованные, валидные (`>= 1`) spans,
  описывающие её `Size` — что и было главным требованием MAJOR-1 (устранить `GridAligned`-панель,
  неотличимую от битой), — но `Position` пока не квантована к сетке. Это не хуже, чем уже
  толерируется сегодня для вручную перетащенной `GridAligned`-панели до применения команды «Align to
  workspace grid» — та же временная асимметрия, просто с другой стороны (сначала spans, потом позиция,
  а не наоборот).

### MINOR — `Enum.TryParse` пропускал числовые/составные строки

`PanelGeometryModeParser`/`PanelSizingModeParser`/`IconLayoutPolicyParser`: `Enum.TryParse` на
не-`[Flags]`-enum'ах пропускал вне-диапазонные числовые строки (`"7"`, `"-1"`, `"2"`) без объявленного
члена — закрыто `Enum.IsDefined`. **Отдельно обнаружено в процессе (TDD, red phase):** для
двухчленных enum'ов `{0, 1}` (все три enum'а этого пакета) `Enum.IsDefined` **не ловит**
comma-separated комбинацию (`"LegacyFreeform,GridAligned"` парсится в OR `0|1=1`, что совпадает с
реально существующим членом `GridAligned` — `IsDefined` не возражает). Добавлена явная проверка
`!value.Contains(',')` перед `TryParse` — не полагается на числовое совпадение, которое имело место
только из-за того, что в этих enum'ах ровно два члена со значениями `0`/`1`.

### MINOR — неверный JSON-тип поля ронял `Load()`

Документ, прошедший recovery ladder и миграции (валиден на уровне JSON/схемы), но с полем неверного
JSON-типа (`"widthSpanUnits":"2"` — строка вместо `int?`; `"geometryMode":1` — число вместо `string`)
кидал необработанный `JsonException` из `ApplicationStateService.DeserializeDocument`
(`payload.Deserialize<ApplicationStateDocumentDto>`, строгая типизация `System.Text.Json` по
умолчанию) — падение на **каждом** следующем запуске (то же семейство бага, что ADR-024 M-6 уже закрыл
для миграций, только на слой выше). Закрыто: `ApplicationStateService.Load()` теперь оборачивает
десериализацию в `try/catch(JsonException)`, вызывает уже существующий
`IApplicationStateStore.QuarantineLoadedDocument("PayloadDeserializationFailed")` (тот же карантинный
путь ADR-024 finalization C-1), пишет диагностическое событие `StatePayloadDeserializationFailed`
(только `errorType`, без `Message`/пути — существующий инвариант) и деградирует к пустому, доступному
для записи структурному состоянию вместо падения. Возвращаемый `StateLoadStatus` в этом случае —
`Quarantined` (переиспользован существующий член enum — ближайшая по смыслу семантика, без добавления
нового значения и без расширения контракта `StateLoadStatus`).

### MINOR — `PanelStateMapper.ToDto` писал spans и для `LegacyFreeform`

Если панель когда-то была `GridAligned`, затем переключена обратно в `LegacyFreeform` без явной очистки
`WidthSpanUnits`/`HeightSpanUnits` на модели, `ToDto` писал устаревшую пару spans на диск вместе с
`geometryMode: "LegacyFreeform"`. Закрыто: `WidthSpanUnits`/`HeightSpanUnits` теперь пишутся только
когда `model.GeometryMode == GridAligned`, иначе всегда `null` (и, соответственно, отсутствуют в JSON —
`StateJson`'s `WhenWritingNull`).

### MINOR — дефолты новой панели из двух копий → `PanelDefaults`

`App.xaml.cs`'s `NewPanelWithDefaults` и `MainWindowViewModel.AddPanel` независимо повторяли один и тот
же список из семи присваиваний из `IAppSettings`/`GlobalDefaultsViewModel`. Закрыто: новый
`DesktopOrganizer.UI.Services.PanelDefaults` (public static) — `Apply(model, IAppSettings)` (appearance +
`GeometryMode`/`IconLayoutPolicy`) и `ReconcileGridAlignedSpans(model)` (MAJOR-1(b) выше). Обе точки
создания панели теперь вызывают один и тот же тип; `MainWindowViewModel` получил сохранённое поле
`_appSettings` (раньше `IAppSettings` использовался только транзитно для построения `GlobalDefaults`).
Тесты — `PanelDefaultsTests` (новый файл, покрывает обе публичные точки входа напрямую, так как ни
`App.xaml.cs`, ни закрытый метод `MainWindowViewModel.AddPanel` практически не юнит-тестируемы
end-to-end — WPF `Application`/полный DI-граф).

### MINOR — нет постоянной копии документа до миграции

До этого фикса единственное место, где переживал pre-migration документ (например, v4 перед
миграцией в v5), — файл `.bak`, который следующий же обычный `Save()` (любое действие пользователя после
старта) перезаписывает содержимым уже мигрированного документа. Если пользователь затем откатится на
старую сборку (не понимающую v5), у него не осталось бы читаемого артефакта с точным содержимым его
данных на момент до миграции — только сам факт, что старая сборка безопасно откажется писать поверх v5
(rung 7, уже подтверждено §4 выше), но не восстановление. Закрыто: `JsonApplicationStateStore` теперь
пишет однократный, постоянный снимок **до** первой записи мигрированного документа —
`application-state.premigration-v{N}.json` (`{N}` — версия схемы документа на диске **до** миграции,
`StateStorePaths.PreMigrationBackupPath`), рядом с `application-state.json`/`.bak` в `State\`. Пишется
для всех трёх рангов лестницы, где реально применяется in-place-миграция уже существующего на диске
документа (rung 1 — primary; rung 2/3 — recovery из `.bak`, включая quarantine-then-recover); **не**
пишется для rung 5 (`ImportedFromLegacy`) — там не существовало предыдущего JSON-файла на диске, а
`user.config` и так остаётся замороженным без изменений (ADR-024 D-10). Идемпотентно: путь
детерминирован по версии, `File.Exists`-guard перед записью — повторная миграция с той же исходной
версии не перезаписывает уже сохранённый снимок. Best-effort (как `QuarantineFile`) — ошибка записи не
влияет на исход `Load()`.

**Rollback-процедура для пользователя (ручная, добавляется в release notes / troubleshooting, не
автоматизируется этим фиксом):** если после отката на сборку без ADR-028 нужно восстановить именно
pre-migration состояние (а не начать с пустого, что и так безопасно происходит автоматически — см. §4):
закрыть приложение, перейти в `%LOCALAPPDATA%\DesktopOrganizer\State\`, найти
`application-state.premigration-v4.json` (или актуальный `{N}` — путь называет исходную версию),
скопировать/переименовать его в `application-state.json` (заменив текущий v5-файл — рекомендуется
сначала сохранить его копию на всякий случай), затем запустить старую сборку. Старая сборка увидит
файл со знакомой ей `schemaVersion: 4` и загрузит его как обычно.

### NIT — дефолтные литералы дублированы, `SchemaContractTests` дополнен

Литералы `"LegacyFreeform"`/`"Manual"`/`"LegacyStretch"` неизбежно дублируются между `V4ToV5Migration`
(Services-слой, работает только с `JsonObject`, не может ссылаться на UI-типы — ADR-024 D-1, осознанный
trade-off, уже задокументированный этим ADR) и DTO-дефолтами (`PanelGeometryDto`/`PanelAppearanceDto`,
UI-слой). Полное объединение в одну константу невозможно без нарушения layering-принципа D-1. Вместо
этого — как и предполагает D-1's «Cost» — усилен `SchemaContractTests`: новый
`V4ToV5Migration_DefaultValues_MatchApplicationStateDocumentDtoDefaults` мигрирует `v4-full.json` и
сверяет **значения**, которые реально произвела миграция, со значениями `new PanelGeometryDto()`/
`new PanelAppearanceDto()` (не с захардкоженной строкой второй раз в тесте) — раньше существовавший
`MigratedV1Document_KeyShape_...` сверял только пути ключей, не значения, и не поймал бы дрейф между
этими двумя копиями.

### 10.7.1 Итоговый test log fix cycle 1

`dotnet build DesktopOrganizer.sln` — 0 Error(s) (решение целиком, включая ранее существовавшие
StyleCop-warning'и, не относящиеся к этому фиксу).
`dotnet test DesktopOrganizer.sln` — **2305/2305 green** (0 failed, 0 skipped), включая:

- `PanelStateMapperTests` — 5 новых тестов: `ToDto_LegacyFreeformWithStaleSpans_WritesNullSpans`,
  `FromDto_GridAlignedWithNullSpans_NormalizesToLegacyFreeform`,
  `FromDto_GridAlignedWithNonPositiveSpan_NormalizesToLegacyFreeform` (Theory, 4 case),
  `FromDto_GridAlignedWithValidSpans_StaysGridAligned`.
- `WorkspaceGridEnumParsersTests` (новый файл) — 12 тестов на все три парсера: числовые/отрицательные/
  составные строки → безопасный дефолт; валидные значения парсятся case-insensitive.
- `PanelDefaultsTests` (новый файл) — 12 тестов на `PanelDefaults.Apply`/`ReconcileGridAlignedSpans`,
  включая «lasso-путь» (explicit non-degenerate `Size` перед вызовом) и «Add Panel»-путь (вырожденный
  `Size`, откат к `PanelSizePreset.Medium`), и явную проверку «`Position` не трогается».
- `ApplicationStateServiceTests` — 5 новых тестов на неверный JSON-тип поля: никогда не бросает, панели
  деградируют к пустому списку, `StateLoadStatus.Quarantined`, `QuarantineLoadedDocument` вызван с
  правильным `reason`, и sanity-тест «нормальный документ не карантинится».
- `JsonApplicationStateStoreTests` — 5 новых тестов на premigration-бэкап: пишется при реальной
  in-place-миграции (rung 1), переживает последующий обычный `Save()` (сценарий, который фикс и
  закрывает), не перезаписывается повторно, не пишется когда миграций не было, пишется и на пути
  rung 2 (recovery из `.bak`).
- `SchemaContractTests` — 1 новый тест, `V4ToV5Migration_DefaultValues_MatchApplicationStateDocumentDtoDefaults`.

Полный список изменённых/новых файлов — в отчёте оркестратору этого прохода (не дублируется здесь).

## 10.8 Fix cycle 2 (ПОСЛЕДНИЙ, 2026-09-22, ветка `worktree-agent-a5978545e359988db`)

Повторный `code-quality-reviewer` нашёл 2 MAJOR + 3 MINOR + 1 NIT на ветке §10.7; все закрыты в этом
(последнем) цикле. Дополнительно оркестратор решил судьбу двух findings того же прохода без их полного
текста, переданных ему напрямую (не через это ревью): **N-1** и **N-4** переносятся, а не фиксятся здесь.

### N-1 и N-4 — перенесены в P03/P05, не фиксятся в этом цикле

- **N-1** (лассо-жест пишет физические px напрямую в `ChildWindowModel.Size`, а
  `PanelDefaults.ReconcileGridAlignedSpans` квантует их как DIP — при масштабе 150% span завышен
  примерно в полтора раза). Решение оркестратора: закрывается в **P03** вместе с F-7 (ADR-028 «Lasso-
  создание панели: px vs DIP») одним и тем же изменением — конверсия px→DIP через
  `IMonitorService.GetSystemDpiScale()` (добавляется параллельным Stage S), которую нельзя добавить в
  этом fix-цикле, не трогая `App.xaml.cs` (явно запрещено правилами зоны этого прохода). Задокументировано
  здесь как известное, непочиненное ограничение — не тихий пропуск: `PanelDefaults.ReconcileGridAlignedSpans`
  всегда получает уже присвоенный `model.Size` как «якобы DIP» и не может сам знать, был ли он на самом деле
  px с экрана лассо; это TODO(P03) уже зафиксирован в XML-doc метода (fix-cycle 1) — этот цикл не добавляет
  новый TODO, а лишь подтверждает его актуальность и явно называет масштаб риска (150% → ×1.5 завышение
  span).
- **N-4** — finding того же повторного ревью, полный текст которого не был передан исполнителю этого
  fix-цикла (решение оркестратора маршрутизировать его напрямую в P05, минуя этот цикл). Зона этого прохода
  (persistence-фундамент P02b: store/service/DTO/mapper/`PanelDefaults`/`PanelMetrics`) не пересекается с
  владением P05 (`IconLayoutPolicy.FixedGap`/`AdaptiveGridLayout`, ADR-029) — перенос не требует ревизии
  кода, тронутого в этом цикле. Записано здесь для трассируемости находок ревью, а не как фиктивная замена
  недостающего описания.

### MAJOR N-2 — `WritePreMigrationBackupIfMissing` игнорировал advisory lock и писал неатомарно

`JsonApplicationStateStore.WritePreMigrationBackupIfMissing` (fix-cycle 1, §10.7 «нет постоянной копии
документа до миграции») не проверял `_storageLock.IsHeld`, в отличие от соседнего `PersistAfterLoad`,
которому этот метод предшествует для того же самого мигрированного документа — read-only инстанс (лок
проигран другому процессу, ADR-024 D-8) всё равно создавал этот side-channel снапшот на диске, нарушая
инвариант «read-only сессия ничего не пишет, ни по какому пути». Отдельно, `File.Exists` + `File.WriteAllText`
не атомарны: обрыв процесса между двумя вызовами оставлял усечённый файл под финальным, детерминированным
именем (`application-state.premigration-v{N}.json`) — а поскольку следующий же `Load()` видел этот файл
через `File.Exists` и никогда больше не пытался его переписать (в отличие от `NewQuarantinePath`,
который всегда генерирует новое уникальное имя), повреждение было непоправимым на весь срок жизни
инсталляции.

**Фикс.** (1) Guard `if (!_storageLock.IsHeld) return;` — тот же, что уже есть в `PersistAfterLoad`.
(2) Запись через временный файл (`StateStorePaths.NewTempFilePath()` — та же схема имён, что уже использует
`AtomicJsonFileWriter` для собственных temp-файлов, поэтому осиротевший temp-файл от прерванной попытки
подчищается уже существующим `SweepOrphanedTempFiles()` в начале каждого `LoadCore`) + `File.Move(temp,
target, overwrite: false)`. Обрыв между записью temp и переименованием оставляет осиротевший temp-файл
(подчищается на следующем запуске) и, что важно, финальный путь просто не существует — никогда не
существует под постоянным именем в усечённом виде. Гонка с конкурентным писателем (или более ранней
попыткой в том же процессе) даёт `IOException` («уже существует») от `File.Move`, который тихо
проглатывается как успех (первая запись побеждает), а не как ошибка.

Тесты (`JsonApplicationStateStoreTests`): `Load_PrimaryAtOlderSchema_StorageLockNotHeld_DoesNotWritePreMigrationSnapshot`
(без лока — файла нет) и `Load_PrimaryAtOlderSchema_OrphanedTempFileFromPriorCrash_DoesNotLeaveATruncatedSnapshot`
(симуляция обрыва: заранее существующий осиротевший temp-файл с намеренно невалидным JSON — после `Load()`
он подчищен, а по финальному пути лежит полный, парсящийся снапшот, а не усечённый).

### MAJOR N-3 — `null` вместо коллекции в payload ронял `Load()` за пределами существующего guard'а

`ApplicationStateService.Load()` — `document.Panels.Select(...)`, `document.Pages.Ids`/`.CurrentPageId`,
`document.LayoutSnapshots.Select(...)` читались без проверки на `null`. Документ вида `{"panels":null}`
(или `"pages":null"`/`"layoutSnapshots":null`) — валидный JSON, успешно проходящий и recovery ladder
(ADR-024 D-7), и саму десериализацию `payload.Deserialize<ApplicationStateDocumentDto>(...)` (JSON `null`
для *reference*-типизированного свойства — это не несовпадение типов, `System.Text.Json` просто
присваивает `null`, независимо от C#-nullable-аннотаций, которые чисто compile-time и не влияют на
runtime-поведение десериализатора). Результат — `NullReferenceException` СНАРУЖИ существующего
`try/catch(JsonException)` (fix-cycle 1, §10.7 «неверный JSON-тип поля ронял Load()») — тот же класс
крашa `Program.Main` на каждом последующем запуске, который тот catch должен был закрыть целиком, но не
закрыл для этого конкретного случая.

**Фикс.** `Panels = (document.Panels ?? []).Select(...)`; `var pages = document.Pages ?? new
PagesStateDto();` (`pages.Ids`/`pages.CurrentPageId` читаются из локальной переменной); `Snapshots =
(document.LayoutSnapshots ?? []).Select(...)`. `SystemIconsPanel`-веткa не тронута — она уже безопасна
(тернарник читает `document.SystemIconsPanel` только когда `sipNode is not null`, а эти два условия
всегда согласованы).

Тесты (`ApplicationStateServiceTests`): `Load_NullPanelsInPayload_NeverThrows_DegradesToEmptyPanels`,
`Load_NullPagesInPayload_NeverThrows_FallsBackToDefault`,
`Load_NullLayoutSnapshotsInPayload_NeverThrows_DegradesToEmptySnapshots`.

### MINOR N-7 — типизированные enum-поля DTO принимали любое число, не только распознанное имя

`WindowState`/`PanelViewMode`/`PanelSortMode`/`PanelSurfaceStyle` — настоящие C#-enum на
`PanelGeometryDto`/`PanelContentDto`/`PanelAppearanceDto` (в отличие от новых ADR-028/029 полей — те
намеренно `string` + свой `Enum.TryParse`-парсер, см. §10.2), сериализуются общим `StateJson.Options`'s
`JsonStringEnumConverter()`. Нераспознанная JSON-*строка* для этих полей уже бросала `JsonException`
(и уже безопасно ловилась существующим guard'ом, fix-cycle 1) — но `JsonStringEnumConverter`'s дефолтный
`allowIntegerValues: true` молча принимал ЛЮБОЕ JSON-*число*, включая не соответствующее ни одному члену
enum'а (`"windowState": 999` → `(WindowState)999`, неопределённое значение) — асимметрия между двумя
JSON-формами одного и того же логического значения. Риск не гипотетический: `(WindowState)999`,
дойдя до реального `System.Windows.Window.WindowState`-сеттера, бросает
`InvalidEnumArgumentException` — крашing другого рода, ниже по потоку, вне зоны видимости уже
существующего guard'а на десериализации.

**Фикс.** `new JsonStringEnumConverter(allowIntegerValues: false)` в `StateJson.Options` — единственная
строка. Замыкает асимметрию: JSON-число для любого из четырёх полей теперь бросает тот же
`JsonException`, что уже бросает нераспознанная строка, и уже безопасно ловится существующим guard'ом
(fix-cycle 1) с деградацией до пустого структурного состояния, а не с распространением неопределённого
enum-значения дальше по коду. Риск регрессии golden-фикстур/цепочки миграций — нулевой: миграции работают
на уровне `JsonObject` напрямую (ADR-024 D-1), никогда не через этот `JsonSerializerOptions`, а
`V1ToV2Migration`/`V2ToV3Migration` уже конвертируют легаси-числа в имя (или в задокументированный дефолт
для вне-диапазонного значения, см. `V2ToV3MigrationTests.Migrate_UnknownWindowStateValue_DefaultsToNormal`)
до того, как документ вообще достигает текущей схемы, которую десериализует этот `Options`.

Тест (`ApplicationStateServiceTests`, Theory на все 4 поля):
`Load_TypedEnumFieldAsOutOfRangeNumber_NeverThrows_DegradesToQuarantined`
(`geometry.windowState`/`content.viewMode`/`content.sortMode`/`appearance.surfaceStyle`, значение `999`).

### MINOR N-5 — `PanelDefaults.ReconcileGridAlignedSpans` не клампил к `MinSpanExpanded`

`WorkspaceLayoutPolicy.BoundsToSpans` сам по себе никогда не гарантирует минимум —
`WorkspaceLayoutPolicy.SnapBounds` (математика команды «Align to workspace grid») всегда композирует его
с явным клампом к минимуму, а `ReconcileGridAlignedSpans` (fix-cycle 1, MAJOR-1(b)) — нет. Крошечный явный
`Size` (например, очень маленький прямоугольник лассо) мог засеять новую `GridAligned`-панель ниже
задокументированного минимума (`WorkspaceGridMetrics.MinSpanExpanded` = 12×12 units).

**Фикс.** `span = new GridSpan(Math.Max(rawSpan.WidthUnits, MinSpanExpanded.WidthUnits),
Math.Max(rawSpan.HeightUnits, MinSpanExpanded.HeightUnits))` — та же логика, что приватный
`WorkspaceLayoutPolicy.ClampToMinimum` реализует внутри `SnapBounds`, продублированная в
`PanelDefaults` (не вызывается напрямую — метод `private`, а `WorkspaceLayoutPolicy.cs` вне зоны этого
прохода, см. «Правила» задания; риск расхождения логики минимален — это ровно `Math.Max` по двум полям).

Тест (`PanelDefaultsTests`): `ReconcileGridAlignedSpans_TinyExplicitSize_ClampsToMinSpanExpanded` (явный
`Size(10, 10)` → зажат к `MinSpanExpanded`, `Size` пересчитан обратно через `SpansToBounds`).

### MINOR N-6 — `AddPanel`/GridAligned давал 248×232 вместо прежних 260×300

`PanelDefaults.ReconcileGridAlignedSpans`'s fallback для вырожденного (CLR-дефолтного) `Size` — плоское
«Add Panel»-действие, которое никогда не присваивает `Position`/`Size` — использовал
`PanelSizePreset.Medium` (248×232 DIP). Это ДРУГОЙ, меньший размер, чем 260×300 DIP, который
`LegacyFreeform`-панель при том же «Add Panel» получает от `ChildWindow.xaml`'s собственного
`Window.Width`/`Window.Height` — при `DefaultPanelGeometryMode=GridAligned` (дефолт с ADR-028 «Вопрос 3»)
новая панель оказывалась заметно меньше, чем была бы годом раньше или чем всё ещё получает
`LegacyFreeform`-панель — незапрошенное регрессионное изменение размера по умолчанию.

**Фикс.**

1. Источник `260×300` (`ChildWindow.xaml:24`, было `Height="300" Width="260"` литералами) вынесен в
   единую константу — `PanelMetrics.DefaultPanelWidth`/`DefaultPanelHeight` (по образцу уже существующей
   пары `HeaderHeight`/`HeaderRowHeight`: два plain-`double` для `x:Static`-потребления из XAML, плюс
   `PanelMetrics.DefaultPanelSize` (`System.Windows.Size`) для C#-потребителей, которым нужны обе
   размерности сразу — `x:Static` не умеет адресовать вложенный член (`DefaultPanelSize.Width`), поэтому
   для `Window.Width`/`Window.Height` заведены именно raw-`double`-константы, а не одно свойство `Size`).
2. `ChildWindow.xaml:24` — единственная изменённая строка атрибутов `Window` в этом файле (как и требует
   зона задания — параллельно этот же файл правят P07 fix (`ResizeOverlay`/a11y) и Stage S
   (`ContextMenu`)): `Height="{x:Static uimodels:PanelMetrics.DefaultPanelHeight}"
   Width="{x:Static uimodels:PanelMetrics.DefaultPanelWidth}"`.
3. `PanelDefaults.ReconcileGridAlignedSpans`'s fallback-seed меняется с
   `WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset.Medium, ...)` на `PanelMetrics.DefaultPanelSize`.
   `BoundsToSpans(260×300, U=8, G=8)` → `ceil((260+8)/8 − 0.5/8) = 34`, `ceil((300+8)/8 − 0.5/8) = 39`
   (см. `WorkspaceLayoutPolicy.SpanQuantizationToleranceDip`, MAJOR-1 из P02 fix cycle 1) →
   `SpansToBounds((34,39), 8, 8)` = `264×304` DIP — ближайший span-extent, как и предполагало решение
   оркестратора.

Тест (`PanelDefaultsTests`, переименован и обновлён):
`ReconcileGridAlignedSpans_GridAlignedWithDegenerateSize_SeedsDefaultPanelSizeInstead` — проверяет и
вычисленный через `WorkspaceLayoutPolicy.BoundsToSpans(PanelMetrics.DefaultPanelSize, ...)` span (не
дублируя арифметику вручную), и pinned-регрессию на конкретные числа (`34`/`39`/`264`/`304`).

### NIT N-8 — потеря более специфичного `StateLoadStatus` при overwrite на `Quarantined`

`ApplicationStateService.Load()`'s `catch (JsonException)` безусловно перезаписывает `status` в
`Quarantined` — это уже задокументированное, намеренное решение fix-cycle 1
(`IApplicationStateService.Load()`'s XML-doc: «...становится `Quarantined` в этом случае, независимо от
того, что сообщил нижележащий store»), поэтому возвращаемое значение метода этим циклом не меняется (это
расширило бы уже зафиксированный контракт). Вместо этого более специфичный исходный статус (например,
`RecoveredFromBackup` — документ ОДНОВРЕМЕННО потребовал восстановления из `.bak` И затем не прошёл
десериализацию на уровне DTO) сохраняется в диагностике: `DiagPayloadDeserializationFailed` теперь
принимает и пишет `originalStatus` (закрытое множество имён enum'а — так же безопасно логировать, как уже
логируемый `errorType`).

Тест (`ApplicationStateServiceTests`):
`Load_PayloadFieldHasWrongJsonType_DiagnosticsRecordsOriginalStatusBeforeOverwrite` — стор возвращает
`RecoveredFromBackup` с payload, содержащим неверный тип поля; `StatePayloadDeserializationFailed`
записан с `originalStatus: "RecoveredFromBackup"`.

### 10.8.1 Итоговый test log fix cycle 2

`dotnet build DesktopOrganizer.sln` — 0 Error(s) (полная пересборка решения — 627 warning'ов). Один новый
StyleCop-warning этого цикла: SA1203 на `PanelMetrics.DefaultPanelWidth`/`DefaultPanelHeight` (константы
объявлены после уже существующего `static readonly ContentLeftInsetColumnWidth`) — принятый trade-off
зоны задания «`PanelMetrics` (только новая константа)»: перестановка уже существующих полей файла для
формального удовлетворения SA1203 не входит в разрешённый объём правки этого файла в этом цикле. Тот же
файл уже содержит идентичное, более старое нарушение того же правила (`ContentLeftInset` после
`HeaderRowHeight`) — не новый класс проблемы, а продолжение уже принятого в файле стиля «const рядом со
своим производным wrapper'ом», а не строго «все const впереди». Остальные 626 warning'ов —
ранее существовавший StyleCop-шум по всему решению (включая тот же паттерн «секционный комментарий +
пустая строка», что уже использует остальной тестовый код), не регрессия этого цикла.
`dotnet test DesktopOrganizer.sln` — **2474/2474 green** (0 failed, 0 skipped), включая:

- `JsonApplicationStateStoreTests` — 2 новых теста (N-2):
  `Load_PrimaryAtOlderSchema_StorageLockNotHeld_DoesNotWritePreMigrationSnapshot`,
  `Load_PrimaryAtOlderSchema_OrphanedTempFileFromPriorCrash_DoesNotLeaveATruncatedSnapshot`.
- `ApplicationStateServiceTests` — 3 новых теста на `null`-коллекции (N-3); 1 Theory × 4 case на
  вне-диапазонные числовые enum'ы (N-7); 1 новый тест на сохранение `originalStatus` в диагностике (N-8).
- `PanelDefaultsTests` — 1 новый тест на клапан `MinSpanExpanded` (N-5); 1 существующий тест переименован
  и обновлён под новый дефолтный размер (N-6): `ReconcileGridAlignedSpans_GridAlignedWithDegenerateSize_SeedsDefaultPanelSizeInstead`.

Изменённые файлы этого цикла: `src/DesktopOrganizer.Services/Storage/JsonApplicationStateStore.cs` (N-2),
`src/DesktopOrganizer.UI/State/ApplicationStateService.cs` (N-3, N-8),
`src/DesktopOrganizer.UI/State/IApplicationStateService.cs` (N-8, doc only),
`src/DesktopOrganizer.UI/State/StateJson.cs` (N-7), `src/DesktopOrganizer.UI/Models/PanelMetrics.cs` (N-6,
новые константы), `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (N-6, одна строка атрибутов `Window`),
`src/DesktopOrganizer.UI/Services/PanelDefaults.cs` (N-5, N-6),
`tests/DesktopOrganizer.Tests/Unit/Storage/JsonApplicationStateStoreTests.cs`,
`tests/DesktopOrganizer.Tests/Unit/State/ApplicationStateServiceTests.cs`,
`tests/DesktopOrganizer.Tests/Unit/Services/PanelDefaultsTests.cs`, этот план.

### 10.9 Финальная верификация P02b (2026-09-22) — MAJOR-1: null-элементы коллекций

Верификация fix-цикла 2 нашла тот же класс дефекта, что N-3, на уровень глубже: `{"panels":[null]}`,
`{"layoutSnapshots":[null]}` и `{"layoutSnapshots":[{"panels":null}]}` давали `ArgumentNullException`/
`NullReferenceException` вне `catch (JsonException)` → детерминированный краш `Program.Main` при каждом
старте. Закрыто оркестратором напрямую (без нового цикла): `ApplicationStateService.Load()` и
`ToLayoutSnapshot` пропускают null-элементы (`.Where(x => x is not null)`) и `dto.Panels ?? []`.
Тесты: `Load_NullElementInPanels_NeverThrows_SkipsTheNullEntry`,
`Load_NullElementInLayoutSnapshots_NeverThrows_SkipsTheNullEntry`,
`Load_SnapshotWithNullPanels_NeverThrows_YieldsEmptySnapshot` (`ApplicationStateServiceTests`).
Перенесено как известное: `RollUpBehavior._expandedHeight = 300` — оставшийся дубль
`PanelMetrics.DefaultPanelHeight` (P04); `MinSpanExpanded` 88 DIP < `ChildWindow.MinWidth` 120 (P04, вместе
с `MinWidthSpanExpanded` сверкой). Статус P02b: **закрыт** (Approve).
