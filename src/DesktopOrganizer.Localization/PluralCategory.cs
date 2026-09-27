namespace DesktopOrganizer.Localization;

/// <summary>
/// CLDR plural category for a cardinal number, used to pick which <c>_One</c>/<c>_Few</c>/<c>_Many</c>/
/// <c>_Other</c> suffixed RESX key applies to a given count (docs/PLANS/localization.md L-8). Every
/// plural base key must define all four forms in both <c>Strings.resx</c> and <c>Strings.ru.resx</c> —
/// English collapses <see cref="Few"/> and <see cref="Many"/> onto the same text as <see cref="Other"/>,
/// Russian collapses <see cref="Other"/> onto the same text as <see cref="Many"/> — so the two RESX files
/// always carry the exact same key set (a simple, testable invariant; see <c>ResxConsistencyTests</c>).
/// </summary>
public enum PluralCategory
{
    /// <summary>Singular form (e.g. Russian "1 элемент", English "1 item").</summary>
    One,

    /// <summary>CLDR "few" form (Russian 2–4, excluding 12–14; unused by English, which maps to <see cref="Other"/>).</summary>
    Few,

    /// <summary>CLDR "many" form (Russian 5–20, 0, and any count ending 5–9 or 11–14; unused by English, which maps to <see cref="Other"/>).</summary>
    Many,

    /// <summary>Fallback/plural form for every count not covered by the categories above.</summary>
    Other,
}
