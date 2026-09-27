using System.Windows;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="HoverOverlayPolicy"/> (MAJOR-3, docs/PLANS/workspace-grid-07-visual.md
/// fix-cycle 1) — pure direction-selection logic, no <see cref="System.Windows.Media.Color"/> compositing
/// of its own (see <c>HoverOverlayBrushConverterTests</c> and
/// <see cref="AmbientAccentContrastAuditTests"/> for the end-to-end contrast claim).
/// </summary>
[Trait(UnitTests, "UI")]
public class HoverOverlayPolicyTests
{
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.1, T-C2)
    // — PART_SurfaceHighlight (D-11) must not change HoverOverlayPolicy's own darken/lighten DECISION.
    // Same "duplicated by hand, real production constants where they exist" convention
    // AmbientAccentContrastAuditTests already establishes for its own NeutralSurfaceBase/AccentNeutral
    // mirrors — this class had none of its own before, since it never previously needed
    // SurfaceComposer/AccentPalette. Placed at the very top of the class (fields/static members before
    // the pre-existing instance [Theory]/[Fact] tests below) to satisfy StyleCop's static-before-instance
    // member ordering.
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    private static readonly Color NeutralSurfaceBase =
        (Color)ColorConverter.ConvertFromString(PanelSurfaceBrushConverter.NeutralSurfaceHex)!;

    private static readonly Color HighlightTop = LoadColorChromeHighlightTop();

    /// <summary>
    /// Provides every combination of accent hex value (Neutral plus all presets) and
    /// <see cref="PanelSurfaceStyle"/> as theory data for the surface-highlight direction sweep below.
    /// </summary>
    public static IEnumerable<object[]> AccentStyleCombinations()
    {
        var accents = new List<string> { AccentPalette.Neutral };
        accents.AddRange(AccentPalette.Presets);

        foreach (var accent in accents)
        {
            foreach (PanelSurfaceStyle style in Enum.GetValues<PanelSurfaceStyle>())
                yield return new object[] { accent, style };
        }
    }

    /// <summary>
    /// Verifies that a relative luminance below the dark-surface threshold returns
    /// <see cref="HoverOverlayPolicy.DarkenWash"/>.
    /// </summary>
    /// <param name="luminance">The relative luminance value under test.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.4999)]
    public void For_LuminanceBelowThreshold_ReturnsDarkenWash(double luminance)
    {
        Assert.Equal(HoverOverlayPolicy.DarkenWash, HoverOverlayPolicy.For(luminance));
    }

    /// <summary>
    /// MAJOR-3 fix (review round, docs/PLANS/color-presets-and-size-step.md): verifies that a relative
    /// luminance at or above the dark-surface threshold returns <see cref="HoverOverlayPolicy.LightSurfaceDarkenWash"/>,
    /// NOT <see cref="HoverOverlayPolicy.LightenWash"/> — lightening an already near-white surface
    /// (<see cref="SurfaceComposer.LightNeutralBase"/>) is imperceptible.
    /// </summary>
    /// <param name="luminance">The relative luminance value under test.</param>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.5001)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void For_LuminanceAtOrAboveThreshold_ReturnsLightSurfaceDarkenWash_NotLightenWash(double luminance)
    {
        var result = HoverOverlayPolicy.For(luminance);

        Assert.Equal(HoverOverlayPolicy.LightSurfaceDarkenWash, result);
        Assert.NotEqual(HoverOverlayPolicy.LightenWash, result);
    }

    /// <summary>
    /// Verifies that a NaN luminance value is treated as dark, returning
    /// <see cref="HoverOverlayPolicy.DarkenWash"/> instead of throwing.
    /// </summary>
    [Fact]
    public void For_NaN_TreatsAsDark_DoesNotThrow()
    {
        Assert.Equal(HoverOverlayPolicy.DarkenWash, HoverOverlayPolicy.For(double.NaN));
    }

    // --- NEW-1 fix (docs/PLANS/release-polish-1.0.md) — ForItemTile ------------------------------------

    /// <summary>
    /// NEW-1 fix: verifies that a relative luminance below the dark-surface threshold returns
    /// <see cref="HoverOverlayPolicy.LightenWash"/> — unlike <see cref="HoverOverlayPolicy.For"/>, which
    /// returns <see cref="HoverOverlayPolicy.DarkenWash"/> there (nearly invisible over
    /// <c>PanelItemContainerStyle</c>'s own dark surfaces).
    /// </summary>
    /// <param name="luminance">The relative luminance value under test.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.4999)]
    public void ForItemTile_LuminanceBelowThreshold_ReturnsLightenWash(double luminance)
    {
        Assert.Equal(HoverOverlayPolicy.LightenWash, HoverOverlayPolicy.ForItemTile(luminance));
    }

    /// <summary>
    /// NEW-1 fix: verifies that a relative luminance at or above the dark-surface threshold returns
    /// <see cref="HoverOverlayPolicy.LightSurfaceDarkenWash"/>, the SAME light-surface wash
    /// <see cref="HoverOverlayPolicy.For"/> returns there.
    /// </summary>
    /// <param name="luminance">The relative luminance value under test.</param>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.5001)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void ForItemTile_LuminanceAtOrAboveThreshold_ReturnsLightSurfaceDarkenWash(double luminance)
    {
        Assert.Equal(HoverOverlayPolicy.LightSurfaceDarkenWash, HoverOverlayPolicy.ForItemTile(luminance));
    }

    /// <summary>
    /// NEW-1 fix: verifies that a NaN luminance value is treated as dark, returning
    /// <see cref="HoverOverlayPolicy.LightenWash"/> instead of throwing.
    /// </summary>
    [Fact]
    public void ForItemTile_NaN_TreatsAsDark_DoesNotThrow()
    {
        Assert.Equal(HoverOverlayPolicy.LightenWash, HoverOverlayPolicy.ForItemTile(double.NaN));
    }

    /// <summary>
    /// MAJOR-3 fix: <see cref="HoverOverlayPolicy.LightSurfaceDarkenWash"/> is deliberately WEAKER
    /// (lower alpha) than <see cref="HoverOverlayPolicy.DarkenWash"/> — both are black washes, but the
    /// light-surface one only needs to be perceptible, not as strong as the dark-surface one.
    /// </summary>
    [Fact]
    public void LightSurfaceDarkenWash_IsWeakerThanDarkenWash_BothBlack()
    {
        Assert.True(HoverOverlayPolicy.LightSurfaceDarkenWash.A < HoverOverlayPolicy.DarkenWash.A);
        Assert.Equal(0, HoverOverlayPolicy.LightSurfaceDarkenWash.R);
        Assert.Equal(0, HoverOverlayPolicy.LightSurfaceDarkenWash.G);
        Assert.Equal(0, HoverOverlayPolicy.LightSurfaceDarkenWash.B);
    }

    /// <summary>
    /// MAJOR-3 fix: verifies that darkening <see cref="SurfaceComposer.LightNeutralBase"/> by
    /// <see cref="HoverOverlayPolicy.LightSurfaceDarkenWash"/> keeps the light-surface chrome's own
    /// "Secondary" text/icon token (<c>#666666</c>) at/above <see cref="ContrastCalculator.MinimumIconContrast"/>.
    /// </summary>
    [Fact]
    public void LightSurfaceDarkenWash_OverLightNeutralBase_KeepsLightSecondaryChromeReadable()
    {
        var darkened = ContrastCalculator.CompositeOverOpaque(HoverOverlayPolicy.LightSurfaceDarkenWash, SurfaceComposer.LightNeutralBase);
        var contrast = ContrastCalculator.ContrastRatio(PanelChromeForegroundPolicy.LightSecondary, darkened);

        Assert.True(contrast >= ContrastCalculator.MinimumIconContrast, $"expected >= {ContrastCalculator.MinimumIconContrast}:1; got {contrast:F2}.");
    }

    /// <summary>
    /// Verifies that <see cref="HoverOverlayPolicy.LightenWash"/> still matches the pre-fix-cycle-1
    /// <c>Color.Overlay.Hover</c> XAML token value.
    /// </summary>
    [Fact]
    public void LightenWash_MatchesThePreFixCycleOneColorOverlayHoverToken()
    {
        // Colors.xaml's Color.Overlay.Hover = #1AFFFFFF — kept in sync by comment, same duplication
        // convention AmbientAccentContrastAuditTests already uses for its own XAML-only token mirrors.
        Assert.Equal(System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), HoverOverlayPolicy.LightenWash);
    }

    /// <summary>
    /// T-C2: for the same 21 accent×style combinations <see cref="AmbientAccentContrastAuditTests"/>'
    /// own T-C1 sweeps (opacity is irrelevant here — <see cref="HoverOverlayPolicy.For"/> only ever reads
    /// <see cref="ContrastCalculator.RelativeLuminance"/> of the panel's own RGB mix, which
    /// <see cref="SurfaceComposer.ComposeSurface"/> never varies by opacity, per this class' own remarks
    /// above), compositing <see cref="HighlightTop"/> over the panel's own resting surface colour (the
    /// same "ignore the surface's own alpha, read RGB only" convention <see cref="HoverOverlayPolicy"/>'s
    /// own remarks document) must not flip the darken/lighten decision <see cref="HoverOverlayPolicy.For"/>
    /// returns.
    /// </summary>
    /// <param name="accentHex">The accent hex value under test.</param>
    /// <param name="style">The surface style under test.</param>
    [Theory]
    [MemberData(nameof(AccentStyleCombinations))]
    public void HoverOverlayDirection_IsUnaffectedBySurfaceHighlight(string accentHex, PanelSurfaceStyle style)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var surface = SurfaceComposer.ComposeSurface(NeutralSurfaceBase, accent, style, 0.8);
        var highlighted = ContrastCalculator.CompositeOverOpaque(HighlightTop, surface);

        var baselineWash = HoverOverlayPolicy.For(ContrastCalculator.RelativeLuminance(surface));
        var highlightedWash = HoverOverlayPolicy.For(ContrastCalculator.RelativeLuminance(highlighted));

        Assert.Equal(
            baselineWash,
            highlightedWash);
    }

    /// <summary>
    /// ADR-030 T-C2 — loads the REAL <c>Color.Chrome.Highlight.Top</c> resource from the compiled
    /// Colors.xaml (same convention <c>AmbientAccentContrastAuditTests</c>' own identically-named helper
    /// uses). Declared last (private, StyleCop's public-before-private method ordering).
    /// </summary>
    private static Color LoadColorChromeHighlightTop()
    {
        return StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var colors = new ResourceDictionary
            {
                Source = new Uri("/DesktopOrganizer.UI;component/Resources/Styles/Colors.xaml", UriKind.Relative),
            };
            return (Color)colors["Color.Chrome.Highlight.Top"];
        });
    }
}
