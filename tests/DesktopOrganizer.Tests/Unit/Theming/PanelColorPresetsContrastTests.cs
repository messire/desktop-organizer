using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Swatch-level contrast audit for <see cref="PanelColorPresets"/> (ADR-042) — a preset's mode texts
/// (<see cref="PanelColorPreset.DarkModeTextHex"/>/<see cref="PanelColorPreset.LightModeTextHex"/>) painted
/// directly on a SOLID <see cref="PanelColorPreset.AccentHex"/> square (the "Aa" sample swatch each preset
/// renders, <c>Controls/PanelColorEditor.xaml</c>), independent of the panel's own composited surface.
/// </summary>
/// <remarks>
/// ADR-042 changes what this class asserts: the pre-ADR-042 ten-preset palette hand-picked its accents so
/// that whichever single text colour a preset shipped (always the one actually applied to a panel using
/// that preset) met <see cref="ContrastCalculator.MinimumIconContrast"/> (3:1) against the raw accent
/// square. Under ADR-042, EITHER mode's text can be applied to ANY of the twelve accents (mode is now an
/// independent user choice, <see cref="PanelThemeMode"/>), and — critically — neither mode's text ever
/// actually renders directly on the raw, unmixed accent in the LIVE panel UI: Dark-mode text sits on
/// <see cref="SurfaceComposer.DarkNeutralBase"/> (accent mixed in at most 8%, ADR-025's
/// <see cref="SurfaceComposer.AmbientAccentRatio"/>, or not at all for <see cref="DesktopOrganizer.UI.Models.PanelSurfaceStyle.Solid"/>),
/// and Light-mode text sits on <see cref="SurfaceComposer.LightNeutralBase"/> the same way — so a raw
/// accent-square contrast floor is not a real UI constraint for either mode any more (confirmed by measurement:
/// e.g. Graphite's own <see cref="PanelColorPreset.LightModeTextHex"/> (<c>#22262E</c>) scores only ~2.48:1
/// against its raw accent (<c>#596273</c>), below the old 3:1 floor, while still scoring &gt;=4.5:1 against
/// the actual <see cref="SurfaceComposer.LightNeutralBase"/> it is composited onto in the live UI — see
/// <c>SurfaceComposerTests</c>/<c>AmbientAccentContrastAuditTests</c> for that composited-surface contrast,
/// which remains the authoritative, enforced contract). This class therefore no longer enforces a 3:1
/// floor for either mode's raw-swatch contrast; it pins a much looser sanity floor (colours are not
/// near-identical/accidentally duplicated) and explicitly documents the known, accepted gap.
/// </remarks>
[Trait(UnitTests, "UI")]
public class PanelColorPresetsContrastTests
{
    /// <summary>
    /// A loose sanity floor — NOT <see cref="ContrastCalculator.MinimumIconContrast"/> (see class remarks
    /// for why that floor no longer applies to a raw accent-square swatch) — just enough to catch a gross
    /// data-entry mistake (e.g. an accidentally duplicated or swapped accent/text pair).
    /// </summary>
    private const double SanityContrastFloor = 1.5;

    /// <summary>Every preset's (Id, AccentHex, DarkModeTextHex, LightModeTextHex), read straight from <see cref="PanelColorPresets.All"/>.</summary>
    public static IEnumerable<object[]> AllPresets() =>
        PanelColorPresets.All.Select(p => new object[] { p.Id, p.AccentHex, p.DarkModeTextHex, p.LightModeTextHex });

    /// <summary>Every (preset, mode, style, opacity) combination this class's own <see cref="ComposedSurface_MeetsMinimumTextContrast_ForEveryStyleAndOpacity"/> sweeps (backlog 1.x item 4, §4.2a/§4.5).</summary>
    public static IEnumerable<object[]> AllPresetModeStyleOpacityCombinations()
    {
        var styles = new[] { PanelSurfaceStyle.Solid, PanelSurfaceStyle.Ambient, PanelSurfaceStyle.Glass };
        var opacities = new[] { SurfaceComposer.MinSurfaceOpacity, SurfaceComposer.MaxSurfaceOpacity };
        var modes = new[] { PanelThemeMode.Dark, PanelThemeMode.Light };

        foreach (var preset in PanelColorPresets.All)
        {
            foreach (var mode in modes)
            {
                foreach (var style in styles)
                {
                    foreach (var opacity in opacities)
                        yield return new object[] { preset.Id, preset.AccentHex, preset.TextFor(mode), mode, style, opacity };
                }
            }
        }
    }

    /// <summary>
    /// Verifies that both of every preset's mode texts are at least loosely distinguishable
    /// (<see cref="SanityContrastFloor"/>) from its own solid accent — not a real UI contrast requirement
    /// (see class remarks), just a guard against an accidental data-entry duplicate.
    /// </summary>
    /// <param name="id">The preset's <see cref="PanelColorPreset.Id"/> (theory data readability only).</param>
    /// <param name="accentHex">The preset's accent hex value — the swatch's solid background.</param>
    /// <param name="darkModeTextHex">The preset's dark-mode text hex value.</param>
    /// <param name="lightModeTextHex">The preset's light-mode text hex value.</param>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Swatch_BothModeTexts_AreSanelyDistinctFromTheAccent(string id, string accentHex, string darkModeTextHex, string lightModeTextHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var darkText = (Color)ColorConverter.ConvertFromString(darkModeTextHex)!;
        var lightText = (Color)ColorConverter.ConvertFromString(lightModeTextHex)!;

        var darkContrast = ContrastCalculator.ContrastRatio(darkText, accent);
        var lightContrast = ContrastCalculator.ContrastRatio(lightText, accent);

        Assert.True(darkContrast >= SanityContrastFloor, $"{id}: dark-mode swatch contrast {darkContrast:F2} looks like a data-entry mistake (accent {accentHex}, text {darkModeTextHex}).");
        Assert.True(lightContrast >= SanityContrastFloor, $"{id}: light-mode swatch contrast {lightContrast:F2} looks like a data-entry mistake (accent {accentHex}, text {lightModeTextHex}).");
    }

    /// <summary>
    /// Documents the known, accepted gap (see class remarks): <see cref="PanelColorPresets.Amber"/>'s own
    /// <see cref="PanelColorPreset.DarkModeTextHex"/> (backlog 1.x item 4: its own pale amber-hued tint,
    /// no longer the pre-#4 shared <c>#FFFFFF</c>) does not meet <see cref="ContrastCalculator.MinimumIconContrast"/>
    /// against its own raw accent square — pinned so a future accidental change is caught, not silently
    /// "fixed" by chance.
    /// </summary>
    [Fact]
    public void KnownGap_DarkModeTextOnSolidAccent_AmberFallsBelowMinimumIconContrast()
    {
        var accent = (Color)ColorConverter.ConvertFromString(PanelColorPresets.Amber.AccentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(PanelColorPresets.Amber.DarkModeTextHex)!;

        var contrast = ContrastCalculator.ContrastRatio(text, accent);

        Assert.True(contrast < ContrastCalculator.MinimumIconContrast, $"Expected the known gap (< 3:1); got {contrast:F2}.");
        Assert.InRange(contrast, 1.7, 2.2);
    }

    /// <summary>
    /// Documents the known, accepted gap (see class remarks): <see cref="PanelColorPresets.Graphite"/>'s
    /// own <see cref="PanelColorPreset.LightModeTextHex"/> does not meet
    /// <see cref="ContrastCalculator.MinimumIconContrast"/> against its own raw accent square, even though
    /// (per <c>SurfaceComposerTests</c>) it comfortably meets <see cref="ContrastCalculator.MinimumTextContrast"/>
    /// against the actual <see cref="SurfaceComposer.LightNeutralBase"/> it is composited onto in the live UI.
    /// </summary>
    [Fact]
    public void KnownGap_LightModeTextOnSolidAccent_GraphiteFallsBelowMinimumIconContrast()
    {
        var accent = (Color)ColorConverter.ConvertFromString(PanelColorPresets.Graphite.AccentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(PanelColorPresets.Graphite.LightModeTextHex)!;

        var contrast = ContrastCalculator.ContrastRatio(text, accent);

        Assert.True(contrast < ContrastCalculator.MinimumIconContrast, $"Expected the known gap (< 3:1); got {contrast:F2}.");
        Assert.InRange(contrast, 2.2, 2.7);
    }

    /// <summary>
    /// Verifies that the SAME <see cref="PanelColorPresets.Graphite"/> light-mode text meets
    /// <see cref="ContrastCalculator.MinimumTextContrast"/> against <see cref="SurfaceComposer.LightNeutralBase"/>
    /// — the composited surface it is ACTUALLY rendered on in the live UI — proving the raw-swatch gap
    /// above is cosmetic (swatch preview only), not a real readability regression.
    /// </summary>
    [Fact]
    public void GraphiteLightModeText_StillMeetsMinimumTextContrast_AgainstTheActualComposedSurface()
    {
        var text = (Color)ColorConverter.ConvertFromString(PanelColorPresets.Graphite.LightModeTextHex)!;

        var contrast = ContrastCalculator.ContrastRatio(text, SurfaceComposer.LightNeutralBase);

        Assert.True(contrast >= ContrastCalculator.MinimumTextContrast, $"Expected >= {ContrastCalculator.MinimumTextContrast}:1; got {contrast:F2}.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Backlog 1.x item 4 (§4.2a/§4.5): the ACTUAL enforced contract for every preset's mode text — unlike
    // the raw-accent-square sanity check above, this is the real composited surface a panel's text sits on
    // (SurfaceComposer.ComposeSurface), which must clear ContrastCalculator.MinimumTextContrast (4.5:1) for
    // EVERY PanelSurfaceStyle at both SurfaceComposer.MinSurfaceOpacity and MaxSurfaceOpacity, for BOTH
    // PanelThemeMode values. ContrastCalculator.ContrastRatio ignores the alpha channel entirely (it always
    // treats its input as already-opaque), so — by construction of SurfaceComposer.ComposeSurface, whose
    // RGB mix ratio (AmbientAccentRatio) does not depend on the opacity parameter at all — this sweep's
    // result is identical at MinSurfaceOpacity and MaxSurfaceOpacity for a given (preset, mode, style); both
    // are still exercised explicitly, so a future change coupling opacity into the RGB mix would be caught
    // here rather than silently assumed safe.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that every preset's mode text, composited via <see cref="SurfaceComposer.ComposeSurface"/>
    /// onto the neutral base <see cref="SurfaceComposer.ResolveNeutralBase"/> resolves for that text's own
    /// <see cref="PanelThemeMode"/>, meets <see cref="ContrastCalculator.MinimumTextContrast"/> (4.5:1) for
    /// every <see cref="PanelSurfaceStyle"/> at both <see cref="SurfaceComposer.MinSurfaceOpacity"/> and
    /// <see cref="SurfaceComposer.MaxSurfaceOpacity"/> — the real, enforced DoD for backlog 1.x item 4
    /// (§4.2a), as opposed to this class's own raw-accent-square sanity check above.
    /// </summary>
    /// <param name="id">The preset's <see cref="PanelColorPreset.Id"/> (failure message readability only).</param>
    /// <param name="accentHex">The preset's accent hex value.</param>
    /// <param name="textHex">The preset's text hex value for <paramref name="mode"/>.</param>
    /// <param name="mode">The <see cref="PanelThemeMode"/> under test.</param>
    /// <param name="style">The <see cref="PanelSurfaceStyle"/> under test.</param>
    /// <param name="opacity">The surface opacity under test.</param>
    [Theory]
    [MemberData(nameof(AllPresetModeStyleOpacityCombinations))]
    public void ComposedSurface_MeetsMinimumTextContrast_ForEveryStyleAndOpacity(
        string id, string accentHex, string textHex, PanelThemeMode mode, PanelSurfaceStyle style, double opacity)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(textHex)!;
        var neutralBase = SurfaceComposer.ResolveNeutralBase(accent, text);

        var surface = SurfaceComposer.ComposeSurface(neutralBase, accent, style, opacity);
        var contrast = ContrastCalculator.ContrastRatio(text, surface);

        Assert.True(
            contrast >= ContrastCalculator.MinimumTextContrast,
            $"{id} ({mode}, {style} @ {opacity:F2}) scored {contrast:F2}, below the {ContrastCalculator.MinimumTextContrast}:1 target.");
    }
}
