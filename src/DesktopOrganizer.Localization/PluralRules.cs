using System.Globalization;

namespace DesktopOrganizer.Localization;

/// <summary>
/// CLDR cardinal plural rules for the two languages this app supports (docs/PLANS/localization.md §2.1/L-8).
/// Pure and never throws.
/// </summary>
public static class PluralRules
{
    /// <summary>
    /// Selects the <see cref="PluralCategory"/> for <paramref name="n"/> under <paramref name="culture"/>.
    /// Negative values are categorized by their absolute value (CLDR convention: plural category depends
    /// on magnitude, not sign).
    /// </summary>
    /// <param name="culture">
    /// The culture whose plural rule applies. Only <c>ru</c> (by <see cref="CultureInfo.TwoLetterISOLanguageName"/>,
    /// case-insensitive) uses the four-category Russian rule below; every other culture — including
    /// <c>en</c> — uses the two-category English-style rule.
    /// </param>
    /// <param name="n">The cardinal count being formatted.</param>
    /// <returns>The plural category to use for the corresponding <c>_One</c>/<c>_Few</c>/<c>_Many</c>/<c>_Other</c> RESX key.</returns>
    public static PluralCategory Select(CultureInfo culture, long n)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var abs = n < 0 ? -n : n;

        return string.Equals(culture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase)
            ? SelectRussian(abs)
            : SelectDefault(abs);
    }

    /// <summary>
    /// English (and every non-Russian culture) CLDR rule: only <c>1</c> is singular, everything else —
    /// including <c>0</c> — is <see cref="PluralCategory.Other"/>.
    /// </summary>
    private static PluralCategory SelectDefault(long abs) => abs == 1 ? PluralCategory.One : PluralCategory.Other;

    /// <summary>
    /// Russian CLDR rule: <c>n % 10 == 1 &amp;&amp; n % 100 != 11</c> → <see cref="PluralCategory.One"/>;
    /// <c>n % 10</c> in <c>2..4</c> and <c>n % 100</c> not in <c>12..14</c> → <see cref="PluralCategory.Few"/>;
    /// everything else (including <c>0</c> and every <c>*11..*14</c>) → <see cref="PluralCategory.Many"/>.
    /// <see cref="PluralCategory.Other"/> is never returned for Russian — <c>Strings.ru.resx</c> mirrors
    /// its <c>_Other</c> value from <c>_Many</c> purely so both RESX files share the same key set (L-8).
    /// </summary>
    private static PluralCategory SelectRussian(long abs)
    {
        var mod10 = abs % 10;
        var mod100 = abs % 100;

        if (mod10 == 1 && mod100 != 11)
            return PluralCategory.One;

        if (mod10 is >= 2 and <= 4 && mod100 is not(>= 12 and <= 14))
            return PluralCategory.Few;

        return PluralCategory.Many;
    }
}
