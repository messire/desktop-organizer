using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Produces a luminance-adaptive <see cref="SolidColorBrush"/> for fixed (non-accent, non-<c>Model.TextColor</c>)
/// panel chrome — header icon buttons (Sort/View/RollUp/Overflow), the overlay scrollbar thumb,
/// <c>PanelChrome</c>'s own hairline strokes, and <c>PanelItemContainerStyle</c>'s own <c>Foreground</c>
/// fallback (docs/PLANS/color-presets-and-size-step.md WP-B "ADDITIONAL"). <c>values[0]</c>/<c>values[1]</c>
/// are the panel's own accent/text brushes — every consumer binds
/// <c>{Binding Path=(theming:PanelTheme.Accent), RelativeSource={RelativeSource Self}}</c>/
/// <c>{Binding Path=(theming:PanelTheme.Text), RelativeSource={RelativeSource Self}}</c>, the same
/// inherited attached properties <c>Panels.xaml</c>'s own <c>SelectionFill</c> and <c>Controls.xaml</c>'s
/// <c>ResizeGripVisualStyle</c> already read. <c>parameter</c> selects which
/// <see cref="PanelChromeForegroundKind"/> to resolve, parsed case-insensitively from its string form;
/// unrecognized/missing defaults to <see cref="PanelChromeForegroundKind.Primary"/>.
/// </summary>
/// <remarks>
/// ADR-042: <c>values[1]</c> (<c>text</c>) is what decides this converter's light/dark token set, via
/// <see cref="PanelChromeForegroundPolicy.Resolve"/>/<see cref="SurfaceComposer.ResolveNeutralBase"/>
/// (that text colour's own <see cref="Theming.PanelThemeMode"/>) — not the accent, which
/// <see cref="PanelChromeForegroundPolicy.Resolve"/> does not read. A legacy panel with a custom light
/// accent paired with light default text (e.g. <c>#FFD700</c>/<c>#DDDDDD</c>) stays on the dark token set,
/// since that text contrasts better against the dark base than the light one (<see cref="Theming.PanelThemeRules.FromTextColor(System.Windows.Media.Color)"/>).
/// <para>
/// High Contrast passthrough — same shape as <see cref="HoverOverlayBrushConverter"/> (see that class's
/// own remarks): when <c>values[2]</c> (<c>highContrast</c>) is <see langword="true"/>, this converter
/// returns <c>values[3]</c> (<c>fallbackBrush</c>) VERBATIM instead of computing anything, so
/// <c>Colors.HighContrast.xaml</c>'s own <c>Color.Text.*</c> overrides (resolved once, at bind time, into
/// whichever <c>Brush.Text.*</c>/<c>Brush.ScrollBar.*</c>/<c>Brush.Border.Default</c> <c>StaticResource</c>
/// the XAML binding supplies as that 4th value) keep controlling this chrome under Windows High Contrast,
/// completely unaffected by this converter's own light/dark heuristic. Same defensive contract as
/// <see cref="AccentBrushConverter"/>/<see cref="HoverOverlayBrushConverter"/> otherwise: never throws,
/// frozen brushes, bounded cache.
/// </para>
/// </remarks>
public sealed class PanelChromeForegroundBrushConverter : IMultiValueConverter
{
    /// <summary>Bounded for the same reason as <see cref="HoverOverlayBrushConverter.MaxCachedBrushes"/> — a small, effectively-fixed key space (accent x text x 8 kinds).</summary>
    internal const int MaxCachedBrushes = 512;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string AccentHex, string TextHex, PanelChromeForegroundKind Kind), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 4)
            return Brushes.Transparent;

        var highContrast = values[2] is true;
        if (highContrast)
            return values[3] as Brush ?? Brushes.Transparent;

        var kind = ParseKind(parameter);

        // Defensive default: PanelTheme.Accent/Text are always frozen SolidColorBrushes in practice (see
        // PanelTheme.CreateDefaultAccentBrush/CreateDefaultTextBrush), but a converter must never throw or
        // produce a visibly-wrong result for a stray non-SolidColorBrush value — fall back to the dark
        // (today's shipped, byte-identical) token set, same fallback direction HoverOverlayBrushConverter
        // uses for a missing/wrong-typed accent/text.
        var accent = values[0] is SolidColorBrush solidAccent
            ? solidAccent.Color
            : (Color)ColorConverter.ConvertFromString(AccentPalette.Neutral)!;
        var text = values[1] is SolidColorBrush solidText
            ? solidText.Color
            : PanelSurfaceBrushConverter.DefaultTextColor;

        var key = (AccentPalette.Normalize(HexFrom(accent)), HexFrom(text), kind);
        if (TryGetCached(key, out var cachedBrush))
            return cachedBrush;

        var color = PanelChromeForegroundPolicy.Resolve(accent, text, kind);
        return CreateAndCache(key, color);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static PanelChromeForegroundKind ParseKind(object? parameter) =>
        parameter is string text && Enum.TryParse<PanelChromeForegroundKind>(text, ignoreCase: true, out var kind)
            ? kind
            : PanelChromeForegroundKind.Primary;

    private static string HexFrom(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryGetCached((string AccentHex, string TextHex, PanelChromeForegroundKind Kind) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string AccentHex, string TextHex, PanelChromeForegroundKind Kind) key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        lock (CacheLock)
        {
            if (Cache.Count >= MaxCachedBrushes)
                Cache.Clear();

            Cache[key] = brush;
        }

        return brush;
    }
}
