# ADR-014: Сортировка и ручной порядок значков панели

**Статус:** Accepted — Amended 2026-09-27 (ADR-036: drag-out закрывает Risk R7 — перетаскивание значка из панели наружу/в другую панель); Amended 2026-09-27 (сортировка
финализирована — Manual/Name окончательны, Type/Date/Size не планируются, won't-do)
**Дата:** 2026-08-16

---

## Контекст

Мастер-план [docs/PLANS/panel-item-sorting.md](../docs/PLANS/panel-item-sorting.md) (2026-08-16)
зафиксировал две неразрывно связанные UX-фичи паритета с Stardock Fences («Sort icons by» + ручная
раскладка значков внутри fence), реализованные раздельными Блоками A–D с явными зависимостями:

- **Часть 1 — режим сортировки.** Новая настройка панели `PanelSortMode`: `Manual` (произвольный,
  ручной порядок — то, что панель и так всегда показывала) или `Name` (алфавитно по отображаемому
  имени, папки вперёд).
- **Часть 2 — drag-to-reorder.** Перетаскивание значка мышью внутри панели меняет ручной порядок;
  любое ручное перетаскивание переводит панель в `Manual`, как в Fences.

До этой фичи порядок элементов панели был побочным эффектом порядка добавления (`_allItems.Add`
всегда в хвост, см. план §1.1), никак не управляемым пользователем; для Folder Portal-панелей это
дополнительно означало, что новый файл, пойманный watcher'ом, всегда «прилипал» к концу списка
независимо от алфавита (план §1.2).

Обе части реализованы, покрыты тестами и прошли **два параллельных раунда**
`code-quality-reviewer`/`fences-ux-parity-reviewer` с фиксами. Ниже фиксируются 8 архитектурных
решений — как расширение [ADR-012](ADR-012-performance-and-resilience-hardening.md)
(batch-уведомления, `ObservableRangeCollection`, дисциплина `Reset` vs точечных уведомлений) и
[ADR-013](ADR-013-panel-item-selection-and-rename.md) (`ListBox`-контейнеры, восстановление
выделения при перестройке `FileItems`).

**Важно:** решения ниже описывают **финальное состояние кода** после ревью, а не только
первоначальный замысел плана — Решения 6 и 7 получили детали реализации (явный флаг-подавитель,
явный `PropertyChanged` для `EffectiveSortMode`), добавленные по итогам ревью и не бывшие частью
исходного черновика; Блок C (drag-to-reorder) содержит критическую деталь Win32 OLE drag&drop,
найденную **независимо двумя ревьюерами** — см. Решение 5.

---

## Решения

### Решение 1: `PanelSortMode` — новое поле `ChildWindowModel`, `Manual = 0` (обратная совместимость)

Новый enum `PanelSortMode` (`src/DesktopOrganizer.UI/Models/PanelSortMode.cs`), рядом с
`PanelViewMode`/`PanelContentFilter`:

```csharp
public enum PanelSortMode
{
    Manual = 0, // произвольный (ручной) порядок — порядок FilePaths / _allItems
    Name = 1,   // алфавитно по FileItem.DisplayName, папки вперёд
}
```

`ChildWindowModel.SortMode` — INPC-свойство через существующий `SetField` (как `ViewMode`/
`ContentFilter`), сериализуется в тот же JSON `ChildWindowsState`.

**Почему `Manual = 0`:** `System.Text.Json` в этом проекте сериализует enum'ы **числом** (для
`ChildWindowModel` не зарегистрирован `JsonStringEnumConverter`, в отличие от некоторых других
enum'ов кодовой базы), а отсутствующее в старом JSON поле десериализуется в `default` типа — `0`.
`Manual = 0` даёт **побайтовую обратную совместимость поведения**: панели, сохранённые до этой
фичи, продолжают показывать элементы ровно в том же порядке, что и раньше, без единой строчки
миграционного кода — старое необъявленное «порядок добавления» поведение буквально **и есть**
`Manual`. Будь нулём `Name`, все существующие панели молча перетасовались бы при первом же запуске
после обновления — незапрошенное изменение поведения. Дополнительно это совпадает с Fences: ручная
раскладка там — состояние по умолчанию, сортировка — явный opt-in.

**Почему поле на `ChildWindowModel`, а не в `IAppSettings`:** сортировка — свойство конкретной
панели (в Fences у каждого fence свой порядок), и `ChildWindowModel` — уже установленный «единый
источник правды вида панели», разделяемый живой панелью и окном настроек (ADR-002/ADR-005).

**Alternatives considered:**
- **`bool IsManualOrder`.** Отклонено: enum расширяем без миграции схемы (`Type`, `DateModified`,
  `Size`, `NameDescending` — очевидные будущие пункты паритета с Fences), bool потребовал бы замены
  поля целиком.
- **Отдельный persisted-список `ManualOrder : List<string>` рядом с `FilePaths`.** Отклонено — см.
  Решение 2.

---

### Решение 2: источник правды ручного порядка — существующий `_allItems`/`FilePaths`; сортировка по имени — немутирующая проекция

- `SortMode.Manual` → видимый список = `AllItems.Where(filter)` (порядок `_allItems` как есть) —
  ровно текущее поведение до фичи.
- `SortMode.Name` → видимый список = `AllItems.Where(filter).OrderBy(...)`. **`_allItems` при этом
  не мутируется**, `FilePaths` не переписывается.

**Почему так:**
- `FilePaths` **уже** был персистентным упорядоченным списком, и `ChildWindow.SyncToModel()` уже
  писал в него порядок `AllItems.Select(f => f.Path)` — фиче не понадобилось ни нового
  персистентного поля, ни изменения схемы JSON, ни миграции. Тот же принцип, что ADR-009 Решение 2
  и ADR-013 Решение 2: не заводить второй источник правды, если существующий уже несёт нужную
  информацию.
- Немутирующая сортировка означает, что переключение `Manual → Name → Manual` из окна настроек
  **возвращает прежний ручной порядок** — явное требование запроса. Мутирующая сортировка сделала
  бы этот возврат невозможным: ручной порядок терялся бы безвозвратно при первом же взгляде на
  «сортировку по имени».
- Персистентность reorder'а достаётся бесплатно: `WindowManager.PersistWindowsState` →
  `SyncAllWindowsToModels` → `ChildWindow.SyncToModel` → `FilePaths = AllItems.Select(Path)` —
  достаточно поднять существующее событие `FilesChanged` (как уже делают `RemoveFileItem`,
  `SwitchView`, `CommitRename`).

**Alternatives considered:**
- **Мутировать `_allItems` при включении `Name`** (сортировка как одноразовая команда, Fences-style
  «Sort icons by → Name»). Отклонено как основное поведение — запрос требует **опцию**
  (переключатель), а не команду. Ровно эта мутация используется **точечно** при drag-триггерном
  переходе `Name → Manual` — см. Решение 6.
- **Отдельный persisted `ManualOrder : List<string>`.** Отклонено: дублирует `FilePaths`, требует
  синхронизации на трёх существующих путях мутации (add/remove/rename) и политики для расхождений
  между списками — весь этот класс багов не существует, пока порядок хранится ровно в одном списке.

---

### Решение 3: точка применения сортировки — `ChildWindowViewModel.ApplyFilterAndSort`, без нового хука в `BaseViewModel`

`ApplyFilter()` переименован в `ApplyFilterAndSort()`:

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

`OnSourceChanged` уже был переопределён на этот метод — значит **все** пути перестройки (`AddFiles`,
`RemoveFile`, `RemoveFileByPath`, `ReplaceAll`, тик Folder Portal-резинка, post-rename
`OnSourceChanged`) автоматически получили сортировку без единой правки в каждом из них.

**Почему не `protected virtual IEnumerable<FileItem> ApplySort(...)` в `BaseViewModel`:**
`BaseViewModel` ничего не знает про `ChildWindowModel`, а единственный наследник с моделью —
`ChildWindowViewModel`, который уже владеет единственной точкой проекции. Хук в базовом классе был
бы «сверху вниз» без второго пользователя и дублировал бы уже существующую точку расширения.
Прецедент `ResolveDisplayName` (ADR-013 Решение 3) — не контрпример: тот хук нужен **внутри**
`AddFilesCore`, куда наследник иначе не дотягивается.

**Почему не `CollectionViewSource.SortDescriptions`/`ICollectionView` в XAML:** сортировка нужна и
как источник порядка для трансляции индексов drag-reorder (Решение 5) — на уровне VM, не View;
`ICollectionView` поверх `ObservableRangeCollection` с `Reset`-уведомлениями (ADR-012) добавил бы
лишний слой пересортировки на каждую перестройку; две разные `DataTemplate` (список/плитка)
потребовали бы дублирования `CollectionViewSource` либо общего ресурса с shared-состоянием между
панелями. Отклонено.

---

### Решение 4: компаратор — собственный natural-order в Core, без P/Invoke; папки вперёд

Новые чистые (без I/O, без WPF) типы в `DesktopOrganizer.Core.Models`:

- **`NaturalStringComparer`** (`Compare(string?, string?)`) — посегментное сравнение цифровых/
  нецифровых фрагментов; цифровые фрагменты сравниваются как числа произвольной длины (магнитуда
  через `TrimLeadingZeros` + одинаковая длина после отсечения нулей ⇒ ordinal-сравнение спанов;
  тай-брейк по исходной длине для различающихся только ведущими нулями строк — `"01"` перед `"1"`),
  нецифровые — `StringComparison.CurrentCultureIgnoreCase`; смешанный случай (один фрагмент цифровой,
  другой нет — «форма» расходится) падает назад на культурное сравнение сырых фрагментов, оставаясь
  тотальным, а не бросая исключение. `null`/пустая строка — «меньше» любой непустой строки, равны
  между собой.
- **`PanelItemSortComparer.ByName`** (`IComparer<FileItem>`) — трёхуровневый порядок: (1)
  `IsDirectory` (папки перед файлами — `y.IsDirectory.CompareTo(x.IsDirectory)`, обратный обычному
  bool-порядку), (2) `DisplayName` через `NaturalStringComparer.Instance`, (3) тай-брейк по `Path`
  (`OrdinalIgnoreCase`).

Оба — `static readonly`/singleton (`Instance`/`ByName`), без аллокаций на вызов, без per-call
состояния.

**Почему `DisplayName`, а не `Name`:** пользователь сортирует то, что видит; для legacy
managed-элементов `Name` содержит GUID-каталог в пути (ADR-013 Решение 3), и сортировка по нему
выглядела бы случайной.

**Почему тай-брейк по `Path` обязателен:** два элемента панели легально могут иметь одинаковый
`DisplayName` — разные managed GUID-каталоги с одинаковым оригинальным именем файла (ADR-013 Risk
R11). Без тай-брейка их взаимный порядок не был бы определён и мог бы «дрожать» между перестройками
одного и того же набора.

**Почему собственный natural-order, а не P/Invoke `StrCmpLogicalW` (shlwapi):**
`StrCmpLogicalW` дал бы побайтовый паритет с Проводником, но это P/Invoke, а инвариант проекта —
«P/Invoke только за `INativeApi`». Заводить нативный вызов ради сравнения двух строк означало бы
либо нарушить инвариант, либо протащить `INativeApi` в компаратор, вызываемый O(n log n) раз на
каждую перестройку списка панели и обязанный оставаться юнит-тестируемой чистой функцией — прецедент
`GridSnap` (ADR-011), `DesktopGestureStateMachine` (ADR-012), `ManagedItemName`/`FileNameValidation`
(ADR-013).

**Trade-off (Risk R11 плана):** возможны расхождения с Проводником в экзотических случаях
(культурная коллация диакритики, символы вне BMP). Осознанно принято.

**Alternatives considered:**
- **`StringComparer.OrdinalIgnoreCase`.** Отклонено: `file10` оказался бы перед `file2` — заметное
  расхождение с Проводником на реальных нумерованных именах.
- **`StringComparer.CurrentCultureIgnoreCase`** как единственный механизм. Отклонено: культурно
  корректно, но не числово-осведомлённо; используется **внутри** natural-компаратора для сравнения
  нецифровых фрагментов.
- **Папки и файлы вперемешку.** Отклонено: расходится и с Проводником, и с рабочим столом Windows.

---

### Решение 5: `BaseViewModel.MoveItem` — трансляция видимого индекса в полный набор через якорение к видимому соседу

Новый метод `BaseViewModel.MoveItem(FileItem item, int visibleInsertIndex) : bool`.
`visibleInsertIndex` — индекс **щели вставки** в текущем видимом `FileItems`, `[0, FileItems.Count]`.

Реализованный алгоритм:

```
1. vFrom = _fileItems.IndexOf(item); если < 0 → false (элемент сейчас не виден)
2. если visibleInsertIndex == vFrom || visibleInsertIndex == vFrom + 1 → false (щель вплотную к
   собственной позиции элемента — drop обратно на себя, no-op)
3. vTo = visibleInsertIndex > vFrom ? visibleInsertIndex - 1 : visibleInsertIndex
4. _allItems.Remove(item)
5. rest = видимый список БЕЗ item (FileItems.Where(f => !ReferenceEquals(f, item)))
   если rest пуст        → allIndex = 0
   иначе если vTo == 0    → allIndex = _allItems.IndexOf(rest[0])       // ПЕРЕД первым видимым
   иначе                  → allIndex = _allItems.IndexOf(rest[vTo-1])+1 // СРАЗУ ПОСЛЕ предыдущего видимого
6. _allItems.Insert(allIndex, item)
7. _fileItems.Move(vFrom, vTo)   // ObservableCollection<T>.Move → одно уведомление Move, не Reset
8. → true
```

**Почему трансляция через «видимый сосед-якорь»:** при активном `ContentFilter` видна лишь часть
`AllItems`. Наивное `_allItems.Move(vFrom, vTo)` с теми же числовыми индексами, что валидны для
`FileItems`, переставило бы не тот элемент. Якорение к видимому соседу гарантирует: относительный
порядок **скрытых** фильтром элементов сохраняется — они не отбрасываются и не «схлопываются» в
конец, меняется только позиция перетаскиваемого элемента относительно видимых соседей.

**Осознанная асимметрия:** скрытые элементы, лежавшие в щели между `rest[vTo-1]` и `rest[vTo]`,
оказываются **после** вставленного элемента (правило «сразу после предшествующего видимого»).
Симметричное правило («сразу перед последующим видимым») столь же корректно; выбрано первое, потому
что при перетаскивании в **конец** списка (`vTo == rest.Count`) второго якоря просто не существует.
Зафиксировано в XML-doc и покрыто тестом (`BaseViewModelCollectionTests`, набор с чередующимися
видимыми/скрытыми элементами).

**Почему `_fileItems.Move`, а не полная перестройка через `ReplaceFileItems`:** `ReplaceFileItems`
поднимает `Reset` (ADR-012), который заставляет `ListBox` сбросить `SelectedItem` и позицию скролла.
Сразу после перетаскивания в проскролленной панели это выглядело бы как «панель прыгнула наверх» —
регрессия ровно в том жесте, ради которого фича делается. `ObservableCollection<T>.Move` даёт
точечное `Move`-уведомление: скролл и выделение сохраняются, элемент визуально переезжает.

**Почему в `BaseViewModel`, а не в `ChildWindowViewModel`:** `_allItems`/`_fileItems`/`_allItemPaths`
приватны для `BaseViewModel` — только он вправе мутировать их согласованно. `MoveItem` **не
трогает** `_allItemPaths` — множество путей не меняется, только их порядок; зафиксировано в
XML-doc и тесте (повторный `AddFiles` с тем же путём после `MoveItem` по-прежнему дедуплицируется).

---

### Решение 6: `ChildWindowViewModel.ReorderItem` — авто-переключение `Name → Manual` с материализацией текущего видимого порядка, и явный флаг-подавитель `FilesChanged`

```csharp
public void ReorderItem(FileItem item, int visibleInsertIndex)
{
    if (IsPortal) return;

    var switchedToManual = false;
    if (EffectiveSortMode == PanelSortMode.Name)
    {
        SortAllItems(PanelItemSortComparer.ByName);

        _suppressFilesChangedForSortModeSwitch = true;
        try { Model.SortMode = PanelSortMode.Manual; }
        finally { _suppressFilesChangedForSortModeSwitch = false; }

        switchedToManual = true;
    }

    var moved = MoveItem(item, visibleInsertIndex);

    if (moved || switchedToManual)
        FilesChanged?.Invoke(this, EventArgs.Empty);
}
```

**Почему авто-переключение, а не предупреждение:** в Fences любое ручное перетаскивание значка
переводит fence в ручную раскладку — это и есть ожидаемый жест. Модальное подтверждение из панели с
`WS_EX_NOACTIVATE` технически проблемно (ADR-013 R1: `MessageBox` из неактивируемого окна — отдельный
риск), а немодальный вопрос посреди drag'а разрушил бы сам жест. Отклонено.

**Почему материализация (`SortAllItems(PanelItemSortComparer.ByName)`) обязательна и идёт до
присваивания `SortMode`:** без неё `_allItems` остался бы в старом ручном порядке, и в момент
переключения на `Manual` список **скачком** перестроился бы в этот старый порядок — а затем ещё и
перетаскиваемый элемент встал бы не туда, куда его тянули. Материализация сортированного порядка в
`_allItems` **до** переключения делает переход визуально невидимым (проекция до и после переключения
идентична по ссылкам, попадает под ранний выход `ReplaceFileItems` — Решение 8), и только затем
`MoveItem` двигает элемент в уже стабилизированном порядке — WYSIWYG-результат перетаскивания.

**Почему это не противоречит Решению 2:** Решение 2 запрещает мутировать `_allItems` при *включении*
сортировки по имени в настройках (иначе теряется ручной порядок «просто от того, что посмотрел»).
Здесь мутация — следствие **явного разрушающего сортировку действия пользователя** (перетаскивание
мышью): он уже решил задать порядок руками, старый ручной порядок в этот момент и так перестаёт быть
актуальным. Итог для пользователя — два предсказуемых правила: переключение режима **в
настройках/меню** ничего не разрушает (`Manual → Name → Manual` возвращает прежний ручной порядок в
точности); перетаскивание значка мышью **фиксирует** видимый порядок как новый ручной.

**Деталь реализации, добавленная по итогам ревью (MAJOR-3, отличие от первоначального замысла
плана):** `_suppressFilesChangedForSortModeSwitch` — явный флаг-подавитель, выставляемый строго на
время присваивания `Model.SortMode = Manual`. Первоначальный замысел плана полагался на неявный
побочный эффект: `OnModelPropertyChanged` реагирует на смену `SortMode` вызовом
`ApplyFilterAndSort()` + подъёмом `FilesChanged`, и план предполагал, что этого одного подъёма
достаточно (а второй, из-за `MoveItem`, будет отдельным явным вызовом в конце `ReorderItem` —
неявно предполагая, что порядок вызовов гарантирует «ровно один подъём»). Ревью указало, что это
не контракт, а совпадение: если бы, например, кто-то в будущем сделал `SortMode`-присваивание
условным или `ApplyFilterAndSort` перестала быть тождественной операцией в момент переключения,
`FilesChanged` мог бы подняться дважды (один раз до фактического перемещения элемента —
наблюдатели увидели бы «изменилось», хотя `MoveItem` ещё не выполнился) или не подняться вовсе.
Финальная реализация делает это явным контрактом: `OnModelPropertyChanged` **не** поднимает
`FilesChanged` для `SortMode`, пока `_suppressFilesChangedForSortModeSwitch == true`; `ReorderItem`
сам поднимает `FilesChanged` **ровно один раз**, **строго после** того, как и переключение режима,
и `MoveItem` уже полностью применены (`moved || switchedToManual` — гарантирует подъём и при чистом
`Manual`-перемещении, и при переходе с сопутствующим переключением режима, даже если само
перемещение оказалось no-op'ом, например drop в ту же щель после материализации).

`MaterializeSortOrder` реализован как `BaseViewModel.SortAllItems(IComparer<FileItem>)` —
переупорядочивает `_allItems` на месте через `List<T>.Sort` (не гарантированно стабильный, но
безразлично — `PanelItemSortComparer.ByName` тотален вплоть до `Path`, так что для любых двух
различных элементов существует ровно один валидный результат сравнения), не трогает
`_allItemPaths`, не поднимает `OnSourceChanged` — вызывающий сам решает, когда перестраивать.

---

### Решение 7: портальные панели — `Manual` недоступен, `EffectiveSortMode` всегда `Name`, и это свойство наблюдаемо

```csharp
public PanelSortMode EffectiveSortMode => IsPortal ? PanelSortMode.Name : Model.SortMode;
```

Drag-reorder в портале отклоняется на уровне `ReorderItem` (`if (IsPortal) return;`) и на уровне
`TryResolveReorderItem` (`DragOver`/`Drop`, `Effects = None`). В окне настроек `ComboBox` сортировки
для портальной панели **disabled** (`IsEnabled="{Binding SelectedPanelSupportsManualOrder}"`) с
поясняющим тултипом. `Model.SortMode` при этом **не перезаписывается** — панель, временно ставшая
порталом и вернувшаяся обратно, сохраняет свой прежний ручной/`Name`-режим.

**Почему так:**
1. Ручной порядок портала физически негде хранить: `SyncToModel` не персистит `FilePaths` для
   портальных панелей, а список пересобирается из `Directory.GetFileSystemEntries` при каждом
   `ResyncRequired`. «Поддержать с ограничениями» означало бы завести второй персистентный список
   порядка именно для порталов, с политикой слияния для появившихся/исчезнувших файлов — ровно тот
   класс сложности, которого избегает Решение 2.
2. Следует уже установленному в проекте паттерну асимметрии portal vs regular: `RemoveFileCommand`
   заблокирован для портала, rename — разрешён (ADR-013). Критерий разделения один: операция над
   реальным файлом (rename) разрешена, операция над курируемым списком панели (remove, порядок) — не
   имеет смысла для живого зеркала.
3. Чинит дефект «новый файл прилипает к концу портальной панели» (план §1.2) бесплатно: файл,
   пойманный watcher'ом, встаёт на алфавитное место, а не в конец.

**Деталь реализации, добавленная по итогам ревью (CRITICAL-2, отличие от первоначального замысла
плана):** `EffectiveSortMode` — вычисляемое свойство без собственного поля, и в первоначальной
реализации оно не имело собственного `PropertyChanged`, полагаясь на то, что оно наблюдается только
опосредованно через что-то ещё. `code-quality-reviewer` указал, что это ломает портальный гейтинг в
UI: контекстное меню заголовка панели (`Templates.xaml`, пункты «Manual»/«By name») биндит
`IsChecked` не к `Model.SortMode`, а к `EffectiveSortMode` — и без явного `PropertyChanged` для
этого имени меню продолжало бы показывать устаревшую галочку после любого события, которое меняет
именно то, что стоит **за** `EffectiveSortMode`, но не сам `Model.SortMode`. Два таких события: (а)
`IsPortal` переключается (`LoadFromModel` при первичном подключении портала, обе ветки
`SelectSourceFolder` — включение и выключение) — здесь `EffectiveSortMode` меняется от Portal-force
к `Model.SortMode` или обратно, при неизменном самом `Model.SortMode`; (б) `Model.SortMode` меняется
напрямую (`OnModelPropertyChanged`) — здесь `EffectiveSortMode` меняется вслед за ним 1:1, но раз
это отдельное публичное свойство, у него должно быть собственное уведомление, а не расчёт на то, что
подписчик пересчитает его заодно с чем-то ещё. Финальная реализация добавляет
`OnPropertyChanged(nameof(EffectiveSortMode))` в трёх местах: оба существующих подъёма
`OnPropertyChanged(nameof(IsPortal))` (`LoadFromModel`, `SelectSourceFolder`) и внутри
`OnModelPropertyChanged`, сразу после ветки `SortMode`. Без этого фикса портальный гейтинг был бы
корректен только по данным (`ReorderItem`/`TryResolveReorderItem` всё равно отклонили бы drag), но
визуально сломан — тот самый класс бага, который эта фича обязана не допустить, будучи прямым
паритетом с Fences.

**Alternatives considered:**
- **Разрешить `Manual` в портале без персистентности.** Отклонено: порядок терялся бы при каждом
  перезапуске и при каждом `ResyncRequired` (переполнение буфера watcher'а) — воспринималось бы как
  баг.
- **Разрешить `Manual` с отдельным persisted-списком порядка для портала.** Отклонено по причине из
  п.1; кандидат в бэклог при появлении отдельного запроса.

---

### Решение 8: `BaseViewModel.ReplaceFileItems` не поднимает `Reset`, если проекция ссылочно идентична текущей

`ReplaceFileItems` материализует входную последовательность и сравнивает её с текущим `_fileItems`
поэлементно **по ссылке** (`SequenceEqualByReference`); при полном совпадении — ранний выход без
`_fileItems.ReplaceAll`, без `Reset`-уведомления и без `FileCountText`-уведомления.

**Почему это входит в объём именно этой фичи:** переход `Name → Manual` при drag'е (Решение 6, шаг
материализации) конструктивно порождает перестройку с идентичным по ссылкам результатом —
`SortAllItems` уже привёл `_allItems` к тому же порядку, что уже показан, поэтому следующий
`ApplyFilterAndSort()`, вызванный из `OnModelPropertyChanged` в ответ на смену `SortMode`, производит
проекцию, которая ссылочно совпадает с уже отображаемой. Без этого раннего выхода этот шаг поднял бы
видимый `Reset` (сброс скролла/выделения) ровно в момент, когда пользователь ещё держит мышь зажатой
посреди drag-жеста — прямая регрессия того самого UX, ради которого фича делается.

Альтернатива — точечный флаг-подавитель конкретно вокруг присваивания `Model.SortMode` (аналогично
`_suppressFilesChangedForSortModeSwitch` из Решения 6, но для перестройки коллекции, а не для
`FilesChanged`) — отклонена: это добавило бы ещё одно скрытое временное состояние в VM, которое
пришлось бы держать в уме при любой будущей правке `OnModelPropertyChanged`. Проверка на ссылочное
равенство — локальная в `BaseViewModel`, безусловно корректная (одинаковая по ссылкам коллекция ⇒
одинаковые уведомления объективно не нужны никому) и попутно гасит лишние `Reset`'ы на других путях
(например, тик Folder Portal-резинка, не изменивший состав после фильтрации).

**Trade-off:** если когда-нибудь понадобится «перерисовать список принудительно, не меняя состава»,
это перестанет работать через `ReplaceFileItems` (перестройка станет no-op'ом). Такого сценария
сейчас нет — перерисовка отдельных элементов идёт через INPC самого `FileItem`; зафиксировано в
XML-doc.

---

## Последствия

- **Публичные контракты не изменились** сверх аддитивного расширения `ChildWindowModel` полем
  `SortMode` (сериализуется числом, `Manual = 0` — полная обратная совместимость persistence, без
  миграции). `IPanelItemStorageService`, `IFileItemRenameService`, `IWindowManager`, `IIconProvider`
  не затронуты.
- **Новые чистые (без I/O, без WPF, без P/Invoke) типы в Core**, расширяющие прецедент
  `GridSnap`/`DesktopGestureStateMachine`/`ManagedItemName`/`FileNameValidation`:
  `NaturalStringComparer`, `PanelItemSortComparer` — оба покрыты юнит-тестами
  (`NaturalStringComparerTests`, `PanelItemSortComparerTests`).
- **`BaseViewModel`** получил `SortAllItems(IComparer<FileItem>)` и `MoveItem(FileItem, int) : bool`
  — оба не трогают `_allItemPaths`; `MoveItem` даёт точечное `Move`-уведомление вместо `Reset`.
  `ReplaceFileItems` получил ранний выход при ссылочно идентичной проекции (Решение 8) — покрыто
  расширенным `BaseViewModelCollectionTests`.
- **`ChildWindowViewModel`** получил `EffectiveSortMode`, `SetSortModeCommand`, `ReorderItem`;
  `ApplyFilter` переименован в `ApplyFilterAndSort`; `OnModelPropertyChanged` реагирует на
  `SortMode` так же, как на `ContentFilter` (перестройка + `FilesChanged`, с явным подавлением на
  время `ReorderItem` — Решение 6); обе ветки `SelectSourceFolder` (включение/выключение портала)
  теперь вызывают `ApplyFilterAndSort()` явно, чтобы `EffectiveSortMode`-переключение отражалось
  немедленно, а не при следующей случайной перестройке.
- **`MainWindowViewModel`** получил `SortModes` (`Enum.GetValues<PanelSortMode>()`),
  `SelectedPanelSortMode` (fallback `Manual`, по паттерну `SelectedPanelViewMode`),
  `SelectedPanelSupportsManualOrder` (`false` для панели с непустым `SourceFolderPath`) — оба новых
  свойства добавлены в `RaiseSelectedPanelProperties()`.
- **UI**: `MainWindow.xaml` — строка `Sort order` + `ComboBox` в секции настроек выбранной панели
  (disabled + тултип для портала). `ChildWindow.xaml` — секция контекстного меню заголовка
  `Arrange icons` с чекируемыми пунктами `Manual`/`By name` (биндинг `IsChecked` на
  `EffectiveSortMode`, не `Model.SortMode` — важно для корректного гейтинга портала, см. Решение 7);
  `FileListHost` получил `AllowDrop`, `PreviewMouseMove`/`DragOver`/`DragLeave`/`Drop` для формата
  `DesktopOrganizer.PanelItemReorder`, и индикатор вставки (`ReorderInsertionIndicator`, `Border`
  поверх `FileListHost`, вертикальная линия в Wrap-виде / горизонтальная в List-виде).
- **`ChildWindow.xaml.cs`**: старт жеста переиспользует существующий
  `FileListHost_PreviewMouseLeftButtonDown` (запоминает `_reorderStartPoint`/`_reorderCandidate`, не
  ставит `e.Handled` — чтобы не сломать штатное выделение `ListBox`); порог начала drag'а —
  системный (`SystemParameters.MinimumHorizontal/VerticalDragDistance`); payload —
  `item.Path` (string), не сам `FileItem` — на drop элемент восстанавливается поиском по пути в
  собственных `FileItems`, что попутно даёт бесплатный гард «drag из чужой панели» (путь не найден ⇒
  отказ). `ComputeInsertionGap` вынесен в чистый статический метод, принимающий примитивы
  (`containerIndex`/`cursorPosition`/`containerSize`/`viewMode`/`itemCount`), а не живые UI-объекты —
  юнит-тестируем без реального `ListBox` (`ChildWindowReorderGeometryTests`, новый тестовый файл).

### Критическая деталь реализации Win32 OLE drag&drop (найдена независимо двумя ревьюерами) — инвариант для будущих правок `FileListHost_DragOver`/`FileListHost_Drop`

Риск R1 плана (**CRITICAL**): `AllowDrop="True"` на `FileListHost` делает его собственной drop-целью
поверх уже существующего входящего `DataFormats.FileDrop` (файлы, перетаскиваемые с рабочего стола —
главный сценарий приложения, `Window.OnDragEnter`/`OnDrop`). Реализация должна была гарантировать,
что чужой формат продолжает всплывать до обработчиков окна необработанным.

Первая версия реализации не ставила `e.Handled` для чужого формата, но **всё же трогала
`e.Effects`** (например, устанавливала `Effects = None` как часть общей отбраковки перед проверкой
формата). Оба ревьюера — `code-quality-reviewer` и `fences-ux-parity-reviewer` — **независимо друг
от друга** указали, что этого недостаточно: по контракту OLE drag&drop то значение
`DragEventArgs.Effects`, которое было выставлено на **последнем** `DragOver` перед тем, как
пользователь отпустил кнопку мыши, определяет, вызовет ли система `IDropTarget.Drop` или молча
`IDropTarget.DragLeave` вместо него — независимо от того, стоит `Handled` или нет.
`Window` переопределяет `OnDragEnter`/`OnDrop`, но **не** `OnDragOver` — значит, если бы
`FileListHost_DragOver` для чужого формата хоть раз выставил `Effects = None`, ничто выше по
дереву визуальных элементов не успело бы пересчитать `Effects` на этот же тик, и входящий
drag&drop файлов с рабочего стола **перестал бы срабатывать функционально** (не просто показывал
бы неверный курсор) в тот момент, когда курсор находится над `FileListHost` в момент отпускания
кнопки.

Финальная реализация исправляет это структурно: `FileListHost_DragOver` и `FileListHost_Drop` оба
начинаются с `if (!e.Data.GetDataPresent(PanelItemReorderFormat)) return;` **до** любого обращения к
`e.Effects` или `e.Handled` — для чужого формата метод выходит, не тронув `DragEventArgs` вообще, ни
единым полем. Только после того, как формат подтверждён как свой (`PanelItemReorderFormat`),
методы имеют право трогать `Effects`/`Handled`.

**Это фиксируется здесь как явный инвариант**, а не просто описание текущего кода, потому что деталь
неочевидна (легко ошибочно посчитать «не ставлю `Handled`» достаточным условием невмешательства) и
без явной фиксации в ADR будущая правка этих обработчиков (например, добавление ранней проверки
`IsPortal` до проверки формата, или рефакторинг в общий хелпер) может случайно вернуть регрессию,
сломав главный сценарий приложения — и это не будет обнаружено в CI (весь этот путь недоступен вне
живого Windows OLE drag&drop, см. manual verify R1 в ROADMAP Фазе 9).

### Manual verify (Win10/Win11) — выполнено пользователем на Windows 11 24H2, 2026-09-27

Признано достаточным (Windows 10 отдельно не проверяется), включая R1 (входящий drag&drop файлов с
рабочего стола в панель после добавления `AllowDrop`/обработчиков reorder на `FileListHost`) и R2
(`DoDragDrop` из окна с `WS_EX_NOACTIVATE`). Исторический полный список — `docs/PLANS/panel-item-sorting.md`,
§9 (17 пунктов), см. также `docs/ROADMAP.md`, Фаза 9.

---

## Amendment 2026-09-27: сортировка финализирована — `Manual`/`Name` окончательны, Type/Date/Size не планируются

**Решение:** `PanelSortMode` остаётся ровно двумя значениями — `Manual` и `Name`. Расширение enum'а
дополнительными режимами (`Type`, `Date`, `Size`, `NameDescending` и т.п.), упомянутыми в Решении 1 как
«очевидные будущие пункты паритета с Fences», **не планируется**. Соответствующий пункт бэклога Фазы 10
(«Сортировка по типу/дате») закрыт как **won't-do**.

**Обоснование:** владелец продукта считает двух режимов достаточно для сценария использования, ради
которого делался паритет со Stardock Fences — расширение `PanelSortMode` не несёт достаточной ценности
относительно стоимости (новые компараторы, UI для каждого режима, миграция ожиданий пользователей от
`Manual`-порядка). Явное решение, а не забытый бэклог-пункт.

**Последствие:** enum `PanelSortMode` (Решение 1) сохраняется в текущем виде (`Manual = 0`, `Name = 1`)
без добавления новых членов. `PanelItemSortComparer`/`NaturalStringComparer` (Решение 4) не получают
второй компаратор для даты/размера/типа. Бэклог-пункты, ссылавшиеся на будущее расширение сортировки
(ROADMAP, Фаза 10, «Бэклог, найденный ревью»), закрыты как won't-do со ссылкой на этот amendment.

---

## Amendment 2026-09-27 (ADR-036): drag-out закрывает Risk R7

[ADR-036](ADR-036-panel-item-drag-out.md) реализует перетаскивание значка **из** панели (наружу, на рабочий
стол/в проводник, или в другую панель): один `DoDragDrop` с тремя форматами (внутренний reorder-формат этого
ADR + `FileDrop` + `Preferred DropEffect=Move`), self-claim → `Reordered`, чужая панель → `TransferredTo`,
иначе `External` (проверка `MovedOutVerifier`). Это закрывает Risk R7 плана
`docs/PLANS/panel-item-sorting.md` (сценарий «перетащить значок из панели наружу» был вне объёма этого
ADR). Инвариант невмешательства в `DragEventArgs` для чужих форматов (`FileListHost_DragOver`/`Drop`) выше
сохранён.

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-27 | [ADR-036](ADR-036-panel-item-drag-out.md) | Drag-out из панели; Risk R7 закрыт | Оговорки «перетаскивание наружу вне объёма» |
| 2026-09-27 | (финализация сортировки) | `PanelSortMode` = `Manual`/`Name` окончательно | Решение 1: «очевидные будущие пункты Type/Date/Size» |

---

**Расширяет:** [ADR-012](ADR-012-performance-and-resilience-hardening.md) (батч-уведомления,
`ObservableRangeCollection`, `Reset`-дисциплина), [ADR-013](ADR-013-panel-item-selection-and-rename.md)
(`ListBox`-контейнеры, восстановление выделения по пути после перестройки, инвариант
`WS_EX_NOACTIVATE`).

**Источник:** [docs/PLANS/panel-item-sorting.md](../docs/PLANS/panel-item-sorting.md) — план
считается выполненным и остаётся историческим артефактом (см. прецедент
`docs/PLANS/icon-selection-and-rename.md` / ADR-013).
