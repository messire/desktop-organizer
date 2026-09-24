# Мастер-план Фазы 6 — Edit Layout UX

> Источник правды: `docs/Refactoring/PHASES/PHASE-6-edit-layout-ux.md`, `adr/ADR-026-edit-layout-mode.md` (Proposed),
> `docs/Refactoring/ROADMAP.md` (строка Phase 6, «Правило выполнения», «Global DoD»),
> `docs/Refactoring/BACKLOG.md` (DO-002, DO-013).
> Смежные закрытые решения, чьи инварианты Фаза 6 обязана сохранить: ADR-021 (gesture lifecycle),
> ADR-022 (`NativePanelSizing`), ADR-017 (anti-overlap), ADR-011 (magnetic grid snap), ADR-018
> (adaptive grid), ADR-025 (Ambient Accent visual states), ADR-013 (`WS_EX_NOACTIVATE`-риски),
> ADR-019/ADR-020 (z-order боковой панели, `Topmost` убран).
> Ветка-предшественник: `ui/ambient-accent-design-tokens` (HEAD `ea808d4`, Фаза 5 закрыта). Дата: 2026-09-21.
> Общий scope фазы: **XL** (UI-взаимодействие + глобальное состояние + persistence-настройки + опциональное
> новое desktop-окно). Разбит на 7 логических пунктов P6-1…P6-7.
>
> **Главная особенность фазы:** это фаза о *вводе*. В этой среде (нет живого Windows GUI) проверяемы
> только чистая логика политики/математики, метаданные DP и компиляция XAML. Всё, что касается реального
> поведения мыши/клавиатуры на `WS_EX_NOACTIVATE`-окне в слое `WorkerW`, — **manual verify (Win10/Win11)**.
> Раздел §7 явно делит работы на две корзины.

---

## 0. Границы фазы — что НЕ трогаем

Жёсткие запреты (нарушение = regression в уже закрытых фазах):

| Не трогать | Почему |
|---|---|
| `Interaction/ResizeGeometryCalculator.cs`, `ResizeGestureSession.cs`, `ResizeConstraints.cs`, `Interaction/Native/*` | Фазы 1–2, ADR-021/ADR-022, bit-for-bit golden-эквивалентность легаси и нативного путей. Фаза 6 **гейтит вход** в resize, но не меняет ни геометрию, ни lifecycle. |
| `Interaction/PanelInteractionTracker.cs`, `PanelInteractionState.cs` | ADR-021. Фаза 6 **читает** трекер и вызывает `ForceIdle()` (уже разрешённый escape hatch), но не меняет матрицу переходов и не добавляет значений в enum. `Hover`/`Editing` остаются «panel-local visual states» — глобальный режим редактирования это **не** шестое значение этого enum (см. D-1). |
| `Models/GridSnap.cs`, `Models/PanelCollision.cs`, `Models/AdaptiveGridLayout.cs`, `Models/GridMetrics.cs` | ADR-011/ADR-017/ADR-018. Новая snap-guide математика — **отдельный** пятый helper, вставляемый в существующий конвейер между ними (D-7), а не правка любого из четырёх. |
| `Behaviors/RollUpBehavior.cs` (тело `ApplyRollUp`, M-6 generation guard, H3 fix) | Фаза 1. Меняется **только** тип, который `RollUpBehavior` получает в параметре `resizeAffordance` (теперь это гейт, а не конечная affordance) — ни одной строки внутри класса. |
| `ViewModels/ChildWindowViewModel.cs` | DO-002, 973 строки. Как и в Фазе 5 — цель **ноль изменённых строк**. Всё новое идёт через singleton-сервис, attached DP и code-behind-обработчики (D-12). |
| `Theming/InteractionVisualState.cs` — значения `BorderOpacityFor`/`ChromeOpacityFor`/`AccentEmphasisFor` | ADR-025 + `InteractionVisualStateTests` (строгая монотонность — машинная форма DoD Фазы 5). Edit-mode-эмфазис — **новые** функции рядом, не правка существующих пяти веток. |
| `State/Dto/*`, `Services/Storage/Migrations/*`, `StateEnvelope.CurrentSchemaVersion` | D-8: Фаза 6 **не трогает схему JSON вообще** — новые настройки глобальные и живут в `IAppSettings`/`user.config`. Это сознательный выбор, а не упущение. |

---

## 1. Решения (детализация и уточнение ADR-026)

### D-1. Режим — глобальный для приложения, не per-panel; ровно два значения

`ADR-026` говорит «Normal mode prioritizes launching/selecting/renaming content; Edit Layout mode exposes
move/resize affordances», а scaffold фазы перечисляет четыре режима: `Normal | Edit Layout | Drag Item |
Rename Item`. **План уточняет:** `Drag Item` и `Rename Item` — это уже существующие *panel-local*
состояния `PanelInteractionState.Dragging`/`Editing` (ADR-021) и визуальные состояния `PanelVisualState`
(ADR-025); они **не** становятся значениями глобального режима. Сам scaffold это и говорит («Panel
interaction state остаётся локальным визуальным состоянием») — фиксируем явно, чтобы реализующий агент
не завёл второй, конкурирующий enum поверх трекера.

```csharp
// src/DesktopOrganizer.UI/Models/LayoutInteractionMode.cs
public enum LayoutInteractionMode
{
    Normal = 0,      // контент имеет приоритет
    EditLayout = 1,  // геометрия панелей имеет приоритет
}
```

**Режим глобальный (одно значение на приложение), а не на панель.** Обоснование: snap-guides и alignment
по определению межпанельные (D-7) — панель A в edit-режиме, выравниваемая по панели B в normal-режиме,
даёт неопределённую семантику; плюс «Edit mode включается/выключается **одним действием**» (DoD scaffold'а)
несовместимо с per-panel переключателем при 10 панелях. Альтернатива «per-panel edit» отвергнута.

Носитель состояния — новый singleton `ILayoutEditModeService` (`src/DesktopOrganizer.UI/Services/`),
прямой аналог уже существующего `IPanelSelectionCoordinator` (тот же слой, тот же способ регистрации,
тот же паттерн «глобальный факт, который панели не могут узнать через фокус, потому что несут
`WS_EX_NOACTIVATE`»):

```csharp
public interface ILayoutEditModeService : INotifyPropertyChanged
{
    LayoutInteractionMode Mode { get; }
    bool IsLayoutLocked { get; set; }          // персистится (D-8)
    bool CanEnterEditLayout { get; }           // == !IsLayoutLocked

    bool TryEnterEditLayout(LayoutModeChangeSource source);
    void ExitEditLayout(LayoutModeChangeSource source);
    bool Toggle(LayoutModeChangeSource source);

    /// Поднимается ПОСЛЕ смены Mode. Подписчики (ChildWindow) обязаны сначала завершить активные
    /// жесты, потом перерисоваться (D-4).
    event EventHandler<LayoutModeChangedEventArgs>? ModeChanged;
}

public enum LayoutModeChangeSource { Tray, PanelMenu, SettingsWindow, Keyboard, Programmatic }
```

`Mode` — чистое in-memory состояние, **не персистится** (D-8). `IsLayoutLocked` — персистится.

### D-2. Политика Normal-режима: per-gesture, а не «всё или ничего»

Scaffold допускает оба варианта («move/resize можно отключить полностью либо оставить modifier-based»).
Решение плана — **разная политика для move и для resize**, потому что источники «случайности» у них разные:

| Жест | Normal (default) | Обоснование |
|---|---|---|
| **Move** (drag заголовка) | **разрешён**, но с порогом начала жеста | Заголовок — однозначная ручка: в нём не живёт контент, промахнуться по нему нельзя. Настоящий дефект здесь другой и он задокументирован в продуктовом ROADMAP (Фаза 2, примечание): одиночный клик по заголовку стартует драг, который тут же завершается с нулевым смещением, и `SnapToGrid()`/`SaveWindowsState()` всё равно вызываются. Лечится порогом `SystemParameters.MinimumHorizontal/VerticalDragDistance` (тот же механизм, что уже применён в `ListReorderGestureController`, ADR-014), а не запретом жеста. |
| **Resize** (8 thumb'ов по 6px / нативный `WM_NCHITTEST`) | **запрещён** | Это и есть реальный источник случайных геометрических изменений: невидимая 6-пиксельная рамка лежит поверх краёв контентной области, перехватывая клики, адресованные крайним иконкам, и делает панель «дрожащей» при попытке выделить/перетащить иконку у границы. Ровно тот gesture conflict, ради которого ADR-026 написан. |

Пользовательская настройка (один ключ, три значения — чтобы старое поведение осталось достижимым):

```csharp
public enum NormalModeLayoutGestures { MoveAndResize = 0, MoveOnly = 1, None = 2 }
// IAppSettings.NormalModeLayoutGestures : string, default "MoveOnly"
```

**Это уточнение, а не противоречие ADR-026.** Буквальное чтение ADR («move/resize become first-class
layout editing operations») привело бы к тому, что панель нельзя сдвинуть, пока пользователь не нашёл
Edit Layout — регрессия обнаруживаемости, которую никакая формулировка DoD не компенсирует. Заявленная
*причина* ADR («reduces gesture conflict and accidental panel movement») достигается сильнее и дешевле
комбинацией «порог + запрет resize». Формулировка ADR-026 Decision должна быть расширена соответствующе
(P6-7).

**Машинно-проверяемое ядро политики** (единственная точка правды, из которой читают и гейты, и визуал):

```csharp
// src/DesktopOrganizer.UI/Models/LayoutGesturePolicy.cs  — чистая логика, без WPF
public readonly record struct LayoutGestureAllowance(bool CanMove, bool CanResize);

public static class LayoutGesturePolicy
{
    public static LayoutGestureAllowance Resolve(
        LayoutInteractionMode mode, bool isLayoutLocked, NormalModeLayoutGestures normalPolicy);
}
```

Таблица истинности (она же — тест-матрица, 2×2×3 = 12 кейсов):

| Locked | Mode | NormalPolicy | CanMove | CanResize |
|---|---|---|---:|---:|
| true | любой | любая | ✖ | ✖ |
| false | EditLayout | любая | ✔ | ✔ |
| false | Normal | `MoveAndResize` | ✔ | ✔ |
| false | Normal | `MoveOnly` (default) | ✔ | ✖ |
| false | Normal | `None` | ✖ | ✖ |

### D-3. Что именно блокирует `IsLayoutLocked` — и что НЕ блокирует

`Lock layout` (пункт scaffold'а «lock layout доступен») — это пользовательская настройка «геометрия
зафиксирована», аналог Fences' «Lock fences». Границы намеренно узкие:

| Действие | При `IsLayoutLocked = true` |
|---|---|
| Drag заголовка (move) | ✖ запрещён |
| Resize (обе ветки) | ✖ запрещён |
| Вход в Edit Layout | ✖ запрещён (`CanEnterEditLayout == false`; пункты меню disabled с тултипом «Layout is locked») |
| Roll-up / roll-down | ✔ **разрешён** — это видимость контента, а не раскладка (и он уже имеет свой собственный toggle, анимацию и M-6 guard) |
| Rename заголовка, выделение, запуск файла, drag-reorder иконок, drop файлов | ✔ разрешены — Lock про геометрию окон, не про контент |
| `SnapWidthToGrid()` на `Initialize` и при смене `IconSize`/`ViewMode` | ✔ **разрешён** — это внутренняя ре-нормализация под текущую сетку (ADR-018), а не пользовательский жест. Иначе заблокированная раскладка молча рассинхронизируется с сеткой и «починится» скачком в момент разблокировки (см. R-9) |

Эта таблица обязана попасть в ADR-026 — она нетривиальна и ровно про неё будет спор на ревью.

### D-4. Выход из Edit Layout гарантированно завершает активные жесты

DoD scaffold'а: «Exit edit mode гарантированно завершает active gestures». Механика:

`ILayoutEditModeService.ModeChanged` → каждый живой `ChildWindow` (подписан в конструкторе, отписан в
`OnClosing`) выполняет **строго в этом порядке**:

1. `_resizeController.AbortResize(ResizeEndReason.SourceChanged)` — уже существующий и идемпотентный путь
   общего интерфейса `IPanelResizeController`, работает для обеих веток resize (ADR-022 §1.6). Персист
   подчиняется уже принятому правилу ADR-021 (`ShouldPersist = AppliedDeltaCount > 0`) — геометрия,
   уже оказавшаяся на экране, сохраняется; откатывать нечего и WPF механизма отката не даёт.
2. `_dragMoveBehavior.AbortDrag()` — **новый публичный метод** (см. ниже).
3. `_interactionTracker.ForceIdle()` — backstop, уже разрешённый ADR-021 escape hatch.
4. Применение нового гейта resize (D-5) и перерисовка (D-6).

`PanelDragMoveBehavior` сегодня не имеет публичного способа прервать драг снаружи: `EndDrag(UIElement?)`
приватен и получает элемент из `sender`. Требуемая правка — минимальная и точечная:

```csharp
// поле, заполняется в OnTitleBarMouseLeftButtonDown сразу после успешного CaptureMouse()
private UIElement? _captureElement;

/// Внешнее прерывание драга (смена режима/блокировка раскладки). Идемпотентен: no-op, если
/// драг уже завершён. Проходит ровно тот же путь, что LostMouseCapture — release capture,
/// SnapToGrid, clear obstacles, SaveWindowsState, tracker.Exit в finally (ADR-021).
public void AbortDrag() => EndDrag(_captureElement);
```

⚠️ `_captureElement` обнуляется в `EndDrag`, иначе гейт будет дёргать уже отпущенный элемент.

### D-5. Гейт resize — один композит поверх существующего `IResizeAffordance`, без правок Фаз 1–2

Фаза 2 (ADR-022 R-9) оставила ровно **одну** точку включения/выключения resize — `IResizeAffordance`,
которую сегодня флипает исключительно `RollUpBehavior`. Теперь входов два и они независимы
(«не свёрнута» И «разрешено политикой»). Решение — не добавлять второго вызывающего, а вставить композит:

```csharp
// src/DesktopOrganizer.UI/Behaviors/ResizeAffordanceGate.cs
/// Сводит два независимых условия в один вызов SetEnabled на нижележащей affordance.
/// RollUpBehavior получает ЭТОТ объект в параметре resizeAffordance и продолжает вызывать
/// SetEnabled(!collapsed), ничего не зная о режиме — ни одной строки внутри RollUpBehavior не меняется
/// (§0). Режим пишет во второй вход через SetAllowedByPolicy.
public sealed class ResizeAffordanceGate : IResizeAffordance
{
    public ResizeAffordanceGate(IResizeAffordance inner);
    public void SetEnabled(bool enabled);            // вход «не свёрнута» (вызывает RollUpBehavior)
    public void SetAllowedByPolicy(bool allowed);    // вход «разрешено политикой» (вызывает ChildWindow)
    // inner.SetEnabled(_notCollapsed && _allowedByPolicy) — пересчёт на каждом изменении, идемпотентно
}
```

Чистый, тестируемый headless класс (зависит только от `IResizeAffordance`, не от WPF). Порядок сборки в
`ChildWindow`-конструкторе: `ThumbOverlayAffordance`/`NativeSizingAffordance` → `ResizeAffordanceGate` →
`RollUpBehavior(..., gate, ...)`; `NativePanelResizeController.isResizeEnabled` по-прежнему читает
`nativeAffordance.IsEnabled` — **плоское чтение поля, без похода в сервис и без блокировок** (инвариант
CLAUDE.md «колбэк хука — минимум работы»; `IsEnabled` читается на каждом `WM_NCHITTEST`, см. R-2).

Гейт move'а устроен проще: `PanelDragMoveBehavior` получает в конструкторе `Func<bool> canMove` и
проверяет его **первым делом** в `OnTitleBarMouseLeftButtonDown` — до `CaptureMouse()`, до
`TryEnter(Dragging)`. Важно: проверка идёт **после** `_onDragGestureStarting()` (коммит активного rename
+ сброс выделения) и **после** ветки `ClickCount == 2` — двойной клик по заголовку обязан продолжать
сворачивать панель даже при запрещённом move (D-3).

### D-6. Визуальный язык Edit Layout — переиспользование Фазы 5, без нового словаря

Edit Layout **не** становится шестым значением `PanelVisualState`: это ортогональный факт (можно быть
`Dragging` внутри edit-режима и внутри normal-режима). Доставка — четвёртое наследуемое attached-DP рядом
с тремя существующими:

```csharp
// Theming/PanelTheme.cs — аддитивно
public static readonly DependencyProperty IsLayoutEditingProperty =   // bool, default false, Inherits
    DependencyProperty.RegisterAttached("IsLayoutEditing", typeof(bool), typeof(PanelTheme),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
```

Ставится на `PanelRoot` (тот же элемент, что несёт `PanelTheme.Accent`/`SurfaceStyle`/`VisualState`),
пишется из нового `LayoutEditModeBehavior` (сосед `PanelVisualStateBehavior`, тот же паттерн
«подписка + один `SetValue` + `Dispose`»).

Что меняется в `Panels.xaml` по триггеру `IsLayoutEditing=True` (всё — уже существующие токены и части,
новых цветов не заводим, grep-гейт Фазы 5 обязан остаться зелёным):

| Часть | Idle-normal | Edit Layout |
|---|---|---|
| `PART_InteractionOverlay` | `BorderOpacity` по `InteractionVisualState.BorderOpacityFor(state)` | множитель `LayoutEditVisuals.BorderEmphasis` (пол не ниже уровня `Editing`) |
| `PART_AccentIndicator` | ширина через `AccentIndicatorWidthConverter` | тот же конвертер, дополнительный множитель `LayoutEditVisuals.AccentEmphasis` |
| `PART_HeaderActions` | `ChromeOpacityFor(Idle) = 0.45` («actions muted») | форсировано `1.0` — в режиме редактирования приглушать управляющие элементы контрпродуктивно |
| `ResizeOverlay` (8 Thumb'ов) | `ResizeThumbStyle` — полностью прозрачные | новый `ResizeHandleStyle` — видимые accent-тонированные грипы |

```csharp
// src/DesktopOrganizer.UI/Theming/LayoutEditVisuals.cs — чистая логика, unit-тестируемая
public static class LayoutEditVisuals
{
    public const double BorderEmphasis = 1.0;   // итог: max(BorderOpacityFor(state), EditFloor)
    public const double EditBorderFloor = 0.60; // == BorderOpacityFor(Editing) — намеренно та же ступень
    public const double AccentEmphasis  = 1.35;
    public static double BorderOpacityFor(PanelVisualState state, bool isLayoutEditing);
    public static double ChromeOpacityFor(PanelVisualState state, bool isLayoutEditing);
    public static double AccentEmphasisFor(PanelVisualState state, bool isLayoutEditing);
}
```

Инвариант-тест (машинная форма DoD «edit-режим визуально отличим»): для **каждого** из пяти
`PanelVisualState` значение при `isLayoutEditing:true` строго ≥ значения при `false`, и хотя бы по
одному каналу строго больше. Плюс регресс-тест: при `isLayoutEditing:false` все три функции возвращают
ровно то же, что `InteractionVisualState.*` (Фаза 5 не сдвинулась ни на единицу).

⚠️ **`ResizeHandleStyle` меняет только `ControlTemplate` Thumb'а — геометрия `ResizeOverlay` (три колонки
6px/`*`/6px и три строки) не меняется ни на пиксель** (R-5): иначе hit-testing resize поедет и Фаза 1/2
получит регрессию, невоспроизводимую в этой среде.

### D-7. Snap guides и alignment — чистая математика отдельно от рендеринга

Пятый чистый helper рядом с `GridSnap`/`PanelCollision`/`AdaptiveGridLayout`/`GridMetrics`:

```csharp
// src/DesktopOrganizer.UI/Models/SnapGuideCalculator.cs — без WPF, без P/Invoke
public enum SnapGuideKind { LeftEdge, RightEdge, TopEdge, BottomEdge, CenterX, CenterY }

public readonly record struct SnapGuide(SnapGuideKind Kind, double Coordinate, Rect Against);

public static class SnapGuideCalculator
{
    public const double DefaultThreshold = 8.0;   // DIU

    /// Возвращает скорректированную позицию + до двух активных направляющих (одна на ось).
    /// Детерминирован: при равных кандидатах выигрывает меньший индекс препятствия, затем
    /// меньшая |дельта| — никакого дрожания на границе (эпсилон 0.5 DIU, как в PanelCollision).
    public static SnapGuideResult Snap(
        Rect moving, IReadOnlyList<Rect> obstacles, Rect workArea, double threshold);
}
```

**Место вставки в конвейер драга — строго фиксировано** (`PanelDragMoveBehavior.OnTitleBarMouseMove`):

```text
raw курсор (DIU)
  → GridSnap.Snap                     (ADR-011, без изменений)
  → SnapGuideCalculator.Snap          (НОВОЕ; выполняется ТОЛЬКО в Edit Layout)
  → PanelCollision.ClampAgainstObstacles (ADR-017, без изменений, ВСЕГДА последний)
  → Window.Left/Top
```

Clamp обязан остаться последним: «твёрдая стена» ADR-017 — безусловный инвариант, направляющая не имеет
права протолкнуть панель в соседа. В Normal-режиме конвейер **побайтово прежний** (guide-шаг выключен) —
это и есть гарантия нулевой регрессии для 100% сегодняшних пользователей.

Источник препятствий — уже существующий `IWindowManager.CollectOtherPanelBounds` (снапшот раз за жест),
рабочая область монитора — `IMonitorService` (уже есть).

### D-8. Persistence: **никакой миграции схемы**, только два аддитивных ключа `IAppSettings`

| Состояние | Персистится? | Где | Обоснование |
|---|---|---|---|
| `LayoutInteractionMode.Mode` | **НЕТ** | — | Транзиентный режим. Приложение, стартующее в Edit Layout после перезагрузки машины, — сюрприз, а не фича. Отвечает на DoD scaffold'а «State persists only if product decision explicitly says so»: решение принято — не персистим, и это пинится тестом (R-11). |
| `IsLayoutLocked` | **ДА** | `IAppSettings.IsLayoutLocked` (bool, default `false`) | Пользовательская преференция того же класса, что `SnapResizeToIconGrid`. |
| `NormalModeLayoutGestures` | **ДА** | `IAppSettings.NormalModeLayoutGestures` (string, default `"MoveOnly"`) | Там же. |

**Решение: НЕ трогать `ApplicationStateDocumentDto`/`StateEnvelope.CurrentSchemaVersion` (остаётся 4).**
Оба ключа — глобальные скаляры приложения, а не состояние панели/страницы/снапшота; в `IAppSettings`
для них уже есть ровно этот класс соседей (`GridSize`, `GridSnapDistance`, `SnapResizeToIconGrid`,
`NativePanelSizing`, `HasShownMoveHint`). Альтернатива «положить в JSON-документ» отвергнута: она стоит
полноценной `V4ToV5Migration` + регенерации `expected-current-from-v1/v2/v3.json` + прохода по
`SchemaContractTests`/`GoldenMigrationTests` (самый дорогой блок Фазы 5, C3) — ради двух булевых
преференций, которые не относятся ни к одной из четырёх секций документа. Строкой `NormalModeLayoutGestures`
(а не int) — тот же приём, что `DefaultSurfaceStyle` в Фазе 5: устойчив к перестановке значений enum.

Долг за расхождение «часть настроек в `user.config`, часть в JSON» уже заведён (DO-014); новым долгом не
плодим, но в ADR-026 фиксируем явно.

### D-9. Точки переключения режима (DoD «одним действием»)

| # | Точка | Слой | Риск | Статус |
|---|---|---|---|---|
| 1 | **Пункт трея «Edit layout» (checkable)** | `DesktopOrganizer.Services` (`TrayIconManager`, `ITrayIconManager`) | низкий | **Обязателен — это гарантированный путь.** Трей — единственная поверхность, не зависящая от `WS_EX_NOACTIVATE`. Текущее меню — только `Settings`/`Exit`; добавляются `Edit layout` (checkable) и `Lock layout` (checkable) + два новых события интерфейса. `UpdateContextMenu` пересобирает меню целиком (уже так устроен) — заодно закрывает мёртвый флаг из DO-006, но **не** трогаем его в этой фазе, чтобы не смешивать фронты. |
| 2 | **Пункт `HeaderContextMenu` панели «Edit layout» (checkable)** | UI (`ChildWindow.xaml` + code-behind) | средний | Переиспользуется **тот же самый** `ContextMenu`-экземпляр, что уже доказанно получает ввод на `WS_EX_NOACTIVATE`-окне (R-7 Фазы 5). Обработчик — `Click="EditLayoutMenu_Click"` в code-behind, точный прецедент `RollUpMenu_Click`. |
| 3 | **Тоггл в окне настроек + `Ctrl+E`** (`InputBinding` окна настроек) | UI (`MainWindow.xaml`/`MainWindowViewModel`) | низкий | Обычное активируемое окно — клавиатура работает штатно. |
| 4 | **`Escape` в панели выходит из Edit Layout** | UI (`ChildWindow_PreviewKeyDown`) | **высокий, best-effort** | Тот же непроверенный класс риска, что ADR-013 R1 (F2/inline-rename на `WS_EX_NOACTIVATE`). Реализуем (это 3 строки рядом с существующей веткой `Key.F2`), но **не объявляем единственным путём выхода** и помечаем V-манулом. |
| 5 | ~~Глобальная горячая клавиша (`RegisterHotKey`/`WM_HOTKEY`)~~ | — | — | **Вынесено из Фазы 6.** В проекте нет инфраструктуры глобальных хоткеев (`grep RegisterHotKey` → 0), а Фазе 7 она понадобится для «Peek / Overlay mode». Заводить message-only окно + `WM_HOTKEY` ради одного потребителя — построить инфраструктуру дважды. → новый пункт бэклога **DO-016**, владелец — Phase 7. |
| 6 | ~~Десктоп-жест (двойной клик / лассо)~~ | — | — | **Отвергнуто.** Двойной клик по пустому десктопу уже занят переключением видимости иконок, правый drag — созданием панели лассо (ADR-003). Третий жест на том же `WH_MOUSE_LL` — рост риска в самом опасном месте кодовой базы при нулевом выигрыше в обнаруживаемости против пункта трея. |

### D-10. Отрисовка направляющих — отдельный, отбрасываемый пункт

Направляющие межпанельные: их нельзя нарисовать внутри одной панели, не выйдя за её границы. Варианты:

- **(a) Отдельное прозрачное окно-оверлей** `LayoutGuideOverlayWindow` в слое десктопа
  (`WS_EX_NOACTIVATE | WS_EX_TRANSPARENT`, click-through, `AllowsTransparency`, создаётся на время драга
  в Edit Layout, уничтожается на его конце). Полноценный UX Fences-класса.
- **(b) Без отдельного окна:** подсветка выровненного ребра **внутри** самой перетаскиваемой панели
  (accent-полоска по стороне, которая «защёлкнулась»). Дёшево, нулевой z-order-риск, слабее по UX.
- **(c) Совсем без визуала:** только магнитное выравнивание.

**Решение: планируем (a) как отдельный пункт P6-6, с (b) как заранее спроектированным fallback'ом.**
Причина выделения: z-order в слое `WorkerW` — исторически самая дорогая область этого проекта
(ADR-019/ADR-020: `Topmost` пришлось убирать, порядок `ApplyDesktopWindowStyles` относительно `Show()`
оказался критичным, поведение на Win10 ≠ Win11 и до сих пор не проверено). Если P6-6 при живой проверке
окажется хрупким — фаза закрывается на (b), а (a) уходит в бэклог без блокировки остальных шести пунктов.
P6-5 (математика + магнит) при этом ценна сама по себе и не зависит от исхода P6-6.

«Grid spacing visualization» из scaffold'а — рисуется тем же оверлеем; при fallback на (b) выпадает
целиком → **DO-017**, не блокирует DoD фазы.

### D-11. Диагностика (ADR-027)

Аддитивное событие `IGestureDiagnostics.LayoutModeChanged(LayoutInteractionMode from, LayoutInteractionMode to,
LayoutModeChangeSource source, bool isLocked)` + строка в `docs/Refactoring/Testing/diagnostics-log-events.md`.
Без этого ручная проверка «почему панель не двигается» на живой Windows превращается в гадание. Стоимость —
один метод интерфейса + одна запись JSONL.

### D-12. Ноль новых свойств в `ChildWindowViewModel` (DO-002)

Проблема: checkable-пункт `HeaderContextMenu` должен показывать текущий режим, а `ContextMenu` живёт в
отдельном визуальном дереве и **не наследует** attached DP (известная ловушка R-6, задокументирована
прямо в `PanelTheme` XML-doc). Обычный путь — свойство на `ChildWindowViewModel` — запрещён §0.

Решение: сервис публикуется как ресурс уровня приложения сразу после резолва из DI (в `App.StartApp`,
рядом с `LoadResourceDictionaries()`):

```csharp
Resources["LayoutEditMode"] = _serviceProvider.GetRequiredService<ILayoutEditModeService>();
```

и меню биндится через `StaticResource` (который через границу `ContextMenu` проходит штатно):

```xml
<MenuItem Header="Edit layout" IsCheckable="True" Click="EditLayoutMenu_Click"
          IsChecked="{Binding IsEditing, Source={StaticResource LayoutEditMode}, Mode=OneWay}"
          IsEnabled="{Binding CanEnterEditLayout, Source={StaticResource LayoutEditMode}, Mode=OneWay}"/>
```

(`IsEditing` — удобный bool-фасад над `Mode`, поднимающий `PropertyChanged` синхронно с ним.)
Тот же приём — для пункта «Lock layout». Ноль строк в `ChildWindowViewModel`, ноль binding-ошибок в
`SystemIconsPanelWindow` (который эти меню вообще не показывает).

### D-13. DO-013 (`ChildWindowModel.TextColor`) — **вне объёма Фазы 6**

Бэклог помечает DO-013 как «кандидат на удаление/пересмотр в Фазе 6/9». **Решение: Фаза 9.**

Обоснование: Фаза 6 — про взаимодействие и состояние ввода; `TextColor` — про внешний вид. Все три
причины, по которым Фаза 5 (D-4) отказалась его убирать, в Фазе 6 в силе и не становятся дешевле:
удаление = user-facing removal + правка окна настроек + правка `Settings.settings` + **миграция схемы
v4→v5**, которой у Фазы 6 иначе нет вообще (D-8). Втащить сюда миграцию ради косметики — потерять
главное преимущество плана (нулевой blast radius по persistence-тестам). В Фазе 9 (hardening + release)
консолидирующий проход по схеме уместен сам по себе.

Действие: в `docs/Refactoring/BACKLOG.md` поле «Владеющая фаза» у DO-013 меняется с «Phase 6/9
(кандидат)» на «Phase 9» с однострочной причиной (делает `architecture-documenter` в P6-7).

---

## 2. Артефакты

### 2.1 Новые файлы

| Путь | Тип | Scope | Пункт |
|---|---|---|---|
| `src/DesktopOrganizer.UI/Models/LayoutInteractionMode.cs` | enum | S | P6-1 |
| `src/DesktopOrganizer.UI/Models/NormalModeLayoutGestures.cs` | enum + парсер (по образцу `PanelSurfaceStyleParser`) | S | P6-1 |
| `src/DesktopOrganizer.UI/Models/LayoutGesturePolicy.cs` | static class (чистая логика) | S | P6-1 |
| `src/DesktopOrganizer.UI/Services/ILayoutEditModeService.cs` | interface | S | P6-1 |
| `src/DesktopOrganizer.UI/Services/LayoutEditModeService.cs` | singleton, INPC | M | P6-1 |
| `src/DesktopOrganizer.UI/Behaviors/ResizeAffordanceGate.cs` | `IResizeAffordance`-композит | S | P6-2 |
| `src/DesktopOrganizer.UI/Behaviors/LayoutEditModeBehavior.cs` | behavior (мост сервис → attached DP) | S | P6-4 |
| `src/DesktopOrganizer.UI/Theming/LayoutEditVisuals.cs` | static class (чистая логика) | S | P6-4 |
| `src/DesktopOrganizer.UI/Models/SnapGuideCalculator.cs` | static class (чистая логика) | M | P6-5 |
| `src/DesktopOrganizer.UI/Views/LayoutGuideOverlayWindow.xaml(.cs)` | Window (click-through оверлей) | L | P6-6 *(отбрасываемый)* |
| `src/DesktopOrganizer.UI/Services/ILayoutGuideOverlay.cs` + реализация | interface + singleton | M | P6-6 *(отбрасываемый)* |
| `tests/.../Unit/Models/LayoutGesturePolicyTests.cs` | tests | S | P6-1 |
| `tests/.../Unit/Services/LayoutEditModeServiceTests.cs` | tests | M | P6-1 |
| `tests/.../Unit/Behaviors/ResizeAffordanceGateTests.cs` | tests | S | P6-2 |
| `tests/.../Unit/Theming/LayoutEditVisualsTests.cs` | tests | S | P6-4 |
| `tests/.../Unit/Models/SnapGuideCalculatorTests.cs` | tests | M | P6-5 |
| `docs/Refactoring/Testing/edit-layout-manual-matrix.md` | manual-матрица | S | P6-7 |

### 2.2 Изменяемые файлы

| Путь | Что меняется | Риск | Пункт |
|---|---|---|---|
| `src/DesktopOrganizer.UI/Services/IAppSettings.cs`, `Services/AppSettings.cs`, `Properties/Settings.settings(.Designer.cs)` | `IsLayoutLocked`, `NormalModeLayoutGestures` | низкий | P6-1 |
| `src/DesktopOrganizer.UI/Configuration/DiConfig.cs` | `AddSingleton<ILayoutEditModeService, LayoutEditModeService>()` | низкий | P6-1 |
| `src/DesktopOrganizer.UI/Diagnostics/IGestureDiagnostics.cs` + реализация | `LayoutModeChanged` | низкий | P6-1 |
| `src/DesktopOrganizer.UI/Behaviors/PanelDragMoveBehavior.cs` | `canMove`-гейт, порог начала жеста, `AbortDrag()`, `_captureElement`; (P6-5) вставка guide-шага | **высокий** (сердце ADR-011/ADR-017) | P6-2, P6-5 |
| `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` | конструктор (gate, подписка на `ModeChanged`), `OnClosing` (отписка), `EditLayoutMenu_Click`/`LockLayoutMenu_Click`, `Escape` в `ChildWindow_PreviewKeyDown` | средний | P6-2, P6-3 |
| `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` | 2 пункта `HeaderContextMenu`; `PanelTheme.IsLayoutEditing` на `PanelRoot`; стиль Thumb'ов по триггеру | средний | P6-3, P6-4 |
| `src/DesktopOrganizer.UI/Theming/PanelTheme.cs` | `IsLayoutEditingProperty` (аддитивно) | низкий | P6-4 |
| `src/DesktopOrganizer.UI/Resources/Styles/Panels.xaml` | триггеры edit-режима на `PART_*` | средний | P6-4 |
| `src/DesktopOrganizer.UI/Resources/Styles/Controls.xaml` (или `Panels.xaml`) | `ResizeHandleStyle` | средний | P6-4 |
| `src/DesktopOrganizer.Services.Abstractions/Interfaces/ITrayIconManager.cs`, `Services/Providers/TrayIconManager.cs` | 2 новых события + 2 пункта меню | низкий | P6-3 |
| `src/DesktopOrganizer.UI/App.xaml.cs` | ресурс `LayoutEditMode` (D-12), подписка на события трея | низкий | P6-1/P6-3 |
| `src/DesktopOrganizer.UI/Views/MainWindow.xaml`, `ViewModels/MainWindowViewModel.cs` | тоггл + `Ctrl+E` + настройка `NormalModeLayoutGestures` | средний | P6-3 |
| `adr/ADR-026-edit-layout-mode.md` | Proposed → Accepted, полный текст | — | P6-7 |
| `docs/Refactoring/ROADMAP.md`, `BACKLOG.md`, `PHASES/PHASE-6-edit-layout-ux.md`, `ARTIFACT_INDEX.md`, `Testing/diagnostics-log-events.md` | close-out | — | P6-7 |

### 2.3 Тесты, которые могут потребовать обновления (blast radius)

Существенно меньше, чем в Фазах 4–5 (persistence не трогается): `WindowManagerTests`,
`MainWindowViewModelTests` (новые настройки), `NativeSizingAffordanceTests` (соседство с гейтом),
`RollUpTests` (`RollUpBehavior` теперь получает гейт вместо конечной affordance — поведение то же,
но конструктор в тестах меняется), `DesktopOrganizer.Tests` DI-композиция (новый singleton).
**Не должны сломаться ни одним тестом:** `GoldenMigrationTests`, `SchemaContractTests`,
`V*ToV*MigrationTests`, `PanelStateMapperTests`, `InteractionVisualStateTests`,
`ResizeGeometryCalculatorTests`, `PanelInteractionTrackerTests` — если ломаются, план нарушен (§0).

---

## 3. Порядок работ и зависимости

| # | Пункт | Содержание | Scope | Зависит от | Входной агент |
|---|---|---|---|---|---|
| **P6-1** | `feat(ui): global layout edit mode state + gesture policy` | enum'ы, `LayoutGesturePolicy`, `ILayoutEditModeService`, `IAppSettings`-ключи, DI, диагностика. **UI не трогается вообще** | M | — | `dotnet-services-engineer` (тесты — `tdd-test-engineer` до кода) |
| **P6-2** | `feat(ui): gate panel move/resize by layout mode` | `ResizeAffordanceGate`, гейт move + порог + `AbortDrag`, подписка `ChildWindow` на `ModeChanged`, abort-последовательность D-4 | **L** | P6-1 | `wpf-ui-builder` |
| **P6-3** | `feat(ui): edit layout toggles (tray, panel menu, settings)` | пункты трея (Services), пункты `HeaderContextMenu`, тоггл+`Ctrl+E` в настройках, `Escape` | M | P6-1 | `dotnet-services-engineer` (трей) ∥ `wpf-ui-builder` (меню/настройки) |
| **P6-4** | `feat(ui): edit layout visual language + visible resize handles` | `PanelTheme.IsLayoutEditing`, `LayoutEditVisuals`, `LayoutEditModeBehavior`, триггеры `Panels.xaml`, `ResizeHandleStyle` | M | P6-2 | `wpf-ui-builder` |
| **P6-5** | `feat(ui): panel alignment snap guides (math + pipeline)` | `SnapGuideCalculator`, вставка шага в конвейер драга (только Edit Layout) | M | P6-2 | `wpf-ui-builder` + `tdd-test-engineer` |
| **P6-6** | `feat(ui): layout guide overlay window` — **отбрасываемый** | click-through оверлей в слое десктопа, рендер направляющих (+ опц. grid spacing) | **L, высокий риск** | P6-5 | `win32-interop-specialist` ∥ `wpf-ui-builder` |
| **P6-7** | `docs(adr): ADR-026 Proposed→Accepted; phase 6 close-out` | ADR-026, ROADMAP, BACKLOG (DO-013→Phase 9, DO-016, DO-017), PHASE-6 DoD, manual-матрица, diagnostics-log-events | S | P6-2…P6-6 | `architecture-documenter` |

**Граф:** `P6-1 → (P6-2 ∥ P6-3) → (P6-4 ∥ P6-5) → P6-6 → P6-7`.
Параллелить одним сообщением можно **P6-2 ∥ P6-3** (пересечение только по `ChildWindow.xaml.cs`, разные
методы — вести последовательно внутри одной волны, если агент один) и **P6-4 ∥ P6-5** (разные файлы:
`Panels.xaml`/`PanelTheme` против `PanelDragMoveBehavior`/`Models`). Лимит ≤5 параллельных соблюдается.
P6-1 строго первый — из него читают все остальные.

После **каждого** пункта: `tdd-test-engineer` → `[debug-healer` при красном] → на волнах P6-2/P6-4/P6-6 —
`code-quality-reviewer` ∥ `fences-ux-parity-reviewer` одним сообщением.

---

## 4. Definition of Done

Формат — по образцу `docs/Refactoring/PHASES/PHASE-5-ambient-accent-ui.md`: каждый критерий имеет
машинную форму (проверяемо здесь) и/или явную пометку manual verify.

| # | Критерий (scaffold + ADR-026) | Как закрывается | Где проверяется |
|---|---|---|---|
| **DoD-1** | Случайный drag иконки не двигает панель | Move стартует только с заголовка **и** только после системного порога; `LayoutGesturePolicy` запрещает resize в Normal (default) | unit (`LayoutGesturePolicyTests`, 12 кейсов) + V-3, V-4 |
| **DoD-2** | Rename / context menu не запускают resize/move | `canMove`-гейт проверяется до `CaptureMouse()`; проверка идёт после `_onDragGestureStarting()` и после ветки `ClickCount==2`; resize-thumb'ы в Normal выключены гейтом на обеих ветках | unit (`ResizeAffordanceGateTests`) + V-5, V-6 |
| **DoD-3** | Edit mode включается/выключается **одним действием** | 3 независимые точки (D-9 №1–3), любая — один клик/`Ctrl+E`; `Toggle()` идемпотентен и однозначен | unit (`LayoutEditModeServiceTests`) + V-1, V-2 |
| **DoD-4** | Exit edit mode **гарантированно** завершает активные жесты | D-4: `AbortResize(SourceChanged)` → `AbortDrag()` → `ForceIdle()`; каждый шаг идемпотентен, `Exit` в `finally` (ADR-021) | unit (`ResizeGestureSessionTests` — уже зелёные; новый тест на порядок вызовов) + V-7 |
| **DoD-5** | Состояние персистится **только** там, где решение это явно разрешает | `Mode` — нет; `IsLayoutLocked`/`NormalModeLayoutGestures` — да, через `IAppSettings` | unit («после Load приложение всегда в `Normal`») + V-12 |
| **DoD-6** | В Edit Layout активны resize handles / hit zones, panel drag, guides; доступен lock layout; усилен accent border | D-5 + D-6 + D-7 + D-3 | build + `LayoutEditVisualsTests` + V-8, V-9, V-10 |
| **DoD-7** | В Normal контролы приглушены и приоритет у контента | `ChromeOpacityFor(Idle)=0.45` (Фаза 5, не меняется) + запрет resize | регресс-тест «`isLayoutEditing:false` ⇒ значения Фазы 5 без изменений» + V-11 |
| **DoD-8** | Нулевая регрессия move/resize/drop/rename/view mode (Global DoD ROADMAP) | Normal-конвейер драга побайтово прежний (guide-шаг выключен); §0-запреты; `ResizeOverlay`-геометрия не меняется | **грep-гейт**: `ResizeGeometryCalculator.cs`/`ResizeGestureSession.cs`/`PanelInteractionTracker.cs`/`GridSnap.cs`/`PanelCollision.cs` — 0 изменённых строк в диффе фазы; полный `dotnet test` зелёный + V-13..V-16 |
| **DoD-9** | `ChildWindowViewModel.cs` — 0 изменённых строк (DO-002) | D-12 | грep-гейт по диффу |
| **DoD-10** | Ни одного нового hex-литерала в `ChildWindow.xaml`/`MainWindow.xaml` (наследуемый гейт Фазы 5) | Edit-визуал — только существующие токены | `rg -n "#[0-9A-Fa-f]{3,8}"` → 0 |
| **DoD-11** | Решение отражено в ADR (Global DoD) | ADR-026 Proposed → Accepted с D-2/D-3/D-8/D-10 и отвергнутыми альтернативами | P6-7 |
| **DoD-12** | Схема состояния не изменена; апгрейд user state не требуется | `StateEnvelope.CurrentSchemaVersion` остаётся 4 | `GoldenMigrationTests`/`SchemaContractTests` зелёные **без единой правки** |

---

## 5. Риски и гейты

| ID | Риск | Митигация |
|---|---|---|
| **R-1** | Гейт обходит трекер ADR-021 и оставляет панель в `Dragging`/`Resizing` навсегда (блокирует все будущие жесты панели) | Гейт проверяется **до** `TryEnter`, никогда после; `AbortDrag` идёт через существующий `EndDrag` с `tracker.Exit` в `finally`; `ForceIdle()` как backstop в обработчике `ModeChanged` и в `ChildWindow_Unloaded` (уже есть). Тест на последовательность D-4. |
| **R-2** | Нативная ветка (`NativePanelSizing=True`): гейт добавляет работу в путь `WM_NCHITTEST`/хук | `NativePanelResizeController.isResizeEnabled` по-прежнему читает плоское поле `NativeSizingAffordance.IsEnabled`; гейт **пишет** в него заранее, при смене режима, а не вычисляется на каждом хит-тесте. Никаких походов в DI/сервис/локов из колбэка (инвариант CLAUDE.md). Обязательна проверка **обеих** веток: V-9 и V-14. |
| **R-3** | Порог начала драга ломает связку «одиночный клик / двойной клик (roll-up)» | Порог применяется только к переходу «нажатие → начало движения»; ветка `ClickCount == 2` остаётся **первой** и не зависит от порога; `RollUpBehavior` M-6 generation guard не трогается. V-15. |
| **R-4** | Наследуемый `PanelTheme.IsLayoutEditing` не доходит до `ContextMenu`/`Popup` (R-6 Фазы 5) — checkable-пункт молча всегда `false` | D-12: биндинг через `{StaticResource LayoutEditMode}` на сам сервис, не через attached DP. Комментарий-предупреждение рядом с пунктом меню. Ревью обязано это проверить. |
| **R-5** | Видимые resize-грипы меняют hit-геометрию `ResizeOverlay` → регрессия Фаз 1–2, не воспроизводимая здесь | Меняется **только** `ControlTemplate` Thumb'а; `ColumnDefinition`/`RowDefinition` (6/`*`/6) и `Tag`/`Cursor`/обработчики — дословно те же. Диф по `ChildWindow.xaml` в области `ResizeOverlay` должен затрагивать только атрибут `Style`. |
| **R-6** | `Escape`/`Ctrl+E` не доходят до панели с `WS_EX_NOACTIVATE` (класс риска ADR-013 R1, никогда не верифицирован) | Клавиатура — **не** основной путь (D-9 №4). Трей гарантирует переключение всегда. При провале V-2 — эскалация на `win32-interop-specialist` (тот же фолбэк, что ADR-013 R1: временное снятие `WS_EX_NOACTIVATE`), но фаза от этого не блокируется. |
| **R-7** | Оверлей направляющих не виден / ломает z-order слоя десктопа (ADR-019/ADR-020: `Topmost` пришлось убирать, порядок стилей относительно `Show()` критичен, Win10 ≠ Win11) | P6-6 изолирован и **отбрасываем**; заранее спроектированный fallback (b) из D-10. Никакой другой пункт фазы от него не зависит. При провале — DO, не блокер. |
| **R-8** | Guide-snap проталкивает панель в соседа, ломая «твёрдую стену» ADR-017 | Жёсткий порядок конвейера D-7: `ClampAgainstObstacles` **всегда последний**. Тест: для любого набора препятствий результат конвейера с включённым guide-шагом не создаёт нового пересечения (property-style тест поверх существующего `PanelCollision.CreatesNewOverlap`). |
| **R-9** | `IsLayoutLocked` трактуется слишком широко и блокирует `SnapWidthToGrid()` на загрузке/смене `IconSize` → раскладка молча дрейфует и «чинится» скачком при разблокировке | D-3 явно разрешает внутреннюю ре-нормализацию. Таблица D-3 обязана попасть в ADR-026 дословно. |
| **R-10** | Roll-up трактуется как изменение раскладки и блокируется при Lock → пользователь не может свернуть панель | D-3: roll-up разрешён при Lock. Тест на политику. V-16. |
| **R-11** | Edit Layout случайно начинает персиститься (через `IAppSettings` «за компанию») | D-8 + тест: после `Load()` сервис всегда в `Normal`, независимо от того, в каком режиме был сохранён предыдущий сеанс. |
| **R-12** | Соблазн «заодно» декомпозировать `ChildWindowViewModel` (DO-002) или убрать `TextColor` (DO-013) | §0 + D-13. DoD-9 — грep-гейт. |
| **R-13** | Расползание области: «раз уж есть глобальный режим, добавим Peek / профили раскладки» | Peek, tabs, smart panels, layout profiles — **Фаза 7** (`PHASE-7-product-features.md`). Глобальный хоткей — DO-016, владелец Phase 7. В Фазу 6 не затягивать. |

---

## 6. Отложено / вне объёма (DO)

| ID | Пункт | Куда |
|---|---|---|
| **DO-013** (существующий) | `ChildWindowModel.TextColor` — пересмотр/удаление | Перецелен: «Phase 6/9 (кандидат)» → **Phase 9** (D-13) |
| **DO-016** (новый) | Инфраструктура глобальных горячих клавиш (`RegisterHotKey`/`WM_HOTKEY`, message-only окно) — один общий потребительский контракт для Edit Layout и Peek | **Phase 7** (D-9 №5) |
| **DO-017** (новый) | Визуализация grid spacing в Edit Layout | Не назначена; зависит от исхода P6-6 (D-10) |
| **DO-018** (новый, условный) | Полноценный оверлей направляющих, если P6-6 отброшен в пользу fallback (b) | Не назначена |
| — | Resize anti-overlap (уже в бэклоге с ADR-017, конфликтует с квантизацией `GridMetrics`/ADR-015) | Не трогаем — отдельное решение |
| — | Per-panel edit mode, десктоп-жест переключения, multi-select панелей и групповое перемещение | Отвергнуто (D-1, D-9 №6) |

---

## 7. Тест-план: проверяемо здесь vs manual-only

### 7.1 Проверяемо в этой среде (`dotnet build` + `dotnet test`)

1. **`LayoutGesturePolicyTests`** — полная таблица истинности D-2 (12 кейсов) + устойчивость к мусорной
   строке настройки (парсер → default `MoveOnly`, никогда не бросает — прецедент `PanelSurfaceStyleParser`).
2. **`LayoutEditModeServiceTests`** — `Toggle`/`TryEnterEditLayout` при `IsLayoutLocked`; `ModeChanged`
   поднимается ровно один раз на реальный переход и не поднимается на no-op (прецедент `T-TRK-07`);
   `INotifyPropertyChanged` для `Mode`/`IsEditing`/`CanEnterEditLayout`; `IsLayoutLocked = true` во время
   Edit Layout принудительно выводит в `Normal`.
3. **`ResizeAffordanceGateTests`** — таблица 2×2 входов; идемпотентность; «`inner.SetEnabled` вызывается
   только при реальном изменении итогового значения».
4. **`LayoutEditVisualsTests`** — монотонность относительно Фазы 5 (`edit:true` ≥ `edit:false` по всем
   пяти состояниям, строго больше хотя бы по одному каналу) + регресс-тест точного совпадения с
   `InteractionVisualState.*` при `edit:false`.
5. **`SnapGuideCalculatorTests`** — выравнивание по 6 видам направляющих; детерминизм при равных
   кандидатах; эпсилон-устойчивость (нет дрожания на границе порога); отсутствие направляющих при пустом
   наборе препятствий; **композиционный тест с `PanelCollision`** (R-8): конвейер `GridSnap → Guides →
   Clamp` никогда не создаёт нового пересечения.
6. **`PanelThemeMetadataTests`** (расширение) — `IsLayoutEditingProperty`: тип `bool`, default `false`,
   флаг `Inherits` (статическое чтение метаданных, без визуального дерева и без STA).
7. **`ResourceDictionaryMergeTests`** (существующий STA smoke) — новые стили/триггеры резолвятся.
8. **DI-композиция** — `ILayoutEditModeService` резолвится как singleton; `ChildWindow` конструируется.
9. **Грep-гейты DoD-8/DoD-9/DoD-10** — 0 изменённых строк в списке файлов §0; 0 hex-литералов.
10. **Полный прогон** — 1551+ существующих тестов остаются зелёными; persistence-тесты **не меняются ни одной строкой**.

Ожидаемый прирост: ~55–75 тестов.

### 7.2 Принципиально manual verify (Win10/Win11) — `docs/Refactoring/Testing/edit-layout-manual-matrix.md`

| ID | Проверка |
|---|---|
| V-1 | Трей → «Edit layout»: галочка появляется, все панели одновременно получают edit-вид; повторный клик возвращает Normal |
| V-2 | Панель → ПКМ по заголовку → «Edit layout» (checkable): состояние галочки совпадает с реальным режимом (R-4); `Escape` в панели выходит из режима (best-effort, D-9 №4) |
| V-3 | **Normal:** попытка ресайза за любой из 8 краёв/углов — геометрия не меняется, курсор не превращается в `Size*`; клик по иконке у самой границы панели выделяет иконку, а не начинает ресайз |
| V-4 | **Normal:** одиночный клик по заголовку не сдвигает панель и не вызывает «подскок» к сетке (порог, R-3); драг заголовка по-прежнему двигает панель с магнитным снапом и анти-оверлапом |
| V-5 | **Normal:** ПКМ по заголовку → меню открывается, панель не сдвинулась; «Переименовать» → ввод работает (регресс ADR-013 R1) |
| V-6 | **Normal:** drag&drop файла с рабочего стола, drag-reorder иконок, F2-переименование иконки — без изменений (регресс ADR-014 R1/R2) |
| V-7 | **Выход из режима во время жеста:** начать ресайз → не отпуская, выключить Edit layout из трея → жест завершается чисто, панель не «залипает», следующий жест стартует нормально (DoD-4) |
| V-8 | **Edit Layout:** 8 грипов видимы, accent-рамка усилена, кнопки заголовка не приглушены; вид отличим от Normal с первого взгляда |
| V-9 | **Edit Layout, легаси-ветка (`NativePanelSizing=False`):** ресайз за все 8 краёв работает как раньше, квантование к сетке иконок не изменилось |
| V-10 | **Edit Layout:** направляющие появляются при сближении рёбер/центров двух панелей; панель защёлкивается; «твёрдая стена» анти-оверлапа по-прежнему не пробивается (R-8) |
| V-11 | **Normal, idle:** кнопки заголовка приглушены ровно как в Фазе 5 (сравнение с baseline-скриншотами) |
| V-12 | Перезапуск приложения: всегда стартует в `Normal`; `Lock layout` и `NormalModeLayoutGestures` пережили перезапуск (DoD-5) |
| V-13 | `Lock layout` включён: move и resize запрещены в обоих режимах; пункт «Edit layout» disabled с тултипом; roll-up, rename, выделение, запуск файлов, drop — работают (D-3) |
| V-14 | **Edit Layout, нативная ветка (`NativePanelSizing=True`):** `WM_NCHITTEST` отдаёт края только в Edit Layout; в Normal — `HTCLIENT`; ввод системы не подтормаживает (R-2) |
| V-15 | Двойной клик по заголовку сворачивает/разворачивает панель в обоих режимах и при включённом Lock (R-3, R-10) |
| V-16 | Свёрнутая панель в Edit Layout: грипы не появляются (гейт roll-up'а имеет приоритет), панель двигается |
| V-17 | DPI 100/125/150/200%: грипы не размываются, порог драга ощущается одинаково, направляющие попадают в пиксель |
| V-18 | Мультимонитор: направляющие считаются в пределах рабочей области монитора панели; переключение режима не дёргает панели на других мониторах |
| V-19 | *(только если P6-6 реализован)* Оверлей направляющих виден поверх обоев и панелей, кликов не перехватывает, исчезает на mouse-up, не появляется в Alt+Tab/панели задач; Win10 **и** Win11 (R-7) |

---

## 8. Открытые вопросы (решать по месту; эскалация только если реализующий агент не уверен)

1. **Q-1.** Дефолт `NormalModeLayoutGestures`: `MoveOnly` (рекомендация плана, D-2) vs `MoveAndResize`
   (полная обратная совместимость, но фаза тогда ничего не чинит «из коробки»). Если
   `fences-ux-parity-reviewer` настаивает на варианте B — меняется одна константа по умолчанию + одна
   строка в ADR-026 Consequences.
2. **Q-2.** Порог начала драга: системный (`SystemParameters.MinimumHorizontal/VerticalDragDistance`,
   рекомендация — так же, как в `ListReorderGestureController`) vs собственная константа. Рекомендация —
   системный: пользователь уже настроил его под свою руку.
3. **Q-3.** Порог магнита направляющих: отдельная константа `SnapGuideCalculator.DefaultThreshold = 8`
   (рекомендация) vs переиспользование `IAppSettings.GridSnapDistance` (дефолт 12). Рекомендация —
   отдельная: это разные магниты (сетка vs соседняя панель), и склеивание их одним ползунком сделает
   настройку невнятной. Выносить в UI в этой фазе **не** нужно.
4. **Q-4.** P6-6: делать сразу или сначала закрыть P6-1..P6-5 и оценить живьём. Рекомендация — второе
   (D-10): фаза может быть закрыта как `[~]` с fallback (b) без потери ни одного DoD-критерия.

---

## 9. Recommended Next Actions

1. 🚨 **CRITICAL — `dotnet-services-engineer`**: **P6-1** (`ILayoutEditModeService`, `LayoutGesturePolicy`,
   enum'ы, `IAppSettings`-ключи, DI, `IGestureDiagnostics.LayoutModeChanged`). Это вход конвейера — из него
   читают все остальные пункты. Передать §1 D-1, D-2, D-3, D-8, D-11 и §5 R-11.
2. ⚠️ **RECOMMENDED — `tdd-test-engineer` (до реализации P6-1)**: `LayoutGesturePolicyTests` (таблица D-2)
   и `LayoutEditModeServiceTests`. Это единственная часть фазы, где DoD-1/DoD-3/DoD-5 вообще машинно
   проверяемы — §7.1 п.1–2.
3. 🚨 **CRITICAL — `wpf-ui-builder`**: **P6-2** (`ResizeAffordanceGate`, гейт move + порог + `AbortDrag`,
   подписка `ChildWindow` на `ModeChanged`), затем **P6-4** и **P6-5**. Передать §1 D-4, D-5, D-6, D-7,
   §5 R-1, R-3, R-5, R-8 и **весь §0** — здесь самый высокий риск регрессии в закрытых фазах.
4. ⚠️ **RECOMMENDED — `dotnet-services-engineer` ∥ `wpf-ui-builder`**: **P6-3** (трей в Services ∥ меню и
   окно настроек в UI) — можно вести параллельно с P6-2, зависимость только от P6-1. Передать §1 D-9, D-12
   и §5 R-4, R-6.
5. ⚠️ **RECOMMENDED (параллельно, одним сообщением) — `code-quality-reviewer` ∥ `fences-ux-parity-reviewer`**
   после P6-2 и ещё раз после P6-4/P6-5. Для `code-quality-reviewer` — грep-гейты DoD-8/DoD-9/DoD-10 (§4).
   Для `fences-ux-parity-reviewer` — Q-1 (дефолт политики Normal) и семантика Lock (D-3) против поведения
   «Lock fences» в Stardock Fences.
6. 💡 **OPTIONAL / условно CRITICAL — `win32-interop-specialist`**: **P6-6** (оверлей направляющих в слое
   десктопа) — только после живой оценки P6-5, и **немедленно**, если V-2/V-14 покажут, что клавиатура или
   нативный hit-test ведут себя не так, как предполагает D-5/D-9. Передать ADR-019/ADR-020 как список уже
   набитых шишек по z-order.
7. 🚨 **CRITICAL — `architecture-documenter`**: **P6-7** — ADR-026 Proposed → Accepted с дописанными
   Context / Alternatives / Consequences (обязательно: уточнение Decision по D-2, таблица Lock из D-3,
   «схема не мигрируется» из D-8, отброшенность P6-6 из D-10), ROADMAP Phase 6 → `[~]`, DoD-чеклист в
   `PHASE-6-edit-layout-ux.md`, BACKLOG (DO-013 → Phase 9, новые DO-016/DO-017/DO-018),
   `diagnostics-log-events.md`, `ARTIFACT_INDEX.md`, `edit-layout-manual-matrix.md`.
8. 💡 **OPTIONAL — повторный `fences-feature-planner`**: если при реализации P6-2 гейт move/resize окажется
   больше L (например, потребует разделить `PanelDragMoveBehavior` на «жест» и «политику») — дочерний план
   `docs/PLANS/phase-6-gesture-gating.md`, не «плющить» структуру.

**Ручная проверка на Windows 10/11 по этой фазе не опциональна:** фаза целиком про ввод, и ни один из
V-1..V-19 не воспроизводим в этой среде. Без них Фаза 6 может быть закрыта только как `[~]` (код
завершён, поведение не верифицировано) — тем же статусом, что Фазы 0–5.
