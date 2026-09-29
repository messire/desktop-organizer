using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Phase 6.0 of docs/PLANS/design-system-1x.md (T-3, "храповик"): a COUNT-ONLY-DECREASES gate over the
/// "chrome" surfaces (<c>Views/**</c>, <c>Controls/**</c>, and the single file
/// <c>Resources/Styles/Settings.xaml</c> — NOT <c>Resources/Styles/Controls.xaml</c>, whose whole purpose
/// today is exactly this kind of literal, and which 6.2a rewrites onto <c>Theme.*</c> tokens directly; NOT
/// <c>Colors.xaml</c>/<c>Brushes.xaml</c>/<c>Typography.xaml</c>, the token dictionaries themselves) for the
/// four inline-literal shapes docs/PLANS/design-system-1x.md §2.12/§3 6.0 flags as the "Palette T" chrome's
/// own technical debt, ahead of 6.1's <c>Theme.*</c>/<c>Font.Size.*</c> tokens existing to replace them:
/// a literal hex colour, a literal <c>FontSize="…"</c>, a literal NAMED colour (<c>Foreground="White"</c>
/// etc. — <c>Transparent</c> excluded: a universal, non-visual, hit-test-only WPF idiom this plan's own
/// token migration was never going to touch), and a <c>Setter Property="Opacity"</c> inside an
/// <c>IsEnabled="False"</c> <see cref="System.Windows.Trigger"/> (§2.2's own "disabled ≠ Opacity" rule,
/// D-6). THIS phase changes no production file (see this class's own remarks on why the counts below are a
/// BASELINE, not a target) — a later phase (6.1/6.2) that fixes one of these should shrink the
/// corresponding baseline constant in the SAME commit, never leave it stale; a phase that adds a NEW one
/// outside <c>Theme.*</c>/<c>Font.Size.*</c> tokens should fail this gate, exactly as intended.
/// </summary>
/// <remarks>
/// Reads source files directly off disk (<see cref="RepoRootLocator"/>), the same convention
/// <see cref="XamlLiteralGuardTests"/> already established for its own (stricter, zero-tolerance) gate over
/// user-facing text literals — this gate is deliberately a RATCHET (allows today's known debt, blocks
/// growth) rather than a zero-tolerance gate, because unlike a missing <c>{loc:Loc}</c> call (always a
/// straightforward fix), every one of today's offenders needs a <c>Theme.*</c>/<c>Font.Size.*</c> token
/// that does not exist until 6.1 — failing outright in 6.0 would be unfixable without touching production
/// code, which 6.0's own DoD (docs/PLANS/design-system-1x.md §3 6.0) forbids.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChromeXamlLiteralGuardTests
{
    /// <summary>
    /// Current, known-debt baseline for <see cref="HexLiteralPattern"/> — see this class's own remarks.
    /// </summary>
    /// <remarks>
    /// Phase 6.2c (design-system-1x.md §3): shrunk 2 → 0 — <c>PanelPreviewCard.xaml</c>'s two
    /// "wallpaper" <c>GradientStop</c> literals moved to local <c>Color</c> resources (element content,
    /// not an attribute — see that file's own remarks), the only two offenders this baseline ever counted.
    /// </remarks>
    private const int HexLiteralBaseline = 0;

    /// <summary>Current, known-debt baseline for <see cref="FontSizeLiteralPattern"/> — see this class's own remarks.</summary>
    /// <remarks>
    /// Phase 6.2a: unchanged by that phase — <c>Settings.SegmentedItem.Compact</c>'s own <c>FontSize</c>
    /// literal was a <em>Setter</em>-shaped declaration (<c>&lt;Setter Property="FontSize" Value="11"/&gt;</c>,
    /// now <c>{DynamicResource Font.Size.Caption}</c>) — a different shape this attribute-only pattern never
    /// matched in the first place (see <see cref="FontSizeLiteralPattern"/>'s own remarks); it is T-4's
    /// scan, not T-3's, that would have counted it.
    /// Phase 6.2c (design-system-1x.md §3): shrunk from the pre-6.2c baseline as this phase's own files
    /// (<c>ChildWindow.xaml</c>'s hint popups, <c>PanelCustomizeWindow.xaml</c>'s two group headers,
    /// <c>PanelColorEditor.xaml</c>'s swatch/glyph sizes) moved to <c>Font.Size.*</c> tokens.
    /// L-1 fix (6.5 review round): the previous <c>8</c> baseline had drifted ahead of the remarks above,
    /// which only ever itemised 6 of it — this documents the full, accurate 7-entry breakdown instead: two
    /// literals in <c>ChildWindow.xaml</c> (the panel's own title text/edit box — Palette P, out of this
    /// guard's <c>Theme.*</c>/<c>Font.Size.*</c> migration mission entirely, same as every entry below),
    /// four in <c>PanelPreviewCard.xaml</c> (the scaled-down preview miniature — an explicit §2.3 allowlist
    /// exception, not Palette T chrome text), and one in <c>SystemIconsPanelWindow.xaml:126</c> (the desktop
    /// icon panel's own item label — Palette P/Ambient Accent, not Palette T chrome, so it is a permanent
    /// baseline entry rather than a target for a <c>Font.Size.*</c> token the way an actual Palette T chrome
    /// literal would be — the unrelated, unused-so-far <c>Typography.IconLabel</c> Palette P token would
    /// also change this literal's rendered size (11 vs. the current 10) and swap its <c>Foreground</c> to a
    /// luminance-adaptive brush, an unverifiable-without-Windows visual change outside this fix-pack's own
    /// scope).
    /// </remarks>
    private const int FontSizeLiteralBaseline = 7;

    /// <summary>Current, known-debt baseline for <see cref="NamedColorLiteralPattern"/> (excluding <see cref="ExemptNamedColorValues"/>) — zero today; kept as a ratchet (not a bare zero-tolerance Assert) so a future finding is reported the same way as the other three.</summary>
    private const int NamedColorLiteralBaseline = 0;

    /// <summary>Current, known-debt baseline for <see cref="OpacityInDisabledTriggerPattern"/> — see this class's own remarks.</summary>
    /// <remarks>
    /// Phase 6.2a (design-system-1x.md §3, ADR-047 D-6 "disabled ≠ Opacity"): shrunk 2 → 0 —
    /// <c>Settings.ToggleCheckBox</c> (the first offender) was removed outright (replaced by
    /// <c>Theme.ToggleSwitch</c>, Resources/Styles/Theme.Controls.xaml, whose disabled trigger uses
    /// <c>Theme.Control.Disabled</c>/<c>Theme.Text.Disabled</c> tokens instead), and
    /// <c>Settings.SegmentedItem</c>'s own Opacity trigger (the second) was replaced with a
    /// <c>Theme.Text.Disabled</c> Foreground Setter.
    /// </remarks>
    private const int OpacityInDisabledTriggerBaseline = 0;

    // A literal hex colour assigned to ANY attribute (e.g. Color="#FF4A6FA5") — three to eight hex digits
    // (WPF accepts #RGB/#RRGGBB/#AARRGGBB).
    private static readonly Regex HexLiteralPattern = new(
        "[A-Za-z][A-Za-z.]*=\"#[0-9A-Fa-f]{3,8}\"",
        RegexOptions.Compiled);

    // A literal FontSize="N" (or "N.N") attribute — the plan's own "no bespoke size outside Font.Size.*"
    // debt marker. Setter-based FontSize declarations INSIDE a Style (e.g. Settings.CardHeader's own
    // <Setter Property="FontSize" Value="13"/>) are a DIFFERENT shape (T-4's own concern, 6.1/6.2 — that
    // type does not exist yet) and are deliberately NOT matched by this attribute-only pattern.
    private static readonly Regex FontSizeLiteralPattern = new(
        "FontSize=\"[0-9]+(\\.[0-9]+)?\"",
        RegexOptions.Compiled);

    // A literal NAMED colour assigned to one of the five most common brush-bearing attributes — matches
    // only a bare word (no leading '{', which every {StaticResource}/{DynamicResource}/{Binding} value
    // starts with), so it never matches a real token reference.
    private static readonly Regex NamedColorLiteralPattern = new(
        "(?<!\\.)(Foreground|Background|BorderBrush|Fill|Stroke)=\"([A-Za-z]+)\"",
        RegexOptions.Compiled);

    // WPF idioms that are colours by TYPE but carry no visual-design decision of their own — never a
    // Theme.* token candidate, so excluded from the named-colour count entirely (not merely allowlisted).
    private static readonly HashSet<string> ExemptNamedColorValues = new(StringComparer.Ordinal) { "Transparent" };

    // A Setter Property="Opacity" nested directly inside an IsEnabled=False Trigger — the D-6 "disabled
    // must use a token, not a blanket Opacity" anti-pattern (§2.2's own rule; TargetName-qualified Setters,
    // e.g. `<Setter TargetName="Chrome" Property="Opacity" .../>`, are the SAME anti-pattern and matched
    // too via the optional TargetName group).
    private static readonly Regex OpacityInDisabledTriggerPattern = new(
        "<Trigger Property=\"IsEnabled\" Value=\"False\">\\s*<Setter(?:\\s+TargetName=\"[^\"]*\")?\\s+Property=\"Opacity\"\\s+Value=\"[^\"]*\"\\s*/>",
        RegexOptions.Compiled);

    /// <summary>Fails if a NEW inline hex colour literal appears beyond <see cref="HexLiteralBaseline"/>.</summary>
    [Fact]
    public void KnownDebt_InlineHexColorLiterals_DoesNotIncreaseBeyondBaseline()
    {
        var offenders = ScanScopedFiles(HexLiteralPattern);
        AssertWithinBaseline(offenders, HexLiteralBaseline, "inline hex colour literal");
    }

    /// <summary>Fails if a NEW inline <c>FontSize="…"</c> literal appears beyond <see cref="FontSizeLiteralBaseline"/>.</summary>
    [Fact]
    public void KnownDebt_InlineFontSizeLiterals_DoesNotIncreaseBeyondBaseline()
    {
        var offenders = ScanScopedFiles(FontSizeLiteralPattern);
        AssertWithinBaseline(offenders, FontSizeLiteralBaseline, "inline FontSize literal");
    }

    /// <summary>Fails if a NEW inline named-colour literal (outside <see cref="ExemptNamedColorValues"/>) appears beyond <see cref="NamedColorLiteralBaseline"/>.</summary>
    [Fact]
    public void KnownDebt_InlineNamedColorLiterals_DoesNotIncreaseBeyondBaseline()
    {
        var offenders = ScanScopedFiles(NamedColorLiteralPattern, match => !ExemptNamedColorValues.Contains(match.Groups[2].Value));
        AssertWithinBaseline(offenders, NamedColorLiteralBaseline, "inline named colour literal");
    }

    /// <summary>Fails if a NEW <c>Opacity</c> Setter inside an <c>IsEnabled="False"</c> Trigger appears beyond <see cref="OpacityInDisabledTriggerBaseline"/>.</summary>
    [Fact]
    public void KnownDebt_OpacityInsideDisabledTrigger_DoesNotIncreaseBeyondBaseline()
    {
        var offenders = ScanScopedFiles(OpacityInDisabledTriggerPattern);
        AssertWithinBaseline(offenders, OpacityInDisabledTriggerBaseline, "Opacity Setter inside an IsEnabled=False Trigger");
    }

    // --- helpers -------------------------------------------------------------------------------------

    /// <summary>Asserts <paramref name="offenders"/>' count has not grown past <paramref name="baseline"/>, listing every offender (<c>file:line: match</c>) on failure.</summary>
    private static void AssertWithinBaseline(List<string> offenders, int baseline, string kind)
    {
        var message = $"{kind} count grew to {offenders.Count} (baseline {baseline}) — docs/PLANS/design-system-1x.md T-3:\n" +
            string.Join('\n', offenders);
        Assert.True(offenders.Count <= baseline, message);
    }

    /// <summary>Scans every file in <see cref="ScopedFiles"/> for <paramref name="pattern"/>, returning each match as a <c>file:line: value</c> string, optionally narrowed by <paramref name="filter"/>.</summary>
    private static List<string> ScanScopedFiles(Regex pattern, Func<Match, bool>? filter = null)
    {
        var offenders = new List<string>();
        var repoRoot = RepoRootLocator.Find();

        foreach (var file in ScopedFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            foreach (Match match in pattern.Matches(text))
            {
                if (filter is not null && !filter(match)) continue;

                var line = 1 + text[..match.Index].Count(c => c == '\n');
                offenders.Add($"{relativePath}:{line}: {match.Value}");
            }
        }

        return offenders;
    }

    /// <summary>
    /// docs/PLANS/design-system-1x.md T-3's own scope: <c>Views/**</c>, <c>Controls/**</c> (the CUSTOM
    /// CONTROL markup folder, <c>src/DesktopOrganizer.UI/Controls</c> — not to be confused with the
    /// RESOURCE DICTIONARY <c>Resources/Styles/Controls.xaml</c>, deliberately excluded, see this class's
    /// own remarks), and the single file <c>Resources/Styles/Settings.xaml</c>.
    /// </summary>
    private static IEnumerable<string> ScopedFiles(string repoRoot)
    {
        var uiRoot = Path.Combine(repoRoot, "src", "DesktopOrganizer.UI");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(uiRoot, "Views"), "*.xaml", SearchOption.AllDirectories))
            yield return file;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(uiRoot, "Controls"), "*.xaml", SearchOption.AllDirectories))
            yield return file;

        yield return Path.Combine(uiRoot, "Resources", "Styles", "Settings.xaml");
    }
}
