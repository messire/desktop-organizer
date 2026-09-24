# Workspace Grid — P02 чистая геометрия (`WorkspaceGridMetrics`/`WorkspaceLayoutPolicy`)

**Этап:** P02 пакета [`docs/fix-prompts/`](../fix-prompts/README.md) · **Ветка:** `bugfix/workspace-grid`
**Статус:** реализовано (чистая математика; runtime-подключение — стадии 03/04, не этот проход) · **Дата:** 2026-09-22

Опирается на [ADR-028](../../adr/ADR-028-workspace-grid.md) (System B — workspace panel grid), таблицу
«invariants/API для этапа 02» и `docs/PLANS/workspace-grid-00-baseline.md` (F-1…F-8). Продолжает стиль
существующих pure-helper'ов `src/DesktopOrganizer.UI/Models/GridSnap.cs`, `PanelCollision.cs`,
`AdaptiveGridLayout.cs`, `src/DesktopOrganizer.UI/Interaction/ResizeGeometryCalculator.cs`.

---

## 1. Новые файлы

| Файл | Роль |
|---|---|
| `src/DesktopOrganizer.UI/Models/GridSpan.cs` | `readonly record struct GridSpan(int WidthUnits, int HeightUnits)` — позиция/размер в узлах сетки. |
| `src/DesktopOrganizer.UI/Models/PanelSizePreset.cs` | `enum { Small, Medium, Large, ExtraLarge }` — именованные ярлыки поверх spans (не единственный разрешённый набор размеров). |
| `src/DesktopOrganizer.UI/Models/WorkspaceGridMetrics.cs` | `static class` — константы `U`/`G`/минимумы/presets, `ValidateGap`/`ValidateSpan`. |
| `src/DesktopOrganizer.UI/Models/WorkspaceGridOptions.cs` | `sealed record` — конфигурируемый, валидируемый бандл параметров (для будущего `IAppSettings`-биндинга; сам `WorkspaceLayoutPolicy` его не требует). |
| `src/DesktopOrganizer.UI/Models/WorkspaceLayoutPolicy.cs` | `static class` — вся чистая геометрия: snap, spans↔bounds, resize-anchoring, presets, safe clamp. |

Тесты: `tests/DesktopOrganizer.Tests/Unit/Models/WorkspaceGridMetricsTests.cs` (18 методов),
`WorkspaceGridOptionsTests.cs` (11 методов), `WorkspaceLayoutPolicyTests.cs` (38 методов, многие —
`[Theory]` с несколькими рядами/сгенерированными диапазонами) — суммарно **183 test case** (с учётом
раскрытия `[Theory]`/`[MemberData]`).

## 2. Публичный контракт

Все методы `WorkspaceGridMetrics`/`WorkspaceLayoutPolicy` — `static`, без `DependencyObject`, без
P/Invoke, без побочных эффектов. Единственная WPF-зависимость — `System.Windows.Point`/`Size`/`Rect`
(WindowsBase), уже доступные тестовому проекту через `UseWPF=true`.

**Единицы измерения** (см. class remarks в `WorkspaceLayoutPolicy.cs`): все `double`-параметры/результаты
— **DIP**, никогда физические px и никогда `GridMetrics.CellSize`/`AdaptiveGridLayout.SnapExtent`
(система C, ADR-029). `GridSpan` — целые **`U`-unit'ы**. Округление к физическому device pixel полностью
делегировано существующему `NativeSizingCoordinateMapper` (ADR-022 D-3) — этот слой его не дублирует и не
знает про DPI-scale.

```csharp
namespace DesktopOrganizer.UI.Models;

public static class WorkspaceGridMetrics
{
    public const double UnitSize = 8d;               // U, DIP
    public const double DefaultGap = 8d;              // G, DIP
    public const int MinWidthSpanExpanded = 12;       // U-units — TODO(04): сверить с реальным header
    public const int MinHeightSpanExpanded = 12;      // U-units
    public const int MinWidthSpanCollapsed = MinWidthSpanExpanded;
    public const int CollapsedHeaderSpan = (int)(PanelMetrics.HeaderHeight / UnitSize); // = 4

    public static readonly GridSpan MinSpanExpanded;   // (12, 12)
    public static readonly GridSpan MinSpanCollapsed;  // (12, 4)
    public static readonly IReadOnlyDictionary<PanelSizePreset, GridSpan> DefaultPresetSpans;

    public static GridSpan SpanForPreset(PanelSizePreset preset);
    public static void ValidateGap(double unitSize, double gap);              // throws on invalid
    public static void ValidateSpan(GridSpan span, GridSpan minimum);         // throws on invalid
}

public sealed record WorkspaceGridOptions(
    double UnitSize, double Gap,
    int MinWidthSpanExpanded, int MinHeightSpanExpanded, int CollapsedHeaderSpan,
    IReadOnlyDictionary<PanelSizePreset, GridSpan> PresetSpans)
{
    public static WorkspaceGridOptions Default { get; } // built from WorkspaceGridMetrics constants
    public GridSpan MinSpanExpanded { get; }
    public GridSpan MinSpanCollapsed { get; }
    public void Validate();  // throws ArgumentException/ArgumentOutOfRangeException on any invariant violation
}

public static class WorkspaceLayoutPolicy
{
    // Position snap (per-axis + Point overload) — origin is the panel's monitor WorkArea top-left (ADR-028).
    public static double SnapCoordinate(double raw, double origin, double unitSize);
    public static Point SnapPosition(Point raw, Point origin, double unitSize);

    // Size <-> span, "cell + gap" model: Extent(n) = n*unitSize - gap.
    public static GridSpan BoundsToSpans(Size size, double unitSize, double gap);   // Ceiling, never loses space
    public static Size SpansToBounds(GridSpan span, double unitSize, double gap);

    // Whole-rectangle quantisation ("Align to workspace grid").
    public static Rect SnapBounds(Rect raw, Point origin, double unitSize, double gap, GridSpan minSpan);

    // Resize-gesture quantisation: keeps the edge OPPOSITE `edge` fixed (reuses UI.Interaction.ResizeEdge).
    public static Rect SnapBounds(Rect raw, ResizeEdge edge, double unitSize, double gap, GridSpan minSpan);

    public static Size PreferredSize(PanelSizePreset preset, double unitSize, double gap);

    // Positional clamp into a work area; NEVER shrinks Width/Height.
    public static Rect SafeClamp(Rect proposed, Rect workAreaDip);
}
```

Нет ни одной сигнатуры с параметром `IconSize`/`ViewMode` — правило ADR-028 «IconSize/ViewMode не меняют
panel bounds» выполняется по построению (структурно невозможно нарушить, а не по конфигурации/флагу).

## 3. Ключевые инварианты и как они проверены тестами

| Инвариант (ADR-028) | Реализация | Тест(ы) |
|---|---|---|
| Origin — work area монитора, работает при отрицательных координатах без спецкейса | `SnapCoordinate`/`SnapBounds` — обычное `origin + n*U`, никакого `if (origin < 0)` | `SnapCoordinate_NegativeOrigin_NoSpecialCasingForSign`, `SnapBounds_NegativeOriginMonitor_SnapsCorrectly`, `SafeClamp_NegativeOriginWorkArea_ClampsRelativeToNegativeOrigin` |
| `Snap(Snap(x)) == Snap(x)` (позиция) | Результат всегда кратен `U` относительно origin; повторное округление точного кратного — тождество | `SnapCoordinate_RepeatedApplication_IsIdempotent[_WithNegativeOrigin]`, `SnapPosition_RepeatedApplication_IsIdempotent_AcrossDpiScales` (100/125/150/200%) |
| `Ceiling(Ceiling(x)) == Ceiling(x)` (протяжённость) | `BoundsToSpans`/`SpansToBounds` round-trip | `SpansToBounds_BoundsToSpans_RoundTrip_IsIdempotent[_AcrossDpiScales]` |
| Round-to-even на границе (та же семантика, что `GridSnap.SnapAxis`) | `Math.Round` без явного `MidpointRounding` | `SnapCoordinate_HalfwayBetweenGridLines_RoundsToEven` |
| Непрерывная траектория — монотонность, отсутствие дребезга у ±0.5U | `Math.Round` — монотонная ступенчатая функция | `SnapCoordinate_Continuous{Increasing,Decreasing}Trajectory_IsMonotonic*`, `SnapCoordinate_NearHalfUnitBoundary_SmallPerturbationStaysWithinOneStep` |
| Resize с любого из 8 edges/corners — противоположный край неподвижен | `SnapBounds(Rect, ResizeEdge, ...)` использует `ResizeEdge.AnchorsRight()/AnchorsBottom()` (переиспользован, не продублирован) | `SnapBounds_ResizeFromEachEdge_KeepsOppositeEdgeFixed` (`[Theory]` по всем 8 `ResizeEdge`) |
| Min/Max: минимумы применяются, presets `S..XL` валидны | `ClampToMinimum` внутри обеих перегрузок `SnapBounds`; `DefaultPresetSpans` проверены против `MinSpanExpanded` | `SnapBounds_EnforcesMinimumSpan`, `SnapBounds_ResizeFromEachEdge_EnforcesMinimumSpan`, `DefaultPresetSpans_AllMeetMinimumExpandedSpan`, `PreferredSize_MatchesPresetSpanExtent` |
| Несколько панелей одного span → идентичная геометрия на одном workspace | `SpansToBounds` — чистая функция только от `span`/`unitSize`/`gap` | `SnapBounds_SeveralPanelsSameSpan_ProduceIdenticalGeometryOnSameWorkspace` |
| Равный зазор `G` между соседями (закрывает backlog ADR-017 для `GridAligned`) | Модель «cell + gap»: `n*U - G` | `AdjacentSlots_LeaveExactlyGapBetweenPanels` |
| `SafeClamp` — двигает только позицию, никогда не меняет размер; работает с отрицательным origin | `ClampAxis` пин к `areaStart`, если панель не помещается | `SafeClamp_PanelLargerThanWorkArea_PinsToStart_NeverShrinksSize`, `SafeClamp_NegativeOriginWorkArea_ClampsRelativeToNegativeOrigin`, `SafeClamp_NeverChangesSize` |
| `U`/`G`/presets/минимумы — конфигурируемые и валидируемые | `WorkspaceGridOptions` (record) + `WorkspaceGridMetrics.ValidateGap`/`ValidateSpan` | `WorkspaceGridOptionsTests` (11 тестов на валидные/невалидные комбинации) |
| Не подмешаны `GridMetrics.CellSize`/`AdaptiveGridLayout.SnapExtent` в workspace-размер | Ни один файл этого этапа не ссылается на `GridMetrics`/`AdaptiveGridLayout` (кроме `PanelMetrics.HeaderHeight` — общая, не icon-specific константа) | Верифицировано чтением кода (`grep` в файлах этапа не находит ссылок); архитектурный инвариант, не поведенческий тест |

## 4. Открытые вопросы (не блокируют этап 02, наследуются из ADR-028)

1. **`MinWidthSpanExpanded = 96 DIP` (12×U)** — placeholder, не сверен с реальной вёрсткой заголовка
   `PanelChrome` (ADR-028 Открытый вопрос 1, вариант A). Единственная точка правки — одна константа
   `WorkspaceGridMetrics.MinWidthSpanExpanded`; сверка — задача стадии 04 (когда заголовок реализуется).
2. **`U = 8 DIP`/presets `S/M/L/XL`** — структурно обоснованная, визуально не проверенная гипотеза
   (manual verify — release-gate P09, см. ADR-028 «Manual verify»).
3. **Пространство `Window.Left/Top` после `SetParent(WorkerW)` для монитора с отрицательными
   координатами** — не решается этим этапом (чистая математика не пишет в `Window.Left/Top`); остаётся
   обязательным manual-verify gate перед стадией 03 (ADR-028 Вопрос 2).

## 5. Test log

```
dotnet build DesktopOrganizer.sln -c Debug
  0 Warning(s) в файлах этого этапа (GridSpan.cs, PanelSizePreset.cs, WorkspaceGridMetrics.cs,
  WorkspaceGridOptions.cs, WorkspaceLayoutPolicy.cs, Workspace*Tests.cs)
  0 Error(s)

dotnet test DesktopOrganizer.sln -c Debug
  Passed!  - Failed: 0, Passed: 2112, Skipped: 0, Total: 2112, Duration: ~10 s
  (baseline P00: 1925 passed; +187 за счёт P01 enum-тестов + P02: 183 Workspace*-тестов)

dotnet test tests/DesktopOrganizer.Tests --filter "FullyQualifiedName~Workspace"
  Passed!  - Failed: 0, Passed: 183, Skipped: 0, Total: 183, Duration: ~0.1 s
```

Старые тесты не удалялись и не менялись; `AdaptiveGridLayoutBaselineTests` (P00 characterization) остаётся
зелёным без изменений.

## 6. Передача на следующий этап

Стадии 03 (drag/monitor tracking)/04 (resize/chrome) могут подключать `WorkspaceLayoutPolicy` к реальному
`ChildWindow`/`WindowManager`-коду напрямую — контракт (сигнатуры, единицы, инварианты) стабилен и покрыт
тестами. `PanelGeometryMode.GridAligned` per-panel persistence (schema v4→v5) — отдельный, уже
спроектированный план (`docs/PLANS/workspace-grid-migration.md`), не трогается этим этапом.

---

## 7. Fix cycle 1 (2026-09-22) — code-review findings MAJOR-1…7, MINOR, NIT

Полный нарратив решений — в [ADR-028, "Amendment 2026-09-22 — P02 review"](../../adr/ADR-028-workspace-grid.md#amendment-2026-09-22--p02-review-fix-cycle-1).
Здесь — таблица находка → статус и финальный публичный контракт после фикса (заменяет §2 выше).

### 7.1 Таблица находка → статус

| Finding | Статус | Суть фикса |
|---|---|---|
| MAJOR-1 (Ceiling без допуска → ratchet) | ✅ Fixed | `SpanCountForExtent`: `Ceiling(raw − 0.5/unitSize)`, клампится к `int` до cast |
| MAJOR-2 (`SafeClamp` даёт off-grid позицию) | ✅ Fixed | Новая перегрузка `SafeClamp(Rect, Rect, double unitSize, double gap)` — grid-aware, аддитивная |
| MAJOR-3 (resize `SnapBounds` без origin, asymmetric) | ✅ Fixed | Сигнатура `+ Point origin`; Round (не Ceiling); anchor выравнивается, если не на сетке |
| MAJOR-4 (расхождение с ADR для стадии 04) | ✅ Documented | ADR-028 Amendment, план для `ResizeConstraintsFactory`/`ResizeGeometryCalculator` (код не тронут) |
| MAJOR-5(a) (slot↔position API) | ✅ Fixed | `PositionToSlot`/`SlotToPosition`; `GridSpan.cs` doc исправлен |
| MAJOR-5(b) (gap-aware clamp к препятствиям) | ✅ Fixed | Аддитивная `PanelCollision.ClampAgainstObstacles(..., double gap)` |
| MAJOR-5(c) (anti-overlap resize по слотам) | ✅ Fixed | `WorkspaceLayoutPolicy.ClampResizeToSlots(...)` |
| MAJOR-6 (`PreferredSize` игнорирует `WorkspaceGridOptions`) | ✅ Fixed | Перегрузка `PreferredSize(PanelSizePreset, WorkspaceGridOptions)` |
| MAJOR-7 (тесты: тавтологии/слепые зоны) | ✅ Fixed | См. ADR amendment, раздел MAJOR-7 — 6 тестов исправлены/добавлены |
| MINOR (`Gap < U*minSpan`, null/incomplete `PresetSpans`) | ✅ Fixed | `WorkspaceGridOptions.Validate()` — 2 новые проверки |
| MINOR (`NativeSizingCoordinateMapper` независимое округление краёв) | ✅ Documented | ADR amendment, открытый вопрос для стадии 04; код mapper'а не тронут |
| NIT (`DefaultPresetSpans` мутабельный `Dictionary`) | ✅ Fixed | `FrozenDictionary` |
| NIT (типы исключений валидации) | ⚪ Не унифицировано намеренно | Существующее разделение `ArgumentOutOfRangeException` (диапазон одного параметра)/`ArgumentException` (реляционная проверка) — устоявшийся, уже протестированный паттерн в этом файле; новые проверки следуют тому же паттерну, но старые типы не тронуты, чтобы не сломать тесты, явно проверяющие конкретный тип исключения |

### 7.2 Финальный публичный контракт (заменяет черновик из §2)

```csharp
namespace DesktopOrganizer.UI.Models;

public static class WorkspaceGridMetrics
{
    public const double UnitSize = 8d;
    public const double DefaultGap = 8d;
    public const int MinWidthSpanExpanded = 12;
    public const int MinHeightSpanExpanded = 12;
    public const int MinWidthSpanCollapsed = MinWidthSpanExpanded;
    public const int CollapsedHeaderSpan = 4;

    public static readonly GridSpan MinSpanExpanded;
    public static readonly GridSpan MinSpanCollapsed;
    public static readonly FrozenDictionary<PanelSizePreset, GridSpan> DefaultPresetSpans; // fix-cycle 1: was IReadOnlyDictionary-over-Dictionary

    public static GridSpan SpanForPreset(PanelSizePreset preset);       // fix-cycle 1: ArgumentOutOfRangeException, not KeyNotFoundException
    public static void ValidateGap(double unitSize, double gap);
    public static void ValidateSpan(GridSpan span, GridSpan minimum);
}

public sealed record WorkspaceGridOptions(
    double UnitSize, double Gap,
    int MinWidthSpanExpanded, int MinHeightSpanExpanded, int CollapsedHeaderSpan,
    IReadOnlyDictionary<PanelSizePreset, GridSpan> PresetSpans)
{
    public static WorkspaceGridOptions Default { get; }
    public GridSpan MinSpanExpanded { get; }
    public GridSpan MinSpanCollapsed { get; }
    public void Validate();  // fix-cycle 1: + Gap-vs-smallest-span check, + null/incomplete PresetSpans check
}

public static class WorkspaceLayoutPolicy
{
    // Position snap — unchanged from P02 (Round/banker's, origin-relative).
    public static double SnapCoordinate(double raw, double origin, double unitSize);
    public static Point SnapPosition(Point raw, Point origin, double unitSize);

    // NEW fix-cycle 1, MAJOR-5(a): position <-> whole (column, row) slot indices.
    public static (int Col, int Row) PositionToSlot(Point raw, Point origin, double unitSize);
    public static Point SlotToPosition((int Col, int Row) slot, Point origin, double unitSize);

    // Size <-> span. BoundsToSpans: Ceiling+0.5 DIP tolerance (fix-cycle 1, MAJOR-1) -- for
    // Align-to-grid/Fit/migration. SpansToBounds unchanged.
    public static GridSpan BoundsToSpans(Size size, double unitSize, double gap);
    public static Size SpansToBounds(GridSpan span, double unitSize, double gap);

    // Whole-rectangle quantisation ("Align to workspace grid") -- unchanged signature, Ceiling+tolerance internally.
    public static Rect SnapBounds(Rect raw, Point origin, double unitSize, double gap, GridSpan minSpan);

    // CHANGED fix-cycle 1, MAJOR-3: + Point origin param; Round (nearest span), not Ceiling; anchor
    // edge aligns to the grid first if it wasn't already on it.
    public static Rect SnapBounds(Rect raw, Point origin, ResizeEdge edge, double unitSize, double gap, GridSpan minSpan);

    // NEW fix-cycle 1, MAJOR-5(c): shrinks a resize candidate by whole slots until clear of every
    // obstacle by `gap` DIP, or MinSpan is reached (residual overlap accepted at the floor).
    public static Rect ClampResizeToSlots(Rect candidate, ResizeEdge edge, IReadOnlyList<Rect> obstacles, double unitSize, double gap, GridSpan minSpan);

    public static Size PreferredSize(PanelSizePreset preset, double unitSize, double gap);
    public static Size PreferredSize(PanelSizePreset preset, WorkspaceGridOptions options); // NEW fix-cycle 1, MAJOR-6

    // Positional clamp; NEVER shrinks Width/Height.
    public static Rect SafeClamp(Rect proposed, Rect workAreaDip);                                   // unchanged, LegacyFreeform
    public static Rect SafeClamp(Rect proposed, Rect workAreaDip, double unitSize, double gap);       // NEW fix-cycle 1, MAJOR-2, grid-aware
}

// PanelCollision (existing type, additive-only change):
public static class PanelCollision
{
    public static bool Overlaps(Rect a, Rect b);
    public static Rect ClampAgainstObstacles(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles); // unchanged
    public static Rect ClampAgainstObstacles(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles, double gap); // NEW fix-cycle 1, MAJOR-5(b)
    public static bool CreatesNewOverlap(Rect before, Rect after, IReadOnlyList<Rect> obstacles);
}
```

**Обратная совместимость.** `SnapBounds(Rect, ResizeEdge, ...)` (5 параметров, без `origin`) — это была
единственная сигнатура, у которой ещё не было ни одного вызывающего в рантайм-коде (стадия 04 ещё не
реализована), поэтому сигнатура заменена (не добавлена перегрузкой) — правка затронула только тесты
этого же PR. Все остальные изменения этого fix-цикла — чисто аддитивные (новые перегрузки/новые методы),
существующие сигнатуры и их тесты не менялись.

### 7.3 Test log (fix cycle 1)

```
dotnet build DesktopOrganizer.sln -c Debug
  0 Warning(s), 0 Error(s)

dotnet test tests/DesktopOrganizer.Tests --filter "FullyQualifiedName~Workspace|FullyQualifiedName~PanelCollision" -c Debug
  Passed! - Failed: 0, Passed: 279, Skipped: 0, Total: 279, Duration: ~0.6 s

dotnet test DesktopOrganizer.sln -c Debug
  Passed! - Failed: 0, Passed: 2312, Skipped: 0, Total: 2312, Duration: ~12 s
  (fix-cycle 1 baseline: 2112 после P02 original + P05/P02b merges; +200 net за счёт fix-cycle 1's новых/
  расширенных Workspace*/PanelCollision тестов)
```

Старые тесты не удалялись; там, где находка требовала изменить УЖЕ ЗАФИКСИРОВАННОЕ ожидание теста
(из-за намеренного изменения контракта — MAJOR-1's tolerance, MAJOR-3's signature/anchoring rule), тест
обновлён на месте с явным комментарием, объясняющим старое/новое поведение (не удалён и не пропущен).

---

## 8. Fix cycle 2 (2026-09-22, ПОСЛЕДНИЙ, лимит итераций) — code-review findings MAJOR-A, MAJOR-B, MINOR-C/D/E, NIT-F/G

Повторное ревью чистой геометрии P02 после fix-cycle 1. Полный нарратив решений — в [ADR-028, "Amendment
2026-09-22 — P02 review (fix-cycle 2, короткая заметка)"](../../adr/ADR-028-workspace-grid.md). Как и
fix-cycle 1, весь объём — чистая математика в `PanelCollision`/`WorkspaceLayoutPolicy`
(`WorkspaceGridMetrics`/`WorkspaceGridOptions` не тронуты содержательно, кроме одной валидационной правки);
ничего не подключено к рантайму. Зона правок не выходила за `PanelCollision.cs`,
`WorkspaceLayoutPolicy.cs`, их тесты и эти два документа — как и требовалось заданием; ADR-017 не изменена
(её текст/семантика R1 не пересмотрены — fix-cycle 2 лишь исправляет БАГ в том, КАК gap-aware код применял
уже существующее правило R1, не меняя само правило).

### 8.1 Таблица находка → статус, с числами репро «до/после»

| Finding | Статус | Репро «до» | Результат «после» |
|---|---|---|---|
| MAJOR-A (`PanelCollision` gap-aware overload — R1 амнистирует против ИНФЛЕЙЧЕННОГО rect) | ✅ Fixed | obstacle X∈[150,250], panel X∈[45,145] (реальный зазор 5 DIP < gap=8), proposedLeft=200 → инфлейченный overlap с current = 3 DIP > `GhostEps`(2.0) → амнистия → panel.X=200, панель на 50 DIP внутри реального препятствия | Ghost-check и "behind"-guard теперь против РЕАЛЬНОГО rect, стена — арифметический сдвиг `o.X - gap - width`: тот же вызов даёт `result.X=45` (панель держится на месте, не притягивается назад к «честному» gap=8, но и не приближается дальше); панель, отодвигающаяся от той же позиции (`proposedLeft=-400`), свободна (`result.X=-400`) |
| MAJOR-B (`ClampResizeToSlots` — anchor-side/пред-существующее препятствие никогда не «очищается», ужимает до `minSpan`) | ✅ Fixed | candidate=(100,100,248,248), edge=Right (anchor=Left=100), obstacle=(50,100,100,248) (X∈[50,150], перекрывает anchor) → 20 итераций → (100,100,88,248), ширина уничтожена, всё ещё пересекается | Новый одноразовый фильтр `CanEverClear`/`FilterObstaclesResolvableByThisEdge` против ОРИГИНАЛЬНОГО anchor: обстакл не проходит фильтр → `result == candidate` (100,100,248,248) неизменно, для всех 8 `ResizeEdge`. Обратная ситуация (obstacle=(300,100,100,248), впереди по направлению движения) — ужимается ровно до `result.Right=292` (`obstacle.X(300) - gap(8)`), anchor (`Left=100`) не двигается |
| MINOR-C (`ClampResizeToSlots` floor-guard проверяет ось, которую edge не двигает) | ✅ Fixed | candidate=(0,0,248,24) (height span 4, свёрнутая панель), `minSpan=MinSpanExpanded` (12,12), edge=Right, obstacle=(252,0,100,24) (4 DIP внутри требуемого зазора 8) → старый guard: `shrunkHeight(4) < minSpan.HeightUnits(12)` → `true` на первой же итерации → тихий no-op, `result.Right` остаётся 248, всё ещё в пределах gap от препятствия | Guard теперь смотрит только на `widthMoves`/`heightMoves`: `result.Right=240` (ушла ровно на 1 слот), `result.Height=24` не тронута, несмотря на её span(4) < minSpan.HeightUnits(12) |
| MINOR-D (`SafeClamp(Rect,Rect)` не говорит, что это только `LegacyFreeform`) | ✅ Fixed | — | Переименована в `SafeClampFreeform(Rect,Rect)` (нет вызывающих в рантайме — безопасное переименование, не аддитивная перегрузка, тот же прецедент MAJOR-3 fix-cycle 1); все `<see cref>`/тесты обновлены |
| MINOR-E (`SnapBounds` resize-overload, `ClampResizeToSlots` не вызывают `ValidateGap`) | ✅ Fixed | `gap=5, unitSize=8` (не кратно) молча принимался обеими сигнатурами | `WorkspaceGridMetrics.ValidateGap(unitSize, gap)` добавлена в начало `SnapBounds(Rect,Point,double,double,GridSpan)`, `SnapBounds(Rect,Point,ResizeEdge,...)`, `ClampResizeToSlots`, и (расширение сверх буквального finding, для консистентности) `SafeClamp(Rect,Rect,double,double)` — все четыре теперь бросают `ArgumentException` на `gap=5,unitSize=8`; 4 новых теста |
| NIT-F (`gap≥0` не проверяется, только `IsFinite`) | ✅ Fixed | `gap=-100` в `BoundsToSpans`/`SpansToBounds`/`RoundBoundsToSpans` мог дать некорректный (в `SpansToBounds` — искусственно РАЗДУТЫЙ) результат вместо явного отказа | `safeGap = IsFinite(gap) ? Math.Max(0d, gap) : 0d` — консистентно с уже верной `OverlapsAnyWithGap`; выбран `Math.Max` (не `ArgumentOutOfRangeException`), чтобы сохранить давно принятый в этом файле контракт «эти pure-функции — no-op/clamp на вырожденном вводе, не throw» (`SnapCoordinate`, `AdaptiveGridLayout.RowCapacity`) — `ValidateGap` (throw) уже покрывает случай, когда caller ХОЧЕТ раннюю ошибку конфигурации (MINOR-E) |
| NIT-G (`ClampAxisToGrid` — overshoot ≤0.5 DIP при ширине не равной точному extent) | ✅ Fixed | Буквальное repro-число задания (width=250) математически НЕ воспроизводит баг (250 всегда округляет span ВВЕРХ — occupied-extent 256 > 250, что лишь усиливает консервативность, никогда не даёт overshoot); реальный repro — width=248.5 (внутри 0.5-DIP допуска MAJOR-1, span остаётся 32, occupied-extent=248 < 248.5) при area=1360 (slot 139 — точная граница без запаса на floor): старый span-based `ClampAxisToGrid` даёт `result.Right=1360.5`, на 0.5 DIP ЗА пределами area (1360) | `ClampAxisToGrid` теперь считает `maxSlot` от РЕАЛЬНОГО `proposed.Width`/`Height`, не от `SpansToBounds(span)`; `result.Right=1352.5 <= 1360`. Оба теста в наборе — «сохранён буквальный repro задания (250, теперь явно задокументировано как ложное срабатывание невозможно)» и «настоящий регрессионный repro (248.5)» |

**Отклонение от буквальных чисел задания.** Для NIT-G ширина `250 DIP`, предложенная в задании, была
математически проверена и НЕ воспроизводит дефект (см. таблицу и doc-comment
`SafeClamp_GridAware_NeverOvershootsWorkArea_WithWidth250`) — `250` при `U=8,G=8` всегда квантуется
`Ceiling`'ом ВВЕРХ (span 33, occupied-extent 256 DIP), что делает старую формулу СТРОЖЕ, а не мягче
фактической ширины. Тест на это число сохранён как регрессионный guard (описан и обоснован в тесте), а
настоящий "падающий-до-фикса" repro использует `248.5 DIP` — единственное значение внутри MAJOR-1's
0.5-DIP допуска, которое действительно провоцирует переполнение area. Для остальных находок числа
задания воспроизводятся дословно (см. таблицу).

### 8.2 Изменения публичного контракта

Все изменения **аддитивны или являются переименованием без вызывающих** (см. MINOR-D) — ни одна
существующая сигнатура с известным вызывающим не изменена.

```csharp
// PanelCollision — оба публичных метода теперь делегируют в один internal core (было: gap-overload
// инфлейтил Rect[] и звал zero-gap overload напрямую):
public static Rect ClampAgainstObstacles(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles);              // unchanged signature, now zero-allocation core with gap=0
public static Rect ClampAgainstObstacles(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles, double gap);   // unchanged signature; MAJOR-A fixes internals
// private static Rect ClampAgainstObstaclesCore(Rect, double, double, IReadOnlyList<Rect>, double gap);  // NEW, fix-cycle 2
// private static Rect Inflate(Rect, double gap);                                                          // NEW, fix-cycle 2 (replaces the old Rect[] allocation)

// WorkspaceLayoutPolicy
public static Rect SafeClampFreeform(Rect proposed, Rect workAreaDip);                 // RENAMED from SafeClamp(Rect,Rect) -- fix-cycle 2, MINOR-D (no runtime callers yet)
public static Rect SafeClamp(Rect proposed, Rect workAreaDip, double unitSize, double gap); // unchanged signature; NIT-G fixes ClampAxisToGrid internals + validates gap (MINOR-E)
public static Rect ClampResizeToSlots(Rect candidate, ResizeEdge edge, IReadOnlyList<Rect> obstacles, double unitSize, double gap, GridSpan minSpan); // unchanged signature; MAJOR-B/MINOR-C fix internals + validates gap (MINOR-E)
// private static IReadOnlyList<Rect> FilterObstaclesResolvableByThisEdge(Rect, ResizeEdge, IReadOnlyList<Rect>); // NEW, fix-cycle 2, MAJOR-B
// private static bool CanEverClear(Rect original, ResizeEdge edge, Rect obstacle);                              // NEW, fix-cycle 2, MAJOR-B
// private static double ClampAxisToGrid(double position, double origin, double extent, double unitSize, double areaExtent); // CHANGED signature (was: int spanUnits, double gap) -- private, no external callers
```

`WorkspaceGridMetrics.ValidateGap` — не изменена (уже существовала с fix-cycle 1); просто вызывается из
трёх дополнительных мест (MINOR-E).

### 8.3 Test log (fix cycle 2)

```
dotnet build DesktopOrganizer.sln -c Debug
  0 Error(s) (только пред-существующие StyleCop warnings, не связанные с этим проходом)

dotnet test tests/DesktopOrganizer.Tests --filter "FullyQualifiedName~Workspace|FullyQualifiedName~PanelCollision" -c Debug
  Passed! - Failed: 0, Passed: 300, Skipped: 0, Total: 300, Duration: ~0.3 s
  (fix-cycle 1 baseline: 279 -- +21 fix-cycle 2: 5 PanelCollision MAJOR-A tests + 16 WorkspaceLayoutPolicy
  tests [4 MINOR-E ArgumentException tests, 8 MAJOR-B anchor-side [Theory] cases, 1 MAJOR-B reverse case,
  1 MINOR-C collapsed-panel case, 2 NIT-G overshoot cases])

dotnet test DesktopOrganizer.sln -c Debug
  Passed! - Failed: 0, Passed: 2380, Skipped: 0, Total: 2380, Duration: ~12 s
```

Старые тесты не удалялись и не меняли своё численное ожидание (кроме переименования `SafeClamp` →
`SafeClampFreeform` в 6 существующих call site'ов — чисто механическая правка имени, ни одно числовое
ожидание/семантика теста не изменены).

### 8.4 Итог по зоне работ

Изменены только `src/DesktopOrganizer.UI/Models/{PanelCollision,WorkspaceLayoutPolicy}.cs`, их тесты
(`tests/DesktopOrganizer.Tests/Unit/Models/{PanelCollisionTests,WorkspaceLayoutPolicyTests}.cs`), этот
документ и `adr/ADR-028-workspace-grid.md`. `WorkspaceGridMetrics.cs`/`WorkspaceGridOptions.cs` не
редактировались. Zero-gap семантика `PanelCollision.ClampAgainstObstacles(Rect,double,double,
IReadOnlyList<Rect>)` и её 24 существующих теста (1-24, PanelCollisionTests.cs) — не изменены (проверено
и математическим доказательством в ADR-028's amendment, и полным прогоном тестов). ADR-017 не изменена.

### 8.5 Carry-over из финальной верификации fix-cycle 2 — закрыт в P03

Финальная верификация fix-cycle 2 нашла ещё один дефект того же класса, что MAJOR-A, но на
**перпендикулярной** оси; зона `PanelCollision.cs` к тому моменту была передана P03, поэтому фикс сделан
там (коммит `fix(ui): gap-aware clamp must not widen WHICH obstacles are walls (P02 carry-over)`).

| | Было (fix-cycle 2) | Стало (P03) |
|---|---|---|
| Репро | `current=(150,100,100,100)`, `obstacle=(100,0,100,95)` (зазор по Y = 5 < `gap` 8), `proposedLeft=300` | те же входные данные |
| Результат | `X = 150` — движение вбок заблокировано в **обе** стороны, хотя при `gap = 0` то же движение свободно (`overlapY = −5`) | `X = 300` — свободно, как и при `gap = 0` |
| Причина | `Inflate(o, gap)` расширял препятствие по **обеим** осям, поэтому сосед, который лишь «ближе чем `gap`» по вертикали (и вообще не перекрывается по ней), становился стеной для горизонтального движения. Ломался инвариант «X и Y разрешаются независимо → скольжение вдоль стены» (`PanelCollision.cs` `ClampAgainstObstacles` remarks) | Инфляция стала по-осевой: `InflateX` для горизонтального прохода, `InflateY` для вертикального. `gap` сдвигает только **позицию** стены, но никогда не расширяет **множество** препятствий, которые являются стенами |
| Тесты | — | `PanelCollisionTests` 35/36 (репро + вертикальное зеркало, оба сверяются с zero-gap перегрузкой), 37 (позитивная форма: блокировка по X ровно на `gap`, свободное скольжение по Y) |

Zero-gap перегрузка снова не затронута: `InflateX`/`InflateY` при `gap <= 0` возвращают исходный
прямоугольник (тот же приём, что был у `Inflate`), все 24 её теста не изменены.

## Fix cycle 3 — carry-over из верификаций P03/P04 (2026-09-22, оркестратор)

Публичные члены `WorkspaceLayoutPolicy`, добавленные P04 fix-циклом 1 (не были отражены в §8.2):
`ClampResizeToWorkArea(Rect, ResizeEdge, Rect workAreaDip, double unitSize, double gap, GridSpan minSpan)` — ужимает
ДВИЖУЩИЙСЯ край до стены work area (якорь неподвижен); `RoundBoundsToSpans(Size, unitSize, gap)` (nearest — правило
интерактивного resize) и `ClampToMinimum(GridSpan, GridSpan minSpan)` повышены до `public` (используются
`PanelSizingGridSupport.PersistSpans`, чтобы не дублировать инверсию cell+gap).

`PanelDragPipeline.CommittedSize(width, height, actualWidth, actualHeight)` — размер, о котором рассуждает
drop-time snap: явные `Window.Width/Height` (только что закоммиченные контроллером), `Actual*` — лишь fallback при
NaN; закрывает условный MAJOR верификации P04 (WPF-путь: `ActualWidth` отстаёт на один layout-pass → Alt-resize
480→320 клампился как 480 → якорь прыгал на 160 DIP).
