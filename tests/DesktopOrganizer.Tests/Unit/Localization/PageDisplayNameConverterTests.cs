using System.Globalization;
using DesktopOrganizer.UI.Localization;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="PageDisplayNameConverter"/> (docs/PLANS/localization.md D-5/L-10, WP-0U).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PageDisplayNameConverterTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>The <c>"default"</c> page ID is shown as its localized display name, per culture.</summary>
    [Fact]
    public void Convert_DefaultPageId_ReturnsLocalizedDisplayName()
    {
        var sut = new PageDisplayNameConverter();

        Assert.Equal("Default", sut.Convert("default", typeof(string), null, English));
        Assert.Equal("Основная", sut.Convert("default", typeof(string), null, Russian));
    }

    /// <summary>
    /// Any other page identifier — including the un-translated <c>"Page 2"</c> ID a new page gets — is
    /// shown exactly as-is, regardless of culture (D-5: page IDs are data, not translated).
    /// </summary>
    /// <param name="pageId">A page identifier other than <c>"default"</c>.</param>
    [Theory]
    [InlineData("Page 2")]
    [InlineData("My renamed page")]
    public void Convert_NonDefaultPageId_ReturnsIdUnchanged(string pageId)
    {
        var sut = new PageDisplayNameConverter();

        Assert.Equal(pageId, sut.Convert(pageId, typeof(string), null, English));
        Assert.Equal(pageId, sut.Convert(pageId, typeof(string), null, Russian));
    }

    /// <summary>A non-<see cref="string"/> value (including <see langword="null"/>) returns <see cref="string.Empty"/>.</summary>
    /// <param name="value">The non-<see cref="string"/> (or <see langword="null"/>) value to convert.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public void Convert_NonStringValue_ReturnsEmpty(object? value)
    {
        var sut = new PageDisplayNameConverter();

        Assert.Equal(string.Empty, sut.Convert(value!, typeof(string), null, English));
    }

    /// <summary><see cref="PageDisplayNameConverter.ConvertBack"/> is one-way only.</summary>
    [Fact]
    public void ConvertBack_Throws()
    {
        var sut = new PageDisplayNameConverter();

        Assert.Throws<NotSupportedException>(() => sut.ConvertBack("default", typeof(string), null, English));
    }
}
