using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="PanelSubtitleConverter"/> (docs/prompts/settings-window-redesign.md finding
/// M-5): the Panels settings section's per-row subtitle. Localized (docs/PLANS/localization.md §4.3,
/// WP-2): every culture-sensitive assertion below fixes an explicit <see cref="CultureInfo"/> rather
/// than relying on the calling thread's culture (L-3/R-5).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSubtitleConverterTests
{
    private static readonly PanelSubtitleConverter Converter = new();
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>Verifies the plural "items" form for a Grid-view panel with more than one file (English).</summary>
    [Fact]
    public void Convert_GridViewWithMultipleItems_English_ReturnsGridViewAndCount()
    {
        var text = Converter.Convert([PanelViewMode.Grid, null!, 12], typeof(string), null, English);

        Assert.Equal("Grid view · 12 items", text);
    }

    /// <summary>Verifies the singular "item" form for a List-view panel with exactly one file (English).</summary>
    [Fact]
    public void Convert_ListViewWithOneItem_English_ReturnsSingularItem()
    {
        var text = Converter.Convert([PanelViewMode.List, null!, 1], typeof(string), null, English);

        Assert.Equal("List view · 1 item", text);
    }

    /// <summary>Verifies the zero-item form still reads naturally ("0 items", not blank) in English.</summary>
    [Fact]
    public void Convert_GridViewWithNoItems_English_ReturnsZeroItems()
    {
        var text = Converter.Convert([PanelViewMode.Grid, null!, 0], typeof(string), null, English);

        Assert.Equal("Grid view · 0 items", text);
    }

    /// <summary>
    /// Verifies that a non-blank <c>SourceFolderPath</c> (Folder Portal) REPLACES the item count with
    /// the localized "Folder Portal" text rather than appending a second line, in English.
    /// </summary>
    [Fact]
    public void Convert_FolderPortalPanel_English_ReplacesCountWithFolderPortal()
    {
        var text = Converter.Convert(
            [PanelViewMode.Grid, @"C:\Users\me\Documents", 999],
            typeof(string),
            null,
            English);

        Assert.Equal("Grid view · Folder Portal", text);
    }

    /// <summary>Verifies that a whitespace-only <c>SourceFolderPath</c> is treated as "no portal".</summary>
    [Fact]
    public void Convert_WhitespaceOnlySourceFolderPath_TreatedAsNoPortal()
    {
        var text = Converter.Convert([PanelViewMode.List, "   ", 3], typeof(string), null, English);

        Assert.Equal("List view · 3 items", text);
    }

    /// <summary>Verifies that fewer than three input values never throws — returns empty instead.</summary>
    [Fact]
    public void Convert_TooFewValues_ReturnsEmptyString()
    {
        var text = Converter.Convert([PanelViewMode.Grid], typeof(string), null, English);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>Verifies that an empty input array never throws — returns empty instead.</summary>
    [Fact]
    public void Convert_EmptyValues_ReturnsEmptyString()
    {
        var text = Converter.Convert([], typeof(string), null, English);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>Verifies that a non-<see cref="PanelViewMode"/> first value never throws — returns empty instead.</summary>
    [Fact]
    public void Convert_FirstValueNotAPanelViewMode_ReturnsEmptyString()
    {
        var text = Converter.Convert(["not-a-view-mode", null!, 5], typeof(string), null, English);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// Verifies the Russian view-mode word, "· Портал папки" portal note, and Russian plural forms for
    /// 1/2/5/21 items — the CLDR One/Few/Many boundary cases (docs/PLANS/localization.md L-8).
    /// </summary>
    /// <param name="count">The item count to convert.</param>
    /// <param name="expected">The exact expected Russian subtitle text.</param>
    [Theory]
    [InlineData(1, "Сетка · 1 элемент")]
    [InlineData(2, "Сетка · 2 элемента")]
    [InlineData(5, "Сетка · 5 элементов")]
    [InlineData(21, "Сетка · 21 элемент")]
    public void Convert_Russian_GridView_UsesRussianPluralForms(int count, string expected)
    {
        var text = Converter.Convert([PanelViewMode.Grid, null!, count], typeof(string), null, Russian);

        Assert.Equal(expected, text);
    }

    /// <summary>Verifies the Russian List-view word and Russian Folder Portal note.</summary>
    [Fact]
    public void Convert_Russian_FolderPortalPanel_ReplacesCountWithFolderPortal()
    {
        var text = Converter.Convert(
            [PanelViewMode.List, @"C:\Users\me\Documents", 999],
            typeof(string),
            null,
            Russian);

        Assert.Equal("Список · Портал папки", text);
    }

    /// <summary>
    /// Verifies that a fourth element carrying a live <see cref="CultureInfo"/> (from
    /// <c>{loc:CultureBinding}</c>, docs/PLANS/localization.md §4.3) overrides the <c>culture</c>
    /// argument, so the subtitle re-evaluates in the newly selected language on a hot switch.
    /// </summary>
    [Fact]
    public void Convert_FourthElementCarriesCulture_OverridesCultureArgument()
    {
        var text = Converter.Convert([PanelViewMode.Grid, null!, 5, Russian], typeof(string), null, English);

        Assert.Equal("Сетка · 5 элементов", text);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSubtitleConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("Grid view · 1 item", [typeof(PanelViewMode), typeof(string), typeof(int)], null, English));
    }
}
