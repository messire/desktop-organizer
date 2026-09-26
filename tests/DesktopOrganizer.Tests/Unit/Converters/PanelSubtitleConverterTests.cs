using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="PanelSubtitleConverter"/> (docs/prompts/settings-window-redesign.md finding
/// M-5): the Panels settings section's per-row subtitle.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSubtitleConverterTests
{
    private static readonly PanelSubtitleConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>Verifies the plural "items" form for a Grid-view panel with more than one file.</summary>
    [Fact]
    public void Convert_GridViewWithMultipleItems_ReturnsGridViewAndCount()
    {
        var text = Converter.Convert([PanelViewMode.Grid, null!, 12], typeof(string), null, AnyCulture);

        Assert.Equal("Grid view · 12 items", text);
    }

    /// <summary>Verifies the singular "item" form for a List-view panel with exactly one file.</summary>
    [Fact]
    public void Convert_ListViewWithOneItem_ReturnsSingularItem()
    {
        var text = Converter.Convert([PanelViewMode.List, null!, 1], typeof(string), null, AnyCulture);

        Assert.Equal("List view · 1 item", text);
    }

    /// <summary>Verifies the zero-item form still reads naturally ("0 items", not blank).</summary>
    [Fact]
    public void Convert_GridViewWithNoItems_ReturnsZeroItems()
    {
        var text = Converter.Convert([PanelViewMode.Grid, null!, 0], typeof(string), null, AnyCulture);

        Assert.Equal("Grid view · 0 items", text);
    }

    /// <summary>
    /// Verifies that a non-blank <c>SourceFolderPath</c> (Folder Portal) REPLACES the item count with
    /// "Folder Portal" rather than appending a second line/segment.
    /// </summary>
    [Fact]
    public void Convert_FolderPortalPanel_ReplacesCountWithFolderPortal()
    {
        var text = Converter.Convert(
            [PanelViewMode.Grid, @"C:\Users\me\Documents", 999],
            typeof(string),
            null,
            AnyCulture);

        Assert.Equal("Grid view · Folder Portal", text);
    }

    /// <summary>Verifies that a whitespace-only <c>SourceFolderPath</c> is treated as "no portal".</summary>
    [Fact]
    public void Convert_WhitespaceOnlySourceFolderPath_TreatedAsNoPortal()
    {
        var text = Converter.Convert([PanelViewMode.List, "   ", 3], typeof(string), null, AnyCulture);

        Assert.Equal("List view · 3 items", text);
    }

    /// <summary>Verifies that fewer than three input values never throws — returns empty instead.</summary>
    [Fact]
    public void Convert_TooFewValues_ReturnsEmptyString()
    {
        var text = Converter.Convert([PanelViewMode.Grid], typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>Verifies that an empty input array never throws — returns empty instead.</summary>
    [Fact]
    public void Convert_EmptyValues_ReturnsEmptyString()
    {
        var text = Converter.Convert([], typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>Verifies that a non-<see cref="PanelViewMode"/> first value never throws — returns empty instead.</summary>
    [Fact]
    public void Convert_FirstValueNotAPanelViewMode_ReturnsEmptyString()
    {
        var text = Converter.Convert(["not-a-view-mode", null!, 5], typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSubtitleConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack("Grid view · 1 item", [typeof(PanelViewMode), typeof(string), typeof(int)], null, AnyCulture));
    }
}
