# P07 — Ambient Accent visual polish (border radius, header a11y, contrast, reduced motion)

**Этап:** P07 пакета [`docs/fix-prompts/`](../fix-prompts/README.md) (`07-visual.md`) · **Ветка:**
`bugfix/workspace-grid` (worktree, содержит P00–P02, P02b, P05 FixedGap, P08) · **Дата:** 2026-09-22
**Статус:** реализация выполнена, `dotnet build`/`dotnet test` зелёные; **fix-cycle 1 (2026-09-22,
`code-quality-reviewer`) применён — см. §12** (MAJOR-1/MAJOR-2/MAJOR-3 закрыты, MINOR/NIT/UX-P2
закрыты); **fix-cycle 2 (2026-09-22, повторное ревью) применён и ЗАКРЫВАЕТ этап P07 — см. §13 ниже**
(MAJOR-A resize-grip/hot-zone split, MAJOR-B List-ellipsis честный тест закрыты; MINOR/NIT закрыты);
визуальная сверка на реальном экране — manual (см. ниже), среда разработки не имеет живого Windows GUI.

Зависимость: результаты этапов 05/06 (P05 `IconLayoutPolicy`+F-3, P08 icon pipeline — не затронуты).
Референс «третий Ambient Accent» — визуальная концепция, изображение отсутствует в репозитории; ни одна
константа здесь не «снята с картинки» — все обоснованы существующими токенами/инвариантами кода (см.
`docs/fix-prompts/07-visual.md`, `07-visual.md`'s собственное предупреждение об этом).

**Зона.** `src/DesktopOrganizer.UI/Controls/PanelChrome.cs`, `Resources/**`, `Views/ChildWindow.xaml`
(визуальные части — header, item template visuals), плюс два точечных, аддитивных выхода за
формально перечисленную зону, обоснованных ниже: `Theming/*.cs` (источник самих токенов/контрастной
математики — не Resources-файл, но тот же логический слой) и `Behaviors/RollUpBehavior.cs` (единственное
место, запускающее анимацию на живой панели — без него «reduced motion» из задания нечего реализовывать).
**НЕ тронуто**, как и предписано: `PanelMetrics.HeaderHeight` (32), `PanelMetrics.ContentLeftInset` (8),
`GridMetrics`/`AdaptiveGridLayout`/`AdaptiveIconGridPanel` (icon pipeline, P08 идёт параллельно),
`PanelDragMoveBehavior`/resize-контроллеры, `ChildWindow.xaml.cs` логика, persistence/схема JSON.

---

## 1. Border radius: 6 → 8 DIP, с явным clip

### Решение и обоснование числа

`Radius.Panel` (`Resources/Styles/Spacing.xaml`) поднят с `6` до `8` DIP.

| Проверка | Значение | Итог |
|---|---|---|
| Доля `PanelMetrics.HeaderHeight` (32) | `8/32 = 0.25` | заголовок остаётся прямоугольной полосой, не «таблеткой» |
| Доля `WorkspaceGridMetrics.MinWidthSpanExpanded` (96 DIP, ADR-028) | `8/96 ≈ 0.083` | даже самая узкая разрешённая `GridAligned`-панель сохраняет прямой край |
| Доля `GridMetrics.MinPanelSize` (120 DIP, жёсткий WPF-флор) | `8/120 ≈ 0.067` | то же для `LegacyFreeform` |

`8` выбран не как часть workspace-сетки (ADR-028/029 явно фиксируют, что workspace- и icon-сетки
«namely несоизмеримы» — эта величина косметическая, без snap-семантики), а как уже нагруженное смыслом
круглое число в этой кодовой базе (`WorkspaceGridMetrics.UnitSize = 8`), удобное для не изобретения
произвольной новой константы.

### Precondition — clip (закрывает риск clipping из задания)

До этого прохода `Border` красил скруглённым только собственные `Background`/`BorderBrush` —
дочерний контент (`ListBox`'s скроллбар, selection-рамка вплотную к краю) не клипуется под ту же форму
автоматически. При `CornerRadius=6` это было на грани заметности; на `8` стало бы видимым дефектом.
Это и есть причина, по которой Фаза 5 (`Spacing.xaml` D-1) сознательно НЕ трогала этот токен — «нет DoD
на изменение радиуса, риск непроверенной регрессии». P07 добавляет DoD и снимает precondition:

- **Новый `Converters/RoundedRectClipConverter.cs`** (`IMultiValueConverter`) строит `RectangleGeometry`
  из `(ActualWidth, ActualHeight, CornerRadius)`.
- Забинжен на `PART_Chrome.Clip` (`Resources/Styles/Panels.xaml`) через `RelativeSource Self` — клипует
  СВОЙ собственный прямоугольник, не размер `TemplatedParent`.
- Тот же «никогда не бросает» контракт, что у остальных Ambient Accent конвертеров: некорректный/ещё не
  измеренный вход (`width/height <= 0`, `NaN`, `Infinity`, не тот тип) → `Binding.DoNothing` (оставляет
  `Clip` как есть — изначально `null`/unclipped), а не вырожденная пустая геометрия, которая спрятала бы
  всю панель.
- `RectangleGeometry` поддерживает только единый `(RadiusX, RadiusY)` — читается `CornerRadius.TopLeft`;
  все потребители `Radius.Panel` в этой кодовой базе объявляют равномерный `CornerRadius`, потери нет.

### Hit test — MAJOR-1 найден и исправлен в fix-cycle 1 (см. §12)

**Исходное утверждение ниже было НЕТОЧНЫМ** (fix-cycle 1, code-quality-reviewer) — оставлено
зачёркнуто-по-смыслу для истории, исправленная версия следует за ним:

- ~~`ResizeOverlay` (`Views/ChildWindow.xaml`) — **сосед** `PanelChrome` в общем `Grid`, не потомок.
  `UIElement.Clip` действует только на подписанный элемент и его собственное поддерево → физически не
  может достать WPF `Thumb`-хендлы.~~ Эта часть (про сам WPF-scoping `Clip`) остаётся верной как
  утверждение о WPF-внутреннем hit-test дереве, но из неё сделан НЕВЕРНЫЙ вывод «значит, риска нет».
- Нативный путь (`NativeHitTestMapper.Map`, ADR-022) классифицирует точку по прямоугольным
  `GetWindowRect`/border-strip геометриям — понятия «радиус скругления» в этом коде нет вообще (это
  утверждение верно и не переоткрывается).
- ~~`Clip` на `PART_Chrome` влияет на hit-testing только для того, что ВНУТРИ него (заголовок/контент):
  клик по «отрезанному» скруглённому уголку корректно становится промахом (WYSIWYG) — несколько
  треугольных пикселей на 4 угла, пренебрежимо рядом с полноразмерной зоной drag заголовка (32×ширина).~~
  **Неверно: «пренебрежимо» не учитывало отдельный, реальный механизм.**

**Исправленное объяснение.** Окно панели несёт `AllowsTransparency="True"` — слоистое (`WS_EX_LAYERED`)
окно, чей мышиный hit-test на уровне ОС решается по ФАКТИЧЕСКОЙ скомпонованной альфе пикселя, полностью
независимо от WPF-дерева хит-теста. Пиксель с альфой 0 в итоговом композите — а это ровно то, чем всегда
является квадрат угла СНАРУЖИ дуги скругления `Border`'а (при любом `CornerRadius > 0`, вне зависимости
от того, применён ли `Clip` этим проходом вообще — `Border.Background` и так рисует скруглённую форму
нативно) — уходит клику напрямую под окно (на рабочий стол), и WPF message loop этого окна вообще не
видит события. Это касается и самого `Thumb`-хендла в том же месте: его `Background="Transparent"`
(`Controls.xaml`'s `ResizeHandleStyle`) тоже не красит там ничего непрозрачного. Площадь этой всегда-
прозрачной «линзы» — `r² · (1 - π/4)` ≈ 13.7 DIP² при `r=8` — не зависит от размера resize-grip'а, но
ВЫРОСЛА при расширении `Radius.Panel` 6→8 в этом же проходе (была ≈7.7 DIP² при `r=6`). Внутри
ПРЕЖНЕГО 6×6 grip'а эта линза занимала ≈38% его площади. **Фикс:** `ResizeOverlay`'s grip расширен
6→8 DIP (`PanelMetrics.ResizeGripThickness`, единый источник правды с
`NativePanelResizeController.BorderDip`) — линза не уменьшилась, но её доля от площади увеличенного
grip'а упала до ≈21%, оставляя бо́льшую часть диагонального resize-жеста над всё ещё непрозрачным
пикселем. `RoundedRectClipConverter.cs`/`Panels.xaml`/ADR-025 Amendment переформулированы (убрано
«физически не может» применительно к ЭТОМУ риску — WPF-scoping утверждение остаётся верным САМО ПО
СЕБЕ, но не покрывает OS-уровневый alpha-click-through).

### Никакого Blur/DropShadow (ADR-025 R-8)

`Border.Clip` — векторная геометрия, не `System.Windows.Media.Effects.Effect` (`DropShadowEffect`/
`BlurEffect`, явно запрещённые R-8 для живых десктоп-панелей — они требуют промежуточного render target/
софт-фоллбэка). Добавленный `Clip` — тот же класс стоимости, что уже существующий `CornerRadius` на
`Background`/`BorderBrush`, не новая категория рендер-затрат.

---

## 2. Header: категория/icon, controls, keyboard-доступные tooltips

- **Категория/icon слева «если данные есть».** У `ChildWindowModel` сегодня нет понятия «категория» —
  `PanelChrome.SemanticIcon` уже существует (Фаза 5), но остаётся **reserved, без потребителя** (см. его
  doc-comment). Добавление реального понятия категории — это модель данных (services/persistence слой,
  не «визуальный полишинг» этого этапа) и рискует конфликтовать с параллельной работой над схемой
  (ADR-028/029 v5). Не введено в этом проходе — данных нет, слот зарезервирован как был.
- **Controls muted idle / чёткие на hover.** Уже реализовано Фазой 5 (`InteractionVisualState.ChromeOpacityFor`,
  0.45 idle → 1.0 hover/dragging/resizing) — не тронуто, регрессии не внесены (см. тест-лог).
- **Keyboard-доступные tooltips.** Добавлен `AutomationProperties.Name` на все 4 кнопки заголовка
  (Sort/View/RollUp/Overflow, `Views/ChildWindow.xaml`), забинженный на тот же источник, что их
  `ToolTip` (никогда не задублированный литерал — оба меняются вместе). До этого их доступное имя для UI
  Automation/скринридера было пустым (только Segoe MDL2 глиф, без текста). Панель несёт
  `WS_EX_NOACTIVATE` (ADR-013 R1) — обычная клавиатурная фокусировка кнопок остаётся непроверенным
  риском того же класса, что и раньше; `AutomationProperties.Name` полезен независимо от фокуса для
  клиентов UI Automation (Narrator, Accessibility Insights) и не расширяет и не сужает этот риск.
  Focus visual — стандартный WPF fallback не переопределён (сознательно, не тронут).

---

## 3. Подписи элементов

| Требование | Grid mode (`GridViewTemplate`) | List mode (`ListViewTemplate`) |
|---|---|---|
| 2 строки + ellipsis | Уже было (Фаза до P07): `TextWrapping="Wrap"`, `Height="32"`, `TextTrimming="CharacterEllipsis"` — не тронуто | **Исправлено в fix-cycle 1** (UX-P2) — см. ниже |
| Tooltip полного пути | `ToolTip="{Binding Path}"` на контейнере — уже было | То же — уже было |
| Отсутствие картинки не сжимает слот | `Grid Height="{Binding ...IconSize}"` wrapper — уже было (ui-polishing-1 §3.6) | N/A (иконка фиксирована 16×16, всегда занимает место) |
| Item hover/selected frame | `PanelItemContainerStyle` (`Panels.xaml`) — общий для обоих режимов, лёгкая рамка + accent-fill на выделении, не «тяжёлая карточка» — не тронуто | То же |
| Consistent padding | `GridMetrics.ItemMargin = 6` (5px margin + 1px selection border, ADR-015) — не тронуто, ширина слота не менялась | N/A |

**Исправлено в fix-cycle 1 (UX-P2): List-mode текст теперь обрезается эллипсисом.** Первоначально этот
проход сознательно НЕ чинил это (см. историю ниже) — `code-quality-reviewer` пересмотрел это решение и
затребовал структурную правку. `ListViewTemplate`'s item root заменён `StackPanel Orientation="Horizontal"`
→ `Grid` с колонками `Auto` (иконка 16×16) / `*` (текст) — `StackPanel` всегда измеряет детей вдоль оси
стека с `availableSize = ∞`, поэтому просто добавленный `TextTrimming="CharacterEllipsis"` не имел бы
эффекта (проверено ранее и подтверждено регрессионным тестом ниже), а `Grid`'s звёздная колонка получает
реальную (конечную) ширину на Arrange даже когда Measure видела ту же бесконечность — это и позволяет
`TextTrimming` реально обрезать. `FileItemRootStyle`'s `TargetType` расширен `StackPanel` → `Panel`
(общий базовый класс), чтобы тот же стиль работал и для нового `Grid` (List mode), и для старого
`StackPanel` (Grid mode, не тронут). `Behaviors/ListReorderGeometry.cs` — не тронут и не затронут:
работает по границам `ListBoxItem`-контейнера, не по внутренней структуре DataTemplate (проверено
статическим анализом, см. §12). Regression pin —
`ResourceDictionaryMergeTests.ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth`
(STA, реальный `ListViewTemplate`/`ItemTemplate`, длинное `DisplayName`, узкая ширина) — падал на СТАРОМ
шаблоне (проверено вручную реверсом перед фиксом), проходит на новом.

---

## 4. Contrast — WCAG-калькулятор и аудит реальных токенов

### Калькулятор

Новый `Theming/ContrastCalculator.cs` — чистые функции по формулам WCAG 2.x:
`RelativeLuminance`, `ContrastRatio` (симметричный, `[1.0, 21.0]`), `CompositeOverOpaque` (Porter-Duff
"source-over" для непрозрачного получателя — обои всегда непрозрачны), `ContrastAgainstComposited`
(композиция обоих шагов). Покрыт `ContrastCalculatorTests` — эталонные значения самого WCAG (чёрное на
белом = 21:1, идентичные цвета = 1:1, порядок аргументов не важен).

Цели (заданы в задании): **≥4.5:1** для подписей (`Color.Text.Primary`/`Secondary`), **≥3:1** для
иконок-кнопок.

### Таблица 1 — дефолтная непрозрачность (`SurfaceOpacity = 0.8`, `ChildWindowModel`'s дефолт), Accent = Neutral

| Style | Wallpaper | Composited | vs Text.Primary (≥4.5) | vs Text.Secondary (≥3.0) |
|---|---|---|---|---|
| Solid | Light `#F0F0F0` | `#484848` (a=204) | 6.73 **PASS** | 3.94 **PASS** |
| Solid | Dark `#101010` | `#1B1B1B` (a=204) | 12.68 **PASS** | 7.41 **PASS** |
| Ambient | Light `#F0F0F0` | `#4D4E4E` (a=204) | 6.15 **PASS** | 3.59 **PASS** |
| Ambient | Dark `#101010` | `#202122` (a=204) | 11.87 **PASS** | 6.94 **PASS** |
| Glass | Light `#F0F0F0` | `#8E8F8F` (a=122) | 2.39 **FAIL** | 1.40 **FAIL** |
| Glass | Dark `#101010` | `#1A1A1B` (a=122) | 12.80 **PASS** | 7.49 **PASS** |

### Таблица 2 — минимальная непрозрачность (`SurfaceComposer.MinSurfaceOpacity = 0.35`, сегодняшний пол слайдера)

| Style | Wallpaper | Composited | vs Text.Primary (≥4.5) | vs Text.Secondary (≥3.0) |
|---|---|---|---|---|
| Solid | Light | `#A7A7A7` (a=89) | 1.77 **FAIL** | 1.04 **FAIL** |
| Solid | Dark | `#151515` (a=89) | 13.44 **PASS** | 7.86 **PASS** |
| Ambient | Light | `#A9A9A9` (a=89) | 1.73 **FAIL** | 1.01 **FAIL** |
| Ambient | Dark | `#171718` (a=89) | 13.19 **PASS** | 7.71 **PASS** |
| Glass | Light | `#A9A9A9` (a=89) | 1.73 **FAIL** | 1.01 **FAIL** |
| Glass | Dark | `#171718` (a=89) | 13.19 **PASS** | 7.71 **PASS** |

### Таблица 3 — Glass «потолок» (`SurfaceOpacity=1.0`, максимум для Glass), все 7 акцентов, светлые обои

| Accent | Composited | vs Text.Primary (≥4.5) | vs Text.Secondary (≥3.0) |
|---|---|---|---|
| Neutral | `#767677` (a=153) | 3.34 **FAIL** | 1.95 **FAIL** |
| Blue | `#73777C` (a=153) | 3.32 **FAIL** | 1.94 **FAIL** |
| Violet | `#79747C` (a=153) | 3.36 **FAIL** | 1.96 **FAIL** |
| Amber | `#7C7871` (a=153) | 3.23 **FAIL** | 1.89 **FAIL** |
| Green | `#727A75` (a=153) | 3.25 **FAIL** | 1.90 **FAIL** |
| Cyan | `#71797B` (a=153) | 3.27 **FAIL** | 1.91 **FAIL** |
| Coral | `#7C7676` (a=153) | 3.28 **FAIL** | 1.92 **FAIL** |

**Вывод: `PanelSurfaceStyle.Glass` структурно не может достичь 4.5:1 на светлых обоях ни при какой
непрозрачности** — `GlassOpacityFactor=0.60` жёстко ограничивает альфу сверху. Это прямое, уже
задокументированное (R-8, «Glass ≠ acrylic») следствие того, что Glass обязан быть «более прозрачным» —
понижение потолка непрозрачности лишило бы стиль смысла. **Не исправлено в этом проходе.**

### Таблица 4 — worst-case по всем 7 акцентам (Solid/Ambient), светлые обои, разные кандидаты пола

| Floor opacity | Worst vs Text.Primary | Worst vs Text.Secondary |
|---|---|---|
| 0.35 (сегодня) | 1.70 **FAIL** | 1.00 **FAIL** |
| 0.60 | 3.23 **FAIL** | 1.89 **FAIL** |
| 0.70 | 4.32 **FAIL** | 2.53 **FAIL** |
| 0.72 | 4.60 **PASS** | 2.69 **FAIL** |
| 0.78 | 5.53 **PASS** | 3.23 **PASS** |
| 0.80 (= сегодняшний дефолт `SurfaceOpacity`) | 5.88 **PASS** | 3.44 **PASS** |

**Вывод: сегодняшний `MinSurfaceOpacity=0.35` даёт ≈1.7:1 на светлых обоях для ЛЮБОГО стиля** —
далеко ниже цели. Совпадение: поднятие пола ровно до `0.80` (текущего дефолта новых панелей) убрало бы
проблему с запасом *и* не изменило бы поведение панелей, никогда не трогавших слайдер. **Не исправлено в
этом проходе** — см. «Открытые вопросы» ниже, почему это решение не принято здесь unilaterally.

### Таблица 5 — header-кнопка (иконка, `Text.Secondary`) поверх Ambient@0.8/Neutral, idle vs hover

**Исправлено в fix-cycle 1 (MAJOR-3, см. §12).** Было (до фикса, `Color.Overlay.Hover = #1AFFFFFF`
безусловно):

| Wallpaper | Idle | Hover (старый, всегда-осветляющий wash) |
|---|---|---|
| Light `#F0F0F0` | 3.59 **PASS** | 2.72 **FAIL** |
| Dark `#101010` | 6.94 **PASS** | 5.06 **PASS** |

Стало (`Theming/HoverOverlayPolicy.cs` выбирает затемняющий/осветляющий wash по relative luminance
скомпонованной поверхности — при её текущем значении для Ambient@0.8/Neutral это всегда «тёмная»
ветка, `HoverOverlayPolicy.DarkenWash`, тот же альфа-канал `0x1A`, но чёрный, а не белый):

| Wallpaper | Idle | Hover (новый, направленный wash) |
|---|---|---|
| Light `#F0F0F0` | 3.59 **PASS** | **4.08 PASS** (было 2.72 FAIL) |
| Dark `#101010` | 6.94 **PASS** | **7.19 PASS** (было 5.06 PASS) |

Затемняющий wash на светлых обоях не просто убирает регрессию — hover теперь ЛУЧШЕ читаем, чем idle
(отдаляется от среднесерой иконки, а не приближается). На тёмных обоях эффект в ту же сторону —
затемнение уже тёмного фона тоже увеличивает контраст с более светлой иконкой, а не уменьшает, как было
у старого wash'а. Пины — `AmbientAccentContrastAuditTests.HeaderButtonHoverOverlay_OnLightWallpaper_MeetsIconContrastTarget`/
`_OnDarkWallpaper_StillPassesIconContrast`.

### Таблица 6 — High Contrast

`SurfaceComposer.ComposeSurface(..., highContrast: true)` принудительно даёт непрозрачную нейтральную
заливку (альфа=255) независимо от стиля/непрозрачности — 12.27:1 против `Text.Primary`, 7.18:1 против
`Text.Secondary`, оба PASS с большим запасом. Реальные HC-цвета — `SystemColors.*`, выбранные
пользовательской темой ОС (`Colors.HighContrast.xaml`, D-12) — контраст там гарантирует сама Windows, не
токены приложения; этот проход подтверждает лишь, что собственная ветка `highContrast` не вносит
регрессию (тест `HighContrastMode_ForcesOpaqueNeutralSurface_AlwaysPassesTextContrast`).

### Открытые вопросы контраста (не решены этим проходом, флаг для оркестратора/следующего ревью)

**Вопрос 1 — поднять `SurfaceComposer.MinSurfaceOpacity` (0.35 → 0.80)?**
- **A.** Поднять до 0.80 (= дефолт новых панелей). Убирает находку Таблицы 2/4 с запасом. Риск: молча
  меняет рендер уже сохранённых панелей, где пользователь осознанно выставил непрозрачность ниже 0.80 —
  нарушает `AGENT_CONTRACT.md` «не перезаписывай пользовательские изменения», хотя и в пользу
  читаемости. Также ломает 2 существующих теста (`SurfaceComposerTests.AlphaFromOpacity_ProducesRoundedByteAlpha`
  жёстко проверяет `0.35→89`; `ClampOpacity_FiniteInput_ClampsToRange` проверяет `0.5→0.5` без изменений)
  — потребовалась бы их осознанная правка, не просто побочный эффект.
- **B (рекомендовано для решения оркестратором/владельцем продукта).** Оставить как есть, зафиксировать
  находку тестом-пином (`AmbientAccentContrastAuditTests.KnownGap_MinSurfaceOpacityFloor_*`, сделано) и
  документом (сделано), вынести решение в отдельный, явно поименованный тикет — не решать в рамках
  «визуального полишинга» без явного запроса на изменение персистентного поведения.
- **C.** Ограничить слайдер в UI (не в самой математике) — визуально не даёт пользователю выставить
  значение ниже нового, более высокого практического пола, оставляя `SurfaceComposer` как есть.
  Не реализовано (правка `MainWindow.xaml`/окна настроек — вне зоны этого прохода per `07-visual.md`).

**Вопрос 2 — hover-wash иконок на светлых обоях (Таблица 5). РЕШЕНО в fix-cycle 1 (вариант A).**
Оркестратор выбрал вариант A: `Theming/HoverOverlayPolicy.cs` (чистая функция) +
`Converters/HoverOverlayBrushConverter.cs` (MultiBinding поверх `SurfaceComposer`/`ContrastCalculator`,
без нового «framework») выбирают затемняющий или осветляющий wash по relative luminance композированной
(без учёта обоев — они рантайму не видны, см. `HoverOverlayPolicy`'s собственные remarks) поверхности.
High Contrast passthrough сохранён (`Colors.HighContrast.xaml`'s `SystemColors.ControlLightColor`
по-прежнему используется под HC — конвертер получает уже резолвленный `Brush.Overlay.Hover` четвёртым
значением и возвращает его как есть, если `highContrast==true`, не пересчитывая). Область фикса — только
header-кнопки (Sort/View/RollUp/Overflow, `Controls.xaml`); `Panels.xaml`'s `PanelItemContainerStyle`
(hover иконок-плиток файлов в теле панели) сознательно не тронут — другая, не измеренная в этом аудите
поверхность. См. §12.

**Вопрос 3 — Glass на светлых обоях (Таблица 3). Заметка добавлена в fix-cycle 1 (вариант C).**
- Не вопрос выбора — фиксированное следствие уже принятого `GlassOpacityFactor` (R-8). Токены Glass не
  менялись. Реализовано: usability-подсказка рядом с выбором Surface style в окне настроек
  (`Views/MainWindow.xaml`, «Glass reads best on dark wallpapers; for light wallpapers, Solid or Ambient
  stay more readable.») + эта строка в документации.

---

## 5. High Contrast и Reduced Motion

### High Contrast — подтверждено, не менялось

`Colors.HighContrast.xaml`/`HighContrastState` (Фаза 5, D-12) уже покрывают все токены поверхности/
текста/границ/overlay/selection/sidebar/settings — не тронуто этим проходом. Контраст подтверждён
аудитом (Таблица 6). Известное, уже задокументированное ограничение («live-переключение HC не
подхватывается без рестарта») не переоткрыто.

### Reduced motion — новое в этом проходе

**Решение.** `Theming/MotionState.cs` (новый) — тот же паттерн, что `HighContrastState`: один статический
`bool AnimationsEnabled` (дефолт `true`), выставляется один раз при старте (`App.xaml.cs`'s
`LoadResourceDictionaries()`, рядом с `HighContrastState.IsActive`) из
`SystemParameters.ClientAreaAnimation` (Win32 `SPI_GETCLIENTAREAANIMATION` — переключатель Windows
"Animation effects", Settings → Accessibility → Visual effects).

`Behaviors/RollUpBehavior.ApplyRollUp` — единственное место в проекте, запускающее
`DoubleAnimation` на живой десктоп-панели (roll-up/roll-down, 150 мс). При
`MotionState.AnimationsEnabled == false` метод пропускает `DoubleAnimation` и применяет целевую высоту
синхронно (`BeginAnimation(Window.HeightProperty, null)` — снимает возможную незавершённую предыдущую
анимацию — затем прямое `Height = toHeight`). Остальной конвейер метода (collapse `ContentRow`, видимость
`FileListHost`, `IResizeAffordance.SetEnabled`, инкремент `_rollUpGeneration` против гонки с предыдущим
жестом — M-6) выполняется одинаково в обеих ветках, ДО развилки — ранний `return` в reduced-motion ветке
не пропускает ни один из этих шагов.

Live-переключение системной настройки во время работы приложения не подхватывается — тот же осознанный
минимум «read once at startup», что уже принят для `HighContrastState` (D-12).

---

## 6. Collapsed panel

Не менялось (уже корректно до этого прохода): `PanelChrome.IsRolledUp` оставляет заголовок (title,
accent-индикатор на всю высоту `RowSpan=2`, header actions) полностью видимым и интерактивным — сворачивается
только `ContentRow` (`Views/ChildWindow.xaml`). Никаких перекрывающихся hot zones: `ResizeOverlay`
отключается через `IResizeAffordance.SetEnabled(false)` при сворачивании (существующий инвариант,
`RollUpBehavior`/R-9), header buttons остаются единственной интерактивной зоной. Border radius (см. §1)
применяется идентично в свёрнутом и развёрнутом состоянии — `PART_Chrome` всегда спанит обе строки.

---

## 7. Performance / WorkerW

Ни `DropShadowEffect`, ни `BlurEffect`, ни любой другой `System.Windows.Media.Effects.Effect` не
добавлены — единственное новое рендер-средство, `Border.Clip` (`RectangleGeometry`), явление того же
класса, что уже существующий `CornerRadius`-рендеринг фона/рамки, не Effect-конвейер (см. §1). Reduced
motion (§5) СНИЖАЕТ, не повышает, нагрузку на композитор при выключенной в ОС анимации. Никаких новых
`Topmost`/`SetWindowPos`/`z-order` вызовов — ADR-019/020 инварианты не затронуты.

---

## 8. Token values — сводка

| Токен | Было | Стало | Файл |
|---|---|---|---|
| `Radius.Panel` | `6` | `8` | `Resources/Styles/Spacing.xaml` |
| `Radius.Control` | `3` | `3` (не менялось) | `Resources/Styles/Spacing.xaml` |
| `Radius.Popup` | `4` | `4` (не менялось) | `Resources/Styles/Spacing.xaml` |
| `RoundedRectClipConverter` | — | новый | `Converters/RoundedRectClipConverter.cs` |
| `PART_Chrome.Clip` | не задан | `MultiBinding` → `RoundedRectClipConverter` | `Resources/Styles/Panels.xaml` |
| `AutomationProperties.Name` (4 кнопки заголовка) | не задан | забинжен на тот же источник, что `ToolTip` | `Views/ChildWindow.xaml` |
| `ContrastCalculator` | — | новый (чистые функции, без состояния) | `Theming/ContrastCalculator.cs` |
| `MotionState.AnimationsEnabled` | — | новый (дефолт `true`, startup-only write) | `Theming/MotionState.cs` |
| `RollUpBehavior.ApplyRollUp` | всегда анимирует | пропускает анимацию при `!MotionState.AnimationsEnabled` | `Behaviors/RollUpBehavior.cs` |

### Fix-cycle 1 (MAJOR-1/MAJOR-3/UX-P2/NIT) — новые/изменённые токены

| Токен | Было | Стало | Файл |
|---|---|---|---|
| `PanelMetrics.ResizeGripThickness` | — (два независимых литерала `6d`) | новый, `8d` — единый источник правды | `Models/PanelMetrics.cs` |
| `ResizeOverlay`'s corner/edge `ColumnDefinition`/`RowDefinition` | `6` (литерал) | `{x:Static uimodels:PanelMetrics.ResizeGripThicknessLength}` | `Views/ChildWindow.xaml` |
| `NativePanelResizeController.BorderDip` | `6d` (независимый литерал) | `PanelMetrics.ResizeGripThickness` (const-ref) | `Behaviors/NativePanelResizeController.cs` |
| `HoverOverlayPolicy` | — | новый (чистая функция, `DarkenWash`/`LightenWash`, порог 0.5) | `Theming/HoverOverlayPolicy.cs` |
| `HoverOverlayBrushConverter` | — | новый (заменяет статический `Brush.Overlay.Hover` для 3 header-кнопок) | `Converters/HoverOverlayBrushConverter.cs` |
| `PanelHeaderButtonStyle`/`PanelHeaderToggleButtonStyle`/`Control.Button.Icon`'s hover `Setter` | `StaticResource Brush.Overlay.Hover` | `MultiBinding` → `HoverOverlayBrush` (+ HC passthrough) | `Resources/Styles/Controls.xaml` |
| `ListViewTemplate`'s item root | `StackPanel Orientation="Horizontal"` | `Grid` (`Auto`/`*` columns), `TextTrimming="CharacterEllipsis"` на label | `Resources/Templates.xaml` |
| `FileItemRootStyle`'s `TargetType` | `StackPanel` | `Panel` (общий базовый класс для Grid/List item root) | `Resources/Templates.xaml` |
| `AutomationProperties.Name` (окно панели) | не задан | `{Binding Model.Title}` (то же, что `Title`) | `Views/ChildWindow.xaml` |
| `BoolToGlyphConverter`'s tooltip-текст | `"Expand"`/`"Collapse"` | `"Expand panel"`/`"Collapse panel"` | `Converters/BoolToGlyphConverter.cs` |
| Surface style hint (окно настроек) | нет | `TextBlock` под `ComboBox`, «Glass reads best on dark wallpapers…» | `Views/MainWindow.xaml` |

**Не изменено (явно, по заданию):** `PanelMetrics.HeaderHeight` (32), `PanelMetrics.ContentLeftInset` (8),
`GridMetrics.*`, `AdaptiveGridLayout.*`, `SurfaceComposer.MinSurfaceOpacity` (0.35 — долг `DO-021`,
`docs/Refactoring/BACKLOG.md`/`docs/ROADMAP.md`), `Color.Overlay.Hover`'s собственное XAML-значение
(осталось `#1AFFFFFF` — теперь используется как fallback ТОЛЬКО под High Contrast и напрямую всеми
консьюмерами кроме header-кнопок, см. Вопрос 2), любые persistence/схема-JSON поля.

### Fix-cycle 2 (MAJOR-A/MAJOR-B/MINOR/NIT) — новые/изменённые токены

| Токен | Было (fix-cycle 1) | Стало (fix-cycle 2) | Файл |
|---|---|---|---|
| `PanelMetrics.ResizeGripThickness` | единый `8d` для угла И прямого ребра | **удалён**, разделён на `ResizeCornerThickness`/`ResizeEdgeThickness` ниже | `Models/PanelMetrics.cs` |
| `PanelMetrics.ResizeCornerThickness` | — | новый, `8d` (= прежний `ResizeGripThickness`, только для 4 угловых `Thumb`) | `Models/PanelMetrics.cs` |
| `PanelMetrics.ResizeEdgeThickness` | — | новый, `6d` (восстановлен пре-fix-cycle-1 прямой-край размер) | `Models/PanelMetrics.cs` |
| `ResizeOverlay`'s угловые `Thumb` | делили `ColumnDefinition`/`RowDefinition` с прямыми рёбрами (единая ширина 8) | явные `Width`/`Height="{x:Static ...ResizeCornerThickness}"` + угловой `Horizontal`/`VerticalAlignment`, оверфлоу поверх 6-DIP ячейки | `Views/ChildWindow.xaml` |
| `ResizeOverlay`'s прямые `Thumb` (Top/Bottom/Left/Right) | ячейка `ResizeGripThickness` (8) | ячейка `ResizeEdgeThicknessLength` (6) | `Views/ChildWindow.xaml` |
| `NativeHitTestMapper.Map` | один параметр `borderDip` | два параметра `edgeDip`/`cornerDip`, угол проверяется первым (приоритет) | `Interaction/Native/NativeHitTestMapper.cs` |
| `NativePanelResizeController.BorderDip` | один константа | `EdgeDip`/`CornerDip`, обе читают `PanelMetrics.ResizeEdgeThickness`/`ResizeCornerThickness` | `Behaviors/NativePanelResizeController.cs` |
| `ListViewTemplate`'s `ListBox` | без `ScrollViewer.HorizontalScrollBarVisibility` (дефолт `Auto`) | `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` (как у `GridViewTemplate`) | `Resources/Templates.xaml` |
| `HoverOverlayPolicy.For`'s параметр | `compositedSurfaceLuminance` | переименован `ownSurfaceLuminance` + уточнён doc-comment (own-tint, не композит с обоями) | `Theming/HoverOverlayPolicy.cs` |
| `HoverOverlayBrushConverter.MaxCachedBrushes`'s doc-comment | «по той же причине, что `PanelSurfaceBrushConverter`» (неточно — та причина не применима) | уточнено: defensive-only, реальное пространство ключей 21 (7×3) | `Converters/HoverOverlayBrushConverter.cs` |
| Surface style `ComboBox` (окно настроек) | без `AutomationProperties` | `LabeledBy`/`HelpText`, оба забинжены на существующие `TextBlock`, не задублированный литерал | `Views/MainWindow.xaml` |

**Не изменено (явно, по заданию):** `PanelMetrics.HeaderButtonSize` (28), `PanelMetrics.HeaderHeight` (32),
`Views/ChildWindow.xaml.cs` (никакой partial/code-behind правки), `ListReorderGeometry.cs`,
`AdaptiveIconGridPanel`/`GridMetrics`/`AdaptiveGridLayout` (P05 fix-2 идёт параллельно),
`ResizeGestureSession`/`ResizeConstraintsFactory`/`RollUpBehavior`/`WorkspaceGridMetrics` (P04 следом),
`FileListHost`'s margin (правый margin=0 остаётся — правка вне зоны "только `ResizeOverlay` блок"),
persistence/схема JSON.

---

## 9. Usability checklist (manual, Win10/Win11)

- [ ] Border radius 8 DIP на 100/125/150% DPI — нет видимого выхода скроллбара/selection-рамки за
      границу скругления (светлые и тёмные обои).
- [ ] Тот же радиус — уголок панели визуально не «съедает» заметную часть заголовка/контента на самой
      маленькой разрешённой панели (120×120 DIP, `LegacyFreeform`).
- [ ] Resize за угол (WPF `Thumb` путь и нативный `WM_SIZING` путь, если `NativePanelSizing=true`) —
      курсор меняется на resize в 8-DIP угловом квадрате (fix-cycle 1/2, `ResizeCornerThickness`,
      НЕ изменился во fix-cycle 2); диагональный resize из самого угла ловится, а не проваливается на
      рабочий стол (MAJOR-1 — самая узкая, самая рискованная часть угла, ближе всего к 4 пикселям от
      геометрического угла окна). Resize за прямой край (Top/Bottom/Left/Right, НЕ угол) — курсор меняется
      в 6-DIP полосе (fix-cycle 2, `ResizeEdgeThickness`, восстановлено с 8 обратно на 6 — MAJOR-A).
- [ ] **Новое во fix-cycle 2 (MAJOR-A) — header-кнопки vs угловой грип 8×8.** Навести курсор точно на
      верх-правый угол `OverflowButton` (последняя кнопка заголовка, ближе всего к правому краю окна) —
      подтвердить, что клик В ВЕРХНЕМ-ПРАВОМ УГЛУ САМОЙ КНОПКИ (внутри её верхних ~6 DIP и правых ~4 DIP,
      см. `PanelMetricsTests.ResizeEdgeThickness_TopStrip_...`/численный комментарий в
      `PanelMetrics.ResizeCornerThickness`'s doc) может распознаться как resize-жест вместо клика по
      кнопке — это ДОКУМЕНТИРОВАННЫЙ, не устранённый в этом фикс-цикле остаточный оверлап (структурно
      неустраним без изменения `HeaderButtonSize`/`HeaderHeight`/отступа кнопки от края, все три —
      замороженные инварианты этого прохода); клик по ОСТАЛЬНОЙ (большей) части кнопки должен работать
      нормально. Сравнить субъективно с ощущением ДО этого фикса (было хуже — угловой грип покрывал
      верхние ~21% высоты кнопки против ~14% сейчас, см. §11).
- [ ] **Новое во fix-cycle 2 (MAJOR-A) — правая полоса vs вертикальный скроллбар.** При появлении
      вертикального скроллбара (список файлов длиннее видимой области) — подтвердить, что resize-за-
      правый-край по-прежнему возможен НЕ ТОЛЬКО с внешнего (не занятого скроллбаром) края, но и заметно
      легче, чем до фикса: полоса теперь 6 DIP (было 8) — покрывает меньшую часть скроллбара (см.
      `PanelMetricsTests.ResizeEdgeThickness_RightStrip_...` для точной доли). Полностью нулевой оверлап
      НЕ ожидается (потребовал бы правки `FileListHost`'s margin — вне зоны этого фикс-цикла).
- [ ] `AutomationProperties.Name` четырёх кнопок заголовка И самого окна панели (= заголовок панели) —
      проверить Narrator/Accessibility Insights for Windows (имя совпадает с текстом тултипа/заголовком).
- [ ] **Новое во fix-cycle 2 — Surface style hint a11y.** С Narrator/Accessibility Insights: фокус на
      Surface style `ComboBox` в окне настроек озвучивает и метку поля («Surface style»), и usability-
      подсказку про Glass (`AutomationProperties.LabeledBy`/`HelpText`).
- [ ] Roll-up/roll-down при включённой в ОС анимации — визуально не изменилось (150 мс, ease-out).
- [ ] Roll-up/roll-down при ВЫКЛЮЧЕННОЙ в ОС «Animation effects» (Settings → Accessibility → Visual
      effects) — мгновенный переход высоты, без анимации и без визуального «прыжка»/двойного кадра.
- [ ] `PanelSurfaceStyle.Glass` на светлой рабочей поверхности (обои `#F0F0F0`-класса) — субъективно
      подтвердить сниженную читаемость (ожидаемо per Таблица 3, фиксированное следствие
      `GlassOpacityFactor`); подсказка в окне настроек рядом с Surface style (fix-cycle 1) видна и
      читаема.
- [x] ~~Header-кнопка при наведении на светлых обоях/дефолтной `Ambient@0.8`~~ — **исправлено в
      fix-cycle 1** (MAJOR-3): hover теперь ЗАТЕМНЯЕТ, а не осветляет, композированную поверхность —
      контраст 3.59→4.08 (было 3.59→2.72). Manual verify: субъективно подтвердить, что hover
      воспринимается как «чётче», не «более блёкло», на светлых И тёмных обоях.
- [ ] List-mode — длинное имя файла обрезается многоточием (`…`), не вылезает за правую границу панели
      на узкой ширине панели, и НЕ показывает горизонтальный скроллбар списка (fix-cycle 1 UX-P2 структура
      + fix-cycle 2 MAJOR-B `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` — fix-cycle 1's
      собственный regression-тест измерял только сам `DataTemplate`, минуя реальный `ListBox`/
      `ScrollViewer`, и поэтому не поймал этот дефект; честный тест — см.
      `ResourceDictionaryMergeTests.ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow`);
      наведение на обрезанное имя показывает полный путь в tooltip (не менялось).
- [ ] High Contrast (любая из системных HC-тем) — все 4 кнопки заголовка/подписи элементов остаются
      читаемыми (унаследованная проверка Фазы 5, не заново вводимая этим проходом); hover-подсветка
      header-кнопок под HC не изменилась визуально (High Contrast passthrough, fix-cycle 1 — конвертер
      возвращает уже резолвленный `Brush.Overlay.Hover`/`SystemColors.ControlLightColor`, не считает
      свою собственную математику).
- [ ] Collapsed (rolled-up) панель — заголовок и accent-полоса видны, нет перекрывающихся hot zones с
      resize-хендлами (должны быть отключены).

### Инструкция review-скриншотов (100%/150% DPI, светлые/тёмные обои)

1. Выставить масштаб экрана 100%, затем повторить на 150% (Параметры экрана → Масштаб).
2. Установить светлые обои (близко к `#F0F0F0`) и тёмные обои (близко к `#101010`) — два прогона.
3. Создать минимум 3 панели: `PanelSurfaceStyle.Solid`, `Ambient` (дефолт), `Glass`, все с
   `AccentColor = Neutral` (дефолт) и хотя бы одна с ярким акцентом (например, `Amber`).
4. Для каждой — снять скриншот в Idle, Hover (навести на заголовок, не кликать), Dragging
   (зафиксировать кадр во время перетаскивания), Resizing (то же для resize), Rolled-up.
5. Обязательно включить в кадр угол панели крупным планом (100% zoom скриншота, кроп по 40×40 px от
   каждого из 4 углов) — визуальная проверка §1 (clip).
6. Повторить п.4 с ОС-настройкой «Animation effects» выключенной — снять видео (не скриншот) roll-up/
   roll-down, подтвердить отсутствие анимации.
7. Сохранить в `docs/PLANS/assets/workspace-grid-07-visual/` (создать при первой съёмке), имена файлов
   `{dpi}-{wallpaper}-{style}-{state}.png` (например, `150-dark-glass-hover.png`).

---

## 10. Test log

Первый проход (до fix-cycle 1):

```
dotnet build DesktopOrganizer.sln  → Build succeeded, 0 Error(s)
dotnet test DesktopOrganizer.sln --no-build → Passed: 2291, Failed: 0, Skipped: 0
```

Новые тестовые файлы (34 + 13 = фактически посчитано `dotnet test` выше, не отдельно):

- `tests/DesktopOrganizer.Tests/Unit/Theming/ContrastCalculatorTests.cs` — чистая математика (WCAG
  эталонные значения).
- `tests/DesktopOrganizer.Tests/Unit/Theming/AmbientAccentContrastAuditTests.cs` — характеризационные
  тесты над РЕАЛЬНЫМИ токенами (пины трёх находок §4 + regression-пин для проходящих комбинаций).
- `tests/DesktopOrganizer.Tests/Unit/Theming/MotionStateTests.cs` — round-trip сеттера/геттера.
- `tests/DesktopOrganizer.Tests/Unit/Behaviors/RollUpBehaviorMotionTests.cs` — STA (`StaTestRunner`),
  реальный `Window`/`RollUpBehavior`, разница между анимированной и reduced-motion веткой.
- `tests/DesktopOrganizer.Tests/Unit/Converters/RoundedRectClipConverterTests.cs` — геометрия, defensive
  contract (`Binding.DoNothing` на некорректном вводе).

Существующие resource/theming тесты (`ResourceDictionaryMergeTests`,
`PanelChromeTemplate_AccentIndicatorColumn...`, `SurfaceComposerTests`, `HighContrastStateTests`, вся
`AdaptiveGridLayout*`/`AdaptiveIconGridPanel*` серия) — зелёные без единой правки, включая
`ResourceDictionaryMergeTests`, которая реально материализует `PanelChrome`'s шаблон (значит, новый
`Border.Clip`/`RoundedRectClipConverter`/расширенный `Radius.Panel` не ломают XAML-парсинг ни в normal, ни
в high-contrast merge-порядке).

### Fix-cycle 1 (2026-09-22)

```
dotnet build DesktopOrganizer.sln  → Build succeeded, 0 Error(s)
dotnet test DesktopOrganizer.sln --no-build → Passed: 2389, Failed: 0, Skipped: 0
```

Дельта — 98 новых тестов (2389 − 2291), из них 2 пина переписаны на новую формулировку (не в счёте
дельты, `RollUpTests.Convert_*WithTooltipParameter_Returns*`). Новые тестовые файлы:

- `tests/DesktopOrganizer.Tests/Unit/Theming/HoverOverlayPolicyTests.cs` — чистая функция направления
  wash'а, включая `NaN`-defensive ветку.
- `tests/DesktopOrganizer.Tests/Unit/Converters/HoverOverlayBrushConverterTests.cs` — контракт конвертера
  (never-throws, frozen cache, HC passthrough, все 7×3 = 21 комбинаций акцент×стиль дают `DarkenWash`
  сегодня).
- `tests/DesktopOrganizer.Tests/Unit/Models/PanelMetricsTests.cs` (расширен) — `ResizeGripThickness`/
  `ResizeGripThicknessLength` round-trip + геометрический пин площади «мёртвой зоны» угла (≥75% grip'а
  остаётся вне неё).
- `tests/DesktopOrganizer.Tests/Unit/Resources/ResourceDictionaryMergeTests.cs` (расширен) — STA-тест
  `ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth` (падал на дореформенном
  `StackPanel`-шаблоне, подтверждено вручную временным `git stash` реверта перед фиксом).
- `tests/DesktopOrganizer.Tests/Unit/Theming/AmbientAccentContrastAuditTests.cs` (изменён, не новый файл) —
  Glass-потолок теперь `[Theory]` по всем 7 акцентам (было `[Fact]` по одному); header-hover тесты
  переписаны с «known gap» на «meets target» (MAJOR-3); 2 захардкоженных токена (`NeutralSurfaceBase`/
  `AccentNeutral`) заменены реальными константами (`PanelSurfaceBrushConverter.NeutralSurfaceHex`/
  `AccentPalette.Neutral`); неиспользуемый `OverlayHover`-литерал удалён.
- `tests/DesktopOrganizer.Tests/Unit/RollUp/RollUpTests.cs` (изменён) — 2 пина обновлены под новый текст
  тултипа («Expand»/«Collapse» → «Expand panel»/«Collapse panel»).

### Fix-cycle 2 (2026-09-22, ПОСЛЕДНИЙ)

```
dotnet build DesktopOrganizer.sln  → Build succeeded, 0 Error(s)
dotnet test DesktopOrganizer.sln --no-build → Passed: 2631, Failed: 0, Skipped: 0
```

Дельта — 9 новых тестов (2631 − 2622, стартовая база этого прохода). Проверено 5 последовательных полных
прогонов `dotnet test` зелёными подряд (см. ниже про одну наблюдавшуюся несвязанную флейк-неудачу).
Изменённые/новые тестовые файлы:

- `tests/DesktopOrganizer.Tests/Unit/Interaction/Native/NativeHitTestMapperTests.cs` (переписан) — новая
  двухпараметровая (`edgeDip`/`cornerDip`) сигнатура `NativeHitTestMapper.Map`; +4 к счёту (2 новых теста
  на приоритет угла/ребра при разных порогах, остальные — геометрически эквивалентные переименования).
- `tests/DesktopOrganizer.Tests/Unit/Models/PanelMetricsTests.cs` (расширен) — `ResizeGripThickness` →
  `ResizeCornerThickness`/`ResizeEdgeThickness` (раздельные тесты + инвариант "corner >= edge");
  `ResizeCornerThickness_CoversMajorityOfCornerDeadZone` теперь читает `Radius.Panel` из РЕАЛЬНОГО
  скомпилированного XAML-ресурса через `StaTestRunner` (не хардкод-литерал) — единственный тест в этом
  фикс-цикле, коснувшийся `Application.Current`/global test state, ИСПРАВЛЕН после первого прохода
  (см. «Инцидент теста» ниже) так, чтобы не портить ambient-состояние для других фикстур; два новых
  геометрических пина документируют ЧИСЛЕННО уменьшенный (не нулевой) остаточный оверлап
  header-кнопка/угловой+прямой грип и scrollbar/правая полоса — MAJOR-A.
- `tests/DesktopOrganizer.Tests/Unit/Resources/ResourceDictionaryMergeTests.cs` (расширен) — новый
  честный STA-тест `ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow`:
  реальный `ListBox` из `ListViewTemplate` (не только его `ItemTemplate`), минимальный, но структурно
  верный `ControlTemplate` (реальный `ScrollViewer`, `TemplateBindingExtension` на
  `ScrollViewer.HorizontalScrollBarVisibility`, реальный `ItemsPresenter`/`StackPanel`), реальный
  `ItemContainerGenerator.ContainerFromIndex(0)`. Подтверждено вручную (временный revert фикса
  Templates.xaml + диагностический `throw` внутри теста, затем убран): без фикса контейнер измерялся
  в 1576 DIP при доступных 150 (`hbv=Auto`) — тест ловит регрессию по-настоящему, не только по видимости.
  Старый `ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth` НЕ удалён (по заданию)
  — измеряет `DataTemplate`'s корень напрямую, минуя `ListBox`/`ScrollViewer`, поэтому не поймал MAJOR-B;
  оставлен как валидный (но недостаточный сам по себе) пин структуры Grid-колонок.
- `src/DesktopOrganizer.UI/Theming/HoverOverlayPolicy.cs`/`Converters/HoverOverlayBrushConverter.cs` —
  doc-comment-only правки (переименование параметра, уточнение формулировки), НЕ тестовые файлы;
  `HoverOverlayPolicyTests.cs`/`HoverOverlayBrushConverterTests.cs` не менялись (позиционные вызовы,
  не зависят от имени параметра) — оба остались зелёными без единой правки.

**Инцидент теста (обнаружен и исправлен в рамках этого же прохода, не переходит в manual verify).** Первая
версия `PanelMetricsTests.ResizeCornerThickness_CoversMajorityOfCornerDeadZone` делала
`Application.Current ?? new Application()` БЕЗ заполнения `Resources` — при полном прогоне `dotnet test`,
если этот тест оказывался ПЕРВЫМ, кто вообще трогает `Application` в процессе, он навсегда (WPF не даёт
"развоплотить" `Application`) оставлял `Application.Current.Resources` ПУСТЫМ для всех последующих тестов
процесса — сломало 72 несвязанных теста (`ChildWindowViewModelTests` и др.), полагающихся на то, что
ambient `Application.Current.Resources` уже содержит полный merge (`ListViewTemplate` и т.д.), который
`ResourceDictionaryMergeTests`/аналогичные фикстуры обычно готовят первыми. Исправлено: если тест
оказывается владельцем `Application`, он теперь мёржит тот же полный набор из 8 словарей (тот же
normal/non-HC список, что `ResourceDictionaryMergeTests.MergeResourceDictionaries`), а не оставляет
`Resources` пустым. Обнаружено и исправлено ДО передачи на ревью — 5 последовательных полных прогонов
(`dotnet test DesktopOrganizer.sln --no-build`) после фикса зелёные (2631/2631 каждый раз).

**Одна несвязанная наблюдавшаяся флейк-неудача.** В одном из ~7 полных прогонов
`ChildWindowViewModelTests.Portal_WatcherCreatedEvent_NewFileInsertedAtAlphabeticalPosition_NotAppended`
(файл не тронут этим фикс-циклом — `FileSystemWatcher`/`Moq.Raise`-таймингового класса тест) провалился
один раз, прошёл на немедленном повторном прогоне. Не связан ни с одним изменением этого прохода (git diff
подтверждает: `ChildWindowViewModel.cs`/`ChildWindowViewModelTests.cs` не изменялись) — предсуществующая
флейкость, не новая регрессия.

---

## 11. Открытые риски (статус после fix-cycle 2)

1. ~~Q1/Q2 из §4~~ — **Q2 решён fix-cycle 1** (вариант A, hover-wash). **Q1 остаётся сознательно не
   решён** (вариант B, оркестратор) — `MinSurfaceOpacity` floor, теперь `DO-021`
   (`docs/Refactoring/BACKLOG.md`/`docs/ROADMAP.md`), требует отдельного решения владельца продукта
   (персистентность, не чисто визуальное).
2. ~~List-mode ellipsis (§3)~~ — **исправлено fix-cycle 1 структурно, честно протестировано fix-cycle 2**
   (MAJOR-B — `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` + реальный `ListBox`-тест), см. §3/§12/§13.
3. ~~Resize hit-zone единая толщина угол=ребро~~ — **исправлено fix-cycle 2** (MAJOR-A) —
   `ResizeCornerThickness`(8)/`ResizeEdgeThickness`(6) разделены; header-кнопка/scrollbar оверлап
   ЧИСЛЕННО уменьшен, но НЕ обнулён (структурный предел при замороженных `HeaderButtonSize`/
   `HeaderHeight`/`FileListHost` margin — см. §13 finding-таблица и manual verify §9).
4. Border radius/clip/resize-grip — по-прежнему не проверено на реальном экране (нет живого Windows GUI
   в среде разработки); весь §1 (включая исправленное MAJOR-1 объяснение) обоснован статическим анализом
   кода и геометрической математикой (`PanelMetricsTests`), не наблюдением. Это ГЛАВНЫЙ оставшийся
   manual-verify риск этого прохода — сама природа находки (OS-level alpha click-through) физически не
   наблюдаема без живого экрана.
5. `AutomationProperties.Name` — не проверено с реальным screen reader (Narrator) на `WS_EX_NOACTIVATE`-окне;
   тот же непроверенный класс риска, что ADR-013 R1 для клавиатуры. Расширено fix-cycle 1 на само окно
   панели (не только 4 кнопки) — тот же непроверенный статус. Fix-cycle 2 добавило `LabeledBy`/`HelpText`
   на Surface style `ComboBox` (окне настроек) — тот же непроверенный статус.
6. **Новое в fix-cycle 1:** MINOR по device-pixel snap рассинхрону `Border.Clip`/`SnapsToDevicePixels`
   на дробном DPI (125%/150%) — оценено как нетривиальное для безопасного фикса без живого экрана для
   проверки (потребовало бы знать DPI-scale внутри чистого `IMultiValueConverter`, которого у него сегодня
   нет) — оставлено в manual verify по явному escape-пункту исходной находки («если дёшево… иначе
   оставить в manual verify»), не исправлено. Не переоткрыто fix-cycle 2 (вне зоны MAJOR-A/MAJOR-B).
7. **Новое в fix-cycle 1:** MAJOR-3 фикс охватывает только 4 header-кнопки (Sort/View/RollUp/Overflow) —
   `Panels.xaml`'s `PanelItemContainerStyle` (hover иконок-плиток файлов в теле панели) продолжает
   использовать статический `Brush.Overlay.Hover`; не измерялось в этом аудите, не входит в
   scope находки §4-3. Не переоткрыто fix-cycle 2.
8. **Новое в fix-cycle 2 (ПОСЛЕДНИЙ):** MAJOR-A's остаточный (уменьшенный, не нулевой) оверлап
   header-кнопка/угловой грип и правая полоса/scrollbar — структурный предел, а не недосмотр: угловой грип
   ДОЛЖЕН оставаться 8×8 (MAJOR-1's click-through-lens требование), header-кнопка ДОЛЖНА оставаться 28×28
   в 32-DIP заголовке (`HeaderButtonSize`/`HeaderHeight` — заморожены, вне зоны этого фикс-цикла), а
   `FileListHost` НЕ имеет правого margin (правка вне зоны "только `ResizeOverlay` блок"). Полностью нулевой
   оверлап потребовал бы изменения хотя бы одного из этих трёх инвариантов — явно вынесено в открытый вопрос
   для владельца продукта/следующего этапа, а не решено unilaterally здесь. Численно пришпилено тестами
   `PanelMetricsTests.ResizeEdgeThickness_TopStrip_...`/`..._RightStrip_...` (см. §8/§10); manual verify —
   два новых явных пункта в §9.

---

## 12. Fix cycle 1 (2026-09-22) — итерация 1 из макс. 2

Конвейер: `tdd-test-engineer`/`code-quality-reviewer` review первого прохода → решения оркестратора
по 3 открытым вопросам контраста (§4) → этот проход (`wpf-ui-builder`). Ограничение объёма: XAML
визуальных ресурсов, `ChildWindow.xaml` (ResizeOverlay/a11y), `Theming/*`, `Converters/*`,
`MainWindow.xaml` (только Glass-подсказка), `NativeHitTestMapper`/`PanelMetrics` (только толщина grip),
тесты, документация — не тронуты `ChildWindow.xaml.cs`, `ListReorderGeometry.cs`,
`AdaptiveIconGridPanel`/`GridMetrics`/`AdaptiveGridLayout`, persistence/`App.xaml.cs`/
`MainWindowViewModel`, icon pipeline, drag/resize-контроллеры (параллельные fix-циклы других этапов).

### Finding → статус

| Finding | Статус | Реализация |
|---|---|---|
| MAJOR-1 (resize hit-test за скруглённым углом, `AllowsTransparency` click-through) | **Исправлено** | `PanelMetrics.ResizeGripThickness` (8, единый источник с `NativePanelResizeController.BorderDip`); `ResizeOverlay` grip 6→8 DIP; переформулированы `RoundedRectClipConverter.cs`/`Panels.xaml`/ADR-025 (убрано «физически не может» для OS-alpha-click-through риска); геометрический regression-тест площади «мёртвой зоны» |
| MAJOR-2 (3 контрастных дефекта → долг) | **Частично**: §4-3 исправлено (не долг), §4-1 — usability-подсказка (не долг, следствие R-8), §4-2 — заведён долг `DO-021` | См. ниже MAJOR-3/Вопросы 1-3 |
| §4-1 Glass < 4.5:1 на светлых обоях (оркестратор: вариант C) | **Реализовано** | Usability-подсказка в `MainWindow.xaml` рядом с Surface style + строка в §4 «Вопрос 3»; токены Glass не менялись |
| §4-2 `MinSurfaceOpacity=0.35` → ≈1.7:1 (оркестратор: вариант B) | **Долг заведён** | `DO-021` в `docs/Refactoring/BACKLOG.md` и `docs/ROADMAP.md`; тесты-пины не менялись (по-прежнему характеризуют дефект) |
| §4-3 `Color.Overlay.Hover` снижает контраст иконок на hover (оркестратор: вариант A) | **Исправлено** | `Theming/HoverOverlayPolicy.cs` + `Converters/HoverOverlayBrushConverter.cs`; 4 header-кнопки; HC passthrough сохранён; пины теперь ассертят ≥3.0 |
| MINOR: 5 захардкоженных токенов в `AmbientAccentContrastAuditTests` | **Исправлено** | `NeutralSurfaceBase`/`AccentNeutral` теперь читают `PanelSurfaceBrushConverter.NeutralSurfaceHex`/`AccentPalette.Neutral`; `TextPrimary`/`TextSecondary` остаются захардкожены (нет C#-зеркала для этих токенов нигде в продакшен-коде); `OverlayHover` удалён (стал неиспользуемым после MAJOR-3) |
| MINOR: `InRange(3.0,3.6)` тестировал только 1 акцент из «всех 7» | **Исправлено** | `[Theory]`/`MemberData` по `AccentPalette.Neutral` + всем 6 `Presets` |
| MINOR: device-pixel snap рассинхрон `Clip` vs `SnapsToDevicePixels` на дробном DPI | **Оставлено manual verify** | Оценено как небезопасное для фикса без живого экрана (нет DPI-scale внутри чистого `IMultiValueConverter`); задокументировано в §11 п.5 |
| NIT: `RollUpToggleButton`'s "Expand"/"Collapse" вводит в заблуждение относительно `RollUpMenu_Click` | **Исправлено (формулировка)** | Текст → «Expand panel»/«Collapse panel» (`BoolToGlyphConverter.cs`) — сам `RollUpMenu_Click` не переименован (в `ChildWindow.xaml.cs`, параллельный P05 fix, вне зоны); имя объясняется тем, что тот же обработчик разделяют кнопка и пункт контекстного меню, а не тем, что кнопка открывает меню — уточнено в комментарии конвертера |
| NIT: garbled фраза в ADR-025 Amendment | **Исправлено** | См. `adr/ADR-025-ambient-accent-visual-language.md` — убрана фраза про «соседа `PART_AccentIndicator`» |
| NIT: `AutomationProperties.Name` для окна панели и «остальных icon-only кнопок» | **Частично**: окно — исправлено; доп. кнопок не найдено | `ChildWindow.xaml`'s `Window` получил `AutomationProperties.Name="{Binding Model.Title}"`; в кодовой базе только 4 icon-only кнопки заголовка существуют (Sort/View/RollUp/Overflow) — все уже покрыты первым проходом P07; `SystemIconsPanelWindow.xaml` кнопок не содержит |
| UX P2: List-mode подписи без ellipsis | **Исправлено** | `StackPanel` → `Grid` (`Auto`/`*`) в `ListViewTemplate`, `FileItemRootStyle.TargetType` → `Panel`; STA regression-тест (падал на старом шаблоне, подтверждено `git stash` реверсом) |

### Не переоткрыто

ADR-025's «Известные проблемы» (Dragging/Resizing различимость по курсору, мёртвая ветка `"accent"`,
HC-тема title bar настроек) и все решения Фазы 5 (нейтральная поверхность vs заливка категорией,
Glass ≠ acrylic, D-5 лоссивность миграции) — не пересматривались этим циклом, как и предписано
`docs/fix-prompts/AGENT_CONTRACT.md`/`CLAUDE.md`.

---

## 13. Fix cycle 2 (2026-09-22) — итерация 2 из макс. 2 (ПОСЛЕДНИЙ)

Повторное ревью fix-cycle 1's Amendment нашло, что оба MAJOR-находки того прохода («расширить резайз-грип
до 8 DIP») и одну структурную UX-находку («List-ellipsis теперь работает») были реализованы НЕПОЛНО:
MAJOR-1's фикс (единая толщина 8 для угла И прямого края) сам создал новый, ранее не существовавший
регресс (header-кнопки/scrollbar перекрыты шире, чем нужно), а UX-P2's фикс (Grid-колонка вместо
StackPanel) был структурно верным, но НЕ подключён к реальному `ListBox`'s `ScrollViewer` — оставляя
дефект живым в реальном приложении, невидимым только тестам. Ограничение объёма — как в §12, плюс явный
запрет трогать `ChildWindow.xaml.cs`/`PanelDragMoveBehavior`/`App.xaml.cs`/`WindowManager` (P03
параллельно), `AdaptiveIconGridPanel`/`GridMetrics`/`ListReorderGeometry` (P05 fix-2, тот же
`Templates.xaml` файл — правил только `GridViewTemplate`/связанный `ScrollViewer`-биндинг, не
`ListViewTemplate`'s `ListBox`-атрибуты, которые правил этот проход),
`ResizeGestureSession`/`ResizeConstraintsFactory`/`RollUpBehavior`/`WorkspaceGridMetrics` (P04 следом).

### Finding → статус

| Finding | Статус | Реализация |
|---|---|---|
| MAJOR-A (единая толщина 8 DIP для угла И прямого края resize-грипа перекрывает header-кнопки/scrollbar) | **Исправлено** | `PanelMetrics.ResizeGripThickness` разделён на `ResizeCornerThickness`(8)/`ResizeEdgeThickness`(6); `ResizeOverlay`'s 4 угловых `Thumb` получили явные `Width`/`Height`+corner-anchoring alignment (оверфлоу поверх 6-DIP ячейки); `NativeHitTestMapper.Map` теперь принимает пару `(edgeDip, cornerDip)`, угол проверяется первым (приоритет); `NativePanelResizeController.EdgeDip`/`CornerDip` — обе читают `PanelMetrics`; header-кнопка/scrollbar оверлап ЧИСЛЕННО уменьшен (21%→14% высоты кнопки; ~47%→~35% ширины scrollbar), но НЕ обнулён — см. §11 п.8 для честного объяснения структурного предела |
| MAJOR-A geometry test (нет hit-зоны ребра, пересекающей bbox кнопок/scrollbar более чем на N DIP) | **Реализовано, с честной документацией допустимого overlap** | `PanelMetricsTests.ResizeEdgeThickness_TopStrip_OverlapWithHeaderButtonTopMargin_IsReducedFromFixCycle1`/`..._RightStrip_OverlapWithVerticalScrollBar_IsReducedFromFixCycle1` — численно пришпиливают УМЕНЬШЕННЫЙ (не нулевой) остаточный оверлап; литеральный 0 для кнопок признан НЕДОСТИЖИМЫМ в scope этого фикс-цикла (замороженные `HeaderButtonSize`/`HeaderHeight`/угловой грип=8 для MAJOR-1) — задокументировано явно, не молча принято |
| Golden hit-test тесты (`NativeHitTestMapperTests`) | **Обновлены с объяснением** | Полностью переписан doc-comment класса + все существующие тесты (не удалены, геометрически эквивалентно переименованы под новую сигнатуру) + 2 новых теста, специально подобранных так, чтобы отличать двухпороговую реализацию от ЛЮБОЙ однопороговой (ни `uniform-6`, ни `uniform-8` не прошли бы оба новых теста одновременно) |
| `PanelMetricsTests.cs` захардкоженный `radiusPanel = 8d` | **Исправлено** | Тест теперь читает `Radius.Panel` из РЕАЛЬНОГО скомпилированного `Resources/Styles/Spacing.xaml` через `StaTestRunner`, а не из литерала — тот же класс бага (XAML/C# drift), что и весь MAJOR-1/MAJOR-A, устранён и здесь |
| MAJOR-B (List-mode `ScrollViewer.HorizontalScrollBarVisibility` не `Disabled`, ellipsis не работает в реальном приложении) | **Исправлено** | `Resources/Templates.xaml`'s `ListViewTemplate`'s `ListBox` получил `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` (как у `GridViewTemplate`, которое уже имело его) |
| Fix-cycle 1's regression-тест — ложно-зелёный (`LoadContent()` + `Measure` мимо реального `ListBox`) | **Исправлено (новый честный тест добавлен, старый не удалён)** | `ResourceDictionaryMergeTests.ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow` — реальный `ListBox`, минимальный-но-верный `ControlTemplate` (реальный `ScrollViewer` + `TemplateBindingExtension` на ту же attached property + `ItemsPresenter`), реальный `ItemContainerGenerator.ContainerFromIndex(0)`; подтверждено вручную (временный revert + диагностический `throw`), что без фикса контейнер измеряется в 1576 DIP при 150 доступных |
| `ListReorderGeometry`/drag-reorder в List не зависят от горизонтального скролла | **Подтверждено, не переоткрыто** | `ListReorderGeometry.cs` не тронут (вне scope); работает по границам `ListBoxItem`-контейнера (см. его собственные remarks, не переоткрыты этим проходом) — не по внутренней структуре `DataTemplate`/`ScrollViewer`, поэтому `HorizontalScrollBarVisibility="Disabled"` не может повлиять на его логику |
| MINOR: `HoverOverlayBrushConverter.MaxCachedBrushes=512` — мёртвая ветка `Cache.Clear()` | **Исправлено (комментарий, не удаление ветки)** | Doc-comment уточнён: bound defensive-only для ЭТОГО конвертера (реальное пространство ключей — 21, 7 акцентов × 3 стиля), в отличие от `PanelSurfaceBrushConverter`'s одноимённой константы (та растёт вместе с opacity-слайдером) — старый комментарий ошибочно ссылался на чужую причину; сама ветка оставлена как структурный safeguard на будущее расширение ключа |
| `MainWindow.xaml:~304` Glass-подсказка → `AutomationProperties.LabeledBy`/`HelpText` на `ComboBox` | **Исправлено** | Surface style `ComboBox` получил `AutomationProperties.LabeledBy` (биндинг на существующий label `TextBlock`) и `AutomationProperties.HelpText` (биндинг на существующий hint `TextBlock`, не задублированный литерал) |
| `HoverOverlayPolicy` doc: «always increases contrast» уточнение (luminance собственного тинта, не композита) | **Исправлено** | Параметр `HoverOverlayPolicy.For` переименован `compositedSurfaceLuminance` → `ownSurfaceLuminance`; doc-comments в обоих файлах (`HoverOverlayPolicy.cs`/`HoverOverlayBrushConverter.cs`) явно уточняют: luminance СОБСТВЕННОГО тона поверхности (accent, смешанный в neutral base), не композита с реальными обоями рабочего стола (которых рантайм не видит) |

### Инцидент теста (найден и исправлен в рамках этого же прохода)

Первая версия `PanelMetricsTests.ResizeCornerThickness_CoversMajorityOfCornerDeadZone` конструировала
`new Application()` (если `Application.Current` был `null`) БЕЗ заполнения её `Resources` реальными
словарями — при полном прогоне `dotnet test`, если этот тест оказывался первым, кто вообще трогает
`Application` в процессе, он навсегда оставлял `Application.Current.Resources` пустым для ВСЕХ
последующих тестов процесса (WPF не даёт "развоплотить" `Application` — `Application.Current`, once
set, остаётся на весь процесс). Сломало 72 несвязанных теста (`ChildWindowViewModelTests` и другие,
полагающиеся на ambient `Application.Current.Resources`, уже содержащий полный merge). Обнаружено и
исправлено в рамках этой же итерации, ДО передачи на ревью: тест теперь мёржит тот же полный
(non-High-Contrast) набор из 8 словарей, что `ResourceDictionaryMergeTests.MergeResourceDictionaries`,
если он оказывается владельцем `Application`. 5 последовательных полных прогонов после фикса — зелёные.
См. §10 для полного описания и для одной несвязанной, предсуществующей флейк-неудачи, тоже
задокументированной там (не вызвана этим фикс-циклом).

### Не переоткрыто

ADR-025's «Известные проблемы», все решения Фазы 5, и fix-cycle 1's Amendment решения по Вопросам 1/3
(§4) — не пересматривались этим циклом. §4 Вопрос 2 (hover-wash direction) тоже не пересмотрен по сути —
только формулировка doc-comment'а (см. finding-таблицу выше), математика/поведение не менялись.
`ListReorderGeometry.cs`, `AdaptiveIconGridPanel`/`GridMetrics`/`AdaptiveGridLayout`,
`ResizeGestureSession`/`ResizeConstraintsFactory`/`RollUpBehavior`/`WorkspaceGridMetrics`,
`ChildWindow.xaml.cs`, persistence/схема JSON — не тронуты, как и предписано.

### P07 — статус после fix-cycle 2

Это был **последний** разрешённый fix-cycle для P07 (лимит 2 итерации, `docs/fix-prompts/AGENT_CONTRACT.md`/
`CLAUDE.md`). Все MAJOR/MINOR/NIT находки повторного ревью либо исправлены, либо явно, численно
задокументированы как структурный предел вне scope (не молча приняты). Единственный открытый пункт,
требующий решения владельца продукта, а не инженерного фикса — `DO-021`
(`SurfaceComposer.MinSurfaceOpacity`, §11 п.1) — уже заведён как отдельный backlog-тикет вне этого этапа.
P07 передаётся дальше по конвейеру (`tdd-test-engineer`/`code-quality-reviewer`/`fences-ux-parity-reviewer`
→ `architecture-documenter`) как содержательно закрытый; визуальная сверка на реальном экране (скриншоты
100/125/150% DPI, светлые/тёмные обои — см. §9) остаётся manual-verify — этот проход по заданию
(`AGENT_CONTRACT.md`) не запускал приложение и не делал скриншотов, только статический анализ/
`dotnet build`/`dotnet test`, которые полностью зелёные (2631/2631).

---

## 14. P09 release-gate fix (2026-09-22) — угловые resize-грипы фактически 6×6, не 8×8, на WPF-пути

**Контекст.** Точечный фикс P1-дефекта из release-gate defect list (P09), не полный fix-цикл этого
документа — `wpf-ui-builder` в изолированном worktree, TDD. Закрывает §11 п.4 («border radius/clip/
resize-grip — не проверено») в его самой рискованной части (WPF-path geometry) — сама 100/125/150% DPI
визуальная сверка на живом экране остаётся manual-verify (см. ниже), не закрыта этим проходом.

**Находка.** fix-cycle 2's MAJOR-A дал угловым `Thumb`'ам (`ResizeOverlay`, `Views/ChildWindow.xaml`)
явные `Width`/`Height=PanelMetrics.ResizeCornerThickness` (8 DIP), оставив их внутри ячейки Grid,
по-прежнему размером `PanelMetrics.ResizeEdgeThickness` (6 DIP) — расчёт был на то, что
`Horizontal`/`VerticalAlignment` дадут ребёнку «перелиться» на лишние 2 DIP за пределы своей ячейки. Это
не сработало: WPF применяет автоматический layout-clip к КАЖДОМУ элементу, чей `RenderSize` превышает
слот, реально выделенный ему Grid'ом (`FrameworkElement`'s внутренний `NeedsClipBounds` — отдельный
механизм от `Panel.ClipToBounds`, alignment его не отменяет) — «перелив» fix-cycle 2 молча обрезался
обратно до 6×6; alignment решал лишь КАКОЙ угол 8×8-содержимого выживет после обрезки, а не отменял саму
обрезку. Собственный комментарий fix-cycle 2 («WPF does not clip a Grid child to its cell unless
ClipToBounds is set») был попросту неверен для этой формы бага (child больше своей ячейки) — обе фазы
предыдущих фикс-циклов (MAJOR-1 расширение 6→8, MAJOR-A разделение corner/edge) молча откатывались этим
механизмом на WPF-пути всё это время, пока нативный путь (`NativeHitTestMapper.Map`, там такого clip нет)
честно использовал 8×8 — расхождение путей никогда не проявлялось, потому что ни один тест не измерял
РЕАЛЬНУЮ geometry живого `ResizeOverlay` (`PanelMetricsTests`'ы — чистая арифметика констант).

**Фикс.** Внешние `Grid.ColumnDefinitions`/`RowDefinitions` `ResizeOverlay` теперь размером
`PanelMetrics.ResizeCornerThicknessLength` (новый GridLength-wrapper, 8 DIP) вместо edge-толщины —
угловые `Thumb`'ы просто заполняют свою СОБСТВЕННУЮ выделенную ячейку (default `Stretch`, без явных
`Width`/`Height`/переливов — точное совпадение размера с ячейкой никогда не обрезается). Прямые
рёберные `Thumb`'ы (Top/Bottom/Left/Right) стали МЕНЬШЕ своей (угловой, 8-DIP) ячейки — явные
`Width`/`Height=ResizeEdgeThickness` (6), заякоренные к своему внешнему краю через
`Horizontal`/`VerticalAlignment`; ребёнок МЕНЬШЕ своей ячейки никогда не обрезается независимо от
alignment. `NativeHitTestMapper`/`NativePanelResizeController` не менялись (никогда не были багованной
стороной). Поправлены комментарии в `PanelMetrics.cs` (описание механизма) и битый `<see cref>` на
удалённый `ResizeGripThickness` в `RoundedRectClipConverter.cs` → `ResizeCornerThickness`.

**Тест (TDD, реальный STA/`Show()`).** Новый `tests/DesktopOrganizer.Tests/Unit/Views/ResizeOverlayGeometryTests.cs`
— конструирует РЕАЛЬНЫЙ `ChildWindow`, вызывает `Window.Show()` (offscreen/non-activating, тот же
паттерн, что уже установлен `AdaptiveIconGridPanelLayoutPolicyBindingTests` — голый `Measure`/`Arrange`
на непоказанном `Window` эмпирически не даёт реального layout-прохода), после чего проверяет для 4
угловых `Thumb`'ов `ActualWidth==ActualHeight==8`, `LayoutInformation.GetLayoutClip==null` и точную
позицию через `TranslatePoint`; для 4 рёберных — толщину 6 и тоже отсутствие clip. До-фикс: 6×6 (красный
тест). После: 8×8, без clip (зелёный).

**Побочный инцидент теста (найден и исправлен в рамках этого же прохода).** Первая версия теста строила
СВОЙ собственный STA-поток по образцу `ChildWindowInitializeTests`/`ChildWindowSizingGridTests` — рабочий
для тех классов, потому что они НИКОГДА не вызывают `Show()`. Здесь это привело к падению ВСЕГО процесса
`dotnet test` на 801-м из 3119 тестов полного прогона: `ChildWindowViewModel`'s фоновый
`_existenceTimer` какого-то НЕСВЯЗАННОГО, ранее не диспозящегося теста попытался
`Dispatcher.Invoke` на диспетчер, который к тому моменту уже был неявно завершён (поток, породивший его,
вышел сразу после своего теста, не владея `Application`) — необработанное исключение на потоке таймера
из `ThreadPool` фатально для всего процесса, не только для одного теста. Исправлено: если
`Application.Current` уже существует, тело теста маршалируется на ЕГО СОБСТВЕННЫЙ (уже живой, уже
прокачивающий сообщения) `Dispatcher` через `Dispatcher.Invoke`, а не выполняется на новом, отдельном
STA-потоке — гарантируя, что показанное окно и владеющий `Application` поток всегда совпадают. Полный
`dotnet test` (3119/3119, из них 3117 — базовая линия plus 2 новых) зелёный, прогнан 5 раз подряд без
единого падения/краша.

## Test log — P09

```
dotnet build DesktopOrganizer.sln  → Build succeeded, 0 Error(s)
dotnet test DesktopOrganizer.sln --no-build → Passed: 3119, Failed: 0, Skipped: 0 (× 5 последовательных прогонов)
```

## Manual verify (Win10/Win11) — добавлено P09

Наивысший приоритет (закрывает самую рискованную часть §11 п.4): диагональный mouse-down точно в
геометрическом углу панели (допуск 1-2 DIP) надёжно стартует диагональный resize (курсор
`SizeNWSE`/`SizeNESW`) на ДЕФОЛТНОМ (Thumb-based, `NativePanelSizing=False`) пути — это именно то ранее
молча сломанное поведение, которое чинит этот фикс; сравнение реально «ловимой» площади угла до/после на
100/125/150% DPI (скриншот/запись экрана) — самая ценная ручная проверка для этого фикса.
