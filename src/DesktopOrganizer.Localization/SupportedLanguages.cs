using System.Globalization;

namespace DesktopOrganizer.Localization;

/// <summary>
/// The fixed set of UI languages this app supports (docs/PLANS/localization.md D-4, §2.1) and the
/// resolution rule from a raw <c>IAppSettings.Language</c>-style preference string (that type lives in
/// <c>DesktopOrganizer.UI</c>, above this dependency-free project — see L-1) to an effective, neutral
/// <see cref="CultureInfo"/>. Pure, thread-safe, never throws.
/// </summary>
public static class SupportedLanguages
{
    /// <summary>
    /// The <c>IAppSettings.Language</c> value meaning "follow the Windows UI language" (D-1's default,
    /// empty string).
    /// </summary>
    public const string FollowSystem = "";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Every supported UI culture, in picker display order: English first, then Russian.
    /// </summary>
    public static IReadOnlyList<CultureInfo> All { get; } = new[] { English, Russian };

    /// <summary>
    /// <see langword="true"/> when <paramref name="tag"/> is <c>"en"</c> or <c>"ru"</c>
    /// (case-insensitive). <see langword="false"/> for <see langword="null"/>, empty, or any other value —
    /// including <see cref="FollowSystem"/> itself, which is a preference, not a supported language tag.
    /// </summary>
    /// <param name="tag">The candidate two-letter language tag.</param>
    public static bool IsSupported(string? tag) =>
        tag is not null &&
        (string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(tag, "ru", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a raw language preference to the effective, neutral UI culture. Never throws.
    /// </summary>
    /// <param name="preference">
    /// The raw <c>IAppSettings.Language</c> value: <c>"en"</c>/<c>"ru"</c>
    /// (case-insensitive) pick that language directly. Anything else — <see langword="null"/>,
    /// <see cref="FollowSystem"/>, or an unrecognised tag such as <c>"de"</c> — falls back to
    /// <paramref name="systemUiCulture"/>.
    /// </param>
    /// <param name="systemUiCulture">
    /// The Windows UI culture to follow when <paramref name="preference"/> does not name a supported
    /// language directly (typically <see cref="SystemCulture.OriginalUiCulture"/>).
    /// </param>
    /// <returns>
    /// <see cref="English"/> ("en") or <see cref="Russian"/> ("ru") — always one of <see cref="All"/>.
    /// </returns>
    public static CultureInfo Resolve(string? preference, CultureInfo systemUiCulture)
    {
        if (string.Equals(preference, "en", StringComparison.OrdinalIgnoreCase))
            return English;

        if (string.Equals(preference, "ru", StringComparison.OrdinalIgnoreCase))
            return Russian;

        return string.Equals(systemUiCulture?.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase)
            ? Russian
            : English;
    }
}
