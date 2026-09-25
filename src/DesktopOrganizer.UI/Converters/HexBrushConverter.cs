using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a hex colour string (e.g. <c>#DDDDDD</c>) to a <see cref="SolidColorBrush"/>.
/// </summary>
public class HexBrushConverter : IValueConverter
{
    /// <summary>
    /// Maximum number of distinct hex keys cached at once. Bounded because the hex string is
    /// typically bound to a live-editing <see cref="System.Windows.Controls.TextBox"/> in the
    /// settings window, where every keystroke (including transiently invalid input) produces a
    /// new key — an unbounded cache here would leak just as badly as an unbounded model cache.
    /// Raised from an earlier 64 to 256: a single opacity slider drag alone visits up to 256
    /// distinct alpha values per colour in <see cref="ColorOpacityBrushConverter"/>, and this
    /// converter's own cache should comfortably outlive a typical hex-editing session without
    /// thrashing.
    /// </summary>
    internal const int MaxCachedBrushes = 256;

    /// <summary>
    /// Validates the shape of a hex colour string before it is handed to
    /// <see cref="ColorConverter.ConvertFromString(string)"/>. That API accepts more than plain
    /// <c>#RRGGBB</c> syntax (named colours, <c>ContextColor &lt;path&gt;.icc</c> ICC-profile
    /// references, ...) and can throw exceptions other than <see cref="FormatException"/> — or
    /// return <see langword="null"/> — for such input. Pre-filtering with this pattern keeps this
    /// converter's contract limited to the hex syntax it is actually meant to support.
    /// </summary>
    private static readonly Regex HexPattern = new(@"^#[0-9A-Fa-f]{3,8}$", RegexOptions.Compiled);

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex)
            return Brushes.Transparent;

        // Cache-first: check the cache with the raw (un-normalized) string before doing any
        // parsing or allocation. This is the fast path for the common case where the input is
        // already in its normalized form (e.g. values coming from a colour picker), and it
        // avoids the NormalizeKey allocation entirely on a cache hit.
        if (TryGetCached(hex, out var cachedByRaw))
            return cachedByRaw;

        var key = NormalizeKey(hex);

        // Second lookup with the normalized key covers inputs that differ only by casing/
        // whitespace from something already cached (e.g. "#ddd" after "#DDD" was cached).
        if (TryGetCached(key, out var cachedByKey))
            return cachedByKey;

        // Only reached on a full cache miss: this is the sole place that pays for the expensive
        // ColorConverter.ConvertFromString parse (string parsing + known-colours table lookup).
        if (!TryParseColor(hex, out var color))
            return Brushes.Transparent;

        return CreateAndCache(key, color);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SolidColorBrush brush ? brush.Color.ToString() : Binding.DoNothing;

    /// <summary>
    /// Normalizes a hex colour string into a cache key that is insensitive to surrounding
    /// whitespace and letter casing (e.g. <c>" #ddd "</c> and <c>"#DDD"</c> map to the same key).
    /// </summary>
    /// <param name="hex">The raw hex colour string to normalize.</param>
    internal static string NormalizeKey(string hex) => hex.Trim().ToUpperInvariant();

    /// <summary>
    /// Attempts to parse <paramref name="hex"/> into a <see cref="Color"/>, defensively: invalid,
    /// empty, or pathological input (e.g. an ICC-profile reference) must never throw out of this
    /// method, since it is reachable from a live-editing settings <see cref="System.Windows.Controls.TextBox"/>
    /// binding where an uncaught exception would crash the whole application.
    /// </summary>
    /// <param name="hex">The candidate hex colour string (e.g. <c>#DDDDDD</c>).</param>
    /// <param name="color">The parsed <see cref="Color"/> on success, or <see cref="Colors.Transparent"/> on failure.</param>
    internal static bool TryParseColor(string hex, out Color color)
    {
        var trimmed = hex.Trim();
        if (!HexPattern.IsMatch(trimmed))
        {
            color = Colors.Transparent;
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(trimmed) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (Exception)
        {
            // ColorConverter.ConvertFromString can throw more than FormatException for
            // pathological-but-regex-matching input (e.g. ArgumentException/IOException while
            // probing an ICC profile path). Any exception here must be swallowed defensively.
        }

        color = Colors.Transparent;
        return false;
    }

    private static bool TryGetCached(string key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        lock (CacheLock)
        {
            // Simple bounded strategy: once the cache is full, drop everything rather than
            // tracking recency. At MaxCachedBrushes entries this is cheap and avoids unbounded
            // growth from rapid hex edits in the settings TextBox.
            if (Cache.Count >= MaxCachedBrushes)
                Cache.Clear();

            Cache[key] = brush;
        }

        return brush;
    }
}
