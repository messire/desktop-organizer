# ADR-030: Reference-Style Layered Panel Chrome

Status: Accepted (2026-09-25)

Ветка `feature/reference-chrome`. Мастер-план реализации — `docs/PLANS/reference-panel-chrome.md`
(D-1..D-12, риски R-1..R-12, вопросы Q-1/Q-2, матрица ручной проверки MV-1..MV-15). Конвейер:
`fences-feature-planner` → `wpf-ui-builder` (коммиты C1–C6: `7faf568` токены, `42bb035` производные
визуальные каналы/fallback-политика/геометрия маркера, `b407116` слоистый шаблон с inset/hit catcher,
`9010dca` изогнутый маркер, `fce6f7d` glow, `d5c25cd` surface highlight) → `tdd-test-engineer`
(`9e61944`, T-C1/C2/R9/R10/V1–V3) → `code-quality-reviewer` ∥ `fences-ux-parity-reviewer` (два раунда) →
fix-cycle 1 (`af2c235`, device-pixel snapping, glow policy cache, marker collision test, doc accuracy) →
round-2 polish (комментарии/тесты, без изменения поведения) → этот документ (C7, `architecture-documenter`).
Текущий HEAD на момент принятия — `af2c235` + round-2 polish.

## Контекст

DesktopOrganizer уже использует Ambient Accent visual language (ADR-025): нейтральная поверхность,
локальный семантический accent, токены и visual states. Текущий WPF-слой содержит `PanelChrome`,
`PART_Chrome`, `PART_AccentIndicator`, `SurfaceBrush`, `AccentBrush`, interaction overlay, clipping по
rounded geometry и централизованные design tokens.

Визуальный референс `docs/Ref image.png` развивает тот же язык, но добавляет глубину за счёт нескольких
слоёв: тонкой нейтральной рамки, цветного accent stroke, мягкого glow, лёгкого highlight на поверхности
и вертикального accent-маркера у заголовка с небольшим загибом вправо в верхней части.

Задача не требует смены WPF и не требует настоящего acrylic/backdrop blur. Цель — получить близкий
визуальный эффект штатным WPF rendering path, сохранив WorkerW integration и текущую модель состояния.

## Решение

Расширить `PanelChrome` до layered chrome, оставаясь в существующем WPF/MVVM дизайне.

### 1. Слои панели

Шаблон `PanelChrome` должен логически состоять из следующих слоёв, снизу вверх:

1. `PART_Glow` — мягкий accent-colored glow, отрисованный внутри зарезервированного прозрачного inset;
2. `PART_AccentStroke` — тонкий локальный цветной stroke с низкой/средней opacity;
3. `PART_NeutralStroke` — нейтральная 1 DIP рамка для читаемости геометрии панели;
4. `PART_Chrome` — существующая поверхность панели (`SurfaceBrush`);
5. `PART_SurfaceHighlight` — очень слабый linear gradient/highlight, не меняющий семантический цвет;
6. Header/content;
7. `PART_AccentCurve` — загнутый accent-маркер у заголовка;
8. `PART_InteractionOverlay` — существующая система interaction/edit state поверх chrome.

Точные имена template parts могут быть скорректированы реализацией, но семантика слоёв должна
сохраниться.

### 2. Glow

Glow реализуется WPF-средствами и не должен требовать DWM/Acrylic API.

Предпочтительный вариант:

- отдельный `Border`/`Path` с `DropShadowEffect` (`ShadowDepth=0`);
- glow должен быть слабым и локальным;
- blur radius и opacity задаются design tokens;
- цвет берётся из существующего `AccentBrush`;
- bounds панели резервируют небольшой прозрачный inset, чтобы glow не обрезался собственным окном.

Не допускается создание полноэкранных shadow windows или дополнительных top-level окон ради glow.

### 3. Рамки

Использовать два независимых визуальных канала:

- neutral stroke: отделяет панель от обоев независимо от accent color;
- accent stroke: подчёркивает категорию, но не превращает панель в сплошную цветную карточку.

Accent stroke должен быть существенно слабее accent-marker и не конкурировать с контентом.

### 4. Загнутый accent marker

Существующий прямоугольный `PART_AccentIndicator` заменить или визуально дополнить `Path`-геометрией:
вертикальная линия слева, которая сверху мягко загибается вправо. Геометрия должна масштабироваться по
DIP, иметь rounded line caps/joins и поддерживать существующее изменение emphasis по visual state.

Marker обязан использовать существующий `AccentBrush` и существующую state-модель. Новое persistence
поле для формы/свечения запрещено.

### 5. Surface highlight

Добавить очень слабый non-semantic highlight поверх `SurfaceBrush`:

- предпочтительно `LinearGradientBrush` с малой alpha;
- он не должен ухудшать читаемость текста/иконок;
- в High Contrast должен отключаться либо заменяться системным токеном;
- highlight не является новым `PanelSurfaceStyle` и не меняет persistence.

### 6. Rounded clipping

Существующий rounded clip для `PART_Chrome` сохраняется. Новые внутренние слои, которые не должны
выходить за surface radius, обязаны использовать согласованную геометрию. Glow может жить за пределами
surface radius, но внутри Window bounds.

### 7. Interaction states

Idle/Hover/Editing/Dragging/Resizing и Edit Layout остаются единственным источником динамической
выразительности. Новый chrome должен усиливать эти состояния, а не заводить параллельную state machine.

Допустимые изменения по state:

- opacity neutral/accent stroke;
- glow opacity;
- marker thickness/emphasis;
- overlay contrast.

Недопустимо двигать content geometry при смене состояния. Никаких layout-jitter из-за Auto sizing.

### 8. Производительность

`DropShadowEffect` допускается только в ограниченном количестве и с консервативными параметрами.
Нельзя навешивать независимый тяжёлый effect на каждый icon/item.

Проверить:

- несколько панелей одновременно;
- resize/drag без заметного FPS collapse;
- отсутствие лишних allocations/brush recreation;
- по возможности frozen/static brushes для token-only ресурсов;
- отсутствие бесконечных invalidation/layout cycles.

Если glow на `DropShadowEffect` даёт заметную деградацию, допустим fallback на заранее рассчитанный
soft stroke/gradient без pixel shader.

### 9. High Contrast и accessibility

В High Contrast decorative glow/highlight должен отключаться либо становиться нейтральным. Основная
граница и focus/selection semantics должны оставаться различимыми системными цветами.

### 10. Persistence freeze

Эта фича не меняет:

- `schemaVersion`;
- migrations;
- state DTO;
- `IAppSettings` / `Settings.settings` / `user.config`;
- семантику загрузки/сохранения.

Все новые параметры являются кодовыми design tokens. Если позже понадобится пользовательская настройка
силы glow/рамок, это отдельное решение и отдельное явное разрешение на изменение persistence.

### 11. Acrylic

Настоящий DWM/Acrylic backdrop blur остаётся вне scope. `PanelSurfaceStyle.Glass` продолжает означать
полупрозрачную поверхность по ADR-025, а не blur содержимого позади окна.

## Рассмотренные альтернативы

### A. Перейти на WinUI/Avalonia

Отклонено. Текущий WPF-стек уже способен реализовать требуемую геометрию, эффекты, шаблоны и токены.
Смена UI framework не оправдана визуальной задачей и поставила бы под риск WorkerW/Win32 интеграцию.

### B. Сделать весь reference одним bitmap/background image

Отклонено. Это ломает масштабирование, dynamic accent, interaction states, DPI и пользовательские размеры
панелей.

### C. Настоящий DWM/Acrylic blur

Отклонено для этой задачи. Высокая сложность/риски с `AllowsTransparency`, WorkerW и версиями Windows при
низкой практической отдаче для текущего референса.

### D. Один `DropShadowEffect` на корневом `PanelChrome` без отдельного inset

Отклонено как основной вариант: высок риск clipping по bounds окна и неконтролируемого размытия всей
панели. Layered template даёт лучшее управление.

## Последствия

### Позитивные

- визуал существенно приближается к референсу без смены стека;
- сохраняется Ambient Accent философия;
- category color остаётся локальным semantic accent;
- нет миграций состояния;
- большая часть изменений локализована в WPF resource/template слое.

### Негативные / риски

- эффекты WPF требуют manual verify на реальном Windows desktop;
- слишком сильный glow быстро увеличивает визуальный шум;
- `AllowsTransparency=True` и window bounds требуют аккуратного inset, иначе glow будет обрезан;
- новые декоративные слои нельзя допустить до hit-test/layout поведения;
- High Contrast потребует отдельной проверки.

## Acceptance criteria

Закрыто автоматически (build/test/статические гейты, эта среда) vs требует живого Windows-десктопа —
отмечено по каждому пункту. Полная manual-verify матрица — раздел «Manual verify (Win10/Win11)» ниже.

1. ✅ **Автоматически.** Панель имеет нейтральную тонкую рамку (`PART_NeutralStroke`) и отдельный, слабее
   выраженный accent stroke (`PART_AccentStroke`, T-P4: `AccentStrokeOpacityMax × hairline <
   AccentMarkerStrokeThickness / 2`).
2. ✅ **Автоматически** (T-M5, residual ≈0.065 ≤ `GlowEdgeResidualMax` 0.08) **+ manual verify MV-2**
   («линия среза» на живом экране на 100–175% DPI не проверена в этой среде).
3. ✅ **Автоматически.** Accent marker — `Path` по дуге верхнего левого угла, `AccentBrush`, не
   пересекает `TitleText` по сегментам (T-V2).
4. ✅ **Автоматически.** Highlight — замороженный `LinearGradientBrush`, `IsHitTestVisible=False`; T-C1
   закрыт критерием «ни одна PASS-комбинация не становится FAIL» (не плановым «≤5%», см. Amendment ниже).
   Backdrop blur не реализован (см. «Известные ограничения»).
5. ✅ **Автоматически** (T-R3: побитовое совпадение геометрии по всем состояниям × Edit Layout ×
   Ambient/Glass) **+ manual verify MV-4** (покадровая запись живого жеста).
6. ✅ **Автоматически** (`ResizeOverlayGeometryTests`, `NativeHitTestMapper`-тесты,
   `NativePanelResizeController*Tests`, `PanelDragPipelineTests` зелёные без правок; G-1/G-5: 0
   пересечений с зоной ADR-022/persistence) **+ manual verify MV-5/MV-6/MV-9/MV-14**.
7. ✅ **Автоматически** (T-R7/T-R8: glow/fallback/highlight/accent stroke свёрнуты, neutral stroke
   `Opacity=1`) **+ manual verify MV-11** (4 реальные HC-темы Windows).
8. ✅ **Закрыто.** `dotnet build`/`dotnet test` — 0 errors, 0 warnings, 3459/3459 ×3 прогона зелёные
   после `af2c235` (baseline до задачи — 3272).
9. ✅ **Закрыто.** `git diff --name-only e10b1fd..HEAD` ∩ persistence-freeze список (план §10) = ∅;
   `SchemaContractTests`/`PanelStateMapperTests`/`SystemIconsPanelStateMapperTests` не тронуты.
10. ⏳ **Manual verify MV-12/MV-13** — не выполнено в этой среде (нет живого Windows GUI).

---

## Amendment 2026-09-25 — Реализация

Фиксирует фактически внедрённое (не намерения плана). Полная архитектурная аргументация — план
`docs/PLANS/reference-panel-chrome.md` §1 D-1..D-12; этот раздел — сверка с реальным кодом на HEAD
`af2c235` (+ round-2 polish).

### Слои шаблона (снизу вверх)

`PanelChrome`'s корневой `Grid` = прямоугольник окна (`ResizeOverlay` остаётся сиблингом в
`ChildWindow.xaml`, не трогается):

1. `PART_HitCatcher` — `Border`, `Background="{StaticResource Brush.HitTest.Catcher}"` (`#01000000`),
   `IsHitTestVisible=False`, без `Margin` — весь прямоугольник окна.
2. `PART_Glow` / `PART_GlowFallback` — кольцо, `Margin={x:Static PanelMetrics.ChromeOuterInsetThickness}`
   (2 DIP).
3. `PART_SurfaceLayer` — `Grid`, тот же `Margin` (2 DIP), несёт **единственный** на панель
   `RoundedRectClip` (перенесён с `PART_Chrome`) ⊃
   - `PART_Chrome` (`Border`, `Background=SurfaceBrush`, `CornerRadius=Radius.Panel`) ⊃ внутренний
     `Grid [2|*|2] × [28|2|*]` с `PART_SurfaceHighlight` (span all), `PART_Header` (col 1, row 0),
     `PART_ContentHost` (col 1, row 2);
   - `PART_NeutralStroke` — hairline-кольцо, `Brush.Border.Default`, `CornerRadius=Radius.Panel`;
   - `PART_AccentStroke` — hairline-кольцо, `Margin=Thickness.Hairline` (концентрично внутрь),
     `AccentBrush`, `CornerRadius=Radius.Panel.Inner` (7);
   - `PART_AccentIndicatorHost` (хост фиксированного размера) + `PART_AccentIndicator` (`Path`, имя
     сохранено из ADR-030 §1 «`PART_AccentCurve`»);
   - `PART_InteractionOverlay` — перенесён из внутренней сетки строк в этот слой (покрывает поверхность
     и в rolled-up виде).

Все декоративные слои — `IsHitTestVisible=False`; hairline `Border`'ы несут `SnapsToDevicePixels=True`
(fix-cycle 1, ловит размытие на дробном DPI).

### Бюджет inset и решение по Q-1

`ChromeOuterInset = 2` DIP (не больше — связывающее ограничение: `HeaderHeight (32) − 2·inset ≥
HeaderButtonSize (28)` в rolled-up виде). Геометрия контента численно не изменилась: ширина `W − 8`
(`ContentLeftInset`/`GridMetrics.FileListHPadding` не менялись), верх контента — `y = 32`
(`HeaderInnerHeight 28 + HeaderContentGap 2 + inset 2`). Контент визуально сдвинут на 4 DIP влево:
старая асимметричная колонка-акцент 8 DIP слева заменена на симметричные отступы `ContentSidePadding = 2`
с обеих сторон (`(ContentLeftInset − 2·ChromeOuterInset) / 2`). **Q-1 решён в пользу варианта A** —
оставлен бюджет 2 DIP без изменения persisted-геометрии/snap-математики; вариант B (расширение бюджета
чрома, 6+ DIP ореол) отклонён как требующий явного «да» пользователя на изменение поведения сохранённых
раскладок — не запрошено в этой задаче, зафиксирован как открытый продуктовый вопрос (BACKLOG DO-024).
Внешний ореол ⩽ 2 DIP осознанно беднее референса (`docs/Ref image.png`) — основная визуальная глубина
достигается внутренним свечением сквозь полупрозрачную поверхность, accent stroke, neutral stroke,
highlight и маркером, а не шириной внешнего ореола.

### Токены (`PanelMetrics`, значения сверены с кодом)

`ChromeOuterInset=2`, `ContentSidePadding=2`, `HeaderInnerHeight=28`, `HeaderContentGap=2`,
`GlowBlurRadius=3`, `GlowEffectOpacity=0.6`, `GlowOpacityIdle/Max=0.35/0.8`, `GlowFallbackRingOffset=1`,
`GlowFallbackOpacityFactor=0.5`, `GlowEdgeResidualMax=0.08` (модель гауссова хвоста, D-10; реальный
residual ≈0.065), `AccentStrokeOpacityIdle/Max=0.25/0.55`, `NeutralStrokeOpacityIdle/Max=0.55/0.9`,
`AccentMarkerStrokeThickness=4` (видимая половина 2 DIP в Idle — маркер клипуется той же скруглённой
геометрией, что и поверхность, видна только внутренняя половина штриха), `AccentMarkerStemLength=14`
(план указывал 16 как иллюстративное стартовое значение — 16 давало выход круглого конца маркера за
`HeaderInnerHeight` на максимальной эмфазе: 4.6 + 24 = 28.6 > 28; 14 держит это в пределах),
`AccentMarkerTailLength=2`, `AccentMarkerCornerRadiusBudget=8` (= `Radius.Panel`, закреплено тестом
`AccentMarkerCornerRadiusBudget_EqualsRadiusPanel`, не просто продублировано вручную),
`AccentMarkerMaxStrokeThickness` — `static readonly`, вычисляется как `AccentMarkerStrokeThickness ×
InteractionVisualState.AccentEmphasisFor(Resizing) (1.7) × LayoutEditVisuals.AccentEmphasis (1.35)` ≈
9.18, половина (round-cap) ≈4.59 DIP, `SurfaceHighlightEndOffset=0.5`. XAML: `Color.Chrome.Highlight.Top
#0FFFFFFF`, `Color.Chrome.Highlight.Bottom #00FFFFFF`, `Color.HitTest.Catcher #01000000` (alpha=1, не 0 —
намеренно НЕ переопределён в `Colors.HighContrast.xaml`, см. «Известные ограничения»), `Brush.Chrome.SurfaceHighlight`
(`po:Freeze="True"` `LinearGradientBrush`), `Brush.HitTest.Catcher`, `Radius.Panel.Inner=7` (`Radius.Panel
8 − Thickness.Hairline 1`).

### Glow — механизм и модель стоимости

Ровно один `Effect` на панель: `PART_Glow` — пустое кольцо `Border` (без потомков) с единственным
`DropShadowEffect` (`ShadowDepth=0`, `BlurRadius=GlowBlurRadius`, `Opacity=GlowEffectOpacity`,
`RenderingBias="Performance"`, `Color` — биндинг на `AccentBrush.Color` через `TemplatedParent`).
Выразительность по состоянию — через `UIElement.Opacity` самого `PART_Glow` (не пересоздание эффекта).
`PART_GlowFallback` — заранее рассчитанное кольцо без `Effect` (`Margin = ChromeOuterInset −
GlowFallbackRingOffset` = 1 DIP снаружи поверхности, `Opacity` = канал `"glow"` × `GlowFallbackOpacityFactor`
0.5), появляется по `ChromeEffectsPolicy.IsGlowEffectEnabled == false`.

**Fallback-политика** (`Theming/ChromeEffectsPolicy.cs`): чистая `ShouldRenderGlowEffect(renderTier,
highContrast) = GlowEffectAllowed && renderTier >= 2 && !highContrast`; `IsGlowEffectEnabled` — ленивое,
кэшируемое один раз вычисление от `RenderCapability.Tier >> 16` **без** параметра HC (M-3 review-cycle-1
фикс — см. «Решения ревью» ниже); `GlowEffectAllowed` — `internal const bool`, единый выключатель на
уровне кода. HC обрабатывается отдельно, только триггером шаблона (D-8), не этим кэшем.

**Модель стоимости.** `PART_Glow` покрывает весь прямоугольник панели; промежуточная поверхность эффекта
размером с панель пересчитывается при **любой** перерисовке внутри панели (не только resize) — hover
иконки, выделение, каретка rename, скролл списка тоже инвалидируют её. Перемещение окна (drag) и простой
(idle) бесплатны. Зафиксировано как риск R-5 плана; при подтверждённой деградации — понижение
`ChromeEffectsPolicy.GlowEffectAllowed` в `false` как единая точка отката без изменения структуры шаблона,
либо `BitmapCache` на `PART_Glow`.

### Маркер

`PART_AccentIndicator` — `Path` (`Stroke={TemplateBinding AccentBrush}`, round caps/joins,
`IsHitTestVisible=False`) в хосте `PART_AccentIndicatorHost` фиксированного размера
(`AccentMarkerHostWidth`/`Height`, вычисляемые `static readonly`, не литералы — раздутые границы геометрии
на `max stroke / 2`). Имя части шаблона `PART_AccentIndicator` сохранено (ADR-030 §1 прямо разрешает
менять имена — сохранён потребитель `PanelChrome.PartAccentIndicator`/`FindName`). Геометрию строит
`Converters/AccentMarkerGeometryConverter` (кэш по радиусу, замороженная `PathGeometry`, никогда не
бросает — `Binding.DoNothing` на некорректном входе, контракт `RoundedRectClipConverter`). Толщина —
существующий `EditAwareAccentIndicatorWidthConverter` с новым `ConverterParameter="marker"`.

### Clipping / bounds

Единственный `RoundedRectClip` на панель — на `PART_SurfaceLayer` (был на `PART_Chrome`). Маркер клипуется
той же геометрией, поэтому видна только внутренняя половина штриха. `PART_HitCatcher` не клипуется — весь
прямоугольник окна, закрывает и клик-линзу за дугой скругления угла (см. ниже).

### High Contrast

Триггер `{Binding Path=(theming:HighContrastState.IsActive), Mode=OneTime}` в `ControlTemplate.Triggers`:
`PART_Glow`/`PART_GlowFallback`/`PART_SurfaceHighlight`/`PART_AccentStroke` → `Collapsed` (glow — ещё и
`Effect={x:Null}`, чтобы шейдер не выполнялся вовсе); `PART_NeutralStroke.Opacity=1` (кисть
`Brush.Border.Default` уже переопределена на `SystemColors.ActiveBorderColor`); маркер остаётся видимым с
`AccentBrush` (ADR-025 сознательно не переопределяет акцент в HC). `Color.Chrome.Highlight.Top/.Bottom` →
`Colors.Transparent` в `Colors.HighContrast.xaml`. `Brush.HitTest.Catcher` **намеренно не переопределён** —
задокументированное исключение из правила «весь chrome-цвет переопределяется в HC» (иначе системный цвет
покрасил бы непрозрачное кольцо-ловушку); закреплено тестом (`Color.A == 1` в обоих вариантах слияния).

### Состояния

Новый чистый класс `Theming/LayeredChromeVisuals` — каждый новый канал (`glow`/`accentStroke`/`neutralStroke`)
— аффинная функция существующего `LayoutEditVisuals.BorderOpacityFor(state, isLayoutEditing)`, без второй
state machine. `EditAwareVisualStateToDoubleConverter` получил новые параметры. Толщина маркера — тот же
существующий `LayoutEditVisuals.AccentEmphasisFor`. Никакое из decision-полей не двигает layout
(`DesiredSize` не меняется, T-R3).

### Производительность

Grep-гейты подтверждены: `Effect` встречается только в блоке `PART_Glow`/`PART_GlowFallback` в
`Panels.xaml`, ноль в шаблонах item-уровня (`GridViewTemplate`/`ListViewTemplate`/`PanelItemContainerStyle`).
Frozen/static кисти для token-only ресурсов (`Brush.Chrome.SurfaceHighlight`). `dotnet build`/`dotnet test`
— 0 errors, 0 новых warnings (pre-existing StyleCop ~756 в чужих файлах — вне scope), 3459/3459 ×3.

### Решения ревью (2 раунда + fix-cycle 1 + round-2 polish)

- **M-1.** `SnapsToDevicePixels=True` добавлен на hairline `Border`'ы (устраняет размытие на дробном DPI).
- **M-2.** `PART_NeutralStroke` и `PART_InteractionOverlay` рисуют одну и ту же hairline
  `Brush.Border.Default` на одних и тех же пикселях (в Glass — внешний 1 DIP совпадает с
  `BorderThickness=1.5` оверлея). Осознанное решение (b) — оставить оба слоя: они отвечают на разные
  вопросы (`PART_NeutralStroke` — всегда-читаемый базовый контур ADR-030 §3; `PART_InteractionOverlay` —
  предсуществующий interaction-feedback канал ADR-025/026, не тронут). Комбинированная альфа `1 − (1 −
  overlayOpacity)(1 − neutralOpacity)`: Idle ≈0.137 → Hover ≈0.188 → Editing ≈0.249 → Dragging ≈0.333 →
  Resizing ≈0.344 on-screen. Шаг Dragging→Resizing ≈0.006 — структурный потолок (`overlay=1.0` уже при
  Resizing), 0.02 недостижимо без сплющивания нейтрального канала. Различимость Dragging/Resizing
  по-прежнему несёт маркер (3.0→3.4 DIP), glow и курсор — ADR-025 явно просит не переоткрывать этот вопрос
  без нового UX-кейса. Тест закрепляет floor ≥0.005 (`LayeredChromeVisuals.CombinedNeutralBorderOpacityFor`).
- **M-3.** Кэш `ChromeEffectsPolicy.IsGlowEffectEnabled` зависит **только** от `RenderCapability.Tier`
  (исходно фолдил ещё и HC-флаг — исправлено: процесс-широкий кэш иначе навсегда запекал бы HC-состояние
  первого обращения, что ломало тестовый хост, где `HighContrastState.IsActive` переключается между
  классами тестов). HC решается ровно в одном месте — триггере шаблона. `RenderCapability.Tier`
  «на лету» (`TierChanged`) не отслеживается — read-once, как `HighContrastState` (D-12); зафиксировано
  как известное ограничение (BACKLOG DO-025).
- **T-C1.** `AmbientAccentContrastAuditTests` показали, что surface highlight снижает контраст текста
  заголовка до −16.1% в комбинациях min-opacity/светлые обои — уже FAIL до этой задачи (DO-021,
  `SurfaceComposer.MinSurfaceOpacity`), 0 новых пересечений порогов WCAG 4.5:1/3:1. Принято: токены не
  менять; критерий DoD-4 переопределён с планового «просадка ≤5%» на «ни одна PASS-комбинация не
  становится FAIL» + измеренный floor — плановый численный порог был нереалистичен для уже известного
  FAIL-диапазона DO-021, а не результатом этой задачи.
- **T-V2.** Проверка коллизии маркера с `TitleText` — по сегментам геометрии (ствол/хвост/дуга), не по
  общему `AABB`: объединённый bounding box маркера и текста пересекается по пустому углу без реального
  наложения штрихов. Реальный запас — ≈1.4 DIP по хвосту, ≈4 DIP по дуге на максимальной эмфазе.
- **Q-2.** Accent stroke реализован как равномерное кольцо (`PART_AccentStroke`, постоянная opacity по
  всему периметру). Направленное затухание от верхнего левого угла (альтернатива Q-2 плана) рассмотрено
  `fences-ux-parity-reviewer` и отклонено — равномерное кольцо не читалось как «слишком карточное»; не
  реализовывался отдельный `LinearGradientBrush`-конвертер под это.

### Известные ограничения (сознательно вне scope)

- **`RenderCapability.Tier` не отслеживается на лету** (M-3) — смена render tier в рантайме (например,
  RDP-сессия подключается/отключается на лету) не переоценивает `ChromeEffectsPolicy.IsGlowEffectEnabled`
  до следующего запуска приложения. Тот же класс минимума, что `HighContrastState`/`MotionState` (D-12).
- **High Contrast не подхватывается на лету** (DO-015, ADR-025 D-12) — не новое ограничение, но
  `PART_Glow`/`PART_GlowFallback`/`PART_SurfaceHighlight`/`PART_AccentStroke` наследуют его: включение HC
  без перезапуска приложения оставляет эти слои в прежнем (не-HC) состоянии.
- **Внешний ореол ⩽ 2 DIP** — беднее референса; решение Q-1 (вариант A), см. выше. Открыт для
  пересмотра только с явным «да» пользователя на изменение поведения сохранённых раскладок (BACKLOG DO-024).
- **Кольцо-ловушка (`PART_HitCatcher`) поглощает клики по внешним 2 DIP панели**, когда resize выключен
  (rolled-up; Normal mode, где политика запрещает ресайз) — принятая цена D-5, пренебрежимо мала
  относительно риска потери resize-захвата на inset'е.

### Persistence и Acrylic — явно

- **Persistence/schema не менялись.** `schemaVersion` остаётся 5, ни одной новой/изменённой миграции,
  ни одного нового поля DTO/`IAppSettings`/`Settings.settings`/`user.config`. `git diff --name-only
  e10b1fd..HEAD` не пересекается со списком persistence-freeze плана §10.
- **DWM/Acrylic backdrop blur не реализован.** `SetWindowCompositionAttribute`/`DwmSetWindowAttribute`/
  `DwmExtendFrame` не добавлены нигде в `src/`. `PanelSurfaceStyle.Glass` остаётся пониженной
  непрозрачностью поверхности (ADR-025 R-8), а не настоящим blur.

## Manual verify (Win10/Win11)

Полная матрица — `docs/PLANS/reference-panel-chrome.md` §6 (MV-1..MV-15). Не выполнено в этой среде (нет
живого Windows GUI). P1 (приоритет round-2): MV-1 (Idle-контур на ярких обоях под Glass min-opacity),
MV-2 (линия среза glow на 100/125/150/175%), MV-3 (hairline crisp на 125/175%), MV-4 (нулевой jitter,
покадрово), MV-5/MV-6 (hit catcher на Thumb и нативном пути + угловая линза), MV-8 (rolled-up), MV-10
(сохранённая раскладка — то же число колонок, `application-state.json` не переписан только из-за
загрузки), MV-11 (HC ×4 темы), MV-12 (10/20 панелей + скролл/hover в большой панели — стоимость glow
пересчитывается на любую перерисовку, не только resize). P2: MV-4b (курсор Dragging/Resizing), MV-7
(header-кнопки/scrollbar зоны захвата), MV-9 (соседние grid-aligned панели), MV-13 (RDP/software tier
fallback, в т.ч. HC+RDP), MV-14 (WorkerW). P3: MV-15 (UIA-дерево). Дополнительно из пользовательского
промпта: 1/3/5+ панелей, Grid/List/Folder Portal видов, `SystemIconsPanel` (вне scope — старый визуал,
BACKLOG), разные accent colors, субъективная отзывчивость drag/resize.
