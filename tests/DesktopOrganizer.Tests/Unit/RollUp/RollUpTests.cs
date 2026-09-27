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
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — glyph mode
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that converting <see langword="true"/> with a <see langword="null"/> parameter (default
    /// glyph mode) returns the ChevronDown glyph.
    /// </summary>
    [Fact]
    public void Convert_TrueWithNullParameter_ReturnsChevronDown()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), null!, Invariant);

        Assert.Equal(ChevronDownGlyph, result);
    }

    /// <summary>
    /// Verifies that converting <see langword="false"/> with a <see langword="null"/> parameter (default
    /// glyph mode) returns the ChevronUp glyph.
    /// </summary>
    [Fact]
    public void Convert_FalseWithNullParameter_ReturnsChevronUp()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), null!, Invariant);

        Assert.Equal(ChevronUpGlyph, result);
    }

    /// <summary>
    /// Verifies that converting <see langword="true"/> with an explicit "glyph" parameter returns the
    /// ChevronDown glyph.
    /// </summary>
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
    // Localized (docs/PLANS/localization.md §4.3, WP-2): the tooltip text now comes from
    // Panel_RollUp_Expand_Tooltip/Panel_RollUp_Collapse_Tooltip, so these assertions fix an explicit
    // culture (L-3/R-5) rather than relying on CultureInfo.InvariantCulture, which the glyph-mode tests
    // above still use since the glyph itself is culture-invariant.

    /// <summary>
    /// Verifies that converting <see langword="true"/> with the "tooltip" parameter returns the
    /// "Expand panel" tooltip text in English.
    /// </summary>
    [Fact]
    public void Convert_TrueWithTooltipParameter_English_ReturnsExpand()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), "tooltip", English);

        // Pin updated (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1, NIT): "Expand" -> "Expand
        // panel" for screen-reader clarity out of context — see BoolToGlyphConverter's own remarks.
        Assert.Equal("Expand panel", result);
    }

    /// <summary>
    /// Verifies that converting <see langword="false"/> with the "tooltip" parameter returns the
    /// "Collapse panel" tooltip text in English.
    /// </summary>
    [Fact]
    public void Convert_FalseWithTooltipParameter_English_ReturnsCollapse()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), "tooltip", English);

        Assert.Equal("Collapse panel", result);
    }

    /// <summary>
    /// Verifies that converting <see langword="true"/> with the "tooltip" parameter returns the
    /// Russian "Развернуть панель" tooltip text.
    /// </summary>
    [Fact]
    public void Convert_TrueWithTooltipParameter_Russian_ReturnsExpand()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), "tooltip", Russian);

        Assert.Equal("Развернуть панель", result);
    }

    /// <summary>
    /// Verifies that converting <see langword="false"/> with the "tooltip" parameter returns the
    /// Russian "Свернуть панель" tooltip text.
    /// </summary>
    [Fact]
    public void Convert_FalseWithTooltipParameter_Russian_ReturnsCollapse()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), "tooltip", Russian);

        Assert.Equal("Свернуть панель", result);
    }

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — ConvertBack
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="BoolToGlyphConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
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

    /// <summary>
    /// Verifies that setting <see cref="ChildWindowModel.IsRolledUp"/> to <see langword="true"/> raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> with the correct property name.
    /// </summary>
    [Fact]
    public void IsRolledUp_SetTrue_RaisesPropertyChangedWithCorrectName()
    {
        var model = new ChildWindowModel();
        var raisedProperties = new List<string?>();
        model.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        model.IsRolledUp = true;

        Assert.Contains(nameof(ChildWindowModel.IsRolledUp), raisedProperties);
    }

    /// <summary>
    /// Verifies that setting <see cref="ChildWindowModel.IsRolledUp"/> to the same value twice does not
    /// raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> on the second set.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private static BoolToGlyphConverter CreateConverter() => new();
}
