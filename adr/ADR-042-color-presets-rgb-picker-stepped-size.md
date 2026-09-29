# ADR-042: Цветовые пресеты, RGB-пикер и шаговые слайдеры размера

**Статус:** Accepted (2026-09-28) — Amended 2026-09-28 (редизайн: 12 пресетов вместо 10 с раздельным
светлым/тёмным текстом, явный `PanelThemeMode`, окно в стиле Windows 11 Personalization — см. «Amendment
2026-09-28» ниже) — Amended 2026-09-29 (бэклог 1.x пункты 4/5: каждый хроматический пресет получил
собственный тёмный тинт текста вместо общего белого; редактор упрощён до карточки «Цвет» + свёрнутое
«Дополнительно» — см. «Amendment 2026-09-29» ниже)

---

Мастер-план — [docs/PLANS/color-presets-and-size-step.md](../docs/PLANS/color-presets-and-size-step.md)
(главные находки планирования §0, решения D-S1/D-S2/D-C1/D-U1 §2, контракты §3, пакеты работ и владение
файлами §8, риски и открытый вопрос Q-1 §9). Ветка `feature/release-polish-1.0`, поверх закрытой Фазы 10 и
Release polish 1.0 (ADR-039–ADR-041). Конвейер: пользовательский запрос → `dotnet-services-engineer`
(WP-A: чистые контракты в `UI/Models`/`UI/Theming`, `IColorPickerService`+`ColorPickerService` в
Services, RESX en/ru) ∥ `wpf-ui-builder` (WP-B: `SteppedSlider`/`IntegerInputBox`/`SteppedValueEditor`/
`PanelColorEditor`, ViewModel'и, три места применения в XAML) → `tdd-test-engineer` (WP-C) → раунд
`code-quality-reviewer`/`fences-ux-parity-reviewer` (находки MAJOR-2, MAJOR-3, MINOR-4, MINOR-8, H-1 —
зафиксированы ниже как часть финального решения, не как отдельный fix-cycle) → этот документ.

## Контекст

До этой задачи в приложении не было ни одного способа выбрать цвет панели пресетом — только два hex
`TextBox`'а (акцент, текст) в `PanelCustomizeWindow` и в `Settings → New panel defaults`. Три слайдера
размера (icon size в Customize, icon size в New panel defaults, grid step в Layout & snapping) использовали
`TickFrequency=4`/`IsSnapToTickEnabled` без стабильной семантики «куда именно щёлкнет thumb», диапазон icon
size был ограничен 96 px, а WPF-коэрция `Value` в `[Minimum, Maximum]` при TwoWay-привязке могла молча
переписать сохранённое значение при простом открытии окна.

Задача вводит три связанные, но независимые фичи паритета с Fences: (1) 10 готовых цветовых пар как основной
способ выбора цвета панели; (2) системный RGB-пикер как вторичный способ; (3) единый шаг размера с ручным
целочисленным вводом для всех трёх слайдеров. Побочно, в процессе ревью, обнаружилось и было решено
架构-уровневое следствие первой фичи: пять из десяти пресетов — светлые (тёмный текст на светлом акценте), а
единственная существовавшая на тот момент поверхность панели — тёмная (ADR-025), из-за чего текст этих
пресетов оказывался практически нечитаем (~1:1 контраста). Это открытый вопрос Q-1 плана, разрешённый в
рамках этой же задачи (Решение E ниже), а не отложенный.

## Решения

### A. 10 цветовых пресетов (`PanelColorPresets`) — основной способ, отдельно от legacy `AccentPalette.Presets`

> **Историческое — заменено.** Палитра из 10 пар и `FindExact` ниже описывают состояние на момент
> написания этого раздела (2026-09-28, до конца дня). В тот же день, той же задачей, они были заменены 12
> пресетами с раздельным светлым/тёмным текстом на каждый и `FindByAccent` — см. «Amendment 2026-09-28»
> в конце документа. Раздел оставлен как есть ради истории решения (почему 10, почему такой порядок).

Новый статический класс `UI/Theming/PanelColorPresets.cs` — 10 пар (accent, text), в фиксированном порядке
показа (сетка 5×2, row-major):

| Пресет | Accent | Text |
|---|---|---|
| Azure | `#3F7FD9` | `#FFFFFF` |
| Violet | `#7A5BE7` | `#FFFFFF` |
| Coral | `#D95F59` | `#FFFFFF` |
| Forest | `#397A5F` | `#FFFFFF` |
| Graphite | `#596273` | `#FFFFFF` |
| Sky | `#B9DCFF` | `#17324D` |
| Lavender | `#D9C7FF` | `#392C57` |
| Peach | `#FFD1B8` | `#5B3528` |
| Mint | `#BFE8D5` | `#23483B` |
| Rose | `#F3C3D7` | `#533142` |

Каждый пресет — `record PanelColorPreset(string Id, string AccentHex, string TextHex)` с вычисляемым
`NameKey = "ColorPreset_" + Id` (локализованное название в `Strings.resx`/`Strings.ru.resx`). Клик, Space
или Enter на пресете в новом составном контроле `Controls/PanelColorEditor` ставит **оба** цвета одной
атомарной операцией (Решение F). Точное совпадение текущей пары (accent, text) подсвечивается через
`PanelColorPresets.FindExact(accentHex, textHex)`: обе строки нормализуются через internal
`TryNormalizeOpaqueRgb` (`#RGB`/`#RRGGBB`/`#FRGB`/`#FFRRGGBB` → канонический `#RRGGBB`, регистронезависимо;
неполностью непрозрачный альфа-канал или мусор → нет совпадения ни с одним пресетом). Пикер добавлен в двух
местах, использующих один и тот же `PanelColorEditor`: `PanelCustomizeWindow` (Appearance) и
`Settings/NewPanelDefaultsSection` (Defaults for new panels).

**Отношение к `AccentPalette.Presets` (ADR-025).** Шесть legacy-акцентов `AccentPalette.Presets` **не
удалены и не объединены** с новыми десятью: они остаются исключительно кодом/тестовой инфраструктурой —
на них держатся `AccentPaletteTests` и `AmbientAccentContrastAuditTests`, а `AccentPalette.Neutral`
по-прежнему дефолт новой панели (Решение F: цвет по умолчанию не меняется). У пользователя нет UI-пути,
которым он мог бы увидеть эти шесть legacy-пресетов как «выбираемые» — они относятся к другому измерению
(семантическая категория Work/Games/Media/… из ADR-025 «Rules»), тогда как новые 10 — это готовые,
самостоятельные пары (accent, text), не привязанные к категориям. Явная фиксация этого различия в XML-doc
`PanelColorPresets` — намеренная защита от будущей попытки «объединить два списка пресетов», которая на
первый взгляд выглядела бы разумным рефакторингом, но сломала бы контрактные тесты ADR-025.

**Custom-слоты `ColorDialog` (Решение B) засеваются теми же 10+6 цветами**, а не отдельным списком:
`PanelColorPresets.CustomDialogSeed` — 10 акцентов в порядке `All`, затем до 6 уникальных цветов текста,
которых ещё нет среди акцентов (сегодня ровно 16 — предел `ColorDialog.CustomColors`).

**Alternatives considered:**
- **Объединить новые 10 пресетов с legacy `AccentPalette.Presets` (6) в один список.** Отклонено (R-9
  плана): разные измерения (категория vs готовая пара), риск сломать существующие аудиты контраста, которые
  явно перечисляют шесть legacy-цветов.
- **`AccentPalette.Normalize` для сравнения пар в `FindExact`.** Отклонено: `Normalize` на мусорном входе
  возвращает `Neutral`, из-за чего два разных мусорных значения сравнивались бы как «равные» — неверная
  семантика для точечной подсветки выбранного пресета.

---

### B. Вторичный способ — системный RGB-пикер (`ColorDialog`) за `IColorPickerService`, живёт в Services, не в UI

Кнопка «Custom…» у каждого из двух цветов (акцент, текст) открывает `System.Windows.Forms.ColorDialog`
(`AllowFullOpen=true`, `FullOpen=true`, `AnyColor=true`, `SolidColorOnly=true`) через интерфейс
`IColorPickerService` (`Services.Abstractions/Interfaces/IColorPickerService.cs`), реализованный в
`Services/Tools/ColorPickerService.cs` (внутренний, `IAppLog`-логируемый) + чистые преобразования в
`Services/Tools/ColorDialogColor.cs` (`TryParseHex`, `ToColorRef` — COLORREF `0x00BBGGRR`, `ToHex`,
`BuildCustomColors`). Зарегистрирован как singleton в `Services/Configuration/DiConfig.cs`, рядом с
`INotifyIconAdapter`. `PickColor(initialHex, customColors)` возвращает `"#RRGGBB"` или `null`
(отмена/сбой) и никогда не бросает исключений — сбой (`Win32Exception`/`ExternalException`/
`InvalidOperationException`) логируется и деградирует к `null`.

**Почему `ColorDialog` не в UI-проекте.** `UseWindowsForms` включён только в
`DesktopOrganizer.Services.csproj` (там уже живут `NotifyIcon`/`ContextMenuStrip` — `Tools/NotifyIconAdapter.cs`,
`Providers/TrayIconManager.cs`). Включение `UseWindowsForms` в `DesktopOrganizer.UI.csproj` дало бы два
несовместимых эффекта: (1) `ImplicitUsings=enable` + `UseWindowsForms` добавляют неявные
`global using System.Windows.Forms`/`System.Drawing`, что в WPF-проекте создаёт массовые неоднозначности
имён (`Application`, `MessageBox`, `Clipboard`, `Button`, `TextBox`, `Color`, …); (2) у UI-exe уже есть
`app.manifest` с `dpiAware/dpiAwareness` (ADR-037), и WinForms-анализатор для WinForms-приложения выдаёт на
это `WFAC010` — а действующее правило проекта «0 warnings» не допускает подавления анализатора. Поэтому
`ColorDialog` обёрнут интерфейсом ровно там, где уже живёт прецедент WinForms-кода (Services), а UI-слой
видит только `IColorPickerService` — ViewModel'и остаются юнит-тестируемыми без STA/WinForms.

**Владелец диалога.** `IColorPickerService.PickColor` не принимает HWND. `CommonDialog.ShowDialog()` без
явного владельца использует активное окно вызывающего потока — им всегда оказывается то окно, где
пользователь только что нажал «Custom…» (`PanelCustomizeWindow` или окно настроек), оба — обычные
активируемые окна без `WS_EX_NOACTIVATE`. Инвариант «P/Invoke только за `INativeApi`» не нарушен:
`GetActiveWindow` вызывает сам WinForms, не этот проект.

**Hex-поле остаётся третьим, «продвинутым» способом.** В `ColorDialog` нет поля hex, поэтому его некуда
свернуть — `PanelColorEditor` держит оба hex `TextBox`'а (акцент, текст) как и раньше, с прежней семантикой
`LostFocus`-коммита и flush при закрытии `PanelCustomizeWindow` (`CommitPendingEdits()`).

**Fallback-критерии для собственного WPF-пикера (сознательно не реализован сейчас):** переход на in-house
RGB-пикер оправдан, только если manual verify покажет, что диалог не модален к окну-источнику, DPI/вид
заметно ломает UX на Win11, или модальная петля мешает LL-хуку/жестам рабочего стола. Замена в этом случае
затрагивает только реализацию `IColorPickerService`; контракт, VM и XAML не меняются.

**Alternatives considered:**
- **Собственный WPF-пикер (RGB-слайдеры + hex + превью) сразу, вместо `ColorDialog`.** Отклонено как
  основной путь: ≈300+ строк XAML/VM ради функциональности, которую Windows уже предоставляет (палитра +
  RGB/HSL + 16 custom-слотов), плюс собственная a11y-поверхность, которую пришлось бы поддерживать. Остаётся
  явным fallback-планом (см. выше), а не отвергнутой альтернативой навсегда.
- **`Application.EnableVisualStyles()` перед показом диалога**, чтобы получить comctl v6-вид. Отклонено:
  это глобальное состояние WinForms, которое также изменило бы вид трей-меню `ContextMenuStrip`
  (`TrayIconManager`) — риск нежелательного побочного эффекта ради вида одного диалога. Классический вид
  `ChooseColor` без comctl v6 принят как manual-verify риск (см. ниже).

---

### C. Единый шаг размера = 8, диапазоны icon 16–128 / grid step 2–128, шаг не настраивается пользователем

`UI/Models/SizeStepPolicy.cs`:

```csharp
public static class SizeStepPolicy
{
    public const int Step = 8;
    public const int IconSizeMinimum = 16;
    public const int IconSizeMaximum = 128;   // было 96
    public const int GridStepMinimum = 2;     // не поднят до кратного 8 — легаси GridSize=2/3
    public const int GridStepMaximum = 128;
}
```

**Почему 8.** Все «родные» размеры иконок кратны 8 — 16/24/32/40/48/64/72/96/128 (это и бакеты
`IconPixelSize` при 100% DPI, ADR-039, за исключением 20) — при 100% выбранный слайдером размер почти всегда
попадает ровно в бакет без ресэмплинга. 8 px — также базовая единица Fluent/Material spacing-сетки и
примерный шаг самого Fences. Шаг 16 дал бы всего 8 позиций на диапазоне 16–128 (слишком грубо), шаг 4 — 29
позиций и частые переходы между бакетами иконок при перетаскивании слайдера (лишние перезапросы иконок,
ADR-039 debounce). Шаг 8 даёт 15 позиций для icon size и 17 для grid step.

**Почему максимум диапазона поднят до 128, а минимум — нет.** 128 — по запросу продукта; минимум `16`
(icon)/`2` (grid) не поднят, иначе часть уже сохранённых значений оказалась бы вне нового диапазона.
Минимум grid step **сознательно не выровнен на 8** — легаси `GridSize = 2` или `3` (M-6,
`LayoutSnappingSection.xaml`) должны оставаться показываемыми без переписывания при простом открытии окна
(см. Решение D); `1` не входит в редактируемый диапазон — это состояние «выключено», управляемое отдельным
тумблером, поэтому ручной ввод `1` отклоняется как `OutOfRange`.

**Проверка замороженного контура persistence (почему `CLARIFICATION_NEEDED` не потребовался).** Все
существующие источники значений (`Settings.settings.DefaultIconSize=48`, `GridSize` legacy `2`/`3`,
`PanelAppearanceDto.IconSize` default `48`, значения миграций `V2ToV3`/`V3ToV4`, любые значения, которые
могли записать старые слайдеры — icon 16–96 включая дробные, grid 2–128) остаются валидными: новые диапазоны
— надмножества старых, ни один дефолт не меняется, дробные значения (`IconSize` вида `31.51…`, известные из
`AdaptiveGridLayoutBaselineTests`) по-прежнему не округляются и не переписываются при показе (Решение D).

**Alternatives considered:**
- **Шаг 4 (нынешний tick).** Отклонено — избыточно мелко относительно бакетов иконок, требует больше
  переходов между размерами кэша.
- **Шаг 16.** Отклонено — слишком грубо (8 позиций на весь диапазон).
- **Поднять минимум grid step до кратного 8.** Отклонено — сломало бы legacy `GridSize=2/3` (M-6,
  `GlobalDefaultsViewModelTests.LegacyGridSizeTwoOrThree_IsShownUnchanged`).

---

### D. Ручной целочисленный ввод + `SteppedValue`: направление берётся из жеста ввода, а не из дельты `ValueChanged`

Три чистых, WPF-независимых типа в `UI/Models`:

- **`SteppedValue`** (`Next`, `SnapForDrag`, `RoundAndClamp`) — единая точка, через которую проходят все
  четыре механизма ввода (клавиатура, drag мышью, колесо, ручной ввод), чтобы они всегда соглашались об
  одном и том же целевом значении. `Next(current, step, direction, min, max)`: ближайшее кратное `step`
  строго выше (`Up`) или строго ниже (`Down`) `current`, с допуском `Tolerance = 1e-6` (значение в пределах
  допуска от кратного считается этим кратным), затем clamp в `[min, max]`. Например, из `34`: вверх → `40`,
  вниз → `32`; из дробного `32.0101` (реальные персистентные значения существуют,
  `AdaptiveGridLayoutBaselineTests`): вверх → `40`, вниз → `32` (в пределах допуска трактуется как `32`).
  `SnapForDrag(raw, dragStart, …)` никогда не двигает результат «против» направления реального смещения
  курсора относительно `dragStart` — устраняет ложный откат назад из-за округления в момент, когда курсор
  едва сдвинулся. `current`/`raw`/`dragStart` вида `NaN`/`±Infinity` трактуются как `minimum`, не бросают.
- **`IntegerInputParser`** — `TryParse` (только ASCII-цифры, `NumberStyles.None`, `InvariantCulture`;
  ведущие нули разрешены; переполнение → `NotAnInteger`), `IsAllowedInputChunk` (для фильтрации ввода/paste),
  `FormatForDisplay` (`Math.Round(AwayFromZero)` → строка, `NaN`/`±Infinity` → `""`).
- **`SteppedSlider : Slider`** (`Controls/SteppedSlider.cs`) — направление берётся из того, **какой именно
  механизм WPF сработал** (переопределённые `OnIncreaseSmall/Large`, `OnDecreaseSmall/Large`,
  `OnMinimizeValue/OnMaximizeValue`, `OnThumbDragStarted/Delta/Completed`), а не из знака дельты
  `ValueChanged` — это то, что делает семантику детерминированной независимо от того, клавиатура это,
  клик по треку, PageUp/Down, drag или колесо мыши (последнее — только когда слайдер в фокусе,
  `IsKeyboardFocusWithin`, чтобы не перехватывать прокрутку окна настроек).
- **`IntegerInputBox : TextBox`** + **`SteppedValueEditor`** (`Controls/IntegerInputBox.cs`,
  `Controls/SteppedValueEditor.xaml(.cs)`) — составной контрол `[SteppedSlider] [IntegerInputBox] [px]`,
  используемый во всех трёх местах (icon size × 2, grid step × 1).

**«Не пишет при открытии» — разрыв WPF-коэрции.** Внутренний слайдер и бокс внутри `SteppedValueEditor`
**не привязаны к `Value` через `Binding`** — синхронизация идёт императивно под guard'ом
(`_syncing`), поэтому стандартная WPF-коэрция `Slider.Value` в `[Minimum, Maximum]` при TwoWay-привязке
(которая раньше писала обратно в источник) больше не может переписать значение просто от того, что окно
было открыто. Коммит наружу (`CommitFromUser`) происходит только если новое значение отличается от текущего
хотя бы на `0.5`, и только как реакция на явное действие пользователя (шаговый переход слайдера, коммит
бокса по Enter/LostFocus, или внешний `RangeValuePattern.SetValue` через UI Automation — тоже считается
явным действием и округляется через `RoundAndClamp`). Открытие окна со значением `34`, `32.0101` или
значением вне диапазона (ручная правка JSON) не производит ни одной записи.

**Места применения:** `PanelCustomizeWindow.xaml` (icon size), `Settings/NewPanelDefaultsSection.xaml`
(icon size), `Settings/LayoutSnappingSection.xaml` (grid step, `Delay=300` на биндинге `GridStep` сохранён —
каждый коммит по-прежнему вызывает `NormalizePanelsToGrid()` для всех панелей).

**Alternatives considered:**
- **Оставить `TickFrequency`/`IsSnapToTickEnabled` WPF-слайдера как основной механизм шага.** Отклонено —
  именно эта коэрция и была источником нежелательной записи при простом открытии окна (M-6 прецедент); не
  даёт единой точки, которой могли бы одинаково пользоваться клавиатура/drag/колесо/ручной ввод.
- **Определять направление по знаку дельты `ValueChanged`.** Отклонено — WPF поднимает `ValueChanged` уже
  после того, как `Slider` сам скорректировал `Value` (включая коэрцию в диапазон), так что дельта не
  надёжно отражает исходный жест пользователя, особенно на границах диапазона.

---

### E. Q-1: адаптивная нейтральная база поверхности (`SurfaceComposer.ResolveNeutralBase`) для светлых пресетов, решённая через правило пары, а не через цвет акцента в одиночку

> **Историческое — правило пары заменено.** Порог `LightAccentLuminanceThreshold = 0.5` и решение «по паре
> (accent, text)», описанные в этом разделе, в тот же день заменены явным, выбираемым пользователем
> `PanelThemeMode` (`Light`/`Dark`) с выводом режима по контрасту `text` к двум базам — см. «Amendment
> 2026-09-28». `ResolveNeutralBase` в текущем коде параметр `accent` не читает вовсе. Раздел ниже оставлен
> как история решения Q-1 (почему вообще появилась вторая база, откуда взялся порог 0.5).

Пять из десяти пресетов (Sky/Lavender/Peach/Mint/Rose) — светлые (тёмный текст на светлом акценте). До
этой задачи поверхность панели всегда была тёмной базой `#1E1E1E` (ADR-025) + 0–8% акцента, поэтому текст
этих пяти пресетов читался бы на тёмной поверхности на уровне ~1:1 контраста — открытый вопрос Q-1 плана
(«ship as-is» / «отдельная фича пастельной поверхности» / «сменить цвет текста»). Задача разрешает Q-1,
реализуя вариант «адаптивная поверхность» **в рамках этой же задачи**, а не откладывая его отдельной фичей:

- **`SurfaceComposer.LightAccentLuminanceThreshold = 0.5`** (WCAG relative luminance) — граница, при которой
  акцент считается «светлым». Выбрана с большим запасом: все шесть legacy `AccentPalette.Presets` +
  `AccentPalette.Neutral` лежат на или ниже `0.44` (Amber, максимум), все пять новых светлых пресетов — на
  или выше `0.63` (Rose, минимум) — зазор ≈0.19 с обеих сторон.
- **`SurfaceComposer.LightNeutralBase = #F3F5F8`** — новая светлая нейтральная база (рядом с существующей
  `DarkNeutralBase = #1E1E1E`, байт-в-байт идентичной прежней единственной базе).
- **`ResolveNeutralBase(Color accent, Color text)` — решает по ПАРЕ (accent, text), не по одному только
  акценту.** Это ключевое исправление, найденное в раунде ревью (обозначено в коде как «MAJOR-2 fix,
  orchestrator decision "variant A"» — не путать с вариантами A/B/C самого Q-1 в плане, это отдельная
  развилка внутри уже выбранного «адаптивная поверхность»): светлый акцент резолвится в `LightNeutralBase`
  **только если** его собственный `text` реально читается на светлой базе лучше, чем на тёмной
  (`ContrastCalculator.ContrastRatio(text, LightNeutralBase) > ContrastRatio(text, DarkNeutralBase)`). Акцент
  ниже порога луминантности всегда резолвится в `DarkNeutralBase`, независимо от `text`. Это единственное,
  что удерживает **legacy-панель с пользовательским светлым акцентом** (например, `#FFD700`), у которой
  `text` — обычный светлый дефолт (`#DDDDDD`/`#FFFFFF`), от переключения на светлую поверхность: такая пара
  читается far лучше на тёмной базе (почти-белое на почти-чёрном), чем на светлой (почти-белое на
  почти-белом, ~1:1) — правило пары держит её тёмной, даже хотя один только акцент «перевёл» бы её на
  светлую. На светлую базу переключаются только пары, у которых **и** акцент светлый, **и** свой текст —
  тёмный (ровно пять новых пресетов).
- **`PanelTheme.Text`** — новое, наследуемое attached-свойство (`FrameworkPropertyMetadataOptions.Inherits`),
  единый источник правды текста панели рядом с уже существующим `PanelTheme.Accent`; читается тремя новыми
  потребителями (`PanelSurfaceBrushConverter`, `HoverOverlayBrushConverter`,
  `PanelChromeForegroundBrushConverter`) — та же архитектура «inherited attached property от `PanelRoot`»,
  что и `Accent`, с тем же R-6 предупреждением про `ContextMenu`/`Popup`, не наследующие attached-свойства.
- **`PanelChromeForegroundPolicy`** (новый) — luminance-адаптивный аналог `HoverOverlayPolicy` для
  фиксированных (не пользовательских) элементов хрома: `Primary`/`Secondary`/`Muted` (текстовые веса
  заголовка/подписей) и четыре состояния `ScrollBarThumb*` (overlay-скроллбар), плюс `Border`
  (`PART_NeutralStroke`/`PART_InteractionOverlay`, MINOR-4 — на светлой поверхности прежний фиксированный
  `Brush.Border.Default` был почти невидим). `Resolve(accent, text, kind)` резолвит тем же правилом пары,
  что и `ResolveNeutralBase`, набор тёмных токенов байт-в-байт идентичен уже существовавшим `Color.Text.*`/
  `Color.ScrollBar.*`.
- **`HoverOverlayPolicy.For` расширен на светлые поверхности без изменения существующей логики**: он уже
  выбирал затемняющий/осветляющий wash по luminance композированной поверхности (P07-visual fix-cycle 1,
  ADR-025 Amendment); светлая поверхность теперь получает `LightSurfaceDarkenWash` (`#14000000`, слабее
  `DarkenWash`) вместо `LightenWash` — осветление уже почти-белой поверхности незаметно, а лёгкое
  затемнение остаётся видимым и не роняет контраст токенов `PanelChromeForegroundPolicy.LightPrimary`/
  `LightSecondary` (`#1B1B1B`/`#666666`) ниже `ContrastCalculator.MinimumIconContrast`.

**Контрактные тесты, закрепляющие «legacy-пары byte-identical»:**
`SurfaceComposerTests.ResolveNeutralBase_LegacyDarkAccent_ReturnsDarkNeutralBase`,
`SurfaceComposerTests.ResolveNeutralBase_LightAccentPairedWithLightText_ReturnsDarkNeutralBase`,
`SurfaceComposerTests.DarkNeutralBase_IsByteIdenticalToPreVariantBSingleBase`,
`SurfaceComposerTests.LightAccentLuminanceThreshold_IsPinnedAt05_WithMarginOnBothSides`,
`SurfaceComposerTests.ResolveNeutralBase_LuminanceExactlyAtThreshold_ReturnsLightNeutralBase`,
`SurfaceComposerTests.ResolveNeutralBase_NewLightAccent_ReturnsLightNeutralBase`,
`AmbientAccentContrastAuditTests.LightPreset_DefaultOpacity_SolidOrAmbient_PassesTextContrastOnBothWallpapers`
(все пять светлых пресетов проходят `MinimumTextContrast` на обоих представительных обоях для
`Solid`/`Ambient` при дефолтной непрозрачности), плюс два известных, зафиксированных как `KnownGap`
исключения для `Glass` при минимальной непрозрачности (`LightPreset_KnownGap_Glass_AtMinimumOpacity_On*`) —
симметричное зеркало уже существовавшего `KnownGap_MinSurfaceOpacityFloor_OnLightWallpaper_FailsTextContrast`
для тёмной базы (DO-021), не новый класс проблемы.

**High Contrast не затронут** — `SurfaceComposer.ComposeSurface(highContrast: true)` по-прежнему
принудительно возвращает непрозрачный `neutralBase` без подмешивания акцента; какая именно `neutralBase`
сюда попадает, решает тот же `ResolveNeutralBase`, так что HC-режим для светлых пресетов тоже получает
светлую, а не тёмную принудительную заливку — согласованно с остальным поведением.

**Alternatives considered (варианты самого Q-1, докeanие плана §9):**
- **Вариант A плана («ship as-is», разрыв закрепляется `KnownGap`-тестами, пользователь оценивает на
  Windows).** Не выбран как окончательное решение — реализована адаптивная поверхность в рамках этой же
  задачи вместо того, чтобы откладывать заведомо нечитаемый результат до ручной проверки.
- **Вариант C плана (сменить текст светлых пресетов на светлый).** Отклонён как противоречащий
  зафиксированному решению пользователя о конкретных hex-парах (§0 плана — «Решения пользователя
  окончательны»).
- **Резолвить светлую/тёмную базу по одному только акценту (без пары).** Рассмотрено и отклонено в раунде
  ревью (MAJOR-2) — превращало бы существующие legacy-панели со светлым пользовательским акцентом в
  светлую поверхность, ломая их байт-в-байт идентичность с поведением до этой задачи.

---

### F. Замороженный контур persistence не тронут; применение пресета — одна атомарная запись

Ни `schemaVersion`, ни `IAppSettings`/`Settings.settings`/`user.config`, ни State/DTO/Migrations не
изменены. Пресеты не хранятся как отдельная сущность — панель по-прежнему хранит обычные
`AccentColor`/`TextColor` строки, как и раньше; пользовательские custom-цвета `ColorDialog` между сессиями
не сохраняются (WinForms сам не персистит `CustomColors` за рамками текущего процесса).

Применение пресета пишет оба цвета одной операцией, а не двумя независимыми: `SelectedPanelSettingsViewModel.ApplyColors(accent, text)`
делает один `Apply(p => { p.AccentColor = accent; p.TextColor = text; })` → один `SaveWindowsState()`;
`GlobalDefaultsViewModel.SetDefaultColors(accent, text)` — два `SetGlobalNoSave` и один `Save()`, по образцу
уже существующего `ApplyFromPanel`. Сохраняемое содержимое идентично раздельной записи двух полей — меняется
только число вызовов save (с двух до одного), что не является изменением персистентного формата/семантики
загрузки и не подпадает под заморозку ADR-024.

## Последствия

### Позитивные

- Паритет с Fences по выбору цвета панели: готовые пресеты как основной, быстрый путь; системный RGB-пикер
  как полноценный вторичный, без написания собственного колор-пикера с нуля.
- Единая, детерминированная семантика шага для всех трёх размерных слайдеров вместо разрозненного
  `TickFrequency`/`IsSnapToTickEnabled`; устранена нежелательная запись значения при простом открытии окна
  (закрывает класс бага, ранее принятый как ограничение M-6).
- Icon size расширен до 128 px без изменения дефолтов и без миграции — все существующие сохранённые
  значения остаются валидными.
- Побочно закрыт открытый вопрос Q-1 (светлые пресеты нечитаемы на тёмной поверхности) в рамках этой же
  задачи, с сохранением байт-в-байт поведения для каждой панели, существовавшей до задачи.
- Новые чистые типы (`SteppedValue`, `IntegerInputParser`, `PanelColorPresets`, `PanelChromeForegroundPolicy`)
  без WPF/P/Invoke-зависимостей — юнит-тестируемы без STA, тем же паттерном, что `GridSnap`/
  `NaturalStringComparer`/`ContrastCalculator`.

### Известные ограничения / принятые trade-off'ы

- **`ColorDialog` без comctl v6** (осознанное решение ADR-037 — «no comctl v6» в манифесте) отрисуется
  классическими кнопками; `Application.EnableVisualStyles()` намеренно не вызывается, чтобы не задеть вид
  трей-меню. Если вид окажется неприемлемым на живом Windows — отдельная задача, не блокирует эту.
- **Владелец `ColorDialog` — активное окно вызывающего потока, не явный HWND.** Если в момент вызова нет
  активного окна, WinForms создаёт parking-окно, и диалог открывается без визуальной привязки к владельцу —
  деградация без краха, не проверяемая вне живого Windows-десктопа.
- **Icon size 97–128 использует тот же fallback-путь `IconHandler`, что и раньше** — для типов без
  256-кадра в системном image list возможна маленькая иконка в увеличенном поле на самых крупных бакетах;
  при DPI > 200% бакет 256 слегка апскейлится. Это ограничение существовало и до этой задачи (ADR-039), не
  новое.
- **`Glass` при минимальной непрозрачности для светлых пресетов на тёмных обоях** — тот же класс уже
  принятого разрыва (`DO-021`), что и раньше для тёмной базы, теперь симметрично зафиксированный и для
  светлой (`AmbientAccentContrastAuditTests.LightPreset_KnownGap_Glass_AtMinimumOpacity_OnDarkWallpaper_FailsIconContrast`).
  Не исправлено по той же причине, что и `DO-021`: изменение `MinSurfaceOpacity` молча перерисовало бы уже
  сохранённые панели пользователей.
- **Шаг размера (`8`) не настраивается пользователем** — намеренное решение продукта (D-S1), не техническое
  ограничение; расширение до настраиваемого шага не запланировано.

## Изменения существующих ADR

- **[ADR-025](ADR-025-ambient-accent-visual-language.md)** — «нейтральная поверхность» перестаёт быть
  единственной константой `#1E1E1E`: `SurfaceComposer.ResolveNeutralBase` вводит вторую, светлую базу
  `#F3F5F8` для пар (accent, text), где это читается лучше (Решение E). Не переоткрывает ни decision о
  «акцент — семантический и разреженный», ни D-5 (лоссивность миграции v3→v4), ни `Glass ≠ acrylic` (R-8) —
  всё это стоит без изменений; расширяется только выбор нейтральной БАЗЫ, в которую подмешивается акцент.
- **[ADR-030](ADR-030-reference-panel-chrome.md)** — фиксированные (не пользовательские) слои хрома
  (`PART_NeutralStroke`/`PART_InteractionOverlay` hairline, overlay-скроллбар, header-текст/иконки) получают
  luminance-адаптивные токены через `PanelChromeForegroundPolicy` вместо единственного фиксированного набора
  `Color.Text.*`/`Color.ScrollBar.*`/`Color.Border.Default` — необходимое следствие того, что поверхность
  панели больше не гарантированно тёмная (Решение E). Слоистая структура шаблона, glow, accent stroke/marker,
  clipping и persistence-заморозка (§10 того ADR) не затронуты.
- **[ADR-039](ADR-039-sized-shell-icons.md)** — упоминание диапазона слайдера icon size «16–96 DIP, step 4»
  (раздел «200 ms debounce of a size-driven bulk refetch») устарело: диапазон теперь 16–128, шаг 8
  (`SizeStepPolicy`, Решение C). Сам механизм трёхстадийного фолбэка, бакеты `IconPixelSize` (16…256,
  снап вверх) и 200-мс debounce не меняются — расширенный диапазон слайдера лишь пересекает больше бакетов
  за один и тот же жест drag, что уже покрыто существующим debounce-механизмом без изменений в нём.

`ADR-029` (Icon Layout Policy) упоминает диапазон `16–96` только в описании охвата регрессионного теста
`ContentLeftInsetRowCapacityRegressionTests`, зафиксированного на момент своей собственной реализации
(P05, 2026-09-22) — это исторический факт о constructed test grid, а не текущее архитектурное ограничение;
формальный amendment к ADR-029 не требуется, тестовый файл расширен под новый диапазон вместе с этой задачей
без изменения решений того ADR.

## Amendment 2026-09-28 (редизайн: явный режим, 12 пресетов, окно в стиле Windows 11 Personalization)

Та же задача, та же ветка (`feature/release-polish-1.0`), тот же день — до первого выхода ADR-042 в релиз
(ветка ещё не смёржена). Заменяет часть Решений A и E выше по факту реализации; оформлено как amendment
внутри этого же ADR, а не отдельным номером, потому что заменяет решения, ещё не покинувшие черновую ветку.

### Палитра — 12 пресетов вместо 10, раздельный светлый/тёмный текст на каждый

`PanelColorPresets` заменена целиком. Место прежних 10 пар (Azure…Rose, метод `FindExact`, Решение A выше —
теперь историческое) заняли **12** пресетов в порядке спектра, с двумя нейтральными в конце:

| Пресет | Accent |
|---|---|
| Red | `#D9534F` |
| Orange | `#E0812F` |
| Amber | `#D6A324` |
| Lime | `#8FB43A` |
| Green | `#3F9A5A` |
| Teal | `#2F9C93` |
| Cyan | `#2E98C8` |
| Blue (дефолт) | `#3F7FD9` |
| Violet | `#7A5BE7` |
| Magenta | `#C8559E` |
| Graphite | `#596273` |
| Stone | `#7D7468` |

Каждый пресет несёт **оба** текстовых цвета — `DarkModeTextHex` (`#FFFFFF` у всех 12) и свой
`LightModeTextHex` (индивидуальный тёмный оттенок) — вместо одной фиксированной пары (accent, text), как
было у старых 10. Какой из двух применяется, решает выбранный пользователем режим
(`PanelColorPreset.TextFor(PanelThemeMode)`), не сам пресет. `FindByAccent(accentHex)` заменяет
`FindExact(accent, text)`: подсветка выбранного пресета смотрит только на акцент, режим показывается
отдельным сегментированным контролом, а не выводится из пары.

### Явный `PanelThemeMode` вместо порогового вывода по luminance одного акцента

Новый `PanelThemeMode { Dark, Light }` + `PanelThemeRules` заменяют «правило пары»
(`LightAccentLuminanceThreshold = 0.5`, Решение E выше — теперь историческое). Схема persistence не
меняется (замороженный контур ADR-024): режим не хранится отдельным полем, а **выводится** из уже
сохранённого `Model.TextColor` при каждом обращении —

```
PanelThemeMode.Light  ⟺  ContrastRatio(text, LightNeutralBase #F3F5F8) > ContrastRatio(text, DarkNeutralBase #1E1E1E)
```

(WCAG-контраст; точное равенство → `Dark`; практический порог перехода — примерно серый `#7B7B7B`).
`PanelThemeRules.FromTextColor(string?)` парсит вход **только** через `HexBrushConverter.TryParseColor` —
тот же парсер, что красит саму панель, — поэтому вывод режима никогда не разойдётся с тем, что пользователь
реально видит; невалидная строка → `Dark` (тот же fail-safe принцип, что и раньше).
`SurfaceComposer.ResolveNeutralBase(accent, text)` **больше не читает `accent`** — параметр остаётся только
ради стабильности сигнатуры существующих вызовов; база выбирается исключительно
`PanelThemeRules.IsLightSurface(text)`. Все legacy-панели с прежним `TextColor` (`#DDDDDD`/`#FFFFFF`)
остаются byte-identical `Dark` — их текст по-прежнему контрастнее на тёмной базе, чем на светлой.

### Редизайн редактора — карточный flow в духе Windows 11 → Персонализация → Цвета

`PanelColorEditor`, новый отдельный `PanelPreviewCard`, `PanelCustomizeWindow`, `NewPanelDefaultsSection` и
новые примитивы `Settings.xaml` (`SegmentedGroup`/`SegmentedItem`/`CardExpander`) заменяют плоскую
компоновку «10 свотчей 5×2 + 2 hex-поля» на последовательность карточек:

1. **Title card** — заголовок.
2. **Preview card** (`PanelPreviewCard`) — мини-панель с реальными кистями (`SurfaceComposer`/
   `PanelChromeForegroundPolicy`), 4 иконки заголовка, заголовок/подписи в актуальном `TextColor` — живой
   предпросмотр без применения к настоящей панели.
3. **Mode** — сегментированный `Light | Dark`; переключение ставит `TextColor = preset.TextFor(mode)`, если
   текущий акцент совпадает с одним из 12 пресетов, иначе `PanelThemeRules.DefaultTextFor(mode)`;
   идемпотентно (повторный клик по уже выбранному сегменту ничего не переписывает).
4. **Accent colour** — 12 круглых свотчей + 13-я ячейка «Custom…» в одном `WrapPanel` (перенос 7+6); ячейка
   «Custom…» показывает текущий произвольный акцент как выбранный (закрашенный кружок + кольцо + галочка
   вместо пунктирного «+»), когда он не совпадает ни с одним из 12 пресетов.
5. **Advanced** (`Settings.CardExpander`, свёрнут по умолчанию) — исходные hex-поля accent/text + Reset;
   Reset в Customize восстанавливает цвета и appearance из new-panel-defaults `IAppSettings` одним
   сохранением, Reset в Settings — на Blue/Dark.
6. **Surface** — сегментированный `Solid | Ambient | Glass` + Opacity; минимум слайдера —
   `SurfaceComposer.MinSurfaceOpacity` (0.35), коэрция значения при отображении — только визуальная, не
   переписывает уже сохранённое меньшее значение.
7. **Icons**, **Content**, **Layout** — прежние секции, перенесены в тот же карточный вид без изменения
   своей внутренней логики.

**Доступность.** `AutomationProperties.LabeledBy` — на каждой радиокнопке сегмента (не только на группе),
так что читалка озвучивает и название сегмента, и имя группы для каждого варианта; `Focusable="False"` на
`ItemsControl` со свотчами и на заголовке `Expander`, чтобы не добавлять лишний Tab-стоп сверх уже
расставленных; уменьшенная непрозрачность у отключённого сегмента; HC-фикс
`Color.Settings.TabActiveBackground` (`SystemColors.HighlightColor`) для активного сегмента.

**Известный пробел High Contrast (не исправлен, backlog).** Foreground текста сегмента и кольцо выбора
свотча ещё не связаны с `SystemColors.HighlightBrush` — в HC-теме активный сегмент/выбранный свотч может
быть неотличим от невыбранного по цвету текста/кольца (граница видна по 1px hairline, но не по
Highlight-паре). Требует manual verify и отдельную задачу, если подтвердится.

**Источники дизайн-решения:** Параметры Windows 11 → Персонализация → Цвета; WinUI `ColorPicker` guidance;
Fluent segmented control; Stardock Fences 5/6 Personalization; тональные палитры Material 3; WCAG 1.4.3/1.4.11.

### Затронутые изменения существующих ADR (дополнение к разделу выше)

Ничего сверх уже зафиксированного для ADR-025/ADR-030/ADR-039 — `PanelChromeForegroundPolicy` и
адаптивная нейтральная база остаются архитектурно теми же, что описаны в основном разделе «Изменения
существующих ADR»; изменился только способ, которым выбирается режим (явный выбор вместо вывода по одному
акценту), не сам факт существования двух баз/токенов.

---

## Amendment 2026-09-29 (бэклог 1.x, пункты 4 и 5)

Источник — `docs/ROADMAP.md` → «Бэклог 1.x: UX-полировка», пункты «Пресеты цвета должны задавать и цвет
текста» и «Упростить настройку цветов»; мастер-план
[docs/PLANS/backlog-1x-master-plan.md](../docs/PLANS/backlog-1x-master-plan.md) §4–§5. Агент:
`wpf-ui-builder`. Ветка `rc/0.8.0`. Коммит `904ef4d` (третья итерация ревью, доводка до зелёной сборки).

### (а) Пресеты задают собственный цвет текста — не общий белый

**Корневая причина (по коду).** До этой поправки все 12 пресетов несли одинаковый
`DarkModeTextHex = #FFFFFF` — в Dark-режиме (дефолт для новых панелей) любой пресет писал в `TextColor`
одно и то же значение, из-за чего пользователь видел «пресет не меняет текст». Отдельно, no-op guard в
`ApplyAccent` сравнивал **только** accent — если панель уже имела accent пресета, но другой текст (стар
`#DDDDDD`, ручная правка в «Дополнительно»), повторный клик по тому же свотчу не делал ничего: применить
текст пресета заново было нельзя.

**Решение.**
- Каждый из **10 хроматических** пресетов (Red…Magenta) получил **собственный** бледный тинт своего
  оттенка для `DarkModeTextHex` (HSL lightness ≈ 91%, насыщенность ≈ 32%) вместо общего `#FFFFFF`.
  Два нейтральных пресета (Graphite, Stone) намеренно оставлены белыми — у нейтрального акцента нет
  собственного оттенка, к которому имело бы смысл тонировать текст. Каждое значение проверено тестом
  (`PanelColorPresetsContrastTests`) на контраст ≥ 4.5:1 против `SurfaceComposer.ComposeSurface` для
  **каждого** `PanelSurfaceStyle` при минимальной и максимальной `SurfaceOpacity`, и на то, что
  `PanelThemeRules.FromTextColor(DarkModeTextHex)` по-прежнему резолвит `Dark` (тинт остаётся достаточно
  ярче тёмной базы, чтобы режим не «перепрыгнул» на Light только от смены текста пресета).
- **Guard в `ApplyAccent`** (`PanelColorEditorViewModel`) стал no-op только если **и** accent, **и**
  ожидаемый для текущего `Mode` текст пресета (`preset.TextFor(mode)` или `PanelThemeRules.DefaultTextFor(mode)`
  для не-пресетного accent) уже совпадают с текущими значениями панели. Иначе пара пишется одной
  атомарной операцией `_setBoth` — повторный клик по уже выбранному, но «раздрейфовавшему» по тексту
  пресету теперь реально переприменяет его пару.
- Свотч пресета показывает текстовую половину пары — `ColorPresetOption.PreviewTextHex`, пересчитывается
  при смене `Mode` (INPC).
- Состояние существующих панелей **не мигрирует** — `TextColor` уже сохранённой панели не меняется этой
  правкой; новая пара пресета применяется только по явному клику пользователя.

### (б) Упрощённый редактор цвета — один очевидный путь, остальное под «Дополнительно»

**Цель.** До этой правки `PanelColorEditor` показывал 4–6 карточек равного визуального веса (Mode,
Accent grid, Advanced hex, Surface, Opacity) в обоих местах применения (`PanelCustomizeWindow`,
`Settings → New panel defaults`) — решение о режиме текста стояло раньше выбора цвета, а Surface/Opacity
дублировались отдельной разметкой в каждом из двух хостов.

**Решение.** Единая структура для обоих хостов:
1. **Preview** (`PanelPreviewCard`, без изменений).
2. **Карточка «Цвет»** — сетка из 12 пресетов + ячейка «Custom…», с видимой текстовой парой (см. (а)
   выше); под сеткой — компактный, вторичный переключатель Light/Dark. Клик по пресету — это весь путь
   для подавляющего большинства случаев.
3. **«Дополнительно»** (`Settings.CardExpander`, **свёрнуто по умолчанию**; состояние раскрытия хранится
   только в памяти окна редактора, не персистентно — `IAppSettings` не расширялся): raw hex accent/text,
   «Свой цвет текста…», стиль поверхности (Solid/Ambient/Glass), прозрачность, «Сбросить». Прежняя
   отдельная карточка «Mode» как самостоятельная сущность удалена — переключатель переехал под сетку
   пресетов (п.2).
4. Surface/Opacity перенесены из разметки каждого хоста в общий контрол через новый DP-слот
   `PanelColorEditor.AdvancedExtraContent` (`object`, рендерится через `ContentPresenter` внутри
   `Settings.CardExpander`) — каждый хост по-прежнему передаёт свои собственные привязки
   (`Appearance` у Customize, `GlobalDefaults` у New panel defaults); ViewModel самого редактора о
   Surface/Opacity ничего не знает — владение данными осталось у хоста (тот же принцип «thin proxy», что
   уже применён к `SelectedPanelSettingsViewModel`). Icon size и Icon layout остались в хостах — это не
   цвет.

**Ничего не потеряно.** Произвольный accent и текст, hex-ввод, стиль поверхности, прозрачность и Reset —
всё доступно из того же редактора, просто под одним дополнительным раскрытием. Формат хранения цвета
(`accentColor`/`textColor` в состоянии панели) не изменён.

### Затронутые изменения существующих ADR

**Amendment [ADR-031](ADR-031-settings-window-navigation-and-panel-customize.md):** раскладка карточек
внутри `PanelCustomizeWindow`/`NewPanelDefaultsSection` меняется (Preview → «Цвет» → «Дополнительно» →
остальные секции), сама боковая навигация из 8 разделов и немодальность `PanelCustomizeWindow` (решения
самого ADR-031) не пересматриваются.

## Manual verify (Win10/Win11), дополнение к списку ниже

- В Dark и Light каждый хроматический пресет меняет и фон, и текст; на свотче видна текстовая пара.
- У панели с accent пресета и «раздрейфовавшим» текстом (например, ручная правка в «Дополнительно» до
  этой задачи) повторный клик по тому же свотчу применяет текст пресета заново.
- И в Customize, и в New panel defaults сразу видны Preview и сетка пресетов; «Дополнительно» свёрнуто по
  умолчанию, всё прежнее доступно внутри.
- Русская раскладка на ширине окна ≈420 DIP — подписи карточек и пресетов не обрезаются.

---

## Manual verify (Win10/Win11)

- [ ] `ColorDialog` (ячейка «Custom…») из Customize и из Settings модален к своему окну, открывается поверх
      него и возвращает фокус; Esc отменяет; DPI 100/150/200% и перенос окна на монитор с другим DPI
      масштабируется корректно.
- [ ] Вид диалога (классические кнопки без comctl v6) приемлем.
- [ ] Пока открыт диалог, панели на рабочем столе не «замирают»; после закрытия работают LL-хук, двойной
      клик по десктопу и лассо.
- [ ] 13 ячеек (12 пресетов + «Custom…») в одном WrapPanel (перенос 7+6): названия en/ru, подсветка ровно
      одной выбранной ячейки в любой момент, High Contrast (кольцо и галочка видны, активный сегмент Mode/
      Surface отличим — `TabActiveBackground`), Narrator читает название и состояние «выбран».
- [ ] **Ячейка «Custom…» — состояние «выбрана».** Когда текущий акцент не совпадает ни с одним из 12
      пресетов, ячейка показывает закрашенный кружок текущим цветом + кольцо + галочку (не пунктирный «+»),
      и остаётся единственной выбранной ячейкой сетки.
- [ ] **Переключение режима Light/Dark** для всех 12 пресетов на светлых и тёмных обоях — заголовок и
      подписи реально читаемы в обоих режимах (закрывает Q-1 по-новому); переключение сегмента идемпотентно
      (повторный клик не переписывает уже применённый цвет); адаптивная поверхность (светлая/тёмная база)
      переключается вместе с режимом, не выглядит как визуальный баг.
- [ ] **Клавиатурная навигация сегментированных групп** (Mode; Surface Solid/Ambient/Glass) — Tab делает
      одну остановку на всю группу, стрелки переключают фокус между сегментами по кругу/в границах группы,
      Space/Enter применяет фокусный сегмент без «живого» применения при простом перемещении фокуса.
- [ ] **RU-раскладка на ширине окна 420 DIP.** Названия пресетов, подписи сегментов (Mode/Surface) и
      заголовки карточек на русском не обрезаются и не переносятся уродливо ни в `PanelCustomizeWindow`, ни
      в `NewPanelDefaultsSection`, при обоих языках интерфейса.
- [ ] **High Contrast — известный пробел.** Явно проверить и зафиксировать по факту: отличим ли активный
      сегмент/выбранный свотч по foreground/кольцу, если полагаться только на HC-тему (без 1px hairline) —
      см. «Известный пробел High Contrast» в Amendment выше; при подтверждении завести отдельную задачу.
- [ ] **Пол непрозрачности (`SurfaceComposer.MinSurfaceOpacity` = 0.35).** Слайдер Surface Opacity не даёт
      установить значение ниже пола; уже сохранённая панель со значением ниже пола (ручная правка JSON)
      отображается и открывается без перезаписи значения при простом открытии окна.
- [ ] Icon size: ввод 34, затем стрелки, PageUp/Down, клик по треку, drag и колесо (только в фокусе) →
      40/32…; Home/End; ввод «200», «abc», пусто → откат без исключений.
- [ ] Icon size 128 в Grid и List: чёткость на 100/150/200%, типы без 256-кадра, автоширина панели.
- [ ] Grid step: 2 → 8 → … → 128; при смене все LegacyFreeform-панели перестраиваются на новую сетку один
      раз после паузы (Delay 300); тумблер «выкл» гасит слайдер и бокс.
- [ ] Закрытие Customize крестиком с недокоммиченным hex или размером — значение применилось (flush в
      `OnClosing`).
- [ ] `PanelCustomizeWindow` (420×640) на 1366×768 при масштабе 125% (~582 DIP рабочей области) не
      обрезается — `MaxHeight`-коэрция (`ClampHeightToWorkArea`) и внутренний `ScrollViewer` дают доступ ко
      всем полям без выхода окна за экран.

---

**Источник:** [docs/PLANS/color-presets-and-size-step.md](../docs/PLANS/color-presets-and-size-step.md) —
план считается выполненным и остаётся историческим артефактом (прецедент — `docs/PLANS/panel-item-sorting.md`
и ADR-014).

**Расширяет:** [ADR-025](ADR-025-ambient-accent-visual-language.md) (нейтральная поверхность + акцент),
[ADR-030](ADR-030-reference-panel-chrome.md) (слоистый хром), [ADR-039](ADR-039-sized-shell-icons.md)
(бакеты `IconPixelSize`, дальше не меняется).
