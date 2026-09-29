# План: переименование страниц — отображаемые имена страниц, схема v6

> **Статус: реализовано, см. [ADR-043](../../adr/ADR-043-page-display-names.md) (Accepted, 2026-09-29).**
> План остаётся историческим артефактом.

> **Источник:** `docs/ROADMAP.md` → «Бэклог 1.x: UX-полировка» → пункт «Переименование страниц».
> **Ветка:** `rc/0.8.0` (база `v0.7.0`, `5b83eb5`). **Планировщик:** `fences-feature-planner`, 2026-09-29.
> **Scope: L** (Services/Storage + UI/State + UI/Services + UI/ViewModels + XAML + RESX; нативного кода нет).
> **Новый ADR:** ADR-043 «Page display names, schema v6» (ближайший свободный номер на момент написания;
> если параллельная задача бэклога заняла 043 раньше — взять следующий) + amendment к ADR-024 и ADR-032.

---

## 0. Решение пользователя и границы разрешения

**Явное решение пользователя (2026-09-29, не переспрашивать):** у страницы стабильный `id` и отдельное
отображаемое `name` — так же, как у панелей (`PanelStateDto.Id` + `Title`). Это явное разрешение на
расширение замороженного контура persistence **только в объёме**:

- `StateEnvelope.CurrentSchemaVersion`: **5 → 6**;
- новая миграция `V5ToV6Migration` (лестница `ISettingsMigration`, `JsonObject`-only, ADR-024 D-1),
  без потерь и идемпотентная, по образцу `V4ToV5Migration`;
- изменение DTO `pages` (`PagesStateDto`) и маппинга в `ApplicationStateService`;
- premigration-бэкап и rung 7 «newer schema → read-only» — **существующим механизмом, без изменений**.

**Вне разрешения (не трогать):** `IAppSettings`/`AppSettings`/`Settings.settings`/`user.config`
(включая legacy-ключи `Pages`/`CurrentPage`), `UserConfigFolderMigration`, `JsonApplicationStateStore`
(лестница восстановления, атомарная запись, `.bak`, карантин, advisory lock, `WritePreMigrationBackupIfMissing`,
`StateStorePaths`), `StateMigrationRunner`, `StateRestoreCommand`, `LegacySettingsImporter`,
`LayoutSnapshotService`, формат `panels`/`layoutSnapshots`/`systemIconsPanel`, семантика
`RequestSave`/`SaveNow`. Любая потребность выйти за эти рамки → `CLARIFICATION_NEEDED`, не самодеятельность.

### 0.1. Расхождение в брифе: имя premigration-бэкапа

В брифе сказано «premigration-бэкап `application-state.premigration-v6.json`». По коду
(`StateStorePaths.PreMigrationBackupPath(fromSchemaVersion)`, вызов
`WritePreMigrationBackupIfMissing(primaryDocument, primarySchemaVersion)` в `JsonApplicationStateStore.LoadCore`)
файл именуется по **исходной** версии: миграция v5→v6 пишет **`application-state.premigration-v5.json`**.
Файла `premigration-v6.json` существующий механизм не создаёт.

План **не меняет** механизм (запрет «ничего другого в контуре persistence не трогать»). Но есть связанный
риск R-2 (§9): если на машине пользователя остался `application-state.premigration-v5.json` от
инцидента 2026-09-24, `WritePreMigrationBackupIfMissing` увидит `File.Exists` → **пропустит запись**, и
premigration-копией состояния на момент апгрейда до 0.8.0 окажется устаревший файл пятидневной давности.
Варианты вынесены оркестратору в §12 (Q-1). Решение по умолчанию: **вариант A** — код store не трогать,
перед первым запуском 0.8.0 переименовать старый файл вручную (шаг manual verify M-0).

---

## 1. Требования

### Функциональные
- F-1. У каждой страницы есть стабильный `id` (не меняется никогда, на него ссылаются `panels[].pageId`
  и `layoutSnapshots[].panels[].pageId`) и отображаемое `name`.
- F-2. Пользователь переименовывает страницу inline в окне настроек (раздел «Страницы»): кнопка
  «Переименовать» / двойной клик по имени / F2 на строке → `TextBox`; Enter/потеря фокуса — подтвердить,
  Esc — отменить (паттерн ADR-013 Решение 6 и `PanelTitleRenameBehavior`).
- F-3. Валидация: trim; непустое; ≤ 40 символов; без управляющих символов; уникально среди отображаемых
  имён страниц без учёта регистра. Ошибка — ненавязчивый inline-хинт, **никогда** `MessageBox`
  (ADR-013 Решение 6).
- F-4. Страница `"default"` без заданного имени по-прежнему показывается локализованным
  `Page_Default_DisplayName` и переключается вместе с языком UI. После переименования — показывается
  заданное имя как есть (данные, ADR-032 D-5). Пустой ввод для `"default"` = сброс к локализованному имени.
- F-5. Новые страницы получают непрозрачный `id` (GUID) и локализованное в момент создания имя
  `Page_NewName_Format` («Page {0}» / «Страница {0}»), уникальное среди отображаемых имён.
- F-6. Все места, где сейчас виден id страницы (раздел «Страницы», селектор страницы в «Панелях»,
  меню «Переместить на страницу»), показывают отображаемое имя.
- F-7. Существующее состояние v5 мигрирует без потерь: `"default"` → без `name`; любой другой id →
  `name = id` (пользователь видит ровно то же, что видел до апгрейда).

### Нефункциональные
- NF-1. 0 errors / 0 warnings (CS/CA/SA/nullable/`linter.ruleset`), без подавлений.
- NF-2. Rename — структурное изменение → синхронный `SaveNow()` (ADR-024 D-12), как `AddPage`/`RemovePage`.
- NF-3. Битые данные не валят загрузку: `null`-элементы, пустые id, отсутствующий `name`, дубли id —
  деградация, не исключение.
- NF-4. Заложить интерфейс под будущие пункты бэклога (трей-подменю «Страницы», round-robin по
  двойному клику) — **не реализовывать** их.

---

## 2. Целевой формат `pages` в JSON (schema v6)

### 2.1. Сравнение вариантов

| Вариант | v6 `pages` | Плюсы | Минусы | Итог |
|---|---|---|---|---|
| **A. Массив объектов «как панели»** | `{"items":[{"id":"default"},{"id":"work","name":"Work"}],"currentPageId":"default"}` | Один источник правды: порядок, id и имя в одной записи; форма идентична `panels[]` (`id` + отображаемое поле); расширяемо (иконка/цвет страницы в будущем — ещё одно поле записи); `SchemaContractTests.CollectKeyPaths` работает без спец-случаев (ключи = схема) | Не «аддитивно» на уровне ключа: `ids` заменяется на `items` (как V3ToV4 перестраивал `appearance`) | **Выбран** |
| B. Параллельная карта `names` | `{"ids":[…],"names":{"work":"Work"},"currentPageId":…}` | Буквально аддитивно (`ids` не трогается) | Два источника правды (осиротевшие ключи `names`, рассинхрон при удалении/добавлении); **ключи словаря — данные, а не схема** → `SchemaContractTests.CollectKeyPaths` соберёт `pages.names.work` как «путь схемы» и сломает контракт-тест R-9; JSON-ключи с пробелами/кириллицей из legacy-id | Отклонён |
| B'. Параллельный массив `names` | `{"ids":[…],"names":[null,"Work"],…}` | Аддитивно, без проблемы ключей-данных | Связь по индексу — хрупко (любая правка одного массива без другого молча перепутывает имена) | Отклонён |
| C. Переименование самого `id` | `ids` как было, но id меняется на имя | Схема не меняется | Перезапись `pageId` во всех `panels` **и во всех `layoutSnapshots[].panels`** при каждом переименовании; снапшоты, снятые до rename, теряют связь; id снова становится видимой и переводимой строкой (конфликт с ADR-032 D-5) | Отклонён (и отклонён пользователем) |

**Почему отказ от «аддитивности» в варианте A ничего не стоит.** Аддитивность полезна только если
старая сборка сможет прочитать новый документ. Здесь это невозможно в принципе: v0.7.0 видит
`schemaVersion: 6 > 5` и уходит в rung 7 (`NewerSchema`) **до** чтения любого payload-ключа — независимо
от того, сохранён ли `ids`. Значит, критерии для миграции — «без потерь + идемпотентна», и A им
удовлетворяет: каждый id сохраняется побайтно, порядок и `currentPageId` не меняются.

### 2.2. Точная форма v6

```json
"pages": {
  "items": [
    { "id": "default" },
    { "id": "work", "name": "work" },
    { "id": "3f2a9c0e5b7d4e1a9c3b2d1e0f4a5b6c", "name": "Проекты" }
  ],
  "currentPageId": "default"
}
```

- `items` — упорядоченный массив; порядок = порядок страниц в UI и для Ctrl+PgUp/PgDn/будущего round-robin.
- `items[].id` — строка, обязательна, стабильна навсегда.
- `items[].name` — строка, **опциональна**: ключ отсутствует (а не `null`), когда имени нет. Отсутствие
  допустимо только у `"default"` (локализуется конвертером); для остальных страниц отсутствие —
  повреждение, на загрузке лечится `name = id` (§3.3). `StateJson.Options` уже пишет с
  `WhenWritingNull` — `null`-имя на диск не попадает, так же как `widthSpanUnits` в v5.
- `currentPageId` — без изменений (инвариант ADR-024 §0.2 «`currentPageId` ∈ ids» пишется атомарно в
  том же объекте, поэтому обёртка-объект `pages` сохраняется, а не превращается в голый массив).
- `ids` в v6 **не пишется** (единственный источник — `items`).

### 2.3. Правила миграции `V5ToV6Migration` (`Services/Storage/Migrations/V5ToV6Migration.cs`)

`FromVersion = 5`, `ToVersion = 6`. Работает только на `JsonObject` (ADR-024 D-1), не бросает на
«странных» данных — пропускает их, как V4ToV5.

1. **Все top-level ключи payload, кроме `pages`, копируются как есть** (`DeepClone`): `panels`,
   `layoutSnapshots`, `systemIconsPanel` и любые неизвестные. *Отличие от V4ToV5 (белый список из четырёх
   ключей) — осознанное: здесь нечего менять за пределами `pages`, а копирование всего исключает класс
   ошибок «миграция молча выбросила ключ».* `panels[].pageId` и `layoutSnapshots[].panels[].pageId` —
   побайтно без изменений (id стабильны).
2. `pages` отсутствует или не `JsonObject` → в результате `pages` отсутствует (загрузка уже даёт дефолт
   `["default"]`, `ApplicationStateService.Load`).
3. `pages` — объект:
   - **Идемпотентность:** если в нём уже есть `items` (массив) — объект копируется как есть, кроме ключа
     `ids` (отбрасывается, если вдруг присутствует рядом с `items`). Повторный прогон = тождество.
   - Иначе строится `items` из `ids`: для каждого элемента-строки `s`:
     `s == "default"` (ordinal) → `{"id":"default"}`; иначе → `{"id":s,"name":s}`.
     Не-строки и `null` пропускаются. Дубликаты id **не** чистятся (санитизация — задача маппера, как
     сказано в ремарках V4ToV5; миграция не второе место с шансом разойтись).
   - `ids` отсутствует или не массив → `items: []` (маппер на загрузке вернёт `default`).
   - `currentPageId` и любые иные ключи объекта `pages` копируются как есть.
4. Регистрация: `DiConfig.cs` — `services.AddSingleton<ISettingsMigration, V5ToV6Migration>();` рядом с
   V4ToV5 (строка ~162). `StateMigrationRunner` сам проверит непрерывность цепочки.
5. `StateEnvelope.CurrentSchemaVersion = 6`; xml-doc константы обновить.

Migration чейн из legacy (`user.config` → v1 → … → v6) и `--restore-state-backup` (восстановление v5
`.bak` в v6-сборке) проходят через ту же лестницу автоматически — изменений в них не нужно.

### 2.4. Откат на v0.7.0 — известный риск (документировать, не чинить)

- v0.7.0 открывает v6-файл → rung 7 `NewerSchema`: пустая структурная сессия, запись запрещена. Для
  пользователя это выглядит как «все панели пропали» (ровно сценарий инцидента 2026-09-24), но файл
  **не перезаписывается**.
- Возврат к 0.8.0+ → состояние на месте, потерь нет.
- Реальный откат *с данными* на 0.7.0 — только вручную: при закрытом приложении скопировать
  `State\application-state.premigration-v5.json` поверх `State\application-state.json`. Получите
  состояние **на момент апгрейда**; изменения, сделанные в 0.8.0 (включая имена страниц), теряются.
  Автоматического понижающего конвертера нет и не планируется (ADR-024 D-7: вниз не мигрируем).
- Задокументировать: ADR-043 «Consequences», `docs/Refactoring/Storage/rollback.md` (новый подраздел
  «Scenario B, v6 → v5»), CHANGELOG (раздел «Upgrade notes»), README (строка про откат).

---

## 3. Доменная модель, DTO, маппинг

### 3.1. Модель (UI-слой — страницы живут в `DesktopOrganizer.UI`)

`src/DesktopOrganizer.UI/Models/PageDescriptor.cs`

```csharp
/// Immutable snapshot of one page: stable Id + optional stored display Name (null only for an unnamed "default").
public sealed record PageDescriptor(string Id, string? Name);
```

`src/DesktopOrganizer.UI/Models/PageDisplayInfo.cs` — `readonly record struct PageDisplayInfo(string Id, string? Name)`:
значение для биндинга в конвертер (см. §5.3). *Можно слить с `PageDescriptor`, если wpf-ui-builder
решит, что одного типа достаточно — тогда `PageItemViewModel.Display` возвращает новый `PageDescriptor`
при каждом изменении имени; главное — новая ссылка/значение при rename, чтобы биндинг перерисовался.*

`src/DesktopOrganizer.UI/Models/PageRenameResult.cs`

```csharp
public enum PageRenameResult { Renamed, Unchanged, NotFound, Empty, TooLong, Duplicate, InvalidCharacters }
```

### 3.2. DTO (`src/DesktopOrganizer.UI/State/Dto/`)

- `PagesStateDto` → v6:
  ```csharp
  internal sealed class PagesStateDto
  {
      public List<PageStateDto> Items { get; set; } = [new() { Id = "default" }];
      public string CurrentPageId { get; set; } = "default";
  }
  ```
  Свойство `Ids` **удаляется** (v6 его не пишет; при чтении v6-документа ключа нет). xml-doc: «Schema-v6
  `pages` (ADR-043)…».
- Новый `PageStateDto`: `public string Id { get; set; } = string.Empty; public string? Name { get; set; }`.

### 3.3. Маппер `src/DesktopOrganizer.UI/State/PagesStateMapper.cs` (internal static, по образцу `PanelStateMapper`)

- `FromDto(PagesStateDto? dto) → (IReadOnlyList<PageDescriptor> Pages, string CurrentPageId)`:
  - `dto` или `dto.Items` `null` → `[("default", null)]`, current `"default"`.
  - `null`-элементы и элементы с пустым/whitespace `Id` — пропустить.
  - Дубли id (ordinal) — первый выигрывает.
  - `Name` whitespace → `null`. Для не-`default` страницы `Name == null` → `Name = Id` (лечение
    повреждённого/hand-edited файла; пользователь видит то же, что видел бы в v5).
  - Длину/дубли имён на загрузке **не** проверять и не обрезать (данные пользователя; валидация — только
    на rename).
  - Список пуст после фильтра → `[("default", null)]`. `"default"` отсутствует → вставить в начало
    (сейчас это делает `PageManagerService.EnsureDefaultPage` — оставить там, маппер не дублирует).
  - `CurrentPageId` blank → `"default"` (перенос текущей логики `ApplicationStateService.Load`).
- `ToDto(IReadOnlyList<PageDescriptor> pages, string currentPageId) → PagesStateDto`.

### 3.4. `IApplicationStateService` / `ApplicationStateService`

- `IReadOnlyList<string> PageIds` **заменить** на `IReadOnlyList<PageDescriptor> Pages` (один источник;
  xml-doc «всегда содержит хотя бы `default`» переносится). Обновить все места: `PageManagerService`,
  `PageManagerServiceTests`, `ApplicationStateServiceTests`, `SchemaContractTests`.
- `Load()`: строки 126–131 → `(Pages, CurrentPageId) = PagesStateMapper.FromDto(document.Pages)`.
  Защита от `null`/JsonException — существующая, без изменений.
- `PersistNow()`: строки 265–269 → `Pages = PagesStateMapper.ToDto(pageManager.PageDescriptors, pageManager.CurrentPage)`.
- Больше ничего в сервисе не трогать (барьеры записи, диагностика, scheduler).

---

## 4. `IPageManager` / `PageManagerService`

### 4.1. Контракт (добавления; существующее не ломаем)

```csharp
public interface IPageManager
{
    IReadOnlyList<string> Pages { get; }                        // без изменений: ordered ids
    IReadOnlyList<PageDescriptor> PageDescriptors { get; }      // NEW: ordered (id, name), same order as Pages
    string CurrentPage { get; }                                 // без изменений
    event Action<string> PageChanged;                           // без изменений: смена ТЕКУЩЕЙ страницы
    event Action? PagesChanged;                                 // NEW: состав/порядок/имена (add/remove/rename)

    void SwitchTo(string pageId);                               // без изменений
    void AddPage(string pageId);                                // без изменений по сигнатуре; name = id (legacy-семантика, для тестов/совместимости)
    string AddNewPage();                                        // NEW: GUID id + уникальное локализованное имя; возвращает id
    bool RemovePage(string pageId);                             // без изменений
    string? GetPageName(string pageId);                         // NEW: сохранённое имя (null = unnamed default / unknown)
    PageRenameResult ValidatePageName(string pageId, string? newName);   // NEW: чистая проверка без побочных эффектов
    PageRenameResult RenamePage(string pageId, string? newName);         // NEW
    void Save();                                                // без изменений
}
```

`PagesChanged` — задел под трей-подменю «Страницы» (ROADMAP: «список перестраивается при `PageChanged` и
при добавлении/удалении страниц»): одно событие на любое изменение списка, без аргументов — подписчик
перечитывает `PageDescriptors`. Поднимается **после** `Save()`. `PageChanged` при rename **не** поднимается.

### 4.2. Правила `RenamePage` (единый `PageNameRules`, `src/DesktopOrganizer.UI/Services/PageNameRules.cs`, internal static, чистые функции)

Порядок проверок (первый провал — результат):
1. `pageId` неизвестен → `NotFound`.
2. `trimmed = newName?.Trim() ?? ""`.
3. `trimmed` пуст: для `"default"` → сброс `Name = null` (`Renamed`, либо `Unchanged`, если уже `null`);
   для остальных → `Empty`.
4. `trimmed.Any(char.IsControl)` → `InvalidCharacters` (вставка многострочного текста из буфера).
5. `trimmed.Length > PageNameRules.MaxLength` (**40**, UTF-16 code units) → `TooLong`.
6. Совпадение (`StringComparer.OrdinalIgnoreCase`) с **эффективным отображаемым именем** любой *другой*
   страницы → `Duplicate`. Эффективное имя = `Name ?? (Id == "default" ? localized Page_Default_DisplayName (текущая культура UI через `ILocalizationService.GetString`) : Id)`.
   Сама переименовываемая страница исключается — смена регистра («work» → «Work») разрешена.
7. `trimmed` ordinal-равен текущему `Name` → `Unchanged` (без записи на диск, без события).
8. Иначе `Name = trimmed`, `Save()` (→ `SaveNow`, ADR-024 D-12), `PagesChanged`, `Renamed`.

`ValidatePageName` — шаги 1–7 без мутации (для live-хинта во время ввода, опционально в UI).
Эффективное имя — одна функция `PageNameRules.EffectiveName(PageDescriptor, string localizedDefault)`,
её же использует `PageDisplayNames.Resolve` в UI (§5.3), чтобы уникальность и отображение не разошлись.

### 4.3. `AddNewPage`

- `id = Guid.NewGuid().ToString("N")` (непрозрачный, никогда не показывается; не «Page N» — иначе имя и
  id снова сливаются, а переводимое имя оказывается в id).
- `name = _localization.Format("Page_NewName_Format", n)`, где `n` начинается с `Pages.Count + 1` и растёт,
  пока имя не уникально по правилу шага 6 (сейчас эта логика — в `MainWindowViewModel.AddPage` по id;
  переезжает в менеджер, где есть `ILocalizationService`).
- Локализуется **в момент создания** и далее — данные (ADR-032 D-5); это заодно закрывает CN-2
  ADR-032 («Page 2» в русском UI) для новых страниц.
- `Save()`, `PagesChanged`, вернуть id.

### 4.4. Прочее в `PageManagerService`

- Внутреннее хранилище: `List<string> _pages` → `List<PageDescriptor>` (или id-список + `Dictionary<string,string?>`
  имён — на усмотрение исполнителя; наружу — `Pages` (ids) и `PageDescriptors`).
- Конструктор читает `applicationStateService.Pages`; `EnsureDefaultPage` вставляет `("default", null)`.
- `AddPage(id)`/`RemovePage` поднимают `PagesChanged` при фактическом изменении.
- `RemovePage("default")` — по-прежнему запрещено (не меняется).

---

## 5. UI

### 5.1. Где переименовывать

- **Окно настроек → «Страницы»** (`Views/Settings/PagesSection.xaml`) — единственная точка ввода в этой
  задаче. Строка страницы: `TextBlock` (имя) ↔ `TextBox` (редактор), кнопки
  `[Переименовать] [Сделать текущей] [Удалить]`.
  - Вход: кнопка `Common_Rename` (ключ уже есть), двойной клик по имени, F2 на строке с фокусом.
  - Выход: Enter / потеря фокуса — commit; Esc — cancel (как `PanelTitleRenameBehavior`/`RenameEditBehavior`).
  - Ошибка: текст под строкой (`Settings.CardDescription` + предупреждающий цвет), редактор остаётся
    открытым с введённым текстом при Enter; при потере фокуса с ошибкой — редактирование отменяется,
    хинт показывается 4 с (как Move-хинт) — либо остаётся до следующего действия; выбрать одно и
    зафиксировать в ADR. Никогда `MessageBox`.
  - `TextBox.MaxLength = 40` (жёсткий предел ввода; `TooLong` остаётся защитой на уровне менеджера).
- **Трей** — не в этой задаче (WinForms `ContextMenuStrip` не даёт inline-ввода). Будущее подменю
  «Страницы» только показывает имена/переключает; переименование из трея, если понадобится, — через
  открытие окна настроек на разделе «Страницы».
- **Контекстное меню панели / рабочего стола** — не добавляем (в Fences имена страниц не
  редактируются с рабочего стола; достаточно одной точки).

### 5.2. ViewModel

`src/DesktopOrganizer.UI/ViewModels/PageItemViewModel.cs` (`ObservableObject`):
`Id` (readonly), `Name` (`string?`, INPC), `Display` (`PageDisplayInfo`, INPC при смене `Name`),
`IsEditing`, `EditingName`, `RenameError` (`PageRenameResult?`), `IsDefault`.

`MainWindowViewModel`:
- `ObservableCollection<string> Pages` → `ObservableCollection<PageItemViewModel> Pages`.
- `SyncPages()` перестраивает из `_pageManager.PageDescriptors`, **сохраняя** существующие экземпляры по
  `Id` (чтобы не рвать выделение `ComboBox`/фокус редактора); подписка на `_pageManager.PagesChanged` →
  `SyncPages()`. Из `AddPage()` убрать ручной `Pages.Add(newId)` (иначе двойное добавление), генерацию id
  заменить на `_pageManager.AddNewPage()`.
- Новые команды: `BeginRenamePageCommand(PageItemViewModel)`, `CommitRenamePageCommand`,
  `CancelRenamePageCommand` (одновременно редактируется не больше одной страницы; начало нового
  редактирования коммитит предыдущее — как `ChildWindowViewModel.BeginRename`).
- `SelectedPage` остаётся `string` (id); в XAML — `SelectedValuePath="Id"` + `SelectedValue`.
- `SwitchPageCommand`/`RemovePageCommand`/`MovePanelToPageCommand` продолжают принимать **id**
  (`CommandParameter="{Binding Id}"`).
- `Views/MainWindow.xaml.cs` `SwitchPage(bool)`: `pages.IndexOf(currentId)` → поиск индекса по `Id`.

Поведение редактора: новый attached-behavior `src/DesktopOrganizer.UI/Behaviors/InlineRenameBehavior.cs`
с DP `CommitCommand`/`CancelCommand` (Enter/Esc/LostKeyboardFocus + focus/SelectAll при появлении).
`RenameEditBehavior` (ADR-013) **не трогать** — он жёстко привязан к `ChildWindowViewModel`; перевод его на
новый behavior — возможный follow-up, не в этой задаче.

### 5.3. Отображение имени: `PageDisplayNameConverter` → работа по имени

- Новый чистый хелпер `src/DesktopOrganizer.UI/Localization/PageDisplayNames.cs`:
  `static string Resolve(string id, string? name, CultureInfo culture)` =
  `!IsNullOrWhiteSpace(name) ? name : id == "default" ? Page_Default_DisplayName(culture) : id`.
  Тот же хелпер пригодится будущему трей-адаптеру в UI-слое (Services не видит UI-типов — трей получит
  готовые пары `(id, displayName)` через колбэк; см. §7).
- `PageDisplayNameConverter.Convert`: принимает `PageDisplayInfo`/`PageDescriptor` → `Resolve(...)`;
  `string` → прежнее поведение (id; обратная совместимость); иначе `string.Empty`. xml-doc обновить
  (ссылка на ADR-043 вместо «ID показывается как есть»).
- Биндинги: `{loc:LocBinding Display, Converter={StaticResource PageDisplayName}}` (и с `FormatKey=` для
  `AutomationProperties.Name`). Путь `Display` — а не весь элемент — чтобы биндинг обновлялся по INPC при
  rename; `LocBinding` продолжает обновлять при смене культуры (unnamed `default` переключается с языком).
- Обновить комментарии в XAML «Page IDs are DATA…» → «Page names are DATA (ADR-043)…».

### 5.4. Затрагиваемые XAML

| Файл | Изменение |
|---|---|
| `Views/Settings/PagesSection.xaml` | шаблон строки: TextBlock↔TextBox, кнопка «Переименовать», хинт ошибки, `CommandParameter="{Binding Id}"`, `EqualityToBool` сравнивает `Id` с `CurrentPage`; описание карточки `Settings_Pages_Description` |
| `Views/Settings/PanelsSection.xaml` | `ComboBox`: `SelectedValuePath="Id"`, `SelectedValue="{Binding SelectedPage}"`, ItemTemplate на `Display`; меню «Переместить на страницу»: Header на `Display`, `CommandParameter="{Binding Id}"` |
| `Resources/Styles/Settings.xaml` | без изменений (ключ `PageDisplayName` остаётся) |

### 5.5. RESX (en `Strings.resx` / ru `Strings.ru.resx`, одинаковый набор ключей — `ResxConsistencyTests`)

| Ключ | Регион | en | ru |
|---|---|---|---|
| `Page_NewName_Format` | SHARED (`Page_`) | `Page {0}` | `Страница {0}` |
| `Page_Rename_Error_Empty` | WP-5 ViewModels | `Page name can't be empty.` | `Имя страницы не может быть пустым.` |
| `Page_Rename_Error_Duplicate` | WP-5 ViewModels | `A page with this name already exists.` | `Страница с таким именем уже есть.` |
| `Page_Rename_Error_TooLong_Format` | WP-5 ViewModels | `Page name must be {0} characters or fewer.` | `Имя страницы — не длиннее {0} символов.` |
| `Page_Rename_Error_InvalidCharacters` | WP-5 ViewModels | `Page name can't contain line breaks or control characters.` | `Имя страницы не может содержать переносы строк и управляющие символы.` |
| `Settings_Pages_Rename_Format` | Settings | `Rename “{0}”` | `Переименовать «{0}»` |
| `Settings_Pages_NameBox_AutomationName` | Settings | `Page name` | `Имя страницы` |
| `Settings_Pages_Description` | Settings | `Double-click a name or press F2 to rename a page.` | `Чтобы переименовать страницу, дважды щёлкните имя или нажмите F2.` |

Кнопка — существующий `Common_Rename`. Каждый новый ключ должен реально использоваться (иначе
`KeyUsageCoverageTests`/`XamlKeyCoverageTests` покраснеют). Тексты можно отполировать через
`design:ux-copy`, но ключи — фиксированы этим планом.

---

## 6. Диаграмма

```mermaid
graph TD
    subgraph Disk["State\\application-state.json"]
        V5["v5: pages.ids[] + currentPageId"] -->|V5ToV6Migration (JsonObject-only)| V6["v6: pages.items[{id,name?}] + currentPageId"]
    end
    V6 -->|ApplicationStateService.Load + PagesStateMapper.FromDto| ASS["IApplicationStateService.Pages : PageDescriptor[]"]
    ASS --> PM["PageManagerService: PageDescriptors, RenamePage, AddNewPage, PagesChanged"]
    PM -->|Save() = SaveNow| ASS2["ApplicationStateService.PersistNow → PagesStateMapper.ToDto"]
    ASS2 --> Disk
    PM -->|PagesChanged / PageChanged| MWVM["MainWindowViewModel.Pages : PageItemViewModel[]"]
    MWVM --> XAML["PagesSection (inline rename) / PanelsSection (ComboBox, Move-to-page)"]
    XAML -->|LocBinding Display| CONV["PageDisplayNameConverter → PageDisplayNames.Resolve"]
    PM -.->|future: колбэк (id, displayName)| TRAY["TrayIconManager (Services) — подменю «Страницы», round-robin (НЕ в этой задаче)"]
```

---

## 7. Задел под будущие пункты бэклога (не реализовывать)

- **Трей-подменю «Страницы»:** будет подписываться на `IPageManager.PageChanged` + `PagesChanged` и
  брать `PageDescriptors` → `PageDisplayNames.Resolve` в UI-слое (адаптер в `App`), а в
  `TrayIconManager` (Services) передавать примитивы `IReadOnlyList<(string Id, string DisplayName, bool IsCurrent)>`
  через существующий механизм колбэков — без зависимости Services → UI.
- **Round-robin по двойному клику:** использует упорядоченный `IPageManager.Pages` и `SwitchTo` — формат
  `items[]` сохраняет порядок; ничего дополнительного не нужно. Возможный хелпер `SwitchToNext()` —
  решение того пункта, не этого.

---

## 8. Тесты (tdd-test-engineer; писать до/параллельно реализации)

### 8.1. Миграция — `tests/.../Unit/Storage/Migrations/V5ToV6MigrationTests.cs`
- `Migrate_DefaultPage_HasNoNameKey` — `{"id":"default"}`, ключа `name` нет (не `null`).
- `Migrate_NonDefaultPages_NameEqualsId` — включая id с пробелами/кириллицей («Page 2», «Работа»).
- `Migrate_PreservesOrderAndCurrentPageId`.
- `Migrate_DropsIdsKey_WritesItems`.
- `Migrate_IsIdempotent_WhenItemsAlreadyPresent` — v6-shaped `pages` → тождество; повторный прогон
  результата → `JsonAssert.Equal`.
- `Migrate_ItemsAndIdsBothPresent_ItemsWins_IdsDropped`.
- `Migrate_SkipsNonStringAndNullIds`; `Migrate_DuplicateIds_PreservedVerbatim` (санитизация — не здесь).
- `Migrate_PagesMissing_StaysMissing`; `Migrate_PagesNotObject_Dropped`; `Migrate_IdsMissing_EmptyItems`.
- `Migrate_PanelsUnchanged_ByteForByte` — весь массив `panels`, включая `pageId`, v5-поля geometry/appearance.
- `Migrate_LayoutSnapshotsUnchanged_ByteForByte` — включая `panels[].pageId` внутри снапшотов.
- `Migrate_SystemIconsPanelAndUnknownTopLevelKeys_Preserved`.
- `Migrate_DoesNotMutateInput`.
- `FromVersion_Is5_ToVersion_Is6`.

### 8.2. Golden / контракт
- Новые input-фикстуры: `golden/v6-full.json` (с `default` без имени, переименованным `default`,
  страницей с GUID-id и кириллическим именем, `currentPageId` ≠ `default`). Существующие `v1..v5-full.json`
  **не редактировать** (README golden).
- Новый `expected-current-from-v5.json`; **регенерировать** `expected-current-from-v1..v4.json` на
  target v6 (разрешённое исключение ADR-025 §6 R-3); обновить таблицу в `golden/README.md`.
- `GoldenMigrationTests`: runner + `V5ToV6Migration`; счётчики `applied` +1 (5/4/3/2/1);
  `V5Full_MigratesToCurrent_MatchesExpected` (1 миграция); `V6Full_AlreadyAtCurrentSchema_IsIdentity`;
  `V6Full_MigratedTwice_IsByteForByteIdentical` (перенос v5-варианта).
- `SchemaContractTests`: runner +V5ToV6; `BuildFullyPopulatedDocument` → `Items = [new() { Id = "work", Name = "Work" }]`
  (непустое `name`, иначе `WhenWritingNull` уберёт путь `pages.items[].name` только с DTO-стороны);
  **важно:** v1-фикстура содержит страницу `work` → после миграции путь `pages.items[].name` есть и на
  стороне миграции — проверить, что сравнение путей зелёное; новый
  `V5ToV6Migration_NeverProducesAReservedEnvelopeKey`.
- `StateMigrationRunnerTests` — если там перечислены все миграции/версии, добавить V5ToV6.

### 8.3. DTO / маппер / сервис состояния
- `PagesStateMapperTests`: round-trip `ToDto`→JSON(`StateJson.Options`)→`FromDto`; `default` без имени
  не пишет `name`; `null`-items → default; пустые id пропущены; дубли id — первый выигрывает; non-default без
  имени → `Name = Id`; whitespace-имя → `null`/`Id`; blank `currentPageId` → `default`; длинное имя на
  загрузке **не** обрезается.
- `ApplicationStateServiceTests`: `Load` v6-payload → `Pages`/`CurrentPageId`; `PersistNow` пишет
  `pages.items` (через мок `IApplicationStateStore.Save`, проверка JSON); `{"pages":{"items":null}}` не
  падает; `{"pages":{"items":[null]}}` не падает.

### 8.4. `PageManagerService` (`PageManagerServiceTests`, мок `IApplicationStateService.Pages`)
- Rename: `Renamed` + `Name` обновлён + `SaveNow` вызван ровно 1 раз + `PagesChanged` поднят, `PageChanged` — нет.
- `Unchanged` (то же имя) → без `SaveNow`, без события. Смена регистра → `Renamed`.
- `Empty`/whitespace для не-default; для `default` → сброс к `null` (`Renamed`), повторно → `Unchanged`.
- `TooLong` (41 символ) / ровно 40 → `Renamed`; trim перед проверкой длины.
- `InvalidCharacters` (`"a\nb"`, `"\t"`).
- `Duplicate`: против имени другой страницы без учёта регистра (включая кириллицу «Работа»/«РАБОТА»);
  против локализованного имени unnamed `default` («Main»/«Основная» в соответствующей культуре
  `TestLocalization`); self-exclusion.
- `NotFound`.
- `ValidatePageName` не мутирует и не сохраняет.
- `AddNewPage`: GUID-id (32 hex), имя из `Page_NewName_Format`, уникальность при коллизии с
  пользовательским именем «Page 2», `PagesChanged`, `SaveNow`.
- `RemovePage`/`AddPage` поднимают `PagesChanged` только при фактическом изменении.
- Конструктор: `default` вставляется, если отсутствует в `Pages`.

### 8.5. UI-логика (без реального окна)
- `PageDisplayNameConverterTests`: `PageDisplayInfo("default", null)` → локализованное (en/ru);
  `("default","Дом")` → «Дом»; `("x","Work")` → «Work»; `("x",null)` → «x»; строка-id — прежнее поведение
  (существующие тесты остаются зелёными); прочее → `""`.
- `PageDisplayNamesTests` (`Resolve`) — те же случаи, плюс согласованность с `PageNameRules.EffectiveName`.
- `MainWindowViewModelTests` (если есть; иначе новые, STA по `docs/Refactoring/Testing/sta-test-conventions.md`):
  `SyncPages` сохраняет экземпляры по `Id`; `AddPage` не дублирует элемент при `PagesChanged`;
  Begin/Commit/Cancel rename; ошибка → `RenameError` установлен, редактор открыт; Esc восстанавливает имя.
- Локализация: `ResxConsistencyTests`/`KeyUsageCoverageTests`/`XamlKeyCoverageTests` зелёные с новыми ключами.

---

## 9. Риски

| # | Риск | Натив/вне CI? | Митигация |
|---|---|---|---|
| R-1 | Откат 0.8.0 → 0.7.0 показывает пустой рабочий стол (rung 7) | нет | Документировать (§2.4); восстановление — переустановить 0.8.0 или вручную `premigration-v5.json` |
| R-2 | Устаревший `application-state.premigration-v5.json` от инцидента 2026-09-24 блокирует запись свежего premigration-бэкапа (`File.Exists` → skip) | проверяется только на реальной машине | Q-1 (§12), по умолчанию вариант A: шаг M-0 manual verify — переименовать старый файл до первого запуска 0.8.0 |
| R-3 | Остатки v6-документа экспериментальной ветки (другой формы) в `State\` (`.bak`, карантин) будут прочитаны 0.8.0 как «свои» v6 | вне CI | M-0: убедиться, что `application-state.json`/`.bak` имеют `schemaVersion: 5` перед апгрейдом; неправильный тип поля → существующий путь `StatePayloadDeserializationFailed` → карантин, не падение |
| R-4 | `SchemaContractTests` краснеет из-за `WhenWritingNull` и отсутствующего `name` у `default` | нет | §8.2: DTO-сторона с непустым именем; v1-фикстура содержит `work` |
| R-5 | Смена `Pages` на `ObservableCollection<PageItemViewModel>` ломает биндинги (`SelectedItem`, `CommandParameter`, `EqualityToBool`, Ctrl+PgUp) | частично (визуально) | Таблица §5.4; поиск всех `Binding Pages`/`CommandParameter="{Binding}"`; manual verify M-4..M-6 |
| R-6 | Двойное добавление страницы (ручной `Pages.Add` + `PagesChanged`) | нет | §5.2 — убрать ручное добавление; тест §8.5 |
| R-7 | Уникальность против локализованного имени `default` зависит от культуры (переименовали стр. в «Main» в ru-UI, затем переключили на en → два «Main») | нет | Осознанное ограничение (как ADR-032 D-5 «данные на языке создания»); зафиксировать в ADR-043 |
| R-8 | Параллельный пункт бэклога берёт ADR-043 / правит те же RESX-регионы | нет | Номер ADR — «следующий свободный»; RESX — добавлять ключи в конец регионов, конфликты мёржа тривиальны |

---

## 10. Definition of Done

1. `dotnet build` всего solution: **0 errors, 0 warnings** (CS/CA/SA/nullable/`linter.ruleset`), без
   `#pragma`/`SuppressMessage`/`NoWarn`/правок ruleset.
2. `dotnet test`: все тесты зелёные, 0 warnings; добавлены тесты §8 (миграция ≥ 15 кейсов, golden v5→v6
   и v6-identity, маппер, менеджер, конвертер).
3. `StateEnvelope.CurrentSchemaVersion == 6`; `V5ToV6Migration` зарегистрирована в `DiConfig`; цепочка
   v1→v6 проходит golden-тесты; `v1..v5-full.json` не изменены; `expected-current-from-v1..v5.json` — target v6.
4. `JsonApplicationStateStore`, `StateStorePaths`, `StateMigrationRunner`, `StateRestoreCommand`,
   `LegacySettingsImporter`, `IAppSettings`/`AppSettings`/`Settings.settings`, `UserConfigFolderMigration`,
   `LayoutSnapshotService` — **без диффа** (проверяется `git diff --stat` ревьюером).
5. Переименование работает из «Страниц» (кнопка/двойной клик/F2; Enter/Esc/потеря фокуса); все места
   показа (§5.4) показывают имя; unnamed `default` переключается с языком; ошибки валидации — inline.
6. Новые страницы: GUID-id, локализованное уникальное имя.
7. Документация:
   - **ADR-043** «Page display names, schema v6» (Context — ROADMAP-пункт и решение пользователя 2026-09-29;
     Decision — §2/§4/§5; Alternatives — таблица §2.1; Consequences — откат §2.4, R-2, R-7).
   - **Amendment ADR-024** «2026-09-29 (ADR-043): schema v6» — v5→v6 без потерь, rung 7 без изменений,
     premigration именуется по исходной версии (`premigration-v5.json`), контур store не менялся.
   - **Amendment ADR-032** — D-5/CN-2: имена страниц теперь отдельное поле-данные; `"Page N"` для новых
     страниц локализуется в момент создания.
   - `adr/README.md` — строка ADR-043.
   - `docs/Refactoring/Storage/rollback.md` — сценарий v6 → v5.
   - `docs/ROADMAP.md` — пункт `[x]` со ссылкой на ADR-043; в пунктах про трей упомянуть `PagesChanged`/`PageDescriptors`.
   - `CHANGELOG.md` — Added (переименование страниц) + Upgrade notes (схема v6, откат, premigration-файл).
   - `README.md` (en/ru) — абзац о страницах/переименовании и строка про откат.
   - `CLAUDE.md` — строка раздела «Настройки и persistence — ЗАМОРОЖЕНЫ»: `schemaVersion = 6` (с
     упоминанием ADR-043) — правка документации, а не контура.
8. Manual verify на Win11 вынесен отдельным блоком (§11) — **не блокер** закрытия.

---

## 11. Manual verify (Windows 11 24H2) — отдельным блоком, не блокер

Сначала — на Debug-корне `%LOCALAPPDATA%\DesktopOrganizer.Dev` (копия рабочего `State\`), потом Release.

- **M-0 (до первого запуска 0.8.0):** в `%LOCALAPPDATA%\DesktopOrganizer\State\` проверить
  `application-state.json` и `.bak` → `"schemaVersion": 5`. Если существует
  `application-state.premigration-v5.json` от 2026-09-24 — переименовать (например,
  `application-state.premigration-v5.incident-2026-09-24.json`), чтобы 0.8.0 записала свежий (R-2).
- M-1. Первый запуск 0.8.0: все панели/страницы на месте; `application-state.json` → `schemaVersion: 6`,
  `pages.items`; создан свежий `application-state.premigration-v5.json`.
- M-2. «Страницы»: переименование кнопкой, двойным кликом, F2; Enter/Esc/клик мимо; ошибки (пусто,
  дубль в другом регистре, 41 символ, вставка многострочного текста) — inline, без MessageBox.
- M-3. `default`: без имени переключается при смене языка en↔ru; после переименования — нет; пустой ввод
  возвращает локализованное имя.
- M-4. «Панели»: селектор страницы показывает имена, выбор переключает страницу; «Переместить на
  страницу» показывает имена и работает.
- M-5. Ctrl+PgUp/PgDn в окне настроек — порядок и переключение как раньше.
- M-6. Добавить страницу в ru-UI → «Страница N»; удалить переименованную страницу — подтверждение и
  Корзина как раньше (ADR-035).
- M-7. Перезапуск — имена сохранены; снапшот, снятый до переименования, восстанавливается на ту же страницу.
- M-8. Откат (опционально, на Dev-корне): запуск 0.7.0 → пустая read-only сессия, файл не перезаписан;
  снова 0.8.0 → всё на месте.
- M-9. Экранный диктор/UIA: `AutomationProperties.Name` кнопок «Переименовать/Сделать текущей/Удалить»
  содержит имя страницы.

---

## 12. Вопрос оркестратору (не блокирует старт WP-1…WP-3)

**Q-1. Premigration-бэкап при уже существующем `premigration-v5.json` (R-2).**
- **A (по умолчанию, рекомендовано):** код store не трогать; риск закрывается ручным шагом M-0 и
  заметкой в CHANGELOG/Upgrade notes. Полностью в рамках разрешения пользователя.
- **B:** буквально по брифу — именовать бэкап v5→v6 как `premigration-v6.json` (по целевой версии).
  Требует изменить `StateStorePaths.PreMigrationBackupPath`/вызов в `LoadCore` — это семантика store, за
  рамками выданного разрешения; ломает единообразие с уже существующими `premigration-v4.json`.
- **C:** если детерминированный файл существует, писать рядом timestamp-вариант
  (`premigration-v5-{yyyyMMdd-HHmmss}.json`) вместо пропуска. Самое надёжное для всех будущих миграций,
  но это изменение `WritePreMigrationBackupIfMissing` — нужно отдельное явное «да» пользователя.

Реализующие агенты работают по варианту A, пока оркестратор/пользователь не выберет B или C.

---

## 13. Разбивка по агентам и порядок

| Пакет | Агент | Содержание | Зависит от |
|---|---|---|---|
| **WP-1 Persistence** | `dotnet-services-engineer` | `V5ToV6Migration`, `CurrentSchemaVersion = 6`, `DiConfig`; `PagesStateDto`/`PageStateDto`; `PagesStateMapper`; `IApplicationStateService.Pages` (замена `PageIds`), `ApplicationStateService.Load/PersistNow`; golden-фикстуры (`v6-full.json`, `expected-current-from-v5.json`, регенерация `v1..v4`, README); адаптация `GoldenMigrationTests`/`SchemaContractTests` | — |
| **WP-2 PageManager** | `dotnet-services-engineer` (тот же запуск, после WP-1) | `PageDescriptor`, `PageRenameResult`, `PageNameRules`, `IPageManager`/`PageManagerService` (§4), `PagesChanged`, `AddNewPage`; RESX-ключи `Page_NewName_Format`, `Page_Rename_Error_*` | WP-1 (`IApplicationStateService.Pages`) |
| **WP-3 UI** | `wpf-ui-builder` | `PageItemViewModel`, `PageDisplayInfo`, `PageDisplayNames`, `PageDisplayNameConverter`, `InlineRenameBehavior`, `MainWindowViewModel` (§5.2), `MainWindow.xaml.cs`, `PagesSection.xaml`, `PanelsSection.xaml`, RESX `Settings_Pages_*` | контракт §4.1 (может стартовать параллельно WP-1/2, кодируя против сигнатур §4.1; сборка зелёная — после слияния WP-2) |
| **Тесты** | `tdd-test-engineer` | §8.1–8.2 — сразу, параллельно WP-3 (не зависят от UI); §8.3–8.5 — после WP-2/WP-3 | WP-1 для §8.1–8.2 |
| Сборка/красное | `debug-healer` | только если build/test красные или warnings ≠ 0 | — |
| **Ревью** | `code-quality-reviewer` ∥ `fences-ux-parity-reviewer` | один вызов, параллельно; ревьюер качества дополнительно проверяет DoD-4 (нет диффа в замороженных файлах) | всё выше |
| **Документация** | `architecture-documenter` | DoD-7 (ADR-043, amendments ADR-024/ADR-032, README ADR, rollback.md, ROADMAP, CHANGELOG, README, CLAUDE.md) | ревью зелёное, warnings = 0 |

Параллельно можно: **(WP-1→WP-2) ∥ WP-3 ∥ tdd §8.1–8.2**. Рекомендуемый безопасный порядок при
ограниченном числе агентов: `dotnet-services-engineer` (WP-1+WP-2) → `wpf-ui-builder` (WP-3) ∥
`tdd-test-engineer` → ревьюеры (параллельно) → `architecture-documenter`. Win32-interop не требуется.
