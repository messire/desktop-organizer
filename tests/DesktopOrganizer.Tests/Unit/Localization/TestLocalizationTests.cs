using System.Globalization;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Localization fix-cycle 1 FIX-C item 4: pins <see cref="TestLocalization"/>'s two behaviors every
/// caller relies on implicitly — the resolved culture, and that <see cref="TestLocalization.English"/>/
/// <see cref="TestLocalization.Russian"/> hand back a FRESH instance every time (§ this type's own
/// remarks: several tests mutate the instance they get via <c>ApplyLanguage</c>, so a shared singleton
/// would leak state between tests).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TestLocalizationTests
{
    /// <summary>Verifies that <see cref="TestLocalization.English"/> resolves to the neutral English culture.</summary>
    [Fact]
    public void English_ResolvesToEnglishCulture()
    {
        Assert.Equal(CultureInfo.GetCultureInfo("en"), TestLocalization.English.CurrentCulture);
    }

    /// <summary>Verifies that <see cref="TestLocalization.Russian"/> resolves to the neutral Russian culture.</summary>
    [Fact]
    public void Russian_ResolvesToRussianCulture()
    {
        Assert.Equal(CultureInfo.GetCultureInfo("ru"), TestLocalization.Russian.CurrentCulture);
    }

    /// <summary>Verifies that <see cref="TestLocalization.English"/> never hands back a shared singleton.</summary>
    [Fact]
    public void English_ReturnsANewInstanceOnEachAccess()
    {
        var first = TestLocalization.English;
        var second = TestLocalization.English;

        Assert.NotSame(first, second);
    }

    /// <summary>Verifies that <see cref="TestLocalization.Create"/> honors an explicit preference override.</summary>
    [Fact]
    public void Create_WithExplicitPreference_OverridesFollowSystem()
    {
        var sut = TestLocalization.Create(CultureInfo.GetCultureInfo("en"), "ru");

        Assert.Equal(CultureInfo.GetCultureInfo("ru"), sut.CurrentCulture);
        Assert.Equal("ru", sut.LanguagePreference);
    }
}
