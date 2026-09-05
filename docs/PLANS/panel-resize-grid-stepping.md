# План: Ресайз панели кратно сетке иконок

> Scope: **M** (один слой — UI: `ChildWindow.xaml.cs` + `ChildWindowModel` + хелпер расчёта ячейки). Не затрагивает native/persistence-формат, поэтому не XL.
> Источник: запрос пользователя (поведенческий паритет с Fences «resize snaps to icon grid»).

## 1. Контекст и решение (ADR-style)

### Текущее состояние
- `Resize_DragDelta` (ChildWindow.xaml.cs:248–271) меняет `Width`/`Height` **попиксельно**, без шага сетки.
- `SnapToGrid` (строка 295) сейчас округляет только **позицию** (`Left`/`Top`) к `_appSettings.GridSize`, и только на `DragCompleted`/после `DragMove`. Размер он не трогает.
- Размер ячейки иконки в коде **нигде не вычисляется** — `ChildWindowModel.IconSize` (default 48) и `ViewMode` (`PanelViewMode.List` по умолчанию) есть, но связи с геометрией окна нет.
- Константы: `TitleBarHeight = 32`, `MinPanelSize = 120`.

### Формула ячейки (из ТЗ)
```
cell = IconSize + (IconSize / 4) * 2   // = IconSize * 1.5  (иконка + 25% паддинг с каждой стороны)
```
Вынести в один хелпер `GridMetrics`, чтобы и расчёт, и тесты, и (в будущем) layout панели использовали единый источник правды. **Не дублировать формулу инлайн** в DragDelta.

### Decision
Степпинг применяется **в реальном времени в `Resize_DragDelta`**: новый размер вычисляется как накопленный «сырой» размер, округлённый вниз/к ближайшему к кратному `cell` (плюс фиксированный «хром»: рамки + `TitleBarHeight`). Это даёт визуальный «щелчок» по ячейкам при перетаскивании, как в Fences.

**Ключевой инвариант для плавности:** нельзя инкрементить `Width += e.HorizontalChange` и потом округлять — накопится дрейф и появится дёрганье. Нужно вести **сырой (raw) аккумулятор** размера за время текущего drag-жеста и квантовать его при каждом DragDelta:
```
_rawWidth  += e.HorizontalChange;        // инициализируется в Resize_DragStarted
Width = chrome + Quantize(_rawWidth - chrome, cell);
```
где `Quantize(content, cell) = Math.Max(cell, Math.Round(content / cell) * cell)`.

### Alternatives considered
- **A. Квантовать только на MouseUp (DragCompleted).** Проще, но ТЗ явно требует real-time → отклонено.
- **B. Инкрементальное округление прямо на `Width`.** Накопление ошибки/джиттер → отклонено в пользу raw-аккумулятора.
- **C. Полностью переехать на `WindowChrome`/`ResizeMode`.** Слишком большой рефактор (XL), окно chromeless по дизайну → вне scope.

### Связь с `SnapToGrid` (позиция)
- `GridSize` (px, глобальный) и cell иконок (px, на панель) — **разные сетки** и должны остаться независимыми: позиция панели снапится к `GridSize`, размер — к `cell`. Не смешивать.
- Добавить настройку-тумблер `SnapResizeToIconGrid` (bool) в `IAppSettings`/`AppSettings`, по аналогии с тем как `GridSize<=1` отключает позиционный снап. При выключенном тумблере `Resize_DragDelta` работает как сейчас (попиксельно).

## 2. Мастер-план (файлы, изменения, порядок)

### Шаг 1 — Хелпер расчёта ячейки (новый файл)
`src/DesktopOrganizer.UI/Models/GridMetrics.cs` (static helper или struct):
- `static double CellSize(double iconSize) => iconSize + (iconSize / 4d) * 2d;`
- `static double Quantize(double content, double cell)` — округление к ближайшему кратному, минимум 1 ячейка.
- Чистые функции без зависимостей от WPF → юнит-тестируемо вне Windows.

### Шаг 2 — Режим List vs Tile
В `ChildWindow.xaml.cs`:
- Tile/Grid (`PanelViewMode` grid-вариант): квантовать **и ширину, и высоту** по `cell`.
- List: ширина по другой логике — список не раскладывается в колонки иконок, поэтому **ширину квантовать НЕ по `cell`** (квантовать по `GridSize` или оставить свободной), **высоту** квантовать по высоте строки списка (= `cell` или отдельная `RowHeight`, уточнить у реализатора по факту вёрстки `FileListHost`).
- Решение оформить как выбор `(stepX, stepY)` в зависимости от `_model.ViewMode`, вычисляемый один раз в `Resize_DragStarted`.

> ⚠️ Точное значение PanelViewMode для grid-режима реализатору сверить в `Core.Models.PanelViewMode` (в модели сейчас `PanelViewMode.List` дефолт; убедиться в наличии второго члена, напр. `Tile`/`Grid`).

### Шаг 3 — Аккумулятор drag-жеста
В `ChildWindow.xaml.cs`:
- Поля: `private double _rawWidth, _rawHeight; private double _rawLeft, _rawTop;`
- Добавить обработчик `Resize_DragStarted(object, DragStartedEventArgs)` → инициализирует raw-поля текущими `Width/Height/Left/Top` и кэширует `(stepX, stepY)`.
- Подписать Thumb на `DragStarted` в `ChildWindow.xaml` (рядом с уже существующими `DragDelta`/`DragCompleted`).

### Шаг 4 — Переписать `Resize_DragDelta` (строки 248–271)
- Накапливать изменения в raw-поля (а не в `Width/Height/Left/Top` напрямую).
- Вычислять квантованный размер через `GridMetrics.Quantize`, учитывая «хром» (рамка + `TitleBarHeight` для высоты) — квантуется **контентная область**, не всё окно.
- Для `Left`/`Top` (тяги Left/Top): после квантования размера пересчитать `Left = _rawRight - newWidth` (правый край фиксирован), `Top = _rawBottom - newHeight` — чтобы противоположный край не «плыл».
- Соблюсти `MinPanelSize` через минимум 1 ячейка в `Quantize`.
- Если `_appSettings.SnapResizeToIconGrid == false` → старое попиксельное поведение.

### Шаг 5 — `Resize_DragCompleted` (строка 273)
- Оставить вызов `SnapToGrid()` (позиция) + `SaveWindowsState()`.
- Финально засинхронизировать `_model.Size` уже квантованным значением (проверить, что `SyncToModel`/`SaveWindowsState` берёт актуальные `Width/Height`).

### Шаг 6 — Настройка-тумблер (persistence, тонкий)
- `IAppSettings.SnapResizeToIconGrid : bool` + реализация в `AppSettings.cs` + запись в `Settings.settings`/`Settings.Designer.cs` (default `true`).
- Чекбокс в окне настроек (Settings View) рядом с `GridSize`.

### Шаг 7 — Реакция на смену IconSize/ViewMode
- При изменении `IconSize` или `ViewMode` (INPC уже есть) — **по желанию** переквантовать текущий размер панели к новой сетке. Решение: применять при следующем resize, немедленный ре-layout вне scope этого плана (отметить как follow-up).

### Порядок выполнения
1 → 3 → 4 → 2 → 5 → 6 → 7. (Хелпер и аккумулятор раньше переписывания DragDelta; настройка и UI-тумблер после того как поведение работает.)

## 3. Definition of Done

1. `GridMetrics.CellSize(48) == 72` и `Quantize` покрыты юнит-тестами (граничные: 0, отрицательные, ровно на границе ячейки, минимум 1 ячейка).
2. При перетаскивании за угол/край в Tile-режиме ширина и высота меняются **дискретными щелчками по `cell`**, видно в реальном времени (не только на отпускании). — manual verify (Win10/Win11).
3. Нет джиттера/дрейфа: медленное перетаскивание туда-обратно возвращает панель к исходному размеру без накопления ошибки. — manual verify.
4. Тяги Left/Top: противоположный край панели остаётся на месте при квантовании (правый/нижний не «уезжает»). — manual verify.
5. List-режим использует свою логику ширины (не квантуется по cell), высота квантуется по высоте строки; поведение задокументировано в коде. — manual verify.
6. Позиционный `SnapToGrid` (`GridSize`) продолжает работать независимо и не конфликтует с размерным степпингом.
7. Тумблер `SnapResizeToIconGrid` в настройках включает/выключает степпинг; при выключении — старое попиксельное поведение; значение персистится между сессиями.
8. Размер сохраняется в `ChildWindowModel.Size` квантованным и восстанавливается после рестарта. — manual verify.
9. `MinPanelSize` соблюдён (нельзя ужать меньше 1 ячейки/120px).
10. `dotnet build` + `dotnet test` зелёные; StyleCop без новых нарушений.

## 4. Риски

- **Native/непроверяемо в CI:** всё поведение перетаскивания и визуальный «щелчок» проверяемы только на Windows с реальным desktop-окном → пункты DoD 2–5, 8 помечены **manual verify (Win10/Win11)**. Раздел ручной проверки обязателен.
- **PanelViewMode grid-член** может называться иначе — реализатору сверить enum до правки.
- **List row height** не задан константой — реализатору вытащить фактическую высоту строки из вёрстки `FileListHost`, иначе высотный степпинг в List будет неточным.
- **Хром (рамка)** толщина рамки должна вычитаться из окна перед квантованием контента, иначе панель будет на 1-2px не кратна — проверить актуальную толщину border в `ChildWindow.xaml`.

## 5. Артефакты
- NEW `src/DesktopOrganizer.UI/Models/GridMetrics.cs`
- EDIT `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` (поля, `Resize_DragStarted`, `Resize_DragDelta`, `Resize_DragCompleted`)
- EDIT `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (подписка `DragStarted`)
- EDIT `src/DesktopOrganizer.UI/Services/IAppSettings.cs` + `AppSettings.cs` + `Properties/Settings.*` (`SnapResizeToIconGrid`)
- EDIT Settings View (чекбокс)
- NEW тесты `GridMetricsTests` в тестовом проекте UI/Core.
