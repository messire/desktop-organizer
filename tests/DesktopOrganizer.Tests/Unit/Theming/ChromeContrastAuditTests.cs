using System.IO;
using System.Windows.Controls;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Phase 6.0 of docs/PLANS/design-system-1x.md ("Аудит и храповики") characterized four "Palette T"
/// (chrome — Settings window, panel header menu) contrast gaps against the fixed-light tokens that existed
/// then (<c>Color.Settings.TabActiveBackground</c>/<c>IconMuted</c>/<c>ToggleTrackOff</c>/<c>Warning</c>).
/// Phase 6.1 (docs/PLANS/design-system-1x.md §2.2/§3, ADR-047 D-2) removed every one of those fixed tokens
/// and replaced them with the theme-aware <see cref="ThemePalette"/>/<see cref="ThemeKeys"/> set, closing
/// three of those four gaps outright (S-1 sidebar indicator, S-3 toggle track, S-6/S-7 Warning text) and
/// upgrading the fourth (S-2 sidebar glyph) from "barely meets icon contrast" to "meets full text
/// contrast" — exactly the "a future phase that raises any of these numbers above their target should
/// update the corresponding assertion together, not leave this test stale" convention this class's own
/// original remarks called for (mirroring <see cref="AmbientAccentContrastAuditTests"/>'s "KnownGap_*"
/// convention). The four <c>S-1</c>/<c>S-2</c>/<c>S-3</c>/<c>S-6</c>/<c>S-7</c> tests below assert the FIXED
/// state, reading straight from <see cref="ThemePalette.Light"/> (pure C#, no STA/XAML needed) instead of a
/// real compiled <c>Colors.xaml</c> — there is no XAML resource for these tokens any more (Palette T's
/// source of truth is <see cref="ThemePalette"/> itself, per ADR-047 D-1). H-1 (docs/PLANS/design-system-1x.md
/// §1.1 S-18) was closed in 6.2a and is pinned by <c>ChromeStylesStaTests</c>' own
/// <c>MenuItem_SubmenuHeaderRole_PopupBackgroundIsThemeSurfaceFlyout_WithReadableTextContrast</c> instead
/// (that test used to build against this class's own <c>Control.ContextMenu</c>/<c>Control.MenuItem</c>
/// KnownGap fixture — both styles were removed outright in 6.2c once ChildWindow.xaml's real
/// <c>HeaderContextMenu</c> migrated to <c>Theme.ContextMenu</c>/<c>Theme.MenuItem</c>, so that fixture no
/// longer has anything left to build against). S-17 is closed in 6.2c and asserted below, directly against
/// the real, compiled <c>ChildWindow.xaml</c>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChromeContrastAuditTests
{
    // ── §1.1 S-1 (FIXED in 6.1): Settings sidebar selection indicator. Theme.Selection.Fill is still
    // white-on-Theme.Surface.Window in Light (byte-identical to the old TabActiveBackground/ChromeBackground
    // pair, ≈ 1.14:1) — but the sidebar template now ALSO paints a dedicated Theme.Selection.Indicator bar,
    // which is what WCAG 1.4.11's ≥ 3:1 state-indicator floor is actually checked against. ────────────────

    /// <summary>
    /// Confirms the plan's own S-1 gap is closed: <see cref="ThemePalette.SelectionIndicator"/> (the
    /// dedicated indicator bar <c>Settings.SidebarListBoxItem</c>/<c>Settings.SidebarAboutToggle</c> now
    /// paint alongside the selected fill) meets the ≥ 3:1 WCAG 1.4.11 non-text/state-indicator floor
    /// against the sidebar's own background, unlike the old fill-only signal it replaces.
    /// </summary>
    [Fact]
    public void SettingsSidebar_SelectionIndicator_MeetsNonTextContrastAgainstWindow()
    {
        var contrast = ContrastCalculator.ContrastRatio(ThemePalette.Light.SelectionIndicator, ThemePalette.Light.SurfaceWindow);

        Assert.True(contrast >= ContrastCalculator.MinimumIconContrast, $"Expected >= 3:1 non-text contrast; got {contrast:F2}.");
    }

    // ── §1.1 S-2 (upgraded in 6.1): sidebar glyph now shares Theme.Text.Secondary with every other muted
    // label, instead of its own separate (barely-3:1) IconMuted token. ───────────────────────────────────

    /// <summary>
    /// Confirms the plan's own S-2 finding no longer applies: <see cref="ThemePalette.TextSecondary"/> (the
    /// sidebar glyph's colour post-6.1) meets the full 4.5:1 TEXT contrast floor against the sidebar's own
    /// background — not just the looser 3:1 icon floor the old, separate <c>IconMuted</c> token barely
    /// cleared.
    /// </summary>
    [Fact]
    public void SettingsSidebar_TextSecondary_MeetsTextContrastAgainstWindow()
    {
        var contrast = ContrastCalculator.ContrastRatio(ThemePalette.Light.TextSecondary, ThemePalette.Light.SurfaceWindow);

        Assert.True(contrast >= ContrastCalculator.MinimumTextContrast, $"Expected >= 4.5:1 text contrast; got {contrast:F2}.");
    }

    // ── §1.1 S-3 (FIXED in 6.1): Layout & snapping toggle track, "off" state — Theme.Stroke.Input replaces
    // the old ToggleTrackOff, against the card's own Theme.Surface.Card background. ──────────────────────

    /// <summary>
    /// Confirms the plan's own S-3 gap is closed: <see cref="ThemePalette.StrokeInput"/> (the "off" toggle
    /// track's colour post-6.1) meets the ≥ 3:1 WCAG 1.4.11 UI-component-boundary floor against the card
    /// background it sits on.
    /// </summary>
    [Fact]
    public void LayoutSnapping_ToggleTrackOff_MeetsNonTextContrastAgainstCard()
    {
        var contrast = ContrastCalculator.ContrastRatio(ThemePalette.Light.StrokeInput, ThemePalette.Light.SurfaceCard);

        Assert.True(contrast >= ContrastCalculator.MinimumIconContrast, $"Expected >= 3:1 non-text contrast; got {contrast:F2}.");
    }

    // ── §1.1 S-6/S-7 (FIXED in 6.1): Warning text — Theme.Text.Caution replaces the old Warning brush,
    // against the card's own Theme.Surface.Card background. Pages rename error, System icons warning. ────

    /// <summary>
    /// Confirms the plan's own S-6/S-7 gap is closed: <see cref="ThemePalette.TextCaution"/> (used as TEXT —
    /// Pages rename error, System icons warning — so the 4.5:1 text floor applies, not the looser 3:1 icon
    /// floor) meets that floor against the card background it sits on.
    /// </summary>
    [Fact]
    public void SettingsCaution_MeetsTextContrastAgainstCard()
    {
        var contrast = ContrastCalculator.ContrastRatio(ThemePalette.Light.TextCaution, ThemePalette.Light.SurfaceCard);

        Assert.True(contrast >= ContrastCalculator.MinimumTextContrast, $"Expected >= 4.5:1 text contrast; got {contrast:F2}.");
    }

    // ── §1.1 S-17 (FIXED in 6.2c): panel header menu group-header pseudo-items ("Содержимое"/"Упорядочить
    // значки", ChildWindow.xaml) are no longer disabled IsEnabled="False" labels — they are real, enabled
    // MenuItem submenu headers (design-system-1x.md §2.6 p.3) with their own nested items. ────────────────

    /// <summary>
    /// Closes the plan's own S-17 finding: <c>Panel_Menu_ContentHeader</c>/<c>Panel_Menu_ArrangeIconsHeader</c>
    /// (ChildWindow.xaml's <c>HeaderContextMenu</c>) used to be plain <c>IsEnabled="False"</c>
    /// <see cref="MenuItem"/>s with no own <c>Foreground</c> override (the stock Aero2 disabled-item trigger
    /// then rendered them at ≈ 3.0:1, below the 4.5:1 text floor). Phase 6.2c converts both into real,
    /// ENABLED submenu headers (<see cref="MenuItemRole.SubmenuHeader"/>, styled by <c>Theme.MenuItem</c>,
    /// ≥ 4.5:1 in every theme per <c>ChromeStylesStaTests</c>' own catalogue-wide coverage) — this pins the
    /// two textual preconditions of that fix directly against the real, compiled XAML: neither header
    /// element is <c>IsEnabled="False"</c>, and each now opens onto its own nested <c>&lt;MenuItem&gt;</c>
    /// children (the old shape was a single self-closing <c>/&gt;</c> — an element with children cannot be
    /// self-closing) instead of the old flat, disabled pseudo-label run.
    /// </summary>
    [Fact]
    public void Fixed_S17_ContentAndArrangeIconsHeaders_AreEnabledSubmenuHeaders_NotDisabledPseudoLabels()
    {
        var repoRoot = RepoRootLocator.Find();
        var childWindowXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DesktopOrganizer.UI", "Views", "ChildWindow.xaml"));

        // Each header now opens a real element (not self-closing) — captures up to its own matching
        // </MenuItem>, tolerating one level of nested MenuItem children in between (non-greedy).
        var contentHeaderMatch = System.Text.RegularExpressions.Regex.Match(
            childWindowXaml,
            "<MenuItem Header=\"\\{loc:Loc Panel_Menu_ContentHeader\\}\"[^>]*>.*?</MenuItem>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var arrangeHeaderMatch = System.Text.RegularExpressions.Regex.Match(
            childWindowXaml,
            "<MenuItem Header=\"\\{loc:Loc Panel_Menu_ArrangeIconsHeader\\}\"[^>]*>.*?</MenuItem>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.True(contentHeaderMatch.Success, "Panel_Menu_ContentHeader MenuItem not found (or still self-closing) in ChildWindow.xaml — S-17 fix regressed.");
        Assert.True(arrangeHeaderMatch.Success, "Panel_Menu_ArrangeIconsHeader MenuItem not found (or still self-closing) in ChildWindow.xaml — S-17 fix regressed.");

        foreach (var element in new[] { contentHeaderMatch.Value, arrangeHeaderMatch.Value })
        {
            // Only the OPENING tag matters for IsEnabled — the nested children have their own.
            var openingTag = element[..element.IndexOf('>')];
            Assert.DoesNotContain("IsEnabled=\"False\"", openingTag);
            Assert.Contains("<MenuItem", element[(openingTag.Length + 1)..]); // has at least one nested child item
        }
    }
}
