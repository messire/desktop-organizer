using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="SortModeTooltipConverter"/> (ui-polishing-1 §4.3/§4.4 — renamed and
/// simplified from the old <c>SortModeGlyphConverter</c>, which used to also produce button-content
/// glyph text before the sort button's state became a highlight instead of a glyph swap).
/// </summary>
[Trait(UnitTests, "UI")]
public class SortModeTooltipConverterTests
{
    private static readonly SortModeTooltipConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_Name_ReturnsSortedByNameTooltip()
    {
        var tooltip = Converter.Convert(PanelSortMode.Name, typeof(string), null!, AnyCulture);

        Assert.Equal("Sorted by name — click to arrange manually", tooltip);
    }

    [Fact]
    public void Convert_Manual_ReturnsManualOrderTooltip()
    {
        var tooltip = Converter.Convert(PanelSortMode.Manual, typeof(string), null!, AnyCulture);

        Assert.Equal("Manual order — click to sort by name", tooltip);
    }

    [Fact]
    public void Convert_IgnoresConverterParameter()
    {
        // No glyph/tooltip parameter branch left — the converter always returns tooltip text now.
        var withParameter = Converter.Convert(PanelSortMode.Name, typeof(string), "glyph", AnyCulture);
        var withoutParameter = Converter.Convert(PanelSortMode.Name, typeof(string), null!, AnyCulture);

        Assert.Equal(withoutParameter, withParameter);
    }

    [Fact]
    public void Convert_UnrecognizedValue_TreatedAsManual()
    {
        var tooltip = Converter.Convert(null!, typeof(string), null!, AnyCulture);

        Assert.Equal("Manual order — click to sort by name", tooltip);
    }

    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("x", typeof(PanelSortMode), null!, AnyCulture));
    }
}
