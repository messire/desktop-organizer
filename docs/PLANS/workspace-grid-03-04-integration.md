# Workspace Grid — P03/P04: план интеграции drag/snap/collision/мониторы и resize/native/roll-up

**Этапы:** P03 (`docs/fix-prompts/03-positioning.md`) + P04 (`docs/fix-prompts/04-resize.md`) ·
**Ветка:** `bugfix/workspace-grid` (HEAD = мерж P02 fix-цикла 1, `dotnet test` 2463 зелёные) ·
**Статус:** Стадия S реализована и слита (см. §1.7, worktree `agent-acd3ed8256d258971`,
`dotnet test` 2517 зелёные) · **P03 реализован (§2.8, worktree `agent-abf2b9b384b739c09`)** · **P04 реализован
(§3.11, worktree `agent-ab88848231c36443c`)** · оба слиты в `bugfix/workspace-grid`. · **Дата:** 2026-09-22

Источники правды: [ADR-028](../../adr/ADR-028-workspace-grid.md) (включая «Amendment 2026-09-22 — P02
review (fix-cycle 1)»), [`workspace-grid-02-metrics.md`](workspace-grid-02-metrics.md) §7.2 (финальный
публичный контракт `WorkspaceLayoutPolicy`/`WorkspaceGridMetrics`/`PanelCollision`),
[`workspace-grid-00-baseline.md`](workspace-grid-00-baseline.md) часть A (writers W1–W21, три системы
координат, F-1/F-6/F-7/F-8), [`workspace-grid-migration.md`](workspace-grid-migration.md) §10
(persistence-контракт, §10.5 — открытый риск для 03/04), `docs/fix-prompts/AGENT_CONTRACT.md`,
ADR-011/017/021/022/026.

Все `file:line` — по состоянию HEAD на момент написания плана. Строки, которые сдвинут параллельные
fix-циклы (§0), помечены «(сдвинется)» — исполнитель обязан перепроверить grep'ом по имени метода, а не
слепо по номеру.

---

## 0. Предусловия: что будет смержено ДО старта P03/P04

Планируем **поверх** двух параллельных fix-циклов. Стартовать P03 только после их мержа и зелёного
`dotnet test`.

### 0.1 P02b fix (persistence-фундамент, доработка)

| Что | Где ожидается | Как план это учитывает |
|---|---|---|
| Нормализация `GeometryMode == GridAligned && WidthSpanUnits == null → LegacyFreeform` в `PanelStateMapper.FromDto` | `src/DesktopOrganizer.UI/State/PanelStateMapper.cs` | **Инвариант В1** (ниже). Рантайм-код P03/P04 обязан работать и без неё (defence in depth). |
| Общий `PanelDefaults.Apply(model, IAppSettings)` для `MainWindowViewModel.AddPanel` (`MainWindowViewModel.cs:449-474`, поле на `:468`) и `App.NewPanelWithDefaults` (`App.xaml.cs:402-418`, поле на `:416`) | новый файл в `src/DesktopOrganizer.UI/Models/` или `Services/` | **§2.6**: план покрывает ОБА исхода — (a) спаны вычисляются при создании через `BoundsToSpans`, (b) панель создаётся `LegacyFreeform` с `TODO(P03)`. |

**Инвариант В1 (обязателен независимо от исхода P02b).** Любой рантайм-потребитель режима обязан
спрашивать не `model.GeometryMode`, а одну общую функцию (см. §1.3 `PanelGridState.Resolve`), которая
возвращает `LegacyFreeform` для «`GridAligned` без валидных spans И без возможности их вычислить».
Ни один `if (model.GeometryMode == PanelGeometryMode.GridAligned)` не пишется напрямую в
behaviors/controllers — это структурная защита от исхода (b) и от битого JSON.

- **Исход (a)** — spans есть с момента создания: `PanelGridState.Resolve` просто возвращает их.
- **Исход (b)** — `GridAligned` + `null` spans: `Resolve` **лениво** вычисляет spans через
  `WorkspaceLayoutPolicy.BoundsToSpans` (`WorkspaceLayoutPolicy.cs:142`, Ceiling+допуск — корректная
  функция для одноразовой миграции размера, ADR-028 Amendment «решения оркестратора») от текущего
  `model.Size`, записывает их в модель и помечает состояние как «мигрировано» (persist — через обычный
  `SaveWindowsState`, без отдельного прохода). Если `model.Size` ещё нулевой (`AddPanel`,
  `MainWindowViewModel.cs:451-471` не задаёт `Size` → XAML `260×300`, W11), `Resolve` возвращает
  `LegacyFreeform` для этого кадра и повторит попытку после первого `OnRenderSizeChanged`
  (`ChildWindow.xaml.cs:455-461`).

### 0.2 P05 fix (icon layout / chrome)

- `_resizeController.SnapWidthToGrid()` удалён из `ChildWindow.Initialize` (`ChildWindow.xaml.cs:300`).
  **Следствие для P03:** исчезает главный источник «jump on first launch» (baseline риск 7) — план НЕ
  дублирует этот фикс и НЕ возвращает вызов.
  Вызов на `ChildWindow.xaml.cs:876` (`OnModelPropertyChanged` на `IconSize`/`ViewMode`) **остаётся** —
  он должен стать no-op для `GridAligned` (§3.7).
- Введён policy-зависимый `GridMetrics.ContentHorizontalChrome(policy)` (резерв скроллбара при
  `FixedGap`). **Следствие для P04:** `ResizeConstraintsFactory.cs:60` (`HorizontalChrome:
  GridMetrics.FileListHPadding`) к тому моменту уже будет читать новую функцию — P04 **не трогает
  горизонтальный хром вообще**, только добавляет ветку `GridAligned` рядом (§3.2). Если P05 ещё не
  смержен на момент старта P04 — P04 всё равно не меняет эту строку.

### 0.3 Границы объёма (из промптов)

Вне рамок обоих этапов: рефакторинг не затронутых модулей, новые продуктовые фичи, переписывание native
sizing с нуля, замена WorkerW/хранилища, изменение DPI-awareness на PerMonitorV2, resize anti-overlap для
`LegacyFreeform` (ADR-028 явно оставляет backlog-пункт ADR-017 открытым для старого режима).

---

## 1. Стадия S (shared) — фундамент, выполняется ОДНИМ агентом ДО P03/P04

Пять маленьких, чисто аддитивных артефактов. Без них P03 и P04 неизбежно подерутся за одни и те же
строки `ChildWindow.xaml.cs`/`ChildWindow.xaml`.

### 1.1 `DesktopCoordinateSpace` — изоляция открытого вопроса WorkerW/отрицательных координат

**Проблема (ADR-028 «Открытые вопросы», Вопрос 2; baseline «Пространства координат» п. 3).** После
`SetParent(hwnd, WorkerW)` (`ChildWindow.xaml.cs:325` → `DesktopIntegrationService.EmbedInDesktop`)
пространство `Window.Left/Top` **документально не определено**: возможно, оно совпадает с экранным DIP, а
возможно — смещено на `SM_XVIRTUALSCREEN`/`SM_YVIRTUALSCREEN`. `MonitorInfo.Bounds`/`WorkArea`
(`MonitorInfo.cs:12`, `MonitorService.cs:122-126`) — всегда экранные DIP. До P03 это никому не мешало,
потому что `GridSnap` (`GridSnap.cs:19-32`) считал от абсолютного нуля и ни разу не сравнивал два
пространства. P03 обязан их сравнивать (origin = work area монитора).

**Решение (проектное, закрывает требование «одна константа/функция изолирует неопределённость»).**
Новый файл `src/DesktopOrganizer.UI/Models/DesktopCoordinateSpace.cs` — pure static:

```csharp
namespace DesktopOrganizer.UI.Models;

/// Единственная точка перевода между экранным DIP-пространством (MonitorInfo.Bounds/WorkArea,
/// IMonitorService — ВСЕГДА screen DIP) и пространством Window.Left/Top панели, встроенной в WorkerW.
public static class DesktopCoordinateSpace
{
    /// Смещение screenSpace - windowSpace. (0,0) = пространства совпадают.
    /// ЕДИНСТВЕННАЯ точка, где живёт неопределённость ADR-028 Вопрос 2.
    public static Vector Offset { get; private set; } = new(0d, 0d);

    public static bool IsIdentity => Offset.X == 0d && Offset.Y == 0d;

    public static Point ToWindowSpace(Point screenDip) => new(screenDip.X - Offset.X, screenDip.Y - Offset.Y);
    public static Rect  ToWindowSpace(Rect screenDip);
    public static Point ToScreenSpace(Point windowDip) => new(windowDip.X + Offset.X, windowDip.Y + Offset.Y);
    public static Rect  ToScreenSpace(Rect windowDip);

    /// Калибровка по факту (аналог NativeSizingOriginResolver.Resolve для WM_SIZING-пространства):
    /// вызывается ОДИН раз из ChildWindow.OnSourceInitialized после EmbedInDesktop, сравнивает
    /// GetWindowRect-производную screen-позицию с Window.Left/Top. Идемпотентна.
    public static void CalibrateOnce(Point screenTopLeftDip, Point windowTopLeftDip);
}
```

**Почему именно так, а не «просто предположить, что пространства совпадают»:** ADR-028 Вопрос 2 вариант B
(оптимистичное допущение) отклонён. Но и блокировать весь P03 до ручного замера нельзя (среда без GUI).
Компромисс: **вся** неопределённость живёт в одном свойстве `Offset`, по умолчанию `(0,0)` — то есть
поведение по умолчанию **байт-в-байт** равно «пространства совпадают» (сегодняшнее фактическое
допущение), и весь остальной код P03/P04 написан так, что ему всё равно, чему равен `Offset`. Если
manual verify покажет расхождение, правится одна функция `CalibrateOnce` (или включается уже
подготовленный вызов), а не двадцать call-site'ов.

**Прецедент в коде:** ровно этот приём уже применён к другому недокументированному пространству —
`NativeSizingOriginResolver`/`_originOffsetPx` (`NativePanelResizeController.cs:369-379` вход,
`:456-461` симметричный выход). Здесь повторяется тот же паттерн на уровень выше.

**Manual verify, который это закрывает (см. §7):** пункт MV-1.

**Точки вставки калибровки:** `ChildWindow.OnSourceInitialized` (`ChildWindow.xaml.cs:321-333`), сразу
после `_desktopIntegration.EmbedInDesktop(hwnd)` (`:325`) и до `_nativeResizeController?.AttachTo(hwnd)`
(`:332`). Источник экранных координат — `INativeApi.GetWindowRect`, недоступный из UI (см. §1.2) →
на первом проходе `CalibrateOnce` вызывается с результатом нового `IMonitorService.GetWindowRectDip`
(§1.2) либо не вызывается вовсе (тогда `Offset` остаётся `(0,0)` — сегодняшнее поведение). Решение
между этими двумя вариантами — открытый вопрос **OQ-1** (§8).

### 1.2 `IMonitorService` — два аддитивных члена

`INativeApi` физически недоступен из `DesktopOrganizer.UI` (явно задокументировано в
`App.xaml.cs:79-89`: «`INativeApi` is internal to DesktopOrganizer.Services»), а единственная точка
system-DPI-конверсии — `MonitorService.ToDipRect` (`MonitorService.cs:122-126`, вызовы `:88-90`, `:131`).
Три источника DPI уже названы риском (baseline «Источники DPI», риск 15) — четвёртый заводить нельзя.

Аддитивно в `src/DesktopOrganizer.Services.Abstractions/Interfaces/IMonitorService.cs` (сейчас 5 членов,
`:16`–`:44`) + реализация в `src/DesktopOrganizer.Services/Tools/MonitorService.cs`:

```csharp
/// System-DPI scale (1.0 == 96 DPI) — ТОТ ЖЕ коэффициент, которым GetMonitors() уже конвертирует
/// Bounds/WorkArea в DIP. Нужен UI-слою для единственной операции: перевод физических px лассо-жеста
/// (DesktopLassoEventArgs, "screen pixels") в DIP без заведения второго DPI-источника (F-7).
double GetSystemDpiScale();

/// Экранные DIP-координаты окна по его HWND (GetWindowRect / system DPI). Нужен ТОЛЬКО для
/// DesktopCoordinateSpace.CalibrateOnce (ADR-028 Вопрос 2). Возвращает null, если HWND невалиден.
Rect? GetWindowRectDip(IntPtr hwnd);
```

Реализация — однострочные делегаты на уже существующие `nativeApi.GetSystemDpiScale()`
(`MonitorService.cs:88`) и `INativeApi.GetWindowRect` + тот же `ToDipRect`-делитель. Оба метода —
чистые, мокируются `Mock<IMonitorService>` в тестах.

**Альтернатива (если OQ-1 закроется вариантом «не калибровать»):** добавить только
`GetSystemDpiScale()`, `GetWindowRectDip` не вводить. Второй член — ровно цена варианта A.

### 1.3 `PanelGridState` — единый резолвер режима/spans/контекста (инвариант В1)

Новый файл `src/DesktopOrganizer.UI/Models/PanelGridState.cs`, pure static + `readonly record struct`:

```csharp
/// Разрешённое геометрическое состояние панели на ОДИН кадр жеста/операции.
public readonly record struct PanelGridState(
    bool IsGridAligned,
    Point Origin,        // work area монитора панели, в ПРОСТРАНСТВЕ Window.Left/Top
    Rect WorkArea,       // то же пространство
    double UnitSize,
    double Gap,
    GridSpan MinSpan,
    string? MonitorDeviceId)
{
    public static readonly PanelGridState Legacy; // IsGridAligned=false, всё остальное — нули
}

public static class PanelGridStateResolver
{
    /// Единственная точка, где читается ChildWindowModel.GeometryMode (инвариант В1).
    /// monitors — снимок (Bounds, WorkArea, DeviceName) в ЭКРАННЫХ DIP; конверсия в window-space
    /// делается здесь через DesktopCoordinateSpace.
    public static PanelGridState Resolve(
        ChildWindowModel? model,
        Rect currentBoundsWindowSpace,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors,
        bool isRolledUp);
}
```

Правила `Resolve` (все — прямые следствия ADR-028, не новые решения):

1. `model is null` или `model.GeometryMode != GridAligned` → `PanelGridState.Legacy`.
2. Монитор панели ищется в порядке: `MonitorDeviceId` (точное совпадение `DeviceName`) → монитор,
   чей `Bounds` содержит **центр** `currentBoundsWindowSpace` (переведённый в screen-space) → primary.
   **Центр, не top-left** — прямое требование ADR-028 «`MonitorDeviceId` при межмониторном drag»
   (top-left-эвристика `MonitorService.FindMonitorAt`, `MonitorService.cs:111-112`, даёт ложный
   результат, если верхний левый угол вышел за все мониторы — baseline «Отрицательные координаты»).
   Мониторов нет вовсе (`GetMonitors()` вернул `[]`, `MonitorService.cs:97`) → `PanelGridState.Legacy`
   (fail-safe: без origin `GridAligned` не определён).
3. `Origin = DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea.TopLeft)`,
   `WorkArea = DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea)`.
4. `UnitSize = WorkspaceGridMetrics.UnitSize` (`WorkspaceGridMetrics.cs:24`),
   `Gap = WorkspaceGridMetrics.DefaultGap` (`:32`).
   `MinSpan = isRolledUp ? WorkspaceGridMetrics.MinSpanCollapsed (:73) : MinSpanExpanded (:70)`.
5. Spans: если `WidthSpanUnits`/`HeightSpanUnits` оба непустые и `>= MinSpan` — берутся как есть; иначе —
   ленивое вычисление из `currentBoundsWindowSpace.Size` через `WorkspaceLayoutPolicy.BoundsToSpans`
   (`WorkspaceLayoutPolicy.cs:142`) **с записью обратно в модель**; если размер невалиден
   (`<= 0`/`NaN`) — `PanelGridState.Legacy` на этот кадр (см. §0.1 исход (b)).

**Производительность.** `Resolve` вызывается **не на каждый MouseMove**: P03 вызывает его один раз на
жест (в `OnTitleBarMouseMove` при переходе `_isDragging → true`, `PanelDragMoveBehavior.cs:248`) и один
раз в `EndDrag`; P04 — один раз в `TryBegin`-пути. Это тот же контракт «снимок один раз на жест», что уже
у `_dragObstacles` (`PanelDragMoveBehavior.cs:36-40`) и `_dragMonitors` (`:42-60`).

### 1.4 `ChildWindow` — партиалы и «пустые» пункты меню (антиконфликтный шов)

`ChildWindow` уже `partial` (`ChildWindow.xaml.cs:35`). Стадия S создаёт **два пустых партиала** и
добавляет в `ChildWindow.xaml` пункты меню, ссылающиеся на их обработчики:

| Файл (новый) | Владелец после стадии S | Содержимое после стадии S |
|---|---|---|
| `src/DesktopOrganizer.UI/Views/ChildWindow.WorkspaceGrid.cs` | **P03** | `AlignToGridMenu_Click` → пока `{ }` (no-op) |
| `src/DesktopOrganizer.UI/Views/ChildWindow.Sizing.cs` | **P04** | `SizePresetMenu_Click` → пока `{ }` (no-op) |

`ChildWindow.xaml`, блок `HeaderContextMenu` (`:113`–`:193`; порядок пунктов сегодня: `Rename` `:119`,
`Roll up / down` `:120`, разделитель, `Edit layout` `:130`, `Lock layout` `:133`, заголовок `Content`
`:136`, …). Вставка **между** `Roll up / down` (`:120`) и разделителем перед `Edit layout`:

```xml
<MenuItem Header="Size" x:Name="SizePresetMenu">      <!-- P04 -->
  <MenuItem Header="Small"      Tag="Small"      Click="SizePresetMenu_Click"/>
  <MenuItem Header="Medium"     Tag="Medium"     Click="SizePresetMenu_Click"/>
  <MenuItem Header="Large"      Tag="Large"      Click="SizePresetMenu_Click"/>
  <MenuItem Header="Extra large" Tag="ExtraLarge" Click="SizePresetMenu_Click"/>
</MenuItem>
<MenuItem Header="Align to workspace grid" x:Name="AlignToGridMenu" Click="AlignToGridMenu_Click"/>  <!-- P03 -->
```

`Tag`-строки — те же имена членов `PanelSizePreset` (`src/DesktopOrganizer.UI/Models/PanelSizePreset.cs`),
парсинг — `Enum.TryParse` в обработчике; это ровно тот же приём «`Thumb.Tag` = точное имя члена enum +
`TryParse`», который уже используется для `ResizeEdge` (`ResizeEdge.cs:30-42`, `ChildWindow.xaml`
`ResizeOverlay`).

**Gating обоих пунктов (ADR-026, §«Gating»).** Оба — explicit-команды меню: блокируются
`IsLayoutLocked`, но **не** блокируются `NormalModeLayoutGestures = None/MoveOnly`. Реализация — та же,
что уже у `Edit layout` (`ChildWindow.xaml:130-132`, биндинг на `CanEnterEditLayout`): `IsEnabled`
биндится на новое computed-свойство. Источник — `ILayoutEditModeService.IsLayoutLocked`
(`ILayoutEditModeService.cs:69`). Обработчики дополнительно делают ранний `return` при
`_layoutEditMode.IsLayoutLocked` (defence in depth, как `EditLayoutMenu_Click`,
`ChildWindow.xaml.cs:477-484`).

### 1.5 `PanelGeometryWriter` — единая точка записи геометрии (защита от рекурсии, baseline риск 17)

Новый файл `src/DesktopOrganizer.UI/Behaviors/PanelGeometryWriter.cs`.

**Проблема.** `ChildWindowModel.Position` (`ChildWindowModel.cs:158`) и `.Size` (`:163`) **не** поднимают
INPC, поэтому цикла «model → window» сегодня нет (baseline риск 17). Но `GeometryMode`
(`ChildWindowModel.cs:171`), `WidthSpanUnits` (`:184`), `HeightSpanUnits` (`:191`) — **поднимают**
(`SetField`). Если P03/P04 наивно подпишутся на них в `OnModelPropertyChanged`
(`ChildWindow.xaml.cs:873-877`) и начнут писать `Window.Width/Height`, получится цикл
`OnRenderSizeChanged (:455-461) → model.Size → … → Width → OnRenderSizeChanged`.

**Решение.** Один класс с ре-энтранси-guard'ом:

```csharp
internal sealed class PanelGeometryWriter
{
    private bool _applying;
    /// Пишет Left/Top/Width/Height окна И соответствующие поля модели (включая spans) ОДНИМ
    /// актом; повторный вход (из OnLocationChanged/OnRenderSizeChanged, которые сработают
    /// синхронно) игнорируется.
    public void Apply(Rect boundsWindowSpace, GridSpan? span, string? monitorDeviceId);
    public bool IsApplying => _applying;
}
```

`ChildWindow.OnLocationChanged` (`:441-446`) и `OnRenderSizeChanged` (`:455-461`) получают ранний выход
`if (_geometryWriter.IsApplying) return;` — **единственное** изменение этих двух методов во всём
объёме P03/P04. Подписка на `WidthSpanUnits`/`HeightSpanUnits` в `OnModelPropertyChanged`
**не вводится вообще** (spans — производная, а не вход; писать их обратно в окно некому).

### 1.6 DoD стадии S

- [x] Новые типы компилируются, `dotnet test` зелёный (2463 baseline + 54 новых unit-тестов стадии S =
      2517, см. §1.7 test log).
- [x] `DesktopCoordinateSpace.Offset == (0,0)` по умолчанию, `IsIdentity == true` — доказано тестом;
      при `Offset == (0,0)` `ToWindowSpace`/`ToScreenSpace` — тождество (тест на round-trip, включая
      отрицательные координаты).
- [x] `PanelGridStateResolver.Resolve` покрыт: null-модель, `LegacyFreeform`, `GridAligned` без spans +
      валидный `Size`, `GridAligned` без spans + `Size = (0,0)`, пустой список мониторов, монитор по
      `DeviceName`, монитор по центру, fallback на primary, отрицательный origin.
- [x] `IMonitorService` расширен аддитивно; ни один существующий тест `MonitorServiceTests`/
      `MonitorServiceNativeApiTests` не изменён (новые тесты добавлены рядом, старые — байт-в-байт).
- [x] Два пустых партиала + два пункта меню существуют; `AlignToGridMenu`/`SizePresetMenu` disabled при
      `IsLayoutLocked` — реализовано через тест на computed-свойство (`CanEnterEditLayout` — уже
      `!IsLayoutLocked`, уже покрыт `LayoutEditModeServiceTests.CanEnterEditLayout_IsInverseOfIsLayoutLocked`)
      + реflection-тест, что оба partial-обработчика существуют с правильной сигнатурой
      (`ChildWindowWorkspaceGridPartialTests`); XAML-проводка (`Click="..."`) валидируется самим markup
      compiler'ом на `dotnet build` (см. §1.7 «что отложено»).
- [x] Ни одного изменения поведения: `LegacyFreeform`-панель проходит весь существующий набор тестов
      без правок (2463 старых теста не изменены и зелёные).

### 1.7 Стадия S — реализовано (факт, для P03/P04)

Все пять артефактов — аддитивные, публичные сигнатуры ниже — контракт, на который P03/P04 кодируют
дальше. Ветка `worktree-agent-acd3ed8256d258971` (изолированный worktree поверх `bugfix/workspace-grid`
@ `50d5bd2`).

**1. `DesktopCoordinateSpace`** — `src/DesktopOrganizer.UI/Models/DesktopCoordinateSpace.cs` (новый).
Реализовано ровно по OQ-1 = A: `Offset` — `private set`, дефолт `(0,0)`; `IsIdentity`;
`ToWindowSpace(Point|Rect)`/`ToScreenSpace(Point|Rect)`; `CalibrateOnce(Point screenTopLeftDip, Point
windowTopLeftDip)` реализован, но **не вызывается нигде** в рантайм-коде (гейт — MV-1). Добавлен
`internal static void ResetForTests()` (доступен тестам через `InternalsVisibleTo`) — процесс-wide
static state, тестовая сборка уже отключает параллелизацию (`AssemblyInfo.cs`), так что сброс между
тестами безопасен без блокировок.

**2. `IMonitorService` — два аддитивных члена** (`Services.Abstractions/Interfaces/IMonitorService.cs`,
реализация `Services/Tools/MonitorService.cs`):
```csharp
double GetSystemDpiScale();          // делегат на nativeApi.GetSystemDpiScale() — тот же коэффициент, что GetMonitors()
Rect? GetWindowRectDip(IntPtr hwnd); // nativeApi.GetWindowRect + тот же ToDipRect-делитель; null при failure
```
Оба — однострочные обёртки над уже существующими `INativeApi.GetSystemDpiScale()`/`GetWindowRect`. Ни
один существующий тест `MonitorServiceTests`/`MonitorServiceNativeApiTests` не тронут; 5 новых тестов
добавлены в `MonitorServiceNativeApiTests.cs`.

**3. `PanelGridState` + `PanelGridStateResolver`** — `src/DesktopOrganizer.UI/Models/PanelGridState.cs`
(новый). Сигнатура **идентична** плану §1.3, с одним уточнением контракта параметра `monitors` (не
менявшим форму кортежа): при отсутствии совпадения ни по `MonitorDeviceId`, ни по центру — используется
**первый элемент списка** как fallback на primary (кортеж `(DeviceName, Bounds, WorkArea)` не несёт
`IsPrimary` — ровно то, что понесёт `_dragMonitors` после P03-D1 — так что "primary" здесь означает
«первый в снимке», задокументировано в XML-doc обоих типов; P03/WindowManager обязаны класть primary
первым при построении снимка, если это для них важно). Инвариант В1 реализован буквально:
`GeometryMode` читается **только** внутри `Resolve`. Ленивое вычисление spans (правило 5) пишет
`WidthSpanUnits`/`HeightSpanUnits` обратно в модель без клампинга к `MinSpan` (модель — производная,
дальнейший клэмп — забота P04/`ResizeConstraintsFactory`).

**4. `ChildWindow` партиалы + пункты меню**:
- `src/DesktopOrganizer.UI/Views/ChildWindow.WorkspaceGrid.cs` (новый, пустой) — `AlignToGridMenu_Click`
  no-op, комментарий «P03 владелец».
- `src/DesktopOrganizer.UI/Views/ChildWindow.Sizing.cs` (новый, пустой) — `SizePresetMenu_Click` no-op,
  комментарий «P04 владелец».
- `ChildWindow.xaml`: вставлены **между** `Roll up / down` и разделителем перед `Edit layout` (без
  разделителя между «Roll up / down» и новыми пунктами — само дерево уже разделяет их отдельным
  `<Separator/>` перед «Edit layout», см. diff). `SizePresetMenu` — подменю 4 пунктов (`Tag="Small"` /
  `"Medium"` / `"Large"` / `"ExtraLarge"`, имена член `PanelSizePreset` для `Enum.TryParse` в будущем
  P04-обработчике). `AlignToGridMenu` — `IsCheckable="True"`, `IsChecked` привязан (`OneWay`,
  `EnumEquals` конвертер) к `PlacementTarget.DataContext.Model.GeometryMode ==
  PanelGeometryMode.GridAligned` (OQ-3 = B). **Гейтинг обоих пунктов реализован через переиспользование**
  `ILayoutEditModeService.CanEnterEditLayout` (`== !IsLayoutLocked` буквально — тот же биндинг-паттерн,
  что уже `Edit layout`) — отдельного нового computed-свойства заводить не потребовалось, семантика уже
  совпадала 1:1.

**5. `PanelGeometryWriter`** — `src/DesktopOrganizer.UI/Behaviors/PanelGeometryWriter.cs` (новый,
`internal sealed`). Конструктор `(Window window, Func<ChildWindowModel?> getModel)`; единственный метод
`void Apply(Rect boundsWindowSpace, GridSpan? span, string? monitorDeviceId)`; `bool IsApplying { get; }`.
`ChildWindow` получил поле `_geometryWriter`, сконструированное один раз в конструкторе (общее для
Thumb/native веток), и guard `if (_geometryWriter.IsApplying) return;` в `OnLocationChanged`/
`OnRenderSizeChanged` — единственное изменение этих двух методов.

**Что отложено (сознательно, не блокирует P03/P04):**
- Полный STA-тест, конструирующий живой `ChildWindow` для проверки, что `AlignToGridMenu`/
  `SizePresetMenu` реально дизейблятся в визуальном дереве — не написан: `ChildWindow`'s XAML использует
  `{StaticResource LayoutEditMode}`, публикуемый в `Application.Current.Resources` только из
  `App.xaml.cs`'s `StartApp`; конструирование `Application` + все 8 DI-зависимостей `ChildWindow`
  ради двух статических пунктов меню было признано непропорциональным (никакой существующий тест в
  проекте не строит живой `ChildWindow` вообще). Вместо этого: (a) `dotnet build`'s markup-компилятор
  уже гарантирует, что `Click="AlignToGridMenu_Click"`/`Click="SizePresetMenu_Click"` резолвятся к
  существующим методам совместимой сигнатуры (иначе сборка красная); (b) биндинг `IsEnabled` — **дословно
  тот же паттерн**, что уже используют «Edit layout»/`ContentFilter`-пункты того же `ContextMenu`,
  и его семантика (`CanEnterEditLayout == !IsLayoutLocked`) уже покрыта
  `LayoutEditModeServiceTests.CanEnterEditLayout_IsInverseOfIsLayoutLocked`; (c) реflection-тест
  подтверждает, что оба partial-обработчика существуют с сигнатурой `(object, RoutedEventArgs)`. Если
  ревьюер сочтёт этого недостаточно — добавление полного STA-теста с mock'нутым `Application` не требует
  правки production-кода, только тестовой инфраструктуры, и не блокирует P03/P04.
- `CalibrateOnce`'s реальный вызов из `ChildWindow.OnSourceInitialized` — намеренно не подключён (OQ-1 =
  A), ждёт MV-1.
- `WorkspaceGridMetrics.CollapsedHeaderSpan` (OQ-4) — **не тронут** (файл владеет параллельный P02
  fix-cycle 2 агент); в тестах стадии S используется символически (`WorkspaceGridMetrics.MinSpanCollapsed`/
  `CollapsedHeaderSpan`), не как захардкоженное число.

---

## 2. P03 — drag / snap / collision / мониторы

### 2.1 Scope

Подключить `WorkspaceLayoutPolicy` к реальному пути перемещения панели, к восстановлению при старте, к
`MonitorsChanged`/`WM_SETTINGCHANGE`, к лассо-созданию и добавить явную команду выравнивания.
**Не трогать** resize-контроллеры, `RollUpBehavior`, `ResizeConstraintsFactory`,
`ResizeGeometryCalculator`, `NativeSizingCoordinateMapper` — это объём P04.

### 2.2 Definition of Done (из `03-positioning.md`, все проверяемые)

| # | Критерий (формулировка промпта) | Как проверяется |
|---|---|---|
| D3-1 | Независимо созданные панели с одинаковым span совпадают по краям | Unit: две панели, одинаковые spans, один монитор → `SpansToBounds`+`SlotToPosition` дают идентичные `Width`/`Height` и края на одних линиях. Уже есть база: `SnapBounds_SeveralPanelsSameSpan_ProduceIdenticalGeometryOnSameWorkspace`. Новый уровень — через `PanelGridStateResolver`+drag-конвейер. |
| D3-2 | Соседние панели имеют равные gaps | Unit: gap-aware `PanelCollision.ClampAgainstObstacles(..., gap)` (`PanelCollision.cs:173`) на конце drag'а даёт ровно `Gap` DIP между краями (расширение `AdjacentSlots_LeaveExactlyGapBetweenPanels` на путь drag'а). |
| D3-3 | Restore не переносит панель на другой монитор при доступном исходном | Unit `WindowManagerTests`: `MonitorDeviceId` найден → монитор не меняется ни в `ResolveMonitorPlacement`, ни в `OnMonitorsChanged`. + новый: `GridAligned` при изменившемся `WorkArea` того же монитора остаётся на нём. |
| D3-4 | Нет jump на first launch | (a) `LegacyFreeform` — байт-в-байт (golden/характеризационные тесты не меняются); (b) `SnapWidthToGrid` из `Initialize` уже убран P05; (c) `GridAligned` с валидными spans восстанавливается через `SpansToBounds` от origin текущего монитора — тест «загрузка → позиция/размер равны сохранённым с точностью до квантования, второй запуск даёт тот же результат». |
| D3-5 | Позиция стабильна после drag-release и рестарта | Unit: `EndDrag`-путь идемпотентен (`Snap(Snap(x)) == Snap(x)` уже доказано на уровне policy); новый тест «drag → spans → persist → restore → те же bounds». |
| D3-6 | Клавиатурный/доступный fallback не деградирует | Регрессия: `LayoutGesturePolicy` (`LayoutGesturePolicy.cs:21-40`) не меняется; `ChildWindow.xaml.cs:434-438` (Escape) не трогается; пункты меню доступны при `NormalModeLayoutGestures = None`. |
| D3-7 | `MonitorDeviceId` обновляется при межмониторном drag (F-6) | Unit: `EndDrag` с финальным центром на втором мониторе → `MonitorDeviceId` изменился; последующий `OnMonitorsChanged` не возвращает панель. |
| D3-8 | Лассо: px → DIP (F-7) | Unit: `scale=1.5`, `DesktopLassoEventArgs(300,300,600,600)` → `Position=(200,200)`, `Size=(400,400)`. |
| D3-9 | Alt подавляет квантование на кадр, но не на drop | Unit на pure-уровне + STA-тест на поведение. |
| D3-10 | Anti-overlap не пробивается никогда | Unit: во всех комбинациях (Alt/не-Alt, Edit Layout/Normal) результат не создаёт overlap. |

### 2.3 Конвейер drag: точные точки вставки

Файл `src/DesktopOrganizer.UI/Behaviors/PanelDragMoveBehavior.cs`.

**Где сейчас origin монитора.** Уже есть `_dragMonitors` (`:60`) — снимок `(Bounds, WorkArea)` **всех**
мониторов, берётся один раз за жест на `:263-265` рядом со снимком препятствий (`:255-258`). Этого
снимка **достаточно**: нового обращения к `IMonitorService` на per-frame-пути не появится. Но в снимок
нужно добавить `DeviceName` (сегодня он отбрасывается на `:265`), иначе `EndDrag` не сможет обновить
`MonitorDeviceId` (F-6).

| # | Точка | Изменение |
|---|---|---|
| P03-D1 | `:60` поле `_dragMonitors` | Тип `List<(Rect Bounds, Rect WorkArea)>` → `List<(string DeviceName, Rect Bounds, Rect WorkArea)>`. Doc-комментарий `:42-59` дополнить одной фразой про F-6. |
| P03-D2 | `:263-265` заполнение снимка | `_dragMonitors.Add((monitor.DeviceName, monitor.Bounds, monitor.WorkArea));` |
| P03-D3 | `:248` (`_isDragging = true`) — сразу после | Один раз за жест: `_gridState = PanelGridStateResolver.Resolve(_getModel(), new Rect(_window.Left,_window.Top,_window.ActualWidth,_window.ActualHeight), _dragMonitors, isRolledUp)`. Новое приватное поле `_gridState` + новый ctor-параметр `Func<ChildWindowModel?> getModel` (сегодня behavior получает только `getPanelId`, `:123`/`:168`/`:202`). |
| P03-D4 | `:283` (`GridSnap.Snap`) | Разветвление: `GridAligned` → `WorkspaceLayoutPolicy.SnapPosition(new Point(rawLeft,rawTop), _gridState.Origin, _gridState.UnitSize)` (`WorkspaceLayoutPolicy.cs:75`), **подавляется при удержании Alt** (см. P03-D5). `LegacyFreeform` → существующая строка `:283` **без единого изменения**. |
| P03-D5 | новый код рядом с `:283` | `var freeMove = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;` — читается **на каждый кадр**, никогда не снапшотится (та же конвенция «read fresh», что у `_canMove`, `:98-105`, и `_layoutEditMode.IsEditing`, `:291`). При `freeMove && IsGridAligned` шаг квантования пропускается целиком; `snappedLeft/Top` = `rawLeft/rawTop`. |
| P03-D6 | `:291-317` блок guides | **Не меняется.** `SnapGuideCalculator.Snap` (`SnapGuideCalculator.cs:120`, порог `DefaultThreshold=8.0`, `:76`) остаётся вторым шагом и остаётся под `_layoutEditMode.IsEditing` (`:291`). Меняется только источник `workArea`: для `GridAligned` берётся `_gridState.WorkArea` (уже в window-space), а не повторный скан `_dragMonitors` на `:305-312`; для `LegacyFreeform` — скан `:303-312` **без изменений**. |
| P03-D7 | `:322-323` clamp | `GridAligned` → `PanelCollision.ClampAgainstObstacles(current, snappedLeft, snappedTop, _dragObstacles, _gridState.Gap)` (gap-aware перегрузка, `PanelCollision.cs:173`). `LegacyFreeform` → существующая gap-less перегрузка (`PanelCollision.cs:93`) **без изменений**. Позиция в конвейере не меняется — **всегда последний шаг** (ADR-017 «твёрдая стена», ADR-028 «Конвейер `GridAligned`»). |
| P03-D8 | `:325-326` запись | Не меняется (пишет `_window.Left/Top`). |
| P03-D9 | `SnapToGrid()` `:444-464` | Добавляется ветка `GridAligned` **в начале метода**: финальный `WorkspaceLayoutPolicy.SnapPosition` от `_gridState.Origin` **независимо от того, удерживается ли Alt** (ADR-028: «на `EndDrag` позиция обязательно квантуется»), затем grid-aware `SafeClamp(rect, _gridState.WorkArea, U, G)` (`WorkspaceLayoutPolicy.cs:374`), затем повторный gap-aware `ClampAgainstObstacles` — с откатом по осям через `CreatesNewOverlap` (`PanelCollision.cs:206`), ровно как сегодня на `:456`/`:462`. Ранний выход `if (grid <= 1) return;` (`:447`) остаётся, но **только** для `LegacyFreeform`-ветки (`GridSize` — настройка старого режима, ADR-011; `GridAligned` от неё не зависит). |
| P03-D10 | `EndDrag` `:384-398`, между `SnapToGrid()` (`:387`) и `_dragObstacles.Clear()` (`:394`) | **F-6:** если `IsGridAligned` (или даже `LegacyFreeform` — см. ниже) и центр финального прямоугольника в `Bounds` другого монитора из `_dragMonitors`, записать `model.MonitorDeviceId = thatDeviceName` + пересчитать/записать spans через `PanelGeometryWriter` (§1.5). Порядок обязателен: **до** `_windowManager.SaveWindowsState()` (`:397`). **Исправлено fix-циклом 1 (C-1, ADR-028 «Реализация P03 — fix-цикл 1»): формулировка «между `SnapToGrid()` и `_dragObstacles.Clear()`» была ошибочной предпосылкой** — резолюция монитора по сырому (до clamp) rect обязана происходить ДО `SnapToGrid()`, не между ним и очисткой снимков, иначе `SnapToGrid`'s `ResolveGridDrop` успевает клампить панель обратно в work area стартового монитора первым. Фактическая реализация: `PanelDragMoveBehavior.SnapToGrid()` вызывает новый `PanelDragPipeline.RetargetToDropMonitor(current, state, _dragMonitors)` непосредственно ПЕРЕД `ResolveGridDrop`; `UpdateMonitorDeviceIdOnDrop()` (уже реализованный корректно) остаётся ПОСЛЕ `SnapToGrid()`, как и было. |

**Решение по F-6 и `LegacyFreeform`.** ADR-028 формулирует инвариант `MonitorDeviceId`-на-`EndDrag` без
оговорки о режиме, а сам баг (baseline риск 2) ломает DoD D3-3/D3-5 для **обоих** режимов. Но
«`LegacyFreeform` — байт-в-байт» — жёстче. **Выбор: обновлять `MonitorDeviceId` в обоих режимах**,
обоснование: `MonitorDeviceId` не участвует ни в одном drag-кадре и не влияет на визуальную геометрию
жеста; он влияет только на то, какой монитор будет выбран при следующем `ResolveMonitorPlacement`
(`WindowManager.cs:380-393`) — то есть исправляет именно тот сценарий, который DoD называет багом
(«restore не переносит панель на другой монитор»). Инвариант «байт-в-байт» относится к
snap/collision-математике и к `Width`/`Height`, а не к сохранению идентификатора монитора, который
сегодня просто не обновляется (M4, baseline). **Помечено как решение этого плана, требует подтверждения
ревьюером** — см. OQ-2 (§8).

### 2.4 Restore при старте и `MonitorsChanged`

Файл `src/DesktopOrganizer.UI/Services/WindowManager.cs`.

| # | Точка | Изменение |
|---|---|---|
| P03-W1 | `ResolveMonitorPlacement` `:380-393` | Разветвление после выбора `target` (`:389-391`): `LegacyFreeform` → `ClampIntoMonitor(model, target)` (`:392` → `:404-423`) **без изменений**. `GridAligned` c валидными spans → (1) `size = WorkspaceLayoutPolicy.SpansToBounds(span, U, G)` (`WorkspaceLayoutPolicy.cs:164`); (2) `pos = WorkspaceLayoutPolicy.SnapPosition(model.Position, origin, U)` где `origin = DesktopCoordinateSpace.ToWindowSpace(target.WorkArea.TopLeft)`; (3) `rect = WorkspaceLayoutPolicy.SafeClamp(new Rect(pos,size), workAreaWindowSpace, U, G)` (**grid-aware перегрузка**, `:374` — именно она гарантирует, что результат уже на сетке и повторный `Snap` его не сдвинет, MAJOR-2); (4) запись в `model.Position`/`model.Size`. |
| P03-W2 | `ClampIntoMonitor` `:404-423` | **Не меняется вообще.** Остаётся `LegacyFreeform`-путь (и единственный путь, если spans невалидны). Это защищает D3-4 (a). |
| P03-W3 | `OnMonitorsChanged` `:441-469` | Внутри цикла `:447-466`: сохраняется существующая логика «монитор пропал → primary» (`:453-458`) **без изменений** (ADR-028: M4 не в scope). Для монитора, который **остался**, но у которого изменился `WorkArea`: `LegacyFreeform` → `ClampIntoMonitor` (`:462`) без изменений; `GridAligned` → тот же расчёт, что P03-W1 (spans от НОВОГО origin) — то есть панель **остаётся в том же логическом слоте** и на том же мониторе. Запись в `window.Left/Top` (`:464-465`) дополняется записью `window.Width/Height` **только для `GridAligned`** (`SpansToBounds` мог дать другой DIP-размер, если origin/U изменились) — через `PanelGeometryWriter` (§1.5), чтобы `OnRenderSizeChanged` не устроил рекурсию. `SaveWindowsState()` (`:468`) — без изменений. |
| P03-W4 | новый метод `AlignPanelsToWorkspaceGrid(Guid? panelId)` | См. §2.5. Публикуется через `IWindowManager` (`IWindowManager.cs`, аддитивный член рядом с `CollectOtherPanelBounds`, `:155`). |

**«Нет jump на first launch» — точное обоснование.** `WM_SETTINGCHANGE` поднимает `MonitorsChanged`
(`MonitorService.cs:72-73`) буквально на смену темы/обоев — то есть P03-W3 будет срабатывать часто.
Для `LegacyFreeform` ничего не меняется (ранний выход `ClampIntoMonitor` `:414-416` при «панель целиком
внутри»). Для `GridAligned` результат **идемпотентен** по построению: `SpansToBounds` — чистая функция
от span, а `SnapPosition`+grid-aware `SafeClamp` доказанно идемпотентны (P02 тесты
`SnapCoordinate_RepeatedApplication_IsIdempotent*`, MAJOR-2 фикс). Требуется **новый** тест
«`OnMonitorsChanged` дважды подряд при неизменившейся конфигурации → `Position`/`Size` не изменились ни
на один DIP».

### 2.5 Команда «Align to workspace grid»

Семантика — дословно ADR-028 «Явная команда»: (1) `GeometryMode ← GridAligned`; (2) позиция через
`SnapPosition`; (3) размер вверх через `BoundsToSpans`/`SpansToBounds` (Ceiling+допуск — одноразовая
операция, не теряющая контент); (4) итог пропускается через тот же gap-aware
`PanelCollision.ClampAgainstObstacles` — панель может не долететь до идеала, это ожидаемое поведение.

Композиция уже существует одним вызовом: `WorkspaceLayoutPolicy.SnapBounds(Rect raw, Point origin,
double unitSize, double gap, GridSpan minSpan)` (`WorkspaceLayoutPolicy.cs:192`) — шаги (2)+(3)+минимум.

| Уровень | Где | Что |
|---|---|---|
| Per-panel | `ChildWindow.WorkspaceGrid.cs` (создан стадией S), обработчик `AlignToGridMenu_Click` | Ранний выход при `_layoutEditMode.IsLayoutLocked`; `PanelGridStateResolver.Resolve` (с принудительным `IsGridAligned=true`, т.к. режим включается этой же командой); `SnapBounds`; `ClampAgainstObstacles(..., gap)` с `_windowManager.CollectOtherPanelBounds` (`WindowManager.cs:123-157`); запись через `PanelGeometryWriter`; `model.GeometryMode = GridAligned`; `model.WidthSpanUnits/HeightSpanUnits`; `_windowManager.SaveWindowsState()`. |
| All panels | `WindowManager.AlignPanelsToWorkspaceGrid(null)` (P03-W4) | Тот же алгоритм в цикле по `_childWindows` (`WindowManager.cs:19`). **Порядок обработки детерминирован** (по `Position.Y`, затем `Position.X` — иначе результат зависит от порядка словаря и «кто первый занял слот»). Уже выровненные панели участвуют как препятствия. Один `SaveWindowsStateNow()` в конце (не N штук) — тот же приём, что `deferSave` в `RemoveChildWindow` (`WindowManager.cs:198-220`). |
| UI (all) | `MainWindow.xaml`, Expander «Desktop panels» (`:225-245`), рядом с `Add panel`/`Remove` (`:240-241`) | `<Button Content="Align all to grid" Command="{Binding AlignAllPanelsCommand}"/>`; команда — в `MainWindowViewModel` рядом с `AddPanelCommand` (`MainWindowViewModel.cs:415`), `CanExecute` = `!IsLayoutLocked`. |
| UI (tray) | **Отложено** | `ITrayIconManager.UpdateContextMenu` (`ITrayIconManager.cs:59`) — позиционные `bool`-параметры; добавление пункта требует нового события + изменения сигнатуры в `Services.Abstractions` + `App.InitTrayIcon` (`App.xaml.cs:347-380`). Цена не оправдана: команда уже доступна из контекстного меню каждой панели и из окна настроек. Зафиксировать в ADR как сознательное решение, а не пропуск. |

**Обратной команды нет** (ADR-028: «массовое разравнивание не запрошено и не имеет очевидного
безопасного целевого положения»). Снятие режима — per-panel, через будущий переключатель в настройках
панели; в объём P03 не входит (OQ-3, §8).

### 2.6 Лассо (F-7) и создание панели

| # | Точка | Изменение |
|---|---|---|
| P03-L1 | `App.CreateLassoPanel` `App.xaml.cs:389-400`, записи на `:393-394` | Перед записью: `var scale = _monitorService.GetSystemDpiScale();` (§1.2) → `args.X/Y/Width/Height` делятся на `scale`. Округление — `(int)Math.Round(...)` (поля `System.Drawing.Point/Size` — `int`), а не `(int)`-усечение: усечение к нулю асимметрично для отрицательных координат (baseline M1/M5). `Math.Max(MinPanelSize, …)` (`:23`, `:394`) применяется **после** конверсии. |
| P03-L2 | `App.NewPanelWithDefaults` `:402-418` | Если P02b fix ввёл `PanelDefaults.Apply` — вызывать его; иначе оставить как есть. Новая панель, созданная `GridAligned` (`:416`), получает spans **сразу** здесь же (исход (a)) либо лениво в `PanelGridStateResolver` (исход (b)) — §0.1. |
| P03-L3 | `MainWindowViewModel.AddPanel` `:449-474` | `Position=(0,0)`/`Size=(0,0)` (M8, baseline) — панели ложатся друг на друга. **Минимальный фикс в объёме P03:** для `GridAligned` — первый свободный слот на work area монитора под курсором/primary через `SlotToPosition` (`WorkspaceLayoutPolicy.cs:110`) + `PanelCollision.Overlaps` (`PanelCollision.cs:51`) по уже существующим панелям. Для `LegacyFreeform` — **без изменений** (сегодняшнее поведение). |

**Почему `GetSystemDpiScale` на `IMonitorService`, а не конверсия внутри `DesktopGestureService`:**
второй вариант меняет публичный контракт `DesktopLassoEventArgs` («screen pixels»,
`DesktopLassoEventArgs.cs:8-18`) и его тесты; первый — аддитивен и оставляет единственный DPI-источник.

### 2.7 Порядок коммитов P03

1. `feat(ui): drag-time grid state resolution` — P03-D1…D3 (снимок мониторов + `_gridState`, ещё без
   изменения математики). Тесты: снимок содержит `DeviceName`; `Resolve` вызывается один раз за жест.
2. `feat(ui): GridAligned drag pipeline (SnapPosition → guides → gap-aware clamp)` — P03-D4…D8.
   Отдельный коммит: здесь меняется поведение живого drag'а.
3. `feat(ui): Alt free-move modifier` — P03-D5 (если не влит в п.2 — предпочтительно отдельно, ради
   изолированного revert'а).
4. `fix(ui): update MonitorDeviceId on cross-monitor drag (F-6)` — P03-D10.
5. `feat(ui): GridAligned drop-time snap + SafeClamp` — P03-D9.
6. `feat(ui): GridAligned restore and MonitorsChanged handling` — P03-W1…W3.
7. `feat(ui): Align to workspace grid (panel + all)` — §2.5 (P03-W4, партиал, `MainWindow.xaml`, VM).
8. `fix(ui): lasso px→DIP (F-7)` + `AddPanel` slot placement — P03-L1…L3.
9. `test(ui): P03 integration matrix` — §5 (или тесты идут внутри каждого коммита — предпочтительно, TDD
   по `AGENT_CONTRACT.md`).

### 2.8 P03 — реализовано (факт)

> **Fix-цикл 1 (code-review, 2026-09-22)** нашёл и закрыл CRITICAL C-1 (F-6 фактически не работал для
> меж-мониторного drag — см. правку P03-D10 выше) и три MAJOR (M-1 guide work area заморожена на старте
> жеста; M-2 «Align all» смешивало страницы; M-3 `OnMonitorsChanged` писало/сохраняло безусловно) плюс
> пять MINOR/NIT — полная запись в ADR-028 «Реализация P03 — fix-цикл 1». Тест-таблица ниже (2718) —
> состояние DoD НА МОМЕНТ первой реализации P03, отдельного отсчёта. Базис ИМЕННО fix-цикла 1 (worktree
> этого прохода) — 2747; ПОСЛЕ fix-цикла 1 — **2766 passed** (+19, все новые, ни один старый не удалён/не
> изменил ожидание).

Ветка-worktree `worktree-agent-abf2b9b384b739c09` поверх `bugfix/workspace-grid` @ `ab60986` (включает
P02 fix-cycle 2, P02b fix-2, P05 fix-2, P07 fix-1). `dotnet build` — 0 ошибок; `dotnet test` — **2718
passed, 0 failed** (базис ветки 2622 → +96 новых тестов; ни один существующий тест не удалён и не изменил
численного ожидания).

**Новые файлы (все — чистые, без WPF-окна, покрыты unit-тестами):**

| Файл | Что | Тесты |
|---|---|---|
| `UI/Models/PanelDragPipeline.cs` | `ResolveGridFrame` (кадр), `ResolveGridDrop` (drop), `ClampHardWall` (стена), `ResolveDropMonitor` (F-6) | `Unit/Models/PanelDragPipelineTests.cs` (27) |
| `UI/Models/WorkspaceGridPlacement.cs` | `ExpandedBounds`, `TryAlign`, `ClearAlignment`, `FindFreeSlot` + `AlignedPanelGeometry` | `Unit/Models/WorkspaceGridPlacementTests.cs` (25) |
| `UI/Models/LassoPanelGeometry.cs` | `ToDip` (F-7), `ApplyTo` | `Unit/Models/LassoPanelGeometryTests.cs` (14) |

Остальные +30 тестов — в существующих файлах: `WindowManagerGridPlacementTests.cs` (12, новый файл рядом
с `WindowManagerTests`), `PanelCollisionTests` (3, carry-over), `MainWindowViewModelTests` (5),
`PanelGeometryWriterTests` (5), `PanelGridStateResolverTests` (3), `ChildWindowWorkspaceGridPartialTests` (2).

**Изменённые файлы:** `UI/Behaviors/PanelDragMoveBehavior.cs` (P03-D1…D10), `UI/Behaviors/PanelGeometryWriter.cs`
(+`ApplyPosition`, `internal` → `public`), `UI/Models/PanelCollision.cs` (carry-over fix, §8.5 метрик),
`UI/Services/WindowManager.cs` + `IWindowManager.cs` (P03-W1…W4 + `PlaceNewPanelOnFreeSlot`),
`UI/Views/ChildWindow.WorkspaceGrid.cs` (владелец — P03), `UI/Views/ChildWindow.xaml.cs` (только
ctor-проводка: `getModel`/`geometryWriter` + перенос конструирования `_geometryWriter` выше),
`UI/ViewModels/MainWindowViewModel.cs` (+`AlignAllPanelsCommand`, `AddPanel` → слот),
`UI/Views/MainWindow.xaml` (одна кнопка), `UI/App.xaml.cs` (только lasso-блок).
**Не тронуты:** `WorkspaceLayoutPolicy.cs`, `GridSnap.cs`, `SnapGuideCalculator.cs`, `WorkspaceGridMetrics.cs`,
все resize-файлы и `RollUpBehavior` (зона P04), `PanelDefaults`/store/mapper (P02b), `ChildWindow.xaml`.

**Отклонения от плана §2 (осознанные, задокументированы в ADR-028 «Реализация P03»):**

1. **`SnapToGrid` для `GridAligned` не зависит от `_appSettings.GridSize`** — ранний выход `if (grid <= 1)`
   остался только в `LegacyFreeform`-ветке (как и предписывал P03-D9), а ветка `GridAligned` стоит **до**
   него и сама резолвит `PanelGridState`, если `_gridState` пуст (вызов из resize-контроллеров, §4.3 п. 3).
2. **Per-frame запись осталась прямой** (`_window.Left/Top`), через `PanelGeometryWriter` идут только
   фиксирующие записи (drop, F-6, restore, align) — ровно как в P03-D8; иначе каждый кадр драга писал бы
   `model.Size` и ломал `LegacyFreeform`-инвариант «байт-в-байт».
3. **`ClampHardWall` добавляет per-axis откат** поверх gap-aware clamp'а (ADR-028 «Реализация P03» п. 2).
4. **«Тот же логический слот» при сдвиге work area не реализуем на схеме v5** — реализована формула плана
   `SnapPosition(model.Position, newOrigin, U)` (ADR-028 «Реализация P03» п. 3) — открытый вопрос **OQ-6**
   (§8).
5. **Добавлен `PanelGridStateResolver.ParticipatesInGrid(model)`** — дешёвая проверка «может ли панель
   вообще участвовать в сетке», живущая в том же типе (инвариант В1 не нарушен). Нужна потому, что
   `SnapToGrid` общий с resize-контроллерами: без неё каждый resize `LegacyFreeform`-панели платил бы за
   `IMonitorService.GetMonitors()`. Разрешено использовать **только** как ранний выход в
   `PanelGridState.Legacy`.
6. **Добавлен аддитивный член `IWindowManager.PlaceNewPanelOnFreeSlot`** — P03-L3 требовал слот-плейсмент
   в `MainWindowViewModel.AddPanel`, но VM не имеет доступа к `IMonitorService`; расширять её конструктор
   (и все существующие тесты) дороже, чем добавить член интерфейсу, у которого уже есть вся нужная
   информация.

**Test log (P03):**

```
dotnet build DesktopOrganizer.sln   -> 0 Error(s)  (warnings: pre-existing SA1512/SA0001/xUnit2013)
dotnet test  tests/DesktopOrganizer.Tests
  Passed! - Failed: 0, Passed: 2718, Skipped: 0, Total: 2718, Duration: ~12 s
```

**DoD §2.2 — статус:**

| # | Статус | Чем закрыт |
|---|---|---|
| D3-1 | закрыт | `WorkspaceGridPlacementTests.TryAlign_*` (одинаковый span → `SpansToBounds` даёт идентичный размер, позиция — на линиях одного origin) + `PanelDragPipelineTests.ResolveGridFrame_QuantisesToTheMonitorWorkAreaOrigin_NotGlobalZero` |
| D3-2 | закрыт | `ClampHardWall_StopsExactlyOneGapShortOfTheNeighbour`, `ClampHardWall_TwoNeighboursApproachedFromBothSides_LeaveTheSameGap`, `FindFreeSlot_OriginTaken_ReturnsTheNextSlotWithAtLeastTheGap` |
| D3-3 | закрыт | `WindowManagerGridPlacementTests.ResolveGridAlignedBounds_WorkAreaOriginShifts*`, `…_UsesTheMonitorItIsGiven_NotTheOneOnTheModel` + F-6 (D3-7) |
| D3-4 | закрыт | `…_PanelAlreadyOnTheGrid_ReturnsExactlyItsSavedBounds`, `…_RepeatedResolution_IsIdempotent`, `…_LegacyFreeformPanel_ReturnsNull_SoTheUntouchedClampPathRuns` + весь существующий набор (2622) без правок |
| D3-5 | закрыт | `ResolveGridDrop_IsIdempotent`, `…_AfterADropAndPersist_RestoresTheSameBounds` |
| D3-6 | закрыт | `LayoutGesturePolicy`/`ChildWindow_PreviewKeyDown` не тронуты; пункт меню гейтится только `IsLayoutLocked` (`CanEnterEditLayout`-биндинг + ранний выход в обработчике; `MainWindowViewModelTests.AlignAllPanels_LayoutLocked_*`) |
| D3-7 | переоткрыт и закрыт заново fix-циклом 1 | Исходные `PanelDragPipelineTests.ResolveDropMonitor_*` (6 тестов) покрывали только чистую функцию в изоляции и не ловили C-1 (стейл `state.WorkArea` в `SnapToGrid` клампил панель обратно на стартовый монитор ДО того, как `UpdateMonitorDeviceIdOnDrop` успевал её увидеть на новом) — см. ADR-028 «Реализация P03 — fix-цикл 1». Закрыт заново композиционным `RetargetToDropMonitor_ComposedWithResolveGridDrop_KeepsThePanelOnTheMonitorItWasDroppedOn` (пиннует и старое число 1672, и новое `X > 1920`/`MonitorDeviceId = DISPLAY2`) + `RetargetToDropMonitor_*` (5 тестов). |
| D3-8 | закрыт | `LassoPanelGeometryTests` (14), включая точный кейс промпта `scale=1.5, (300,300,600,600) → (200,200)+(400,400)` |
| D3-9 | закрыт | `ResolveGridFrame_FreeMove_*` (3) + `ResolveGridDrop_QuantisesEvenWhenTheGestureEndedOffGrid` |
| D3-10 | закрыт | `ResolveGridFrame_RandomTrajectories_NeverCreateANewOverlap` (1000 кадров x 4 комбинации Alt/Edit Layout) + `PanelCollisionTests` 35–37 |

---

---

## 3. P04 — spans, native resizing, roll-up

### 3.1 Scope

Встроить `WorkspaceLayoutPolicy` в **существующие** `ResizeConstraintsFactory`/`ResizeGestureSession`/
`PanelResizeController`/`NativePanelResizeController`, не переписывая native sizing и **не изменяя**
`ResizeGeometryCalculator.Apply` (инвариант ADR-021 D-2/R6, подтверждён ADR-028 и Amendment MAJOR-4).
Добавить presets, collapsed-высоту, deferred-persistence spans. **Не трогать**
`PanelDragMoveBehavior`, `WindowManager`, `App.xaml.cs`, `MainWindowViewModel` — это объём P03.

### 3.2 Definition of Done (из `04-resize.md`)

| # | Критерий | Как проверяется |
|---|---|---|
| D4-1 | Нет зависшего resize в 30 повторных циклах | Существующие lifecycle-тесты (`ResizeGestureSessionTests`, `NativePanelResizeControllerTests`) + новый стресс-тест 30 циклов begin/update/end на обоих путях, включая `GridAligned`. |
| D4-2 | Изменение `IconSize` не меняет panel bounds | Unit: `GridAligned` + смена `IconSize` → `ResizeConstraintsFactory` не читает `IconSize` вовсе; `OnModelPropertyChanged` (`ChildWindow.xaml.cs:873-877`) — no-op. `LegacyFreeform` — старое поведение сохраняется (F-2 как задокументированное ограничение). |
| D4-3 | Resize каждой стороны/угла сохраняет противоположный край | Уже есть `SnapBounds_ResizeFromEachEdge_KeepsOppositeEdgeFixed_WhenAlreadyOnGrid` + `…_WhenOppositeEdgeOffGrid_AlignsItFirst` (P02 fix-cycle 1) — расширить до уровня `ResizeGestureSession.TryUpdate` по всем 8 `ResizeEdge` (`ResizeEdge.cs:9-19`). |
| D4-4 | Collapsed header не становится размером полноценной ячейки | Unit: `IsRolledUp` → `MinSpan = MinSpanCollapsed` (`WorkspaceGridMetrics.cs:73`, `(12,4)`), `SpansToBounds((·,4),8,8) = 24 DIP`?? — **нет**: `4*8 − 8 = 24`. См. §3.6 (важное расхождение, требует решения). |
| D4-5 | После рестарта bounds/span стабильны, native fallback работает | Round-trip: resize → spans → persist → restore → bounds совпадают; тот же тест для обоих путей (Thumb/native). |
| D4-6 | Golden-эквивалентность legacy↔native сохранена | `ResizeGeometryCalculatorApplyEquivalenceTests` (`tests/…/Unit/Interaction/Native/ResizeGeometryCalculatorApplyEquivalenceTests.cs:48`) — **не переписывается**, только дополняется `GridAligned`-кейсами (ADR-028: «пере-baseline'ен, не переписан»). |

### 3.3 Точки вставки: constraints

Файл `src/DesktopOrganizer.UI/Behaviors/ResizeConstraintsFactory.cs`, метод `BuildConstraints`
(`:31-62`). Это — **единственная** точка, где `GridAligned` входит в resize-конвейер (ADR-028
Amendment MAJOR-4 п.1).

| # | Точка | Изменение |
|---|---|---|
| P04-C1 | сигнатура `:31` | Добавить параметр `PanelGridState gridState` (или `bool isGridAligned` + `GridSpan minSpan` — предпочтительно целый `PanelGridState`, меньше параметров-примитивов). Оба вызывающих: `PanelResizeController.cs:133`, `NativePanelResizeController` (в `SizeMoveEntered`, рядом с `:300-321`). |
| P04-C2 | `:33-44` (icon-snap) | Новая ветка **перед** существующей: при `gridState.IsGridAligned` → `snapCellWidth = snapCellHeight = 0d` — то есть **free-ветка** `ResizeGeometryCalculator.Apply` (`:110-118`/`:124-132` — `else`-половины), **без** `AdaptiveGridLayout.SnapExtent`. Прямое требование ADR-028 Amendment MAJOR-4 п.1: система B ≠ система C. Существующая ветка `:35-44` для `LegacyFreeform` — **без изменений**. |
| P04-C3 | `:50-52` (`minWidth`) | При `IsGridAligned`: `minWidth = SpansToBounds(new GridSpan(minSpan.WidthUnits, 0), U, G).Width`, `minHeight` — симметрично по `minSpan.HeightUnits`; **не** `GridMetrics.MinPanelWidth(IconSize)` (`:51`) и **не** `GridMetrics.MinPanelSize` (`:52`/`:56`). Прямое следствие правила «IconSize не меняет panel bounds» (ADR-028 Amendment MAJOR-4 п.3). `LegacyFreeform` — без изменений. |
| P04-C4 | `:60-61` (`HorizontalChrome`/`VerticalChrome`) | **Не трогать** — владение P05 (§0.2). |

### 3.4 Точки вставки: сам жест

**`ResizeGeometryCalculator.Apply` (`ResizeGeometryCalculator.cs:77-163`) не изменяется ни на строку.**
Квантование к workspace-сетке — **второй шаг того же кадра**, после `Apply`.

| # | Точка | Изменение |
|---|---|---|
| P04-G1 | `ResizeGestureSession` `:131-146` `TryBegin` | Добавить поля `_gridState` (передаётся вместе с `constraints`, `:129`/`:141`) — сессия уже владеет всем «фиксированным на жест» состоянием, это её естественное место. `StartBounds` (`:105`), `AnchorRight`/`AnchorBottom` (`:111`/`:114`) — **не меняются**: corner anchoring (`HTLEFT` меняет left+width, `HTTOP` — top+height) уже реализован в `Apply` (`:141-160`) через `edge.AnchorsRight()`/`AnchorsBottom()` (`ResizeEdge.cs:83`/`:91`) и в `WorkspaceLayoutPolicy.SnapBounds`-resize теми же хелперами. Новый enum edge не заводится (ADR-028 Alternative 5). |
| P04-G2 | `ResizeGestureSession.TryUpdate` `:161-174`, строка `:170` | После `applied = ResizeGeometryCalculator.Apply(...)` — **три новые строки под одним `if (_gridState.IsGridAligned)`**:<br>1. `applied = WorkspaceLayoutPolicy.SnapBounds(applied, _gridState.Origin, edge, U, G, _gridState.MinSpan)` (`WorkspaceLayoutPolicy.cs:252` — Round к ближайшему span, anchor выравнивается, если был вне сетки);<br>2. `applied = WorkspaceLayoutPolicy.ClampResizeToSlots(applied, edge, _obstacles, U, G, _gridState.MinSpan)` (`:555`) — только если `_obstacles.Count > 0`;<br>3. `_appliedDeltaCount++` (`:171`) — без изменений. |
| P04-G3 | препятствия для resize | Сегодня resize **намеренно** работает с пустым списком препятствий (`PanelDragMoveBehavior.cs:436-441` — «resize anti-overlap explicitly out of scope», `PanelResizeController.cs:151-161` вызывает `_clearDragObstacles()`). ADR-028 закрывает этот backlog-пункт **только для `GridAligned`**. Значит: в `TryBegin`-пути для `GridAligned` нужен **свой** снимок препятствий (`IWindowManager.CollectOtherPanelBounds`, `WindowManager.cs:123-157`), один раз за жест, в новое поле сессии. Для `LegacyFreeform` список остаётся пустым → `ClampResizeToSlots` не вызывается → байт-в-байт. **Не «чинить»** `PanelDragMoveBehavior._dragObstacles` заполнением из resize (явный запрет в `:441`). |
| P04-G4 | `PanelResizeController.OnResizeDragDelta` `:183-249` | **Ни одной строки.** Вся GridAligned-логика — внутри `TryUpdate`. Записи `:207-210` остаются. |
| P04-G5 | `NativePanelResizeController.ProposeBounds` `:327-462` | **Ни одной строки** в геометрии: синтетическая дельта `:404-410` и `TryUpdate` `:410` уже проходят через ту же сессию. Единственное изменение — передача `gridState` в `BuildConstraints` при `SizeMoveEntered`. |
| P04-G6 | `EndResize` (Thumb `:386-419`, native `:553-593`) | Persist-путь `:399-403` / `:560-566`+`:596-600`: после `_snapToGrid()` (`:401`/`:598` — это `PanelDragMoveBehavior.SnapToGrid`, уже расширенный P03) записать spans в модель: `model.WidthSpanUnits/HeightSpanUnits = BoundsToSpans(finalSize, U, G)`. **Deferred persistence сохраняется как есть**: `deferPersist` (`NativePanelResizeController.cs:533-551`) не трогается — spans пишутся внутри `PersistAfterGesture` (`:596-600`), то есть уже после выхода из нативного модального цикла. |

**Почему квантование внутри `TryUpdate`, а не в двух контроллерах.** Единственная альтернатива —
продублировать три строки в `PanelResizeController.OnResizeDragDelta:197-210` и
`NativePanelResizeController.ProposeBounds:410-416`. Это ровно тот дубль, который уже один раз стоил
проекту дорого (`SnapWidthToGrid` в двух контроллерах, `ResizeSnapEdgePadding` в трёх местах — baseline
риск 10) и который golden-эквивалентность (`ResizeGeometryCalculatorApplyEquivalenceTests`) существует,
чтобы ловить. `TryUpdate` — единственная точка, общая для обоих путей и уже владеющая per-gesture
состоянием.

### 3.5 Presets S/M/L/XL

| # | Точка | Изменение |
|---|---|---|
| P04-P1 | `ChildWindow.Sizing.cs` (создан стадией S), `SizePresetMenu_Click` | `Enum.TryParse<PanelSizePreset>(menuItem.Tag as string, out var preset)`; ранний выход при `IsLayoutLocked`; `size = WorkspaceLayoutPolicy.PreferredSize(preset, U, G)` (`WorkspaceLayoutPolicy.cs:277`); позиция — текущий top-left, квантованный `SnapPosition`; `SafeClamp` grid-aware (`:374`); gap-aware `ClampAgainstObstacles`; запись через `PanelGeometryWriter`; `GeometryMode ← GridAligned` + spans; `SaveWindowsState()`. |
| P04-P2 | `ChildWindow.xaml` | Пункты уже созданы стадией S (§1.4) — P04 только реализует обработчик. |
| P04-P3 | чекмарк текущего preset | `IsCheckable`/`IsChecked` — **опционально**, вне DoD. Если делать: сравнение текущего `GridSpan` с `WorkspaceGridMetrics.SpanForPreset` (`:106`). |

Presets — ярлык поверх spans, **не** единственный разрешённый набор размеров: свободный resize остаётся
(ADR-028 «Presets `S`/`M`/`L`/`XL`», CRITIC_REVIEW п.3).

### 3.6 Roll-up и `CollapsedHeaderSpan` — ⚠ расхождение, требующее решения (OQ-4)

**Факт 1.** `WorkspaceGridMetrics.CollapsedHeaderSpan = (int)(PanelMetrics.HeaderHeight / UnitSize) = 4`
(`WorkspaceGridMetrics.cs:67`); `MinSpanCollapsed = (12, 4)` (`:73`).

**Факт 2.** Модель «cell + gap» (ADR-028): `Extent(n) = n·U − G`. При `U = G = 8`:
`Extent(4) = 4·8 − 8 = 24 DIP`, а не `32 DIP`.

**Факт 3.** Фактическая свёрнутая высота — `ChildWindow.TitleBarHeight = PanelMetrics.HeaderHeight = 32`
(`ChildWindow.xaml.cs:91`), она же `MinHeight` в XAML (`ChildWindow.xaml:25`), она же целевая высота
анимации `RollUpBehavior.ApplyRollUp` (`RollUpBehavior.cs:114-115`), она же высота панели-препятствия
(`WindowManager.cs:148-150`).

То есть `SpansToBounds(CollapsedHeaderSpan)` даёт **24**, а реальная свёрнутая высота — **32**. Это не
ошибка P02 (там `CollapsedHeaderSpan` задумывался как «ровно `HeaderHeight`»), а не замеченное следствие
gap-модели. Варианты:

- **A (рекомендуется).** `CollapsedHeaderSpan = 5` (`5·8 − 8 = 32 DIP` — точно `HeaderHeight`). Правка
  **одной константы** (`WorkspaceGridMetrics.cs:67`: `(int)(PanelMetrics.HeaderHeight / UnitSize) + 1`,
  с явным комментарием «+1 компенсирует gap в модели `n·U − G`»). Плюс: свёрнутая панель занимает ровно
  5 узлов и её нижний край **на сетке**; `RollUpBehavior` не трогается вообще.
- **B.** Не вычитать gap для collapsed-высоты (спецкейс в `SpansToBounds`). Отклонить: спецкейс в
  чистой функции, которая сегодня безусловна и покрыта тестами идемпотентности.
- **C.** Согласиться, что свёрнутая высота 32 не лежит на сетке (исключение). Отклонить: прямо
  противоречит ADR-028 («collapsed-высота обязана лежать на workspace-сетке без отдельного исключения»)
  и DoD D4-4.

**Роль `RollUpBehavior` в P04 (при варианте A — минимальная):**

| # | Точка | Изменение |
|---|---|---|
| P04-R1 | `ToggleRollUp` `:88-98`, строка `:93` | `_expandedHeight = _window.Height` — **не менять**. Дополнительно: при `GridAligned` перед сворачиванием зафиксировать `model.HeightSpanUnits` (развёрнутый span) — чтобы разворот восстанавливал **span**, а не сырой DIP. |
| P04-R2 | `ApplyRollUp` `:104-170` | Целевые высоты `:114-115` для `GridAligned`: `collapsed → SpansToBounds((·, CollapsedHeaderSpan)).Height`, `expanded → SpansToBounds((·, model.HeightSpanUnits)).Height`, вместо `_titleBarHeight`/`_expandedHeight`. Оба варианта проходят через ту же анимацию/generation-guard (`:120`, `:156-165`) — **логика анимации не меняется**. |
| P04-R3 | `ChildWindow.OnRenderSizeChanged` `:455-461` | Guard `if (_model.IsRolledUp) return;` (`:459`) — **не менять**: именно он защищает `Size` от записи промежуточной высоты анимации. Spans при этом тоже не перезаписываются — «`RollUpBehavior` не ломает span-высоту при развороте» выполняется по построению. |
| P04-R4 | `WindowManager.CollectOtherPanelBounds` `:147-150` | `ChildWindow.TitleBarHeight` как высота свёрнутой панели-препятствия — для `GridAligned` заменить на `SpansToBounds((·, CollapsedHeaderSpan)).Height` (при варианте A это то же число 32 — правка косметическая, но делает источник правды единым). |

### 3.7 `SnapWidthToGrid` при смене `IconSize`/`ViewMode` (F-2)

`ChildWindow.OnModelPropertyChanged` (`:873-877`) вызывает `_resizeController.SnapWidthToGrid()`
(`PanelResizeController.cs:342-358` / `NativePanelResizeController.cs:491-506`).

**Изменение:** в обе реализации добавляется ранний выход по `PanelGeometryMode` — первая строка после
получения `model` (`PanelResizeController.cs:344-345`, `NativePanelResizeController.cs:493-494`):
`if (model.GeometryMode == PanelGeometryMode.GridAligned) return;` — точнее, через
`PanelGridStateResolver` (инвариант В1). Это единственный способ выполнить правило ADR-028 «`GridAligned`
— правило абсолютно, без исключений» и DoD D4-2. `LegacyFreeform` — без изменений (F-2 остаётся
задокументированным ограничением старого режима).

### 3.8 `NativeSizingCoordinateMapper` — раздельное округление краёв

`ToPx` (`NativeSizingCoordinateMapper.cs:33-41`) округляет `Left`/`Right` **независимо**
(`Math.Round(Left*scale)`, `Math.Round((Left+Width)*scale)`), поэтому для панели, чей `Left` не выровнен
по целому физическому пикселю, итоговая ширина может отличаться от `Width*scale` на ±1 px. Для
`GridAligned` (края — целые кратные 8 DIP, на 110%/175% почти никогда не целые px) это системно.

**Решение для P04: принять как унаследованное ограничение, НЕ менять `ToPx`.** Обоснование:
(1) `ToPx` покрыт golden-тестами и обслуживает оба режима — изменение округления затронет
`LegacyFreeform` и сломает bit-for-bit baseline; (2) ошибка ≤ 1 физический px не видна и не
накапливается (каждый кадр `WM_SIZING` пересчитывается из `targetDip` заново,
`NativePanelResizeController.cs:381-410`, а не инкрементально); (3) span-представление делает её
неперсистентной — на диск уходит span, а не px. **Обязательная работа P04:** добавить в ADR-028
Amendment абзац, фиксирующий это решение явно (ADR-028 Amendment MINOR прямо требует от стадии 04
«явно решить» — решение = «принять», с этим обоснованием), и отдельный тест
`ToPx_GridAlignedEdges_WidthDeviatesByAtMostOnePixel_AcrossDpiScales` (110/125/150/175%), фиксирующий
границу ошибки, чтобы будущая регрессия была видна.

### 3.9 Риски рекурсии и потери capture (явное требование `04-resize.md`)

| Риск | Механизм | Митигация в этом плане |
|---|---|---|
| Рекурсивные Bounds updates | `OnRenderSizeChanged` (`:455-461`) → `model.Size` → (новое) запись spans → INPC `WidthSpanUnits` (`ChildWindowModel.cs:184`) → подписчик → `Window.Width` → `OnRenderSizeChanged` | §1.5 `PanelGeometryWriter` + **запрет** подписываться на `WidthSpanUnits`/`HeightSpanUnits` в `OnModelPropertyChanged`. Spans — производная, односторонний поток. |
| Потеря capture (Thumb) | `OnCaptureLost` `:261-284`, `RecoverStaleDrag` `:311-335` | **Не трогать.** Новая логика живёт строго внутри `TryUpdate`, которая уже защищена `_isActive`/`edge`-проверками (`:163`). Стресс-тест D4-1 (30 циклов) включает прерывания `CaptureLost`/`SourceChanged`. |
| Потеря capture (native) | `SizeMoveAborted` `:468`, `DpiChanged` `:471-479` | Не трогать; `deferPersist=true` сохраняется. |
| Анимация roll-up пинит `HeightProperty` | `RollUpBehavior` `:144-165` (H3) | P04-R2 меняет только значения `from`/`to`, не механику `BeginAnimation(...,null)`/generation-guard. |

### 3.10 Порядок коммитов P04

1. `refactor(ui): PanelGridState into resize constraints` — P04-C1 (сигнатура + проброс, поведение не
   меняется, обе ветки дают те же значения).
2. `feat(ui): GridAligned resize constraints (free branch, span minimums)` — P04-C2, P04-C3.
3. `feat(ui): workspace-grid quantisation inside ResizeGestureSession` — P04-G1, P04-G2.
4. `feat(ui): per-gesture obstacle snapshot for GridAligned resize` — P04-G3 + `ClampResizeToSlots`.
5. `feat(ui): persist spans after resize` — P04-G6.
6. `fix(ui): IconSize no longer re-snaps GridAligned width` — §3.7.
7. `fix(ui): collapsed header span` — §3.6 (после решения OQ-4), P04-R1…R4.
8. `feat(ui): S/M/L/XL size presets` — §3.5.
9. `docs(adr): ADR-028 amendment — ToPx edge rounding accepted` + тест — §3.8.
10. `test(ui): P04 matrix + golden equivalence GridAligned cases` — §5.

### 3.11 P04 — реализовано (факт)

Ветка `worktree-agent-ab88848231c36443c` (изолированный worktree поверх `bugfix/workspace-grid`
@ `d514fb4` — Stage S + P02 fix-2 + P05 fix-2). `dotnet build` — 0 ошибок; `dotnet test` — **2820**
зелёных (базис 2651 + 169 новых; ни один существующий тест не удалён, намеренно изменены ожидания в
двух — см. ниже).

**Коммиты (5, в порядке §3.10, сгруппированы там, где правки лежат в одних и тех же строках):**

| # | Коммит | Пункты плана |
|---|---|---|
| 1 | `feat(ui): GridAligned resize constraints and workspace-grid quantisation` | P04-C1…C3, P04-G1…G3, P04-G6, §3.7 |
| 2 | `fix(ui): collapsed header span lands on the workspace grid (OQ-4 = A)` | §3.6, P04-R1/R2 |
| 3 | `fix(ui): MinWidthSpanExpanded matches the real window/header minimum (120 DIP)` | §3 п. 6 (ADR-028 Вопрос 1) |
| 4 | `feat(ui): S/M/L/XL panel size presets` | §3.5, P04-P1/P2 |
| 5 | `test(resize): pin the accepted ToPx edge-rounding bound and the span round trip` | §3.8, часть D4-5 |

**Ключевые отличия факта от плана (все обоснованы в ADR-028 «Amendment — Реализация P04»):**

1. **`IPanelSizingGridHost` вместо ctor-параметров.** Новый `public interface` в `UI/Behaviors`,
   реализуемый самим `ChildWindow` в партиале `ChildWindow.Sizing.cs`; поведения получают его как
   `window as IPanelSizingGridHost`. Итог: P04 сделал **ноль** правок в `ChildWindow.xaml.cs` — участок
   `OnModelPropertyChanged`, который §4.2 выделяла P04, не понадобился вовсе, и риск конфликта с P03
   в этом файле равен нулю.
2. **F-2 (§3.7) реализован внутри `SnapWidthToGrid`** (обе копии), а не гейтом в
   `OnModelPropertyChanged`: правило принадлежит операции, поэтому его наследует любой будущий
   вызывающий (в т.ч. P06).
3. **Квантование опирается на свободную ветку `Apply`, а не на `SnapCell*`**: для `GridAligned`
   `SnapCellWidth/Height = 0`, а квантование делает второй шаг кадра в `TryUpdate`. Формулировка
   прошлого amendment ADR-022 («`SnapCell*` берутся из `UnitSize`») исправлена там же.
4. **Presets не меняют `PanelGeometryMode`** (вопреки P04-P1) — обоснование в ADR-028 п. 9; доступны в
   обоих режимах, свёрнутую панель игнорируют.
5. **P04-R4 не выполнен намеренно**: `WindowManager.CollectOtherPanelBounds` принадлежит P03, а после
   правки `CollapsedHeaderSpan` `ChildWindow.TitleBarHeight` и `SpansToBounds(CollapsedHeaderSpan).Height`
   — одно и то же число (32), т.е. правка была бы косметической.
6. **Anti-overlap для presets — два односторонних прохода** (`Right`, затем `Bottom`) вместо одного
   углового: угловой проход ужимал бы и высоту при чисто горизонтальном столкновении.

**Точка синхронизации с P03 (обязательна к проверке на интеграционном проходе §4.3).** `EndResize`
по-прежнему вызывает `_snapToGrid()` (= `PanelDragMoveBehavior.SnapToGrid`) ПЕРЕД записью span. В
worktree P04 этот метод ещё legacy-only: он снапит `Left`/`Top` к старой сетке `IAppSettings.GridSize`
(ADR-011) и поэтому **может сдвинуть `GridAligned`-панель с workspace-сетки по позиции** после resize.
Размер и span при этом корректны всегда (метод позиционный, размер не трогает — проверено), то есть
D4-5/D4-3 не нарушены. Ветку `GridAligned` в `SnapToGrid` добавляет P03-D9; после мержа обоих этот
пункт закрывается сам. Если P03-D9 почему-то не войдёт — минимальная замена на стороне P04: не вызывать
`_snapToGrid()` для `GridAligned` (одна строка в обоих `EndResize`), но делать это в P04 сейчас значило
бы править поведение, которым владеет P03.

**Новые/изменённые файлы (production):** `UI/Behaviors/IPanelSizingGridHost.cs` (новый),
`UI/Behaviors/PanelSizingGridSupport.cs` (новый), `UI/Models/PanelSizePresetCalculator.cs` (новый),
`UI/Views/ChildWindow.Sizing.cs`, `UI/Behaviors/ResizeConstraintsFactory.cs`,
`UI/Interaction/ResizeGestureSession.cs`, `UI/Behaviors/PanelResizeController.cs`,
`UI/Behaviors/NativePanelResizeController.cs`, `UI/Behaviors/RollUpBehavior.cs`,
`UI/Models/WorkspaceGridMetrics.cs`, `UI/Models/WorkspaceGridOptions.cs` (устаревший комментарий).

**Тесты (169 новых):** `ResizeConstraintsFactoryGridAlignedTests` (16),
`ResizeGestureSessionGridAlignedTests` (48), `NativePanelResizeControllerGridAlignedTests` (54),
`RollUpBehaviorGridAlignedTests` (5), `PanelSizePresetCalculatorTests` (16),
`ChildWindowSizingGridTests` (11, STA, живой `ChildWindow`), `NativeSizingCoordinateMapperGridAlignedTests`
(16), + 4 в `WorkspaceGridMetricsTests`. Общий headless-хост — `Helpers/FakePanelSizingGridHost`.

**Намеренно изменённые ожидания (2, старое/новое задокументировано у ассерта):**
`WorkspaceGridMetricsTests.CollapsedHeaderSpan_*` (было `span*U == HeaderHeight`, стало
`SpansToBounds(span).Height == HeaderHeight`) и `WorkspaceGridOptionsTests` (граница «gap ≥ наименьшего
span-extent» сместилась с 32 на 40 DIP; оба кейса теперь выводятся из константы, а не захардкожены).

**DoD §3.2:** D4-1 ✅ (30 циклов на уровне сессии и реального native-контроллера, с вкраплением
`CaptureLost`/`SourceChanged`), D4-2 ✅ (constraints не читают `IconSize`; STA-тест на живом окне),
D4-3 ✅ (8 сторон × {на сетке, вне сетки} × {сессия, native-контроллер}), D4-4 ✅ (`CollapsedHeaderSpan`
= 5, collapsed = 32 DIP = `MinHeight` окна), D4-5 — половина P04 ✅ (bounds→span→bounds тождество,
round-trip через px), вторая половина (restore) — за P03-W1, D4-6 ✅ (golden-набор не переписан,
`GridAligned`-кейсы добавлены рядом).


### 3.12 P04 — fix-cycle 1 (факт)

Изолированный worktree поверх `bugfix/workspace-grid` @ `ee4432a` (P03 + P04 + P06 смержены).
`dotnet build DesktopOrganizer.sln` — 0 ошибок; `dotnet test` — **3057** зелёных (базис 2978 + **79**
новых; ни один существующий тест не удалён и ни одно существующее ожидание не изменено). Обоснования —
в ADR-028 «Amendment 2026-09-22 — Реализация P04, fix-cycle 1».

| Finding | Статус | До → после (числа) |
|---|---|---|
| MAJOR-1 `ResizeGestureSession.QuantiseToWorkspaceGrid` не учитывал `WorkArea`; mouse-up `SafeClamp` ТРАНСЛИРОВАЛ панель | ✅ закрыт | WA 1920×1040, `Left=1600 W=280`, drag `Right +200`: было `Right = 2080` → на release `Left = 1440` (якорь прыгал на 160 DIP); стало `Right = 1920`, `W = 320` (span 41), `Left = 1600` неподвижен, `SafeClamp` — доказанный no-op на всех 8 сторонах |
| MAJOR-2 `PersistSpans` — `Ceiling` по неквантованным native-bounds, ratchet +1 unit/жест | ✅ закрыт | 150%, +1 px: было span 41 (320 DIP) при `model.Size = 312`, пять подряд commit давали 312 → 312.667 → 313.333 → 314 → 314.667 → 315.333; стало span 40 и bounds ровно 312 на 100/125/150/175% и ±1 px |
| MAJOR-3 `PanelGridStateResolver.Resolve` — ленивая миграция без `ClampToMinimum` | ✅ закрыт | панель 56 DIP: было `HeightSpanUnits = 8` (< `MinHeightSpanExpanded` 12) и перевывод на каждом resolve; стало `12` один раз, второй resolve не пишет ничего |
| UX P1 «ступенчатость» 8 DIP при resize | ✅ вариант B | `Alt` подавляет квантование кадра (результат байт-в-байт равен `ResizeGeometryCalculator.Apply`), commit квантует к ближайшему span; для `LegacyFreeform` модификатор не опрашивается вовсе |
| UX P0 пресет на `AutoContent`-панели | ✅ закрыт | `SizePresetMenu_Click` ставит `SizingMode = Manual` ДО `_geometryWriter.Apply`, поэтому debounce content-sizer (~300 мс) не отменяет высоту пресета |
| UX P1 MV-21 пункты «Size» на свёрнутой панели | ✅ закрыт | `IsEnabled` = `CanEnterEditLayout AND NOT Model.IsRolledUp` (новый `NotBooleanConverter` + `AllTrueMultiConverter` P06) |
| Preset `Small` (20×20 = 152×152) | ✅ задокументирован | span не изменён; ToolTip «fits ~2×1 icons at 32 px», обоснование в ADR-028 |
| MINOR `IsGridAligned` без fast path | ✅ закрыт | `IPanelSizingGridHost.ParticipatesInGrid` (default-реализация `true`) — `LegacyFreeform` больше не вызывает `GetMonitors()` на тик `IconSize` |
| MINOR `FakePanelSizingGridHost` — константа | ✅ закрыт | `MirrorRealResolver` прогоняет настоящий `PanelGridStateResolver`, включая побочный эффект миграции |
| MINOR `ResolveGridState` до `TryBegin` | ✅ задокументирован | оставлено (constraints нуждаются в state); после MAJOR-3 побочный эффект идемпотентен |
| NIT доки `CollapsedHeaderSpan, 4`; `ChildWindow.xaml` «currently no-op»; аллокация списка препятствий на клик | ✅ закрыты | 4 → 5; оба handler описаны как реализованные; `_presetObstacles` — переиспользуемый буфер |

**Изменённые файлы (production):** `UI/Models/WorkspaceLayoutPolicy.cs` (новый
`ClampResizeToWorkArea`; `ClampToMinimum`/`RoundBoundsToSpans` стали `public`),
`UI/Behaviors/PanelSizingGridSupport.cs` (новый `QuantiseResize`, переписанный `PersistSpans`),
`UI/Behaviors/IPanelSizingGridHost.cs` (аддитивный `ParticipatesInGrid` с default-реализацией),
`UI/Interaction/ResizeGestureSession.cs` (Alt-гейт + делегирование квантования),
`UI/Behaviors/PanelResizeController.cs`, `UI/Behaviors/NativePanelResizeController.cs`
(порядок commit, `_commitObstacles`, захват `outcome.Edge`), `UI/Models/PanelGridState.cs` (только
clamp в `Resolve`), `UI/Views/ChildWindow.Sizing.cs`, `UI/Views/ChildWindow.xaml` (только подменю
«Size»), `UI/Converters/NotBooleanConverter.cs` (новый).

**Отступление от зоны, зафиксировано сознательно.** Промпт ограничивал `WorkspaceLayoutPolicy.cs`
доками. Фактически туда добавлена одна чистая функция (`ClampResizeToWorkArea`) и повышена видимость
двух приватных помощников (`ClampToMinimum`, `RoundBoundsToSpans`). Альтернатива — продублировать
формулу nearest-rounding и cell+gap-инверсию в `PanelSizingGridSupport` — это ровно тот анти-паттерн
(«две копии `SnapWidthToGrid`», baseline риск 10), от которого §3.4 предостерегает. Файл не входит в
зоны параллельных P03/P06/P07 fix-агентов, поэтому риск конфликта минимален.

**Новые тесты (79):** `WorkspaceLayoutPolicyResizeClampTests` (28),
`ResizeGestureSessionFixCycleTests` (26), `PanelSizingGridSupportCommitTests` (17),
`PanelGridStateResolverMigrationTests` (5), `ChildWindowSizingGridTests` +3 (STA, живой `ChildWindow`).
Каждый MAJOR сначала доведён до красного на pre-fix-поведении (числа в таблице выше — фактический вывод
xUnit), затем закрыт.

---

## 4. Параллелизация: что можно вести двумя агентами, что — строго последовательно

### 4.1 Обязательная последовательность

```
[стадия S — один агент]
   → { P03 ∥ P04 }   (после зелёного билда стадии S)
      → интеграционный проход (§4.3)
         → code-quality-reviewer ∥ fences-ux-parity-reviewer
            → architecture-documenter (ADR-028 amendment)
```

Стадия S **не параллелится**: её артефакты (`PanelGridState`, `DesktopCoordinateSpace`,
`PanelGeometryWriter`, два партиала, два пункта меню, `IMonitorService`) — общий вход обоих этапов.

Промпт `04-resize.md` формально требует «не приступай до DoD этапа 03». **Отступление, которое этот план
предлагает сознательно:** после стадии S объёмы P03 и P04 не пересекаются по файлам (§4.2), а
единственная содержательная зависимость — вызов `_snapToGrid()` из `EndResize` (`PanelResizeController.cs:401`,
`NativePanelResizeController.cs:598`), то есть P04 **потребляет** результат P03-D9. Решение:
P04-G6 (коммит 5) — единственный шаг P04, который обязан ждать мержа P03-D9; всё остальное можно вести
параллельно. Если ревьюер не примет отступление — выполнять строго последовательно, план от этого не
меняется, только календарь.

### 4.2 Разграничение файлов (антиконфликтная таблица)

| Файл | Стадия S | P03 | P04 |
|---|---|---|---|
| `UI/Models/DesktopCoordinateSpace.cs` (новый) | ✍ создаёт | читает | читает |
| `UI/Models/PanelGridState.cs` (новый) | ✍ создаёт | читает | читает |
| `UI/Behaviors/PanelGeometryWriter.cs` (новый) | ✍ создаёт | читает | читает |
| `Services.Abstractions/Interfaces/IMonitorService.cs`, `Services/Tools/MonitorService.cs` | ✍ | — | — |
| `UI/Views/ChildWindow.xaml` (меню) | ✍ (оба пункта сразу) | — | — |
| `UI/Views/ChildWindow.xaml.cs` | ✍ (guard'ы в `OnLocationChanged`/`OnRenderSizeChanged`, ctor-проводка) | **только** ctor-аргумент `getModel` для `PanelDragMoveBehavior` (`:164-175`, `:198-209`) | **только** ранний выход в `OnModelPropertyChanged` (`:873-877`) |
| `UI/Views/ChildWindow.WorkspaceGrid.cs` (новый) | ✍ пустой | ✍ **владелец** | — |
| `UI/Views/ChildWindow.Sizing.cs` (новый) | ✍ пустой | — | ✍ **владелец** |
| `UI/Behaviors/PanelDragMoveBehavior.cs` | — | ✍ **владелец** | — |
| `UI/Services/WindowManager.cs`, `IWindowManager.cs` | — | ✍ **владелец** | — |
| `UI/App.xaml.cs`, `UI/ViewModels/MainWindowViewModel.cs`, `UI/Views/MainWindow.xaml` | — | ✍ **владелец** | — |
| `UI/Behaviors/ResizeConstraintsFactory.cs` | — | — | ✍ **владелец** |
| `UI/Interaction/ResizeGestureSession.cs` | — | — | ✍ **владелец** |
| `UI/Behaviors/PanelResizeController.cs`, `NativePanelResizeController.cs` | — | — | ✍ **владелец** |
| `UI/Behaviors/RollUpBehavior.cs` | — | — | ✍ **владелец** |
| `UI/Models/WorkspaceGridMetrics.cs` | — | — | ✍ (одна константа, §3.6) |
| `UI/Interaction/ResizeGeometryCalculator.cs`, `Native/NativeSizingCoordinateMapper.cs` | — | — | **никто** (инвариант) |
| `UI/Models/GridSnap.cs`, `PanelCollision.cs`, `SnapGuideCalculator.cs`, `WorkspaceLayoutPolicy.cs` | — | — | **никто** (P02 закрыт) |

**Две строки риска конфликта** (`ChildWindow.xaml.cs`): P03 меняет только конструктор в двух ветках
`nativeSizingEnabled` (`:164-175`, `:198-209`); P04 — только тело `OnModelPropertyChanged` (`:873-877`).
Разнос ~700 строк — git смержит автоматически. Если всё же конфликт: приоритет у P03 (его правка
структурная).

### 4.3 Интеграционный проход (после мержа обоих)

1. `dotnet build` + полный `dotnet test`.
2. Перекрёстный тест, который невозможно написать ни в одном этапе по отдельности:
   **drag → resize → drag → рестарт** на `GridAligned` даёт стабильные spans и позицию.
3. Проверка, что `_snapToGrid()` из resize-пути (`PanelResizeController.cs:401`) корректно видит
   `_gridState`, который `PanelDragMoveBehavior` заполняет **только в drag-жесте** — то есть при вызове
   из resize `_gridState` будет пустым. **Это реальная ловушка:** `SnapToGrid()` (`:444-464`) вызывается
   из обоих контроллеров через делегат (`ChildWindow.xaml.cs:184`, `:218`). Митигация: `SnapToGrid`
   обязан **сам** резолвить `PanelGridState`, если `_gridState` не заполнен (а не полагаться на
   поле жеста) — зафиксировано как требование к P03-D9.

---

## 5. Тест-матрица

### 5.1 Unit (pure, без STA)

| Область | Тесты |
|---|---|
| `DesktopCoordinateSpace` | identity по умолчанию; round-trip при `Offset != 0`; отрицательные координаты; `CalibrateOnce` идемпотентна |
| `PanelGridStateResolver` | 9 кейсов из §1.3 DoD стадии S |
| Drag-конвейер (чистая часть) | порядок `SnapPosition → guides → gap-aware clamp`; Alt подавляет только шаг 1; clamp никогда не пробивается (property-тест: для 1000 случайных траекторий результат не пересекается ни с одним препятствием более чем на `Eps`) |
| F-6 | центр в `Bounds` другого монитора → новый `DeviceName`; центр вне всех мониторов → `DeviceName` не меняется |
| F-7 | таблица `scale × (X,Y,W,H)` → ожидаемые DIP; `Math.Max(MinPanelSize,…)` после конверсии |
| Restore/`MonitorsChanged` | `WindowManagerTests`: `ClampIntoMonitor` не изменён (существующие тесты зелёные); новые — `GridAligned` при сдвиге `WorkArea`, двойной `OnMonitorsChanged` идемпотентен, монитор пропал → primary (старое поведение) |
| `ResizeConstraintsFactory` | `LegacyFreeform` → те же значения, что сегодня (характеризационный тест **до** правки); `GridAligned` → `SnapCell* == 0`, `MinWidth/MinHeight` из `MinSpan`, ни одного чтения `IconSize` |
| `ResizeGestureSession` | 8 `ResizeEdge` × {уже на сетке, вне сетки} → противоположный край; `ClampResizeToSlots` при соседе; min-span floor; 30 циклов begin/end |
| Roll-up | collapsed-высота == `HeaderHeight`; разворот восстанавливает span-высоту; `OnRenderSizeChanged`-guard не даёт записать промежуточную высоту |
| `ToPx` граница | §3.8 |

### 5.2 Unit с моками

- `Mock<IMonitorService>` — `GetMonitors()` возвращает сконструированные наборы: один монитор; два с
  отрицательным `Left = -1366`; монитор с верхним taskbar (`WorkArea.Top = 48`); монитор исчез;
  `GetSystemDpiScale()` = 1.0/1.25/1.5/1.75.
- `Mock<INativePanelSizingService>` + headless-конструктор `NativePanelResizeController`
  (`NativePanelResizeController.cs:179-223`, делегаты `getBounds`/`setWidth`/`scheduleDeferredPersist`,
  `:226-237`) — native-путь тестируется **без STA и без HWND**, как уже делает
  `NativePanelResizeControllerTests`.
- `Mock<IWindowManager>` — `CollectOtherPanelBounds` заполняет буфер заданными прямоугольниками.

### 5.3 STA-тесты (`tests/DesktopOrganizer.Tests/Helpers/StaTestRunner.cs:29`)

Только там, где нужен живой `Window`/`UIElement`:

- `PanelGeometryWriter` — рекурсия: `Apply` → `OnRenderSizeChanged` → повторный вход подавлен.
- `RollUpBehavior` — анимация + generation-guard при span-высотах (расширение
  `RollUpBehaviorMotionTests`, `MotionState.AnimationsEnabled = false` — детерминированная ветка
  `RollUpBehavior.cs:129-134`).
- Контекстное меню: `AlignToGridMenu`/`SizePresetMenu` `IsEnabled` при `IsLayoutLocked`.

### 5.4 Golden / equivalence

- `ResizeGeometryCalculatorApplyEquivalenceTests` (`:48`) — **дополняется**, не переписывается:
  те же сценарии с `SnapCellWidth/Height = 0` (GridAligned-ветка) должны давать идентичный результат на
  Thumb- и native-путях.
- Характеризационные тесты `LegacyFreeform` — снять **до** первого коммита P03/P04
  (`ResizeGeometryCharacterizationTests` как образец) и держать зелёными весь этап: это машинная
  проверка «байт-в-байт» из DoD.
- `GoldenMigrationTests`/`v5-full.json` — не трогаются (persistence-контракт закрыт P02b).

### 5.5 Ожидаемый счётчик

Базис 2359 → после стадии S ≈ +40, P03 ≈ +90, P04 ≈ +90. Ни один существующий тест не удаляется; тест,
чьё ожидание меняется намеренно (только `CollapsedHeaderSpan`, §3.6 вариант A), правится на месте с
комментарием «старое/новое поведение» — конвенция P02 fix-cycle 1.

---

## 6. Риски и rollback

| # | Риск | Вероятность | Rollback / флаг |
|---|---|---|---|
| R-1 | `Offset != (0,0)` на реальном WorkerW → `GridAligned` панели сдвинуты на весь виртуальный экран | средняя | `DesktopCoordinateSpace.Offset` остаётся `(0,0)` (не калибруем) → поведение = сегодняшнее. Откат = не вызывать `CalibrateOnce`. Один call-site. |
| R-2 | `U = 8`/presets визуально неудачны | средняя | Одна константа `WorkspaceGridMetrics.UnitSize` (`:24`) + таблица `DefaultPresetSpans` (`:86`). ADR-028 уже зафиксировал эту цену. |
| R-3 | `GridAligned` по умолчанию для новых панелей (`Settings.settings`, `DefaultPanelGeometryMode = "GridAligned"`) окажется неприятным | низкая | **Существующий** флаг: переключить дефолт в `Settings.settings` обратно на `"LegacyFreeform"` — одна строка, без правки кода. Существующие панели не затронуты никогда. |
| R-4 | gap-aware clamp ощущается «липким» при drag | средняя | `Gap` вынесен в `WorkspaceGridOptions` (`WorkspaceGridOptions.cs`); отключение = `Gap = 0` → gap-aware перегрузка делегирует в gap-less (`PanelCollision.cs:175-176`) без изменения кода. |
| R-5 | `ClampResizeToSlots` даёт дрожание у границы соседа | средняя | Шаг 2 в `TryUpdate` (P04-G2) снимается одной строкой; span-квантование (шаг 1) остаётся. |
| R-6 | Рекурсия Bounds → зависание UI | низкая (митигировано §1.5) | `PanelGeometryWriter.IsApplying` — guard; при откате убирается вместе с записью spans в `OnRenderSizeChanged`-пути. |
| R-7 | Регрессия `LegacyFreeform` | низкая | Характеризационные тесты §5.4 — падение видно в CI до мержа. |
| R-8 | Конфликт мержа P03/P04 в `ChildWindow.xaml.cs` | низкая | §4.2: два участка, разнесённые на ~700 строк, + партиалы. |
| R-9 | `WM_SETTINGCHANGE` спамит `OnMonitorsChanged` (тема/обои) и P03-W3 начинает двигать панели | средняя | Идемпотентность P03-W3 доказана тестом «дважды подряд → 0 изменений»; при проблеме — ранний выход «конфигурация мониторов не изменилась» (сравнение снимка), аддитивно. |

**Общий принцип отката:** ни одно изменение P03/P04 не трогает `LegacyFreeform`-ветку, поэтому
максимальный откат = «все панели → `LegacyFreeform`» (одна настройка + `GeometryMode` в JSON), и
приложение возвращается к поведению до пакета без миграции данных.

---

## 7. Manual verify (Win10/Win11) — обязательно, вне CI

| # | Что | Закрывает |
|---|---|---|
| MV-1 | Монитор с отрицательными координатами (вторичный слева/сверху): создать `GridAligned`-панель, прочитать `Window.Left` и `GetWindowRect`, сверить. **Блокирующий gate** перед включением `CalibrateOnce`. | ADR-028 Вопрос 2 / §1.1 / R-1 |
| MV-2 | Монитор с top- или left-докованным taskbar: первая grid-линия `GridAligned`-панели не под taskbar. | ADR-028 «Origin» |
| MV-3 | **Fix-цикл 1: сценарий стал реальным, не только теоретическим** (C-1 закрыт). Drag с DISPLAY1 на DISPLAY2, отпустить → панель остаётся примерно там, где её отпустили (НЕ телепортирует обратно на DISPLAY1 из-за стейл `state.WorkArea`), `MonitorDeviceId = DISPLAY2` сразу после drop; затем сменить тему (`WM_SETTINGCHANGE`), перезапустить → панель по-прежнему на DISPLAY2. | D3-3/D3-7 (F-6) |
| MV-4 | Alt во время drag: «сетка → свободно → обратно на сетке при отпускании» ощущается как единый жест, без рывка. | D3-9 / ADR-028 п.5 manual verify |
| MV-5 | «Align to workspace grid» на панели, пересекающейся с соседом: не телепортирует, останавливается предсказуемо. | ADR-028 п.6 |
| MV-6 | Две соседние `GridAligned`-панели, растянуть одну в сторону другой: останов у границы слотов, без дрожания. | D4-3 / ADR-028 п.7 |
| MV-7 | 30 циклов resize каждой из 8 сторон/углов на обоих путях (`NativePanelSizing` вкл/выкл) — нет зависшего состояния. | D4-1 |
| MV-8 | Смена `IconSize` слайдером на `GridAligned`-панели → bounds не дрогнули; на `LegacyFreeform` → ведут себя ровно как сегодня. | D4-2 / ADR-028 п.8 |
| MV-9 | Roll-up/roll-down `GridAligned`-панели: свёрнутая высота == высота заголовка, разворот возвращает ту же span-высоту. | D4-4 |
| MV-10 | Лассо на 125%/150% → панель точно под нарисованной рамкой. | D3-8 (F-7) |
| MV-11 | DPI 100/125/150/175%: `GridAligned` панель не «плывёт» по ширине на ±1 px между кадрами resize. | §3.8 |
| MV-12 | Рестарт Explorer (WorkerW пересоздан) при `GridAligned`-панелях — поведение не хуже сегодняшнего. | baseline риск 16 |
| MV-13 | **P03.** «Align to workspace grid» в контекстном меню: галка ставится → панель выравнивается; снять галку → режим `LegacyFreeform`, панель НЕ двигается; повторный вход в меню показывает актуальное состояние галки (биндинг `IsChecked` пережил клик). | OQ-3 = B / §2.5 |
| MV-14 | **P03.** «Align all to grid» в окне настроек: диалог подтверждения; Отмена → ничего не изменилось; ОК → панели не наезжают друг на друга, зазоры визуально одинаковы. | §2.5 |
| MV-15 | **P03.** Drag `GridAligned`-панели ВДОЛЬ соседа, стоящего ближе чем `G` по перпендикулярной оси (например, чуть выше): движение вбок свободно, не «залипает». | carry-over fix, метрики §8.5 |
| MV-16 | **P03.** Drag свёрнутой (roll-up) `GridAligned`-панели, затем рестарт: панель разворачивается в ПРЕЖНЮЮ высоту (span не выведен из высоты заголовка). | ADR-028 «Реализация P03» п. 4 |
| MV-17 | **P03.** «Add panel» пять раз подряд при `DefaultPanelGeometryMode = GridAligned`: пять панелей встают рядом, а не стопкой в углу. | P03-L3 / baseline M8 |
| MV-18 | **P03.** Панель на неактивной странице (никогда не измерена) при «Align all»: не схлопывается до минимального span'а, просто пропускается. | `TryAlign` guard |
| MV-19 | **(P04)** «Size → S/M/L/XL» на `GridAligned`-панели: размер ровно пресетный, верхний левый угол не «убегает»; на `LegacyFreeform` — меняется только размер, галка «Align to workspace grid» НЕ появляется. | §3.5 / ADR-028 п. 9 |
| MV-20 | **(P04)** «Size → Extra large» на панели, у которой справа сосед: остановка ровно в одном gap'е от соседа, высота пресета при этом не уменьшилась. | §3.5 / D4-3 |
| MV-21 | **(P04, fix-cycle 1 — реализовано)** Пункты «Size» на СВЁРНУТОЙ панели: подменю ВИЗУАЛЬНО дизейблено (`CanEnterEditLayout AND NOT IsRolledUp`); развернуть панель — пункт сразу становится активным без переоткрытия меню. | §3.5 / ADR-028 fix-cycle 1 |
| MV-22 | **(P04)** ESC во время НАТИВНОГО resize `GridAligned`-панели: геометрия остаётся там, где её видно (R-8), и после рестарта панель того же размера (span сохранён). | ADR-022 R-8 / §3.4 |
| MV-23 | **(P04)** Панель на сетке + слайдер `IconSize` от 16 до 96: ширина/высота окна не дрогнули ни на пиксель (на `LegacyFreeform` — ведут себя как раньше). | D4-2 / §3.7 |
| MV-24 | **P03 fix-цикл 1 (M-2/M-3).** «Align all» на многостраничном layout: панели ДРУГИХ страниц не двигаются и не участвуют (область — только текущая страница). Отдельно: повторный `WM_SETTINGCHANGE` (смена темы) без реального изменения монитора/work area, дважды подряд — 0 дополнительных записей на диск, `Width`/`Height` панелей не дрогнули ни на DIP. | M-2, M-3 |
| MV-25 | **(P04 fix-1)** `GridAligned`-панель у ПРАВОГО края экрана: тянуть правый край далеко за границу монитора. Панель останавливается ровно у края work area; на отпускании ЛЕВЫЙ край не сдвигается ни на пиксель. Повторить для всех 8 сторон/углов у соответствующей стены, в т.ч. у top/left-докованного taskbar. | MAJOR-1 / D4-3 |
| MV-26 | **(P04 fix-1)** DPI 125/150/175%: 10 подряд resize одной панели (по одному «шагу» туда-обратно) — ширина/высота возвращаются к ТОМУ ЖЕ числу, панель не «раздувается» на 8 DIP за жест; после рестарта размер тот же. | MAJOR-2 / D4-5 / MV-11 |
| MV-27 | **(P04 fix-1)** `Alt` во время resize `GridAligned`-панели: край идёт за курсором без ступенек 8 DIP; отпустить кнопку мыши (с зажатым `Alt` и без) — панель ВСЕГДА встаёт на сетку. Проверить оба пути (`NativePanelSizing` вкл/выкл) и что `Alt` не меняет галку «Align to workspace grid». | ADR-028 «Модификатор свободного движения» (fix-cycle 1) |
| MV-28 | **(P04 fix-1)** Панель с «Auto-size to content» ВКЛ → «Size → Large»: галка «Auto-size to content» снимается, высота пресета держится дольше 1 с (то есть после срабатывания 300-мс debounce content-sizer). | UX P0 / ADR-029 Решение 7 |
| MV-29 | **(P04 fix-1)** Документ со старой панелью ниже 88 DIP (или восстановление под меньшим разрешением): после запуска панель одна раз подрастает до минимума, span сохраняется, при следующих открытиях меню/resize повторного «подскока» нет. | MAJOR-3 |

---

## 8. Открытые вопросы (требуют решения оркестратора до/во время реализации)

**OQ-1 — калибровать ли `DesktopCoordinateSpace` в этом пакете. — РЕШЕНО (2026-09-22, оркестратор): A.**
- **A (выбрано, реализовано стадией S).** Ввести `DesktopCoordinateSpace` с `Offset = (0,0)` и **без**
  вызова `CalibrateOnce`; добавить `IMonitorService.GetWindowRectDip` и включить калибровку отдельным
  коммитом после MV-1. Риск нулевой, структура готова. **Факт:** оба типа существуют
  (`DesktopCoordinateSpace.cs`, `IMonitorService.GetWindowRectDip`), `CalibrateOnce` нигде не вызывается
  из рантайм-кода — см. §1.7.
- **B.** Сразу калибровать. Отклонено: без MV-1 неизвестно, что именно сравнивать, и калибровка может
  сама внести сдвиг там, где его не было.
- **C.** Не вводить тип вообще, считать пространства равными. Отклонено: это вариант B из ADR-028
  Вопроса 2, уже отклонённый.

**OQ-2 — обновлять ли `MonitorDeviceId` (F-6) для `LegacyFreeform` тоже. — РЕШЕНО (2026-09-22,
оркестратор): A.**
- **A (выбрано, обосновано в §2.3).** Да, в обоих режимах — это фикс бага, не изменение геометрии.
  Реализация — задача P03 (P03-D10); стадия S подготовила только фундамент
  (`PanelGridState.MonitorDeviceId` как read-only output резолвера — сама запись в модель делается через
  `PanelGeometryWriter.Apply(..., monitorDeviceId: ...)`, которую P03-D10 вызовет).
- **B.** Только для `GridAligned`. Строго соблюдает «байт-в-байт», но оставляет DoD D3-3 невыполненным
  для большинства существующих панелей.

**OQ-3 — как пользователь снимает `GridAligned` с панели. — РЕШЕНО (2026-09-22, оркестратор): B.**
- **B (выбрано).** Пункт меню «Align to workspace grid» — `IsCheckable`; checked = `GridAligned` (при
  включении — выравнивание, реализация P03); снятие галки → `GeometryMode = LegacyFreeform`, spans
  очищаются, текущие `Position`/`Size` сохраняются как есть (реализация клика — P03, стадия S
  реализовала только `IsCheckable="True"` + `IsChecked` binding, см. §1.7 п.4; сам `Click`-обработчик
  пока no-op).
- **A.** Вне объёма P03/P04, закрывается в окне настроек панели (P07/P08). Отклонено оркестратором в
  пользу B — дешевле и не требует нового UI-поверхности.

**OQ-4 — `CollapsedHeaderSpan` = 4 или 5 (§3.6). — РЕШЕНО (2026-09-22, оркестратор): A (5); РЕАЛИЗОВАНО
в P04** (коммит 2, §3.11; в коде записано как `(PanelMetrics.HeaderHeight + DefaultGap) / UnitSize` —
точная инверсия `Extent(n) = n·U − G`, а не «+1»). Исторический контекст (актуальный на момент стадии S)
сохранён ниже как есть. `WorkspaceGridMetrics.cs` в момент выполнения стадии S правил параллельный
агент (P02 fix-цикл 2) — стадия S **не тронула** этот файл. Правка константы
(`CollapsedHeaderSpan = (int)(PanelMetrics.HeaderHeight / UnitSize) + 1` → даёт `5`, с комментарием
«+1 компенсирует gap в модели `n·U − G`») и обновление `WorkspaceGridMetricsTests` — задача **P04**
(§3.6/§3.10 коммит 7), выполняется ПОСЛЕ мержа P02 fix-цикла 2. Тесты стадии S ссылаются на
`WorkspaceGridMetrics.CollapsedHeaderSpan`/`MinSpanCollapsed` символически, не жёстко на `4`/`5`, так что
переживут эту будущую правку без изменений.

**OQ-6 — позиция `GridAligned`-панели: абсолютная точка или индекс слота. — РЕШЕНО (2026-09-22,
оркестратор, fix-цикл 1): A.**
ADR-028 описывает позицию как «spans от origin» (панель едет вместе с origin при сдвиге work area,
сохраняя индекс слота), но схема v5 хранит spans только для РАЗМЕРА, а позиция — абсолютный DIP; прежний
origin нигде не записан, поэтому «логический слот» из восстановленного документа не вычислим. P03
реализовал формулу плана §2.4 (`SnapPosition(model.Position, newOrigin, U)` — абсолютная позиция,
ре-квантованная к новому origin, сдвиг <= `U/2`), что лучше всего удовлетворяет DoD «нет jump».
- **A (выбрано).** Оставить как есть (текущая реализация). Цена: при появлении taskbar'а панели не
  «едут» вместе с сеткой, а лишь ре-квантуются — но ре-квантование уже доказанно идемпотентно и сдвигает
  панель не более чем на `U/2` (а при сдвиге work area на ЦЕЛОЕ число units — например, простое
  изменение высоты taskbar — не двигает панель вовсе, см.
  `ResolveGridAlignedBounds_WorkAreaOriginShiftsByWholeUnits_PanelDoesNotMove`), то есть визуально
  практически неотличимо от «настоящего» слота почти во всех реальных сценариях.
- **B.** Добавить в схему persisted `ColSlot`/`RowSlot` (зона P02b/persistence) и восстанавливать позицию
  как `SlotToPosition`. Отклонено: новая миграция схемы + новый владелец (не P03) ради устранения
  под-`U/2` расхождения в редком случае (сдвиг work area НЕ на целое число units) — цена/выгода не
  оправданы в рамках этого fix-цикла. Остаётся доступным как будущая работа, если manual verify (MV-1/
  MV-2) когда-либо покажет, что расхождение всё же заметно на практике.

Полная запись решения — `adr/ADR-028-workspace-grid.md` «Реализация P03 — fix-цикл 1», раздел OQ-6.

**OQ-5 — отступление от «P04 не начинать до DoD P03» (§4.1). — РЕШЕНО (2026-09-22, оркестратор):
разрешено.** P03 и P04 стартуют параллельно сразу после стадии S (этот проход); единственная точка
синхронизации остаётся P04-G6 ждёт мержа P03-D9 (§4.1). Артефакты стадии S (см. §1.7) — завершённые,
покрытые тестами, `dotnet build`/`dotnet test` зелёные — оба этапа могут кодировать против них немедленно.

---

## 9. Обновления документации по завершении

- `adr/ADR-028-workspace-grid.md`: новый раздел «Amendment — P03/P04 integration» —
  (1) `DesktopCoordinateSpace` как изоляция Вопроса 2; (2) решение по `ToPx` (§3.8) — закрывает
  требование Amendment MINOR; (3) решение OQ-2/OQ-4; (4) отказ от tray-пункта (§2.5); (5) перевод
  статуса `Proposed → Accepted` **только** после MV-1/MV-2 (ADR-028 сам называет их блокирующими).
- `docs/PLANS/workspace-grid-migration.md` §10.5: пометить открытый риск закрытым (исход (a)/(b), §0.1).
- `docs/ROADMAP.md`: отметить P03/P04.
- Этот файл: раздел «Test log» и «Windows manual results» — по факту выполнения (требование
  `AGENT_CONTRACT.md`).
