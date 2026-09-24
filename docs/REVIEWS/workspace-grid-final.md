# Workspace Grid — P09: финальная сверка и релизный gate

**Этап:** P09 пакета [`docs/fix-prompts/`](../fix-prompts/README.md) (`09-acceptance.md`) · **Ветка:** `bugfix/workspace-grid`
(от `rc/0.4.0` @ `dde28df`, схема v4) · **Дата:** 2026-09-22

| | |
|---|---|
| **HEAD gate-прогона** | `261058b` (close-out P00–P08) — все проверки §8 выполнены на нём |
| **HEAD подтверждающего прогона** | `5241dbf` (merge фикса тест-хоста / WGD-02 + WGD-26, +9 файлов, +704/−31) — build + 2× `dotnet test` повторены |
| **Diff от базы** | `git diff dde28df..261058b --stat`: **211 файлов, +33 716 / −411**; `5241dbf` добавляет 9 файлов |
| **Build (Release)** | `dotnet build DesktopOrganizer.sln -c Release` — **0 ошибок**, 723 предупреждения на обоих HEAD (StyleCop: SA1512 652, SA1202 266, SA1025 172 …; `CS1574` — 0, XML-doc анализ отключён, SA0001 ×14) |
| **Тесты** | P00 baseline **1925** → `261058b` **3117** → `5241dbf` **3125** (+1200 за пакет, ни один старый тест не удалён — см. планы этапов) |
| **Стабильность** | `261058b`: **3 прогона подряд 3117 / 0 / 0**, 16–17 с, **0 abort**; `5241dbf`: **2 прогона 3125 / 0 / 0**, 16 с. Фильтр `Migration\|Golden\|Baseline`: **100 / 100** (355 мс) |
| **Среда** | Windows 11 10.0.26200, один монитор 1920×1080 @ 96 DPI. **Приложение не запускалось** (встраивается в WorkerW пользователя). Manual-матрица: **0 / 87 пунктов выполнено** — отрицательный origin (WG-MV-1), второй монитор, DPI ≠ 100 % физически недостижимы на этой машине |
| **Release decision** | **GO with manual-verify gate** (повторный gate на `6a87a2c`, §9): все P0/P1 закрыты и смержены; релиз блокируется только невыполненной ручной матрицей (§6, топ-10 обязательны на Win11) |

Дополнительно к статическому анализу выполнен один **воспроизводимый WPF-эксперимент вне репозитория**
(throwaway-проект в scratchpad, §8.4): он **подтвердил** WGD-01 (угловые resize-грипы на дефолтном пути
`NativePanelSizing=False` фактически 6×6 DIP, а не заявленные 8×8) и **подтвердил рецепт фикса**.

---

## §1. Сравнительная матрица «до / после»

Фокус — desktop usability. «До» = `rc/0.4.0` @ `dde28df` по данным P00 (`docs/PLANS/workspace-grid-00-baseline.md`,
находки F-1…F-12). Статусы: `closed` — доказано кодом/тестом и не требует экрана; `closed-pending-manual` —
код/тесты зелёные, поведение на живом экране не подтверждено (ссылка на MV-ID); `debt` — открыто/принято
(ссылка на `WGD-NN`, `docs/REVIEWS/workspace-grid-debt.md`); `in progress` — чинится параллельно.

| # | Область | Baseline (P00) | Что сделано (файл / ADR / тест) | Как проверить вручную | Статус |
|---|---|---|---|---|---|
| 1.1 | **Origin сетки / snap позиции** | F-1: `GridSnap.SnapAxis` квантует абсолютные `Left/Top` к глобальному (0,0); `GridSize=1` (snap фактически выключен) | ADR-028 «Origin = work area монитора». `WorkspaceLayoutPolicy.SnapPosition` (origin + n·U), `PanelDragPipeline.ResolveGridFrame` (Models/PanelDragPipeline.cs:58) — квантование к `state.Origin`; `PanelGridStateResolver` резолвит origin один раз на жест (PanelDragMoveBehavior.cs:312). Тесты: `WorkspaceLayoutPolicyTests` (idempotent ×DPI 100/125/150/200, negative origin), `PanelDragPipelineTests.ResolveGridFrame_QuantisesToTheMonitorWorkAreaOrigin_NotGlobalZero` | WG-MV-1 (отрицательный origin — **блокирующий gate ADR-028 Вопрос 2**), WG-MV-2 (докованный taskbar) | closed-pending-manual; `DesktopCoordinateSpace.Offset=(0,0)` без калибровки (OQ-1=A) — до WG-MV-1 ADR-028 остаётся `Proposed` |
| 1.2 | **Глобальные размеры / spans; IconSize не меняет bounds** | F-2: ширина зависит от `IconSize` в 3 местах, переснап при каждом старте; F-4: высота не связана ни с чем (разброс 2.93× при одинаковом контенте) | `GridSpan`, `WorkspaceGridMetrics` (U=8, G=8, `MinWidthSpanExpanded=16`=120 DIP, `CollapsedHeaderSpan=5`=32 DIP, presets S/M/L/XL — Models/WorkspaceGridMetrics.cs:24-118). Resize: `ResizeGestureSession.TryUpdate` → `PanelSizingGridSupport.QuantiseResize` (кадр, Alt подавляет), `PersistSpans` — `RoundBoundsToSpans` (nearest) на commit; `SnapWidthToGrid` для `GridAligned` — no-op (F-2). Тесты: `ResizeGestureSessionGridAlignedTests` (48), `NativePanelResizeControllerGridAlignedTests` (54), `PanelSizingGridSupportCommitTests` (17), `ChildWindowSizingGridTests` (STA, живое окно), `ResizeGeometryCalculatorApplyEquivalenceTests` (golden legacy↔native не переписан) | WG-MV-7 (30 циклов ×8 сторон, оба пути), WG-MV-8/23 (IconSize 16→96 — bounds не дрогнули), WG-MV-19/20 (presets), WG-MV-26 (DPI 125/150/175 без «раздувания») | closed-pending-manual. Для `LegacyFreeform` F-2 сохранён **сознательно** (ADR-028 «LegacyFreeform — существующее поведение») |
| 1.3 | **Равные gutters G = 8 DIP** | ADR-017 backlog «resize anti-overlap» открыт; zero-gap стена | `PanelCollision.ClampAgainstObstacles(…, gap)` с по-осевой инфляцией `InflateX/InflateY` (Models/PanelCollision.cs:228-234, carry-over P02→P03), `PanelDragPipeline.ClampHardWall`, `WorkspaceLayoutPolicy.ClampResizeToSlots` (:762). Тесты: `ClampHardWall_StopsExactlyOneGapShortOfTheNeighbour`, `…TwoNeighbours…LeaveTheSameGap`, `PanelCollisionTests` 35-37, `AdjacentSlots_LeaveExactlyGapBetweenPanels` | WG-MV-6 (resize к соседу — останов у слота, без дрожания), WG-MV-15 (скольжение вдоль соседа не «залипает»), WG-MV-20 | closed-pending-manual; **debt WGD-23** (P2, продуктовый): `Gap=0` (панели вплотную, как в Fences) структурно невозможен и не настраивается |
| 1.4 | **Alignment к origin монитора: межмониторный drag, lasso, restore** | F-6: `MonitorDeviceId` не обновляется при drag → панель «телепортирует» назад на `WM_SETTINGCHANGE`/рестарте; F-7: lasso пишет px как DIP (×1.5 на 150 %) | `PanelDragPipeline.ResolveDropMonitor`/`RetargetToDropMonitor` (C-1 fix-cycle P03), `LassoPanelGeometry.ToDip` (App.xaml.cs lasso-блок), `WindowManager.ResolveGridAlignedBounds` + no-op gate `OnMonitorsChanged` (M-3), `PlaceNewPanelOnFreeSlot`. Тесты: `RetargetToDropMonitor_ComposedWithResolveGridDrop_KeepsThePanelOnTheMonitorItWasDroppedOn`, `LassoPanelGeometryTests` (14, кейс `scale=1.5,(300,300,600,600)→(200,200)+(400,400)`), `WindowManagerGridPlacementTests` (idempotent, work-area shift by whole units → no move) | WG-MV-3 (drag DISPLAY1→DISPLAY2, тема, рестарт), WG-MV-10 (lasso 125/150 %), WG-MV-12 (рестарт Explorer/WorkerW), WG-MV-17 (5× Add panel — рядом, не стопкой), WG-MV-24 (двойной `WM_SETTINGCHANGE` — 0 записей) | closed-pending-manual; WGD-08 (центро-эвристика, accepted), WGD-09 (C-1 живьём — MV-3) |
| 1.5 | **Плотность: empty vs fit** | F-4: высота отвязана от контента; пустая панель/1 иконка — произвольный высокий прямоугольник | ADR-029 Решение 7: `PanelContentSizer` (Models/PanelContentSizer.cs:45) — `ResolveDisplayRowCount(0 items)=1` (минимальная drop-zone), `FitHeight`/`RoundHeightUnitsForGridAligned` → `SpansToBounds`; `ChildWindow.ContentSizing.cs` — «Fit panel to content», `AutoContent` (debounce ~300 мс, не тикает посреди жеста — MAJOR-3), `Manual` не трогается, завершённый вертикальный resize демотирует `AutoContent→Manual` (MAJOR-4), presets ставят `Manual` (UX P0). Только Grid-режим (Q3=B). Тесты: `PanelContentSizerTests` (29), `ChildWindowContentSizingTests`, `MainWindowViewModelTests` (apply-to-all, N панелей, `IsLayoutLocked`) | IL-MV-4/5/6 (0/1/2/8+ иконок; Manual не меняется), IL-MV-16…19 (live пересчёт, debounce, page switch, roll-up), IL-MV-24/25/28/29 (fix-cycle P06), WG-MV-28 (preset снимает AutoContent) | closed-pending-manual; debt WGD-13/14 (doc/clamp `widthUnits`), WGD-15 (`_isAnimating`), WGD-16 (первый показ на неактивной странице), WGD-17 (save после демоции), WGD-19 (`ShowOnDisabled`) — все P2/P3 |
| 1.6 | **Icon grid: `FixedGap` vs `LegacyStretch`; F-3 «потеря колонки»** | F-5: stretch-gap полного ряда (ADR-018) — шаг между иконками зависит от ширины панели, orphan-row 9+1; F-3: `FileListHPadding=0` при колонке хрома 8 DIP → снап на k колонок рендерится как k−1 | ADR-029 Решения 1–4: `IconLayoutPolicy {LegacyStretch=0, FixedGap=1}` per-panel; `AdaptiveGridLayout.RowGap(…, policy)` (Models/AdaptiveGridLayout.cs:234 — `FixedGap` = ветка неполного ряда безусловно, `LegacyStretch` байт-в-байт старый путь); `GridMetrics.FileListHPadding = PanelMetrics.ContentLeftInset` (=8, GridMetrics.cs:84 — F-3 для ОБЕИХ политик, Q2=A); `AdaptiveIconGridPanel.CapacityWidth` (Controls/AdaptiveIconGridPanel.cs:170 — резерв скроллбара вычитается один раз, CRITICAL-1 P05). Тесты: `AdaptiveGridLayoutIconLayoutPolicyTests`, `AdaptiveIconGridPanelTests` (координаты 1/3/7 иконок × ширины), `ContentLeftInsetRowCapacityRegressionTests`, `AdaptiveGridLayoutBaselineTests` (legacy запинен, P00), `ListReorderGeometryHitTestTests` | IL-MV-1 (шаг 1-й/2-й иконки одинаков на 3 ширинах), IL-MV-2/3 (F-3 колонка появилась; legacy визуально прежний), IL-MV-9/10 (порог скроллбара, live resize без отставания на кадр), IL-MV-13 (переключение политики — переснап ≤1 шаг), IL-MV-14 (drop в левый гуттер) | closed-pending-manual. **Release note F-3** (см. §3.5): легаси-панель получит потерянную колонку только при первом ПОЛЬЗОВАТЕЛЬСКОМ переснапе |
| 1.7 | **Визуальная иерархия: радиус, hover-wash, a11y, reduced motion, resize-грипы** | Радиус 6, `Brush.Overlay.Hover` статический (осветляет на светлых обоях → контраст падает), нет `AutomationProperties.Name`, roll-up всегда анимирует; resize-грип 6 DIP везде | `Radius.Panel` 6→8 (Resources/Styles/Spacing.xaml:46) + `RoundedRectClipConverter` на `PART_Chrome.Clip` (Panels.xaml:90-96); `HoverOverlayPolicy`/`HoverOverlayBrushConverter` (luminance-aware, HC passthrough); `AutomationProperties.Name` на окне и 4 кнопках (ChildWindow.xaml:25, 419-443), `LabeledBy`/`HelpText` на Surface style; `MotionState.AnimationsEnabled` (SystemParameters.ClientAreaAnimation) — roll-up без анимации при reduced motion; `PanelMetrics.ResizeCornerThickness=8`/`ResizeEdgeThickness=6` (PanelMetrics.cs:63/75), `NativeHitTestMapper` (edge/corner, угол первым). Тесты: `RoundedRectClipConverterTests`, `HoverOverlayBrushConverterTests`, `HoverOverlayPolicyTests`, `ContrastCalculatorTests`, `AmbientAccentContrastAuditTests`, `MotionStateTests`, `NativeHitTestMapperTests`, `PanelMetricsTests` | VIS-MV-1/2 (радиус, клип на 100/125/150 %), **VIS-MV-3** (угловой grip), VIS-MV-4/5 (остаточный overlap), VIS-MV-6/7 (Narrator), VIS-MV-8/9 (анимация вкл/выкл), VIS-MV-11 (hover читается чётче), VIS-MV-13 (HC), VIS-MV-15 (device-pixel snap на дробном DPI) | **debt / in progress — WGD-01 (P1, ПОДТВЕРЖДЁН экспериментом §8.4):** на дефолтном WPF-пути угловые `Thumb` layout-клипятся до 6×6, «click-through lens» MAJOR-1 не закрыт, а с ростом радиуса 6→8 доля мёртвой зоны в реальном 6×6 грипе выросла с ~21 % до ~38 % — регресс относительно базы. Остальное — closed-pending-manual; WGD-03 (остаточный overlap 14 %/35 %) accepted; WGD-25/DO-021 (пол `MinSurfaceOpacity=0.35` ≈1.7:1 на светлых обоях) open, продуктовое решение |
| 1.8 | **Тексты: ellipsis Grid/List, tooltips** | List: длинное имя вылезало за правую границу (горизонтальный скролл); Grid: 2 строки без гарантии обрезки | List: `TextTrimming="CharacterEllipsis"` (Resources/Templates.xaml:222) + структурная Grid-колонка + `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` на `ListBox` (:186, MAJOR-B); Grid: `TextWrapping="Wrap"` + `CharacterEllipsis` (:331-333); полный путь в `ToolTip="{Binding Path}"` (:211/:307). Тест: `ResourceDictionaryMergeTests.ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow` (реальный `ListBox`, 1576→150 DIP) | VIS-MV-12 (узкая List-панель: многоточие, без h-скролла, tooltip) | closed-pending-manual |
| 1.9 | **Render reliability: placeholder, stale paths, retry, invalidation** | F-9: 20/26 путей реального state — устаревшие pre-Move; placeholder для отсутствующего пути **полностью прозрачен** (0/256 opaque px, `using var` DrawingContext); F-10: ошибки в `Console.Error` (теряются), иконка не перечитывается при появлении файла | `IconFactory.CreatePlaceHolder` — блочный `using`, 32×32 непрозрачный; `IconProvider.GetIcon` (Providers/IconProvider.cs:97-134) — generic OS-иконка по расширению для отсутствующего пути, missing-результат **не кешируется**; `IPanelItemStorageService.TryFindManagedPath` + `WindowManager.ResolveStalePanelPaths` (в т.ч. `LayoutSnapshotService.Restore`, contested → отказ обоим); `BaseViewModel.RefreshItemsLiveness` (3-с poll, missing→present re-fetch) + bounded retry `MaxPlaceholderRetryAttempts=3` (BaseViewModel.cs:447); `IconDiagnostics` — 4 события категории `icon`, privacy-safe payload. Тесты: `IconFactoryTests`, `IconProviderTests`, `IconHandlerTests`, `PanelItemStorageServiceTests`, `ChildWindowViewModelTests`, `IconExtractionIntegrationTests` (8, **реальный shell**: `.exe`/папка/`.lnk`/битый `.lnk`/`.url`/missing/кеш) | ICO-MV-1 (грязный state — реальные иконки сразу), ICO-MV-2 (серый placeholder правильного размера), ICO-MV-3 (самоисцеление state), ICO-MV-4 (удалить/вернуть файл ≤3 с), ICO-MV-5 (200+), ICO-MV-6 (Win10 `SHGFI_USEFILEATTRIBUTES`), ICO-MV-7/8 | closed-pending-manual; **debt WGD-21** (accepted): синхронная загрузка ≈2.8 мс/элемент на UI-потоке (200 → ≈0.56 с), требует отдельного ADR; WGD-22 (1/20 путей без manifest — отказ по конструкции) |
| 1.10 | **Persistence / первый запуск** | Схема v4; нет полей режимов; переснап ширины при каждом старте (F-2) | v4→v5 (`V4ToV5Migration`, аддитивно, идемпотентно), premigration-бэкап, `allowIntegerValues:false`, init re-snap удалён — детали §3 | MIG-MV-1/2/3, IL-MV-7, WG-MV-29 | closed-pending-manual (см. §3) |
| 1.11 | **НОВОЕ (вне F-списка): крах процесса при выходе** | (не в P00) `ChildWindowViewModel.RefreshExistence` (3-с таймер) и `OnFolderChanged` делали `Dispatcher.Invoke` на остановленный диспетчер → `TaskCanceledException` на thread-pool-потоке → убийство процесса (то же при выходе с открытым Folder Portal); проявлялось как intermittent «Test Run Aborted» | `5241dbf`: `UiDispatch.TryInvoke` (Services/UiDispatch.cs — probe `HasShutdownStarted/Finished` + `catch OperationCanceledException`), `ChildWindowViewModel.cs:693/740`, `App.xaml.cs` (activation pipe); тест-харнес `StaTestRunner` (владелец `Application.Current` пампит), `WpfApplicationHost`; тесты `UiDispatchTests`, `StaTestRunnerTests`; док `docs/Refactoring/Testing/sta-test-conventions.md` (4 abort / 22 прогона → 0 / 24; по данным оркестратора 30 / 30 без abort) | **MV-EXTRA-4 (новый):** открыть панель-портал (Folder Portal), дождаться тика liveness (≤3 с) и закрыть приложение / выйти из сеанса — чистый выход, без WER-диалога и записи в Event Log; повторить 10×; **плюс (после ревью `UiDispatch`):** закрывать Folder Portal-панель ровно в момент срабатывания debounce (менять файлы в папке и тут же закрывать панель, 20×) — нет зависания; запуск второй инстанции во время выхода — не блокирует и не задерживает выход | **in progress** — WGD-26 закрыт на `5241dbf`, но ревью нашло CRITICAL **WGD-32** (взаимоблокировка `FolderWatcher._debounceLock` ↔ UI-поток) и MAJOR **WGD-33** (блокирующие `Invoke` из фоновых потоков); оба фикса в работе параллельно |

---

## §2. Сверка DoD этапов 00–08

Источник формулировок — `docs/fix-prompts/0x-*.md`, раздел «Definition of Done». Статусы: `✓` — доказано;
`✓/manual` — доказано тестом, живого подтверждения нет; `частично` — есть незакрытая часть; `manual` — только
ручная проверка; `✗` — не выполнено.

### P00 — Аудит (`00-audit.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| Каждый writer геометрии и icon-loading путь в карте | `workspace-grid-00-baseline.md` части A (21 writer) и C (pipeline map) | ✓ |
| Измерения отделены от визуальных предположений | маркировка ИЗМЕРЕНО/ВЫЧИСЛЕНО/ВИЗУАЛЬНОЕ во всех частях | ✓ |
| Реализация не менялась | коммит `22faada`: только `AdaptiveGridLayoutBaselineTests` (4) | ✓ |
| ≥3 репродукции непропорциональных панелей | часть B, репро 1–4 (+F-3) | ✓ |
| Baseline-скриншоты 100 %/150 % | процедура в части D, **не выполнена** | manual |

### P01 — Архитектура (`01-architecture.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| Независимые системы координат, DIP/px, origin | ADR-028 «Три независимые системы координат», «px↔DIP», «Origin = work area» | ✓ |
| Оба режима (`GridAligned`/`LegacyFreeform`), manual override | ADR-028 «Режимы геометрии», `Alt`, «Align to workspace grid» (checkable, OQ-3=B) | ✓ |
| Backward compatibility, сохранение snapshots, alternatives | `workspace-grid-migration.md` §1–§6, ADR-028 «Alternatives considered» | ✓ |
| Конфликт ADR-018 устранён явно | ADR-029 Решение 1 + amendment ADR-018 (`adr/README.md:30`), UX-компромиссы ADR-029 «Последствия» | ✓ |
| Согласовано с ADR-021/022/026 | amendments ADR-022 ×2, ADR-026, ADR-004/011/015/017/024 (`adr/README.md`) | ✓ |

### P02 — Чистая геометрия (`02-metrics.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| `Snap(Snap(x))==Snap(x)` (с допуском) | `SnapCoordinate_RepeatedApplication_IsIdempotent*`, `SpansToBounds_BoundsToSpans_RoundTrip_IsIdempotent_AcrossDpiScales`; ratchet-фикс `Math.Ceiling` с допуском 0.5 DIP (MAJOR-1) | ✓ |
| Несколько окон одного span — одинаковая геометрия | `SnapBounds_SeveralPanelsSameSpan_ProduceIdenticalGeometryOnSameWorkspace` | ✓ |
| IconSize не входит в зависимости window sizing | план §3: ни один файл этапа не ссылается на `GridMetrics`/`AdaptiveGridLayout` (кроме `PanelMetrics.HeaderHeight`); `ResizeConstraintsFactoryGridAlignedTests` (P04) — constraints не читают `IconSize` | ✓ |
| Все geometry-тесты зелёные, старые не удалены | `WorkspaceLayoutPolicyTests` (1176 строк), `WorkspaceGridMetricsTests`, `WorkspaceGridOptionsTests`; счётчик тестов монотонно рос 1925→3125 | ✓ |

### P02b — Persistence v4→v5 (`workspace-grid-migration.md` §10, вне нумерации промптов)

| Пункт | Доказательство | Статус |
|---|---|---|
| Миграция аддитивна, идемпотентна, дефолты = сегодняшнее поведение | `V4ToV5Migration.cs:194-223` (`ContainsKey`-guards), `V4ToV5MigrationTests` (12), `SchemaContractTests` (дефолты = DTO-дефолты) | ✓ |
| Golden-цепочка v1→…→v5 | `GoldenMigrationTests` (6), `expected-current-from-v4.json`, `v5-full.json` | ✓ |
| Устойчивость `Load()` (null-коллекции, null-элементы, неверный JSON-тип, out-of-range enum) | `ApplicationStateServiceTests` (N-3, 10.9, N-7 Theory ×4) | ✓ |
| Premigration-бэкап атомарный, под advisory lock | `JsonApplicationStateStoreTests.Load_PrimaryAtOlderSchema_*` (2) | ✓ |
| Реальный апгрейд/откат файла | — | manual (MIG-MV-1/2/3) |

### P03 — Drag / snap / collision / мониторы (`03-positioning.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| Независимо созданные панели одного span совпадают по краям (D3-1) | `WorkspaceGridPlacementTests.TryAlign_*`, `ResolveGridFrame_QuantisesToTheMonitorWorkAreaOrigin_NotGlobalZero` | ✓ |
| Соседние панели — равные gaps (D3-2) | `ClampHardWall_*` (3), `FindFreeSlot_OriginTaken_ReturnsTheNextSlotWithAtLeastTheGap` | ✓/manual (WG-MV-6/15) |
| Restore не переносит панель на другой монитор (D3-3) | `WindowManagerGridPlacementTests.ResolveGridAlignedBounds_*` + F-6 | ✓/manual (WG-MV-3) |
| Нет jump на first launch (D3-4) | `…_PanelAlreadyOnTheGrid_ReturnsExactlyItsSavedBounds`, `…_RepeatedResolution_IsIdempotent`, `ChildWindowInitializeTests` (344≠352), golden без правок | ✓/manual (MIG-MV-1, IL-MV-7) |
| Позиция стабильна после drag-release и рестарта (D3-5) | `ResolveGridDrop_IsIdempotent`, `…_AfterADropAndPersist_RestoresTheSameBounds` | ✓ |
| Клавиатурный/доступный fallback не деградирует (D3-6) | `LayoutGesturePolicy`/`PreviewKeyDown` не тронуты; `MainWindowViewModelTests.AlignAllPanels_LayoutLocked_*` | ✓ |
| F-6 `MonitorDeviceId` при межмониторном drag (D3-7) | переоткрыт C-1 и закрыт `RetargetToDropMonitor_ComposedWithResolveGridDrop_*` | ✓/manual (WG-MV-3, WGD-09) |
| Лассо px→DIP (D3-8) | `LassoPanelGeometryTests` (14) | ✓/manual (WG-MV-10) |
| Alt — только кадр, не drop (D3-9) | `ResolveGridFrame_FreeMove_*`, `ResolveGridDrop_QuantisesEvenWhenTheGestureEndedOffGrid` | ✓/manual (WG-MV-4) |
| Anti-overlap не пробивается (D3-10) | `ResolveGridFrame_RandomTrajectories_NeverCreateANewOverlap` (1000 кадров × 4) | ✓ |
| Реальный чек-лист multi-monitor (выходной артефакт) | план §7 → сводная матрица A (29 пунктов) | manual |

### P04 — Spans / native resize / roll-up (`04-resize.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| Нет зависшего resize в 30 циклах (D4-1) | стресс-тесты 30 циклов на `ResizeGestureSession` и реальном `NativePanelResizeController` (с `CaptureLost`/`SourceChanged`) | ✓/manual (WG-MV-7) |
| IconSize не меняет panel bounds (D4-2) | `ResizeConstraintsFactoryGridAlignedTests`, STA `ChildWindowSizingGridTests` | ✓/manual (WG-MV-8/23) |
| Каждая сторона/угол сохраняет противоположный край (D4-3) | 8 сторон × {на сетке, вне} × {сессия, native}; MAJOR-1 fix-cycle: clamp движущегося края к work area, якорь неподвижен (`WorkspaceLayoutPolicyResizeClampTests`, 28) | ✓/manual (WG-MV-25) |
| Collapsed header не равен полноценной ячейке (D4-4) | `CollapsedHeaderSpan=5` → 32 DIP = `HeaderHeight` (`WorkspaceGridMetricsTests`, `RollUpBehaviorGridAlignedTests`) | ✓/manual (WG-MV-9/16) |
| После рестарта bounds/span стабильны, native fallback работает (D4-5) | round-trip bounds→span→bounds через px 100/125/150/175 % (`PanelSizingGridSupportCommitTests`, MAJOR-2: span 40 = 312 DIP ±1 px); restore — P03-W1; оба пути (`Thumb`/native) | ✓/manual (WG-MV-22/26/27) |
| Golden-эквивалентность legacy↔native сохранена | `ResizeGeometryCalculatorApplyEquivalenceTests` дополнен, не переписан | ✓ |
| Ручная матрица resize (артефакт) | план §7 → матрица A | manual |

### P05 — Fixed-gap icon grid (`05-icon-layout.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| При одинаковом preset горизонтальный шаг одинаков в панелях разной ширины | `AdaptiveGridLayoutIconLayoutPolicyTests`, `AdaptiveIconGridPanelTests` (позиции 1-й/2-й иконки на разных ширинах) | ✓/manual (IL-MV-1) |
| Full/partial rows согласованы; последний ряд не растянут; surplus справа | `RowGap(…, FixedGap)` = ветка неполного ряда всегда (AdaptiveGridLayout.cs:234-237) | ✓ |
| Ни одна иконка/подпись не пропадает | Решение 4 «scroll, не clip»; `CapacityWidth` (M-2/CRITICAL-1) — колонка не прыгает на пороге скроллбара | ✓/manual (IL-MV-9) |
| Drag/rename/selection/scroll не регрессируют | `ListReorderGeometryHitTestTests` (nearest-row fallback, drop в surplus → конец ряда); rename/selection — код не тронут | ✓/manual (IL-MV-8/14) |
| Legacy layout воспроизводим под явным флагом | `IconLayoutPolicy.LegacyStretch` делегирует в 6-аргументный `RowGap` без изменений; `AdaptiveGridLayoutBaselineTests` (P00) зелёный | ✓ |
| Нет init re-snap (fix-cycle MAJOR-1) | `ChildWindow.Initialize` без `SnapWidthToGrid` (ChildWindow.xaml.cs:310-343), `ChildWindowInitializeTests` | ✓/manual (IL-MV-7) |

### P06 — Fit-to-content (`06-fit-content.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| AutoContent подбирает 0/1/2 ряда, не отрезает подписи | `PanelContentSizerTests` (29): `ResolveDisplayRowCount(0)=1`, `FitHeight` для 1/2/N рядов, `RoundHeightUnitsForGridAligned` (Ceiling) | ✓/manual (IL-MV-4/5) |
| Manual сохраняет высоту при изменении числа иконок | `ChildWindowContentSizingTests` (Manual — no-op на `CollectionChanged`) | ✓/manual (IL-MV-6) |
| Legacy не меняется без opt-in | миграция пишет `sizingMode=Manual` явно; m-1 guard первого `IsVisibleChanged` | ✓/manual (IL-MV-28) |
| Импорт/экспорт и snapshots после двух циклов restore одинаковы | `PanelStateMapperTests` (round-trip всех полей v5), `LayoutSnapshotServiceTests`, `Migrate_IsIdempotent`, `…_RepeatedResolution_IsIdempotent` — прямого теста «два цикла restore подряд» нет, идемпотентность доказана покомпонентно | частично (косвенно ✓) → **manual MIG-MV-3** |
| UI action + `PanelSizingMode`, документация «не трогать ручные размеры» | меню «Panel sizing ▸» / «Icon layout ▸», план §3 «Правила изменения», §8 release note | ✓ |
| Debounce/roll-up/DPI живьём | не крутится `Dispatcher` в STA-harness | manual (IL-MV-17/19/24, IL-MV-11) |

### P07 — Ambient Accent visual (`07-visual.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| Панели визуально из одного семейства | токены §8 плана (радиус, clip, hover-wash) | manual (VIS-MV-1/2/11) |
| Текст и кнопки читаемы поверх двух контрастных обоев | `ContrastCalculator` + `AmbientAccentContrastAuditTests` (таблицы 1–6 плана §4, вычислено); hover-wash направленный (MAJOR-3) | ✓/manual (VIS-MV-10/11/13); **пол `MinSurfaceOpacity=0.35` ≈1.7:1 — DO-021/WGD-25 open** |
| Заголовки и подписи не режутся | ellipsis Grid/List + реальный `ListBox`-тест | ✓/manual (VIS-MV-12) |
| Collapsed panel понятна | `RollUpBehavior` + `MotionState`; resize-хендлы отключены при roll-up (`Size` disabled) | manual (VIS-MV-14) |
| **Нет overlapping hot zones** | остаточный overlap header-кнопка 14 % / scrollbar 35 % — **accepted** (WGD-03); **угловой grip 8×8 на WPF-пути НЕ реализован** (WGD-01, подтверждено §8.4) | **частично / ✗ до мержа WGD-01** |
| Внешний вид не нарушает performance/WorkerW | без Blur/DropShadow (ADR-025 R-8), только `Clip`-геометрия | ✓/manual (VIS-MV-1) |
| Review-скриншоты 100/150 %, светлые/тёмные обои (артефакт) | инструкция в плане §9 | manual |

### P08 — Icon pipeline (`08-icons.md`)

| Пункт DoD | Доказательство | Статус |
|---|---|---|
| Валидные `.exe`/`.lnk`/folder → реальная иконка или проверенный OS fallback | `IconExtractionIntegrationTests` (8, реальный shell, **только Win11 26200**) | ✓/manual (ICO-MV-6 — Win10) |
| Отсутствующий target не ломает layout | фиксированный слот (Templates.xaml, «ui-polishing-1 §3.6»), placeholder 32×32 непрозрачный | ✓/manual (ICO-MV-2/7) |
| Нет silent exception | `IconDiagnostics` 4 события + `panel.ExistenceRefreshFailed`; `Console.Error` удалён | ✓ |
| Single-item panel не теряет icon из-за opacity/virtualization | слот фиксирован; `Opacity=0.45` только на подписи при `Exists=false` | ✓/manual (ICO-MV-7) |
| **UI отзывчив при 200+ items** | измерено 557 мс / 200 некешированных `.exe` синхронно на UI-потоке; **не устранено, задокументировано** (WGD-21, требует ADR) | **✗ (accepted debt)** → manual ICO-MV-5 |
| Диагностический документ с evidence | `docs/DIAGNOSTICS/icon-loading-findings.md` (root cause 2 фактора, pipeline, fix-cycles 1–2) | ✓ |

**Итог по DoD:** все проверяемые кодом пункты P00–P08 закрыты тестами; **не закрыты**: P07 «нет overlapping
hot zones» (WGD-01 — P1, in progress) и P08 «UI отзывчив при 200+» (WGD-21 — accepted debt с ADR-кандидатом);
все «живые» пункты — manual (§6).

---

## §3. Backward compatibility и первый запуск

### 3.1 Миграция v4→v5

- `StateEnvelope.CurrentSchemaVersion = 5` (Services/Storage/StateEnvelope.cs:27); на базе `dde28df` — `4` (проверено `git show`).
- `V4ToV5Migration` (`FromVersion=4`, `ToVersion=5`): копирует `geometry`/`appearance` через `DeepClone`, дописывает
  `geometryMode="LegacyFreeform"` и `sizingMode="Manual"` в `geometry`, `iconLayoutPolicy="LegacyStretch"` в `appearance`
  **только при отсутствии ключа** (V4ToV5Migration.cs:194-223) → **идемпотентна по построению** (тест `Migrate_IsIdempotent`);
  `widthSpanUnits`/`heightSpanUnits` не пишутся (нет ни одной `GridAligned`-панели после миграции); `iconSize`
  (в т.ч. дробный `32.0101…`, F-11) проходит байт-в-байт; нераспознанные строки enum сохраняются verbatim, безопасный
  fallback — в `PanelStateMapper.FromDto` (строгие парсеры `WorkspaceGridEnumParsersTests`: числа/составные строки отвергаются).
- Golden: `expected-current-from-v4.json`, `v5-full.json`; `SchemaContractTests` сверяет значения миграции с `new PanelGeometryDto()`/`new PanelAppearanceDto()`.

### 3.2 Premigration-бэкап и rollback-процедура

- `JsonApplicationStateStore.WritePreMigrationBackupIfMissing` (:536): guard `!_storageLock.IsHeld → return` (:545),
  запись во временный файл `StateStorePaths.NewTempFilePath()` + `File.Move(temp, target, overwrite:false)` (:554-559),
  вызывается на рангах 1/2/3 лестницы (:265/:278/:300), не на rung 5 (legacy import). Путь:
  `%LOCALAPPDATA%\DesktopOrganizer\State\application-state.premigration-v4.json` (`StateStorePaths.PreMigrationBackupPath`, :139).
  Осиротевший temp подчищается `SweepOrphanedTempFiles()` (:241) на следующем `Load()`.
- **Старая сборка на v5-файле (rung 7):** `primarySchemaVersion > CurrentSchemaVersion` → `StateLoadStatus.NewerSchema`
  (:252-254), `_newerSchemaLockout=true` (:115), `IsWritable => _storageLock.IsHeld && !_newerSchemaLockout` (:104),
  `Save()` отказывает без похода на диск (:141). Итог: старая сборка стартует с пустым состоянием, **файл на диске
  остаётся v5 и не портится**; при возврате на новую сборку всё восстанавливается.
- **Ручное восстановление pre-migration состояния под старой сборкой:** закрыть приложение → в `State\` сохранить копию
  текущего `application-state.json` (v5) → скопировать `application-state.premigration-v4.json` поверх
  `application-state.json` → запустить старую сборку (`schemaVersion: 4` читается штатно). Примечание: `.bak` после
  первого `Save()` новой сборки уже v5, поэтому `--restore-state-backup` (ADR-024 C7) для отката на старую сборку
  **не подходит** — только premigration-файл.

### 3.3 «Нет jump on first launch»

| Механизм | Где | Доказательство |
|---|---|---|
| Init re-snap ширины удалён для всех режимов | `ChildWindow.Initialize` (ChildWindow.xaml.cs:310-343, комментарий :326) | `ChildWindowInitializeTests` (панель 344 DIP при `IconSize=48` открывается на 344, не 352) |
| Мигрированные панели — `LegacyFreeform`/`Manual`/`LegacyStretch` явно | миграция §3.1 | `V4ToV5MigrationTests`, `SchemaContractTests` |
| `AutoContent` не пересчитывает на первом `Show()` | `ChildWindow.ContentSizing.cs:303-321` (`_contentSizingShownOnce`) | `ChildWindowContentSizingTests` (m-1) |
| `OnMonitorsChanged` не пишет без реального изменения | `WindowManager` (M-3) | `WindowManagerGridPlacementTests` (дважды подряд → 0 записей) |
| `GridAligned` restore идемпотентен, панель на сетке → ровно сохранённые bounds | `WindowManager.ResolveGridAlignedBounds` | `…_PanelAlreadyOnTheGrid_ReturnsExactlyItsSavedBounds`, `…_RepeatedResolution_IsIdempotent` |
| Ленивая миграция spans (панель < минимума) — один раз | `PanelGridStateResolver.Resolve` + `ClampToMinimum` (MAJOR-3 P04) | `PanelGridStateResolverMigrationTests` (5) |

Новые ключи `Settings.settings`: `DefaultPanelGeometryMode=GridAligned`, `DefaultIconLayoutPolicy=FixedGap`
(Properties/Settings.settings:71-76) — влияют **только на новые** панели; отсутствие ключей в старом `user.config`
даёт эти дефолты (решение оркестратора, ADR-028 Вопрос 3=B). `NativePanelSizing=False` (дефолт не менялся).
Живая проверка: **MIG-MV-1, IL-MV-7, WG-MV-29**.

### 3.4 Риск `allowIntegerValues:false` (StateJson.cs:53)

Типизированные enum-поля DTO (`windowState`/`viewMode`/`sortMode`/`surfaceStyle`) теперь отвергают JSON-**число**
так же, как нераспознанную строку → `JsonException` → существующий guard → документ **карантинируется**
(`application-state.corrupt-*.json`), загружается `.bak` или пустое состояние (тест
`Load_TypedEnumFieldAsOutOfRangeNumber_NeverThrows_DegradesToQuarantined`). Оценка: **низкий, принятый риск** —
единственные производители документа (собственный писатель через тот же `StateJson.Options`, `V1ToV2`/`V2ToV3`
конвертируют легаси-числа в имена до десериализации, `tools/perf/New-PerfFixture.ps1` пишет строки) числа не
пишут; сценарий = вручную отредактированный файл. Последствие тяжелее «пустой сессии» (primary переименовывается),
но обратимо (`corrupt-*` файл остаётся, `--restore-state-backup`). Рекомендация: строка в release notes / troubleshooting.

### 3.5 Release note F-3 (где описано)

`docs/PLANS/workspace-grid-06-fit-content.md` §8 п.1; ADR-029 «Открытые вопросы» Q2-уточнение; ADR-028 «Manual verify» п.8.
Формулировка для release notes: *«Панели, существовавшие до обновления, при первом запуске выглядят ровно как раньше.
Колонка иконок, ранее терявшаяся из-за неучтённой полосы акцента (8 DIP), появится при первом изменении размера
панели, смене размера иконок/вида или переключении «Icon layout» — не раньше.»*

### 3.6 Прогон тестов миграции/golden/baseline

`dotnet test … --filter "FullyQualifiedName~Migration|FullyQualifiedName~Golden|FullyQualifiedName~Baseline"` →
**100 / 100 passed** (355 мс), по классам: `V1ToV2MigrationTests` 17, `V4ToV5MigrationTests` 12, `V3ToV4MigrationTests` 12,
`StateMigrationRunnerTests` 11, `JsonApplicationStateStoreTests` 11, `V2ToV3MigrationTests` 10, `GoldenMigrationTests` 6,
`PanelGridStateResolverTests` 6, `SchemaContractTests` 5, `PanelGridStateResolverMigrationTests` 5,
`AdaptiveGridLayoutBaselineTests` 4, `LayoutEditVisualsTests` 1.

### 3.7 Новый закрытый P1 (совместимость поведения при выходе) — WGD-26

`5241dbf`: `ChildWindowViewModel.RefreshExistence`/`OnFolderChanged` через `UiDispatch.TryInvoke`
(ChildWindowViewModel.cs:693/740; Services/UiDispatch.cs) — тик liveness или событие FileSystemWatcher, пришедшие
во время shutdown диспетчера, больше не убивают процесс. До фикса: реальный крах приложения при выходе с открытым
Folder Portal (или на любом 3-с тике во время закрытия). Manual verify — **MV-EXTRA-4** (§6).

**Ревью этого фикса (после `5241dbf`) нашло следующий слой — не закрыто, in progress (WGD-32/33, §5).**
`FolderWatcher.OnDebounceElapsed` (Services/Tools/FolderWatcher.cs:150-165) вызывает `Changed` **под `_debounceLock`** (:27), а подписчик
`ChildWindowViewModel.OnFolderChanged` (:691-693) делает **блокирующий** `UiDispatch.TryInvoke` на UI-поток; если UI-поток в этот момент
закрывает панель (`DetachWatcher` :655 → `FolderWatcher.Stop` :57 → `lock (_debounceLock)` :72) — взаимоблокировка, приложение зависает.
Тот же блокирующий `Invoke` — в `RefreshExistence` (:740; истощение thread-pool, если UI не прокачивается) и в activation-пути
(App.xaml.cs:326; задержка выхода до 2 с и дедлок `ActivationRequestRelay._gate` (:22/:76) ↔ `SingleInstanceCoordinator.AttachSink` (:124)).
Фиксы в работе: (1) `FolderWatcher` вызывает подписчиков **вне** lock + try/catch; (2) `UiDispatch.TryBeginInvoke` (fire-and-forget) для трёх
call-site'ов, суженный `catch`, `WpfApplicationHost` во всех STA-тестах. До их мержа release decision — не GO (§7).

---

## §4. Perf / allocation (статический анализ + инструкция замера)

### 4.1 Hot path drag — `PanelDragMoveBehavior.OnTitleBarMouseMove` (:249-405)

- Мониторы и препятствия снимаются **один раз на жест** при пересечении порога (:295-312), буферы `_dragObstacles`/`_dragMonitors`
  переиспользуются (комментарий :40-59); `PanelGridState` резолвится один раз (:312).
- Кадр `GridAligned` (:330-347): `PanelDragPipeline.ResolveGridFrame` — `SnapPosition` (чистая арифметика),
  `ClampHardWall` (структуры `Rect`, `CreatesNewOverlap` индексными циклами) → **0 heap-аллокаций** вне Edit Layout.
  В Edit Layout — `SnapGuideCalculator.Snap` аллоцирует `new List<SnapGuide>(2)` на кадр (Models/SnapGuideCalculator.cs:131) —
  **пред-существующее** (ADR-026), не введено пакетом.
- `Keyboard.Modifiers` читается на кадр (дёшево); `_diagnostics.DragDelta` на кадр, но `GestureDiagnostics` сэмплирует
  раз в 50 мс (`DefaultDeltaSampleIntervalMs`) — запись в JSONL не покадровая.
- Вердикт: **OK**.

### 4.2 Hot path resize — `ResizeGestureSession.TryUpdate` (:221-243)

- `Accumulate`/`Apply` — чистые; `QuantiseResize` (PanelSizingGridSupport.cs:63) → `SnapBounds`, `ClampResizeToWorkArea` — чистые;
  `ClampResizeToSlots` (WorkspaceLayoutPolicy.cs:762) → `FilterObstaclesResolvableByThisEdge` **аллоцирует `new List<Rect>(n)`
  на каждый кадр**, когда есть ≥1 релевантное препятствие (:813); плюс `ValidateGap` на кадр. Препятствия собираются один раз
  на жест в `TryBegin` (PanelResizeController.cs:179, NativePanelResizeController.cs:356), буферы `_resizeObstacles`/`_commitObstacles`
  переиспользуются.
- Вердикт: **приемлемо** (маленькая gen0-аллокация только для `GridAligned` с соседями); **WGD-30 (P3)** — поднять фильтр в
  `TryBegin`/переиспользовать буфер.
- `DefaultFreeResizeModifier` (:256-266) — широкий `catch (Exception)`, см. WGD-10.

### 4.3 Icon loading

- `BaseViewModel.AddFilesCore` (:302-315) — синхронный `IIconProvider.GetIcon` на элемент; `IconProvider.GetIcon` (:97-134) делает
  `File.Exists` + `Directory.Exists` (+ `LastWriteTimeUtc`) на UI-потоке → **≈2.79 мс/элемент, 200 некешированных = 557 мс**
  (измерено P00 на Win11). **WGD-21 (accepted)** — требует ADR (STA-воркер).
- Liveness-poll: `RefreshExistence` (thread-pool timer, 3 с) → `UiDispatch.TryInvoke` → `RefreshItemsLiveness()` (**N × `File.Exists`/`Directory.Exists`
  на UI-потоке**, ChildWindowViewModel.cs:736-752, FileItem.cs:129-132/195); при missing→present — ещё `GetIcon`. Пред-существующий
  паттерн (до P08 тоже `Dispatcher.Invoke`), P08 добавил только re-fetch. Для сетевых/съёмных путей — потенциальный stall UI.
  **WGD-29 (P3)**: перенести stat-вызовы на пул, маршалить только результат.

### 4.4 `CapacityWidth` binding (Templates.xaml:291 → `AdaptiveIconGridPanel.CapacityWidthProperty`, `AffectsMeasure|AffectsArrange`)

`CapacityWidth = ScrollViewer.ActualWidth` → каждое изменение ширины `ScrollViewer` (т.е. каждый кадр resize по ширине)
инвалидирует measure/arrange панели **ещё раз после** уже прошедшего layout-pass → O(N items) лишний pass на кадр. Сходится за один
дополнительный pass и **не зацикливается**: `ActualWidth` `ScrollViewer` не зависит от desired-ширины контента
(`HorizontalScrollBarVisibility="Disabled"`, Templates.xaml:186/253), появление вертикального скроллбара `ActualWidth` не меняет
(ради этого `CapacityWidth` и введён). Вердикт: **приемлемо, задокументировано (WGD-31, P3)**; живая проверка отставания на кадр — IL-MV-10.

### 4.5 Инструкция замера startup / UI hangs / allocation (НЕ выполнялась — приложение не запускалось)

1. Publish: `./installer/build.ps1` (или `dotnet publish src/DesktopOrganizer.UI -c Release -r win-x64`) → `installer/publish/win-x64/DesktopOrganizer.UI.exe`.
   Собрать так же публикацию `rc/0.4.0` @ `dde28df` во второй каталог (база сравнения).
2. Фикстура: `./tools/perf/New-PerfFixture.ps1 -Panels 5 -ItemsPerPanel 100` (бэкапит реальный state; пишет **schema v3**, поэтому первый
   запуск новой сборки прогонит цепочку v3→v4→v5 и создаст `premigration-v3.json` — warm-up-прогон скрипта это поглощает).
3. `./tools/perf/Measure-Startup.ps1 -ExePath <base>/DesktopOrganizer.UI.exe -Runs 5` и то же для `<new>`; `-SettleMs 1000`.
   Восстановить состояние: `./tools/perf/New-PerfFixture.ps1 -Restore`.
4. Критерии GO: медиана startup **новой ≤ база × 1.10**; working set **≤ база × 1.10**; таблица —
   `docs/Refactoring/Testing/performance-baseline-template.md`, процедура — `performance-baseline-procedure.md`.
5. UI hangs: включить диагностику (ADR-027 JSONL), выполнить WG-MV-7 (30 циклов resize) и ICO-MV-5 (200 items): ни одного
   `ResizeCompleted`/`DragCompleted` с `durationMs` аномально выше кадра ввода; блокировка старта панели с 200 элементами ≤ 0.7 с (P00 0.56 с).
6. Allocation baseline: `dotnet-counters monitor --process-id <pid> System.Runtime` во время 10-секундного drag `GridAligned`-панели
   (без Edit Layout) и 10-секундного resize: **0 gen2**, gen0-count/с сравним с базой; при Edit Layout допустим рост gen0 (4.1).

---

## §5. Defect list (консолидация `workspace-grid-debt.md` WGD-01…25 + новые WGD-26…33)

**P0 — нет.**

### P1

| ID | Описание | file:line | Статус | Действие |
|---|---|---|---|---|
| **WGD-01** | Угловые `Thumb` в `ResizeOverlay` объявлены 8×8 (`ResizeCornerThickness`) в 6-DIP ячейке `Grid` с корнер-alignment; WPF применяет **layout-clip** к слоту (не зависит от `ClipToBounds`) → hit-зона 6×6. **Подтверждено экспериментом §8.4**: `VisualClip=0,0,6,6`, hit (7,7)/(7.9,7.9) → ничего (проваливается в пустую центральную ячейку → под ней заголовок → жест **move** вместо resize). Дефолтный путь (`NativePanelSizing=False`); на native-пути `NativeHitTestMapper` корректен. С радиусом 6→8 доля прозрачной «линзы» в реальном 6×6 грипе **выросла ~21 % → ~38 %** — регресс относительно `rc/0.4.0`. Комментарий XAML :519-523 неверен | `src/DesktopOrganizer.UI/Views/ChildWindow.xaml:519-543` | **In progress** (фикс параллельно; на `5241dbf` в git не виден) | промпт P1-A ниже |
| **WGD-26 (новый)** | `Dispatcher.Invoke` на остановленный диспетчер из thread-pool (`RefreshExistence` 3-с таймер, `OnFolderChanged`, activation pipe) → `TaskCanceledException` → крах процесса при выходе (Folder Portal / любой тик во время закрытия); проявлялся как «Test Run Aborted» | `src/DesktopOrganizer.UI/Services/UiDispatch.cs`; `ViewModels/ChildWindowViewModel.cs:693,740`; `App.xaml.cs` | **Closed на `5241dbf`; ревью открыло WGD-32/33** | промпт P1-B; manual MV-EXTRA-4 |
| **WGD-32 (новый, CRITICAL по ревью)** | Взаимоблокировка: `FolderWatcher.OnDebounceElapsed` вызывает `Changed` **под `_debounceLock`**; подписчик `OnFolderChanged` блокирующе ждёт UI-поток (`UiDispatch.TryInvoke`), а UI-поток закрывает панель (`DetachWatcher` → `FolderWatcher.Stop` → `lock (_debounceLock)`) → **зависание приложения** при закрытии Folder Portal в момент debounce | `src/DesktopOrganizer.Services/Tools/FolderWatcher.cs:27,57-72,150-165`; `src/DesktopOrganizer.UI/ViewModels/ChildWindowViewModel.cs:655,691-693` | **In progress** (два агента параллельно: `FolderWatcher` — подписчики вне lock + try/catch; `UiDispatch.TryBeginInvoke`) | промпт P1-B; manual MV-EXTRA-4 (расширен) |
| **WGD-33 (новый, MAJOR по ревью)** | Блокирующий `Dispatcher.Invoke` из фоновых потоков: `RefreshExistence` (истощение thread-pool при непрокачиваемом UI) и activation-путь (`SingleInstanceActivationHandler` → задержка выхода до 2 с; дедлок `ActivationRequestRelay._gate` ↔ `SingleInstanceCoordinator.AttachSink`) | `ChildWindowViewModel.cs:736-752`; `App.xaml.cs:326`; `src/DesktopOrganizer.Services/SingleInstance/ActivationRequestRelay.cs:22,72-76`; `SingleInstanceCoordinator.cs:124` | **In progress** (fire-and-forget `TryBeginInvoke`, суженный `catch`, `WpfApplicationHost` во всех STA-тестах) | промпт P1-B |
| **WGD-27 (новый)** | Wall-clock-флейки (не abort): `AtomicJsonFileWriterTests.Write_TransientIOException_RetriesAndSucceeds` / `…UnauthorizedAccess…` (фиксированные 5 с / 80 мс vs 50/150 мс бюджеты), `NamedPipeActivationIntegrationTests.*` (`Faulted` вместо `PrimaryShuttingDown`/`OtherSession`); частота мала, в 5 прогонах этого gate не воспроизвелись | `tests/DesktopOrganizer.Tests/Unit/Storage/AtomicJsonFileWriterTests.cs:208,273`; `tests/…/Integration/SingleInstance/NamedPipeActivationIntegrationTests.cs` | **In progress** (параллельно) | промпт P1-C |
| WGD-02 | Хрупкое совместное владение `Application.Current` между STA-тестами / глушение диспетчера владельца | `tests/DesktopOrganizer.Tests/Helpers/StaTestRunner.cs`, `WpfApplicationHost.cs` | **Closed на `5241dbf`** — владелец пампит, конвенции `docs/Refactoring/Testing/sta-test-conventions.md`; довнесение `WpfApplicationHost` во все STA-тесты — в составе фикса WGD-33 | входит в P1-B |

### P2

| ID | Описание | file:line | Статус |
|---|---|---|---|
| WGD-05 | «Align all» — область действия (текущая страница) без автотеста | `UI/Services/WindowManager.cs` (`AlignPanelsToWorkspaceGrid`) | Manual verify (WG-MV-18/24) |
| WGD-15 | `RollUpBehavior._isAnimating` не сбрасывается при закрытии окна посреди анимации | `UI/Behaviors/RollUpBehavior.cs:56,180,202-212` | Open |
| WGD-23 | `Gap=0` (панели вплотную, как в Fences) структурно невозможен, нет UI | `UI/Models/WorkspaceGridOptions.cs` | Open — продуктовое решение |
| WGD-25 / DO-021 | `MinSurfaceOpacity=0.35` → ≈1.7:1 на светлых обоях | `UI/Theming/SurfaceComposer.cs` | Open — продуктовое решение |
| **WGD-28 (новый)** | Release hygiene: `Directory.Build.props` `<Version>0.3.0</Version>` на ветке линии `rc/0.4.0` (то же на `rc/0.4.0` — пред-существующее); в рабочем дереве **незакоммиченная** правка `installer/DesktopOrganizer.iss` (версия читается из FILEVERSION exe). До сборки RC: поднять `<Version>` до 0.4.0, зафиксировать/отбросить `.iss`, собрать через `build.ps1` (ISCC требует свежий publish) | `Directory.Build.props:4`; `installer/DesktopOrganizer.iss` (uncommitted) | Open |

### P3 (кратко; полный текст — `workspace-grid-debt.md`)

Open: WGD-04 (битый `cref` `PanelMetrics.ResizeGripThickness`, `RoundedRectClipConverter.cs:48` — `CS1574` не виден, т.к. XML-doc выключен),
WGD-06 (Align all при N=0), WGD-10 (широкий `catch` в `DefaultFreeResizeModifier` :256-266; `Keyboard.Modifiers` внутри `WM_SIZING` — MV-26/27),
WGD-13/14 (`ApplyFitHeight` doc/clamp `widthUnits`, `ChildWindow.ContentSizing.cs:432-437`), WGD-16 (`_contentSizingShownOnce` :303-321),
WGD-17 (save после демоции :370-377), WGD-18 (`NormalModeLayoutGesturesOptions` без `EnumDisplayNameConverter`, `MainWindow.xaml:170`),
WGD-19 (`ToolTipService.ShowOnDisabled` отсутствует — `ChildWindow.xaml:150-151,253-258`), WGD-20 (ESC-путь + AutoContent — MV-EXTRA-1),
**WGD-29** (liveness-poll I/O на UI-потоке, §4.3), **WGD-30** (`FilterObstaclesResolvableByThisEdge` аллокация на кадр, `WorkspaceLayoutPolicy.cs:813`),
**WGD-31** (`CapacityWidth` — лишний layout-pass на кадр, §4.4). Accepted: WGD-03, 08, 21, 22, 24. Manual: WGD-07, 09. Closed: WGD-11, 12.

### Промпты исполнителям (P1)

**P1-A — WGD-01 (агент: `wpf-ui-builder`, затем `tdd-test-engineer`; ревью `code-quality-reviewer` ∥ `fences-ux-parity-reviewer`).**
Файлы: `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` (блок `ResizeOverlay`, :489-543), новый STA-тест в
`tests/DesktopOrganizer.Tests/Unit/Views/` (или в `ChildWindowSizingGridTests`), `docs/PLANS/workspace-grid-07-visual.md` §13, ADR-025 amendment,
`docs/REVIEWS/workspace-grid-debt.md` (WGD-01 → Closed). Задача: угловые `Thumb` должны реально хит-тестится 8×8 DIP от внешнего угла на WPF-пути.
**Проверенный рецепт (§8.4, вариант «SPAN FIX»):** оставить `Width/Height=ResizeCornerThickness` и корнер-alignment, но дать каждому угловому
`Thumb` слот ≥ 8 DIP через span в соседнюю `*`-строку/колонку: TopLeft `Grid.Row=0 Grid.RowSpan=2 Grid.Column=0 Grid.ColumnSpan=2`;
TopRight `Row=0 RowSpan=2 Column=1 ColumnSpan=2` (`HorizontalAlignment=Right`); BottomLeft `Row=1 RowSpan=2 Column=0 ColumnSpan=2`;
BottomRight `Row=1 RowSpan=2 Column=1 ColumnSpan=2`. Объявление после прямых рёбер сохраняет приоритет угла (в эксперименте (7,3)→TopLeft,
(9,3)→Top). Исправить комментарий :519-523 (WPF **клипит** к слоту независимо от `ClipToBounds`). Не трогать `NativeHitTestMapper`/`PanelMetrics`.
DoD: STA-тест на живом `ChildWindow`/копии `ResizeOverlay`: для каждого углового `Thumb` `VisualTreeHelper.GetClip(thumb) == null`,
`RenderSize == 8×8`, `VisualTreeHelper.HitTest(overlay, (7,7))` (и зеркала) → угловой `Thumb`, `(7,3)` → угловой, `(9,3)` → `Top`;
`dotnet test` зелёный; ручной пункт VIS-MV-3 на 100/125/150 %; ChildWindow.xaml.cs не менять.

**P1-B — WGD-32/WGD-33 (+ закрытие WGD-26/02). Два агента уже в работе параллельно; далее `tdd-test-engineer` → `code-quality-reviewer`.**
(1) **`dotnet-services-engineer` — `FolderWatcher`** (`src/DesktopOrganizer.Services/Tools/FolderWatcher.cs:150-165`): под `_debounceLock` только
снять снапшот аргументов/сбросить таймер; `Changed?.Invoke` — **вне** lock, в try/catch (исключение подписчика не должно убивать timer-поток);
`Stop()` (:57-72) не должен ждать подписчика. (2) **`wpf-ui-builder`/`dotnet-services-engineer` — `UiDispatch.TryBeginInvoke`**
(`src/DesktopOrganizer.UI/Services/UiDispatch.cs`): fire-and-forget (`BeginInvoke`/`InvokeAsync`, приоритет `Background`/`Normal`), тот же probe
`HasShutdownStarted/Finished`, `catch` сужен до `OperationCanceledException`/`TaskCanceledException`; перевести три call-site'а —
`ChildWindowViewModel.OnFolderChanged` (:691-693), `RefreshExistence` (:736-752), `App.xaml.cs:326` (activation); `WpfApplicationHost` во всех
STA-тестах. (3) **`tdd-test-engineer`:** тест дедлока — `FolderWatcher` с подписчиком, который блокируется на событии, пока другой поток вызывает
`Stop()`; ожидание с таймаутом 5 с должно завершиться (до фикса — красный); тесты `UiDispatchTests` для `TryBeginInvoke` (shutdown → `false`,
не блокирует вызывающий поток); `grep -rn "Dispatcher.Invoke" src/` — не осталось блокирующих вызовов из не-UI потоков (либо каждый доказуемо
UI-потоковый, с комментарием). (4) **`code-quality-reviewer`:** повторное ревью без MAJOR; проверить, что дроп события при shutdown не теряет
данных (liveness/иконки/активация — идемпотентные повторяемые операции), что `Dispose` останавливает таймеры до shutdown диспетчера, что
`ActivationRequestRelay._gate` не удерживается во время вызова sink. DoD: все четыре пункта, `dotnet test` ×3 зелёный, WGD-26/32/33/02 → Closed,
ADR-012/ADR-027 amendment (потоковая модель liveness/watcher), manual MV-EXTRA-4 (три сценария §6 #4).

**P1-C — WGD-27 (агент: `debug-healer`, затем `tdd-test-engineer`).** Файлы: `tests/…/Unit/Storage/AtomicJsonFileWriterTests.cs:208-300`,
`tests/…/Integration/SingleInstance/NamedPipeActivationIntegrationTests.cs`. Задача: убрать зависимость от wall-clock — синхронизация
через `ManualResetEventSlim`/`TaskCompletionSource` (сигнал «lock-holder захватил handle», «ReadOnly снят») вместо `Task.Delay(80)`/фикс. 5 с;
для pipe-тестов — ожидание статуса с polling до 30 с и явный `PrimaryShuttingDown`-сигнал вместо гонки. DoD: 30 прогонов `dotnet test --filter`
по обоим классам без падений; полный прогон ×3 зелёный; ни одно ожидание теста не ослаблено (проверять тот же исход, не «любой»).

---

## §6. Manual verify (Win10/Win11)

Сводная матрица — [`docs/REVIEWS/workspace-grid-manual-verify.md`](workspace-grid-manual-verify.md): **87 пунктов**
(A: WG-MV-1…29, B: IL-MV-1…29, C: VIS-MV-1…15, D: ICO-MV-1…8, E: MIG-MV-1…3, F: MV-EXTRA-1…3) + новый **MV-EXTRA-4** (ниже).
Выполнено в этом gate: **0** (нет живой сессии приложения; один монитор 96 DPI). Требуемое оборудование: Win11 + Win10 машина/ВМ,
**второй монитор** (для WG-MV-1/2/3, IL-MV-11), DPI 125/150 %.

**Топ-10 критичных сценариев (обязательны до RC-cut):**

| # | ID | Что | Почему критично |
|---|---|---|---|
| 1 | VIS-MV-3 (после мержа P1-A) | Диагональный resize из самого угла, WPF-путь и native, 100/125/150 % | закрывает P1 WGD-01; сейчас жест уходит в move |
| 2 | MIG-MV-1 + IL-MV-7 | Реальный апгрейд v4→v5: ни одна панель не двигается/не меняет размер, панель 344 DIP не переснапается на 352 | «нет jump on first launch» — центральный инвариант пакета |
| 3 | MIG-MV-2 | Откат: v5-файл под сборкой `rc/0.4.0` — пустой старт, файл не тронут; затем процедура §3.2 с `premigration-v4.json` | rollback-гарантия релиза |
| 4 | **MV-EXTRA-4 (новый; после мержа P1-B)** | (a) открыть Folder Portal, дождаться 3-с тика, закрыть приложение ×10 — чистый выход без WER/Event Log; (b) в папке портала менять файлы и **закрывать панель ровно в момент debounce** (20 попыток) — нет зависания; (c) запустить вторую инстанцию во время выхода первой — выход не задерживается (>2 с), вторая не блокируется | закрывает P1 WGD-26/32/33 |
| 5 | WG-MV-3 + WG-MV-24 | Drag DISPLAY1→DISPLAY2, тема, рестарт — панель остаётся; двойной `WM_SETTINGCHANGE` — 0 записей | F-6 был «мёртв» до C-1; защита от спама записей |
| 6 | WG-MV-1 + WG-MV-2 | Монитор с отрицательными координатами / докованный taskbar: `Window.Left` vs `GetWindowRect`, первая линия сетки не под taskbar | **блокирующий gate ADR-028 → Accepted**; `DesktopCoordinateSpace` не калиброван |
| 7 | WG-MV-7 + WG-MV-22 + WG-MV-27 | 30 циклов × 8 сторон оба пути; ESC в native resize (R-8, span сохранён); Alt при resize | DoD P04 D4-1; WGD-10/20 |
| 8 | WG-MV-26 + IL-MV-11 | DPI 125/150/175 %: 10 resize туда-обратно — то же число; `FixedGap` на мониторе с другим DPI | ratchet-класс багов (MAJOR-2 P04) |
| 9 | IL-MV-9 + IL-MV-10 + IL-MV-1 | Порог скроллбара `FixedGap` — колонка не прыгает; live resize без отставания на кадр; шаг иконок одинаков на 3 ширинах | CRITICAL-1 P05, WGD-31 |
| 10 | ICO-MV-1 + ICO-MV-4 + ICO-MV-5 | Грязный state — реальные иконки; удалить/вернуть файл ≤3 с; 200+ items — блокировка ≤0.6 с | F-9/F-10, WGD-21 |

Далее по приоритету: WG-MV-12 (рестарт Explorer/WorkerW), VIS-MV-13 (High Contrast), VIS-MV-12 (List ellipsis), IL-MV-20/22
(apply-to-all, `IsLayoutLocked`), ICO-MV-6 (Win10 shell-эквивалентность — единственный Win10-специфичный пункт с измерением только на Win11).

---

## §7. Release decision и rollback

### Решение (первичный gate, `5241dbf`): **BLOCKED** → условия выполнены на `6a87a2c`, см. §9

Обоснование:
1. **Открытый P1 на HEAD** — WGD-01 подтверждён воспроизводимым экспериментом (§8.4): на дефолтном пути угловой resize-grip 6×6, а не 8×8,
   и относительно базы доля мёртвой зоны выросла (радиус 6→8). DoD P09: «все P0/P1 исправлены и повторно проверены» — не выполнено, пока
   фикс не смержен. Фикс локальный (XAML, рецепт проверен), риск мержа низкий.
2. **DoD P09 требует реально выполненной manual-матрицы «либо выпуск помечен blocked»** — выполнено 0/87; часть пунктов (WG-MV-1/2/3)
   на доступном оборудовании недостижима.
3. **Открытые P1 в shutdown/Folder Portal-пути (WGD-32 CRITICAL, WGD-33 MAJOR по ревью)** — ревью фикса `5241dbf` нашло взаимоблокировку
   `FolderWatcher._debounceLock` ↔ UI-поток (зависание приложения при закрытии портала в момент debounce) и блокирующие `Invoke` из фоновых
   потоков (thread-pool, activation); оба фикса в работе — **до их мержа и ревью без MAJOR — не GO**.
4. Тест-хост: abort-класс закрыт (WGD-26/02, 0 abort в 5 прогонах этого gate и 30/30 у оркестратора); остаточные wall-clock-флейки (WGD-27)
   не блокируют, но должны быть закрыты до включения CI-gate.

Что **не** блокирует: 3 × 3117 + 2 × 3125 зелёных прогонов; миграция/rollback доказаны кодом и 100 тестами; P02–P08 DoD закрыты тестами;
остальной долг — P2/P3, задокументирован (§5).

**Путь к GO with manual-verify gate:**
1. Мерж P1-A (WGD-01) **и обоих фиксов P1-B** (WGD-32 — `FolderWatcher` вне lock; WGD-33 — `UiDispatch.TryBeginInvoke`) с ревью
   `code-quality-reviewer` без MAJOR → повторить быстрый gate: `dotnet build`, 3× `dotnet test`, STA-тест угловых грипов и тест дедлока
   `FolderWatcher` зелёные.
2. Выполнить топ-10 §6 на Win11 (обязательно) и Win10 (если доступно), заполнить колонку «Результат» матрицы; при Fail на #1–#4 — возврат в BLOCKED.
3. Затем **GO with manual-verify gate**: оставшиеся 77 пунктов матрицы выполняются как gate релиза (до публикации инсталлятора), ADR-028/029
   переводятся в `Accepted` **только** после WG-MV-1/2 (второй монитор). Закрыть WGD-28 (версия 0.4.0, `.iss`) до сборки инсталлятора.

### Rollback

| Уровень | Как | Что произойдёт |
|---|---|---|
| **Сборка** | Вернуться на ветку `rc/0.4.0` @ `dde28df` (schema v4, `<Version>0.3.0</Version>` — та же литеральная версия, WGD-28) | Приложение без ADR-028/029; новые ключи `user.config` (`DefaultPanelGeometryMode`, `DefaultIconLayoutPolicy`) игнорируются без ошибок |
| **Данные (автоматически)** | Старая сборка открывает v5 `application-state.json` → rung 7 `NewerSchema`, `_newerSchemaLockout` | Сессия стартует с **пустым** структурным состоянием; **файл на диске не изменяется** (read-only на всю сессию, `Save()` отказывает); возврат на новую сборку восстанавливает всё |
| **Данные (восстановить pre-migration)** | §3.2: скопировать `%LOCALAPPDATA%\DesktopOrganizer\State\application-state.premigration-v4.json` → `application-state.json` (предварительно сохранив копию v5) | Старая сборка читает `schemaVersion: 4` штатно; изменения, сделанные под v5 после апгрейда, остаются только в сохранённой копии v5 |
| **`--restore-state-backup`** | Не использовать для отката на старую сборку — `.bak` после первого `Save()` новой сборки уже v5 | Только для сценария C ADR-024 внутри одной версии схемы (MIG-MV-3 проверяет корректность spans) |
| **Функциональный откат без переустановки** | Настройки → «Defaults for new panels» → `Free-form (legacy)` / `Adaptive stretch (legacy)`; на панели снять галку «Align to workspace grid» (spans очищаются, `Position/Size` сохраняются), «Icon layout → Adaptive stretch (legacy)» + «Apply to all panels…», «Auto-size to content» выкл. | Поведение = до пакета для всех панелей; `Gap` пользователю недоступен (WGD-23) |

---

## §8. Команды и логи

### 8.1 Сборка

```
dotnet build DesktopOrganizer.sln -c Release -nologo -v q
  HEAD 261058b: Build succeeded. 0 Error(s), 723 Warning(s)
  HEAD 5241dbf: Build succeeded. 0 Error(s), 723 Warning(s)
  Распределение: SA1512 652, SA1202 266, SA1025 172, SA1515 76, SA1204 48, SA1117 48, SA1203 30, SA1118 22, SA1407 20, SA1134 18, SA0001 14, …; CS1574 0
```

### 8.2 Тесты

```
dotnet test DesktopOrganizer.sln -c Release --no-build -nologo   (HEAD 261058b, 3 прогона подряд)
  run1: Passed! - Failed: 0, Passed: 3117, Skipped: 0, Total: 3117, Duration: 16 s
  run2: Passed! - Failed: 0, Passed: 3117, Skipped: 0, Total: 3117, Duration: 17 s
  run3: Passed! - Failed: 0, Passed: 3117, Skipped: 0, Total: 3117, Duration: 17 s
dotnet test … (HEAD 5241dbf, 2 прогона)
  run1: Passed! - Failed: 0, Passed: 3125, Skipped: 0, Total: 3125, Duration: 16 s
  run2: Passed! - Failed: 0, Passed: 3125, Skipped: 0, Total: 3125, Duration: 16 s
dotnet test … --filter "FullyQualifiedName~Migration|FullyQualifiedName~Golden|FullyQualifiedName~Baseline"
  Passed! - Failed: 0, Passed: 100, Skipped: 0, Total: 100, Duration: 355 ms   (по классам — §3.6)
```
«Test Run Aborted» не наблюдался ни в одном из 5 полных прогонов.

### 8.3 Git

```
git log --oneline -1                      -> 5241dbf merge: test-host abort root cause (…)   [gate выполнен на 261058b]
git diff dde28df..261058b --stat | tail -1 -> 211 files changed, 33716 insertions(+), 411 deletions(-)
git diff --stat 261058b 5241dbf | tail -1  -> 9 files changed, 704 insertions(+), 31 deletions(-)
git show dde28df:src/DesktopOrganizer.Services/Storage/StateEnvelope.cs | grep CurrentSchemaVersion -> = 4
git status --short                        -> M installer/DesktopOrganizer.iss ; ?? DesktopOrganizer.zip ; ?? DesktopOrganizer_WorkspaceGrid_Prompts.zip
git worktree list / git log HEAD..worktree-* -> ни одна из 25 agent-веток не опережает HEAD (фикс WGD-01 в git не виден)
```

### 8.4 Эксперимент WGD-01 (воспроизводимо; код вне репозитория, `net8.0-windows` + `UseWPF`, без окна)

Структура повторяет `ResizeOverlay` (`ChildWindow.xaml:502-543`): `Grid` 6/*/6 × 6/*/6, четыре прямых `Thumb` в крайних ячейках, четыре
угловых `Thumb` `Width=Height=8` с корнер-alignment в угловых ячейках, шаблон `Border Background=Transparent` (как `ResizeHandleStyle`,
`Controls.xaml:118-128`). `Measure(200×150)`, `Arrange`, затем `VisualTreeHelper.GetClip` и `VisualTreeHelper.HitTest` (поднимаясь до `Thumb`).

```
[AS SHIPPED — как в HEAD]
TopLeft     RenderSize=8,8  VisualClip=0,0,6,6  offset=0,0
BottomRight RenderSize=8,8  VisualClip=2,2,6,6  offset=192,142
hit (3,3)->TopLeft  (7,3)->Top  (3,7)->Left  (7,7)-><none>  (7.9,7.9)-><none>  (8.1,8.1)-><none>  (9,3)->Top
hit (197,147)->BottomRight  (193,143)-><none>  (191.9,141.9)-><none>

[SPAN FIX — угловые Thumb с RowSpan=2/ColumnSpan=2 в соседнюю *-строку/колонку, всё остальное без изменений]
TopLeft     RenderSize=8,8  VisualClip=null  offset=0,0
BottomRight RenderSize=8,8  VisualClip=null  offset=192,142
hit (3,3)->TopLeft  (7,3)->TopLeft  (3,7)->TopLeft  (7,7)->TopLeft  (7.9,7.9)->TopLeft  (8.1,8.1)-><none>  (9,3)->Top
hit (197,147)->BottomRight  (193,143)->BottomRight  (191.9,141.9)-><none>
```

Вывод: в поставляемом XAML угловой grip хит-тестится 6×6 (layout-clip к слоту `Grid`, `NeedsClipBounds` в `FrameworkElement.ArrangeCore`,
не зависит от `ClipToBounds`); рецепт со span даёт полные 8×8 при сохранённом приоритете угла над ребром. `PanelMetricsTests`/`NativeHitTestMapperTests`
проверяют только арифметику констант и этот дефект поймать не могли.

### 8.5 Что НЕ выполнялось (blocked/manual)

Запуск приложения; WPF smoke; PerMonitorV2 100→150 %; negative origin; monitor unplug/replug; WorkerW restart; native fallback живьём;
restore snapshots живьём; 200 icons живьём; замер startup/UI hangs/allocation (инструкция §4.5). Все — в матрице §6.

---

## §9. Повторный gate (2026-09-22, HEAD `6a87a2c`)

| Условие §7 | Статус | Доказательство |
|---|---|---|
| Мерж P1 WGD-01 (угловые грипы 6×6) | ✓ | `7b22cd2`: dedicated corner cells 8×8, `ResizeOverlayGeometryTests` (`ActualWidth/Height==8`, `LayoutClip==null`) |
| Мерж + ревью WGD-32/33 (`FolderWatcher` вне lock; `UiDispatch.TryBeginInvoke`) | ✓ | `c7c9c60`/`3cea2d1` (fire-and-forget маршалы, single-flight liveness, `WpfApplicationHost` во всех STA-тестах; верификация Approve-with-debt → долг закрыт guard'ом `_disposed` в `OnFolderChanged`), `6a87a2c` (`RaiseChanged` вне `_debounceLock` + try/catch + `SubscriberThrew`) |
| WGD-26 (Invoke на остановленный диспетчер — production) | ✓ | `5241dbf` + `UiDispatchTests`; STA-конвенции `docs/Refactoring/Testing/sta-test-conventions.md` (правила 1–4) |
| WGD-27 (wall-clock флейки) | ✓ | `6a87a2c`: `AtomicJsonFileWriter` test-only seam (retry без sleep), бюджеты pipe-тестов 10 с + условие вместо wall-clock; 10 прогонов агента + 5 прогонов оркестратора — 0 Failed/0 Aborted |
| Стабильность тест-хоста | ✓ | `dotnet test --no-build` ×5 подряд: 3141/3141, 15 с каждый, 0 Aborted |
| Ручная матрица (§6 топ-10) | ✗ не выполнена | среда без GUI-сессии для приложения, один монитор 1920×1080 @ 96 DPI — **обязательна перед релизом** |

**Решение: GO with manual-verify gate.** Код-часть пакета (P00–P08) закрыта; релиз `rc/0.4.0` открывается после
прохождения топ-10 §6 на Win11 (Win10 — при наличии). При Fail пунктов #1–#4 — возврат в BLOCKED. Остаточный
долг — только P2/P3 (`workspace-grid-debt.md`), включая WGD-28 (версия `0.3.0` в `Directory.Build.props` на
линии `rc/0.4.0` — решение владельца релиза).

## Recommended Next Actions

- 🚨 **CRITICAL — `wpf-ui-builder` → `tdd-test-engineer`** (промпт P1-A, §5): смержить фикс WGD-01 по проверенному рецепту (RowSpan/ColumnSpan),
  добавить STA-тест на `VisualClip == null` + hit-test (7,7), поправить комментарий XAML; затем повторить быстрый gate (build, 3× test).
- 🚨 **CRITICAL — `dotnet-services-engineer` (`FolderWatcher`) ∥ `wpf-ui-builder` (`UiDispatch.TryBeginInvoke`) → `tdd-test-engineer` →
  `code-quality-reviewer`** (промпт P1-B): довести и смержить оба in-flight фикса WGD-32/33, тест на дедлок (закрытие панели во время debounce),
  grep всех `Dispatcher.Invoke` из не-UI потоков; до мержа и ревью без MAJOR — **не GO**.
- ⚠️ **RECOMMENDED — `debug-healer` → `tdd-test-engineer`** (промпт P1-C): wall-clock-флейки WGD-27 до включения CI-gate.
- ⚠️ **RECOMMENDED — ручной прогон (пользователь, Win11 + второй монитор; Win10 при наличии):** топ-10 §6, заполнить `workspace-grid-manual-verify.md`;
  MV-EXTRA-4 добавить в матрицу (раздел F).
- ⚠️ **RECOMMENDED — `architecture-documenter`:** после мержа P1-A и топ-10 — статус в `docs/fix-prompts/README.md` (P09), ADR-028/029 → `Accepted`
  **только** после WG-MV-1/2; ADR-027/ADR-012 amendment для `UiDispatch` (если ревью P1-B подтвердит); release notes (F-3 §3.5, риск §3.4,
  rollback §7); закрыть WGD-28 (`<Version>` 0.4.0, `installer/DesktopOrganizer.iss`).
- 💡 **OPTIONAL — `dotnet-services-engineer`/`wpf-ui-builder`** (если планируется полировочный проход до RC): пакет P3 из §5 одним PR —
  WGD-04, 18, 19, 13/14, 15, 17, 30; WGD-29 (liveness stat-вызовы на пул) — отдельно, с тестом.
- 💡 **OPTIONAL — `fences-feature-planner`:** ADR-кандидат «async icon loading (STA-воркер)» (WGD-21) и продуктовое решение по `Gap=0` (WGD-23)
  / полу контраста (DO-021) — после релиза.
