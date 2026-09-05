# Мастер-план: UI-полировка дочерних окон и боковой панели (ветка `polishing/ui-1`)

Источник: пользовательский запрос от 2026-09-01 (6 связанных UI-задач, «как есть», без готового дизайна).
Статус: план готов, передан реализующим агентам.
Scope суммарно: **L** (UI/Models + UI/ViewModels + UI/Converters + XAML + code-behind + новый Abstractions/Services-слой для виртуальных рабочих столов).

Это **не** пункт ROADMAP-фазы, а батч связанных полировочных правок. Оркестрируется как «несколько
пунктов»: каждый блок A–E прогоняется по полному конвейеру
(`wpf-ui-builder` / `win32-interop-specialist` / `dotnet-services-engineer` → `tdd-test-engineer` →
`debug-healer` при красной сборке → `code-quality-reviewer` ∥ `fences-ux-parity-reviewer` →
`architecture-documenter`) до закрытия всех шести задач.

> **⚠️ ЯЗЫК UI-СТРОК.** Все новые пользовательские строки (тултипы, лейблы настроек) — **только на
> английском**. Русский остаётся языком плана/ADR/ROADMAP. Конкретные требуемые строки указаны в
> тексте блоков — реализующие агенты обязаны использовать именно их.

> **⚠️ НЕЗАКОММИЧЕННАЯ БАЗА.** В рабочем дереве уже лежит рефакторинг `PanelViewMode.Wrap → Grid`
> (`GridMetrics.cs`, `IconBoxWidthConverter.cs`, `PanelViewMode.cs`, `Templates.xaml`,
> `ChildWindowViewModel.cs`, `ChildWindow.xaml(.cs)`, два тестовых файла). Это **база**, поверх
> которой планируются изменения. **Не откатывать**, не «чинить» термин `Wrap` обратно.

---

## 0. Карта задач → блоков

| # | Задача пользователя | Блок | Scope | Агент-исполнитель |
|---|---|---|---|---|
| 1 | Сброс `SelectedFile` при любом другом клике | **A** | M | `wpf-ui-builder` + `dotnet-services-engineer` |
| 2 | Отступы/сетка иконок в Grid-режиме, обрезанное выделение, ревизия «desktop grid size» | **B** | L | `wpf-ui-builder` (после research-шага) |
| 6 | Иконка прижата к верхней границе бокса, текст на общем Y | **B** | S | `wpf-ui-builder` |
| 3 | Порядок кнопок заголовка, иконка View, подсветка кнопки сортировки | **C** | M | `wpf-ui-builder` |
| 4 | Выделение в боковой панели тем же визуальным языком | **D** | M | `wpf-ui-builder` |
| 5 | Настройка «показывать боковую панель на всех виртуальных рабочих столах» | **E** | L | `win32-interop-specialist` + `dotnet-services-engineer` |

Задачи 2 и 6 объединены в один блок **B** намеренно: обе переписывают `GridViewTemplate` и
`GridMetrics`; раздельная реализация означала бы двойную переработку одной и той же разметки.

---

## 1. Зависимости и порядок выполнения

```
Волна 1 (параллельно, разные файлы):
    B  — геометрия Grid-режима + top-align иконки   (wpf-ui-builder)
    E0 — диагностический spike по виртуальным столам (win32-interop-specialist)  ← см. §6.1
         └─ E — реализация настройки                (win32 + dotnet-services)

Волна 2:
    C  — кнопки заголовка                            (wpf-ui-builder)
         зависит от B: обе правки живут в ChildWindow.xaml / Templates.xaml,
         параллельный запуск даст конфликт правок в одном файле.

Волна 3:
    A  — сброс выделения + координатор выделения     (wpf-ui-builder + dotnet-services)

Волна 4:
    D  — выделение в боковой панели                  (wpf-ui-builder)
         зависит от A (переиспользует координатор)
         зависит от B (переиспользует ИСПРАВЛЕННЫЙ PanelItemContainerStyle, см. B-R2)
```

**Критично:** блок D **не должен** стартовать до блока B. Он переиспользует
`PanelItemContainerStyle`, у которого сейчас есть дефект отрисовки рамки (`Margin="-1"`, см. B-R2);
запуск D раньше просто перенесёт этот дефект в боковую панель.

E полностью независим от A–D (другие файлы, другой слой) и может идти параллельно всему.

---

## 2. Блок A — сброс выделения при любом другом клике (задача 1)

### 2.1. Текущее состояние

- `BaseViewModel.SelectedFile` (`src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs:49`) —
  единственный источник правды выделения панели; оба шаблона (`ListViewTemplate`,
  `GridViewTemplate` в `Templates.xaml:189`, `:225`) биндят к нему `ListBox.SelectedItem` TwoWay,
  `SelectionMode="Single"`.
- Частичный сброс уже есть: `ChildWindow.FileListHost_PreviewMouseLeftButtonDown`
  (`ChildWindow.xaml.cs:647–662`) обнуляет `SelectedFile`, если клик не попал в `ListBoxItem`.
  Это покрывает **только** пустое место внутри `FileListHost`.

### 2.2. Что НЕ сбрасывает выделение сейчас (дыры)

1. Клик по заголовку панели (`TitleBar_MouseLeftButtonDown`) — заголовок, кнопки Sort/View/RollUp.
2. Клик по resize-таmbам (`ResizeOverlay`).
3. Клик по **другой** fence-панели — выделение остаётся подсвеченным в обеих.
4. Клик по боковой панели (после блока D — и наоборот).
5. Клик по голому рабочему столу.

### 2.3. Ключевое архитектурное ограничение

Панели несут `WS_EX_NOACTIVATE` и (при удачном lookup) являются детьми `WorkerW`
(`DesktopIntegrationService.EmbedInDesktop`). Поэтому:

- `Window.Deactivated` / `IsKeyboardFocusWithin` / `ListBoxItem.IsSelectionActive` **непригодны** как
  триггер сброса — окно никогда не становится активным. Это уже зафиксировано в комментарии к
  `PanelItemContainerStyle` (`Templates.xaml:70–91`) и в ADR-013.
- Сброс обязан быть **явным, событийным** (мышиные события), а не фокус-ориентированным.

### 2.4. Решение

**A1 — внутриоконный сброс (обязательно).**
Добавить обнуление `SelectedFile` в:
- `TitleBar_MouseLeftButtonDown` (перед drag-логикой; не мешает переименованию заголовка);
- обработчик `Resize_DragStarted`;
- клик по любой из трёх кнопок заголовка (проще всего — общий `PreviewMouseLeftButtonDown` на
  `StackPanel` группы кнопок, а не по кнопке).
Нельзя ставить `e.Handled = true` — это сломает существующую drag/resize-логику.

**A2 — межоконный сброс (обязательно, требуется задачей 4).**
Новый UI-сервис `src/DesktopOrganizer.UI/Services/IPanelSelectionCoordinator.cs` +
`PanelSelectionCoordinator.cs`, синглтон в DI (`DiConfig`):

```csharp
public interface IPanelSelectionCoordinator
{
    /// <summary>Объявляет владельца текущего выделения; все остальные владельцы сбрасываются.</summary>
    void NotifySelected(object owner);

    /// <summary>Сбрасывает выделение везде.</summary>
    void ClearAll();

    /// <summary>Подписчику приходит сигнал «сбрось своё выделение» (owner != он сам).</summary>
    event EventHandler<object>? SelectionClaimed;
}
```

- `ChildWindowViewModel` (через `BaseViewModel.SelectedFile` setter) вызывает `NotifySelected(this)`
  при установке ненулевого значения и подписывается на `SelectionClaimed`, обнуляя `SelectedFile`,
  если владелец — не он.
- Аналогично подключается `SystemIconsPanelViewModel` в блоке D.
- **Обязательно** отписка в `Dispose()`/`OnClosed` — иначе координатор удержит закрытые окна
  (утечка, тот же класс дефекта, что чинила Фаза 7).
- Реентрантность: `NotifySelected` вызывается из сеттера `SelectedFile`, а подписчики этот же сеттер
  и дёргают → нужен флаг подавления (`_suppressCoordinatorEcho`), иначе бесконечная рекурсия.
  Это **обязательный пункт DoD и обязательный unit-тест.**

**A3 — клик по голому рабочему столу (OPTIONAL, по умолчанию — в бэклог).**
Единственный источник такого события — существующий глобальный хук `WH_MOUSE_LL`
(`IDesktopGestureService` / `DesktopGestureStateMachine`). Сейчас он отдаёт только
`DesktopDoubleClicked` и `LassoCompleted`. Добавление события «любой left-down вне наших окон»
трогает hook-callback, у которого жёсткий инвариант «минимум работы + try/catch».
**Рекомендация:** вынести в бэклог-тикет ROADMAP, не делать в этом батче. Если решим делать —
реализовывать в `DesktopGestureStateMachine` (чистый, тестируемый), а не в callback'е.

### 2.5. DoD блока A — статус

1. [x] Клик по заголовку панели снимает выделение в этой панели — `_viewModel.SelectedFile = null;`
   в начале `TitleBar_MouseLeftButtonDown` (`ChildWindow.xaml.cs`), до ветвления double-click/drag,
   без `e.Handled`.
2. [x] Клик по кнопке Sort / View / RollUp снимает выделение и при этом **выполняет** свою команду —
   единый `PreviewMouseLeftButtonDown="HeaderButtons_PreviewMouseLeftButtonDown"` на родительском
   `StackPanel` группы кнопок (`ChildWindow.xaml`), а не на каждой кнопке отдельно; обработчик не
   ставит `e.Handled`, поэтому `Click`/`Command` конкретной кнопки отрабатывает штатно на bubbling-фазе
   следом.
3. [x] Начало resize снимает выделение — `_viewModel.SelectedFile = null;` первой строкой
   `Resize_DragStarted`.
4. [x] Выделение элемента в панели №1 снимает выделение в панели №2 (и наоборот) — реализовано через
   новый `IPanelSelectionCoordinator`/`PanelSelectionCoordinator` (синглтон), подключённый в
   `ChildWindowViewModel` через переопределённый хук `BaseViewModel.OnSelectedFileChanged()`
   (вызывается из сеттера `SelectedFile` после смены значения) и подписку на `SelectionClaimed`.
   Покрыто интеграционным unit-тестом с двумя реальными `ChildWindowViewModel`, делящими один реальный
   (не mock) `PanelSelectionCoordinator`
   (`ChildWindowViewModelTests.TwoPanels_SharingCoordinator_SelectingInOneClearsTheOther`).
5. [x] Существующее поведение сохранено (не тронуто): клик по пустому месту списка по-прежнему снимает
   выделение (`FileListHost_PreviewMouseLeftButtonDown`, без изменений); ПКМ по элементу выделяет его
   перед показом контекстного меню (`FileListHost_PreviewMouseRightButtonDown`, без изменений); клик
   внутри активного rename-`TextBox` **не** сбрасывает выделение и **не** отменяет переименование
   (ADR-013) — ни один из новых A1-хуков (заголовок/кнопки/resize) не пересекается с зоной списка файлов
   или с `TitleEditBox`, так что этот edge-case не затронут; полный набор существующих
   `ChildWindowViewModelTests`/`ChildWindowReorderGeometryTests` остаётся зелёным без изменений их
   логики.
6. [x] Нет рекурсии в координаторе — двойная защита: (а) `ChildWindowViewModel.OnSelectedFileChanged()`
   вызывает `NotifySelected` только когда новое значение `SelectedFile` не `null` (сброс к `null`
   никогда не "заявляет" выделение повторно); (б) `_suppressCoordinatorEcho` подавляет заявку на время
   сброса, вызванного чужим `SelectionClaimed`; (в) сам `PanelSelectionCoordinator.NotifySelected`
   идемпотентен для повторной заявки того же владельца (не поднимает событие повторно). Unit-тесты:
   `PanelSelectionCoordinatorTests.NotifySelected_SubscriberReClaimsSameOwnerFromWithinHandler_DoesNotRecurseInfinitely`,
   `NotifySelected_SubscriberClaimsADifferentOwnerFromWithinHandler_TerminatesAndDeliversBoth`,
   `NotifySelected_SameOwnerTwiceInARow_RaisesSelectionClaimedOnlyOnce`,
   `ChildWindowViewModelTests.TwoPanels_SharingCoordinator_NoRecursionOrStackOverflow` (50 циклов
   перекрёстного выделения между двумя реальными панелями — без исключений/переполнения стека).
7. [x] Нет утечки: закрытая панель отписана от координатора — `ChildWindowViewModel.Dispose()` теперь
   дополнительно вызывает `_selectionCoordinator.SelectionClaimed -= OnSelectionClaimed;` (симметрично
   подписке в конструкторе). Unit-тест: `ChildWindowViewModelTests.Dispose_UnsubscribesFromSelectionClaimed`
   (реальный, не mock, координатор — после `Dispose()` дальнейшие `NotifySelected` не бросают
   исключений и не трогают состояние disposed-ViewModel).
8. [x] `dotnet build` — зелёный, новых предупреждений нет (14 warnings — все предсуществующие, из
   файлов, не тронутых этим блоком). `dotnet test` — 623 passed / 6 failed; все 6 падений
   предсуществующие и идентичны списку, зафиксированному в блоках B/C/E (`RollUpTests` × 3,
   `NativeNameAttributeTests` × 2, `SystemIconsPanelViewModelTests.OnRecycleBinChanged…` × 1) — не
   связаны ни с одним файлом этого блока.

**Finalization (code-quality-reviewer, финальное ревью всей ветки `polishing/ui-1`):**

9. [x] **MAJOR M-1 — координатор держал strong-reference на уничтоженный ViewModel.** `Dispose()`
   отписывался от `SelectionClaimed`, но не сбрасывал `_currentOwner` в синглтон-координаторе — закрытая
   панель оставалась достижимой через координатор до следующего `NotifySelected` любой другой панели.
   Фикс: новый метод `IPanelSelectionCoordinator.Release(object owner)` — обнуляет `_currentOwner`
   ТОЛЬКО если `ReferenceEquals(_currentOwner, owner)`, идемпотентен для не-владельца, не поднимает
   `SelectionClaimed` (в отличие от `ClearAll()`, который остался как есть и не тронут). Вызывается
   безусловно из `ChildWindowViewModel.Dispose()` и `SystemIconsPanelViewModel.Dispose()`.
10. [x] **MAJOR M-2 — кросс-панельное снятие выделения не закрывало активный inline-rename.**
    `WS_EX_NOACTIVATE` не даёт другой панели получить `LostKeyboardFocus`, когда фокус переключается
    между панелями кликом — активный редактор переименования оставался открытым. Фикс, по продуктовому
    решению «click-away коммитит, а не отменяет» (та же семантика, что уже была у
    `RenameEditBehavior`'s `LostKeyboardFocus` → `CommitRenameCommand`): если `EditingItem is not null`,
    коммит переименования вызывается ПЕРЕД сбросом выделения — добавлено в
    `ChildWindowViewModel.OnSelectionClaimed` (кросс-панельный путь через координатор) и во всех трёх
    A1-хуках `ChildWindow.xaml.cs` (`TitleBar_MouseLeftButtonDown`, `HeaderButtons_PreviewMouseLeftButtonDown`,
    `Resize_DragStarted`) через новый общий приватный helper `CommitActiveItemRenameIfAny()`. Никогда не
    вызывает `CancelRenameCommand`.
11. [x] `dotnet build`/`dotnet test` после M-1/M-2 — зелёные, 642/642 (актуальный baseline ветки на
    момент финализации), без новых StyleCop-предупреждений.

### 2.6. Артефакты блока A

- `src/DesktopOrganizer.UI/Services/IPanelSelectionCoordinator.cs` (новый)
- `src/DesktopOrganizer.UI/Services/PanelSelectionCoordinator.cs` (новый)
- `src/DesktopOrganizer.UI/ViewModels/BaseViewModel.cs` (сеттер `SelectedFile` вызывает новый
  переопределяемый хук `OnSelectedFileChanged()`, по умолчанию no-op)
- `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` (новая зависимость
  `IPanelSelectionCoordinator`, подписка в конструкторе / отписка в `Dispose()`, переопределение
  `OnSelectedFileChanged()`, новый обработчик `OnSelectionClaimed`, поле `_suppressCoordinatorEcho`)
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml(.cs)` (A1-хуки: `TitleBar_MouseLeftButtonDown`,
  новый `HeaderButtons_PreviewMouseLeftButtonDown` + биндинг на `StackPanel` группы кнопок,
  `Resize_DragStarted`)
- `src/DesktopOrganizer.UI/Configuration/DiConfig.cs` (регистрация синглтона
  `IPanelSelectionCoordinator` — этот файл, а не `DesktopOrganizer.Services/Configuration/DiConfig.cs`,
  — по образцу уже существующих UI-синглтонов `IWindowManager`/`IPageManager`, которые тоже живут в
  `DesktopOrganizer.UI.Services` и регистрируются здесь же; исходный список артефактов плана указывал
  файл ошибочно, `IVirtualDesktopService` блока E — отдельная, действительно Services-слойная
  регистрация и не затронута)
- `tests/DesktopOrganizer.Tests/Unit/Services/PanelSelectionCoordinatorTests.cs` (новый — 10 тестов:
  единичная/множественная доставка события, идемпотентность повторной заявки того же владельца,
  `ClearAll`, отписка, две реентрантных регрессии на рекурсию, `null`-owner guard)
- `tests/DesktopOrganizer.Tests/Unit/ViewModels/ChildWindowViewModelTests.cs` (+9 тестов на интеграцию
  с координатором: заявка/неявка при `null`, реакция на чужую/свою заявку, `Dispose` отписывает,
  сценарий с двумя реальными панелями и защита от рекурсии)

**Finalization (M-1/M-2):**

- `src/DesktopOrganizer.UI/Services/IPanelSelectionCoordinator.cs` (новый метод `Release(object owner)`)
- `src/DesktopOrganizer.UI/Services/PanelSelectionCoordinator.cs` (реализация `Release`)
- `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` (`Dispose()` вызывает
  `_selectionCoordinator.Release(this)`; `OnSelectionClaimed` коммитит активный rename перед сбросом
  выделения)
- `src/DesktopOrganizer.UI/ViewModels/SystemIconsPanelViewModel.cs` (`Dispose()` вызывает
  `_selectionCoordinator.Release(this)`)
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` (новый helper `CommitActiveItemRenameIfAny()`,
  вызывается из трёх A1-хуков перед сбросом `SelectedFile`)

---

## 3. Блок B — геометрия Grid-режима + верхнее выравнивание иконки (задачи 2 и 6)

### 3.1. ОБЯЗАТЕЛЬНЫЙ research-шаг B0 (прямое требование пользователя)

До единой строки кода исполнитель обязан оформить короткий раздел «Research» (в этом файле, §3.7)
с ответами на вопросы ниже и ссылками на источники:

| Вопрос | Что смотреть |
|---|---|
| Как Windows задаёт шаг сетки иконок | `GetSystemMetrics(SM_CXICONSPACING / SM_CYICONSPACING)`; `HKCU\Control Panel\Desktop\WindowMetrics\IconSpacing` / `IconVerticalSpacing`; `LVM_SETICONSPACING` |
| Размеры иконок Explorer'а и как масштабируется ячейка | Extra large 256 / Large 96 / Medium 48 / Small 16; растёт ли поле вокруг иконки пропорционально или остаётся константным |
| Подпись под иконкой | Максимум строк (2 у Explorer), обрезка многоточием, может ли подпись быть шире иконки, где её базовая линия |
| Как рисуется выделение на краю вьюпорта | Explorer/WinUI: chrome выделения **внутри** ячейки, никогда за её границей — из-за клиппинга скролл-вьюера |
| Stardock Fences | Настраиваемый размер иконки, отдельная настройка «icon spacing», привязка ширины fence к колонкам |
| Общий паттерн uniform-grid | cell = content-box + равномерный gutter; на краях контейнера — половина gutter'а (иначе край выглядит «толще» межколоночного шва) |

**Deliverable B0:** сравнительная таблица + **выбранная формула** ячейки, зафиксированная в §3.7,
до правки XAML.

### 3.2. Root-cause: «слева от первой иконки большой пустой отступ»

Разбор фактического левого зазора при `IconSize = 48`:

| Слагаемое | Значение | Откуда |
|---|---|---|
| `FileListHost.Margin.Left` | 4 | `ChildWindow.xaml:196` (`Margin="4,0,4,4"`) |
| `StackPanel.Margin` элемента | 6 | `Templates.xaml:236` (`Margin="6"`), = `GridMetrics.ItemMargin` |
| Внутренний горизонтальный padding бокса | 12 | `BoxSize = IconSize * 1.5` (`GridMetrics.cs:22`), а `Image` с явной `Width` внутри вертикального `StackPanel` **центрируется** ⇒ `(BoxSize − IconSize)/2 = IconSize/4` |
| **Итого до первого пикселя иконки** | **22 px** | |

При этом межколоночный шов = `6 + 6 = 12` px, а зазор у левого края = `4 + 6 = 10` px «пустого»
плюс 12 px внутрибоксового поля. Асимметрия («край толще шва») и доминирующее слагаемое
`IconSize/4` — и есть визуальный дефект.

### 3.3. Root-cause: «выделение первой иконки обрезано слева»

`PanelItemContainerStyle` → `SelectionBorder` объявлен как `Margin="-1"` + `BorderThickness="1"`
(`Templates.xaml:98–104`). Отрицательный margin намеренно компенсирует вклад толщины рамки в
desired-size (чтобы не разъехалась grid-математика — см. комментарий `Templates.xaml:84–90`), но
побочный эффект: рамка **рисуется за пределами arrange-бокса `ListBoxItem`**. `ScrollViewer`
`ListBox`'а клиппит содержимое по своим границам ⇒ у элементов **первой колонки и первой строки**
1-пиксельный штрих рамки срезается. Это ровно тот симптом, что на скриншоте.

Варианты исправления:

- **B-F1 (рекомендуется).** `SelectionBorder Margin="0"`, `BorderThickness="1"`, а `Margin` элемента
  в `GridViewTemplate` уменьшить `6 → 5`. Итоговый footprint: `5 + 1 + content + 1 + 5 = content + 12`
  — **байт в байт прежний `CellSize`**, рамка больше не выходит за arrange-бокс, клиппинга нет.
  `GridMetrics.ItemMargin` остаётся `6`, но его doc-комментарий обязан объяснить расщепление
  «5 px margin шаблона + 1 px рамка выделения».
- B-F2. Оставить `Margin="-1"`, дать `ListBox` `Padding="1"` и учесть +2 в ширине. **Отклонено:**
  ровно тот рассинхрон chrome/`FileListHPadding`, от которого предостерегает комментарий
  `Templates.xaml:176–182`.
- B-F3. Отключить клиппинг (`ClipToBounds="False"` / `CanContentScroll`). **Отклонено:** ломает
  визуал прокрутки и виртуализацию.

### 3.4. Решение D-B1: судьба «desktop grid size» — НЕ удалять

Пользователь предположил «убрать концепцию desktop grid size и всё выводить из размера иконки».
Разбор показывает, что это **две разные сетки**, и они не сводятся друг к другу:

| Сетка | Где живёт | Что делает | Источник |
|---|---|---|---|
| `IAppSettings.GridSize` (1–128) | `IAppSettings.cs:11`, слайдер `MainWindow.xaml:123` | Снаппинг **позиции окна панели** (Left/Top) при drag'е и на drop'е | `ChildWindow.SnapToGrid`, `GridSnap.SnapAxis`, ADR-011 |
| `GridMetrics.BoxSize/CellSize` | `GridMetrics.cs` | Раскладка элементов внутри панели + квантизация **ширины окна** при resize | `Resize_DragDelta`/`SnapWidthToGrid`, `IconBoxWidthConverter` |

Внутрипанельная сетка **уже** целиком выводится из `IconSize` — просьба пользователя в этой части
фактически выполнена. Удаление `GridSize` убрало бы снаппинг позиции панелей, и заменить его
«размером иконки» нельзя: `IconSize` — **пер-панельная** настройка, а сетка позиций — **глобальная**
(две панели с разным `IconSize` не могут иметь общий шаг позиционирования).

**Рекомендация (и рекомендация для основного агента при отчёте пользователю):**
`GridSize` сохранить, но переименовать лейбл/тултип в настройках, чтобы устранить путаницу —
именно она и породила задачу:
- Лейбл: `Panel position grid` (сейчас — секция про «Desktop icon grid cell size»).
- Тултип: `Snap step for panel window position, in pixels. 1 disables snapping. Does not affect icon spacing inside a panel.`

Если пользователь всё же настаивает на удалении — это отдельный **CLARIFICATION** с вариантами
A: сохранить и переименовать (рекомендуется) / B: удалить `GridSize` и снаппить позицию по константе
(например 8 px) / C: удалить и вовсе отказаться от снаппинга позиции. Реализующему агенту
**не решать это самостоятельно** — выполнять вариант A.

### 3.5. Целевая модель ячейки (стартовая гипотеза для B0)

```
IconSlotHeight = IconSize                    // фиксированная строка под иконку (см. §3.6)
LabelHeight    = 32                          // 2 строки FontSize=11, уже MaxHeight="32"
BoxWidth       = round(IconSize * K)         // K ∈ [1.3 … 1.5], уточнить в B0
Gutter         = 6                           // константа, НЕ пропорциональна IconSize (гипотеза B0)
CellSize       = BoxWidth + 2 * Gutter
```

Отдельное решение по краевому зазору: чтобы левый край не выглядел толще межколоночного шва,
`FileListHost.Margin` по горизонтали должен давать **половину** шва, т.е. `0` при `Gutter = 6`
(тогда край = 6 px, шов = 12 px = 2 × 6 — классическое правило «половина gutter'а на краях»).
Это меняет `GridMetrics.FileListHPadding` (`8 → 0`) и, соответственно, формулу ширины окна:
`Width = n * CellSize + FileListHPadding`. **Обязательно** обновить существующие тесты
`GridMetrics` и `tests/DesktopOrganizer.Tests/Unit/Views/ChildWindowReorderGeometryTests.cs`.

`MaxHeight="32"` у подписи заменить на фиксированную `Height="32"`, иначе элементы с однострочной
подписью выше/ниже соседей и подписи «пляшут» по Y — а по задаче 6 они обязаны быть на одном уровне.

### 3.6. Задача 6 — иконка прижата к верху бокса

Текущее: `<Image Width="{IconSize}" Height="{IconSize}"/>` (`Templates.xaml:240–242`),
`Stretch` по умолчанию `Uniform` ⇒ неквадратный источник получает letterbox **по центру** — сверху
и снизу поровну.

Целевое: тот же общий canvas, подпись на прежнем Y, но картинка прижата к **верхней** границе слота;
весь остаточный зазор уходит вниз, к подписи.

Реализация:

```xml
<Grid Height="{Binding DataContext.Model.IconSize, RelativeSource={RelativeSource AncestorType=Window}}">
    <Image Source="{Binding Icon}"
           Width="{Binding DataContext.Model.IconSize, RelativeSource={RelativeSource AncestorType=Window}}"
           MaxHeight="{Binding DataContext.Model.IconSize, RelativeSource={RelativeSource AncestorType=Window}}"
           Stretch="Uniform"
           VerticalAlignment="Top"
           HorizontalAlignment="Center"
           RenderOptions.BitmapScalingMode="HighQuality"/>
</Grid>
```

Почему именно так:
- **`Height` снимается с `Image`** — иначе `Uniform` продолжит центрировать внутри заданного квадрата.
- **Фиксированная `Height` у обёртки `Grid`** — слот всегда одинаковой высоты, значит подпись всегда
  на одном Y, **включая** случай `Icon == null` (битый путь): раньше высоту держала явная `Height`
  у `Image`, после её снятия без обёртки строка схлопнулась бы в 0 и подпись «уехала» вверх.
- `MaxHeight` вместо `Height` — источник выше своей ширины не вылезет за слот.
- `RenderOptions.BitmapScalingMode="HighQuality"` — паритет с боковой панелью
  (`SystemIconsPanelWindow.xaml:67`), где это уже стоит; в `GridViewTemplate` отсутствует.

Edge-cases для DoD: широкая неквадратная иконка (зазор уходит вниз), высокая неквадратная (ширина
уменьшается, иконка по центру по X), `Icon == null` (слот сохраняет высоту), `Exists == false`
(`Opacity 0.45` из `FileItemRootStyle` продолжает работать), активный inline-rename (`TextBox`
подменяет `TextBlock` — обе высоты должны совпадать, иначе элемент «прыгает» при входе в rename).

### 3.7. Research (заполнено исполнителем блока B по итогам B0)

> Окружение агента без выхода в интернет (нет browser/WebFetch-инструмента в этой сессии). Ответы
> ниже — задокументированные, стабильные годами факты Win32/Explorer API из обучающих данных модели
> (`GetSystemMetrics`, `LVM_SETICONSPACING`, стандартные размеры значков Explorer), перепроверенные
> на внутреннюю согласованность с уже существующим кодом/комментариями этого репозитория
> (`GridMetrics.cs`, `Templates.xaml`, ADR-011/ADR-013/ADR-014). Где утверждение — общее инженерное
> знание паттерна (uniform-grid + gutter), а не цитата конкретного документа Microsoft, это отмечено
> явно. Это не заменяет проверку по официальной документации, если у оркестратора появится доступ к
> сети — тогда стоит перепроверить точные числовые константы (`IconSpacing`/`IconVerticalSpacing`
> default-значения) перед публикацией в ADR-015.

| Вопрос | Ответ |
|---|---|
| Как Windows задаёт шаг сетки иконок | `GetSystemMetrics(SM_CXICONSPACING / SM_CYICONSPACING)` возвращает горизонтальный/вертикальный шаг сетки значков рабочего стола в пикселях; источник значения — `HKCU\Control Panel\Desktop\WindowMetrics\IconSpacing` / `IconVerticalSpacing` (хранится в twips — 1/20 пункта, отрицательное значение по соглашению этого куста реестра). Значение — это **шаг между центрами/якорями соседних ячеек**, т.е. уже включает и размер значка, и зазор вокруг него, а не только зазор. Проекции: это тот же класс величины, что наш `CellSize` — «шаг сетки», а не «содержимое минус отступ». Список‑вью Explorer'а (`SysListView32`) имеет собственный, локальный аналог — сообщение `LVM_SETICONSPACING`, которым конкретное окно List View может переопределить системный шаг под свой масштаб иконок (что и объясняет, почему шаг в окне Explorer отличается от шага значков собственно рабочего стола). |
| Размеры иконок Explorer'а и как масштабируется ячейка | Стандартная лестница размеров: Small 16, Medium 48, Large 96, Extra Large 256 px. Экспериментально наблюдаемое (и общеизвестное) поведение классического проводника — зазор/поле вокруг значка растёт **вместе** с размером значка, но не строго линейно 1:1 — визуальное соотношение «ячейка / значок» у Explorer держится в диапазоне примерно 1.3×–1.6× в разных масштабах, т.е. ближе к константному *коэффициенту*, чем к константному числу пикселей на каждом шаге. Это прямо поддерживает уже принятую в кодовой базе формулу `BoxSize = IconSize * 1.5` (коэффициент `K = 1.5`, см. `GridMetrics.BoxSize`) — она попадает в этот наблюдаемый диапазон и не требует пересмотра в рамках B0. |
| Подпись под иконкой | Explorer ограничивает подпись двумя строками, дальше — обрезка многоточием (`TextTrimming`/`…`), подпись **не** шире колонки/значка (центрирована по ширине ячейки, а не по ширине текста), базовая линия текста — сразу под зарезервированной зоной значка, без дополнительного «прыжка» независимо от фактической высоты картинки. В нашем шаблоне это уже соответствует `TextAlignment="Center"`, `TextWrapping="Wrap"`, `TextTrimming="CharacterEllipsis"` и фиксированной высоте лейбла (см. §3.5 — `MaxHeight="32"` → `Height="32"`, чтобы «под значком всегда одна и та же строка Y» было гарантировано, а не только «как правило»). |
| Как рисуется выделение на краю вьюпорта | И классический Explorer, и современный WinUI `GridView`/`ListView` рисуют chrome выделения **строго внутри** собственного arrange-прямоугольника элемента — этот прямоугольник образует границу клиппинга `ScrollViewer`'а; ничего не рисуется «за» границей элемента. Это прямая причина найденного в §3.3 дефекта: наш `SelectionBorder` с `Margin="-1"` рисует один пиксель рамки **за пределами** arrange-бокса `ListBoxItem`, что и обрезается `ScrollViewer` у первой строки/колонки. Решение B-F1 приводит наш код к тому же инварианту, что у Explorer/WinUI — рамка целиком внутри арендж-бокса. |
| Stardock Fences | Fences даёт пользователю настраиваемый размер значка (слайдер/пресеты) и отдельную настройку интервала между значками («icon spacing» — управляется отдельно от размера значка, что подтверждает вывод §3.4: сетка позиционирования и сетка раскладки значков — разные независимые оси управления, а не одна производная величина). Ширина самого fence-окна привязана к целому числу колонок значков — то же требование, что уже реализовано в этом проекте через `GridMetrics.CellSize`+`Quantize`/`SnapWidthToGrid` (§3.2, `Resize_DragDelta`). |
| Общий паттерн uniform-grid | Устоявшийся инженерный паттерн (не специфичный для одного вендора): `cell = content-box + равномерный gutter`, а на **краях** контейнера — не полный `gutter`, а **половина** его (иначе видимый отступ у края в 2 раза «толще», чем шов между двумя соседними ячейками, — асимметрия, которая и была найдена как корневая причина в §3.2). Отсюда прямое следствие для этого репозитория: `FileListHost.Margin` по горизонтали должен давать ровно половину межколоночного шва, что и реализовано в §3.5 (`FileListHPadding: 8 → 0`, при этом «половина шва» уже даёт сама рамка+отступ элемента). |

**Выбранная формула ячейки (итог B0 — фиксируется, реализуется в §3.3–§3.6):**

```
IconSlotHeight = IconSize                         // фиксированный слот под иконку, top-align (§3.6)
LabelHeight    = 32                                // фиксированная (было MaxHeight, стало Height)
BoxWidth       = round(IconSize * 1.5)             // K = 1.5 — уже в коде (GridMetrics.BoxSize), в
                                                    // допустимом диапазоне Explorer (1.3–1.6×) — не меняется
Gutter         = 6                                 // константа, НЕ пропорциональна IconSize; расщепляется
                                                    // как 5px margin шаблона + 1px рамка выделения (B-F1)
CellSize       = BoxWidth + 2 * Gutter             // формула не меняется — footprint сохранён байт-в-байт
```

Правило края: `FileListHost.Margin` по горизонтали = `0` (было `4,0,4,4` → `0,0,0,4`), потому что
сам элемент уже даёт `Gutter = 6` px до первого пикселя иконки за счёт своего собственного отступа
(`5px margin + 1px рамка`) — этого достаточно, чтобы край (`6px`) был ровно половиной межколоночного
шва (`6 + 6 = 12px`), без дополнительного отступа от `FileListHost`.

### 3.8. DoD блока B — статус

1. [x] §3.7 заполнен: таблица источников + выбранная формула ячейки.
2. [x] (код) Выделение элемента **первой колонки и первой строки** отрисовано полной рамкой со всех
   четырёх сторон (нет срезанного штриха) — `SelectionBorder Margin="0"` вместо `"-1"` (B-F1).
   ⚠️ manual verify + скриншот — на пользователе (см. §8 п.1).
3. [x] (код) Левый зазор до первой иконки равен половине межколоночного шва — `FileListHost.Margin`
   `4,0,4,4 → 0,0,0,4`, `GridMetrics.FileListHPadding` `8 → 0`. ⚠️ визуальная симметрия — manual
   verify (см. §8 п.2).
4. [x] `CellSize` по-прежнему = footprint элемента байт в байт (`GridMetrics.CellSize`/`BoxSize`
   формулы не менялись; footprint 5px margin + 1px рамка = прежние 6px); тест-инвариант
   `CellSize_EqualsBoxSizePlusTwoItemMargins` зелёный. ⚠️ визуальное отсутствие «дрожания» при resize
   — manual verify (см. §8 п.4).
5. [x] (код) Все элементы одинаковой высоты; подпись на одном Y — `TextBlock.Height="32"` (было
   `MaxHeight`), `TextBox` edit-режима тоже `Height="32"` (чтобы rename не «прыгал»).
6. [x] (код) Иконка прижата к верху слота — `Image` без явной `Height`, `VerticalAlignment="Top"`,
   обёрнута в `Grid Height="{IconSize}"`.
7. [x] (код) `Icon == null` не ломает выравнивание — высоту держит обёртка `Grid`, а не `Image`.
8. [x] `GridMetrics` обновлён и зелёный (`dotnet test` — все `GridMetricsTests` проходят, включая
   новые `CellSize_EqualsBoxSizePlusTwoItemMargins`, `Quantize_IsIdempotent_OnAlreadyQuantisedValue`,
   `FileListHPadding_IsZero`). `ChildWindowReorderGeometryTests` не ссылается на изменённые константы
   (`ItemMargin`/`FileListHPadding`/`BoxSize`/`CellSize`) — правок не потребовалось, тесты зелёные без
   изменений.
9. [x] Doc-комментарии `GridMetrics` и `PanelItemContainerStyle` (+ doc-комментарий в
   `GridViewTemplate` рядом с `Margin="5"`, + doc-комментарий `FileListHost.Margin` в `ChildWindow.xaml`)
   описывают новую модель (расщепление margin/border, роль `FileListHPadding`).
10. [x] `GridSize` НЕ удалён; лейбл `Panel position grid` + тултип `Snap step for panel window
    position, in pixels. 1 disables snapping. Does not affect icon spacing inside a panel.` добавлены
    в `MainWindow.xaml` (вариант A по §3.4, без пересмотра).

`dotnet build` и таргетированный `dotnet test` (Block B файлы) — зелёные, 0 новых предупреждений.
Полный прогон `dotnet test` содержит 6 упавших тестов (`RollUpTests` × 3, `NativeNameAttributeTests`
× 2, `SystemIconsPanelViewModelTests` × 1) — все они **предсуществующие/environment-specific**, не
касаются файлов Блока B (Converters/RollUp, Attributes, SystemIconsPanelViewModel — редактируются
блоками C/D/окружением, не B) и воспроизводятся идентично на `git stash` снимке до правок блока B.

### 3.9. Артефакты блока B

- `src/DesktopOrganizer.UI/Models/GridMetrics.cs`
- `src/DesktopOrganizer.UI/Converters/IconBoxWidthConverter.cs`
- `src/DesktopOrganizer.UI/Resources/Templates.xaml` (`GridViewTemplate`, `PanelItemContainerStyle`)
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (`FileListHost.Margin`)
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` (`SnapWidthToGrid`, если поменялся `FileListHPadding`)
- `src/DesktopOrganizer.UI/Views/MainWindow.xaml` (лейбл/тултип `GridSize`)
- `tests/.../Unit/Views/ChildWindowReorderGeometryTests.cs`, `tests/.../Unit/Models/GridMetricsTests.cs`
- Новый **ADR-015** «Модель геометрии сетки значков панели» (пишет `architecture-documenter`)

---

## 4. Блок C — кнопки заголовка (задача 3)

### 4.1. Порядок кнопок

`ChildWindow.xaml:139–169`, `StackPanel Orientation="Horizontal"`. Сейчас:
`SortModeToggleButton` → `RollUpToggleButton` → безымянная View-кнопка.
Требуемое: `SortModeToggleButton` → **View** → `RollUpToggleButton`
(крайняя правая — свернуть/развернуть; сортировка — крайняя левая).
Правка — только перестановка узлов XAML.

### 4.2. Кнопка View

Текущее: `Content="⊞"`, обычный шрифт, `FontSize="14"`, без `x:Name`, без `FontFamily` —
**визуально выпадает** из группы (соседи — `Segoe MDL2 Assets`, `FontSize="12"`).

Требуется:
- `x:Name="ViewModeToggleButton"`, `FontFamily="Segoe MDL2 Assets"`, `FontSize="12"` — как у соседей.
- Новый конвертер `src/DesktopOrganizer.UI/Converters/ViewModeGlyphConverter.cs` — зеркало
  `SortModeGlyphConverter`: `ConverterParameter` по умолчанию = глиф, `="tooltip"` = текст тултипа.
  Биндится к `Model.ViewMode`.
- **Конвенция глифа (решение, зафиксировать в ADR/комментарии): кнопка показывает ЦЕЛЕВОЙ режим** —
  то, что получишь по клику. Это согласовано с решением §4.3 «состояние показываем подсветкой, а не
  подменой глифа»: у Sort-кнопки глиф = функция, состояние = подсветка; у View-кнопки глиф = функция
  (переключить на такой-то режим). Оба глифа обязаны иметь тултип, снимающий двусмысленность.
- Кандидаты глифов `Segoe MDL2 Assets` (**исполнитель обязан сверить кодпойнты по официальному
  списку глифов и вставить XAML-escape `&#xE___;`, а не литеральный символ**):
  - цель Grid: `ViewAll` (`E8A9`), запасной вариант `Tiles` (`ECA5`)
  - цель List: `List` (`E8FD`)
- Тултипы (английский, точные строки): `Switch to grid view` / `Switch to list view`.

### 4.3. Кнопка сортировки

**Функциональность не меняется** (прямое подтверждение пользователя): `ToggleSortModeCommand`,
`PanelSortMode { Manual, Name }`, никакого направления сортировки asc/desc не добавляем.

Что меняется:
1. **Глиф постоянный** — всегда `Sort` (`&#xE8CB;`, тот же, что сейчас в ветке `Name`
   `SortModeGlyphConverter.cs:28`). Ветка `GripperBarHorizontal` (`&#xE76F;`) больше не используется.
2. **Состояние — подсветкой.** Кнопка визуально «нажата/выбрана», когда
   `EffectiveSortMode == PanelSortMode.Name`, и обычная при `Manual`.
3. Тултип остаётся зависимым от режима — строки не меняем:
   `Sorted by name — click to arrange manually` / `Manual order — click to sort by name`.

**Реализация подсветки — решение C-F1 (рекомендуется):**
оставить `Button` (не `ToggleButton`) и вести подсветку через `Style` + `DataTrigger` на новом
свойстве VM. `ToggleButton` отклонён: он меняет `IsChecked` локально по клику, что конфликтует с
`OneWay`-биндингом к VM и даёт рассинхрон в Portal-режиме, где команда отключена.

- Новое свойство `ChildWindowViewModel.IsSortedByName => EffectiveSortMode == PanelSortMode.Name`.
- **Риск (повторение уже случавшегося дефекта CRITICAL-2, см. комментарии
  `ChildWindowViewModel.cs:715–719`):** `EffectiveSortMode` уведомляется вручную минимум из четырёх
  мест (`:500`, `:553`, `:719`, плюс пути `LoadFromModel`/`SelectSourceFolder`). Забыть одно —
  подсветка «залипнет».
  **Обязательное митигирование:** ввести приватный хелпер
  `private void NotifySortModeChanged()`, поднимающий `EffectiveSortMode` **и** `IsSortedByName`,
  и заменить им **все** существующие `OnPropertyChanged(nameof(EffectiveSortMode))`.
  Unit-тест обязан проверять оба уведомления на всех четырёх путях.
- Новый общий стиль `PanelHeaderButtonStyle` в `Templates.xaml` (`TargetType="Button"`):
  `Width/Height=28`, `FontFamily="Segoe MDL2 Assets"`, `FontSize=12`, `Foreground="#AAAAAA"`,
  `Background="Transparent"`, `BorderThickness="0"`, `ControlTemplate` = `Border` + `ContentPresenter`
  с `CornerRadius="3"`. Триггеры:
  - `IsMouseOver` → `Background="#1AFFFFFF"` (те же токены, что у `PanelItemContainerStyle`);
  - `IsEnabled=False` → `Opacity="0.5"` (нужно для Portal, см. ниже);
  - «checked» — отдельный производный стиль `PanelHeaderToggleButtonStyle` с `DataTrigger` на
    `IsSortedByName=True` → `Background="#4D3D8BFF"`, `BorderBrush="#B33D8BFF"`,
    `BorderThickness="1"`, `Foreground="#FFFFFF"`.
    **Токены обязаны быть теми же, что у выделения элемента** — это единый визуальный язык
    «выбрано» на весь проект (требование задачи 4).
  Применить `PanelHeaderButtonStyle` ко всем трём кнопкам — заодно уходит троекратное дублирование
  атрибутов в `ChildWindow.xaml`.
- **Portal edge-case:** в панели-портале `ToggleSortModeCommand.CanExecute == false`, но
  `EffectiveSortMode` принудительно `Name` ⇒ кнопка окажется одновременно **подсвеченной и
  disabled**. Это семантически верно («отсортировано по имени, изменить нельзя») и должно быть
  явно зафиксировано в DoD; за счёт `IsEnabled=False → Opacity 0.5` она не выглядит кликабельной.

### 4.4. Судьба `SortModeGlyphConverter`

Ветка глифа умирает, ветка тултипа остаётся. Решение: **переименовать в
`SortModeTooltipConverter`**, убрать параметр `"glyph"`/ветку глифа, упростить сигнатуру
(`Convert` возвращает только текст тултипа), обновить ключ ресурса в `Templates.xaml:14`
(`SortModeGlyph` → `SortModeTooltip`) и его юнит-тесты. Оставлять мёртвую ветку нельзя — её
подхватит следующий агент.

### 4.5. DoD блока C — статус

1. [x] Порядок кнопок слева направо: Sort, View, RollUp (`ChildWindow.xaml`, только перестановка
   узлов StackPanel).
2. [x] Все три кнопки визуально однородны (один шрифт, размер, цвет, hover) — вынесены в общий
   `PanelHeaderButtonStyle`, применённый ко всем трём `Button`; устранено троекратное дублирование
   атрибутов.
3. [x] Глиф Sort-кнопки постоянен и не зависит от режима — статичный `Content="&#xE8CB;"` прямо в
   XAML, ветка `GripperBarHorizontal` (`&#xE76F;`) удалена вместе со старым конвертером.
4. [x] Sort-кнопка подсвечена ⇔ `EffectiveSortMode == Name` (через `IsSortedByName`); подсветка —
   `PanelHeaderToggleButtonStyle` с `DataTrigger` на `IsSortedByName=True`, теми же токенами, что
   выделение элемента — с финализации (MINOR m-1) это общие ресурсы `{StaticResource SelectionFillBrush}`
   / `{StaticResource SelectionStrokeBrush}` (`#4D3D8BFF` / `#B33D8BFF`, объявлены один раз в начале
   `Templates.xaml`), плюс `BorderThickness=1`/`Foreground=#FFFFFF`. **Finalization (MINOR m-6):**
   наведение мышью на уже подсвеченную (активную) Sort-кнопку раньше гасило подсветку — унаследованный
   из `PanelHeaderButtonStyle` `ControlTemplate.Triggers`'ный `IsMouseOver`-триггер пишет
   `HeaderButtonBorder.Background` напрямую по `TargetName`, что как template-триггер перебивает
   Style-уровневый `DataTrigger`. Фикс: `PanelHeaderToggleButtonStyle` переопределяет `Template`,
   добавляя в конец того же `ControlTemplate.Triggers` `MultiDataTrigger` (`IsMouseOver=True` через
   `RelativeSource=TemplatedParent` **и** `IsSortedByName=True`), который повторно применяет
   `SelectionFillBrush`/`SelectionStrokeBrush` — выигрывает по правилу «последний в коллекции
   побеждает» при одновременном совпадении условий, не трогая простой hover неактивной кнопки.
5. [x] Подсветка обновляется мгновенно на всех путях смены режима — все обнаруженные через
   `grep EffectiveSortMode` пути (клик по кнопке — `ToggleSortModeCommand`/`ToggleSortMode`; пункты
   меню «Manual»/«By name» — `SetSortModeCommand` пишет `Model.SortMode`, ловится
   `OnModelPropertyChanged`; включение/выключение Folder Portal — `LoadFromModel`,
   `SelectSourceFolder`; drag-to-reorder — `ReorderItem` тоже пишет `Model.SortMode`, тот же
   `OnModelPropertyChanged` путь) идут через единственный источник — `Model.SortMode`-сеттер плюс
   ровно 3 явных вызова `NotifySortModeChanged()` (в `LoadFromModel`, `SelectSourceFolder`,
   `OnModelPropertyChanged`'s `SortMode`-ветке — все три найдены и заменены, живых
   `OnPropertyChanged(nameof(EffectiveSortMode))` не осталось, см. п.10 ниже).
6. [x] В Portal-панели кнопка подсвечена и disabled (`Opacity 0.5` от `IsEnabled=False`-триггера
   `PanelHeaderButtonStyle`), клик ничего не делает (`ToggleSortModeCommand.CanExecute() == false`
   при `IsPortal`). Явно зафиксировано как ожидаемое поведение (не баг) в комментарии над
   `SortModeToggleButton` в `ChildWindow.xaml`.
7. [x] View-кнопка показывает целевой режим (Grid↔List), тултип соответствует
   (`Switch to grid view` / `Switch to list view`). Кодпойнты: Grid-target = `ViewAll` **`U+E8A9`**,
   List-target = `List` **`U+E8FD`** — записаны как C# `\uXXXX`-escape внутри
   `ViewModeGlyphConverter` (не XAML `&#x...;`, поскольку глиф вычисляется в конвертере, а не задан
   статично в разметке, в отличие от Sort-кнопки). Верификация кодпойнтов: у этого агента нет
   доступа в сеть, поэтому вместо прямой сверки по официальному списку выполнена косвенная
   перекрёстная проверка — байты уже существовавшего в кодовой базе `SortModeGlyphConverter.cs`
   (до его переименования) были декодированы из UTF-8 напрямую (`EE A3 8B` → `U+E8CB` для ветки
   `Name`/`Sort`, `EE 9D AF` → `U+E76F` для ветки `Manual`/`GripperBarHorizontal`), подтвердив, что
   план оперирует настоящими кодпойнтами Segoe MDL2 Assets, а не плейсхолдерами — те же кодпойнты,
   что широко задокументированы для `Symbol.Sort`/`Symbol.GripperBarHorizontal` в справочниках
   UWP/WinUI. `ViewAll (E8A9)`/`List (E8FD)` взяты из того же известного справочного списка по
   аналогии; **рекомендуется визуальная manual-verify проверка на реальной Windows-машине**, что оба
   глифа рендерятся ожидаемыми пиктограммами (см. общий раздел §8) — запасной вариант `Tiles (ECA5)`
   не потребовался.
8. [x] `SortModeGlyphConverter` переименован в `SortModeTooltipConverter`, ветка/параметр `"glyph"`
   убраны, `Convert` возвращает только текст тултипа безусловно (параметр больше не читается);
   ключ ресурса в `Templates.xaml` обновлён `SortModeGlyph → SortModeTooltip`; старый файл конвертера
   удалён, никаких ссылок на старое имя в `src`/`tests` не осталось (проверено `grep`).
9. [x] Новые тесты: `ViewModeGlyphConverterTests` (8 тестов), `SortModeTooltipConverterTests`
   (5 тестов) — оба зелёные. `ChildWindowViewModelTests` дополнен 5 тестами на
   `IsSortedByName`/`EffectiveSortMode`: `LoadFromModel_EntersPortal_...`,
   `ModelSortModeChanged_...`, `ToggleSortModeCommand_...`, `IsSortedByName_ReflectsEffectiveSortMode_...`
   плюс задокументированное (не автоматизированное) покрытие для `SelectSourceFolder` — см. п.10.
10. **Уточнение к риску R4 (обнаруженные пути vs. четыре из плана):** `grep EffectiveSortMode` по
    всему `ChildWindowViewModel.cs` **до** правки нашёл ровно **3** места с
    `OnPropertyChanged(nameof(EffectiveSortMode))` (в `LoadFromModel`, `SelectSourceFolder`,
    `OnModelPropertyChanged`), а не 4, как осторожно предполагал план (план сам отметил, что список
    может быть неполным) — все 3 заменены на `NotifySortModeChanged()`. `SelectSourceFolder` **не
    покрыт unit-тестом**: метод инстанцирует `new Microsoft.Win32.OpenFolderDialog().ShowDialog()`
    инлайн, без DI-шва — эта диалоговая зависимость не мокается существующей тестовой
    инфраструктурой (и не мокалась ни для одного теста в проекте ранее — `grep` подтверждает
    отсутствие любых тестов `SelectSourceFolderCommand` до этой правки). Введение шва
    (`IFolderPickerService`-подобная абстракция) — архитектурное изменение вне объёма Блока C;
    корректность этого конкретного вызова подтверждена ревью кода (тот же вызов
    `NotifySortModeChanged();`, что и в двух покрытых тестами путях), задокументирована как
    code-reviewed-but-not-unit-tested прямо в тестовом файле (комментарий рядом с местом, где тест
    ожидался).

**Finalization (code-quality-reviewer, финальное ревью всей ветки):**

11. [x] **MINOR m-1 — дублирование хардкод-токенов выделения.** `#4D3D8BFF`/`#B33D8BFF` были продублированы
    в `PanelItemContainerStyle`, `PanelHeaderToggleButtonStyle`, `FileNameEditBoxStyle`. Вынесены в
    `SelectionFillBrush`/`SelectionStrokeBrush` в начале `Templates.xaml`; все шесть вхождений заменены
    на `{StaticResource ...}`.
12. [x] **MINOR m-6 — hover перебивал подсветку активной Sort-кнопки** — см. п.4 выше.
13. [x] `dotnet build`/`dotnet test` после m-1/m-6 — зелёные, 642/642, без новых StyleCop-предупреждений.

### 4.6. Артефакты блока C

- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (перестановка Sort→View→RollUp, применение
  `PanelHeaderButtonStyle`/`PanelHeaderToggleButtonStyle`, статичный Sort-глиф)
- `src/DesktopOrganizer.UI/Resources/Templates.xaml` (новые стили `PanelHeaderButtonStyle` +
  `PanelHeaderToggleButtonStyle`, ключи конвертеров `SortModeGlyph → SortModeTooltip`,
  добавлен `ViewModeGlyph`)
- `src/DesktopOrganizer.UI/Converters/ViewModeGlyphConverter.cs` (новый)
- `src/DesktopOrganizer.UI/Converters/SortModeGlyphConverter.cs` → `SortModeTooltipConverter.cs`
  (переименован, упрощён — файл удалён и создан заново под новым именем)
- `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` (`IsSortedByName`,
  `NotifySortModeChanged`, все 3 найденных вызова `OnPropertyChanged(nameof(EffectiveSortMode))`
  заменены)
- `tests/DesktopOrganizer.Tests/Unit/Converters/ViewModeGlyphConverterTests.cs` (новый)
- `tests/DesktopOrganizer.Tests/Unit/Converters/SortModeTooltipConverterTests.cs` (новый, заменяет
  отсутствовавший ранее тест на `SortModeGlyphConverter`)
- `tests/DesktopOrganizer.Tests/Unit/ViewModels/ChildWindowViewModelTests.cs` (+5 тестов на
  `IsSortedByName`/`EffectiveSortMode`)

---

## 5. Блок D — выделение в боковой панели (задача 4)

### 5.1. Текущее состояние

`SystemIconsPanelWindow.xaml:30–80`: `ItemsControl` + `Border` на элемент. Ни `IsSelected`, ни
`SelectedItem` — **выделения нет вообще**. Двойной клик открывает (`MouseBinding`), ПКМ показывает
нативное меню (`IconButton_PreviewMouseRightButtonUp`), drag-reorder висит на `ItemsControl`.

### 5.2. 🐞 Найденный предсуществующий дефект (обязателен к починке в этом блоке)

`SystemIconsPanelWindow.xaml.cs:110`:

```csharp
var button = FindAncestorOrSelf<Button>(source);
if (button?.DataContext is SystemIconItemModel item) { _dragItem = item; }
```

Шаблон элемента больше **не содержит `Button`** — он был заменён на `Border x:Name="IconBorder"`
(комментарий «FIX 5», `SystemIconsPanelWindow.xaml:45–50`). Значит `FindAncestorOrSelf<Button>`
всегда возвращает `null`, `_dragItem` всегда `null` ⇒ **drag-reorder иконок в боковой панели сейчас
не работает вообще** (мёртвый код). ROADMAP при этом содержит пункт manual-verify «Drag-reorder
иконок внутри панели работает без артефактов» (ADR-007, п. 11) — он не выполнялся.

Исправление: искать не тип элемента, а носителя данных — подниматься по дереву до первого
`FrameworkElement`, чей `DataContext is SystemIconItemModel`. При миграции на `ListBox` (§5.3)
эквивалент — `FindAncestorOrSelf<ListBoxItem>`, как это уже сделано в `ChildWindow.xaml.cs`.

### 5.3. Решение: миграция `ItemsControl` → `ListBox`

- **D-F1 (рекомендуется):** `ListBox` + `ItemContainerStyle="{StaticResource PanelItemContainerStyle}"`,
  `SelectionMode="Single"`, `Background="Transparent"`, `BorderThickness="0"`, `Padding="0"`,
  `ScrollViewer.HorizontalScrollBarVisibility="Disabled"`,
  `SelectedItem="{Binding SelectedItem, Mode=TwoWay}"`; `ItemsPanel` остаётся вертикальным
  `StackPanel` (drag-логика в code-behind ищет именно `StackPanel` — `FindVisualChild<StackPanel>`,
  `SystemIconsPanelWindow.xaml.cs:152`, `:182`).
  Плюсы: тот же визуальный язык выделения **без дублирования hex-токенов**, бесплатный hover,
  готовая семантика single-selection.
- D-F2: остаться на `ItemsControl`, добавить `IsSelected` в `SystemIconItemModel` + `DataTrigger`'ы.
  **Отклонено:** дублирует токены выделения (два места правды на один визуальный язык) и требует
  ручной логики «снять выделение с остальных».

Обязательные проверки при миграции (ListBoxItem перехватывает часть мышиных событий):
1. ПКМ → нативное меню Shell по-прежнему работает (обработчик `Preview*`, туннелирует раньше — OK,
   но проверить `e.Handled`).
2. Двойной клик открывает элемент (`MouseBinding` на `LeftDoubleClick` внутри шаблона).
3. Drop файлов на «Корзину» (`IconButton_DragOver`/`IconButton_Drop` на `Border` внутри шаблона).
4. Drag-reorder (с учётом §5.2).
5. `SizeToContent="Width"` окна: `ListBox` вносит свой `ScrollViewer` — ширина не должна «схлопнуться»
   или начать расти. **Важно:** `PanelItemContainerStyle` с `Margin="-1"` дал бы здесь тот же
   срезанный штрих слева (§3.3) — поэтому **D после B**.

### 5.4. ViewModel

- `SystemIconsPanelViewModel.SelectedItem : SystemIconItemModel?` (INPC).
- Обнуление, если выбранный элемент перестал быть видимым или исчез из коллекции
  (`RebuildVisibleItems`) — иначе повиснет ссылка на невидимый элемент.
- Сохранение выделения при reorder (элемент тот же, меняется только порядок).
- Подключение к `IPanelSelectionCoordinator` из блока A: `NotifySelected(this)` при ненулевом
  выделении, сброс по `SelectionClaimed` от чужого владельца, отписка в `Dispose()`.
- **Выделение НЕ персистится** (не попадает в `PanelDto`) — это транзиентное UI-состояние.

### 5.5. Сброс выделения (правило из задачи 1, область охвата)

- Клик по пустому месту боковой панели (padding `Border`'а, зона ниже последнего элемента) →
  сброс выделения панели. Реализация — тот же паттерн, что `FileListHost_PreviewMouseLeftButtonDown`:
  если `FindAncestorOrSelf<ListBoxItem>` вернул `null`, обнулить `SelectedItem`.
- Клик по fence-панели → сброс выделения в боковой панели, и наоборот (через координатор блока A).
- Двойной клик по элементу оставляет его выделенным (открытие ≠ снятие выделения) — как в Explorer.

### 5.6. DoD блока D — статус

1. [x] Одиночный клик по иконке боковой панели выделяет её; визуал идентичен выделению в fence-панели
   — за счёт переиспользования того же `PanelItemContainerStyle` (`ItemContainerStyle=
   "{StaticResource PanelItemContainerStyle}"` в `SystemIconsPanelWindow.xaml`), без дублирования
   hex-токенов `#4D3D8BFF`/`#B33D8BFF`, того же радиуса (`CornerRadius="3"`) и того же hover
   (`#1AFFFFFF`). ⚠️ визуальное совпадение — manual verify (см. §8, п. 10).
2. [x] Выделен максимум один элемент боковой панели — `ListBox.SelectionMode="Single"` +
   `SelectedItem="{Binding SelectedItem, Mode=TwoWay}"`, `SystemIconsPanelViewModel.SelectedItem`
   типа `SystemIconItemModel?` (единственное поле, не индекс/список).
3. [x] Клик по пустому месту боковой панели снимает выделение —
   `SystemIconsPanelWindow.IconsListBox_PreviewMouseLeftButtonDown`: если
   `FindAncestorOrSelf<ListBoxItem>(source)` вернул `null`, `_viewModel.SelectedItem = null;`, без
   `e.Handled` (тот же паттерн, что `ChildWindow.FileListHost_PreviewMouseLeftButtonDown`).
4. [x] Выделение в fence-панели снимает выделение в боковой и наоборот — оба ViewModel'а подключены
   к одному синглтону `IPanelSelectionCoordinator` (блок A, без изменения контракта):
   `SystemIconsPanelViewModel.SelectedItem`'s setter вызывает `NotifySelected(this)` при ненулевом
   значении, `OnSelectionClaimed` обнуляет `SelectedItem`, когда владелец — не эта ViewModel, с тем
   же `_suppressCoordinatorEcho`-флагом, что в `ChildWindowViewModel` (защита от рекурсии, risk R3).
   Покрыто интеграционным unit-тестом с реальным (не mock) координатором между
   `ChildWindowViewModel` и `SystemIconsPanelViewModel`:
   `ChildWindowViewModelTests.TwoPanels_SharingCoordinator_ChildWindowAndSystemIconsPanel_SelectingInOneClearsTheOther`,
   плюс аналогичный тест между двумя экземплярами `SystemIconsPanelViewModel`:
   `SystemIconsPanelViewModelTests.SharingCoordinatorWithAnotherSidebarInstance_SelectingInOneClearsTheOther`.
5. [x] (код, не тронуто по сути) Двойной клик по-прежнему открывает системный объект — `MouseBinding
   Gesture="LeftDoubleClick"` на `IconBorder` внутри `DataTemplate` не пострадал от миграции на
   `ListBox` (тот же `Border`, просто теперь обёрнут в `ListBoxItem` через `ItemContainerStyle`,
   команда — `RelativeSource AncestorType=ListBox` вместо `ItemsControl`, работает и до, и после
   миграции благодаря наследованию `ListBox : ItemsControl`). ПКМ по-прежнему даёт **нативное** меню
   Shell — `IconButton_PreviewMouseRightButtonUp` не переименован и не тронут по логике, только
   продолжает висеть на том же `Border`. ⚠️ живая проверка — manual verify (см. §8, п. 11).
6. [x] (код, не тронуто по сути) Drop файла на «Корзину» — `IconButton_DragOver`/`IconButton_Drop`
   не тронуты по логике (только комментарий "ItemsControl → ListBox" в соседней ветке). ⚠️ живая
   проверка — manual verify (см. §8, п. 11).
7. [x] **Drag-reorder иконок работает** (регресс §5.2 закрыт) — предсуществующий дефект
   `FindAncestorOrSelf<Button>` (шаблон элемента больше не содержит `Button` с версии "FIX 5")
   заменён на `FindAncestorOrSelf<ListBoxItem>` в переименованном
   `IconsListBox_PreviewMouseLeftButtonDown` — тот же паттерн, что уже используется в
   `ChildWindow.xaml.cs`. `_dragItem` теперь реально устанавливается. ⚠️ живая проверка на Windows —
   manual verify (см. §8, п. 12), закрывает недовыполненный пункт 11 ADR-007.
8. [x] Ширина окна панели не изменилась после миграции на `ListBox` — новый `ListBox` явно
   зануляет весь дефолтный chrome темы, который иначе добавил бы непредусмотренные пиксели к
   `SizeToContent="Width"` (`Background="Transparent"`, `BorderThickness="0"`, `Padding="0"`,
   `ScrollViewer.HorizontalScrollBarVisibility="Disabled"`) — тот же приём, что уже документирован
   рядом с `ListViewTemplate`/`GridViewTemplate` в `Templates.xaml` для fence-панелей. `ItemsPanel`
   остался вертикальным `StackPanel` (drag-логика в code-behind ищет именно его через
   `FindVisualChild<StackPanel>`, не по имени — пережил переименование `IconsItemsControl →
   IconsListBox` без правок). ⚠️ визуальное подтверждение отсутствия «схлопывания»/«распухания» —
   manual verify (см. §8, п. 13).
9. [x] Рамка выделения не срезана ни у первого, ни у последнего элемента — унаследовано от уже
   исправленного блока B (`PanelItemContainerStyle`'s `SelectionBorder Margin="0"`), боковая панель
   просто переиспользует тот же стиль как есть, ничего в нём не менялось этим блоком.
10. [x] Unit-тесты `SystemIconsPanelViewModelTests` (12 новых тестов): `SelectedItem_CanBeSetAndRead`,
    `SelectedItem_CanBeClearedBackToNull`, `SelectedItem_ClearedWhenItemIsHidden`,
    `SelectedItem_ClearedWhenItemIsRemovedFromModel`, `SelectedItem_PreservedAcrossReorder`,
    `SelectedItem_SetToNonNull_NotifiesCoordinator`, `SelectedItem_SetToNull_DoesNotNotifyCoordinator`,
    `SelectionClaimed_FromAnotherOwner_ClearsSelectedItem`,
    `SelectionClaimed_FromSelf_DoesNotClearSelectedItem`, `Dispose_UnsubscribesFromSelectionClaimed`,
    `SharingCoordinatorWithAnotherSidebarInstance_SelectingInOneClearsTheOther` — плюс 1 cross-VM
    тест в `ChildWindowViewModelTests`
    (`TwoPanels_SharingCoordinator_ChildWindowAndSystemIconsPanel_SelectingInOneClearsTheOther`,
    п. 4 выше). Все зелёные.

`dotnet build` — зелёный, 0 новых предупреждений (14 warnings — все предсуществующие, не из файлов
этого блока). `dotnet test` — 635 passed / 6 failed; все 6 падений идентичны предсуществующему списку,
зафиксированному блоками A/B/E (`RollUpTests` × 3, `NativeNameAttributeTests` × 2,
`SystemIconsPanelViewModelTests.OnRecycleBinChanged_WhenEventFired_UpdatesRecycleBinItemIcon` × 1) —
воспроизведены прогоном полного набора тестов до и после правок этого блока с идентичным результатом;
блок D их не чинит и не усугубляет, новых падений не внесено.

### 5.7. Артефакты блока D

- `src/DesktopOrganizer.UI/Views/SystemIconsPanelWindow.xaml` (`ItemsControl → ListBox` (D-F1),
  `ItemContainerStyle="{StaticResource PanelItemContainerStyle}"`, `SelectedItem` TwoWay-биндинг,
  переименование `IconsItemsControl → IconsListBox`)
- `src/DesktopOrganizer.UI/Views/SystemIconsPanelWindow.xaml.cs` (починка §5.2:
  `FindAncestorOrSelf<Button>` → `FindAncestorOrSelf<ListBoxItem>` в переименованном
  `IconsListBox_PreviewMouseLeftButtonDown`; сброс `SelectedItem` при клике по пустому месту;
  остальные обработчики переименованы вслед за `x:Name`, логика не менялась)
- `src/DesktopOrganizer.UI/ViewModels/SystemIconsPanelViewModel.cs` (новое свойство `SelectedItem`,
  новая зависимость `IPanelSelectionCoordinator`, подписка в конструкторе / отписка в `Dispose()`,
  `OnSelectionClaimed`, поле `_suppressCoordinatorEcho`, `RebuildVisibleItems` сохраняет/сбрасывает
  `SelectedItem`)
- `src/DesktopOrganizer.UI/Configuration/DiConfig.cs` (регистрация `SystemIconsPanelViewModel`
  дополнена `IPanelSelectionCoordinator`)
- `tests/DesktopOrganizer.Tests/Unit/ViewModels/SystemIconsPanelViewModelTests.cs` (+12 тестов,
  `BuildSut` дополнен опциональным `IPanelSelectionCoordinator`)
- `tests/DesktopOrganizer.Tests/Unit/ViewModels/ChildWindowViewModelTests.cs` (+1 кросс-VM
  интеграционный тест между `ChildWindowViewModel` и `SystemIconsPanelViewModel`)
- Дополнение к **ADR-013** (Решение 7: единое глобальное выделение на два типа desktop-окон) —
  **передано `architecture-documenter`**, не написано этим агентом (вне роли `wpf-ui-builder`);
  материал для ADR — весь этот раздел §5 плюс `IPanelSelectionCoordinator`/
  `PanelSelectionCoordinator`'s existing XML-doc (уже упоминает Block D в своих комментариях).

---

## 6. Блок E — панель на всех виртуальных рабочих столах (задача 5)

**Терминология зафиксирована:** речь о **Windows Virtual Desktops (Task View, Win+Ctrl+←/→)**,
а НЕ о физических мониторах. `IMonitorService`/`MonitorInfo` — про мониторы и к задаче
отношения не имеют.

### 6.1. 🚨 ОБЯЗАТЕЛЬНЫЙ spike E0 до написания кода

`SystemIconsPanelWindow` при старте вызывает `IDesktopIntegrationService.EmbedInDesktop`, который
делает `SetParent(hwnd, workerW)` (`DesktopIntegrationService.cs:26`). **Окно, ставшее ребёнком
`WorkerW`, перестаёт быть top-level-окном с точки зрения оболочки** и не отслеживается менеджером
виртуальных рабочих столов — оно рисуется как часть слоя рабочего стола и, скорее всего, **уже
видно на всех виртуальных столах**. «Только на одном столе» — это, вероятно, поведение **fallback-ветки**,
когда lookup `WorkerW` не удался (см. коммит `8bfd6f8`, `EmbedInDesktop` возвращает управление,
применив только оконные стили, без `SetParent`).

Это критично ещё и потому, что `IVirtualDesktopManager::MoveWindowToDesktop` /
`GetWindowDesktopId` требуют **top-level** окна и на child-HWND вернут ошибку.

**E0 — manual verify на Windows 10 и Windows 11, до реализации:**
1. Включить боковую панель, переключиться на другой виртуальный стол (Win+Ctrl+→). Панель видна?
2. Тот же тест при неудачном embed (эмулировать сложно — зафиксировать хотя бы фактическое
   значение: является ли HWND панели ребёнком `WorkerW`; `GetParent(hwnd) != 0`).
3. То же для fence-панелей (`ChildWindow`) — их поведение обязано остаться неизменным.

**Развилка:**
- **Исход A (панель уже видна везде):** реализуется только выключенное состояние настройки —
  скрывать панель, когда активный виртуальный стол не тот, на котором она создана. Детекция —
  та же, что в §6.2.
- **Исход B (панель только на «своём» столе):** реализуется §6.2 как основной путь.

Реализующий агент обязан **записать исход E0 в этот файл (§6.6)** до правок кода.

### 6.2. Варианты решения

| Вариант | Механизм | Плюсы | Минусы |
|---|---|---|---|
| **E-A «Follow-me» (рекомендуется)** | Публичный документированный COM `IVirtualDesktopManager` (CLSID `AA509086-5CA9-4C25-8F95-589D3C07B48A`, IID `A5CD92FF-29BE-454C-8D04-D82879FB3F1B`, `shobjidl_core.h`, стабилен с Win10 1607). Опрос `IsWindowOnCurrentVirtualDesktop(panelHwnd)`; если `false` — взять GUID текущего стола через `GetWindowDesktopId(GetForegroundWindow())` и вызвать `MoveWindowToDesktop(panelHwnd, id)`. | Только документированный API. Один IID на все версии Windows. Не ломается обновлениями. | Не «настоящий pin»: окно одно и переезжает. Задержка до одного тика опроса после переключения стола. |
| E-B «Настоящий pin» | Недокументированный `IVirtualDesktopPinnedApps` через `IServiceProvider` на CLSID_ImmersiveShell `C2F03A33-21F5-47FA-B4BB-156362A2F239`. | Истинное закрепление, без задержки. | **IID меняются между сборками** (Win10 1809/1903/2004, Win11 21H2/22H2/23H2/24H2). Нужна таблица build→IID и её сопровождение. Ломается будущими обновлениями. |
| E-C «Опора на слой рабочего стола» | Ничего нативного: гарантировать успешный `EmbedInDesktop`. | Ноль нового кода. | Не даёт **выключенного** состояния настройки; неприменимо, если `WorkerW` не найден. |

**Решение: E-A.** E-B **отклоняется** и фиксируется в ADR как отклонённая альтернатива (вернуться к
ней можно только если E0 покажет непригодность E-A). E-C — объяснение поведения по умолчанию.

**Деградация (инвариант проекта, стиль ADR-012):** любая ошибка COM/HRESULT — тихий `catch`,
`IsSupported = false`, настройка ведёт себя как выключенная, панель продолжает работать. Никаких
исключений наружу, никаких падений.

### 6.3. Архитектура

**Abstractions** — `src/DesktopOrganizer.Services.Abstractions/IVirtualDesktopService.cs` (новый):

```csharp
public interface IVirtualDesktopService
{
    /// <summary>false, если COM-объект менеджера виртуальных столов недоступен на этой системе.</summary>
    bool IsSupported { get; }

    /// <summary>true, если окно находится на активном виртуальном столе. При ошибке — true (не трогаем окно).</summary>
    bool IsWindowOnCurrentDesktop(IntPtr hwnd);

    /// <summary>Пытается перенести окно на активный виртуальный стол. false при любой неудаче.</summary>
    bool TryMoveWindowToCurrentDesktop(IntPtr hwnd);
}
```

**Services** — `src/DesktopOrganizer.Services/Tools/VirtualDesktopService.cs` (новый):
`[ComImport, Guid, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]`-объявление
`IVirtualDesktopManager`, активация через `Activator.CreateInstance(Type.GetTypeFromCLSID(...))`,
ленивая инициализация + кеширование, `IsSupported` вычисляется один раз.

**Инвариант «P/Invoke только за `INativeApi`»:** `GetForegroundWindow` — обычный P/Invoke, поэтому
он обязан попасть в `INativeApi`/`NativeApi` (`src/DesktopOrganizer.Services/Interfaces/INativeApi.cs`,
`Tools/NativeApi.cs`), а не быть объявлен в `VirtualDesktopService`. COM-активация — не P/Invoke;
границей абстракции для неё служит сам `IVirtualDesktopService`. Это отступление зафиксировать в ADR.

**Детекция смены стола** — `DispatcherTimer` ~1 с в `SystemIconsPanelManager`, по образцу
poll-таймера Корзины (ADR-007). Таймер запускается **только** когда настройка включена И окно
существует И `IsSupported`; останавливается в `ClosePanel`/`Dispose`.
`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` отклонён: второй глобальный хук в процессе, где уже есть
`WH_MOUSE_LL` со своими инвариантами, — неоправданный риск ради экономии одного тика в секунду.

### 6.4. Настройка и persistence

**Не добавлять** новое свойство верхнего уровня в `IAppSettings`. Расширяется существующая
JSON-секция `SystemIconsPanel`:

- `SystemIconsPanelManager.PanelDto` (`SystemIconsPanelManager.cs:202`) += `bool ShowOnAllVirtualDesktops`
  (default `false`).
- `SystemIconsPanelModel` (`Models/SystemIconsPanelModel.cs`) += `ShowOnAllVirtualDesktops` (INPC,
  через существующий `SetField`).
- `Load`/`Save` — чтение/запись поля; отсутствующий ключ ⇒ `false`, **миграция не нужна**
  (обратная совместимость по построению).
- `MainWindowViewModel` += `SystemIconsPanelOnAllVirtualDesktops` по образцу
  `SystemIconsPanelEnabled` (`MainWindowViewModel.cs:261–272`): сеттер пишет в модель, вызывает
  `Apply()` + `Save()`, поднимает `PropertyChanged`.
- `MainWindow.xaml`, Expander «System icons panel», под чекбоксом «Show system icons panel»,
  `IsEnabled="{Binding SystemIconsPanelEnabled}"`:
  - `Content` (точная строка): `Show on all virtual desktops`
  - `ToolTip` (точная строка): `Keep the panel visible when you switch Windows virtual desktops (Task View).`

### 6.5. Риски блока E

| ID | Риск | Митигация |
|---|---|---|
| E-R1 | Панель — child `WorkerW` ⇒ виртуальными столами не управляется, любой вызов бессмысленен | Spike E0 **до** кода; исход фиксируется в §6.6 |
| E-R2 | `MoveWindowToDesktop` на child-HWND возвращает ошибку | `TryMove*` возвращает `false`, тихая деградация; в E0 проверить `GetParent(hwnd)` |
| E-R3 | Переезд окна между столами сбрасывает/ломает `SetParent`-встраивание | После успешного `TryMove*` повторно вызвать `EmbedInDesktop` (уже есть прецедент в `OnMonitorsChanged`, `SystemIconsPanelManager.cs:181–196`) |
| E-R4 | Таймер продолжает тикать после закрытия панели | Явный `Stop()` в `ClosePanel`/`Dispose`; unit-тест на политику менеджера |
| E-R5 | Виртуальные столы отключены/недоступны (Server SKU, старая сборка) | `IsSupported = false`, настройка не даёт эффекта, панель работает |
| E-R6 | Нельзя проверить в CI (нужен интерактивный Windows с переключением столов) | Явный раздел «Manual verify» §8 |
| E-R7 | Поведение fence-панелей (`ChildWindow`) непреднамеренно изменится | Блок E **не трогает** `ChildWindow`/`WindowManager`; в E0 отдельным пунктом зафиксировать их текущее поведение как базовую линию |

### 6.6. Результат spike E0 (заполнено исполнителем блока E)

> **⚠️ ГРАНИЦА ЭТОГО SPIKE.** Работа велась в неинтерактивной сессии оркестрации.
> **Физическое переключение виртуального стола (Win+Ctrl+←/→ / Task View) и наблюдение панели
> глазами в этой сессии НЕ проверялись — требуется manual verify пользователем** (см. §8, пп. 14–17;
> ограничение среды зафиксировано в `CLAUDE.md`: «Нативный/WPF-код не выполняется вне Windows…
> поведение помечай как manual verify»). Всё, что ниже, — это (а) статический разбор кода и
> (б) фактические измерения Win32/COM, выполненные **не-визуально**: без показа окон на экране, без
> переключения столов, без изменения активного окна пользователя и **без отправки `0x052C`
> в `Progman`** (дерево окон Explorer'а не мутировалось).

#### E0.1. Статический разбор `EmbedInDesktop` — гипотеза плана ПОДТВЕРЖДЕНА

`DesktopIntegrationService.EmbedInDesktop` (`src/DesktopOrganizer.Services/Tools/DesktopIntegrationService.cs:14–27`):

1. `ApplyDesktopWindowStyles(hwnd)` — **безусловно** добавляет `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`.
2. `GetOrCreateWorkerW()` — шлёт `Progman` сообщение `0x052C` и ищет `WorkerW`.
3. `if (workerW == IntPtr.Zero) return;` — **fallback-ветка**: стили применены, `SetParent` НЕ вызван.
4. Иначе `nativeApi.SetParent(hwnd, workerW)`.

То есть панель — действительно child `WorkerW` в удачном сценарии (гипотеза §6.1 подтверждена на
уровне кода), и остаётся обычным top-level tool-окном в fallback-ветке (коммит `8bfd6f8`).
Это ровно та развилка A/B, которую предполагал план.

#### E0.2. Фактические измерения `IVirtualDesktopManager` (Windows 11, build 10.0.26200, x64)

Не-визуальный зонд (собственный процесс, окно 1×1 в точке −32000,−32000, `WS_EX_TOOLWINDOW |
WS_EX_NOACTIVATE`, показ через `SW_SHOWNOACTIVATE`; фокус не забирался; столы не переключались):

| Что измерялось | Результат |
|---|---|
| `CoCreateInstance(CLSID AA509086-…, IID A5CD92FF-…)` | **OK** — документированный COM-объект активируется |
| `GetWindowDesktopId(GetForegroundWindow())` | `S_OK`, реальный GUID стола ⇒ выбранный §6.2 способ узнать активный стол работает |
| `GetWindowDesktopId(Progman)` | `0x8002802B` (`TYPE_E_ELEMENTNOTFOUND`) — **окно слоя рабочего стола менеджером виртуальных столов не отслеживается** |
| Существующий `WorkerW` **без** отправки `0x052C` | `0x0` — на этой сборке Win11 WorkerW не существует, пока приложение (или иной клиент трюка `0x052C`) его не создаст |
| Наше top-level окно, ещё не показанное | `IsWindowOnCurrentVirtualDesktop` = `S_OK`/`true`; `GetWindowDesktopId` = `TYPE_E_ELEMENTNOTFOUND` |
| Наше top-level окно, показанное (off-screen) | `GetWindowDesktopId` = `S_OK` + **реальный GUID** текущего стола |
| То же окно **после `SetParent` в другое окно** | `GetWindowDesktopId` = `S_OK`, но **`GUID_NULL`** — окно теряет принадлежность к виртуальному столу |
| `MoveWindowToDesktop` во **всех** случаях (включая нетрекаемые окна) | `S_OK` — **ошибку не возвращает**, молча ничего не делает |

#### E0.3. Выводы

1. **E-R1/E-R2 уточнены.** `MoveWindowToDesktop` на нетрекаемом окне не возвращает ошибку — она
   возвращает `S_OK` и является no-op. Значит `TryMoveWindowToCurrentDesktop == true` **не** является
   доказательством фактического переезда. На безопасность это не влияет (деградация всё равно тихая),
   но это зафиксировано в XML-doc `IVirtualDesktopService.TryMoveWindowToCurrentDesktop`.
2. **Косвенное подтверждение исхода A.** `Progman` (канонический обитатель слоя рабочего стола) для
   VDM — `TYPE_E_ELEMENTNOTFOUND`, а любое окно сразу после смены родителя теряет desktop-id
   (`GUID_NULL`). Это согласуется с гипотезой §6.1: окно, встроенное в `WorkerW`, виртуальными
   столами не управляется и, вероятнее всего, **уже видно на всех столах**.
   **Строгого визуального подтверждения нет** — см. пометку в начале раздела.
3. **Развилка A/B не решается автоматически, и это не блокирует реализацию.** Реализован путь
   **E-A «Follow-me»** (как и предписано планом): при исходе B он работает по назначению; при
   исходе A `IsWindowOnCurrentDesktop` возвращает `true` (или вызовы — no-op), тик ничего не делает,
   поведение не меняется. Тихая деградация §6.2 закрывает оба исхода.
4. **Fence-панели (`ChildWindow`) — базовая линия не тронута.** Блок E не изменил ни одного файла
   `ChildWindow`/`WindowManager`; таймер и COM живут исключительно в системе `SystemIconsPanel`
   (риск E-R7 закрыт по построению; проверяется `git diff --name-only`).
5. **Не проверено в этой сессии (остаётся пользователю):** видимость панели после реального
   Win+Ctrl+→ при включённой и при выключенной настройке; Z-order после переезда; поведение на
   Windows 10 (все измерения выше сделаны только на Win11 26200). См. §8, пп. 14–17.

#### E0.4. Как воспроизвести измерения

Зонд — одноразовый консольный проект в scratchpad-каталоге сессии (в репозиторий не добавлялся):
`ComImport IVirtualDesktopManager` + `GetParent`/`SetParent`/`GetForegroundWindow`, вывод HRESULT'ов
по каждому кейсу. Повторить можно, продублировав таблицу E0.2 на Windows 10 — это и есть недостающая
половина spike'а.

### 6.7. DoD блока E

| # | Критерий | Статус |
|---|---|---|
| 1 | §6.6 заполнен результатами E0 на Win10 **и** Win11; выбранный исход (A или B) обоснован | ⚠️ **Частично.** §6.6 заполнен; измерения сделаны на Win11 26200. Win10 и визуальная проверка переключения стола — **manual verify пользователем**. Развилка A/B закрыта архитектурно (тихая деградация покрывает оба исхода), см. E0.3 п. 3 |
| 2 | Настройка `Show on all virtual desktops` есть в окне настроек, disabled при выключенной панели | ✅ `MainWindow.xaml`, `IsEnabled="{Binding SystemIconsPanelEnabled}"`, точные строки Content/ToolTip из §6.4 |
| 3 | Значение переживает перезапуск приложения (round-trip через `SystemIconsPanel` JSON) | ✅ `SaveThenLoad_RoundTripsShowOnAllVirtualDesktops` |
| 4 | Старый файл настроек без нового ключа читается без ошибок, значение = `false` | ✅ `Load_LegacyJsonWithoutVirtualDesktopKey_DefaultsToFalse` |
| 5 | При включённой настройке панель видна после переключения виртуального стола | ⏳ **Manual verify** (§8, п. 15) — не проверяется автотестами |
| 6 | При выключенной — прежнее поведение | ⏳ **Manual verify** (§8, п. 15) |
| 7 | Недоступность COM-объекта не роняет приложение и не мешает панели работать | ✅ `UnsupportedVirtualDesktops_DoesNotBreakManagerLifecycle`, `UnsupportedVirtualDesktops_NeverQueriesWindowPosition`; в проде — тихий `catch` в `VirtualDesktopService` |
| 8 | Таймер не работает, когда настройка выключена или панель закрыта | ✅ 5 тестов политики: настройка off / панель off / нет окна / `IsSupported=false` / после `Dispose` |
| 9 | Ни один вызов COM не выходит за пределы `VirtualDesktopService`; `GetForegroundWindow` добавлен в `INativeApi` | ✅ `ComImport` объявлен `private` внутри `VirtualDesktopService`; `GetForegroundWindow` — в `INativeApi`/`NativeApi` (`LibraryImport`) |
| 10 | Новый **ADR-016** с отклонённой альтернативой E-B и разделом Manual verify | ⏳ **Передано `architecture-documenter`** (§9); материал для ADR — в §6.6 и в XML-doc `VirtualDesktopService` |

Дополнительно (вне исходного списка, по факту реализации):

- `dotnet build` — зелёный, новых предупреждений нет (16 warnings — все предсуществующие).
- `dotnet test` — 542 passed / 6 failed; **все 6 падений предсуществующие** и воспроизводятся на
  чистом `HEAD` (`NativeNameAttributeTests` ×2, `RollUpTests` ×3,
  `SystemIconsPanelViewModelTests.OnRecycleBinChanged…`) — к блоку E отношения не имеют.
- E-R7 закрыт: `ChildWindow`/`WindowManager`/`Templates.xaml` блоком E не изменялись.

**Finalization (code-quality-reviewer, финальное ревью всей ветки):**

11. [x] **MAJOR M-3 — circuit breaker виртуальных столов не имел обратной связи в UI.**
    `SystemIconsPanelModel.VirtualDesktopFeatureIneffective` выставлялся в
    `SystemIconsPanelManager.PollVirtualDesktopOnce` при срабатывании circuit breaker, но никак не был
    забинжен в UI — чекбокс «Show on all virtual desktops» оставался включённым, хотя фича уже тихо
    отключилась. Фикс: новое read-only проксируемое свойство
    `MainWindowViewModel.SystemIconsVirtualDesktopFeatureIneffective`, читающее
    `SystemIconsPanelManager.Model.VirtualDesktopFeatureIneffective`; конструктор подписывается на
    `Model.PropertyChanged` (единственное место в `MainWindowViewModel`, где проксируемое свойство
    меняется НЕ через собственный сеттер ViewModel, а фоновым поллером менеджера — отсюда явная
    подписка, без неё UI никогда не узнал бы об изменении) и форвардит смену этого одного поля в свой
    `OnPropertyChanged`. В `MainWindow.xaml` рядом с чекбоксом добавлен предупреждающий `StackPanel`
    (⚠ + текст `"This feature isn't working on this system and has been disabled."`),
    `Visibility` через (новый в проекте) `BooleanToVisibilityConverter` — WPF built-in класс, объявлен
    в новом `Window.Resources` секции `MainWindow.xaml` (секции раньше не было).
12. [x] `dotnet build`/`dotnet test` после M-3 — зелёные, 642/642, без новых StyleCop-предупреждений
    (потребовалась точечная правка теста: `MainWindowViewModelTests.BuildSut()` не стабил
    `ISystemIconsPanelManager.Model`, из-за чего новая подписка в конструкторе на `Model.PropertyChanged`
    падала с `NullReferenceException` на un-stubbed Moq-свойстве — добавлен
    `_systemIconsPanelManagerMock.Setup(m => m.Model).Returns(new SystemIconsPanelModel());`).

### 6.8. Артефакты блока E

- `src/DesktopOrganizer.Services.Abstractions/IVirtualDesktopService.cs` (новый)
- `src/DesktopOrganizer.Services/Tools/VirtualDesktopService.cs` (новый)
- `src/DesktopOrganizer.Services/Interfaces/INativeApi.cs`, `Tools/NativeApi.cs` (`GetForegroundWindow`)
- `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` (регистрация)
- `src/DesktopOrganizer.UI/Services/SystemIconsPanelManager.cs` (таймер + политика + DTO)
- `src/DesktopOrganizer.UI/Models/SystemIconsPanelModel.cs`
- `src/DesktopOrganizer.UI/ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.xaml`
- `tests/.../Unit/Services/SystemIconsPanelManagerTests.cs` (политика + DTO round-trip)
- Новый **ADR-016** «Видимость desktop-окон на виртуальных рабочих столах»

**Finalization (M-3):**

- `src/DesktopOrganizer.UI/ViewModels/MainWindowViewModel.cs` (`SystemIconsVirtualDesktopFeatureIneffective`,
  подписка на `Model.PropertyChanged` в конструкторе)
- `src/DesktopOrganizer.UI/Views/MainWindow.xaml` (`Window.Resources` + предупреждающий `StackPanel`)
- `tests/DesktopOrganizer.Tests/Unit/ViewModels/MainWindowViewModelTests.cs` (`BuildSut()` стабит
  `ISystemIconsPanelManager.Model`)

---

## 7. Сводные риски

| ID | Риск | Блок | Уровень | Митигация |
|---|---|---|---|---|
| R1 | Изменение `FileListHPadding`/`ItemMargin` ломает квантизацию ширины при resize | B | Высокий | Обновить `GridMetrics`-тесты и `ChildWindowReorderGeometryTests` **до** правки XAML (TDD) |
| R2 | Правка `PanelItemContainerStyle` меняет footprint элемента и рассинхронит grid-математику | B, D | Высокий | Формула B-F1 сохраняет footprint байт в байт; тест-инвариант на `CellSize` |
| R3 | Рекурсия «координатор ↔ сеттер `SelectedFile`» | A | Высокий | Флаг подавления + обязательный unit-тест |
| R4 | Забыт один из путей уведомления `IsSortedByName` ⇒ залипшая подсветка | C | Средний | Единый `NotifySortModeChanged()`, тесты на все четыре пути |
| R5 | Миграция на `ListBox` ломает нативное ПКМ-меню / drag-reorder / `SizeToContent` в боковой панели | D | Средний | Чек-лист §5.3, manual verify |
| R6 | Drag-reorder боковой панели уже сломан (`FindAncestorOrSelf<Button>`) | D | Средний | Починка в §5.2 + пункт DoD |
| R7 | 🚨 Нативный: COM виртуальных столов недоступен/непригоден для child-окна `WorkerW` | E | **Высокий, непроверяемый вне Windows** | Spike E0 с развилкой; тихая деградация; ADR-016 |
| R8 | Конфликт правок B и C в одном `ChildWindow.xaml` / `Templates.xaml` | B, C | Средний | Строгая последовательность: B → C (волны §1) |
| R9 | Незакоммиченный рефакторинг `Wrap → Grid` откачен агентом | все | Средний | Явный запрет в шапке плана; проверять `git status` перед коммитом |
| R10 | UI-строки на русском | все | Низкий | Точные английские строки заданы в §4.2, §6.4 |

---

## 8. Manual verify (Win10 / Win11) — единый список

Не покрывается автотестами, требует интерактивной Windows-сессии:

1. **B** — выделение элемента в **первой колонке/первой строке** Grid-режима: рамка полная, слева не
   срезана (скриншот до/после).
2. **B** — левый край сетки визуально симметричен межколоночному шву.
3. **B** — иконка прижата к верху; неквадратная иконка не даёт верхнего зазора; подписи всех
   элементов на одном Y (в т.ч. при битом пути/`Icon == null`).
4. **B** — resize панели: ширина квантуется без «дрожания» и без полупустой колонки.
5. **B** — смена `IconSize` слайдером пересчитывает сетку без артефактов.
6. **A** — клик по заголовку / кнопке / resize снимает выделение; клик в другой панели снимает
   выделение в первой.
7. **A** — клик внутри активного inline-rename не отменяет переименование (регресс ADR-013).
8. **C** — порядок кнопок Sort/View/RollUp; подсветка Sort в режиме Name; глифы отрисованы шрифтом
   `Segoe MDL2 Assets` (не «тофу»-квадратики).
9. **C** — Portal-панель: Sort-кнопка подсвечена и disabled.
10. **D** — выделение в боковой панели: клик выделяет, клик по пустому месту снимает, визуал совпадает
    с fence-панелью.
11. **D** — ПКМ по иконке боковой панели даёт **нативное** меню Shell; двойной клик открывает объект;
    drop файла на «Корзину» работает.
12. **D** — **drag-reorder иконок боковой панели работает** (закрывает недовыполненный п. 11 из ADR-007).
13. **D** — ширина окна боковой панели не изменилась после миграции на `ListBox`.
14. **E0** — панель на другом виртуальном столе: видна / не видна (зафиксировать исход, оба ОС).
15. **E** — с включённой настройкой панель видна на всех виртуальных столах; с выключенной — нет.
16. **E** — переезд между столами не ломает Z-order (панель остаётся поверх обоев, под окнами).
17. **E** — приложение не падает при недоступном COM-объекте виртуальных столов.

---

## 9. Документация (для `architecture-documenter` в конце каждого блока)

- **ADR-015** «Модель геометрии сетки значков панели» — блок B. Обязательно зафиксировать:
  разделение «сетка позиций окна (`GridSize`)» vs «сетка элементов (`GridMetrics`, от `IconSize`)»
  (§3.4); правило «chrome выделения — внутри ячейки, никогда за её границей» (§3.3); правило
  «фиксированный слот иконки + top-align» (§3.6); отклонённые B-F2/B-F3.
- **ADR-016** «Видимость desktop-окон на виртуальных рабочих столах» — блок E. Решение E-A,
  отклонённые E-B/E-C, отступление от инварианта «P/Invoke за `INativeApi`» для COM-активации,
  раздел Manual verify.
- **Дополнение к ADR-013** — Решение 7: единое глобальное выделение на два типа desktop-окон
  (координатор блока A) + миграция боковой панели на `ListBox` (блок D).
- **ROADMAP** — новый раздел «Фаза 10. UI-полировка (`polishing/ui-1`)» с шестью пунктами и
  перенос списка §8 в «Сквозные задачи → Manual verify». Отдельным пунктом закрыть/переоткрыть
  ADR-007 п. 11 (drag-reorder боковой панели, §5.2).
- **Бэклог-тикет** — A3: сброс выделения по клику на голом рабочем столе (требует расширения
  `IDesktopGestureService`, осознанно вне объёма этого батча).

---

## 10. Общие критерии готовности всего батча

С реализацией блока D (последнего из шести) весь батч закрыт на уровне кода/тестов. Статус по
пунктам ниже — по факту завершения блока D:

1. [x] Все шесть задач пользователя закрыты, каждая — со своим DoD (§2.5, §3.8, §4.5, §5.6, §6.7).
   Блоки A, B, C, E — реализованы и лежат в рабочем дереве до начала блока D (не откатывались, не
   изменялись этим агентом за пределами уже существующих публичных контрактов
   `IPanelSelectionCoordinator`/`PanelItemContainerStyle`). Блок D (§5.6) закрыт этим агентом.
2. [x] §3.7 (research) и §6.6 (spike E0) заполнены — не «TODO» (заполнены при реализации блоков B/E,
   без изменений в этой сессии).
3. [x] `dotnet build` и `dotnet test` зелёные (для полного репозитория, после блока D): build — 0
   ошибок, 14 предупреждений (все предсуществующие, ни одно не из файлов блока D); test — 635 passed
   / 6 failed, все 6 падений — тот же предсуществующий список, что зафиксирован блоками A/B/E
   (`RollUpTests` × 3, `NativeNameAttributeTests` × 2, `SystemIconsPanelViewModelTests.OnRecycleBinChanged…`
   × 1). `linters/linter.ruleset`/StyleCop не подали новых диагностик при сборке.
4. [x] Новые публичные члены блока D (`SystemIconsPanelViewModel.SelectedItem`,
   конструкторный параметр `IPanelSelectionCoordinator selectionCoordinator`) документированы
   XML-doc, со ссылками на ui-polishing-1 §5.4/§2.4 Block A2. `GridMetrics`/`PanelItemContainerStyle`
   не трогались этим блоком (их doc-комментарии актуальны с блока B).
5. [x] Мёртвого кода не осталось: `SortModeGlyphConverter`-ветка глифа удалена блоком C (без
   изменений); `FindAncestorOrSelf<Button>` (последний известный элемент дефекта из §10.5) заменён на
   `FindAncestorOrSelf<ListBoxItem>` этим блоком (§5.2) — `grep FindAncestorOrSelf<Button>` по `src/`
   не находит совпадений.
6. [x] Незакоммиченный рефакторинг `Wrap → Grid` сохранён — блок D не касался
   `GridMetrics.cs`/`IconBoxWidthConverter.cs`/`PanelViewMode.cs`/`ChildWindowViewModel.cs`/
   `ChildWindow.xaml(.cs)`/тестовых файлов геометрии.
7. [x] Все UI-строки — на английском: блок D не добавил новых пользовательских строк (никаких
   тултипов/лейблов настроек — выделение в боковой панели работает через уже существующий визуальный
   язык `PanelItemContainerStyle`, без новых видимых пользователю подписей).
8. [x] Сводка manual-verify (§8) — единый список для всех блоков (пп. 1–17), включая пп. 10–13 для
   блока D; передаётся пользователю в итоговом отчёте этой сессии (см. финальное сообщение агента).
   Ни один manual-verify пункт не отмечен выполненным — все требуют интерактивной Windows-сессии.
