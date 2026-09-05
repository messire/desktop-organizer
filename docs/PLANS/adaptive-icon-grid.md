# План: адаптивное равномерное распределение иконок внутри панели (Fences parity)

Источник: пользовательский технический спек от 2026-09-02 (математика обеих осей задана пользователем
дословно и **не пересматривается** — этот план решает только интеграцию в существующий код).
Ветка: `polishing/ui-1`, база — коммит `c4a2dce`.

**Scope: M** (верхняя граница M) — один проект `DesktopOrganizer.UI`, один слой (UI):
1 новый чистый статик-helper (`Models/AdaptiveGridLayout.cs`), 1 новая кастомная `Panel`
(`Controls/AdaptiveIconGridPanel.cs`), правки в `Models/GridMetrics.cs`, `Resources/Templates.xaml`,
`Views/ChildWindow.xaml.cs`. Нативного кода нет, persistence-схема не меняется, миграции данных нет,
новых пользовательских строк нет (значит, вопрос языка UI-строк из `ui-polishing-1.md` не возникает).

---

## 0. Что НЕ откатывать (наследие предыдущих батчей этой ветки)

Явные решения прошлых батчей, которые эта задача **не отменяет** и которые реализующий агент обязан
сохранить байт-в-байт:

| Решение | Где | Источник |
|---|---|---|
| Иконка прижата к **верхней** границе слота (`Grid Height={IconSize}` + `Image` без `Height`, `VerticalAlignment="Top"`, `MaxHeight`) | `Templates.xaml`, `GridViewTemplate` | ADR-015 Решение 3 |
| `SelectionBorder Margin="0"` + `BorderThickness="1"`, компенсация в контенте (`StackPanel Margin="5"`) — суммарно 6px per-side chrome | `Templates.xaml`, `PanelItemContainerStyle` + `GridViewTemplate` | ADR-015 Решение 2 (B-F1) |
| Инвариант «chrome выделения целиком внутри arrange-бокса элемента» | общий принцип проекта | ADR-015, раздел «Правило на будущее» |
| Фиксированные `Height="32"` у `TextBlock`/`TextBox` подписи (подписи на одном Y, rename не «прыгает») | `Templates.xaml`, `GridViewTemplate` | ADR-015 Решение 3 |
| `FileListHost.Margin="0,0,0,4"` / `GridMetrics.FileListHPadding = 0` | `ChildWindow.xaml` | ADR-015 Решение 2 |
| Anti-overlap панелей при драге (`PanelCollision`, `_dragObstacles`, `CollectOtherPanelBounds`) | `Models/PanelCollision.cs`, `WindowManager`, `ChildWindow.xaml.cs` | ADR-017 |
| Кросс-панельное выделение (`IPanelSelectionCoordinator`), коммит rename при click-away | `Services/PanelSelectionCoordinator.cs`, `ChildWindowViewModel` | ui-polishing-1 §2 |

Пересечение с anti-overlap — **только по файлу** `ChildWindow.xaml.cs` и **разным методам**:
эта задача трогает `Resize_DragStarted`/`Resize_DragDelta`/`SnapWidthToGrid`, anti-overlap живёт в
`TitleBar_MouseMove`/`SnapToGrid`/`EndDrag`. `_dragObstacles.Clear()` в начале `Resize_DragStarted`
(финализация ADR-017 MINOR m-1) **не трогать**.

---

## 1. Текущее состояние (проверено по коду — перепроверять не нужно)

| Что | Где | Факт |
|---|---|---|
| Раскладка Grid-режима | `Templates.xaml:355–359` | `<ListBox.ItemsPanel><ItemsPanelTemplate><WrapPanel/>` — штатный `WrapPanel`, per-row justify не умеет |
| Ширина элемента | `Templates.xaml:370–371` | `StackPanel Margin="5" Width={IconSize→IconBoxWidthConverter}` = `GridMetrics.BoxSize` = `IconSize*1.5` |
| Контейнер | `Templates.xaml:217–246` | `PanelItemContainerStyle` → `Border` (`BorderThickness=1`, `Margin=0`) + `ContentPresenter` |
| **Фактический footprint элемента** | вывод из двух строк выше | `Width  = 5 + 1 + BoxSize + 1 + 5 = BoxSize + 12 = GridMetrics.CellSize(IconSize)` (84 при 48)<br>`Height = 1 + 5 + IconSize + 32 + 5 + 1 = IconSize + 44` (92 при 48) — **не квадрат** |
| Скролл | `Templates.xaml:348–349` | на самом `ListBox`: `ScrollViewer.HorizontalScrollBarVisibility="Disabled"`, `VerticalScrollBarVisibility="Auto"`; `Padding="0"`, `BorderThickness="0"`. Скролл физический (пиксельный): ни `WrapPanel`, ни будущая кастомная панель не реализуют `IScrollInfo` |
| Квантизация ширины окна | `ChildWindow.xaml.cs:1210–1218` `SnapWidthToGrid()` | `Quantize(Width − FileListHPadding, CellSize(IconSize)) + FileListHPadding`; вызывается из `Initialize()` (`:139`) и `OnModelPropertyChanged` при смене `IconSize`/`ViewMode` (`:1220–1224`) |
| Квантизация при ресайзе | `ChildWindow.xaml.cs:633–658`, `661–713` | `Resize_DragStarted`: Grid → `_stepX = _stepY = CellSize(IconSize)`; List → `1/1`. `Resize_DragDelta`: `contentW = raw − FileListHPadding`, `contentH = raw − TitleBarHeight`, оба через `Quantize` |
| **Авто-подгонки высоты под контент НЕТ** | — | `contentH`/`_stepY` — это только квантизация **интерактивного ресайза**. Отдельной фичи «ужать панель по контенту» в коде не существует (`SizeToContent` есть только у `SystemIconsPanelWindow`) |
| Хит-тест drag&drop | `ChildWindow.xaml.cs:930–1017` | `VisualTreeHelper.HitTest(listBox, …)` → `FindAncestorOrSelf<ListBoxItem>` → `ItemContainerGenerator.IndexFromContainer` → `ComputeInsertionGap(idx, posInContainer, container.ActualWidth/Height, viewMode, count)`. Комментарий `:935–937` явно фиксирует независимость от типа `ItemsPanel` |
| Выделение | `Templates.xaml:314`, `:350` | `SelectionMode="Single"` в обоих шаблонах — **множественного выделения в проекте нет**; «множественный выбор» из спека = единичное выделение + кросс-панельный координатор |
| Сортировка | `Models/PanelItemSortComparer.cs`, `ChildWindowViewModel` | порядок задаёт VM (коллекция), панель порядок **не меняет** |
| Тесты геометрии | `tests/.../Unit/Models/GridMetricsTests.cs`, `.../Unit/Views/ChildWindowReorderGeometryTests.cs` | STA/WPF-инфраструктуры в тестах **нет** (`Grep` по `STAThread`/`WpfFact` — пусто) ⇒ **вся новая математика обязана быть чистой, без `Panel`/`Visual`** |

---

## 2. Отображение символов спека на код (D-0) — фундамент всех остальных решений

Спек оперирует `x`, `pmin`, `m`, `x_v`, `pmin_v`, `m_v`. Их нужно однозначно связать с существующими
величинами. Пользователь сам задал якорь: «`x_v` — высота ячейки (по текущей модели совпадает с
`CellSize(iconSize)`)», т.е. **`x` — это полный footprint ячейки вместе с её собственным chrome**, а
`pmin` — зазор **между ячейками** сверх этого chrome.

**Решение D-0:**

| Символ | Значение в коде | Величина при `IconSize = 48` |
|---|---|---|
| `x` (ширина ячейки) | измеренный `child.DesiredSize.Width` контейнера `ListBoxItem`; аналитический двойник — `GridMetrics.CellSize(IconSize) = BoxSize + 2*ItemMargin` | 84 |
| `x_v` (высота ячейки) | измеренный `child.DesiredSize.Height`; аналитический двойник — **новый** `GridMetrics.RowHeight(IconSize) = IconSize + LabelHeight + 2*ItemMargin` | 92 |
| `pmin` / `pmin_v` | DP `AdaptiveIconGridPanel.MinGap`, **default `0`** | 0 |
| `m` / `m_v` | `x * EdgePaddingRatio` / `x_v * EdgePaddingRatio`, DP `EdgePaddingRatio` **default `0.5`** (= `x/2` по спеку) | 42 / 46 |

Два принципиальных следствия, которые надо понимать явно:

1. **`x_v ≠ x`.** Пользовательская оговорка «ячейки квадратные» — приближение: реальная ячейка
   `84 × 92`. Берём **измеренную** высоту, а не `CellSize`, иначе подписи обрежутся/наложатся.
   Формулы спека от этого не меняются (в них `x` и `x_v` и так независимые символы).
2. **`pmin = 0` — это НЕ «иконки впритык».** Ячейка уже несёт по 6px собственного chrome с каждой
   стороны (ADR-015: 5px margin шаблона + 1px рамка выделения), поэтому при `MinGap = 0` видимый шов
   между двумя соседними иконками равен ровно сегодняшним **12px** — плотность неполного ряда
   сохраняется байт-в-байт. Это обязательно зафиксировать doc-комментарием на DP, иначе «ноль,
   который выглядит как 12» станет ловушкой для следующего агента.

**Почему не альтернативная привязка `x = BoxSize`, `pmin = 12`** (т.е. «панель владеет всеми
отступами, у шаблона `Margin="0"`»): пришлось бы снять `Margin="5"` с `StackPanel` в
`GridViewTemplate`, после чего рамка выделения вплотную облепит иконку с подписью (визуальный
регресс подсветки), и заново переписать расщепление 5+1 из ADR-015 Решения 2. Выбранная привязка не
трогает содержимое шаблона вообще — панель только позиционирует уже существующие ячейки.

**Приятное следствие для совместимости.** При `MinGap = 0` и `EdgePaddingRatio = 0.5`:
`W(k) = 2m + k*x = x*(k+1)` — то есть допустимые ширины остаются **кратными `x = CellSize`**, ровно
как сегодня. Значит `SnapWidthToGrid()` на загрузке (`Initialize`) для уже сохранённых панелей —
**no-op**: геометрия сохранённых панелей не поедет, изменится только число колонок в ней
(панель шириной 336 = 4×84 покажет 3 колонки вместо 4, т.к. `2m = x` съедает одну колонку на поля).
Это ожидаемое и неизбежное следствие правила `m = x/2` из спека — см. риск R2.

---

## 3. Решение по вопросу 1: судьба `GridMetrics.CellSize` / `Quantize`

Полный `Grep` по репозиторию (`CellSize|Quantize|BoxSize|ItemMargin|FileListHPadding`) даёт
**исчерпывающий** список потребителей — других мест нет:

| Член | Потребители (production) | Решение |
|---|---|---|
| `BoxSize` | `IconBoxWidthConverter:17` (биндинг ширины `StackPanel` в `GridViewTemplate`), `CellSize` | **Остаётся без изменений** |
| `ItemMargin = 6` | только `CellSize` + doc-комментарии `Templates.xaml`/`ChildWindow.xaml` | **Остаётся**; doc дополняется: это per-side chrome самой ячейки, а не зазор между ячейками (зазором теперь владеет панель через `MinGap`) |
| `FileListHPadding = 0` | `Resize_DragDelta:680`, `SnapWidthToGrid:1216–1217` | **Остаётся** (обе точки переживают правку; смысл «горизонтальный chrome хоста вне сетки» не меняется) |
| `CellSize` | `Resize_DragStarted:635`, `SnapWidthToGrid:1215` | **Остаётся, но меняет смысл**: это `x` — ширина ячейки адаптивной модели, а **не** «шаг квантизации». Формула и значение не меняются (`BoxSize + 2*ItemMargin`), инвариант-тест `CellSize_EqualsBoxSizePlusTwoItemMargins` остаётся зелёным. Doc-комментарий обязан переписаться: убрать фразу «step used for wrap-mode width quantisation» |
| `Quantize` | `Resize_DragDelta:685,689`, `SnapWidthToGrid:1217` | **УДАЛЯЕТСЯ.** «Округлить к кратному» больше не выражает целевую ширину/высоту: `W(k) = 2m + k*x + (k−1)*pmin` — не кратное константе в общем случае. Оставлять её значит держать второй, конкурирующий способ считать то же самое (ровно то, от чего предостерегает спек). Заменяется на `AdaptiveGridLayout.SnapExtent` |

**Новое в `GridMetrics`:**

```csharp
/// <summary>Фиксированная высота подписи (TextBlock/TextBox Height="32" в GridViewTemplate).</summary>
public const double LabelHeight = 32d;

/// <summary>Вертикальный chrome FileListHost вне сетки (Margin="0,0,0,4").</summary>
public const double FileListVPadding = 4d;

/// <summary>Полный вертикальный footprint ячейки (x_v): icon slot + label + два ItemMargin.</summary>
public static double RowHeight(double iconSize) => iconSize + LabelHeight + ItemMargin * 2d;
```

- `LabelHeight` и `RowHeight` — аналитический двойник измеренного `DesiredSize.Height`; doc-комментарий
  обязан ссылаться на `Templates.xaml` (`Height="32"` у обоих — `TextBlock` и `TextBox`) и наоборот,
  тем же «указывают друг на друга» приёмом, что уже применён для `ItemMargin`↔`Margin="5"`.
- `FileListVPadding = 4` **чинит существующий off-by-4**: сегодня `Resize_DragDelta` вычитает из высоты
  только `TitleBarHeight`, игнорируя `FileListHost.Margin.Bottom = 4`, из-за чего квантованная высота
  систематически на 4px не совпадает с высотой контента. С новой формулой это перестаёт быть
  безобидным (высота теперь напрямую сравнивается с `H(R)`), поэтому исправляется здесь же.

**Удаляемые тесты** (`GridMetricsTests`): `Quantize_100_Cell84_Returns84`, `Quantize_130_Cell84_Returns168`,
`Quantize_84_Cell84_Returns84`, `Quantize_Zero_Cell84_ReturnsOneCell`, `Quantize_42_Cell84_Returns84_BankersRounding`,
`Quantize_IsIdempotent_OnAlreadyQuantisedValue`. Тесты `BoxSize_*`, `CellSize_*`, `FileListHPadding_IsZero`
и инвариант `CellSize_EqualsBoxSizePlusTwoItemMargins` — **остаются**; добавляются `RowHeight_*`.

---

## 4. Решение по вопросу 2: квантуется ли ширина панели (`SnapWidthToGrid` / `Resize_DragDelta` / `_stepX`)

**Решение D-2 (однозначное, развилки реализующему агенту не остаётся): квантизация ширины СОХРАНЯЕТСЯ,
но целью снапа становится не «кратное `CellSize`», а «ширина, вмещающая ровно `k` колонок»:**

```
ContentWidth(k) = 2*m + k*x + (k−1)*pmin          // = AdaptiveGridLayout.Extent(k, x, pmin, m)
Width           = Max(MinPanelSize, ContentWidth(k) + FileListHPadding)
```

Снап — к **ближайшему** валидному `k ≥ 1` (`AdaptiveGridLayout.SnapExtent`), как и раньше
(`Math.Round`-семантика сохраняется). Гейт прежний — настройка `SnapResizeToIconGrid` (default `True`,
`Settings.settings:12`), её лейбл/тултип **не меняются**.

Обоснование (почему не «убрать квантизацию ширины вовсе»):

1. **Паритет с Fences** — ширина fence привязана к целому числу колонок значков; это уже
   зафиксировано как факт research'а в `ui-polishing-1.md` §3.7 и реализовано в проекте.
2. **Настройка `SnapResizeToIconGrid` уже видима пользователю.** Убрать квантизацию = сделать
   настройку мёртвой ⇒ её пришлось бы удалять или перепрофилировать, а это отдельная UX-правка с
   изменением пользовательских строк и persisted-настройки — вне объёма этой задачи.
3. **Конфликта с адаптивной раскладкой нет.** Квантованная ширина — это в точности вырожденный случай
   `n == k_max, s = (n−1)*pmin ⇒ p = pmin` из спека: раскладка та же самая формула, просто без
   «лишнего» остатка. Адаптивное распределение при этом **не становится мёртвым кодом** — оно работает
   (а) при `SnapResizeToIconGrid = false`, (б) на любой ширине, пришедшей не из ресайза (сохранённая
   до этой фичи геометрия, смена `IconSize` в момент, когда `SnapWidthToGrid` ещё не отработал,
   изменение DPI/масштаба), (в) в неполном последнем ряду — всегда.
4. **Трогать этот код придётся в любом случае** — старая цель снапа (`кратное CellSize`) больше не
   соответствует новой геометрии из-за слагаемого `2m`.

Техническая правка (`ChildWindow.xaml.cs`):

- Поля `_stepX`/`_stepY` (объявлены рядом с `_rawWidth`/`_rawHeight`) **переименовать** в
  `_snapCellWidth`/`_snapCellHeight` со смыслом «размер ячейки для снапа; `0` = снап выключен,
  свободный ресайз». Оставлять имена `_step*` нельзя — семантика «шаг» умерла вместе с `Quantize`.
- `Resize_DragStarted`: Grid + `SnapResizeToIconGrid` → `_snapCellWidth = GridMetrics.CellSize(IconSize)`,
  `_snapCellHeight = GridMetrics.RowHeight(IconSize)`; List **или** снап выключен → оба `0`
  (List-режим свободен по обеим осям — поведение и комментарий `:637–642` сохраняются).
- `Resize_DragDelta`:

```csharp
var contentW = Math.Max(0d, _rawWidth  - GridMetrics.FileListHPadding);
var contentH = Math.Max(0d, _rawHeight - TitleBarHeight - GridMetrics.FileListVPadding);

var newWidth = _snapCellWidth > 0d
    ? Math.Max(MinPanelSize, AdaptiveGridLayout.SnapExtent(contentW, _snapCellWidth, MinGap, EdgePad(_snapCellWidth)) + GridMetrics.FileListHPadding)
    : Math.Max(MinPanelSize, contentW + GridMetrics.FileListHPadding);

var newHeight = _snapCellHeight > 0d
    ? Math.Max(MinPanelSize, TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, _snapCellHeight, MinGap, EdgePad(_snapCellHeight)))
    : Math.Max(MinPanelSize, _rawHeight);
```

- `SnapWidthToGrid()` — та же замена `Quantize(content, cell)` → `AdaptiveGridLayout.SnapExtent(content, x, MinGap, m)`;
  условия входа (`ViewMode != Grid` / `!SnapResizeToIconGrid` / `_model is null`) и точки вызова
  (`Initialize`, `OnModelPropertyChanged`) **не меняются**.
- `MinGap`/`EdgePad` в code-behind брать из тех же констант, что и дефолты DP панели —
  `AdaptiveGridLayout.DefaultMinGap` / `AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio)`.
  **Если** в будущем DP переопределят в XAML, снап ресайза разъедется с раскладкой — это осознанный
  компромисс (code-behind не имеет доступа к панели внутри `DataTemplate`), обязателен
  doc-комментарий в обоих местах: «менять `MinGap`/`EdgePaddingRatio` в `Templates.xaml` только
  вместе с дефолтами в `AdaptiveGridLayout`».

---

## 5. Решение по вопросу 3: `_stepY` / высота

Полный список мест, где `CellSize` использовался для **высоты** (`Grep`, исчерпывающе):
`Resize_DragStarted:651` (`_stepY = cell`) и `Resize_DragDelta:689` (`Quantize(contentH, _stepY)`).
Больше нигде — отдельной «авто-подгонки под контент» в коде нет (см. §1).

**Решение D-3:**

- `_snapCellHeight = GridMetrics.RowHeight(IconSize)` (а не `CellSize`) — высота ряда, не ширина колонки.
  Это заодно чинит давнюю неточность: сегодня высота квантуется шагом 84 при реальной высоте ряда 92.
- Квантизация высоты = снап к ближайшему `H(R) = 2*m_v + R*x_v + (R−1)*pmin_v` через тот же
  `AdaptiveGridLayout.SnapExtent` (`Extent`/`SnapExtent` — **оси-агностичные**: одна формула для
  `W(k)` и `H(R)`, один источник правды).
- Полная высота окна = `TitleBarHeight + FileListVPadding + H(R)`.
- Вертикальные зазоры **никогда не растягиваются** — `H` всегда считается с `gap = MinGap`
  (в отличие от горизонтали, где `p` может расти до `s/(n−1)`). Это прямое требование спека.
- Отдельной фичи «подогнать высоту панели под фактическое число рядов» этот план **не вводит**
  (её нет и сейчас). Если она понадобится — это отдельный тикет, и он уже будет однострочным:
  `Height = TitleBarHeight + FileListVPadding + AdaptiveGridLayout.Extent(R, x_v, MinGap, m_v)`.

---

## 6. Решение по вопросу 4: механизм скролла

**Факт.** `ScrollViewer` создаёт сам `ListBox` (его дефолтный `ControlTemplate`); в `GridViewTemplate`
он настроен attached-свойствами на `ListBox`: `HorizontalScrollBarVisibility="Disabled"`,
`VerticalScrollBarVisibility="Auto"`, плюс `Padding="0"`, `BorderThickness="0"` (`Templates.xaml:344–354`).
`CanContentScroll` остаётся дефолтным; ни `WrapPanel` сегодня, ни новая панель не реализуют
`IScrollInfo`, поэтому скролл физический (пиксельный) — поведение не меняется.

**Решение D-4 — контракт панели с `ScrollViewer`:**

1. `HorizontalScrollBarVisibility="Disabled"` **обязано остаться**. Именно оно гарантирует, что
   `MeasureOverride` получает **конечную** `availableSize.Width` — иначе `l` = `+∞` и `k_max`
   не определён. Зафиксировать doc-комментарием в `Templates.xaml`.
2. `MeasureOverride` получает `availableSize.Height == +∞` (вертикальный скролл) — высоту из
   `availableSize` **не читать никогда**.
3. `DesiredSize`, возвращаемый из `MeasureOverride`:
   `Width  = конечная availableSize.Width` (панель заполняет вьюпорт; горизонтального скролла нет),
   `Height = AdaptiveGridLayout.Extent(R, x_v, MinGap, m_v)` — то есть **`H` целиком, включая оба
   `m_v`**. Это и есть `ExtentHeight` для `ScrollViewer` ⇒ полоса появляется ровно при `H > viewport`.
4. **Двойного учёта отступов не возникает**, потому что все вертикальные поля контента (`m_v` сверху и
   снизу) принадлежат **только** панели: `ListBox.Padding="0"`, `ListBox.BorderThickness="0"`,
   у панели нет собственного `Margin`/`Padding`, `FileListHost.Margin="0,0,0,4"` — внешний по
   отношению к `ScrollViewer` chrome, учтённый отдельно (`FileListVPadding`, §5). Любое из этих
   значений, изменённое в будущем, ломает соответствие «`H` ⇔ порог скролла» — doc-комментарий обязан
   это перечислить.
5. `ArrangeOverride(finalSize)`: `finalSize.Height` может быть **больше** `H` (вьюпорт выше контента)
   или равна `H` (есть скролл). Позиции рядов считать **только** от `m_v` и `x_v`, никогда не
   «растягивать» на `finalSize.Height` (прямое требование спека: вертикальные зазоры не пружинят).
   Возвращать `new Size(finalSize.Width, Math.Max(finalSize.Height, H))`.
6. `IScrollInfo` **не реализовывать** (сохраняем текущее поведение); виртуализацию **не вводить** —
   её нет и сейчас (`WrapPanel` тоже не виртуализирует), это не регресс. См. риск R6.

---

## 7. Решение по вопросу 5: хит-тестинг, drag&drop, выделение

**Решение D-5: кастомная панель — чисто geometry-layer замена `WrapPanel`.** Структура
`ListBox` → `ScrollViewer` → `ItemsPresenter` → `Panel` → `ListBoxItem`-контейнеры **не меняется**,
`ItemContainerGenerator` работает как раньше. Отсюда жёсткие правила реализации:

1. Панель наследуется от `System.Windows.Controls.Panel` и работает **только** с `InternalChildren`;
   ничего не добавляет, не удаляет и не переупорядочивает. Порядок раскладки = порядок `InternalChildren`
   ⇒ существующая сортировка (`PanelItemSortComparer`, `EffectiveSortMode`, drag-reorder) не затронута.
2. Каждый child арранжится ровно в размер ячейки: `child.Arrange(new Rect(offsetX, offsetY, x, x_v))`,
   где `x`/`x_v` = единая (uniform) ячейка. **Ширину child'а под остаток НЕ растягивать** — весь
   остаток уходит в зазоры (позиции), не в размеры. Причина не косметическая:
   `ChildWindow.ComputeInsertionGap` сравнивает курсор с `container.ActualWidth / 2` — растянутый
   контейнер сдвинул бы середину и сломал выбор «до/после» при drag-reorder.
3. Прямоугольники ячеек **не пересекаются** (гарантируется `p ≥ 0`, `MinGap ≥ 0`) ⇒
   `VisualTreeHelper.HitTest` однозначен, `FindAncestorOrSelf<ListBoxItem>` (`ChildWindow.xaml.cs:771`,
   `:783`, `:1016`) работает без изменений.
4. Позиционирование **только через `Arrange`** — никаких `RenderTransform`/`TranslateTransform`
   (иначе `e.GetPosition(container)` и `TranslatePoint` в `ShowReorderInsertionIndicator` дадут
   рассогласование с логическими координатами).
5. `ClipToBounds` не включать (клиппит `ScrollViewer`); инвариант ADR-015 «chrome выделения внутри
   arrange-бокса» сохраняется автоматически, т.к. рамка живёт внутри `ListBoxItem`, а панель арранжит
   контейнер целиком.
6. Код-бихайнд `ChildWindow.xaml.cs` в части DnD/выделения/хит-теста **не правится вообще**. Комментарий
   `:934–937` («не зависит от типа `ItemsPanel`: `StackPanel` vs `WrapPanel`») обновить — дописать
   `AdaptiveIconGridPanel` в перечисление, чтобы утверждение осталось честным.
7. Клик по пустому месту (зазоры/поля) по-прежнему не попадает в `ListBoxItem` ⇒ снимает выделение
   (`FileListHost_PreviewMouseLeftButtonDown:771–772`) — поведение сохраняется, но зона «пустого
   места» физически увеличивается (поля `m`, `m_v`). Это не регресс, а следствие §2; в manual verify.
8. `SelectionMode="Single"` не меняется (множественного выделения в проекте нет).

---

## 8. Решение по вопросу 6: схема классов

### 8.1. Чистая математика — `AdaptiveGridLayout` (новый файл)

`src/DesktopOrganizer.UI/Models/AdaptiveGridLayout.cs`, namespace `DesktopOrganizer.UI.Models`.
**Не** внутрь `GridMetrics`: у них разные предметы (`GridMetrics` = размеры одной ячейки, выведенные из
`IconSize`; `AdaptiveGridLayout` = раскладка N ячеек в заданной ширине), и `GridMetrics` разросся бы вдвое.
Это четвёртый чистый вычислительный helper проекта — рядом с `GridMetrics` (ADR-005), `GridSnap`
(ADR-011) и `PanelCollision` (ADR-017); ту же формулировку («third computational helper alongside…»)
из doc-комментария `PanelCollision.cs:10` дополнить.

Без зависимостей от WPF-типов вообще (только `double`/`int`) ⇒ юнит-тестируемо без STA.

```csharp
public static class AdaptiveGridLayout
{
    /// <summary>pmin по умолчанию. 0 = ячейки вплотную, что даёт сегодняшний видимый шов 12px,
    /// т.к. ячейка уже несёт по 6px собственного chrome с каждой стороны (ADR-015).</summary>
    public const double DefaultMinGap = 0d;

    /// <summary>m = x * ratio. 0.5 — правило спека m = x/2.</summary>
    public const double DefaultEdgePaddingRatio = 0.5d;

    /// <summary>m (или m_v) для ячейки указанного размера.</summary>
    public static double EdgePadding(double cell, double ratio);

    /// <summary>k_max = max(1, floor((available − 2m + minGap) / (cell + minGap))).</summary>
    public static int RowCapacity(double available, double cell, double minGap, double edgePadding);

    /// <summary>Число рядов R = ceil(itemCount / rowCapacity); 0 при itemCount == 0.</summary>
    public static int RowCount(int itemCount, int rowCapacity);

    /// <summary>Сколько элементов в ряду rowIndex (greedy-перенос: все ряды полные, кроме последнего).</summary>
    public static int ItemsInRow(int itemCount, int rowCapacity, int rowIndex);

    /// <summary>Фактический зазор в ряду: minGap при n &lt; rowCapacity или n &lt;= 1;
    /// иначе max(minGap, (available − 2m − cell*n) / (n − 1)).</summary>
    public static double RowGap(int itemsInRow, int rowCapacity, double available, double cell, double minGap, double edgePadding);

    /// <summary>Смещение i-го элемента вдоль оси: m + i*(cell + gap). Годится для обеих осей.</summary>
    public static double Offset(int indexInLine, double cell, double gap, double edgePadding);

    /// <summary>Полная протяжённость линии из count ячеек: 0 при count &lt;= 0,
    /// иначе 2m + count*cell + (count − 1)*gap. Это и W(k), и H(R).</summary>
    public static double Extent(int count, double cell, double gap, double edgePadding);

    /// <summary>Обратная к Extent: ближайшее целое count &gt;= 1 для заданной протяжённости.</summary>
    public static int CountForExtent(double extent, double cell, double gap, double edgePadding);

    /// <summary>Extent(CountForExtent(extent, …), …) — снап размера окна к целому числу колонок/рядов.</summary>
    public static double SnapExtent(double extent, double cell, double gap, double edgePadding);
}
```

Граничные условия, обязательные к реализации (и к тестам):
`cell <= 0` → `RowCapacity` = 1, `Extent` = 0 (без деления на ноль);
`available <= 0` → `RowCapacity` = 1; `itemCount == 0` → `RowCount` = 0, `Extent` = 0
(пустая панель не создаёт ни полей, ни скролла); `n == 1` → `gap = 0`, элемент по смещению `m`;
`s < 0` (ряд не влезает) → `gap = minGap`, обрезка отдаётся `ScrollViewer`/клипу.
Инвариант, который обязан держаться и быть протестирован: для полного ряда `p >= minGap` всегда
(следует из определения `k_max`).

### 8.2. Кастомная панель — `AdaptiveIconGridPanel` (новый файл + новая папка)

`src/DesktopOrganizer.UI/Controls/AdaptiveIconGridPanel.cs`, namespace `DesktopOrganizer.UI.Controls`.
Новая папка `Controls/` — панель не `View`, не `Behavior` и не `Model`; проект SDK-style, отдельная
регистрация в `.csproj` не нужна. В `Templates.xaml` добавить
`xmlns:controls="clr-namespace:DesktopOrganizer.UI.Controls"`.

DependencyProperty (оба — `FrameworkPropertyMetadata` с `AffectsMeasure | AffectsArrange`):

| DP | Тип | Default | Назначение |
|---|---|---|---|
| `MinGap` | `double` | `AdaptiveGridLayout.DefaultMinGap` (0) | `pmin` = `pmin_v` (спек допускает общий; второй DP для вертикали не вводим до появления причины) |
| `EdgePaddingRatio` | `double` | `AdaptiveGridLayout.DefaultEdgePaddingRatio` (0.5) | `m = x*ratio`, `m_v = x_v*ratio` |

**`IconSize`-DP не нужен**: размеры ячейки берутся из `child.DesiredSize` — при смене `IconSize`
дети переизмеряются и сами инвалидируют measure родителя. Это исключает второй источник правды
внутри панели.

Псевдокод (реализующему агенту — без LINQ и без аллокаций в `Measure`/`Arrange`, это hot path
live-resize'а):

```
MeasureOverride(availableSize):
    if InternalChildren.Count == 0: return new Size(0, 0)
    foreach child: child.Measure(new Size(∞, ∞))
    cell   = max(child.DesiredSize.Width)      // x
    rowH   = max(child.DesiredSize.Height)     // x_v
    m      = EdgePadding(cell, EdgePaddingRatio)
    mv     = EdgePadding(rowH, EdgePaddingRatio)
    l      = availableSize.Width
    k      = double.IsInfinity(l) ? Count : RowCapacity(l, cell, MinGap, m)   // fallback: один ряд
    R      = RowCount(Count, k)
    width  = double.IsInfinity(l) ? Extent(min(Count, k), cell, MinGap, m) : l
    return new Size(width, Extent(R, rowH, MinGap, mv))

ArrangeOverride(finalSize):
    // пересчёт от finalSize.Width, а не от закэшированного measure — arrange-ширина может отличаться
    … cell/rowH/m/mv как выше …
    k = RowCapacity(finalSize.Width, cell, MinGap, m)
    i = 0
    for row in 0 .. RowCount(Count, k) − 1:
        n = ItemsInRow(Count, k, row)
        p = RowGap(n, k, finalSize.Width, cell, MinGap, m)
        y = Offset(row, rowH, MinGap, mv)
        for j in 0 .. n − 1:
            InternalChildren[i++].Arrange(new Rect(Offset(j, cell, p, m), y, cell, rowH))
    return new Size(finalSize.Width, Math.Max(finalSize.Height, Extent(RowCount(Count, k), rowH, MinGap, mv)))
```

Пиксельная резкость: на панели выставить `UseLayoutRounding="True"` (в `ItemsPanelTemplate`), чтобы
дробные `p = s/(n−1)` не давали размытых иконок; собственного `Math.Round` в чистой математике **не**
делать (иначе тесты придётся писать на округлённые значения и накопится дрейф) — округлением
занимается WPF на уровне arrange.

### 8.3. XAML

```xml
<ListBox.ItemsPanel>
    <ItemsPanelTemplate>
        <controls:AdaptiveIconGridPanel UseLayoutRounding="True"/>
    </ItemsPanelTemplate>
</ListBox.ItemsPanel>
```

`MinGap`/`EdgePaddingRatio` в разметке **не задавать** — дефолты живут в `AdaptiveGridLayout`, чтобы
code-behind ресайза (§4) и панель гарантированно читали одно и то же значение.
`ListViewTemplate` (List-режим) — **не трогать вообще**.

---

## 9. Артефакты

**Новые:**
- `src/DesktopOrganizer.UI/Models/AdaptiveGridLayout.cs`
- `src/DesktopOrganizer.UI/Controls/AdaptiveIconGridPanel.cs`
- `tests/DesktopOrganizer.Tests/Unit/Models/AdaptiveGridLayoutTests.cs`
- `adr/ADR-018-adaptive-icon-grid-layout.md` (пишет `architecture-documenter`; следующий свободный номер — 017 занят)

**Изменяемые:**
- `src/DesktopOrganizer.UI/Models/GridMetrics.cs` — `+LabelHeight`, `+FileListVPadding`, `+RowHeight()`, `−Quantize()`, переписанные doc-комментарии `CellSize`/`ItemMargin`
- `src/DesktopOrganizer.UI/Resources/Templates.xaml` — `xmlns:controls`, `WrapPanel` → `AdaptiveIconGridPanel`, doc-комментарий про `HorizontalScrollBarVisibility="Disabled"` как предусловие `k_max`
- `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` — `_stepX/_stepY` → `_snapCellWidth/_snapCellHeight`, `Resize_DragStarted`, `Resize_DragDelta`, `SnapWidthToGrid`, обновление комментария `:934–937`
- `tests/DesktopOrganizer.Tests/Unit/Models/GridMetricsTests.cs` — удалить 6 `Quantize_*`, добавить `RowHeight_*`
- `docs/ROADMAP.md`, `adr/README.md` (`architecture-documenter`)

**Не изменяются (проверить, что не задеты):** `Converters/IconBoxWidthConverter.cs`,
`Models/PanelCollision.cs`, `Models/GridSnap.cs`, `Services/WindowManager.cs`,
`Views/SystemIconsPanelWindow.xaml(.cs)`, `ListViewTemplate`, `PanelItemContainerStyle`,
`GridViewTemplate`'s `DataTemplate` (содержимое ячейки), `tests/.../ChildWindowReorderGeometryTests.cs`.

---

## 10. Тесты (пишет `tdd-test-engineer`, файл `AdaptiveGridLayoutTests.cs`)

Все — чистые, без WPF/STA. Базовый набор параметров: `x = 84`, `x_v = 92`, `pmin = 0`, `m = 42`/`46`
(соответствует `IconSize = 48`), плюс серия с `pmin = 12` (проверка, что формулы не «зашиты» под ноль).

**Горизонталь:**
1. `RowCapacity`: `l` ровно `Extent(k)` → `k`; `l = Extent(k) − 1` → `k − 1`; `l = Extent(k) + cell − 1` → `k`.
2. `RowCapacity` при `l < 2m + x` → `1` (клемп, не 0 и не исключение).
3. `RowCapacity` при `cell <= 0`, `l <= 0`, отрицательных значениях → `1`, без исключений/деления на ноль.
4. Полный ряд `n == k_max, n > 1`: `p == (l − 2m − x*n)/(n − 1)`; правая граница последнего элемента
   `Offset(n−1) + x == l − m` (с допуском 1e-9).
5. Полный ряд при `s == 0` (ширина ровно `Extent(k)`) → `p == pmin`.
6. Инвариант: для любого полного ряда `p >= pmin`.
7. Неполный ряд `n < k_max` → `p == pmin`, `Offset(0) == m`, «хвост» `l − m − (Offset(n−1) + x) > 0`.
8. `n == 1` → зазора нет, `Offset(0) == m` (и для полного `k_max == 1`, и для неполного).
9. Перенос: `itemCount = 0` → `R = 0`; `= k_max` → `R = 1`; `= k_max + 1` → `R = 2`,
   `ItemsInRow(row 0) == k_max`, `ItemsInRow(row 1) == 1`; `= 2*k_max` → `R = 2`, оба ряда полные.
10. `Offset` монотонно возрастает, соседние ячейки не пересекаются (`Offset(j) + x <= Offset(j+1)`).

**Вертикаль:**
11. `Offset(0, x_v, pmin_v, m_v) == m_v`; `Offset(i) == m_v + i*(x_v + pmin_v)`.
12. `Extent(1) == 2*m_v + x_v`; `Extent(R) == 2*m_v + R*x_v + (R−1)*pmin_v` для `R = 1..5`.
13. `Extent(0) == 0` (пустая панель — ни полей, ни скролла).
14. Порог скролла: `Extent(R) == viewport` → скролл не нужен (`Extent <= viewport`);
    `viewport = Extent(R) − 1` → `Extent > viewport` (скролл нужен);
    `viewport = Extent(R) * 3` → большой пустой хвост, `Extent` **не** меняется (зазоры не пружинят).
15. Вертикальный зазор никогда не растягивается: `Extent(R, …, gap: pmin)` не зависит от viewport'а
    (в API viewport'а для вертикали просто нет — тест фиксирует это как контракт).

**Снап (§4/§5):**
16. `SnapExtent(Extent(k)) == Extent(k)` (идемпотентность) для `k = 1..6`, обе оси.
17. `SnapExtent` округляет к ближайшему: `Extent(k) + (x+pmin)*0.4` → `Extent(k)`;
    `+ (x+pmin)*0.6` → `Extent(k+1)`.
18. `SnapExtent(0)`/отрицательного → `Extent(1)` (минимум одна колонка/ряд).
19. `CountForExtent(SnapExtent(e)) == CountForExtent(e)` для набора произвольных `e`.
20. Регресс-совместимость: при `pmin = 0, ratio = 0.5` все `Extent(k)` кратны `x`
    (`Extent(k) == x*(k+1)`) — тест, защищающий вывод §2 о том, что сохранённые ширины панелей
    остаются валидными точками снапа.

**`GridMetricsTests` (обновление):** `RowHeight(48) == 92`, `RowHeight(32) == 76`,
`RowHeight_EqualsIconPlusLabelPlusTwoItemMargins` (инвариант-параметризованный, по образцу
существующего `CellSize_EqualsBoxSizePlusTwoItemMargins`), `FileListVPadding_Is4`.

**Что тестами НЕ покрывается** (нет STA/WPF-инфраструктуры, вводить её в этой задаче — отдельный
скоуп): `MeasureOverride`/`ArrangeOverride` самой панели, реальный хит-тест, скролл, drag&drop.
Всё это уходит в §12 «manual verify».

---

## 11. DoD

1. [x] `AdaptiveGridLayout` реализован ровно по формулам спека (§8.1), без WPF-зависимостей;
   все граничные случаи из §8.1 не бросают исключений и не делят на ноль.
2. [x] `GridViewTemplate` использует `AdaptiveIconGridPanel` вместо `WrapPanel`; `ListViewTemplate`
   и содержимое ячейки (`DataTemplate`, `PanelItemContainerStyle`) не изменены.
3. [x] Горизонталь: полный ряд (`n == k_max`) растягивает зазоры на весь остаток, отступы от краёв
   остаются `m`; неполный ряд (`n < k_max`) прижат влево с зазором ровно `pmin`. (Формулы покрыты
   юнит-тестами `AdaptiveGridLayoutTests`; визуальный результат — manual verify §12 пп. 2–3.)
4. [x] Вертикаль: `y(i) = m_v + i*(x_v + pmin_v)`, `H = 2*m_v + R*x_v + (R−1)*pmin_v`, зазоры между
   рядами не растягиваются ни при каком viewport'е.
5. [x] `DesiredSize.Height` панели равен `H` ⇒ вертикальный скролл включается ровно при `H > viewport`;
   `ListBox.Padding="0"`, `BorderThickness="0"`, `HorizontalScrollBarVisibility="Disabled"` сохранены
   и снабжены doc-комментариями как предусловия.
6. [x] `GridMetrics.Quantize` удалён; в репозитории не осталось ни одной ссылки (`Grep`);
   `CellSize`/`ItemMargin`/`BoxSize`/`FileListHPadding` сохранены, doc-комментарии переписаны под
   новую модель; добавлены `LabelHeight`, `FileListVPadding`, `RowHeight`.
7. [x] Ширина панели в Grid-режиме при `SnapResizeToIconGrid = true` снапится к `Extent(k)`
   (`SnapWidthToGrid` + `Resize_DragDelta`); при `false` — свободна. Настройка и её строки не менялись.
8. [x] Высота панели в Grid-режиме снапится к `Extent(R)` от `RowHeight`, с учётом
   `TitleBarHeight + FileListVPadding`; List-режим остался свободным по обеим осям.
9. [x] `_stepX`/`_stepY` переименованы в `_snapCellWidth`/`_snapCellHeight` с семантикой «0 = без снапа»;
   мёртвой «шаговой» семантики в коде не осталось.
10. [x] Drag&drop reorder работает без изменений в `ChildWindow.xaml.cs`: контейнеры арранжатся в
    свой `DesiredSize`, не пересекаются, позиционируются только через `Arrange`;
    `ComputeInsertionGap`-тесты зелёные без правок; комментарий `:934–937` дополнен новой панелью.
11. [x] Выделение (в т.ч. кросс-панельное), inline-rename, сортировка (`PanelItemSortComparer`,
    ручной порядок), контекстное меню — без регрессов по построению (код этих подсистем не менялся,
    порядок элементов = порядок `InternalChildren`); фактическое визуальное/интерактивное поведение —
    manual verify §12 п.9–10.
12. [x] Live-resize пересчитывает раскладку: DP помечены `AffectsMeasure|AffectsArrange`, панель не
    кэширует measure-значения между `Measure` и `Arrange` (arrange считает от `finalSize.Width`).
13. [x] Смена `IconSize` пересчитывает ячейку без дополнительных биндингов в панель
    (размеры берутся из `child.DesiredSize`).
14. [x] Полная матрица §10 закрыта в `AdaptiveGridLayoutTests` (`tdd-test-engineer`): к базовому набору
    из 27 тестов/теорий добавлено 20 новых test-методов (с учётом `[Theory]`-раскрытия — 87 test cases
    в файле, 754/754 в сборке), закрывающих все явно перечисленные в §10 случаи, включая: серию
    `pmin = 12` для `RowCapacity` (граничные `l`), `RowGap`-инвариант `p >= pmin` на разных `slack` и
    `SnapExtent` (округление + идемпотентность); правую границу последнего элемента полного ряда
    (`Offset(n−1) + x == l − m`); «хвост» неполного ряда (`> 0`) вместе с `p == pmin`/`Offset(0) == m`;
    `n == 1` в ряду с `k_max > 1`; общую формулу `Offset` по вертикали (`m_v + i*(x_v+pmin_v)`, включая
    `pmin_v = 12`); `Extent` по вертикали для `R = 1..5`; порог появления скролла (`viewport == H`,
    `H − 1`, `H*3` — без растяжения зазоров); идемпотентность/округление/`CountForExtent`-round-trip
    `SnapExtent` по вертикальной оси. `GridMetricsTests` (`RowHeight_*`, `FileListVPadding_Is4`) была
    уже полностью обновлена реализующим агентом — дополнений не потребовалось. Сознательно не
    покрывается чистой математикой (требует STA/WPF): `MeasureOverride`/`ArrangeOverride` самой
    панели, реальный хит-тест, скролл, drag&drop — см. §12 «manual verify», без изменений от
    предыдущего батча.
15. [x] `dotnet build` — 0 ошибок, без новых предупреждений (только унаследованные CS8625/CS0067,
    не связанные с этой веткой изменений, ни одного в новом тестовом коде). `dotnet test` — 754/754
    зелёных (было 712 до этого прохода `tdd-test-engineer`; рост — новые тесты §10, падений нет).
16. [x] Каждый пункт §12 явно помечен как **manual verify (Win10/Win11)** в финальном отчёте
    реализующего агента (см. отчёт `wpf-ui-builder`).

---

## 12. Manual verify (Win10 / Win11) — не проверяется в этой среде

1. **Главный пункт: воздушность полей.** `m = x/2` и `m_v = x_v/2` дают при `IconSize = 48` отступы
   42px по бокам и 46px сверху/снизу (сегодня — 6px и ~0). Оценить визуально. Если чрезмерно —
   менять **только** `EdgePaddingRatio` (одно значение, `AdaptiveGridLayout.DefaultEdgePaddingRatio`),
   математику не трогать.
2. Полный ряд: зазоры равномерны, последняя иконка ряда заканчивается ровно в `m` от правого края.
3. Неполный (последний) ряд: иконки прижаты влево, зазор такой же, как в «тесном» ряду, пустой хвост
   справа — без «расползания» по всей ширине.
4. Живой ресайз ширины: раскладка пересчитывается плавно, без дрожания и мигания скроллбара;
   при `SnapResizeToIconGrid = true` ширина щёлкает по колонкам, при `false` — плавно тянется.
5. Живой ресайз высоты: вертикальный скролл появляется ровно тогда, когда контент перестал влезать,
   и исчезает ровно тогда, когда влез (без «полосы при пустом месте снизу» и без обрезанного
   последнего ряда).
6. Прокрутка колесом: последний ряд доскролливается полностью, включая нижнее поле `m_v`.
7. Drag&drop reorder внутри панели: индикатор вставки встаёт между правильными ячейками, включая
   растянутые зазоры полного ряда и пустой хвост неполного; drop в пустую зону = конец списка.
8. Drag&drop файлов с рабочего стола/Explorer в панель — по-прежнему работает (не перехвачен).
9. Выделение: клик по иконке выделяет; клик в зазор/поле снимает выделение (зоны стали крупнее);
   клик в другой панели снимает выделение в первой; рамка выделения не обрезана ни у первой колонки,
   ни у первого ряда (регресс-проверка ADR-015 Решения 2 в новой геометрии).
10. Inline-rename: вход/выход из режима не сдвигает ячейку и не меняет раскладку ряда.
11. `IconSize` на обоих краях диапазона (**16px и 96px**, не только 48) — раскладка, поля, снап
    ширины и высоты без артефактов (продолжение manual-verify п.5 ADR-015).
12. Сохранённые до обновления панели: при первом запуске геометрия окна не изменилась
    (вывод §2 — `Extent(k)` кратен `x`), изменилось только число колонок.
13. Панель с 0 элементов и с 1 элементом: нет скроллбара, нет «схлопывания» и артефактов.
14. Битый путь (`Icon == null`) и несуществующий файл (`Exists == false`, `Opacity 0.45`) не ломают
    выравнивание ряда.
15. List-режим (`ListViewTemplate`) — поведение не изменилось вообще.
16. Anti-overlap (ADR-017) при перетаскивании панелей — без регресса.
17. DPI 125% / 150%: иконки резкие (`UseLayoutRounding`), зазоры визуально равномерны, индекс вставки
    при drag&drop корректен.

---

## 13. Риски

| # | Риск | Митигация |
|---|---|---|
| **R1** | **Правило `m = x/2` — самое рискованное место фичи**: поля резко больше сегодняшних, панель выглядит «пустой» по краям | Дефолт ровно по спеку (`ratio = 0.5`), но вынесен в DP + константу — правка в одну строку после визуальной проверки (§12 п.1). Математика от значения не зависит |
| **R2** | Панель той же ширины показывает **на одну колонку меньше** (`2m = x` съедает колонку) | Неизбежное следствие спека, не баг. Геометрия сохранённых окон при этом **не меняется** (§2: `Extent(k) = x*(k+1)` — старые кратные `84` остаются валидными точками снапа), поэтому «прыжка» окон при обновлении не будет |
| **R3** | Регресс drag&drop/хит-теста из-за подмены `ItemsPanel` | Жёсткие правила §7 (arrange в `DesiredSize`, без трансформов, без пересечений) + существующие `ChildWindowReorderGeometryTests` остаются зелёными без правок + manual verify §12 пп. 7–9 |
| **R4** | Двойной учёт отступов: `ScrollViewer`/`ListBox` добавит свои поля поверх `m_v` ⇒ скролл включится не по формуле | §6 п.4 перечисляет все четыре места (`ListBox.Padding`, `BorderThickness`, `Margin` панели, `FileListHost.Margin`) и требует doc-комментария; `FileListVPadding` вводится именно чтобы нижние 4px учитывались явно |
| **R5** | Два источника правды: дефолты `MinGap`/`EdgePaddingRatio` в code-behind ресайза vs DP в XAML | Дефолты живут **только** в `AdaptiveGridLayout`; в `Templates.xaml` DP не задаются; взаимные doc-комментарии в обоих местах (§4) |
| **R6** | Производительность: панель не виртуализирует, `Measure`/`Arrange` — hot path live-resize'а | Паритет с сегодняшним `WrapPanel` (тоже не виртуализирует) — не регресс. Требование: без LINQ и аллокаций в `MeasureOverride`/`ArrangeOverride`, `RowCapacity` считать один раз на проход (контекст Фазы 7 / ADR-012) |
| **R7** | `availableSize.Width == +∞` (если кто-то включит горизонтальный скролл или встроит панель в `StackPanel`) | Явная fallback-ветка «один ряд» в `MeasureOverride` (§8.2) + doc-комментарий в `Templates.xaml`, что `HorizontalScrollBarVisibility="Disabled"` — предусловие |
| **R8** | При ширине меньше `2m + x` не влезает даже одна колонка | `RowCapacity` клемпится к 1, элемент клиппится `ScrollViewer`'ом; при включённом снапе минимум = `Extent(1) = 2x` (168 при `IconSize=48`) > `MinPanelSize = 120`, т.е. проблема достижима только при выключенном снапе. Зафиксировать в ADR как известное поведение |
| **R9** | Дробные `p = s/(n−1)` → размытые иконки на 100% DPI | `UseLayoutRounding="True"` на панели; округление — на стороне WPF, не в чистой математике |
| **R10** | Удаление публичного `GridMetrics.Quantize` заденет неучтённого потребителя | `Grep` выполнен по всему репозиторию: потребители только `ChildWindow.xaml.cs` (2 места) и `GridMetricsTests` — оба правятся в этой же задаче |

---

## 14. Порядок работ и агенты

```
[1] wpf-ui-builder          — Models/AdaptiveGridLayout.cs (чистая математика §8.1)
                              Controls/AdaptiveIconGridPanel.cs (§8.2)
                              Resources/Templates.xaml (§8.3)
                              Models/GridMetrics.cs (§3)
                              Views/ChildWindow.xaml.cs (§4, §5, комментарий §7 п.6)
[2] tdd-test-engineer       — AdaptiveGridLayoutTests.cs (§10) + обновление GridMetricsTests
[3] debug-healer            — только если сборка/тесты красные
[4] code-quality-reviewer ∥ fences-ux-parity-reviewer   (одним сообщением, параллельно)
[5] architecture-documenter — ADR-018 + adr/README.md + docs/ROADMAP.md (новый раздел батча,
                              со сводным списком manual verify §12)
```

Паттерн тот же, что в `docs/PLANS/panel-anti-overlap.md` §11 и в блоках `ui-polishing-1.md`:
реализующий агент пишет production-код, отдельный проход `tdd-test-engineer` — тесты чистой
математики. Шаги [1] и [2] последовательны (тесты пишутся по фактическим сигнатурам §8.1,
которые в этом плане уже зафиксированы — при расхождении правится код, а не план).

Реализующему агенту **не решать самостоятельно** и эскалировать оркестратору, если появится желание:
изменить формулы спека (§2 D-0 уже сопоставил символы — этого достаточно), удалить настройку
`SnapResizeToIconGrid`, включить виртуализацию, реализовать `IScrollInfo`, поменять `SelectionMode`
или тронуть `ListViewTemplate` / `PanelItemContainerStyle` / содержимое ячейки `GridViewTemplate`.
