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

    [Fact]
    public void Convert_ValidRadius_ReturnsFrozenPathGeometry()
    {
        var result = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.True(geometry.IsFrozen);
    }

    [Fact]
    public void Convert_SameRadiusTwice_ReturnsSameCachedInstance()
    {
        var first = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);
        var second = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);

        Assert.Same(first, second);
    }

    [Fact]
    public void Convert_DifferentRadii_ReturnCachedButDistinctInstances()
    {
        var r8 = Converter.Convert(new CornerRadius(8), typeof(Geometry), null, AnyCulture);
        var r6 = Converter.Convert(new CornerRadius(6), typeof(Geometry), null, AnyCulture);

        Assert.NotSame(r8, r6);
    }

    [Fact]
    public void Convert_ValidRadius_StartPointIsBelowArcStartByStemLength()
    {
        const double r = 8d;
        var geometry = (PathGeometry)Converter.Convert(new CornerRadius(r), typeof(Geometry), null, AnyCulture);

        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(new Point(0, r + PanelMetrics.AccentMarkerStemLength), figure.StartPoint);
        Assert.False(figure.IsClosed);
    }

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

    [Fact]
    public void Convert_NotACornerRadius_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert("not-a-corner-radius", typeof(Geometry), null, AnyCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_NullValue_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert(null, typeof(Geometry), null, AnyCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

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

    [Fact]
    public void Convert_ZeroRadius_DoesNotThrow_ProducesGeometry()
    {
        var result = Converter.Convert(new CornerRadius(0), typeof(Geometry), null, AnyCulture);
        Assert.IsType<PathGeometry>(result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(new PathGeometry(), typeof(CornerRadius), null, AnyCulture));
    }
}
