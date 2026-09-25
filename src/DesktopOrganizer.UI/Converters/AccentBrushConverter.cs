using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts an accent hex string into a <see cref="SolidColorBrush"/> tinted for a specific usage,
/// selected by <c>parameter</c>: <c>"indicator"</c>/<c>"full"</c> (fully opaque), <c>"selection"</c>
/// (subtle fill), or <c>"hover"</c> (subtler still) — see docs/PLANS/phase-5-ambient-accent-ui.md §3.4
/// and §5.5. Same defensive contract as <see cref="HexBrushConverter"/>: never throws, frozen brushes,
/// bounded cache.
/// </summary>
public sealed class AccentBrushConverter : IValueConverter
{
    /// <summary>
    /// Alpha applied to a selected panel item's accent fill. <c>public</c> (CRITICAL-1 fix — NOT merely
    /// <c>internal</c>, despite this type and <c>Resources/Styles/Panels.xaml</c>'s <c>PanelItemContainerStyle</c>
    /// living in the same assembly): WPF on .NET (Core) removed the old .NET Framework
    /// <c>GeneratedInternalTypeHelper</c> shim, so <see cref="System.Windows.Markup.StaticExtension.ProvideValue"/>
    /// resolves an <c>{x:Static conv:AccentBrushConverter.SelectionAlpha}</c> reference via plain public
    /// reflection only — assembly-internal visibility (even with same-assembly access, even with
    /// <c>InternalsVisibleTo</c>) is NOT sufficient, unlike ordinary C# member access. An <c>internal</c>
    /// field here throws <c>XamlParseException</c> → <c>ArgumentException</c> ("StaticExtension value
    /// cannot be resolved to an enumeration, static field, or static property") the moment
    /// <c>ListViewTemplate</c>/<c>GridViewTemplate</c>/<c>SystemIconsPanelWindow.xaml</c> materialize
    /// <c>PanelItemContainerStyle</c> — i.e. on every panel render. Referenced directly via
    /// <c>{x:Static conv:AccentBrushConverter.SelectionAlpha}</c> instead of mirroring the value as a
    /// hand-synced XAML literal (MAJOR-2) — and so <c>DesktopOrganizer.Tests</c> can also pin it in a
    /// regression test.
    /// </summary>
    public const double SelectionAlpha = 0.28;

    /// <summary>
    /// Alpha applied to a hovered (non-selected) panel item's accent fill. <c>public</c> for the same
    /// <c>x:Static</c>-resolution reason as <see cref="SelectionAlpha"/> — kept public for symmetry/
    /// consistency even though nothing currently references it from XAML via <c>x:Static</c>.
    /// </summary>
    public const double HoverAlpha = 0.16;

    /// <summary>Upper bound on the number of cached brushes before <see cref="Cache"/> is cleared and rebuilt from scratch.</summary>
    internal const int MaxCachedBrushes = 256;

    private const double IndicatorAlpha = 1.0;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string Hex, string Mode), SolidColorBrush> Cache = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = AccentPalette.Normalize(value as string);
        var mode = (parameter as string)?.Trim().ToLowerInvariant() ?? "full";

        var key = (hex, mode);
        if (TryGetCached(key, out var cachedBrush))
            return cachedBrush;

        if (!HexBrushConverter.TryParseColor(hex, out var color))
            return Brushes.Transparent;

        var alpha = mode switch
        {
            "selection" => SelectionAlpha,
            "hover" => HoverAlpha,
            _ => IndicatorAlpha, // "indicator", "full", and any unrecognized mode default to fully opaque
        };

        var tinted = SurfaceComposer.AccentTint(color, alpha);

        return CreateAndCache(key, tinted);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static bool TryGetCached((string Hex, string Mode) key, out SolidColorBrush brush)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out brush!);
        }
    }

    private static SolidColorBrush CreateAndCache((string Hex, string Mode) key, Color color)
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
