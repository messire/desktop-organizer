using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Converters;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="RoundedRectClipConverter"/> (docs/PLANS/workspace-grid-07-visual.md
/// "Border radius") — never throws, degrades to <see cref="Binding.DoNothing"/> on any garbage/
/// not-yet-measured input, produces a frozen <see cref="RectangleGeometry"/> matching the requested
/// size/radius otherwise. Same contract style as <see cref="PanelSurfaceBrushConverterTests"/>/
/// <see cref="Converters.PanelSurfaceBrushConverter"/> and friends.
/// </summary>
[Trait(UnitTests, "UI")]
public class RoundedRectClipConverterTests
{
    private static readonly RoundedRectClipConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_ValidInputs_ReturnsFrozenRectangleGeometryWithExpectedBoundsAndRadius()
    {
        var result = Converter.Convert(new object?[] { 200.0, 100.0, new CornerRadius(8) }, typeof(Geometry), null, AnyCulture);

        var geometry = Assert.IsType<RectangleGeometry>(result);
        Assert.True(geometry.IsFrozen);
        Assert.Equal(new Rect(0, 0, 200, 100), geometry.Rect);
        Assert.Equal(8, geometry.RadiusX);
        Assert.Equal(8, geometry.RadiusY);
    }

    [Fact]
    public void Convert_ZeroCornerRadius_StillProducesGeometry()
    {
        var result = Converter.Convert(new object?[] { 50.0, 50.0, new CornerRadius(0) }, typeof(Geometry), null, AnyCulture);

        var geometry = Assert.IsType<RectangleGeometry>(result);
        Assert.Equal(0, geometry.RadiusX);
        Assert.Equal(0, geometry.RadiusY);
    }

    [Fact]
    public void Convert_NonUniformCornerRadius_UsesTopLeft()
    {
        var result = Converter.Convert(new object?[] { 50.0, 50.0, new CornerRadius(3, 8, 12, 20) }, typeof(Geometry), null, AnyCulture);

        var geometry = Assert.IsType<RectangleGeometry>(result);
        Assert.Equal(3, geometry.RadiusX);
        Assert.Equal(3, geometry.RadiusY);
    }

    [Theory]
    [InlineData(0.0, 100.0)]
    [InlineData(-10.0, 100.0)]
    [InlineData(100.0, 0.0)]
    [InlineData(100.0, -5.0)]
    [InlineData(double.NaN, 100.0)]
    [InlineData(100.0, double.PositiveInfinity)]
    public void Convert_NonPositiveOrNonFiniteSize_ReturnsDoNothing(double width, double height)
    {
        var result = Converter.Convert(new object?[] { width, height, new CornerRadius(8) }, typeof(Geometry), null, AnyCulture);

        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_TooFewValues_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { 100.0, 100.0 }, typeof(Geometry), null, AnyCulture);

        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_NullValues_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert(null!, typeof(Geometry), null, AnyCulture);

        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_WrongTypes_ReturnsDoNothing_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "not-a-double", 100.0, new CornerRadius(8) }, typeof(Geometry), null, AnyCulture);

        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_CornerRadiusSlotHoldsWrongType_ReturnsDoNothing()
    {
        var result = Converter.Convert(new object?[] { 100.0, 100.0, "not-a-corner-radius" }, typeof(Geometry), null, AnyCulture);

        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void ConvertBack_AlwaysThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(new RectangleGeometry(), [typeof(double), typeof(double), typeof(CornerRadius)], null, AnyCulture));
    }
}
