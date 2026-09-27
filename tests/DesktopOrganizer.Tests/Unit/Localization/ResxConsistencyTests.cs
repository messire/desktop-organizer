using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Gate G-L3 (docs/PLANS/localization.md §6): pins the invariants that keep <c>Strings.resx</c>
/// (neutral = English) and <c>Strings.ru.resx</c> in lockstep as every localization work package adds
/// its own keys into its own region (§3.3) — a mismatch here is exactly the "silently falls back to
/// English text" failure mode L-8 exists to prevent. Reads the RESX files directly off disk (not the
/// compiled satellite resources), so this also catches a key added to only one file before either is
/// ever built.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class ResxConsistencyTests
{
    // §2.6: PascalCase tokens joined by '_', at least one underscore (every real key has an Area
    // prefix). Intentionally not "PascalCase per word" — SystemIcon_ThisPc's "ThisPc" is one token.
    private static readonly Regex KeyNamePattern = new(@"^[A-Z][A-Za-z0-9]*(_[A-Z0-9][A-Za-z0-9]*)+$", RegexOptions.Compiled);

    // Matches "{0}", "{1:dd.MM HH:mm}", etc. — capturing just the numeric index.
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    // L-8: a plural base key's four category-suffixed forms.
    private static readonly Regex PluralKeyPattern = new(@"^(?<base>.+)_(?<category>One|Few|Many|Other)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Neutral = LoadResx("Strings.resx");
    private static readonly Dictionary<string, string> Russian = LoadResx("Strings.ru.resx");

    /// <summary>
    /// Verifies that <c>Strings.resx</c> and <c>Strings.ru.resx</c> define exactly the same key set.
    /// </summary>
    [Fact]
    public void BothFiles_HaveTheSameKeySet()
    {
        var neutralOnly = Neutral.Keys.Except(Russian.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var russianOnly = Russian.Keys.Except(Neutral.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var message =
            $"Key sets differ. Only in Strings.resx: [{string.Join(", ", neutralOnly)}]. " +
            $"Only in Strings.ru.resx: [{string.Join(", ", russianOnly)}].";

        Assert.True(neutralOnly.Count == 0 && russianOnly.Count == 0, message);
    }

    /// <summary>
    /// Verifies that every key in either file matches the §2.6 naming convention
    /// (<c>Area_Sub_Element[_Part]</c>, PascalCase tokens joined by <c>_</c>).
    /// </summary>
    [Fact]
    public void AllKeys_MatchNamingConvention()
    {
        var offenders = Neutral.Keys.Concat(Russian.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !KeyNamePattern.IsMatch(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, $"Key(s) violate the §2.6 naming convention: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Verifies that no key's value has a leading or trailing whitespace character in either file (§2.6:
    /// "без ведущих и хвостовых пробелов"). A leading/trailing space is exactly the anti-pattern §2.6 asks
    /// for instead as a <c>_Format</c> key with an explicit placeholder — e.g. a badge meant to sit next
    /// to another element should get its spacing from layout (<c>Margin</c>), not from a string that
    /// silently disappears when concatenated without whitespace, or doubles up when a caller already adds
    /// its own separator.
    /// </summary>
    [Fact]
    public void AllValues_HaveNoLeadingOrTrailingWhitespace()
    {
        var offenders = new List<string>();

        foreach (var (dict, label) in new[] { (Neutral, "Strings.resx"), (Russian, "Strings.ru.resx") })
        {
            offenders.AddRange(
                dict.Where(kv => kv.Value != kv.Value.Trim())
                    .Select(kv => $"{label}:{kv.Key}=\"{kv.Value}\""));
        }

        Assert.True(offenders.Count == 0, $"Value(s) with leading/trailing whitespace (§2.6): {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Verifies that no key's value is empty in either file.
    /// </summary>
    [Fact]
    public void NoValues_AreEmpty()
    {
        var emptyInNeutral = Neutral.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToList();
        var emptyInRussian = Russian.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToList();
        var message =
            $"Empty value(s). Strings.resx: [{string.Join(", ", emptyInNeutral)}]. " +
            $"Strings.ru.resx: [{string.Join(", ", emptyInRussian)}].";

        Assert.True(emptyInNeutral.Count == 0 && emptyInRussian.Count == 0, message);
    }

    /// <summary>
    /// Verifies that every <c>_Format</c> key and every plural-category key present in both files uses
    /// the same set of numbered placeholders (<c>{0}</c>, <c>{1}</c>, …) in English and Russian.
    /// </summary>
    [Fact]
    public void FormatAndPluralKeys_HaveMatchingPlaceholderIndicesBetweenLanguages()
    {
        var offenders = new List<string>();

        foreach (var key in Neutral.Keys.Intersect(Russian.Keys, StringComparer.Ordinal))
        {
            if (!IsFormatOrPluralKey(key))
                continue;

            var neutralIndices = ExtractPlaceholderIndices(Neutral[key]);
            var russianIndices = ExtractPlaceholderIndices(Russian[key]);

            if (!neutralIndices.SetEquals(russianIndices))
                offenders.Add(key);
        }

        Assert.True(offenders.Count == 0, $"Placeholder index mismatch between en/ru: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Verifies that no key outside the <c>_Format</c>/plural-category set contains a numbered
    /// placeholder (§2.6: "у не-<c>_Format</c> ключей нет <c>{\d</c>").
    /// </summary>
    [Fact]
    public void NonFormatNonPluralKeys_HaveNoNumberedPlaceholders()
    {
        var offenders = new List<string>();

        foreach (var (dict, label) in new[] { (Neutral, "Strings.resx"), (Russian, "Strings.ru.resx") })
        {
            offenders.AddRange(
                dict.Where(kv => !IsFormatOrPluralKey(kv.Key) && PlaceholderPattern.IsMatch(kv.Value))
                    .Select(kv => $"{label}:{kv.Key}"));
        }

        Assert.True(offenders.Count == 0, $"Non-_Format/non-plural key(s) contain a numbered placeholder: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Verifies that every plural base key has all four <see cref="PluralCategory"/> forms
    /// (<c>_One</c>/<c>_Few</c>/<c>_Many</c>/<c>_Other</c>) defined in both files (L-8).
    /// </summary>
    [Fact]
    public void PluralBaseKeys_HaveAllFourCategoriesInBothFiles()
    {
        var missing = new List<string>();

        CollectMissingPluralForms(Neutral.Keys, "Strings.resx", missing);
        CollectMissingPluralForms(Russian.Keys, "Strings.ru.resx", missing);

        Assert.True(missing.Count == 0, $"Plural base key(s) missing one or more of One/Few/Many/Other: {string.Join(" | ", missing)}.");
    }

    /// <summary>
    /// Verifies that <see cref="LoadResx"/> — and therefore every other test in this class — only ever
    /// treats <c>&lt;data name="..."&gt;</c> elements as keys, never the schema's own
    /// <c>&lt;resheader name="..."&gt;</c> elements (<c>resmimetype</c>/<c>version</c>/<c>reader</c>/
    /// <c>writer</c>). A regex-based scan of <c>name="..."</c> across the whole file would wrongly count
    /// those as keys; parsing via <see cref="XDocument"/> and selecting only <c>data</c> elements does not.
    /// </summary>
    [Fact]
    public void LoadResx_DoesNotTreatResheaderElementsAsKeys()
    {
        var resheaderNames = new[] { "resmimetype", "version", "reader", "writer" };

        foreach (var resheaderName in resheaderNames)
        {
            Assert.DoesNotContain(resheaderName, Neutral.Keys);
            Assert.DoesNotContain(resheaderName, Russian.Keys);
        }
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="key"/> is a <c>_Format</c> key or one of the four
    /// plural-category-suffixed forms — the two shapes allowed to carry numbered placeholders.
    /// </summary>
    /// <param name="key">The RESX key.</param>
    private static bool IsFormatOrPluralKey(string key) =>
        key.EndsWith("_Format", StringComparison.Ordinal) || PluralKeyPattern.IsMatch(key);

    /// <summary>
    /// Extracts the set of numbered placeholder indices (<c>{0}</c>, <c>{1}</c>, …) used in <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The RESX value text.</param>
    private static HashSet<int> ExtractPlaceholderIndices(string value) =>
        PlaceholderPattern.Matches(value)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToHashSet();

    /// <summary>
    /// Appends one entry to <paramref name="missing"/> for every plural base key among
    /// <paramref name="keys"/> that is missing one or more of the four <see cref="PluralCategory"/> forms.
    /// </summary>
    /// <param name="keys">The full key set of one RESX file.</param>
    /// <param name="label">A short label identifying the file, used in the appended message.</param>
    /// <param name="missing">The list to append missing-form messages to.</param>
    private static void CollectMissingPluralForms(IEnumerable<string> keys, string label, List<string> missing)
    {
        var categoriesByBase = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var match = PluralKeyPattern.Match(key);
            if (!match.Success)
                continue;

            var baseKey = match.Groups["base"].Value;
            if (!categoriesByBase.TryGetValue(baseKey, out var categories))
            {
                categories = new HashSet<string>(StringComparer.Ordinal);
                categoriesByBase[baseKey] = categories;
            }

            categories.Add(match.Groups["category"].Value);
        }

        foreach (var (baseKey, categories) in categoriesByBase)
        {
            var missingForms = new[] { "One", "Few", "Many", "Other" }.Except(categories).ToList();
            if (missingForms.Count > 0)
                missing.Add($"{label}:{baseKey} missing [{string.Join(", ", missingForms)}]");
        }
    }

    /// <summary>
    /// Finds the repo root (<see cref="RepoRootLocator"/>) and reads the given RESX file from
    /// <c>src/DesktopOrganizer.Localization/</c>. Failing to find either is a hard failure (an
    /// exception from a static field initializer), not a silent skip — a green-but-checking-nothing
    /// version of this gate would be worse than not having it.
    /// </summary>
    private static Dictionary<string, string> LoadResx(string fileName)
    {
        var repoRoot = RepoRootLocator.Find();

        var path = Path.Combine(repoRoot, "src", "DesktopOrganizer.Localization", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"RESX file not found at '{path}'.", path);

        var document = XDocument.Load(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var data in document.Root!.Elements("data"))
        {
            var name = data.Attribute("name")?.Value;
            if (name is null)
                continue;

            result[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return result;
    }
}
