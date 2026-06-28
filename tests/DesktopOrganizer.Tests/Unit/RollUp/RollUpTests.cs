using System.Globalization;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.RollUp;

/// <summary>
/// Unit tests for the «Свернуть до заголовка» (roll-up) feature:
/// <see cref="BoolToGlyphConverter"/> and <see cref="ChildWindowModel.IsRolledUp"/> INPC.
/// </summary>
[Trait(UnitTests, "RollUp")]
public class RollUpTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static BoolToGlyphConverter CreateConverter() => new();

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — glyph mode
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_TrueWithNullParameter_ReturnsDownTriangle()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), null!, Invariant);

        Assert.Equal("▽", result);
    }

    [Fact]
    public void Convert_FalseWithNullParameter_ReturnsUpTriangle()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), null!, Invariant);

        Assert.Equal("△", result);
    }

    [Fact]
    public void Convert_TrueWithGlyphParameter_ReturnsDownTriangle()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), "glyph", Invariant);

        Assert.Equal("▽", result);
    }

    // ------------------------------------------------------------------
    // BoolToGlyphConverter — tooltip mode
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_TrueWithTooltipParameter_ReturnsExpand()
    {
        var converter = CreateConverter();

        var result = converter.Convert(true, typeof(string), "tooltip", Invariant);

        Assert.Equal("Развернуть", result);
    }

    [Fact]
    public void Convert_FalseWithTooltipParameter_ReturnsCollapse()
    {
        var converter = CreateConverter();

        var result = converter.Convert(false, typeof(string), "tooltip", Invariant);

        Assert.Equal("Свернуть", result);
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
