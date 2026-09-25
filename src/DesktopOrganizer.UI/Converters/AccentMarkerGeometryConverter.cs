using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Builds the curved accent marker's <see cref="PathGeometry"/> from a <see cref="CornerRadius"/>
/// (ADR-030, docs/PLANS/reference-panel-chrome.md §1 D-4) — <c>PART_AccentIndicator</c>'s
/// <see cref="System.Windows.Shapes.Path.Data"/> (<c>Resources/Styles/Panels.xaml</c>), bound against
/// <c>{StaticResource Radius.Panel}</c>. The traced axis repeats the panel surface's own top-left rounded
/// corner: a vertical stem down the left edge, a quarter-circle arc of radius <c>r</c> around the corner,
/// then a short horizontal tail along the top edge — <c>(0, r + AccentMarkerStemLength)</c> →
/// <c>(0, r)</c> → arc → <c>(r, 0)</c> → <c>(r + AccentMarkerTailLength, 0)</c>. The same geometry renders
/// at every visual state; only <see cref="System.Windows.Shapes.Shape.StrokeThickness"/> (via
/// <c>EditAwareAccentIndicatorWidthConverter</c>'s <c>"marker"</c> parameter) changes.
/// </summary>
/// <remarks>
/// Same defensive contract as this codebase's other Ambient Accent converters
/// (<see cref="RoundedRectClipConverter"/>): never throws, degrades to <see cref="Binding.DoNothing"/> on
/// a missing/wrongly-typed/non-finite/negative radius. Frozen output, cached by radius (this codebase's
/// panels all share <c>Radius.Panel</c>, so in practice the cache holds exactly one entry, but a bounded
/// cache — not an unbounded one — matches every other converter cache in this codebase, in case a future
/// caller ever binds a per-instance radius).
/// </remarks>
public sealed class AccentMarkerGeometryConverter : IValueConverter
{
    internal const int MaxCachedGeometries = 32;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<double, PathGeometry> Cache = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CornerRadius radius)
            return Binding.DoNothing;

        var r = radius.TopLeft;
        if (double.IsNaN(r) || double.IsInfinity(r) || r < 0)
            return Binding.DoNothing;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(r, out var cached))
                return cached;

            var geometry = BuildGeometry(r);

            if (Cache.Count >= MaxCachedGeometries)
                Cache.Clear();

            Cache[r] = geometry;
            return geometry;
        }
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static PathGeometry BuildGeometry(double r)
    {
        var stem = PanelMetrics.AccentMarkerStemLength;
        var tail = PanelMetrics.AccentMarkerTailLength;

        var figure = new PathFigure
        {
            StartPoint = new Point(0, r + stem),
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment(new Point(0, r), isStroked: true));
        figure.Segments.Add(new ArcSegment(
            new Point(r, 0),
            new Size(r, r),
            rotationAngle: 0,
            isLargeArc: false,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true));
        figure.Segments.Add(new LineSegment(new Point(r + tail, 0), isStroked: true));

        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }
}
