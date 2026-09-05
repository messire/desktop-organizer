# План: магнитное прилипание к сетке во время перетаскивания панели

Источник: `docs/prompts/grid-snap-drag-and-settings-order.md`, Задача 1.
Статус: план готов, передан реализующим агентам.
Scope: **M** (слой UI: view-code + persistence одного setting + ViewModel + XAML + чистый helper с тестами).

---

## 1. Проблема (корень)

`ChildWindow.TitleBar_MouseLeftButtonDown` использует штатный WPF `Window.DragMove()`.
`DragMove()` блокирует вызывающий поток и передаёт перемещение окна нативному циклу ОС
(`WM_SYSCOMMAND` / `SC_MOVE`) до отпускания кнопки. Управление в наш код возвращается **только
после mouse-up**, поэтому `SnapToGrid()` физически может сработать лишь «на drop». Чтобы снаппинг
происходил покадрово во время движения, `DragMove()` в текущем виде непригоден.

Текущая логика снаппинга позиции — `ChildWindow.SnapToGrid()` (строки ~430–437):
`Left = Round(Left/grid)*grid; Top = Round(Top/grid)*grid`. Она квантует **всегда**, без порога.
Источник истины позиции — `ChildWindowModel.Position` (INPC), обновляется в `OnLocationChanged`.

---

## 2. Архитектурное решение (ADR-стиль)

### Decision
Заменить `DragMove()` на **ручную реализацию перетаскивания** в WPF-слое: `CaptureMouse()` в
`TitleBar_MouseLeftButtonDown`, обработчики `MouseMove` / `MouseLeftButtonUp` на titlebar (или на
окне), самостоятельное выставление `Window.Left`/`Window.Top` на каждый `MouseMove` через **чистую
функцию магнитного снаппинга с порогом**.

### Alternatives Considered
- **A. `HwndSource.AddHook` + перехват `WM_MOVING`/`WM_WINDOWPOSCHANGING`.** Оставляет нативный
  цикл `DragMove()`, правит целевой прямоугольник в оконной процедуре. Плюс: «нативно-плавно».
  Минусы: новый P/Invoke (по инварианту — только через `INativeApi`), правка `RECT` в `lParam`
  небезопасна/сложнее тестируется, координаты в device-пикселях (нужен DPI-пересчёт vs WPF DIP).
  **Отклонено** как более рискованное и хуже покрываемое тестами.
- **B. Глобальный `WH_MOUSE_LL`.** Драг панелей в этот хук не заходит (он только для
  `DesktopGestureService`, десктопные жесты). Тянуть драг в глобальный хук нарушает инвариант
  «колбэк хука — минимум работы». **Отклонено.**
- **C. Ручной драг на WPF mouse-событиях (выбрано).** Никакого нового P/Invoke, вся логика в
  managed-коде, чистая математика снаппинга выносится в тестируемый helper. Минус: нужно аккуратно
  воспроизвести поведение `DragMove` (захват мыши, отпускание, потеря захвата, отмена).

### Магнитная модель (UX)
Порог, а **не** жёсткое квантование: по каждой оси отдельно
```
nearest  = Round(raw / grid) * grid
distance = |raw - nearest|
result   = distance <= threshold ? nearest : raw
```
- В пределах `threshold` от линии сетки — окно «залипает» на линию (кажется плавным, т.к. по
  мере приближения к линии оно совпадает с ней; вдали от линии свободно следует за курсором).
- Оси независимы (можно залипнуть по X, но свободно двигаться по Y) — как в Fences.
- `grid <= 1` → снаппинг выключен (raw as-is), сохраняем текущую семантику `GridSize=1`.
- На mouse-up дополнительно вызвать финальный снаппинг (существующий `SnapToGrid()` можно оставить
  как «доводчик», но он квантует без порога — лучше на up тоже пройти через новую функцию с тем же
  порогом, чтобы не было рывка в конце; решение оставить на реализатора, зафиксировать в тестах
  поведение именно helper-функции).

---

## 3. Чистый helper (то, что покрывается тестами)

Новый статический класс, рядом с `GridMetrics` (namespace `DesktopOrganizer.UI.Models`), без WPF/
P/Invoke зависимостей — по образцу `GridMetrics` (полностью unit-testable).

Предлагаемая сигнатура:
```csharp
public static class GridSnap
{
    /// <summary>Магнитная привязка одной координаты к сетке с порогом.
    /// grid <= 1 или threshold < 0 → возвращает raw без изменений.</summary>
    public static double SnapAxis(double raw, int grid, double threshold);

    /// <summary>Привязка точки (Left, Top) по обеим осям независимо.</summary>
    public static (double Left, double Top) Snap(double left, double top, int grid, double threshold);
}
```
Реализатор волен назвать/разместить иначе, но **логика снаппинга обязана жить в чистом статическом
методе** (не внутри обработчика мыши), иначе она непокрываема тестами — это ключевой DoD.

`ChildWindow.SnapToGrid()` рефакторится на вызов `GridSnap.SnapAxis` с `threshold = grid` (=прежнее
безусловное квантование эквивалентно порогу в полклетки; выбрать так, чтобы не сломать текущее
поведение на drop, либо переиспользовать общий helper напрямую).

---

## 4. Хранение/чтение порога снаппинга

Новый user-scoped setting `GridSnapDistance` (int, px), **рядом** с `GridSize`/`SnapResizeToIconGrid`.
Дефолт: **12 px** (разумный магнит; конфигурируемо).

Точки, которые нужно синхронно тронуть (persistence в проекте — через `Properties.Settings`):
1. `src/DesktopOrganizer.UI/Properties/Settings.settings` — новый `<Setting Name="GridSnapDistance"
   Type="System.Int32" Scope="User">` со значением `12`.
2. `src/DesktopOrganizer.UI/Properties/Settings.Designer.cs` — соответствующее авто-свойство
   (`DefaultSettingValueAttribute("12")`). Оба файла правятся вместе, иначе рассинхрон.
3. `IAppSettings` (`src/DesktopOrganizer.UI/Services/IAppSettings.cs`) — `int GridSnapDistance { get; set; }` + xml-doc.
4. `AppSettings` (`.../Services/AppSettings.cs`) — проброс на `Properties.Settings.Default.GridSnapDistance`.
5. `MainWindowViewModel` (`.../ViewModels/MainWindowViewModel.cs`) — свойство `GridSnapDistance` по
   образцу `GridSize` (get из `_appSettings`, set с `Save()` + `OnPropertyChanged`).
6. `MainWindow.xaml` — слайдер внутри Expander «Привязка к сетке» (строки ~185–205), рядом с
   существующим слайдером `GridSize`. Диапазон, напр., `Minimum=0 Maximum=64`, `0` = магнит выключен
   (в этом случае — только квантование на drop, движение свободное). Подпись «Дистанция прилипания».

> Примечание к Задачам 2/3 промпта: там опция «Привязка к сетке» переезжает в группу «Автозапуск и
> рабочий стол». Новый слайдер `GridSnapDistance` должен переехать **вместе** с ней (это тот же
> смысловой блок). Зафиксировать это для `wpf-ui-builder`, чтобы порядок групп и переезд опции
> согласовались за один проход.

`ChildWindow` уже получает `IAppSettings _appSettings` в конструкторе — читать
`_appSettings.GridSnapDistance` в обработчике `MouseMove` дёшево (обычный get настройки), кеш не нужен;
при желании реализатор может считать порог один раз в `MouseLeftButtonDown` (начало жеста).

---

## 5. Изменения в `ChildWindow.xaml.cs` (реализующий агент — wpf-ui-builder)

- `TitleBar_MouseLeftButtonDown`: убрать `DragMove()`. Сохранить: проверку `ButtonState`, ветку
  double-click → `ToggleRollUp()`. Начать ручной драг: запомнить стартовую позицию курсора
  (screen) и стартовые `Left/Top`, `((UIElement)sender).CaptureMouse()`, выставить флаг `_isDragging`.
- Новый `TitleBar_MouseMove`: если `_isDragging` и кнопка нажата — вычислить сырые `Left/Top` из
  дельты курсора, прогнать через `GridSnap.Snap(rawLeft, rawTop, GridSize, GridSnapDistance)`,
  выставить `Left`/`Top`. `OnLocationChanged` сам обновит `ChildWindowModel.Position` (не дублировать).
- Новый `TitleBar_MouseLeftButtonUp`: завершить драг — `ReleaseMouseCapture()`, снять флаг,
  финальный снап (через тот же helper/`SnapToGrid`), `_windowManager.SaveWindowsState()`.
- Обработать потерю захвата (`LostMouseCapture`) и `Escape`/деактивацию окна как аккуратное
  завершение жеста (не оставлять `_isDragging=true`). Это тонкие места ручного драга — отметить в DoD.
- XAML titlebar: подписать новые события `MouseMove` / `MouseLeftButtonUp` (там, где сейчас
  `MouseLeftButtonDown`). Проверить, что клики по кнопкам titlebar (rollup/rename/меню) не запускают
  драг (не глотать их события / проверять `OriginalSource`).

Курсор мыши в WPF — DIP (те же единицы, что `Left/Top`), поэтому DPI-пересчёт не требуется; для
получения позиции курсора использовать WPF API (`Mouse.GetPosition`/`PointToScreen`), **без** нового
P/Invoke — это сохраняет инвариант «P/Invoke только за `INativeApi`».

---

## 6. Definition of Done (измеримо)

1. Перетаскивание панели работает без `DragMove()`; окно следует за курсором и «залипает» к линиям
   сетки в пределах `GridSnapDistance`, независимо по осям X и Y.
2. Вне порога окно движется свободно (px-to-px) как раньше.
3. `GridSize <= 1` → снаппинг при драге выключён (свободное движение); поведение drop-снаппинга не
   регрессировало.
4. Логика снаппинга вынесена в чистый статический helper (`GridSnap` или экв.), без WPF/P/Invoke.
5. Новый setting `GridSnapDistance` (default 12) добавлен во все 6 точек (§4), читается в драге,
   редактируется слайдером в окне настроек.
6. Источник истины позиции — по-прежнему `ChildWindowModel.Position`; альтернативных хранилищ
   координат не создано.
7. Никакого нового P/Invoke; глобальный `WH_MOUSE_LL` (`DesktopGestureService`) не затронут.
8. Двойной клик по titlebar по-прежнему сворачивает панель; клики по кнопкам titlebar не инициируют
   драг.
9. `dotnet build` зелёный, StyleCop без новых нарушений; юнит-тесты helper зелёные.
10. Аккуратное завершение жеста при `LostMouseCapture`/деактивации (нет «прилипшего» драга).

---

## 7. Тесты (tdd-test-engineer)

Покрывается только **чистая математика** (сам drag-цикл мышью — manual verify):
файл `tests/DesktopOrganizer.Tests/Unit/Models/GridSnapTests.cs` (по образцу `GridMetricsTests`):
- внутри порога → снап к линии (`SnapAxis(103, 20, 5) == 100`);
- ровно на пороге (граница `<=`) → снап (`SnapAxis(105, 20, 5) == 100`);
- за порогом → raw без изменений (`SnapAxis(107, 20, 5) == 107`);
- `grid <= 1` → raw (`SnapAxis(x, 1, 12) == x`, `SnapAxis(x, 0, 12) == x`);
- `threshold = 0` → снап только при точном совпадении с линией;
- отрицательные координаты (мультимонитор слева от основного) снапаются корректно;
- `Snap(...)` квантует оси независимо (X внутри, Y снаружи порога → снапнут только X);
- поведение на полпути между линиями (документировать округление, как в `GridMetricsTests` про
  banker's rounding).
Опционально: тест ViewModel `GridSnapDistance` (get/set → `_appSettings` + `Save`), если есть паттерн
тестов на `MainWindowViewModel`/`IAppSettings` (Moq).

---

## 8. Риски и manual verify (Win10/Win11)

**Нативный/поведенческий, НЕ проверяется в этой среде (`dotnet build`/`test` only):**
- Реальное перетаскивание мышью с залипанием — **manual verify (Win10/Win11)**: плавность,
  отсутствие рывков, независимость осей, поведение у краёв экрана и на мультимониторе.
- Корректность захвата/освобождения мыши: перетаскивание за пределы экрана, alt-tab во время драга,
  отпускание вне titlebar — **manual verify**.
- Двойной клик vs одиночный драг (тайминги `ClickCount`) — **manual verify**.
- Взаимодействие с встраиванием окна в WorkerW (панель живёт в десктоп-слое) — убедиться, что ручной
  драг не конфликтует с z-order/родителем окна — **manual verify**.
- Подбор дефолта `GridSnapDistance=12` под ощущение «как в Fences» — **manual verify**, значение
  подкрутить по итогам.

---

## 9. Recommended Next Actions (для оркестратора)

1. CRITICAL: **wpf-ui-builder** — реализация §4–§5 (ручной драг, helper `GridSnap`, setting во всех
   6 точках, слайдер в XAML). Причина: фича целиком в WPF-слое, нового P/Invoke не требуется.
   - НЕ нужен **win32-interop-specialist**: выбран вариант C (managed WPF mouse events), альтернатива
     с `HwndSource.AddHook`/`WM_MOVING` отклонена (§2). Привлекать его только если реализатор
     столкнётся с непреодолимой проблемой плавности и сознательно перейдёт на вариант A — тогда
     новый P/Invoke обязан идти через `INativeApi`.
2. RECOMMENDED: **tdd-test-engineer** — тесты §7 (чистый helper; drag-цикл не тестируется).
3. Если сборка/тесты красные: **debug-healer** (лимит 2–3 итерации).
4. Параллельно: **code-quality-reviewer** ∥ **fences-ux-parity-reviewer** (одним сообщением).
5. Финал: **architecture-documenter** — зафиксировать решение (замена `DragMove` на ручной драг +
   магнитная модель + новый setting); при необходимости мини-ADR.
