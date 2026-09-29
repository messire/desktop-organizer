using System.IO;
using System.Text.RegularExpressions;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// T-2 (docs/PLANS/design-system-1x.md §3 phase 6.1, ADR-047 D-2): every <see cref="ThemeKeys"/> constant
/// resolves in every <see cref="ThemeResourceBuilder"/>-built dictionary (Light/Dark/HighContrast — the
/// "completeness" half), and every <c>{DynamicResource Theme.*}</c>/<c>{DynamicResource Font.Size.*}</c>
/// reference actually found in the real, checked-in XAML resolves to a real <see cref="ThemeKeys"/> constant
/// (the "no silent typo" half — a <c>DynamicResource</c> with a misspelled key never throws, it just quietly
/// never paints). Also guards the two hard rules ADR-047 D-2/D-3 depend on: no <c>{StaticResource Theme.*}</c>
/// anywhere (that would freeze whichever theme happened to be active when the file first parsed) and no
/// lingering <c>Brush.Settings.</c>/<c>Color.Settings.</c> reference in <c>src/</c> (the pre-6.1 fixed-light
/// tokens this phase removed).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ThemeKeysCoverageTests
{
    private static readonly Regex DynamicThemeResourceRegex = new(
        @"\{DynamicResource\s+((?:Theme|Font\.Size)\.[A-Za-z0-9.]+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex StaticThemeResourceRegex = new(
        @"\{StaticResource\s+(Theme\.[A-Za-z0-9.]+)\s*\}", RegexOptions.Compiled);

    /// <summary>The three <see cref="AppTheme"/> values a built dictionary must exist for.</summary>
    public static TheoryData<AppTheme> Themes => [AppTheme.Light, AppTheme.Dark, AppTheme.HighContrast];

    /// <summary>Every <see cref="ThemeKeys"/> constant is present in every built dictionary.</summary>
    /// <param name="theme">The theme to build and check.</param>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Build_ContainsEveryThemeKey(AppTheme theme)
    {
        var dictionary = ThemeResourceBuilder.Build(theme, messageFontSize: 12);

        foreach (var key in ThemeKeys.All)
            Assert.True(dictionary.Contains(key), $"{theme}: missing key '{key}'.");
    }

    /// <summary>
    /// Every <c>{DynamicResource Theme.*}</c>/<c>{DynamicResource Font.Size.*}</c> key referenced anywhere
    /// in the real, checked-in <c>src/DesktopOrganizer.UI</c> XAML is a real <see cref="ThemeKeys"/>
    /// constant — a typo'd key (e.g. <c>Theme.Surace.Card</c>) would otherwise compile fine and simply
    /// paint nothing, forever, with no build error.
    /// </summary>
    [Fact]
    public void EveryDynamicThemeResourceReference_IsAKnownThemeKey()
    {
        var known = new HashSet<string>(ThemeKeys.All, StringComparer.Ordinal);
        var unknown = new List<string>();

        foreach (var (path, content) in EnumerateUiXaml())
        {
            foreach (Match match in DynamicThemeResourceRegex.Matches(content))
            {
                var key = match.Groups[1].Value;
                if (!known.Contains(key))
                    unknown.Add($"{key} ({RelativePath(path)})");
            }
        }

        Assert.True(unknown.Count == 0, "Unknown Theme.*/Font.Size.* DynamicResource key(s):\n" + string.Join('\n', unknown));
    }

    /// <summary>
    /// No <c>{StaticResource Theme.*}</c> against a real <see cref="ThemeKeys"/> colour/typography/geometry
    /// constant anywhere — a theme switch replaces the whole dictionary in
    /// <see cref="System.Windows.Application.Resources"/>, and only <c>DynamicResource</c> re-resolves
    /// after that swap (ADR-047 D-2). A <c>StaticResource</c> reference would silently freeze whichever
    /// theme happened to be active the first time that XAML file was parsed.
    /// </summary>
    /// <remarks>
    /// Phase 6.2a (design-system-1x.md §2.5, ADR-047 D-7): distinguishes a <c>Theme.*</c> COLOUR/geometry
    /// token (must be <c>DynamicResource</c> — this is what the rule is actually about) from a <c>Theme.*</c>
    /// STYLE key (<c>Theme.ContextMenu</c>, <c>Theme.MenuItem</c>, <c>Theme.Button.Primary</c>, …) — the
    /// latter is a stable <see cref="System.Windows.Style"/>/<see cref="System.Windows.Controls.ControlTemplate"/>
    /// OBJECT that never changes identity on a theme switch (only the <c>DynamicResource</c> colour Setters
    /// INSIDE it do), so <c>Style="{StaticResource Theme.ContextMenu}"</c> is the plan's own documented,
    /// correct usage (§2.5: "Меню получают стиль явно… сам стиль ключевой и неизменяемый"). Before 6.2a
    /// every <c>Theme.*</c> key WAS a colour/typography/geometry token, so the original blanket regex was
    /// accurate for its own scope; now that a style catalogue shares the same prefix, only a match against
    /// a real <see cref="ThemeKeys.All"/> member is an actual violation.
    /// </remarks>
    [Fact]
    public void NoStaticResourceReferencesAnyThemeKey()
    {
        var known = new HashSet<string>(ThemeKeys.All, StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var (path, content) in EnumerateUiXaml())
        {
            foreach (Match match in StaticThemeResourceRegex.Matches(content))
            {
                if (known.Contains(match.Groups[1].Value))
                    offenders.Add($"{match.Groups[1].Value} ({RelativePath(path)})");
            }
        }

        Assert.True(offenders.Count == 0, "{StaticResource Theme.*} against a real colour/typography/geometry key found in:\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// DoD 6.1 #1: no lingering <c>Brush.Settings.</c>/<c>Color.Settings.</c> reference anywhere in
    /// <c>src/</c> (code or XAML) — the pre-6.1 fixed-light Settings-window tokens this phase's migration
    /// removed from <c>Colors.xaml</c>/<c>Brushes.xaml</c>/<c>Colors.HighContrast.xaml</c>.
    /// </summary>
    [Fact]
    public void NoSourceFileReferencesTheRemovedFixedLightSettingsTokens()
    {
        var repoRoot = RepoRootLocator.Find();
        var srcRoot = Path.Combine(repoRoot, "src");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var extension = Path.GetExtension(path);
            if (extension != ".xaml" && extension != ".cs")
                continue;

            var content = File.ReadAllText(path);
            if (content.Contains("Brush.Settings.", StringComparison.Ordinal) || content.Contains("Color.Settings.", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(repoRoot, path));
        }

        Assert.True(offenders.Count == 0, "Brush.Settings./Color.Settings. reference(s) found in:\n" + string.Join('\n', offenders));
    }

    private static IEnumerable<(string Path, string Content)> EnumerateUiXaml()
    {
        var repoRoot = RepoRootLocator.Find();
        var uiRoot = Path.Combine(repoRoot, "src", "DesktopOrganizer.UI");

        foreach (var path in Directory.EnumerateFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            yield return (path, File.ReadAllText(path));
        }
    }

    private static string RelativePath(string fullPath) => Path.GetRelativePath(RepoRootLocator.Find(), fullPath);
}
