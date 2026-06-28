using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Combines a hex colour string and an opacity (0.0–1.0) into a <see cref="SolidColorBrush"/>
/// whose alpha channel reflects the opacity. Applying alpha to the brush (instead of the element's
/// Opacity) keeps the panel background translucent without fading its icons or text.
/// </summary>
public class ColorOpacityBrushConverter : IMultiValueConverter
{
    /// <summary>
    /// Maximum number of distinct (hex, alpha) keys cached at once. Bounded for the same reason as
    /// <see cref="HexBrushConverter.MaxCachedBrushes"/>: the hex string is typically bound to a
    /// live-editing settings <see cref="System.Windows.Controls.TextBox"/>, and the alpha channel
    /// can vary continuously via an opacity slider. Sized at 512 (rather than a tighter bound) so
    /// that a full opacity-slider sweep — up to 256 distinct alpha values for a single colour —
    /// does not by itself evict entries for other colours the user was just editing.
    /// </summary>
    internal const int MaxCachedBrushes = 512;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string Hex, byte Alpha), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string hex)
            return Brushes.Transparent;

        var opacity = values[1] is double d ? d : 1.0;
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        var alpha = (byte)Math.Round(opacity * 255);

        // Cache-first, raw key: avoids the NormalizeKey allocation entirely on a cache hit.
        if (TryGetCached((hex, alpha), out var cachedByRaw))
            return cachedByRaw;

        var key = (HexBrushConverter.NormalizeKey(hex), alpha);
        if (TryGetCached(key, out var cachedByKey))
            return cachedByKey;

        // Only reached on a full cache miss: this is the sole place that pays for the expensive
        // hex parse (shared, defensive implementation in HexBrushConverter).
        if (!HexBrushConverter.TryParseColor(hex, out var color))
            return Brushes.Transparent;

        color.A = alpha;
        return CreateAndCache(key, color);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryGetCached((string Hex, byte Alpha) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string Hex, byte Alpha) key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        lock (CacheLock)
        {
            // Simple bounded strategy: once the cache is full, drop everything rather than
            // tracking recency. At MaxCachedBrushes entries this is cheap and avoids unbounded
            // growth from rapid hex/opacity edits in the settings window.
            if (Cache.Count >= MaxCachedBrushes)
                Cache.Clear();

            Cache[key] = brush;
        }

        return brush;
    }
}
