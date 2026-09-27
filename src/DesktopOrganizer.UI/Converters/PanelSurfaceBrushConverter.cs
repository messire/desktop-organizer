using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Combines a panel's accent hex, surface opacity, and <see cref="PanelSurfaceStyle"/> (plus an
/// optional high-contrast flag) into the final <see cref="SolidColorBrush"/> for its surface fill,
/// via the pure <see cref="SurfaceComposer.ComposeSurface"/> (docs/PLANS/phase-5-ambient-accent-ui.md
/// §3.4). Same defensive contract as <see cref="ColorOpacityBrushConverter"/>: never throws, frozen
/// brushes, bounded cache.
/// </summary>
public sealed class PanelSurfaceBrushConverter : IMultiValueConverter
{
    /// <summary>
    /// Neutral panel surface base colour for <see cref="Theming.PanelThemeMode.Dark"/> panels (every panel
    /// this app shipped before ADR-042). Duplicated from <c>Colors.xaml</c>'s <c>Color.Surface.Base</c>
    /// token: this converter is plain C# with no access to a live <see cref="System.Windows.ResourceDictionary"/>
    /// lookup, so the literal is kept in exact sync with that token by comment, the same way
    /// <see cref="AccentPalette"/>'s constants are kept in sync with <c>Color.Accent.*</c>. Byte-identical
    /// to <see cref="SurfaceComposer.DarkNeutralBase"/> — kept as its own named constant (rather than
    /// removed) because <c>highContrast</c> mode (below) and existing tests reference it directly,
    /// unaffected by the <see cref="Theming.PanelThemeMode.Light"/> case <see cref="SurfaceComposer.ResolveNeutralBase"/>
    /// now also handles.
    /// </summary>
    internal const string NeutralSurfaceHex = "#1E1E1E";

    /// <summary>Bounded for the same reason as <see cref="ColorOpacityBrushConverter.MaxCachedBrushes"/> — a live opacity slider visits many distinct alpha values.</summary>
    internal const int MaxCachedBrushes = 512;

    /// <summary>
    /// Default text colour used when no <c>Model.TextColor</c> value is supplied (fewer than 5 binding
    /// values — every pre-ADR-042 caller/test) or it fails to parse — byte-identical to
    /// <see cref="Theming.PanelTheme"/>'s own default text brush (<c>Color.Text.Primary</c>, <c>#DDDDDD</c>).
    /// A near-white default reads far better on <see cref="Theming.SurfaceComposer.DarkNeutralBase"/> than on
    /// <see cref="Theming.SurfaceComposer.LightNeutralBase"/>, so a caller that omits text keeps resolving
    /// dark — the same byte-identical-for-every-existing-caller guarantee <see cref="AccentPalette.Normalize"/>
    /// already gives a missing/garbage accent.
    /// </summary>
    internal static readonly Color DefaultTextColor = Color.FromRgb(0xDD, 0xDD, 0xDD);

    private static readonly Color NeutralSurfaceColor = (Color)ColorConverter.ConvertFromString(NeutralSurfaceHex)!;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string AccentHex, string TextHex, byte Alpha, PanelSurfaceStyle Style, bool HighContrast), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 3)
            return Brushes.Transparent;

        var accentHex = AccentPalette.Normalize(values[0] as string);
        var surfaceOpacity = values[1] is double opacity ? opacity : SurfaceComposer.MinSurfaceOpacity;
        var style = values[2] is PanelSurfaceStyle s ? s : PanelSurfaceStyle.Ambient;
        var highContrast = values.Length > 3 && values[3] is bool hc && hc;
        var textHex = values.Length > 4 ? values[4] as string : null;

        var clampedOpacity = SurfaceComposer.ClampOpacity(surfaceOpacity);
        var alphaKey = highContrast ? (byte)255 : SurfaceComposer.AlphaFromOpacity(clampedOpacity);
        var key = (accentHex, textHex ?? string.Empty, alphaKey, style, highContrast);

        if (TryGetCached(key, out var cachedBrush))
            return cachedBrush;

        if (!HexBrushConverter.TryParseColor(accentHex, out var accentColor))
            accentColor = Colors.Transparent; // defensive: AccentPalette.Normalize should already guarantee a valid hex here

        if (textHex is null || !HexBrushConverter.TryParseColor(textHex, out var textColor))
            textColor = DefaultTextColor;

        // highContrast keeps the OLD, non-adaptive dark base verbatim (ADR-042 deliberately does not touch
        // High Contrast mode's own opaque-base behaviour) — only the normal-mode path resolves a
        // text-colour-driven neutral base via SurfaceComposer.ResolveNeutralBase (ADR-042).
        var neutralBase = highContrast ? NeutralSurfaceColor : SurfaceComposer.ResolveNeutralBase(accentColor, textColor);
        var composed = SurfaceComposer.ComposeSurface(neutralBase, accentColor, style, clampedOpacity, highContrast);

        return CreateAndCache(key, composed);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryGetCached((string AccentHex, string TextHex, byte Alpha, PanelSurfaceStyle Style, bool HighContrast) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string AccentHex, string TextHex, byte Alpha, PanelSurfaceStyle Style, bool HighContrast) key, Color color)
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
