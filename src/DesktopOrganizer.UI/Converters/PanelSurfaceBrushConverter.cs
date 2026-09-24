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
    /// Neutral panel surface base colour. Duplicated from <c>Colors.xaml</c>'s <c>Color.Surface.Base</c>
    /// token: this converter is plain C# with no access to a live <see cref="System.Windows.ResourceDictionary"/>
    /// lookup, so the literal is kept in exact sync with that token by comment, the same way
    /// <see cref="AccentPalette"/>'s constants are kept in sync with <c>Color.Accent.*</c>.
    /// </summary>
    internal const string NeutralSurfaceHex = "#1E1E1E";

    /// <summary>Bounded for the same reason as <see cref="ColorOpacityBrushConverter.MaxCachedBrushes"/> — a live opacity slider visits many distinct alpha values.</summary>
    internal const int MaxCachedBrushes = 512;

    private static readonly Color NeutralSurfaceColor = (Color)ColorConverter.ConvertFromString(NeutralSurfaceHex)!;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string AccentHex, byte Alpha, PanelSurfaceStyle Style, bool HighContrast), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 3)
            return Brushes.Transparent;

        var accentHex = AccentPalette.Normalize(values[0] as string);
        var surfaceOpacity = values[1] is double opacity ? opacity : SurfaceComposer.MinSurfaceOpacity;
        var style = values[2] is PanelSurfaceStyle s ? s : PanelSurfaceStyle.Ambient;
        var highContrast = values.Length > 3 && values[3] is bool hc && hc;

        var clampedOpacity = SurfaceComposer.ClampOpacity(surfaceOpacity);
        var alphaKey = highContrast ? (byte)255 : SurfaceComposer.AlphaFromOpacity(clampedOpacity);
        var key = (accentHex, alphaKey, style, highContrast);

        if (TryGetCached(key, out var cachedBrush))
            return cachedBrush;

        if (!HexBrushConverter.TryParseColor(accentHex, out var accentColor))
            accentColor = Colors.Transparent; // defensive: AccentPalette.Normalize should already guarantee a valid hex here

        var composed = SurfaceComposer.ComposeSurface(NeutralSurfaceColor, accentColor, style, clampedOpacity, highContrast);

        return CreateAndCache(key, composed);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryGetCached((string AccentHex, byte Alpha, PanelSurfaceStyle Style, bool HighContrast) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string AccentHex, byte Alpha, PanelSurfaceStyle Style, bool HighContrast) key, Color color)
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
