using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Produces the hover-wash <see cref="SolidColorBrush"/> for a panel's header icon buttons
/// (Sort/View/RollUp/Overflow) — MAJOR-3, docs/PLANS/workspace-grid-07-visual.md fix-cycle 1. Feeds the
/// same <c>Model.AccentColor</c>/<c>Model.SurfaceStyle</c> values <c>PanelSurfaceBrushConverter</c>
/// already uses through <see cref="SurfaceComposer.ComposeSurface"/> to get the panel's own resting
/// surface colour, then picks a lightening or darkening wash via <see cref="HoverOverlayPolicy.For"/> so
/// hovering always increases (never decreases) icon contrast AGAINST THAT SAME OWN-TINT LUMINANCE — see
/// <see cref="HoverOverlayPolicy.For"/>'s own remarks for why this is a resting-surface-colour heuristic,
/// not a guarantee computed against the real (unknown-at-runtime) desktop wallpaper behind a translucent
/// panel. <c>SurfaceOpacity</c> is deliberately NOT one of this converter's inputs —
/// <see cref="SurfaceComposer.ComposeSurface"/> only ever varies the composed colour's ALPHA channel by
/// opacity, never its RGB, and <see cref="ContrastCalculator.RelativeLuminance"/> (which
/// <see cref="HoverOverlayPolicy.For"/> is driven by) ignores alpha entirely — so opacity cannot change
/// this converter's output and including it as a binding would be dead weight.
/// </summary>
/// <remarks>
/// <para>
/// High Contrast passthrough: when <c>highContrast</c> (values[2]) is <see langword="true"/>, this
/// converter returns <c>fallbackHoverBrush</c> (values[3]) VERBATIM instead of computing anything —
/// <c>Colors.HighContrast.xaml</c> overrides <c>Color.Overlay.Hover</c> to
/// <see cref="System.Windows.SystemColors.ControlLightColor"/> for accessibility reasons that have
/// nothing to do with this converter's own light/dark-surface heuristic, and must not be bypassed by it.
/// The XAML binding supplies that fallback as <c>Binding Source="{StaticResource Brush.Overlay.Hover}"</c>
/// — resolved once by WPF, at bind time, against whichever dictionary (normal or High Contrast) is
/// currently merged, rather than this converter reaching into
/// <see cref="System.Windows.Application.Current"/>'s resources itself (none of this project's
/// converters do that — they only ever see what a binding hands them).
/// </para>
/// <para>
/// Same defensive contract as the other Ambient Accent converters (never throws, frozen brushes, bounded
/// cache): missing/wrongly-typed values fall back to <see cref="AccentPalette.Neutral"/>/
/// <see cref="PanelSurfaceStyle.Ambient"/> the same way <see cref="PanelSurfaceBrushConverter"/> does,
/// rather than throwing or returning <see cref="Binding.DoNothing"/> (a hover Setter has no sensible
/// "leave it as it was" state to fall back to the way a not-yet-measured Clip binding does).
/// </para>
/// </remarks>
public sealed class HoverOverlayBrushConverter : IMultiValueConverter
{
    /// <summary>
    /// Defensive-only bound, unlike <see cref="PanelSurfaceBrushConverter.MaxCachedBrushes"/>'s namesake
    /// (that converter's key space genuinely grows with a live opacity slider's many distinct alpha
    /// values). This converter's own cache key is <c>(AccentHex, PanelSurfaceStyle)</c> —
    /// <see cref="AccentPalette"/>'s fixed preset count times <see cref="PanelSurfaceStyle"/>'s 3 values,
    /// 21 total combinations today — so <c>Cache.Count &gt;= MaxCachedBrushes</c> below is, in practice,
    /// dead code at this key space's current size; kept anyway as a structural safeguard against a future
    /// change widening the key (e.g. adding a per-panel dimension) without anyone remembering to revisit
    /// this bound.
    /// </summary>
    internal const int MaxCachedBrushes = 512;

    private static readonly Color NeutralSurfaceColor =
        (Color)ColorConverter.ConvertFromString(PanelSurfaceBrushConverter.NeutralSurfaceHex)!;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string AccentHex, PanelSurfaceStyle Style), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 4)
            return Brushes.Transparent;

        var highContrast = values[2] is true;
        if (highContrast)
            return values[3] as Brush ?? Brushes.Transparent;

        var accentHex = AccentPalette.Normalize(values[0] as string);
        var style = values[1] is PanelSurfaceStyle s ? s : PanelSurfaceStyle.Ambient;
        var key = (accentHex, style);

        if (TryGetCached(key, out var cachedBrush))
            return cachedBrush;

        if (!HexBrushConverter.TryParseColor(accentHex, out var accentColor))
            accentColor = Colors.Transparent; // defensive: AccentPalette.Normalize should already guarantee a valid hex here

        // surfaceOpacity is irrelevant here (see this class's own remarks) — pass the max so
        // ComposeSurface's alpha branch never gets a chance to matter; only the RGB channels are read.
        var composed = SurfaceComposer.ComposeSurface(NeutralSurfaceColor, accentColor, style, SurfaceComposer.MaxSurfaceOpacity);
        var luminance = ContrastCalculator.RelativeLuminance(composed);
        var wash = HoverOverlayPolicy.For(luminance);

        return CreateAndCache(key, wash);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryGetCached((string AccentHex, PanelSurfaceStyle Style) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string AccentHex, PanelSurfaceStyle Style) key, Color color)
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
