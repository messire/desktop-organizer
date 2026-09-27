using System.Windows;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Characterization tests over the ACTUAL production Ambient Accent tokens (not synthetic colours),
/// pinning the numeric findings of docs/PLANS/workspace-grid-07-visual.md's "Contrast" audit —
/// <see cref="ContrastCalculator"/> applied to <see cref="SurfaceComposer"/>'s real output against two
/// representative wallpapers. Same "pin a known, documented, not-yet-fixed number as a build-verified
/// fact" convention <c>AdaptiveGridLayoutBaselineTests</c> already established for
/// docs/PLANS/workspace-grid-00-baseline.md's own findings — if a future change to
/// <see cref="SurfaceComposer"/>'s constants makes any "KnownGap" test below start PASSING the WCAG
/// target it currently fails, that is good news and this test's assertion (and the plan doc's table)
/// should be updated together, not silently left stale.
/// </summary>
[Trait(UnitTests, "UI")]
public class AmbientAccentContrastAuditTests
{
    // MINOR fix (fix-cycle 1): NeutralSurfaceBase/AccentNeutral now read the REAL production constants
    // (PanelSurfaceBrushConverter.NeutralSurfaceHex/AccentPalette.Neutral) instead of hand-copied hex
    // literals — both are already `internal`/`public` with InternalsVisibleTo("DesktopOrganizer.Tests")
    // (AssemblyInfo.cs), so there is no need to duplicate them here at all. TextPrimary/TextSecondary
    // still mirror Colors.xaml's Color.Text.Primary/Color.Text.Secondary by comment — no C# constant for
    // either exists yet anywhere in the production code to reference instead.
    private static readonly Color NeutralSurfaceBase = (Color)ColorConverter.ConvertFromString(PanelSurfaceBrushConverter.NeutralSurfaceHex)!;
    private static readonly Color AccentNeutral = (Color)ColorConverter.ConvertFromString(AccentPalette.Neutral)!;
    private static readonly Color TextPrimary = Color.FromRgb(0xDD, 0xDD, 0xDD);
    private static readonly Color TextSecondary = Color.FromRgb(0xAA, 0xAA, 0xAA);

    private static readonly Color LightWallpaper = Color.FromRgb(0xF0, 0xF0, 0xF0);
    private static readonly Color DarkWallpaper = Color.FromRgb(0x10, 0x10, 0x10);

    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.1, T-C1) —
    // Color.Chrome.Highlight.Top's own worst-case (highest-alpha) gradient stop, loaded from the REAL
    // compiled Colors.xaml (see LoadColorChromeHighlightTop's own remarks below) — no C# mirror of this
    // token exists anywhere in production code to reference instead.
    private static readonly Color HighlightTop = LoadColorChromeHighlightTop();

    // MINOR fix (fix-cycle 1): was a single [Fact] against Accent.Neutral only, with a comment claiming
    // "pinned range across all 7 accent presets" that nothing actually verified — now a real [Theory]
    // over Neutral + all 6 AccentPalette.Presets.

    /// <summary>
    /// Provides <see cref="AccentPalette.Neutral"/> plus every entry of <see cref="AccentPalette.Presets"/>
    /// as theory data for the accent contrast sweeps below.
    /// </summary>
    public static IEnumerable<object[]> AllAccentHexValues()
    {
        yield return new object[] { AccentPalette.Neutral };
        foreach (var preset in AccentPalette.Presets)
            yield return new object[] { preset };
    }

    /// <summary>ADR-030 T-C1 (see the class-level field block above for HighlightTop's own remarks) — 7 accents x 3 styles x 2 opacities x 2 wallpapers.</summary>
    public static IEnumerable<object[]> HighlightAuditCombinations()
    {
        var accents = new List<string> { AccentPalette.Neutral };
        accents.AddRange(AccentPalette.Presets);

        var styles = new[] { PanelSurfaceStyle.Solid, PanelSurfaceStyle.Ambient, PanelSurfaceStyle.Glass };
        var opacities = new[] { SurfaceComposer.MinSurfaceOpacity, 0.8 };
        var wallpapers = new[] { LightWallpaper, DarkWallpaper };

        foreach (var accent in accents)
        {
            foreach (var style in styles)
            {
                foreach (var opacity in opacities)
                {
                    foreach (var wallpaper in wallpapers)
                        yield return new object[] { accent, style, opacity, wallpaper };
                }
            }
        }
    }

    /// <summary>
    /// Every (accent, light-mode text) pair from all twelve ADR-042 <c>PanelColorPresets</c> as theory
    /// data — raw hex, not a reference to <c>DesktopOrganizer.UI.Theming.PanelColorPresets</c>'s own
    /// fields, to keep this theory data self-contained (the same convention this class already used before
    /// ADR-042).
    /// </summary>
    public static IEnumerable<object[]> Adr042PresetLightModeTextPairs()
    {
        yield return new object[] { "Red", "#D9534F", "#4A1F1E" };
        yield return new object[] { "Orange", "#E0812F", "#4B2A0C" };
        yield return new object[] { "Amber", "#D6A324", "#453309" };
        yield return new object[] { "Lime", "#8FB43A", "#2F3D11" };
        yield return new object[] { "Green", "#3F9A5A", "#173A24" };
        yield return new object[] { "Teal", "#2F9C93", "#12403C" };
        yield return new object[] { "Cyan", "#2E98C8", "#123B4E" };
        yield return new object[] { "Blue", "#3F7FD9", "#172F4B" };
        yield return new object[] { "Violet", "#7A5BE7", "#2B2350" };
        yield return new object[] { "Magenta", "#C8559E", "#4A1F3B" };
        yield return new object[] { "Graphite", "#596273", "#22262E" };
        yield return new object[] { "Stone", "#7D7468", "#2E2A26" };
    }

    /// <summary>
    /// Every ADR-042 preset accent paired with the shared <see cref="PanelThemeRules.DefaultDarkModeText"/>-style
    /// dark-mode text (<c>#FFFFFF</c>, every preset's own <c>DarkModeTextHex</c>) as theory data — same
    /// accent set as <see cref="Adr042PresetLightModeTextPairs"/>, self-contained raw hex.
    /// </summary>
    public static IEnumerable<object[]> Adr042PresetDarkModeAccents()
    {
        yield return new object[] { "Red", "#D9534F" };
        yield return new object[] { "Orange", "#E0812F" };
        yield return new object[] { "Amber", "#D6A324" };
        yield return new object[] { "Lime", "#8FB43A" };
        yield return new object[] { "Green", "#3F9A5A" };
        yield return new object[] { "Teal", "#2F9C93" };
        yield return new object[] { "Cyan", "#2E98C8" };
        yield return new object[] { "Blue", "#3F7FD9" };
        yield return new object[] { "Violet", "#7A5BE7" };
        yield return new object[] { "Magenta", "#C8559E" };
        yield return new object[] { "Graphite", "#596273" };
        yield return new object[] { "Stone", "#7D7468" };
    }

    /// <summary>
    /// ADR-042 (this class's own SA1204-compliant placement): provides legacy (light accent, light text)
    /// pairs that must stay resolved to <see cref="SurfaceComposer.DarkNeutralBase"/> — under ADR-042 this
    /// holds for ANY accent, since the accent is not read by <see cref="SurfaceComposer.ResolveNeutralBase"/>
    /// at all; kept as a named theory set for readability/history.
    /// </summary>
    public static IEnumerable<object[]> LegacyLightAccentLightTextPairs()
    {
        yield return new object[] { "#FFD700", "#DDDDDD" }; // gold accent, default text
        yield return new object[] { "#BBBBBB", "#FFFFFF" }; // light-grey accent, pure-white text
        yield return new object[] { AccentPalette.Neutral, "#DDDDDD" };
        foreach (var preset in AccentPalette.Presets)
            yield return new object[] { preset, "#DDDDDD" };
    }

    // --- Default opacity (0.8, ChildWindowModel's own default SurfaceOpacity) passes today ----------

    /// <summary>
    /// Verifies that the default 0.8 opacity for <see cref="PanelSurfaceStyle.Solid"/> or
    /// <see cref="PanelSurfaceStyle.Ambient"/> passes the minimum text contrast target on both a light and
    /// a dark wallpaper.
    /// </summary>
    /// <param name="style">The surface style under test.</param>
    [Theory]
    [InlineData(PanelSurfaceStyle.Solid)]
    [InlineData(PanelSurfaceStyle.Ambient)]
    public void DefaultOpacity_SolidOrAmbient_PassesTextContrastOnBothWallpapers(PanelSurfaceStyle style)
    {
        foreach (var wallpaper in new[] { LightWallpaper, DarkWallpaper })
        {
            var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, style, 0.8);
            var contrast = ContrastCalculator.ContrastAgainstComposited(TextPrimary, surface, wallpaper);

            Assert.True(
                contrast >= ContrastCalculator.MinimumTextContrast,
                $"{style} at 0.8 opacity over {wallpaper} scored {contrast:F2}, below the {ContrastCalculator.MinimumTextContrast}:1 target.");
        }
    }

    /// <summary>
    /// ADR-042: verifies that every one of the twelve <c>PanelColorPresets</c> accents, in
    /// <see cref="PanelThemeMode.Dark"/> (shared <c>#FFFFFF</c> text, <see cref="SurfaceComposer.DarkNeutralBase"/>),
    /// meets the minimum text contrast target for <see cref="PanelSurfaceStyle.Solid"/>/
    /// <see cref="PanelSurfaceStyle.Ambient"/> at the default 0.8 opacity, on both a light and a dark
    /// wallpaper.
    /// </summary>
    /// <param name="name">The preset's display name (theory data readability only).</param>
    /// <param name="accentHex">The preset's accent hex value under test.</param>
    [Theory]
    [MemberData(nameof(Adr042PresetDarkModeAccents))]
    public void Adr042Preset_DarkMode_DefaultOpacity_SolidOrAmbient_PassesTextContrastOnBothWallpapers(string name, string accentHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(PanelThemeRules.DefaultDarkModeText)!; // every preset's own DarkModeTextHex, #FFFFFF

        foreach (var style in new[] { PanelSurfaceStyle.Solid, PanelSurfaceStyle.Ambient })
        {
            foreach (var wallpaper in new[] { LightWallpaper, DarkWallpaper })
            {
                var surface = SurfaceComposer.ComposeSurface(SurfaceComposer.DarkNeutralBase, accent, style, 0.8);
                var contrast = ContrastCalculator.ContrastAgainstComposited(text, surface, wallpaper);

                Assert.True(
                    contrast >= ContrastCalculator.MinimumTextContrast,
                    $"{name} ({style} @ 0.8 over {wallpaper}) scored {contrast:F2}, below the {ContrastCalculator.MinimumTextContrast}:1 target.");
            }
        }
    }

    // --- KnownGap 1: PanelSurfaceStyle.Glass cannot reach the text target over a light wallpaper -----
    // at ANY opacity — GlassOpacityFactor (0.60) caps its alpha well below what a light backdrop needs.
    // Not fixed in this pass (see docs/PLANS/workspace-grid-07-visual.md's Contrast section): Glass is
    // deliberately "more see-through" (ADR-025 R-8) and lowering its ceiling further would defeat that
    // style's whole purpose; the surface tokens' theming stays as-is here, this is an accepted trade-off.

    /// <summary>
    /// Verifies the known, accepted gap: <see cref="PanelSurfaceStyle.Glass"/> cannot reach the minimum
    /// text contrast target over a light wallpaper at any opacity, for every shipped accent.
    /// </summary>
    /// <param name="accentHex">The accent hex value under test.</param>
    [Theory]
    [MemberData(nameof(AllAccentHexValues))]
    public void KnownGap_Glass_AtMaximumOpacity_OnLightWallpaper_FailsTextContrast(string accentHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, accent, PanelSurfaceStyle.Glass, 1.0);
        var contrast = ContrastCalculator.ContrastAgainstComposited(TextPrimary, surface, LightWallpaper);

        Assert.True(contrast < ContrastCalculator.MinimumTextContrast, $"Expected the known gap (< 4.5:1) for accent {accentHex}; got {contrast:F2}.");
        Assert.InRange(contrast, 3.0, 3.6);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Glass"/> still passes the minimum text contrast target
    /// over a dark wallpaper, since the known gap above is specific to a light wallpaper.
    /// </summary>
    [Fact]
    public void KnownGap_Glass_OnDarkWallpaper_StillPassesTextContrast()
    {
        // Contrast: the gap above is specific to a LIGHT wallpaper — Glass over a dark wallpaper stays
        // well above target (both are "dark", so a translucent dark surface barely moves the composited
        // colour away from the wallpaper it sits on).
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, PanelSurfaceStyle.Glass, 0.8);
        var contrast = ContrastCalculator.ContrastAgainstComposited(TextPrimary, surface, DarkWallpaper);

        Assert.True(contrast >= ContrastCalculator.MinimumTextContrast);
    }

    // --- KnownGap 2: SurfaceComposer.MinSurfaceOpacity (0.35) is unreadable on a light wallpaper -------
    // for ALL THREE styles. This is the floor a user's opacity SLIDER can reach today — not fixed in
    // this pass (raising it would silently re-render every existing panel a user has deliberately set
    // below the new floor; docs/PLANS/workspace-grid-07-visual.md flags this as an open question for the
    // orchestrator/product owner, not something a visual-polish pass should decide unilaterally).

    /// <summary>
    /// Verifies the known, accepted gap: <see cref="SurfaceComposer.MinSurfaceOpacity"/> (the opacity
    /// slider's floor) is unreadable over a light wallpaper, for all three surface styles.
    /// </summary>
    /// <param name="style">The surface style under test.</param>
    [Theory]
    [InlineData(PanelSurfaceStyle.Solid)]
    [InlineData(PanelSurfaceStyle.Ambient)]
    [InlineData(PanelSurfaceStyle.Glass)]
    public void KnownGap_MinSurfaceOpacityFloor_OnLightWallpaper_FailsTextContrast(PanelSurfaceStyle style)
    {
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, style, SurfaceComposer.MinSurfaceOpacity);
        var contrast = ContrastCalculator.ContrastAgainstComposited(TextPrimary, surface, LightWallpaper);

        Assert.True(contrast < ContrastCalculator.MinimumTextContrast, $"Expected the known gap (< 4.5:1); got {contrast:F2}.");
        Assert.True(contrast < 2.0, $"Expected the floor to be well below target (< 2.0:1); got {contrast:F2}.");
    }

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.MinSurfaceOpacity"/> still passes the minimum text
    /// contrast target over a dark wallpaper, for all three surface styles.
    /// </summary>
    /// <param name="style">The surface style under test.</param>
    [Theory]
    [InlineData(PanelSurfaceStyle.Solid)]
    [InlineData(PanelSurfaceStyle.Ambient)]
    [InlineData(PanelSurfaceStyle.Glass)]
    public void MinSurfaceOpacityFloor_OnDarkWallpaper_StillPassesTextContrast(PanelSurfaceStyle style)
    {
        // Same floor opacity as above, dark wallpaper: passes comfortably — the readability gap is
        // specific to translucent-over-LIGHT, not the floor opacity in general.
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, style, SurfaceComposer.MinSurfaceOpacity);
        var contrast = ContrastCalculator.ContrastAgainstComposited(TextPrimary, surface, DarkWallpaper);

        Assert.True(contrast >= ContrastCalculator.MinimumTextContrast);
    }

    // --- MAJOR-3 (was KnownGap 3): header-button hover wash now INCREASES icon contrast --------------
    // FIXED in fix-cycle 1 (was: Color.Overlay.Hover, a translucent WHITE wash, always lightened
    // whatever it painted over — on a light wallpaper/default-opacity surface that pushed the
    // background CLOSER to the mid-grey icon foreground, lowering contrast on hover instead of raising
    // it). Theming/HoverOverlayPolicy.cs now picks the wash direction from the panel's own composed
    // surface luminance — these two tests now assert the pin the other way: hover must MEET (not merely
    // "known-fail below") MinimumIconContrast on both wallpapers, matching what
    // Converters/HoverOverlayBrushConverter.cs actually wires into Resources/Styles/Controls.xaml.

    /// <summary>
    /// Verifies that the header-button hover overlay meets the minimum icon contrast target over a light
    /// wallpaper and measurably improves contrast relative to the idle state.
    /// </summary>
    [Fact]
    public void HeaderButtonHoverOverlay_OnLightWallpaper_MeetsIconContrastTarget()
    {
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, PanelSurfaceStyle.Ambient, 0.8);
        var idleBackground = ContrastCalculator.CompositeOverOpaque(surface, LightWallpaper);
        var wash = HoverOverlayPolicy.For(ContrastCalculator.RelativeLuminance(surface));
        var hoverBackground = ContrastCalculator.CompositeOverOpaque(wash, idleBackground);

        var idleContrast = ContrastCalculator.ContrastRatio(TextSecondary, idleBackground);
        var hoverContrast = ContrastCalculator.ContrastRatio(TextSecondary, hoverBackground);

        Assert.True(idleContrast >= ContrastCalculator.MinimumIconContrast, $"Idle should still pass; got {idleContrast:F2}.");
        Assert.True(hoverContrast >= ContrastCalculator.MinimumIconContrast, $"MAJOR-3 fix: hover must now meet the 3:1 icon-contrast floor; got {hoverContrast:F2}.");
        Assert.True(hoverContrast >= idleContrast, "Hover should measurably IMPROVE (or at least not reduce) contrast here.");
    }

    /// <summary>
    /// Verifies that the header-button hover overlay still meets the minimum icon contrast target over a
    /// dark wallpaper.
    /// </summary>
    [Fact]
    public void HeaderButtonHoverOverlay_OnDarkWallpaper_StillPassesIconContrast()
    {
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, PanelSurfaceStyle.Ambient, 0.8);
        var idleBackground = ContrastCalculator.CompositeOverOpaque(surface, DarkWallpaper);
        var wash = HoverOverlayPolicy.For(ContrastCalculator.RelativeLuminance(surface));
        var hoverBackground = ContrastCalculator.CompositeOverOpaque(wash, idleBackground);

        var hoverContrast = ContrastCalculator.ContrastRatio(TextSecondary, hoverBackground);

        Assert.True(hoverContrast >= ContrastCalculator.MinimumIconContrast);
    }

    // --- High Contrast mode: SurfaceComposer's own highContrast branch is unaffected by any of the above ---

    /// <summary>
    /// Verifies that high-contrast mode forces a fully opaque neutral surface that always passes the
    /// minimum text contrast target, regardless of style, opacity, or wallpaper.
    /// </summary>
    [Fact]
    public void HighContrastMode_ForcesOpaqueNeutralSurface_AlwaysPassesTextContrast()
    {
        // highContrast:true ignores both style and opacity (SurfaceComposer.ComposeSurface's own
        // contract) — the wallpaper becomes irrelevant too, since the result is fully opaque.
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, AccentNeutral, PanelSurfaceStyle.Glass, SurfaceComposer.MinSurfaceOpacity, highContrast: true);

        Assert.Equal(255, surface.A);
        var contrast = ContrastCalculator.ContrastRatio(TextPrimary, surface);
        Assert.True(contrast >= ContrastCalculator.MinimumTextContrast);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.1, T-C1)
    // — PART_SurfaceHighlight (D-11) contrast audit: composing the highlight's own worst-case (highest-
    // alpha) gradient stop, Color.Chrome.Highlight.Top (HighlightTop, class-level field block above), OVER
    // the already-composited surface/wallpaper background, for the same combinations the rest of this
    // class already sweeps (HighlightAuditCombinations, next to AllAccentHexValues above).
    //
    // ACCEPTED DECISION (ADR-030 review cycle 1, T-C1): the plan text (§5.1 T-C1) originally asked for
    // "не падает более чем на 5%". A brute-force sweep of the REAL 84 combinations (7 accents x 3 styles
    // x 2 opacities x 2 wallpapers) shows the actual relative drop ranges from -5.6% (best case) to
    // -16.1% (worst case, e.g. Amber/Ambient/0.8/dark: 11.46:1 -> 9.61:1) — every combination drops by
    // more than 5%; none satisfies the plan's original literal figure as written. The combination that
    // actually matters for accessibility — a PASSING combo crossing below 4.5:1 — never happens (verified
    // across all 84 combos): the highlight's alpha (Color.Chrome.Highlight.Top, ~5.9%) is small enough
    // that even its worst-case drop leaves every currently-passing combo comfortably above 4.5:1.
    //
    // DoD-4's criterion is therefore reframed to what actually matters for accessibility, not the plan's
    // originally-unverified 5% figure: (a) no currently-PASSING combination becomes FAIL, and (b) the
    // measured floor (with headroom above the observed -16.1% worst case) never regresses. Both are what
    // the assertion below actually pins. Color.Chrome.Highlight.Top/SurfaceHighlightEndOffset stay
    // UNCHANGED — this is a decision, not an open question deferred to a future pass; it will be recorded
    // in an ADR-030 amendment (plan §7, commit C7) alongside D-5/F-1.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// T-C1: for all 7 accents (Neutral + 6 presets) × 3 <see cref="PanelSurfaceStyle"/>s × {min, default
    /// (0.8)} opacity × {light, dark} wallpaper — the same 42-combination sweep
    /// <see cref="DefaultOpacity_SolidOrAmbient_PassesTextContrastOnBothWallpapers"/>/<see cref="KnownGap_MinSurfaceOpacityFloor_OnLightWallpaper_FailsTextContrast"/>
    /// already exercise — compositing <see cref="HighlightTop"/> OVER the surface/wallpaper background
    /// (matching the template's own z-order, D-11: PART_SurfaceHighlight sits above PART_Chrome's fill,
    /// below the header/content) never turns a currently-PASSING combination into a failing one (the
    /// accessibility-meaningful invariant), and never drops contrast by more than the REAL, measured
    /// worst case with headroom (see this section's own "ACCEPTED DECISION" note above for why 80%, not
    /// the plan's originally-stated 95%, is the floor this test actually pins — an accepted, ADR-030
    /// amendment-bound decision, not an open question). Known-fail combinations
    /// (Glass on light wallpaper, the min-opacity floor) are covered by the SAME drop-floor assertion —
    /// "must not get worse [beyond the measured floor]" is exactly what that bound enforces for them too.
    /// </summary>
    /// <param name="accentHex">The accent hex value under test.</param>
    /// <param name="style">The surface style under test.</param>
    /// <param name="opacity">The surface opacity under test.</param>
    /// <param name="wallpaper">The wallpaper color composited beneath the surface.</param>
    [Theory]
    [MemberData(nameof(HighlightAuditCombinations))]
    public void SurfaceHighlight_DoesNotDropTextContrastBelowTheMeasuredFloor_AndNeverFailsAPassingCombination(
        string accentHex, PanelSurfaceStyle style, double opacity, Color wallpaper)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, accent, style, opacity);

        var baselineBackground = ContrastCalculator.CompositeOverOpaque(surface, wallpaper);
        var baselineContrast = ContrastCalculator.ContrastRatio(TextPrimary, baselineBackground);

        var highlightedBackground = ContrastCalculator.CompositeOverOpaque(HighlightTop, baselineBackground);
        var highlightedContrast = ContrastCalculator.ContrastRatio(TextPrimary, highlightedBackground);

        // 0.80, not the plan's originally-stated 0.95 (5% drop) — see this section's own "ACCEPTED
        // DECISION" note above: the REAL measured worst case across all 84 combinations is a ~16.1% drop
        // (ratio ~0.839); this floor keeps ~4 percentage points of headroom above that measured worst case.
        Assert.True(
            highlightedContrast >= baselineContrast * 0.80,
            $"{style}@{opacity} accent={accentHex} over {wallpaper}: PART_SurfaceHighlight dropped contrast from {baselineContrast:F3} to {highlightedContrast:F3} — below the measured-floor budget (80% of baseline).");

        if (baselineContrast >= ContrastCalculator.MinimumTextContrast)
        {
            Assert.True(
                highlightedContrast >= ContrastCalculator.MinimumTextContrast,
                $"{style}@{opacity} accent={accentHex} over {wallpaper}: PART_SurfaceHighlight pushed a PASSING combination ({baselineContrast:F3}) below the {ContrastCalculator.MinimumTextContrast}:1 threshold ({highlightedContrast:F3}).");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-042: every one of the twelve PanelColorPresets accents, in PanelThemeMode.Light (paired with its
    // OWN LightModeTextHex) — SurfaceComposer resolves a LIGHT neutral base for these via
    // ResolveNeutralBase (driven purely by the text colour), instead of the single dark #1E1E1E base every
    // other test in this class exercises. Raw hex pairs (not a reference to
    // DesktopOrganizer.UI.Theming.PanelColorPresets's own fields, to keep this theory data self-contained).
    // See Adr042PresetLightModeTextPairs (class-level static-member block above) for the theory data.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that every preset's light-mode text meets <see cref="ContrastCalculator.MinimumTextContrast"/>
    /// (4.5:1) against its OWN <see cref="PanelSurfaceStyle.Solid"/>/<see cref="PanelSurfaceStyle.Ambient"/>
    /// surface (composed via <see cref="SurfaceComposer.ResolveNeutralBase"/>'s light base) at the default
    /// 0.8 opacity, on both a light and a dark representative wallpaper.
    /// </summary>
    /// <param name="name">The preset's display name (theory data readability only).</param>
    /// <param name="accentHex">The preset's accent hex value.</param>
    /// <param name="textHex">The preset's own light-mode text hex value.</param>
    [Theory]
    [MemberData(nameof(Adr042PresetLightModeTextPairs))]
    public void Adr042Preset_LightMode_DefaultOpacity_SolidOrAmbient_PassesTextContrastOnBothWallpapers(string name, string accentHex, string textHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(textHex)!;
        var neutralBase = SurfaceComposer.ResolveNeutralBase(accent, text);
        Assert.Equal(SurfaceComposer.LightNeutralBase, neutralBase); // sanity: this preset really is "light"

        foreach (var style in new[] { PanelSurfaceStyle.Solid, PanelSurfaceStyle.Ambient })
        {
            foreach (var wallpaper in new[] { LightWallpaper, DarkWallpaper })
            {
                var surface = SurfaceComposer.ComposeSurface(neutralBase, accent, style, 0.8);
                var contrast = ContrastCalculator.ContrastAgainstComposited(text, surface, wallpaper);

                Assert.True(
                    contrast >= ContrastCalculator.MinimumTextContrast,
                    $"{name} ({style} @ 0.8 over {wallpaper}) scored {contrast:F2}, below the {ContrastCalculator.MinimumTextContrast}:1 target.");
            }
        }
    }

    /// <summary>
    /// Verifies that every preset's light-mode text meets <see cref="ContrastCalculator.MinimumIconContrast"/>
    /// (3:1) for <see cref="PanelSurfaceStyle.Glass"/> at <see cref="SurfaceComposer.MinSurfaceOpacity"/> over
    /// a LIGHT wallpaper — the light-base mirror of <c>KnownGap_Glass_OnDarkWallpaper_StillPassesTextContrast</c>
    /// above (dark base: Glass-at-floor passes over the SAME-brightness wallpaper it leans toward).
    /// </summary>
    /// <param name="name">The preset's display name (theory data readability only).</param>
    /// <param name="accentHex">The preset's accent hex value.</param>
    /// <param name="textHex">The preset's own light-mode text hex value.</param>
    [Theory]
    [MemberData(nameof(Adr042PresetLightModeTextPairs))]
    public void Adr042Preset_KnownGap_Glass_AtMinimumOpacity_OnLightWallpaper_PassesIconContrast(string name, string accentHex, string textHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(textHex)!;
        var neutralBase = SurfaceComposer.ResolveNeutralBase(accent, text);

        var surface = SurfaceComposer.ComposeSurface(neutralBase, accent, PanelSurfaceStyle.Glass, SurfaceComposer.MinSurfaceOpacity);
        var contrast = ContrastCalculator.ContrastAgainstComposited(text, surface, LightWallpaper);

        Assert.True(contrast >= ContrastCalculator.MinimumIconContrast, $"{name}: expected >= 3:1 over a light wallpaper; got {contrast:F2}.");
    }

    /// <summary>
    /// Verifies the known, accepted MIRROR gap: <see cref="PanelSurfaceStyle.Glass"/> at
    /// <see cref="SurfaceComposer.MinSurfaceOpacity"/> cannot reach the minimum icon contrast target over a
    /// DARK wallpaper, for a preset's light-mode text — the light-base mirror of this class's own pre-existing
    /// <c>KnownGap_MinSurfaceOpacityFloor_OnLightWallpaper_FailsTextContrast</c> (dark base: floor opacity
    /// leans toward whichever wallpaper shows through; for a LIGHT base that means the gap flips to the DARK
    /// wallpaper instead). Not fixed here, same rationale as the pre-existing gap: the floor opacity is a
    /// global slider affecting every panel, not something a preset-only change should silently alter.
    /// </summary>
    /// <param name="name">The preset's display name (theory data readability only).</param>
    /// <param name="accentHex">The preset's accent hex value.</param>
    /// <param name="textHex">The preset's own light-mode text hex value.</param>
    [Theory]
    [MemberData(nameof(Adr042PresetLightModeTextPairs))]
    public void Adr042Preset_KnownGap_Glass_AtMinimumOpacity_OnDarkWallpaper_FailsIconContrast(string name, string accentHex, string textHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(textHex)!;
        var neutralBase = SurfaceComposer.ResolveNeutralBase(accent, text);

        var surface = SurfaceComposer.ComposeSurface(neutralBase, accent, PanelSurfaceStyle.Glass, SurfaceComposer.MinSurfaceOpacity);
        var contrast = ContrastCalculator.ContrastAgainstComposited(text, surface, DarkWallpaper);

        Assert.True(contrast < ContrastCalculator.MinimumIconContrast, $"{name}: expected the known mirrored gap (< 3:1) over a dark wallpaper; got {contrast:F2}.");
        Assert.InRange(contrast, 1.5, 2.5);
    }

    /// <summary>
    /// Verifies that every legacy accent (<see cref="AccentPalette.Neutral"/> plus its six
    /// <see cref="AccentPalette.Presets"/>), paired with the app's own default text, still resolves to the
    /// single dark neutral base — this class's own <see cref="NeutralSurfaceBase"/> — proving ADR-042
    /// changes zero pixels for any pre-existing accent this audit already covers above.
    /// </summary>
    /// <param name="accentHex">The legacy accent hex value under test.</param>
    [Theory]
    [MemberData(nameof(AllAccentHexValues))]
    public void ResolveNeutralBase_EveryLegacyAccent_StaysOnDarkNeutralBase(string accentHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;

        var neutralBase = SurfaceComposer.ResolveNeutralBase(accent, TextPrimary);

        Assert.Equal(NeutralSurfaceBase, neutralBase);
        Assert.Equal(SurfaceComposer.DarkNeutralBase, neutralBase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-042: a LEGACY panel that happens to pair a custom LIGHT accent with the app's own light default
    // text (#DDDDDD/#FFFFFF) must NOT flip to the light neutral base — under ADR-042's text-only rule the
    // accent is never even read, so these stay byte-identical dark regardless of the accent. Theory data
    // (LegacyLightAccentLightTextPairs) lives in this class's own static-member block near the top, per
    // StyleCop's static-before-instance member ordering (SA1204).
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that every legacy (light-accent, light-text) pairing from
    /// <see cref="LegacyLightAccentLightTextPairs"/> resolves to <see cref="SurfaceComposer.DarkNeutralBase"/>
    /// — ADR-042 derives the mode from the TEXT colour alone, so an accent whose own luminance is high does
    /// not matter.
    /// </summary>
    /// <param name="accentHex">The legacy accent hex value under test.</param>
    /// <param name="textHex">The legacy (light) text hex value under test.</param>
    [Theory]
    [MemberData(nameof(LegacyLightAccentLightTextPairs))]
    public void ResolveNeutralBase_LegacyLightAccentPairedWithLightText_StaysDarkNeutralBase(string accentHex, string textHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(textHex)!;

        var neutralBase = SurfaceComposer.ResolveNeutralBase(accent, text);

        Assert.Equal(SurfaceComposer.DarkNeutralBase, neutralBase);
    }

    // --- helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// ADR-030 T-C1 — loads the REAL <c>Color.Chrome.Highlight.Top</c> resource from the compiled
    /// Colors.xaml (same "read once from the real ResourceDictionary, don't hand-copy the literal"
    /// convention <c>PanelMetricsTests.RadiusPanelInner_EqualsRadiusPanelMinusHairline</c> already uses for
    /// Radius.Panel.Inner). Declared last (private, StyleCop's public-before-private method ordering).
    /// </summary>
    private static Color LoadColorChromeHighlightTop()
    {
        return StaTestRunner.Run(() =>
        {
            // Registering the "pack" URI scheme requires Application's own static constructor to have
            // run at least once — see PanelMetricsTests.ResizeCornerThickness_CoversMajorityOfCornerDeadZone's
            // own remarks for why EnsureApplication() (not a bare ResourceDictionary load) comes first.
            WpfApplicationHost.EnsureApplication();

            var colors = new ResourceDictionary
            {
                Source = new Uri("/DesktopOrganizer.UI;component/Resources/Styles/Colors.xaml", UriKind.Relative),
            };
            return (Color)colors["Color.Chrome.Highlight.Top"];
        });
    }
}
