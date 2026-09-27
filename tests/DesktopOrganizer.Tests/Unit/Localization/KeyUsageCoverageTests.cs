using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DesktopOrganizer.Core;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Localization fix-cycle 1 FIX-C item 3 (docs/PLANS/localization.md §7.4 "unused key cleanup"):
/// every key defined in <c>Strings.resx</c> must be reachable from at least one real call site — a
/// literal <c>{loc:Loc Key}</c>/<c>FormatKey=Key</c> in XAML, a literal RESX-key-shaped string anywhere
/// in C# source (<c>GetString("Key")</c>, a ternary feeding <c>GetString</c>, an indexer, …), or one of
/// the four dynamically-built key families this codebase actually uses: <c>Enum_{Type}_{Member}</c> via
/// <see cref="EnumDisplayNameConverter"/> (directly, or through the shared
/// <c>Settings.EnumRadioLabel</c> <see cref="System.Windows.DataTemplate"/>), <c>SystemIcon_{Kind}</c> via
/// <c>SystemIconsPanelManager</c>, <c>Plural_{Base}_{Category}</c> via <c>FormatPlural</c>, and
/// <c>ColorPreset_{Id}</c> (<see cref="PanelColorPreset.NameKey"/>) via <c>PanelColorEditor</c>'s preset
/// swatch grid (docs/PLANS/color-presets-and-size-step.md §3.4/§4.2).
/// <see cref="XamlKeyCoverageTests"/>/<see cref="CodeKeyCoverageTests"/> already guarantee the REVERSE
/// direction (every reference resolves to a real key); this is the other half — a key nothing references
/// is dead and silently rots out of sync with whatever it once backed. This replaces the old
/// warning-only <c>UnusedResxKeys_ReportedAsWarningNotFailure</c> report with a hard gate.
/// </summary>
/// <remarks>
/// The C# side deliberately does not restrict itself to specific call syntax (<c>GetString(...)</c>,
/// <c>Format(...)</c>, an indexer): a real dead-key miss was found during this test's own development —
/// <c>LocalizationResources.GetString(cond ? "KeyA" : "KeyB", culture)</c> puts the literal one token
/// after a ternary, not immediately after <c>GetString(</c>. Any quoted string literal anywhere in C#
/// source that merely LOOKS like a RESX key (matches <see cref="KeyNamePattern"/>) is therefore treated
/// as a potential reference — a coincidental non-key PascalCase-with-underscore literal is harmless here
/// (it only matters if it also happens to equal an actual RESX key name, which the very specific §2.6
/// convention makes vanishingly unlikely for anything that is not, in fact, a key reference).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class KeyUsageCoverageTests
{
    // §2.6: PascalCase tokens joined by '_', at least one underscore — same filter
    // ResxConsistencyTests/CodeKeyCoverageTests use to separate real key candidates from call-site noise.
    private static readonly Regex KeyNamePattern = new(@"^[A-Z][A-Za-z0-9]*(_[A-Z0-9][A-Za-z0-9]*)+$", RegexOptions.Compiled);

    private static readonly Regex LocKeyPattern = new(@"\{loc:Loc\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex FormatKeyPattern = new(@"FormatKey=([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex QuotedLiteralPattern = new("\"([A-Za-z0-9_]+)\"", RegexOptions.Compiled);

    private static readonly string[] PluralCategories = { "One", "Few", "Many", "Other" };

    /// <summary>
    /// §3.2/R-15: enum types actually rendered through <see cref="EnumDisplayNameConverter"/> — either
    /// bound directly (<c>Converter={StaticResource EnumDisplayName}</c>) or through the shared
    /// <c>Settings.EnumRadioLabel</c> <see cref="System.Windows.DataTemplate"/> — as opposed to an
    /// <c>Enum_*</c> key referenced by its own literal <c>{loc:Loc ...}</c> token (already covered by the
    /// plain XAML scan below). Found by tracing every <c>{StaticResource EnumDisplayName}</c>/
    /// <c>Settings.EnumRadioLabel</c> usage in <c>src/DesktopOrganizer.UI/Views/**/*.xaml</c> and
    /// <c>Resources/Styles/Settings.xaml</c> back to the bound property's/<c>x:Static</c>'s enum type.
    /// <c>PanelSizePreset</c>/<c>PanelSizingMode</c> are NOT in this list: both are only ever referenced
    /// by their own literal <c>Enum_PanelSizePreset_*</c>/<c>Enum_PanelSizingMode_*</c> keys (menu
    /// <c>Header</c>s, a ternary feeding <c>GetString</c>), never through this converter.
    /// </summary>
    private static readonly Type[] EnumTypesDisplayedViaConverter =
    {
        typeof(IconLayoutPolicy),
        typeof(NormalModeLayoutGestures),
        typeof(PanelContentFilter),
        typeof(PanelGeometryMode),
        typeof(PanelSortMode),
        typeof(PanelSurfaceStyle),
        typeof(PanelViewMode),
        typeof(SystemIconSize),

        // ADR-042: PanelColorEditor's Mode segmented control reuses the same
        // {loc:LocBinding Source={x:Static ...}, Converter={StaticResource EnumDisplayName}} idiom every
        // other 2/3-option radio/segmented group in this codebase already uses — Enum_PanelThemeMode_Light/
        // Enum_PanelThemeMode_Dark, not a pair of bespoke Common_Mode_Light/Dark keys.
        typeof(PanelThemeMode),
    };

    /// <summary>
    /// Every key in <c>Strings.resx</c> must be used from at least one XAML/C# call site, or be one of
    /// the <c>Enum_</c>/<c>SystemIcon_</c>/<c>Plural_</c> dynamic key families this codebase actually
    /// builds at runtime.
    /// </summary>
    [Fact]
    public void AllResxKeys_AreUsedFromXamlCSharpOrADynamicKeyConvention()
    {
        var repoRoot = RepoRootLocator.Find();
        var resxKeys = LoadResxKeyNames(repoRoot);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateXamlFiles(repoRoot))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in LocKeyPattern.Matches(text))
                used.Add(match.Groups[1].Value);

            foreach (Match match in FormatKeyPattern.Matches(text))
                used.Add(match.Groups[1].Value);
        }

        foreach (var file in EnumerateCSharpFiles(repoRoot))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in QuotedLiteralPattern.Matches(text))
            {
                var literal = match.Groups[1].Value;
                if (!KeyNamePattern.IsMatch(literal)) continue;

                used.Add(literal);

                // A FormatPlural base ("Plural_Items") is itself never a RESX key — only its four
                // category-suffixed forms are (L-8) — so seeing the base literal must also mark those
                // four forms used, regardless of which specific call passed it.
                if (literal.StartsWith("Plural_", StringComparison.Ordinal))
                {
                    foreach (var category in PluralCategories)
                        used.Add($"{literal}_{category}");
                }
            }
        }

        foreach (var enumType in EnumTypesDisplayedViaConverter)
        {
            foreach (var member in Enum.GetNames(enumType))
                used.Add($"Enum_{enumType.Name}_{member}");
        }

        foreach (var kind in Enum.GetNames(typeof(SystemIconKind)))
            used.Add($"SystemIcon_{kind}");

        // ColorPreset_{Id} (docs/PLANS/color-presets-and-size-step.md §3.4/§4.2): PanelColorEditor.xaml
        // binds each preset's name via {loc:LocBinding NameKey, Converter={StaticResource LocKey}} — a
        // data-driven RESX key built at runtime from PanelColorPreset.NameKey, never a literal
        // {loc:Loc ColorPreset_X} token the plain XAML scan above could find on its own.
        foreach (var preset in PanelColorPresets.All)
            used.Add(preset.NameKey);

        var unused = resxKeys.Except(used, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        var message = "RESX key(s) unreachable from any XAML/C# call site or dynamic key convention — dead, remove " +
            "from both Strings.resx and Strings.ru.resx: " + string.Join(", ", unused);
        Assert.True(unused.Count == 0, message);
    }

    /// <summary>
    /// MINOR-C (fix-cycle 2): guards the exclusion above. <see cref="EnumerateCSharpFiles"/> skips
    /// <c>*.Designer.cs</c> so that a future <c>StronglyTypedLanguage</c>/<c>ResXFileCodeGenerator</c>
    /// output (<c>Strings.Designer.cs</c>) — which would embed the FULL text of every key name as a
    /// literal constant, in both RESX-derived C# files if ever added for en AND ru — could not silently
    /// make <see cref="AllResxKeys_AreUsedFromXamlCSharpOrADynamicKeyConvention"/> vacuously green (every
    /// key trivially "used" by its own generated accessor, defeating the point of the gate). L-2 already
    /// forbids strongly-typed generation for <c>Strings.resx</c>; this asserts that decision has not been
    /// quietly reversed by a generated file showing up on disk.
    /// </summary>
    [Fact]
    public void LocalizationProject_HasNoStronglyTypedDesignerFile()
    {
        var repoRoot = RepoRootLocator.Find();
        var localizationRoot = Path.Combine(repoRoot, "src", "DesktopOrganizer.Localization");

        var designerFiles = Directory.EnumerateFiles(localizationRoot, "Strings*.Designer.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var message = "L-2 (docs/PLANS/localization.md §1) forbids strongly-typed RESX generation, but found: " +
            string.Join(", ", designerFiles);
        Assert.True(designerFiles.Count == 0, message);
    }

    private static IEnumerable<string> EnumerateXamlFiles(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "src", "DesktopOrganizer.UI"), "*.xaml", SearchOption.AllDirectories);

    private static IEnumerable<string> EnumerateCSharpFiles(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase));

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
