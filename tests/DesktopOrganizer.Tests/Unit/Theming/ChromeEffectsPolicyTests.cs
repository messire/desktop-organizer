using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="ChromeEffectsPolicy"/> (ADR-030, docs/PLANS/reference-panel-chrome.md §5.1
/// T-P6) — <see cref="ChromeEffectsPolicy.ShouldRenderGlowEffect"/> is a pure function over an injected
/// render tier/High-Contrast flag, so this class never touches the real <see cref="System.Windows.Media.RenderCapability"/>
/// or <see cref="HighContrastState"/> at all.
/// </summary>
[Trait(UnitTests, "UI")]
public class ChromeEffectsPolicyTests
{
    /// <summary>
    /// Verifies that a low render tier returns <see langword="false"/> regardless of the high-contrast flag.
    /// </summary>
    /// <param name="tier">The render tier under test.</param>
    /// <param name="highContrast">Whether high-contrast mode is active.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ShouldRenderGlowEffect_LowRenderTier_ReturnsFalse(int tier, bool highContrast)
        => Assert.False(ChromeEffectsPolicy.ShouldRenderGlowEffect(tier, highContrast));

    /// <summary>
    /// Verifies that render tier 2 without high-contrast mode returns <see langword="true"/>.
    /// </summary>
    [Fact]
    public void ShouldRenderGlowEffect_Tier2NotHighContrast_ReturnsTrue()
        => Assert.True(ChromeEffectsPolicy.ShouldRenderGlowEffect(2, highContrast: false));

    /// <summary>
    /// Verifies that a render tier higher than 2 without high-contrast mode returns <see langword="true"/>.
    /// </summary>
    [Fact]
    public void ShouldRenderGlowEffect_HigherTierNotHighContrast_ReturnsTrue()
        => Assert.True(ChromeEffectsPolicy.ShouldRenderGlowEffect(3, highContrast: false));

    /// <summary>
    /// Verifies that high-contrast mode returns <see langword="false"/> regardless of render tier.
    /// </summary>
    /// <param name="tier">The render tier under test.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShouldRenderGlowEffect_HighContrast_ReturnsFalseAtAnyTier(int tier)
        => Assert.False(ChromeEffectsPolicy.ShouldRenderGlowEffect(tier, highContrast: true));
}
