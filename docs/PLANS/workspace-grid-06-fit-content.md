# Workspace Grid — P06 Fit-to-content (`PanelContentSizer`, `AutoContent`, «Icon layout»/«Apply to all panels»)

**Этап:** P06 пакета [`docs/fix-prompts/`](../fix-prompts/README.md) (`06-fit-content.md`) · **Ветка:**
`bugfix/workspace-grid` (этот проход выполнен на изолированном worktree, копия @ `d514fb4`) ·
**Статус:** реализовано, fix-цикл итерация 1 (2026-09-22) — см.
[ADR-029 «Fix-цикл P06»](../../adr/ADR-029-icon-layout-policy.md) для MAJOR-1..4/MINOR/UX-P1/P2/NIT ·
**Дата:** 2026-09-22

Опирается на [ADR-029](../../adr/ADR-029-icon-layout-policy.md) (Решение 7 — `PanelSizingMode`/«Fit
panel to content»; см. там же «Реализация P06» для формальных решений, затрагивающих сам ADR-контракт),
[ADR-028](../../adr/ADR-028-workspace-grid.md) (workspace panel grid — spans/`PanelGeometryMode`, куда
округляется итоговая высота для `GridAligned`), `docs/PLANS/workspace-grid-00-baseline.md` (F-4 — «высота
полностью отвязана от контента»), `docs/PLANS/workspace-grid-migration.md` §10 (`ChildWindowModel.
SizingMode`/`IconLayoutPolicy` уже персистентны, per-panel; `IAppSettings.DefaultIconLayoutPolicy`),
`docs/PLANS/workspace-grid-03-04-integration.md` §1 (Stage S — `PanelGeometryWriter`,
`PanelGridStateResolver`, оба уже landed на этой ветке и переиспользуются здесь без изменений).

---

## 1. Новые/изменённые файлы

| Файл | Роль |
|---|---|
| `src/DesktopOrganizer.UI/Models/PanelContentSizer.cs` (новый) | Чистая, WPF-независимая формула ADR-029 Решения 7: `ResolveColumnCapacity`/`ResolveDisplayRowCount`/`ComputeRawContentHeight`/`RoundForLegacyFreeform`/`RoundHeightUnitsForGridAligned`/`FitHeight`. |
| `src/DesktopOrganizer.UI/Views/ChildWindow.ContentSizing.cs` (новый партиал) | Click-обработчики контекстного меню, `PanelSizingMode.AutoContent`-подписки/дебаунс, «Apply to all panels» broadcast. |
| `src/DesktopOrganizer.UI/Converters/AllTrueMultiConverter.cs` (новый) | Универсальный `IMultiValueConverter` (AND нескольких bool-биндингов из разных источников) — гейтит «Fit panel to content»/«Auto-size to content» на `CanEnterEditLayout` И `ViewMode == Grid` одновременно. |
| `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` | Только блок `<ContextMenu x:Name="HeaderContextMenu">`: новые пункты «Icon layout» (радио + «Apply to all panels…»), «Fit panel to content», «Auto-size to content» + «Apply sizing mode to all panels…»; плюс `xmlns:conv` на корневом `<Window>` (обязательный минимум для резолва `AllTrueMultiConverter` в XAML). |
| `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` | Аддитивно: новое поле `_confirmation : IConfirmationService` + конструкторный параметр; три однострочных хука (`InitializeContentSizing()`/`AttachContentSizing(model)`/`DetachContentSizing()`). `OnModelPropertyChanged` (P04) не тронут. |
| `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs` | Новый `SetIconLayoutPolicyCommand` (`RelayCommand<IconLayoutPolicy>`), тот же идиом, что `SetContentFilterCommand`/`SetSortModeCommand`. |
| `src/DesktopOrganizer.UI/ViewModels/MainWindowViewModel.cs` | Новые forwarding-свойства `DefaultPanelGeometryMode`/`PanelGeometryModeOptions`/`DefaultIconLayoutPolicy`/`IconLayoutPolicyOptions` → `GlobalDefaultsViewModel` (уже существовали там, не было UI). |
| `src/DesktopOrganizer.UI/Views/MainWindow.xaml` | Только секция «Defaults for new panels»: два новых `ComboBox`-ряда. |
| `tests/DesktopOrganizer.Tests/Unit/Models/PanelContentSizerTests.cs` (новый) | 29 тестов, чистая математика. |
| `tests/DesktopOrganizer.Tests/Unit/Views/ChildWindowContentSizingTests.cs` (новый) | 17 тестов, реальный `ChildWindow` (STA-harness, тот же приём, что `ChildWindowInitializeTests`). |
| `tests/DesktopOrganizer.Tests/Unit/ViewModels/ChildWindowViewModelTests.cs` | +2 теста (`SetIconLayoutPolicyCommand`). |
| `tests/DesktopOrganizer.Tests/Unit/ViewModels/MainWindowViewModelTests.cs` | +6 тестов (forwarding-свойства/Options). |
| `tests/DesktopOrganizer.Tests/Unit/Views/ChildWindowInitializeTests.cs` | Механическая правка: `new ChildWindow(...)` получил девятый обязательный аргумент (`Mock.Of<IConfirmationService>()`). |
| `adr/ADR-029-icon-layout-policy.md` | Новая секция «Реализация P06»; статус `Proposed → Accepted`; 7 новых пунктов Manual verify; правка пункта 4 (superseded by Q3 = B). |

**Не создано/не тронуто** (сознательно): `IWindowManager`/`WindowManager.cs` (см. §4 ниже — не
потребовался новый метод), `Resources/Templates.xaml`, `ChildWindow.Sizing.cs`,
`ChildWindow.WorkspaceGrid.cs`, `Behaviors/RollUpBehavior.cs`, `Behaviors/PanelGeometryWriter.cs`,
`Models/PanelGridState.cs` — все читаются, ни один не изменён.

---

## 2. Формула (`PanelContentSizer`)

```
columnCapacity  = AdaptiveGridLayout.RowCapacity(contentWidth, cell, DefaultMinGap, m)
                  contentWidth = windowWidth − GridMetrics.ContentHorizontalChrome(policy, vScrollBarWidth)
                  cell = GridMetrics.CellSize(iconSize); m = EdgePadding(cell, DefaultEdgePaddingRatio)

displayRows     = itemCount <= 0 ? 1 : AdaptiveGridLayout.RowCount(itemCount, columnCapacity)

rawHeight       = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding
                + AdaptiveGridLayout.Extent(displayRows, RowHeight(iconSize), DefaultMinGap, m_v)
                  m_v = EdgePadding(RowHeight(iconSize), DefaultVerticalEdgePaddingRatio)  // = 0

FitHeight       = geometryMode == GridAligned
                    ? SpansToBounds(GridSpan(1, RoundHeightUnitsForGridAligned(rawHeight)), U, G).Height
                    : Math.Ceiling(rawHeight)                                   // LegacyFreeform
```

**Отличие от исходного черновика ADR-029 Решения 7.** Черновик использовал голый
`Window.Width − PanelMetrics.ContentLeftInset` для `contentWidth` — написан до P05's fix-cycle 2
(`CapacityWidth`/M-2). Реализация здесь использует policy-aware `GridMetrics.ContentHorizontalChrome`
(тот же chrome, которым уже пользуется весь снап-код), чтобы `FixedGap`-панель, автоматически
подгоняемая под контент, считала РОВНО тот `columnCapacity`, который реально отрисует
`AdaptiveIconGridPanel` для той же ширины окна — без этого уточнения P06 заново вносил бы «lost
column» класс рассинхрона, который F-3/M-2 уже закрыли для другого пути (resize-снап). Формально
задокументировано как правка черновика в самом ADR-029 (см. «Реализация P06» там же), не расхождение с
принятым решением.

**Однонаправленность.** `windowWidth` — параметр, никогда не выводится и не перечитывается внутри
`PanelContentSizer`; единственный побочный эффект любого вызова — `Window.Height` (через
`PanelGeometryWriter`) либо `ChildWindowModel.Size`/`RollUpBehavior.ExpandedHeight` (roll-up ветка, см.
§3). Ширина никогда не пишется этим кодом ни при каких обстоятельствах.

---

## 3. Правила изменения (когда высота пересчитывается, а когда — нет)

Ниже — таблица из ADR-029 Решения 7, дополненная фактическим механизмом (см. также ADR-029's
«Реализация P06» — эта таблица её краткое resume, не дубль):

| Событие | `Manual` | `AutoContent` | Механизм |
|---|---|---|---|
| Добавление/удаление/скрытие иконки (в т.ч. content-фильтром) | не меняет | пересчитывает | `ChildWindowViewModel.FileItems.CollectionChanged` |
| `IconSize`/`ViewMode`/`IconLayoutPolicy` меняются | не меняет | пересчитывает (после пере-снапа ширины, если он случился) | `ChildWindowModel.PropertyChanged`, подписан ПОСЛЕ P04's `OnModelPropertyChanged` |
| Ручной resize по ширине | не меняет | пересчитывает, с debounce ~200мс, не на каждый кадр | `Window.SizeChanged`, фильтр `e.WidthChanged`, `DispatcherTimer` |
| Смена вкладки/страницы → панель снова видна | не меняет | пересчитывает | `Window.IsVisibleChanged` |
| Смена монитора/DPI без изменения ширины | не меняет | не меняет | нет отдельного триггера — покрывается триггером ширины, если он сработал |
| Roll-up (`IsRolledUp = true`) | не меняет | пересчитывает `RollUpBehavior.ExpandedHeight`/`ChildWindowModel.Size`, НИКОГДА `Window.Height` | `ApplyHeight`'s `IsRolledUp`-ветка |
| Roll-down (`IsRolledUp = false`) | — | не отдельный триггер — уже актуальный `ExpandedHeight` применяется штатным `RollUpBehavior.ApplyRollUp` | — |
| Переключение `SizingMode` → `AutoContent` | — | немедленный fit (иначе режим выглядел бы «не подействовавшим») | побочный эффект той же `PropertyChanged`-подписки |
| Переключение `SizingMode` → `Manual` | — | высота НЕ меняется | явный early-return в `MaybeAutoFit` |
| Явное «Fit panel to content» | применяет ОДИН РАЗ, не меняет `SizingMode` | применяет ОДИН РАЗ (то же самое) | `FitPanelToContentMenu_Click`, работает независимо от режима |
| `ViewMode = List` | не меняет | «замораживается» — `MaybeAutoFit`'s `ViewMode`-гейт молча ничего не делает, высота остаётся какой была | `PanelViewMode.Grid`-проверка |
| `IsLayoutLocked = true` (ADR-026) | — | НЕ блокирует пассивный пересчёт (см. §5) | — |

**Не трогать ручные размеры — единственный инвариант, который нельзя нарушать ни одной будущей правкой:**
`PanelSizingMode.Manual` не подписан вообще ни на что через `MaybeAutoFit` (единственный вызов —
`ApplyFitHeight`, достижимый только явным Click на «Fit panel to content»). Любой будущий код, который
захочет автоматически менять `Window.Height` для панели, обязан сначала прочитать
`ChildWindowModel.SizingMode` и рано выйти для `Manual` — ровно тот же паттерн, что уже применяет
`MaybeAutoFit`.

---

## 4. «Apply to all panels» — область действия и почему не понадобился новый `IWindowManager`-метод

Обе broadcast-команды («Icon layout → Apply to all panels…», «Sizing mode → Apply to all panels…»)
применяются **ко всем панелям на всех страницах**, не только к текущей — `IWindowManager.
GetAllChildWindows()` уже ничего не фильтрует по `ChildWindowModel.PageId`, и заведение отдельного
фильтра «только текущая страница» исключительно для этой фичи создало бы асимметрию с любым другим
потребителем той же коллекции (например, `LayoutSnapshotService`). Решение оркестратора этого прохода,
задокументировано здесь по требованию промпта «реши и задокументируй».

`IWindowManager.GetAllChildWindows()` возвращает **живые** инстансы `ChildWindowModel` — те же самые,
на которые подписан открытый `ChildWindow` (`WindowManager._childWindows[model.Id] = (window, model)`).
Присваивание `panel.IconLayoutPolicy = targetPolicy` / `panel.SizingMode = targetMode` в цикле поэтому
автоматически доходит до каждой открытой панели через её собственные, уже существующие подписки
(resize-снап P04, `AutoContent`-пересчёт этого же прохода) — broadcast-код не дублирует ни то, ни
другое, только присваивает свойство и один раз вызывает `SaveWindowsState()`. Итог: пункт промпта
«`IWindowManager`/`WindowManager` только аддитивный метод, если нужен» закрыт решением «не нужен» — ни
один из этих двух файлов не изменён этим проходом.

Обе команды — confirmation-gated через уже существующий `IConfirmationService` (тот же сервис, что
`MainWindowViewModel.RemovePanel`), диалог называет число затрагиваемых панелей.

---

## 5. `IsLayoutLocked` (ADR-026) — разное поведение для явного действия и пассивного пересчёта

- **Явные действия** («Fit panel to content», Click по «Auto-size to content», обе «Apply to all
  panels…») — гейтятся `ILayoutEditModeService.IsLayoutLocked` (через `CanEnterEditLayout` в XAML —
  `IsEnabled`, плюс defensive-проверка внутри самого обработчика). Тот же уровень строгости, что уже
  применяют соседние «Size»/«Align to workspace grid».
- **Пассивный пересчёт `AutoContent`** (добавление/удаление иконки и т.п.) — **не** гейтится
  `IsLayoutLocked`. Обоснование: добавление/удаление файлов уже разрешено при заблокированном layout
  (существующая формулировка UI — «Roll up/down, renaming, and file operations are unaffected»); если
  бы высотный отклик на РАЗРЕШЁННОЕ действие молча блокировался, это читалось бы как баг, а не как
  защита позиции/размера от жеста.

---

## 6. List-режим (Q3 = B)

«Fit panel to content» и «Auto-size to content» оба no-op'ят и задизейблены (`AllTrueMultiConverter`,
комбинирует `CanEnterEditLayout` И `Model.ViewMode == Grid`) вне `PanelViewMode.Grid` — ToolTip
объясняет причину. «Icon layout» **не** гейтится `ViewMode` — поле безвредно/инертно в List-режиме,
тот же принцип, что уже применяет соседнее подменю «Size» (P04).

Follow-up (вне объёма P06, см. ADR-029 Открытые вопросы Q3): формализовать аналитическую высоту строки
List-режима, если Fences-паритет когда-либо потребует `AutoContent` там же.

---

## 7. Test log

```
dotnet build DesktopOrganizer.sln     — 0 Error(s) (существующие StyleCop-предупреждения — baseline, не новые)
dotnet test  DesktopOrganizer.sln     — 2704/2704 green, 0 failed, 0 skipped
```

Новые тесты: `PanelContentSizerTests` (29), `ChildWindowContentSizingTests` (17),
`ChildWindowViewModelTests` (+2), `MainWindowViewModelTests` (+6). Существующие тесты не удалены;
`ChildWindowInitializeTests`'s единственный `new ChildWindow(...)` вызов обновлён механически (девятый
аргумент).

**Не протестировано автоматически** (см. ADR-029 Manual verify, пункты 15–21):
- Debounce-таймер `Window.SizeChanged`-триггера — `ChildWindowContentSizingTests`'s STA-harness не
  крутит `Dispatcher`-цикл внутри тела теста (тот же ограничивающий фактор, что и
  `ChildWindowInitializeTests`), поэтому `DispatcherTimer.Tick` физически не может сработать
  синхронно в этих тестах.
- Визуальная анимация roll-up/roll-down в сочетании с обновлённым `RollUpBehavior.ExpandedHeight`.
- Реальный DPI ≠ 100% (юнит-тесты используют управляемые DIP-значения, не реальный `HwndSource`).

---

## 8. Release note (для P09 — `docs/fix-prompts/09-acceptance.md`)

Пункты, которые релизная сверка (P09) должна знать о поведенческих изменениях этого прохода:

1. **F-3 «потерянная колонка» — легаси-панели получат её при первом ПОЛЬЗОВАТЕЛЬСКОМ переснапе, не
   раньше.** Это поведение landed в P05 (не в этом проходе) — повторяется здесь как явное
   release-note-требование промпта P06. Панель, существовавшая до апгрейда (`LegacyStretch` после
   миграции), не меняет геометрию при простой загрузке (ADR-028 "no jump on first launch"); колонка,
   которую F-3 чинит, появляется только когда пользователь САМ вызывает пере-снап — resize-жест, смена
   `IconSize`/`ViewMode`, ИЛИ (после P06) переключение `IconLayoutPolicy` через новый пункт меню «Icon
   layout». До этого действия легаси-панель выглядит РОВНО как до апгрейда.
2. **Новые пункты контекстного меню панели** (правый клик по заголовку): «Icon layout» (подменю:
   «Adaptive stretch (legacy)» / «Fixed spacing», радио + «Apply to all panels…»), «Fit panel to
   content» (одноразовое действие), «Auto-size to content» (переключатель, чекбокс) + «Apply sizing
   mode to all panels…». Последние два недоступны (задизейблены, с ToolTip) в List-режиме.
3. **Новые настройки** («Defaults for new panels» в окне настроек): «Panel geometry»
   (`LegacyFreeform`/`GridAligned`) и «Icon layout» (`Adaptive stretch (legacy)`/`Fixed spacing`) —
   оба поля УЖЕ существовали как `IAppSettings`-ключи (workspace-grid-migration.md §10) с дефолтами
   `LegacyFreeform`/`FixedGap` соответственно, но не имели элемента управления до этого прохода;
   пользователь, ни разу не открывавший это окно, не заметит изменения (дефолты не изменились, только
   стали видимыми/настраиваемыми).
4. **Никаких изменений формата persisted JSON.** `SizingMode`/`IconLayoutPolicy` уже были персистентны
   до этого прохода (P02b) — P06 добавляет только UI/поведение поверх уже существующей схемы v5, не
   новую версию схемы и не новую миграцию.

---

## 9. Открытые вопросы / известные ограничения (перенесены на будущие проходы)

- **List-режимная формула `AutoContent`** — вне объёма (ADR-029 Открытые вопросы Q3), см. §6 выше.
- **`SnapWidthToGrid` не различает `PanelGeometryMode`** (пред-существующий пробел, не создан и не
  устранён этим проходом — см. ADR-029's `OnModelPropertyChanged` doc-комментарий) — `AutoContent`'s
  height-пересчёт наследует ту же особенность транзитивно (пересчитывает после уже случившегося
  ширинного пере-снапа, каким бы он ни был для любого `GeometryMode`).
- **Живой визуальный debounce/roll-up/DPI** — не проверено на реальном Windows-дисплее в этой среде
  (см. §7 «Не протестировано автоматически» и ADR-029 Manual verify пункты 15–21).
- **Fix-цикл P06 (2026-09-22, итерация 1).** MAJOR-1..4, MINOR m-1/m-2, UX-P1/P2, NIT — реализованы, см.
  [ADR-029 «Fix-цикл P06»](../../adr/ADR-029-icon-layout-policy.md). MINOR m-3 (`CollectionChanged`'s
  недебаунсенный `GetMonitors()`/`SaveWindowsState()`) сознательно НЕ применён этим циклом — конфликтует
  с уже задокументированным и протестированным контрактом «эти триггеры синхронны» (см. ADR-029's
  соответствующий раздел для полного обоснования) — перенесён как открытый вопрос на будущий проход.
