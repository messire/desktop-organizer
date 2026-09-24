using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.RollUp;

/// <summary>
/// Unit tests for the "collapse to title bar" (roll-up) feature:
/// <see cref="BoolToGlyphConverter"/> and <see cref="ChildWindowModel.IsRolledUp"/> INPC.
/// </summary>
[Trait(UnitTests, "RollUp")]
public class RollUpTests
{
    /// <summary>
    /// Segoe MDL2 Assets glyph rendered while the panel IS rolled up — ChevronDown (U+E70D),
    /// i.e. "click to roll back down". The panel header buttons all render in the
    /// "Segoe MDL2 Assets" font (PanelHeaderButtonStyle), so the converter deliberately returns
    /// private-use MDL2 code points rather than the plain Unicode geometric triangles an earlier
    /// iteration used.
    /// </summary>
    private const string ChevronDownGlyph = "\uE70D";

    /// <summary>
    /// Segoe MDL2 Assets glyph rendered while the panel is expanded — ChevronUp (U+E70E),
    /// i.e. "click to roll up".
    /// </summary>
    private const string ChevronUpGlyph = "\uE70E";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static BoolToGlyphConverter CreateConverter() => new();

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — glyph mode
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_TrueWithNullParameter_ReturnsChevronDown()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), null!, Invariant);

        Assert.Equal(ChevronDownGlyph, result);
    }

    [Fact]
    public void Convert_FalseWithNullParameter_ReturnsChevronUp()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), null!, Invariant);

        Assert.Equal(ChevronUpGlyph, result);
    }

    [Fact]
    public void Convert_TrueWithGlyphParameter_ReturnsChevronDown()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), "glyph", Invariant);

        Assert.Equal(ChevronDownGlyph, result);
    }

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — tooltip mode
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_TrueWithTooltipParameter_ReturnsExpand()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), "tooltip", Invariant);

        // Pin updated (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1, NIT): "Expand" -> "Expand
        // panel" for screen-reader clarity out of context — see BoolToGlyphConverter's own remarks.
        Assert.Equal("Expand panel", result);
    }

    [Fact]
    public void Convert_FalseWithTooltipParameter_ReturnsCollapse()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), "tooltip", Invariant);

        Assert.Equal("Collapse panel", result);
    }

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — ConvertBack
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertBack_AlwaysThrowsNotSupportedException()
    {
        var converter = CreateConverter();

        Assert.Throws<NotSupportedException>(
            () => converter.ConvertBack("▽", typeof(bool), null!, Invariant));
    }

    // ------------------------------------------------------------------
    // ChildWindowModel.IsRolledUp — INPC
    // ------------------------------------------------------------------

    [Fact]
    public void IsRolledUp_SetTrue_RaisesPropertyChangedWithCorrectName()
    {
        var model = new ChildWindowModel();
        var raisedProperties = new List<string?>();
        model.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        model.IsRolledUp = true;

        Assert.Contains(nameof(ChildWindowModel.IsRolledUp), raisedProperties);
    }

    [Fact]
    public void IsRolledUp_SetSameValueTwice_DoesNotRaisePropertyChangedSecondTime()
    {
        var model = new ChildWindowModel();
        model.IsRolledUp = true;

        var raisedAfterSecondSet = new List<string?>();
        model.PropertyChanged += (_, e) => raisedAfterSecondSet.Add(e.PropertyName);

        // Set the same value again — should be a no-op
        model.IsRolledUp = true;

        Assert.Empty(raisedAfterSecondSet);
    }
}
