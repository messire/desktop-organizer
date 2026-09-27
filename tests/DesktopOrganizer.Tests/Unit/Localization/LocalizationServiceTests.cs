using System.Globalization;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Localization;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Unit tests for <see cref="LocalizationService"/> (docs/PLANS/localization.md §2.2, WP-0S DoD 1/5).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class LocalizationServiceTests
{
    private static readonly CultureInfo EnglishSystem = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo RussianSystem = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Verifies that the parameterless-preference constructor defaults to "follow system" and resolves
    /// immediately against the given system culture.
    /// </summary>
    [Fact]
    public void Ctor_DefaultsToFollowSystem()
    {
        var sut = new LocalizationService(RussianSystem);

        Assert.Equal(SupportedLanguages.FollowSystem, sut.LanguagePreference);
        Assert.Equal("ru", sut.CurrentCulture.Name);
    }

    /// <summary>
    /// Verifies that an explicit initial preference resolves immediately, regardless of the system culture.
    /// </summary>
    [Fact]
    public void Ctor_WithExplicitPreference_ResolvesImmediately()
    {
        var sut = new LocalizationService(RussianSystem, "en");

        Assert.Equal("en", sut.LanguagePreference);
        Assert.Equal("en", sut.CurrentCulture.Name);
    }

    /// <summary>
    /// Fix cycle 1 NIT: a hand-edited <c>user.config</c> value like <c>"RU"</c> normalizes to the
    /// canonical lower-case <c>"ru"</c> — so exact-case comparisons against <see cref="ILocalizationService.LanguagePreference"/>
    /// elsewhere (e.g. the Settings language picker matching it against its own lower-case option tags)
    /// agree with the effective culture actually resolved here.
    /// </summary>
    [Fact]
    public void Ctor_UppercasePreference_NormalizesToLowerCase()
    {
        var sut = new LocalizationService(EnglishSystem, "RU");

        Assert.Equal("ru", sut.LanguagePreference);
        Assert.Equal("ru", sut.CurrentCulture.Name);
    }

    /// <summary>
    /// Verifies that the string indexer (the XAML binding path <c>"[Key]"</c> uses) returns the
    /// localized string for <see cref="ILocalizationService.CurrentCulture"/>.
    /// </summary>
    [Fact]
    public void Indexer_ReturnsLocalizedString()
    {
        var sut = new LocalizationService(EnglishSystem, "ru");

        Assert.Equal("ОК", sut["Common_Ok_Button"]);
    }

    /// <summary>
    /// Verifies the fixed event order (docs/PLANS/localization.md §2.2) when
    /// <see cref="LocalizationService.ApplyLanguage"/> changes the effective culture:
    /// <c>PropertyChanged("LanguagePreference")</c>, then <c>PropertyChanged("CurrentCulture")</c>, then
    /// <c>PropertyChanged("Item[]")</c>, then <see cref="ILocalizationService.CultureChanged"/> last.
    /// </summary>
    [Fact]
    public void ApplyLanguage_EffectiveCultureChanges_RaisesEventsInFixedOrder()
    {
        var sut = new LocalizationService(EnglishSystem, "en");
        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        var cultureChangedRaised = false;
        sut.CultureChanged += (_, args) =>
        {
            cultureChangedRaised = true;
            Assert.Equal("en", args.OldCulture.Name);
            Assert.Equal("ru", args.NewCulture.Name);

            // CultureChanged must fire only after all three PropertyChanged notifications below it
            // (docs/PLANS/localization.md §2.2's fixed order) have already happened.
            Assert.Equal(new[] { "LanguagePreference", "CurrentCulture", "Item[]" }, raised);
        };

        var result = sut.ApplyLanguage("ru");

        Assert.Equal("ru", result.Name);
        Assert.True(cultureChangedRaised);
        Assert.Equal(new[] { "LanguagePreference", "CurrentCulture", "Item[]" }, raised);
    }

    /// <summary>
    /// Verifies that when the preference changes but the effective culture does not (e.g. "" -&gt; "ru"
    /// on an already-Russian system), only <c>PropertyChanged("LanguagePreference")</c> fires — no
    /// <c>CurrentCulture</c>/<c>Item[]</c> notification and no <see cref="ILocalizationService.CultureChanged"/>.
    /// </summary>
    [Fact]
    public void ApplyLanguage_PreferenceChangesButEffectiveCultureSame_OnlyRaisesLanguagePreference()
    {
        // On a Russian system, "" (follow Windows) and "ru" both resolve to the same effective culture.
        var sut = new LocalizationService(RussianSystem, "ru");
        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        var cultureChangedRaised = false;
        sut.CultureChanged += (_, _) => cultureChangedRaised = true;

        var result = sut.ApplyLanguage(SupportedLanguages.FollowSystem);

        Assert.Equal("ru", result.Name);
        Assert.Equal(new[] { "LanguagePreference" }, raised);
        Assert.False(cultureChangedRaised);
    }

    /// <summary>
    /// Fix cycle 1 NIT: <see cref="LocalizationService.ApplyLanguage"/> normalizes a supported tag's
    /// casing before storing it, so re-applying the same tag in a different case (e.g. <c>"RU"</c> after
    /// already being on the normalized <c>"ru"</c>) is treated as the exact-same-preference no-op — not
    /// a fresh, would-be-mismatched preference.
    /// </summary>
    [Fact]
    public void ApplyLanguage_UppercasePreference_NormalizesToLowerCaseAndIsIdempotentWithLowerCase()
    {
        var sut = new LocalizationService(EnglishSystem, "en");
        var cultureChangedCount = 0;
        sut.CultureChanged += (_, _) => cultureChangedCount++;

        sut.ApplyLanguage("RU");
        Assert.Equal("ru", sut.LanguagePreference);
        Assert.Equal(1, cultureChangedCount);

        // Re-applying the already-current preference in lower case must be recognized as unchanged.
        sut.ApplyLanguage("ru");
        Assert.Equal(1, cultureChangedCount);
    }

    /// <summary>
    /// Verifies that re-applying the exact same preference raises no events at all (per
    /// <see cref="ILocalizationService.CultureChanged"/>'s documented contract).
    /// </summary>
    [Fact]
    public void ApplyLanguage_SamePreferenceAgain_RaisesNoEvents()
    {
        var sut = new LocalizationService(EnglishSystem, "en");
        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        var cultureChangedRaised = false;
        sut.CultureChanged += (_, _) => cultureChangedRaised = true;

        sut.ApplyLanguage("en");

        Assert.Empty(raised);
        Assert.False(cultureChangedRaised);
    }

    /// <summary>
    /// Verifies that <see cref="LocalizationService.ApplyLanguage"/> never touches
    /// <see cref="CultureInfo.CurrentUICulture"/>/<see cref="CultureInfo.DefaultThreadCurrentUICulture"/>
    /// (L-3/L-6: that is <c>CultureSync</c>'s job, not this service's).
    /// </summary>
    [Fact]
    public void ApplyLanguage_DoesNotTouchThreadCulture()
    {
        var originalCurrentUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultThreadCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = EnglishSystem;
            CultureInfo.DefaultThreadCurrentUICulture = EnglishSystem;

            var sut = new LocalizationService(EnglishSystem, "en");
            sut.ApplyLanguage("ru");

            Assert.Equal(EnglishSystem, CultureInfo.CurrentUICulture);
            Assert.Equal(EnglishSystem, CultureInfo.DefaultThreadCurrentUICulture);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCurrentUiCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultThreadCulture;
        }
    }

    /// <summary>
    /// Verifies that <see cref="LocalizationService.Format"/> formats using
    /// <see cref="ILocalizationService.CurrentCulture"/>.
    /// </summary>
    [Fact]
    public void Format_UsesCurrentCulture()
    {
        var sut = new LocalizationService(EnglishSystem, "ru");

        var result = sut.Format("Language_FollowWindows_Format", "Русский");

        Assert.Equal("Как в Windows (Русский)", result);
    }

    /// <summary>
    /// Verifies that <see cref="LocalizationService.FormatPlural"/> selects the plural category using
    /// <see cref="ILocalizationService.CurrentCulture"/>.
    /// </summary>
    [Fact]
    public void FormatPlural_UsesCurrentCulture()
    {
        var sut = new LocalizationService(EnglishSystem, "ru");

        Assert.Equal("2 элемента", sut.FormatPlural("Plural_Items", 2));
    }

    /// <summary>
    /// Verifies that <see cref="ILocalizationService.SupportedCultures"/> matches
    /// <see cref="SupportedLanguages.All"/> exactly.
    /// </summary>
    [Fact]
    public void SupportedCultures_MatchesSupportedLanguagesAll()
    {
        var sut = new LocalizationService(EnglishSystem);

        Assert.Equal(SupportedLanguages.All, sut.SupportedCultures);
    }

    /// <summary>
    /// Fix cycle 1 MINOR-1: a <see cref="ILocalizationService.CultureChanged"/> subscriber that throws
    /// must not stop later subscribers from being invoked, and must not propagate out of
    /// <see cref="LocalizationService.ApplyLanguage"/> itself. No <see cref="IDiagnosticsLog"/> is
    /// supplied here, exercising the <see cref="System.Diagnostics.Debug.WriteLine(string)"/> fallback
    /// path.
    /// </summary>
    [Fact]
    public void ApplyLanguage_SubscriberThrows_OtherSubscribersStillInvokedAndNoExceptionEscapes()
    {
        var sut = new LocalizationService(EnglishSystem, "en");
        var secondSubscriberInvoked = false;
        sut.CultureChanged += (_, _) => throw new InvalidOperationException("boom");
        sut.CultureChanged += (_, _) => secondSubscriberInvoked = true;

        var result = sut.ApplyLanguage("ru");

        Assert.Equal("ru", result.Name);
        Assert.True(secondSubscriberInvoked);
    }

    /// <summary>
    /// Fix cycle 1 MINOR-1: when an <see cref="IDiagnosticsLog"/> is supplied and enabled, a
    /// <see cref="ILocalizationService.CultureChanged"/> subscriber's exception is recorded through it
    /// (ADR-027) rather than the <see cref="System.Diagnostics.Debug.WriteLine(string)"/> fallback —
    /// privacy-safe: only the exception's type name is ever written, never its message.
    /// </summary>
    [Fact]
    public void ApplyLanguage_SubscriberThrows_WritesToEnabledDiagnosticsLog()
    {
        var diagnosticsLog = new Mock<IDiagnosticsLog>();
        diagnosticsLog.SetupGet(d => d.IsEnabled).Returns(true);
        var sut = new LocalizationService(EnglishSystem, "en", diagnosticsLog.Object);
        sut.CultureChanged += (_, _) => throw new InvalidOperationException("boom");

        sut.ApplyLanguage("ru");

        diagnosticsLog.Verify(
            d => d.Write(
                "localization",
                "CultureChangedSubscriberFailed",
                null,
                It.Is<KeyValuePair<string, object?>[]>(payload =>
                    payload.Length == 1 &&
                    payload[0].Key == "exceptionType" &&
                    Equals(payload[0].Value, nameof(InvalidOperationException)))),
            Times.Once);
    }

    /// <summary>
    /// Fix cycle 1 MINOR-1: when the supplied <see cref="IDiagnosticsLog"/> is disabled
    /// (<see cref="IDiagnosticsLog.IsEnabled"/> <see langword="false"/> — the default in Release
    /// builds), a subscriber's exception falls back to
    /// <see cref="System.Diagnostics.Debug.WriteLine(string)"/> rather than calling
    /// <see cref="IDiagnosticsLog.Write"/> at all.
    /// </summary>
    [Fact]
    public void ApplyLanguage_SubscriberThrows_DisabledDiagnosticsLogNeverWritten()
    {
        var diagnosticsLog = new Mock<IDiagnosticsLog>();
        diagnosticsLog.SetupGet(d => d.IsEnabled).Returns(false);
        var sut = new LocalizationService(EnglishSystem, "en", diagnosticsLog.Object);
        sut.CultureChanged += (_, _) => throw new InvalidOperationException("boom");

        sut.ApplyLanguage("ru");

        diagnosticsLog.Verify(
            d => d.Write(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Never);
    }
}
