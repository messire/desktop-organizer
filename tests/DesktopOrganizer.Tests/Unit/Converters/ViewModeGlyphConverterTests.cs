using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="ViewModeGlyphConverter"/> (ui-polishing-1 §4.2). The glyph/tooltip
/// convention is "target mode" — what the button switches TO on click, not the mode currently
/// active — so a <see cref="PanelViewMode.Grid"/> input must describe switching to List, and vice
/// versa.
/// </summary>
[Trait(UnitTests, "UI")]
public class ViewModeGlyphConverterTests
{
    private static readonly ViewModeGlyphConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_CurrentlyGrid_GlyphDescribesListTarget()
    {
        var glyph = Converter.Convert(PanelViewMode.Grid, typeof(string), null!, AnyCulture);

        Assert.Equal("\uE8FD", glyph); // List
    }

    [Fact]
    public void Convert_CurrentlyGrid_TooltipDescribesListTarget()
    {
        var tooltip = Converter.Convert(PanelViewMode.Grid, typeof(string), "tooltip", AnyCulture);

        Assert.Equal("Switch to list view", tooltip);
    }

    [Fact]
    public void Convert_CurrentlyList_GlyphDescribesGridTarget()
    {
        var glyph = Converter.Convert(PanelViewMode.List, typeof(string), null!, AnyCulture);

        Assert.Equal("\uE8A9", glyph); // ViewAll
    }

    [Fact]
    public void Convert_CurrentlyList_TooltipDescribesGridTarget()
    {
        var tooltip = Converter.Convert(PanelViewMode.List, typeof(string), "tooltip", AnyCulture);

        Assert.Equal("Switch to grid view", tooltip);
    }

    [Fact]
    public void Convert_TooltipParameterIsCaseInsensitive()
    {
        var tooltip = Converter.Convert(PanelViewMode.Grid, typeof(string), "TOOLTIP", AnyCulture);

        Assert.Equal("Switch to list view", tooltip);
    }

    [Fact]
    public void Convert_UnrecognizedValue_TreatedAsList_GlyphDescribesGridTarget()
    {
        // Defensive default: anything that isn't literally PanelViewMode.Grid falls back to the
        // same branch as List, matching the pattern-match `value is PanelViewMode.Grid` check.
        var glyph = Converter.Convert(null!, typeof(string), null!, AnyCulture);

        Assert.Equal("\uE8A9", glyph);
    }

    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("x", typeof(PanelViewMode), null!, AnyCulture));
    }
}
