# ADR-028 — журнал реализации (P02–P04, fix-циклы, реестр решений оркестратора)

> **Назначение.** Это перенесённая БЕЗ потерь историческая часть [ADR-028](../../adr/ADR-028-workspace-grid.md):
> разделы «Amendment 2026-09-22 — P02 review», «Реализация P03», «Реализация P03 — fix-цикл 1»,
> «Amendment — Реализация P04», «Amendment — P04 fix-cycle 1», консолидированный реестр решений оркестратора и
> amendment ADR-011 от 2026-09-24. Исходный ADR на ~1470 строк на 55% состоял из этого журнала; в рамках
> ADR-audit WP-E (2026-10-09) ADR-028 переписан как документ на ~300 строк (действующие решения), а журнал
> вынесен сюда. Текст ниже сохранён дословно; единственные правки — относительные ссылки (файл переехал из
> `adr/` в `docs/PLANS/`) и этот заголовок.
>
> **Как читать.** Журнал — хронологический и содержит промежуточные формулировки, часть которых позже
> отменена (например, `SafeClamp(Rect, Rect)` → `SafeClampFreeform`, `CollapsedHeaderSpan` 4 → 5,
> `MinWidthSpanExpanded` 12 → 16, «init re-snap не удаляется» → удалён, «позиция как spans от origin» →
> OQ-6 = A). **Действующие правила — в [ADR-028](../../adr/ADR-028-workspace-grid.md), а не здесь**; при
> расхождении приоритет у ADR. Предыдущая (полная) редакция ADR-028 — в истории git
> (`git log --follow -- adr/ADR-028-workspace-grid.md`, коммит `v0.8.0` и ранее).
>
> Парный журнал для ADR-029 — [icon-layout-policy-adr029-implementation-log.md](icon-layout-policy-adr029-implementation-log.md).

---

## Amendment 2026-09-22 — P02 review (fix-cycle 1)

Code-review находки по `docs/PLANS/workspace-grid-02-metrics.md` (P02, чистая геометрия). Все пункты —
изменения **чистой математики** в `WorkspaceLayoutPolicy`/`WorkspaceGridMetrics`/`WorkspaceGridOptions`/
`PanelCollision` (аддитивно); ничего из этого не подключено к рантайму — это по-прежнему остаётся
architecture-only/pure-math объёмом, подключение — стадии 03/04.

### Решения оркестратора, зафиксированные этим amendment

- **Интерактивный resize (стадия 04) квантует размер ROUND'ом к БЛИЖАЙШЕМУ span**, симметрично, как
  сегодняшний legacy `ResizeGeometryCalculator.Apply`/`AdaptiveGridLayout.CountForExtent` (не
  `Math.Ceiling`). `Math.Ceiling` (с допуском, см. ниже) остаётся зарезервирован для одноразовых
  операций, которые не имеют права потерять контент: «Align to workspace grid», «Fit panel to content»,
  миграция legacy-размера в `GridAligned`. Реализовано как `WorkspaceLayoutPolicy.SnapBounds(Rect, Point
  origin, ResizeEdge, ...)` (Round) vs `WorkspaceLayoutPolicy.BoundsToSpans`/`SnapBounds(Rect, Point
  origin, ...)` (Ceiling+допуск) — две раздельные, поимённые квантующие функции, не один переключаемый
  режим одной функции.
- **Округление на `.5` для round-to-nearest — `Math.Floor(t + 0.5)`, НЕ banker's rounding**
  (`Math.Round`'s default `ToEven`). Обоснование: `Floor(t + 0.5)` даёт каждому целому span **равную по
  ширине** зону захвата `[n-0.5, n+0.5)`, тогда как round-to-even чередует, в какую сторону падает `.5`,
  от одного целого к другому — нежелательно для живого, непрерывно перетаскиваемого resize-жеста, где
  каждая половинная граница должна вести себя одинаково, а не только «чётные». Совпадает с уже
  действующим выбором `AdaptiveGridLayout.CountForExtent` (`MidpointRounding.AwayFromZero`) для всех
  неотрицательных `t` (здесь `t` всегда неотрицателен — `extent`/`gap`/`unitSize` уже клампятся к `>= 0`
  до этой точки). Это НЕ меняет `SnapCoordinate`/`SnapPosition` (позиция по-прежнему round-to-even,
  ADR-028's исходное решение, «px↔DIP» выше, — согласовано с `GridSnap.SnapAxis`) — затрагивает только
  НОВУЮ функцию `RoundSpanCountForExtent`, используемую исключительно resize-квантованием span'а.

### MAJOR-1 — `Math.Ceiling` без допуска → «ratchet» при round-trip DIP→px→DIP

`WorkspaceLayoutPolicy.BoundsToSpans`/`SpanCountForExtent` теперь вычисляет
`Ceiling((extent+gap)/unitSize − eps)`, `eps = 0.5 DIP / unitSize`. Обоснование `0.5 DIP`: одиночное
`Math.Round` в `NativeSizingCoordinateMapper.ToPx` (ADR-022 D-3) даёт погрешность не более `0.5`
физического px на грань; при масштабах `>= 100%` (единственный поддерживаемый диапазон, System-DPI,
см. «DPI-awareness» выше) это не больше `0.5 DIP` в худшем случае. Значение достаточно велико, чтобы
поглотить этот класс шума (пример из ревью: span 12 → 88 DIP → 97px @110% → 88.1818 DIP → без допуска
Ceiling даёт 13, затем 14 при повторном round-trip), и достаточно мало, чтобы не маскировать настоящий
рост контента на целый DIP и более. Результат клампится к `int` через `Math.Clamp(…, 1d, int.MaxValue)`
**до** приведения типа (NIT: предотвращает undefined narrowing conversion `double→int` для
патологически больших входов). Регрессия закрыта тестом
`SpansToBounds_BoundsToSpans_RoundTrip_IsIdempotent_AcrossDpiScales` (100/110/125/150/175/200%,
`Assert.Equal(span, span1)` — не только `span1==span2`, см. MAJOR-7 ниже).

### MAJOR-2 — `SafeClamp` даёт позицию вне сетки → следующий `Snap` выводит за work area

Добавлена **аддитивная** grid-aware перегрузка `WorkspaceLayoutPolicy.SafeClamp(Rect proposed, Rect
workAreaDip, double unitSize, double gap)` (исходная `SafeClamp(Rect, Rect)` не изменена — она
по-прежнему обслуживает `LegacyFreeform`/`WindowManager.ClampIntoMonitor`). Новая перегрузка выбирает
**наибольший слот сетки**, чей размер целиком помещается в work area:
`maxSlot = Floor((areaExtent + gap) / unitSize) − spanUnits`, клампится к `>= 0` (не помещается вовсе →
слот `0`, то есть позиция = origin — размер не трогается, то же правило «SafeClamp двигает только
позицию»). Пример из находки (`W=1366`, панель 248 DIP): непрерывный clamp даёт `X=1118` (вне сетки),
следующий `Snap` округляет до `1120`, `Right=1368 > 1366`; новая перегрузка сразу даёт `X=1112`
(`Right=1360`), что уже на сетке и внутри work area — повторный `Snap` не меняет результат.

### MAJOR-3 — resize-перегрузка `SnapBounds(Rect, ResizeEdge, …)` без origin, асимметричный Ceiling

Сигнатура изменена (аддитивных изменений тут недостаточно — сама перегрузка была структурно неверна, и
рантайм-вызывающего кода ещё нет ни у одного этапа): `SnapBounds(Rect raw, Point origin, ResizeEdge
edge, double unitSize, double gap, GridSpan minSpan)`. Два поведенческих изменения:

1. Квантование размера — через новую `RoundBoundsToSpans` (Round, см. решение оркестратора выше), не
   `BoundsToSpans` (Ceiling).
2. Противоположный (`anchor`) край **остаётся неподвижным, только если он уже на сетке** относительно
   `origin`; если нет — он квантуется (`SnapCoordinate`) как часть этого же вызова («сначала
   выравнивается», а не остаётся вне сетки навсегда). Работает благодаря инварианту
   `WorkspaceGridMetrics.ValidateGap` (`gap` — целое кратное `unitSize`): `SpansToBounds`'s extent —
   ВСЕГДА целое кратное `unitSize`, поэтому snap одного anchor-края плюс квантованный размер
   гарантированно кладёт **оба** результирующих края на сетку, не только anchor.

Тесты: `SnapBounds_ResizeFromEachEdge_KeepsOppositeEdgeFixed_WhenAlreadyOnGrid` (старое поведение, когда
условие выполнено) + новый `SnapBounds_ResizeFromEachEdge_WhenOppositeEdgeOffGrid_AlignsItFirst` (новое
правило), оба — по всем 8 `ResizeEdge`.

### MAJOR-4 — план подключения для этапа 04 (документация, код `ResizeGeometryCalculator`/`ResizeConstraintsFactory` не меняется в этом проходе)

Обнаружено ревью: `ResizeGeometryCalculator.Apply`'s `GridAligned`-путь **не должен** квантовать через
`AdaptiveGridLayout.SnapExtent` (`gap=4`, edge padding, chrome — система C, ADR-029) — это прямое
нарушение «Три независимые системы координат» (система B ≠ система C). План для стадии 04, фиксируемый
здесь как явный контракт (не реализуется этим проходом):

1. В `ResizeConstraintsFactory.BuildConstraints`, при `PanelGeometryMode == GridAligned`: **свободная
   ветка** `ResizeGeometryCalculator.Apply` — `SnapCellWidth`/`SnapCellHeight = 0` (т.е. тот же «free»
   путь, что сегодня использует List-режим/выключенный `SnapResizeToIconGrid`), **без** icon-snap.
   `Apply` возвращает `raw`-геометрию (chrome вычтен/добавлен обратно, но без квантования к
   `AdaptiveGridLayout`) — единственная точка, где `GridAligned` входит в существующий resize-конвейер
   (`Apply` сам не меняется и не дублируется, ADR-021 D-2/R6 — инвариант этого ADR подтверждён
   неизменным).
2. Результат `Apply` (ещё не квантован к workspace-сетке) передаётся в
   `WorkspaceLayoutPolicy.SnapBounds(Rect, Point origin, ResizeEdge, unitSize, gap, minSpan)` —
   **вторым** шагом того же кадра gesture, а затем (если рядом есть другая `GridAligned`-панель) —
   `WorkspaceLayoutPolicy.ClampResizeToSlots` (см. MAJOR-5(c) ниже).
3. `MinWidth`/`MinHeight` для `GridAligned`-ветки — из `WorkspaceGridMetrics.MinSpanExpanded`/
   `MinSpanCollapsed` (или `WorkspaceGridOptions`, если к тому моменту уже подключены настройки), **не**
   из `GridMetrics.MinPanelWidth(IconSize)` — прямое следствие правила «IconSize не меняет panel
   bounds», иначе минимум сам оставался бы последней невыполненной частью этого правила.
   `LegacyFreeform`-ветка `ResizeConstraintsFactory` — **без изменений**.

### MAJOR-5 — API для этапа 03/04

**(a) Slot ↔ position.** `WorkspaceLayoutPolicy.PositionToSlot(Point raw, Point origin, double
unitSize) → (int Col, int Row)` и обратная `SlotToPosition((int Col, int Row) slot, Point origin,
double unitSize) → Point` — позиционный аналог `BoundsToSpans`/`SpansToBounds` (те — для размера, эти —
для позиции). Doc-comment `GridSpan.cs` исправлен: этот тип хранит только SIZE, никогда позицию (старая
формулировка «панель, сидящая в слоте `(WidthUnits, HeightUnits)`» была неточной — у типа нет полей для
X/Y слота).

**(b) Gap-aware clamp к препятствиям.** Аддитивная перегрузка `PanelCollision.ClampAgainstObstacles(Rect
current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles, double gap)` —
существующая `gap`-less перегрузка **не изменена** (обслуживает `LegacyFreeform`, флеш-прилегание как
сегодня). Выбранная реализация — инфляция каждого препятствия на `gap` по всем четырём сторонам перед
делегированием в существующую sweep-математику (а не параллельная реализация): переиспользует уже
проверенную R1 ghost-obstacle/sweep-логику дословно, ценой одной небольшой аллокации массива на вызов —
приемлемо, так как эта перегрузка предназначена для `GridAligned`-resize/drag (сравнительно редкий
жест), а не для per-`MouseMove` пути `LegacyFreeform`, под который оптимизирована `gap`-less перегрузка.

**(c) Anti-overlap при resize по целым слотам.** `WorkspaceLayoutPolicy.ClampResizeToSlots(Rect
candidate, ResizeEdge edge, IReadOnlyList<Rect> obstacles, double unitSize, double gap, GridSpan
minSpan)` — чистая функция: пока кандидат оказывается ближе `gap` DIP к препятствию (см. (b)), утягивает
перетаскиваемый край на один целый `unitSize`-слот и пересчитывает размер/anchor (та же схема anchor'а,
что `SnapBounds`-resize), пока либо не очистится, либо не упрётся в `minSpan` (в этом случае остаточное
пересечение принимается — «floor побеждает», тот же принцип, что уже применяется в minSpan-клампах этого
типа). Реализует ADR-028's «resize-clamp по целым слотам» (см. «Anti-overlap при resize» выше) — стадия
04 подключает это как шаг конвейера сразу после `SnapBounds`-resize (см. MAJOR-4, п.2).

### MINOR — валидация, типы, документация

- `WorkspaceGridOptions.Validate()` теперь дополнительно требует `Gap < UnitSize *
  min(MinWidthSpanExpanded, MinHeightSpanExpanded, CollapsedHeaderSpan)` — исходная таблица invariants
  неявно предполагала это «для любого span», что неверно при патологическом `Gap` (пример:
  `Gap=40, UnitSize=8, CollapsedHeaderSpan=4` → `4*8-40=-32` DIP, молча клампится в `0` существующим
  `SpansToBounds`'s floor вместо явной ошибки конфигурации). Также — явная проверка `PresetSpans != null`
  и **полноты** таблицы (запись на каждый член `PanelSizePreset`), чтобы неполная/`null` таблица падала с
  понятным `ArgumentException`/`ArgumentNullException` в момент `Validate()`, а не позже как
  малопонятный `KeyNotFoundException` где-то в глубине `WorkspaceLayoutPolicy.PreferredSize`.
- `WorkspaceGridMetrics.SpanForPreset` — `TryGetValue` вместо индексатора, `ArgumentOutOfRangeException`
  с понятным сообщением вместо generic `KeyNotFoundException` (достижимо только если добавить член
  `PanelSizePreset` без соответствующей записи в таблице).
- `WorkspaceGridMetrics.DefaultPresetSpans` — `FrozenDictionary` вместо мутабельного `Dictionary`,
  выданного через `IReadOnlyDictionary` (иммутабельность на уровне типа, а не по соглашению).
- **`NativeSizingCoordinateMapper.ToPx` не изменён** (вне зоны этого прохода) — но задокументирован
  здесь как открытый вход для стадии 04: `ToPx` округляет `Left`/`Right` **независимо**
  (`Math.Round(Left*scale)`/`Math.Round(Right*scale)`, не «сначала Width, потом Right=Left+Width»), из-за
  чего итоговая физическая ширина окна, чей `Left` не выровнен по целому физическому пикселю, может
  отличаться от «идеальной» `Width*scale` на `±1` физический px. Для `GridAligned`-панели (у которой
  `Left`/`Right`, будучи целыми кратными `UnitSize`, почти никогда не выровнены по целому физическому
  пикселю на нецелых DPI-масштабах вроде 110%/175%) стадия 04 обязана явно решить, компенсировать ли этот
  класс расхождения (например, округлять `Width`, а не `Right`, для `GridAligned` конкретно) или принять
  его как унаследованное, задокументированное ограничение (тот же класс trade-off, что уже принят для
  `LegacyFreeform`, ADR-022 D-3) — открытый вопрос для стадии 04, не решается этим amendment.

### MAJOR-6 — `PreferredSize` игнорировал `WorkspaceGridOptions.PresetSpans`

Добавлена перегрузка `WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset preset, WorkspaceGridOptions
options)`, реально читающая `options.PresetSpans` (с fallback на `WorkspaceGridMetrics.SpanForPreset`,
если в `options` записи нет — defense in depth, не требует, чтобы вызывающий уже вызвал `Validate()`).
Исходная `PreferredSize(PanelSizePreset, double, double)` не изменена (всегда использует
default-таблицу) — обе перегрузки сосуществуют для разных вызывающих.

### MAJOR-7 — тесты: устранены тавтологии и слепые зоны

- Round-trip тест на idempotency расширен с 4 до 6 DPI-масштабов (добавлены 110%/175% — единственные из
  требуемого списка, которые реально бьют не-целыми кратными `8 DIP`) и получил вторую assert
  (`span == span1`, не только `span1 == span2` — см. MAJOR-1).
- `SnapPosition`-idempotency тест на отрицательный/дробный origin (было — только `origin=(0,0)`).
- Реалистичная drag-траектория (`raw` с нарастающим `delta` → `SnapPosition` → gap-aware
  `PanelCollision.ClampAgainstObstacles`) на отрицательном/дробном origin — вместо изолированного
  `SnapCoordinate`-monotonicity теста.
- Resize-min-clamp тест с `minSpan > 1×1` (реальный `WorkspaceGridMetrics.MinSpanExpanded`, 12×12),
  дополнительно проверяющий anchor-позицию — старый `EnforcesMinimumSpan`-тест проверял только размер.
- `SnapBounds_SeveralPanelsSameSpan_…`: исходные данные (и комментарий «very different… sizes») были
  вводящими в заблуждение — оба «панеля» использовали идентичный `expectedSize`, отличались только на
  доли DIP по позиции. Заменено на четыре генуинно разных сырых ширины/высоты, попадающих в один и тот
  же span.
- `AdjacentSlots_LeaveExactlyGapBetweenPanels`: позиция соседнего слота теперь вычисляется через новый
  `SlotToPosition` (MAJOR-5(a)) вместо инлайновой арифметики.

Все находки закрыты в `src/DesktopOrganizer.UI/Models/{WorkspaceLayoutPolicy,WorkspaceGridMetrics,
WorkspaceGridOptions,GridSpan,PanelCollision}.cs` и соответствующих тестах
(`tests/DesktopOrganizer.Tests/Unit/Models/{WorkspaceLayoutPolicyTests,WorkspaceGridMetricsTests,
WorkspaceGridOptionsTests,PanelCollisionTests}.cs`) — `dotnet build`/`dotnet test` зелёные (см.
`docs/PLANS/workspace-grid-02-metrics.md`, «Fix cycle 1»).

---

## Amendment 2026-09-22 — P02 review (fix-cycle 2, короткая заметка)

Полный разбор находок/тестов fix-cycle 2 — в [`docs/PLANS/workspace-grid-02-metrics.md`, «Fix cycle
2»](workspace-grid-02-metrics.md). Здесь фиксируются только два инварианта на уровне
архитектуры (не только кода), которые легко нарушить снова при будущих правках этих файлов.

**R1 и gap — разные вопросы, отвечают на них разные прямоугольники.** Правило R1 (ADR-017:
пред-существующее физическое пересечение не «чинится», амнистируется в любом направлении) обязано
проверяться **только** против РЕАЛЬНОГО (неинфлейченного) прямоугольника препятствия — величина `gap`
входит исключительно в арифметику итоговой позиции «стены» (`o.X - gap`/`o.Right + gap`) и не имеет права
участвовать в решении «это пред-существующее пересечение или нет». Смешение этих двух ролей (подход
fix-cycle 1 — инфлейтить препятствие целиком и затем переиспользовать zero-gap путь как есть) превращает
«панель ближе к соседу, чем требует gap» в «панель как будто уже физически внутри препятствия» и
амнистирует настоящее приближение к реальному прямоугольнику — панель проходит сквозь стену вместо того,
чтобы остановиться. Отдельный, независимый от R1 вопрос — направление движения относительно препятствия
(«уже внутри gap-зоны, но удаляется» обязано быть свободным; «уже внутри gap-зоны и приближается ещё» —
обязано быть заблокировано на текущей позиции, не отброшено назад) — решается не тем же ghost-флагом, а
отдельной проверкой «препятствие позади направления движения» (`PanelCollision.ClampAgainstObstaclesCore`,
fix-cycle 2 MAJOR-A). Тот же принцип «фильтрация/амнистия — против реального/анкерного состояния,
арифметика зазора — отдельно» применён и в `WorkspaceLayoutPolicy.ClampResizeToSlots` (fix-cycle 2
MAJOR-B): препятствие со стороны неподвижного anchor'а этого resize-жеста исключается из рассмотрения
целиком (оно физически не может быть «дожато» до `minSpan`, а не просто «дожимается впустую»).

**Владение перегрузок `SafeClamp`.** `WorkspaceLayoutPolicy.SafeClampFreeform(Rect, Rect)` (переименована в
fix-cycle 2 из `SafeClamp(Rect, Rect)`) — единственная перегрузка, принадлежащая
`PanelGeometryMode.LegacyFreeform`/`WindowManager.ClampIntoMonitor`. `SafeClamp(Rect, Rect, double,
double)` — единственная, принадлежащая `PanelGeometryMode.GridAligned`. Переименование (а не добавление
второй, `[Obsolete]`-совместимой перегрузки) было допустимо только потому, что ни у одной из двух
перегрузок ещё нет ни одного вызывающего в рантайм-коде (весь этот файл по-прежнему architecture-only/
pure-math — стадии 03/04 его ещё не подключили) — тот же прецедент, что fix-cycle 1 (MAJOR-3) уже применил
к `SnapBounds(Rect, ResizeEdge, ...)`. Будущий вызывающий обязан связывать выбор перегрузки с ИМЕНЕМ
метода (какой `PanelGeometryMode` он обслуживает), а не с количеством параметров.

---

## Реализация P03 (2026-09-22) — drag / snap / collision / мониторы

Этап `docs/fix-prompts/03-positioning.md` по плану
[`docs/PLANS/workspace-grid-03-04-integration.md`](workspace-grid-03-04-integration.md) §2.
Статус ADR остаётся **Proposed**: перевод в Accepted по-прежнему гейтится MV-1/MV-2 (ручная проверка на
Windows), см. «Manual verify» выше. Ниже — только то, что отличается от текста ADR или уточняет его.

### Что подключено

| Контракт ADR | Где реализован |
|---|---|
| Конвейер `GridAligned` (`SnapPosition → guides → anti-overlap`) | `UI/Models/PanelDragPipeline.ResolveGridFrame` — чистая функция; `PanelDragMoveBehavior.OnTitleBarMouseMove` — тонкий адаптер (ветка `_gridState.IsGridAligned`, иначе нетронутый `LegacyFreeform`-путь) |
| `Alt` — свободное движение | `PanelDragPipeline`'s `freeMove` (подавляет **только** шаг квантования), читается из `Keyboard.Modifiers` свежим кадром, никогда не снапшотится |
| Квантование на `EndDrag` независимо от `Alt` | `PanelDragPipeline.ResolveGridDrop` ← `PanelDragMoveBehavior.SnapToGrid` |
| `MonitorDeviceId` на `EndDrag` по центру (F-6) | `PanelDragPipeline.ResolveDropMonitor` ← `PanelDragMoveBehavior.UpdateMonitorDeviceIdOnDrop`, **в обоих режимах** (OQ-2 = A) |
| Restore / `MonitorsChanged` | `WindowManager.ResolveGridAlignedBounds` (internal static, чистая) ← `ResolveMonitorPlacement`/`OnMonitorsChanged`; `LegacyFreeform` продолжает идти через нетронутый `ClampIntoMonitor` |
| Явная команда «Align to workspace grid» | `WorkspaceGridPlacement.TryAlign`/`ClearAlignment` (чистые) ← `ChildWindow.WorkspaceGrid.cs` (per-panel, `IsCheckable`, OQ-3 = B) и `WindowManager.AlignPanelsToWorkspaceGrid` (все панели, детерминированный порядок, одно сохранение, подтверждение в окне настроек) |
| Lasso px→DIP (F-7) | `UI/Models/LassoPanelGeometry` ← `App.CreateLassoPanel`, коэффициент — `IMonitorService.GetSystemDpiScale()` |
| Единая запись геометрии | `PanelGeometryWriter` (+ новая `ApplyPosition` — move-only) во всех фиксирующих записях P03; per-frame запись драга осталась прямой (`Window.Left/Top`), как и предписывает план P03-D8 |

### Уточнения и решения, принятые при реализации

1. **`gap` расширяет позицию стены, а не множество стен (carry-over fix-cycle 2 → P03).** Инфляция
   препятствия в `PanelCollision.ClampAgainstObstaclesCore` стала по-осевой (`InflateX`/`InflateY`).
   Инвариант «X и Y разрешаются независимо → панель, упёршаяся по одной оси, свободно скользит по другой»
   (ADR-017) не должен зависеть от того, включён ли зазор. Репро и числа —
   `docs/PLANS/workspace-grid-02-metrics.md` §8.5.
2. **Дополнительный per-axis откат поверх clamp'а (`PanelDragPipeline.ClampHardWall`).** Gap-aware clamp
   уже дважды ломал инвариант «стена не пробивается» (fix-cycle 2 MAJOR-A и п. 1 выше), оба раза —
   из-за взаимодействия зазора с R1-амнистией. Поэтому результат clamp'а дополнительно сверяется с
   `PanelCollision.CreatesNewOverlap` против РЕАЛЬНЫХ препятствий и откатывается по оси, если шаг создаёт
   НОВОЕ пересечение. Против текущей реализации это no-op; цена — один O(N) проход на ось, покупка —
   инвариант, который не может тихо регрессировать.
3. **Позиция `GridAligned`-панели хранится как абсолютная точка, не как слот.** ADR выше описывает
   позицию как «spans от origin», из чего следовало бы, что при сдвиге work area (появился taskbar)
   панель едет вместе с origin, сохраняя индекс слота. Схема v5 (зона P02b) хранит spans только для
   **размера**; позиция — абсолютный DIP, прежний origin нигде не записан, поэтому пересчёт «по слоту»
   из восстановленного документа не вычислим. P03 реализует формулу плана §2.4 —
   `SnapPosition(model.Position, newOrigin, U)`: панель сохраняет абсолютную позицию, ре-квантованную к
   новому origin (сдвиг ≤ `U/2`), что заодно лучше всего удовлетворяет DoD «нет jump». Полноценный
   «логический слот» требует нового поля в схеме — **открытый вопрос**, не решается этим этапом.
4. **Span никогда не выводится из свёрнутой высоты.** `PanelGridStateResolver.Resolve` вычисляет spans
   из переданного размера; драг свёрнутой панели передавал живой прямоугольник, то есть высоту
   заголовка — панель после рестарта осталась бы свёрнутой навсегда. Введено единое правило
   `WorkspaceGridPlacement.ExpandedBounds` (живой размер, с подстановкой сохранённого `model.Size` при
   `IsRolledUp` и до первого измерения), общее для drag-резолва и обеих точек «Align».
5. **`PanelGeometryWriter.ApplyPosition`** (аддитивно к `Apply`): запись только позиции. Нужна там, где
   операция перемещает панель и не меняет её размер — иначе живая (свёрнутая) высота затёрла бы
   сохранённую развёрнутую в `model.Size`, а неинициализированный `Width`/`Height` (`NaN`) превратился бы
   в бессмысленный целочисленный размер. Тип стал `public` (его принимает конструктор публичного
   `PanelDragMoveBehavior`).
6. **Пункт в трее — не добавлен** (сознательно, как и предлагал план §2.5): команда доступна из
   контекстного меню каждой панели и из окна настроек; расширение `ITrayIconManager.UpdateContextMenu`
   (позиционные `bool`-параметры в `Services.Abstractions`) не окупается.
7. **Новая панель через «Add panel»** больше не создаётся в `(0,0)` поверх предыдущей: для `GridAligned`
   выбирается первый свободный слот (`WorkspaceGridPlacement.FindFreeSlot`, row-major, с зазором `G`).
   Для `LegacyFreeform` поведение не изменено.

---

## Реализация P03 — fix-цикл 1 (2026-09-22, code-review)

Итерация 1 из максимум 2 (`docs/fix-prompts/03-positioning.md`) по итогам code-review реализации выше.
`dotnet build` — 0 ошибок; `dotnet test` — **2766 passed, 0 failed** (базис worktree'а 2747 → +19, ни один
существующий тест не удалён и не изменил численного ожидания).

### CRITICAL C-1 — GridAligned-панель нельзя было перетащить на другой монитор (F-6 фактически мёртв)

**Находка.** `PanelDragMoveBehavior._gridState` резолвится ОДИН раз в начале жеста
(`ResolveGridState()` → `PanelGridStateResolver.Resolve`, который на старте предпочитает
`model.MonitorDeviceId`, т.е. монитор СТАРТА жеста) и переиспользуется на всём протяжении drag, включая
`SnapToGrid()` на `EndDrag`. `PanelDragPipeline.ResolveGridDrop` клампит финальную позицию через
`WorkspaceLayoutPolicy.SafeClamp(..., state.WorkArea, ...)` — `state.WorkArea` здесь всегда WorkArea
СТАРТОВОГО монитора, даже если курсор давно ушёл на второй монитор. Числовой повтор находки: DISPLAY1
WA `(0,0,1920,1040)`/`(0,40,1920,1040)` (сборка теста ниже), DISPLAY2 WA `(1920,0,1920,1080)`, панель
248×248, `U=8`; drop с раскладкой Left=2200 (то есть уже физически на DISPLAY2) даёт `maxSlot =
floor((1920-248)/8) = 209` → клампится на `Left = 0 + 209*8 = 1672` — телепорт на 528 DIP НАЗАД, на
DISPLAY1. `UpdateMonitorDeviceIdOnDrop` запускается ПОСЛЕ `SnapToGrid()` и читает уже клампнутый (снова
на DISPLAY1) `_window.Left/Top` — центр закономерно резолвится на DISPLAY1 → `PanelDragPipeline.
ResolveDropMonitor` возвращает `null` ("нет изменения") → `MonitorDeviceId` никогда не обновляется для
меж-мониторного drag, вопреки тому, что сам `UpdateMonitorDeviceIdOnDrop` реализован правильно (F-6
раздел выше остаётся корректным описанием НАМЕРЕНИЯ, но не факта).

**Фикс.** Новый чистый метод `PanelDragPipeline.RetargetToDropMonitor(current, state, monitors)`:
резолвит `PanelDragPipeline.ResolveDropMonitor` по СЫРОМУ (до какого-либо квантования/clamp'а)
финальному прямоугольнику жеста и — если центр оказался на ДРУГОМ мониторе, чем тот, для которого
`state` был резолвлен, — подменяет `Origin`/`WorkArea`/`MonitorDeviceId` состояния на этот монитор
(`readonly record struct … with { … }`, без новой энумерации — монитор ищется в уже снятом снимке
`_dragMonitors`). `PanelDragMoveBehavior.SnapToGrid()` вызывает его непосредственно перед
`ResolveGridDrop`. Порядок вызовов в `EndDrag` (`SnapToGrid()` → `UpdateMonitorDeviceIdOnDrop()`) при
этом остаётся НЕИЗМЕННЫМ: `UpdateMonitorDeviceIdOnDrop` уже был реализован верно и самостоятельно
резолвит монитор по финальному (пост-`SnapToGrid`) прямоугольнику — как только `SnapToGrid` перестаёт
клампить панель обратно на стартовый монитор, `UpdateMonitorDeviceIdOnDrop` естественным образом видит
правильный, уже реально другой монитор и обновляет `model.MonitorDeviceId`. Композиционный тест
(`PanelDragPipelineTests.RetargetToDropMonitor_ComposedWithResolveGridDrop_KeepsThePanelOnTheMonitorItWasDroppedOn`)
пин­нует и старое число (1672 — характеризационно, как задокументированный баг) и новое (`X > 1920`,
`MonitorDeviceId = DISPLAY2`).

**План P03-D10 скорректирован.** Формулировка «после `SnapToGrid()`» из
`docs/PLANS/workspace-grid-03-04-integration.md` §2.3 была ошибочной предпосылкой — правильный порядок:
резолюция монитора по сырому rect **до** `SnapToGrid`, а не после него (см. правку плана §2.3 ниже).
**D3-7 переоткрыт и закрыт заново**: закрывающие его тесты `PanelDragPipelineTests.ResolveDropMonitor_*`
из первого прохода тестировали только чистую функцию `ResolveDropMonitor` в изоляции — они были и
остаются корректны, но НЕ покрывали фактическую интеграцию (порядок вызовов в `SnapToGrid`), в которой
жил баг; DoD пересчитан как закрытый только теперь, новым композиционным тестом.

### MAJOR M-1 — guide-шаг использовал замороженную work area стартового монитора

`PanelDragPipeline.ResolveGridFrame`'s guide-шаг (`SnapGuideCalculator.Snap`) передавал `state.WorkArea`
безусловно — тот же стартовый монитор, что и в C-1, но здесь баг мягче: guide-коррекция, уводящая панель
далеко за пределы work area стартового монитора, отбрасывалась `StaysOnScreenX/Y`, даже если она была
абсолютно валидна на мониторе, где панель ФАКТИЧЕСКИ находится сейчас. `LegacyFreeform`-путь в
`PanelDragMoveBehavior.OnTitleBarMouseMove` уже специально пересканирует `_dragMonitors` на каждый кадр
для этого же guide-шага (комментарий MAJOR-A/fix-cycle 2) — `GridAligned`-путь этому не следовал.
**Фикс.** `ResolveGridFrame` получил новый параметр `monitors` (тот же once-per-gesture снимок) и
приватный `ResolveWorkAreaAtPosition`, резолвящий work area ПО ТЕКУЩЕЙ (после квантования) позиции кадра
— тот же скан списка, что уже делает Legacy-путь, без единого нового обращения к `IMonitorService`.
Fallback при отсутствии совпадения — `Rect.Empty` (тот же контракт «отключить guard», что уже
документирован `SnapGuideCalculator.Snap`), а не молчаливо устаревшая work area. Тест:
`ResolveGridFrame_GuideWorkArea_IsResolvedPerFrame_NotFrozenAtGestureStart` — панель и её сосед оба на
DISPLAY2, guide правильно резолвится по work area DISPLAY2, а не отбрасывается границами DISPLAY1.

### MAJOR M-2 — «Align all» смешивало панели разных страниц

`WindowManager.AlignPanelsToWorkspaceGrid(null)` перебирало ВСЕ `_childWindows` независимо от страницы, а
`CollectOtherPanelBounds` пропускает `!IsVisible` (панели неактивных страниц). Если несколько панелей
неактивной страницы уже имели измеренный (не нулевой) `ActualWidth/Height` от прошлого визита на эту
страницу — `WorkspaceGridPlacement.TryAlign`'s guard на нулевой размер их не отсеивал, и они выравнивались
без единого препятствия (друг для друга — тоже, потому что ни один из них не `IsVisible`), сходясь в
один и тот же первый слот. **Решение оркестратора** (с учётом UX P1-1 из прошлых итераций): область
«Align all» — ТЕКУЩАЯ страница; панель, переданная явным `panelId` (per-panel меню), не затронута.
Текст унифицирован: кнопка в окне настроек — «Align all panels to grid» (было «Align all to grid»,
рассинхронизировано с заголовком диалога подтверждения); сообщение диалога явно называет число панелей
текущей страницы. Тест на уровне `WindowManager` для самого фильтра не заведён — метод остаётся
«not practically unit-testable end-to-end» (тот же класс ограничения, что и `OnMonitorsChanged`, см. его
собственные remarks) — покрыт `MV-18` (панель неактивной страницы не трогается «Align all»); текстовая
часть M-2 закрыта `MainWindowViewModelTests.AlignAllPanels_ConfirmationMessage_*`.

### MAJOR M-3 — `OnMonitorsChanged` писало и сохраняло безусловно на каждый `WM_SETTINGCHANGE`

Даже когда монитор/work area панели не изменились ни на DIP (тема, локаль — что угодно, поднимающее
`WM_SETTINGCHANGE`), `OnMonitorsChanged` для каждой `GridAligned`-панели безусловно вызывало
`ApplyWorkspaceGridGeometry` (запись `Width`/`Height` через `PanelGeometryWriter`) и в конце — безусловный
`SaveWindowsState()`. **Фикс.** Новый `WindowManager.GridAlignedBoundsUnchanged(current, bounds,
tolerance: 0.5)` (тот же допуск 0.5 DIP, что уже документирован для `SpanQuantizationToleranceDip`) —
пропускает запись, когда пересчитанные bounds совпадают с текущими в пределах допуска; общий флаг
`anyChanged` (учитывающий также реассайн `MonitorDeviceId` при пропавшем мониторе и реальное изменение
позиции в `LegacyFreeform`-ветке через `ClampIntoMonitor`) гейтит финальный `SaveWindowsState()`.
`GridAlignedBoundsUnchanged` — `internal static`, юнит-тестируется напрямую (тот же паттерн, что уже
применён к `ClampIntoMonitor`); сам цикл `OnMonitorsChanged` остаётся manual verify (см. новый MV-24
ниже) по той же причине, что и раньше (требует живого `ChildWindow` через DI).

### MINOR-находки

- **m-1.** Три писателя геометрии расходились в округлении `double → int`: `PanelGeometryWriter.Write` и
  `WindowManager.WriteModelGeometry`/`ClampIntoMonitor`/`PlaceNewPanelOnFreeSlot` усекали `(int)x`
  (асимметрично к нулю), тогда как `LassoPanelGeometry.ToDip`/`ApplyTo` (F-7) уже используют
  `Math.Round`. Унифицировано на `Math.Round` во всех пяти местах — регрессия зафиксирована тестами с
  отрицательной полуцелой координатой (`Apply_NegativeFractionalCoordinate_RoundsToNearest_NotTowardZero`,
  `ClampIntoMonitor_FractionalNegativeClampResult_RoundsToNearest_NotTowardZero`).
- **m-2.** `PanelDragPipeline.ResolveGridDrop` клампило по `current.Size` — для свёрнутой (roll-up)
  панели это её collapsed-высота (~32 DIP), а не развёрнутая. Новая 4-аргументная перегрузка принимает
  `expandedSizeForClamp`; `PanelDragMoveBehavior.SnapToGrid` передаёт
  `WorkspaceGridPlacement.ExpandedBounds(...).Size`. 3-аргументная перегрузка (используется везде, где
  явно НЕ нужен этот нюанс) делегирует на 4-аргументную с `current.Size` — байт-в-байт прежнее поведение,
  ни один существующий вызывающий код/тест не менялся.
- **m-3.** `PanelDragPipeline.ClampHardWall`'s второй уровень отката (per-axis `CreatesNewOverlap`
  ре-проверка поверх gap-aware clamp'а) — решение: **оставить как есть**. Он сегодня no-op против
  реальной реализации (задокументировано в его собственных remarks), но защищает ровно тот инвариант
  («стена не пробивается»), который в этом же файле уже дважды регрессировал по независимым причинам
  (fix-cycle 2 MAJOR-A и carry-over §8.5). Цена — один лишний O(N)-проход на ось; выгода — инвариант,
  который не может тихо сломаться при будущей правке gap-арифметики. Удаление отклонено как
  несоразмерный риск ради экономии, которая не является узким местом (`ResolveGridFrame_RandomTrajectories_
  NeverCreateANewOverlap`, 1000 кадров × 4 комбинации, остаётся зелёным ИМЕННО благодаря этому уровню).
- **m-4.** `AlignToGridMenu_Click`'s отказ `TryAlign` был полностью тихим (галка и раньше корректно
  возвращалась к реальному состоянию через `BindingOperations.UpdateTarget` — это не менялось). Добавлена
  `Console.Error.WriteLine`-диагностика при отказе (тот же паттерн, что `WindowManager.CreateEachIsolated`
  уже использует для похожего тихого отказа); `AlignToWorkspaceGrid()` стал `bool`-возвращающим.
- **n-1.** `WindowManager.CollectAllPanelBounds` использовал `Guid.Empty` как сентинел «исключить
  ничего». `IWindowManager.CollectOtherPanelBounds(Guid excludingId, …)` → `Guid? excludingId` (тот же
  nullable-паттерн, что уже принят для `AlignPanelsToWorkspaceGrid(Guid? panelId)`); `null` теперь явно
  означает «не исключать никого». Аддитивно для всех вызывающих (неявная конверсия `Guid → Guid?`).

### UX P2 — подсказка про `Alt`

`ChildWindow.xaml`'s пункт «Align to workspace grid» получил `ToolTip="Hold Alt while dragging to move
freely"` (единственная правка в этом файле в рамках fix-цикла — соседний P06 добавляет свои пункты
меню параллельно). В `docs/` подсказка не дублируется отдельным файлом — сочтено избыточным сверх этого
ADR и обновлённого плана.

### UX P1-2 — зазор 8 DIP не даёт панелям вставать вплотную (задокументированный продуктовый риск)

Кодом не меняется в этом фикс-цикле. Stardock Fences допускает панели без видимого зазора; `WorkspaceGridMetrics.
DefaultGap = UnitSize = 8` (ADR-028 «Модуль U, зазор G») делает это структурно невозможным для
`GridAligned`-панелей сегодня. Зафиксировано как известное расхождение с Fences (см. Consequences ниже) и
как продуктовая возможность будущей настройки — **не задача этого fix-цикла**, WorkspaceGridOptions уже
параметризует `Gap` для тестового/будущего использования (`WorkspaceGridOptions.Gap`), но нет UI, который
бы позволял пользователю установить его в `0`; заведение такого UI требует отдельного решения оркестратора
о том, разрешать ли `Gap = 0` вообще (при `Gap = 0` соседние панели визуально сливаются краями — сам ли
это желаемое поведение или Fences-паритет должен сохранять минимальный визуальный зазор — открытый
продуктовый вопрос, не инженерный).

### OQ-6 — закрыт решением оркестратора: **A**

**Решение оркестратора (2026-09-22): A** — оставить как реализовано (позиция `GridAligned`-панели —
абсолютная точка, ре-квантуемая к новому origin при сдвиге work area, `SnapPosition(model.Position,
newOrigin, U)`). Обоснование выбора A над B (добавить `ColSlot`/`RowSlot` в схему v5): B требует новой
миграции схемы (владение P02b, не этого пакета) ради устранения ре-квантования, которое и так уже
идемпотентно и сдвигает панель не более чем на `U/2` — то есть визуально почти никогда не заметно
(сдвиг work area на целое число units — например, изменение высоты taskbar — вообще не двигает панель,
см. `ResolveGridAlignedBounds_WorkAreaOriginShiftsByWholeUnits_PanelDoesNotMove`). Цена варианта B (новая
персистентная схема, новый владелец, новая миграция) не оправдана выгодой (устранение под-`U/2`-сдвига в
редком случае сдвига work area НЕ на целое число units). ADR остаётся **Proposed** — перевод в `Accepted`
по-прежнему гейтится MV-1/MV-2 (не выполнены, ограничение среды разработки), теперь дополненными MV-19
(ниже) и уточнённым MV-3 (см. план §7).

### Manual verify — дополнение (MV-24)

| # | Что | Закрывает |
|---|---|---|
| MV-24 | Повторный `WM_SETTINGCHANGE` (смена темы) БЕЗ реального изменения монитора/work area, дважды подряд — 0 дополнительных записей на диск, `Width`/`Height` панелей не дрогнули ни на DIP. | M-3 |

**MV-3 уточнён** (был описан как сценарий, теперь реально закрыт этим fix-циклом, не только
теоретически): drag с DISPLAY1 на DISPLAY2, отпустить — панель должна остаться примерно там, где её
отпустили (не телепортировать на DISPLAY1), `MonitorDeviceId = DISPLAY2` сразу после drop, ДО любого
`WM_SETTINGCHANGE`/рестарта. См. `docs/PLANS/workspace-grid-03-04-integration.md` §7.

---

## Amendment 2026-09-22 — Реализация P04 (spans, native resize, roll-up, presets)

Этап P04 (`docs/fix-prompts/04-resize.md`, план
[`workspace-grid-03-04-integration.md` §3](workspace-grid-03-04-integration.md)) реализован.
Ниже — только те решения, которые меняют или уточняют архитектуру, зафиксированную выше; построчный
разбор и test log — в §3.11 плана.

### 1. Точка входа `GridAligned` в resize-конвейер: две, а не одна

Amendment MAJOR-4 предполагал единственную точку (`ResizeConstraintsFactory`). Фактически их две, и обе
обязательны:

1. **`ResizeConstraintsFactory.BuildConstraints(..., PanelGridState gridState = default)`** — *какими
   правилами* квантуется кадр: для `GridAligned` `SnapCellWidth/Height = 0` (свободная ветка
   `ResizeGeometryCalculator.Apply`, система C не участвует) и `MinWidth/MinHeight = SpansToBounds(MinSpan)`
   (не `GridMetrics.MinPanelWidth(IconSize)`). Поля хрома не тронуты (владение P05).
2. **`ResizeGestureSession.TryUpdate`** — *собственно квантование*: `Apply` → `SnapBounds(rect, origin,
   edge, U, G, minSpan)` → `ClampResizeToSlots(...)`. Это второй шаг ТОГО ЖЕ кадра, а не правка `Apply`:
   `ResizeGeometryCalculator` не изменён ни на строку (ADR-021 D-2/R6).

Почему в сессии, а не в двух контроллерах: `TryUpdate` — единственная точка, общая для Thumb- и
native-пути. Любое из двух других мест означало бы две копии одной математики — ровно тот дубль
(`SnapWidthToGrid` × 2), который golden-эквивалентность и существует, чтобы ловить.

### 2. `IPanelSizingGridHost` — как контроллеры получают монитор/препятствия/writer

Новый `public interface IPanelSizingGridHost` (`UI/Behaviors`) с тремя членами: `ResolveGridState(Rect)`,
`CollectResizeObstacles(IList<Rect>)`, `CommitGridGeometry(Rect, GridSpan)`. Реализуется **самим
`ChildWindow`** в его партиале `ChildWindow.Sizing.cs` (владение P04), а поведения получают его из уже
имеющегося у них `Window` (`window as IPanelSizingGridHost`; у native-контроллера дополнительно
опциональный параметр headless-конструктора для тестов).

Альтернатива «три новых ctor-параметра» отклонена **по структурной причине, а не из-за календаря**:
конструктор `ChildWindow.xaml.cs` — единственный файл, который P03 и P04 делят, и план (§4.2) явно
выделяет P04 в нём ровно один участок. Выбранная форма даёт P04 **ноль** правок в `ChildWindow.xaml.cs`
(включая F-2 — см. п. 4) при той же тестируемости: `FakePanelSizingGridHost` подменяет хост в headless-
тестах. Инвариант В1 сохранён: `GeometryMode` по-прежнему читается только внутри
`PanelGridStateResolver.Resolve`, который и вызывает хост.

`null`-хост ≡ `PanelGridState.Legacy` — именно это сохраняет `LegacyFreeform` байт-в-байт в тестах,
конструирующих контроллеры над «голым» `Window`.

### 3. Anti-overlap при resize: снимок препятствий у сессии, не у drag-поведения

Открытый пункт ADR-017 закрыт для `GridAligned` так: снимок `CollectOtherPanelBounds` берётся **один раз
за жест** в `TryBegin` и живёт в сессии. `PanelDragMoveBehavior._dragObstacles` не трогается вообще (его
`_clearDragObstacles()` на старте resize остаётся) — у resize теперь свой снимок, у drag свой.
`LegacyFreeform` получает пустой список → `ClampResizeToSlots` не вызывается → поведение прежнее.

### 4. F-2 реализован внутри `SnapWidthToGrid`, а не в `OnModelPropertyChanged`

Правило «`IconSize`/`ViewMode`/`IconLayoutPolicy` не меняют bounds `GridAligned`-панели» реализовано
ранним выходом в обеих копиях `IPanelResizeController.SnapWidthToGrid` (Thumb и native), а не гейтом на
единственном call-site в `ChildWindow.OnModelPropertyChanged`. Причина: правило принадлежит операции, а
не вызывающему — любой будущий вызывающий (P06 «Fit panel to content», re-snap при смене политики, и т. д.)
наследует его автоматически. Побочная выгода — P04 не правит `ChildWindow.xaml.cs` вовсе (п. 2).

### 5. Persist spans — через `PanelGeometryWriter`, внутри `PersistAfterGesture`

Spans пишутся после `_snapToGrid()` из финальных bounds панели и **только** через
`PanelGeometryWriter.Apply(bounds, span, null)` (одним актом с `Position`/`Size`; подписка на INPC
`WidthSpanUnits`/`HeightSpanUnits` по-прежнему запрещена). На native-пути запись живёт внутри
`PersistAfterGesture`, то есть уважает `deferPersist` (M-3): при завершении жеста изнутри модального
`WM_SIZING`-цикла span уходит на диск вместе с `SaveWindowsState`, после выхода из цикла, а не inline.

**Следствие для R-8 (ADR-022).** Документированное отклонение сохранено: ESC в нативном цикле
неотличим от обычного завершения, поэтому такой жест **персистит** итоговую геометрию — включая span.
Это согласовано с уже зафиксированным обоснованием R-8 (панель визуально уже в новой геометрии;
сохранять что-то другое — значит рассинхронизировать диск с экраном). На Thumb-пути ESC/`CaptureLost`
ведут себя как прежде.

### 6. OQ-4 = A реализован: `CollapsedHeaderSpan` = 5, roll-up работает span'ами

`CollapsedHeaderSpan = (int)((PanelMetrics.HeaderHeight + DefaultGap) / UnitSize)` = 5 — точная инверсия
модели `Extent(n) = n·U − G`, а не «магическое +1»: `5·8 − 8 = 32 = HeaderHeight`. Свёрнутая панель
занимает целые 5 узлов, её нижний край на сетке, DoD D4-4 выполняется по построению.

`RollUpBehavior` для `GridAligned`: collapsed-цель = `SpansToBounds((·, CollapsedHeaderSpan)).Height`,
expanded-цель = `SpansToBounds((·, model.HeightSpanUnits)).Height`; механика анимации (H3-детач,
generation-guard M-6) не изменена — изменены только значения `from`/`to`. `ToggleRollUp` перед
сворачиванием фиксирует развёрнутый span через writer.

**Новый инвариант, не очевидный из ADR до этого этапа:** grid-состояние для roll-up резолвится против
**развёрнутых** bounds, никогда против текущей (свёрнутой или промежуточной) высоты. Ленивая миграция
spans в `PanelGridStateResolver` берёт размер из переданного прямоугольника — передать ей свёрнутые 32 DIP
значило бы записать свёрнутую высоту как span панели и тем самым уничтожить значение, которое разворот
собирается восстановить.

`WindowManager.CollectOtherPanelBounds` (P04-R4 в плане) **не тронут**: после правки константы
`ChildWindow.TitleBarHeight` и `SpansToBounds(CollapsedHeaderSpan).Height` — одно и то же число (32),
правка была бы чисто косметической, а файл принадлежит параллельному P03. Зафиксировано как сознательный
пропуск, а не забытый пункт.

### 7. Открытый вопрос 1 закрыт: `MinWidthSpanExpanded` = 16 (120 DIP)

Плейсхолдер P02 (12 = 88 DIP) был меньше двух независимых реальных минимумов: жёсткой коэрции
`ChildWindow.xaml` `MinWidth = 120` и ширины ряда кнопок заголовка (4 × `PanelMetrics.HeaderButtonSize`
= 112 DIP). 16 единиц = 120 DIP удовлетворяет обоим и совпадает с `GridMetrics.MinPanelSize`, так что все
три минимума (WPF-коэрция, legacy-floor, workspace-floor) — одно число. Константа осталась литералом
(тип не должен зависеть от системы C), согласование обеспечено тестами, в том числе STA-тестом, который
читает `MinWidth` у живого `ChildWindow`.

### 8. `NativeSizingCoordinateMapper.ToPx` — решение «принять» (закрывает требование MINOR fix-cycle 1)

Раздельное округление краёв оставлено как есть. Ошибка ограничена 1 физическим пикселем, не
накапливается (каждый кадр пересчитывается из абсолютного DIP-прямоугольника, а не из предыдущего кадра)
и неперсистентна (на диск уходит span). Изменение округления затронуло бы `LegacyFreeform` и сдвинуло бы
golden-базу, ради которой этот код и существует. Граница зафиксирована тестом на 100/110/125/150/175 %,
включая проверку, что отклонение в 1 px реально достижимо (иначе тест был бы тавтологией), и что
span после round-trip px→DIP не меняется.

### 9. Presets доступны в обоих режимах и НЕ меняют `PanelGeometryMode`

План (P04-P1) предполагал, что пресет заодно включает `GridAligned`. Отклонено: OQ-3 = B сделал
checkable-пункт «Align to workspace grid» **единственной** явной точкой входа/выхода из режима; пресет,
молча конвертирующий панель, был бы вторым, скрытым входом, который тем же меню не отменить. Поэтому:

- `GridAligned` — пресет = span пресета (не ниже `MinSpan`), от квантованного top-left, `SafeClamp` в
  work area, затем anti-overlap **двумя** односторонними проходами `ClampResizeToSlots` (`Right`, затем
  `Bottom`). Один угловой проход сокращал бы и высоту при чисто горизонтальном столкновении — два прохода
  ужимают каждую ось ровно настолько, насколько эта ось действительно мешает.
- `LegacyFreeform` — пресет = только размер (extent пресета) на текущей позиции, `SafeClampFreeform`,
  без квантования, без anti-overlap (для этого режима ADR-017 остаётся открытым), без записи span.
- Свёрнутая панель пресет игнорирует (иначе либо разворот «мимо» `IsRolledUp`, либо запись свёрнутой
  высоты в `model.Size`, который обязан хранить развёрнутый размер).

### 10. Что P04 сознательно НЕ сделал

- Не трогал `WindowManager`/`PanelDragMoveBehavior`/`App.xaml.cs`/`MainWindowViewModel` (объём P03),
  `ChildWindow.xaml` (Stage S/P07), `PanelContentSizer`/`ChildWindow.ContentSizing.cs` (P06).
- Не изменял `ResizeGeometryCalculator`, `NativeSizingCoordinateMapper`, `PanelCollision`,
  `WorkspaceLayoutPolicy`, `GridSnap`, `SnapGuideCalculator`.
- Не закрывал восстановление после рестарта (`ResolveMonitorPlacement`) — это P03-W1; P04 покрыл только
  свою половину D4-5 (bounds → span → bounds — тождество).
- Не покрыл автотестом сам Thumb-жест (нужен реальный `Mouse.Captured` на `Thumb`); квантование обоих
  путей покрыто на уровне общей сессии, Thumb-специфичная часть остаётся в ручной матрице (MV-7).

---

## Amendment 2026-09-22 — Реализация P04, fix-cycle 1 (MAJOR-1…3, Alt при resize, presets)

Статус: принято. Изолированный worktree поверх `bugfix/workspace-grid` @ `ee4432a`.
`dotnet build` — 0 ошибок, `dotnet test` — **3057** зелёных (базис 2978 + 79 новых; ни один
существующий тест не удалён и ни одно существующее ожидание не изменено).

### MAJOR-1 — clamp «на кадре», а не трансляция на mouse-up

**Проблема.** `SnapBounds(…, ResizeEdge, …)` ничего не знает о work area, поэтому живой кадр resize мог
расти за край монитора сколько угодно. Коррекция приходила только на отпускании: `EndResize` →
`PanelDragMoveBehavior.SnapToGrid` → `PanelDragPipeline.ResolveGridDrop` → `SafeClamp`, а `SafeClamp` по
своему контракту **не ужимает размер, а двигает панель** (это верно для drag и неверно для resize).
Репро: work area `1920×1040`, панель `Left=1600 W=280`, тянем правый край `+200` → живая ширина 480
(`Right = 2080`); на release `maxSlot = Floor((1920−480)/8) = 180` → `Left = 1440`. Пользователь тянул
ПРАВЫЙ край, а прыгнул ЛЕВЫЙ — на 160 DIP, то есть ровно инвариант D4-3 наоборот.

**Решение.** Новая чистая функция `WorkspaceLayoutPolicy.ClampResizeToWorkArea(candidate, edge,
workArea, unit, gap, minSpan)`: ужимает span ДВИЖУЩЕГОСЯ края до наибольшего целого, который влезает
между якорем и стеной (`maxSpan = Floor((available + G) / U)`), якорный край не трогает вовсе. Работает
как второй шаг единого конвейера `PanelSizingGridSupport.QuantiseResize`:
`SnapBounds → ClampResizeToWorkArea → ClampResizeToSlots`. После него `SafeClamp` на mouse-up
гарантированно no-op (покрыто тестом на все 8 сторон). Единственный остаточный случай трансляции —
`minSpan` не влезает в work area (монитор уже минимальной панели): тогда «floor wins», как и в
`ClampResizeToSlots`.

**Почему ужимать, а не двигать.** Якорный край по определению resize — тот, который пользователь НЕ
двигает; он обязан пережить жест. Значит единственная оставшаяся степень свободы — сам span.

### MAJOR-2 — commit пишет ТОЧНЫЙ extent span, а не сырые bounds

**Проблема.** `PanelSizingGridSupport.PersistSpans` выводил span через `BoundsToSpans` (Ceiling) из того,
что вернули `Window.Width/Height` ПОСЛЕ px-round-trip `NativeSizingCoordinateMapper`, и коммитил эти же
сырые bounds. На 125/150/175% ошибка одного физического пикселя — 0.8/0.67/0.57 DIP, больше допуска
`SpanQuantizationToleranceDip` (0.5 DIP): панель 312 DIP (span 40) возвращалась как 312.667, `Ceiling`
давал span 41 (= 320 DIP), а `PanelGeometryWriter` писал `model.Size = (int)312.667 = 312`. Три источника
правды расходились, и каждый следующий жест добавлял по единице span (замерено на пяти подряд
commit: 312 → 312.667 → 313.333 → 314 → 314.667 → 315.333 при span, застрявшем на 41/32).

**Решение.** Span выводится `RoundBoundsToSpans` (ближайший; половина юнита — 4 DIP — на порядок больше
любой px-погрешности), а в окно/модель/span коммитится **ровно** `SpansToBounds(span)` с сохранённым
якорем. Три источника совпадают по построению. `BoundsToSpans` (Ceiling) остаётся за одноразовыми
операциями «никогда не терять контент» (Align / Fit-to-content / миграция legacy-размера).

### MAJOR-3 — ленивая миграция spans должна быть идемпотентной

`PanelGridStateResolver.Resolve` выводил span из живых bounds и писал его в модель БЕЗ `ClampToMinimum`.
Ширина самоисцелялась (`Window.MinWidth` = 120 DIP коэрсится WPF), высота — нет: `ChildWindow.xaml`
задаёт `MinHeight` = свёрнутые 32 DIP, а `MinHeightSpanExpanded` = 12 юнитов = 88 DIP. Панель высотой
56 DIP выводила span 8, записывала его, и `HasValidPersistedSpan` отвергал это же значение на следующем
resolve — навсегда. Панель никогда не получала span, который приняла бы персистентность, а
roll-down/restore воспроизводили заниженную высоту. Исправлено клампом производного span к `minSpan`
перед записью; второй resolve теперь находит span валидным и не пишет ничего.

### `Alt` при resize — вариант B (решение оркестратора)

Раздел «Модификатор свободного движения» распространяется на resize **симметрично** drag (P03):

- пока `Alt` удерживается, `ResizeGestureSession.TryUpdate` **пропускает** квантование этого кадра —
  край идёт за курсором 1:1, байт-в-байт как `ResizeGeometryCalculator.Apply` в одиночку
  (= поведение `LegacyFreeform`);
- модификатор читается **свежим на каждом кадре** (`Keyboard.Modifiers`, тот же источник, что у
  `PanelDragMoveBehavior.IsFreeMoveModifierDown`), никогда не снапшотится на `TryBegin`, поэтому
  нажатие/отпускание срабатывает со следующего же кадра;
- на commit (mouse-up / `WM_EXITSIZEMOVE`) `PersistSpans` прогоняет ТОТ ЖЕ `QuantiseResize`, поэтому
  `GridAligned`-панель **никогда не остаётся в состоянии покоя вне сетки** — абсолютное правило ADR
  сохраняется, и это единственное место, где оно обеспечивается;
- `Alt` не меняет `PanelGeometryMode` (отклонённая альтернатива раздела выше остаётся отклонённой) и
  не опрашивается вовсе для `LegacyFreeform`-жеста.

Логическое обоснование то же, что и для drag: `Alt` — временное ослабление жёсткости ОДНОГО жеста, а не
постоянное освобождение от сетки. UI: подсказка в ToolTip пункта «Size» (пункт «Align to workspace
grid» — зона P03).

### Preset `Small` (20×20) — сознательно compact-пресет

`20 × 20` юнитов = `152 × 152` DIP. При `IconSize = 48` это 1×1 иконка, что выглядит как ошибка таблицы.
Решение: **span не менять**, а задокументировать назначение — компактный пресет для мелких иконок
(`IconSize = 32`: ~2×1). Изменение таблицы пресетов затронуло бы персистентность и `WorkspaceGridOptions`
ради косметики; вместо этого пункт меню «Small» несёт ToolTip «fits ~2×1 icons at 32 px». Пересмотр —
после MV-визуальной проверки модуля `U` (открытый gate ADR-028).

### Preset выключает `AutoContent` (UX P0)

Пресет — это явное РУЧНОЕ решение о размере. Оставлять панель в `PanelSizingMode.AutoContent`
(ADR-029 Решение 7) значило бы, что content-sizer молча отменит ВЫСОТУ пресета через ~300 мс, когда
сработает его debounce по изменению ширины (а пресет меняет ширину). Поэтому `SizePresetMenu_Click`
ставит `SizingMode = Manual` ДО записи геометрии. Разграничение с параллельным P06: перевод в `Manual`
по завершению вертикального resize — зона P06 (хук конца жеста), перевод по пресету — зона P04
(`ChildWindow.Sizing.cs`).

### MV-21 закрыт: подменю «Size» дизейблится на свёрнутой панели

`SizePresetMenu_Click` всегда возвращался рано для `IsRolledUp` (иначе либо разворот «мимо» флага, либо
запись свёрнутой высоты в `model.Size`), но молча — это читается как зависание. Теперь `IsEnabled`
пункта «Size» — `AllTrueMultiConverter(CanEnterEditLayout, Not(Model.IsRolledUp))`; ранний return
остаётся защитой в глубину. Добавлен минимальный `NotBooleanConverter` (AND-конвертер P06 не умеет
инвертировать отдельный вход).

### Прочее (MINOR/NIT)

- `IPanelSizingGridHost.ParticipatesInGrid` — новый член с **default-реализацией** (`true`, то есть
  «спросить резолвер»), поэтому добавление не ломает ни одного реализатора.
  `PanelSizingGridSupport.IsGridAligned`/`PersistSpans` короткозамыкаются на нём: `LegacyFreeform`-панель
  больше не платит `IMonitorService.GetMonitors()` на каждый тик слайдера `IconSize`.
- У обоих контроллеров появился ВТОРОЙ буфер препятствий (`_commitObstacles`): первый отдан по ссылке
  в `ResizeGestureSession.TryBegin` и принадлежит (возможно, уже следующему) жесту — отложенный persist
  (M-3) не имеет права его перезаполнять.
- `outcome.Edge` захватывается по значению в замыкание отложенного persist.
- `ResolveGridState` по-прежнему вызывается ДО `TryBegin` (обоим контроллерам нужны floors для
  constraints); после MAJOR-3 побочный эффект миграции идемпотентен, поэтому это задокументировано, а не
  переставлено.
- `FakePanelSizingGridHost` научился зеркалить РЕАЛЬНЫЙ `PanelGridStateResolver` (`MirrorRealResolver`),
  иначе константный fake структурно прятал MAJOR-3 от контроллерных тестов.
- Устаревшие доки: `ClampResizeToSlots` («`CollapsedHeaderSpan`, 4» → 5), `ChildWindow.xaml`
  («Click handlers currently no-op» → оба реализованы).
- `ChildWindow.Sizing.cs` переиспользует буфер препятствий пресета вместо `new List<Rect>()` на клик.

### Порядок конвейера на commit изменён (важно для интеграции с P03)

Было `_snapToGrid()` → `PersistSpans`. Стало `PersistSpans` → `_snapToGrid()`. Причина — MAJOR-1:
`PersistSpans` возвращает панель внутрь work area УЖИМАНИЕМ, после чего `SafeClamp` внутри `SnapToGrid`
уже нечего «чинить» трансляцией. Для `LegacyFreeform` `PersistSpans` — no-op, поэтому legacy-порядок
(snap, затем save) не изменился.

---

## Решения оркестратора 2026-09-22 — консолидированный реестр

Единая справочная точка по итогам P01–P06 (и их fix-циклов) для этого ADR и парного ADR-029 — каждый
пункт уже принят и реализован где-то выше (в этом документе, в ADR-029 или в
`docs/PLANS/workspace-grid-*.md`); здесь только сведён итог решения без повторения обоснования. Собрано
по запросу `architecture-documenter` для сверки P09.

| # | Решение | Итог | Источник |
|---|---|---|---|
| 1 | Дефолт режимов для НОВЫХ панелей | `PanelGeometryMode.GridAligned` + `IconLayoutPolicy.FixedGap` | ADR-028 «Глобальная настройка default для НОВЫХ панелей» (OQ-3=B контекст), ADR-029 Решение 6 |
| 2 | Дефолт режимов для СУЩЕСТВУЮЩИХ (мигрированных) панелей | `PanelGeometryMode.LegacyFreeform` + `IconLayoutPolicy.LegacyStretch` + `PanelSizingMode.Manual` — явно проставлено миграцией v4→v5, не унаследовано «по умолчанию класса» | ADR-028 «Per-panel поле», ADR-029 Решение 6/Решение 7 «Существующие панели → Manual», `workspace-grid-migration.md` §10.3 |
| 3 | F-3 (учёт хрома `ContentLeftInset`) — одна или обе политики | Обе (`LegacyStretch` и `FixedGap`) — Открытый вопрос Q2 = A | ADR-029 Решение 2, «Открытые вопросы» Q2 |
| 4 | `AutoContent`/«Fit panel to content» — оба режима вида или только Grid | Только Grid-режим (Q3 = B); List остаётся только `Manual` до отдельного follow-up | ADR-029 Открытые вопросы Q3, «Реализация P06» §6 |
| 5 | Квантование интерактивного resize по span | `Round` к БЛИЖАЙШЕМУ span (симметрично legacy `ResizeGeometryCalculator.Apply`), НЕ `Ceiling` | ADR-028 «Amendment — P02 review (fix-cycle 1)», решения оркестратора |
| 6 | Квантование одноразовых операций (Align/Fit-to-content/миграция) | `Ceiling` (с допуском 0.5 DIP против ratchet-эффекта на round-trip DIP↔px) | ADR-028 «Amendment — P02 review (fix-cycle 1)», MAJOR-1 |
| 7 | Округление половинного случая (`.5`) при round-to-nearest | `Math.Floor(t + 0.5)`, НЕ banker's rounding (`ToEven`) — равная зона захвата на каждый span | ADR-028 «Amendment — P02 review (fix-cycle 1)» |
| 8 | Переснап при загрузке панели (`ChildWindow.Initialize`) | Удалён для ВСЕХ режимов (`GridAligned`/`LegacyFreeform`, `FixedGap`/`LegacyStretch`) — загрузка никогда не меняет персистентную геометрию | ADR-028 «`LegacyFreeform` — существующее поведение…», Amendment (P05 fix-cycle MAJOR-1) |
| 9 | OQ-1 (калибровка `DesktopCoordinateSpace`) | A — `Offset = (0,0)` без вызова `CalibrateOnce`; реальная калибровка — отдельным коммитом ПОСЛЕ MV-1 | `workspace-grid-03-04-integration.md` §8 OQ-1 |
| 10 | OQ-2 (`MonitorDeviceId` на drop — оба режима или только `GridAligned`) | A — оба режима (это фикс бага F-6, не изменение геометрии) | `workspace-grid-03-04-integration.md` §8 OQ-2 |
| 11 | OQ-3 (как снять `GridAligned` с панели) | B — checkable пункт меню «Align to workspace grid» | `workspace-grid-03-04-integration.md` §8 OQ-3, реализовано P03 |
| 12 | OQ-4 (`CollapsedHeaderSpan` = 4 или 5) | A → `5` (точная инверсия `Extent(n) = n·U − G`, не «+1») | `workspace-grid-03-04-integration.md` §8 OQ-4, реализовано P04 п.6 |
| 13 | OQ-5 (P03/P04 параллельно или строго последовательно) | Разрешено параллельно; единственная точка синхронизации — P04-G6 ждёт мержа P03-D9 | `workspace-grid-03-04-integration.md` §8 OQ-5 |
| 14 | OQ-6 (позиция `GridAligned` — абсолютная точка или индекс слота) | A — абсолютная точка, ре-квантуемая к новому origin на `EndDrag`/`OnMonitorsChanged` (индекс слота потребовал бы новой миграции схемы, не оправдано) | ADR-028 «Реализация P03 — fix-цикл 1», OQ-6; `workspace-grid-03-04-integration.md` §8 |
| 15 | `Alt` во время drag | Подавляет только квантование позиции текущего кадра; commit на `EndDrag` — всегда квантован, независимо от состояния `Alt` | ADR-028 «Модификатор свободного движения» |
| 16 | `Alt` во время resize | Симметрично drag (вариант B) — подавляет квантование кадра `ResizeGestureSession.TryUpdate`; commit — всегда квантован | ADR-028 «Amendment — Реализация P04, fix-cycle 1», «`Alt` при resize — вариант B» |
| 17 | Commit геометрии `GridAligned` | Всегда на сетке (span), никогда не оставляет панель в состоянии покоя вне сетки — инвариант обеспечен на КАЖДОМ пути (drag/resize/Align/Fit/restore) | ADR-028, множественные разделы; закрывающий тест — `QuantiseResize` конвейер |
| 18 | Пресет (S/M/L/XL) и `PanelSizingMode` | Клик по пресету переводит панель в `Manual` (иначе content-sizer молча отменил бы размер пресета через ~300 мс) | ADR-028 «Amendment — Реализация P04, fix-cycle 1», «Preset выключает AutoContent (UX P0)» |
| 19 | Завершённый вертикальный resize и `PanelSizingMode` | Демотирует `AutoContent → Manual` (Fences-подобное поведение); resize только по ширине — не демотирует | ADR-029 «Fix-цикл P06», MAJOR-4 |
| 20 | «Align all» — область действия | ТЕКУЩАЯ страница (панели других страниц не участвуют — иначе схлопываются в один слот, MAJOR M-2) | ADR-028 «Реализация P03 — фикс-цикл 1», MAJOR M-2 |
| 21 | «Apply … to all panels» (Icon layout / Sizing mode) — область действия | ВСЕ страницы (текст диалогов явно это называет — «on all pages») | ADR-029 «Реализация P06» §4, «Fix-цикл P06» MINOR m-4/UX-P1 |
| 22 | Fences-паритет для `AutoContent`/«Fit panel to content» | НЕ заявляется как буквальный паритет — bespoke-функция этого проекта (формула не взята из Fences, а выведена из существующих `GridMetrics`/`AdaptiveGridLayout`) | ADR-029 Решение 7, «Последствия» |
| 23 | `Small`-пресет (20×20 units = 152×152 DIP) | Оставлен как есть — сознательно compact-пресет для мелких иконок (`IconSize≈32`), не ошибка таблицы; ToolTip поясняет назначение | ADR-028 «Amendment — Реализация P04, fix-cycle 1», «Preset `Small`» |
| 24 | Glass на светлых обоях (контраст < 4.5:1) | Вариант C — usability-подсказка рядом с выбором Surface style в окне настроек; токены Glass не меняются (фиксированное следствие `GlassOpacityFactor`, R-8) | `workspace-grid-07-visual.md` §4 Вопрос 3, ADR-025 Amendment (P07-visual fix-cycle 1) |
| 25 | `SurfaceComposer.MinSurfaceOpacity = 0.35` (контраст ≈1.7:1 на светлых обоях) | Вариант B — не поднимать пол автоматически (перезаписало бы пользовательские значения ниже нового порога); заведён долг `DO-021` | `workspace-grid-07-visual.md` §4 Вопрос 1, `docs/Refactoring/BACKLOG.md` DO-021 |
| 26 | Hover-wash header-кнопок (осветляет вместо затемняет на светлых обоях → контраст-регрессия) | Вариант A — исправлено: `HoverOverlayPolicy` выбирает затемняющий/осветляющий wash по luminance поверхности | `workspace-grid-07-visual.md` §4 Вопрос 2 (fix-cycle 1) |

Полные обоснования и альтернативы — по ссылкам в колонке «Источник»; этот реестр — индекс, не замена
оригинальных разделов.

---

## Amendment 2026-09-24 (ADR-011) — точечное, явно запрошенное исключение из «нет jump on first launch»

[ADR-011](../../adr/ADR-011-manual-drag-with-magnetic-grid-snap.md) вводит `GridSizeNormalizer` — по прямому запросу
пользователя, чтобы панель, чьи `Position`/`Size` остались кратны **старому** `IAppSettings.GridSize`
после того, как пользователь сменил эту глобальную настройку, при restore/смене настройки подтягивалась к
**новой** решётке. Это единственное известное на сегодня отклонение от DoD этого документа «нет jump on
first launch»: при `GridSize > 1` restore может сдвинуть уже существующую `LegacyFreeform`-панель (и, при
кратности `GridSize` числу `WorkspaceGridMetrics.UnitSize`, панель `GridAligned`) на величину до одного
шага сетки. Полные детали, обоснование и таблица «до/после» — в ADR-011, раздел «Amendment 2026-09-24 —
нормализация при смене `GridSize`»; здесь фиксируется только сам факт исключения и его граница:

- При `GridSize = 1` (дефолт проекта, «сетка выключена») — исключения нет, DoD «нет jump on first launch»
  выполняется буквально, как и было сформулировано изначально.
- Исключение активируется только явным действием пользователя (включение ненулевой сетки/смена её шага),
  никогда — самим фактом перезапуска приложения при неизменных настройках.
- Область действия — `Position`/`Size` (`GridSizeNormalizer`), а не `GeometryMode`/`WidthSpanUnits`/
  `HeightSpanUnits` как персистентные поля: `PanelGeometryMode` панели этим amendment'ом не меняется и не
  мигрируется, меняются только числа `Left/Top/Width/Height`, ровно та же категория данных, которую и
  раньше корректировал `ClampIntoMonitor`/`ResolveGridAlignedBounds`.
