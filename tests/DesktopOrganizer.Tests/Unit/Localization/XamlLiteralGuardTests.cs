using System.IO;
using System.Text.RegularExpressions;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Gate G-L1 (docs/PLANS/localization.md §6/§4.8, WP-6): scans every <c>*.xaml</c> file under
/// <c>src/DesktopOrganizer.UI</c> for a hardcoded, user-facing string literal in one of the attributes
/// that actually renders text to the user — <c>Text</c>, <c>Content</c>, <c>Header</c>, <c>ToolTip</c>,
/// <c>Title</c>, <c>AutomationProperties.Name</c>, <c>AutomationProperties.HelpText</c> — outside the
/// plan's own allowlist (§0.2 D-0.2: the product name <c>DesktopOrganizer</c>, Segoe MDL2 glyph codes,
/// <c>Tag</c>, <c>InputGestureText</c>). A regression here means a future edit slipped an English literal
/// past every <c>{loc:Loc}</c>/<c>{loc:LocBinding}</c> call site this codebase otherwise uses everywhere.
/// </summary>
/// <remarks>
/// Reads source files directly off disk (<see cref="RepoRootLocator"/>), not the compiled assembly, so it
/// catches a literal the moment it is typed, not only after a rebuild. <c>Tag</c> and
/// <c>InputGestureText</c> are simply not in <see cref="AttributeLiteralPattern"/> — they hold internal
/// identifiers (<c>Tag="Small"</c>, the size-preset menu's own dispatch value; <c>InputGestureText="F2"</c>,
/// a keyboard-shortcut hint) rather than user-facing prose, so they were never candidates for translation
/// in the first place. <c>d:</c>-prefixed design-time attributes and attached properties whose OWN name
/// merely ends with one of the guarded tokens (e.g. <c>SizeToContent</c> ending in <c>"Content"</c>) are
/// excluded by requiring the matched attribute name be a whole token — see the pattern's leading negative
/// look-behind. A value starting with <c>{</c> (a markup extension/binding, including <c>{loc:Loc ...}</c>
/// itself) is excluded via a negative look-ahead rather than by "contains no letters", since a resolved
/// binding's own key text obviously contains letters.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class XamlLiteralGuardTests
{
    private static readonly Regex AttributeLiteralPattern = new(
        "(?<![\\w:.])(Text|Content|Header|ToolTip|Title|AutomationProperties\\.Name|AutomationProperties\\.HelpText)" +
        "\\s*=\\s*\"(?!\\{)([^\"]*)\"",
        RegexOptions.Compiled);

    // A numeric XML character reference, e.g. "&#xE8CB;" (a Segoe MDL2 glyph code) — §0.2 D-0.2 allowlist.
    private static readonly Regex GlyphCodePattern = new(@"^&#x[0-9A-Fa-f]+;$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Exact literal values that are deliberately never translated, with the reason inline (§0.2 D-0.2
    /// plus one project-specific exception).
    /// </summary>
    private static readonly Dictionary<string, string> AllowedLiteralValues = new(StringComparer.Ordinal)
    {
        // The product name is never translated (§0.2).
        ["DesktopOrganizer"] = "product name (§0.2)",
    };

    /// <summary>
    /// Matches a <c>StringFormat=</c> value in any of the four shapes this codebase uses: a standalone
    /// double-quoted attribute (<c>StringFormat="{}{0:P0}"</c> on <c>MultiBinding</c>), a standalone
    /// single-quoted attribute (<c>StringFormat='{}{0:F0} px'</c>, needed because a single-quoted value can
    /// itself sit inside an outer double-quoted XAML attribute), or an inline extension property
    /// (<c>StringFormat={}{0:F0} px</c> nested inside a <c>Binding</c>/<c>MultiBinding</c> markup
    /// extension, itself inside an outer quoted attribute). The WPF <c>{}</c> escape prefix is OPTIONAL in
    /// every shape: <c>StringFormat=Total: {0}</c> (no leading brace at all — still a valid, un-escaped
    /// <c>MarkupExtension</c> property value because it does not start with <c>{</c>) is just as legal as
    /// <c>StringFormat="px {0}"</c> with no escape inside quotes. The inline (unquoted) branch stops at the
    /// next <c>,</c> or <c>"</c> — whichever ends the property or the enclosing attribute — so it never
    /// swallows a sibling property like <c>, Mode=OneWay</c> that follows on the same markup extension.
    /// </summary>
    private static readonly Regex StringFormatPattern = new(
        "StringFormat\\s*=\\s*(?:\"(?<q>[^\"]*)\"|'(?<q>[^']*)'|(?<inline>(?:\\{\\}|)(?:[^{},\"]|\\{[^}]*\\})*))",
        RegexOptions.Compiled);

    // A single "{n}" or "{n:format}" placeholder group — removed wholesale (format specifier letters like
    // P0/F0/g/dd.MM HH:mm included) before checking the remainder for literal letters.
    private static readonly Regex PlaceholderGroupPattern = new(@"\{[^}]*\}", RegexOptions.Compiled);

    /// <summary>
    /// Scans every <c>*.xaml</c> file under <c>src/DesktopOrganizer.UI</c> and fails, listing every
    /// offending <c>file:line</c> and the literal value found, if any hardcoded user-facing string turns
    /// up outside the allowlist above.
    /// </summary>
    [Fact]
    public void XamlFiles_HaveNoHardcodedUserFacingLiteralOutsideAllowlist()
    {
        var repoRoot = RepoRootLocator.Find();
        var uiRoot = Path.Combine(repoRoot, "src", "DesktopOrganizer.UI");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            foreach (Match match in AttributeLiteralPattern.Matches(text))
            {
                var attributeName = match.Groups[1].Value;
                var value = match.Groups[2].Value;

                if (string.IsNullOrEmpty(value)) continue; // e.g. Header=""
                if (GlyphCodePattern.IsMatch(value)) continue;
                if (AllowedLiteralValues.ContainsKey(value)) continue;
                if (!value.Any(char.IsLetter)) continue; // symbols/digits only, e.g. Text="⚠"

                var line = 1 + text[..match.Index].Count(c => c == '\n');
                offenders.Add($"{relativePath}:{line}: {attributeName}=\"{value}\"");
            }

            foreach (Match match in StringFormatPattern.Matches(text))
            {
                var value = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["inline"].Value;

                if (!ContainsLiteralLetterOutsidePlaceholders(value)) continue;

                var line = 1 + text[..match.Index].Count(c => c == '\n');
                offenders.Add($"{relativePath}:{line}: StringFormat={value}");
            }
        }

        var message = "Hardcoded XAML literal(s) found outside the G-L1 allowlist (docs/PLANS/localization.md §0.2/§6):\n" +
            string.Join('\n', offenders);
        Assert.True(offenders.Count == 0, message);
    }

    /// <summary>
    /// RED-first regression coverage (MINOR-A, fix-cycle 2) for <see cref="StringFormatPattern"/> itself:
    /// each of the four shapes it must extract correctly from a bare attribute fragment (no full XAML
    /// document needed), including the previously-mishandled cases — a sibling markup extension property
    /// after a comma (<c>, Mode=OneWay</c>) must NOT leak into the extracted value, a single-quoted value
    /// must be found at all, and a value with no <c>{}</c> escape prefix (either quoted or inline) must be
    /// found at all rather than silently skipped.
    /// </summary>
    /// <param name="attributeFragment">A minimal <c>StringFormat=...</c> fragment, as it would appear
    /// embedded in a larger XAML attribute value.</param>
    /// <param name="expectedExtractedValue">The exact value <see cref="StringFormatPattern"/> must
    /// capture in its <c>q</c> or <c>inline</c> group.</param>
    /// <param name="expectedHasLiteralOutsidePlaceholders">Whether the extracted value should be flagged
    /// as containing a literal letter outside every placeholder group.</param>
    [Theory]
    [InlineData("StringFormat=\"{}{0:P0}\"", "{}{0:P0}", false)]
    [InlineData("StringFormat='{}{0:F0} px'", "{}{0:F0} px", true)]
    [InlineData("StringFormat={}{0:P0}, Mode=OneWay}", "{}{0:P0}", false)]
    [InlineData("StringFormat=Total: {0}", "Total: {0}", true)]
    [InlineData("StringFormat=\"px {0}\"", "px {0}", true)]
    public void StringFormatPattern_ExtractsValueAndDetectsLiteralTextForEveryShape(
        string attributeFragment, string expectedExtractedValue, bool expectedHasLiteralOutsidePlaceholders)
    {
        var match = StringFormatPattern.Match(attributeFragment);

        Assert.True(match.Success, $"StringFormatPattern did not match: {attributeFragment}");

        var extracted = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["inline"].Value;

        Assert.Equal(expectedExtractedValue, extracted);
        Assert.Equal(expectedHasLiteralOutsidePlaceholders, ContainsLiteralLetterOutsidePlaceholders(extracted));
    }

    /// <summary>
    /// Isolated unit coverage (no disk I/O) for the letter-outside-placeholders detection the
    /// <c>StringFormat=</c> scan above relies on: a pure format specifier (<c>{0:P0}</c>/<c>{0:g}</c>/
    /// <c>{0:F0}</c>), even though the specifier letter itself is a letter, must NOT be flagged — only a
    /// literal suffix/prefix like <c>" px"</c> sitting outside the placeholder braces should be (§2.6:
    /// such a suffix belongs in RESX as a <c>_Format</c> key via <c>FormatKey=</c>, e.g.
    /// <c>Common_ValuePx_Format</c>, not hardcoded in XAML).
    /// </summary>
    /// <param name="stringFormatValue">The raw, <c>{}</c>-escaped <c>StringFormat=</c> value under test.</param>
    /// <param name="expected">Whether a literal letter outside every placeholder group is expected.</param>
    [Theory]
    [InlineData("{}{0:P0}", false)]
    [InlineData("{}{0:g}", false)]
    [InlineData("{}{0:F0}", false)]
    [InlineData("{}{0}", false)]
    [InlineData("{}{0:F0} px", true)]
    [InlineData("{}px {0:F0}", true)]
    [InlineData("{}{0} шт.", true)]
    public void ContainsLiteralLetterOutsidePlaceholders_DetectsLiteralTextOutsideFormatSpecifiers(
        string stringFormatValue,
        bool expected)
    {
        Assert.Equal(expected, ContainsLiteralLetterOutsidePlaceholders(stringFormatValue));
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="stringFormatValue"/> — a raw <c>StringFormat=</c> value
    /// including its leading <c>{}</c> escape — has a Latin or Cyrillic letter OUTSIDE every
    /// <c>{n[:format]}</c> placeholder group. Format specifier letters (<c>P0</c>, <c>g</c>, <c>dd.MM
    /// HH:mm</c>) live INSIDE a placeholder group and are stripped along with it, so they never count.
    /// </summary>
    /// <param name="stringFormatValue">The raw <c>StringFormat=</c> attribute value, <c>{}</c>-escaped.</param>
    private static bool ContainsLiteralLetterOutsidePlaceholders(string stringFormatValue)
    {
        var withoutEscape = stringFormatValue.StartsWith("{}", StringComparison.Ordinal)
            ? stringFormatValue[2..]
            : stringFormatValue;
        var withoutPlaceholders = PlaceholderGroupPattern.Replace(withoutEscape, string.Empty);
        return withoutPlaceholders.Any(char.IsLetter);
    }
}
