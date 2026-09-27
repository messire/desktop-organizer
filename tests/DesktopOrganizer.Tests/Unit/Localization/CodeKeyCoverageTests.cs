using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Gate G-L2b (docs/PLANS/localization.md §6/§4.8, WP-6): every RESX key referenced from C# as a literal
/// string — <c>GetString("Key")</c>, <c>Format("Key", ...)</c>, <c>FormatPlural("BaseKey", ...)</c>,
/// <c>LocalizationResources.GetString/Format/FormatPlural(...)</c>, and the
/// <c>ILocalizationService</c> indexer (<c>_localization["Key"]</c>/<c>localization["Key"]</c>/
/// <c>LocalizationSource.Current["Key"]</c>) — must actually exist in <c>Strings.resx</c>. A
/// <c>FormatPlural</c> base key is checked differently: the literal argument itself is never a RESX key
/// (only its four <see cref="DesktopOrganizer.Localization.PluralCategory"/>-suffixed forms are, per L-8),
/// so this gate instead requires all four <c>_One</c>/<c>_Few</c>/<c>_Many</c>/<c>_Other</c> forms exist.
/// </summary>
/// <remarks>
/// Reads C# source files under <c>src/</c> directly off disk (<see cref="RepoRootLocator"/>, excluding
/// <c>bin</c>/<c>obj</c>), not compiled assemblies. Only candidate strings matching the §2.6 RESX naming
/// convention (<c>Area_Sub_Element[_Part]</c>, PascalCase tokens joined by <c>_</c>) are treated as RESX
/// key references at all — this is what keeps the indexer scan from false-positivating on the many OTHER
/// string-indexed things this codebase has (JSON DTO property names, <c>Properties.Settings.Default</c>'s
/// own indexer, dictionary lookups): none of those happen to also be PascalCase-with-underscore, so the
/// naming-convention filter alone removes essentially all of that noise without needing a broader
/// call-site allowlist. <c>Enum_*</c> and <c>SystemIcon_*</c> keys are built dynamically
/// (<c>$"Enum_{type}_{member}"</c>, <c>$"SystemIcon_{kind}"</c>) and never appear as a plain string
/// literal argument, so they are simply never candidates here — see <c>XamlKeyCoverageTests</c>' remarks
/// for where those are actually covered (R-15).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class CodeKeyCoverageTests
{
    // §2.6: PascalCase tokens joined by '_', at least one underscore — matches ResxConsistencyTests'
    // own KeyNamePattern. Filters call-site noise (JSON property names, unrelated string.Format calls,
    // etc.) down to things that are plausibly actual RESX key references.
    private static readonly Regex KeyNamePattern = new(@"^[A-Z][A-Za-z0-9]*(_[A-Z0-9][A-Za-z0-9]*)+$", RegexOptions.Compiled);

    private static readonly Regex DirectKeyCallPattern = new(
        @"\b(?:GetString|Format(?!Plural))\(\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex PluralBaseKeyCallPattern = new(
        @"\bFormatPlural\(\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex IndexerPattern = new(
        @"(?:_localization|localization|Localization|LocalizationSource\.Current)\s*\[\s*""([A-Za-z0-9_]+)""\s*\]",
        RegexOptions.Compiled);

    private static readonly string[] PluralCategories = { "One", "Few", "Many", "Other" };

    /// <summary>
    /// Every literal <c>GetString(...)</c>/<c>Format(...)</c>/indexer key reference under <c>src/</c>
    /// that matches the §2.6 naming convention must exist in <c>Strings.resx</c>.
    /// </summary>
    [Fact]
    public void EveryDirectKeyReference_ExistsInNeutralResx()
    {
        var repoRoot = RepoRootLocator.Find();
        var resxKeys = LoadResxKeyNames(repoRoot);
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var text = File.ReadAllText(file);

            foreach (var (key, index) in EnumerateMatches(text, DirectKeyCallPattern).Concat(EnumerateMatches(text, IndexerPattern)))
            {
                if (!KeyNamePattern.IsMatch(key)) continue;
                if (resxKeys.Contains(key)) continue;

                offenders.Add(FormatOffender(repoRoot, file, text, index, key));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "C# reference(s) to a RESX key missing from Strings.resx (G-L2b):\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// Every <c>FormatPlural("BaseKey", ...)</c> call's base key must have all four
    /// <c>_One</c>/<c>_Few</c>/<c>_Many</c>/<c>_Other</c> forms in <c>Strings.resx</c> (L-8).
    /// </summary>
    [Fact]
    public void EveryFormatPluralBaseKey_HasAllFourCategoryFormsInNeutralResx()
    {
        var repoRoot = RepoRootLocator.Find();
        var resxKeys = LoadResxKeyNames(repoRoot);
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var text = File.ReadAllText(file);

            foreach (var (baseKey, index) in EnumerateMatches(text, PluralBaseKeyCallPattern))
            {
                if (!KeyNamePattern.IsMatch(baseKey)) continue;

                var missing = PluralCategories.Where(category => !resxKeys.Contains($"{baseKey}_{category}")).ToList();
                if (missing.Count == 0) continue;

                offenders.Add(FormatOffender(repoRoot, file, text, index, $"{baseKey} missing [{string.Join(", ", missing)}]"));
            }
        }

        var message = "FormatPlural base key(s) missing one or more category forms in Strings.resx (G-L2b/L-8):\n" +
            string.Join('\n', offenders);
        Assert.True(offenders.Count == 0, message);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Key, int Index)> EnumerateMatches(string text, Regex pattern) =>
        pattern.Matches(text).Select(m => (m.Groups[1].Value, m.Index));

    private static string FormatOffender(string repoRoot, string file, string text, int index, string detail)
    {
        var line = 1 + text[..index].Count(c => c == '\n');
        var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
        return $"{relativePath}:{line}: {detail}";
    }

    private static HashSet<string> LoadResxKeyNames(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "src", "DesktopOrganizer.Localization", "Strings.resx");
        var document = XDocument.Load(path);
        return document.Root!.Elements("data")
            .Select(data => data.Attribute("name")?.Value)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
