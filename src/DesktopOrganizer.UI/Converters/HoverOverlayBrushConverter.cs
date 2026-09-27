using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Produces the hover-wash <see cref="SolidColorBrush"/> for a panel's header icon buttons
/// (Sort/View/RollUp/Overflow) and icon-tile hover (MAJOR-3, docs/PLANS/workspace-grid-07-visual.md
/// fix-cycle 1; icon-tile hover added by the color-presets-and-size-step.md MAJOR-3 follow-up fix). Feeds
/// <c>(theming:PanelTheme.Accent)</c>/<c>(theming:PanelTheme.SurfaceStyle)</c> — the SAME inherited
/// attached properties <c>PanelChrome</c>'s own <c>AccentBrush</c>/<c>SurfaceStyle</c> already read —
/// through <see cref="SurfaceComposer.ComposeSurface"/> to get the panel's own resting surface colour,
/// then picks a lightening or darkening wash via <see cref="HoverOverlayPolicy.For"/> (header icon buttons,
/// no <c>ConverterParameter</c>) or <see cref="HoverOverlayPolicy.ForItemTile"/> (icon-tile hover,
/// <c>ConverterParameter="ItemTile"</c> — NEW-1 fix, docs/PLANS/release-polish-1.0.md; a dark surface must
/// get the ORIGINAL <see cref="HoverOverlayPolicy.LightenWash"/> there, not <see cref="HoverOverlayPolicy.DarkenWash"/>,
/// which is nearly invisible over an already near-black surface) so hovering always increases (never
/// decreases) icon/tile contrast AGAINST THAT SAME OWN-TINT LUMINANCE — see <see cref="HoverOverlayPolicy.For"/>'s
/// own remarks for why this is a resting-surface-colour heuristic, not a guarantee computed against the real
/// (unknown-at-runtime) desktop wallpaper behind a translucent panel. <c>SurfaceOpacity</c> is deliberately
/// NOT one of this converter's inputs —
/// <see cref="SurfaceComposer.ComposeSurface"/> only ever varies the composed colour's ALPHA channel by
/// opacity, never its RGB, and <see cref="ContrastCalculator.RelativeLuminance"/> (which
/// <see cref="HoverOverlayPolicy.For"/> is driven by) ignores alpha entirely — so opacity cannot change
/// this converter's output and including it as a binding would be dead weight.
/// </summary>
/// <remarks>
/// <para>
/// ADR-042: this converter also takes <c>(theming:PanelTheme.Text)</c> (values[2]) and resolves the
/// neutral base from that text colour's own <see cref="Theming.PanelThemeMode"/> via
/// <see cref="SurfaceComposer.ResolveNeutralBase"/>, exactly like <see cref="PanelSurfaceBrushConverter"/>
/// — the single source of truth for that text colour is <see cref="Theming.PanelTheme"/>'s own
/// <c>AccentProperty</c>/<c>TextProperty</c>, set once on <c>ChildWindow.xaml</c>'s <c>PanelRoot</c> from
/// <c>Model.AccentColor</c>/<c>Model.TextColor</c> and inherited down to every consumer, rather than each
/// consumer re-deriving its own answer independently.
/// </para>
/// <para>
/// High Contrast passthrough: when <c>highContrast</c> (values[3]) is <see langword="true"/>, this
/// converter returns <c>fallbackHoverBrush</c> (values[4]) VERBATIM instead of computing anything —
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
/// <see cref="PanelSurfaceBrushConverter.DefaultTextColor"/>/<see cref="PanelSurfaceStyle.Ambient"/> the
/// same way <see cref="PanelSurfaceBrushConverter"/> does, rather than throwing or returning
/// <see cref="Binding.DoNothing"/> (a hover Setter has no sensible "leave it as it was" state to fall
/// back to the way a not-yet-measured Clip binding does).
/// </para>
/// </remarks>
public sealed class HoverOverlayBrushConverter : IMultiValueConverter
{
    /// <summary>
    /// Defensive-only bound, unlike <see cref="PanelSurfaceBrushConverter.MaxCachedBrushes"/>'s namesake
    /// (that converter's key space genuinely grows with a live opacity slider's many distinct alpha
    /// values). This converter's own cache key is <c>(AccentHex, PanelSurfaceStyle, TextHex, ParameterKey)</c>
    /// (<c>ParameterKey</c> added by the NEW-1 fix, docs/PLANS/release-polish-1.0.md, so the header-button
    /// and icon-tile hover washes for the SAME accent/style/text never collide in the cache) — a small,
    /// effectively-fixed key space — so <c>Cache.Count &gt;= MaxCachedBrushes</c> below is, in practice,
    /// dead code at this key space's current size; kept anyway as a structural safeguard against a future
    /// change widening the key (e.g. adding a per-panel dimension) without anyone remembering to revisit
    /// this bound.
    /// </summary>
    internal const int MaxCachedBrushes = 512;

    /// <summary>
    /// The <c>ConverterParameter</c> value <c>PanelItemContainerStyle</c>'s own icon-tile hover MultiBinding
    /// supplies (Panels.xaml, NEW-1 fix, docs/PLANS/release-polish-1.0.md) to route through
    /// <see cref="HoverOverlayPolicy.ForItemTile"/> instead of <see cref="HoverOverlayPolicy.For"/> — the
    /// header icon buttons (Controls.xaml) pass no parameter at all and keep <see cref="HoverOverlayPolicy.For"/>.
    /// </summary>
    internal const string ItemTileParameter = "ItemTile";

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string AccentHex, PanelSurfaceStyle Style, string TextHex, string ParameterKey), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 5)
            return Brushes.Transparent;

        var highContrast = values[3] is true;
        if (highContrast)
            return values[4] as Brush ?? Brushes.Transparent;

        var accentColor = values[0] is SolidColorBrush accentBrush
            ? accentBrush.Color
            : (Color)ColorConverter.ConvertFromString(AccentPalette.Neutral)!;
        var style = values[1] is PanelSurfaceStyle s ? s : PanelSurfaceStyle.Ambient;
        var textColor = values[2] is SolidColorBrush textBrush ? textBrush.Color : PanelSurfaceBrushConverter.DefaultTextColor;

        var isItemTile = parameter is string parameterText && parameterText == ItemTileParameter;
        var parameterKey = isItemTile ? ItemTileParameter : string.Empty;

        var accentHex = HexFrom(accentColor);
        var key = (accentHex, style, HexFrom(textColor), parameterKey);

        if (TryGetCached(key, out var cachedBrush))
            return cachedBrush;

        // surfaceOpacity is irrelevant here (see this class's own remarks) — pass the max so
        // ComposeSurface's alpha branch never gets a chance to matter; only the RGB channels are read.
        // ADR-042: the neutral base now depends on the text colour's own PanelThemeMode via
        // SurfaceComposer.ResolveNeutralBase, same as PanelSurfaceBrushConverter — this converter's own
        // HighContrast passthrough above means this path never runs in HC mode, so there is no
        // non-adaptive base to preserve here (unlike PanelSurfaceBrushConverter's highContrast branch).
        var neutralBase = SurfaceComposer.ResolveNeutralBase(accentColor, textColor);
        var composed = SurfaceComposer.ComposeSurface(neutralBase, accentColor, style, SurfaceComposer.MaxSurfaceOpacity);
        var luminance = ContrastCalculator.RelativeLuminance(composed);

        // NEW-1 fix (docs/PLANS/release-polish-1.0.md): PanelItemContainerStyle's icon-tile hover asks for
        // HoverOverlayPolicy.ForItemTile (LightenWash on a dark surface) instead of HoverOverlayPolicy.For
        // (DarkenWash on a dark surface, nearly invisible over every dark panel's #1E1E1E-based surface) —
        // see HoverOverlayPolicy.ForItemTile's own remarks.
        var wash = isItemTile ? HoverOverlayPolicy.ForItemTile(luminance) : HoverOverlayPolicy.For(luminance);

        return CreateAndCache(key, wash);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string HexFrom(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryGetCached((string AccentHex, PanelSurfaceStyle Style, string TextHex, string ParameterKey) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string AccentHex, PanelSurfaceStyle Style, string TextHex, string ParameterKey) key, Color color)
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
