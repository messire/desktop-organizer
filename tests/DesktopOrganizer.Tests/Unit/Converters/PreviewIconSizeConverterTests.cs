using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="PreviewIconSizeConverter"/> (ADR-042): maps the real icon-size range onto
/// <c>PanelPreviewCard</c>'s own small, fixed placeholder-tile size range.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PreviewIconSizeConverterTests
{
    private static readonly PreviewIconSizeConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>Verifies that the minimum real icon size maps to the minimum tile size.</summary>
    [Fact]
    public void Convert_MinimumIconSize_ReturnsMinTileSize()
    {
        var result = Converter.Convert((double)SizeStepPolicy.IconSizeMinimum, typeof(double), null, AnyCulture);

        Assert.Equal(PreviewIconSizeConverter.MinTileSize, result);
    }

    /// <summary>Verifies that the maximum real icon size maps to the maximum tile size.</summary>
    [Fact]
    public void Convert_MaximumIconSize_ReturnsMaxTileSize()
    {
        var result = Converter.Convert((double)SizeStepPolicy.IconSizeMaximum, typeof(double), null, AnyCulture);

        Assert.Equal(PreviewIconSizeConverter.MaxTileSize, result);
    }

    /// <summary>Verifies that a value below the minimum clamps to the minimum tile size rather than extrapolating.</summary>
    [Fact]
    public void Convert_BelowMinimum_ClampsToMinTileSize()
    {
        var result = Converter.Convert((double)(SizeStepPolicy.IconSizeMinimum - 100), typeof(double), null, AnyCulture);

        Assert.Equal(PreviewIconSizeConverter.MinTileSize, result);
    }

    /// <summary>Verifies that a value above the maximum clamps to the maximum tile size rather than extrapolating.</summary>
    [Fact]
    public void Convert_AboveMaximum_ClampsToMaxTileSize()
    {
        var result = Converter.Convert((double)(SizeStepPolicy.IconSizeMaximum + 100), typeof(double), null, AnyCulture);

        Assert.Equal(PreviewIconSizeConverter.MaxTileSize, result);
    }

    /// <summary>Verifies that a value halfway between minimum and maximum maps to the midpoint tile size.</summary>
    [Fact]
    public void Convert_Midpoint_ReturnsMidpointTileSize()
    {
        var midIcon = (double)(SizeStepPolicy.IconSizeMinimum + SizeStepPolicy.IconSizeMaximum) / 2;
        var expectedMid = (PreviewIconSizeConverter.MinTileSize + PreviewIconSizeConverter.MaxTileSize) / 2;

        var result = Converter.Convert(midIcon, typeof(double), null, AnyCulture);

        Assert.Equal(expectedMid, (double)result, precision: 6);
    }

    /// <summary>Verifies that non-<see cref="double"/>/non-finite input never throws and resolves to the minimum tile size.</summary>
    /// <param name="value">The candidate (invalid) input value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Convert_InvalidInput_NeverThrows_ReturnsMinTileSize(object? value)
    {
        var exception = Record.Exception(() => Converter.Convert(value, typeof(double), null, AnyCulture));

        Assert.Null(exception);
        Assert.Equal(PreviewIconSizeConverter.MinTileSize, Converter.Convert(value, typeof(double), null, AnyCulture));
    }

    /// <summary>Verifies that <see cref="PreviewIconSizeConverter.ConvertBack"/> is not supported.</summary>
    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => Converter.ConvertBack(24d, typeof(double), null, AnyCulture));
    }
}
