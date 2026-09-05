# План: Copy → Move при drag&drop в панель

Статус: DONE (реализовано, протестировано, дважды провалидировано ревью, документация актуализирована 2026-07-07)
Scope: **L** (Services.Abstractions + Services/Tools + Core-модель + UI/ViewModels + UI/Views + persistence-схема + тесты)
Автор: fences-feature-planner
Дата: 2026-07-07
Источник: запрос пользователя (эволюция уже реализованного Copy-фикса, ROADMAP Фаза 1, пункт «Копирование при drag&drop»)
Итог: см. [docs/ROADMAP.md](../ROADMAP.md) (Фаза 1, пункт «Move при drag&drop»),
[ADR-009](../../adr/ADR-009-drag-drop-move-semantics.md) — архитектурные решения (ADR-A..D из этого
плана перенесены/раскрыты в ADR-009 как Решения 2–6, плюс новое Решение 1 про managed-copy vs
in-place repositioning).

---

## 1. Цель и суть изменения

Уже реализованный **Copy**-режим (drop файла/папки в панель копирует оригинал в приватное
хранилище `%LocalAppData%\DesktopOrganizer\PanelItems`) эволюционируем в **Move**-семантику:

- drop **перемещает** оригинал (не оставляет копию на десктопе) — курсор `DragDropEffects.Move`;
- «Убрать из панели» для managed-элемента **возвращает файл на исходное место** (а не удаляет);
- cross-volume перемещение — через copy-then-delete-after-verified-success с инвариантом
  «оригинал не удаляется, пока копия не подтверждена полной».

**НЕ трогаем:** логику Copy с нуля (эволюционируем её), Folder Portals (`SourceFolderPath`),
`AddFiles`/`LoadFromModel`, поллинг-таймер существования (`FileItem.Refresh`), любой P/Invoke.

---

## 2. Архитектурное решение по открытым вопросам (ADR-стиль)

### ADR-A: Где хранить original path для restore

**Решение:** вариант **(b) — manifest внутри `PanelItemStorageService`**, персистентный между
запусками, сопоставляющий `managed path → original path`. Персистентный JSON-manifest
(`%LocalAppData%\DesktopOrganizer\PanelItems\.manifest.json`), записывается атомарно
(write-temp + `File.Replace`/`File.Move`).

**Почему (b), а не (a):**

- **Инкапсуляция слоёв.** Original path — это деталь механизма Move, а не свойство файла для UI.
  `FileItem` (Core) намеренно тонкий и UI-ориентированный (`Path`, `Icon`, `Exists`, `IsDirectory`,
  `IsShortcut`). Добавлять в него `OriginalPath` — протечь механику storage в доменную модель,
  которую видят и Folder Portals, и `LoadFromModel`, где это поле не имеет смысла.
- **Персистентная схема остаётся стабильной.** Вариант (a) требует ломать
  `ChildWindowModel.FilePaths : List<string>` → `List<PanelFileEntry>`, а это меняет формат
  сериализации settings, требует миграции и трогает `SyncToModel`/`GetSourceFiles`/`LoadFromModel`
  (все места, которые CLAUDE.md просит держать в стороне). Manifest живёт целиком внутри Services
  и не касается persistence-схемы панелей.
- **Единая точка владения.** `PanelItemStorageService` уже единственный владелец storage root и
  единственный, кто знает, что такое managed path. Manifest логически принадлежит ему.

**Trade-off:** manifest — отдельный источник состояния, который может рассинхронизироваться со
storage (managed-файл есть, записи в manifest нет, или наоборот). Обрабатываем деградацией
(см. ADR-B): нет записи → restore недоступен, деградируем до старого release/no-op.

**Интерфейс** (`IPanelItemStorageService`) — механику manifest наружу не раскрываем, только
поведение через новые методы (см. раздел 3).

### ADR-B: Обратная совместимость (панели, сохранённые до Move)

Для старых managed-копий (импортированных Copy-режимом до этого изменения) записи original path в
manifest нет. Для legacy-элементов, ссылающихся прямо на user-файл (`IsManaged == false`), тоже нет.

**Решение:** `Restore(storedPath)` возвращает результат-код (см. `RestoreOutcome` ниже). Если
original path неизвестен (нет записи в manifest) → возвращает `OriginalPathUnknown`, **файл не
трогается**. Вызывающая сторона (`RemoveFileItem`) в этом случае деградирует до старого поведения:
`Release(item.Path)` (managed → удалить копию, как было для Copy) либо no-op для не-managed.
Никаких падений; поведение для старых панелей идентично сегодняшнему.

### ADR-C: Коллизия имён при restore

Если на исходном месте уже лежит другой файл/папка с тем же именем — **не перезаписывать**.
Дать отличающееся имя по паттерну Explorer: `name (2).ext`, `name (3).ext`, … (для папок —
`name (2)`). Первый свободный индекс. Реализуется в Services (`PanelItemStorageService`),
не в UI. Move в найденное свободное имя, запись в manifest снимается только после успеха.

### ADR-D: Исходная директория не существует при restore

**Решение:** **создать недостающие родительские директории** (`Directory.CreateDirectory` по
`Path.GetDirectoryName(originalPath)`) и восстановить туда. Это предсказуемо (файл возвращается
именно откуда пришёл) и не требует знания Desktop-root в слое Services. Fallback на Desktop
**отклонён** — Services.Tools не должен знать про Desktop как понятие (это UI/shell-концепт), и
«файл появился не там, откуда я его брал» хуже для пользователя, чем воссозданная папка.
Если и создание родителя падает (нет прав, диск) → `Restore` возвращает `Failed`, оригинал
managed-копии **остаётся на месте** (не теряем файл), лог ошибки; вызывающая сторона оставляет
элемент в панели.

---

## 3. Изменения по слоям

### 3.1 Services.Abstractions — `IPanelItemStorageService.cs`

Добавить (Import/ImportDirectory/IsManaged **сохранить**; поведение Import меняем — см. ниже):

```csharp
/// Move-семантика: перемещает файл в managed storage. Same-volume → File.Move;
/// cross-volume → copy-then-delete-after-verified-success. При любом сбое оригинал НЕ удаляется,
/// частичная копия откатывается, возвращается null (caller фолбэчит на ссылку). Записывает
/// original path в manifest для последующего Restore.
string? MoveIn(string sourcePath);

/// Move-семантика для папки. Same-volume → Directory.Move (без safety-cap). Cross-volume →
/// рекурсивный copy (safety-cap 2 ГиБ / 20000 файлов ТОЛЬКО здесь), затем delete оригинала
/// после подтверждённого успеха. Сбой на середине — оригинал цел, частичная копия удалена, null.
string? MoveInDirectory(string sourcePath);

/// Возвращает managed-элемент на исходное место (см. ADR-C/ADR-D). Снимает запись из manifest
/// после успеха. Не удаляет оригинал managed-копии, если restore не удался.
RestoreOutcome Restore(string storedPath);
```

`RestoreOutcome` (enum, Services.Abstractions):
`Restored` · `OriginalPathUnknown` (нет записи → caller деградирует до Release/no-op) ·
`NotManaged` (path вне storage → no-op) · `Failed` (сбой ФС → элемент остаётся).

**Решение по Import/ImportDirectory:** переименовать семантику, но не ломать вызывающих без
надобности. Вариант — оставить `Import`/`ImportDirectory` как есть (Copy) **не нужен**: единственный
вызывающий — `AddDroppedFiles`. Заменяем вызовы на `MoveIn`/`MoveInDirectory`, а старые
`Import`/`ImportDirectory` **удаляем** из интерфейса и реализации (мёртвый Copy-код после эволюции).
`Release` и `IsManaged` — **оставить** (Release нужен для ADR-B деградации legacy и для будущей
очистки осиротевших копий).

> Executor-заметка: если `Import`/`ImportDirectory` окажутся нужны где-то ещё (проверить Grep
> перед удалением) — оставить, но пометить `[Obsolete]`. На момент планирования единственный
> потребитель — `AddDroppedFiles`.

### 3.2 Services/Tools — `PanelItemStorageService.cs`

- **`MoveIn(sourcePath)`:**
  - валидации как в `Import` (не пусто, не директория, файл существует);
  - определить тома: `SameVolume(src, storageRoot)` = `string.Equals(Path.GetPathRoot(fullSrc),
    Path.GetPathRoot(fullStorageRoot), OrdinalIgnoreCase)`;
  - целевое имя — существующая GUID-суффикс схема (коллизии в storage исключены);
  - **same-volume:** `Directory.CreateDirectory(root)` + `File.Move(src, dest)` (атомарно, без cap);
  - **cross-volume:** `File.Copy(src, dest, overwrite:false)` → проверить успех (dest существует;
    опц. сверить размер `new FileInfo(dest).Length == new FileInfo(src).Length`) → только затем
    `File.Delete(src)`. Если copy кинул/размер не сошёлся → `TryDeletePartialCopy(dest)`, оригинал
    не трогать, вернуть null;
  - при успехе — `WriteManifestEntry(dest, src)`; вернуть dest.
- **`MoveInDirectory(sourcePath)`:**
  - валидации как в `ImportDirectory`;
  - **same-volume:** `Directory.Move(src, destRoot)` напрямую — **safety-cap НЕ применять**
    (atomic rename, ничего не копируется);
  - **cross-volume:** существующая логика safety-cap (enumerate → count > 20000 → null;
    сумма размеров > 2 ГиБ → null) → рекурсивный `File.Copy` (текущий код) → **после полного
    успеха копии** `Directory.Delete(src, recursive:true)`. Сбой копии → `TryDeletePartialCopy`,
    оригинал цел, null;
  - при успехе — `WriteManifestEntry(destRoot, src)`; вернуть destRoot.
- **`Restore(storedPath)`:**
  - `if (!IsManaged) return NotManaged;`
  - `original = ReadManifestEntry(storedPath); if (original is null) return OriginalPathUnknown;`
  - `targetDir = Path.GetDirectoryName(original)` → `Directory.CreateDirectory(targetDir)`
    (ADR-D; если падает → лог, `Failed`);
  - разрешить коллизию имени: `finalTarget = ResolveCollisionFreeName(original)` (ADR-C:
    `name (2).ext` …);
  - `File.Move`/`Directory.Move(storedPath, finalTarget)`;
    - если storedPath и finalTarget на разных томах — Move всё равно работает для файла;
      для директории cross-volume `Directory.Move` бросит `IOException` → в этом случае
      copy-then-delete (переиспользовать внутренний cross-volume копир). (Практически storage и
      original обычно на разных томах — предусмотреть это.)
  - при успехе — `RemoveManifestEntry(storedPath); return Restored;`
  - любой сбой ФС → лог, managed-копия остаётся, `return Failed;`
- **Manifest-механика (private):** `ReadManifest()`/`WriteManifest()` — `Dictionary<string,string>`
  (managed fullpath → original fullpath, ключи `OrdinalIgnoreCase`), сериализация
  `System.Text.Json`, атомарная запись (temp + `File.Replace`). Manifest-файл сам исключить из
  `IsManaged`-удаления и из enumerations. Загрузка ленивая/кешируемая; потокобезопасность — `lock`
  (сервис — singleton, drop и restore идут с UI-потока, но таймер существования — с пула; manifest
  таймер не трогает, однако lock дёшев и страхует).
- **`Release`** — оставить без изменений (удаление managed-копии + guard IsManaged).
- **`internal`-конструктор с `storageRoot`** — сохранить; manifest-путь выводить из `storageRoot`,
  чтобы тесты с temp-root работали изолированно.

### 3.3 Core — `FileItem.cs`

**Изменений НЕ требуется** (следствие выбора ADR-A/вариант b). Явно фиксируем: `FileItem` остаётся
тонким; original path в модель не протекает.

### 3.4 UI/ViewModels — `ChildWindowViewModel.cs`

- **`AddDroppedFiles`:** заменить `_itemStorage.Import` → `_itemStorage.MoveIn`,
  `_itemStorage.ImportDirectory` → `_itemStorage.MoveInDirectory`. Fallback на оригинальный path
  при `null` — **сохранить** (referencing-поведение при сбое/сверх-cap). Обновить XML-doc:
  «перемещает» вместо «копирует».
- **`RemoveFileItem`:** заменить прямой `_itemStorage.Release(item.Path)` на:
  ```
  var outcome = _itemStorage.Restore(item.Path);
  if (outcome is RestoreOutcome.OriginalPathUnknown)
      _itemStorage.Release(item.Path); // ADR-B: legacy managed-копия — старое поведение
  // NotManaged → Restore уже no-op; Failed → лог внутри сервиса, элемент всё равно убран из панели
  ```
  Порядок: сперва `RemoveFile(item)` (из UI-списка), затем restore/release, затем
  `FilesChanged`. (Как сейчас.)
  > Открытый микро-вопрос для executor: при `Failed` элемент уже убран из UI, но файл ещё в
  > storage — осиротеет. Приемлемо (лог есть); не откатывать удаление из UI, чтобы не усложнять.
  > Зафиксировать в тесте как ожидаемое.
- Портальную ветку и `AddFiles`/`LoadFromModel` — не трогать.

### 3.5 UI/Views — `ChildWindow.xaml.cs`

- **`OnDragEnter`:** managed-drop ветка `DragDropEffects.Copy` → `DragDropEffects.Move`
  (портал по-прежнему `None`).
- **`OnDrop`:** курсорный эффект — `Move`. Если WPF требует явно, выставить
  `e.Effects = DragDropEffects.Move` в начале (после проверки портала). Логика вызова
  `AddDroppedFiles` не меняется.
- **One-time хинт (ADR — оценка на усмотрение):** РЕКОМЕНДОВАНО реализовать **ненавязчивый**
  one-time хинт при первом Move-drop (например, короткий тултип/тост «Файл перемещён в панель;
  “Убрать из панели” вернёт его на место»), флаг «показан» — в `IAppSettings`
  (`HasShownMoveHint`, дефолт false). НЕ блокирующий, НЕ на каждый drop, без модалки. Если это
  раздувает scope — вынести в отдельный OPTIONAL под-пункт (см. раздел 6). Штатный `Move`-курсор
  Windows — обязательный минимум, хинт — плюс.

### 3.6 Persistence

Схема `ChildWindowModel.FilePaths : List<string>` **не меняется** (следствие ADR-A/b). Manifest
живёт в storage root, вне settings. Обратная совместимость settings — полная.

---

## 4. Порядок работ и зависимости

```
[1] Abstractions: MoveIn/MoveInDirectory/Restore + RestoreOutcome, удалить Import/ImportDirectory
      (dotnet-services-engineer)
        ↓ (блокирует всё)
[2] Services: реализация MoveIn/MoveInDirectory/Restore + manifest + collision-resolve + cross-volume
      (dotnet-services-engineer)
        ↓
[3] UI/ViewModels: AddDroppedFiles → MoveIn*, RemoveFileItem → Restore/деградация
      (wpf-ui-builder ∥ можно dotnet-services-engineer — это VM-логика без XAML)
[3b] UI/Views: OnDragEnter/OnDrop → Move-курсор; (опц.) one-time хинт + IAppSettings.HasShownMoveHint
      (wpf-ui-builder)   [3 и 3b независимы между собой, оба зависят от 1]
        ↓
[4] Тесты (см. раздел 5) — писать по TDD параллельно с [2]/[3] где возможно
      (tdd-test-engineer)
        ↓
[5] ROADMAP актуализация под Move-семантику (architecture-documenter)
```

Параллелизуемо: [3] и [3b] после [1]; тесты сервиса [4] — вместе с [2].

---

## 5. Definition of Done (исполнимые критерии + тесты)

Каждый пункт DoD → минимум один тест. Цена ошибки — реальная потеря файлов пользователя, поэтому
покрытие негативных путей обязательно.

- [x] **D1. Same-volume move файла.** `MoveIn`: оригинал исчез с исходного места, копия в storage,
      контент идентичен. Тест: `MoveIn_SameVolume_RemovesSourceAndCreatesManaged`.
- [x] **D2. Same-volume move папки.** `MoveInDirectory`: `Directory.Move`, оригинал исчез,
      структура сохранена, **safety-cap НЕ применялся** (тест на большом кол-ве файлов same-volume
      всё равно перемещает). Тест: `MoveInDirectory_SameVolume_MovesAndIgnoresCap`.
      > Замечание для tdd-test-engineer: cross-volume в CI не воспроизвести детерминированно (нужен
      > 2-й том). Cross-volume путь тестировать через выделение внутреннего copy-then-delete в
      > тестируемый метод/перегрузку с флагом `forceCrossVolume`, либо через `internal`-хук. Явно
      > пометить как «логику проверяем юнитом с форсом; реальный cross-volume — manual verify».
- [x] **D3. Cross-volume = copy-then-delete-after-verified-success.** Форсированный cross-volume:
      копия создана и подтверждена → оригинал удалён. Тест:
      `MoveIn_CrossVolume_CopiesThenDeletesOriginalAfterSuccess`.
- [x] **D4. Откат при сбое копирования (файл).** Симулировать сбой копии cross-volume (например,
      dest недоступен) → **оригинал цел**, частичная копия удалена, возврат null. Тест:
      `MoveIn_CrossVolumeCopyFails_OriginalSurvivesAndReturnsNull`.
- [x] **D5. Откат при сбое копирования (папка).** Обрыв рекурсивной копии cross-volume →
      **оригинал папки цел**, частичная managed-копия удалена, null. Тест:
      `MoveInDirectory_CrossVolumeCopyFails_OriginalSurvives`.
- [x] **D6. Restore на исходное место.** `Restore` managed-файла → файл вернулся точно на
      originalPath, managed-копия исчезла, запись manifest снята, `Restored`. Тест:
      `Restore_ManagedFile_ReturnsToOriginalLocation`.
- [x] **D7. Restore — коллизия имён.** На исходном месте уже есть файл с тем же именем → restore в
      `name (2).ext`, существующий не перезаписан. Тест:
      `Restore_TargetNameTaken_UsesCollisionFreeName`.
- [x] **D8. Restore — отсутствующая исходная директория.** Родитель originalPath удалён → директория
      воссоздаётся, файл восстановлен туда. Тест: `Restore_MissingParentDir_RecreatesAndRestores`.
- [x] **D9. Restore — original path неизвестен (legacy/ADR-B).** managed-копия без записи в manifest
      → `OriginalPathUnknown`, файл не тронут. Тест: `Restore_NoManifestEntry_ReturnsUnknownAndKeepsFile`.
      + VM-тест: `RemoveFileItem` при `OriginalPathUnknown` вызывает `Release` (деградация).
- [x] **D10. Restore — path вне storage.** Не-managed path → `NotManaged`, no-op. Тест:
      `Restore_PathOutsideStorage_NoOp`.
- [x] **D11. Restore — сбой ФС.** Move в целевое место падает → `Failed`, managed-копия остаётся
      (не потеряна). Тест: `Restore_MoveFails_KeepsManagedCopy`.
- [~] **D12. UI-эффект Move.** `OnDragEnter`/`OnDrop` для не-портальной панели ставят
      `DragDropEffects.Move`; портал — `None`. Реализовано в коде (`ChildWindow.xaml.cs`); нет
      юнит-теста (WPF drag&drop events не мокаются дёшево) — как и предусматривал план, остаётся
      **manual verify (Win10/Win11)**, см. ROADMAP «Сквозные задачи».
- [x] **D13. AddDroppedFiles fallback.** При `MoveIn`/`MoveInDirectory` == null элемент добавляется
      по оригинальному пути (referencing). VM-тест на fallback (мок storage возвращает null).
- [x] **D14. Manifest атомарность/выживаемость.** После `MoveIn` + перезапуск (пересоздание сервиса
      на том же root) `Restore` всё ещё находит original path. Тест:
      `Manifest_PersistsAcrossServiceInstances`.
- [x] **D15. ROADMAP** (пункт «Копирование при drag&drop») актуализирован: Copy→Move, restore-семантика
      «Убрать из панели», cross-volume инвариант безопасности, что safety-cap только cross-volume.
      Сделано 2026-07-07 — см. [docs/ROADMAP.md](../ROADMAP.md) Фаза 1 и
      [ADR-009](../../adr/ADR-009-drag-drop-move-semantics.md).

Известный тестовый пробел вне исходного списка D1–D15 (обнаружен во время реализации, не блокирует
DoD): cross-volume fallback внутри `Restore` (copy-then-delete при `IOException` от `File.Move`/
`Directory.Move` через тома) не имеет `forceCrossVolume`-хука, в отличие от `MoveIn`/`MoveInDirectory`
— задокументировано в `PanelItemStorageServiceTests.cs`, рекомендация на будущее — добавить такой хук.

Регрессии Copy-тестов: тесты `Import*` удаляются вместе с методами; их гарантии
(независимость от источника, коллизии имён, Release-guard) переносятся в `MoveIn*`-тесты.

---

## 6. Риски

| Риск | Митигция |
|---|---|
| **Потеря файла при cross-volume сбое** (главный риск) | Инвариант «delete только после verified copy»; тесты D3–D5; сверка размера/существования dest перед delete. |
| Cross-volume не воспроизводится в CI (один том) | Внутренний `forceCrossVolume`-хук/перегрузка для юнита; реальный 2-том — **manual verify (Win10/Win11)**. |
| Manifest рассинхрон со storage | Деградация ADR-B (`OriginalPathUnknown` → Release/no-op), никогда не падаем. |
| Restore директории cross-volume (`Directory.Move` бросает через тома) | Fallback на copy-then-delete внутри Restore; тест на файле + заметка для manual verify на папке. |
| Race: таймер существования видит файл в момент Move | Move атомарен (same-volume) либо copy-first (cross); `Refresh` максимум мигнёт — не критично, не трогаем таймер. |
| Осиротевшие managed-копии при `Failed` restore / удалении панели | Известный остаток (уже в ROADMAP); лог. Отдельная задача очистки. |
| One-time хинт раздувает scope | Вынесен в OPTIONAL; обязателен только штатный Move-курсор. |

**Нативных участков нет** — весь Move/Copy/Delete через `System.IO`, без нового P/Invoke.
UI-эффект курсора и реальный cross-volume — единственное, что помечается **manual verify (Win10/Win11)**.

---

## 7. Manual verify (Win10/Win11)

- Drag файла/папки с рабочего стола в панель → курсор показывает **Move** (стрелка перемещения),
  оригинал исчезает с десктопа, элемент в панели рабочий.
- Cross-volume реальный (перетащить с `D:` при storage на `C:`) → файл перемещён, оригинал удалён
  только по завершении.
- «Убрать из панели» → файл вернулся на исходное место (проверить коллизию имён и воссоздание
  удалённой исходной папки).
- Старая панель (сохранённая до Move) → «Убрать из панели» не падает (деградация).
- (Если реализован) one-time хинт показывается один раз.

---

## 8. Recommended Next Actions

**CRITICAL (запускать сразу, автоматически):**
1. 🚨 **dotnet-services-engineer** — пункты [1] и [2]: `IPanelItemStorageService`
   (`MoveIn`/`MoveInDirectory`/`Restore`/`RestoreOutcome`, удалить `Import`/`ImportDirectory`) +
   реализация в `PanelItemStorageService` (same/cross-volume, manifest, collision-resolve).
   *Reason:* ядро фичи и главный риск потери данных живут в этом слое; всё остальное блокировано им.

**CRITICAL (после [1]):**
2. 🚨 **wpf-ui-builder** — пункты [3b] и участие в [3]: `ChildWindow.xaml.cs`
   `OnDragEnter`/`OnDrop` → `DragDropEffects.Move`; (опц.) one-time хинт + `IAppSettings.HasShownMoveHint`;
   `ChildWindowViewModel.AddDroppedFiles` → `MoveIn*`, `RemoveFileItem` → `Restore`/деградация.
   *Reason:* Move-курсор и restore-вызов — обязательная UX-часть DoD (D12/D13/D9-VM).
   *Примечание:* VM-логика [3] без XAML может уйти и к dotnet-services-engineer — распараллелить с [3b].

**RECOMMENDED:**
3. ⚠️ **tdd-test-engineer** — D1–D14; писать по TDD параллельно с реализацией сервиса. Особый акцент
   на негативных путях (D4/D5/D11) — цена ошибки — потеря файлов пользователя.
   *Reason:* критичность данных требует тестов до/вместе с реализацией.

**CRITICAL (в конце конвейера):**
4. 🚨 **architecture-documenter** — D15: актуализировать ROADMAP (пункт «Копирование при drag&drop»)
   под Move-семантику; зафиксировать решение ADR-A (manifest) при желании отдельным ADR.
   *Reason:* меняется задокументированное поведение фичи; docs обязаны совпасть с кодом.

**OPTIONAL:**
5. 💡 **code-quality-reviewer ∥ fences-ux-parity-reviewer** — после реализации+тестов, перед доком.
   *Condition:* сборка и тесты зелёные. Запускать одним сообщением параллельно (не последовательно).
