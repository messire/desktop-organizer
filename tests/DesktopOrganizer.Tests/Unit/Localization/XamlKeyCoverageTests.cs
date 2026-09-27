using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Gate G-L2a (docs/PLANS/localization.md §6/§4.8, WP-6): every RESX key referenced from XAML —
/// <c>{loc:Loc Key}</c> and <c>FormatKey=Key</c> — must actually exist in <c>Strings.resx</c> (the
/// neutral/English file; <c>ResxConsistencyTests.BothFiles_HaveTheSameKeySet</c> already guarantees
/// <c>Strings.ru.resx</c> has the same key set). A key typo or a key deleted from RESX while a
/// <c>{loc:Loc}</c>/<c>FormatKey</c> reference to it survives would otherwise only surface at runtime as
/// the visible-but-wrong placeholder text <c>LocalizationResources.GetString</c> falls back to (the key
/// itself) — this gate catches it at test time instead.
/// </summary>
/// <remarks>
/// Reads XAML source files directly off disk (<see cref="RepoRootLocator"/>), not the compiled assembly.
/// <c>{loc:CultureBinding}</c> takes no key and is not scanned. Dynamically built keys
/// (<c>Enum_{Type}_{Member}</c> via <c>EnumDisplayNameConverter</c>, <c>SystemIcon_{Kind}</c> via
/// <c>SystemIconsPanelManager</c>) never appear as a literal <c>{loc:Loc ...}</c>/<c>FormatKey=...</c> token
/// in XAML — they are supplied as a <c>Converter</c>'s runtime value, not a static key — so this gate
/// cannot and does not need to cover them; they are covered by their own enumerating unit tests instead
/// (<c>EnumDisplayNameConverterTests</c> exercises all ten seeded enums' every member,
/// <c>SystemIconsPanelManagerTests</c>/<c>SystemIconsPanelModelTests</c> exercise every
/// <c>SystemIconKind</c> — docs/PLANS/localization.md R-15).
/// <para>
/// The reverse direction — every RESX key must be USED somewhere, not just every reference must
/// RESOLVE — used to be an informational, non-failing report here
/// (<c>UnusedResxKeys_ReportedAsWarningNotFailure</c>). It is now a hard gate covering XAML, C#, and the
/// <c>Enum_</c>/<c>SystemIcon_</c>/<c>Plural_</c> dynamic key families together in
/// <see cref="KeyUsageCoverageTests"/> — splitting it by source (XAML vs. C#) would have made the
/// <c>Enum_</c>/<c>SystemIcon_</c>/<c>Plural_</c> exclusion permanent instead of closed by reflection.
/// </para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class XamlKeyCoverageTests
{
    private static readonly Regex LocKeyPattern = new(@"\{loc:Loc\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex FormatKeyPattern = new(@"FormatKey=([A-Za-z0-9_]+)", RegexOptions.Compiled);

    /// <summary>
    /// Every <c>{loc:Loc Key}</c>/<c>FormatKey=Key</c> token found anywhere under
    /// <c>src/DesktopOrganizer.UI/**/*.xaml</c> must resolve to a key present in <c>Strings.resx</c>.
    /// </summary>
    [Fact]
    public void EveryXamlLocKeyReference_ExistsInNeutralResx()
    {
        var repoRoot = RepoRootLocator.Find();
        var resxKeys = LoadResxKeyNames(repoRoot, "Strings.resx");
        var offenders = new List<string>();

        foreach (var file in EnumerateXamlFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            foreach (var key in EnumerateReferencedKeys(text))
            {
                if (resxKeys.Contains(key)) continue;

                var index = text.IndexOf(key, StringComparison.Ordinal);
                var line = index < 0 ? 0 : 1 + text[..index].Count(c => c == '\n');
                var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                offenders.Add($"{relativePath}:{line}: {key}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "XAML reference(s) to a RESX key missing from Strings.resx (G-L2a):\n" + string.Join('\n', offenders));
    }

    private static IEnumerable<string> EnumerateXamlFiles(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "src", "DesktopOrganizer.UI"), "*.xaml", SearchOption.AllDirectories);

    private static IEnumerable<string> EnumerateReferencedKeys(string xamlText)
    {
        foreach (Match match in LocKeyPattern.Matches(xamlText))
            yield return match.Groups[1].Value;

        foreach (Match match in FormatKeyPattern.Matches(xamlText))
            yield return match.Groups[1].Value;
    }

    private static HashSet<string> LoadResxKeyNames(string repoRoot, string fileName)
    {
        var path = Path.Combine(repoRoot, "src", "DesktopOrganizer.Localization", fileName);
        var document = XDocument.Load(path);
        return document.Root!.Elements("data")
            .Select(data => data.Attribute("name")?.Value)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
