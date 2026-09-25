# Prompt: wpf-ui-builder — implement Reference Panel Chrome

Реализуй утверждённый план по ADR-030 в WPF.

Работай преимущественно в:

- `src/DesktopOrganizer.UI/Resources/Styles/Panels.xaml`;
- `src/DesktopOrganizer.UI/Resources/Styles/Colors.xaml` / HighContrast counterpart, если действительно
  нужны новые design tokens;
- `src/DesktopOrganizer.UI/Resources/Styles/Brushes.xaml`;
- `src/DesktopOrganizer.UI/Resources/Styles/Spacing.xaml` или `PanelMetrics.cs` для geometry metrics;
- `src/DesktopOrganizer.UI/Controls/PanelChrome.cs`, только если template-facing DP действительно нужен;
- существующих converters/theming classes, если без них нельзя чисто связать state и opacity.

Не трогай persistence contract.

## Реализация

1. Добавь layered chrome:
   - subtle glow layer;
   - accent stroke;
   - neutral stroke;
   - existing surface;
   - subtle highlight;
   - curved accent marker;
   - existing interaction overlay.

2. Accent marker:
   - `Path`/vector geometry;
   - вертикальный штрих слева;
   - сверху мягкий изгиб вправо;
   - rounded cap/join;
   - цвет только из `AccentBrush`;
   - emphasis через текущие state values;
   - reserved column остаётся фиксированным, content не должен двигаться.

3. Glow:
   - умеренный `DropShadowEffect` только на одном chrome/accent layer;
   - `ShadowDepth=0`;
   - low opacity;
   - no per-item effects;
   - обеспечить inset/bounds, чтобы glow не обрезался;
   - если для этого приходится менять размер root layout, не меняй persisted Window size semantics и
     content-grid geometry незаметно. Предпочитай внутренний transparent padding/inset.

4. Surface highlight:
   - очень слабый `LinearGradientBrush`/overlay;
   - `IsHitTestVisible=False`;
   - не затрагивает layout;
   - в HC отключается/заменяется безопасным системным вариантом.

5. Border:
   - neutral stroke всегда читаем;
   - accent stroke слабее marker;
   - цвета/opacity/thickness через tokens, никаких category hex literals.

6. States:
   - Idle/Hover/Editing/Dragging/Resizing/Edit Layout не переписывать;
   - разрешено менять только decorative emphasis;
   - исключить state-dependent desired-size changes.

7. High Contrast:
   - decorative glow и highlight не обязательны;
   - основная граница должна оставаться различимой;
   - не вводить contrast regression.

## Нельзя

- менять `application-state.json` schema/migrations/DTO;
- менять `Settings.settings`, `IAppSettings`, defaults;
- добавлять DWM/Acrylic;
- делать bitmap reference background;
- добавлять дополнительные windows для shadow;
- применять `DropShadowEffect` к каждому icon/item;
- добавлять hard-coded accent colors;
- ломать `RoundedRectClip`, ResizeOverlay, native hit testing.

После реализации: `dotnet build`, затем передай задачу `tdd-test-engineer`.
