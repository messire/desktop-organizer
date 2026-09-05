# План: Фильтр содержимого панели (Panel Content Filter)

Статус: PLANNED
Scope: **L** (Core модель + UI Model + ViewModel + persistence + XAML)
Источник: запрос пользователя (паритет с Fences — фильтрация типов содержимого панели)

---

## 1. Постановка и архитектурное решение

Панель должна уметь скрывать/показывать содержимое по типу элемента. Режимы (enum `PanelContentFilter`):

| Режим | Поведение |
|---|---|
| `ShowAll` | показывать всё (по умолчанию) |
| `HideAll` | скрыть все элементы (панель «свёрнута по содержимому») |
| `HideShortcuts` | скрыть `.lnk` ярлыки |
| `FoldersOnly` | показывать только папки |

### Ключевая находка по архитектуре (важно для исполнителя)

XAML биндится на `FileItems` (`ContentControl Content="{Binding FileItems}"`, ChildWindow.xaml:87).
Это **`BaseViewModel.FileItems`** (BaseViewModel.cs:21), а НЕ `Model.FileItems`.
Весь runtime-поток (`AddFiles` / `RemoveFileByPath` / `ReplaceAll`, FSW-портал) работает именно с
`BaseViewModel.FileItems`.

Следствие: фильтр нельзя реализовать как простое «удаление из коллекции» — иначе скрытые элементы
потеряются при сохранении и при переключении режима обратно на `ShowAll`. Нужна **раздельность
полного набора и видимого набора**.

### Решение (выбранный вариант B)

- **Вариант A (отклонён):** физически удалять отфильтрованные `FileItem` из `FileItems`. Минус:
  теряем «истинный» набор; FSW-события и persistence ломаются; обратное переключение не восстанавливает.
- **Вариант B (выбран):** держать **полный набор** (`_allItems`, приватный список в ViewModel) как
  источник правды, а `FileItems` сделать **проекцией** — пересобирать его из `_allItems` по текущему
  фильтру. Все мутации (`AddFiles`/удаление/FSW) идут в `_allItems`, затем триггерят `ApplyFilter()`,
  который перезаполняет `FileItems`. Минус: больше изменений в BaseViewModel; нужно аккуратно с порталом.
- **Вариант C (отклонён):** `ICollectionView.Filter` поверх `FileItems`. Минус: фильтр-предикат на
  view не переживает persistence-логику, а текущая `RemoveFile*` работает напрямую с коллекцией —
  смешение моделей. Для текущей кодовой базы B чище.

> Persistence хранит только **режим фильтра** (`ContentFilter`), а не отфильтрованный список.
> `FilePaths`/портал остаются полным набором — фильтр это представление, не данные.

---

## 2. Распознавание типов в Core (`FileItem`)

Файл: `src/DesktopOrganizer.Core/Models/FileItem.cs`

Добавить вычисляемые свойства (без I/O сверх существующего `Exists`):

```csharp
/// <summary>True, если путь указывает на каталог.</summary>
public bool IsDirectory => System.IO.Directory.Exists(Path);

/// <summary>True, если это .lnk ярлык (по расширению, регистронезависимо).</summary>
public bool IsShortcut =>
    string.Equals(Type, ".lnk", System.StringComparison.OrdinalIgnoreCase);
```

Замечания:
- НЕ резолвить цель ярлыка (это I/O и нативщина) — достаточно расширения `.lnk`. `.url` опционально
  можно учесть позже; в рамках этой фичи — только `.lnk` (по требованию).
- `IsDirectory` опирается на `Directory.Exists`; для отсутствующих путей вернёт `false` — это
  допустимо (битый путь не считается папкой). Существующий инвариант «битые пути не валят загрузку»
  сохраняется.

---

## 3. Enum режима фильтра

Новый файл: `src/DesktopOrganizer.UI/Models/PanelContentFilter.cs`
(рядом с `PanelViewMode.cs`, тот же namespace `DesktopOrganizer.UI.Models`).

```csharp
namespace DesktopOrganizer.UI.Models;

/// <summary>Какие элементы панель показывает.</summary>
public enum PanelContentFilter
{
    ShowAll,
    HideAll,
    HideShortcuts,
    FoldersOnly,
}
```

---

## 4. Модель панели + persistence (`ChildWindowModel`)

Файл: `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs`

- Добавить backing-поле `private PanelContentFilter _contentFilter = PanelContentFilter.ShowAll;`
- Добавить свойство `ContentFilter` через `SetField` (по образцу `ViewMode`, строки 81–85), чтобы
  изменение поднимало `PropertyChanged` и ViewModel реагировал.

```csharp
public PanelContentFilter ContentFilter
{
    get => _contentFilter;
    set => SetField(ref _contentFilter, value);
}
```

Persistence (JSON):
- `ContentFilter` — публичное auto-через-SetField свойство, сериализуется автоматически
  `System.Text.Json` (как `ViewMode`). **Проверить, что enum пишется как строка** — найти, есть ли
  `JsonStringEnumConverter` в опциях сериализатора (см. §7). Если `ViewMode` уже сохраняется строкой —
  ничего не делать; если числом — оставить как есть для консистентности, но зафиксировать в DoD.
- Дефолт `ShowAll` → старые JSON без поля десериализуются в `ShowAll` (обратная совместимость).
  **Миграция файла настроек не требуется.**

---

## 5. Логика фильтрации (`BaseViewModel` + `ChildWindowViewModel`)

### 5.1 BaseViewModel — разделить полный и видимый наборы

Файл: `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs`

- Ввести приватный полный набор: `private readonly List<FileItem> _allItems = [];`
- `AddFiles`: добавлять валидные элементы в `_allItems` (с тем же дедупом по `Path`), затем вызывать
  виртуальный хук `OnSourceChanged()` вместо прямой записи в `FileItems`.
- `RemoveFile` / `RemoveFileByPath` / `ReplaceAll`: мутировать `_allItems`, затем `OnSourceChanged()`.
- Добавить `protected virtual void OnSourceChanged()` — в базовом классе он просто проецирует
  `_allItems` целиком в `FileItems` (поведение по умолчанию = как сейчас).
- Добавить `protected internal IReadOnlyList<FileItem> AllItems => _allItems;`
- `FileCountText` — решить, считает он видимые или все. Рекомендация: **видимые** (то, что на экране).

> Альтернатива с меньшим объёмом: не трогать BaseViewModel, а сделать всё в ChildWindowViewModel,
> переопределив поведение. Но мутаторы `AddFiles/RemoveFileByPath/ReplaceAll` живут в базе и пишут
> прямо в `FileItems` — без хука их не перехватить чисто. Поэтому хук `OnSourceChanged()` обязателен.

### 5.2 ChildWindowViewModel — применять фильтр

Файл: `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs`

- Переопределить `OnSourceChanged()` → вызвать `ApplyFilter()`.
- `ApplyFilter()`:
  1. вычислить видимый набор из `AllItems` по `Model.ContentFilter` (предикат, см. ниже);
  2. синхронизировать `FileItems` с видимым набором. Для простоты и корректности INPC:
     `FileItems.Clear()` + добавить отфильтрованные (порядок `AllItems` сохраняется).
- Предикат:
  - `ShowAll` → `true`
  - `HideAll` → `false`
  - `HideShortcuts` → `!item.IsShortcut`
  - `FoldersOnly` → `item.IsDirectory`
- В `OnModelPropertyChanged` (строки 312–316) добавить ветку:
  `if (e.PropertyName == nameof(ChildWindowModel.ContentFilter)) ApplyFilter();`
- При смене фильтра поднять `FilesChanged` для persistence — добавить вызов после применения
  (либо обработать в команде, см. §6). Важно: persistence сохраняет **режим**, не список.
- `LoadFromModel` (строки 158–171): после `AddFiles(...)` уже сработает `OnSourceChanged → ApplyFilter`,
  но `Model.ContentFilter` к этому моменту должен быть загружен из модели (он есть в `model`). Убедиться,
  что `AddFiles` вызывается **после** `Model = model`. Сейчас порядок верный.

### 5.3 Взаимодействие с Folder Portal (FSW)

- FSW-события (`OnFolderChanged`) уже идут через `AddFiles/RemoveFileByPath/ReplaceAll` → попадут в
  `_allItems` → `ApplyFilter()` отработает автоматически. Отдельной логики не требуется.
- Проверить: `RemoveFileByPath` ищет в коллекции — переключить на поиск в `_allItems`.

---

## 6. UI выбора режима (`ChildWindow.xaml` + code-behind)

Файл: `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (+ `ChildWindow.xaml.cs`)

Разместить выбор фильтра в **контекстном меню заголовка** (Border.ContextMenu, строки 37–42),
рядом с «Переименовать» / «Свернуть». Подменю «Фильтр содержимого» с 4 пунктами `IsCheckable`,
радио-поведение (отмечен текущий):

```xml
<MenuItem Header="Фильтр содержимого">
    <MenuItem Header="Показывать всё"   IsCheckable="True" Click="ContentFilter_Click" Tag="ShowAll"/>
    <MenuItem Header="Скрыть всё"        IsCheckable="True" Click="ContentFilter_Click" Tag="HideAll"/>
    <MenuItem Header="Скрыть ярлыки"     IsCheckable="True" Click="ContentFilter_Click" Tag="HideShortcuts"/>
    <MenuItem Header="Только папки"       IsCheckable="True" Click="ContentFilter_Click" Tag="FoldersOnly"/>
</MenuItem>
```

Code-behind `ContentFilter_Click`:
- распарсить `Tag` → `PanelContentFilter`, присвоить `ViewModel.Model.ContentFilter` (это поднимет
  INPC → `ApplyFilter`), и поднять persistence (`FilesChanged`) — лучше пробросить через команду VM.
- проставить `IsChecked` так, чтобы был отмечен только активный режим (обновить при открытии меню,
  событие `ContextMenuOpening`, или связать через биндинг+конвертер enum→bool).

> Альтернатива (предпочтительно для MVVM-чистоты, отметит ревьюер качества): команда
> `SetContentFilterCommand` в VM с параметром `PanelContentFilter` + биндинг `IsChecked` через
> `EnumToBoolConverter` (проверить, есть ли уже такой конвертер в `Converters/`; если нет —
> создать). Code-behind тогда не нужен. Решение оставить исполнителю UI.

---

## 7. Сериализация enum — проверить

Файлы persistence (найти фактический): искать по `JsonSerializer`, `JsonSerializerOptions`,
`JsonStringEnumConverter` в `src/DesktopOrganizer.Services/` и месте сохранения `ChildWindowModel`
(там же, где сериализуется `ViewMode`). Подтвердить:
- как сейчас пишется `ViewMode` (строкой/числом) — `ContentFilter` должен вести себя так же;
- наличие `JsonStringEnumConverter` желательно для читаемости файла настроек.

---

## 8. Порядок реализации

1. **Core:** `FileItem.IsDirectory`, `FileItem.IsShortcut` (+ юнит-тесты Core).
2. **Enum:** `PanelContentFilter.cs`.
3. **Model:** `ChildWindowModel.ContentFilter` + проверка JSON-сериализации.
4. **BaseViewModel:** `_allItems` + `OnSourceChanged()` хук + рефактор мутаторов.
5. **ChildWindowViewModel:** `ApplyFilter()` + реакция на `ContentFilter` + FSW-сверка.
6. **UI:** контекстное меню / команда + отметка активного режима.
7. **Тесты:** см. §9. 8. `dotnet build` + `dotnet test`.

Параллелить можно: (1+2) Core/enum независимы от (4) BaseViewModel-рефактора;
UI (6) — после VM (5).

---

## 9. Тесты (TDD)

Core:
- `IsDirectory` true для существующего каталога, false для файла/битого пути.
- `IsShortcut` true для `a.lnk`, `A.LNK`; false для `a.txt`, папки.

ViewModel (с моками `IIconProvider`/`IFileLauncher`/`IFolderWatcherFactory`):
- `ShowAll` → видны все.
- `HideAll` → `FileItems` пуст, `AllItems` не пуст.
- `HideShortcuts` → `.lnk` исключены, остальное видно.
- `FoldersOnly` → только каталоги.
- Переключение `ShowAll → HideShortcuts → ShowAll` восстанавливает полный набор (регрессия на
  «не теряем данные»).
- FSW `Created` для `.lnk` при `HideShortcuts` → попадает в `AllItems`, но не в `FileItems`.
- Смена `ContentFilter` поднимает `FilesChanged` ровно один раз.

Persistence:
- round-trip: `ContentFilter` сохраняется и восстанавливается.
- старый JSON без поля → `ShowAll`.

---

## 10. DoD (Definition of Done)

- [ ] `FileItem` имеет `IsDirectory` и `IsShortcut`; покрыты тестами.
- [ ] Enum `PanelContentFilter { ShowAll, HideAll, HideShortcuts, FoldersOnly }` создан.
- [ ] `ChildWindowModel.ContentFilter` добавлен, дефолт `ShowAll`, поднимает `PropertyChanged`.
- [ ] `ContentFilter` сериализуется/десериализуется в JSON; старые файлы без поля грузятся в `ShowAll`
      без ошибок.
- [ ] Полный набор отделён от видимого: скрытые элементы НЕ теряются и восстанавливаются при возврате
      к `ShowAll` (есть тест-регрессия).
- [ ] Каждый из 4 режимов корректно фильтрует `FileItems` (есть тесты на каждый).
- [ ] Фильтр корректно работает в режиме Folder Portal (FSW-события уважают фильтр).
- [ ] UI даёт выбрать режим из заголовка/контекстного меню панели; активный режим визуально отмечен.
- [ ] Смена режима из UI сохраняется (persistence) и переживает перезапуск.
- [ ] `DesktopIconsService` НЕ затронут (проверить git diff — файл не в изменениях).
- [ ] `dotnet build` и `dotnet test` зелёные; StyleCop без новых нарушений.

---

## 11. Артефакты (файлы)

Изменяются:
- `src/DesktopOrganizer.Core/Models/FileItem.cs`
- `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs`
- `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs`
- `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs`
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (+ `.xaml.cs`)
- (возможно) persistence-сервис — только если нужен `JsonStringEnumConverter`

Создаются:
- `src/DesktopOrganizer.UI/Models/PanelContentFilter.cs`
- (опц.) `src/DesktopOrganizer.UI/Converters/EnumToBoolConverter.cs` — если выбран MVVM-вариант UI
- тестовые файлы в проекте тестов (Core + UI ViewModel + persistence round-trip)

---

## 12. Риски

- **R1 (средний):** рефактор `BaseViewModel` (разделение наборов) затрагивает существующий поток
  add/remove/portal/FSW. Митигировать тестами-регрессиями на портал и FSW до изменений.
- **R2 (низкий):** `IsDirectory` через `Directory.Exists` — I/O на каждый доступ. Для больших папок
  при частой перефильтрации возможна задержка. Митигация: `ApplyFilter` вызывается только при смене
  набора/режима, не на каждый кадр. При необходимости кэшировать в `FileItem` при создании.
- **R3 (низкий):** enum в JSON числом сломает читаемость/совместимость при переименовании членов.
  Митигация: `JsonStringEnumConverter` (свериться с тем, как хранится `ViewMode`).
- **R4 — manual verify (Win10/Win11):** контекстное меню, отметка активного режима, визуальное
  скрытие/возврат элементов, корректность для портала Рабочего стола — проверяются только на Windows
  (вне CI). Особенно: `HideAll` не должен «схлопывать» панель в 0 (контент пуст, но панель и заголовок
  на месте).

Нативного кода (Progman/WorkerW/hooks/P-Invoke) фича НЕ требует.
