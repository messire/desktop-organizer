using System.Globalization;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="LanguageOption"/> (docs/PLANS/localization.md §2.9 contract C-5) — direct
/// coverage of the model itself, complementing <c>MainWindowViewModelTests</c>' end-to-end coverage of the
/// picker it backs (docs/PLANS/localization.md §4.8, WP-6 item 6).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LanguageOptionTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// <c>Tag == "en"</c> resolves to <c>Language_Name_En</c> ("English") regardless of the
    /// <see cref="ILocalizationService"/>'s own current culture — <c>Language_Name_En</c>/
    /// <c>Language_Name_Ru</c> are identical text in both RESX files by design (docs/PLANS/localization.md §3.2).
    /// </summary>
    /// <param name="servicePreference">The backing <see cref="ILocalizationService"/>'s own current-culture preference — must not matter here.</param>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Ctor_EnglishTag_ResolvesToLanguageNameEn(string servicePreference)
    {
        var localization = new LocalizationService(English, servicePreference);

        var option = new LanguageOption("en", localization, English);

        Assert.Equal("en", option.Tag);
        Assert.Equal("English", option.DisplayName);
    }

    /// <summary><c>Tag == "ru"</c> resolves to <c>Language_Name_Ru</c> ("Русский"), same reasoning as above.</summary>
    /// <param name="servicePreference">The backing <see cref="ILocalizationService"/>'s own current-culture preference — must not matter here.</param>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Ctor_RussianTag_ResolvesToLanguageNameRu(string servicePreference)
    {
        var localization = new LocalizationService(English, servicePreference);

        var option = new LanguageOption("ru", localization, English);

        Assert.Equal("ru", option.Tag);
        Assert.Equal("Русский", option.DisplayName);
    }

    /// <summary>
    /// Fix cycle 1 MINOR-2: <c>Tag == ""</c> ("follow Windows") names whichever language
    /// <paramref name="systemCultureTag"/> resolves to, in that language's own self-name — driven by the
    /// <see cref="CultureInfo"/> passed explicitly to the constructor, not by
    /// <see cref="SystemCulture.OriginalUiCulture"/> (the actual test-runner machine's own OS language).
    /// This is what makes both the "ru" and "en" branches deterministically verifiable on any machine —
    /// the previous version of this test only ever exercised whichever branch happened to match the
    /// author's own machine.
    /// </summary>
    /// <param name="systemCultureTag">The <see cref="CultureInfo"/> tag passed as the constructor's <c>systemUiCulture</c>.</param>
    /// <param name="expectedNativeName">The expected embedded native language name for <paramref name="systemCultureTag"/>.</param>
    [Theory]
    [InlineData("en", "English")]
    [InlineData("ru", "Русский")]
    public void Ctor_EmptyTag_NamesTheGivenSystemUiCultureNatively(string systemCultureTag, string expectedNativeName)
    {
        var systemUiCulture = CultureInfo.GetCultureInfo(systemCultureTag);
        var localization = new LocalizationService(English, "en");

        var option = new LanguageOption(string.Empty, localization, systemUiCulture);

        Assert.Equal(string.Empty, option.Tag);
        Assert.Equal(localization.Format("Language_FollowWindows_Format", expectedNativeName), option.DisplayName);
    }

    /// <summary>
    /// Companion to <see cref="Ctor_EmptyTag_NamesTheGivenSystemUiCultureNatively"/>: the wrapping phrase
    /// itself is localized to the constructor's <c>localization</c> argument's CURRENT culture,
    /// independently of which language the embedded native name (driven by <c>systemUiCulture</c>) refers
    /// to — e.g. a Russian UI naming an English "follow Windows" system.
    /// </summary>
    [Fact]
    public void Ctor_EmptyTag_WrappingPhraseUsesLocalizationCurrentCulture_IndependentOfSystemUiCulture()
    {
        var localization = new LocalizationService(English, "ru");

        var option = new LanguageOption(string.Empty, localization, English);

        Assert.Equal("Как в Windows (English)", option.DisplayName);
    }

    /// <summary>
    /// <see cref="LanguageOption.Refresh"/> re-resolves <see cref="LanguageOption.DisplayName"/> for a
    /// DIFFERENT <see cref="ILocalizationService"/> instance's current culture and raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> exactly once for
    /// <c>DisplayName</c> — mirroring how <c>MainWindowViewModel</c> calls it for every live option on
    /// <see cref="ILocalizationService.CultureChanged"/>. <c>systemUiCulture</c> (English, fixed at
    /// construction) does not change across <see cref="LanguageOption.Refresh"/> calls — only the
    /// <see cref="ILocalizationService"/> instance passed to <c>Refresh</c> drives the wrapping phrase's
    /// language here.
    /// </summary>
    [Fact]
    public void Refresh_DifferentDisplayName_UpdatesValueAndRaisesPropertyChangedOnce()
    {
        var option = new LanguageOption(string.Empty, new LocalizationService(English), English);
        var raisedFor = new List<string>();
        option.PropertyChanged += (_, args) => raisedFor.Add(args.PropertyName!);
        var englishDisplayName = option.DisplayName;

        var russianLocalization = new LocalizationService(Russian, "ru");
        option.Refresh(russianLocalization);

        Assert.NotEqual(englishDisplayName, option.DisplayName);
        Assert.Equal("Как в Windows (English)", option.DisplayName);
        Assert.Equal(new[] { nameof(LanguageOption.DisplayName) }, raisedFor);
    }

    /// <summary>
    /// <see cref="LanguageOption.Refresh"/> is a no-op — no
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> at all — when the
    /// re-resolved text is unchanged (here: <c>Tag == "en"</c>, whose <c>DisplayName</c> never depends on
    /// the localization instance's own current culture at all).
    /// </summary>
    [Fact]
    public void Refresh_SameDisplayName_RaisesNoPropertyChanged()
    {
        var option = new LanguageOption("en", new LocalizationService(English), English);
        var raised = false;
        option.PropertyChanged += (_, _) => raised = true;

        option.Refresh(new LocalizationService(Russian, "ru"));

        Assert.False(raised);
        Assert.Equal("English", option.DisplayName);
    }
}
