# Мастер-план Фазы 5 — Ambient Accent Visual System

> Источник правды: `docs/Refactoring/PHASES/PHASE-5-ambient-accent-ui.md`, `adr/ADR-025-ambient-accent-visual-language.md` (Proposed),
> `docs/Refactoring/ROADMAP.md` (строка Phase 5), `docs/Refactoring/BACKLOG.md` (DO-003), референс
> `docs/Refactoring/Reference/ambient-accent-concept.png`.
> Ветка: `ui/ambient-accent-design-tokens` (от `rc/0.4.0`). Дата: 2026-09-21.
> Общий scope фазы: **XL** (UI + persistence-схема + миграция + settings UI). Разбит на 8 логических коммитов C1–C8.
>
> **Главная особенность фазы:** это визуальная фаза. В этой среде (нет живого Windows GUI) проверяемы только
> компиляция XAML, чистая C#-логика и persistence-миграция. Соответствие референсу, DPI-рендеринг и
> high-contrast — **manual verify (Win10/Win11)**. Раздел §7 явно делит работы на две корзины.

---

## 0. Границы фазы — что НЕ трогаем

Жёсткие запреты (нарушение = regression в уже закрытых фазах):

| Не трогать | Почему |
|---|---|
| `Interaction/PanelInteractionTracker.cs`, `PanelInteractionState.cs`, `PanelInteractionTrackerTests` (в т.ч. T-TRK-08) | ADR-021. `Hover`/`Editing` — намеренно «мёртвые» состояния до Фазы 6; T-TRK-08 сторожит это. Фаза 5 **визуализирует** состояния, но не заставляет трекер в них входить (см. D-6). |
| `Behaviors/PanelResizeController.cs`, `NativePanelResizeController.cs`, `ResizeGestureSession.cs`, `ResizeGeometryCalculator.cs`, `Interaction/Native/*` | Фазы 1–2, ADR-021/ADR-022, bit-for-bit golden-эквивалентность. Меняем только **визуальный слой** `ResizeOverlay` (стиль Thumb), не геометрию/захваты. |
| `Behaviors/PanelDragMoveBehavior.cs`, `PanelTitleRenameBehavior.cs`, `RenameEditBehavior.cs` | Rename/drag-поведение сохраняется дословно (спец. требование §5.4 «title retains rename behavior»). |
| `Behaviors/RollUpBehavior.cs` (логика) | Фаза 1, M-6 generation-guard. Меняется **только** источник `titleBarHeight` (см. D-11), тело метода — нет. |
| `ViewModels/ChildWindowViewModel.cs` | DO-002, 973 строки. Фаза 5 не декомпозирует его и **не добавляет в него свойств** — всё новое проходит либо через `ChildWindowModel` (INPC), либо через attached DP/конвертеры. Единственное допустимое касание — ноль строк (цель). |
| `GridMetrics.CellSize/RowHeight/MinPanelWidth` формулы | Grid-snap математика Фаз 1–2. Если типографика меняет высоту лейбла — см. R-4, менять `LabelHeight` + XAML в связке, с тестом. |

---

## 1. Решения (ADR-025 детализация)

### D-1. Раскладка словарей и порядок merge

Новый каталог `src/DesktopOrganizer.UI/Resources/Styles/`:

```text
Resources/
  Styles/
    Colors.xaml        — только <Color>, никаких Brush/Style
    Spacing.xaml       — <sys:Double>, <CornerRadius>, <Thickness>, <GridLength>
    Brushes.xaml       — <SolidColorBrush> над Colors.xaml, все Freeze="True"
    Typography.xaml    — TextBlock/TextBox-стили над Brushes.xaml
    Icons.xaml         — <sys:String> глифы Segoe MDL2 (+ опц. Geometry)
    Controls.xaml      — Button/ContextMenu/MenuItem/Popup-стили
    Panels.xaml        — PanelChrome default style, item container, accent indicator
    Colors.HighContrast.xaml — overlay, мержится условно (D-12)
  Templates.xaml       — существующий, ужимается (см. D-2)
```

**Порядок merge в `App.xaml`** (строго): `Colors → Spacing → Brushes → Typography → Icons → Controls → Panels → Templates`.
Причина: `StaticResource` между merged-словарями резолвится только «назад» по порядку мержа — словарь,
объявленный позже, видит ключи ранее смерженных, но не наоборот. Нарушение порядка = `XamlParseException`
на старте, а не ошибка компиляции.

**Проверяемо здесь:** `dotnet build` ловит синтаксис и неразрешимые типы; неверный порядок merge ловится
только в рантайме → обязателен smoke-запуск на Windows (V-1).

**Подводные камни, которые обязаны быть учтены в коде (черновики в `docs/Refactoring/UI/` их содержат):**

1. `xmlns:sys` черновика — `clr-namespace:System;assembly=mscorlib`. На `net8.0-windows` использовать
   `assembly=System.Runtime`; `mscorlib` работает через type-forwarding, но это не гарантированный контракт
   markup-компилятора. Объявлять `xmlns:sys` **на корне** словаря, не на каждом элементе.
2. Все кисти — `po:Freeze="True"` (`xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"`,
   `mc:Ignorable="po"`). Прецедент производительности уже заложен в `HexBrushConverter`/`ColorOpacityBrushConverter`
   (Фаза 7 hardening, Block A) — незамороженные кисти в ресурсах обнулили бы эту работу.
3. Именование ключей — точка-нотация черновиков (`Color.Surface.Base`, `Brush.Text.Primary`,
   `Typography.PanelTitle`, `Space.8`, `Radius.Panel`). Не смешивать со старым стилем `PanelHeaderButtonStyle`;
   старые ключи, остающиеся в `Templates.xaml`, **переименовать** на `Panel.*`/`Ambient*` в том же коммите,
   где меняются их потребители.

### D-2. Что остаётся в `Templates.xaml`

`Templates.xaml` остаётся **только** для: регистрации конвертеров, `FileContextMenu`, `ListViewTemplate`,
`GridViewTemplate`, `FileNameTextStyle`/`FileNameEditBoxStyle`. Всё «чисто визуальное»
(`PanelHeaderButtonStyle`, `PanelHeaderToggleButtonStyle`, `PanelItemContainerStyle`, `ResizeThumbStyle`,
`SelectionFillBrush`/`SelectionStrokeBrush`) переезжает в `Controls.xaml`/`Panels.xaml`/`Brushes.xaml`.

⚠️ При переносе `PanelHeaderToggleButtonStyle` сохранить **дословно** порядок триггеров в
`ControlTemplate.Triggers` и комментарий про «last declared wins» (finalization m-6) — это не косметика,
а поведение (hover над активной Sort-кнопкой).

### D-3. Модель внешнего вида: три новых поля, два старых уходят

`ChildWindowModel` (`src/DesktopOrganizer.UI/Models/ChildWindowModel.cs`):

| Было | Стало | Комментарий |
|---|---|---|
| `string BackgroundColor = "#1E1E1E"` | **удалить** | заливка панели больше не пользовательская — нейтральная поверхность из токенов |
| `double BackgroundOpacity = 0.8` | `double SurfaceOpacity = 0.8` | переименование + новый смысл (прозрачность нейтральной поверхности) |
| — | `string AccentColor = "#6B7280"` | семантический акцент, локальный (индикатор/иконка/selection) |
| — | `PanelSurfaceStyle SurfaceStyle = Ambient` | `Solid \| Ambient \| Glass` |
| `string TextColor = "#DDDDDD"` | **оставить как есть** | см. D-4 |
| `double IconSize = 48` | без изменений | |

Новый файл `src/DesktopOrganizer.UI/Models/PanelSurfaceStyle.cs`:

```csharp
public enum PanelSurfaceStyle
{
    Solid = 0,    // нейтральная поверхность на SurfaceOpacity, акцент только в индикаторе
    Ambient = 1,  // default: нейтральная база + подмешанный акцент AmbientAccentRatio (~8%)
    Glass = 2,    // пониженная непрозрачность + усиленный border; НЕ acrylic/DWM-blur (см. R-8)
}
```

Все свойства — через `SetField` (INPC), как существующие: живая панель и окно настроек делят один
экземпляр модели.

### D-4. `TextColor` остаётся, но перестаёт быть обязательным источником

Оставляем `TextColor` non-nullable и переносим значение в v4 дословно. Причины:
- убрать его = user-facing removal + переделка окна настроек + правка `Settings.settings` (лишний фронт);
- сделать его `string?` = ключ исчезает из JSON при `null` (`JsonIgnoreCondition.WhenWritingNull` в
  `StateJson.Options`) и `SchemaContractTests.CollectKeyPaths` начинает сравнивать разные наборы ключей — лишний
  риск в контрактном тесте ради косметики.

Дефолт для **новых** панелей не меняем (`#DDDDDD` ≈ `Color.Text.Primary`). DoD «ни одного нового hardcoded color
в `ChildWindow.xaml`» этим не нарушается: биндинг на `Model.TextColor` — не литерал.

Долг зафиксировать новым пунктом бэклога **DO-013** («`TextColor` — per-panel override поверх
типографических токенов; кандидат на удаление в Фазе 6/9»).

### D-5. Persistence: миграция схемы v3 → v4 (не аддитивное расширение)

**Решение: полноценная `V3ToV4Migration`, `StateEnvelope.CurrentSchemaVersion` 3 → 4.**

Альтернатива «аддитивно дописать поля в `PanelAppearanceDto` без миграции» **отвергнута**:
`SchemaContractTests.MigratedV1Document_KeyShape_MatchesApplicationStateDocumentDtoSerialization` сравнивает
полные наборы key-path'ов между выходом цепочки миграций и сериализацией DTO. Любое новое
не-nullable поле DTO ломает этот тест, пока миграция не начнёт его производить. То есть миграция нужна в
любом случае — значит дешевле сделать её честной и убрать мёртвые `backgroundColor`/`backgroundOpacity`,
а не тащить два источника правды о внешнем виде (прямое нарушение инварианта CLAUDE.md
«единый источник правды вида панели» и §5.2 спецификации «Background и accent больше не одно и то же»).

Правила отображения v3 → v4 для `panels[].appearance` **и** для каждого `layoutSnapshots[].panels[].appearance`
(ADR-024 R-11 — один общий приватный helper, как `V2ToV3Migration.GroupPanel`, ни в коем случае не две копии):

```text
surfaceStyle   := "Ambient"                                   // константа для всех мигрированных панелей
surfaceOpacity := v3.backgroundOpacity   ?? 0.8
accentColor    := v3.backgroundColor == LegacyDefaultBackground("#1E1E1E")  ->  "#6B7280"  (Accent.Neutral)
                  иначе                                        ->  v3.backgroundColor (как есть)
textColor      := v3.textColor           ?? "#DDDDDD"          // verbatim
iconSize       := v3.iconSize            ?? 48                 // verbatim
// backgroundColor / backgroundOpacity в выходном документе ОТСУТСТВУЮТ
```

Обоснование ветки `#1E1E1E → #6B7280`: `#1E1E1E` — это исторический дефолт «тёмная заливка», а не
осознанный выбор цвета пользователем; протащить его в акцент = у всех дефолтных панелей появится
почти-чёрный индикатор. Панели, которым пользователь **осознанно** задал цвет (Games=фиолетовый и т.п.),
сохраняют идентичность: цвет переезжает в акцент — ровно то, чего требует ADR-025 («accent is semantic»).
Это единственное лоссивное место миграции, и оно должно быть в ADR-025 → Consequences/Negative.

Константы дефолтов дублируются в `V3ToV4Migration` литералами — `DesktopOrganizer.Services` не может
ссылаться на `DesktopOrganizer.UI` (ADR-024 D-3); повторить комментарий-предупреждение из `V2ToV3Migration`.

### D-6. Визуализация interaction-состояний без изменения трекера

`PanelInteractionTracker` в Фазе 1 реально входит только в `Idle/Dragging/Resizing`; `Hover`/`Editing`
зарезервированы за Фазой 6, и `T-TRK-08` сторожит, чтобы они не стали «тихо живыми».

Поэтому Фаза 5 вычисляет **визуальное** состояние из трёх независимых фактов:

```text
PanelVisualState = f(tracker.State, isPointerOver, isTitleEditing)
   isPointerOver   <- FrameworkElement.IsMouseOver корня панели (чистый XAML-факт)
   isTitleEditing  <- видимость TitleEditBox / PanelTitleRenameBehavior (уже существует)
   tracker.State   <- Dragging/Resizing (уже существует)
```

Приоритет: `Dragging|Resizing > Editing > Hover > Idle`. Трекер не меняется, T-TRK-08 остаётся зелёным,
а DoD «пять состояний визуально различимы» выполняется. Мост «трекер → DependencyProperty» — новое
маленькое поведение `Behaviors/PanelVisualStateBehavior.cs`, подписывающееся на уже существующий
`PanelInteractionTracker.StateChanged` (событие задокументировано как «reserved for future use binding
visual gesture-affordance states» — это ровно его потребитель, первый).

### D-7. Акцент доставляется attached-DP, а не биндингом на DataContext

`PanelItemContainerStyle` используется и в `ChildWindow` (DataContext = `ChildWindowViewModel`), и в
`SystemIconsPanelWindow` (DataContext = `SystemIconsPanelViewModel`, у которого нет `Model.AccentColor`).
Биндинг вида `{Binding DataContext.Model.AccentColor, RelativeSource=AncestorType=Window}` во втором случае
даст binding error в рантайме (DoD «одинаковый appearance для Grid/List/Portal/SystemIcons» при этом
формально сломается).

Решение: `src/DesktopOrganizer.UI/Theming/PanelTheme.cs` — attached DP с наследованием:

```csharp
public static class PanelTheme
{
    // FrameworkPropertyMetadataOptions.Inherits — значение течёт вниз по логическому дереву
    public static readonly DependencyProperty AccentProperty;        // Brush, default = Brush.Accent.Neutral
    public static readonly DependencyProperty SurfaceStyleProperty;  // PanelSurfaceStyle, default Ambient
    public static readonly DependencyProperty VisualStateProperty;   // PanelVisualState, default Idle

    public static Brush GetAccent(DependencyObject d);
    public static void SetAccent(DependencyObject d, Brush value);
    // ... аналогично для остальных
}
```

`ChildWindow` выставляет `PanelTheme.Accent` на корневом `Border` (биндинг через
`AccentBrushConverter`), `SystemIconsPanelWindow` — не выставляет вовсе и получает нейтральный дефолт.
Стиль элемента тогда: `{Binding Path=(theming:PanelTheme.Accent), RelativeSource={RelativeSource Self}}`.

⚠️ **Известная ловушка WPF:** наследуемые attached-DP **не** пересекают границу `ContextMenu`/`Popup`
(они живут в отдельном визуальном дереве). Меню и hint-popup'ы должны тематизироваться `StaticResource`-токенами,
а не наследуемым акцентом (см. R-6).

### D-8. `PanelChrome` — lookless `ContentControl`, не `UserControl` и не «стиль на Border»

`src/DesktopOrganizer.UI/Controls/PanelChrome.cs` + default style в `Panels.xaml`
(`DefaultStyleKeyProperty.OverrideMetadata`, `Themes/Generic.xaml` не заводим — стиль мержится явно).

DependencyProperty:

| DP | Тип | Назначение |
|---|---|---|
| `Title` | `string` | текст заголовка |
| `TitleEditor` | `object` | слот для существующего `TitleEditBox` (rename не переписываем) |
| `HeaderActions` | `object` | слот для Sort/View/RollUp/Overflow кнопок |
| `SemanticIcon` | `string` | глиф Segoe MDL2, опционален (§5.4) |
| `AccentBrush` | `Brush` | |
| `SurfaceBrush` | `Brush` | результат `PanelSurfaceBrushConverter` |
| `SurfaceStyle` | `PanelSurfaceStyle` | |
| `VisualState` | `PanelVisualState` | для триггеров (D-6) |
| `IsRolledUp` | `bool` | свёрнутое состояние (§5.6) |
| `ItemCount` | `int` | опциональный счётчик в rolled-up |
| `HeaderHeight` | `double` | по умолчанию `PanelMetrics.HeaderHeight` |
| `Content` (унаследован) | `object` | `FileListHost` |

Части шаблона: `PART_Header`, `PART_AccentIndicator`, `PART_ContentHost` (`ContentPresenter`),
`PART_InteractionOverlay` (`Border`, `IsHitTestVisible=False` — обязательно, иначе перехватит drag/resize/drop).

Альтернативы:
- `UserControl` — отвергнут: нельзя перешаблонить, тянет свой XAML+code-behind, ломает перенос
  drag/rename/drop-обработчиков, которые обязаны остаться на элементах `ChildWindow`.
- «просто Style на Border» — отвергнут: триггерам всё равно нужны DP, а именованные части
  (`PART_*`) дают контракт, проверяемый ревью и (частично) тестом на метаданные DP.

**`ChildWindow.xaml` после C4:** `Window → PanelChrome` → внутри `PanelChrome.HeaderActions` лежит
существующий `StackPanel` с кнопками (со всеми обработчиками), `PanelChrome.TitleEditor` — существующий
`TitleEditBox`, `PanelChrome.Content` — существующий `FileListHost`. `ResizeOverlay`, `ReorderInsertionIndicator`,
`MoveHintPopup`, `RenameErrorPopup` остаются **сиблингами** `PanelChrome` в том же внешнем `Grid`, чтобы
z-order и hit-testing не поменялись ни на пиксель.

### D-9. DO-003: обесцвечивание `MainWindow.xaml` без ре-дизайна окна настроек

Окно настроек — обычное светлое системное окно; токены Ambient Accent — тёмные, для панелей.
Фаза 5 **не перекрашивает** окно настроек, а только убирает литералы. В `Colors.xaml` заводится
отдельная секция с префиксом `Color.Settings.*`:

| Литерал сейчас | Токен | Строки `MainWindow.xaml` |
|---|---|---|
| `#F0F0F0` | `Color.Settings.ChromeBackground` | 18 |
| `#CCCCCC` | `Color.Settings.ChromeBorder` | 18, 228, 355 |
| `White` (активная вкладка) | `Color.Settings.TabActiveBackground` | 74 |
| `#888888` | `Color.Settings.IconMuted` | 92 |
| `#666666` | `Color.Settings.TextMuted` | 130, 141, 155, 166, 372 |
| `#888` (border превью цвета) | `Color.Settings.SwatchBorder` | 272, 279, 328, 335 |
| `#B8860B` | `Color.Settings.Warning` | 194, 196 |

Дополнительно: `Style` на кнопку вкладки/крестик из инлайна (строки 62–99) переносится в `Controls.xaml`
как `Settings.PageTabButtonStyle` / `Settings.PageTabCloseButtonStyle` — это и есть содержательная часть DO-003
(«инлайновые стили вместо ресурсов»), а не только цвета. Поведение (`EqualityToBool`-мультибиндинг,
DataTrigger `Value="default"`) переносится дословно.

### D-10. Settings UI и глобальные дефолты — минимальное касание

Окно настроек должно управлять новой моделью (иначе пользователь теряет контроль над внешним видом):

- `SelectedPanelSettingsViewModel`: `SelectedPanelBackgroundColor` → `SelectedPanelAccentColor`,
  `SelectedPanelOpacity` → `SelectedPanelSurfaceOpacity`, новое `SelectedPanelSurfaceStyle` +
  `IReadOnlyList<PanelSurfaceStyle> SurfaceStyles`.
- `MainWindowViewModel` — фасадные свойства переименовать синхронно (строки ~105–122).
- `MainWindow.xaml` строки 269–287: «Background color (hex)» → «Accent color (hex)» + превью-свотч;
  «Opacity» → «Surface opacity»; новая строка ComboBox «Surface style».

**Глобальные дефолты (`Settings.settings`/`IAppSettings`) — лёгкий путь (рекомендуется):**
ключи `user.config` (`DefaultBackgroundColor`, `DefaultOpacity`, `DefaultTextColor`, `DefaultIconSize`)
**не переименовываем** (это второй фронт миграции, уже не JSON-store, и он не стоит того), добавляем ровно один
новый: `DefaultSurfaceStyle` (string, `"Ambient"`). `DefaultBackgroundColor` документируется в
`AppSettings`/`IAppSettings` XML-doc как «seed для `AccentColor` новых панелей», лейбл в UI меняется на
«Accent color». Расхождение имя-в-конфиге ↔ смысл фиксируется как долг **DO-014**.

Точки создания панели, требующие правки: `App.xaml.cs:289-292` и `MainWindowViewModel.cs:382`.

### D-11. Высота заголовка: остаётся 32px, но перестаёт быть тройным литералом

Спека допускает 32–36. Берём **32** — иначе придётся синхронно менять `ChildWindow.xaml.cs:72`
(`TitleBarHeight`), `RollUpBehavior` (целевая высота свёрнутой панели), `Window.MinHeight="32"` и пере-снимать
все baseline-скриншоты; выигрыш чисто косметический, риск — в чужих фазах.

Вместо этого убираем рассинхрон: новый `src/DesktopOrganizer.UI/Models/PanelMetrics.cs`

```csharp
public static class PanelMetrics
{
    public const double HeaderHeight = 32d;
    public static readonly GridLength HeaderRowHeight = new(HeaderHeight);
    public const double AccentIndicatorThickness = 3d;
    public const double HeaderButtonSize = 28d;
}
```

XAML: `<RowDefinition Height="{x:Static models:PanelMetrics.HeaderRowHeight}"/>` (для `PanelChrome` — через
`TemplateBinding HeaderHeight` с дефолтом из `PanelMetrics`), `ChildWindow.xaml.cs` — `TitleBarHeight`
становится `=> PanelMetrics.HeaderHeight`. `MinHeight="32"` в `Window` остаётся литералом (WPF-коэрсия, как
`MinWidth="120"`) — рядом обязателен комментарий-близнец существующего про `MinPanelSize`.

### D-12. High contrast / readability

Минимальный, но реальный механизм: `Resources/Styles/Colors.HighContrast.xaml` — переопределяет
`Color.Surface.*`/`Color.Text.*`/`Color.Border.*` значениями из `SystemColors.*` и форсирует
`SurfaceOpacity = 1.0` (через `SurfaceComposer`, см. §2.2 — параметр `highContrast`). Мержится в
`App.xaml.cs` при старте, если `SystemParameters.HighContrast == true`, и на смене
`SystemParameters.StaticPropertyChanged`/`SystemEvents.UserPreferenceChanged` — минимум при старте (смена
темы на лету — OPTIONAL, вынести в DO-015, если усложняет).

Проверяемо здесь: только факт корректной сборки словаря и юнит-тест на «`ComposeSurface(..., highContrast:true)`
всегда возвращает alpha=255». Само отображение — manual verify (V-14).

---

## 2. Артефакты

### 2.1 Новые файлы

| Путь | Тип | Scope |
|---|---|---|
| `src/DesktopOrganizer.UI/Resources/Styles/Colors.xaml` | ResourceDictionary | S |
| `src/DesktopOrganizer.UI/Resources/Styles/Spacing.xaml` | ResourceDictionary | S |
| `src/DesktopOrganizer.UI/Resources/Styles/Brushes.xaml` | ResourceDictionary | S |
| `src/DesktopOrganizer.UI/Resources/Styles/Typography.xaml` | ResourceDictionary | S |
| `src/DesktopOrganizer.UI/Resources/Styles/Icons.xaml` | ResourceDictionary | S |
| `src/DesktopOrganizer.UI/Resources/Styles/Controls.xaml` | ResourceDictionary | M |
| `src/DesktopOrganizer.UI/Resources/Styles/Panels.xaml` | ResourceDictionary | L |
| `src/DesktopOrganizer.UI/Resources/Styles/Colors.HighContrast.xaml` | ResourceDictionary | S |
| `src/DesktopOrganizer.UI/Models/PanelSurfaceStyle.cs` | enum | S |
| `src/DesktopOrganizer.UI/Models/PanelMetrics.cs` | static class | S |
| `src/DesktopOrganizer.UI/Theming/AccentPalette.cs` | static class (чистая логика) | S |
| `src/DesktopOrganizer.UI/Theming/SurfaceComposer.cs` | static class (чистая логика) | M |
| `src/DesktopOrganizer.UI/Theming/PanelVisualState.cs` | enum | S |
| `src/DesktopOrganizer.UI/Theming/InteractionVisualState.cs` | static class (чистая логика) | S |
| `src/DesktopOrganizer.UI/Theming/PanelTheme.cs` | attached DP | S |
| `src/DesktopOrganizer.UI/Controls/PanelChrome.cs` | lookless ContentControl | L |
| `src/DesktopOrganizer.UI/Behaviors/PanelVisualStateBehavior.cs` | behavior | M |
| `src/DesktopOrganizer.UI/Converters/PanelSurfaceBrushConverter.cs` | IMultiValueConverter | M |
| `src/DesktopOrganizer.UI/Converters/AccentBrushConverter.cs` | IValueConverter | S |
| `src/DesktopOrganizer.UI/Converters/VisualStateToDoubleConverter.cs` | IValueConverter | S |
| `src/DesktopOrganizer.UI/Converters/ItemCountToTextConverter.cs` | IValueConverter | S |
| `src/DesktopOrganizer.Services/Storage/Migrations/V3ToV4Migration.cs` | ISettingsMigration | M |
| `tests/.../Resources/Storage/golden/v4-full.json` | fixture | S |
| `tests/.../Resources/Storage/golden/expected-current-from-v3.json` | fixture | S |
| `tests/.../Unit/Theming/SurfaceComposerTests.cs` | tests | M |
| `tests/.../Unit/Theming/AccentPaletteTests.cs` | tests | S |
| `tests/.../Unit/Theming/InteractionVisualStateTests.cs` | tests | S |
| `tests/.../Unit/Theming/PanelThemeMetadataTests.cs` | tests | S |
| `tests/.../Unit/Converters/AmbientConverterTests.cs` | tests | M |
| `tests/.../Unit/Storage/Migrations/V3ToV4MigrationTests.cs` | tests | M |
| `docs/Refactoring/Testing/ambient-accent-visual-matrix.md` | manual-матрица | S |

### 2.2 Изменяемые файлы

| Путь | Что меняется | Риск |
|---|---|---|
| `src/DesktopOrganizer.UI/App.xaml` | MergedDictionaries (порядок D-1) | низкий, но рантайм-ошибка при ошибке порядка |
| `src/DesktopOrganizer.UI/App.xaml.cs` | high-contrast merge; фабрика панели (стр. ~289–292) | низкий |
| `src/DesktopOrganizer.UI/Views/ChildWindow.xaml` | корень → `PanelChrome`; убраны литералы `#FF4080FF`, `#EE1E1E1E`, `#33FFFFFF`, `White` (стр. 249, 265–274, 288–297) | **высокий** (визуальная регрессия) |
| `src/DesktopOrganizer.UI/Views/ChildWindow.xaml.cs` | `TitleBarHeight` → `PanelMetrics`; `OverflowButton_Click` (~8 строк); подключение `PanelVisualStateBehavior` | средний |
| `src/DesktopOrganizer.UI/Views/MainWindow.xaml` | DO-003 (D-9) + новые контролы appearance (D-10) | средний |
| `src/DesktopOrganizer.UI/Views/SystemIconsPanelWindow.xaml` | `#55000000`/`White`/`#FF4080FF` → токены; `PanelTheme.Accent` дефолт | средний |
| `src/DesktopOrganizer.UI/Resources/Templates.xaml` | ужимается (D-2); item-шаблоны ретокенизируются | средний |
| `src/DesktopOrganizer.UI/Models/ChildWindowModel.cs` | D-3 | средний |
| `src/DesktopOrganizer.UI/State/Dto/PanelAppearanceDto.cs` | D-3/D-5 | **высокий** (контрактный тест) |
| `src/DesktopOrganizer.UI/State/PanelStateMapper.cs` | новые поля | высокий |
| `src/DesktopOrganizer.Services/Storage/StateEnvelope.cs` | `CurrentSchemaVersion` 3 → 4 | **высокий** |
| `src/DesktopOrganizer.Services/Configuration/DiConfig.cs` | `services.AddSingleton<ISettingsMigration, V3ToV4Migration>()` (стр. ~101–102) | низкий |
| `src/DesktopOrganizer.UI/ViewModels/SelectedPanelSettingsViewModel.cs` | D-10 | средний |
| `src/DesktopOrganizer.UI/ViewModels/GlobalDefaultsViewModel.cs`, `MainWindowViewModel.cs` | D-10 | средний |
| `src/DesktopOrganizer.UI/Services/AppSettings.cs`, `IAppSettings.cs`, `Properties/Settings.settings` | `DefaultSurfaceStyle` | низкий |
| `adr/ADR-025-...md` | Proposed → Accepted, полный текст | — |
| `docs/Refactoring/ROADMAP.md`, `docs/Refactoring/BACKLOG.md` (DO-003 закрыт, DO-013/014 заведены) | — | — |
| `tests/.../Resources/Storage/golden/README.md` + `expected-current-from-v1/v2.json` | регенерация под v4 | **высокий** (см. R-3) |

### 2.3 Тесты, которые сломаются и должны быть обновлены (blast radius миграции)

`GoldenMigrationTests`, `SchemaContractTests`, `JsonApplicationStateStoreTests` (22 совпадения),
`StateRestoreCommandTests` (17), `PanelStateMapperTests` (4), `StateMigrationRunnerTests` (3),
`ApplicationStateServiceTests`, `LegacyStateImporterTests`, `ChildWindowModelSerializationTests`,
`MainWindowViewModelTests` (переименования D-10), `BrushConverterTests` (если трогается конвертер-кэш).

---

## 3. Сигнатуры чистой логики (то, что тестируется headless)

### 3.1 `Theming/AccentPalette.cs`

```csharp
public static class AccentPalette
{
    public const string Neutral = "#6B7280";
    public const string Blue = "#3B82F6";  public const string Violet = "#A855F7";
    public const string Amber = "#F59E0B";  public const string Green  = "#22C55E";
    public const string Cyan  = "#06B6D4";  public const string Coral  = "#F06464";

    public static IReadOnlyList<string> Presets { get; }        // 6 пресетов ADR-025, без Neutral
    public static string Default => Neutral;

    /// <summary>"#abc"/" #AABBCC " -> "#AABBCC"; мусор/пусто -> <see cref="Default"/>. Никогда не бросает.</summary>
    public static string Normalize(string? hex);

    public static bool IsPreset(string? hex);
}
```

### 3.2 `Theming/SurfaceComposer.cs`

```csharp
public static class SurfaceComposer
{
    public const double MinSurfaceOpacity = 0.35;   // пол читаемости (DoD readability)
    public const double MaxSurfaceOpacity = 1.0;
    public const double AmbientAccentRatio = 0.08;  // «~10% акцента» из ADR-025
    public const double GlassOpacityFactor = 0.60;

    public static double ClampOpacity(double value);              // NaN/±Inf -> MinSurfaceOpacity
    public static byte AlphaFromOpacity(double opacity);          // round(clamp*255)
    public static Color WithAlpha(Color color, byte alpha);
    public static Color Mix(Color a, Color b, double ratio);      // ratio 0..1, clamp

    /// <summary>Итоговая заливка поверхности панели. highContrast форсирует alpha=255 и ratio=0.</summary>
    public static Color ComposeSurface(
        Color neutralBase, Color accent, PanelSurfaceStyle style, double surfaceOpacity, bool highContrast = false);

    /// <summary>Акцент на низкой альфе — selection/hover-подложка иконки (§5.5).</summary>
    public static Color AccentTint(Color accent, double alpha);
}
```

Инварианты, которые обязаны стать тестами:
- `ComposeSurface(..., Solid, o)` не зависит от `accent` (акцент не заливает панель — DoD);
- `ComposeSurface(..., Ambient, o).A == AlphaFromOpacity(o)`;
- `Glass` всегда даёт alpha ≤ `Ambient` при том же `o`, но не ниже `AlphaFromOpacity(MinSurfaceOpacity)`;
- `highContrast:true` ⇒ `A == 255` для всех трёх стилей;
- `ClampOpacity` монотонна и обрабатывает `double.NaN`.

### 3.3 `Theming/InteractionVisualState.cs`

```csharp
public enum PanelVisualState { Idle, Hover, Editing, Dragging, Resizing }

public static class InteractionVisualState
{
    /// <summary>Приоритет: Dragging|Resizing > Editing > Hover > Idle (D-6).</summary>
    public static PanelVisualState Resolve(PanelInteractionState gesture, bool isPointerOver, bool isTitleEditing);

    public static double BorderOpacityFor(PanelVisualState state);   // Idle 0.15 .. Resizing 0.95
    public static double ChromeOpacityFor(PanelVisualState state);   // «actions muted in idle»: Idle 0.45 -> 1.0
    public static double AccentEmphasisFor(PanelVisualState state);  // множитель толщины/яркости индикатора
}
```

**Это и есть тестируемая форма DoD «Idle/Hover/Editing/Dragging/Resizing визуально различимы»:**
тест утверждает, что пять значений `BorderOpacityFor`/`ChromeOpacityFor` попарно различны и монотонно
возрастают от `Idle`. Рендеринг тест не проверяет — но «состояния не схлопнулись в одно» проверяет.

### 3.4 Конвертеры

```csharp
// values: [string accentHex, double surfaceOpacity, PanelSurfaceStyle style] (+ опц. bool highContrast)
public sealed class PanelSurfaceBrushConverter : IMultiValueConverter { /* frozen + bounded cache */ }

// value: string accentHex; parameter: "indicator" | "selection" | "hover" | "full"
public sealed class AccentBrushConverter : IValueConverter { }

// value: PanelVisualState; parameter: "border" | "chrome" | "accent"
public sealed class VisualStateToDoubleConverter : IValueConverter { }

// value: int; -> "" | "1 item" | "{n} items"
public sealed class ItemCountToTextConverter : IValueConverter { }
```

Контракт всех конвертеров (прецедент `HexBrushConverter`/`ColorOpacityBrushConverter`, Фаза 7 Block A):
**никогда не бросают**, на некорректный вход возвращают `Brushes.Transparent`/`Binding.DoNothing`,
кисти `Freeze()`-ятся, кэш ограничен (`MaxCachedBrushes`). `ConvertBack` → `NotSupportedException`
или `Binding.DoNothing`, как у соседей.

---

## 4. Порядок работ (логические коммиты)

| # | Коммит | Содержание | Scope | Зависит от | Проверка здесь |
|---|---|---|---|---|---|
| C1 | `feat(ui): ambient accent design tokens` | 7 словарей + `Colors.HighContrast.xaml` + merge в `App.xaml`. Потребителей ещё нет | M | — | `dotnet build` (XAML-компиляция) |
| C2 | `feat(ui): pure theming logic + converters` | `Theming/*`, 4 конвертера, `PanelMetrics`, `PanelSurfaceStyle` + тесты | M | C1 | **полностью**: build + unit |
| C3 | `feat(state): panel appearance schema v4` | `ChildWindowModel`, `PanelAppearanceDto`, `PanelStateMapper`, `V3ToV4Migration`, `StateEnvelope=4`, DI, фикстуры, обновление сломанных тестов | L | C2 | **полностью**: build + unit + golden |
| C4 | `feat(ui): PanelChrome control` | `Controls/PanelChrome.cs`, шаблон в `Panels.xaml`, `PanelVisualStateBehavior`, `PanelTheme`; `ChildWindow.xaml` переведён на `PanelChrome`; literal-free | L | C3 | build + метаданные DP; **вид — manual** |
| C5 | `feat(ui): icon tiles + shared item chrome` | ретокенизация `PanelItemContainerStyle`/шаблонов, accent-selection, tooltip, `SystemIconsPanelWindow` | M | C4 | build; **вид — manual** |
| C6 | `feat(ui): header actions, overflow menu, rolled-up state` | приглушение actions в idle, кнопка `…` (переиспользует существующий `ContextMenu`), rolled-up = accent\|title\|count\|expand | M | C4 | build; **вид+меню — manual** |
| C7 | `refactor(ui): de-hardcode settings window + appearance controls` | DO-003 (D-9) + surface style/accent/opacity в настройках (D-10) | M | C3 | build + `MainWindowViewModelTests` |
| C8 | `docs(adr): ADR-025 Proposed→Accepted; phase 5 close-out` | ADR-025, ROADMAP, BACKLOG (DO-003 закрыт, DO-013/014/015 заведены), `ambient-accent-visual-matrix.md` | S | C4–C7 | — |

Параллелить можно **C5 ∥ C6 ∥ C7** после C4 (разные файлы). C1→C2→C3→C4 — строго последовательно.

---

## 5. DoD-трассировка

| DoD спецификации | Как закрывается | Где проверяется |
|---|---|---|
| Ни одного нового hardcoded color в `ChildWindow.xaml` | C4/C6; все цвета — `StaticResource`/конвертер | **grep-гейт**: `rg -n "#[0-9A-Fa-f]{3,8}" src/DesktopOrganizer.UI/Views/ChildWindow.xaml` → 0 совпадений (добавить в `code-quality-reviewer` чек-лист) |
| Одинаковый appearance для Grid/List/Portal/SystemIcons | D-7 (attached DP вместо DataContext-биндинга) | build + V-5..V-8 (manual) |
| Idle/Hover/Editing/Dragging/Resizing различимы | D-6 + `InteractionVisualStateTests` | **unit** (значения попарно различны) + V-9..V-13 (manual) |
| Accent локален, не full-panel fill | `SurfaceComposer.ComposeSurface(..., Solid)` игнорирует accent; `Ambient` подмешивает ≤ `AmbientAccentRatio` | **unit** + V-2 (manual) |
| Labels не ломаются на 3+ строки | фиксированная `GridMetrics.LabelHeight=32` + `TextTrimming=CharacterEllipsis` (уже есть), типографика не меняет FontSize | R-4 + V-4 (manual, длинные имена) |
| 100/125/150/200% DPI smoke | — | **только manual** V-15 |
| High-contrast / readability | D-12 + `MinSurfaceOpacity` floor | unit (alpha=255) + V-14 (manual) |
| Global DoD: upgrade существующего state проверен | `V3ToV4MigrationTests` + golden | **unit** + S-матрица Фазы 4 (manual) |

---

## 6. Риски

| ID | Риск | Митигация |
|---|---|---|
| **R-1** | **Визуальная регрессия для существующих пользователей**: цвет панели перестаёт заливать фон. Это намеренное изменение ADR-025, но пользователь увидит «все панели стали одинаковыми» | D-5 сохраняет пользовательский цвет как акцент; в ADR-025 → Consequences/Negative + строка в release notes; V-3 сравнивает «до/после» на baseline-скриншотах Фазы 0 |
| **R-2** | `SchemaContractTests` ловит любое расхождение DTO ↔ миграции по набору ключей | C3 делает обе стороны в одном коммите; тест — гейт, а не помеха |
| **R-3** | Golden-фикстуры объявлены immutable, но `expected-current-from-v1/v2.json` по определению привязаны к «current» | Входные `vN-full.json` **не трогать**; `expected-current-from-*` регенерировать; добавить в `golden/README.md` явное правило «expected-current-* регенерируются при смене `CurrentSchemaVersion`, входные фикстуры — никогда» + новые `v4-full.json`, `expected-current-from-v3.json` |
| **R-4** | Типографика (`Typography.IconLabel`) меняет метрику лейбла → `GridMetrics.LabelHeight=32` рассинхронизируется с реальной высотой → едет grid-snap и `RowHeight` | FontSize лейбла **оставить 11**, `FontFamily` не менять на вариативный без пересчёта; если менять — `LabelHeight` и `Height="32"` в `GridViewTemplate`/`FileNameEditBoxStyle` правятся в связке + V-4 |
| **R-5** | `PanelChrome` перехватывает мышь → ломает drag заголовка / resize-thumbs / drop | `PART_InteractionOverlay` с `IsHitTestVisible="False"`; `ResizeOverlay`/индикатор/popup'ы остаются сиблингами `PanelChrome`, а не его детьми; V-9..V-13 обязательны |
| **R-6** | Наследуемый `PanelTheme.Accent` не доходит до `ContextMenu`/`Popup` (отдельное визуальное дерево) | Меню/hint'ы тематизируются только `StaticResource`-токенами; запрет на accent-зависимые кисти внутри меню зафиксировать комментарием в `Controls.xaml` |
| **R-7** | `WS_EX_NOACTIVATE`: overflow-меню/тултипы на неактивируемом окне могут не получать ввод (ADR-013 R1) | Переиспользовать **существующий** экземпляр `ContextMenu` заголовка (он уже работает по правому клику), не создавать новый; никаких `MessageBox` |
| **R-8** | `Glass` может быть понят как acrylic/DWM-blur, которого в Фазе 5 нет | В `PanelSurfaceStyle` XML-doc и ADR-025 явно: Glass = пониженная непрозрачность + усиленный border; настоящий backdrop-blur — кандидат Фазы 8 (native) |
| **R-9** | `SystemIconsPanelWindow` имеет `SizeToContent="Width"` — любое новое chrome/padding меняет ширину сайдбара | В C5 не добавлять padding/border к item-контейнеру сайдбара; только цвета; V-8 |
| **R-10** | Переименование `SelectedPanelBackgroundColor`/`DefaultOpacity` ломает `MainWindowViewModelTests` | Переименования и правка тестов — один коммит C7 |
| **R-11** | Неверный порядок merge словарей падает только в рантайме (`XamlParseException` на старте) | Порядок зафиксирован в D-1 и в комментарии в `App.xaml`; V-1 — первый пункт ручной матрицы (просто «приложение стартует») |
| **R-12** | Соблазн «заодно» декомпозировать `ChildWindowViewModel` (DO-002) | Явный запрет §0; цель — 0 изменённых строк в `ChildWindowViewModel.cs`. Счётчик rolled-up берётся из уже существующего `FileItems.Count`, новых VM-свойств не заводим |

---

## 7. Тест-план: что проверяемо здесь vs manual-only

### 7.1 Проверяемо в этой среде (`dotnet build` + `dotnet test`)

1. **Компиляция XAML** всех новых словарей и `PanelChrome`-шаблона (markup-компилятор ловит
   неразрешённые типы/namespace, но **не** ошибки порядка merge и **не** ошибки биндингов).
2. **`SurfaceComposerTests`** — инварианты §3.2 (включая `Solid` игнорирует accent — это машинная
   формулировка DoD «accent не заливает панель»).
3. **`AccentPaletteTests`** — нормализация, мусорный вход, пресеты.
4. **`InteractionVisualStateTests`** — таблица приоритетов `Resolve` (5×2×2 кейсов) + попарная
   различимость/монотонность значений (машинная формулировка DoD про пять состояний).
5. **`AmbientConverterTests`** — 4 новых конвертера: frozen-кисти, ограниченный кэш, отсутствие
   исключений на мусорном входе, `ConvertBack`.
6. **`PanelThemeMetadataTests`** — attached DP: тип, дефолт, `Inherits` во `FrameworkPropertyMetadataOptions`
   (читается статически, без создания визуального дерева и без STA).
7. **`PanelChrome` DP-метаданные** — дефолты `HeaderHeight == PanelMetrics.HeaderHeight`, `SurfaceStyle == Ambient`,
   `VisualState == Idle` (статическое чтение `DependencyProperty.DefaultMetadata`, инстанс контрола не создаём).
8. **`V3ToV4MigrationTests`** — полная таблица D-5, включая `layoutSnapshots[].panels`, отсутствие
   `backgroundColor`/`backgroundOpacity` на выходе, отсутствие reserved envelope-ключей, идемпотентность.
9. **`GoldenMigrationTests`** — v1→v4 (3 шага), v2→v4 (2), v3→v4 (1), v4 identity (0).
10. **`SchemaContractTests`** — key-path совпадение DTO ↔ цепочки миграций.
11. **`PanelStateMapperTests`** — round-trip модели с новыми полями.
12. **Grep-гейт DoD:** отсутствие hex-литералов в `ChildWindow.xaml` (и, для DO-003, в `MainWindow.xaml`).

Ожидаемый прирост: ~60–80 тестов поверх текущих 1551.

### 7.2 Принципиально manual verify (Win10/Win11) — `docs/Refactoring/Testing/ambient-accent-visual-matrix.md`

| ID | Проверка |
|---|---|
| V-1 | Приложение стартует, нет `XamlParseException`; в Output нет `System.Windows.Data Error` (binding errors) |
| V-2 | Панель: нейтральная поверхность, акцент только в индикаторе/иконке/selection; заливки категориальным цветом нет |
| V-3 | Сравнение с baseline-скриншотами Фазы 0 (`docs/Refactoring/Screenshots/`) — «было/стало» на светлых и тёмных обоях |
| V-4 | Длинные имена файлов: ровно 2 строки + ellipsis + tooltip с полным именем; лейблы всех плиток на одном Y |
| V-5 | Grid view — вид совпадает с эталоном |
| V-6 | List view — тот же визуальный язык |
| V-7 | Folder Portal — тот же вид (Sort-кнопка highlighted+disabled, как в Фазе 1) |
| V-8 | `SystemIconsPanel` (сайдбар) — те же hover/selected, ширина сайдбара не изменилась |
| V-9 | Idle → Hover: chrome заголовка проявляется, кнопки перестают быть приглушёнными |
| V-10 | Editing (F2 по заголовку и по иконке): состояние визуально отличимо, rename работает как раньше (Enter/Esc/потеря фокуса) |
| V-11 | Dragging заголовка: состояние отличимо, панель двигается, grid-snap не изменился |
| V-12 | Resizing (обе ветки: legacy thumbs и `NativePanelSizing=True`): состояние отличимо, геометрия не изменилась |
| V-13 | Drag&drop файла в панель и drag-reorder: insertion indicator виден на новом фоне, hit-testing не сломан `PanelChrome` |
| V-14 | High contrast (Win+Shift+PrtScn тема): текст читаем, поверхность непрозрачна, акцент различим |
| V-15 | DPI 100 / 125 / 150 / 200%: индикатор акцента не размывается, border 1px не исчезает, заголовок 32px не «плывёт» |
| V-16 | Rolled-up: `accent \| title \| item count \| expand`; двойной клик и кнопка дают одно и то же; высота = 32 |
| V-17 | Overflow-меню `…`: открывается на неактивируемом окне, содержит те же пункты, что и правый клик |
| V-18 | Апгрейд состояния: запуск на `user state` от v0.4.0 (schema v3) → панели на месте, цвет пользователя стал акцентом, дефолтные панели получили нейтральный акцент; backup создан |

---

## 8. Открытые вопросы (решать по месту, эскалация только если реализующий агент не уверен)

1. **Q-1.** `#1E1E1E → Accent.Neutral` vs `#1E1E1E → Accent.Blue` при миграции.
   Рекомендация плана — Neutral (D-5). Если `fences-ux-parity-reviewer` настаивает на «Fences-like
   разноцветности из коробки» — вариант B (Blue) допустим, меняется одна константа + фикстуры.
2. **Q-2.** Высота заголовка 32 (план) vs 36 (верхняя граница спеки). Рекомендация — 32 (D-11);
   36 требует пересъёмки baseline и синхронной правки трёх мест.
3. **Q-3.** Переименовывать ли ключи `Settings.settings`. Рекомендация — нет (D-10, лёгкий путь),
   долг → DO-014.

---

## 9. Recommended Next Actions

1. 🚨 **CRITICAL — `wpf-ui-builder`**: C1, C4, C5, C6, C7 (словари, `PanelChrome`, шаблоны, `ChildWindow.xaml`,
   `MainWindow.xaml`). Условие: фаза целиком в слое XAML/MVVM. Передать §1 (D-1, D-2, D-7, D-8, D-9, D-11) и §6 (R-4..R-9, R-11, R-12).
2. 🚨 **CRITICAL — `dotnet-services-engineer`**: C3 (`V3ToV4Migration`, `StateEnvelope=4`, DTO/mapper, DI,
   golden-фикстуры). Условие: persistence + миграция схемы. Передать §1 D-3/D-4/D-5, §2.3, R-2/R-3.
3. ⚠️ **RECOMMENDED — `tdd-test-engineer`**: C2 — тесты **до** реализации чистой логики
   (`SurfaceComposer`, `AccentPalette`, `InteractionVisualState`, конвертеры, DP-метаданные) и тесты миграции
   для C3. Это единственная корзина, где DoD этой фазы вообще машинно-проверяем — §7.1.
4. ⚠️ **RECOMMENDED (параллельно, одним сообщением) — `code-quality-reviewer` ∥ `fences-ux-parity-reviewer`**
   после C7. Для `code-quality-reviewer` добавить в чек-лист grep-гейт §5 (hex-литералы в
   `ChildWindow.xaml`/`MainWindow.xaml`) и «0 изменённых строк в `ChildWindowViewModel.cs`» (R-12).
   Для `fences-ux-parity-reviewer` — сверка с `docs/Refactoring/Reference/ambient-accent-concept.png` и Q-1/Q-2.
5. 🚨 **CRITICAL — `architecture-documenter`**: C8 — ADR-025 Proposed → Accepted (дописать Context,
   Alternatives, Consequences: лоссивность D-5, Glass≠acrylic R-8, `TextColor`-долг D-4), ROADMAP Phase 5 → `[~]`,
   BACKLOG DO-003 → закрыт, завести DO-013/DO-014/DO-015.
6. 💡 **OPTIONAL — `win32-interop-specialist`**: только если после V-14/V-17 выяснится, что
   high-contrast-тема или меню на `WS_EX_NOACTIVATE` требуют нативного вмешательства, либо если решено
   тянуть настоящий acrylic-backdrop для `Glass` (тогда — отдельный план Фазы 8, не здесь).
7. 💡 **OPTIONAL — повторный `fences-feature-planner`**: если C4 (`PanelChrome`) при реализации окажется
   больше L — разбить на дочерний план `docs/PLANS/phase-5-panel-chrome.md` (шаблон + части отдельно от
   миграции `ChildWindow.xaml`), не «плющить» структуру.

**Ручная проверка на Windows 10/11 по этой фазе не опциональна:** без V-1..V-18 фаза может быть закрыта
только как `[~]` (код завершён, визуал не верифицирован) — тем же статусом, что Фазы 0–4.
