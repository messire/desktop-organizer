using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="ContrastCalculator"/> — the pure WCAG 2.x contrast-ratio math backing
/// docs/PLANS/workspace-grid-07-visual.md's "Contrast" table. Reference values (black/white = 21.0,
/// identical colours = 1.0) are WCAG's own canonical examples
/// (<see href="https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio"/>).
/// </summary>
[Trait(UnitTests, "UI")]
public class ContrastCalculatorTests
{
    private static readonly Color Black = Color.FromRgb(0x00, 0x00, 0x00);
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

    // --- RelativeLuminance --------------------------------------------------------------------

    [Fact]
    public void RelativeLuminance_Black_IsZero()
    {
        Assert.Equal(0.0, ContrastCalculator.RelativeLuminance(Black), precision: 6);
    }

    [Fact]
    public void RelativeLuminance_White_IsOne()
    {
        Assert.Equal(1.0, ContrastCalculator.RelativeLuminance(White), precision: 6);
    }

    [Fact]
    public void RelativeLuminance_IsMonotonicWithBrightness()
    {
        var dark = ContrastCalculator.RelativeLuminance(Color.FromRgb(0x10, 0x10, 0x10));
        var mid = ContrastCalculator.RelativeLuminance(Color.FromRgb(0x80, 0x80, 0x80));
        var bright = ContrastCalculator.RelativeLuminance(Color.FromRgb(0xF0, 0xF0, 0xF0));

        Assert.True(dark < mid);
        Assert.True(mid < bright);
    }

    // --- ContrastRatio -------------------------------------------------------------------------

    [Fact]
    public void ContrastRatio_BlackVsWhite_Is21To1()
    {
        Assert.Equal(21.0, ContrastCalculator.ContrastRatio(Black, White), precision: 2);
    }

    [Fact]
    public void ContrastRatio_IdenticalColour_IsOneToOne()
    {
        var grey = Color.FromRgb(0x77, 0x77, 0x77);

        Assert.Equal(1.0, ContrastCalculator.ContrastRatio(grey, grey), precision: 6);
    }

    [Fact]
    public void ContrastRatio_IsOrderIndependent()
    {
        var a = Color.FromRgb(0x22, 0x33, 0x44);
        var b = Color.FromRgb(0xDD, 0xDD, 0xDD);

        Assert.Equal(ContrastCalculator.ContrastRatio(a, b), ContrastCalculator.ContrastRatio(b, a), precision: 9);
    }

    [Fact]
    public void ContrastRatio_NeverBelowOne()
    {
        var colors = new[]
        {
            Black, White,
            Color.FromRgb(0x12, 0x34, 0x56),
            Color.FromRgb(0xAB, 0xCD, 0xEF),
        };

        foreach (var a in colors)
        foreach (var b in colors)
            Assert.True(ContrastCalculator.ContrastRatio(a, b) >= 1.0);
    }

    // --- CompositeOverOpaque -------------------------------------------------------------------

    [Fact]
    public void CompositeOverOpaque_FullyOpaqueForeground_IgnoresBackground()
    {
        var foreground = Color.FromArgb(255, 0x11, 0x22, 0x33);
        var background = Color.FromRgb(0xFF, 0xFF, 0xFF);

        var result = ContrastCalculator.CompositeOverOpaque(foreground, background);

        Assert.Equal(foreground.R, result.R);
        Assert.Equal(foreground.G, result.G);
        Assert.Equal(foreground.B, result.B);
        Assert.Equal(255, result.A);
    }

    [Fact]
    public void CompositeOverOpaque_FullyTransparentForeground_ReturnsBackground()
    {
        var foreground = Color.FromArgb(0, 0x11, 0x22, 0x33);
        var background = Color.FromRgb(0xAA, 0xBB, 0xCC);

        var result = ContrastCalculator.CompositeOverOpaque(foreground, background);

        Assert.Equal(background.R, result.R);
        Assert.Equal(background.G, result.G);
        Assert.Equal(background.B, result.B);
    }

    [Fact]
    public void CompositeOverOpaque_HalfAlpha_AveragesChannels()
    {
        var foreground = Color.FromArgb(128, 0xFF, 0x00, 0x00);
        var background = Color.FromRgb(0x00, 0x00, 0x00);

        var result = ContrastCalculator.CompositeOverOpaque(foreground, background);

        // 128/255 ≈ 0.502 -> R ≈ 128, within rounding.
        Assert.InRange(result.R, 126, 129);
        Assert.Equal(0, result.G);
        Assert.Equal(0, result.B);
    }

    [Fact]
    public void CompositeOverOpaque_ResultIsAlwaysOpaque()
    {
        var foreground = Color.FromArgb(10, 0x11, 0x22, 0x33);
        var background = Color.FromRgb(0xFF, 0xFF, 0xFF);

        var result = ContrastCalculator.CompositeOverOpaque(foreground, background);

        Assert.Equal(255, result.A);
    }

    // --- ContrastAgainstComposited --------------------------------------------------------------

    [Fact]
    public void ContrastAgainstComposited_OpaqueSurface_SameAsDirectContrast()
    {
        var text = White;
        var surface = Color.FromArgb(255, 0x1E, 0x1E, 0x1E);
        var wallpaper = Color.FromRgb(0xF0, 0xF0, 0xF0);

        var viaComposite = ContrastCalculator.ContrastAgainstComposited(text, surface, wallpaper);
        var direct = ContrastCalculator.ContrastRatio(text, surface);

        Assert.Equal(direct, viaComposite, precision: 9);
    }

    [Fact]
    public void ContrastAgainstComposited_LowOpacitySurfaceOverLightWallpaper_DegradesContrast()
    {
        var text = Color.FromRgb(0xDD, 0xDD, 0xDD); // Color.Text.Primary
        var darkSurfaceOpaque = Color.FromArgb(255, 0x1E, 0x1E, 0x1E);
        var darkSurfaceTranslucent = Color.FromArgb(89, 0x1E, 0x1E, 0x1E); // ~0.35 opacity floor
        var lightWallpaper = Color.FromRgb(0xF0, 0xF0, 0xF0);

        var opaqueContrast = ContrastCalculator.ContrastAgainstComposited(text, darkSurfaceOpaque, lightWallpaper);
        var translucentContrast = ContrastCalculator.ContrastAgainstComposited(text, darkSurfaceTranslucent, lightWallpaper);

        Assert.True(translucentContrast < opaqueContrast);
    }

    // --- Threshold constants -----------------------------------------------------------------

    [Fact]
    public void MinimumTextContrast_IsWcagAaBodyTextThreshold()
    {
        Assert.Equal(4.5, ContrastCalculator.MinimumTextContrast);
    }

    [Fact]
    public void MinimumIconContrast_IsWcagAaGraphicalObjectThreshold()
    {
        Assert.Equal(3.0, ContrastCalculator.MinimumIconContrast);
    }
}
