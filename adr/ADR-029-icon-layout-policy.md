# ADR-029: Icon Layout Policy — FixedGap vs LegacyStretch, учёт хрома, PanelSizingMode

**Статус:** Accepted (2026-10-09, ADR-audit WP-E) — Amended 2026-09-25 (ADR-030: колонка accent упразднена, `ContentLeftInset` пересобран без смены числа); Amended 2026-10-09 (ADR-011 amendment, ADR-audit WP-C: `SnapWidthToGrid()` не подгоняет ширину к сетке значков при `GridSize > 1` — единое правило `IsIconGridSnapActive`)
**Реализация:** 2026-09-22 (P01–P06 + fix-циклы); manual verify выполнена на Windows 11 24H2 2026-09-27
(`docs/ROADMAP.md`, строки 6–8, 903–909), признана достаточной; Windows 10 проект не проверяет.
**Дата:** 2026-09-22 (переписан 2026-10-09)
**Журнал реализации:** [docs/PLANS/icon-layout-policy-adr029-implementation-log.md](../docs/PLANS/icon-layout-policy-adr029-implementation-log.md)
(P05/P06, fix-циклы, manual-verify-список пп. 22–29 — перенесено без потерь).
Парный ADR — [ADR-028](ADR-028-workspace-grid.md).

> **Примечание о редакции.** Исходный текст (~1280 строк) на 65% состоял из журнала. В этой редакции — только
> действующие решения. Исправлено: `ContentLeftInset` больше не «колонка accent» (ADR-030); резерв ширины
> скроллбара берётся из `SystemParameters.VerticalScrollBarWidth`; висячая ссылка «Q4» заменена на Q3;
> «init re-snap» отменён; пробел «`SnapWidthToGrid` не различает `GeometryMode`» закрыт ADR-028. Предыдущая
> редакция — в истории git.

---

## Контекст

Аудит P00 (`docs/PLANS/workspace-grid-00-baseline.md`):

- **F-5** — растянутый зазор полного ряда (ADR-018) делает шаг между иконками зависимым от ширины панели.
- **F-3** — снап-код считал доступную ширину как `Window.Width − GridMetrics.FileListHPadding` при
  `FileListHPadding = 0`, а `PanelChrome` отдавал контенту на 8 DIP меньше → `RowCapacity` в снап-коде
  завышен на колонку относительно реальной раскладки `AdaptiveIconGridPanel`.
- **F-11** — персистентный дробный `IconSize` вне тиков слайдера (обе политики корректны при любом `double`).
- **F-4** — высота панели отвязана от контента → режим `PanelSizingMode`.

ADR-028 владеет workspace-сеткой панели (системы A/B); этот ADR — третьей, внутренней системой.

---

## D-0: третья система координат — внутренняя раскладка иконок

DIP, относительно **content origin** (левый верхний угол контента после вычитания хрома `PanelChrome`).
**Инвариант:** внутренняя раскладка никогда не определяет bounds панели в `GridAligned`; число колонок —
чистая функция уже заданной снаружи ширины. Единственные исключения («от содержимого к высоте окна», никогда
к ширине): действие `Fit panel to content` и режим `AutoContent` (Решение 7).

---

## Решение 1: `IconLayoutPolicy { LegacyStretch = 0, FixedGap = 1 }`

- **`LegacyStretch`** — математика ADR-018 bit-for-bit (`LayoutTolerance = 0.5`, M-1, M-2 принят как
  компромисс): полный ряд растягивает `RowGap` на остаток, неполный — `pmin`.
- **`FixedGap`** — одинаковые слоты (`CellSize × RowHeight`, формулы не меняются) и **фиксированный** зазор
  `pmin` (`AdaptiveGridLayout.DefaultMinGap = 4`) независимо от ширины панели. Это ветка «неполного ряда»
  `LegacyStretch`, применённая ко **всем** рядам; новой формулы зазора нет. Surplus справа (центрирование
  отклонено: сделало бы позицию 1-й иконки зависимой от ширины). Реализация — перегрузка
  `AdaptiveGridLayout.RowGap(..., IconLayoutPolicy)`; шести-аргументная перегрузка — «`LegacyStretch`-версия» —
  не изменена. `AdaptiveIconGridPanel.LayoutPolicy` — DP (дефолт `LegacyStretch`), в шаблоне привязан к
  `Model.IconLayoutPolicy`.

## Решение 2 (F-3): `PanelMetrics.ContentLeftInset` — единая константа хрома

`PanelMetrics.ContentLeftInset = 8d`; `GridMetrics.FileListHPadding = PanelMetrics.ContentLeftInset` (было
независимое `0d`). Константа, а не измерение `PART_ContentHost.ActualWidth`: снап обязан выдать целевую
`Window.Width` **до** layout-прохода (иначе циклическая зависимость).

> **Состояние после ADR-030.** Колонка `PART_AccentIndicator` (8 DIP) **упразднена**. Число не изменилось:
> `ContentLeftInset = 2·ChromeOuterInset + 2·ContentSidePadding` (2+2 DIP симметричных отступа внутри
> хром-inset; `ContentSidePadding = (ContentLeftInset − 2·ChromeOuterInset)/2`), закреплено
> `PanelMetricsTests`. Персистентные размеры панелей остались валидными. Упоминания «колонки accent» в
> исходной редакции и в журнале — историческая формулировка.

Фикс действует для **обеих** политик (Q2 = A): это баг учёта хрома, а не часть математики «stretch vs
fixed». Он применяется только там, где снап и так должен произойти (resize-жест, смена `IconSize`/
`ViewMode`/`IconLayoutPolicy`), **никогда пассивно при загрузке** — см. «Загрузка и init re-snap».

## Решение 3: `FixedGap` не создаёт второй системы отступов

Переиспользуются `GridMetrics.ItemMargin = 6` и `AdaptiveGridLayout.DefaultMinGap = 4`; видимый шов
идентичен шву неполного ряда (~16 px при `IconSize=48`) на любом ряду. Отдельного DP/константы «FixedGap gap»
заводить запрещено.

## Решение 4: недостаточная площадь — scroll; резерв ширины скроллбара

`ScrollViewer.VerticalScrollBarVisibility="Auto"` — единственный механизм. Для `FixedGap` скачок колонок на
пороге скроллбара (ADR-018 M-2) мог быть резче, поэтому реализован вариант (a):

- `GridMetrics.ReservedVerticalScrollBarWidth(policy, w)` = `w` для `FixedGap`, `0` для `LegacyStretch`;
  `GridMetrics.ContentHorizontalChrome(policy, w) = FileListHPadding + ReservedVerticalScrollBarWidth(...)` —
  единая конверсия window↔content width для всего снап-кода (`SnapWidthToGrid` ×2,
  `ResizeConstraintsFactory`, `MinPanelWidth(iconSize, policy, w)`).
- Значение `w` — **`SystemParameters.VerticalScrollBarWidth`** (системное, 17 DIP по умолчанию),
  передаётся параметром (класс остаётся WPF-свободным). Панельный overlay-скроллбар имеет ширину
  **14 DIP** (`ScrollBar.Width`, `Resources/Styles/Spacing.xaml`); резерв **сознательно не привязан** к
  14: переоценка в 3 DIP ничего не стоит визуально, а привязка layout-математики к XAML-токену
  инвертировала бы правило «C#-константа — единственный источник правды» (комментарий в `Spacing.xaml`).
- **`AdaptiveIconGridPanel.CapacityWidth`** (DP, дефолт `NaN`): шаблон привязывает его к `ActualWidth`
  `ScrollViewer` — стабильной внешней ширине, не урезаемой собственным скроллбаром. Формула
  `ResolveAvailableForCapacity`: `reserved > 0 && !NaN(CapacityWidth) ? Max(0, CapacityWidth − reserved) :
  rawWidth`. Резерв вычитается **один раз** (иначе двойное вычитание давало прежний скачок). `LegacyStretch`
  `CapacityWidth` игнорирует (его M-2 — принятый компромисс). Без привязки (`NaN`) — деградация до `rawWidth`
  без резерва.
- Резерв влияет только на `RowCapacity`; `DesiredSize` и позиционирование (surplus справа) не уменьшаются.
  `LayoutPolicy` DP — `AffectsMeasure | AffectsArrange` обоснованно (обе фазы).
- В List-режиме `BuildConstraints` резолвит `LegacyStretch`, резерв не просачивается.

## Решение 5: инварианты, не зависящие от политики

Drag-reorder hit-testing (только `Arrange`, без `RenderTransform`; промах мимо контейнера → **ближайший по
горизонтали** контейнер того же ряда, `FindNearestContainerInSameRow`), selection-chrome внутри ячейки,
inline-rename, List-режим (`ListViewTemplate` не использует `GridMetrics`/`AdaptiveGridLayout`/
`IconLayoutPolicy`), tooltips. **Виртуализации Grid-режима нет** (как и у заменённого `WrapPanel`; ADR-046 —
фоновая загрузка иконок, но не виртуализация; следы в «Открытых вопросах»).

## Решение 6: per-panel поле, миграция и дефолты

- `ChildWindowModel.IconLayoutPolicy` — per-panel (DTO `appearance.iconLayoutPolicy`), плюс
  `IAppSettings.DefaultIconLayoutPolicy` для **новых** панелей, дефолт `FixedGap` (`Settings.settings`).
- Миграция v4→v5 (`V4ToV5Migration`) явно проставляет существующим панелям `iconLayoutPolicy = LegacyStretch`,
  `sizingMode = Manual`, `geometryMode = LegacyFreeform` — не полагаясь на дефолт класса.
- Парсеры (`IconLayoutPolicyParser.ParseOrLegacyStretch`, `PanelSizingModeParser.ParseOrManual`,
  `PanelGeometryModeParser.ParseOrLegacyFreeform`) делают неизвестные значения безопасными.
- **Правило загрузки (ADR-028):** `GridAligned` без валидных spans при загрузке понижается до `LegacyFreeform`
  (`PanelStateMapper.FromDto`) — к политике раскладки не относится, но записано здесь как единая точка
  согласованности полей геометрии.
- UI: пункт «Icon layout ▸» в меню панели (radio + «Apply to all panels…»); дефолты `PanelGeometryMode`/
  `IconLayoutPolicy` для новых панелей — раздел «New panel defaults» окна настроек
  (`NewPanelDefaultsSection.xaml`, шаблон `Settings.EnumRadioLabel`; исходные `ComboBox`-ы P06 заменены
  редизайном окна настроек, ADR-031/042/047). Глобального дефолта `SizingMode` **нет** —
  новая панель берёт дефолт модели `Manual`.

## Решение 7: `PanelSizingMode { Manual = 0, AutoContent = 1 }` + `Fit panel to content`

- **`Fit panel to content`** — одноразовое действие, работает независимо от режима; **`AutoContent`** —
  персистентный per-panel режим, та же формула при каждом триггере. Только **Grid-режим** (Q3 = B); в
  List-режиме оба no-op и задизейблены.
- Формула (`PanelContentSizer.FitHeight`, WPF-независимый; каждый шаг переиспользует `GridMetrics`/
  `AdaptiveGridLayout`/`WorkspaceLayoutPolicy`):

```
columnCapacity   = AdaptiveGridLayout.RowCapacity(contentWidth, cell, minGap, m)
                   // contentWidth = Window.Width − GridMetrics.ContentHorizontalChrome(policy, SystemParameters.VerticalScrollBarWidth)
contentRows      = AdaptiveGridLayout.RowCount(itemCount, columnCapacity)
displayRows      = itemCount == 0 ? 1 : contentRows        // пол: видимая drop-zone
FitContentHeight = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding
                 + AdaptiveGridLayout.Extent(displayRows, GridMetrics.RowHeight(iconSize), minGap, verticalEdgePadding)
```

  (Черновик использовал голый `ContentLeftInset` — исправлено на policy-aware chrome, иначе `FixedGap`
  считал бы на колонку больше, чем рисует панель.) Вертикальный зазор никогда не растягивается.
- **Однонаправленность:** `columnCapacity` из текущей ширины → результат пишется **только** в `Window.Height`.
- **Округление:** `LegacyFreeform` — `Math.Ceiling`; `GridAligned` — `BoundsToSpans`/`SpansToBounds` по высоте с
  полом `WorkspaceGridMetrics.MinHeightSpanExpanded`. Если `PanelGridStateResolver.Resolve` вернул
  `Legacy` (нет мониторов/невалидные bounds) — применяется `LegacyFreeform`-округление и span **не**
  записывается.
- **Триггеры `AutoContent`:** изменение `FileItems`, смена `IconSize`/`ViewMode`/`IconLayoutPolicy`/`SizingMode`
  (после существующего re-snap — порядок подписки важен), ручной resize по ширине (`Window.SizeChanged`,
  debounce 200 мс, не посреди активного жеста по `PanelInteractionTracker` и не во время roll-анимации
  `RollUpBehavior.IsAnimating`), повторная активация панели (`IsVisibleChanged`, кроме самого первого
  показа). Смена screen/DPI — отдельного триггера нет. Roll-up не триггер: при `IsRolledUp` пишутся
  `ExpandedHeight`/`Model.Size`, не `Window.Height`. Включение `AutoContent` сразу считает fit; выключение
  высоту не трогает. `Manual` высоту не меняет никогда.
- **Демотирование:** завершённый resize, затронувший высоту (`ResizeEdge.AffectsHeight`), переводит
  `AutoContent → Manual` (`IPanelSizingGridHost.NotifyHeightResizeCommitted`); resize только по ширине не
  демотирует. Клик по пресету размера ADR-028 тоже переводит в `Manual`. Диагностика —
  `IGestureDiagnostics.SizingModeChanged`.
- **`IsLayoutLocked` (ADR-026):** явные действия (`Fit`, `Auto-size`, обе «Apply to all panels…») блокируются;
  пассивный пересчёт `AutoContent` — нет.
- **«Apply to all panels…»** — все панели **на всех страницах**, с подтверждением (`IConfirmationService`);
  текст называет «all pages» и число панелей.
- Меню: подменю «Panel sizing ▸» (Fit / Auto-size / Apply to all) симметрично «Icon layout ▸».
- **Fences-паритет** для `AutoContent`/`Fit` не заявляется — bespoke-функция (формула выведена из
  `GridMetrics`, а не из Fences).

---

## Загрузка и init re-snap (отмена исходного решения)

`ChildWindow.Initialize` **не вызывает** `SnapWidthToGrid()` ни в одном режиме: F-3 сделал бы из no-op
скачок (344→352 DIP при `IconSize=48`). Загрузка панели никогда не меняет `Left/Top/Width/Height`. Переснап
остаётся только в явных действиях: resize-жест, смена `IconSize`/`ViewMode`/`IconLayoutPolicy`
(`OnModelPropertyChanged`). `GridAligned`-панель переснапу не подвергается — ранний выход
`PanelSizingGridSupport.IsGridAligned(...)` в обеих копиях `SnapWidthToGrid` (ADR-028); пробел «не различает
`GeometryMode`» из журнала P05 закрыт.

---

## Таблица инвариантов/API (фактические имена)

| Компонент | Комментарий |
|---|---|
| `IconLayoutPolicy`, `PanelSizingMode` | enum'ы `DesktopOrganizer.UI.Models` |
| `AdaptiveGridLayout.RowGap(…, IconLayoutPolicy)` | единственная точка влияния политики на чистую математику |
| `AdaptiveIconGridPanel.LayoutPolicy` / `CapacityWidth` | DP; `CapacityWidth` привязан в `GridViewTemplate` к `ActualWidth` `ScrollViewer` |
| `PanelMetrics.ContentLeftInset` (+`ContentLeftInsetColumnWidth`) | `8d`, см. ADR-030 |
| `GridMetrics.FileListHPadding`, `ReservedVerticalScrollBarWidth`, `ContentHorizontalChrome`, `MinPanelWidth(iconSize, policy, w)` | цепочка хрома |
| `PanelContentSizer.FitHeight` | чистая формула Решения 7 |
| `ChildWindowModel.IconLayoutPolicy`, `SizingMode` | INPC, персистентно (схема v5, ADR-024) |
| `IAppSettings.DefaultIconLayoutPolicy` | дефолт `FixedGap`, только для новых панелей |
| `ChildWindow.ContentSizing.cs` | партиал: Fit/Auto-size/Apply-to-all, триггеры |

Единицы измерения — DIP везде.

---

## Alternatives (кратко)

- **IconLayoutPolicy:** выбран per-panel + глобальный дефолт; отклонены: единый глобальный переключатель
  (скачок всех мигрированных панелей), тихая полная замена (`LegacyStretch` удалялся бы), эвристическое
  авто-определение.
- **Учёт хрома:** выбрана константа; отклонены: измерение `PART_ContentHost.ActualWidth` (цикл),
  `Auto`-колонка (джиттер ~2.1 px, ADR-025 MAJOR-1).
- **Surplus `FixedGap`:** выбрано «справа»; отклонены центрирование и равное распределение по краям.

---

## Последствия

**Позитивные.** Предсказуемый шаг иконок (`FixedGap`, устранение F-5); F-3 устранён единой константой;
`AutoContent`/`Fit` закрывает F-4 для тех, кто его выберет.

**Компромиссы.** Неровный правый край `FixedGap` (0…почти `cell+pmin`); `LegacyStretch` сохраняет M-2;
существующие панели не получают `FixedGap` автоматически (`LegacyStretch` по миграции); панельный
скроллбар (14 DIP) уже системного (17) — резерв ширины на 3 DIP больше нужного.

---

## Открытые вопросы

- **Q3 (решён B).** `AutoContent`/`Fit` — только Grid-режим; List остаётся `Manual` (нет аналитической высоты
  строки List). Отдельный follow-up ADR, если понадобится.
- **m-3 (debounce).** Каждый `CollectionChanged` синхронно вызывает `MaybeAutoFit → ApplyFitHeight`
  (для `GridAligned` — `GetMonitors()` + недебаунсенный `SaveWindowsState()`); предложение дебаунсить все
  триггеры отложено — затрагивает контракт синхронного пересчёта и принятые тесты.
- **Виртуализация Grid-режима** (200+ элементов) — не решена; фоновая загрузка иконок — ADR-046.

(Q1 и Q2 закрыты: Q1 — per-panel + глобальный дефолт; Q2 = A — F-3 к обеим политикам, без пассивного
re-snap. Висячие ссылки «Q4» исходной редакции относились к List-режиму = Q3; «MV-19» относится к плану
ADR-028, а не к этому ADR.)

---

## Amendments

| Дата | Источник | Что изменилось | Что больше не действует |
|---|---|---|---|
| 2026-09-22 | P05 fix-цикл MAJOR-1 | `Initialize` не вызывает `SnapWidthToGrid()` | «Загрузка может подтягивать ширину к F-3-точке» |
| 2026-09-22 | P05 fix-цикл 2 | `CapacityWidth`; триггер `IconLayoutPolicy` в `OnModelPropertyChanged` | Прямое вычитание резерва из `availableSize` |
| 2026-09-22 | P06 | `columnCapacity` использует `ContentHorizontalChrome`, не голый `ContentLeftInset` | Черновик формулы Решения 7 |
| 2026-09-22 | P06 fix-цикл | `AutoContent → Manual` при вертикальном resize; gating `IsLayoutLocked`; «Panel sizing ▸» | — |
| 2026-09-25 | [ADR-030](ADR-030-reference-panel-chrome.md) | Колонка accent-индикатора упразднена; `ContentLeftInset = 2·ChromeOuterInset + 2·ContentSidePadding` (число 8 то же) | Формулировка «колонка 8 DIP `PART_AccentIndicator`» (Решение 2, XAML-биндинг `ContentLeftInsetColumnWidth` на accent-колонку) |
| 2026-09-27 | [ADR-037](ADR-037-permonitor-v2-dual-mode-desktop-scale.md) | PerMonitorV2; внутренняя раскладка — DIP, не затронута | — |
| 2026-10-09 | ADR-audit WP-E | Статус `Accepted`; журнал вынесен в `docs/PLANS/` | — |
| 2026-10-09 | [ADR-011](ADR-011-manual-drag-with-magnetic-grid-snap.md) (ADR-audit WP-C) | `SnapWidthToGrid()` (триггеры `IconSize`/`ViewMode`/`IconLayoutPolicy`, «Загрузка и init re-snap» выше) подчиняется общему правилу `ResizeConstraintsFactory.IsIconGridSnapActive`: при `GridSize > 1` подгонки к ячейке сетки значков нет. Тумблер `SnapResizeToIconGrid` в настройках отключён при позиционной привязке (`IsSnapResizeToIconGridAvailable`) | Допущение, что повторная подгонка ширины после смены политики/размера значка зависит только от `SnapResizeToIconGrid` и режима геометрии |
