# Мастер-план: сортировка содержимого панели (по имени / произвольная + drag-to-reorder)

Источник: пользовательский запрос от 2026-08-16 (паритет с Stardock Fences: «Sort icons by» + ручная
раскладка значков внутри fence).
Статус: план готов, передан реализующим агентам.
Scope суммарно: **L** (UI/Models + UI/ViewModels + XAML + code-behind + Core-хелпер + persistence-совместимость).

Фича состоит из двух неразрывно связанных частей:

- **Часть 1 — режим сортировки.** Новая настройка панели: `Manual` (произвольный, ручной порядок) или
  `Name` (алфавитно по отображаемому имени).
- **Часть 2 — drag-to-reorder.** Перетаскивание значка внутри панели меняет ручной порядок; любое
  ручное перетаскивание переводит панель в `Manual` (как в Fences).

Они планируются как один пункт ROADMAP («Фаза 9. Сортировка и ручной порядок значков»), но реализуются
**раздельными блоками A–E** с явными зависимостями (см. §7 «Порядок»).

> **⚠️ ЯЗЫК UI-СТРОК.** Параллельно идёт локализация UI на английский (другой агент правит те же
> `MainWindow.xaml` / `Templates.xaml` / `ChildWindow.xaml`). **Все** новые пользовательские строки,
> вводимые этим планом (лейблы, пункты меню, тултипы, тексты хинтов), задаются **только на английском**.
> Русский остаётся языком плана/ADR/ROADMAP, но не языком XAML. Конкретные требуемые строки
> перечислены в §4, Блоки B и C — реализующие агенты обязаны использовать именно их.

---

## 1. Проблема (корень)

### 1.1. Порядок элементов панели сейчас — побочный эффект, а не свойство

- `ChildWindowModel.FilePaths : List<string>` (`src/DesktopOrganizer.UI/Models/ChildWindowModel.cs:150`)
  персистится как есть; при загрузке `ChildWindowViewModel.LoadFromModel` → `BaseViewModel.ReplaceAll`
  строит `_allItems` строго в порядке этого списка.
- `ChildWindow.SyncToModel()` (`src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs:120`) пишет обратно
  `FilePaths = _viewModel.AllItems.Select(f => f.Path).ToList()` — **порядок `_allItems` уже является
  персистируемым порядком**, просто сейчас он никем осмысленно не задаётся.
- Фактический порядок = порядок добавления: `AddFilesCore` (`BaseViewModel.cs:158`) всегда делает
  `_allItems.Add(...)` в хвост. Значит, каждый новый drop и каждый тик `FileSystemWatcher` подкидывает
  элемент в конец, независимо от имени. Пользователь не может это изменить — ни мышью, ни настройкой.
- `ChildWindowViewModel.ApplyFilter` (`ChildWindowViewModel.cs:649`) проецирует
  `AllItems.Where(predicate)` в `FileItems` **с сохранением порядка** — т.е. видимый порядок = порядок
  `_allItems`, отфильтрованный.

### 1.2. Портальные панели упорядочены ещё случайнее

Для панели-портала (`SourceFolderPath != null`) список пересобирается из
`Directory.GetFileSystemEntries` (`ChildWindowViewModel.cs:511`, `:605`), а инкрементальные события
watcher'а (`OnFolderChanged`, `:529`) делают `AddFiles([e.Path])` — **новый файл всегда попадает в
конец панели**, даже если по алфавиту он первый. `FilePaths` для портала не персистится
(`SyncToModel` пропускает портальные панели), поэтому «запомнить» такой порядок нельзя в принципе.
Это наблюдаемый дефект: создал файл в зеркалируемой папке — он «прилип» к концу списка до перезапуска.

### 1.3. Drag-to-reorder не существует, но соседняя панель его уже умеет

В `ChildWindow` нет ни `DoDragDrop`, ни какого-либо исходящего drag'а: единственный drag-drop —
**входящий** (`ChildWindow.xaml:16` `AllowDrop="True"`, `OnDragEnter`/`OnDrop`,
`ChildWindow.xaml.cs:152`/`:167`, формат `DataFormats.FileDrop` → `AddDroppedFiles`).
Значок из панели **никуда нельзя перетащить** — ни наружу, ни внутри.

При этом в том же репозитории уже есть **рабочий прецедент drag-to-reorder** —
`SystemIconsPanelWindow` (`src/DesktopOrganizer.UI/Views/SystemIconsPanelWindow.xaml.cs:101–195`):
`PreviewMouseLeftButtonDown` (запомнить точку + элемент) → `PreviewMouseMove` (порог
`SystemParameters.MinimumHorizontal/VerticalDragDistance`) → `DoDragDrop` с **кастомным форматом**
`"SystemIconKind"` → `DragOver` (индикатор вставки `InsertionIndicator`, `SystemIconsPanelWindow.xaml:86`)
→ `Drop` → `ViewModel.Reorder(kind, newIndex)`. Именно этот механизм переносится на панель,
с двумя отличиями (см. §2, Решение 4 и 5): другая формула вычисления индекса вставки (два режима
раскладки вместо одного вертикального стека) и обязательная трансляция «видимый индекс → индекс в
полном наборе» из-за `ContentFilter`.

### 1.4. Почему это блокирующее расхождение с Fences

В Fences порядок значков внутри fence — **пользовательское свойство**: значки перетаскиваются мышью,
как на рабочем столе с выключенным auto-arrange, и отдельно есть команда сортировки. Сейчас
DesktopOrganizer не даёт ни того, ни другого: порядок непредсказуем и неуправляем.

---

## 2. Архитектурное решение (ADR-стиль)

Ниже — 8 решений. Все они предназначены к переносу в новый **ADR-014 «Сортировка и ручной порядок
значков панели»** (`architecture-documenter`, блок E), как расширение ADR-012 (batch-уведомления,
`ObservableRangeCollection`) и ADR-013 (`ListBox`-контейнеры, сохранение выделения при перестройке).

### Решение 1: `PanelSortMode` — новое поле `ChildWindowModel`, `Manual = 0` (обратная совместимость)

Новый enum `PanelSortMode` (`src/DesktopOrganizer.UI/Models/PanelSortMode.cs`) рядом с
`PanelViewMode`/`PanelContentFilter`:

```csharp
public enum PanelSortMode
{
    Manual = 0, // произвольный (ручной) порядок — порядок FilePaths
    Name = 1,   // алфавитно по FileItem.DisplayName (папки вперёд)
}
```

`ChildWindowModel.SortMode` — INPC-свойство через существующий `SetField` (как `ViewMode`/`ContentFilter`),
сериализуется в тот же JSON `ChildWindowsState`.

**Почему `Manual = 0`:** `System.Text.Json` в этом проекте сериализует enum'ы **числом** (в
`ChildWindowModel` нет `JsonStringEnumConverter`, ср. `PanelViewMode`), а отсутствующее в старом JSON
поле десериализуется в `default` = `0`. Значит `Manual = 0` даёт **побайтовую обратную совместимость
поведения**: панели, сохранённые до этой фичи, продолжат показывать элементы ровно в том же порядке,
что и раньше. Если бы дефолтом был `Name`, все существующие панели молча перетасовались бы при первом
же запуске — недопустимо. Дополнительно это совпадает с Fences: ручная раскладка там — состояние по
умолчанию, сортировка — явное действие.

**Почему поле на `ChildWindowModel`, а не в `IAppSettings`:** сортировка — свойство конкретной панели
(в Fences у каждого fence свой порядок), и `ChildWindowModel` — уже установленный «единый источник
правды вида панели», разделяемый живой панелью и окном настроек (ADR-002/ADR-005).

**Alternatives considered:**
- **`bool IsManualOrder`.** Отклонено: enum расширяем без миграции схемы (`Type`, `DateModified`,
  `Size`, `NameDescending` — очевидные будущие пункты паритета с Fences), а bool потребовал бы замены
  поля.
- **Отдельный persisted-список `ManualOrder : List<string>` рядом с `FilePaths`.** Отклонено — см.
  Решение 2.

### Решение 2: источник правды ручного порядка — **порядок `_allItems`/`FilePaths`**; сортировка по имени — чистая проекция

- `SortMode.Manual` → видимый список = `AllItems.Where(filter)` (порядок `_allItems` как есть).
  Ровно текущее поведение.
- `SortMode.Name` → видимый список = `AllItems.Where(filter).OrderBy(...)`. **`_allItems` при этом не
  мутируется**, `FilePaths` не переписывается.

**Почему так:**
- `FilePaths` **уже** является персистентным упорядоченным списком, и `SyncToModel` уже пишет туда
  порядок `AllItems` — фиче не нужно ни нового персистентного поля, ни изменения схемы JSON, ни
  миграции. Это тот же принцип, что ADR-009 Решение 2 и ADR-013 Решение 2 («не заводить второй
  источник правды, если существующий уже несёт нужную информацию»).
- Немутирующая сортировка означает, что переключение `Manual → Name → Manual` из окна настроек
  **возвращает прежний ручной порядок** (явное требование запроса). Если бы `Name` перетасовывал
  `_allItems`, ручной порядок терялся бы безвозвратно при первом же взгляде на «сортировку по имени».
- Персистентность reorder'а достаётся бесплатно: `WindowManager.PersistWindowsState` →
  `SyncAllWindowsToModels` → `ChildWindow.SyncToModel` → `FilePaths = AllItems.Select(Path)`.
  Достаточно поднять существующее событие `FilesChanged` (как это делают `RemoveFileItem`,
  `SwitchView`, `CommitRename`).

**Alternatives considered:**
- **Мутировать `_allItems` при включении `Name`** (сортировка как одноразовое действие, Fences-style
  «Sort icons by → Name»). Отклонено как основное поведение: запрос явно требует **опцию**
  (переключатель), а не команду, и явно требует не терять ручной порядок при переключении. Однако
  ровно эта мутация используется точечно при **drag-триггерном** переходе `Name → Manual` — см.
  Решение 6.
- **Отдельный persisted `ManualOrder : List<string>`.** Отклонено: дублирует `FilePaths`, требует
  синхронизации при add/remove/rename (три существующих пути мутации), требует политики для
  «путь есть в `ManualOrder`, но нет в `FilePaths`» и наоборот — весь этот класс багов не существует,
  пока порядок хранится ровно в одном списке.

### Решение 3: точка применения сортировки — `ChildWindowViewModel.ApplyFilter`, без нового хука в `BaseViewModel`

`ApplyFilter()` становится `ApplyFilterAndSort()`:

```csharp
private void ApplyFilterAndSort()
{
    var predicate = BuildFilterPredicate(Model.ContentFilter);
    var visible = AllItems.Where(predicate);
    if (EffectiveSortMode == PanelSortMode.Name)
        visible = visible.OrderBy(item => item, PanelItemSortComparer.ByName);
    ReplaceFileItems(visible);
}
```

`OnSourceChanged` уже переопределён на `ApplyFilter` — значит **все** пути перестройки (`AddFiles`,
`RemoveFile`, `RemoveFileByPath`, `ReplaceAll`, портальный тик, post-rename `OnSourceChanged`)
автоматически получают сортировку, без правок в каждом из них.

**Почему не `protected virtual IEnumerable<FileItem> ApplySort(...)` в `BaseViewModel` (вариант из
запроса):** `BaseViewModel` ничего не знает про `ChildWindowModel`, а единственный его наследник с
моделью — `ChildWindowViewModel`, который уже владеет единственной точкой проекции (`OnSourceChanged` →
`ApplyFilter`). Хук в базовом классе был бы вызовом «сверху вниз» без единого второго пользователя и
дублировал бы уже существующую точку расширения. Прецедент `ResolveDisplayName` (ADR-013 Решение 3) —
не контрпример: там хук нужен был **внутри** `AddFilesCore`, куда наследник иначе не дотягивается.

**Почему не `CollectionViewSource.SortDescriptions` / `ICollectionView` в XAML:** (а) сортировка нужна
и как источник порядка для drag-reorder-трансляции индексов (§ Решение 5) — на уровне VM, а не View;
(б) `ICollectionView` поверх `ObservableRangeCollection` с `Reset`-уведомлениями даёт лишний слой
пересортировки на каждую перестройку; (в) две разные `DataTemplate` (список/плитка) потребовали бы
дублирования `CollectionViewSource` либо общего ресурса с shared-состоянием между панелями.
Отклонено.

### Решение 4: компаратор — собственный natural-order в Core, без P/Invoke; папки вперёд

Новый чистый (без I/O, без WPF) тип `DesktopOrganizer.Core.Models.NaturalStringComparer` +
`PanelItemSortComparer.ByName` (`IComparer<FileItem>`):

1. Сначала `IsDirectory` (папки перед файлами) — как в Проводнике/на рабочем столе Windows при
   «Упорядочить → Имя».
2. Затем `DisplayName` через natural-order сравнение (числовые фрагменты сравниваются как числа:
   `file2` < `file10`), регистронезависимо.
3. Тай-брейк — `Path` (`OrdinalIgnoreCase`), чтобы порядок был **тотальным и стабильным**: два
   элемента панели легально могут иметь одинаковое `DisplayName` (разные managed GUID-каталоги,
   ADR-013 R11), и без тай-брейка их взаимный порядок «дрожал» бы между перестройками.

**Почему `DisplayName`, а не `Name`:** пользователь сортирует то, что видит; для legacy managed-элементов
`Name` содержит GUID-суффикс (ADR-013 Решение 3), и сортировка по нему выглядела бы случайной.

**Почему собственный natural-order, а не `StrCmpLogicalW`:** `StrCmpLogicalW` (shlwapi) дал бы
побайтовый паритет с Проводником, но это P/Invoke, а инвариант проекта — «P/Invoke только за
`INativeApi`». Заводить нативный вызов ради сравнения двух строк означало бы либо нарушить инвариант,
либо протащить `INativeApi` в компаратор, который вызывается O(n log n) раз на каждую перестройку
списка и должен оставаться юнит-тестируемым чистой функцией (прецедент: `GridSnap`,
`DesktopGestureStateMachine`, `ManagedItemName`, `FileNameValidation`).
**Trade-off:** возможны расхождения с Проводником в экзотических случаях (коллация диакритики,
символы вне BMP). Приемлемо и фиксируется в ADR-014.

**Alternatives considered:**
- **`StringComparer.OrdinalIgnoreCase`.** Отклонено: `file10` окажется перед `file2` — заметное
  расхождение с Проводником на реальных данных (нумерованные файлы).
- **`StringComparer.CurrentCultureIgnoreCase`.** Отклонено как единственный механизм: культурно
  корректно, но не числово-осведомлённо. Используется **внутри** natural-компаратора для сравнения
  нечисловых фрагментов.
- **Папки и файлы вперемешку.** Отклонено: расходится и с Проводником, и с рабочим столом Windows.

### Решение 5: drag-to-reorder оперирует **полным** набором; трансляция индекса — чистая, тестируемая функция в `BaseViewModel`

Новый метод `BaseViewModel.MoveItem(FileItem item, int visibleInsertIndex) : bool`.
`visibleInsertIndex` — индекс **щели вставки** в текущем видимом списке `FileItems`, диапазон
`[0, FileItems.Count]`.

Алгоритм (обязателен к реализации буквально — он же спецификация тестов):

```
1. vFrom = FileItems.IndexOf(item);  если < 0 → false (элемент не виден — перетаскивать нечего)
2. если visibleInsertIndex == vFrom || visibleInsertIndex == vFrom + 1 → false (щель до/после самого себя = no-op)
3. vTo = visibleInsertIndex > vFrom ? visibleInsertIndex - 1 : visibleInsertIndex   // финальный видимый индекс
4. _allItems.Remove(item)
5. anchor-вычисление по видимому списку БЕЗ item (обозначим rest = FileItems без item):
     если vTo == 0        → allIndex = _allItems.IndexOf(rest[0])          // вставить ПЕРЕД первым видимым
     иначе                → allIndex = _allItems.IndexOf(rest[vTo - 1]) + 1 // вставить СРАЗУ ПОСЛЕ предшествующего видимого
     если rest пуст       → allIndex = 0
6. _allItems.Insert(allIndex, item)
7. _fileItems.Move(vFrom, vTo)   // ObservableCollection.Move → одно уведомление Move, БЕЗ Reset
8. → true
```

**Почему трансляция через «видимый сосед-якорь»:** при активном `ContentFilter` видна лишь часть
набора. Наивное `_allItems.Move(vFrom, vTo)` перепутало бы индексы двух разных списков и переставило
бы не тот элемент. Якорение к видимому соседу гарантирует требуемое запросом свойство: **относительный
порядок скрытых фильтром элементов сохраняется**, они не отбрасываются и не «схлопываются» в конец —
меняется только позиция перетаскиваемого элемента относительно видимых соседей.
**Осознанная асимметрия:** скрытые элементы, лежавшие в щели между `rest[vTo-1]` и `rest[vTo]`,
оказываются **после** вставленного (правило «сразу после предшествующего видимого»). Обратное правило
(«сразу перед последующим видимым») столь же корректно; выбрано первое, потому что при перетаскивании
в **конец** списка (`vTo == rest.Count`) второго якоря просто не существует. Правило фиксируется в
ADR-014 и покрывается тестом.

**Почему `_fileItems.Move`, а не полная перестройка через `OnSourceChanged`:** `ReplaceFileItems`
поднимает `Reset` (ADR-012), а `Reset` заставляет `ListBox` сбросить позицию скролла (известный
открытый тикет ROADMAP). Сразу после перетаскивания в проскролленной панели это выглядело бы как
«панель прыгнула наверх» — регрессия ровно в том жесте, ради которого фича делается.
`ObservableCollection.Move` даёт точечное `Move`-уведомление: скролл и выделение сохраняются, элемент
визуально переезжает.

**Почему в `BaseViewModel`, а не в `ChildWindowViewModel`:** `_allItems`/`_fileItems` приватны для
`BaseViewModel`; это единственный класс, который вправе их мутировать согласованно (он же владеет
`_allItemPaths`). `MoveItem` **не трогает** `_allItemPaths` — множество путей не меняется, только их
порядок; это явно фиксируется в XML-doc и тесте.

### Решение 6: ручное перетаскивание автоматически переводит панель в `Manual` — с материализацией текущего видимого порядка

`ChildWindowViewModel.ReorderItem(FileItem item, int visibleInsertIndex)`:

```
1. если IsPortal → return (см. Решение 7)
2. если EffectiveSortMode == Name:
     a. MaterializeSortOrder(): переупорядочить _allItems целиком по PanelItemSortComparer.ByName
     b. Model.SortMode = PanelSortMode.Manual
3. MoveItem(item, visibleInsertIndex)
4. если что-то переместилось → FilesChanged
```

**Почему авто-переключение, а не предупреждение:** в Fences любое ручное перетаскивание значка
переводит fence в ручную раскладку — это и есть ожидаемый жест. Модальное подтверждение из панели с
`WS_EX_NOACTIVATE` вдобавок технически проблемно (ADR-013 R1: `MessageBox` из панели — отдельный
риск), а немодальный вопрос посреди drag'а разрушил бы сам жест. Отклонено.

**Почему материализация (шаг 2a) обязательна:** без неё `_allItems` остался бы в старом ручном
порядке (например, порядке добавления), и в момент переключения на `Manual` список **скачком**
перестроился бы в него — а затем ещё и перетаскиваемый элемент встал бы не туда, куда его тянули.
Материализация сортированного порядка в `_allItems` делает переход визуально **невидимым** (проекция
до и после идентична), и пользователь получает WYSIWYG-результат перетаскивания.

**Почему это не противоречит Решению 2:** Решение 2 запрещает мутировать `_allItems` при *включении*
сортировки по имени (иначе теряется ручной порядок «просто от того, что посмотрел»). Здесь мутация —
следствие **явного разрушающего сортировку действия пользователя**: он уже решил задать порядок
руками, старый ручной порядок в этот момент и так перестаёт быть актуальным.
Итог для пользователя — два предсказуемых правила:
- переключение режима **в настройках/меню** ничего не разрушает (`Manual → Name → Manual` возвращает
  прежний ручной порядок);
- перетаскивание значка мышью **фиксирует** видимый порядок как ручной.

**`MaterializeSortOrder` реализуется как `BaseViewModel.SortAllItems(IComparer<FileItem>)`** —
переупорядочивает `_allItems` на месте, `_allItemPaths` не трогает, `OnSourceChanged` не поднимает
(вызывающий сам решает, когда перестраивать).

**Побочный эффект шага 2b, требующий защиты:** `Model.SortMode = Manual` поднимает
`PropertyChanged` → `OnModelPropertyChanged` → `ApplyFilterAndSort()` → `ReplaceFileItems` → `Reset` →
скролл прыгает наверх ровно в момент drop'а. Лечится Решением 8.

### Решение 7: портальные панели — `Manual` недоступен, эффективный режим всегда `Name`

`ChildWindowViewModel.EffectiveSortMode => IsPortal ? PanelSortMode.Name : Model.SortMode`.
Drag-reorder в портале отклоняется на уровне `DragOver` (`Effects = None`), команда/метод
`ReorderItem` защищён гардом. В окне настроек `ComboBox` сортировки для портальной панели
**disabled** с поясняющим тултипом. `Model.SortMode` при этом **не перезаписывается** — панель,
временно ставшая порталом и вернувшаяся обратно, сохраняет свой ручной режим.

**Почему так:**
1. Ручной порядок портала **физически негде хранить**: `SyncToModel` не персистит `FilePaths` для
   портальных панелей (`ChildWindow.xaml.cs:126`), а список пересобирается из
   `Directory.GetFileSystemEntries` при каждом `ResyncRequired`. «Поддержать с ограничениями» означало
   бы завести второй персистентный список порядка именно для порталов, с политикой слияния для
   появившихся/исчезнувших файлов — ровно тот класс сложности, которого Решение 2 избегает.
2. Это следует уже установленному в проекте паттерну асимметрии portal vs regular:
   `RemoveFileCommand` заблокирован для портала (`ChildWindowViewModel.cs:250`), rename — разрешён
   (ADR-013). Критерий разделения ровно один: **операция над реальным файлом** (rename) — разрешена,
   **операция над курируемым списком панели** (remove, порядок) — не имеет смысла для зеркала.
3. Это **чинит дефект §1.2 бесплатно**: новый файл, пойманный watcher'ом, встаёт на своё алфавитное
   место, а не в конец панели.

**Alternatives considered:**
- **Разрешить `Manual` в портале без персистентности.** Отклонено: порядок терялся бы при каждом
  перезапуске и при каждом `ResyncRequired` (переполнение буфера watcher'а) — поведение, которое
  пользователь воспримет как баг.
- **Разрешить `Manual` с отдельным persisted-списком порядка для портала.** Отклонено: см. п.1;
  кандидат в бэклог, если появится запрос.

**Требуемая правка, легко упускаемая:** `SelectSourceFolderCommand` в ветке **выключения** портала
(`ChildWindowViewModel.cs:466–471`) сейчас не перестраивает список. После этой фичи `EffectiveSortMode`
там меняется `Name → Model.SortMode`, поэтому в конце обеих веток нужен `ApplyFilterAndSort()`.

### Решение 8: `ReplaceFileItems` не поднимает `Reset`, если проекция не изменилась

`BaseViewModel.ReplaceFileItems` материализует входную последовательность и сравнивает её с текущим
`_fileItems` **поэлементно по ссылке**; при совпадении — ранний выход без `ReplaceAll`, без `Reset` и
без `FileCountText`-уведомления.

**Почему это входит в объём именно этой фичи:** переход `Name → Manual` при drag'е (Решение 6, шаг 2b)
конструктивно порождает перестройку с идентичным результатом. Альтернатива — флаг-подавитель
(`_suppressRebuild`) вокруг присваивания `Model.SortMode` — это скрытое временное состояние в VM,
которое придётся помнить всем будущим правкам `OnModelPropertyChanged`. Проверка на равенство —
локальная, безусловно корректная (одинаковая коллекция ⇒ одинаковые уведомления не нужны) и попутно
гасит лишние `Reset`'ы на других путях (например, тик портала, не изменивший ничего после фильтрации).
Стоимость — O(n) сравнений ссылок против уже существующего O(n) копирования в `ReplaceAll`.

**Trade-off:** если когда-нибудь понадобится «перерисовать список принудительно, не меняя состава», это
перестанет работать через `ReplaceFileItems`. Такого сценария сейчас нет (перерисовка отдельных
элементов идёт через `FileItem` INPC); фиксируется в XML-doc и ADR-014.

---

## 3. Затронутые файлы и слои

| Слой | Файл | Что делаем | Блок |
|---|---|---|---|
| Core | `src/DesktopOrganizer.Core/Models/NaturalStringComparer.cs` **(новый)** | чистый natural-order `IComparer<string>` (числовые фрагменты как числа, регистронезависимо) | A |
| Core | `src/DesktopOrganizer.Core/Models/PanelItemSortComparer.cs` **(новый)** | `IComparer<FileItem>` `ByName`: папки вперёд → `DisplayName` (natural) → `Path` (тай-брейк) | A |
| UI/Models | `src/DesktopOrganizer.UI/Models/PanelSortMode.cs` **(новый)** | enum `Manual = 0`, `Name = 1` | A |
| UI/Models | `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` | `SortMode` (INPC через `SetField`, сериализуется) | A |
| UI/VM | `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs` | `MoveItem(item, visibleInsertIndex)`, `SortAllItems(comparer)`, ранний выход в `ReplaceFileItems` | A |
| UI/VM | `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` | `EffectiveSortMode`, `ApplyFilterAndSort`, `ReorderItem`, `SetSortModeCommand`, реакция на `SortMode` в `OnModelPropertyChanged`, `ApplyFilterAndSort()` в обеих ветках `SelectSourceFolder` | A/B |
| UI/VM | `src/DesktopOrganizer.UI/ViewModels/MainWindowViewModel.cs` | `SortModes`, `SelectedPanelSortMode`, `SelectedPanelSupportsManualOrder`, оба — в `RaiseSelectedPanelProperties` | B |
| UI/XAML | `src/DesktopOrganizer.UI/Views/MainWindow.xaml` | строка «Sort order» + `ComboBox` в секции настроек выбранной панели (+ 7-й `RowDefinition`) | B |
| UI/XAML | `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` | подменю «Arrange icons» в контекстном меню заголовка; `AllowDrop` + drag-обработчики на `FileListHost`; `InsertionIndicator` | B/C |
| UI/View | `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` | старт drag'а (`PreviewMouseLeftButtonDown`/`PreviewMouseMove`), `DragOver`/`DragLeave`/`Drop` для формата reorder, вычисление индекса вставки, позиционирование индикатора | C |
| Тесты | `tests/DesktopOrganizer.Tests/Unit/...` | см. §5 | D |
| Docs | `adr/ADR-014-*.md`, `adr/README.md`, `docs/ROADMAP.md` | новый ADR + Фаза 9 | E |

**Не трогаем:** `IPanelItemStorageService`, `IFileItemRenameService`, `IWindowManager`, `IIconProvider`,
`FileItem`, схему JSON (поле `SortMode` добавляется **аддитивно**, старый JSON читается без миграции).

---

## 4. Блоки работ, исполнители, порядок

### Блок A — модель сортировки и ядро порядка. Scope **M**. Исполнители: `dotnet-services-engineer` (A1) → `wpf-ui-builder` (A2–A6)

Блокирует B и C.

- **A1.** Core: `NaturalStringComparer` (`Compare(string?, string?)`: посегментное сравнение
  цифровых/нецифровых фрагментов; цифровые — как числа произвольной длины, с игнорированием ведущих
  нулей и тай-брейком по длине; нецифровые — `string.Compare(..., CurrentCultureIgnoreCase)`;
  `null`/пустые — первыми). Без I/O, без WPF, без P/Invoke.
  Плюс `PanelItemSortComparer.ByName : IComparer<FileItem>` (папки вперёд → `DisplayName` через
  `NaturalStringComparer` → `Path` `OrdinalIgnoreCase`). Оба — `static readonly` синглтоны, без
  аллокаций на вызов.
- **A2.** `PanelSortMode` (enum, XML-doc про `Manual = 0` и обратную совместимость JSON).
- **A3.** `ChildWindowModel.SortMode` через `SetField` + XML-doc: «отсутствующее поле в старом JSON
  десериализуется в `Manual`, что сохраняет предыдущее поведение».
- **A4.** `BaseViewModel.SortAllItems(IComparer<FileItem> comparer)` — стабильная сортировка `_allItems`
  на месте (`List.Sort` **нестабилен** — использовать `_allItems.OrderBy(x => x, comparer).ToList()`
  с последующей заменой содержимого, либо `List.Sort` с тотальным компаратором из A1, у которого
  тай-брейк по `Path` уже гарантирует детерминизм). `_allItemPaths` не трогает, `OnSourceChanged` не
  поднимает.
- **A5.** `BaseViewModel.MoveItem(FileItem item, int visibleInsertIndex) : bool` — ровно алгоритм из
  §2 Решение 5. XML-doc обязан зафиксировать: (а) семантику `visibleInsertIndex` как **щели**
  `[0, FileItems.Count]`; (б) правило якорения и судьбу скрытых фильтром элементов; (в) что
  `_allItemPaths` не меняется; (г) что уведомление — `Move`, а не `Reset`, и почему.
- **A6.** `BaseViewModel.ReplaceFileItems` — ранний выход при поэлементном ссылочном совпадении
  (Решение 8). **Не сломать** существующее восстановление выделения по пути (ADR-013): при раннем
  выходе выделение и так не терялось, восстанавливать нечего — но код восстановления должен остаться
  на пути, где перестройка реально произошла.
- **A7.** `ChildWindowViewModel`: `EffectiveSortMode`; `ApplyFilter` → `ApplyFilterAndSort`;
  `OnModelPropertyChanged` реагирует на `SortMode` так же, как на `ContentFilter`
  (`ApplyFilterAndSort()` + `FilesChanged`); `SelectSourceFolder` вызывает `ApplyFilterAndSort()` в
  **обеих** ветках (включение и выключение портала); `ReorderItem(FileItem, int)` по §2 Решение 6.

### Блок B — UI настроек и меню панели. Scope **S**. Исполнитель: `wpf-ui-builder`

Зависит от A. Может идти **параллельно** блоку C.

- **B1.** `MainWindowViewModel`: `IReadOnlyList<PanelSortMode> SortModes { get; } = Enum.GetValues<PanelSortMode>();`,
  `PanelSortMode SelectedPanelSortMode` (по паттерну `SelectedPanelViewMode`: getter с fallback
  `PanelSortMode.Manual`, setter через `SetSelectedPanel`), `bool SelectedPanelSupportsManualOrder`
  (`_selectedPanel is not null && string.IsNullOrWhiteSpace(_selectedPanel.SourceFolderPath)`).
  Оба новых свойства **добавить в `RaiseSelectedPanelProperties()`** — иначе при смене выбранной
  панели поля не обновятся (типичная ошибка в этом файле).
- **B2.** `MainWindow.xaml`, секция настроек выбранной панели: добавить **7-й** `RowDefinition` и
  строку:
  - лейбл (`TextBlock`, колонка 0): **`Sort order`**
  - `ComboBox` (колонки 1–2): `ItemsSource="{Binding SortModes}"`,
    `SelectedItem="{Binding SelectedPanelSortMode, Mode=TwoWay}"`,
    `IsEnabled="{Binding SelectedPanelSupportsManualOrder}"`,
    `ToolTip="Folder Portal panels always sort by name."`
- **B3.** `ChildWindow.xaml`, контекстное меню заголовка (по существующему паттерну секции
  `Содержимое`/`SetContentFilterCommand`): новая секция-заголовок **`Arrange icons`**
  (`IsEnabled="False" FontWeight="SemiBold"`) и два `IsCheckable`-пункта через `EnumEqualsConverter`:
  - **`Manual`** → `CommandParameter="{x:Static uimodels:PanelSortMode.Manual}"`
  - **`By name`** → `CommandParameter="{x:Static uimodels:PanelSortMode.Name}"`
  Команда — новая `ChildWindowViewModel.SetSortModeCommand`
  (`new RelayCommand<PanelSortMode>(mode => Model.SortMode = mode, _ => !IsPortal)`).

> **UI-строки блока B (только эти, английский):** `Sort order`,
> `Folder Portal panels always sort by name.`, `Arrange icons`, `Manual`, `By name`.
> Значения `ComboBox` рендерятся как имена членов enum (`Manual` / `Name`) — тот же существующий
> паттерн, что у `PanelViewMode` (`List`/`Wrap`). Единый `EnumDisplayConverter` для всех
> пользовательских enum-`ComboBox` — отдельный бэклог-тикет, **вне объёма**.

### Блок C — drag-to-reorder. Scope **M**. Исполнитель: `wpf-ui-builder`

Зависит от A. Параллелен B.

Формат данных: константа `ChildWindow.PanelItemReorderFormat = "DesktopOrganizer.PanelItemReorder"`.
**Полезная нагрузка — `item.Path` (string), а не сам `FileItem`.** Причина: `DataObject` кладёт
не-сериализуемые типы в OLE-хранилище с непредсказуемым поведением при пересечении границ окон, а
строка — гарантированно безопасна; на drop элемент восстанавливается поиском по пути в собственных
`FileItems` — что **попутно** даёт бесплатный гард «drag из чужой панели» (путь не найден → отказ).

- **C1.** `ChildWindow.xaml`: на `FileListHost` добавить `AllowDrop="True"`,
  `PreviewMouseMove="FileListHost_PreviewMouseMove"`, `DragOver="FileListHost_DragOver"`,
  `DragLeave="FileListHost_DragLeave"`, `Drop="FileListHost_Drop"`.
- **C2.** Старт жеста (`ChildWindow.xaml.cs`):
  - в **существующий** `FileListHost_PreviewMouseLeftButtonDown` (не создавать второй обработчик!)
    добавить запоминание `_reorderStartPoint = e.GetPosition(null)` и
    `_reorderCandidate = FindAncestorOrSelf<ListBoxItem>(source)?.DataContext as FileItem`.
    **Гарды:** не назначать кандидата, если `FindAncestorOrSelf<TextBox>(source) is not null`
    (клик в активный inline-редактор — там drag выделяет текст), если `_viewModel.EditingItem is not null`,
    или если `_viewModel.IsPortal`. Обработчик **не должен** ставить `e.Handled` — иначе сломается
    выделение элемента `ListBox`-ом (ADR-013).
  - новый `FileListHost_PreviewMouseMove`: если ЛКМ нажата, кандидат есть, `_isReordering == false` и
    превышен порог `SystemParameters.MinimumHorizontal/VerticalDragDistance` → `_isReordering = true`,
    `DragDrop.DoDragDrop(FileListHost, new DataObject(PanelItemReorderFormat, item.Path), DragDropEffects.Move)`,
    после возврата — сбросить `_isReordering`/`_reorderCandidate` и спрятать индикатор
    (`DoDragDrop` блокирует до завершения жеста).
- **C3.** Приём (`ChildWindow.xaml.cs`):
  - `FileListHost_DragOver`: если формат не наш **или** `_viewModel.IsPortal` **или** путь из
    payload не найден в `_viewModel.FileItems` → `e.Effects = None`, спрятать индикатор, **`e.Handled`
    не ставить** (чтобы внешний `DataFormats.FileDrop` продолжал обрабатываться `Window.OnDragEnter`/
    `OnDrop`, как сейчас). Иначе → `Effects = Move`, показать индикатор, `e.Handled = true`.
  - `FileListHost_Drop`: то же разделение. Для нашего формата — вычислить индекс вставки (C4),
    вызвать `_viewModel.ReorderItem(item, index)`, `e.Handled = true`. Для чужого формата — **выйти
    без `Handled`**, чтобы событие всплыло до `Window.OnDrop` и внешний drop файлов работал как раньше.
    Это ключевая точка нерегрессии: `AllowDrop` на `FileListHost` делает его целью drop'а, и молчаливое
    поглощение события сломало бы существующую фичу drag&drop файлов с рабочего стола.
  - `FileListHost_DragLeave`: спрятать индикатор.
- **C4.** Вычисление индекса вставки (`ChildWindow.xaml.cs`, приватный статический хелпер):
  - найти живой `ListBox` внутри `FileListHost` (`FindVisualChild<ListBox>` — как в
    `SystemIconsPanelWindow`), затем контейнер под курсором: `VisualTreeHelper.HitTest` →
    `FindAncestorOrSelf<ListBoxItem>`;
  - если контейнер найден: `i = listBox.ItemContainerGenerator.IndexFromContainer(container)`;
    сравнить курсор с серединой контейнера — **по горизонтали** в `PanelViewMode.Wrap`, **по вертикали**
    в `PanelViewMode.List` (`Model.ViewMode`); результат `i` или `i + 1`;
  - если контейнер не найден (курсор в пустоте под/после элементов) → `FileItems.Count` (вставка в конец).
  **Почему через `ListBoxItem`, а не обходом детей панели** (как в `SystemIconsPanelWindow`): работает
  одинаково для `WrapPanel` и вертикального стека, не зависит от типа `ItemsPanel` и переживает
  виртуализацию.
- **C5.** Индикатор вставки. **Рекомендуется, но не блокирует MVP** — можно вынести в отдельную волну.
  `Border` (2px) в `ChildWindow.xaml`, `IsHitTestVisible="False"`, `Visibility="Collapsed"`, поверх
  `FileListHost` (прямо в родительском `Grid`, по образцу `InsertionIndicator` в
  `SystemIconsPanelWindow.xaml:86`); в `DragOver` — позиционирование `Margin`'ом относительно
  найденного контейнера, **вертикальная** линия в Wrap-виде и **горизонтальная** в List-виде.
  Adorner-слой рассматривался и отклонён: даёт то же самое ценой `AdornerLayer`-инфраструктуры и
  собственного `OnRender`, при этом прецедент в репозитории — именно простой `Border`.

> **UI-строк блока C нет** (чисто жестовая механика). Никаких новых сообщений/хинтов не вводится.

### Блок D — тесты. Исполнитель: `tdd-test-engineer`

Идёт волнами вслед за A и C (по TDD-политике репозитория — вместе с блоком, не в конце). См. §5.

### Блок E — документация. Исполнитель: `architecture-documenter`

`adr/ADR-014-panel-item-sorting-and-manual-order.md` (Решения 1–8 из §2), `adr/README.md`,
`docs/ROADMAP.md` — новая **Фаза 9** (§10) + пополнение сводного списка manual verify.

---

## 5. Тесты (что покрываемо в CI без GUI)

| Что | Где | Комментарий |
|---|---|---|
| `NaturalStringComparer` | `Unit/Models/NaturalStringComparerTests.cs` (новый) | `file2` < `file10`; `a1b2` vs `a1b10`; ведущие нули (`01` vs `1`); чисто числовые имена; регистр (`A` vs `a`); пустая/`null` строка; очень длинные числовые фрагменты (не переполняются `int`); тотальность (антисимметрия, транзитивность на выборке) |
| `PanelItemSortComparer.ByName` | `Unit/Models/PanelItemSortComparerTests.cs` (новый) | папка перед файлом при любом имени; при равном `DisplayName` — тай-брейк по `Path`, порядок детерминирован; сортирует по `DisplayName`, а не по `Name` (кейс legacy-имени с GUID) |
| `ChildWindowModel.SortMode` round-trip | `Unit/Models/ChildWindowModelSerializationTests.cs` (новый) | JSON **без** поля `SortMode` → `Manual`; round-trip `Name` → `Name`; INPC поднимается на изменение |
| `BaseViewModel.MoveItem` — без фильтра | `Unit/ViewModels/BaseViewModelCollectionTests.cs` (расширить) | перемещение вперёд/назад/в начало/в конец; щель до и после самого себя ⇒ `false` и коллекция не изменена; элемент не из списка ⇒ `false` |
| `BaseViewModel.MoveItem` — с фильтром (ключевой) | там же | набор `[A(hidden), B, C(hidden), D]`, видимы `B, D`: перенос `D` перед `B` ⇒ `_allItems` = `A, D, B, C` (правило якорения); скрытые не потеряны, их относительный порядок сохранён; `AllItems.Count` не изменился |
| `MoveItem` не трогает `_allItemPaths` | там же | после перемещения повторный `AddFiles([тот же путь])` по-прежнему дедуплицируется |
| `MoveItem` даёт `Move`, а не `Reset` | там же | подписка на `CollectionChanged`: ровно одно событие, `Action == Move` (прецедент ассертов — `ObservableRangeCollectionTests`) |
| `BaseViewModel.SortAllItems` | там же | `_allItems` переупорядочен; состав и `_allItemPaths` не изменились; `FileItems` **не** перестроен (событий нет) |
| `ReplaceFileItems` ранний выход | там же | одинаковая проекция ⇒ ноль `CollectionChanged` и ноль `PropertyChanged(FileCountText)`; изменённая ⇒ ровно один `Reset` (регресс ADR-012); выделение по-прежнему восстанавливается на пути реальной перестройки (регресс ADR-013) |
| Проекция `SortMode.Name` | `Unit/ViewModels/ChildWindowViewModelTests.cs` (расширить) | `FileItems` отсортированы, `AllItems` — **нет**; переключение `Name → Manual` возвращает исходный порядок `FileItems` |
| Сортировка × `ContentFilter` | там же | `Name` + `HideShortcuts`/`FoldersOnly` — отфильтрованное подмножество отсортировано; `HideAll` ⇒ пусто |
| `AddFiles` в режиме `Name` | там же | новый элемент встаёт на алфавитное место, а не в конец |
| `ReorderItem` авто-переключение | там же | из `Name`: `Model.SortMode` стал `Manual`, `AllItems` материализован в отсортированном порядке, элемент оказался на позиции drop'а, `FilesChanged` поднят |
| `ReorderItem` в `Manual` | там же | `SortMode` не менялся, `FilesChanged` поднят ровно один раз при реальном перемещении и **не поднят** при no-op |
| Портал | там же | `EffectiveSortMode == Name` при `SourceFolderPath != null` даже если `Model.SortMode == Manual`; `ReorderItem` — no-op; `SetSortModeCommand.CanExecute == false`; выключение портала (`SelectSourceFolder` toggle-off) возвращает проекцию к `Model.SortMode` |
| Портал: watcher-тик сохраняет сортировку | там же | `AddFiles([путь])` через `OnFolderChanged`-эквивалент ⇒ элемент на алфавитном месте (фикс §1.2) |
| `MainWindowViewModel` | `Unit/ViewModels/MainWindowViewModelTests.cs` (расширить) | `SelectedPanelSortMode` пишет в модель и вызывает `SaveWindowsState`; `SelectedPanelSupportsManualOrder == false` для панели с `SourceFolderPath`; смена `SelectedPanel` поднимает `PropertyChanged` для обоих новых свойств |

**Не покрываемо в CI:** весь жест drag&drop (`DoDragDrop`, `DragOver`, hit-test контейнеров,
индикатор вставки), а также взаимодействие drag'а с `WS_EX_NOACTIVATE` — см. §8 и §9.
Вычисление индекса вставки, если оно будет выделено в чистый статический хелпер, принимающий
`(индекс контейнера, координата курсора, границы контейнера, ориентация)`, **покрываемо** — сделать так
и покрыть (рекомендация к C4).

---

## 6. Definition of Done

**Часть 1 — режим сортировки:**
1. В окне настроек, секция настроек выбранной панели, есть `ComboBox` **`Sort order`** с двумя
   значениями; изменение применяется к живой панели немедленно и переживает перезапуск.
2. В контекстном меню заголовка панели есть секция **`Arrange icons`** с чекируемыми пунктами
   **`Manual`** / **`By name`**, отражающими текущее состояние.
3. `SortMode.Name` сортирует **видимый** список: папки вперёд, затем алфавитно с natural-order
   (`file2` перед `file10`), по отображаемому имени.
4. `SortMode.Name` **не изменяет** `FilePaths`: переключение `Manual → Name → Manual` возвращает
   прежний ручной порядок в точности.
5. Панель, сохранённая **до** этой фичи, после обновления показывает элементы в **том же** порядке
   (миграции нет, `SortMode` отсутствует в JSON ⇒ `Manual`).
6. Для портальной панели `ComboBox` задизейблен с пояснением, пункты меню недоступны, а содержимое
   отсортировано по имени; новый файл, пойманный watcher'ом, встаёт на алфавитное место.

**Часть 2 — drag-to-reorder:**
7. Значок перетаскивается мышью внутри панели в обоих видах (список и плитка) и встаёт на позицию
   drop'а; порог начала drag'а — системный (короткий клик не начинает перетаскивание).
8. Перетаскивание в панели с `SortMode.Name` автоматически переводит её в `Manual`, **сохраняя
   видимый порядок** (никакого скачка списка), и результат drop'а — ровно то, что видел пользователь.
9. При активном `ContentFilter` перетаскивание меняет позицию в **полном** наборе: скрытые элементы не
   исчезают, их относительный порядок сохраняется (проверяется переключением фильтра на `Show all`).
10. Новый порядок переживает перезапуск приложения (через `FilesChanged` → `SaveWindowsState` →
    `SyncToModel` → `FilePaths`).
11. Существующий drag&drop файлов **с рабочего стола/из Проводника в панель** продолжает работать без
    изменений (курсор `Move`, `AddDroppedFiles`, one-time Move-хинт).
12. Drag-reorder недоступен в портальной панели и во время активного inline-переименования; двойной
    клик по-прежнему открывает файл; клик по пустому месту по-прежнему снимает выделение; `F2` и
    контекстное меню значка не затронуты.
13. Перетаскивание не сбрасывает позицию скролла и выделение (уведомление `Move`, а не `Reset`).

**Сквозное:**
14. `dotnet build` и `dotnet test` зелёные; допустимы только 6 известных предсуществующих падений
    (ROADMAP, Фаза 7) — их число не выросло.
15. Публичные контракты `IPanelItemStorageService`, `IFileItemRenameService`, `IWindowManager`,
    `IIconProvider` **не изменились**; схема JSON `ChildWindowsState` расширена **аддитивно**
    (`SortMode`), старый JSON читается без миграции.
16. Все новые UI-строки — на **английском** (§4).
17. `ADR-014` написан, `adr/README.md` и `docs/ROADMAP.md` (Фаза 9) обновлены.

---

## 7. Порядок выполнения и параллелизм

```
                 Блок A (модель + ядро порядка)
        A1 dotnet-services-engineer → A2–A7 wpf-ui-builder
                           │
                ┌──────────┴──────────┐
                ▼                     ▼
        Блок B (настройки/меню)   Блок C (drag-to-reorder)
          wpf-ui-builder             wpf-ui-builder
                └──────────┬──────────┘
                           ▼
                  Блок D — тесты (волна 2; волна 1 — сразу после A)
                           │
                           ▼
        (code-quality-reviewer ∥ fences-ux-parity-reviewer)
                           │
                           ▼
                 Блок E (architecture-documenter)
```

- **A строго первым:** и B, и C зависят от `PanelSortMode`, `EffectiveSortMode` и `MoveItem`.
- **B и C независимы по файлам,** кроме `ChildWindow.xaml`: B3 правит контекстное меню заголовка,
  C1 — атрибуты `FileListHost` и C5 — индикатор. Разные, несоприкасающиеся участки файла; при ведении
  параллельно — предупредить обоих исполнителей об общем файле. При последовательном ведении конфликта
  нет вовсе.
- **C5 (индикатор вставки)** можно отложить: DoD 7–13 достижимы без него. Если объём поджимает — вынести
  в отдельную волну после ревью MVP.
- Ревьюеры (`code-quality-reviewer` ∥ `fences-ux-parity-reviewer`) — **одним сообщением, параллельно**.

---

## 8. Риски

| # | Риск | Уровень | Митигация |
|---|---|---|---|
| R1 | **`AllowDrop="True"` на `FileListHost` перехватывает входящий `DataFormats.FileDrop`** и ломает существующий drag&drop файлов с рабочего стола — главный сценарий приложения. | **CRITICAL** | C3: обработчики `DragOver`/`Drop` на `FileListHost` **никогда** не ставят `e.Handled` для чужого формата, позволяя событию всплыть до `Window.OnDragEnter`/`OnDrop`. Отдельный обязательный пункт manual verify (§9.6). Обязателен ревью-акцент. |
| R2 | **`DoDragDrop` из окна с `WS_EX_NOACTIVATE`** может вести себя нештатно (курсор, захват мыши, доставка `DragOver`) — тот же класс непроверенных нативных допущений, что ADR-013 R1 (клавиатурный ввод). Вне Windows не воспроизводимо. | **CRITICAL** | Механизм скопирован с уже существующего `SystemIconsPanelWindow`, который живёт в **том же** desktop-слое с теми же стилями окна — т.е. прецедент рабочий (но тоже никогда не верифицированный вживую). Manual verify §9.1 — первым пунктом. При отказе — эскалация на `win32-interop-specialist` (временное снятие `WS_EX_NOACTIVATE` на время жеста через `IDesktopIntegrationService`, по образцу плана-фолбэка ADR-013 R1). |
| R3 | **Авто-переключение `Name → Manual` без материализации** ⇒ список скачком перестраивается в старый ручной порядок прямо в момент drop'а, элемент оказывается «не там». | MAJOR | Решение 6, шаг 2a (материализация **до** присваивания `SortMode`) + Решение 8 (отсутствие лишнего `Reset`). Прямой тест: «после reorder из `Name` порядок `FileItems` = отсортированный с перемещённым элементом». |
| R4 | **Трансляция видимого индекса в индекс полного набора** при активном фильтре — самая вероятная точка логической ошибки (сдвиг на единицу при перемещении вперёд vs назад). | MAJOR | Алгоритм зафиксирован в §2 Решение 5 буквально, включая шаг 3 (`vTo` при `visibleInsertIndex > vFrom`); тесты §5 покрывают обе стороны и краевые щели (0 и `Count`). |
| R5 | **Порядок в `Manual` не тотален для дубликатов путей** — не риск (пути уникальны по `_allItemPaths`), но `Name`-порядок без тай-брейка «дрожал» бы для одинаковых `DisplayName` (ADR-013 R11 допускает такие). | MAJOR | Тай-брейк по `Path` в `PanelItemSortComparer` (Решение 4) + тест на детерминизм. |
| R6 | **Конфликт старта drag'а с существующими жестами**: выделение (`PreviewMouseLeftButtonDown`), двойной клик «Открыть», выделение текста в inline-редакторе, клик в пустоту для снятия выделения. | MAJOR | C2: единый существующий обработчик, без `e.Handled`; гарды на `TextBox`-предка, `EditingItem`, `IsPortal`; порог `SystemParameters.MinimumHorizontal/VerticalDragDistance` (короткий клик drag не начинает). Manual verify §9.7–9.9. |
| R7 | **Пользователь ожидает, что значок можно вытащить ИЗ панели** (на рабочий стол/в другую панель), раз он теперь «таскается». | MINOR | Осознанно вне объёма (нужна семантика Move + `Restore` из managed storage + межпанельный протокол). Формат payload'а (`Path`-строка) и гард «путь не из моей панели ⇒ `Effects = None`» спроектированы так, что чужой reorder-drag **безопасно отклоняется**, а будущий межпанельный перенос ляжет сверху без слома. Бэклог-тикет. |
| R8 | **Внешний drop не учитывает позицию курсора** — файл с рабочего стола всегда падает в конец (в `Manual`), тогда как Fences вставляет его в точку drop'а. | MINOR | Вне объёма первой итерации: требует протаскивания индекса вставки в `AddDroppedFiles`/`AddFilesCore` (сейчас всегда append). Бэклог-тикет, инфраструктура (`MoveItem` + вычисление индекса из C4) для него уже будет готова. |
| R9 | **`Reset`-подавление (Решение 8) маскирует реальную перестройку**, если когда-нибудь понадобится форс-обновление без смены состава. | MINOR | Такого сценария нет (перерисовка — через `FileItem` INPC). Зафиксировать в XML-doc и ADR-014. |
| R10 | **`OrderBy` на каждую перестройку** в режиме `Name` — O(n log n) на каждый тик портала/смену фильтра. | MINOR | Панели — десятки элементов; `ReplaceAll` уже O(n) с копированием, компараторы — синглтоны без аллокаций. Панели на сотни иконок — тот же отдельный тикет, что и виртуализация wrap-вида (ADR-013 Решение 1). |
| R11 | **Natural-order расходится с Проводником** в экзотических случаях (диакритика, символы вне BMP) из-за отказа от `StrCmpLogicalW`. | MINOR | Осознанный trade-off Решения 4 (инвариант «P/Invoke только за `INativeApi`» + тестируемость). Фиксируется в ADR-014. |
| R12 | **Смена `SortMode` поднимает `FilesChanged` → `SaveWindowsState`** на каждый клик в `ComboBox`/меню. | MINOR | Запись дебаунсится (`SaveScheduler`, 500 мс, Фаза 7 Блок C) — тот же путь, что у `ContentFilter`/`SwitchView`. Ничего делать не нужно, но не «чинить» это синхронным `SaveWindowsStateNow`. |

**Нативных/P-Invoke участков в объёме нет** — весь код managed WPF/.NET. Решение 4 сознательно
отказывается от единственного напрашивавшегося P/Invoke. Единственная потенциальная нативная
эскалация — R2 (`win32-interop-specialist`, только по результату ручной проверки).

---

## 9. Ручная проверка на Windows 10/11 (обязательна — вне CI не воспроизводится)

1. **R2 (наивысший приоритет):** перетащить значок внутри панели в **плиточном** виде — жест
   начинается, курсор показывает `Move`, drop срабатывает. То же в **списочном** виде.
   Если `DoDragDrop` не стартует или `DragOver` не приходит — зафиксировать и эскалировать (см. R2).
2. Перетащить значок в начало / в середину / в конец списка — позиция после drop'а совпадает с местом,
   где отпустили кнопку (проверить обе стороны: перемещение вверх и вниз).
3. Панель в режиме `By name` → перетащить значок → режим переключился на `Manual`, **список не
   дёрнулся**, элемент встал ровно туда, куда тянули.
4. Порядок после перетаскивания переживает перезапуск приложения.
5. Включить фильтр `Hide shortcuts` (или `Folders only`), переставить элемент, затем вернуть
   `Show all` — скрытые элементы на месте, их относительный порядок сохранён, переставленный — на
   новой позиции.
6. **R1:** перетащить файл и папку **с рабочего стола** в панель — работает как раньше (курсор `Move`,
   оригинал исчезает с рабочего стола, элемент появился в панели, one-time Move-хинт при первом разе).
7. **R6:** двойной клик по значку по-прежнему **открывает** файл; одиночный клик выделяет; клик в
   пустое место снимает выделение; ПКМ выделяет и открывает меню.
8. **R6:** `F2` / «Rename» → в inline-редакторе выделение текста мышью **не** начинает drag значка;
   после Enter/Esc drag снова работает.
9. Короткий клик с микро-дрожанием руки не начинает перетаскивание (системный порог).
10. Настройки: `Sort order` = `Name` → панель отсортирована (папки вперёд, `file2` перед `file10`);
    обратно `Manual` → вернулся **прежний** ручной порядок.
11. Контекстное меню заголовка → `Arrange icons` → галочка стоит на актуальном режиме, переключение
    работает и синхронизировано с окном настроек.
12. Портальная панель: `ComboBox` в настройках задизейблен с тултипом, пункты `Arrange icons`
    недоступны, содержимое отсортировано по имени; создать файл в зеркалируемой папке → он появился
    на **алфавитном** месте, а не в конце; попытка перетащить значок — жест не проходит.
13. Панель, созданная **до** обновления (с уже сохранённым `ChildWindowsState`), после запуска
    показывает элементы в прежнем порядке.
14. Индикатор вставки (если C5 сделан): в плиточном виде — вертикальная линия между колонками, в
    списочном — горизонтальная между строками; исчезает при уходе курсора и после drop'а.
15. Перетаскивание в проскролленной панели: позиция скролла и выделение **не сбрасываются**.
16. Ресайз панели в плиточном виде после реордера остаётся кратным сетке иконок (`GridMetrics` не
    затронут — но `AllowDrop`/индикатор добавлены в тот же визуальный контейнер).
17. DPI 125%/150%: индекс вставки вычисляется корректно (элемент не встаёт «на одну позицию мимо»).

---

## 10. Что записать в ROADMAP (для `architecture-documenter`)

Новая секция после Фазы 8:

```
## Фаза 9. Сортировка и ручной порядок значков панели

- [ ] Режим сортировки панели (`PanelSortMode`: Manual / Name) — настройка в окне настроек и в
      контекстном меню заголовка панели; сортировка по имени — немутирующая проекция (папки вперёд,
      natural-order), ручной порядок хранится в существующем `FilePaths`, схема JSON расширена
      аддитивно (`Manual = 0` ⇒ полная обратная совместимость поведения).
- [ ] Drag-to-reorder значков внутри панели (оба вида), с трансляцией видимого индекса в полный набор
      при активном `ContentFilter`, авто-переключением в `Manual` при первом ручном перетаскивании и
      точечным `Move`-уведомлением вместо `Reset` (скролл и выделение сохраняются).
```

Плюс в «Сквозные задачи» — пункты manual verify из §9 (с явным выделением R1 и R2 как блокирующих
верификаций) и три бэклог-тикета:
- перетаскивание значка **из** панели (на рабочий стол / в другую панель) — R7;
- внешний drop вставляет файл в позицию курсора, а не в конец — R8;
- `EnumDisplayConverter` для пользовательских enum-`ComboBox` (`PanelViewMode`, `PanelSortMode`) —
  сейчас отображаются сырые имена членов enum.
