using System.Globalization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="IntegerInputParser"/> (docs/PLANS/color-presets-and-size-step.md §3.3).
/// </summary>
[Trait(UnitTests, "UI")]
public class IntegerInputParserTests
{
    /// <summary>Verifies valid, in-range plain-integer text parses to <see cref="IntegerInputError.None"/> and the expected value.</summary>
    /// <param name="text">The candidate input text.</param>
    /// <param name="expected">The expected parsed value.</param>
    [Theory]
    [InlineData("16", 16)]
    [InlineData("128", 128)]
    [InlineData("34", 34)]
    [InlineData("048", 48)]
    [InlineData(" 40 ", 40)]
    public void TryParse_ValidInRangeInput_ReturnsNoneAndValue(string text, int expected)
    {
        var error = IntegerInputParser.TryParse(text, 16, 128, out var value);

        Assert.Equal(IntegerInputError.None, error);
        Assert.Equal(expected, value);
    }

    /// <summary>Verifies <see langword="null"/>, empty, and whitespace-only text returns <see cref="IntegerInputError.Empty"/>.</summary>
    /// <param name="text">The candidate input text.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_EmptyInput_ReturnsEmpty(string? text)
    {
        var error = IntegerInputParser.TryParse(text, 16, 128, out var value);

        Assert.Equal(IntegerInputError.Empty, error);
        Assert.Equal(0, value);
    }

    /// <summary>
    /// Verifies non-plain-ASCII-digit text (letters, signs, decimals, thousands separators, exponents,
    /// non-ASCII digit glyphs, and int overflow) returns <see cref="IntegerInputError.NotAnInteger"/>.
    /// </summary>
    /// <param name="text">The candidate malformed input text.</param>
    [Theory]
    [InlineData("abc")]
    [InlineData("-8")]
    [InlineData("+8")]
    [InlineData("12.5")]
    [InlineData("12,5")]
    [InlineData("1 000")]
    [InlineData("1e2")]
    [InlineData("٣٤")] // Arabic-indic digits
    [InlineData("３４")] // fullwidth digits
    [InlineData("99999999999")] // overflows int
    public void TryParse_NotAnInteger_ReturnsNotAnInteger(string text)
    {
        var error = IntegerInputParser.TryParse(text, 16, 128, out var value);

        Assert.Equal(IntegerInputError.NotAnInteger, error);
        Assert.Equal(0, value);
    }

    /// <summary>Verifies a value below the minimum returns <see cref="IntegerInputError.OutOfRange"/>.</summary>
    [Fact]
    public void TryParse_BelowMinimum_ReturnsOutOfRange()
    {
        var error = IntegerInputParser.TryParse("15", 16, 128, out var value);

        Assert.Equal(IntegerInputError.OutOfRange, error);
        Assert.Equal(0, value);
    }

    /// <summary>Verifies a value above the maximum returns <see cref="IntegerInputError.OutOfRange"/>.</summary>
    [Fact]
    public void TryParse_AboveMaximum_ReturnsOutOfRange()
    {
        var error = IntegerInputParser.TryParse("129", 16, 128, out var value);

        Assert.Equal(IntegerInputError.OutOfRange, error);
    }

    /// <summary>
    /// Verifies "1" (the grid-step toggle-off sentinel) is rejected by manual entry into the 2..128
    /// grid-step range — that state is only ever set by the toggle, never typed (§2.2).
    /// </summary>
    [Fact]
    public void TryParse_GridStepFloorOfOne_ReturnsOutOfRange()
    {
        var error = IntegerInputParser.TryParse("1", 2, 128, out _);

        Assert.Equal(IntegerInputError.OutOfRange, error);
    }

    /// <summary>Verifies <see cref="IntegerInputParser.IsAllowedInputChunk"/> accepts only non-empty all-ASCII-digit chunks.</summary>
    /// <param name="chunk">The candidate input chunk.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData("7", true)]
    [InlineData("34", true)]
    [InlineData("-", false)]
    [InlineData("a", false)]
    [InlineData(" ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedInputChunk_MatchesExpectation(string? chunk, bool expected)
    {
        Assert.Equal(expected, IntegerInputParser.IsAllowedInputChunk(chunk));
    }

    /// <summary>Verifies <see cref="IntegerInputParser.FormatForDisplay"/> rounds ties away from zero.</summary>
    /// <param name="value">The value to format.</param>
    /// <param name="expected">The expected formatted text.</param>
    [Theory]
    [InlineData(32.0101, "32")]
    [InlineData(47.5, "48")]
    public void FormatForDisplay_RoundsAwayFromZero(double value, string expected)
    {
        Assert.Equal(expected, IntegerInputParser.FormatForDisplay(value));
    }

    /// <summary>Verifies non-finite input formats to <see cref="string.Empty"/>.</summary>
    /// <param name="value">A non-finite value: <c>NaN</c> or ±<c>Infinity</c>.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FormatForDisplay_NonFinite_ReturnsEmpty(double value)
    {
        Assert.Equal(string.Empty, IntegerInputParser.FormatForDisplay(value));
    }

    /// <summary>
    /// Verifies <see cref="IntegerInputParser.FormatForDisplay"/> never inserts a thousands separator
    /// regardless of the current UI culture (§3.3: always <see cref="CultureInfo.InvariantCulture"/>).
    /// </summary>
    [Fact]
    public void FormatForDisplay_RussianCulture_HasNoGroupSeparator()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            var formatted = IntegerInputParser.FormatForDisplay(1234);

            Assert.Equal("1234", formatted);
            Assert.DoesNotContain(' ', formatted);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
