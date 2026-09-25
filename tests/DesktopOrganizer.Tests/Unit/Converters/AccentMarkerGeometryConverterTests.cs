using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="AccentMarkerGeometryConverter"/> (ADR-030, docs/PLANS/reference-panel-chrome.md
/// §5.1 T-K3) — same "never throws, <see cref="Binding.DoNothing"/> on garbage input, frozen/cached
/// output" contract style as <see cref="RoundedRectClipConverterTests"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class AccentMarkerGeometryConverterTests
{
    private static readonly AccentMarkerGeometryConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that <see cref="AccentMarkerGeometryConverter"/> converts a valid <see cref="CornerRadius"/>
    /// into a frozen <see cref="PathGeometry"/>.
    /// </summary>
    [Fact]
    public void Convert_ValidRadius_ReturnsFrozenPathGeometry()
    {
        var result = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.True(geometry.IsFrozen);
    }

    /// <summary>
    /// Verifies that converting the same <see cref="CornerRadius"/> twice returns the same cached geometry instance.
    /// </summary>
    [Fact]
    public void Convert_SameRadiusTwice_ReturnsSameCachedInstance()
    {
        var first = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);
        var second = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that different radii produce distinct cached geometry instances rather than sharing one entry.
    /// </summary>
    [Fact]
    public void Convert_DifferentRadii_ReturnCachedButDistinctInstances()
    {
        var r8 = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);
        var r6 = Converter.Convert(new CornerRadius(6), typeof(Geometry), null, AnyCulture);

        Assert.NotSame(r8, r6);
    }

    /// <summary>
    /// Verifies that the generated figure starts below the arc's start point by the fixed stem length.
    /// </summary>
    [Fact]
    public void Convert_ValidRadius_StartPointIsBelowArcStartByStemLength()
    {
        const double r = 8d;
        var geometry = (PathGeometry)Converter.Convert(new CornerRadius(r), typeof(Geometry), null, AnyCulture);

        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(new Point(0, r + PanelMetrics.AccentMarkerStemLength), figure.StartPoint);
        Assert.False(figure.IsClosed);
    }

    /// <summary>
    /// Verifies that the generated figure traces a vertical stem segment, an arc to the top of the corner,
    /// then a horizontal tail segment, in that order.
    /// </summary>
    [Fact]
    public void Convert_ValidRadius_TracesVerticalStem_ArcToTopOfCorner_ThenHorizontalTail()
    {
        const double r = 8d;
        var geometry = (PathGeometry)Converter.Convert(new CornerRadius(r), typeof(Geometry), null, AnyCulture);
        var figure = geometry.Figures[0];

        Assert.Equal(3, figure.Segments.Count);

        var stem = Assert.IsType<LineSegment>(figure.Segments[0]);
        Assert.Equal(new Point(0, r), stem.Point);

        var arc = Assert.IsType<ArcSegment>(figure.Segments[1]);
        Assert.Equal(new Point(r, 0), arc.Point);
        Assert.Equal(new Size(r, r), arc.Size);
        Assert.False(arc.IsLargeArc);
        Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);

        var tail = Assert.IsType<LineSegment>(figure.Segments[2]);
        Assert.Equal(new Point(r + PanelMetrics.AccentMarkerTailLength, 0), tail.Point);
    }

    /// <summary>
    /// Verifies that the geometry bounds never exceed radius-plus-tail by radius-plus-stem.
    /// </summary>
    [Fact]
    public void Convert_ValidRadius_BoundsStayWithinRadiusPlusTailByRadiusPlusStem()
    {
        const double r = 8d;
        var geometry = (PathGeometry)Converter.Convert(new CornerRadius(r), typeof(Geometry), null, AnyCulture);

        var bounds = geometry.Bounds;

        Assert.True(bounds.Left >= -0.001);
        Assert.True(bounds.Top >= -0.001);
        Assert.True(bounds.Right <= r + PanelMetrics.AccentMarkerTailLength + 0.001);
        Assert.True(bounds.Bottom <= r + PanelMetrics.AccentMarkerStemLength + 0.001);
    }

    /// <summary>
    /// Verifies that a value that is not a <see cref="CornerRadius"/> returns <see cref="Binding.DoNothing"/>
    /// instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NotACornerRadius_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert("not-a-corner-radius", typeof(Geometry), null, AnyCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> value returns <see cref="Binding.DoNothing"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NullValue_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert(null, typeof(Geometry), null, AnyCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

    /// <summary>
    /// Verifies that non-finite or negative radius values return <see cref="Binding.DoNothing"/> instead of throwing.
    /// </summary>
    /// <param name="radius">The corner radius value under test, including NaN, infinities, and negative values.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void Convert_NonFiniteOrNegativeRadius_ReturnsDoNothing_DoesNotThrow(double radius)
    {
        var result = Converter.Convert(new CornerRadius(radius), typeof(Geometry), null, AnyCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

    /// <summary>
    /// Verifies that a zero radius still produces a <see cref="PathGeometry"/> without throwing.
    /// </summary>
    [Fact]
    public void Convert_ZeroRadius_DoesNotThrow_ProducesGeometry()
    {
        var result = Converter.Convert(new CornerRadius(0), typeof(Geometry), null, AnyCulture);
        Assert.IsType<PathGeometry>(result);
    }

    /// <summary>
    /// Verifies that <see cref="AccentMarkerGeometryConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(new PathGeometry(), typeof(CornerRadius), null, AnyCulture));
    }
}
