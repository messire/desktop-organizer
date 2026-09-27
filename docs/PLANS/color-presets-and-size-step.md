# План реализации — цветовые пресеты + свой цвет (RGB) + единый шаг размеров с ручным вводом

> **Superseded by redesign 2026-09-28.** В тот же день, той же веткой, поверх этого плана прошёл редизайн
> Customize/New panel defaults: палитра выросла до 12 пресетов с раздельным светлым/тёмным текстом на
> каждый, Q-1 ниже закрыт явным `PanelThemeMode` вместо порога/правила пары, а раскладка редактора стала
> карточным flow в стиле Windows 11 Personalization. Актуальное описание — раздел «Amendment 2026-09-28» в
> [ADR-042](../../adr/ADR-042-color-presets-rgb-picker-stepped-size.md) (ближе к концу документа, перед
> Manual verify). План ниже остаётся историческим артефактом «как это было спланировано и реализовано изначально» — не
> переписан задним числом.

> **Статус:** implemented 2026-09-28. База: ветка `feature/release-polish-1.0`, HEAD `b514a36`.
> **Решения пользователя (п. 1–4 запроса) окончательные** и здесь не пересматриваются. План описывает только
> «как». Новый ADR — **[ADR-042](../../adr/ADR-042-color-presets-rgb-picker-stepped-size.md)** «Colour
> presets, RGB picker and stepped size inputs» — написан `architecture-documenter` после слияния; см. также
> «Резолюция Q-1 и заметки по реализации (2026-09-28)» в конце §9 ниже.
>
> **Scope: L.** Затронуты UI (новые контролы, ViewModels, три XAML-места), Services (обёртка WinForms
> `ColorDialog`), Services.Abstractions (один интерфейс), Localization (RESX en/ru) и тесты. Собственного
> P/Invoke нет, persistence не меняется, поэтому оценка не XL.
>
> **Замороженный контур (CLAUDE.md, ADR-024) не затрагивается ни одним пакетом** — обоснование в §8.
> `CLARIFICATION_NEEDED` по п. 3 (дефолты и диапазоны) **не требуется** — доказательство в §2.3.
> Есть **один неблокирующий вопрос Q-1**: читаемость светлых пресетов на текущей тёмной поверхности панели
> (§9) — **решён 2026-09-28**, см. «Резолюция Q-1» в конце §9.

---

## 0. Главные находки планирования

1. **`UseWindowsForms` в UI-проекте НЕТ.** Он включён только в `src/DesktopOrganizer.Services/DesktopOrganizer.Services.csproj`
   (NotifyIcon/ContextMenuStrip живут там: `Tools/NotifyIconAdapter.cs`, `Providers/TrayIconManager.cs`). Включать
   его в `DesktopOrganizer.UI.csproj` **нельзя** по двум причинам:
   - `ImplicitUsings=enable` (`Directory.Build.props`) плюс `UseWindowsForms` добавляют неявные
     `global using System.Windows.Forms` и `System.Drawing`. В Services это уже так (`NotifyIcon` и `Icon` там без
     квалификации). В UI это даст массовые неоднозначности с WPF: `Application`, `MessageBox`, `Clipboard`,
     `DragEventArgs`, `KeyEventArgs`, `MouseEventArgs`, `Button`, `TextBox`, `Color` и т. д.;
   - у UI-exe есть `app.manifest` с `dpiAware/dpiAwareness` (ADR-037). WinForms-анализатор для WinForms-приложения
     выдаёт на это **WFAC010**. Это warning, а у нас действует правило «0 warnings».

   **Решение D-C1:** `ColorDialog` оборачивается в `DesktopOrganizer.Services/Tools/ColorPickerService.cs`, рядом с
   `NotifyIconAdapter`. Интерфейс `IColorPickerService` лежит в `Services.Abstractions` (как `ITrayIconManager`).
   ViewModel'и UI видят только интерфейс. Сам ColorDialog для этого решения **подходит**, поэтому собственный
   WPF-пикер не нужен (fallback-критерии — §4.3).
2. **Владелец диалога.** В кодовой базе нет прецедента WinForms-диалога с WPF-владельцем. `FolderPickerService`
   (`UI/Services`) вызывает `OpenFolderDialog.ShowDialog()` без owner. `CommonDialog.ShowDialog()` без owner
   использует **активное окно вызывающего потока**. Это окно, в котором пользователь только что нажал «Custom…»:
   `PanelCustomizeWindow` или окно настроек. Оба окна обычные и активируемые, без `WS_EX_NOACTIVATE` (его несут
   только `ChildWindow`/`SystemIconsPanelWindow`). Поэтому `IColorPickerService` **не принимает HWND**. VM остаётся
   чистой, а P/Invoke не нужен (инвариант «P/Invoke только за `INativeApi`» соблюдён: `GetActiveWindow` вызывает сам
   WinForms). Если активного окна нет, WinForms создаёт parking-окно, и диалог открывается без привязки к владельцу.
   Это деградация без краха (R-4).
3. **В процессе нет comctl v6** (`app.manifest`: «no comctl v6» — осознанное решение ADR-037). `ChooseColor` будет
   отрисован с классическими кнопками. Пакет **не** вызывает `Application.EnableVisualStyles()`: это глобальное
   состояние WinForms, и оно может изменить вид трей-меню `ContextMenuStrip`. Вид диалога отнесён к manual verify.
   Если вид неприемлем — отдельная задача (R-3).
4. **Сейчас в UI нет ни одного выбора цвета по пресетам.** `AccentPalette.Presets` (6 акцентов ADR-025) используется
   **только тестами** (`AccentPaletteTests`, `AmbientAccentContrastAuditTests`, `HoverOverlay*Tests`). Новые 10 пар
   живут в отдельном `Theming/PanelColorPresets.cs`. `AccentPalette` **не трогаем**: на нём держатся аудиты
   контраста, а `AccentPalette.Neutral` остаётся дефолтом новой панели.
5. **Светлые пресеты (Sky/Lavender/Peach/Mint/Rose) с тёмным текстом рисуются на тёмной поверхности.**
   Поверхность панели = `SurfaceComposer.ComposeSurface(#1E1E1E, accent, style, opacity)`. При Ambient в неё
   подмешивается только 8 % акцента, при Solid — 0 %. Заголовок (`ChildWindow.xaml:381`) и подписи иконок
   (`Templates.xaml:197/305`) красятся `TextColor`. Для Sky текст `#17324D` на поверхности ≈`#2A2F35` даёт
   контраст **≈1.03:1**, то есть текст практически невидим. Реализации это не блокирует (пресеты — данные).
   Вопрос вынесен в Q-1 (§9) и закрепляется characterization-тестом (§7, T-5).
6. **Ни один из трёх слайдеров не пишет значение сам по себе при открытии.** Исключение — WPF-коэрция `Value` в
   `[Minimum, Maximum]` при TwoWay-привязке: она **пишет обратно** (прецедент M-6 в `LayoutSnappingSection.xaml`:
   `Minimum` оставлен 2, чтобы legacy `GridSize=2/3` не переписывался). Новый контрол разрывает эту связь (§5.3):
   внутренний слайдер синхронизируется императивно под guard'ом. Поэтому даже значение вне диапазона
   (ручная правка JSON) **не переписывается** при открытии. Сегодня такое значение переписалось бы.
7. **Дробные `IconSize` в реальных данных существуют** (`AdaptiveGridLayoutBaselineTests`: 31.51…, 32.0101…).
   Они остаются валидными, не округляются и не переписываются при показе. Ручной ввод и слайдер пишут только целые.
   Это частично закрывает открытый вопрос ADR-018 M-1 без миграции.

---

## 1. Scope и non-goals

### В scope
- **S-1. Пресеты как основной способ.** Сетка 5×2 из 10 свотчей с локализованными названиями (en/ru), подсветка
  выбранного пресета при точном совпадении пары (accent, text), выбор пресета ставит оба цвета. Места:
  `PanelCustomizeWindow.xaml` (Appearance) и `Settings/NewPanelDefaultsSection.xaml`. Оба используют **один и тот же**
  контрол `PanelColorEditor`.
- **S-2. «Custom…» (вторичный способ)** — по кнопке на каждый цвет (акцент, текст). Открывается
  `System.Windows.Forms.ColorDialog` (`FullOpen=true`) через мокаемый `IColorPickerService`. Custom-слоты диалога
  (16 шт.) засеваются 10 акцентами и 6 уникальными цветами текста пресетов.
- **S-3. Hex TextBox остаётся третьим, «продвинутым» способом** внутри того же `PanelColorEditor`. В ColorDialog нет
  поля hex, поэтому сворачивать hex в пикер некуда. Поведение и семантика `LostFocus` сохраняются, как и flush при
  закрытии `PanelCustomizeWindow`.
- **S-4. Единый жёстко заданный шаг `8`** для трёх слайдеров: icon size в Customize, icon size в New panel defaults и
  grid step в Layout & snapping. Максимум обоих диапазонов — `128`. Шаг не настраивается пользователем.
- **S-5. Ручной целочисленный ввод** рядом с каждым из трёх слайдеров, с семантикой «вверх — к ближайшему кратному
  сверху, вниз — к ближайшему кратному снизу». Логика вынесена в чистые хелперы, а контрол `SteppedValueEditor`
  используется во всех трёх местах.

### Non-goals
- Изменение `SurfaceComposer`/рендера поверхности (в т. ч. «пастельная заливка») — это Q-1, вариант B, отдельная фича.
- Любые правки persistence/схемы/`IAppSettings`/дефолтов (§8). Пресеты не сохраняются как сущность, панель
  хранит обычные цвета, как сейчас. Пользовательские custom-цвета ColorDialog между сессиями **не** сохраняются.
- Слайдер «Magnet distance» (0–64, tick 4), слайдеры opacity, `SystemIconSize` (enum) — вне запроса, не меняем.
- Пресеты в контекстном меню самой панели.
- Удаление или слияние `AccentPalette.Presets` (R-9).
- Колесо мыши над слайдером, когда слайдер не в фокусе (§5.2).

---

## 2. Решения (ADR-style, кратко; детально — в ADR-042)

### 2.1 D-S1. Шаг = 8
- **Выбор:** `SizeStepPolicy.Step = 8`.
- **Обоснование.** (a) Все «родные» размеры иконок кратны 8: 16/24/32/40/48/64/72/96/128. Это и бакеты
  `IconPixelSize` (ADR-039) при 100 %, кроме 20. Значит, при 100 % выбранный размер почти всегда попадает ровно в
  бакет без ресэмплинга. (b) 8 px — базовая сетка Fluent/Material (4/8-px spacing) и примерно шаг Fences. (c) Шаг 16
  (кратность шкалы Explorer) даёт всего 8 позиций на 16–128, это слишком грубо. Шаг 4 — 29 позиций и много
  промежуточных бакет-переходов с перезапросом иконок (M3 release-polish). Шаг 8 даёт **15 позиций** иконки
  (16…128) и **17 позиций** сетки (2, 8, 16…128).
- **Отвергнуто:** 4 (нынешний tick — избыточно мелко, дробит бакеты), 16 (грубо).

### 2.2 D-S2. Диапазоны
| Величина | Min | Max | Было | Почему |
|---|---|---|---|---|
| Icon size (Customize, New panel defaults) | 16 | **128** | 16–96, tick 4, snap | Max по запросу. Min не меняется: иначе старые значения окажутся вне диапазона |
| Grid step (Layout & snapping) | **2** | 128 | 2–128, tick 4, Small 1 / Large 4 | Min 2 сохраняется ради legacy `GridSize=2/3` (M-6). `1` = «выкл», это состояние управляется тумблером, поэтому ручной ввод `1` отклоняется |

Лестница грид-шага: 2 → 8 → 16 → … → 128. Минимум не кратен шагу, и это корректно: см. `SteppedValue.Next` (§5.1).

### 2.3 Проверка замороженного контура по п. 3 (почему CLARIFICATION не нужен)
| Источник значения | Значение | В новом диапазоне? | Кратно 8? |
|---|---|---|---|
| `Settings.settings` `DefaultIconSize` | 48 | да | да |
| `Settings.settings` `GridSize` | 1 (выкл; слайдер disabled, показывает in-memory `_lastNonOneGridSize=16`) | н/п (1 не редактируется слайдером) | 16 — да |
| `PanelAppearanceDto.IconSize` default | 48 | да | да |
| `SelectedPanelSettingsViewModel` fallback | 48 | да | да |
| `V2ToV3Migration`/`V3ToV4Migration` `DefaultIconSize` | 48 | да | да |
| Всё, что могли записать старые слайдеры | icon 16–96 (в т. ч. дробные), grid 2–128 | да (новые диапазоны — надмножества) | не обязательно — остаётся валидным |

**Ни один дефолт не меняется, и ни одно сохранённое приложением значение не выпадает из нового диапазона.**
Новый контрол не делает write-back при открытии (§5.3). Значения вне диапазона, возможные только после ручной
правки JSON или user.config, показываются как есть и не переписываются.

### 2.4 D-C1. RGB-пикер = WinForms `ColorDialog` в слое Services (см. §0.1–0.3)
- **Альтернатива** (собственный WPF-пикер: RGB-слайдеры + hex + превью) отвергнута как основная: ≈300+ строк XAML/VM
  ради того, что Windows уже умеет (палитра + RGB/HSL + custom-слоты), плюс собственная a11y-поверхность.
  Остаётся **fallback-планом** (§4.3).
- **Hex TextBox** сохраняется как третий способ (§1, S-3).

### 2.5 D-U1. Один составной контрол на каждую из двух фич
- `Controls/PanelColorEditor` (пресеты + строки accent/text с hex, превью и «Custom…») — в обоих местах выбора цвета.
- `Controls/SteppedValueEditor` (`SteppedSlider` + `IntegerInputBox` + метка «px») — во всех трёх местах выбора размера.
- Поведение задаётся **чистыми** хелперами (`SteppedValue`, `IntegerInputParser`, `PanelColorPresets`), которые
  тестируются без STA.

---

## 3. Контракты (фиксируются ДО начала работ — по ним пакеты работают параллельно)

> Сигнатуры обязательны. Имена приватных членов и XML-doc — на усмотрение реализующего агента (StyleCop: doc на
> всём публичном, как принято в проекте).

### 3.1 `src/DesktopOrganizer.UI/Models/SizeStepPolicy.cs` (WP-A)
```csharp
namespace DesktopOrganizer.UI.Models;
public static class SizeStepPolicy
{
    public const int Step = 8;               // D-S1
    public const int IconSizeMinimum = 16;
    public const int IconSizeMaximum = 128;
    public const int GridStepMinimum = 2;    // legacy M-6
    public const int GridStepMaximum = 128;
}
```

### 3.2 `src/DesktopOrganizer.UI/Models/SteppedValue.cs` (WP-A)
```csharp
namespace DesktopOrganizer.UI.Models;
public enum StepDirection { Down = -1, Up = 1 }
public static class SteppedValue
{
    public const double Tolerance = 1e-6;
    // Ближайшее кратное step строго выше (Up) или строго ниже (Down) current, затем clamp в [minimum, maximum].
    public static int Next(double current, int step, StepDirection direction, int minimum, int maximum);
    // Значение для drag'а: округление raw к ближайшему кратному (tie → вверх), но не «против» направления drag'а
    // относительно dragStart (см. §5.1).
    public static int SnapForDrag(double raw, double dragStart, int step, int minimum, int maximum);
    // Внешнее/UIA-значение → целое: Math.Round(AwayFromZero) + clamp. NaN/±Inf → minimum.
    public static int RoundAndClamp(double value, int minimum, int maximum);
}
```
Семантика — §5.1. Исключения: `step < 1` → `ArgumentOutOfRangeException`; `minimum > maximum` →
`ArgumentOutOfRangeException`. `current`/`raw`/`dragStart` вида NaN/±Inf не бросают исключений и считаются `minimum`.

### 3.3 `src/DesktopOrganizer.UI/Models/IntegerInputParser.cs` (WP-A)
```csharp
namespace DesktopOrganizer.UI.Models;
public enum IntegerInputError { None, Empty, NotAnInteger, OutOfRange }
public static class IntegerInputParser
{
    // Trim; только ASCII-цифры (NumberStyles.None, InvariantCulture); ведущие нули разрешены; переполнение → NotAnInteger.
    public static IntegerInputError TryParse(string? text, int minimum, int maximum, out int value);
    // true, если каждый символ фрагмента — ASCII-цифра (для PreviewTextInput и paste).
    public static bool IsAllowedInputChunk(string? chunk);
    // Math.Round(AwayFromZero).ToString("0", InvariantCulture); NaN/±Inf → string.Empty.
    public static string FormatForDisplay(double value);
}
```

### 3.4 `src/DesktopOrganizer.UI/Theming/PanelColorPresets.cs` (WP-A)
```csharp
namespace DesktopOrganizer.UI.Theming;
public sealed record PanelColorPreset(string Id, string AccentHex, string TextHex)
{
    public string NameKey => "ColorPreset_" + Id;   // RESX-ключ названия
}
public static class PanelColorPresets
{
    public static PanelColorPreset Azure { get; }    // "#3F7FD9" / "#FFFFFF"
    public static PanelColorPreset Violet { get; }   // "#7A5BE7" / "#FFFFFF"
    public static PanelColorPreset Coral { get; }    // "#D95F59" / "#FFFFFF"
    public static PanelColorPreset Forest { get; }   // "#397A5F" / "#FFFFFF"
    public static PanelColorPreset Graphite { get; } // "#596273" / "#FFFFFF"
    public static PanelColorPreset Sky { get; }      // "#B9DCFF" / "#17324D"
    public static PanelColorPreset Lavender { get; } // "#D9C7FF" / "#392C57"
    public static PanelColorPreset Peach { get; }    // "#FFD1B8" / "#5B3528"
    public static PanelColorPreset Mint { get; }     // "#BFE8D5" / "#23483B"
    public static PanelColorPreset Rose { get; }     // "#F3C3D7" / "#533142"
    public static IReadOnlyList<PanelColorPreset> All { get; }          // ровно в этом порядке
    public static IReadOnlyList<string> CustomDialogSeed { get; }       // 16 шт.: 10 акцентов по порядку + 6 уникальных текстов
                                                                        // (#FFFFFF,#17324D,#392C57,#5B3528,#23483B,#533142)
    public static PanelColorPreset? FindExact(string? accentHex, string? textHex);
    internal static bool TryNormalizeOpaqueRgb(string? hex, out string normalized); // #RGB,#RRGGBB,#FRGB,#FFRRGGBB → "#RRGGBB";
                                                                                     // альфа ≠ F/FF или мусор → false
}
```
`FindExact` нормализует обе строки через `TryNormalizeOpaqueRgb` и сравнивает регистронезависимо.
`AccentPalette.Normalize` **не используется**: на мусор она возвращает `Neutral`, что для сравнения неоднозначно.

### 3.5 `src/DesktopOrganizer.Services.Abstractions/Interfaces/IColorPickerService.cs` (WP-A)
```csharp
namespace DesktopOrganizer.Services.Abstractions;
public interface IColorPickerService
{
    /// Показывает системный диалог выбора цвета (модальный к активному окну вызывающего потока).
    /// initialHex — стартовый цвет (#RGB/#RRGGBB/#AARRGGBB; мусор/null → белый).
    /// customColors — до 16 hex-цветов для custom-слотов (лишние отбрасываются, невалидные пропускаются).
    /// Возвращает "#RRGGBB" (верхний регистр) или null при отмене/сбое. Никогда не бросает.
    string? PickColor(string? initialHex, IReadOnlyList<string> customColors);
}
```

### 3.6 `src/DesktopOrganizer.UI/ViewModels/PanelColorEditorViewModel.cs` + `ColorPresetOption.cs` (WP-B)
```csharp
public sealed class PanelColorEditorViewModel : INotifyPropertyChanged
{
    public PanelColorEditorViewModel(
        IColorPickerService colorPicker,
        Func<string> getAccent, Action<string> setAccent,
        Func<string> getText, Action<string> setText,
        Action<string, string> setBoth);                      // атомарное применение пресета (один save)
    public IReadOnlyList<ColorPresetOption> Presets { get; }  // по PanelColorPresets.All
    public string AccentColor { get; set; }                   // → getAccent/setAccent (для hex TextBox, TwoWay)
    public string TextColor { get; set; }
    public PanelColorPreset? SelectedPreset { get; }          // FindExact(AccentColor, TextColor)
    public ICommand PickCustomAccentCommand { get; }          // picker.PickColor(AccentColor, CustomDialogSeed) → setAccent
    public ICommand PickCustomTextCommand { get; }
    public void Refresh();                                    // зовёт владелец при внешнем изменении цветов:
                                                              // raise AccentColor/TextColor/SelectedPreset + option.IsSelected
}
public sealed class ColorPresetOption : INotifyPropertyChanged
{
    public PanelColorPreset Preset { get; }
    public string NameKey { get; }
    public string AccentHex { get; }
    public string TextHex { get; }
    public bool IsSelected { get; }                            // internal set + PropertyChanged
    public ICommand ApplyCommand { get; }                      // → setBoth(Preset.AccentHex, Preset.TextHex)
}
```

### 3.7 Изменения существующих публичных API (WP-B)
- `SelectedPanelSettingsViewModel.ApplyColors(string accent, string text)`. Один `Apply(p => { p.AccentColor = accent;
  p.TextColor = text; })`, то есть **один** `SaveWindowsState()`.
- `GlobalDefaultsViewModel`: ctor `(IAppSettings, IWindowManager, IColorPickerService)`; свойство
  `PanelColorEditorViewModel DefaultColors`; метод `SetDefaultColors(string accent, string text)` — два
  `SetGlobalNoSave` и один `Save()`, ровно по образцу `ApplyFromPanel`.
- `MainWindowViewModel`: новый обязательный параметр `IColorPickerService colorPicker` **сразу после**
  `AboutViewModel about` (перед `IAppLog? appLog = null`), пробрасывается в `GlobalDefaultsViewModel`; свойство
  `PanelColorEditorViewModel DefaultColors => GlobalDefaults.DefaultColors`.
- `PanelCustomizeViewModel`: 5-й параметр ctor `IColorPickerService colorPicker`; свойство
  `PanelColorEditorViewModel Colors`. Делегаты ведут на `Appearance.SelectedPanelAccentColor/TextColor`, а `setBoth` —
  на `Appearance.ApplyColors`. `Refresh()` вызывается из существующего `OnAppearancePropertyChanged` при
  `SelectedPanelAccentColor`/`SelectedPanelTextColor`; это покрывает и retarget, потому что `SetPanel` их рейзит.

---

## 4. Цветовой редактор — детали

### 4.1 `ColorPickerService` (WP-A, `src/DesktopOrganizer.Services/Tools/ColorPickerService.cs`)
- `internal sealed class ColorPickerService(IAppLog appLog) : IColorPickerService`.
- `using var dialog = new ColorDialog { AllowFullOpen = true, FullOpen = true, AnyColor = true, SolidColorOnly = true,
  Color = start, CustomColors = seed }`, затем `dialog.ShowDialog() == DialogResult.OK ? ToHex(dialog.Color) : null`.
- Чистые преобразования вынесены в `Tools/ColorDialogColor.cs` (`internal static`):
  - `bool TryParseHex(string?, out Color)` — `#RGB`/`#RRGGBB`/`#AARRGGBB`, альфа игнорируется;
  - `int ToColorRef(Color)` — COLORREF `0x00BBGGRR`, т. е. `#3F7FD9` → `0x00D97F3F`;
  - `string ToHex(Color)` → `"#RRGGBB"` в верхнем регистре;
  - `int[] BuildCustomColors(IReadOnlyList<string>)` — максимум 16, невалидные пропускаются.
- Сбой (`Win32Exception`/`ExternalException`/`InvalidOperationException`) → `appLog.Warn("ColorPicker", "ShowFailed", …)` и
  `null`. **Без** `Application.EnableVisualStyles()` (§0.3).
- Регистрация: `services.AddSingleton<IColorPickerService, ColorPickerService>();` в
  `src/DesktopOrganizer.Services/Configuration/DiConfig.cs`, рядом с `INotifyIconAdapter`.

### 4.2 `PanelColorEditor` (WP-B, `src/DesktopOrganizer.UI/Controls/PanelColorEditor.xaml(.cs)`)
Раскладка (DataContext = `PanelColorEditorViewModel`):
```
[TextBlock {loc:Loc Common_ColorPresets_Header}]
[ItemsControl Presets — UniformGrid Columns=5 (2 ряда)]
   RadioButton (без GroupName: эксклюзивность задаёт VM через IsSelected)
     IsChecked="{Binding IsSelected, Mode=OneWay}"  Command="{Binding ApplyCommand}"
     AutomationProperties.Name="{loc:LocBinding NameKey, Converter={StaticResource LocKey}}"
     ToolTip = то же название
     Шаблон: Border 36×36 CornerRadius 6, Background=AccentHex|HexBrush, BorderBrush SwatchBorder;
             внутри TextBlock Text="{loc:Loc Common_ColorPreset_Sample}" Foreground=TextHex|HexBrush;
             под свотчем TextBlock (FontSize 11) с названием; кольцо выбора 2px
             {DynamicResource {x:Static SystemColors.HighlightBrushKey}} + галочка-глиф &#xE73E; при IsChecked
             (видно в High Contrast); фокус-визуал стандартный.
   ItemsControl: KeyboardNavigation.TabNavigation="Once", DirectionalNavigation="Cycle" (одна Tab-остановка,
   стрелки двигают фокус, Space/Enter применяет — без «живого» применения при стрелках)
[Grid 4 колонки: Label | hex TextBox (x:Name AccentHexBox) | превью-свотч 24×24 | Button «Custom…»]
   Accent: {loc:Loc Common_AccentColor_Label}; TextBox Text="{Binding AccentColor, UpdateSourceTrigger=LostFocus}"
           HelpText {loc:Loc Common_HexColor_Hint}; Button Content {loc:Loc Common_CustomColor_Button},
           Name {loc:Loc Common_PickAccentColor_A11y}, Command PickCustomAccentCommand
   Text:   то же с Common_TextColor_Label / TextHexBox / Common_PickTextColor_A11y / PickCustomTextCommand
```
- Публичный метод `CommitPendingEdits()`: тот же код, что сейчас в `PanelCustomizeWindow.OnClosing` (`IsDirty` →
  `UpdateSource()` для обоих hex-боксов).
- **Ресурсы.** Можно использовать только ресурсы узкого набора фикстуры `PanelCustomizeWindowClosingTests`
  (Colors/Spacing/Brushes/Typography/Icons/Controls/Panels/Templates: `HexBrush`, `Brush.Settings.SwatchBorder`,
  `Brush.Settings.TextMuted`). `LocKey` объявлен в `Settings.xaml`, которого в этом наборе **нет**, поэтому контрол
  объявляет `<loc:LocKeyConverter x:Key="LocKey"/>` **в своих `UserControl.Resources`** (прецедент — `EnumDisplayName`
  в `PanelCustomizeWindow.xaml`).
- Никаких литералов в `Text/Content/ToolTip/AutomationProperties.*` — только `{loc:…}` (gate G-L1).

### 4.3 Fallback-критерии для собственного WPF-пикера (не делаем сейчас)
Переход на in-house пикер (RGB-слайдеры 0–255 + hex + превью, WPF-окно, те же `IColorPickerService`/тесты VM)
оправдан, только если manual verify покажет одно из:
- (a) диалог не модален к окну-источнику или уходит под него;
- (b) неверный DPI или вид, заметно ломающий UX на Win11;
- (c) сбой модальной петли (хуки/панели «замирают»).

Замена затрагивает только реализацию `IColorPickerService`, VM и XAML не меняются.

---

## 5. Шаговые размеры — детали

### 5.1 Семантика `SteppedValue` (step = 8)
`Next(current, step, dir, min, max)`:
1. `c = clamp(current, min, max)`; NaN/±Inf → `min`.
2. Up: если `c ≥ max − Tol` → `max`; иначе `k = floor((c + Tol)/step)`, `candidate = (k+1)·step` — наименьшее кратное
   строго выше `c`. Значение в пределах `Tol` от кратного считается этим кратным. Результат `min(candidate, max)`.
3. Down: если `c ≤ min + Tol` → `min`; иначе `k = ceil((c − Tol)/step)`, `candidate = (k−1)·step` — наибольшее кратное
   строго ниже. Результат `max(candidate, min)`.

| current | dir | min..max | → |
|---|---|---|---|
| 34 | Up | 16..128 | 40 |
| 34 | Down | 16..128 | 32 |
| 40 | Up | 16..128 | 48 |
| 40 | Down | 16..128 | 32 |
| 32.0101 | Up / Down | 16..128 | 40 / 32 |
| 31.9999999 | Up | 16..128 | 40 (в пределах Tol → 32) |
| 47.5 | Up / Down | 16..128 | 48 / 40 |
| 16 | Down | 16..128 | 16 |
| 124 | Up | 16..128 | 128 |
| 128 | Up | 16..128 | 128 |
| 200 (вне диапазона) | Down | 16..128 | 120 |
| 2 | Up | 2..128 | 8 |
| 8 | Down | 2..128 | 2 |
| 5 | Down / Up | 2..128 | 2 / 8 |
| NaN | Up | 2..128 | 8 |

`SnapForDrag(raw, dragStart, step, min, max)`: `r = clamp(raw)`, `nearest = clamp(round(r/step, AwayFromZero)·step)`.
- `r > dragStart + Tol` → `min(max(nearest, Next(dragStart, Up)), max)`.
- `r < dragStart − Tol` → `max(min(nearest, Next(dragStart, Down)), min)`.
- иначе → `RoundAndClamp(dragStart)`. Вызывающий код не коммитит результат, если он отличается от текущего значения
  меньше чем на 0.5 (guard в §5.3). Поэтому нулевой drag с дробного старта (32.0101) ничего не пишет.

| raw | dragStart | → | Комментарий |
|---|---|---|---|
| 34.2 | 34 | 40 | минимальное движение вверх → ближайшее кратное сверху |
| 33.8 | 34 | 32 | вниз → ближайшее снизу |
| 60 | 34 | 64 | round(7.5)=8 (tie вверх) |
| 44.1 | 34 | 48 | round(5.51)=6 |
| 3 | 8 (grid) | 2 | round → 0 → clamp min |
| 127 | 120 | 128 | |

### 5.2 `SteppedSlider : Slider` (WP-B, `src/DesktopOrganizer.UI/Controls/SteppedSlider.cs`)
- DP `Step` (int, default `SizeStepPolicy.Step`, валидатор `≥1`). В ctor: `IsSnapToTickEnabled=false`,
  `TickFrequency=Step`, `SmallChange=Step`, `LargeChange=Step`. `IsMoveToPointEnabled` не трогаем (false): клик по
  треку идёт через `DecreaseLarge/IncreaseLarge`. Стиль — тема Slider (`DefaultStyleKey` наследуется, неявных
  стилей Slider в проекте нет).
- **Направление берётся из того, какой механизм сработал, а не из дельты `ValueChanged`.** Переопределяются
  protected virtual методы WPF `Slider`:
  - `OnIncreaseSmall/OnIncreaseLarge` → `MoveTo(SteppedValue.Next(Value, Step, Up, (int)Minimum, (int)Maximum))`;
    так обрабатываются клавиши Right/Up/PageUp и клик по треку справа (RepeatButton повторяет шаг при удержании);
  - `OnDecreaseSmall/OnDecreaseLarge` → `Next(…, Down)`;
  - `OnMinimizeValue/OnMaximizeValue` (Home/End) → `MoveTo(Minimum/Maximum)`;
  - `OnThumbDragStarted` → `_dragStart = Value`, затем base; `OnThumbDragCompleted` → `_dragStart = null`, затем base;
  - `OnThumbDragDelta` — **base не вызывается**: `raw = Value + _track.ValueFromDistance(e.HorizontalChange,
    e.VerticalChange)`, затем `MoveTo(SnapForDrag(raw, _dragStart ?? Value, …))`. `_track` берётся из
    `GetTemplateChild("PART_Track") as Track` в `OnApplyTemplate`. Если `_track == null` (чужая тема) → `base`
    (деградация к несошагованному поведению, R-5). Дельта Thumb считается относительно текущей позиции thumb,
    поэтому после записи кратного значения движение продолжается от точки курсора и не «залипает».
  - **Колесо мыши:** `OnPreviewMouseWheel` обрабатывается **только** при `IsKeyboardFocusWithin`
    (`Delta>0` → Up, `<0` → Down, `e.Handled=true`). Без фокуса событие не трогаем, и `ScrollViewer` окна
    прокручивается как обычно. Перехватывать колесо на прокрутке — известный UX-анти-паттерн.
- `MoveTo(int v)`: `SetCurrentValue(ValueProperty, (double)v)`. Состояние хранится одно — `_dragStart`. Коммит во
  внешний мир делает не слайдер, а `SteppedValueEditor` (§5.3).

### 5.3 `IntegerInputBox : TextBox` и `SteppedValueEditor` (WP-B)
**`IntegerInputBox`** (`Controls/IntegerInputBox.cs`):
- DP `Value` (double), `Minimum`/`Maximum` (int). Событие `ValueCommitted(int)` (CLR `EventHandler<int>`).
- `MaxLength = 3`, `HorizontalContentAlignment=Right`, `InputScope` Number.
- Фильтр ввода: `PreviewTextInput` отклоняет фрагмент, если `!IntegerInputParser.IsAllowedInputChunk`. Пробел
  отсекается в `PreviewKeyDown` (Key.Space): в WPF TextBox он не проходит через `PreviewTextInput`. Вставка:
  `DataObject.AddPastingHandler` → `CancelCommand`, если текст не только из цифр.
- **Enter** (`PreviewKeyDown`) → `Commit()`, затем `SelectAll()`, `e.Handled=true`. **Escape** → откат текста.
  **LostKeyboardFocus** → `Commit()`.
- `Commit()`: если текст не менялся с последней синхронизации (флаг `_isDirty`) — **ничего не делать**. Так дробное
  32.0101, показанное как «32», не перезапишется при уходе фокуса. Иначе вызывается `TryParse`. `None` → поднять
  `ValueCommitted(v)`. Любая ошибка → откат текста к `FormatForDisplay(Value)`: значение не меняется, исключений
  нет, звука нет (как у NumberBox в Win11). Подсказка о допустимом диапазоне всегда видна в ToolTip/HelpText.
- Синхронизация: при изменении `Value` текст обновляется, **если** `!(IsKeyboardFocusWithin && _isDirty)`.
- ToolTip/`AutomationProperties.HelpText` = `LocalizationSource.Current.Format("Common_IntegerRange_Format", Minimum,
  Maximum)`. Пересчёт выполняется в `Loaded`, при смене Min/Max и по `ILocalizationService.CultureChanged`.
  Подписка на это событие делается в `Loaded` и снимается в `Unloaded`, чтобы не держать утечку.

**`SteppedValueEditor`** (`Controls/SteppedValueEditor.xaml(.cs)`, UserControl):
- DP: `Value` (double, `BindsTwoWayByDefault`), `Minimum`, `Maximum` (int), `Step` (int, default
  `SizeStepPolicy.Step`), `AutomationName` (string).
- Разметка: `Grid [SteppedSlider *] [IntegerInputBox Width=48] [TextBlock {loc:Loc Common_PixelUnit_Label}]`.
  `AutomationName` уходит в `AutomationProperties.Name` слайдера и бокса.
- **Анти-петля и «без записи при загрузке».** Внутренний слайдер и бокс **не привязаны** к `Value` через Binding.
  Синхронизация императивная:
  - `OnValueChanged` (PropertyChangedCallback): `_syncing=true; slider.Value = Value; box.Value = Value;
    _syncing=false`. Коэрция слайдера к `[Min,Max]` происходит внутри guard'а и **не пишет обратно**;
  - `slider.ValueChanged`: `if (_syncing) return;` иначе `CommitFromUser(SteppedValue.RoundAndClamp(slider.Value, …))`.
    Сюда попадают и шаговые переходы из `SteppedSlider`, и `SetValue` через UIA/RangeValue (он округляется);
  - `box.ValueCommitted` → `CommitFromUser(v)`;
  - `CommitFromUser(int v)`: `if (Math.Abs(v − Value) < 0.5) return;` иначе `SetCurrentValue(ValueProperty, (double)v)`
    (Binding сохраняется, TwoWay уходит в VM) → callback синхронизирует слайдер и бокс под guard'ом → петли нет.
  - `CommitPendingEdit()` → `box.Commit()` (для flush при закрытии окна).
- Частота записи: при drag'е `Value` меняется только на шаговых переходах. Это ≤ 15/17 записей за весь ход вместо
  «на каждый пиксель».

### 5.4 Места применения
| Место | Было | Станет |
|---|---|---|
| `PanelCustomizeWindow.xaml:118–123` (Icon size) | `Slider 16–96 Tick 4 Snap` + `TextBlock {0} px` | `<controls:SteppedValueEditor x:Name="IconSizeEditor" Grid.ColumnSpan="2" Minimum="{x:Static models:SizeStepPolicy.IconSizeMinimum}" Maximum="{x:Static models:SizeStepPolicy.IconSizeMaximum}" Value="{Binding Appearance.SelectedPanelIconSize}" AutomationName="{loc:Loc Common_IconSize_Header}"/>` |
| `NewPanelDefaultsSection.xaml:69–85` (Icon size) | `Slider 16–96 Tick 4 Snap` (кол. 140) + `TextBlock` (кол. 48) | `SteppedValueEditor` (кол. 220), `Value="{Binding DefaultIconSize}"` |
| `LayoutSnappingSection.xaml:38–50` (Grid step) | `Slider 2–128 Tick 4 Small 1 Large 4`, `Delay=300` | `SteppedValueEditor Minimum=GridStepMinimum Maximum=GridStepMaximum Value="{Binding GridStep, Mode=TwoWay, Delay=300}"`. `Delay=300` **сохраняется**: каждый коммит `GridSize` вызывает `NormalizePanelsToGrid()` для всех панелей. `IsEnabled` родительского Grid (SnapToGridEnabled) гасит и бокс |
| `PanelCustomizeWindow.xaml:65–79` (Accent/Text) | 2 hex TextBox + 2 свотча | `<controls:PanelColorEditor x:Name="ColorEditor" DataContext="{Binding Colors}" Grid.ColumnSpan="3"/>` в начале Appearance |
| `NewPanelDefaultsSection.xaml:14–48` (Accent/Text) | 2 карточки | 1 карточка: `CardHeader {loc:Loc Common_Colors_Header}` + `PanelColorEditor DataContext="{Binding DefaultColors}"` |
| `PanelCustomizeWindow.xaml.cs` `OnClosing` | flush `AccentColorTextBox`/`TextColorTextBox` | `ColorEditor.CommitPendingEdits(); IconSizeEditor.CommitPendingEdit();` |
| `PanelCustomizeWindow.xaml` | Height 560 | Height ≈ 640 (ScrollViewer сохраняется) |

---

## 6. Артефакты по слоям

### 6.1 Localization — `src/DesktopOrganizer.Localization/Strings.resx` + `Strings.ru.resx` (WP-A, оба файла, одинаковые позиции)
**Новые ключи:**
| Ключ | en | ru |
|---|---|---|
| `ColorPreset_Azure` | Azure | Лазурь |
| `ColorPreset_Violet` | Violet | Фиалка |
| `ColorPreset_Coral` | Coral | Коралл |
| `ColorPreset_Forest` | Forest | Лес |
| `ColorPreset_Graphite` | Graphite | Графит |
| `ColorPreset_Sky` | Sky | Небо |
| `ColorPreset_Lavender` | Lavender | Лаванда |
| `ColorPreset_Peach` | Peach | Персик |
| `ColorPreset_Mint` | Mint | Мята |
| `ColorPreset_Rose` | Rose | Роза |
| `Common_ColorPresets_Header` | Color presets | Готовые цвета |
| `Common_ColorPreset_Sample` | Aa | Аа |
| `Common_Colors_Header` | Colors | Цвета |
| `Common_AccentColor_Label` | Accent color | Цвет акцента |
| `Common_TextColor_Label` | Text color | Цвет текста |
| `Common_HexColor_Hint` | Hex value, e.g. #3F7FD9 | Hex-значение, например #3F7FD9 |
| `Common_CustomColor_Button` | Custom… | Другой… |
| `Common_PickAccentColor_A11y` | Choose a custom accent color | Выбрать свой цвет акцента |
| `Common_PickTextColor_A11y` | Choose a custom text color | Выбрать свой цвет текста |
| `Common_IntegerRange_Format` | Whole number from {0} to {1} | Целое число от {0} до {1} |
| `Common_PixelUnit_Label` | px | пкс |

**Удаляются** (после замены становятся неиспользуемыми; удалить в обоих файлах, проверив Grep'ом по `src/`):
`Customize_AccentColor_Header`, `Customize_TextColor_Header`, `Settings_NewPanelDefaults_AccentColor_Label`,
`Settings_NewPanelDefaults_TextColor_Label`. Ключ `Common_ValuePx_Format` **остаётся**: он используется в других местах
(`GridSnapDistance`).

### 6.2 UI
| Файл | Пакет | Действие |
|---|---|---|
| `Models/SizeStepPolicy.cs` | A | новый |
| `Models/SteppedValue.cs` | A | новый |
| `Models/IntegerInputParser.cs` | A | новый |
| `Theming/PanelColorPresets.cs` | A | новый |
| `Models/GridMetrics.cs` | A | только комментарий на стр. ~218 (`{16,20,24,28}` → «низ диапазона, шаг 8: {16, 24}») |
| `Controls/SteppedSlider.cs` | B | новый |
| `Controls/IntegerInputBox.cs` | B | новый |
| `Controls/SteppedValueEditor.xaml` + `.xaml.cs` | B | новый |
| `Controls/PanelColorEditor.xaml` + `.xaml.cs` | B | новый |
| `ViewModels/PanelColorEditorViewModel.cs`, `ViewModels/ColorPresetOption.cs` | B | новые |
| `ViewModels/SelectedPanelSettingsViewModel.cs` | B | `ApplyColors` |
| `ViewModels/PanelCustomizeViewModel.cs` | B | ctor + `Colors` + `Refresh` в `OnAppearancePropertyChanged` |
| `ViewModels/GlobalDefaultsViewModel.cs` | B | ctor + `DefaultColors` + `SetDefaultColors` + `Refresh` в `OnPropertyChanged`; doc «4–128» → «2–128» |
| `ViewModels/MainWindowViewModel.cs` | B | ctor-параметр + `DefaultColors`; doc «4–128» → «2–128» |
| `ViewModels/ChildWindowViewModel.cs` | B | doc «(16–96, step 4)» → «(16–128, step 8)» |
| `Views/PanelCustomizeWindow.xaml` + `.xaml.cs` | B | §5.4 |
| `Views/Settings/NewPanelDefaultsSection.xaml` | B | §5.4 |
| `Views/Settings/LayoutSnappingSection.xaml` | B | §5.4 (комментарий M-6 сохранить и дополнить) |
| `Configuration/DiConfig.cs` | B | лямбда `MainWindowViewModel`: `sp.GetRequiredService<IColorPickerService>()` после `AboutViewModel` |

### 6.3 Services / Services.Abstractions
| Файл | Пакет |
|---|---|
| `Services.Abstractions/Interfaces/IColorPickerService.cs` (новый) | A |
| `Services/Tools/ColorPickerService.cs` (новый), `Services/Tools/ColorDialogColor.cs` (новый) | A |
| `Services/Configuration/DiConfig.cs` (одна строка регистрации) | A |

**Не меняются** (проверено): `IconPixelSize` (ADR-039). `FromDip(128, s)` даёт 128/192/256 при 100/125–150/200 %,
а выше 200 % результат упирается в потолок 256 (R-6). Также не меняются `IconHandler.SystemImageListFor`
(128 → jumbo; R-6), `WorkspaceLayoutPolicy`/`WorkspaceGridMetrics` (не зависят от IconSize по ADR-028),
`GridSizeNormalizer`, `ResizeConstraintsFactory`, все State/DTO/Migrations.

### 6.4 Тесты — см. §7 (WP-C, только после A и B; существующие ctor-места чинит WP-B)
Существующие тесты, у которых меняется **только конструирование** (правит WP-B, механически):
`Unit/ViewModels/PanelCustomizeViewModelTests.cs` (`BuildSut`, ~стр. 705),
`Unit/Views/PanelCustomizeWindowClosingTests.cs` (~стр. 166),
`Unit/Services/PanelCustomizeServiceTests.cs` (~стр. 241),
`Unit/ViewModels/MainWindowViewModelTests.cs` (helper, ~стр. 1529),
`Unit/ViewModels/GlobalDefaultsViewModelTests.cs` (`BuildSut`, стр. 340).
В каждом добавляется `Mock<IColorPickerService>().Object`.

---

## 7. Тест-план (WP-C, `tdd-test-engineer`)

**Новые файлы** (`tests/DesktopOrganizer.Tests/Unit/...`):
- **T-1 `Models/SteppedValueTests.cs`** — `[Theory]` по обеим таблицам §5.1; `step=0/−8` → `ArgumentOutOfRangeException`;
  `min>max` → `ArgumentOutOfRangeException`; NaN/±Inf у `current`/`raw`/`dragStart`; `RoundAndClamp` (47.5→48, 200→128,
  NaN→min); свойство «Up никогда не уменьшает и Down никогда не увеличивает значение» — перебор current 2..128 шагом 0.5.
- **T-2 `Models/SizeStepPolicyTests.cs`** — `Step==8`, чётный; `IconSizeMaximum==GridStepMaximum==128`;
  `IconSizeMinimum==16`, `GridStepMinimum==2`; `IconSizeMinimum % Step == 0`; `128 % Step == 0`; надмножество
  legacy-диапазонов (16–96 ⊂ 16–128; 2–128 ⊂ 2–128). Дефолты в диапазоне и кратны шагу:
  `Properties.Settings.Default.Properties["DefaultIconSize"].DefaultValue == "48"` (только метаданные, user.config не
  читается) и `new PanelAppearanceDto().IconSize == 48`.
- **T-3 `Models/IntegerInputParserTests.cs`** —
  - принимаются: `"16"`, `"128"`, `"34"`, `"048"`→48, `" 40 "`→40;
  - `Empty`: `""`, `"   "`, `null`;
  - `NotAnInteger`: `"abc"`, `"-8"`, `"+8"`, `"12.5"`, `"12,5"`, `"1 000"`, `"1e2"`, `"٣٤"`, `"３４"`, `"99999999999"`;
  - `OutOfRange`: `"15"`/`"129"` (16..128), `"1"` (2..128);
  - `IsAllowedInputChunk`: `"7"` → true, `"-"`/`"a"`/`" "`/`""` → false;
  - `FormatForDisplay`: 32.0101→`"32"`, 47.5→`"48"`, NaN→`""`, при ru-культуре — без разделителей групп.
- **T-4 `Theming/PanelColorPresetsTests.cs`** —
  - `All` — ровно 10 пресетов в порядке и с hex из спецификации (таблица-эталон в тесте); уникальность `Id`;
    `NameKey == "ColorPreset_"+Id`;
  - `FindExact`: точное совпадение; регистр (`#3f7fd9`); короткая форма текста (`#FFF`); `#FF3F7FD9` → совпадение;
    `#803F7FD9` → null; совпадает акцент, но не текст → null; перестановка пары → null; null/мусор → null;
  - `CustomDialogSeed` — 16 различных значений, первые 10 = акценты по порядку.
- **T-5 `Theming/PanelColorPresetsContrastTests.cs`** (характеризация по конвенции `AmbientAccentContrastAuditTests`) —
  - swatch-контраст (TextHex на сплошном AccentHex) ≥ 3.0:1 для всех 10 (WCAG 1.4.11 / large text);
  - Ambient-поверхность при 0.8 на `DarkWallpaper`: 5 тёмных пресетов проходят `ContrastCalculator.MinimumTextContrast`
    (если какой-то не проходит — закрепить как KnownGap с цифрой);
  - 5 светлых пресетов — **`KnownGap_…_FailsTextContrast`** с зафиксированными значениями (Q-1). Если Q-1 решится
    вариантом B, эти тесты переписываются вместе с ним.
- **T-6 `Localization/ColorPresetLocalizationTests.cs`** — для каждого `NameKey` и каждого нового ключа из §6.1:
  `GetString` в en и ru ≠ ключ; для названий пресетов ru ≠ en. Удалённые 4 ключа отсутствуют в обоих RESX.
- **T-7 `Services/ColorDialogColorTests.cs`** — `TryParseHex` (`#RGB`, `#RRGGBB`, `#AARRGGBB`, мусор/null);
  `ToColorRef(#3F7FD9) == 0x00D97F3F`; `ToHex` — верхний регистр `#RRGGBB`; `BuildCustomColors` — обрезка до 16 и
  пропуск невалидных. `ColorPickerService` сам не тестируется (он открывает UI) → manual verify.
- **T-8 `ViewModels/PanelColorEditorViewModelTests.cs`** —
  - `Presets` — 10 штук в порядке; `SelectedPreset` и ровно один `IsSelected` при совпадении, ни одного при кастоме;
  - `Refresh` после внешнего изменения рейзит `AccentColor/TextColor/SelectedPreset` и переключает `IsSelected`;
  - `ApplyCommand` вызывает `setBoth` **один раз** с hex пресета, а `setAccent/setText` не вызывает;
  - `PickCustomAccentCommand`: мок вернул `"#123456"` → `setAccent("#123456")`, `setText` не вызван; мок получил
    `initialHex` = текущий акцент и `customColors` = `CustomDialogSeed`; `null` → ни одного сеттера;
  - то же для текста.
- **T-9 `ViewModels/PanelCustomizeColorsTests.cs`** — пресет через `Colors` пишет оба цвета в `Target` и
  **один** `SaveWindowsState()`; retarget на панель с пресетной парой → `SelectedPreset` обновился; после `Dispose`
  изменение Target не рейзит `Colors`.
- **T-10 `ViewModels/GlobalDefaultsColorsTests.cs`** — пресет пишет `DefaultBackgroundColor/DefaultTextColor` и
  **один** `Save()`; legacy `#1E1E1E`/`#DDDDDD` → без выбора; `ApplyFromPanel` с пресетной панелью → выбор обновился;
  `MainWindowViewModel.DefaultColors` — тот же экземпляр, что `GlobalDefaults.DefaultColors`.
- **T-11 `Controls/SteppedSliderTests.cs`** (STA, `StaTestRunner`, тестовый подкласс открывает protected-методы) —
  при `Value=34`: `IncreaseSmall` → 40; `DecreaseSmall` → 32; `IncreaseLarge` → 40; при 128 `Increase` → 128;
  `MinimizeValue/MaximizeValue`. При `Value=2` (grid) `Increase` → 8, при 8 `Decrease` → 2. Фиксированные значения
  `IsSnapToTickEnabled/SmallChange/LargeChange/TickFrequency` после ctor.
- **T-12 `Controls/IntegerInputBoxTests.cs`** (STA) — Enter с «36» → `ValueCommitted(36)`; «abc»/«200»/пусто →
  откат текста, события нет, исключения нет; неизменённый текст при `Value=32.0101` → LostFocus не коммитит;
  paste «12a» отменён; Escape откатывает.
- **T-13 `Controls/SteppedValueEditorTests.cs`** (STA) —
  - привязка к INPC-источнику со счётчиком записей при значениях 34, 32.0101 и 200 (вне диапазона) → **0 записей**
    после создания, `Loaded` и layout;
  - слайдер `IncreaseSmall` из 34 → источник получил 40 **ровно один раз**;
  - ввод «36» + `CommitPendingEdit()` → 36;
  - UIA `RangeValue.SetValue(37.4)` → 37;
  - повторный коммит того же значения → без записи.
- **T-14 `Views/SizeEditorsWiringTests.cs`** (STA) — инстанцировать `LayoutSnappingSection` и
  `NewPanelDefaultsSection` (как `SettingsSectionsInitializeTests`). Через `LogicalTreeHelper` найти
  `SteppedValueEditor` и проверить `Minimum/Maximum/Step` = `SizeStepPolicy`. Проверить, что `PanelColorEditor`
  присутствует в NewPanelDefaults.
- **T-15 `Localization/XamlLocKeysExistTests.cs`** (новый gate, рекомендован) — каждый `{loc:Loc X}` и `FormatKey=X`
  в `src/DesktopOrganizer.UI/**/*.xaml` существует в `Strings.resx`. Это ловит опечатки в новых ключах: сейчас
  отсутствующий ключ молча рендерится как сам ключ.
- **T-16 `Services/IconPixelSizeLargeIconTests.cs`** (если нет аналога) — `FromDip(128, 1.0)=128`, `(128, 1.25)=192`,
  `(128, 1.5)=192`, `(128, 2.0)=256`, `(128, 2.5)=256`.

**Дополнения в существующие файлы** (после WP-B, без параллельных правок):
- `Unit/Views/PanelCustomizeWindowClosingTests.cs`: у `IconSizeEditor` правильные диапазоны; закрытие окна
  с «грязными» hex-боксом и integer-боксом → значения попали в Target.

**Существующие гейты, которые обязаны остаться зелёными без правок:** `ResxConsistencyTests` (паритет ключей,
naming, `_Format`-плейсхолдеры), `XamlLiteralGuardTests` (G-L1), `SettingsSectionsInitializeTests`,
`GlobalDefaultsViewModelTests.LegacyGridSizeTwoOrThree_IsShownUnchanged`, `AdaptiveGridLayoutBaselineTests` (там
пинятся литеральные значения, конфигурацию слайдеров эти тесты не читают), `AccentPaletteTests` и аудиты контраста.

---

## 8. Пакеты работ, владение файлами, порядок

```
Stage 1:  WP-A1 (контракты: §3.1–3.5 + RESX)                  ← первым, небольшой
Stage 2:  WP-A2 (ColorPickerService + DI)  ∥  WP-B (UI)  ∥  WP-C1 (T-1…T-4, T-6, T-7 — только новые файлы)
Stage 3:  WP-C2 (T-5, T-8…T-16 + дополнения существующих файлов) — после B
Stage 4:  debug-healer (если красное) → code-quality-reviewer ∥ fences-ux-parity-reviewer → architecture-documenter (ADR-042, ROADMAP)
```
Если пакеты работают в **одном** рабочем дереве, одновременные `dotnet build` блокируют `obj/` (R-7). Рекомендуется
git worktree на пакет (прецедент — settings-window-redesign) или сериализация сборок оркестратором.

### WP-A — `dotnet-services-engineer`
**Владеет файлами:** `UI/Models/SizeStepPolicy.cs`, `UI/Models/SteppedValue.cs`, `UI/Models/IntegerInputParser.cs`,
`UI/Models/GridMetrics.cs` (только комментарий), `UI/Theming/PanelColorPresets.cs`,
`Services.Abstractions/Interfaces/IColorPickerService.cs`, `Services/Tools/ColorPickerService.cs`,
`Services/Tools/ColorDialogColor.cs`, `Services/Configuration/DiConfig.cs`,
`Localization/Strings.resx`, `Localization/Strings.ru.resx`.
**DoD пакета:** сигнатуры §3 ровно такие, как в плане; RESX §6.1 (добавить и удалить) в обоих файлах;
`dotnet build` — 0/0.
**Замороженный контур затронут? НЕТ.** Добавляются чистые хелперы, константы пресетов, сервис диалога и строки
RESX. Ни `IAppSettings`/`Settings.settings`, ни State/DTO/Migrations/Store не трогаются, в persistence ничего не пишется.

### WP-B — `wpf-ui-builder`
**Владеет файлами:** `UI/Controls/SteppedSlider.cs`, `UI/Controls/IntegerInputBox.cs`,
`UI/Controls/SteppedValueEditor.xaml(.cs)`, `UI/Controls/PanelColorEditor.xaml(.cs)`,
`UI/ViewModels/PanelColorEditorViewModel.cs`, `UI/ViewModels/ColorPresetOption.cs`,
`UI/ViewModels/SelectedPanelSettingsViewModel.cs`, `UI/ViewModels/PanelCustomizeViewModel.cs`,
`UI/ViewModels/GlobalDefaultsViewModel.cs`, `UI/ViewModels/MainWindowViewModel.cs`,
`UI/ViewModels/ChildWindowViewModel.cs` (только doc), `UI/Views/PanelCustomizeWindow.xaml(.cs)`,
`UI/Views/Settings/NewPanelDefaultsSection.xaml`, `UI/Views/Settings/LayoutSnappingSection.xaml`,
`UI/Configuration/DiConfig.cs`, а также **только ctor-места** в 5 тестовых файлах из §6.4.
**Зависит от:** WP-A1 (контракты). До их появления код пишется по §3, собирается после.
**DoD пакета:** §5.4 выполнен полностью; ни в одном из трёх мест не осталось `Maximum="96"`/`TickFrequency="4"`/
`IsSnapToTickEnabled`; `SettingsSectionsInitializeTests` и `PanelCustomizeWindowClosingTests` зелёные; build/test — 0/0.
**Замороженный контур затронут? НЕТ, с одной оговоркой для оркестратора.** Запись идёт только через существующие
сеттеры (`ChildWindowModel.AccentColor/TextColor/IconSize` → существующий `SaveWindowsState`; `IAppSettings.Default*`/
`GridSize` → существующий `Save`). Ключи, типы, формат и дефолты не меняются. **Оговорка:** `ApplyColors`/
`SetDefaultColors` объединяют две записи пресета в **один** save. По образцу `ApplyFromPanel` сохраняемое
содержимое идентично, меняется только число вызовов save. Если оркестратор считает это изменением «когда пишется»,
есть нулевой по семантике вариант: `setBoth = (a, t) => { setAccent(a); setText(t); }` (два save). Тест T-9/T-10
тогда проверяет «оба цвета записаны» вместо «один save».

### WP-C — `tdd-test-engineer`
**Владеет файлами:** все новые файлы из §7. После завершения WP-B — ещё дополнения в
`PanelCustomizeWindowClosingTests.cs`.
**DoD пакета:** T-1…T-16 зелёные, существующие гейты зелёные, `dotnet test` — 0 errors / 0 warnings.
**Замороженный контур затронут? НЕТ** — только тесты. T-2 читает метаданные дефолтов, не user.config.

---

## 9. Риски и открытые вопросы

**Q-1 — CLARIFICATION_NEEDED (неблокирующий: реализация идёт, закрывается до manual-verify sign-off).**
Светлые пресеты Sky/Lavender/Peach/Mint/Rose задают тёмный текст. Поверхность панели при этом остаётся тёмной
(`#1E1E1E` + 8 % акцента при Ambient, 0 % при Solid, см. §0.5), поэтому заголовок и подписи этих 5 пресетов будут
практически нечитаемы (≈1:1).
- **A (по умолчанию, как решено):** выпустить пресеты как заданы. Разрыв закрепляется KnownGap-тестами T-5, и
  пользователь оценивает результат на Windows.
- **B:** отдельная фича — для светлых акцентов поверхность становится «пастельной» (изменение `SurfaceComposer`,
  поправка ADR-025/ADR-030). Нужны своё планирование, аудит контраста и ревью; вне этого плана.
- **C (противоречит окончательному решению, приводится для полноты):** поменять цвет текста светлых пресетов на
  светлый.

| # | Риск | Нативный/непроверяемый вне Windows | Митигация |
|---|---|---|---|
| R-1 | Q-1 | да (визуально) | T-5 + manual verify |
| R-2 | Попытка включить `UseWindowsForms` в UI → неоднозначности + WFAC010 | нет | D-C1: WinForms только в Services |
| R-3 | Классический вид `ChooseColor` без comctl v6 | **да** | manual verify; при неприемлемом виде — отдельное решение (activation context или fallback §4.3) |
| R-4 | Владелец диалога = активное окно; если оно не активно — диалог без владельца | **да** | manual verify; при необходимости — перегрузка с owner-HWND (через View-адаптер), без изменения VM-контракта |
| R-5 | `PART_Track` отсутствует в теме → drag без шагов | нет | null-guard → `base`; T-11 |
| R-6 | Icon size 97–128: fallback-путь `IconHandler` берёт jumbo (256) → у типов без 256-кадра маленькая иконка в пустом поле; при DPI > 200 % бакет 256 немного апскейлится; растут минимальные ширины панелей (`GridMetrics.CellSize(128)=204`) | **да** | manual verify на 100/150/200 %; поведение ADR-029 при смене IconSize уже покрыто |
| R-7 | Параллельные `dotnet build` в одном дереве | нет | worktree или сериализация |
| R-8 | Незакоммиченный ручной ввод при закрытии окна | частично | Customize: flush в `OnClosing`. Settings-окно: коммит по LostFocus; flush при закрытии окна настроек вне scope (у hex-боксов там сейчас такое же поведение) |
| R-9 | Путаница `AccentPalette.Presets` (6, legacy, только тесты) и `PanelColorPresets` (10) | нет | зафиксировать в ADR-042; не удалять |
| R-10 | Модальная петля ColorDialog и LL-хук мыши/десктоп-жесты | **да** | петля `ChooseColor` прокачивает сообщения (хук живёт); manual verify: после закрытия диалога жесты и лассо работают |
| R-11 | Стрелки в группе пресетов: только фокус, применение по Space/Enter/клику | да (UX) | manual verify с клавиатурой и Narrator |

### Резолюция Q-1 и заметки по реализации (2026-09-28)

**Q-1 решён вариантом B (адаптивная поверхность), реализованным в рамках этой же задачи**, а не отложен на
«ship as-is» варианта A и не решён вариантом C. Внутри варианта B ревью нашло дополнительную развилку — резолвить
светлую/тёмную нейтральную базу по одному только акценту или по ПАРЕ (accent, text) — и выбрало **вариант A
этой internal-развилки («правило пары»)**: `SurfaceComposer.ResolveNeutralBase(accent, text)` резолвит
светлую базу `#F3F5F8` только когда акцент светлый (WCAG luminance ≥ `LightAccentLuminanceThreshold = 0.5`)
**и** его собственный `text` контрастнее на светлой базе, чем на тёмной. Это удерживает существующие
legacy-панели со светлым пользовательским акцентом и обычным светлым текстом на прежней тёмной базе —
байт-в-байт то же поведение, что и до задачи. Подробности, формулы и полный список контрактных тестов —
[ADR-042](../../adr/ADR-042-color-presets-rgb-picker-stepped-size.md), решение E. KnownGap-тесты T-5 §7 в
итоге закрепляют не «нечитаемость», а зеркальный, уже принятый разрыв `Glass` при минимальной непрозрачности
(симметрично `DO-021`), не исходную проблему Q-1.

**`GroupName` у `RadioButton`-пресетов — подтверждено отсутствие, не регрессия.** §4.2 этого плана изначально
специфицировал `PanelColorEditor`'s `RadioButton` без `GroupName` (эксклюзивность через VM `IsSelected`).
Ревью-раунд (MINOR-8) подтвердил этот выбор как корректный, а не как упущение: WPF-эксклюзивность по
`GroupName` реагирует только на собственный `IsChecked`, реально переключённый пользовательским кликом, и не
имеет своей логики перемещения «отмеченности» стрелками — у контрола и так нет live-apply на стрелках
(только Space/Enter/клик), так что отсутствие `GroupName` не меняет клавиатурную навигацию. Явно
задокументировано в коде (`Controls/PanelColorEditor.xaml`), решение не пересматривалось.

**Найден и закрыт отдельный дефект (H-1): `PanelCustomizeWindow`'s фиксированный `Height="640"` не помещался
на 1366×768 при масштабе 125%** (~582 DIP рабочей области, `ResizeMode="NoResize"`). Исправлено без изменения
самого XAML `Height` — `PanelCustomizeWindow.xaml.cs`'s `ApplyCentered` коэрцирует `MaxHeight` в
`ClampHeightToWorkArea(Height, workArea.Height)`, что заставляет WPF уменьшить фактическую высоту окна
(`MaxHeight` — единственный способ сжать `Window` меньше объявленного `Height`); существующий `ScrollViewer`
поглощает контент, который перестал помещаться. Центрирование по вертикали считается от фактической
(возможно, урезанной) высоты, а не от номинальных 640. Не входило в исходный §5.4 плана — найдено и закрыто
тем же ревью-раундом, что и Q-1/`GroupName`.

---

## 10. Definition of Done (фича целиком)

1. В Customize и в Settings → New panel defaults — один и тот же `PanelColorEditor` с 10 пресетами в заданном
   порядке и с названиями en/ru; горячее переключение языка обновляет названия.
2. Клик, Space или Enter на пресете ставит **оба** цвета ровно в hex спецификации (`#RRGGBB`); T-8…T-10.
3. Пресет подсвечен тогда и только тогда, когда нормализованная пара (accent, text) совпадает точно; иначе не
   подсвечен ни один (T-4, T-8).
4. «Custom…» для каждого цвета открывает `ColorDialog` (FullOpen) со стартовым текущим цветом и 16 seed-цветами.
   OK пишет `#RRGGBB`, Cancel не пишет ничего (T-8, manual).
5. Hex TextBox сохранён; flush при закрытии Customize работает (дополнение в T-closing).
6. Все три размера используют `SteppedValueEditor` с `Step=8`, icon 16–128, grid 2–128; в XAML трёх мест нет
   литералов 96/Tick 4 (T-14, ревью).
7. Ручной ввод: любое целое в диапазоне принимается по Enter/LostFocus; пусто, нецифры, вне диапазона → откат без
   исключений (T-3, T-12).
8. Семантика слайдера: от ручного 34 вверх → 40, вниз → 32 (клавиатура, PageUp/Down, клик по треку, drag, колесо при
   фокусе); Home/End → min/max; thumb отражает 34 (T-1, T-11, T-13).
9. Открытие окон со значениями 34, 32.0101 или вне диапазона не пишет ничего (T-13).
10. `git diff` не затрагивает `Properties/Settings.settings`, `Settings.Designer.cs`, `IAppSettings.cs`,
    `AppSettings.cs`, `UI/State/**`, `Services/Storage/**` (в т. ч. `Migrations/`), `PanelStateMapper`, DTO.
11. `ResxConsistencyTests`, `XamlLiteralGuardTests`, `SettingsSectionsInitializeTests` и T-15 зелёные.
12. `dotnet build` и `dotnet test` по solution — **0 errors, 0 warnings** (без `#pragma`/`NoWarn`/`SuppressMessage`).
13. ADR-042 написан и отмечен в ROADMAP (сквозные задачи); Q-1 закрыт ответом пользователя.
14. Manual verify (§11) пройден на Win10 и Win11.

## 11. Manual verify (Win10 / Win11)

- [ ] ColorDialog из Customize и из Settings модален к своему окну, открывается поверх него и возвращает фокус; Esc
      отменяет; при DPI 100/150/200 % и переносе окна на монитор с другим DPI масштабируется корректно (PMv2).
- [ ] Вид диалога (классические кнопки без comctl v6) приемлем → иначе R-3.
- [ ] Пока открыт диалог, панели на рабочем столе не «замирают»; после закрытия работают LL-хук, двойной клик по
      десктопу и лассо (R-10).
- [ ] 10 свотчей: названия en/ru, подсветка выбранного, High Contrast (кольцо и галочка видны), Narrator читает
      название и состояние «выбран».
- [ ] Светлые пресеты на светлых и тёмных обоях — читаемость заголовка и подписей (Q-1).
- [ ] Icon size: ввод 34, затем стрелки, PageUp/Down, клик по треку, drag и колесо (только в фокусе) → 40/32…;
      Home/End; ввод «200», «abc», пусто → откат.
- [ ] Icon size 128 в Grid и List: чёткость на 100/150/200 %, типы без 256-кадра (R-6), автоширина панели.
- [ ] Grid step: 2 → 8 → … → 128; при смене все LegacyFreeform-панели перестраиваются на новую сетку один раз после
      паузы (Delay 300); тумблер «выкл» гасит слайдер и бокс.
- [ ] Закрытие Customize крестиком с недокоммиченным hex или размером — значение применилось.
