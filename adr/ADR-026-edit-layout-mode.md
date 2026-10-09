# ADR-026: Dedicated Edit Layout Mode

**Статус:** Accepted — Amended 2026-09-22 (ADR-028: таблица `IsLayoutLocked` явно распространена на команду «Align to workspace grid»); Amended 2026-09-26 (ADR-031: чекбокс «Edit layout mode» удалён из окна настроек, `Ctrl+E` сохранён); Amended 2026-10-09 (ADR-audit WP-C: третий триггер re-gate — `ILayoutEditModeService.NotifyNormalModeGesturesChanged`, смена `NormalModeLayoutGestures` доходит до открытых панелей сразу); Amended 2026-10-10 (ADR-022: видимые resize-грипы Edit Layout на native-пути — `ResizeGripVisuals`, D-6)

Фаза 6 «Edit Layout UX» (`docs/Refactoring/PHASES/PHASE-6-edit-layout-ux.md`,
`docs/Refactoring/ROADMAP.md`). Ветка `ui/ambient-accent-design-tokens` (от Фазы 5, ADR-025 Accepted).
Мастер-план реализации — `docs/PLANS/phase-6-edit-layout-ux.md` (D-1..D-13, риски R-1..R-13, ручная
матрица V-1..V-19). Конвейер: `fences-feature-planner` → P6-1 (`dotnet-services-engineer`) → P6-2..P6-5
(`wpf-ui-builder`, частично параллельно с `dotnet-services-engineer` на P6-3) → **три полных раунда**
`code-quality-reviewer` ∥ `fences-ux-parity-reviewer` с фиксами между ними → финальный вердикт **APPROVE**
(0 CRITICAL, 0 MAJOR, 5 MINOR не блокирующих) → этот документ (P6-7). Текущий HEAD `e43af62`.

## Контекст

Цель фазы — та же, что у всего проекта (см. `CLAUDE.md`): паритет со Stardock Fences. Fences разделяет
два режима работы с рабочим столом: обычный (иконки/панели используются как контент) и режим
редактирования раскладки, в котором панели можно свободно двигать/изменять размер, а обычное
взаимодействие с содержимым отходит на второй план. До Фазы 6 в проекте такого разделения не было:
move/resize были доступны всегда и везде, что создавало два независимых источника случайных изменений
геометрии панели — драг заголовка одиночным кликом (см. ROADMAP Фаза 2, известный баг: клик по заголовку
стартовал и тут же завершал drag с нулевым смещением, но `SnapToGrid()`/`SaveWindowsState()` всё равно
вызывались) и невидимые 6-пиксельные resize-thumb'ы по краям панели, перехватывающие клики, адресованные
крайним иконкам.

## Decision

Panel move/resize/snap-guides становятся операциями режима редактирования раскладки — но не буквально
«всё или ничего». Normal-режим по умолчанию приоритизирует запуск/выделение/переименование контента;
Edit Layout-режим делает move/resize/snap-guides первоклассными операциями с явными визуальными
аффордансами (видимые resize-грипы, усиленная accent-рамка, немьютированные controls заголовка).

### Уточнение: per-gesture политика в Normal-режиме, а не полный запрет (D-2)

Буквальное чтение исходной формулировки решения («move/resize become first-class layout editing
operations») означало бы, что панель нельзя сдвинуть вообще, пока пользователь не найдёт Edit Layout —
это регрессия базовой обнаруживаемости, которую никакая формулировка Definition of Done не компенсирует.
План (`docs/PLANS/phase-6-edit-layout-ux.md` §1 D-2) уточнил решение: политика **разная для move и для
resize**, потому что источники «случайности» у них разные.

| Жест | Normal (default) | Обоснование |
|---|---|---|
| **Move** (drag заголовка) | **разрешён**, но с порогом начала жеста (`SystemParameters.MinimumHorizontal/VerticalDragDistance`, тот же механизм, что `ListReorderGestureController`, ADR-014) | Заголовок — однозначная ручка: в нём не живёт контент, промахнуться по нему нельзя. Порог лечит баг «одиночный клик по заголовку = `SnapToGrid` с нулевым смещением», не запрещая жест как таковой |
| **Resize** (8 thumb'ов по 6px / нативный `WM_NCHITTEST`) | **запрещён** | Это и есть реальный источник случайных геометрических изменений: невидимая 6-пиксельная рамка лежит поверх краёв контентной области, перехватывая клики, адресованные крайним иконкам |

Пользовательская настройка — один ключ с тремя значениями, чтобы старое поведение (полная обратная
совместимость) осталось достижимым по желанию:

```csharp
public enum NormalModeLayoutGestures { MoveAndResize = 0, MoveOnly = 1, None = 2 }
// IAppSettings.NormalModeLayoutGestures : string, default "MoveOnly"
```

Машинно-проверяемое ядро политики — `LayoutGesturePolicy.Resolve(mode, isLayoutLocked, normalPolicy)`,
чистая логика без WPF, таблица истинности 2×2×3 покрыта `LayoutGesturePolicyTests` (12 кейсов).

`fences-ux-parity-reviewer` подтвердил выбор `MoveOnly` как дефолт **в обоих** раундах ревью, где вопрос
поднимался (план §8 Q-1): вариант «полная обратная совместимость» (`MoveAndResize` по умолчанию) ничего
не чинит «из коробки» — фаза потеряла бы весь практический смысл, оставшись лишь новым переключателем без
эффекта по умолчанию; а буквальный запрет move (без исключения для заголовка) убирает базовую
обнаруживаемость и не имеет аналога в Stardock Fences, где панель всегда можно взять за заголовок.
`MoveOnly` — точка, в которой обе стороны компромисса удовлетворены: случайность геометрии устранена
(источник — resize-thumb'ы, не заголовок), обнаруживаемость перемещения панели сохранена.

### Таблица: что блокирует `IsLayoutLocked`, а что нет (D-3)

`Lock layout` — пользовательская настройка «геометрия зафиксирована», прямой аналог «Lock fences» в
Stardock Fences. `fences-ux-parity-reviewer` подтвердил соответствие этой таблицы поведению эталона:

| Действие | При `IsLayoutLocked = true` |
|---|---|
| Drag заголовка (move) | ✖ запрещён |
| Resize (обе ветки — legacy thumb'ы и нативная) | ✖ запрещён |
| Вход в Edit Layout | ✖ запрещён (`CanEnterEditLayout == false`; пункты меню disabled с тултипом «Layout is locked») |
| Roll-up / roll-down | ✔ **разрешён** — это видимость контента, а не раскладка |
| Rename заголовка, выделение, запуск файла, drag-reorder иконок, drop файлов | ✔ разрешены — Lock про геометрию окон, не про контент |
| Внутренняя `SnapWidthToGrid()`-ренормализация (на `Initialize` и при смене `IconSize`/`ViewMode`) | ✔ **разрешена** — это ре-нормализация под текущую сетку (ADR-018), а не пользовательский жест; иначе заблокированная раскладка молча рассинхронизируется с сеткой и «починится» скачком в момент разблокировки |

Границы намеренно узкие: Lock — про геометрию окон, а не про весь функционал панели.

### Визуальный язык (D-6)

Edit Layout переиспользует визуальный язык Фазы 5 (`InteractionVisualState`, ADR-025), не заводит новый
словарь. `PanelTheme.IsLayoutEditingProperty` — четвёртое наследуемое attached-DP рядом с тремя
существующими. `LayoutEditVisuals` — новый, чисто аддитивный слой поверх `InteractionVisualState`:

- border floor `0.60` (та же ступень, что `BorderOpacityFor(Editing)`) — граница панели в Edit Layout
  никогда не опускается ниже уровня, который в Фазе 5 означал «идёт активное редактирование»;
- chrome (кнопки заголовка) форсированно `1.0` на Idle — в режиме редактирования приглушать управляющие
  элементы контрпродуктивно (в Normal они остаются приглушёнными на `0.45`, как в Фазе 5);
- accent-индикатор — множитель `×1.35`;
- resize-грипы становятся видимыми (accent-тонированный `ResizeHandleStyle` вместо полностью прозрачного
  `ResizeThumbStyle`); геометрия `ResizeOverlay` (колонки/строки 6px/`*`/6px) не меняется ни на пиксель —
  меняется только `ControlTemplate` Thumb'а. *(Устарело 2026-10-10: `ResizeOverlay`, `Thumb`'ы и `ResizeHandleStyle`
  удалены (ADR-022 Фаза 1); грипы — чисто визуальный `ResizeGripVisuals`, hit-test у `WM_NCHITTEST`, см. Amendment
  2026-10-10 ниже.)*

Инвариант-тест (`LayoutEditVisualsTests`): для каждого из пяти `PanelVisualState` значение при
`isLayoutEditing:true` строго ≥ значения при `false`, хотя бы по одному каналу — строго больше;
регресс-тест — при `isLayoutEditing:false` все функции возвращают ровно то же, что
`InteractionVisualState.*` (Фаза 5 не сдвинулась ни на единицу).

### Persistence: схема JSON не мигрируется (D-8)

**Решение: `StateEnvelope.CurrentSchemaVersion` остаётся 4.** Схема JSON-хранилища (Фаза 4, ADR-024) не
трогается вообще.

| Состояние | Персистится? | Где |
|---|---|---|
| `LayoutInteractionMode.Mode` | **НЕТ** — транзиентный. Приложение, стартующее в Edit Layout после перезагрузки машины, — сюрприз, а не фича | — |
| `IsLayoutLocked` | ДА | `IAppSettings`/`user.config`, аддитивный скалярный bool-ключ (default `false`) |
| `NormalModeLayoutGestures` | ДА | `IAppSettings`/`user.config`, аддитивный скалярный string-ключ (default `"MoveOnly"`) |

Оба новых ключа — глобальные преференции приложения того же класса, что уже существующие
`GridSize`/`GridSnapDistance`/`SnapResizeToIconGrid`/`NativePanelSizing`, и живут рядом с ними в
`IAppSettings`/`user.config`, а не в JSON-документе состояния. Альтернатива «положить в JSON-документ»
была отклонена: она стоила бы полноценной `V4ToV5Migration` + регенерации golden-фикстур +
прохода по `SchemaContractTests`/`GoldenMigrationTests` ради двух булевых/строковых преференций, не
относящихся ни к одной из существующих секций документа. Известный долг «часть настроек в
`user.config`, часть в JSON-документе» уже зафиксирован как `DO-014` — новым долгом эта фаза не
прирастает, только подтверждает существующий выбор.

## Отвергнутые альтернативы

- **Глобальный режим как шестое значение `PanelInteractionState`.** Отклонено — `Hover`/`Editing`/
  `Dragging`/`Resizing` (ADR-021) остаются panel-local визуальными состояниями; Edit Layout — ортогональный
  глобальный факт (можно быть `Dragging` и внутри Edit Layout, и внутри Normal). Второй, конкурирующий с
  трекером enum не заводится.
- **Per-panel edit mode.** Отклонено — snap-guides и alignment по определению межпанельные: панель A в
  edit-режиме, выравниваемая по панели B в normal-режиме, даёт неопределённую семантику; плюс требование
  «включается одним действием» несовместимо с переключателем на 10 отдельных панелей.
- **Полный запрет move/resize в Normal («буквальное» прочтение исходной формулировки).** См. «Уточнение»
  выше — отклонено как регрессия обнаруживаемости.
- **Полная обратная совместимость (`MoveAndResize` по умолчанию).** Отклонено — фаза не чинит ничего «из
  коробки»; см. подтверждение `fences-ux-parity-reviewer` выше (план §8 Q-1).

## Последствия

### Позитивные

- Случайный drag иконки/клик по заголовку больше не двигает панель непредсказуемо; случайный resize за
  невидимый 6px-thumb устранён в Normal-режиме по умолчанию.
- Выход из Edit Layout гарантированно завершает активные жесты (`AbortResize(SourceChanged)` →
  `AbortDrag()` → `ForceIdle()`, строго в этом порядке, каждый шаг идемпотентен).
- Три независимые точки переключения режима (трей — checkable-пункт, гарантированный путь вне
  `WS_EX_NOACTIVATE`; меню заголовка панели; окно настроек + `Ctrl+E`) — «одним действием» выполнено с
  запасом.
- Snap-guides (магнитное притяжение к соседним панелям, `SnapGuideCalculator`) работают строго между
  `GridSnap.Snap` (ADR-011) и `PanelCollision.ClampAgainstObstacles` (ADR-017, всегда последний в
  конвейере) — «твёрдая стена» анти-оверлапа не пробивается направляющей ни при каких обстоятельствах.
- Zero-regression инвариант: вне Edit Layout конвейер драга побайтово идентичен пред-P6-5-состоянию;
  `ChildWindowViewModel.cs` — 0 изменённых строк (DO-002); ни одного нового hex-литерала.
- `IGestureDiagnostics.LayoutModeChanged` (категория `layout`, ADR-027) фиксирует каждый реальный переход
  режима — `docs/Refactoring/Testing/diagnostics-log-events.md`.

### Принятые trade-off'ы и известные ограничения (Negative)

- **P6-6 (визуальный оверлей направляющих) сознательно не реализован в этой итерации.** Snap-guides
  физически работают (магнитное притяжение к соседним панелям присутствует и проверено unit-тестами
  `SnapGuideCalculatorTests`), но **без визуального оверлея** — пользователь не видит линию, по которой
  панель защёлкнулась, только чувствует магнит. Причина выделения в отдельный, заведомо отбрасываемый
  пункт: z-order в слое `WorkerW` — исторически самая дорогая область этого проекта (ADR-019 —
  `Topmost` пришлось убирать; ADR-020 — порядок `ApplyDesktopWindowStyles` относительно `Show()` оказался
  критичным, поведение Win10 ≠ Win11 и до сих пор не верифицировано живьём). Полноценный click-through
  оверлей-окно поверх слоя десктопа — высокорисковая работа, которую нецелесообразно вести без живой
  проверки на Windows между итерациями. Вынесено в бэклог как `DO-018` (полноценный оверлей) и `DO-017`
  (визуализация grid spacing, зависит от исхода `DO-018`).
- **M-1 (финальное ревью, не блокирует).** Regression-pin тест для cross-monitor snap-guide фикса
  (`SnapGuideCalculatorTests`) покрывает только контракт чистой математики `SnapGuideCalculator` —
  детерминированный расчёт направляющих и рабочей области. Он **не** покрывает поведение вызывающей
  стороны, `PanelDragMoveBehavior` (резолв текущего монитора из снапшота `_dragMonitors`, взятого один
  раз на старте жеста). Причина: в проекте нет headless-WPF-gesture-харнесса, способного симулировать
  реальный `MouseMove`-жест поверх `Window`. Если кто-то повторно внесёт регрессию именно в
  `PanelDragMoveBehavior` (а не в `SnapGuideCalculator`), автотесты её не поймают — только manual verify.
  Зафиксировано в бэклоге как кандидат для Фазы 9 (hardening).
- **M-2 (финальное ревью, не блокирует).** Fallback-семантика `PanelDragMoveBehavior` при точке вне всех
  снапшотнутых на старте жеста мониторов изменилась: было бы естественно ожидать «работать по рабочей
  области основного монитора» (primary monitor's work area), но фактическое поведение после
  MAJOR-2/MAJOR-A фикса — `workArea` остаётся `Rect.Empty`, что по контракту `SnapGuideCalculator.Snap`
  означает «направляющие полностью отключены для этого кадра» (guard disabled), а не «отключены и
  используется primary». Это не bit-for-bit прежнее поведение, но отклонение в сторону «мягче» (магнит
  молча не сработает, а не подставит геометрию не того монитора) — принято как есть, задокументировано.
- **Тултип «Layout is locked»** на задизейбленном пункте трея (`ToolStripMenuItem.ToolTipText`,
  `TrayIconManager`) — логика присвоения корректна и покрыта `TrayIconManagerTests`, но реальное
  отображение WinForms-тултипа на disabled-пункте контекстного меню не проверено headless — только
  manual verify.
- **`Escape` для выхода из Edit Layout — best-effort**, тот же непроверенный класс риска, что ADR-013 R1
  (клавиатура на `WS_EX_NOACTIVATE`-окнах никогда не была верифицирована живьём). Не объявлен
  единственным путём выхода — трей остаётся единственным гарантированным путём переключения режима вне
  зависимости от того, получает ли конкретное окно клавиатурный фокус.
- **High Contrast (Фаза 5, `DO-015`) не подключён к Edit Layout.** Live-переключение HC-темы не
  подхватывается визуальным языком Edit Layout по тем же причинам, что и во всей Фазе 5 — отдельный
  трек, вне скоупа этой фазы.

## Manual verify (Win10/Win11)

Фаза целиком про ввод — ни один из V-1..V-19 не воспроизводим в среде разработки (нет живого Windows
GUI). **Выполнено пользователем на Windows 11 24H2 2026-09-27, признано достаточным** (Windows 10 не
проверяется) — см. [docs/ROADMAP.md](../docs/ROADMAP.md) (строки 6–8); перечень ниже — исторический
чек-лист. Полная матрица — `docs/PLANS/phase-6-edit-layout-ux.md` §7.2 и
`docs/Refactoring/Testing/edit-layout-manual-matrix.md`. Наивысший приоритет: V-1/V-2 (переключение
режима из всех точек), V-3/V-4 (Normal — resize запрещён, move с порогом), V-7 (выход из режима
во время активного жеста), V-9/V-14 (обе ветки resize — legacy и нативная), V-10/V-18 (snap-guides,
включая переход на другой монитор посреди жеста), V-13 (Lock layout).

---

## Amendment 2026-09-22 (ADR-028)

[ADR-028](ADR-028-workspace-grid.md) явно распространяет уже существующую таблицу «что блокирует
`IsLayoutLocked`, а что нет» (D-3 выше) на новые geometry-жесты `PanelGeometryMode.GridAligned` —
**вторая машина состояний режима взаимодействия не заводится** (прямое продолжение решения «Отвергнутые
альтернативы» этого ADR — «Глобальный режим как шестое значение `PanelInteractionState`» уже был
отклонён по той же логике: geometry-режим ортогонален panel-local состояниям, а не заменяет их).

Дополнение к таблице D-3:

| Действие | При `IsLayoutLocked = true` |
|---|---|
| Move/resize `GridAligned`-панели (drag/thumb/native) | ✖ запрещён — та же строка таблицы, что «Move»/«Resize» для `LegacyFreeform`, без отдельного исключения |
| Команда «Align to workspace grid» (per-panel/all) | ✖ запрещена — explicit геометрический жест, той же природы, что drag/resize, не внутренняя ренормализация |
| Workspace-grid quantization на `EndDrag`/`EndResize` (безусловное довыравнивание к сетке уже во время разрешённого жеста) | ✔ разрешена, когда сам жест разрешён — та же логика, что «внутренняя `SnapWidthToGrid()`-ренормализация» в существующей таблице: это часть уже идущего жеста, не отдельное разрешение |

`NormalModeLayoutGestures` (`MoveOnly`/`None`/`MoveAndResize`) применяется к `GridAligned`-панелям
идентично `LegacyFreeform` — move через заголовок с тем же порогом начала жеста, resize запрещён в
Normal-режиме по умолчанию (`MoveOnly`). Единственное явное исключение — команда «Align to workspace
grid» (пункт меню, не drag/resize-жест) доступна независимо от `NormalModeLayoutGestures`, той же
природы, что «Roll-up»/«Rename» в существующей таблице D-3 (не блокируется этой настройкой, только
`IsLayoutLocked`).

Snap-guides конвейер (`GridSnap`/`SnapGuideCalculator`/`PanelCollision.ClampAgainstObstacles`, фиксированный
порядок, ADR-017 «твёрдая стена» непробиваема) — для `GridAligned` получает дополнительный первый шаг
(workspace-grid quantization) **перед** `SnapGuideCalculator`, сам зафиксированный порядок относительно
`ClampAgainstObstacles` (guides строго между `GridSnap`/эквивалентом и стеной) не пересматривается ни для
одного из двух режимов геометрии.

Persistence (D-8) — по аналогии с `IsLayoutLocked`/`NormalModeLayoutGestures`: **не решено этим
amendment**, `PanelGeometryMode` — per-panel поле в JSON-документе состояния (ADR-024 v4→v5,
`docs/PLANS/workspace-grid-migration.md`), а не в `user.config`, в отличие от глобальных
`IsLayoutLocked`/`NormalModeLayoutGestures` — потому что это свойство конкретной панели (как
`IconSize`/`ViewMode`), а не общая преференция приложения. `LayoutInteractionMode.Mode` (транзиентный,
не персистится) — не затронут, ортогонален `PanelGeometryMode` так же, как ортогонален
`PanelInteractionState`.

---

## Amended 2026-09-26 (ADR-031)

[ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md) убирает один из трёх независимых
путей переключения режима, перечисленных в разделе «Последствия» выше («Три независимые точки
переключения режима — трей…, меню заголовка панели, окно настроек + `Ctrl+E`»): чекбокс «Edit layout
mode» в окне настроек удаляется (эта настройка — сессионная команда, а не персистентная преференция, и
дублирует трей/меню заголовка). Клавиатурный жест `Ctrl+E`, работающий при фокусе на `MainWindow`,
сохраняется без изменений. Трей (checkable-пункт, гарантированный путь вне `WS_EX_NOACTIVATE`) и меню
заголовка панели остаются двумя независимыми путями переключения — вход «одним действием» по-прежнему
выполнен с запасом.

## Amended 2026-10-09 (ADR-audit WP-C): третий триггер re-gate — смена `NormalModeLayoutGestures`

**Дефект.** Resize-gate открытой панели (D-5) пересчитывался только при двух событиях: реальный переход режима
(`ILayoutEditModeService.ModeChanged`) и смена `IsLayoutLocked` при неизменном `Mode == Normal`. Третий вход
`LayoutGesturePolicy.Resolve` — настройка `IAppSettings.NormalModeLayoutGestures` (в окне настроек она
редактируется через `GlobalDefaultsViewModel`) — не имел уведомления (`IAppSettings` не INPC), поэтому смена `MoveOnly` ↔ `MoveAndResize` ↔ `None`
доходила до уже открытых панелей только после следующего переключения Edit Layout или перезапуска. Move-жест не
страдал (`canMove` перечитывает политику на каждое нажатие на заголовок), resize — страдал.

**Решение.** Новый член `ILayoutEditModeService.NotifyNormalModeGesturesChanged()` поднимает `PropertyChanged` с именем
`ILayoutEditModeService.NormalModeGesturesPropertyName` (= `nameof(IAppSettings.NormalModeLayoutGestures)`) — ретрансляция
поверх уже общего для всех панелей singleton'а, без нового персистентного состояния и без нового события. Вызывает его
`MainWindowViewModel` при `PropertyChanged(NormalModeLayoutGestures)` от `GlobalDefaultsViewModel` (то есть после записи
настройки). `ChildWindow.OnLayoutEditModePropertyChanged` теперь реагирует на **три** входа политики — `ModeChanged`,
`IsLayoutLocked`, `NormalModeLayoutGestures` — и для всех запускает **один и тот же полный путь** `AbortGesturesAndApplyLayoutGate`
(`AbortResize(SourceChanged)` → `AbortDrag()` → `ForceIdle()` → `SetAllowedByPolicy`, порядок D-4). Укороченного «просто
переприменить gate» нет: в нормальной работе жеста в момент смены настройки нет (пользователь в окне настроек) и abort — идемпотентный
no-op, а единый путь сохраняет гарантию порядка D-4, если программная смена когда-нибудь совпадёт с живым жестом.

Схема, `IAppSettings`/`user.config`, дефолт `MoveOnly` и семантика `LayoutGesturePolicy` не менялись (замороженный контур не затронут).

## Amendment 2026-10-10 (ADR-022): грипы Edit Layout на native-пути — `ResizeGripVisuals`

В D-6 «resize-грипы становятся видимыми (accent-тонированный `ResizeHandleStyle` …)» описывало `Thumb`-шаблон `ResizeOverlay`.
После решения пользователя 2026-10-10 единственным путём ресайза становится native ([ADR-022](ADR-022-native-panel-sizing.md)
Amendment 2026-10-10), где `ResizeOverlay` свёрнут и грипов в Edit Layout не было бы. Фаза 0 добавила **чисто визуальный**
`ResizeGripVisuals` (Grid из 8 `Border`, `IsHitTestVisible="False"`, та же сетка 8/`*`/8) с тем же триггером
`PanelTheme.IsLayoutEditing` → accent; видимость определяет `NativeSizingAffordance(UIElement? gripVisuals)` по тому же флагу
гейта (`ResizeAffordanceGate`: Edit Layout / `IsLayoutLocked` / roll-up / `NormalModeLayoutGestures`), поэтому таблица
`IsLayoutLocked` и политика Normal-режима не менялись. Hit-test по-прежнему `WM_NCHITTEST`; `ResizeHandleStyle` (Thumb) оставался
до Фазы 1 — **удалён 2026-10-10** вместе с `ResizeOverlay` (ADR-022 Фаза 1, `bc60c8e`): грипы — только визуальные
`Border`'ы (стили `ResizeGripVisualStyle`/`ResizeGripVisualEdgeStyle`, тот же триггер `PanelTheme.IsLayoutEditing` → accent),
никогда не hit-test-цель; опора на них политики `IsLayoutLocked`/Normal-режима — через `ResizeAffordanceGate`. Инвариант-тесты
`LayoutEditVisuals` не затронуты; геометрию грипов пинит `ResizeGripVisualsTests`. Ограничение: над полосой ресайза панель не
«горит» hover'ом (край — non-client, DO-052).

## Amendments (сводка)

| Дата | Источник | Что изменилось | Какие разделы/решения больше не действуют |
|---|---|---|---|
| 2026-09-22 | [ADR-028](ADR-028-workspace-grid.md) | Таблица `IsLayoutLocked` распространена на `GridAligned`; команда «Align to workspace grid» блокируется `IsLayoutLocked`; конвейер snap-guides получает шаг workspace-quantization перед `SnapGuideCalculator` | — |
| 2026-09-26 | [ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md) | Чекбокс «Edit layout mode» удалён из окна настроек (сессионная команда); `Ctrl+E` на `MainWindow` сохранён | «Последствия»: «Три независимые точки переключения режима — трей, меню заголовка, окно настроек + `Ctrl+E`» — теперь две (трей, меню заголовка) + `Ctrl+E` |
| 2026-10-09 | ADR-audit WP-C | Третий триггер re-gate resize открытых панелей: `ILayoutEditModeService.NotifyNormalModeGesturesChanged` (ретрансляция из `MainWindowViewModel` при смене `GlobalDefaults.NormalModeLayoutGestures`), полный abort→re-gate | Допущение, что gate пересчитывается только по `ModeChanged`/`IsLayoutLocked` (смена настройки действовала после следующего toggle/перезапуска) |
| 2026-10-10 | [ADR-022](ADR-022-native-panel-sizing.md) | Видимые грипы Edit Layout на native-пути: `ResizeGripVisuals` (8 `Border`, visual-only) + `NativeSizingAffordance(UIElement? gripVisuals)` | D-6: «акцентные грипы = `ControlTemplate` Thumb'а `ResizeHandleStyle`» — как единственный механизм (Thumb-механизм удалён в Фазе 1, 2026-10-10: `ResizeHandleStyle`/`ResizeOverlay` больше нет, остался только `ResizeGripVisuals`) |
