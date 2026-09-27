using System.Globalization;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="LocKeyConverter"/> (docs/PLANS/localization.md §2.4, WP-0U).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LocKeyConverterTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>Verifies that a known RESX key resolves for the given culture.</summary>
    [Fact]
    public void Convert_KnownKey_ReturnsLocalizedStringForCulture()
    {
        var sut = new LocKeyConverter();

        Assert.Equal("OK", sut.Convert("Common_Ok_Button", typeof(string), null, English));
        Assert.Equal("ОК", sut.Convert("Common_Ok_Button", typeof(string), null, Russian));
    }

    /// <summary>A missing key returns the key itself (never throws) — <c>LocalizationResources.GetString</c>'s own contract.</summary>
    [Fact]
    public void Convert_UnknownKey_ReturnsKeyItself()
    {
        var sut = new LocKeyConverter();

        Assert.Equal("No_Such_Key", sut.Convert("No_Such_Key", typeof(string), null, English));
    }

    /// <summary>A non-<see cref="string"/> value (including <see langword="null"/>) returns <see cref="string.Empty"/>.</summary>
    /// <param name="value">The non-<see cref="string"/> (or <see langword="null"/>) value to convert.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public void Convert_NonStringValue_ReturnsEmpty(object? value)
    {
        var sut = new LocKeyConverter();

        Assert.Equal(string.Empty, sut.Convert(value!, typeof(string), null, English));
    }

    /// <summary><see cref="LocKeyConverter.ConvertBack"/> is one-way only.</summary>
    [Fact]
    public void ConvertBack_Throws()
    {
        var sut = new LocKeyConverter();

        Assert.Throws<NotSupportedException>(() => sut.ConvertBack("OK", typeof(string), null, English));
    }
}
