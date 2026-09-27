using System.Globalization;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="PluralRules.Select"/> (docs/PLANS/localization.md L-8, WP-0S DoD 5).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class PluralRulesTests
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>
    /// Verifies the Russian CLDR rule (docs/PLANS/localization.md §2.1) for the count/category pairs the
    /// plan's own DoD calls out: 0, 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 101, 111.
    /// </summary>
    /// <param name="n">The cardinal count.</param>
    /// <param name="expected">The expected plural category.</param>
    [Theory]
    [InlineData(0, PluralCategory.Many)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(14, PluralCategory.Many)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(25, PluralCategory.Many)]
    [InlineData(101, PluralCategory.One)]
    [InlineData(111, PluralCategory.Many)]
    public void Select_Russian_ReturnsExpectedCategory(long n, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Select(Russian, n));

    /// <summary>
    /// Verifies that a negative count is categorized by its absolute value under the Russian rule.
    /// </summary>
    /// <param name="n">The (negative) cardinal count.</param>
    /// <param name="expected">The expected plural category.</param>
    [Theory]
    [InlineData(-1, PluralCategory.One)]
    [InlineData(-2, PluralCategory.Few)]
    [InlineData(-11, PluralCategory.Many)]
    public void Select_Russian_NegativeCounts_UseAbsoluteValue(long n, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Select(Russian, n));

    /// <summary>
    /// Verifies the English rule: only <c>1</c> is <see cref="PluralCategory.One"/>, everything else —
    /// including <c>0</c> — is <see cref="PluralCategory.Other"/>.
    /// </summary>
    /// <param name="n">The cardinal count.</param>
    /// <param name="expected">The expected plural category.</param>
    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Select_English_ReturnsExpectedCategory(long n, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Select(English, n));

    /// <summary>
    /// Verifies that a culture other than Russian or English (e.g. German) still uses the two-category
    /// English-style rule (docs/PLANS/localization.md §2.1: "en и любой другой язык").
    /// </summary>
    [Fact]
    public void Select_NonRussianCulture_UsesEnglishStyleRule()
    {
        var german = CultureInfo.GetCultureInfo("de");

        Assert.Equal(PluralCategory.One, PluralRules.Select(german, 1));
        Assert.Equal(PluralCategory.Other, PluralRules.Select(german, 2));
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> culture throws <see cref="ArgumentNullException"/> rather
    /// than a less specific exception.
    /// </summary>
    [Fact]
    public void Select_ThrowsOnNullCulture() =>
        Assert.Throws<ArgumentNullException>(() => PluralRules.Select(null!, 1));
}
