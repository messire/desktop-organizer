# Мастер-план: выделение значка + честные имена и переименование значков

Источник: пользовательский запрос от 2026-08-15 (две связанные UX-фичи паритета с Fences/Проводником).
Статус: план готов, передан реализующим агентам.
Scope суммарно: **L** (Core + Services.Abstractions + Services + UI/ViewModels + XAML + persistence-совместимость).

Обе фичи делят `FileItem`, `Templates.xaml` и путь загрузки/сохранения панели, поэтому планируются
как один связанный пункт ROADMAP («Фаза 8. UX-паритет значков»), но реализуются **раздельными
блоками A–E** с явными зависимостями (см. §7 «Порядок»).

---

## 1. Проблемы (корень)

### Фича A — нет визуального выделения значка

- `WrapViewTemplate` (`src/DesktopOrganizer.UI/Resources/Templates.xaml:89`) — это `ItemsControl` +
  `WrapPanel`. `ItemsControl` **не имеет** понятия выделения вообще: ни `SelectedItem`, ни
  контейнеров `*Item`, ни `IsSelected`. Wrap — основной/«фенсовый» вид.
- `ListViewTemplate` (`Templates.xaml:63`) — это `ListBox`, т.е. выделение технически есть, но:
  - `SelectedItem` **ни к чему не привязан** (`BaseViewModel.SelectedFile`,
    `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs:49` — мёртвое свойство, никто не пишет и не читает);
  - `ItemContainerStyle` переопределяет только `Background/Foreground/Padding`, шаблон контейнера
    остаётся дефолтным.
- **Ключевая находка (влияет на решение, а не только на стиль):** панель имеет `WS_EX_NOACTIVATE`
  (`DesktopIntegrationService.ApplyDesktopWindowStyles`, `src/DesktopOrganizer.Services/Tools/DesktopIntegrationService.cs:55`),
  т.е. окно **никогда не активируется** по клику. Дефолтная тема `ListBoxItem` рисует выделение
  через `IsSelectionActive`: у неактивного окна это блёклый серый прямоугольник, почти невидимый
  поверх кастомного фона панели (`BackgroundColor` + `BackgroundOpacity`). Поэтому **собственный
  `ControlTemplate` контейнера, реагирующий на `IsSelected` и игнорирующий `IsSelectionActive`, —
  обязательное требование, а не косметика.**

### Фича B — «мусорные» GUID-имена

- `PanelItemStorageService.BuildDestinationPath` (`src/DesktopOrganizer.Services/Tools/PanelItemStorageService.cs:407`)
  и `BuildDestinationDirectoryPath` (`:418`) формируют физическое имя как
  `{name(≤40)}_{Guid:N}{ext}` — плоская раскладка внутри одного storage root, GUID в **имени файла**
  даёт коллизионную безопасность «в один шаг», без цикла проверок.
- `FileItem.Name` (`src/DesktopOrganizer.Core/Models/FileItem.cs:53`) = `Path.GetFileName(Path)`,
  а `Templates.xaml:80` и `:105` биндятся именно на `Name` → UI показывает
  `Отчёт_9f3c1a...e21.docx`.
- Manifest (`.manifest.json`, `managedPath → originalPath`) оригинальное имя знает, но
  ([ADR-009](../../adr/ADR-009-drag-drop-move-semantics.md), Решение 2) **сознательно** не отдаёт его
  ни в `FileItem`, ни в персистентную схему панели.
- Переименования значка нет вообще (ни F2, ни контекстного меню, ни inline-редактора) — есть только
  переименование **заголовка панели** (`ChildWindow.xaml.cs:334` `BeginRename`/`CommitRename`/
  `CancelRename` + `TitleEditBox` в `ChildWindow.xaml:104`).

---

## 2. Архитектурное решение (ADR-стиль)

Ниже — 6 решений. Все они предназначены к переносу в новый **ADR-013 «Выделение и переименование
значков панели»** (`architecture-documenter`, блок E), как расширение ADR-009/ADR-012.

### Решение 1 (Фича A): оба вида — `ListBox`; выделение через штатную модель контейнеров

`WrapViewTemplate` переводится с `ItemsControl` на `ListBox` с `ItemsPanel = WrapPanel`.
Оба вида получают общий `ItemContainerStyle` (`PanelItemContainerStyle`) с собственным
`ControlTemplate`.

**Почему так:**
- Выделение, снятие выделения, клавиатурная навигация, `SelectedItem`-биндинг, hit-test по
  контейнеру — всё уже есть в `Selector`/`ListBox`; ручная реализация на `ItemsControl` (флаг
  `IsSelected` на `FileItem` + обработчики кликов) воспроизводит это хуже и дороже.
- Один общий стиль контейнера гарантирует **одинаковую** подсветку в обоих видах (требование
  паритета: в Fences подсветка не зависит от вида).

**Alternatives considered:**
- **A. `ItemsControl` + `bool IsSelected` на `FileItem` + ручные обработчики.** Отклонено: тянет
  UI-состояние в Core-модель, лишает клавиатурной навигации, дублирует логику «снять выделение».
- **B. `ListView`/`GridView`.** Отклонено: тяжелее, заточен под колонки, не даёт ничего сверх
  `ListBox` для wrap-раскладки.
- **C. Оставить wrap на `ItemsControl`, выделение только в списочном виде.** Отклонено: wrap —
  основной вид, фича была бы бесполезной.

**Trade-off:** `ListBox` вносит собственный `ScrollViewer` (внешний `ScrollViewer` из
`WrapViewTemplate` **должен быть удалён** — вложенные скроллы ломают перенос строк) и виртуализацию
(`WrapPanel` не виртуализирующая — оставляем `VirtualizingStackPanel.IsVirtualizing=False`
по факту; для типового размера панели это не проблема, панели на сотни иконок — отдельный тикет).

### Решение 2 (Фича B, корень): убрать GUID **из имени**, перенеся его в **имя каталога**

`PanelItems\{name}_{guid}{ext}` → `PanelItems\{guid}\{originalName}{ext}`
(и для папок: `PanelItems\{guid}\{OriginalFolderName}`).

**Почему так:**
- Коллизионная безопасность сохраняется ровно в той же форме («в один шаг», без racy
  check-then-create): уникален теперь каталог, а не имя файла.
- `FileItem.Name`/`Path.GetFileName` **сам по себе** становится оригинальным именем — не нужно ни
  нового персистентного поля, ни второго источника правды, ни синхронизации «имя на диске vs имя в
  UI», ни изменения схемы `ChildWindowModel.FilePaths : List<string>` (то, что ADR-009 Решение 2
  явно защищал).
- Переименование становится **единообразным** для managed и не-managed элементов: всегда физическое
  переименование (как в Проводнике/Fences), одна семантика, один класс багов.
- `IsManaged`, `Restore`, `Release`, весь manifest-контракт и ADR-010 (очистка при удалении панели)
  работают без изменений — они оперируют путями, а не раскладкой.

**Alternatives considered:**
- **Вариант 1 из запроса — персистентный `DisplayName` на `FileItem`, независимый от диска.**
  Отклонено как основное решение. Причины:
  1. вводит **второй источник правды** для имени и требует изменения персистентной схемы
     (`FilePaths: List<string>` → карта `path → displayName` или `List<PanelFileEntry>`), т.е. ровно
     то, чего ADR-009 Решение 2 избегал (миграция, `SyncToModel`/`GetSourceFiles`/`LoadFromModel`,
     layout-снапшоты в `LayoutSnapshot.PanelJsonItems`);
  2. даёт наблюдаемые расхождения: «Открыть папку с файлом» показывает в Проводнике другое имя;
     `Restore` («Убрать из панели») возвращает файл под **оригинальным** именем из manifest, молча
     теряя переименование пользователя;
  3. для **портальных** панелей (`SourceFolderPath`) персистентный `DisplayName` в принципе не живёт —
     список там не персистится, а пересобирается из папки, — т.е. пришлось бы иметь два разных
     механизма переименования для портала и обычной панели.
  Остаточно от этого варианта берётся **только** необязательный, не персистируемый
  `displayName`-override в `FileItem` (см. Решение 3) — для legacy-элементов.
- **Оставить плоскую раскладку, а имя для UI брать из manifest (`GetOriginalName(path)`).**
  Отклонено: manifest становится обязательной зависимостью **рендера** (сейчас он нужен только на
  Restore); при рассинхроне manifest/диска (ADR-009 Решение 3 прямо допускает такой сценарий) панель
  показывала бы GUID-имя обратно; и переименование всё равно потребовало бы записи ещё куда-то.
- **Explorer-подобный счётчик `name (2).ext` вместо GUID.** Отклонено: возвращает racy
  check-then-create цикл, от которого ADR-009 ушёл осознанно.

**Trade-off:** длина пути. `root(~60) + 33 + имя` — под MAX_PATH обычно проходит, но для длинных имён
и для глубоких деревьев при `MoveInDirectory` риск есть. Требование к реализации: если итоговый
полный путь > 240 символов — усечь **базовое** имя (сохранив расширение), как сейчас усекается до 40;
в обычном случае имя не усекается вовсе.

### Решение 3 (Фича B, legacy): существующие плоские GUID-имена — только косметическая деградация, без миграции

Уже лежащие в storage элементы вида `name_<32hex>.ext` **не мигрируются** (никакого файлового I/O на
старте — это путь загрузки панелей, ADR-012 Блок C специально его защищал).
Вместо этого:
- `FileItem` получает необязательный, **не персистируемый** параметр конструктора `displayName` и
  свойство `DisplayName => _displayName ?? Path.GetFileName(Path)`;
- `BaseViewModel` получает хук `protected virtual string? ResolveDisplayName(string path) => null;`,
  вызываемый в `AddFilesCore` при конструировании `FileItem`;
- `ChildWindowViewModel` переопределяет его: **только если** `_itemStorage.IsManaged(path)` —
  применить чистый строковый хелпер `ManagedItemName.StripLegacyGuidSuffix(fileName)`
  (снимает ровно суффикс `_<32 hex>` перед расширением, иначе возвращает вход без изменений);
- при переименовании legacy-элемента (Решение 5) он физически переезжает в новую раскладку
  `PanelItems\{guid}\{новоеИмя}{ext}` — т.е. лечится окончательно.

**Почему так:** миграция на старте — файловые операции над пользовательскими данными без запроса,
на самом чувствительном пути (ADR-010/ADR-012); косметическая деградация безопасна и обратима.
**Trade-off:** гипотетический ложноположительный strip у файла, реально названного
`report_0123456789abcdef0123456789abcdef.pdf`, — и то только для файлов **внутри managed storage**
(проверка `IsManaged` обязательна) и только на отображении. Принято.

### Решение 4 (Фича B): переименование — **всегда физическое**, отдельным сервисом

Новая абстракция `IFileItemRenameService` (Services.Abstractions) + реализация в Services/Tools.
Она — единственная точка мутации имени, и она разводит два случая:
- `IPanelItemStorageService.IsManaged(path) == true` → делегирует новому методу самого storage-сервиса
  `string? RenameManaged(string storedPath, string newName)` (только он вправе трогать storage root и
  manifest);
- иначе → обычный `File.Move`/`Directory.Move` в тот же каталог.

**Почему так:** UI/ViewModel не должен делать мутирующее файловое I/O; storage-сервис не должен
переименовывать чужие (не свои) файлы — его `IsManaged`-гард существует именно для того, чтобы он
никогда не трогал то, чем не владеет. Новый сервис — тонкий диспетчер + валидация.

**Alternatives considered:**
- **Расширить `IPanelItemStorageService.Rename(path,…)` на оба случая.** Отклонено: ломает
  инвариант «сервис владеет только своим storage root».
- **Расширить `IFileLauncher`.** Отклонено: `IFileLauncher` — про shell-запуск (read-only действия),
  добавление мутации туда размывает роль.
- **Rename прямо в `ChildWindowViewModel`.** Отклонено: не тестируется, дублирует manifest-логику.

### Решение 5 (Фича B): переименование managed-элемента обновляет и manifest-ключ, и оригинальное имя

`RenameManaged(storedPath, newName)`:
1. вычисляет `newPath` = тот же GUID-каталог + `newName` (для legacy плоского элемента — **новый**
   GUID-каталог, см. Решение 3);
2. **сначала** переписывает manifest: удаляет старый ключ, пишет `newPath → newOriginalPath`, где
   `newOriginalPath = Path.Combine(dirname(oldOriginalPath), newName)`;
3. только затем делает `File.Move`/`Directory.Move`;
4. если запись manifest не удалась (или кеш «недоверенный», см. ADR-012 Блок F) — **абортирует
   переименование**, не трогая файл.

**Почему так:** это ровно та же дисциплина «manifest пишется до деструктивного шага», которую ADR-012
Блок F ввёл для `MoveIn`/`MoveInDirectory`: потеря origin path превращает будущее «Убрать из панели»
из restore в безвозвратное удаление. Обновление **имени** внутри `originalPath` даёт ожидаемое
поведение Проводника: файл, переименованный в панели, вернётся на рабочий стол под новым именем
(коллизии уже обрабатывает `ResolveCollisionFreeName`).

**Trade-off:** «оригинальный путь» перестаёт быть буквально оригинальным (меняется имя, каталог
сохраняется). Это осознанное отступление от буквы ADR-009 в пользу ожидаемого UX; фиксируется в
ADR-013.

### Решение 6 (Фича B, UI): состояние inline-редактирования — на ViewModel, не на `FileItem`

`ChildWindowViewModel.EditingItem : FileItem?` (INPC) + `EditingName : string`.
В шаблоне — `TextBlock`/`TextBox` с переключением видимости через существующий
`EqualityToBoolConverter` (`IMultiValueConverter`, сравнивает `DataContext` элемента и
`DataContext.EditingItem` окна) — конвертер уже есть и используется для страниц.

**Почему так:** `FileItem` — доменная модель, разделяемая порталами, загрузкой и фильтрами; флаг
«сейчас редактируется» — чистое UI-состояние одной панели. Плюс: «редактируется ровно один элемент»
становится инвариантом по построению.
**Alternative:** `bool IsEditing` на `FileItem` — проще в XAML, но допускает несколько одновременно
редактируемых элементов и протекает UI-состоянием в Core. Отклонено.

---

## 3. Затронутые файлы и слои

| Слой | Файл | Что делаем | Блок |
|---|---|---|---|
| Core | `src/DesktopOrganizer.Core/Models/FileItem.cs` | `DisplayName` (+ опц. ctor-параметр), `Relocate(newPath, icon)` c INPC на `Path`/`Icon`/`DisplayName`/`Name`/`Type`/`IsShortcut` | B |
| Core | `src/DesktopOrganizer.Core/Models/ManagedItemName.cs` **(новый)** | чистый хелпер `StripLegacyGuidSuffix` + `IsValidFileName`-часть (или отдельный `FileNameValidation`) | B/C |
| Abstractions | `src/DesktopOrganizer.Services.Abstractions/IPanelItemStorageService.cs` | **+** `string? RenameManaged(string storedPath, string newName)` (аддитивно) | C |
| Abstractions | `src/DesktopOrganizer.Services.Abstractions/IFileItemRenameService.cs` **(новый)** | `RenameOutcome` (enum) + `RenameResult` + `Rename(path, newName)` | C |
| Services | `src/DesktopOrganizer.Services/Tools/PanelItemStorageService.cs` | новая раскладка `{guid}\{name}`; `RenameManaged`; уборка пустого GUID-каталога в `Restore`/`Release`; guard длины пути | B/C |
| Services | `src/DesktopOrganizer.Services/Tools/FileItemRenameService.cs` **(новый)** | валидация + диспетчеризация managed/не-managed | C |
| Services | `src/DesktopOrganizer.Services/ServiceCollectionExtensions.cs` (или где DI-регистрация) | регистрация нового сервиса | C |
| UI/VM | `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs` | хук `ResolveDisplayName`; `TryRenameItem(item,newPath)` с обновлением `_allItemPaths`; сохранение/восстановление выделения при `ReplaceFileItems` | A/B/C |
| UI/VM | `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` | override `ResolveDisplayName`; `BeginRenameCommand`/`CommitRenameCommand`/`CancelRenameCommand`; `EditingItem`/`EditingName`; `RenameFailed`-событие; `FilesChanged` после успеха | B/C |
| UI/XAML | `src/DesktopOrganizer.UI/Resources/Templates.xaml` | wrap → `ListBox`+`WrapPanel`; общий `PanelItemContainerStyle`; биндинг `SelectedItem`↔`SelectedFile`; `DisplayName` вместо `Name`; inline-`TextBox`; пункт «Переименовать» в `FileContextMenu` | A/B/C |
| UI/View | `src/DesktopOrganizer.UI/Views/ChildWindow.xaml(.cs)` | снятие выделения по клику в пустоту; выделение по ПКМ; `F2`; фокус в редактор; popup-ошибка переименования | A/C |
| Тесты | `tests/DesktopOrganizer.Tests/Unit/...` | см. §5 | A–C |
| Docs | `adr/ADR-013-*.md`, `adr/README.md`, `docs/ROADMAP.md` | новый ADR + Фаза 8 | E |

---

## 4. Блоки работ, исполнители, порядок

### Блок A — выделение (Фича A). Scope **M**. Исполнитель: `wpf-ui-builder`
Зависимостей нет — можно вести **параллельно** с блоком B.

- **A1.** `Templates.xaml`: `WrapViewTemplate` — `ItemsControl` → `ListBox`
  (`ItemsPanel = WrapPanel`, `ScrollViewer.HorizontalScrollBarVisibility=Disabled`,
  внешний `ScrollViewer` **удалить**, `Background=Transparent`, `BorderThickness=0`).
- **A2.** Общий `PanelItemContainerStyle` (`TargetType=ListBoxItem`) для обоих видов: собственный
  `ControlTemplate` (`Border` + `ContentPresenter`), триггеры:
  - `IsSelected=True` → полупрозрачная заливка акцентом + рамка 1px (Fences/Explorer-подобно),
    **без** оглядки на `IsSelectionActive` (см. §1, `WS_EX_NOACTIVATE`);
  - `IsMouseOver=True` (и не выбран) → более слабая заливка;
  - `HorizontalContentAlignment=Stretch`, `Padding` — согласовать с `IconBoxWidth`, чтобы
    подсветка ложилась ровно на ячейку wrap-сетки и не ломала `GridMetrics.CellSize`.
- **A3.** Биндинг выделения в обоих шаблонах:
  `SelectedItem="{Binding DataContext.SelectedFile, RelativeSource={RelativeSource AncestorType=Window}, Mode=TwoWay}"`,
  `SelectionMode="Single"`.
- **A4.** `ChildWindow.xaml.cs`: `PreviewMouseLeftButtonDown` на `FileListHost` (и на корневом
  `Border` панели) — если по цепочке визуальных предков нет `ListBoxItem`, то
  `_viewModel.SelectedFile = null` (Explorer-поведение «клик в пустоту снимает выделение»).
  Не трогать, если клик пришёл в редактируемый `TextBox`.
- **A5.** `ChildWindow.xaml.cs`: `PreviewMouseRightButtonDown` на элементе — выделить элемент до
  показа `FileContextMenu` (Explorer-поведение).
- **A6.** `ChildWindowViewModel`/`BaseViewModel`: сохранять выделение при перестройке `FileItems`
  (`ReplaceFileItems` даёт `Reset` → `ListBox` обнуляет `SelectedItem`; см. ADR-012, известный
  побочный эффект). Реализация: запомнить `SelectedFile?.Path` до `ReplaceAll`, после — найти
  элемент с тем же путём (регистронезависимо) и восстановить.

**Не входит в объём (осознанно):** multi-select. Требует множественных команд контекстного меню
(`Открыть`/`Убрать`/`Свойства` для набора) и меняет сигнатуры команд — отдельный тикет.

### Блок B — честные имена (Фича B, часть 1). Scope **M**. Исполнители: `dotnet-services-engineer` (B1–B3) + `wpf-ui-builder` (B4)
Может идти **параллельно** блоку A; блок C зависит от B.

- **B1.** `PanelItemStorageService.BuildDestinationPath`/`BuildDestinationDirectoryPath` →
  `Path.Combine(_storageRoot, guidN, originalFileName)`; создать GUID-каталог перед перемещением
  (в т.ч. в same-volume ветке `Directory.Move`, которой нужен существующий родитель);
  усечение имени — **только** при превышении лимита длины пути (240), с сохранением расширения.
- **B2.** `Restore`/`Release`: после успешного переноса/удаления — удалить осиротевший **пустой**
  GUID-каталог, если он является прямым потомком storage root и пуст. Никогда не удалять сам root,
  никогда не удалять непустой каталог.
- **B3.** `FileItem`: опц. `displayName` в конструкторе + `DisplayName`;
  `BaseViewModel.ResolveDisplayName` (virtual, по умолчанию `null`) вызывается в `AddFilesCore`;
  `ChildWindowViewModel` override → `IsManaged(path) ? ManagedItemName.StripLegacyGuidSuffix(...) : null`.
  Хелпер `ManagedItemName` — чистая строковая функция в Core (прецедент: `GridSnap`,
  `DesktopGestureStateMachine`), покрывается тестами.
- **B4.** `Templates.xaml`: оба `TextBlock` — `{Binding Name}` → `{Binding DisplayName}`;
  добавить `ToolTip="{Binding Path}"` (Explorer-подобная подсказка полного пути).

### Блок C — переименование (Фича B, часть 2). Scope **L**. Исполнители: `dotnet-services-engineer` (C1–C2) → `wpf-ui-builder` (C3–C5)
Зависит от блока B (раскладка + `DisplayName` + `Relocate`).

- **C1.** Abstractions: `RenameOutcome { Renamed, InvalidName, NameTaken, NotFound, NotSupported, Failed }`,
  `readonly record struct RenameResult(RenameOutcome Outcome, string? NewPath)`,
  `IFileItemRenameService.Rename(string path, string newName)`;
  **+** `IPanelItemStorageService.RenameManaged(string storedPath, string newName)` (возвращает новый
  путь или `null`; XML-док обязан зафиксировать инвариант «manifest пишется до перемещения, при сбое
  записи — аборт без касания файла», Решение 5).
- **C2.** Services: `PanelItemStorageService.RenameManaged` (перекладка ключа manifest + переезд
  legacy-элемента в GUID-каталог) и `FileItemRenameService` (валидация имени → диспетчеризация
  managed/не-managed → `File.Move`/`Directory.Move`; коллизия ⇒ `NameTaken`, **без** авто-суффикса —
  Проводник тоже сообщает об ошибке, а не переименовывает молча). Никогда не бросать наружу.
- **C3.** `ChildWindowViewModel`: `EditingItem`/`EditingName`, `BeginRenameCommand` (параметр —
  `FileItem`; по умолчанию `SelectedFile`), `CommitRenameCommand`, `CancelRenameCommand`;
  на успехе — `BaseViewModel.TryRenameItem` (обновляет `_allItemPaths`, вызывает
  `FileItem.Relocate(newPath, _iconProvider.GetIcon(newPath))`), затем `OnSourceChanged()` (переоценка
  фильтра, т.к. смена расширения может менять `IsShortcut`) и `FilesChanged` → `SaveWindowsState`;
  на неуспехе — событие `RenameFailed(RenameOutcome)` для ненавязчивого хинта.
- **C4.** `Templates.xaml`: в обоих шаблонах `TextBlock` дополняется `TextBox`-ом, видимости
  переключаются `MultiBinding` + `EqualityToBoolConverter` (`DataContext` элемента vs
  `DataContext.EditingItem` окна) + `BooleanToVisibilityConverter`;
  в `FileContextMenu` — пункт **«Переименовать»** по существующему паттерну
  `PlacementTarget.Tag.BeginRenameCommand` / `PlacementTarget.DataContext`.
- **C5.** `ChildWindow.xaml.cs`: `F2` (`KeyDown`/`PreviewKeyDown` на окне) → `BeginRename(SelectedFile)`;
  `Enter` — коммит, `Escape` — отмена, `LostKeyboardFocus` — коммит (полная калька с
  `TitleEditBox_KeyDown`/`TitleEditBox_LostKeyboardFocus`, `ChildWindow.xaml.cs:369`);
  фокус в редактор — `Dispatcher.BeginInvoke(DispatcherPriority.Input, …)` + `Focus()`/`SelectAll()`
  (контейнер `ListBoxItem` генерируется не сразу); ошибка переименования — переиспользовать
  `MoveHintPopup`-паттерн (`ChildWindow.xaml:177`, `ShowMoveHint`), а не `MessageBox`
  (модальное окно из панели с `WS_EX_NOACTIVATE` — отдельный риск).

**Осознанно вне объёма первой итерации:** «медленный второй клик по выделенному» как триггер
переименования. Причина: он напрямую конкурирует с двойным кликом «Открыть»
(`FileList_MouseDoubleClick`, `ChildWindow.xaml.cs:487`) и требует таймера на `GetDoubleClickTime`;
цена регрессии главного действия выше ценности триггера. Вносится отдельным пунктом бэклога после
ручной верификации F2/контекстного меню.

### Блок D — тесты. Исполнитель: `tdd-test-engineer`
Идёт волнами вслед за A/B/C (не в конце — по TDD-политике репозитория тесты формулируются до/вместе
с реализацией блока).

### Блок E — документация. Исполнитель: `architecture-documenter`
`adr/ADR-013-panel-item-selection-and-rename.md` (Решения 1–6 из §2), `adr/README.md`,
`docs/ROADMAP.md` — новая секция **«Фаза 8. UX-паритет значков: выделение и переименование»** с двумя
пунктами (A и B) + пополнение сводного списка manual verify.

---

## 5. Тесты (что покрываемо в CI без GUI)

| Что | Где | Комментарий |
|---|---|---|
| `ManagedItemName.StripLegacyGuidSuffix` | `Unit/Models/ManagedItemNameTests.cs` (новый) | чистая строка: с расширением/без, папка, не-GUID-суффикс, 31/33 hex, верхний регистр |
| Валидация имени (`InvalidName`) | `Unit/Models/FileNameValidationTests.cs` (новый) | пустое, пробелы, `Path.GetInvalidFileNameChars`, `..`, точка в конце, зарезервированные (`CON`, `PRN`, `NUL`, `COM1`…) |
| Новая раскладка storage | `Unit/Services/PanelItemStorageServiceTests.cs` (расширить) | `MoveIn`/`MoveInDirectory` кладут в `{guid}\{originalName}`; `Path.GetFileName(result) == оригинальному имени`; два файла с одинаковым именем не конфликтуют |
| Уборка пустого GUID-каталога | там же | после `Restore` и после `Release` каталог удалён; непустой — не удалён; root не удалён |
| `Restore` после смены раскладки | там же (регресс ADR-009) | same-volume и `forceCrossVolume`, файл и папка, коллизия имени `name (2).ext` |
| `RenameManaged` | там же | happy path; ключ manifest перевешен; `originalPath` получил новое имя, каталог сохранён; legacy плоский элемент переехал в GUID-каталог; **сбой записи manifest ⇒ файл не тронут** (через существующую тестовую точку «недоверенного» кеша) |
| `FileItemRenameService` | `Unit/Services/FileItemRenameServiceTests.cs` (новый) | не-managed файл/папка переименованы физически; коллизия ⇒ `NameTaken` и исходный файл цел; несуществующий ⇒ `NotFound`; невалидное имя ⇒ `InvalidName`; managed ⇒ делегирование в storage (Moq) |
| `FileItem.Relocate` | `Unit/Models/FileItemTests.cs` (расширить) | INPC для `Path`/`Name`/`DisplayName`/`Type`/`IsShortcut`/`Icon`; `DisplayName`-override сбрасывается |
| `BaseViewModel.TryRenameItem` | `Unit/ViewModels/BaseViewModelCollectionTests.cs` (расширить) | `_allItemPaths` перестроен (старый путь можно добавить снова, новый — дедуплицируется) |
| Сохранение выделения при `ReplaceFileItems` | `Unit/ViewModels/ChildWindowViewModelTests.cs` (расширить) | выделение восстанавливается по пути; исчезнувший элемент ⇒ `SelectedFile == null` |
| Rename в VM | там же | успех ⇒ `FilesChanged` поднят и фильтр переприменён; неуспех ⇒ `RenameFailed`, коллекция не изменена; портальная панель — rename **разрешён** (в отличие от `RemoveFileCommand`) |

**Не покрываемо в CI:** всё XAML-поведение (подсветка, снятие выделения, wrap-раскладка внутри
`ListBox`, фокус в `TextBox`, F2) — см. §8.

---

## 6. Definition of Done

**Фича A:**
1. Клик по значку в **wrap**-виде подсвечивает его; клик по другому — переносит подсветку.
2. Клик по значку в **списочном** виде даёт визуально **тот же** стиль подсветки.
3. Подсветка видна на панели с любым `BackgroundColor`/`BackgroundOpacity` и **не зависит** от того,
   активно ли окно (`IsSelectionActive` не используется в стиле).
4. Клик по пустому месту панели снимает выделение; ПКМ по значку сначала выделяет его.
5. `BaseViewModel.SelectedFile` реально отражает выделение (двусторонний биндинг), и выделение
   переживает перестройку `FileItems` (смена фильтра, тик портала), если элемент остался в списке.

**Фича B:**
6. Файл/папка, перетащенные в панель **после** изменения, отображаются под **оригинальным** именем
   (никакого `_<guid>`), и это же имя лежит на диске.
7. Элементы, перенесённые **до** изменения, отображаются без GUID-суффикса (legacy-деградация).
8. «Переименовать» доступно из контекстного меню значка и по `F2`; редактирование — инлайн, `Enter`
   подтверждает, `Esc` отменяет, потеря фокуса подтверждает.
9. Переименование **физически** переименовывает файл/папку (проверяемо в Проводнике) — и для
   managed, и для обычных/портальных элементов.
10. Переименование managed-элемента корректно перевешивает запись manifest, и последующее «Убрать из
    панели» возвращает файл на исходное место **под новым именем**.
11. Невалидное имя / занятое имя не приводят к потере файла и сообщаются пользователю ненавязчиво.
12. Имя переживает перезапуск приложения (для managed — потому что оно на диске; для остальных —
    потому что `FilePaths` пересохранён с новым путём).

**Сквозное:**
13. `dotnet build` и `dotnet test` зелёные; допустимы только 6 известных предсуществующих падений
    (см. ROADMAP, Фаза 7) — их число не выросло.
14. Публичные контракты `IPanelItemStorageService` (кроме **добавленного** `RenameManaged`),
    `IWindowManager`, `IIconProvider` не изменились; схема JSON `ChildWindowsState`
    (`ChildWindowModel`) **не изменилась**.
15. `ADR-013` написан, `adr/README.md` и `docs/ROADMAP.md` (Фаза 8) обновлены.

---

## 7. Порядок выполнения и параллелизм

```
        ┌── Блок A (выделение, wpf-ui-builder) ────────────┐
старт ──┤                                                  ├── Блок D-тесты (волна 1)
        └── Блок B (имена: B1–B3 services → B4 xaml) ──────┘
                              │
                              ▼
                   Блок C (переименование)
                   C1–C2 dotnet-services-engineer
                        → C3–C5 wpf-ui-builder
                              │
                              ▼
                     Блок D-тесты (волна 2)
                              │
                              ▼
        (code-quality-reviewer ∥ fences-ux-parity-reviewer)
                              │
                              ▼
                Блок E (architecture-documenter)
```

- **A и B — независимы по файлам**, кроме `Templates.xaml` (A правит структуру шаблонов, B4 — одну
  строку биндинга текста). Чтобы не ловить конфликт: **A выполняет и B4** (замена `Name` →
  `DisplayName` в тех же шаблонах), а `dotnet-services-engineer` в `Templates.xaml` не заходит.
- **C строго после B** (нужны `Relocate`, `DisplayName` и новая раскладка).
- Ревьюеры (`code-quality-reviewer` ∥ `fences-ux-parity-reviewer`) — одним сообщением, параллельно.

---

## 8. Риски

| # | Риск | Уровень | Митигация |
|---|---|---|---|
| R1 | **`WS_EX_NOACTIVATE` не даёт клавиатурного фокуса** → inline-`TextBox` не принимает ввод, `F2` не доходит. Уже помечено в ROADMAP (Фаза 2) для переименования **заголовка** и **никогда не верифицировано вживую**. | **CRITICAL** | Сначала — чистый WPF (`Focus()` из `Dispatcher.BeginInvoke`, `Keyboard.Focus`). Если ручная проверка покажет, что ввод не приходит — **эскалация на `win32-interop-specialist`**: добавить `IDesktopIntegrationService.SetActivationAllowed(IntPtr hwnd, bool)` (снимать `WS_EX_NOACTIVATE` на время правки, возвращать на коммит/отмену) + `SetForegroundWindow`. Контекстное меню «Переименовать» этим не блокируется, но сам ввод текста — блокируется. Обязателен fallback-пункт в manual verify. |
| R2 | Потеря `originalPath` в manifest при переименовании ⇒ будущее «Убрать из панели» превращается в безвозвратное удаление (ADR-009/ADR-012). | **CRITICAL** | Решение 5: manifest пишется **до** `Move`, при сбое записи — аборт без касания файла; отдельный тест на этот путь. |
| R3 | Смена раскладки storage ломает существующие тесты `PanelItemStorageServiceTests` (27+) и/или сценарии `Restore`/`Release`/ADR-010. | MAJOR | Раскладка меняется только в `Build*Path`; `IsManaged`/`Restore`/`Release` путе-агностичны. Полный прогон существующих storage-тестов — часть DoD; тесты, ассертящие плоскую раскладку, править осознанно. |
| R4 | `MAX_PATH` (260) при `{root}\{guid}\{имя}` и особенно при `MoveInDirectory` глубоких деревьев. | MAJOR | Guard на 240 символов с усечением базового имени; тест на длинное имя. Long-path-manifest не включаем (отдельное решение). |
| R5 | Замена `ItemsControl` → `ListBox` в wrap-виде ломает раскладку/скролл/`GridMetrics` (ресайз кратно сетке, `IconBoxWidth`, `FileListHPadding`). | MAJOR | Удалить внешний `ScrollViewer`; проверить `Resize_DragDelta`/`SnapWrapWidthToGrid` вживую; `Padding` контейнера учесть в `GridMetrics.CellSize` **или** обнулить. Пункт manual verify. |
| R6 | Смена расширения при переименовании меняет `IsShortcut`/`IsDirectory` ⇒ элемент выпадает из активного `ContentFilter` и «исчезает» без объяснения. | MINOR | После успешного rename вызывать `OnSourceChanged()`; поведение задокументировать; тест на переприменение фильтра. |
| R7 | Кеш иконок `IconProvider` (LRU 1024, ключ = путь) после rename отдаёт старую иконку. | MINOR | `Relocate` принимает свежую иконку, полученную по новому пути; старая запись просто вытесняется LRU. |
| R8 | Ложноположительный strip legacy-суффикса у файла, реально названного `x_<32hex>.ext`. | MINOR | Strip применяется **только** при `IsManaged(path)`; чисто косметика; фиксируется в ADR-013. |
| R9 | `Reset`-уведомление `ObservableRangeCollection.ReplaceAll` сбрасывает выделение (известный побочный эффект, ADR-012). До этой фичи он был невидим, теперь становится заметен. | MINOR | Блок A6 (восстановление выделения по пути). Точечный diff Add/Remove вместо `Reset` — остаётся отдельным тикетом. |
| R10 | Rename в **портальной** панели: наш локальный `Relocate` и событие `FolderChangeKind.Renamed` от `FileSystemWatcher` обрабатывают одно и то же. | MINOR | Обработка идемпотентна: `RemoveFileByPath(oldPath)` — no-op (пути уже нет), `AddFiles([newPath])` — дедуп по `_allItemPaths`. Явно зафиксировать тестом/комментарием. В отличие от `RemoveFileCommand`, rename в портале **разрешён** (это реальный файл в реальной папке — паритет с Проводником). |
| R11 | Два элемента в панели могут получить одинаковое отображаемое имя (разные GUID-каталоги). | MINOR | Ожидаемо и допустимо (панель — не папка); `ToolTip` с полным путём (B4) снимает неоднозначность. |
| R12 | Multi-select не реализован — пользователь может ожидать рамку/Ctrl+клик как на рабочем столе. | MINOR | Явно вне объёма, отдельный пункт бэклога (нужны множественные команды меню). |

**Нативных/P-Invoke участков в объёме нет** — весь код managed WPF/.NET.
Единственная потенциальная нативная эскалация — R1 (`win32-interop-specialist`, только по результату
ручной проверки).

---

## 9. Ручная проверка на Windows 10/11 (обязательна — вне CI не воспроизводится)

1. Клик по значку в **плитке** и в **списке** — подсветка видна, стиль одинаковый, читается на
   тёмном и светлом `BackgroundColor` при `BackgroundOpacity` 0.3 / 0.8 / 1.0.
2. Клик по пустому месту панели снимает выделение; клик по заголовку панели — не «залипает».
3. ПКМ по значку выделяет его и открывает меню на нём же.
4. **R1:** контекстное меню → «Переименовать» → в `TextBox` реально печатается текст;
   `Enter` коммитит, `Esc` отменяет, клик вне — коммитит. То же для `F2`.
   Если ввод не приходит — зафиксировать и эскалировать (см. R1).
5. Перетащить файл и папку с рабочего стола в панель → отображаются под своими именами; проверить в
   Проводнике: `%LocalAppData%\DesktopOrganizer\PanelItems\<guid>\<оригинальное имя>`.
6. Панель, созданная **до** обновления, с уже перенесёнными файлами → имена без GUID-хвоста;
   переименование такого элемента → он переехал в `<guid>\<новое имя>` и продолжает открываться.
7. Переименовать managed-элемент → «Убрать из панели» → файл вернулся на рабочий стол **под новым
   именем**; повторить при существующем одноимённом файле на столе (`name (2).ext`).
8. Переименовать элемент в **портальной** панели → имя изменилось в реальной папке; панель не
   задвоила элемент.
9. Занятое имя и невалидное имя (`a/b`, `CON`, пустое) → файл цел, показан ненавязчивый хинт.
10. **R5:** ресайз панели в плиточном виде остаётся кратным сетке иконок; перенос строк, скролл при
    переполнении, `IconSize` 32/48/64 — раскладка не ломается.
11. Смена `ContentFilter` и тик Folder Portal при выделенном элементе — выделение сохраняется.
12. Двойной клик по значку по-прежнему **открывает** файл (не начинает переименование).
13. DPI 125%/150%: подсветка и `TextBox` не съезжают относительно иконки.

---

## 10. Что записать в ROADMAP (для `architecture-documenter`)

Новая секция после Фазы 7:

```
## Фаза 8. UX-паритет значков: выделение и переименование
- [ ] Выделение (подсветка) значка по клику в обоих видах панели, снятие по клику в пустоту.
- [ ] Отображение оригинального имени вместо GUID-имени + inline-переименование значка (F2 /
      контекстное меню), физическое переименование на диске, синхронизация manifest.
```
плюс в «Сквозные задачи» — пункты manual verify из §9 и бэклог-тикеты:
multi-select, «медленный второй клик» как триггер rename, точечный diff вместо `Reset`.
