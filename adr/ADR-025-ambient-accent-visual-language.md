# ADR-025: Ambient Accent Visual Language

Status: Accepted

Фаза 5 «Ambient Accent visual system» (`docs/Refactoring/PHASES/PHASE-5-ambient-accent-ui.md`,
`docs/Refactoring/ROADMAP.md`). Ветка `ui/ambient-accent-design-tokens` (от `rc/0.4.0`). Мастер-план
реализации — `docs/PLANS/phase-5-ambient-accent-ui.md` (D-1..D-12, риски R-1..R-12). Конвейер:
`fences-feature-planner` → `wpf-ui-builder` (7 логических коммитов, C1–C7) → **три полных раунда**
`code-quality-reviewer` ∥ `fences-ux-parity-reviewer` с фиксами между ними + финальный узкий
фикс-цикл (MAJOR-1/MAJOR-2, MAJOR-A/MAJOR-B, MAJOR-5) → этот документ. Текущий HEAD `c477c59`.

## Контекст

Цель фазы — та же, что у всего проекта (см. `CLAUDE.md`): функциональный и поведенческий паритет со
Stardock Fences. Для внешнего вида панелей это исторически означало «залить панель цветом категории»,
как делает Fences по умолчанию. На практике реализация до Фазы 5 (`BackgroundColor`/`BackgroundOpacity`
из Фазы 3, ADR-005) уже давно разошлась с этим буквальным поведением — цвет применялся ко всей
поверхности панели произвольно, без семантики категорий, без типографических/spacing-токенов
(инлайновые hex-литералы в `ChildWindow.xaml`/`MainWindow.xaml`, см. `docs/Refactoring/BACKLOG.md`
DO-003), без согласованного языка interaction-состояний (Idle/Hover/Editing/Dragging/Resizing из
ADR-021 не имели визуального выражения вообще).

Фаза 5 сознательно **не** копирует дефолт Fences буквально, а эволюционирует визуальный язык — см.
«Альтернативы» ниже. Паритет с Fences в этой фазе понимается не как «пиксель-в-пиксель», а как
«пользователь может добиться той же функции (визуально выделить категорию панели цветом)», при этом
сам визуальный язык — нейтральная поверхность + локальный семантический акцент + явные
interaction-состояния — целевой дизайн этого проекта, а не Fences.

## Decision

Adopt Ambient Accent as the default panel visual language.

### Rules

- neutral panel surface is primary;
- accent is semantic and sparse;
- full-panel category color fill is not the default;
- controls are low-emphasis in idle state;
- interaction increases contrast rather than adding heavy decoration;
- all colors/spacing/type/radius are design tokens.

### Default semantic accents

These are presets, not hard-coded category rules:

- Work: blue
- Games: violet
- Media: amber
- Network: green
- AI/Launchers: cyan/teal
- Dev Tools: coral/red

User can override.

## Альтернативы

- **Буквальная копия дефолта Fences: сплошная заливка title bar/панели цветом категории.**
  Рассмотрена и отклонена. Это была фактическая реализация Фаз 3 (`BackgroundColor` заливал всю
  поверхность панели через альфа-канал, `BackgroundOpacity`). Отклонена по трём причинам: (1) с ростом
  числа панелей на рабочем столе сплошная заливка визуально шумна и мешает читать содержимое —
  собственно тот UX-риск, который дизайн-эволюция R-1 плана прямо называет («все панели стали
  одинаковыми/слишком яркими» в обратную сторону); (2) сплошная заливка не оставляет визуального
  пространства для отдельного языка interaction-состояний (Idle/Hover/Editing/Dragging/Resizing) — на
  уже закрашенной цветом категории поверхности выделить ещё и состояние взаимодействия попытка
  добавить второй несовместимый слой цвета; (3) не даёт единой нейтральной базы для типографики/spacing
  токенов (D-1 плана) — цвет категории и системный chrome конкурировали бы за один и тот же визуальный
  бюджет. **Выбрано взамен:** нейтральная поверхность (`Color.Surface.*`) + локальный акцент
  (индикатор/иконка/selection/hover) — акцент семантический и разреженный (`Rules` выше), а не
  заливка. Это прямое architecture decision, а не недосмотр или урезание объёма.
- **Аддитивное расширение схемы v3 (`PanelAppearanceDto`) без полноценной миграции v3→v4.**
  Рассмотрена и отклонена (план D-5) — `SchemaContractTests` в любом случае требует миграции для
  нового non-nullable поля, а два источника правды о внешнем виде (`backgroundColor` legacy +
  `accentColor` новое) нарушили бы инвариант CLAUDE.md «единый источник правды вида панели».
- **Настоящий acrylic/DWM-blur материал для `PanelSurfaceStyle.Glass`.** Рассмотрена и отклонена для
  этой фазы (план R-8) — требует нативного backdrop-blur кода, непроверяемого в этой среде без живого
  Windows-GUI; см. «Последствия» ниже (`Glass ≠ acrylic`).

## Последствия

### Позитивные

- Единый источник правды внешнего вида панели сохранён (`ChildWindowModel`, INPC) — акцент,
  surface-стиль и прозрачность через одни и те же токенизированные пути для Grid/List/Folder
  Portal/`SystemIconsPanel` (attached DP `PanelTheme`, не биндинг на конкретный `DataContext`).
- Interaction-состояния (Idle/Hover/Editing/Dragging/Resizing) впервые получили визуальное выражение,
  машинно проверяемое (`InteractionVisualStateTests` — пять состояний попарно различимы и монотонно
  возрастают по `BorderOpacityFor`).
- Персональный цвет, однажды заданный пользователем панели, не теряется при миграции схемы —
  переезжает в `AccentColor` (см. `D-5 лоссивность` ниже про единственное исключение).
- Хардкод-цвета в `ChildWindow.xaml`/`MainWindow.xaml` устранены (закрывает `docs/Refactoring/BACKLOG.md`
  DO-003) — grep-гейт `rg -n "#[0-9A-Fa-f]{3,8}" .../ChildWindow.xaml` даёт 0 совпадений.

### Принятые trade-off'ы (Negative)

- **D-5 лоссивность миграции v3→v4.** Правило миграции: `accentColor := v3.backgroundColor ==
  "#1E1E1E" (легаси дефолт) ? "#6B7280" (Accent.Neutral) : v3.backgroundColor`. Это лоссивно для
  панелей, у которых пользователь никогда явно не менял цвет — они физически неотличимы от панелей,
  где пользователь сознательно выбрал тёмно-серый `#1E1E1E`. Явно принятый trade-off: протащить
  исторический дефолт «тёмная заливка» как есть в акцент означало бы, что у всех дефолтных панелей
  появляется почти-чёрный индикатор — хуже, чем потеря теоретической, никогда не наблюдавшейся на
  практике пользовательской интенции. Панели, где пользователь **осознанно** задал цвет (например,
  Games=фиолетовый), сохраняют идентичность бит-в-бит — лоссивна только ветка с историческим дефолтом.
- **`Glass ≠ acrylic` (R-8).** `PanelSurfaceStyle.Glass` в этой фазе — это пониженная непрозрачность
  поверхности (`SurfaceComposer.GlassOpacityFactor = 0.60` от `Ambient`) + усиленная граница, **не**
  настоящий acrylic/DWM backdrop-blur материал (blur того, что находится позади окна на рабочем столе).
  Причина: настоящий blur требует нативного Win32/DWM-кода (`SetWindowCompositionAttribute` или
  `DwmSetWindowAttribute`+`ACCENT_ENABLE_ACRYLICBLURBEHIND` в необщедоступном ABI), непроверяемого в
  среде разработки без живого Windows-десктопа, и выходит за рамки объёма этой визуальной фазы. Это
  осознанное решение, зафиксированное в XML-doc `PanelSurfaceStyle.cs`, а не недоделка — кандидат
  Фазы 8 «Native acceleration and shell enhancements» (`docs/Refactoring/ROADMAP.md`), если/когда
  паритет с настоящим Fences Glass-режимом станет приоритетом.
- **`TextColor`-долг (D-4, бэклог DO-013).** `ChildWindowModel.TextColor` сознательно **не** удалён и
  не переведён на типографические токены в этой фазе — убрать его означало бы user-facing removal
  (правка окна настроек, `Settings.settings`) ради чисто архитектурной косметики. `TextColor` остаётся
  per-panel override поверх токенов типографики; долг зафиксирован как `DO-013` в
  `docs/Refactoring/BACKLOG.md` — кандидат на удаление в Фазе 6/9.
- **Намеренное расхождение с буквальным дефолтом Fences.** Эталонный Stardock Fences по умолчанию
  красит весь title bar/панель сплошным цветом категории. Этот проект — нейтральная поверхность с
  локальным акцентом (индикатор/иконка/selection/hover). Это зафиксированный **decision** (см.
  «Альтернативы» выше), а не недосмотр реализации: цель «паритет с Fences» в части визуального языка
  панелей трактуется как «пользователь может выразить категорию цветом», а не как «идентичный пиксель
  в пиксель рендеринг».
- **Dragging vs Resizing различимость.** После MAJOR-5/MAJOR-B фиксов финального раунда ревью состояния
  `Dragging` и `Resizing` различаются по двум независимым визуальным каналам: ширина акцент-индикатора
  (`PanelMetrics.AccentIndicatorThickness = 3.0px` база × `InteractionVisualState.AccentEmphasisFor`:
  Idle 1.0 → 3.0px, Dragging 1.5 → 4.5px, Resizing 1.7 → 5.1px) и border opacity (`BorderOpacityFor`:
  Dragging 0.95, Resizing 1.0). Тем не менее основной сигнал пользователю о том, какой именно жест
  выполняется, — **форма курсора** (resize-курсор на краю/углу vs move-курсор на заголовке), а не
  тонкая разница в толщине индикатора или в 0.05 альфы границы, которая на живом экране трудно
  заметна периферийным зрением во время активного жеста. Это принятое рассуждение, явно
  зафиксированное здесь по рекомендации `fences-ux-parity-reviewer` в финальном раунде ревью —
  **не переоткрывать** вопрос «Dragging и Resizing визуально неотличимы» в будущих ревью без нового
  найденного UX-кейса, где именно этот (а не курсор) канал оказывается для пользователя главным.

## Известные проблемы (сознательно вне скоупа Фазы 5)

Зафиксированы по итогам финального фикс-цикла, не блокируют закрытие фазы:

- **`Color.Overlay.Hover = SystemColors.ControlLightColor` в High Contrast.** В отдельных HC-темах
  Windows `ControlLightColor` может совпадать с `WindowColor`/`ControlColor`, из-за чего hover-подсветка
  становится невидимой. Требует ручной проверки на нескольких HC-темах (см. ROADMAP «manual verify»);
  фикс — если подтвердится — отдельный тикет подбора альтернативного `SystemColors.*`-источника.
- **Активная вкладка окна настроек в HC-режиме различается только цветом фона**, без дополнительного
  индикатора (например, рамки) — недостаточно контрастно в некоторых HC-темах. Не исправлено в этой
  фазе.
- **Мёртвая ветка `"accent"` в `Converters/VisualStateToDoubleConverter.cs`.** Конвертер поддерживает
  параметр `"accent"` (`InteractionVisualState.AccentEmphasisFor`), но ни один XAML-биндинг в `Panels.xaml`
  на неё не ссылается — ширина акцент-индикатора доставляется отдельным
  `AccentIndicatorWidthConverter`, не этой веткой. Оставлена как есть (не мёртвый код без назначения —
  API конвертера согласован с планом D-6/§3.4, просто не единственный потребитель эмфазиса).
- **High Contrast подхватывается только при старте приложения.** `Colors.HighContrast.xaml` мержится
  в `App.xaml.cs` один раз при старте, если `SystemParameters.HighContrast == true`. Смена системной
  HC-темы «на лету» (без перезапуска приложения) не подхватывается — это осознанный минимум по D-12
  плана (`docs/PLANS/phase-5-ambient-accent-ui.md`), не баг; live-переключение вынесено в бэклог
  `DO-015`.

## Manual verify (Win10/Win11)

Полный чек-лист — `docs/PLANS/phase-5-ambient-accent-ui.md` §7.2 (V-1..V-18) и
`docs/Refactoring/Testing/ambient-accent-visual-matrix.md`. Не выполнено в этой среде разработки
(нет живого Windows GUI). Наивысший приоритет: нейтральная поверхность vs заливка категории (V-2),
все 4 состояния High Contrast (V-14 + «Известные проблемы» выше), Dragging/Resizing различимость по
курсору (V-11/V-12), selected+hover на разных `SurfaceOpacity` (V-5..V-8), DPI 125%/150% (V-15),
отсутствие layout-shift акцент-полосы при смене interaction-состояния.

---

## Amendment 2026-09-22 (P07-visual)

Этап P07 пакета `docs/fix-prompts/` (`docs/fix-prompts/07-visual.md`) — сверка референса «третий Ambient
Accent» с текущей реализацией. Мастер-план/токены/таблица контраста — `docs/PLANS/workspace-grid-07-visual.md`.
Конвейер: `wpf-ui-builder` (этот проход) → `tdd-test-engineer`/`fences-ux-parity-reviewer`/
`code-quality-reviewer` (следующий шаг). Не переоткрывает ни одно из решений выше — все правки ниже
аддитивны поверх уже принятого визуального языка (нейтральная поверхность + локальный акцент).

### Border radius: 6 → 8 DIP, с явным clip (закрывает риск clipping)

**Решение.** `Radius.Panel` (`Resources/Styles/Spacing.xaml`) увеличен с `6` до `8` DIP — ровно один
workspace-grid `U` (`WorkspaceGridMetrics.UnitSize`, ADR-028), взят как удобное, уже нагруженное смыслом
круглое число в этой кодовой базе, а не потому что этот токен — часть сетки ADR-028 (явно не часть:
ADR-028/029 «две сетки namely несоизмеримы», эта величина здесь исключительно косметическая, без
snap-семантики). Проверка безопасности перед выбором числа: `8` — четверть `PanelMetrics.HeaderHeight`
(32) и меньше шестнадцатой доли `WorkspaceGridMetrics.MinWidthSpanExpanded` (96 DIP)/жёсткого WPF-флора
`GridMetrics.MinPanelSize` (120 DIP) — даже у самой маленькой разрешённой панели остаётся достаточно
прямого края с каждой стороны.

**Precondition этого изменения — clip.** Предыдущая ревизия этого файла (Спейсинг, D-1) сознательно НЕ
трогала `Radius.Panel`, аргументируя это отсутствием DoD на изменение радиуса и риском непроверенной
визуальной регрессии — именно потому, что `Border` красит скруглённым только СВОЙ `Background`/
`BorderBrush`, а не клипует произвольный дочерний контент (скроллбар `ListBox`, selection-рамка у самого
края) под ту же форму. При `CornerRadius=6` эффект был на грани заметности; при более широком радиусе он
стал бы видимым дефектом. Решение — `Converters/RoundedRectClipConverter.cs` (новый, `IMultiValueConverter`),
забинженный на `PART_Chrome.Clip` (`Resources/Styles/Panels.xaml`) через `RelativeSource Self` над
`(ActualWidth, ActualHeight, CornerRadius)` — тот же безопасный, никогда не бросающий контракт, что и у
остальных Ambient Accent конвертеров (`Binding.DoNothing` вместо `null`/вырожденной пустой геометрии на
некорректном/ещё не измеренном входе).

**Hit test — MAJOR-1, найден и исправлен в fix-cycle 1 (см. Amendment 2026-09-22 ниже).** Утверждение
ниже верно ТОЛЬКО про WPF-внутреннее дерево хит-теста, но из него был сделан неверный вывод «значит,
риска нет»: `ResizeOverlay` (`Views/ChildWindow.xaml`) — SIBLING `PanelChrome`, не потомок;
`UIElement.Clip` действует только на подписанный элемент и его собственное поддерево, поэтому новый clip
не может достать до WPF `Thumb`-хендлов ЧЕРЕЗ ЭТОТ МЕХАНИЗМ. Нативный путь (`NativeHitTestMapper.Map`,
ADR-022) классифицирует точку по прямоугольным `GetWindowRect`/border-strip геометриям и вообще не знает
о радиусе скругления. Но панель несёт `AllowsTransparency="True"` — отдельный, реальный механизм: OS
маршрутизирует клик по полностью прозрачному (альфа=0) пикселю слоистого окна напрямую под окно,
независимо от WPF-дерева хит-теста вообще — а угловой квадрат СНАРУЖИ дуги скругления `Border`'а всегда
именно такой пиксель, при любом `CornerRadius > 0`. См. Amendment 2026-09-22 ниже для полного объяснения
и фикса (расширенный resize-grip).

### Header a11y: `AutomationProperties.Name`

**Решение.** Четыре кнопки заголовка (Sort/View/RollUp/Overflow, `Views/ChildWindow.xaml`) получили
`AutomationProperties.Name`, забинженный на тот же источник, что и их `ToolTip` (никогда не
задублированный литерал) — до этого их доступное имя для UI Automation/скринридера было пустым (глиф
Segoe MDL2 без текстовой подписи). Панель несёт `WS_EX_NOACTIVATE` (ADR-013 R1), поэтому обычная
клавиатурная фокусировка кнопок остаётся непроверенным риском того же класса — это не решает и не
переоткрывает; `AutomationProperties.Name` полезен независимо от фокуса для клиентов UI Automation.

### Reduced motion: `MotionState` + `RollUpBehavior`

**Решение.** Новый `Theming/MotionState.cs` (тот же паттерн, что `HighContrastState`, D-12: один раз при
старте, `App.xaml.cs` читает `SystemParameters.ClientAreaAnimation` рядом с `SystemParameters.HighContrast`).
`Behaviors/RollUpBehavior.ApplyRollUp` — единственное место, запускающее `DoubleAnimation` на живой
панели — при `MotionState.AnimationsEnabled == false` пропускает анимацию и применяет целевую высоту
синхронно (`BeginAnimation(..., null)` затем прямое присваивание `Height`), сохраняя весь остальной
конвейер (collapse `ContentRow`, `IResizeAffordance.SetEnabled`, `_rollUpGeneration`-инкремент против
гонки с предыдущим жестом) без изменений. Live-переключение системной настройки во время работы
приложения не подхватывается — тот же осознанный минимум, что уже принят для `HighContrastState` (D-12).

### Contrast: WCAG-калькулятор + аудит реальных токенов (находки, не фикс в этом проходе)

**Решение.** Новый `Theming/ContrastCalculator.cs` — чистые функции (`RelativeLuminance`, `ContrastRatio`,
`CompositeOverOpaque`, `ContrastAgainstComposited`) по формулам WCAG 2.x, покрыты
`ContrastCalculatorTests` (эталонные значения WCAG: чёрное/белое = 21:1, идентичные цвета = 1:1). Полная
таблица контраста (`Solid`/`Ambient`/`Glass` × светлые/тёмные обои × дефолтная/минимальная непрозрачность
× все 7 акцентов) — `docs/PLANS/workspace-grid-07-visual.md`. Три находки зафиксированы как
машинно-проверяемые характеризационные тесты (`AmbientAccentContrastAuditTests`), но **сознательно не
исправлены в этом проходе** — см. план, раздел «Открытые вопросы контраста», почему каждая из них требует
отдельного, не чисто визуального решения:

1. `SurfaceComposer.MinSurfaceOpacity` (0.35, сегодняшний нижний предел слайдера непрозрачности) даёт
   ≈1.7:1 контраста на светлых обоях для ЛЮБОГО стиля — далеко ниже цели 4.5:1. Не исправлено здесь:
   поднятие этой константы молча перерисовало бы уже сохранённые панели пользователей, чья
   `SurfaceOpacity` меньше нового пола, — поведенческое изменение персистентных данных, которое эта
   визуальная фаза не вправе решать в одиночку (см. план).
2. `PanelSurfaceStyle.Glass` структурно не может достичь 4.5:1 на светлых обоях ни при какой
   непрозрачности (максимум ≈3.34:1 даже при `SurfaceOpacity=1.0`) — прямое следствие
   `GlassOpacityFactor=0.60` (R-8, «Glass — не настоящий acrylic», уже задокументированное сознательное
   ограничение). Не исправлено: понижение прозрачности лишило бы `Glass` смысла как отдельного стиля.
3. **Исправлено в fix-cycle 1 (MAJOR-3, см. Amendment ниже).** `Color.Overlay.Hover` (`#1AFFFFFF`,
   полупрозрачный БЕЛЫЙ) на светлых обоях/дефолтной непрозрачности СНИЖАЛ контраст иконки заголовка при
   наведении (idle ≈3.59:1 PASS → hover ≈2.72:1 FAIL порога 3:1 для иконок) — осветляющий wash приближал
   фон к среднесерому `Text.Secondary`, а не отдалял. Новая находка этого аудита (не описанная ни в одном
   предыдущем ревью Фазы 5/6), исправлена тем же циклом, что её нашёл: `Theming/HoverOverlayPolicy.cs`
   выбирает затемняющий/осветляющий wash по luminance композированной поверхности — hover теперь ≈4.08:1
   на светлых обоях (было 2.72), ≈7.19:1 на тёмных (было 5.06).

High Contrast (D-12, `Colors.HighContrast.xaml`) не менялся этим проходом — контраст там гарантирует
выбранная пользователем системная HC-тема, не токены этого приложения; `SurfaceComposer`'s
`highContrast`-ветка (принудительно непрозрачная нейтральная заливка) подтверждена аудитом как
не вносящая регрессию (`HighContrastMode_ForcesOpaqueNeutralSurface_AlwaysPassesTextContrast`).

## Manual verify (Win10/Win11) — добавлено P07-visual

Наивысший приоритет (сверх списка выше): скругление 8 DIP на 100/125/150% DPI без видимого выхода
контента за границу (скроллбар/selection-рамка); собранная сборка `ResizeOverlay`/native resize по-прежнему
хватает точно на скруглённом углу; `AutomationProperties.Name` кнопок заголовка через Narrator/Accessibility
Insights; roll-up/roll-down без анимации при выключенной в ОС «Animation effects» (Settings → Accessibility
→ Visual effects) — мгновенный переход высоты, без промежуточного кадра.

---

## Amendment 2026-09-22 (P07-visual fix-cycle 1)

`code-quality-reviewer`'s round on the P07-visual Amendment above found one real correctness bug (MAJOR-1),
fixed three of the six findings recorded there, and left the remaining three as either an accepted
usability note or a tracked backlog item — decided by the orchestrator, not unilaterally by this fix.
Full detail, contrast tables, and the finding→status table — `docs/PLANS/workspace-grid-07-visual.md` §12.

### MAJOR-1 — resize-corner hit-test: a real bug, not just an overclaim

The Amendment's own "Hit test — проверено, не затронуто" paragraph above was corrected in place rather
than only appended to, because it stated a conclusion ("не может достать", "негligible") that a SEPARATE
mechanism it never considered contradicts. The panel window carries `AllowsTransparency="True"` — a
layered window whose mouse-input routing the OS decides from the actual composited pixel ALPHA at each
screen location, entirely independent of WPF's own hit-test tree. A pixel that ends up alpha-0 in the
final composited bitmap — exactly what the small square OUTSIDE a rounded `Border`'s own quarter-circle
arc always is, at ANY `CornerRadius > 0`, whether or not `RoundedRectClipConverter`'s `Clip` is even
applied (`Border.Background` already paints a rounded shape natively) — routes a mouse-down straight to
the desktop, bypassing this window's WPF message loop entirely, regardless of the `Thumb` resize handle's
own hit-test geometry sitting at that same location (its own `Background="Transparent"` paints nothing
opaque there either). That always-transparent lens' area (`r² · (1 - π/4)`, ≈13.7 DIP² at the widened
`r=8`) does not depend on the resize grip's size, but GREW when `Radius.Panel` widened 6→8 in the same
pass that introduced this ADR's own ambient-accent geometry — occupying ≈38% of the PRE-fix 6×6 grip's
own footprint. **Fix:** `PanelMetrics.ResizeGripThickness` (new, `8d`) is now the single source of truth
for `ChildWindow.xaml`'s `ResizeOverlay` grip AND `NativePanelResizeController.BorderDip` (previously two
independently-maintained `6d` literals) — widening the grip to match the radius shrinks the lens' share of
the grip's own footprint to ≈21%, leaving most of a diagonal-resize gesture's initial mouse-down over a
still-opaque pixel. Regression pin: `PanelMetricsTests.ResizeGripThickness_CoversMajorityOfCornerDeadZone`
(pure geometry, no live screen needed).

### MAJOR-3 — header icon-button hover wash now increases contrast

`Color.Overlay.Hover` (`#1AFFFFFF`) always lightened whatever it was painted over; every
`PanelSurfaceStyle`/accent token this app ships is dark (a mostly-opaque `#1E1E1E` neutral base with at
most an 8% accent tint), so lightening it on hover pushed the surface TOWARD the mid-tone icon colour
instead of away from it — the P07-visual audit's own new finding (idle 3.59:1 PASS → hover 2.72:1 FAIL on
a light wallpaper). **Fix:** `Theming/HoverOverlayPolicy.cs` (pure function) picks a lightening or
darkening wash from the panel's own composed surface luminance (`ContrastCalculator.RelativeLuminance`,
ignoring alpha — the RGB channels `SurfaceComposer.ComposeSurface` produces are independent of
`SurfaceOpacity` by construction, which is what makes this computable without ever knowing the real
desktop wallpaper behind a translucent panel); `Converters/HoverOverlayBrushConverter.cs` wires it into
the 3 header icon-button styles (`Control.Button.Icon`/`PanelHeaderButtonStyle`/
`PanelHeaderToggleButtonStyle`, `Resources/Styles/Controls.xaml`) via a `MultiBinding` over the same
`Model.AccentColor`/`Model.SurfaceStyle` inputs `PanelSurfaceBrushConverter` already reads. High Contrast
passthrough preserved: the converter's 4th binding is the ALREADY-resolved `Brush.Overlay.Hover`
`StaticResource` (which `Colors.HighContrast.xaml` overrides to `SystemColors.ControlLightColor`), and is
returned verbatim — never recomputed — whenever the 3rd binding (`HighContrastState.IsActive`) is
`true`. Result: hover now improves on idle contrast (light wallpaper 3.59→4.08, dark 6.94→7.19) instead of
eroding it. Deliberately scoped to header icon buttons only — `Panels.xaml`'s `PanelItemContainerStyle`
(file-icon-tile hover in the panel body) keeps the plain `Brush.Overlay.Hover`, a different affordance not
covered by this finding.

### UX-P2 — List-mode label now truncates with an ellipsis

Not one of this ADR's own findings, but flagged by `code-quality-reviewer` in the same round: List mode's
item label never truncated because `ListViewTemplate`'s item root was a horizontal `StackPanel`, which
always measures its orientation-axis children with an infinite available width — `TextTrimming` alone
(already tried, per the original P07-visual pass's own note) has nothing to truncate against. **Fix:**
item root switched to a `Grid` (`Auto` icon column / `*` label column) — a `Grid`'s star column receives
the item's real, finite arranged width during Arrange even when Measure saw the same infinite constraint,
which is what lets `TextTrimming="CharacterEllipsis"` actually work. `FileItemRootStyle`'s `TargetType`
widened `StackPanel` → `Panel` (the common base both the new `Grid` and Grid mode's unchanged `StackPanel`
share) so the one shared style still applies to both. Verified not to affect
`Behaviors/ListReorderGeometry.cs` (operates on the `ListBoxItem` container's own bounds, never the
DataTemplate's internal element type) via static analysis plus a passing regression test.

### Usability decisions (orchestrator, not re-litigated by this fix)

- **Glass on light wallpapers (§4-1):** not re-opened as an architecture question — `GlassOpacityFactor`
  (R-8) stays as-is. A usability hint was added next to the Surface style picker in `MainWindow.xaml`
  instead ("Glass reads best on dark wallpapers; for light wallpapers, Solid or Ambient stay more
  readable.").
- **`SurfaceComposer.MinSurfaceOpacity` floor (§4-2):** left unchanged — raising it would silently
  re-render already-saved panels whose `SurfaceOpacity` sits below the new floor, which
  `docs/fix-prompts/AGENT_CONTRACT.md`'s "never overwrite a user's own changes" bars this pass from
  deciding unilaterally. Tracked as backlog debt `DO-021` (`docs/Refactoring/BACKLOG.md`,
  `docs/ROADMAP.md`), naming the existing `AmbientAccentContrastAuditTests` pins.

### Not re-opened

Everything else this ADR already decided or recorded as a known, accepted gap (Dragging/Resizing
distinguishability by cursor, the dead `"accent"` branch, HC settings-tab contrast, `TextColor` debt,
`Glass ≠ acrylic`, D-5 migration lossiness) stands unchanged.

## Manual verify (Win10/Win11) — добавлено fix-cycle 1

Highest priority (in addition to the P07-visual list above): 8-DIP resize grip actually catches a
diagonal-resize mouse-down at the panel's geometric corner (the specific, previously-unproven risk MAJOR-1
names); header icon-button hover reads as MORE legible, not less, on both a light and a dark wallpaper;
Glass usability hint is visible and legible next to the Surface style picker; a long file name in List
mode truncates with an ellipsis and does not overflow the panel's right edge; `AutomationProperties.Name`
on the panel window itself (not just its 4 header buttons) via Narrator/Accessibility Insights.

---

## Amendment 2026-09-22 (P07-visual fix-cycle 2, LAST)

A second review round of the fix-cycle 1 Amendment above found that its own MAJOR-1 fix and its UX-P2 fix
were each real but INCOMPLETE — this Amendment corrects both, and closes P07's last allowed fix cycle
(`docs/fix-prompts/AGENT_CONTRACT.md`/`CLAUDE.md`, max 2 iterations). Full detail, finding→status table,
and the exact numeric before/after — `docs/PLANS/workspace-grid-07-visual.md` §13.

### MAJOR-A — MAJOR-1's fix shared ONE thickness between corners and straight edges; that was itself a regression

MAJOR-1 (fix-cycle 1) widened `ResizeGripThickness` from 6 to 8 DIP UNIFORMLY — every one of
`ResizeOverlay`'s 8 `Thumb`s (4 corners AND 4 straight edges) got the same 8-DIP cell, because the
`ChildWindow.xaml` `Grid` backing them shares its outer row/column definitions between both roles. The
click-through-lens argument MAJOR-1 itself makes (a `Border` with `CornerRadius > 0` never paints — and
therefore never lets the OS route a click to — the small square lens outside its own rounded arc) applies
ONLY to the four CORNERS: a straight edge's midpoint is never near the rounded arc at all. Widening the
straight edges to match the corners had no click-through-lens justification and directly caused a new,
real hot-zone regression: the TOP straight-edge `Thumb` (8 DIP tall) covered the top ~21% of every 28-DIP
header action button (`PanelMetrics.HeaderButtonSize`, centred in the 32-DIP `HeaderHeight` header — a
2-DIP top margin), and the RIGHT straight-edge `Thumb` (8 DIP wide) covered roughly half of
`SystemParameters.VerticalScrollBarWidth`'s default width (`FileListHost` has no right margin). **Fix:**
`PanelMetrics.ResizeGripThickness` is retired, split into `ResizeCornerThickness` (8, unchanged value,
corners ONLY) and `ResizeEdgeThickness` (6, restored to its pre-fix-cycle-1 value, straight edges ONLY).
`ResizeOverlay`'s 4 corner `Thumb`s now carry an explicit `Width`/`Height="{x:Static ...ResizeCornerThickness}"`
plus corner-anchoring `Horizontal`/`VerticalAlignment`, so each one overflows its own (now 6-DIP) Grid cell
by 2 DIP toward the panel's outer corner — WPF does not clip a Grid child to its cell unless
`ClipToBounds` is set (it is not here), so this renders/hit-tests identically to the pre-fix-cycle-2
uniform-8 corner. `NativeHitTestMapper.Map` gained a second parameter (`edgeDip`/`cornerDip` instead of
one shared `borderDip`), checking the (wider) corner bands first — mirroring the corner `Thumb`s being
declared after (so visually/hit-test on top of) the straight-edge ones in the same z-order sense.
`NativePanelResizeController`'s `BorderDip` constant is likewise split into `EdgeDip`/`CornerDip`, both
reading `PanelMetrics`.

**This reduces, but — within this fix cycle's frozen scope — cannot eliminate, either residual overlap.**
The header-button top-margin overlap drops from 6 DIP (21% of the button's own height) to 4 DIP (~14%);
the scrollbar overlap drops from ~47% to ~35% of `SystemParameters.VerticalScrollBarWidth`. A literal zero
for either would require widening `HeaderHeight`, shrinking `HeaderButtonSize`, increasing the header
actions' own margin from the edge, shrinking the corner grip below 8 DIP (reopening MAJOR-1), or adding a
`FileListHost` right margin — every one of these is either an explicitly frozen invariant for this fix
cycle or falls outside its `ResizeOverlay`-only zone (`docs/PLANS/workspace-grid-07-visual.md` "Зона").
This residual is therefore an explicit, numerically-pinned open item for the next stage/product owner, not
a silently-accepted regression — see `docs/PLANS/workspace-grid-07-visual.md` §11 item 8 and the two new
manual-verify checklist items in §9.

### MAJOR-B — UX-P2's fix was structurally correct but never reached the real `ListBox`

UX-P2 (fix-cycle 1) correctly diagnosed that a horizontal `StackPanel` item root can never let
`TextTrimming="CharacterEllipsis"` truncate (it always measures its orientation-axis children with an
infinite available width) and correctly replaced it with a `Grid` (`Auto` icon column / `*` label column).
But `ListViewTemplate`'s own `ListBox` never set `ScrollViewer.HorizontalScrollBarVisibility="Disabled"`
(unlike `GridViewTemplate`, which always has — it is a documented hard precondition of
`AdaptiveIconGridPanel.MeasureOverride` there) — leaving `CanHorizontallyScroll=true` on the ListBox's
internal `ScrollContentPresenter`. That measures the `ItemsPresenter`/its panel with an INFINITE available
width regardless of the item root's own internal `Grid` structure, which propagates all the way down: the
Grid's star column never receives a finite width to constrain against during the real app's actual layout
pass, so `TextTrimming` still had nothing real to trim against — a horizontal scrollbar appeared instead of
the intended ellipsis. **Fix:** `ListViewTemplate`'s `ListBox` gained the same
`ScrollViewer.HorizontalScrollBarVisibility="Disabled"` attribute `GridViewTemplate` already had.

**The fix-cycle 1 regression test for this same finding was itself a false negative.** It measured the
item `DataTemplate`'s root element directly (`itemTemplate.LoadContent()`, then `Measure`/`Arrange` on
THAT), never going through the real `ListBox`/`ScrollViewer` pipeline that actually hosts it — so it
stayed green through this exact bug. Fixed alongside the markup: a new STA test
(`ResourceDictionaryMergeTests.ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow`)
loads the REAL `ListViewTemplate` `ListBox` element, gives it a minimal-but-faithful `ControlTemplate` (a
real `ScrollViewer`, `TemplateBindingExtension`-bound to the SAME attached property, hosting a real
`ItemsPresenter`/`StackPanel` — the OS default `ListBox` template does not reliably realize containers in
this headless test host, the same reason this project's other WPF-instance tests already avoid it), and
asserts against the REAL generated `ListBoxItem` container and its REAL descendant label `TextBlock`.
Confirmed empirically (temporary local revert of the Templates.xaml fix plus a diagnostic `throw` inside
the test, both removed before this fix was finalized): without the fix, the generated container measured
1576 DIP wide against a 150-DIP available width. The fix-cycle 1 test is NOT deleted (it still validly
pins the `Grid`-column structural change) but is no longer the only regression pin for this finding.

### MINOR/NIT — doc-comment and accessibility polish

- `HoverOverlayBrushConverter.MaxCachedBrushes`'s doc-comment cited the wrong reason (borrowed from
  `PanelSurfaceBrushConverter`'s own opacity-driven cache growth, which does not apply here — this
  converter's actual key space is a fixed 21 combinations, 7 accents × 3 surface styles) — corrected to
  explain the bound is defensive-only for this converter, not deleted (kept as a safeguard against a
  future key-space change).
- `HoverOverlayPolicy.For`'s parameter renamed `compositedSurfaceLuminance` → `ownSurfaceLuminance`, and
  both its own doc-comment and `HoverOverlayBrushConverter`'s now explicitly state that "increases
  contrast" is scoped to the luminance of the panel's OWN resting tint (`SurfaceComposer.ComposeSurface`'s
  RGB mix), not a composite with the real desktop wallpaper (which this app has no way to know at
  runtime) — the underlying math/behavior is unchanged, only the wording.
- Surface style `ComboBox` (`MainWindow.xaml`) gained `AutomationProperties.LabeledBy` (bound to the
  existing "Surface style" label `TextBlock`) and `AutomationProperties.HelpText` (bound to the existing
  Glass usability-hint `TextBlock`, never a duplicated string literal) — a screen-reader user previously
  heard only "combo box" with no indication either the field's own label or the Glass caveat existed.

### A test-isolation bug found and fixed within this same pass

The first draft of a new `PanelMetricsTests` test (reading `Radius.Panel` from the real compiled XAML
resource instead of a hand-copied literal) constructed `new Application()` when none existed, without
populating its `Resources` — since a WPF `Application`, once constructed, cannot be un-constructed, this
would have left `Application.Current.Resources` permanently empty for the rest of any `dotnet test`
process where this test happened to run first, breaking 72 unrelated tests that assume the ambient
`Application.Current.Resources` already holds the full resource-dictionary merge. Found and fixed within
this same iteration (before this Amendment was written): the test now populates the same real dictionary
chain if it ends up owning the `Application`. Five consecutive full `dotnet test` runs after the fix were
green. Full detail — `docs/PLANS/workspace-grid-07-visual.md` §10/§13.

### Not re-opened

Everything else this ADR (and its fix-cycle 1 Amendment) already decided or recorded as a known, accepted
gap — Dragging/Resizing distinguishability by cursor, the dead `"accent"` branch, HC settings-tab
contrast, `TextColor` debt, `Glass ≠ acrylic`, D-5 migration lossiness, the `SurfaceComposer.MinSurfaceOpacity`
floor (`DO-021`), and fix-cycle 1's own hover-wash direction decision (§4 Вопрос 2, only its doc wording
changed this cycle, not its math/behavior) — stands unchanged.

## Manual verify (Win10/Win11) — добавлено fix-cycle 2 (LAST)

Two new, explicit checklist items (in addition to the P07-visual and fix-cycle 1 lists above — full text,
`docs/PLANS/workspace-grid-07-visual.md` §9): (1) hover directly over the top-right corner of the
`OverflowButton` (the header action button closest to the panel's right edge) and confirm a click in the
DOCUMENTED residual overlap zone (top ~4 DIP, right ~4 DIP of the button's own 28×28 bounds) may resolve as
a resize gesture instead of a button click, while the REST of the button should work normally — this is an
accepted, numerically-reduced-but-nonzero gap, not a regression to chase further within this fix cycle;
(2) with a vertical scrollbar visible in a panel's file list, confirm resizing from the right edge is
noticeably easier to grab (outside the scrollbar) than before this fix cycle, even though a literal zero
overlap is not expected (would require a `FileListHost` margin change, outside this fix cycle's
`ResizeOverlay`-only zone).

---

## Amendment 2026-09-22 (P09 release-gate fix: corner resize-grip actually 8×8 on the WPF path)

Point fix from the P09 release-gate defect list (a MAJOR/P1 finding, not a full fix cycle of this ADR's
own P07 pipeline — `wpf-ui-builder` only, TDD). Full detail, before/after geometry, and the test log —
`docs/PLANS/workspace-grid-07-visual.md`, new dated section at its own end. Does not reopen anything else
this ADR or its P07-visual Amendments already decided.

**Finding.** Fix-cycle 2's own MAJOR-A fix (above) gave `ResizeOverlay`'s (`Views/ChildWindow.xaml`) four
corner `Thumb`s an explicit `Width`/`Height` of `PanelMetrics.ResizeCornerThickness` (8 DIP) while leaving
each one inside a Grid cell still sized to `PanelMetrics.ResizeEdgeThickness` (6 DIP) — relying on
corner-anchoring `Horizontal`/`VerticalAlignment` to make it "overflow" the extra 2 DIP toward the panel's
outer corner. That fix-cycle's own comment (both in `ChildWindow.xaml` and `PanelMetrics.cs`) asserted "WPF
does not clip a Grid child to its cell unless `ClipToBounds` is set, which this Grid does not." That
assertion was simply wrong for this shape of oversized-child-in-undersized-cell: WPF applies an
**automatic per-element layout clip** (`FrameworkElement`'s internal `NeedsClipBounds` mechanism) to ANY
child whose resolved `RenderSize` exceeds the arrange slot the Grid actually gave it — a mechanism entirely
separate from, and not controlled by, `Panel.ClipToBounds`, and **not bypassed by alignment**. Alignment
only decides WHICH 6×6 corner of the Thumb's 8×8 desired size survives the clip (e.g. `HorizontalAlignment
="Left"`/`VerticalAlignment="Top"` keeps the top-left 6×6 sliver) — it never prevents the clip from being
applied at all. The net effect: every corner `Thumb` on the WPF (Thumb-based) resize path rendered and
hit-tested at exactly 6×6, positioned at its edge-sized cell's own bounds — i.e. fix-cycle 1's MAJOR-1
widening (6→8) and fix-cycle 2's MAJOR-A split (corner 8 / edge 6) were both silently undone on this path
the whole time, while the native `WM_NCHITTEST` path (`NativeHitTestMapper.Map`, no such clip exists there)
genuinely used 8×8 the entire time — the two paths had actually disagreed since fix-cycle 1, undetected,
because no test exercised the REAL rendered/hit-testable geometry of a live `ResizeOverlay` Grid before now
(`PanelMetricsTests`' existing geometric pins were pure arithmetic over the constants, never the Grid).

**Fix.** `ResizeOverlay`'s outer `Grid.ColumnDefinitions`/`RowDefinitions` are now sized to a NEW
`PanelMetrics.ResizeCornerThicknessLength` (8 DIP, added alongside the existing
`ResizeEdgeThicknessLength`) instead of the edge thickness — the corner `Thumb`s now simply fill their own
DEDICATED 8×8 cell via default `Stretch` alignment, no explicit `Width`/`Height`/overflow trick needed at
all (a child that exactly matches its own cell size is never subject to the clip in the first place). The
four straight-edge `Thumb`s (Top/Bottom/Left/Right) become the SMALLER element instead — explicit
`Width`/`Height` of `PanelMetrics.ResizeEdgeThickness` (6), anchored to their own outer edge via
`Horizontal`/`VerticalAlignment` — a child SMALLER than its own Grid cell is never auto-clipped regardless
of alignment, so this renders at the exact intended 6 DIP thickness with no clip semantics involved either
way. `NativeHitTestMapper`/`NativePanelResizeController` (`EdgeDip`/`CornerDip`) are unchanged — they were
never the buggy side. `PanelMetrics.cs`'s and `RoundedRectClipConverter.cs`'s doc comments describing the
old (incorrect) "WPF does not clip... unless `ClipToBounds` is set" / stale `ResizeGripThickness` cref are
corrected in place (documentation accuracy, not a behavior change).

**TDD regression pin.** `Unit.Views.ResizeOverlayGeometryTests` (new) — a real, on-Windows STA test that
constructs an ACTUAL `ChildWindow`, calls `Window.Show()` (kept off-screen/non-activating, following
`AdaptiveIconGridPanelLayoutPolicyBindingTests`' own established precedent for why a plain
`Measure`/`Arrange` pair on an unshown `Window` does not reliably drive a real layout pass), and asserts
against the REAL `ResizeOverlay` Grid's 8 `Thumb` children: all four corners are exactly
`ResizeCornerThickness` (8×8), `LayoutInformation.GetLayoutClip` returns `null` for every one of them (no
auto-clip), and each corner's `TranslatePoint`-resolved top-left sits exactly at the panel's own geometric
corner. Confirmed red on the pre-fix XAML (corner `ActualWidth`/`ActualHeight` measured 6, not 8) and green
after. A real-`Application`/real-`Show()` test infrastructure bug was found and fixed in the same pass
(not a product bug): this class' own `Application`-thread-affinity/`ShutdownMode`/cross-thread-`Window`
handling — see the test class' own remarks and `docs/PLANS/workspace-grid-07-visual.md`'s new section for
the full incident writeup; a naive first draft crashed the ENTIRE `dotnet test` host process partway
through a full-suite run (a leaked, unrelated `ChildWindowViewModel` existence-check timer from some other,
pre-existing test threw an unhandled exception invoking onto a dispatcher this test's own throwaway thread
had by then torn down) — fixed before this fix was finalized; 3119/3119 (3117 baseline + 2 new) green,
repeated across multiple full-suite runs.

## Manual verify (Win10/Win11) — добавлено P09

In addition to fix-cycle 2's own two checklist items above (still open, unrelated numerically-bounded
scrollbar/header-button overlap): confirm a diagonal mouse-down at the panel's literal geometric corner (a
1-2 DIP tolerance around it) reliably starts a diagonal resize gesture (`SizeNWSE`/`SizeNESW` cursor) on the
DEFAULT (Thumb-based, `NativePanelSizing=False`) path — this is the specific, previously-silently-broken
behavior this fix restores; a screen recording/zoomed screenshot comparing the corner grip's actual
catchable area before/after, at 100%/125%/150% DPI, is the highest-value manual check for this fix.

---

## Amendment 2026-09-25 (ADR-030)

Короткая сверка после `adr/ADR-030-reference-panel-chrome.md` (Accepted, ветка `feature/reference-chrome`,
`af2c235` + round-2 polish) — full detail в этом ADR-030, не повторяется здесь. Не переоткрывает ничего из
решений этого документа выше.

- **D-5 (ADR-030) закрывает click-through-линзу MAJOR-1 на обоих путях.** Fix-cycle 1's MAJOR-1 finding
  above (OS-level per-pixel-alpha click-through у угла скруглённого `Border` на `AllowsTransparency="True"`
  окне) был смягчен только шириной resize-грипа. ADR-030's `PART_HitCatcher` (почти прозрачный,
  alpha=1/255, `IsHitTestVisible=False`, во весь прямоугольник окна) закрывает саму линзу целиком — и на
  WPF `Thumb`-пути, и на нативном (`NativeHitTestMapper`) пути — независимо от ширины грипа.
- **Колонка акцента 8 DIP → симметричные отступы 2|*|2.** `PART_AccentIndicator`'s прежняя фиксированная
  LEFT-only `ColumnDefinition.Width=8` (MAJOR-1 этой фазы, «сама широкая колонка с запасом под 6.885»)
  удалена из раскладки колонок. Тот же суммарный горизонтальный чром (`ContentLeftInset=8`,
  `GridMetrics.FileListHPadding` — не изменились численно) теперь распределён как `ChromeOuterInset (2) +
  ContentSidePadding (2)` с каждой стороны — контент визуально сдвинут на 4 DIP влево относительно
  прежнего асимметричного варианта, но ширина/snap-математика не изменились.
- **`PART_AccentIndicator` теперь `Path`, не прямоугольник.** Имя части шаблона сохранено; вместо
  прямоугольной полосы — изогнутая геометрия (вертикальный ствол + четверть окружности `Radius.Panel` +
  короткий хвост), клипованная той же скруглённой геометрией, что и поверхность. Толщина по-прежнему через
  `LayoutEditVisuals.AccentEmphasisFor` (тот же канал, что дал 3.0→4.5→5.1px выше).
- **F-1 doc-drift (найдено, не исправлено в ADR-030's scope).** Комментарий `GridMetrics.FileListVPadding`
  и комментарий в `ChildWindow.xaml` над `FileListHost` описывают `Margin="0,0,0,4"`; фактический XAML —
  `Margin="0,0,0,0"`. Не относится к решениям этого ADR-025, зафиксировано как `docs/Refactoring/BACKLOG.md`
  DO-022.
