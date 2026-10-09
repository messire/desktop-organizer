# ADR-029 — журнал реализации (P05, P06, fix-циклы)

> **Назначение.** Это перенесённая БЕЗ потерь историческая часть [ADR-029](../../adr/ADR-029-icon-layout-policy.md):
> «Реализация P05», «Fix-цикл P05» (итерации 1 и 2), «Реализация P06», «Fix-цикл P06» с manual-verify-списком
> пп. 22–29, а также amendment «Решение 4 (P05 fix-cycle)» и «Q2 уточнение», которые раньше жили в теле
> решений. Исходный ADR на ~1280 строк на 65% состоял из этого журнала; в рамках ADR-audit WP-E (2026-10-09)
> ADR-029 переписан как документ на ~300 строк (действующие решения), а журнал вынесен сюда. Текст ниже
> сохранён дословно; единственные правки — этот заголовок и пометки «[ADR-audit WP-E]».
>
> **Как читать.** Журнал хронологический и содержит промежуточные формулировки, часть которых позже
> отменена или уточнена (например, `ContentLeftInset` — «accent-колонка 8 DIP» → после ADR-030 это
> `2·ChromeOuterInset + 2·ContentSidePadding`; «`SnapWidthToGrid` не различает `GeometryMode`» → закрыто
> ADR-028 P04 §4; `AffectsMeasure|AffectsArrange` «только `ArrangeOverride`» → после M-2 обе фазы).
> **Действующие правила — в [ADR-029](../../adr/ADR-029-icon-layout-policy.md), а не здесь**; при расхождении
> приоритет у ADR. Предыдущая (полная) редакция ADR-029 — в истории git
> (`git log --follow -- adr/ADR-029-icon-layout-policy.md`, коммит `v0.8.0` и ранее).
>
> Парный журнал для ADR-028 — [workspace-grid-adr028-implementation-log.md](workspace-grid-adr028-implementation-log.md).

---

## Из тела решений: Amendment к Решению 4 (P05 fix-cycle, 2026-09-22) — резервирование ширины скроллбара для `FixedGap`

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

> [ADR-audit WP-E] Эта формулировка частично уточнена итерацией 2 (CRITICAL-1, `CapacityWidth`, ниже): резерв
> вычитается ОДИН раз от стабильной внешней ширины `CapacityWidth`, а не из `availableSize` напрямую.

## Из тела решений: «Q2 уточнение (fix-цикл P05, 2026-09-22)»

**Q2 уточнение (fix-цикл P05, 2026-09-22).** Выбор A выше — «применить F-3 к обеим политикам» — говорит
только о ФОРМУЛЕ (`GridMetrics.FileListHPadding`, используемой И `LegacyStretch`, И `FixedGap` в снап-
коде и в реальном рендере `AdaptiveIconGridPanel`). Он НЕ подразумевал и НЕ подразумевает «поэтому при
каждой загрузке нужно повторно прогонять снап, чтобы применить эту формулу к уже сохранённым панелям» —
это было отдельное, ошибочное умозаключение в исходной реализации P05 (`ChildWindow.Initialize` вызывал
`SnapWidthToGrid()` безусловно), исправленное как MAJOR-1 этого же fix-цикла (см. выше). Формула F-3
применяется к обеим политикам **только там, где снап и так должен произойти** (явное действие
пользователя — resize-жест, смена `IconSize`/`ViewMode`) — никогда пассивно при загрузке.

## Из шапки (устаревшая пометка статуса, 2026-09-22)

> **Статус:** Proposed (Решения 1–5 landed P01/P05; Решение 6 landed P02b; Решение 7 + UI-часть Решения 6
> landed P06 — см. «Реализация P06»/«Fix-цикл P06» ниже; статус НАМЕРЕННО возвращён с `Accepted` на
> `Proposed` fix-циклом P06 — см. NIT там же — и остаётся так до релизной сверки P09,
> `docs/fix-prompts/09-acceptance.md`)
> **Implemented 2026-09-22 (P02–P06), Accepted pending manual verify (Win10/Win11) — см. P09** — та же
> причина и тот же gate, что у ADR-028 (парный документ этого пакета): реализация и тесты завершены, но
> manual verify (`FixedGap`/`AutoContent`/«Fit panel to content» на живом экране, пп. 1–29 ниже) не
> выполнен в этой среде.

> [ADR-audit WP-E] Снято: manual verify выполнена на Windows 11 24H2 2026-09-27 (`docs/ROADMAP.md`), статус
> ADR-029 — `Accepted`. Пометка оставлена как исторический след.

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

> [ADR-audit WP-E] Пробел закрыт позднее: ADR-028 «Реализация P04» §4 добавил ранний выход
> `PanelSizingGridSupport.IsGridAligned(...)` в обе копии `SnapWidthToGrid` (проверено по коду:
> `PanelResizeController.SnapWidthToGrid`, `NativePanelResizeController.SnapWidthToGrid`).

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

> [ADR-audit WP-E] Выполнено на Windows 11 24H2 2026-09-27 (`docs/ROADMAP.md`); Win10 проект не проверяет
> (отказ от проверки). Пункты ниже оставлены как исторический чек-лист. Шапка оригинала ссылалась на
> «пп. 1–29», но в теле ADR-029 фактически перечислены только пп. 22–29 (fix-цикл P06); пп. 1–21 в
> документе отсутствовали — это зафиксированный дрейф, а не потеря при переносе.

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
