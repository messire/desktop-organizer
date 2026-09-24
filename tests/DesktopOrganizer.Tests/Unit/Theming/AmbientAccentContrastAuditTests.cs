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

    // --- Default opacity (0.8, ChildWindowModel's own default SurfaceOpacity) passes today ----------

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

    // --- KnownGap 1: PanelSurfaceStyle.Glass cannot reach the text target over a light wallpaper -----
    // at ANY opacity — GlassOpacityFactor (0.60) caps its alpha well below what a light backdrop needs.
    // Not fixed in this pass (see docs/PLANS/workspace-grid-07-visual.md's Contrast section): Glass is
    // deliberately "more see-through" (ADR-025 R-8) and lowering its ceiling further would defeat that
    // style's whole purpose; the surface tokens' theming stays as-is here, this is an accepted trade-off.

    // MINOR fix (fix-cycle 1): was a single [Fact] against Accent.Neutral only, with a comment claiming
    // "pinned range across all 7 accent presets" that nothing actually verified — now a real [Theory]
    // over Neutral + all 6 AccentPalette.Presets.
    public static IEnumerable<object[]> AllAccentHexValues()
    {
        yield return new object[] { AccentPalette.Neutral };
        foreach (var preset in AccentPalette.Presets)
            yield return new object[] { preset };
    }

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
}
