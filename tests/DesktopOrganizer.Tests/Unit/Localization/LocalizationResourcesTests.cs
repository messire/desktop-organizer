using System.Globalization;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="LocalizationResources"/> (docs/PLANS/localization.md §2.1, WP-0S DoD 1/5).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class LocalizationResourcesTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Verifies that the neutral (English) resource assembly is found and returns the expected text.
    /// </summary>
    [Fact]
    public void GetString_NeutralCulture_ReturnsEnglishText() =>
        Assert.Equal("OK", LocalizationResources.GetString("Common_Ok_Button", English));

    /// <summary>
    /// Verifies that the <c>ru</c> satellite resource assembly is found and returns the expected text.
    /// </summary>
    [Fact]
    public void GetString_RussianCulture_ReturnsRussianText() =>
        Assert.Equal("ОК", LocalizationResources.GetString("Common_Ok_Button", Russian));

    /// <summary>
    /// Verifies that a missing key returns the key itself rather than throwing or returning
    /// <see langword="null"/> (G-L2's "visible, greppable placeholder" contract).
    /// </summary>
    [Fact]
    public void GetString_MissingKey_ReturnsKeyItself() =>
        Assert.Equal("Does_Not_Exist_Key", LocalizationResources.GetString("Does_Not_Exist_Key", English));

    /// <summary>
    /// Verifies that <see cref="LocalizationResources.Format"/> both looks up and formats using the
    /// given culture, for both supported languages.
    /// </summary>
    [Fact]
    public void Format_UsesCultureForLookupAndArguments()
    {
        var date = new DateTime(2026, 9, 26, 15, 15, 0);

        var enResult = LocalizationResources.Format("Snapshot_DefaultName_Format", English, date);
        var ruResult = LocalizationResources.Format("Snapshot_DefaultName_Format", Russian, date);

        Assert.Equal("Layout 26.09 15:15", enResult);
        Assert.Equal("Расположение 26.09 15:15", ruResult);
    }

    /// <summary>
    /// Verifies <see cref="LocalizationResources.FormatPlural"/> against the English CLDR rule (only
    /// <c>1</c> is singular).
    /// </summary>
    /// <param name="count">The cardinal count.</param>
    /// <param name="expected">The expected formatted string.</param>
    [Theory]
    [InlineData(1, "1 item")]
    [InlineData(2, "2 items")]
    [InlineData(5, "5 items")]
    public void FormatPlural_English_UsesExpectedForm(long count, string expected) =>
        Assert.Equal(expected, LocalizationResources.FormatPlural("Plural_Items", count, English));

    /// <summary>
    /// Verifies <see cref="LocalizationResources.FormatPlural"/> against the Russian CLDR rule
    /// (One/Few/Many), including the <c>11</c>/<c>21</c> exceptions.
    /// </summary>
    /// <param name="count">The cardinal count.</param>
    /// <param name="expected">The expected formatted string.</param>
    [Theory]
    [InlineData(1, "1 элемент")]
    [InlineData(2, "2 элемента")]
    [InlineData(5, "5 элементов")]
    [InlineData(11, "11 элементов")]
    [InlineData(21, "21 элемент")]
    public void FormatPlural_Russian_UsesExpectedForm(long count, string expected) =>
        Assert.Equal(expected, LocalizationResources.FormatPlural("Plural_Items", count, Russian));

    /// <summary>
    /// Verifies that extra arguments beyond the count are accepted without throwing, even when the
    /// chosen plural form (<c>Plural_Items_One</c>/en, <c>"{0} item"</c>) does not reference them.
    /// </summary>
    [Fact]
    public void FormatPlural_ExtraArgsAreThreadedThroughWithoutThrowing()
    {
        var result = LocalizationResources.FormatPlural("Plural_Items", 1, English, "ignored");

        Assert.Equal("1 item", result);
    }
}
