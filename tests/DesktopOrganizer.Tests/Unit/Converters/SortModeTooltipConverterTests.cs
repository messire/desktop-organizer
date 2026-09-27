using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="SortModeTooltipConverter"/> (ui-polishing-1 §4.3/§4.4 — renamed and
/// simplified from the old <c>SortModeGlyphConverter</c>, which used to also produce button-content
/// glyph text before the sort button's state became a highlight instead of a glyph swap). Localized
/// (docs/PLANS/localization.md §4.3, WP-2): every assertion fixes an explicit culture (L-3/R-5).
/// </summary>
[Trait(UnitTests, "UI")]
public class SortModeTooltipConverterTests
{
    private static readonly SortModeTooltipConverter Converter = new();
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Verifies that <see cref="PanelSortMode.Name"/> converts to the "Sorted by name" tooltip text
    /// in English.
    /// </summary>
    [Fact]
    public void Convert_Name_English_ReturnsSortedByNameTooltip()
    {
        var tooltip = Converter.Convert(PanelSortMode.Name, typeof(string), null!, English);

        Assert.Equal("Sorted by name — click to arrange manually", tooltip);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSortMode.Manual"/> converts to the "Manual order" tooltip text
    /// in English.
    /// </summary>
    [Fact]
    public void Convert_Manual_English_ReturnsManualOrderTooltip()
    {
        var tooltip = Converter.Convert(PanelSortMode.Manual, typeof(string), null!, English);

        Assert.Equal("Manual order — click to sort by name", tooltip);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSortMode.Name"/> converts to the Russian "Sorted by name" tooltip
    /// text.
    /// </summary>
    [Fact]
    public void Convert_Name_Russian_ReturnsSortedByNameTooltip()
    {
        var tooltip = Converter.Convert(PanelSortMode.Name, typeof(string), null!, Russian);

        Assert.Equal("Отсортировано по имени — нажмите, чтобы упорядочить вручную", tooltip);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSortMode.Manual"/> converts to the Russian "Manual order" tooltip
    /// text.
    /// </summary>
    [Fact]
    public void Convert_Manual_Russian_ReturnsManualOrderTooltip()
    {
        var tooltip = Converter.Convert(PanelSortMode.Manual, typeof(string), null!, Russian);

        Assert.Equal("Порядок вручную — нажмите, чтобы отсортировать по имени", tooltip);
    }

    /// <summary>
    /// Verifies that the converter parameter no longer affects the output, since the glyph/tooltip branching
    /// was removed and the converter always returns tooltip text.
    /// </summary>
    [Fact]
    public void Convert_IgnoresConverterParameter()
    {
        // No glyph/tooltip parameter branch left — the converter always returns tooltip text now.
        var withParameter = Converter.Convert(PanelSortMode.Name, typeof(string), "glyph", English);
        var withoutParameter = Converter.Convert(PanelSortMode.Name, typeof(string), null!, English);

        Assert.Equal(withoutParameter, withParameter);
    }

    /// <summary>
    /// Verifies that an unrecognized value (such as <see langword="null"/>) is treated as
    /// <see cref="PanelSortMode.Manual"/> rather than throwing.
    /// </summary>
    [Fact]
    public void Convert_UnrecognizedValue_TreatedAsManual()
    {
        var tooltip = Converter.Convert(null!, typeof(string), null!, English);

        Assert.Equal("Manual order — click to sort by name", tooltip);
    }

    /// <summary>
    /// Verifies that <see cref="SortModeTooltipConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("x", typeof(PanelSortMode), null!, English));
    }
}
