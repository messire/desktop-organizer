using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="HoverOverlayPolicy"/> (MAJOR-3, docs/PLANS/workspace-grid-07-visual.md
/// fix-cycle 1) — pure direction-selection logic, no <see cref="System.Windows.Media.Color"/> compositing
/// of its own (see <see cref="HoverOverlayBrushConverterTests"/> and
/// <see cref="AmbientAccentContrastAuditTests"/> for the end-to-end contrast claim).
/// </summary>
[Trait(UnitTests, "UI")]
public class HoverOverlayPolicyTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.4999)]
    public void For_LuminanceBelowThreshold_ReturnsDarkenWash(double luminance)
    {
        Assert.Equal(HoverOverlayPolicy.DarkenWash, HoverOverlayPolicy.For(luminance));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.5001)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void For_LuminanceAtOrAboveThreshold_ReturnsLightenWash(double luminance)
    {
        Assert.Equal(HoverOverlayPolicy.LightenWash, HoverOverlayPolicy.For(luminance));
    }

    [Fact]
    public void For_NaN_TreatsAsDark_DoesNotThrow()
    {
        Assert.Equal(HoverOverlayPolicy.DarkenWash, HoverOverlayPolicy.For(double.NaN));
    }

    [Fact]
    public void DarkenWash_AndLightenWash_ShareTheSameAlpha()
    {
        // Same intensity/alpha in both directions — only the RGB polarity differs — so switching
        // direction is not also silently changing the wash's overall strength.
        Assert.Equal(HoverOverlayPolicy.DarkenWash.A, HoverOverlayPolicy.LightenWash.A);
    }

    [Fact]
    public void LightenWash_MatchesThePreFixCycleOneColorOverlayHoverToken()
    {
        // Colors.xaml's Color.Overlay.Hover = #1AFFFFFF — kept in sync by comment, same duplication
        // convention AmbientAccentContrastAuditTests already uses for its own XAML-only token mirrors.
        Assert.Equal(System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), HoverOverlayPolicy.LightenWash);
    }
}
