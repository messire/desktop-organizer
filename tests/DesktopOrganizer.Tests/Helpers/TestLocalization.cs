using System.Globalization;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Localization;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Localization fix-cycle 1 FIX-C item 4: a shared factory for the <see cref="LocalizationService"/>
/// instances a majority of non-localization-focused tests (VM/service/converter tests that merely need
/// "some" fixed-culture <see cref="ILocalizationService"/> to satisfy a constructor) used to construct by
/// hand as <c>new LocalizationService(CultureInfo.GetCultureInfo("en"))</c> — repeated verbatim across
/// more than a dozen test files. Centralizing it here also pins docs/PLANS/localization.md R-5 ("tests
/// must fix culture explicitly, never depend on the machine's culture") in one place instead of by
/// convention at each call site.
/// </summary>
/// <remarks>
/// <see cref="English"/>/<see cref="Russian"/> are properties, not fields: each access constructs a
/// FRESH <see cref="LocalizationService"/> instance. A shared singleton would be a hazard here — several
/// consumers (<c>CultureSyncTests</c>, <c>HotSwitchIntegrationTests</c>, <c>LocExtensionTests</c>) call
/// <see cref="ILocalizationService.ApplyLanguage"/> on the instance they get, which would leak mutated
/// state into every other test that happened to reuse the same singleton afterward.
/// </remarks>
internal static class TestLocalization
{
    /// <summary>
    /// A fresh <see cref="LocalizationService"/> whose system UI culture is the neutral English culture
    /// and whose language preference is "follow system" (so <see cref="ILocalizationService.CurrentCulture"/>
    /// resolves to English) — equivalent to the many call sites' own
    /// <c>new LocalizationService(CultureInfo.GetCultureInfo("en"))</c>.
    /// </summary>
    public static ILocalizationService English => Create(CultureInfo.GetCultureInfo("en"));

    /// <summary>
    /// A fresh <see cref="LocalizationService"/> whose system UI culture is the neutral Russian culture
    /// and whose language preference is "follow system" (so <see cref="ILocalizationService.CurrentCulture"/>
    /// resolves to Russian) — equivalent to the many call sites' own
    /// <c>new LocalizationService(CultureInfo.GetCultureInfo("ru"))</c>.
    /// </summary>
    public static ILocalizationService Russian => Create(CultureInfo.GetCultureInfo("ru"));

    /// <summary>
    /// Constructs a fresh <see cref="LocalizationService"/> for tests that need to control the system UI
    /// culture, the language preference, and/or a diagnostics log explicitly (e.g. to exercise
    /// <see cref="SupportedLanguages"/> resolution itself, which <see cref="English"/>/<see cref="Russian"/>
    /// are deliberately too fixed to cover).
    /// </summary>
    /// <param name="systemUiCulture">The system UI culture <see cref="LocalizationService"/> falls back to for "follow system".</param>
    /// <param name="preference">The initial language preference ("", "en", "ru"); <see langword="null"/> means "follow system".</param>
    /// <param name="diagnosticsLog">An optional diagnostics log sink; <see langword="null"/> disables diagnostics logging.</param>
    /// <returns>A new <see cref="LocalizationService"/> instance.</returns>
    public static ILocalizationService Create(
        CultureInfo systemUiCulture,
        string? preference = null,
        IDiagnosticsLog? diagnosticsLog = null)
        => new LocalizationService(systemUiCulture, preference, diagnosticsLog);
}
