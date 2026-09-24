# ADR-029: Icon Layout Policy — FixedGap vs LegacyStretch, учёт хрома, PanelSizingMode

**Статус:** Proposed (Решения 1–5 landed P01/P05; Решение 6 landed P02b; Решение 7 + UI-часть Решения 6
landed P06 — см. «Реализация P06»/«Fix-цикл P06» ниже; статус НАМЕРЕННО возвращён с `Accepted` на
`Proposed` fix-циклом P06 — см. NIT там же — и остаётся так до релизной сверки P09,
`docs/fix-prompts/09-acceptance.md`)
**Implemented 2026-09-22 (P02–P06), Accepted pending manual verify (Win10/Win11) — см. P09** — та же
причина и тот же gate, что у ADR-028 (парный документ этого пакета): реализация и тесты завершены, но
manual verify (`FixedGap`/`AutoContent`/«Fit panel to content» на живом экране, пп. 1–29 ниже) не
выполнен в этой среде. Шапка и `adr/README.md` согласованы: нигде в этом документе статус не
зафиксирован как `Accepted` — единственное упоминание `Accepted` в тексте ниже (раздел «Fix-цикл P06»,
NIT) описывает именно ошибочную предыдущую пометку, которая была возвращена на `Proposed`.
**Дата:** 2026-09-22

---

## Контекст

Этап P00 (`docs/PLANS/workspace-grid-00-baseline.md`) зафиксировал измерением кода (не визуально):

- **F-5** — растянутый зазор полного ряда (ADR-018) делает шаг между иконками зависимым от ширины
  панели: одна и та же «конфигурация иконок» (тот же `IconSize`, то же число элементов) даёт разный
  видимый шов на панелях разной ширины. Прямо конфликтует с ADR-018 D-0/спеком, где растяжка полного
  ряда — осознанное решение, а не баг.
- **F-3** — снап-код (`ResizeGeometryCalculator`/`PanelResizeController.SnapWidthToGrid`) считает
  доступную ширину контента как `Window.Width − GridMetrics.FileListHPadding` (`FileListHPadding = 0`),
  но `PanelChrome` (`Panels.xaml`, Фаза 5/ADR-025) отдаёт контенту (`PART_ContentHost`, колонка `*`)
  только `Window.Width − 8`: колонка `Width="8"` слева зарезервирована под accent-индикатор
  (`PART_AccentIndicator`), фиксирована намеренно (ADR-025 MAJOR-1: `Auto`-колонка давала ~2.1px
  джиттер контента на каждом interaction-переходе). Итог: `RowCapacity` в снап-коде систематически
  завышен на одну колонку относительно того, что реально отрисует `AdaptiveIconGridPanel` внутри
  фактической колонки `*`.
- **F-11** — персистентный дробный `IconSize` (`DefaultIconSize`, per-panel `iconSize`) не лежит на
  тик-сетке слайдера; причина не установлена, но `AdaptiveGridLayout.LayoutTolerance`/`CountForExtent`
  уже спроектированы устойчивыми к дробному входу — этот ADR не меняет это поведение, только
  подтверждает, что обе политики ниже (`LegacyStretch`/`FixedGap`) остаются корректны при любом
  `double IconSize`.
- **F-4** — высота панели полностью отвязана от контента (persisted height никогда не пере-снапается
  пассивно); это отдельная, но смежная проблема — предмет режима `PanelSizingMode` этого же документа.

Параллельно `adr/ADR-028-workspace-grid.md` (другой агент этого этапа) владеет **workspace panel
grid** — сеткой позиционирования/размера самой панели (`PanelGeometryMode {LegacyFreeform,
GridAligned}`, origin per monitor, spans/presets, версия схемы). Этот документ **не переопределяет**
ADR-028 и не трогает `adr/README.md` — только фиксирует третью, внутреннюю систему координат и её
границу с ADR-028.

---

## Решение D-0: третья система координат — внутренняя раскладка иконок

Три независимые системы координат проекта (ADR-028 определяет первые две, этот ADR — третью):

1. **Physical monitor geometry** (px/DIP, per-monitor origin) — ADR-028.
2. **Workspace panel grid** (`PanelGeometryMode`, spans панели как целого окна) — ADR-028.
3. **Internal icon layout** (этот ADR) — DIP, относительно **content origin** (левый верхний угол
   контента ПОСЛЕ вычитания хрома `PanelChrome`, см. Решение F-3 ниже), т.е. `(0,0)` этой системы —
   не `(0,0)` окна панели и тем более не origin монитора.

**Инвариант.** Внутренняя раскладка иконок **никогда не определяет** bounds панели в
`PanelGeometryMode.GridAligned`: число колонок/рядов иконок — чистая функция уже готовой (снаружи
заданной) ширины контента, а не источник этой ширины. В `GridAligned` панель занимает span workspace-
сетки (ADR-028), а иконки внутри просто заполняют то, что получилось. В `LegacyFreeform` панель по-
прежнему меняет размер через drag/resize-жест как сегодня, и иконки так же остаются чистым
потребителем итоговой ширины/высоты — единственное исключение, симметричное в обоих режимах
геометрии панели, — явное действие `Fit panel to content` и режим `PanelSizingMode.AutoContent`
(Решение 7), где направление всегда «от содержимого к высоте окна», никогда не «от содержимого к
ширине» (см. там же про однонаправленность).

---

## Решение 1: `IconLayoutPolicy { LegacyStretch = 0, FixedGap = 1 }`

```csharp
public enum IconLayoutPolicy
{
    LegacyStretch = 0,
    FixedGap = 1,
}
```

### `LegacyStretch`

Математика ADR-018 **bit-for-bit**, включая `AdaptiveGridLayout.LayoutTolerance = 0.5`, M-1 (потеря
колонки при недостаче <1px, `Math.Ceiling` при снапе) и M-2 (скачок колонок на пороге появления
скроллбара — сознательно принятый trade-off, не переоткрывается этим ADR). Полный ряд растягивает
`RowGap` на весь остаток; неполный — `pmin`. Это флаг, а не молчаливая замена: физически —
`AdaptiveGridLayout.RowGap(...)`'s существующая ветка `itemsInRow == rowCapacity > 1` со стретчем.

### `FixedGap`

Одинаковые слоты (`CellSize(iconSize)` × `RowHeight(iconSize)` — те же аналитические формулы
`GridMetrics`, ячейка не меняется) и **фиксированный** горизонтальный/вертикальный зазор `pmin`
(`AdaptiveGridLayout.DefaultMinGap`) для одной конфигурации иконок (тот же `IconSize`, тот же порядок
элементов) **независимо от ширины панели**.

**Ключевое архитектурное наблюдение** (минимизирует новый код): `FixedGap` — это в точности сегодняшняя
ветка «неполного ряда» (`RowGap` возвращает `minGap`, когда `itemsInRow < rowCapacity`), применённая
**безусловно и к полному ряду тоже**. Никакой новой формулы зазора не требуется — только отключение
ветки `stretched` в `RowGap` для этой политики. Прямое следствие:

- позиции 1-й и 2-й иконки одного ряда — `Offset(0, cell, pmin, m)` и `Offset(1, cell, pmin, m)` —
  зависят только от `cell`, `pmin`, `m` (все три — функции `IconSize`, не ширины панели), поэтому
  **не зависят от ширины панели** при одинаковом `IconSize` — ровно требование DoD P05;
- последний неполный ряд и так уже не растягивался при `LegacyStretch` — при `FixedGap` **ни один**
  ряд (включая полные) не растягивается;
- surplus space остаётся **справа** (тот же способ, каким уже сегодня обрабатывается неполный ряд —
  `RowCapacity` колонок начинаются от `m` и идут подряд, остаток ширины после последней колонки не
  используется). **Центрирование остатка отклонено**: оно потребовало бы новой, третьей формулы `m`,
  зависящей от `itemsInRow` в конкретном ряду (а не только от `cell`), что нарушило бы «позиция 1-й/2-й
  иконки не зависит от ширины панели» для рядов, где `itemsInRow < rowCapacity` — именно то требование,
  ради которого вводится `FixedGap`. Прижатие влево/остаток справа — уже отработанный, протестированный
  (существующий) частный случай, не новый риск.

---

## Решение 2 (F-3): `PanelMetrics.ContentLeftInset` — единая константа контракта на хром

**Диагноз.** Хром `PanelChrome`'s accent-колонки (8 DIP, `Panels.xaml`, фиксирован намеренно — см.
Контекст) съедает часть ширины окна, доступную контенту, но это НЕ отражено в `GridMetrics.
FileListHPadding = 0`, которым пользуется весь снап-код (`ResizeGeometryCalculator`, `PanelResizeController.
SnapWidthToGrid`). `AdaptiveIconGridPanel` сам по себе **не затронут этим багом** — он получает
`availableSize`/`finalSize` уже от реального `Grid`-layout WPF (колонка `*`), то есть корректную
`W − 8` естественным путём. Баг живёт только там, где ширина контента вычисляется **аналитически**, до
прохода реального layout — в снап-формулах.

**Решение.** Ввести `PanelMetrics.ContentLeftInset : double = 8d` (и, по аналогии с уже существующей
парой `HeaderHeight`/`HeaderRowHeight`, отдельный `PanelMetrics.ContentLeftInsetColumnWidth :
GridLength`, если `Panels.xaml` когда-либо перейдёт с литерала `Width="8"` на `x:Static`-биндинг —
сейчас это вне scope P01, документируется для P02/P04/P05 как обязательная сверка при правке XAML).
`GridMetrics.FileListHPadding` **перестаёт быть независимой константой `0`** и становится ссылкой:

```csharp
public const double FileListHPadding = PanelMetrics.ContentLeftInset; // было: 0d
```

Это не новая концепция — doc-комментарий `FileListHPadding` уже сегодня явно предвидит этот случай
(«Still added back to the quantised content width... so the formula stays correct if this ever needs
to become non-zero again»). Единственный источник правды остаётся один (`PanelMetrics`), как и для
`HeaderHeight`.

**Почему константа, а не измерение (`PART_ContentHost.ActualWidth`).** Отклонено: снап-код должен
выдать целевую `Window.Width` **до** прохода реального layout (иначе — циклическая зависимость
измерение↔снап, тот же класс проблемы, что и M-2 «Auto-скроллбар меняет то, от чего сам зависит»).
Колонка `8` зафиксирована в `Panels.xaml` намеренно как верхняя граница (не «Auto», см. ADR-025
MAJOR-1) именно чтобы быть безопасной константой контракта, а не переменной величиной — измерение не
даёт здесь ничего, чего не даёт константа, но добавляет риск лишнего layout-прохода/джиттера.

**Действует для ОБЕИХ политик.** Это баг-фикс учёта хрома, а не часть математики «stretch vs
fixed-gap» — применяется одинаково к `LegacyStretch` и `FixedGap`. Обоснование, что это не нарушает
«legacy воспроизводим под явным флагом»: гарантия «легаси воспроизводим» относится к **формуле
распределения зазора** (полный ряд растягивается, неполный — нет), а не к тому, насколько точно снап-
код угадывал реальную ширину контента ДО этого фикса. F-3 — рассинхрон между тем, что снап **считает**
доступным, и тем, что реально доступно; после фикса `RowCapacity` в снап-коде наконец совпадёт с тем,
что фактически отрисует `AdaptiveIconGridPanel` — то есть перестанет систематически терять колонку,
которую сам же и рисует. Прецедент такого «одноразового пересдвига точки снапа как принятого,
задокументированного изменения» уже есть в самом ADR-018 (`DefaultMinGap` 0→4). Это же явно
поставлено как открытый вопрос ниже (см. «Открытые вопросы», Q2), так как альтернатива (фикс только
для `FixedGap`) тоже защитима.

---

## Решение 3: двойной счёт spacing — `FixedGap` не создаёт вторую систему отступов

`GridMetrics.ItemMargin = 6` (chrome одной ячейки, ADR-015: 5px margin шаблона + 1px рамка выделения)
и `AdaptiveGridLayout.DefaultMinGap = 4` (`pmin`, зазор ПОВЕРХ этого chrome) — уже сегодня два разных,
явно задокументированных слоя (видимый шов неполного ряда = `6+6+4 = 16px`, ADR-018). **Правило для
`FixedGap`: он переиспользует ровно те же две константы и ту же композицию**, не вводит третий,
конкурирующий параметр «межиконочный интервал». Раскладка `FixedGap` = ветка «неполного ряда»
`LegacyStretch`, применённая всегда — при этом `ItemMargin` не меняется, `DefaultMinGap` не меняется,
итоговый видимый шов идентичен сегодняшнему шву неполного ряда (~16px при `IconSize=48`) на **любом**
ряду, а не только на последнем. Явно запрещено: заводить отдельный DP/константу «FixedGap gap»,
independent от `AdaptiveIconGridPanel.MinGap` — единственная переменная, которая должна появиться, —
это сам `IconLayoutPolicy` (переключатель ветки `RowGap`), не новое число.

---

## Решение 4: недостаточная площадь — scroll, не clip

Без изменений относительно ADR-018: `ScrollViewer.VerticalScrollBarVisibility="Auto"` остаётся
единственным механизмом для контента, не помещающегося по высоте; ни при `LegacyStretch`, ни при
`FixedGap` содержимое не обрезается и не скрывается — обе политики участвуют в M-2 одинаково.

**Реоценка M-2 для `FixedGap`.** При `LegacyStretch` появление/исчезновение скроллбара на пороге
меняет **величину растяжки** зазора (визуально мягче — тот же набор колонок выглядит чуть плотнее/
свободнее). При `FixedGap` тот же порог меняет **пустое пространство справа** (та же ширина колонки,
тот же зазор — просто на одну колонку меньше и заметно больше surplus space) — потенциально более
резкий, «дёргающийся» визуальный эффект, чем при `LegacyStretch`, потому что ничего не поглощает
разницу растяжкой. Это не блокирует принятие `FixedGap` (M-2 уже принят как компромисс в ADR-018,
вариант (c)), но **повышает** приоритет вариантов (a)/(b) из ADR-018 (резервировать ширину скроллбара
всегда / скроллбар всегда виден) именно для панелей с `FixedGap` — зафиксировано как рекомендация
для последующего визуального ревью (не блокирует P05), не как новое решение этого документа.

**Amendment (P05 fix-cycle, 2026-09-22): вариант (a) реализован для `FixedGap`.** Оркестратор принял
рекомендацию выше по итогам code-review P05: `FixedGap` теперь резервирует
`SystemParameters.VerticalScrollBarWidth` в расчёте `RowCapacity` **безусловно** — независимо от того,
показан ли реальный скроллбар в данный момент. `LegacyStretch` не тронут (M-2 остаётся тем же
задокументированным, принятым в ADR-018 компромиссом — вариант (c) для этой политики). Реализация:

- `GridMetrics.ReservedVerticalScrollBarWidth(IconLayoutPolicy policy, double verticalScrollBarWidth)` —
  новая чистая (WPF-независимая) функция: `verticalScrollBarWidth` для `FixedGap`, `0` для
  `LegacyStretch`. Значение скроллбара передаётся параметром (не читается из `SystemParameters` внутри
  `GridMetrics`), чтобы класс остался WPF-свободным/юнит-тестируемым — ровно тот же приём, что уже
  применён для `IconLayoutPolicy` в `AdaptiveGridLayout.RowGap`.
- `GridMetrics.ContentHorizontalChrome(IconLayoutPolicy policy, double verticalScrollBarWidth)` —
  `FileListHPadding + ReservedVerticalScrollBarWidth(policy, verticalScrollBarWidth)`. Единая функция
  конверсии window-width ↔ content-width для ВСЕГО снап-кода (замена голого `FileListHPadding` там, где
  политика уже известна): `PanelResizeController`/`NativePanelResizeController`'s `SnapWidthToGrid`,
  `ResizeConstraintsFactory.BuildConstraints`'s `HorizontalChrome`, и новая policy-aware перегрузка
  `GridMetrics.MinPanelWidth(iconSize, policy, verticalScrollBarWidth)` (однoаргументный `MinPanelWidth(iconSize)`
  сохранён без изменений — делегирует новой перегрузке с `LegacyStretch`/`0`, тот же паттерн
  сохранения обратной совместимости, что и `AdaptiveGridLayout.RowGap`'s шести-/семиаргументные
  перегрузки).
- `AdaptiveIconGridPanel.MeasureOverride`/`ArrangeOverride` вычитают
  `ReservedVerticalScrollBarWidth(LayoutPolicy, SystemParameters.VerticalScrollBarWidth)` из
  `availableSize.Width`/`finalSize.Width` **только для расчёта `RowCapacity`** — ширина, которую панель
  возвращает как свой `DesiredSize`/использует для позиционирования (`Offset`/`RowGap`), не уменьшается:
  резерв влияет исключительно на «сколько колонок помещается», а не на то, где физически рисуется surplus
  space (он по-прежнему справа, Решение 1). Следствие: `LayoutPolicy` DP теперь используется ОБОИМИ
  `MeasureOverride` И `ArrangeOverride` (не только `ArrangeOverride`, как было до этого амендмента) — DP
  остаётся `AffectsMeasure | AffectsArrange`, но теперь по обеим осям обоснованно, а не только по
  историческому «на всякий случай».
- List-режим: `IconLayoutPolicy` по-прежнему релевантен только Grid-режиму (Решение 5) —
  `ResizeConstraintsFactory.BuildConstraints` резолвит `IconLayoutPolicy.LegacyStretch` явно, если
  `model.ViewMode != Grid` (или `model is null`), так что M-2-резерв никогда не просачивается в List-
  режимный расчёт chrome.

Это делает наблюдение выше не просто рекомендацией на будущее, а реализованным поведением: `FixedGap`
теперь при 344 DIP-панели (три колонки без учёта скроллбара) отдаёт РОВНО ту же раскладку, что и при
344 минус реальную `SystemParameters.VerticalScrollBarWidth` — колонка не «прыгает» на пороге появления/
исчезновения скроллбара (тесты: `AdaptiveIconGridPanelTests.FixedGap_RowCapacity_StableAcrossScrollBarAppearing`,
`...ReservesVerticalScrollBarWidth_MatchesAnalyticFormula`; `LegacyStretch_RowCapacity_DoesNotReserveVerticalScrollBarWidth`
подтверждает отсутствие изменений для `LegacyStretch`).

---

## Решение 5: сохраняемые инварианты (не меняются ни при `LegacyStretch`, ни при `FixedGap`)

- **Drag-reorder hit-testing.** `AdaptiveIconGridPanel`'s инвариант «позиционирование только через
  `Arrange`, без `RenderTransform`» не меняется политикой — `ListReorderGeometry.ComputeInsertionGap`
  и `VisualTreeHelper.HitTest`/`TranslatePoint` в `ChildWindow.xaml.cs` продолжают читать те же
  `Arrange`-прямоугольники независимо от того, какая ветка `RowGap` их произвела.
- **Selection rectangles (ADR-015 Решение 2, "chrome выделения внутри ячейки").** Ячейка (`cell ×
  rowHeight`) не меняется политикой — правило «chrome состояния целиком внутри arrange-бокса»
  выполняется одинаково для обеих политик.
- **Rename** (inline) — не сдвигает ячейку/раскладку ряда ни при одной из политик (ADR-018 Manual
  verify #10 остаётся в силе для обеих).
- **List mode** — не затрагивается вообще: `ListViewTemplate` не использует `GridMetrics`/
  `AdaptiveGridLayout`/`IconLayoutPolicy` ни в каком виде (ADR-018 прецедент — «замена коснулась только
  `GridViewTemplate`»). `IconLayoutPolicy` — DP/поле, релевантное только Grid-режиму; в List-режиме
  игнорируется по построению (аналогично тому, как `SnapResizeToIconGrid`/`MinPanelWidth` уже сегодня
  явно проверяют `ViewMode == Grid`).
- **Tooltips** на подписи элементов — не зависят от геометрии сетки, не трогаются.
- **Виртуализация при 200+ items.** `AdaptiveIconGridPanel` **не виртуализирует** ни при `LegacyStretch`,
  ни при `FixedGap` (то же, что и заменённый им `WrapPanel` — не регресс, а существующее
  пред-ограничение, зафиксированное ещё в ADR-018 и подтверждённое P00 части C, гипотеза H10 —
  «список 200+ в List-режиме не проверен»). Этот ADR **не решает** задачу виртуализации Grid-режима —
  она вне объёма P01/P05/P06 (баг-репорт производительности синхронной загрузки иконок — P08).

---

## Решение 6: миграция и дефолты

Поле `IconLayoutPolicy` — **per-panel**, тем же способом, что `ViewMode`/`IconSize`
(`ChildWindowModel`), а не глобальная настройка (`IAppSettings`, как `NativePanelSizing`/
`SnapResizeToIconGrid`). Обоснование: `IconSize`/`ViewMode` — уже прецедент per-panel визуальных
настроек в этом же слое; глобальный переключатель заставил бы мигрированного пользователя либо
потерять привычную геометрию **всех** панелей разом при переключении на `FixedGap`, либо не иметь
способа опробовать `FixedGap` на одной панели, не трогая остальные — оба исхода хуже, чем per-panel
поле с per-panel миграцией (см. Альтернативы).

**Персистентность (поля/дефолты — владелец версии схемы ADR-028):**

| Поле | Область | Тип/дефолт |
|---|---|---|
| `ChildWindowModel.IconLayoutPolicy` (persist рядом с существующими `viewMode`/`iconSize` в DTO панели — конкретный класс сверить в коде на P02/P05) | per-panel | enum, персистентно |
| `IAppSettings.DefaultIconLayoutPolicy` (тот же класс настроек, что `DefaultIconSize`) | глобально, только для **новых** панелей | enum, дефолт **`FixedGap`** |

**Дефолт для новых установок.** `IAppSettings.DefaultIconLayoutPolicy` = `FixedGap` в
`Settings.settings` (новый ключ, отсутствует в старых `user.config`). Любая **новая** панель (лассо,
`AddPanel`, вручную созданная после этой фичи) получает `FixedGap` — это и есть целевое поведение
Fences-паритета («шаг между иконками не гуляет с шириной панели»).

**Дефолт для мигрированных пользователей.** Миграция схемы (v_N → v_N+1, ADR-028) обязана записать
`IconLayoutPolicy = LegacyStretch` **явно** в каждую существующую панель — не полагаться на
«отсутствие поля = LegacyStretch по умолчанию класса», потому что дефолт нового глобального ключа
(`FixedGap`) иначе рискует быть спутан с дефолтом поля модели при будущей рефакторинге инициализации.
Явная запись `LegacyStretch` при миграции — тот же приём, что ADR-025 D-5 для `AccentColor` (лоссивная,
но предсказуемая, задокументированная миграционная ветка), применённый здесь без потери информации
(маппинг однозначен: любая существующая панель на момент миграции — `LegacyStretch`, без исключений).

Итог: **новые панели (на любой машине, включая после апгрейда) → `FixedGap`; панели, существовавшие
до миграции → `LegacyStretch`, зафиксировано явно, переключаемо пользователем вручную** (UI-переключатель
per-panel — вне scope этого ADR, кандидат для настроек панели рядом с `ViewMode`/`IconSize` в P05).

---

## Решение 7: `PanelSizingMode { Manual = 0, AutoContent = 1 }` + `Fit panel to content`

```csharp
public enum PanelSizingMode
{
    Manual = 0,
    AutoContent = 1,
}
```

Две разные вещи с одной и той же формулой:

- **`Fit panel to content`** — явное, одноразовое императивное действие (пункт меню/кнопка). Работает
  **независимо от `PanelSizingMode`** (в том числе в `Manual`) — «подгони высоту прямо сейчас», без
  изменения режима.
- **`PanelSizingMode.AutoContent`** — персистентный per-panel режим: та же формула вызывается заново
  при каждом триггере из списка ниже, без участия пользователя.

Обе точки входа вызывают одну и ту же чистую функцию (условно `PanelContentSizer.FitHeight(...)`,
WPF-независимая, по аналогии с `AdaptiveGridLayout`/`GridMetrics` — точная сигнатура на усмотрение P06,
но обязана быть чистой и юнит-тестируемой без STA).

### Формула

```
columnCapacity   = AdaptiveGridLayout.RowCapacity(contentWidth, cell, minGap, m)
                    // contentWidth = Window.Width − PanelMetrics.ContentLeftInset (Решение 2, F-3),
                    // minGap/m зависят от IconLayoutPolicy текущей панели (Решение 1)
contentRows      = AdaptiveGridLayout.RowCount(itemCount, columnCapacity)   // 0 при itemCount == 0
displayRows      = itemCount == 0 ? 1 : contentRows
                    // "1" здесь — НЕ переиспользование RowCount(0,k)=0 в другом смысле: это
                    // отдельный, AutoContent-специфичный пол на минимальную видимую drop zone,
                    // не изменение контракта AdaptiveGridLayout.RowCount (который по-прежнему
                    // возвращает 0 для 0 элементов везде, где используется для скролла/measure)
FitContentHeight = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding
                 + AdaptiveGridLayout.Extent(displayRows, GridMetrics.RowHeight(iconSize),
                                              minGap, verticalEdgePadding)
```

`AdaptiveGridLayout.Extent` — та же функция, что уже используется формулой снап-высоты ADR-018
(`H(R)`), переиспользована без дублирования.

**Однонаправленность.** `columnCapacity` вычисляется из **текущей, уже неизменной** ширины окна;
`FitContentHeight` вычисляется из `columnCapacity`; результат применяется **только** к
`Window.Height`. Ширина никогда не читается заново после этого шага и никогда не выводится из
высоты/числа иконок — нет цикла «высота меняет ширину меняет число колонок меняет высоту».
`AdaptiveIconGridPanel` сам по себе не участвует в этом вычислении вообще (та же чистая
формула-дублёр, что и весь снап-код ADR-018, а не чтение `ActualHeight` живой панели).

**Округление до workspace row span.** `FitContentHeight` — аналитическая DIP-величина без округления
до сетки панелей. Финальное применение к `Window.Height` идёт через тот же geometry-pipeline,
которым владеет ADR-028: в `PanelGeometryMode.GridAligned` — округление вверх до ближайшего целого
row-span (workspace-юнит `U`, ADR-028), в `LegacyFreeform` — `Math.Ceiling` до целого DIP (тот же
M-1-конвенция, что и сегодняшний снап высоты). Этот ADR не дублирует правило округления ADR-028.

**0 элементов.** `displayRows = 1` гарантирует, что пустая панель имеет высоту не ниже одного «пустого»
ряда — видимую drop-zone для drag&drop файлов, а не схлопнутую до `HeaderHeight` полоску (которая
была бы неотличима от `IsRolledUp`).

**1 элемент.** `displayRows = RowCount(1, columnCapacity) = 1` — ровно один ряд, никакого
искусственного «высокого прямоугольника» (в отличие от текущего F-4/Репро-3 бага, где сохранённая
высота никак не связана с фактическим 1 рядом контента).

### Триггеры пересчёта в `AutoContent` (per-panel режим)

| Событие | Пересчитывается? | Обоснование |
|---|---|---|
| Добавление/удаление иконки | Да | прямой вход формулы — `itemCount` |
| Смена вкладки/набора (`PageManagerService`) | Да, при активации панели | контент сменился, `itemCount`/состав другой |
| Roll-up (`IsRolledUp = true`) | **Нет** — `AutoContent`-высота считается относительно **развёрнутого** состояния (`ExpandedHeight`, тот же паттерн, что уже существует у `RollUpBehavior`) и не трогается, пока панель свёрнута; при разворачивании отображается последнее вычисленное `FitContentHeight`, не мгновенный пересчёт по устаревшему `itemCount` на момент сворачивания, если что-то изменилось (для этого действует правило «добавление/удаление иконки» выше, применимое и к свёрнутой панели — пересчитывается сохранённый `ExpandedHeight`, не видимая `Height`) |
| List mode | Да, но по **отдельной** формуле (ниже) | Grid-формула зависит от `columnCapacity`, которого в List-режиме нет |
| Смена screen/work area (монитор, DPI) | Нет, если `itemCount`/`columnCapacity` не изменились от смены монитора | `columnCapacity` зависит только от `contentWidth`, а не от того, на каком мониторе панель — пересчёт нужен только если сам resize/re-snap уже изменил `Window.Width` независимо (тогда триггер — не «смена монитора», а «изменение ширины», уже покрыт правилом смены `IconSize`/`ViewMode`/ручного resize из ADR-018) |
| Смена `IconSize`/`ViewMode` | Да | входит в формулу напрямую (`cell`, `rowHeight`) |
| Ручной resize по ширине (пользователь тянет боковой thumb) | Да (высота пересчитывается **после** того, как новая ширина осела — не в реальном времени внутри одного и того же resize-жеста по ширине, во избежание визуальной тряски: высота и ширина остаются раздельными resize-осями, как и сегодня) | новая ширина → новый `columnCapacity` → новая `FitContentHeight` |

`PanelSizingMode.Manual` **никогда** не меняет высоту по любому из событий выше — высота управляется
исключительно пользовательским resize-жестом либо явным вызовом `Fit panel to content`. Это прямое
требование DoD P06 («Manual сохраняет высоту при изменении количества иконок»).

**Существующие панели → `Manual`.** Как и `IconLayoutPolicy`, миграция схемы явно проставляет
`PanelSizingMode = Manual` каждой существующей панели — символьно нейтральная миграция (текущее
поведение — Manual по факту, раз persisted-высота никогда не пересчитывалась пассивно, F-4), без
визуального скачка при первом запуске после апгрейда.

**List mode формула — открытый вопрос.** См. «Открытые вопросы», Q4: List-режим не использует
`GridMetrics`/`AdaptiveGridLayout` вообще (ADR-018 прецедент), поэтому для него нет готового
аналитического аналога «высота ряда», через который можно было бы посчитать `FitContentHeight` без
придумывания нового кода на месте. Решение по объёму P06 отложено, см. рекомендацию там же.

---

## Инварианты/API — таблица для этапов 05/06

| Компонент | Тип | Комментарий |
|---|---|---|
| `IconLayoutPolicy { LegacyStretch, FixedGap }` | `enum`, `DesktopOrganizer.UI.Models` | рядом с `PanelViewMode`/аналогичными enum-ами слоя `UI/Models` |
| `PanelSizingMode { Manual, AutoContent }` | `enum`, `DesktopOrganizer.UI.Models` | — |
| `AdaptiveGridLayout.RowGap(itemsInRow, rowCapacity, available, cell, minGap, edgePadding)` | существующая сигнатура, **не удалять** | продолжает существовать как «`LegacyStretch`-версия»; используется существующими тестами до их переноса (явное требование P05-промпта) |
| `AdaptiveGridLayout.RowGap(..., IconLayoutPolicy policy)` — новая перегрузка | новый статический метод | `FixedGap` → всегда `minGap`; `LegacyStretch` → делегирует старой перегрузке без изменений. Единственная точка, где политика влияет на чистую математику |
| `AdaptiveIconGridPanel.LayoutPolicy` | новый DP, `AffectsMeasure\|AffectsArrange` | дефолт DP = `IconLayoutPolicy.LegacyStretch` (сохраняет поведение при отсутствии биндинга — тот же принцип, что `MinGap`/`EdgePaddingRatio`); фактическое значение всегда приходит через явный `TemplateBinding`/`Binding` на `ChildWindowModel.IconLayoutPolicy` |
| `PanelMetrics.ContentLeftInset` | новая `const double = 8d` | единственный источник правды на хром `PanelChrome`'s accent-колонки (Решение 2) |
| `GridMetrics.FileListHPadding` | изменение значения (было `0d`, литерал) | становится `= PanelMetrics.ContentLeftInset` (ссылка, не независимая константа) |
| `PanelContentSizer.FitHeight(...)` (имя условно) | новая чистая статическая функция, WPF-независимая | вызывается и из действия `Fit panel to content`, и из `PanelSizingMode.AutoContent`-подписки; единицы — DIP; см. формулу выше |
| `ChildWindowModel.IconLayoutPolicy` | новое per-panel INPC-свойство | персистентно, миграция проставляет `LegacyStretch` существующим панелям |
| `ChildWindowModel.PanelSizingMode` | новое per-panel INPC-свойство | персистентно, миграция проставляет `Manual` существующим панелям |
| `IAppSettings.DefaultIconLayoutPolicy` | новое глобальное свойство (по аналогии с `DefaultIconSize`) | дефолт `FixedGap`, применяется только к **новым** панелям |
| Единицы измерения | DIP везде, без исключений | как и весь семейство `GridMetrics`/`AdaptiveGridLayout`/`PanelMetrics` — ни один из перечисленных типов не оперирует физическими px |

---

## Реализация P05 (2026-09-22): `IconLayoutPolicy` + F-3, фактические имена

Этап P05 (`docs/fix-prompts/05-icon-layout.md`) реализовал Решения 1–3 (политика раскладки) и Решение 2
(F-3, учёт хрома) этого документа целиком; Решения 6–7 (персистентность/миграция `ChildWindowModel`,
`IAppSettings.DefaultIconLayoutPolicy`, `PanelSizingMode`/`Fit panel to content`) остаются за P02b/P06 —
статус документа сознательно оставлен **Proposed** до их завершения. Фактические имена и решения по
совместимости, подтверждающие таблицу выше:

- **`AdaptiveGridLayout.RowGap(int, int, double, double, double, double)`** (шестиаргументная,
  «`LegacyStretch`-версия») — **не изменена и не удалена**; используется байт-в-байт всеми существующими
  тестами (`AdaptiveGridLayoutTests`, `AdaptiveGridLayoutBaselineTests`) без единой правки.
- **`AdaptiveGridLayout.RowGap(int, int, double, double, double, double, IconLayoutPolicy)`** — новая
  семиаргументная перегрузка. `FixedGap` → безусловно `minGap` (та же ветка «неполного ряда», применённая
  ко всем рядам); `LegacyStretch` → делегирует шестиаргументной перегрузке без каких-либо изменений.
  Единственная точка кода, где `IconLayoutPolicy` влияет на математику `AdaptiveGridLayout`
  (`RowCapacity`/`RowCount`/`ItemsInRow`/`Offset`/`Extent`/`CountForExtent`/`SnapExtent` — все
  policy-агностичны, как и требовало Решение 3).
- **`AdaptiveIconGridPanel.LayoutPolicy`** — новый DP (`IconLayoutPolicy`, дефолт `LegacyStretch`,
  `AffectsMeasure | AffectsArrange` — ровно как в таблице выше, хотя фактически только `ArrangeOverride`
  читает это свойство: `MeasureOverride` не вызывает `RowGap` вообще ни для одной оси). Валидируется
  `ValidateValueCallback` (`Enum.IsDefined`), тем же паттерном, что и `MinGap`/`EdgePaddingRatio`.
- **`PanelMetrics.ContentLeftInset`** (`const double = 8d`) и **`PanelMetrics.ContentLeftInsetColumnWidth`**
  (`static readonly GridLength`, новое — не было в исходной таблице, добавлено по факту реализации) —
  второе поле нужно потому, что `ColumnDefinition.Width` (GridLength) не имеет `TypeConverter` от `double`
  для `x:Static`; обёртка-`GridLength` даёт единый источник правды и для C#, и для XAML.
  `Resources/Styles/Panels.xaml`'s `PART_AccentIndicator`'s `ColumnDefinition` переведена с литерала
  `Width="8"` на `Width="{x:Static models:PanelMetrics.ContentLeftInsetColumnWidth}"`.
- **`GridMetrics.FileListHPadding`** — было `0d` (независимый литерал), стало
  `= PanelMetrics.ContentLeftInset` (ссылка). Это единственная правка, которая протягивает F-3 фикс сквозь
  весь снап-код: `PanelResizeController.SnapWidthToGrid`, `NativePanelResizeController.SnapWidthToGrid`,
  `ResizeConstraintsFactory.BuildConstraints` (→ `ResizeGeometryCalculator.Apply`'s `HorizontalChrome`) и
  `GridMetrics.MinPanelWidth` уже читали эту константу символически — ни один из них не потребовал правки
  кода, только doc-комментариев.
- **Открытый вопрос Q2 решён как A** (применить F-3 к обеим политикам): подтверждено реализацией — фикс
  живёт в `GridMetrics.FileListHPadding`, общей для `LegacyStretch` и `FixedGap` точке входа снап-кода;
  никакого `IconLayoutPolicy`-условия в этом пути нет и не появилось.
- **XAML-биндинг `AdaptiveIconGridPanel.LayoutPolicy`** (`Resources/Templates.xaml`, `GridViewTemplate`'s
  `ItemsPanelTemplate`) — `LayoutPolicy="{Binding DataContext.Model.IconLayoutPolicy, RelativeSource=
  {RelativeSource AncestorType=Window}}"`, тот же идиом, что уже используют соседние `Model.IconSize`
  биндинги в этом же шаблоне. **`ChildWindowModel.IconLayoutPolicy` на момент P05 ещё не существует**
  (добавляется параллельным этапом P02b на этой же ветке) — биндинг компилируется (WPF `{Binding}` не
  проверяется на этапе сборки XAML/BAML), но на рантайме до мержа P02b не резолвится и молча падает на
  DP-дефолт `LegacyStretch` (текущее поведение без изменений). Ожидается, что биндинг «оживёт» сам собой
  после мержа P02b — доп. правок в `Templates.xaml` не потребуется.
- **Тесты.** `AdaptiveGridLayoutIconLayoutPolicyTests` (новый) — перегрузка `RowGap`, координатные таблицы
  1/3/7 иконок × несколько ширин × обе политики, surplus справа, full/partial row parity.
  `ContentLeftInsetRowCapacityRegressionTests` (новый) — F-3 регрессия
  (`RowCapacity(SnappedWidth(k) − ContentLeftInset) == k`) по всей сетке тиков `IconSize` (16–96) плюс
  реальные дробные значения из P00 (F-11), характеризация до-фиксового бага, совместимость с допуском
  M-1 (`LayoutTolerance`). `PanelMetricsTests` (новый) — новые константы `PanelMetrics`.
  `GridMetricsTests` — обновлены `FileListHPadding`/`MinPanelWidth`-ожидания под новое значение константы.
  `AdaptiveIconGridPanelTests` (новый, `tests/.../Unit/Controls/`) — первый unit-тест самой
  `AdaptiveIconGridPanel` (реальные `Measure`/`Arrange`, не только чистая математика `AdaptiveGridLayout`),
  закрывает пробел, отмеченный в P00 части D; выполняется на выделенном STA-потоке
  (`Tests.Helpers.StaTestRunner`, новый минимальный конвеншн проекта — сторонний STA-пакет тестов не
  подключался). Golden-эквивалентность legacy/native (`ResizeGeometryCalculatorApplyEquivalenceTests`,
  `NativePanelResizeControllerTests`'s "Honest golden equivalence") не потребовала правок — обе стороны
  сравнения читают `GridMetrics.FileListHPadding` из одного и того же источника, поэтому инвариант
  «путь не зависит от способа получения `raw`» не зависит от конкретного значения константы.

**Не в объёме P05** (явно out of scope промпта): UI-переключатель `IconLayoutPolicy` в настройках/
контекстном меню панели (P06); `ChildWindowModel.IconLayoutPolicy`/`IAppSettings.DefaultIconLayoutPolicy`
и миграция схемы (P02b); `PanelSizingMode`/`Fit panel to content` (P06).

---

## Fix-цикл P05 (2026-09-22, итерация 1): MAJOR-1 (Initialize re-snap), M-2 (см. Решение 4 выше)

Code-review P05 нашёл, что реализация выше (F-3 chrome fix, `FileListHPadding` 0→8) взаимодействовала с
уже существующим кодом `ChildWindow.Initialize` разрушительно: `Initialize` безусловно вызывал
`_resizeController.SnapWidthToGrid()` "so persisted sizes stay aligned even after the icon size was
changed in a previous session" — до F-3 это был числовой no-op (`FileListHPadding == 0`), после F-3 —
**силовой одноразовый скачок ширины при первой загрузке панели после апгрейда** (персистентная
3-колоночная ширина 344 DIP при `IconSize=48` становилась 352; 1-колоночная 168 становилась 176),
прямое нарушение инварианта ADR-028 «нет jump on first launch», причём НЕ только для
`PanelGeometryMode.GridAligned` (которым инвариант формально владеет), а для любой панели.

**Решение (MAJOR-1, вариант (a), решение оркестратора).** Строка `_resizeController.SnapWidthToGrid()`
удалена из `ChildWindow.Initialize` целиком — загрузка панели **никогда** не меняет её персистентные
`Left/Top/Width/Height`, ни при `LegacyStretch`, ни при `FixedGap`, ни в `LegacyFreeform`, ни в
`GridAligned` (ADR-028). Переснап при смене `IconSize`/`ViewMode` (`OnModelPropertyChanged`) и при live
resize-жесте (`OnResizeDragCompleted` → `EndResize` → `_snapToGrid()`) остаются полностью
незатронутыми — это явные, инициированные пользователем действия, а не пассивная загрузка. Формально
это то же самое архитектурное правило, что ADR-028 уже сформулировал для `LegacyFreeform`'s
`SnapResizeToIconGrid` (см. раздел «`LegacyFreeform` — существующее поведение сохраняется» там же) —
этот фикс просто убирает единственное оставшееся место, где загрузка (а не жест) могла менять
`Window.Width`, независимо от `PanelGeometryMode`/`IconLayoutPolicy`.

**Тесты.** `ChildWindowInitializeTests` (новый, `tests/.../Unit/Views/`) — первый тест в проекте,
конструирующий РЕАЛЬНЫЙ `ChildWindow` (со всеми его XAML-ресурсами, через тот же приём "fresh STA
thread + throwaway `ResourceDictionary`", что `ResourceDictionaryMergeTests`): `Initialize` с
персистентной шириной 344/168 (3/1 колонка, до F-3) сохраняет её байт-в-байт; List-режимная панель тоже
не трогается; отдельный «характеризующий» тест подтверждает, что ИМЕННО убранный вызов
(`SnapWidthToGrid` через всё ещё активный `OnModelPropertyChanged`-путь) воспроизводит скачок 344→352,
доказывая, что тесты выше проверяют реальный механизм, а не совпадение.

**M-2 (резервирование ширины скроллбара для `FixedGap`)** — реализовано, см. Решение 4's amendment
выше; тесты и затронутые файлы перечислены там же.

**MINOR/NIT, тот же цикл:**

- `ListReorderGeometry.HitTestReorderContainer` — новая перегрузка, принимающая `Point` напрямую (старая
  `DragEventArgs`-перегрузка теперь тонкая обёртка над ней, тот же паттерн «сохранить старую сигнатуру,
  добавить чистую» что и `AdaptiveGridLayout.RowGap`). Прямой point-хит-тест, промахнувшийся мимо
  контейнера, теперь ищет самый правый реализованный контейнер, чья вертикальная протяжённость содержит
  курсор — «конец РЯДА», а не безусловно «конец списка» — закрывая находку, что клик в surplus-зону
  `FixedGap` справа от НЕпоследнего ряда резолвился в `itemCount` (весь список) вместо конца этого ряда.
  Тесты: `ListReorderGeometryHitTestTests`.
- `Tests.Helpers.StaTestRunner` — усилен: рабочий поток теперь `IsBackground = true`; `Thread.Join()`
  заменён на `Join(TimeSpan.FromSeconds(30))` с `TimeoutException` при превышении; любой лениво
  созданный для этого потока `Dispatcher` явно завершается (`InvokeShutdown()`) в `finally`.
- `AdaptiveIconGridPanel.LayoutPolicyProperty`'s doc-comment обновлён: после M-2 `MeasureOverride` тоже
  зависит от `LayoutPolicy` (не только `ArrangeOverride`, как было верно до этого цикла) — `AffectsMeasure`
  больше не «на всякий случай», а обоснованно необходим; устаревшая ссылка на «P02b ещё не landed»
  убрана (P02b к этому циклу уже смержен).
- `Resources/Templates.xaml`'s комментарий у `LayoutPolicy`-биндинга обновлён: `ChildWindowModel.IconLayoutPolicy`
  landed, биндинг резолвится в рантайме — обновлена формулировка, снят устаревший «does not exist yet».
  Добавлен тест `AdaptiveIconGridPanelLayoutPolicyBindingTests`, воспроизводящий именно этот путь
  (`RelativeSource AncestorType=Window` + `DataContext.Model.IconLayoutPolicy`) на реальном `Window`
  (`Show()` понадобился — простого `Measure`/`Arrange` на непоказанном окне недостаточно для полного
  подключения визуального дерева, см. класса remarks).
- `Interaction/ResizeGeometryCalculator.cs`'s комментарий про «numeric no-op only because
  HorizontalChrome/FileListHPadding == 0» исправлен — утверждение относилось не к той оси (высотная
  free-branch асимметрия зависит от `VerticalChrome`, который никогда не был 0, а не от
  `HorizontalChrome`); теперь описывает реальное условие расхождения (клэмп `Math.Max(0d, ...)`).

---

## Fix-цикл P05 (2026-09-22, итерация 2, ПОСЛЕДНЯЯ): CRITICAL-1 (`CapacityWidth`, M-2 фактически достигнут), MAJOR-2 (`IconLayoutPolicy` re-snap), MINOR-1/MINOR-2/NIT

Повторное ревью итерации 1 нашло, что M-2's «безусловное резервирование» (Решение 4's amendment) само
по себе **не давало** заявленной стабильности: `AdaptiveIconGridPanel.MeasureOverride`/`ArrangeOverride`
вычитали `ReservedVerticalScrollBarWidth` напрямую из `availableSize`/`finalSize.Width` — но
`GridViewTemplate`'s `ScrollViewer.VerticalScrollBarVisibility="Auto"` **уже** отдаёт панели урезанную
(на реальную ширину скроллбара) ширину, когда скроллбар фактически показан. Итог — двойное вычитание
именно в момент появления реального скроллбара: `FixedGap` при снапнутой ширине окна `369` (`344 + 8
(ContentLeftInset) + 17 (резерв)`) без скроллбара видел viewport `361` → `361 − 17 = 344` → 3 колонки
(17 DIP впустую, но правильно); со скроллбаром видел viewport `344` → `344 − 17 = 327` → **2 колонки** —
ровно тот же «скачок колонок на пороге скроллбара», который M-2's amendment был призван устранить, просто
сдвинутый на одну ширину скроллбара, а не убранный. Юнит-тест `AdaptiveIconGridPanelTests.
FixedGap_RowCapacity_StableAcrossScrollBarAppearing` проходил только потому, что оба узких значения
(`344 − scrollBarWidth` и `344 − 2*scrollBarWidth`) на эталонной машине попадали в одно и то же ведро
`RowCapacity` — на реальных числах (`361` вместо `344`) он бы упал.

### CRITICAL-1: `AdaptiveIconGridPanel.CapacityWidth` — стабильная внешняя ширина, резерв вычитается один раз

**Решение (вариант A из промпта, DP, реализован).** Новый DP `AdaptiveIconGridPanel.CapacityWidth`
(`double`, дефолт `double.NaN` — «не забинжен»). `Resources/Templates.xaml`'s `GridViewTemplate` биндит
его на `ActualWidth` **того же самого** `ScrollViewer`, который `ListBox`'а собственный (не изменённый)
`ControlTemplate` генерирует изнутри (`RelativeSource AncestorType=ScrollViewer` — тот же идиом
пересечения границы шаблона, что уже используют все соседние биндинги `AncestorType=Window` в этом же
файле). Ключевое свойство этого значения: `ScrollViewer.ActualWidth` задаётся РОДИТЕЛЬСКИМ layout'ом
(колонка `*` `PART_ContentHost`, ADR-025/ADR-029 Решение 2), а не собственным `VerticalScrollBarVisibility=
"Auto"` этого же `ScrollViewer` — то есть НЕ уменьшается, когда сам этот `ScrollViewer` показывает
собственный вертикальный скроллбар. Это ровно та «стабильная внешняя ширина», о которой шла речь в
промпте, без необходимости вводить новый `ScrollViewer.VerticalScrollBarVisibility="Visible"`-режим или
трогать `PART_ContentHost`.

**Формула (`AdaptiveIconGridPanel.ResolveAvailableForCapacity`, приватный метод, вызывается из ОБОИХ
`MeasureOverride`/`ArrangeOverride` вместо прежнего прямого `Math.Max(0d, rawWidth - reservedScrollBar)`):**

```
ResolveAvailableForCapacity(rawWidth, reservedScrollBar) =
    reservedScrollBar > 0 && !IsNaN(CapacityWidth)
        ? Max(0, CapacityWidth - reservedScrollBar)   // резерв — ОДИН раз, от стабильной ширины
        : rawWidth                                     // без резерва вовсе
```

- `reservedScrollBar == 0` (`LegacyStretch`, `ReservedVerticalScrollBarWidth` всегда `0` для этой
  политики) → **всегда** `rawWidth` (сырая, потенциально урезанная реальным скроллбаром
  `availableSize`/`finalSize.Width`) — **без изменений** относительно поведения до этого фикса и
  относительно принятого ADR-018 M-2 варианта (c) для этой политики: `LegacyStretch` по-прежнему
  «плывёт» на пороге появления/исчезновения скроллбара, это сознательно не тронуто. Юнит-тест
  `LegacyStretch_RowCapacity_UnaffectedByCapacityWidth_EvenWhenBound` фиксирует это явно: даже если
  `CapacityWidth` **забинжен** (например, будущим рефакторингом), `LegacyStretch` его игнорирует
  полностью.
- `reservedScrollBar > 0` (`FixedGap`) и `CapacityWidth` забинжен (реальный `Templates.xaml`-путь) →
  резерв вычитается **один раз**, от стабильной `CapacityWidth`, а НЕ от `rawWidth` — устраняет двойное
  вычитание. Числа из ревью теперь совпадают: `CapacityWidth = 361` в обоих случаях (со скроллбаром и
  без) → `361 − 17 = 344` → **3 колонки в обоих случаях**, колонка не прыгает.
- `reservedScrollBar > 0`, но `CapacityWidth` НЕ забинжен (`NaN` — панель сконструирована напрямую, как
  в большинстве существующих юнит-тестов этого файла, вне реального `ScrollViewer`) → деградация до
  `rawWidth` **без какого-либо резерва** — то же самое поведение, что и `LegacyStretch`. Это осознанная,
  задокументированная (в doc-комментарии `CapacityWidthProperty`) деградация: без стабильного источника
  ширины резервировать не от чего, а повторять прежнюю (двойного-вычитающую) ошибку — хуже, чем не
  резервировать вовсе.

**Затронутые файлы.** `src/DesktopOrganizer.UI/Controls/AdaptiveIconGridPanel.cs` (новый DP
`CapacityWidth`, новый приватный `ResolveAvailableForCapacity`, оба `Measure`/`ArrangeOverride`
переведены на него); `src/DesktopOrganizer.UI/Resources/Templates.xaml` (`GridViewTemplate`'s
`AdaptiveIconGridPanel` — добавлен `CapacityWidth="{Binding ActualWidth, RelativeSource=
{RelativeSource AncestorType=ScrollViewer}}"`).

**Тесты (`AdaptiveIconGridPanelTests.cs`).** `FixedGap_RowCapacity_StableAcrossScrollBarAppearing`
переписан на реальные числа производства (`CapacityWidth = 344 + scrollBarWidth`, два прохода `Measure`/
`Arrange` с сырыми ширинами `CapacityWidth` и `CapacityWidth − scrollBarWidth`, тот же `CapacityWidth`
забинжен в обоих) — версия итерации 1 (сырые `344`/`344 − scrollBarWidth` без `CapacityWidth` вообще)
оставлена задокументированной в doc-комментарии как «почему эта версия проходила случайно».
`FixedGap_RowCapacity_ReservesVerticalScrollBarWidth_MatchesAnalyticFormula` обновлён — теперь явно
устанавливает `panel.CapacityWidth = panelWidth` перед `Measure`/`Arrange` (симулируя «`ScrollViewer` без
видимого скроллбара»), иначе с новым кодом (без `CapacityWidth`) резерв вообще не применился бы. Новые
тесты: `FixedGap_RowCapacity_CapacityWidthUnbound_DegradesToNoReservation` (деградация без биндинга),
`LegacyStretch_RowCapacity_UnaffectedByCapacityWidth_EvenWhenBound` (страховка от регресса, который
чуть не был внесён при проектировании фикса — см. ниже).

**Отклонённая альтернатива.** `ScrollViewer.VerticalScrollBarVisibility="Visible"` только для `FixedGap`
(озвучена в промпте как fallback, если binding на `ActualWidth` создаст layout-loop/задержку). Не
потребовалась: `RelativeSource AncestorType=ScrollViewer`-биндинг на `ActualWidth` не создаёт
дополнительного прохода layout — `ActualWidth` уже вычислен к моменту, когда `ItemsPresenter`/панель
измеряются (родительский `ScrollViewer` всегда меряется/arranged раньше своего собственного контента),
и `CapacityWidth`'s `AffectsMeasure|AffectsArrange` инвалидирует эту панель ТОЛЬКО когда сам `ScrollViewer`
реально меняет размер (например, при ресайзе окна) — не на каждый `Auto`-скроллбар toggle этого же
`ScrollViewer` (тот toggle меняет `availableSize`/`finalSize`, которые эта панель получает естественным
путём WPF, не через отдельный DP-инвалидатор). Полный `dotnet test` зелёный без признаков лишних
layout-проходов/зависаний.

**Важный архитектурный риск, обнаруженный и закрытый при проектировании фикса (не регресс в
production-коде, а страховка, добавленная ПРЕВЕНТИВНО).** Наивная реализация «всегда использовать
`CapacityWidth`, когда он забинжен, независимо от политики» сломала бы `LegacyStretch`'s собственный
принятый ADR-018 M-2 вариант (c): поскольку `Templates.xaml` биндит `CapacityWidth` БЕЗУСЛОВНО (одна
`ItemsPanelTemplate` на обе политики), `LegacyStretch`-панель тоже получила бы забинженный `CapacityWidth`
— и если бы формула не проверяла `reservedScrollBar > 0` явно, `LegacyStretch`'s `RowCapacity` тоже стал
бы вычисляться от стабильной внешней ширины вместо реальной урезанной, что могло бы дать `RowCapacity`,
для которого реально доступной (урезанной скроллбаром) ширины уже не хватает — `AdaptiveGridLayout.RowGap`'s
`Math.Max(minGap, stretched)` не допускает отрицательного зазора (шов не растягивается ниже `minGap`), но
итоговый ряд в этом случае превысил бы `finalSize.Width`, то есть визуально обрезался бы/наезжал на правый
край в узком диапазоне ширин. Формула `ResolveAvailableForCapacity` явно гейтит применение `CapacityWidth`
условием `reservedScrollBar > 0` (не просто «если забинжен»), что структурно исключает этот сценарий —
`LegacyStretch`'s `reservedScrollBar` всегда `0`, поэтому эта ветка для него никогда не активируется,
независимо от того, что говорит `CapacityWidth`. Зафиксировано юнит-тестом
`LegacyStretch_RowCapacity_UnaffectedByCapacityWidth_EvenWhenBound` (намеренно забинживает `CapacityWidth`
на заведомо «неправильное» значение `1000` и проверяет, что результат не меняется).

### MAJOR-2: `IconLayoutPolicy` теперь тоже триггерит переснап в `ChildWindow.OnModelPropertyChanged`

**Диагноз.** `OnModelPropertyChanged` слушал только `nameof(ChildWindowModel.IconSize)`/
`nameof(ChildWindowModel.ViewMode)`. Переключение `IconLayoutPolicy` на живой панели (будущий UI-
переключатель, P06) меняет `GridMetrics.ContentHorizontalChrome`/`MinPanelWidth`'s резерв под ширину
скроллбара для ТОЙ ЖЕ персистентной ширины окна — персистентная ширина могла перестать соответствовать
тому, что реально отрисует `AdaptiveIconGridPanel`, вплоть до следующего НЕСВЯЗАННОГО изменения
`IconSize`/`ViewMode`, которое случайно тоже вызовет переснап.

**Фикс.** `nameof(ChildWindowModel.IconLayoutPolicy)` добавлен в тот же список условий
`OnModelPropertyChanged`, вызывающий `_resizeController.SnapWidthToGrid()`. Никакой дублирующей проверки
`ViewMode == Grid`/`SnapResizeToIconGrid` не добавлено — `SnapWidthToGrid()` уже гейтит на оба условия
внутри себя, ровно тем же способом, что уже полагаются существующие триггеры `IconSize`/`ViewMode`.

**Известный, не новый и не устраняемый этим циклом пробел (задокументирован в doc-комментарии метода).**
`SnapWidthToGrid` не различает `ChildWindowModel.GeometryMode` — `PanelGeometryMode.GridAligned`-панель
переснапается на icon-grid-ячейку точно так же, как `LegacyFreeform`, как и было ДО этого фикса для
`IconSize`/`ViewMode`. Владение этим взаимодействием — территория ADR-028/P02 fix-2, явно вне зоны этого
цикла; добавление `IconLayoutPolicy` в тот же список триггеров не делает существующее поведение хуже —
оно остаётся ровно так же (не) корректным относительно `GeometryMode`, как и было для двух других
триггеров, уже стоявших в этом списке.

**Тесты (`ChildWindowInitializeTests.cs`).**
`Initialize_ThenTogglingIconLayoutPolicyToFixedGap_TriggersReSnap_ReservingScrollBarWidth` — переключение
живой `LegacyStretch`-панели (персистентная 3-колоночная ширина `352` DIP, F-3-корректная точка) на
`FixedGap` пересчитывает ширину на бо́льшую (резервирует ширину скроллбара); ожидаемое значение считается
той же формулой, что и сам `SnapWidthToGrid` (`ExpectedSnappedWidth`, приватный helper), а не хардкод-
числом — устойчиво к любому реальному `SystemParameters.VerticalScrollBarWidth` на машине, где выполняется
тест. `Initialize_ThenTogglingIconLayoutPolicyBackToLegacyStretch_ReSnapsToNarrowerWidth` — симметричный
обратный переход (round trip `FixedGap → LegacyStretch`), тем же способом вычисления ожидаемого значения.
`Initialize_ThenTogglingIconLayoutPolicy_WhenSnapDisabled_LeavesWidthUntouched` — при
`SnapResizeToIconGrid = false` переключение политики не трогает ширину вовсе (гейт `SnapWidthToGrid`'а
работает и для этого нового триггера, не только для двух старых).

### MINOR-1: `ListReorderGeometry`'s hit-test fallback — «ближайший», а не «всегда правый»

**Диагноз.** Фолбэк итерации 1 (`FindRightmostContainerInSameRow`) при промахе прямого хит-теста всегда
выбирал САМЫЙ ПРАВЫЙ контейнер того же ряда — корректно для surplus-зоны `FixedGap` СПРАВА от неполного
ряда (тот случай, ради которого фолбэк и был введён), но неверно для симметричного случая: курсор в ЛЕВОМ
гуттере ряда (поле `m`, `42` DIP при `IconSize=48`, до первой колонки) тоже промахивался мимо всех
контейнеров и тоже попадал в тот же фолбэк — который выбирал ПОСЛЕДНИЙ элемент ряда вместо ПЕРВОГО,
резолвя «вставить в начало ряда» как «вставить перед последним элементом ряда».

**Фикс.** `FindRightmostContainerInSameRow` переименован в `FindNearestContainerInSameRow` и переписан:
среди контейнеров того же ряда (та же проверка попадания по Y) выбирается тот, что БЛИЖАЙШИЙ по
горизонтали к курсору (расстояние `0`, если курсор уже в пределах контейнера по X — например, в узком
`pmin`-зазоре между двумя колонками одного ряда; иначе — расстояние до ближайшего края контейнера). Левый
гуттер теперь корректно резолвится в первый элемент ряда (ближайший — он), правая surplus-зона — в
последний (тоже ближайший — сохранена прежняя, уже протестированная итерацией 1 семантика как частный
случай общего правила, не отдельная ветка кода).

**Тесты (`ListReorderGeometryHitTestTests.cs`).**
`HitTestReorderContainer_LeftGutterOfNonFirstRow_ResolvesToRowsFirstItem_NotLastItem` (плюс сквозная
проверка `ComputeInsertionGap` до индекса `0`), `HitTestReorderContainer_LeftGutterOfSecondRow_
ResolvesToThatRowsFirstItem` (не первый ряд — тот самый случай, где старый фолбэк дал бы неверный
ответ), `HitTestReorderContainer_BetweenTwoColumnsInSameRow_ResolvesToOneOfThem` (узкий `pmin`-зазор
между двумя колонками — раньше не покрывалось вовсе),
`HitTestReorderContainer_BelowEveryRow_StillReturnsNull_AcrossXPositions` (`[Theory]`, вертикальный
«ниже последнего ряда» кейс на нескольких X-позициях — существующий `Fact`
`HitTestReorderContainer_BelowEveryRow_StillReturnsNull` оставлен нетронутым как permanent regression pin).

### MINOR-2: golden-эквивалентность расширена на `FixedGap`

**Диагноз.** `ResizeGeometryCalculatorApplyEquivalenceTests`'s `GridSnapConstraints()`/`FreeConstraints()`
и `NativePanelResizeControllerTests`'s «Honest golden equivalence» регион хардкодили/использовали
`ChildWindowModel`'s дефолтный `IconLayoutPolicy` (`LegacyStretch`) — ни `ResizeConstraintsFactory` для
`FixedGap`, ни согласие двух копий `SnapWidthToGrid` (`PanelResizeController.cs`/
`NativePanelResizeController.cs`) для этой политики не проверялись НИ ОДНИМ тестом golden-эквивалентности.

**Фикс.** `ResizeGeometryCalculatorApplyEquivalenceTests`: новый `FixedGapGridSnapConstraints()`, строящий
`ResizeConstraints` через РЕАЛЬНЫЙ `ResizeConstraintsFactory.BuildConstraints` (не вручную, в отличие от
двух существующих фабрик в этом файле) с `IconLayoutPolicy.FixedGap`; новая `[Theory]`
`AccumulateChain_And_SingleAbsoluteApply_ProduceBitForBitIdenticalRect_FixedGapGridSnap` (8 граней, общий
приватный `AssertAccumulateChainMatchesSingleApply`, вынесенный из тела прежнего единственного теста-
теории). `NativePanelResizeControllerTests`: `CreateHarness` получил параметр `iconLayoutPolicy`
(дефолт `LegacyStretch`, обратная совместимость сохранена); новая `[Theory]`
`RealController_GridSnapMode_FixedGapPolicy_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset` (те же
8 граней с ненулевым `WM_SIZING`-оффсетом, что и существующий `LegacyStretch`-тест) с явной sanity-
проверкой, что `FixedGap`'s `HorizontalChrome` реально отличается от `LegacyStretch`'s на этой машине —
иначе тест был бы тавтологичным относительно уже существующего.

### NIT

- `ResizeConstraints.cs`'s doc-комментарии на `MinWidth`/`HorizontalChrome` обновлены — ссылались на
  устаревшие однoаргументные `GridMetrics.MinPanelWidth(iconSize)`/голый `FileListHPadding`, хотя
  `ResizeConstraintsFactory.BuildConstraints` уже с P05-итерации-1 использует policy-aware перегрузки
  (`MinPanelWidth(iconSize, policy, verticalScrollBarWidth)`/`ContentHorizontalChrome(policy, ...)`).
- `Tests.Helpers.StaTestRunner`'s `finally`-блок (`Dispatcher.FromThread(...)?.InvokeShutdown()`) обёрнут
  в собственный `try`/`catch` — исключение из `InvokeShutdown`, не пойманное явно, вышло бы из `finally`
  как необработанное исключение НЕ-UI-потока, что на .NET (Core) немедленно завершает весь процесс
  тест-раннера (а не просто проваливает один тест). Теперь тип исключения (не `Message`/`StackTrace` —
  тот же приватность-инвариант, что и у `PanelResizeController.OnResizeDragDelta`) логируется в
  `Console.Error` и поглощается — дальнейшая очистка ресурсов `Dispatcher`'а всё равно происходит при
  завершении (background-)потока.
- Тавтологичный тест `FixedGap_RowCapacity_ReservesVerticalScrollBarWidth_MatchesAnalyticFormula` не
  заменён на чисто числовой (как предполагалось в промпте), а **исправлен по существу**: корень его
  тавтологичности — не в способе вычисления ожидаемого значения (через `AdaptiveGridLayout.RowCapacity`
  напрямую, что само по себе честно — тест утверждает «поведение панели совпадает с чистой математикой»,
  не «поведение панели равно магическому числу»), а в том, что до фикса CRITICAL-1 он не нуждался в
  `CapacityWidth` вообще (резерв применялся всегда, безусловно). После фикса тест ОБЯЗАН явно установить
  `panel.CapacityWidth`, иначе резерв не сработает — это и есть содержательное изменение, зафиксированное
  в его новом doc-комментарии; отдельный чисто-числовой тест
  (`FixedGap_RowCapacity_StableAcrossScrollBarAppearing`, переписанный) уже покрывает «числовую», не
  формула-в-формулу, версию проверки.

---

## Реализация P06 (2026-09-22): `PanelContentSizer`, «Fit panel to content», `AutoContent`, «Icon layout»/«Apply to all panels»

Этап P06 (`docs/fix-prompts/06-fit-content.md`) реализует Решение 7 целиком (действие «Fit panel to
content», режим `PanelSizingMode.AutoContent`, gating/roll-up/List-mode правила) и UI-часть Решения 6
(per-panel переключатель `IconLayoutPolicy` в контекстном меню — персистентность/миграция самого поля
уже landed в P02b, см. «Не в объёме P05» выше). Полный план/rationale — отдельный документ
`docs/PLANS/workspace-grid-06-fit-content.md`; здесь фиксируются решения, затрагивающие сам контракт
этого ADR.

**Уточнение формулы Решения 7 (`columnCapacity`).** Черновик Решения 7 (написан до P05's fix-cycle 2)
определял `contentWidth = Window.Width − PanelMetrics.ContentLeftInset` — то есть без учёта M-2
(`GridMetrics.ReservedVerticalScrollBarWidth`/`ContentHorizontalChrome`, добавленных именно тем
fix-cycle'ом). Реализация P06 использует `GridMetrics.ContentHorizontalChrome(policy,
verticalScrollBarWidth)` вместо голого `ContentLeftInset` — тот же policy-aware chrome, которым уже
пользуется весь снап-код (`SnapWidthToGrid`, `MinPanelWidth`). Без этого уточнения `FitHeight` для
`FixedGap`-панели пересчитывал бы `columnCapacity` на одну колонку БОЛЬШЕ, чем реально отрисует
`AdaptiveIconGridPanel` для той же ширины окна — то есть заново вносил бы именно тот класс рассинхрона
(«posted width doesn't match what the panel actually renders»), который F-3/M-2 уже закрыли для снап-
кода. Формально это правка ЧЕРНОВИКА Решения 7 в разделе «Формула» выше, не новое решение — таблица
«Инварианты/API» уже указывала `PanelContentSizer.FitHeight(...)` как «точная сигнатура на усмотрение
P06», поэтому это не расценивается как breaking change контракта, зафиксированного этим ADR.

**Фактическая реализация.** `src/DesktopOrganizer.UI/Models/PanelContentSizer.cs` (новый, WPF-независимый,
`UI/Models`) — `ResolveColumnCapacity`/`ResolveDisplayRowCount`/`ComputeRawContentHeight`/
`RoundForLegacyFreeform`/`RoundHeightUnitsForGridAligned`/`FitHeight` (полный пайплайн). Каждый шаг
переиспользует существующие формулы (`GridMetrics`/`AdaptiveGridLayout`/`WorkspaceLayoutPolicy`) — ни
одна не продублирована, требование промпта «не дублируй формулы» выполнено буквально (см. класса
doc-комментарии). Вертикальная ось никогда не растягивается ни при какой `IconLayoutPolicy`
(`AdaptiveGridLayout.DefaultMinGap`, не policy-aware `RowGap`) — то же правило, что уже действует в
`AdaptiveIconGridPanel.ArrangeOverride`'s собственном вертикальном `Offset`.

**Округление.** `LegacyFreeform` → `Math.Ceiling` (M-1 конвенция). `GridAligned` → `WorkspaceLayoutPolicy.
BoundsToSpans`/`SpansToBounds` по высоте (ширина в этот расчёт не передаётся содержательно — оба
метода сепарабельны по осям, см. `RoundHeightUnitsForGridAligned`'s doc-комментарий), с полом
`WorkspaceGridMetrics.MinHeightSpanExpanded` — тот же component-wise минимум, что `PanelDefaults.
ReconcileGridAlignedSpans` уже применяет при создании панели.

**UI-слой.** Новый партиал `Views/ChildWindow.ContentSizing.cs` (третий per-concern партиал этого окна,
рядом с `ChildWindow.Sizing.cs`/`ChildWindow.WorkspaceGrid.cs` — читает оба их домена, но не владеет ни
одним): Click-обработчики «Fit panel to content»/«Auto-size to content»/«Apply to all panels» (оба —
Icon layout и Sizing mode), плюс подписки `PanelSizingMode.AutoContent`-триггеров. `ChildWindow.xaml`'s
`ContextMenu` получил «Icon layout» (radio-группа через новый `ChildWindowViewModel.
SetIconLayoutPolicyCommand`, тот же идиом, что `SetContentFilterCommand`/`SetSortModeCommand`),
«Fit panel to content» и «Auto-size to content» (оба через новый `AllTrueMultiConverter` — комбинирует
`CanEnterEditLayout` (StaticResource `LayoutEditMode`) И `ViewMode == Grid`, две независимые binding-
источника, которые единственный `EnumEqualsConverter` скомбинировать не может).

**Триггеры `AutoContent` — фактическая реализация таблицы Решения 7:**

| Триггер | Механизм |
|---|---|
| Добавление/удаление/фильтрация иконок | `ChildWindowViewModel.FileItems.CollectionChanged` (стабильный instance на весь срок жизни VM) |
| `IconSize`/`ViewMode`/`IconLayoutPolicy`/`SizingMode` | `model.PropertyChanged`, подписка ПОСЛЕ существующей `OnModelPropertyChanged` (P04's re-snap) — порядок подписки гарантирует, что `Window.Width` уже пере-снапнут к моменту пересчёта высоты |
| Ручной resize по ширине | `Window.SizeChanged` (свой обработчик, подписан в коде, не в XAML — партиал не владеет корневым `<Window>` элементом), debounce 200мс через `DispatcherTimer`, фильтр `e.WidthChanged` (высотное-only изменение — в т.ч. от этого же партиала — никогда не долетает до дебаунса) |
| Смена вкладки/набора → активация панели | `Window.IsVisibleChanged` (тот же приём — код, не XAML) |
| Смена screen/DPI | Отдельного триггера нет (см. ADR — покрывается уже существующим триггером ширины, если сам resize/re-snap изменил `Window.Width`) |
| Roll-up | НЕ триггер сам по себе; `MaybeAutoFit`/`ApplyFitHeight` при `IsRolledUp` пишут `RollUpBehavior.ExpandedHeight`/`ChildWindowModel.Size` напрямую, никогда `Window.Height` — тот же контракт, что `RollUpBehavior` уже поддерживает для «expanded height survives while collapsed» |

**Решение (не описанное дословно в Решении 7): переключение `SizingMode` само по себе — тоже триггер.**
Включение `AutoContent` немедленно считает fit (иначе режим выглядел бы «не подействовавшим» до
следующего неродственного триггера) — обычный Click-обработчик `AutoSizeToContentMenu_Click` просто
присваивает `Model.SizingMode`, а немедленный пересчёт — побочный эффект той же `PropertyChanged`-
подписки, что и остальные триггеры (не отдельная ветка кода). Выключение (`AutoContent → Manual`)
высоту не трогает вовсе — прямое требование DoD.

**Роль `IsLayoutLocked` (ADR-026) — различается для явного действия и пассивного пересчёта.** Явное
действие «Fit panel to content» и Click по «Auto-size to content» оба гейтятся `IsLayoutLocked`
(тот же уровень, что «Size»/«Align to workspace grid» — обе меняют геометрию по явной команде
пользователя). Пассивный пересчёт `AutoContent` (добавление/удаление иконки и т.д.) — **не** гейтится
`IsLayoutLocked`: добавление/удаление файлов уже разрешено при заблокированном layout («Roll up/down,
renaming, and file operations are unaffected» — существующая формулировка UI), так что молчаливая
блокировка *только* высотного отклика на это же действие читалась бы как несогласованное поведение, а
не как защита позиции/размера от жеста. Зафиксировано как решение оркестратора этого прохода, не
буквальное следствие текста Решения 7.

**«Apply to all panels» — обе кнопки (Icon layout, Sizing mode) применяются ко ВСЕМ панелям на ВСЕХ
страницах**, не только к текущей: `IWindowManager.GetAllChildWindows()` уже ничего не фильтрует по
`PageId`, и заведение такого фильтра исключительно для этой фичи добавило бы асимметрию с любым другим
местом, читающим ту же коллекцию. Оба действия — confirmation-gated (`IConfirmationService`, тот же
сервис, что `MainWindowViewModel.RemovePanel`; текст диалога называет число панелей). Реализовано БЕЗ
нового метода в `IWindowManager`/`WindowManager` — `GetAllChildWindows()` уже возвращает те самые
live-инстансы `ChildWindowModel`, на которые подписан каждый живой `ChildWindow` (проверено: `WindowManager.
_childWindows[model.Id] = (window, model)`), так что присваивание `panel.IconLayoutPolicy =
targetPolicy`/`panel.SizingMode = targetMode` в цикле автоматически доходит до каждой открытой панели
через её же собственные, уже описанные выше подписки — ни `SaveWindowsState`, ни пере-snap не
дублируются отдельным кодом. Пункт «`IWindowManager`/`WindowManager` только аддитивный метод, если
нужен» из промпта закрыт решением «не нужен».

**List-режим (Q3 = B, подтверждено).** «Fit panel to content»/«Auto-size to content» оба no-op'ят (и
задизейблены в меню, `AllTrueMultiConverter`) вне `PanelViewMode.Grid`. «Icon layout» **не** гейтится
`ViewMode` (поле безвредно/инертно в List-режиме — тот же принцип, что уже применяет соседнее подменю
«Size»).

**Единственная точка записи геометрии.** И «Fit panel to content», и `AutoContent`-пересчёт для НЕ
свёрнутой панели идут через уже существующий `PanelGeometryWriter` (`_geometryWriter.Apply`), тот же
инстанс, что использует Stage S/P03/P04 — переиспользуется `IsApplying`-guard без единой правки в
`PanelGeometryWriter.cs` самом. Единственная новая (аддитивная, минимальная) правка `ChildWindow.xaml.cs`
— новое поле `_confirmation : IConfirmationService` (новый конструкторный параметр, DI singleton уже
зарегистрирован) и три однострочных хука (`InitializeContentSizing()` в конструкторе,
`AttachContentSizing(model)` СРАЗУ ПОСЛЕ `model.PropertyChanged += OnModelPropertyChanged;` внутри
`Initialize`, `DetachContentSizing()` в `OnClosing`) — метод `OnModelPropertyChanged` самого P04 не
тронут ни строкой.

**Настройки — дефолты для новых панелей.** `GlobalDefaultsViewModel.DefaultPanelGeometryMode`/
`DefaultIconLayoutPolicy` уже существовали (docs/PLANS/workspace-grid-migration.md §10), но не были
выведены ни в `MainWindowViewModel` (нет forwarding-свойств), ни в `MainWindow.xaml` — P06 добавляет обе
forwarding-пары (`DefaultPanelGeometryMode`/`PanelGeometryModeOptions`,
`DefaultIconLayoutPolicy`/`IconLayoutPolicyOptions`, тот же идиом, что `DefaultIconSize`) и два новых
`ComboBox`-ряда в «Defaults for new panels». `IAppSettings.DefaultSizingMode` сознательно не заведён —
ADR-029 нигде не предлагает глобальный дефолт `SizingMode` для новых панелей (см. §10.4 «Не добавлено»
в workspace-grid-migration.md); новая панель наследует собственный дефолт поля модели (`Manual`).

**Персистентность — уже покрыта P02b, без изменений в этом проходе.** `PanelStateMapperTests.
RoundTrip_PreservesEveryPersistedProperty` и `LayoutSnapshotServiceTests.
Restore_TwiceInARow_ProducesIdenticalWorkspaceGridAndIconLayoutFields` УЖЕ конструируют свои фикстуры с
`SizingMode = PanelSizingMode.AutoContent`/`IconLayoutPolicy = IconLayoutPolicy.FixedGap` и проверяют
байт-в-байт равенство после двух циклов restore — DoD P06 «snapshots после двух циклов restore
одинаковы» для `SizingMode = AutoContent` был закрыт ДО начала этого прохода, P06 лишь подтверждает это
чтением существующих тестов (не добавляет новых — добавление тавтологичного дубликата было бы шумом).

**Тесты.** `PanelContentSizerTests` (новый, 29 тестов) — `ResolveColumnCapacity`/
`ResolveDisplayRowCount`/`ComputeRawContentHeight`/`RoundForLegacyFreeform`/
`RoundHeightUnitsForGridAligned`/`FitHeight`; 0/1/2/N рядов, обе `IconLayoutPolicy`, обе
`PanelGeometryMode`, «подпись не отрезается» (одна иконка = ровно `RowHeight`, не меньше),
детерминированность (DPI-независимость по построению — класс не читает никакого DPI/`SystemParameters`
состояния сам). `ChildWindowContentSizingTests` (новый, 17 тестов, реальный `ChildWindow` — тот же STA-
harness приём, что `ChildWindowInitializeTests`) — «Fit panel to content» на 0/1/8 иконках, List-mode
no-op, `IsLayoutLocked` no-op, работает при `Manual`, roll-up (`Window.Height` не трогается,
`ChildWindowModel.Size` — трогается), `Auto-size to content` вкл/выкл (немедленный fit / сохранение
высоты), `Manual` не меняет высоту при добавлении иконки, `AutoContent` меняет (и использует
ПОСТ-снап ширину при смене `IconSize` — порядок подписки), переключение `ViewMode → List` останавливает
пересчёт без исключений, `GridAligned`-округление до row span, «Apply to all panels» (оба вида) —
confirm/decline. `ChildWindowViewModelTests` — 2 новых теста на `SetIconLayoutPolicyCommand`.
`MainWindowViewModelTests` — 6 новых тестов на forwarding-свойства/Options. Полный `dotnet test` —
2704/2704 green (0 failed, 0 skipped).

**Не протестировано автоматически (см. Manual verify).** Debounce-таймер самого `Window.SizeChanged`
триггера (`ChildWindowContentSizingTests`'s `RunOnStaThread` не крутит `Dispatcher`-цикл внутри тела
теста — тот же ограничивающий фактор, что и у `ChildWindowInitializeTests`); визуальная анимация
roll-up/roll-down в сочетании с обновлённым `ExpandedHeight`; реальный DPI ≠ 100%.

---

## Fix-цикл P06 (2026-09-22, итерация 1): MAJOR-1..4, MINOR m-1/m-2, UX-P1/P2, NIT

Code-review + UX-ревью реализации P06 нашли 4 MAJOR, 2 MINOR (плюс 2 MINOR/UX помечены как учтённые в
области соседних параллельных проходов — см. ниже), 2 UX-пункта и 1 NIT. Координация с параллельным
fix-циклом P04 на этой же ветке: **перевод `SizingMode → Manual` по клику на пресет S/M/L/XL
(`SizePresetMenu_Click`, `ChildWindow.Sizing.cs`) реализован ПАРАЛЛЕЛЬНЫМ проходом P04** — этот цикл его
не трогает; ниже описана только Thumb/native resize-жест часть MAJOR-4. Аналогично `AlignToGridMenu`'s
ToolTip (P03) не тронут этим циклом.

### MAJOR-1: `IsLayoutLocked`/`IsEnabled` gating для обеих «Apply … to all panels…»

**Диагноз.** «Apply sizing mode to all panels…» (`ChildWindow.xaml`, было ~строка 246) не имело
`IsEnabled` вообще — доступно даже при `IsLayoutLocked = true`. `ChildWindow.ContentSizing.cs`'s
`ApplyIconLayoutPolicyToAllMenu_Click`/`ApplySizingModeToAllMenu_Click` не проверяли
`ILayoutEditModeService.IsLayoutLocked` внутри себя вовсе (в отличие от `FitPanelToContentMenu_Click`/
`AutoSizeToContentMenu_Click`, у которых есть и биндинг, и defence-in-depth проверка) — при заблокированном
layout нажатие всё равно открывало диалог и, при подтверждении, писало геометрию/`SizingMode` во ВСЕ
панели (до 12 в наблюдавшемся репро).

**Фикс.**
- `ChildWindow.xaml`: «Fit panel to content»/«Auto-size to content»/«Apply to all panels…» (sizing)
  объединены в новый submenu «Panel sizing» (см. UX-P2 ниже), чей заголовок несёт ОБЩИЙ `AllTrue`
  `MultiBinding` (`CanEnterEditLayout` AND `ViewMode == Grid`) — «Apply to all panels…» внутри него
  получил СВОЙ собственный, идентичный `MultiBinding` (defence-in-depth, тот же паттерн, что «Fit panel
  to content» уже использует, а не полагание только на недоступность родительского пункта меню). «Apply
  to all panels…» под «Icon layout» получил `IsEnabled="{Binding CanEnterEditLayout, ...}"` (простой
  `Binding`, не `MultiBinding` — эта команда не зависит от `ViewMode`, тот же уровень, что «Icon layout»'s
  собственный заголовок).
- `ChildWindow.ContentSizing.cs`: оба `Apply*ToAllMenu_Click` теперь начинаются с
  `if (_layoutEditMode.IsLayoutLocked) return;` (тот же defence-in-depth стиль, что у соседних
  Click-обработчиков); `ApplySizingModeToAllMenu_Click` дополнительно проверяет `ViewMode == Grid`
  (теперь, когда пункт вложен в «Panel sizing», это симметрично родителю).

**Тесты.** `ChildWindowContentSizingTests.ApplyIconLayoutPolicyToAllMenu_Click_WhenLayoutLocked_IsANoOp`/
`ApplySizingModeToAllMenu_Click_WhenLayoutLocked_IsANoOp` — при `IsLayoutLocked = true` диалог
подтверждения даже не должен быть вызван результативно (другие панели остаются нетронутыми, несмотря на
`Confirm(...) => true`).

### MAJOR-2: `ApplyFitHeight`'s `GridAligned`-ветка игнорировала `PanelGridStateResolver.Resolve`'s возврат

**Диагноз.** `ApplyFitHeight` (`ChildWindow.ContentSizing.cs`) вызывал
`PanelGridStateResolver.Resolve(_model, currentBounds, monitors, _model.IsRolledUp)`, но отбрасывал
результат и продолжал ветку `GridAligned` безусловно (по `_model.GeometryMode`, не по
`state.IsGridAligned`). На fail-safe путях `Resolve` (нет мониторов; `currentBounds` не finite/positive)
возвращает `PanelGridState.Legacy` и **не** мигрирует `WidthSpanUnits`/`HeightSpanUnits` на модель — но
старый код всё равно читал `_model.WidthSpanUnits ?? WorkspaceGridMetrics.MinWidthSpanExpanded`, то есть
подставлял МИНИМАЛЬНЫЙ span (16 units = 120 DIP) для панели, у которой реальная ширина могла быть,
например, 352 DIP. Следующий потребитель `span → bounds` (roll-up, align, restore) затем схлопывал
панель до 120 DIP.

**Фикс.** Возврат `Resolve` теперь единственный источник истины для этой ветки:
```csharp
var state = PanelGridStateResolver.Resolve(_model, currentBounds, monitors, _model.IsRolledUp);
if (!state.IsGridAligned)
{
    ApplyHeight(PanelContentSizer.RoundForLegacyFreeform(raw), span: null);
    return;
}
var widthUnits = WorkspaceLayoutPolicy.BoundsToSpans(currentBounds.Size, state.UnitSize, state.Gap).WidthUnits;
```
`widthUnits` больше никогда не читается из `_model.WidthSpanUnits`/константы минимума — всегда
пересчитывается из ТЕХ ЖЕ `currentBounds`, что уже использованы для `raw`/`columnCapacity` (та же
конверсия, что `PanelSizingGridSupport.PersistSpans` использует на конце любого другого
grid-жеста/коммита).

**Тесты.** `ChildWindowContentSizingTests.AutoContent_GridAligned_RoundsUpToWorkspaceRowSpan` обновлён —
теперь конфигурирует реальный монитор (харнесс получил `Build(IReadOnlyList<MonitorInfo>? monitors)`,
дефолт — пустой список, воспроизводящий именно fail-safe путь) и проверяет
`model.WidthSpanUnits == 45` (`Ceiling((352+8)/8)`), явно **не** равное `MinWidthSpanExpanded` (16).
Новый `AutoContent_GridAligned_NoMonitors_FallsBackToLegacyFreeformRounding_NeverWritesMinimumSpan`
воспроизводит САМ fail-safe путь: 0 мониторов → высота применяется как `LegacyFreeform`-округление,
`WidthSpanUnits`/`HeightSpanUnits` остаются `null` (не мигрированы, не испорчены минимумом).

### MAJOR-3: debounce-тик мог сработать посреди активного resize/drag-жеста

**Диагноз.** `OnAutoFitDebounceTick` (200мс `DispatcherTimer`, перезаряжаемый на каждый
`Window.SizeChanged` во время ручного resize по ширине) не проверял, не идёт ли ПРЯМО СЕЙЧАС другой
gesture — пауза >200мс с зажатой кнопкой мыши посреди resize-жеста давала тик, который вызывал
`ApplyFitHeight()` (пишущий `Window.Height` через `PanelGeometryWriter`) ОДНОВРЕМЕННО с ещё активным
resize-жестом, тоже пишущим геометрию — визуальный прыжок/гонка.

**Фикс.** `OnAutoFitDebounceTick` теперь читает уже существующий, разделяемый всеми жестами этого окна
`PanelInteractionTracker` (ADR-021): если `State is Resizing or Dragging` — перезаряжает тот же таймер
(`Stop(); Start();`) вместо применения, ретраится после следующего интервала. Никакого нового состояния
не введено — переиспользован ОДИН И ТОТ ЖЕ трекер, который `PanelResizeController`/
`PanelDragMoveBehavior` уже используют для взаимного исключения (ADR-021).

**Тесты.** `AutoFitDebounceTick_WhileResizeGestureActive_RechargesTimerInsteadOfApplying`/
`..._WhileDragGestureActive_...` — захватывают `PanelInteractionState.Resizing`/`Dragging` напрямую через
трекер (без запуска настоящего Thumb/native жеста), вызывают тик рефлексией (тот же приём, что уже
использует класс — реальный `DispatcherTimer` не тикает без прокрутки `Dispatcher` в этом харнессе) и
проверяют, что высота НЕ изменилась, пока жест активен, и меняется сразу после `Exit(...)`.

### MAJOR-4: завершённый вертикальный resize не переводил `AutoContent → Manual`

**Решение оркестратора (зафиксировано здесь).** Fences-подобное поведение: завершённый пользовательский
resize-жест, изменивший ВЫСОТУ панели (любой edge, для которого
`ResizeEdgeParser.AffectsHeight(edge) == true` — т.е. любой, кроме чисто горизонтальных `Left`/`Right`),
демотирует `PanelSizingMode.AutoContent → Manual`. Единая точка для Thumb И native путей — конец
`ResizeGestureSession`/`EndResize` в обоих контроллерах. Resize ТОЛЬКО по ширине (`Left`/`Right`) в
`AutoContent` НЕ меняет режим — высота по-прежнему пересчитывается штатным debounce-триггером
`Window.SizeChanged`, как и раньше.

**Реализация (аддитивная, без дублирования логики между Thumb/native).**
- `Behaviors/IPanelSizingGridHost.cs` — новый член `void NotifyHeightResizeCommitted()`. Реализован ОДНОЙ
  строкой в `Views/ChildWindow.Sizing.cs` (explicit interface implementation, рядом с тремя существующими
  P04-членами того же интерфейса): `void IPanelSizingGridHost.NotifyHeightResizeCommitted() =>
  OnUserHeightResizeCommitted();` — никакая другая строка этого файла (P04's зона в этом fix-цикле) не
  тронута.
- `Views/ChildWindow.ContentSizing.cs` — новый приватный метод `OnUserHeightResizeCommitted()`: no-op,
  если `_model is null` или `SizingMode != AutoContent`; иначе `_model.SizingMode = Manual` +
  диагностическое событие (см. ниже). Симметрично уже существующему правилу `MaybeAutoFit`'а «Manual
  никогда не меняет высоту» — сам переход `SizingMode` НЕ пересчитывает высоту (переход в `Manual`
  никогда не вызывает `ApplyFitHeight`).
- `Behaviors/PanelSizingGridSupport.cs` — новый статический метод `NotifyHeightResizeCommitted(
  IPanelSizingGridHost? host, ResizeEdge edge)`: `if (host is not null && edge.AffectsHeight())
  host.NotifyHeightResizeCommitted();` — четвёртая «тройная» операция этого класса (было три: obstacles/
  persist-spans/is-grid-aligned). Единственная точка, где решается «затронул ли этот edge высоту» — ни
  один из двух контроллеров не дублирует эту проверку.
- `Behaviors/PanelResizeController.cs`'s `EndResize` — одна строка ПОСЛЕ существующего
  `PersistSpans`/`SaveWindowsState()` внутри `if (outcome.ShouldPersist)`:
  `PanelSizingGridSupport.NotifyHeightResizeCommitted(_gridHost, outcome.Edge);`.
- `Behaviors/NativePanelResizeController.cs` — тот же вызов внутри `PersistAfterGesture`, которому
  потребовался новый параметр `ResizeEdge edge` (протянут из `EndResize`'s `outcome.Edge`, в т.ч. через
  замыкание `_scheduleDeferredPersist(() => PersistAfterGesture(outcome.Edge))` для отложенного
  (`deferPersist: true`) пути — тот же самый узел, что уже отвечает за отложенную персистентность спанов
  при обрыве посреди `WM_SIZING`).
- Работает униформно для ОБЕИХ `PanelGeometryMode` (не только `GridAligned`) — `NotifyHeightResizeCommitted`
  не гейтится на `IsGridAligned`, в отличие от `PersistSpans`.

**Диагностика.** Новое событие `IGestureDiagnostics.SizingModeChanged(Guid? panelId, PanelSizingMode
from, PanelSizingMode to, string reason)` (категория `layout`, тот же уровень, что уже существующий
`LayoutModeChanged`) — записывается из `OnUserHeightResizeCommitted` с `reason = "HeightResizeCommitted"`.
Чисто наблюдательное, как и весь остальной интерфейс.

**Тесты.**
- `PanelSizingGridSupportNotifyHeightResizeCommittedTests` (новый) — чистая логика helper'а: все 6
  height-affecting edges вызывают host ровно 1 раз, `Left`/`Right` — никогда, `null`-host не бросает.
- `NativePanelResizeControllerGridAlignedTests` — 3 новых теста: полный native-жест на каждом из 6
  height-affecting edges уведомляет host ровно 1 раз; `Left`/`Right` — никогда; прерванный
  (`SizeMoveAborted`) жест откладывает уведомление до `RunDeferredPersistCalls()` (тот же M-3
  deferred-persist механизм, что уже проверен для spans).
- `ChildWindowContentSizingTests` — `NotifyHeightResizeCommitted_WhenAutoContent_TransitionsToManual_
  WithoutTouchingHeight`/`..._WhenAlreadyManual_IsANoOp`/`..._RecordsSizingModeChangedDiagnostics` —
  вызывают `((IPanelSizingGridHost)harness.Window).NotifyHeightResizeCommitted()` через публичную
  поверхность интерфейса (explicit-интерфейсный метод остаётся вызываемым через приведение типа, без
  рефлексии) на РЕАЛЬНОМ `ChildWindow`.
- `FakePanelSizingGridHost` (общий тестовый хелпер) — новый `NotifyHeightResizeCommittedCallCount`-спай.

**Не в объёме этого цикла.** Presets S/M/L/XL → `Manual` — реализовано ПАРАЛЛЕЛЬНЫМ проходом P04 в
`ChildWindow.Sizing.cs`'s `SizePresetMenu_Click`, координация через оркестратора (см. заголовок раздела).

### MINOR m-1: `IsVisibleChanged` на первом `Show()` пересчитывало `AutoContent`-панель при старте

**Диагноз.** `OnWindowIsVisibleChangedForContentSizing` вызывал `MaybeAutoFit()` на КАЖДЫЙ переход в
`true`, включая самый первый — при первом `Show()` панели. Для `AutoContent`-панели это означало запись
геометрии/спана сразу при появлении, что нарушает «no jump on first launch» (тот же инвариант, который
P05 fix-цикл MAJOR-1 уже закрыл для resize-снапа при `Initialize`).

**Фикс.** Новое приватное поле `_contentSizingShownOnce`; первая true-транзиция помечает его и
возвращается БЕЗ пересчёта; каждая последующая (переключение страницы туда-обратно) пересчитывает как и
раньше.

**Тесты.**
`AutoContent_FirstIsVisibleChangedTrue_DoesNotRecompute_SecondTransitionDoes` — вызывает приватный
обработчик рефлексией с реальным `DependencyPropertyChangedEventArgs(UIElement.IsVisibleProperty, ...)`
(настоящий `Show()`/`Hide()`-цикл потребовал бы прокрутки `Dispatcher`, которую этот харнесс намеренно не
делает внутри тела теста).

### MINOR m-2: `ApplyHeight` мог гнаться с 150мс roll-up/roll-down анимацией

**Диагноз.** `RollUpBehavior.ApplyRollUp` анимирует `Window.Height` через `DoubleAnimation` (150мс),
которая удерживает `HeightProperty` на приоритете выше обычного присваивания (см. её же H3-комментарий).
Если ЛЮБОЙ `AutoContent`-триггер (например, `CollectionChanged` от добавления файла) срабатывал ПОСРЕДИ
этой анимации, `ApplyHeight`'s прямое присваивание `Window.Height` молча игнорировалось WPF, но
`ChildWindowModel.Size`/spans всё равно записывались, как будто применение удалось — модель уходила в
дрейф от того, что реально останется на экране после завершения анимации.

**Фикс.** Новое публичное свойство `RollUpBehavior.IsAnimating` (backed приватным полем `_isAnimating`,
`true` только пока реально идёт `DoubleAnimation`, всегда `false` при `MotionState.AnimationsEnabled ==
false` — детерминированная ветка без анимации никогда не устанавливает флаг). `MaybeAutoFit` проверяет
его так же, как M-3 проверяет `PanelInteractionTracker`: при `true` — перезаряжает тот же
debounce-таймер вместо применения.

**Тесты.** `MaybeAutoFit_WhileRollUpAnimationActive_DefersUntilAnimationEnds` — форсирует
`_isAnimating = true` напрямую рефлексией (реальная анимация зависит от системной настройки
`MotionState.AnimationsEnabled`, которая непредсказуема в CI-среде) вместо прогона настоящей 150мс
анимации, добавляет иконку (не должно измениться сразу), затем форсирует `false` и вызывает тик таймера
рефлексией — высота применяется отложенно, тем же механизмом, что M-3.

### MINOR m-3 (НЕ применено в этом цикле — задокументировано как открытый вопрос)

Ревью также отметило, что каждый `CollectionChanged` (например, при batch-добавлении нескольких файлов)
синхронно вызывает `MaybeAutoFit → ApplyFitHeight`, а для `GridAligned`-панели это означает отдельный
`_monitorService.GetMonitors()` + недебаунсенный `SaveWindowsState()` на КАЖДОЕ добавление — предложение
ревью: завести ВСЕ AutoContent-триггеры (не только resize по ширине) через один и тот же 200мс debounce.

**Почему не применено этим циклом.** Существующий, уже задокументированный контракт P06
(`ChildWindowContentSizingTests`'s собственные remarks, `docs/PLANS/workspace-grid-06-fit-content.md` §3)
и сразу несколько existующих зелёных тестов (`AutoContent_AddingAnItemAfterLoad_RecomputesHeight`,
`AutoContent_IconSizeChange_RecomputesHeight_...`, `AutoContent_SwitchingViewModeToList_...`) намеренно
проверяют СИНХРОННЫЙ (не отложенный) пересчёт для этих триггеров — оба явно документированы как
рассчитанные на срабатывание "as a direct side effect of a C# event ... neither of which needs a running
message loop" (класс remarks). Задача debounce'ить их — не разметка бага, а изменение контракта,
затрагивающее несколько уже принятых тестов и поведений (например, «Turning AutoContent ON immediately
fits» полагается на синхронность той же цепочки). Ретроактивно дебаунсить их в рамках итерации 1
MINOR-уровня риска показалось нарушением «не удалять/не ломать существующие тесты без необходимости» —
оставлено как явный, зафиксированный здесь открытый вопрос для отдельного прохода (кандидат — P07/P08
доследующая производительная оптимизация; смотри также существующий пункт про производительность
синхронной загрузки P08 в Решении 5 выше). GetMonitors()-часть тем не менее уже гейтится MAJOR-2's
`state.IsGridAligned`-проверкой — лишний вызов остаётся дешёвым не-мутирующим чтением, а не источником
некорректности.

### UX-P1: raw enum-имена в `MainWindow.xaml`'s `ComboBox`-ах «Defaults for new panels»

**Диагноз.** `PanelGeometryModeOptions`/`IconLayoutPolicyOptions` `ComboBox`-ы биндили `ItemsSource`
напрямую на списки enum-значений без `ItemTemplate` — отображали `ToString()` (`"LegacyFreeform"`,
`"FixedGap"`), не совпадающие с формулировками того же переключателя в контекстном меню панели.

**Фикс.** Новый `Converters/EnumDisplayNameConverter` (единый на оба enum) — маппит на ТЕ ЖЕ строки, что
уже показывает `ChildWindow.xaml`'s подменю «Icon layout» (`"Grid aligned"`/`"Free-form (legacy)"`,
`"Fixed spacing"`/`"Adaptive stretch (legacy)"`), подключён через `ComboBox.ItemTemplate` на обоих
`ComboBox`-ах (WPF применяет `ItemTemplate` и к закрытому виду выбранного элемента, не только к
раскрытому списку — отдельного `SelectionBoxItemTemplate` не потребовалось).

**Тесты.** `EnumDisplayNameConverterTests` (новый) — фиксирует ровно эти 4 строки плюс fallback на
`ToString()` для нераспознанного значения и `null → string.Empty`.

### UX-P2: структура меню — «Panel sizing ▸» симметрично «Icon layout ▸»

**Диагноз.** «Fit panel to content», «Auto-size to content» и «Apply sizing mode to all panels…» были
тремя отдельными плоскими пунктами контекстного меню, тогда как параллельная фича «Icon layout» уже
следует паттерну «подменю + „Apply to all panels…“ внутри».

**Фикс.** `ChildWindow.xaml`: три пункта объединены в новое подменю «Panel sizing» (заголовок несёт общий
`AllTrue`-гейт, см. MAJOR-1 выше); порядок содержимого — «Fit panel to content», «Auto-size to content»,
разделитель, «Apply to all panels…» — то же самое расположение, что «Icon layout» уже использует.
Ни один Click-обработчик/`x:Name` не переименован — реструктуризация чисто визуальная (вложенность), все
существующие рефлексивные вызовы в тестах продолжают работать без изменений.

### MINOR m-4 / UX-P1 (диалоги): «on all pages», унифицированное «cannot be undone»

**Решение оркестратора.** Обе broadcast-команды («Icon layout»/«Sizing mode» → «Apply to all panels…»)
применяются ко ВСЕМ панелям на ВСЕХ страницах (без изменений в этом цикле — решение уже
задокументировано в «Реализация P06» §4/`docs/PLANS/workspace-grid-06-fit-content.md` §4), но текст
диалога теперь говорит об этом ЯВНО: `"Apply the \"{X}\" ... to all panels on all pages ({n}
panel(s))? This cannot be undone automatically."` — единая формулировка для обоих диалогов (sizing mode
диалог ранее не имел фразы «cannot be undone» вовсе). Параллельный P03 fix-цикл делает симметричное для
«Align all» (текущая страница — «on the current page») в СВОИХ файлах (`MainWindowViewModel`) — не
затронуто этим циклом.

### NIT

- `Converters/AllTrueMultiConverter` — теперь `sealed` (было `public class` без модификатора,
  несогласованно с остальными однострочными конвертерами проекта, которые все `sealed`).
- Статус этого документа — см. заголовок: возвращён с `Accepted` на `Proposed` (был выставлен
  преждевременно предыдущим проходом) до релизной сверки P09.

**Test log.** `dotnet build DesktopOrganizer.sln` — 0 Error(s) (194 baseline StyleCop-предупреждения на
`DesktopOrganizer.UI`, ни одного нового error). `dotnet test DesktopOrganizer.sln` — 3013/3013 green (0
failed, 0 skipped) — включает все существующие тесты (не удалены) плюс новые из этого цикла
(`PanelSizingGridSupportNotifyHeightResizeCommittedTests`, `EnumDisplayNameConverterTests`, ~20 новых
тестов в `ChildWindowContentSizingTests`, 3 новых в `NativePanelResizeControllerGridAlignedTests`).

**Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
test`:**

22. **MAJOR-1.** Заблокировать layout («Lock layout»), попробовать «Icon layout → Apply to all panels…»
    и «Panel sizing → Apply to all panels…» — оба пункта визуально задизейблены (серые), клик не
    открывает диалог.
23. **MAJOR-2.** На панели без сконфигурированных мониторов (труднодостижимо вживую — скорее регрессия
    после смены разрешения экрана мгновенно после сна/докинга) — переключить `GeometryMode` в
    `GridAligned`, вызвать «Fit panel to content» — высота применяется, панель не схлопывается до 120 DIP
    ширины.
24. **MAJOR-3.** Начать resize Grid-панели с «Auto-size to content» за нижний край, задержать мышь
    зажатой на месте дольше 200мс НЕ отпуская кнопку — высота не должна дёрнуться посреди этой паузы;
    отпустить — высота применяется штатно.
25. **MAJOR-4.** На Grid-панели с «Auto-size to content» потянуть боковой (Left/Right) край — режим
    остаётся «Auto-size to content» (флажок в меню); потянуть нижний/верхний/угловой край — после
    отпускания мыши флажок «Auto-size to content» снимается сам (режим стал `Manual`).
26. **UX-P1.** Открыть настройки → «Defaults for new panels» — оба `ComboBox`-а показывают «Grid
    aligned»/«Free-form (legacy)» и «Fixed spacing»/«Adaptive stretch (legacy)», не сырые имена enum.
27. **UX-P2.** Контекстное меню панели — «Panel sizing ▸» открывает подменю с «Fit panel to
    content»/«Auto-size to content»/«Apply to all panels…», визуально симметрично «Icon layout ▸».
28. **m-1.** Открыть приложение с уже существующей `AutoContent`-панелью — высота при появлении НЕ
    прыгает; переключиться на другую страницу и обратно — высота пересчитывается (если состав файлов
    изменился за это время).
29. **m-2.** На Grid-панели с «Auto-size to content» свернуть/развернуть (roll-up), сразу же (в первые
    150мс анимации) быстро добавить файл через drag&drop на другую видимую часть экрана, затем —
    убедиться, что высота панели после завершения анимации соответствует новому количеству файлов, без
    видимого рассинхрона.

---

## Alternatives

### Для `IconLayoutPolicy` (выбор: per-panel поле + глобальный дефолт для новых панелей)

| Вариант | Механизм | Почему отклонён / принят |
|---|---|---|
| **A (выбран)** | per-panel `ChildWindowModel.IconLayoutPolicy` + `IAppSettings.DefaultIconLayoutPolicy` для новых панелей | Прецедент `IconSize`/`ViewMode`; позволяет пользователю мигрировать по одной панели, не трогая остальные |
| B | Единый глобальный переключатель на всё приложение, без per-panel поля | Отклонён: переключение после миграции мгновенно меняет геометрию **всех** мигрированных панелей разом (нарушает «явная миграция, не сюрприз при апгрейде», CLAUDE.md); либо, если применяется только к новым, не даёт способа опробовать `FixedGap` точечно |
| C | Тихая полная замена — `FixedGap` всегда, `LegacyStretch` удаляется | Отклонён: прямо противоречит CRITIC_REVIEW.md п.2 и мандату P00/P01 «не переписывать молча принятый ADR-018» |
| D | Эвристическое авто-определение политики по признакам панели (версия схемы на момент создания и т.п.), без явного поля | Отклонён: нарушает CLAUDE.md-инвариант «все изменения размеров/позиций — через согласованную layout policy, не разрозненные эвристики»; явное персистентное поле проще тестировать и объяснить пользователю |

### Для учёта хрома (F-3)

| Вариант | Механизм | Почему отклонён / принят |
|---|---|---|
| **A (выбран)** | Именованная константа контракта `PanelMetrics.ContentLeftInset`, `GridMetrics.FileListHPadding` — ссылка на неё | Единый источник правды, без риска layout-цикла |
| B | Измерять `PART_ContentHost.ActualWidth` в момент снапа | Отклонён: снап должен произвести целевую `Window.Width` ДО прохода layout — измерение после layout создаёт циклическую зависимость (тот же класс проблемы, что M-2) |
| C | Убрать фиксированную колонку `8`, сделать `Auto` | Отклонён: уже отвергнут ADR-025 MAJOR-1 (Auto давал ~2.1px джиттер контента на каждом interaction-переходе) — повторное внесение того же регресса ради упрощения этого ADR неприемлемо |

### Для распределения surplus space в `FixedGap`

| Вариант | Механизм | Почему отклонён / принят |
|---|---|---|
| **A (выбран)** | Surplus справа, прижатие влево (переиспользование существующей ветки «неполный ряд») | Не требует новой формулы; сохраняет «позиция 1-й/2-й иконки не зависит от ширины панели» для любого ряда |
| B | Центрирование остатка | Отклонён: требует зависимости `m` от `itemsInRow`, что делает позицию 1-й иконки НЕПОЛНОГО ряда зависимой от того, сколько иконок физически поместилось в конкретный ряд — то есть от ширины панели косвенно, нарушая ключевое требование DoD P05 |
| C | Surplus распределяется поровну между левым и правым краем (без растяжки внутренних зазоров) | Отклонён: две новые переменные (`m_left`, `m_right`) вместо одной, для выгоды, которая не была запрошена ни одним источником (P00/спек/CRITIC_REVIEW); усложнение без обоснованной пользы |

---

## Последствия

### Позитивные

- Пользователь получает предсказуемый, Fences-паритетный шаг между иконками (`FixedGap`) — прямое
  устранение F-5 — без потери легаси-поведения (`LegacyStretch` остаётся доступен и bit-for-bit точен,
  требование DoD P05/P01).
- F-3 устраняется как побочный эффект того же рефакторинга (единая константа хрома), без раздувания
  scope — тот же `PanelMetrics.ContentLeftInset` используется и снап-кодом, и (при необходимости)
  будущей XAML-правкой.
- `AutoContent`/`Fit panel to content` устраняет класс проблем F-4 (высота, отвязанная от контента)
  для пользователей, которые выберут этот режим, не трогая `Manual`-панели тех, кто предпочитает
  контролировать высоту руками.

### UX-компромиссы (что теряется при `FixedGap` относительно `LegacyStretch`)

- **Неровный правый край.** Главный видимый компромисс: полный ряд `FixedGap` не заполняет ширину
  панели ровно — справа остаётся пустая полоса переменной ширины (0 … почти `cell + pmin`, в
  зависимости от того, насколько ширина панели кратна `Extent(k)` для целого `k`). При `LegacyStretch`
  полный ряд всегда доходит точно до `m` от правого края. Пользователь, привыкший к текущему
  «ровному» виду, увидит асимметрию, если использует `FixedGap` на панели с шириной, не кратной сетке
  иконок. Это принятая цена за «шаг не зависит от ширины» — обе цели одновременно недостижимы
  (математическое противоречие, не недосмотр реализации).
- **`FixedGap`, вероятно, острее ощущает M-2** (см. Решение 4) — резкий скачок surplus-space на пороге
  скроллбара, а не мягкая перестройка растяжки.
- Существующие панели после миграции не получают новый вид автоматически (`LegacyStretch` по
  умолчанию) — пользователь должен сознательно переключить панель на `FixedGap`, если хочет новый
  вид; это осознанный компромисс в пользу «не менять существующее без согласия», а не недоработка.

---

## Открытые вопросы

**Q1 (закрыт этим документом).** Per-panel vs per-global область `IconLayoutPolicy` — см. Решение 6/
Alternatives (выбран per-panel + глобальный дефолт для новых панелей).

**Q2 (решён 2026-09-22: A).** Применять фикс F-3 (`PanelMetrics.ContentLeftInset`) к `LegacyStretch` тоже, или
только к `FixedGap`, оставив легаси-панели с исторически неточным (заниженным на 1 колонку)
`RowCapacity` навсегда?
- **A (рекомендовано).** Применить к обеим политикам — это баг учёта хрома, не часть контракта
  «stretch vs fixed», прецедент одноразового пересдвига точки снапа уже принят в ADR-018
  (`DefaultMinGap` 0→4).
- **B.** Применить только к `FixedGap`; `LegacyStretch` продолжает терять колонку навсегда — максимизирует
  буквальное «bit-for-bit» прочтение гарантии совместимости, ценой сохранения известного визуального
  дефекта в легаси-режиме.
- **C.** Применить к обеим, но за отдельным новым флагом миграции (двойная матрица `IconLayoutPolicy` ×
  `ChromeFixApplied`) — отклонено авторами этого документа как избыточное усложнение без запрошенной
  пользы, но перечислено для полноты.

**Q2 уточнение (fix-цикл P05, 2026-09-22).** Выбор A выше — «применить F-3 к обеим политикам» — говорит
только о ФОРМУЛЕ (`GridMetrics.FileListHPadding`, используемой И `LegacyStretch`, И `FixedGap` в снап-
коде и в реальном рендере `AdaptiveIconGridPanel`). Он НЕ подразумевал и НЕ подразумевает «поэтому при
каждой загрузке нужно повторно прогонять снап, чтобы применить эту формулу к уже сохранённым панелям» —
это было отдельное, ошибочное умозаключение в исходной реализации P05 (`ChildWindow.Initialize` вызывал
`SnapWidthToGrid()` безусловно), исправленное как MAJOR-1 этого же fix-цикла (см. выше). Формула F-3
применяется к обеим политикам **только там, где снап и так должен произойти** (явное действие
пользователя — resize-жест, смена `IconSize`/`ViewMode`) — никогда пассивно при загрузке.

**Q3 (решён 2026-09-22: B — P06 ограничен Grid-режимом, List остаётся `Manual`).** Формула `AutoContent` для **List-режима** (List не использует
`GridMetrics`/`AdaptiveGridLayout`, у него нет аналитического аналога высоты строки).
- **A.** Формализовать новую именованную аналитическую константу высоты строки List-режима в этом же
  семействе (`GridMetrics`-подобный статический класс) и включить List в объём P06 сразу.
- **B (рекомендовано для первой итерации).** Ограничить `AutoContent`/`Fit panel to content` в P06
  Grid-режимом; List-режим остаётся только `Manual` до отдельного follow-up ADR — по прецеденту
  ADR-018, который сознательно не тронул List-режим вообще.
- **C.** Грубая оценка по уже существующим литералам `ListViewTemplate` без формализации константы —
  отклонено: реинтродуцирует анти-паттерн «дублированный литерал», который всё семейство
  `GridMetrics`/`PanelMetrics`/`AdaptiveGridLayout` существует, чтобы устранить.

---

## Manual verify (Win10/Win11)

Перенесено/дополнено из ADR-018 Manual verify — не выполнено в этой среде (нет живого Windows GUI):

1. `FixedGap` на панели произвольной ширины, не кратной `Extent(k)`: шаг между 1-й и 2-й иконкой
   идентичен на панелях 3 разных ширин при одном `IconSize`; surplus space виден только справа.
2. F-3 фикс: после применения панель с шириной, ранее «терявшей» колонку, показывает на одну колонку
   больше при том же `IconSize`, без визуального клиппинга/наложения последней колонки на правый край.
3. `LegacyStretch` на тех же панелях — растяжка полного ряда визуально не изменилась относительно
   текущего поведения (кроме, при выборе Q2=A, точки, где терянная по F-3 колонка теперь появляется).
4. `Fit panel to content` на 0/1/2/8+ иконках, обоих `IconLayoutPolicy`, обоих `ViewMode`.
   **Superseded (P06, Q3 = B):** ограничено Grid-режимом — в List-режиме пункт меню задизейблен, а не
   просто «работает одинаково в обоих режимах», как предполагал этот пункт до резолюции Q3. Проверять
   только для `ViewMode = Grid`; для List — пункт 15 ниже.
5. `AutoContent`: добавление/удаление иконки, смена вкладки, roll-up/roll-down, смена `IconSize` —
   высота отзывается корректно и без «прыжка» при разворачивании после сворачивания.
6. `Manual` панель: добавление/удаление иконок много раз подряд не меняет `Window.Height` ни разу.
7. Мигрированный `application-state.json`: все существующие панели — `LegacyStretch` + `Manual`, без
   визуального изменения геометрии сразу после апгрейда — **включая** (fix-цикл P05 MAJOR-1, 2026-09-22)
   отсутствие любого одноразового скачка `Window.Width` при первой загрузке после апгрейда для панелей с
   уже персистентной (пред-F-3) шириной: конкретно проверить, что панель, сохранённая с шириной,
   соответствующей `k` колонкам ПО СТАРОЙ (пред-F-3) формуле, открывается на той же самой ширине, а НЕ
   пере-снапается на F-3-скорректированное значение (было бы на `PanelMetrics.ContentLeftInset` = 8 DIP
   шире) — это теперь покрыто юнит-тестом (`ChildWindowInitializeTests`), но живого Windows-подтверждения
   всё ещё нет.
8. Drag-reorder/selection/rename/tooltips на `FixedGap`-панели — регрессий нет (см. Решение 5).

**Fix-цикл P05, итерация 2 (2026-09-22) — дополнительные пункты (`CapacityWidth`/M-2, MAJOR-2, MINOR-1):**

9. **M-2 фактически достигнут (`CapacityWidth`).** На `FixedGap`-панели, заполненной ровно до порога
   появления/исчезновения вертикального скроллбара (добавлять/удалять элементы по одному вокруг этого
   порога), число колонок и позиция surplus-зоны справа НЕ меняются в момент появления/исчезновения
   реального скроллбара — только вылезает/прячется сама полоса. Ключевая проверка, которую итерация 1
   заявляла закрытой, но фактически не закрывала (двойное вычитание, см. этот же раздел ADR выше);
   визуально это должно выглядеть иначе, чем на скриншоте/репро из ревью итерации 1.
10. **`CapacityWidth`-биндинг переживает ресайз панели.** Растянуть/сузить `FixedGap`-панель за боковой
    край (ресайз ширины) несколько раз подряд, каждый раз проверяя пункт 9 на новой ширине — `ActualWidth`
    внутреннего `ScrollViewer`а должен успевать переизмериться до следующего измерения панели без видимого
    «отставания на один кадр» (лишнего мигания/дрожания раскладки при живом ресайзе).
11. **Per-monitor DPI (наследие ADR-022 D-6, актуально и для `CapacityWidth`).** Приложение не
    Per-Monitor-V2-aware (System DPI). Перетащить `FixedGap`-панель на монитор с ДРУГИМ DPI (125%/150%) и
    повторить пункт 9 там — `ScrollViewer.ActualWidth`, `SystemParameters.VerticalScrollBarWidth` и
    итоговая ширина `CapacityWidth − ReservedVerticalScrollBarWidth` должны остаться внутренне
    согласованными (тот же инвариант «колонка не прыгает»), даже если абсолютные DIP-значения на новом
    мониторе отличаются от исходного — фикс не вводит новую DPI-зависимость, но и не тестировался живьём
    ни на каком DPI, кроме 100% (юнит-тесты используют управляемые значения, не реальный `HwndSource`).
12. **Смена ширины скроллбара темой/настройками ОС.** Изменить системную ширину полосы прокрутки
    (Панель управления → Мышь/Специальные возможности → «Толщина ползунка», или сторонняя тема
    Windows, если применимо) МЕЖДУ запусками приложения (перечитывается `SystemParameters.
    VerticalScrollBarWidth` — не отслеживается как live-событие ни этим циклом, ни предыдущим), затем
    повторить пункт 9 — новая ширина скроллбара должна быть учтена корректно (та же стабильность), без
    необходимости менять код.
13. **MAJOR-2: переключение `IconLayoutPolicy` на живой панели (когда появится UI-переключатель, P06,
    вне scope этого цикла — до тех пор проверяемо только вручную через отладчик/временный тестовый
    хук).** Переключить `IconLayoutPolicy` панели с `LegacyStretch` на `FixedGap` и обратно — ширина
    окна должна пере-снапнуться немедленно (без необходимости также менять `IconSize`/`ViewMode`, как
    было до этого фикса), без визуального «прыжка» сильнее одного дискретного шага сетки.
14. **MINOR-1: клик/drop в левый гуттер ряда `FixedGap`-панели.** Начать drag-reorder и навести курсор
    точно в левое поле (`m`) НЕ первого ряда — индикатор вставки должен встать ПЕРЕД первым элементом
    этого ряда, не после последнего элемента того же ряда (визуальное подтверждение фикса MINOR-1, юнит-
    тесты покрывают геометрию, но не сам drag-visual индикатор).

**P06 (2026-09-22) — дополнительные пункты («Fit panel to content»/`AutoContent`/«Icon layout»/«Apply to
all panels», см. «Реализация P06» выше):**

15. **«Fit panel to content» в List-режиме — задизейблено, не просто неактивно.** Открыть контекстное
    меню List-режимной панели — пункт «Fit panel to content» и «Auto-size to content» показаны серыми
    (`IsEnabled=False`), с подсказкой (ToolTip), объясняющей, что формула доступна только в Grid-режиме
    (замена пункта 4 выше для этого режима — Q3 = B).
16. **`AutoContent`: живой пересчёт при добавлении/удалении иконки.** На Grid-панели с включённым
    «Auto-size to content» перетащить файл на панель — высота должна визуально «подтянуться» под новое
    число рядов сразу после того, как иконка появится (без видимого запаздывания/двойного скачка).
    Удалить иконку — высота уменьшается симметрично.
17. **`AutoContent`: пересчёт после ручного resize по ширине — с задержкой, без дрожания в процессе.**
    Потянуть боковой край Grid-панели с «Auto-size to content» — высота НЕ должна меняться на каждый
    промежуточный пиксель ширины во время самого перетаскивания; она должна «settle» на новое значение
    примерно через ~200мс ПОСЛЕ того, как палец/курсор отпущен (или движение прекратилось) — визуальное
    подтверждение debounce-таймера, который юнит-тесты не покрывают (см. «Реализация P06»'s «Не
    протестировано автоматически»).
18. **`AutoContent`: пересчёт при активации панели (смена страницы).** На панели с «Auto-size to
    content» в отдельной вкладке/странице настроек изменить состав файлов панели (или её `IconSize`),
    затем переключиться на другую страницу и обратно — высота при возврате должна отражать актуальный
    fit, без «прыжка» позже (`Window.IsVisibleChanged`-триггер, не покрыт живым UI-тестом).
19. **Roll-up ↔ `AutoContent`/«Fit panel to content» — визуальное подтверждение.** На Grid-панели с
    «Auto-size to content» свернуть панель (roll-up), затем — пока свёрнута — добавить/удалить иконки;
    развернуть панель обратно — она должна раскрыться СРАЗУ на новую (пере-посчитанную во время
    сворачивания) высоту, без промежуточного кадра со старой высотой и без второго пере-расчёта после
    разворачивания. Аналогично для явного «Fit panel to content», вызванного, пока панель свёрнута:
    развернуть — высота должна быть уже новой.
20. **«Apply to all panels» (оба вида) — реальный диалог подтверждения.** На панели с 3+ открытыми
    панелями на РАЗНЫХ страницах выбрать «Icon layout → Apply to all panels…» — диалог должен назвать
    точное число панелей (включая панели с других страниц, не только текущей); подтвердить — все панели
    (в т.ч. на других страницах) должны обновить свой видимый вид (шаг между иконками) без необходимости
    их индивидуально открывать/закрывать; отменить — ни одна другая панель не должна измениться.
    Повторить для «Sizing mode → Apply to all panels…» (Grid-режимные панели должны сразу же visually
    fit при переходе в `AutoContent` через эту команду — то же немедленное поведение, что при
    индивидуальном переключении).
21. **Настройки → «Defaults for new panels» → новые ряды «Panel geometry»/«Icon layout».** Открыть окно
    настроек, изменить оба новых `ComboBox`-а, создать новую панель («Add panel») — она должна получить
    выбранные значения; существующие панели не должны измениться.

**Manual verify (Win10/Win11) — не выполнено, среда разработки ограничена `dotnet build`/`dotnet
test`.**
