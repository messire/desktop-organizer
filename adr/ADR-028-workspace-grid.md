# ADR-028: Workspace Grid and Panel Geometry

**Статус:** Accepted (2026-10-09, ADR-audit WP-E) — Amended 2026-09-27 (ADR-037: PerMonitorV2 заменяет решение «System-DPI, манифеста нет»; «window space» = desktop-DIP) — Amended 2026-09-22/24 (см. «Amendments»)
**Реализация:** 2026-09-22 (P02–P06 + fix-циклы); manual verify выполнена на Windows 11 24H2 2026-09-27
(`docs/ROADMAP.md`, строки 6–8), признана достаточной; Windows 10 проект не проверяет. Оговорка: гипотеза
«пространство `Window.Left/Top` на мониторе с отрицательными координатами» (Открытый вопрос 2) стендом с
двумя мониторами не проверялась и остаётся открытой.
**Дата:** 2026-09-22 (переписан 2026-10-09)
**Журнал реализации:** [docs/PLANS/workspace-grid-adr028-implementation-log.md](../docs/PLANS/workspace-grid-adr028-implementation-log.md)
(P02 fix-циклы 1–2, P03/P04 реализация, fix-циклы, реестр решений оркестратора — перенесено без потерь).
Парный ADR — [ADR-029](ADR-029-icon-layout-policy.md).

> **Примечание о редакции.** Исходный текст (~1470 строк) на 55% состоял из журнала P0x/fix-циклов. В этой
> редакции оставлены только действующие решения, исправлены устаревшие числа тела (минимумы 12/4 → 16/5,
> `Validate` → `ValidateGap`, `SafeClamp` → `SafeClampFreeform`, «init re-snap не удаляется» отменено,
> «позиция как spans от origin» отменено OQ-6). Предыдущая редакция — в истории git.

---

## Контекст

Аудит P00 (`docs/PLANS/workspace-grid-00-baseline.md`) нашёл 21 writer геометрии панели и 12 находок
(F-1…F-12); для этого ADR значимы:

- **F-1/F-8** — `GridSnap` квантует абсолютные `Left/Top` к глобальному `(0,0)` виртуального рабочего
  стола, а не к origin монитора/work area.
- **F-2** — ширина панели зависела от `IconSize`/`ViewMode` в трёх местах, включая переснап при каждом
  старте приложения.
- **F-3** — хром `PanelChrome` не был учтён в `GridMetrics.FileListHPadding` (владение ADR-029).
- **F-5** — full-row stretch-gap (ADR-018) конфликтует с «fixed-gap» (владение ADR-029).
- **F-6** — `MonitorDeviceId` не обновлялся при межмониторном drag.
- **F-7** — лассо-создание панели писало физические px как DIP.

ADR-028 владеет **внешней** геометрией (где панель находится и какого она размера); ADR-029 — **внутренней**
раскладкой иконок.

---

## Три независимые системы координат

- **Система A — Physical Monitor Geometry** (`IMonitorService`/`INativeApi`, `MonitorInfo.Bounds`/`WorkArea`;
  Win32 virtual-screen: `(0,0)` = левый верхний угол primary, левее/выше — отрицательные). Не меняется.
- **Система B — Workspace Panel Grid** (DIP, per-monitor origin) — предмет этого ADR; существует в двух
  режимах `PanelGeometryMode`:
  - `LegacyFreeform` — прежнее глобальное DIP-пространство, без origin;
  - `GridAligned` — размер как целые **spans** с шагом `U` и зазором `G`; позиция — абсолютная DIP-точка,
    квантованная относительно origin монитора (см. OQ-6 ниже).
- **Система C — Internal Icon Layout** (DIP, content-relative) — [ADR-029](ADR-029-icon-layout-policy.md).

**Правило границы B↔C.** `Width`/`Height` панели (B) — **вход** для C, никогда **выход**: содержимое
(`IconSize`, `ViewMode`, число иконок) не пишет обратно в bounds, кроме поимённых исключений (`Fit panel to
content`/`AutoContent`, ADR-029 Решение 7 — только высота). Граница B→C материализована константой
`PanelMetrics.ContentLeftInset` (см. «Хром»).

---

## Origin: work area монитора

**Решение.** Origin системы B (только `GridAligned`) — верхний левый угол `MonitorInfo.WorkArea` монитора
панели (`MonitorDeviceId`), не `Bounds` и не глобальный `(0,0)`. Work area уже исключает taskbar, поэтому
линия `0` по построению совпадает с видимым краем полезной области (закрывает F-1). Monitor bounds отклонены
(панель пряталась бы под докованным taskbar); глобальный `(0,0)` оставлен только `LegacyFreeform`.

---

## px↔DIP: конверсия, округление, идемпотентность

Вся арифметика системы B — в DIP. Конверсия px↔DIP — только в существующих точках, не в
`WorkspaceLayoutPolicy` (он принимает и возвращает DIP). Исходная редакция называла единственной точкой
native-resize `NativeSizingCoordinateMapper` (ADR-022 D-3); **после ADR-037** `NativePanelResizeController`
конвертирует `WM_SIZING`-прямоугольники через `IDesktopScaleProvider.Current` (`DesktopScaleModel.PxToDip`/
`DipToPx`, per-monitor), а чтение/запись bounds окна — через `PanelWindowCoordinates`; `NativeSizingCoordinateMapper`
остаётся контрактом округления краёв (`Math.Round` каждого независимо) и более не единственная точка. Квантование идемпотентно:

- **Позиция** (`SnapPosition`/`SnapCoordinate`): `origin + Round((raw − origin)/U)·U`, `Math.Round` по
  умолчанию (ToEven, согласовано с `GridSnap.SnapAxis`).
- **Размер, одноразовые операции** («Align to workspace grid», «Fit panel to content», миграция legacy-размера):
  `BoundsToSpans` = `Math.Ceiling((extent+gap)/U − eps)`, `eps = 0.5 DIP / U` (`SpanQuantizationToleranceDip =
  0.5d`, поглощает шум округления px при round-trip DIP→px→DIP; результат клампится к `int` до приведения).
- **Размер, интерактивный resize:** `RoundBoundsToSpans` — округление к ближайшему span, `Math.Floor(t + 0.5)`
  (не banker's: равная зона захвата у каждого span). Две раздельные поимённые функции, не режим одной.
- Device-pixel округление делегировано native-пути (`ToNativePx`/`NativeSizingCoordinateMapper.ToPx`); на
  нецелых масштабах (125%/150%) `U`-кратный DIP-размер не обязан давать целые px — наследуемое ограничение
  ADR-022. Края округляются независимо (`Math.Round(Left)`/`Math.Round(Right)`); ошибка ≤1 физ. px, не
  накапливается и не персистится (на диск уходит span) — решение «принять».

**DPI-awareness.** Исходная редакция фиксировала «System-DPI, манифеста нет; PerMonitorV2 отложен».
**Отменено ADR-037:** приложение PerMonitorV2, шкала desktop-слоя двухрежимная (E/T). Origin-модель B от
этого не меняется (origin по-прежнему = work area монитора в DIP).

---

## Модуль `U`, зазор `G`, spans, минимумы, presets

### Модель `Extent(n) = n·U − G`

`U = 8 DIP` (`WorkspaceGridMetrics.UnitSize`), `G = 8 DIP` (`DefaultGap`, обязан быть целым кратным `U` —
проверяет `WorkspaceGridMetrics.ValidateGap(unitSize, gap)`). Узлы сетки идут с периодом `U`; DIP-размер
панели из `n` узлов — `n·U − G`, так что соседние панели оставляют ровно `G` пустого места. `U` — стартовая,
структурно обоснованная константа (единственная точка правки — одна константа); сознательно несоизмерима с
icon-сеткой (`CellSize(48)=84`, система C).

**Размер хранится как `GridSpan(WidthUnits, HeightUnits)`**; `Width/Height` в DIP — производная
(`SpansToBounds`).

### Минимумы (фактические константы `WorkspaceGridMetrics`)

| Величина | Значение | DIP (`n·8 − 8`) | Обоснование |
|---|---|---|---|
| `MinWidthSpanExpanded` | `16` | 120 | `ChildWindow.MinWidth=120` (коэрция WPF) = `GridMetrics.MinPanelSize=120` ≥ ряд кнопок заголовка 4×`HeaderButtonSize`=112; три минимума — одно число. Закрыт Открытый вопрос 1 |
| `MinHeightSpanExpanded` | `12` | 88 | Не выводится из `IconSize` (иначе нарушалось бы «IconSize не меняет bounds»); ориентир — `RowHeight(16)=60` + заголовок |
| `CollapsedHeaderSpan` | `5` | 32 | `(HeaderHeight + G)/U` — точная инверсия модели, `5·8−8 = 32 = PanelMetrics.HeaderHeight`; нижний край свёрнутой панели на сетке. Значение `4` из исходной редакции (`HeaderHeight=4·U`) давало бы 24 DIP |
| `MinWidthSpanCollapsed` | `= MinWidthSpanExpanded` | 120 | roll-up меняет только высоту |

(«`HeaderHeight=4×U`, `MinPanelSize=15×U`» и «12 = 96 DIP» исходной редакции в модели `n·U − G`
арифметически неверны — действуют значения таблицы выше.)

### Presets (`PanelSizePreset`)

`DefaultPresetSpans` (`FrozenDictionary`): `Small 20×20 → 152×152`, `Medium 32×30 → 248×232`,
`Large 44×40 → 344×312`, `ExtraLarge 60×50 → 472×392` DIP. `Small` — сознательно compact-пресет для мелких
иконок (`IconSize≈32`, ~2×1), не ошибка таблицы. Пресеты — ярлык поверх spans, а не единственные размеры.
Пресет доступен в обоих режимах и **не** меняет `PanelGeometryMode` (единственная точка входа/выхода —
checkable-пункт «Align to workspace grid»); клик по пресету переводит `PanelSizingMode` в `Manual`;
свёрнутая панель пресет игнорирует (подменю «Size» задизейблено).

Отрицательные координаты мониторов обрабатываются на уровне origin: `SpansToBounds`/`SnapPosition` — сложение/
вычитание DIP без знако-чувствительных операций; `(int)`-усечение в писателях заменено на `Math.Round`.

---

## Режимы геометрии

```csharp
public enum PanelGeometryMode { LegacyFreeform = 0, GridAligned = 1 }
```

- Поле per-panel (`ChildWindowModel.GeometryMode`, DTO `geometryMode`/`widthSpanUnits`/`heightSpanUnits`,
  схема v5 — см. ADR-024).
- **Правило загрузки.** `GridAligned` без валидных spans (оба `> 0`) при загрузке **понижается до
  `LegacyFreeform`** с очисткой spans (`PanelStateMapper.FromDto`, единственная точка загрузки панели).
  Отсутствующее/неизвестное значение `geometryMode` → `LegacyFreeform`.
- **Существующие** панели после миграции v4→v5 — `LegacyFreeform` (нет jump on first launch). **Новые**
  панели — по `IAppSettings.DefaultPanelGeometryMode`, дефолт `GridAligned` (решение оркестратора 2026-09-22,
  Вопрос 3 = B; переключатель в «Defaults for new panels»).
- **«Align to workspace grid»** (per-panel checkable, и «Align all panels to grid» в настройках — область:
  **текущая страница**): `GeometryMode ← GridAligned`, позиция — `SnapPosition`, размер — `BoundsToSpans`
  (Ceiling), далее общий anti-overlap. Блокируется `IsLayoutLocked`, но не `NormalModeLayoutGestures`.
  Обратной массовой команды нет.
- **Модификатор `Alt`** при drag **и** resize подавляет квантование живого кадра (читается свежим каждый
  кадр), но не snap-guides и не anti-overlap; **commit всегда на сетке** (`ResolveGridDrop` /
  `PersistSpans → QuantiseResize`). `Alt` не меняет `PanelGeometryMode`.

---

## Правило «IconSize/ViewMode не меняют panel bounds» (F-2)

- `GridAligned` — абсолютно: размер меняют только resize-жест, «Align», пресет, `Fit/AutoContent` (высота).
  Реализовано ранним выходом `PanelSizingGridSupport.IsGridAligned(...)` в обеих копиях `SnapWidthToGrid`.
- `LegacyFreeform` — `SnapResizeToIconGrid` продолжает переснап при смене `IconSize`/`ViewMode`/
  `IconLayoutPolicy` и во время resize-жеста.
- **Загрузка панели никогда не меняет персистентные `Left/Top/Width/Height` — ни в одном режиме.**
  `ChildWindow.Initialize` не вызывает `SnapWidthToGrid()` (P05 fix-цикл MAJOR-1, решение оркестратора;
  исходное решение «init re-snap для `LegacyFreeform` не удаляется» **отменено**, т.к. F-3 сделал его
  видимым скачком 344→352). «Самоисцеление» узкой панели при старте потеряно; если понадобится — явное
  действие «Fit width to grid» (не реализовано, см. «Открытые вопросы»).
- Недостаточная площадь → scroll (`VerticalScrollBarVisibility=Auto`), без новых механизмов скрытия.

---

## Drag, мониторы, multi-monitor

**Конвейер `GridAligned`** (`PanelDragPipeline.ResolveGridFrame`): `SnapPosition` (подавляется `Alt`) →
`SnapGuideCalculator.Snap` (ADR-026, work area **по текущему кадру**, не по монитору старта) →
`PanelCollision.ClampAgainstObstacles(..., gap)` (ADR-017, всегда последний, непробиваем; плюс
`ClampHardWall` — per-axis откат по `CreatesNewOverlap` против реальных препятствий). Порядок «grid →
guides → anti-overlap» обязателен. **`LegacyFreeform`:** `GridSnap` → guides → anti-overlap, без изменений.

**`MonitorDeviceId` на `EndDrag` (F-6):** по **центру** итогового прямоугольника, в **обоих** режимах;
монитор резолвится по сырому rect **до** `SnapToGrid` (`PanelDragPipeline.RetargetToDropMonitor`), иначе
clamp возвращал бы панель на стартовый монитор.

**Hotplug/work-area:** `LegacyFreeform` — `ClampIntoMonitor`; `GridAligned` — `WindowManager.
ResolveGridAlignedBounds`: позиция — абсолютная точка, ре-квантуемая к новому origin
(`SnapPosition(model.Position, newOrigin, U)`, сдвиг ≤ `U/2`; сдвиг work area на целое число units панель не
двигает). `SafeClamp(Rect, Rect, unit, gap)` — наибольший слот сетки, помещающийся в work area, двигает
только позицию; `SafeClampFreeform(Rect, Rect)` — только для `LegacyFreeform`/`ClampIntoMonitor`
(выбор перегрузки привязан к имени метода, не к числу параметров). `OnMonitorsChanged` пропускает запись и
`SaveWindowsState()`, если bounds совпали в пределах 0.5 DIP (`GridAlignedBoundsUnchanged`).

**OQ-6 (закрыт, A).** Исходная формулировка «позиция хранится как spans от origin (индекс слота)»
**отменена**: схема v5 хранит spans только размера; позиция — абсолютная DIP-точка. Индекс слота потребовал
бы нового поля схемы и миграции; не оправдано под-`U/2` выгодой.

**Новая панель** («Add panel») для `GridAligned` — первый свободный слот (`FindFreeSlot`, row-major, с `G`);
лассо конвертирует px→DIP (`LassoPanelGeometry`, `IMonitorService.GetSystemDpiScale()`, закрывает F-7).

---

## Resize

- Входов в конвейер два: `ResizeConstraintsFactory.BuildConstraints(..., PanelGridState)` — правила
  (`SnapCell*=0`, `MinWidth/MinHeight = SpansToBounds(MinSpan)`, не `MinPanelWidth(IconSize)`), и
  `ResizeGestureSession.TryUpdate` — само квантование: `ResizeGeometryCalculator.Apply` →
  `SnapBounds(rect, origin, edge, U, G, minSpan)` → `ClampResizeToWorkArea` → `ClampResizeToSlots`.
  `ResizeGeometryCalculator` **не изменяется и не дублируется** (ADR-021 D-2/R6); bit-for-bit
  эквивалентность Thumb/native сохранена.
- `SnapBounds(resize)`: противоположный (anchor) край неподвижен, если уже на сетке, иначе сначала
  квантуется. `ClampResizeToWorkArea` **ужимает** движущийся край (не двигает панель). На commit span =
  `RoundBoundsToSpans`, в окно/модель/span пишется **ровно** `SpansToBounds(span)` (три источника совпадают).
- Anti-overlap при resize по целым слотам закрывает открытый пункт ADR-017 для `GridAligned`; для
  `LegacyFreeform` остаётся открытым. Снимок препятствий — один на жест, у сессии.
- Контроллеры получают монитор/препятствия/writer через `IPanelSizingGridHost` (реализует `ChildWindow` в
  `ChildWindow.Sizing.cs`); `null`-хост ≡ `PanelGridState.Legacy`.
- Roll-up в `GridAligned`: collapsed-цель = `SpansToBounds((·, CollapsedHeaderSpan)).Height`; grid-состояние
  резолвится против **развёрнутых** bounds, никогда против свёрнутой высоты; span никогда не выводится из
  свёрнутой высоты (`WorkspaceGridPlacement.ExpandedBounds`).
- ESC в нативном sizing-цикле неотличим от завершения (ADR-022 R-8): жест персистит итоговую геометрию,
  включая span.
- Gating — та же таблица `IsLayoutLocked`/`NormalModeLayoutGestures` (ADR-026), без второй машины состояний.

---

## Хром и граница с ADR-029

`PanelMetrics.ContentLeftInset = 8` — единый источник правды горизонтального хрома между `Width` панели и
контентом; `GridMetrics.FileListHPadding = ContentLeftInset`. С ADR-030 колонка accent-индикатора
**упразднена**: те же 8 DIP теперь `2·ChromeOuterInset + 2·ContentSidePadding` (2+2 DIP симметричных отступа
внутри хром-inset). Число не изменилось, поэтому персистентные размеры не затронуты.

---

## Alternatives considered (кратко)

1. Единая глобальная сетка для всех панелей — отклонено (ломает сохранённые layouts, jump on first launch).
2. Ужесточить `GridSnap` до обязательного — отклонено (смешивает глобальную пороговую и per-panel сетки).
3. Объединить системы B и C — отклонено (корень F-3).
4. Хранить `GridAligned` как сырые DIP без spans — отклонено для **размера** (теряется устойчивость к
   смене origin); для **позиции** см. OQ-6 (фактически выбран абсолютный DIP).
5. Новый `ResizeAnchor` enum — отклонено, переиспользован `ResizeEdge`.

---

## Consequences

**Позитивные.** Три системы координат названы, граница B↔C — явный контракт; размер `GridAligned` устойчив к
taskbar/DPI/hotplug; закрыт resize anti-overlap для нового режима; существующие панели не меняются визуально.

**Негативные.** Два набора констант (`U/G` и `CellSize/DefaultMinGap`) поддерживаются раздельно навсегда;
зазор `G=8` не позволяет панелям вставать вплотную (расхождение с Fences, `Gap=0` — продуктовый вопрос);
`U=8` и пресеты — подобранные константы; единственная точка правки — одна константа; `GridAligned`-панель
хранит позицию абсолютной точкой, а не логическим слотом.

---

## Таблица типов (namespace `DesktopOrganizer.UI.Models`, файлы в `src/DesktopOrganizer.UI/Models/`)

| Тип | Роль |
|---|---|
| `PanelGeometryMode`, `PanelSizePreset` | enum'ы |
| `GridSpan` | `(int WidthUnits, int HeightUnits)` — только размер, не позиция |
| `WorkspaceGridMetrics` | константы `UnitSize=8`, `DefaultGap=8`, `MinWidthSpanExpanded=16`, `MinHeightSpanExpanded=12`, `MinWidthSpanCollapsed=16`, `CollapsedHeaderSpan=5`, `MinSpanExpanded/Collapsed`, `DefaultPresetSpans`; `SpanForPreset`, `ValidateGap`, `ValidateSpan` |
| `WorkspaceGridOptions` | параметризация `U/G/PresetSpans` (+`Validate()`) |
| `WorkspaceLayoutPolicy` (static) | `SnapCoordinate`/`SnapPosition`, `PositionToSlot`/`SlotToPosition`, `BoundsToSpans`/`SpansToBounds`/`RoundBoundsToSpans`, `ClampToMinimum`, `SnapBounds` (×2), `ClampResizeToWorkArea`, `ClampResizeToSlots`, `PreferredSize` (×2), `SafeClampFreeform`, `SafeClamp` |
| `PanelGridState` / `PanelGridStateResolver` | резолв режима/монитора/origin/spans (ленивая идемпотентная миграция spans с `ClampToMinimum`) |
| `PanelDragPipeline`, `WorkspaceGridPlacement`, `LassoPanelGeometry` | drag-конвейер, Align/FindFreeSlot/ExpandedBounds, лассо |
| `PanelSizingGridSupport`, `IPanelSizingGridHost` | `QuantiseResize`, `PersistSpans`, `NotifyHeightResizeCommitted` |
| `PanelSizingMode`, `IconLayoutPolicy` | владение ADR-029 |

Все функции `WorkspaceLayoutPolicy`/`WorkspaceGridMetrics` — `static`, без P/Invoke, юнит-тестируемы без STA.
Типы живут в `UI/Models`, а не в `Core`, потому что компонуют `GridSnap`/`PanelCollision`/`SnapGuideCalculator`
(тот же довод, что ADR-021 D-2).

---

## Открытые вопросы

1. **Вопрос 2 — пространство `Window.Left/Top` на мониторе с отрицательными координатами** (унаследован из
   P00, не решён). На Win11 24H2+ панели top-level (ADR-034/037), `SetParent(WorkerW)` не применяется; в
   Embedded-режиме (Win10/≤23H2) проверки не было, стенда с двумя мониторами нет (ADR-037, QF-1).
2. **`Gap = 0`.** Разрешать ли нулевой зазор (Fences-паритет «вплотную») — продуктовое решение; `WorkspaceGridOptions.Gap`
   параметризован, UI нет.
3. **«Fit width to grid».** Явное действие вместо удалённого init re-snap для `LegacyFreeform` — не реализовано.
4. Позиция как логический слот (`ColSlot/RowSlot`) — отклонено OQ-6, пересмотр требует миграции схемы.

---

## Amendments

| Дата | Источник | Что изменилось | Что больше не действует |
|---|---|---|---|
| 2026-09-22 | P05 fix-цикл MAJOR-1 (ADR-029) | `ChildWindow.Initialize` не вызывает `SnapWidthToGrid()` | «Переснап при старте не удаляется» для `LegacyFreeform` |
| 2026-09-22 | P04 | `MinWidthSpanExpanded` 12 → 16; `CollapsedHeaderSpan` 4 → 5 | Таблица минимумов 12/4 исходной редакции |
| 2026-09-22 | P02 fix-цикл 2 | `SafeClamp(Rect,Rect)` → `SafeClampFreeform`; `Validate(unitSize,gap)` → `ValidateGap` | Старые имена |
| 2026-09-22 | OQ-6 | Позиция `GridAligned` — абсолютная DIP-точка, не индекс слота | Формулировка «позиция как spans от origin» |
| 2026-09-24 | [ADR-011](ADR-011-manual-drag-with-magnetic-grid-snap.md) | `GridSizeNormalizer`: при `GridSize > 1` restore может сдвинуть панель до шага сетки — единственное исключение из «нет jump on first launch» | Абсолютность DoD «нет jump» при `GridSize > 1` после явной смены настройки |
| 2026-09-25 | [ADR-030](ADR-030-reference-panel-chrome.md) | Колонка accent упразднена; `ContentLeftInset = 2·ChromeOuterInset + 2·ContentSidePadding` | «Колонка 8 DIP `PanelChrome`» |
| 2026-09-27 | [ADR-037](ADR-037-permonitor-v2-dual-mode-desktop-scale.md) | PerMonitorV2; «window space»/`DesktopCoordinateSpace` — desktop-DIP | «DPI-awareness остаётся System-DPI (ADR-004/022 D-6)» |
| 2026-10-09 | ADR-audit WP-E | Статус `Accepted`; журнал вынесен в `docs/PLANS/`; исправлены ошибки тела | — |
