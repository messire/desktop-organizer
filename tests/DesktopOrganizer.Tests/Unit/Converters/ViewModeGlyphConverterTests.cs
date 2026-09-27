using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="ViewModeGlyphConverter"/> (ui-polishing-1 §4.2). The glyph/tooltip
/// convention is "target mode" — what the button switches TO on click, not the mode currently
/// active — so a <see cref="PanelViewMode.Grid"/> input must describe switching to List, and vice
/// versa. The glyph itself is culture-invariant (a Segoe MDL2 Assets code point); the tooltip text is
/// localized (docs/PLANS/localization.md §4.3, WP-2), so its assertions fix an explicit culture (L-3/R-5).
/// </summary>
[Trait(UnitTests, "UI")]
public class ViewModeGlyphConverterTests
{
    private static readonly ViewModeGlyphConverter Converter = new();
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Verifies that when the current mode is <see cref="PanelViewMode.Grid"/>, the glyph describes the
    /// List target rather than the current Grid mode.
    /// </summary>
    [Fact]
    public void Convert_CurrentlyGrid_GlyphDescribesListTarget()
    {
        var glyph = Converter.Convert(PanelViewMode.Grid, typeof(string), null!, English);

        Assert.Equal("", glyph); // List
    }

    /// <summary>
    /// Verifies that when the current mode is <see cref="PanelViewMode.Grid"/>, the "tooltip" parameter
    /// describes switching to list view, in English.
    /// </summary>
    [Fact]
    public void Convert_CurrentlyGrid_English_TooltipDescribesListTarget()
    {
        var tooltip = Converter.Convert(PanelViewMode.Grid, typeof(string), "tooltip", English);

        Assert.Equal("Switch to list view", tooltip);
    }

    /// <summary>
    /// Verifies that when the current mode is <see cref="PanelViewMode.List"/>, the glyph describes the
    /// Grid target rather than the current List mode.
    /// </summary>
    [Fact]
    public void Convert_CurrentlyList_GlyphDescribesGridTarget()
    {
        var glyph = Converter.Convert(PanelViewMode.List, typeof(string), null!, English);

        Assert.Equal("", glyph); // ViewAll
    }

    /// <summary>
    /// Verifies that when the current mode is <see cref="PanelViewMode.List"/>, the "tooltip" parameter
    /// describes switching to grid view, in English.
    /// </summary>
    [Fact]
    public void Convert_CurrentlyList_English_TooltipDescribesGridTarget()
    {
        var tooltip = Converter.Convert(PanelViewMode.List, typeof(string), "tooltip", English);

        Assert.Equal("Switch to grid view", tooltip);
    }

    /// <summary>
    /// Verifies the Russian tooltip text for switching to list view.
    /// </summary>
    [Fact]
    public void Convert_CurrentlyGrid_Russian_TooltipDescribesListTarget()
    {
        var tooltip = Converter.Convert(PanelViewMode.Grid, typeof(string), "tooltip", Russian);

        Assert.Equal("Переключить на список", tooltip);
    }

    /// <summary>
    /// Verifies the Russian tooltip text for switching to grid view.
    /// </summary>
    [Fact]
    public void Convert_CurrentlyList_Russian_TooltipDescribesGridTarget()
    {
        var tooltip = Converter.Convert(PanelViewMode.List, typeof(string), "tooltip", Russian);

        Assert.Equal("Переключить на сетку", tooltip);
    }

    /// <summary>
    /// Verifies that the "tooltip" converter parameter is matched case-insensitively.
    /// </summary>
    [Fact]
    public void Convert_TooltipParameterIsCaseInsensitive()
    {
        var tooltip = Converter.Convert(PanelViewMode.Grid, typeof(string), "TOOLTIP", English);

        Assert.Equal("Switch to list view", tooltip);
    }

    /// <summary>
    /// Verifies that an unrecognized value is treated as if the current mode were List, so the glyph
    /// falls back to describing the Grid target.
    /// </summary>
    [Fact]
    public void Convert_UnrecognizedValue_TreatedAsList_GlyphDescribesGridTarget()
    {
        // Defensive default: anything that isn't literally PanelViewMode.Grid falls back to the
        // same branch as List, matching the pattern-match `value is PanelViewMode.Grid` check.
        var glyph = Converter.Convert(null!, typeof(string), null!, English);

        Assert.Equal("", glyph);
    }

    /// <summary>
    /// Verifies that <see cref="ViewModeGlyphConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("x", typeof(PanelViewMode), null!, English));
    }
}
