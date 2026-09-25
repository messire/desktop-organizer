using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="EnumDisplayNameConverter"/> (P06 fix-cycle UX-P1,
/// adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"). Assertions pin the EXACT strings
/// <c>Views/ChildWindow.xaml</c>'s "Icon layout" context-menu radio group already shows, so the two
/// surfaces can never silently drift apart.
/// </summary>
[Trait(UnitTests, "UI")]
public class EnumDisplayNameConverterTests
{
    private static readonly EnumDisplayNameConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that <see cref="PanelGeometryMode.GridAligned"/> converts to the "Grid aligned" display text.
    /// </summary>
    [Fact]
    public void Convert_PanelGeometryMode_GridAligned_ReturnsGridAligned()
    {
        var text = Converter.Convert(PanelGeometryMode.GridAligned, typeof(string), null, AnyCulture);

        Assert.Equal("Grid aligned", text);
    }

    /// <summary>
    /// Verifies that <see cref="PanelGeometryMode.LegacyFreeform"/> converts to the "Free-form (legacy)" display text.
    /// </summary>
    [Fact]
    public void Convert_PanelGeometryMode_LegacyFreeform_ReturnsFreeFormLegacy()
    {
        var text = Converter.Convert(PanelGeometryMode.LegacyFreeform, typeof(string), null, AnyCulture);

        Assert.Equal("Free-form (legacy)", text);
    }

    /// <summary>
    /// Verifies that <see cref="IconLayoutPolicy.FixedGap"/> converts to the "Fixed spacing" display text.
    /// </summary>
    [Fact]
    public void Convert_IconLayoutPolicy_FixedGap_ReturnsFixedSpacing()
    {
        var text = Converter.Convert(IconLayoutPolicy.FixedGap, typeof(string), null, AnyCulture);

        Assert.Equal("Fixed spacing", text);
    }

    /// <summary>
    /// Verifies that <see cref="IconLayoutPolicy.LegacyStretch"/> converts to the "Adaptive stretch (legacy)"
    /// display text.
    /// </summary>
    [Fact]
    public void Convert_IconLayoutPolicy_LegacyStretch_ReturnsAdaptiveStretchLegacy()
    {
        var text = Converter.Convert(IconLayoutPolicy.LegacyStretch, typeof(string), null, AnyCulture);

        Assert.Equal("Adaptive stretch (legacy)", text);
    }

    /// <summary>
    /// Verifies that an unrecognized value falls back to its <see cref="object.ToString"/> representation.
    /// </summary>
    [Fact]
    public void Convert_UnrecognizedValue_FallsBackToToString()
    {
        var text = Converter.Convert(42, typeof(string), null, AnyCulture);

        Assert.Equal("42", text);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> value converts to an empty string.
    /// </summary>
    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        var text = Converter.Convert(null!, typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// Verifies that <see cref="EnumDisplayNameConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("Grid aligned", typeof(PanelGeometryMode), null, AnyCulture));
    }
}
