using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="SurfaceComposer"/> — the pure colour-composition logic backing
/// <c>PanelSurfaceBrushConverter</c> (docs/PLANS/phase-5-ambient-accent-ui.md §3.2). These are the
/// machine-checkable form of the phase's DoD "accent is local, not a full-panel fill" and the
/// readability/high-contrast floor.
/// </summary>
[Trait(UnitTests, "UI")]
public class SurfaceComposerTests
{
    private static readonly Color NeutralBase = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly Color AccentA = Color.FromRgb(0x3B, 0x82, 0xF6); // Blue
    private static readonly Color AccentB = Color.FromRgb(0xA8, 0x55, 0xF7); // Violet

    // --- ClampOpacity -------------------------------------------------------------------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampOpacity_NonFiniteInput_ReturnsMinSurfaceOpacity(double value)
    {
        Assert.Equal(SurfaceComposer.MinSurfaceOpacity, SurfaceComposer.ClampOpacity(value));
    }

    [Theory]
    [InlineData(-5.0, SurfaceComposer.MinSurfaceOpacity)]
    [InlineData(0.0, SurfaceComposer.MinSurfaceOpacity)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, SurfaceComposer.MaxSurfaceOpacity)]
    [InlineData(5.0, SurfaceComposer.MaxSurfaceOpacity)]
    public void ClampOpacity_FiniteInput_ClampsToRange(double value, double expected)
    {
        Assert.Equal(expected, SurfaceComposer.ClampOpacity(value));
    }

    [Fact]
    public void ClampOpacity_IsMonotonicallyNonDecreasing()
    {
        double[] inputs = { -10, 0, 0.1, 0.35, 0.5, 0.75, 1.0, 2.0, 10 };
        var results = Array.ConvertAll(inputs, SurfaceComposer.ClampOpacity);

        for (var i = 1; i < results.Length; i++)
            Assert.True(results[i] >= results[i - 1], $"ClampOpacity not monotonic at index {i}");
    }

    // --- AlphaFromOpacity -----------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 255)]
    [InlineData(0.0, 89)] // clamps to MinSurfaceOpacity (0.35) -> round(0.35*255) = 89
    [InlineData(0.35, 89)]
    public void AlphaFromOpacity_ProducesRoundedByteAlpha(double opacity, byte expected)
    {
        Assert.Equal(expected, SurfaceComposer.AlphaFromOpacity(opacity));
    }

    [Fact]
    public void AlphaFromOpacity_NaN_UsesMinSurfaceOpacityFloor()
    {
        Assert.Equal(SurfaceComposer.AlphaFromOpacity(SurfaceComposer.MinSurfaceOpacity), SurfaceComposer.AlphaFromOpacity(double.NaN));
    }

    // --- WithAlpha --------------------------------------------------------------------------

    [Fact]
    public void WithAlpha_ReplacesAlphaChannelOnly()
    {
        var result = SurfaceComposer.WithAlpha(NeutralBase, 128);

        Assert.Equal(128, result.A);
        Assert.Equal(NeutralBase.R, result.R);
        Assert.Equal(NeutralBase.G, result.G);
        Assert.Equal(NeutralBase.B, result.B);
    }

    // --- Mix ----------------------------------------------------------------------------------

    [Fact]
    public void Mix_RatioZero_ReturnsFirstColor()
    {
        var result = SurfaceComposer.Mix(NeutralBase, AccentA, 0.0);

        Assert.Equal(NeutralBase, result);
    }

    [Fact]
    public void Mix_RatioOne_ReturnsSecondColorRgb()
    {
        var result = SurfaceComposer.Mix(NeutralBase, AccentA, 1.0);

        Assert.Equal(AccentA.R, result.R);
        Assert.Equal(AccentA.G, result.G);
        Assert.Equal(AccentA.B, result.B);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Mix_RatioOutOfRange_IsClamped(double ratio)
    {
        var result = SurfaceComposer.Mix(NeutralBase, AccentA, ratio);

        Assert.True(result == NeutralBase || (result.R == AccentA.R && result.G == AccentA.G && result.B == AccentA.B));
    }

    // --- ComposeSurface: Solid ignores accent (DoD "accent is not a full-panel fill") -----------

    [Fact]
    public void ComposeSurface_Solid_DoesNotDependOnAccent()
    {
        var withAccentA = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Solid, 0.8);
        var withAccentB = SurfaceComposer.ComposeSurface(NeutralBase, AccentB, PanelSurfaceStyle.Solid, 0.8);

        Assert.Equal(withAccentA, withAccentB);
    }

    [Fact]
    public void ComposeSurface_Solid_AlphaMatchesSurfaceOpacity()
    {
        var result = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Solid, 0.8);

        Assert.Equal(SurfaceComposer.AlphaFromOpacity(0.8), result.A);
    }

    // --- ComposeSurface: Ambient mixes accent, alpha == AlphaFromOpacity(o) ----------------------

    [Fact]
    public void ComposeSurface_Ambient_AlphaEqualsAlphaFromOpacity()
    {
        var result = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Ambient, 0.8);

        Assert.Equal(SurfaceComposer.AlphaFromOpacity(0.8), result.A);
    }

    [Fact]
    public void ComposeSurface_Ambient_DependsOnAccent()
    {
        var withAccentA = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Ambient, 0.8);
        var withAccentB = SurfaceComposer.ComposeSurface(NeutralBase, AccentB, PanelSurfaceStyle.Ambient, 0.8);

        Assert.NotEqual(withAccentA, withAccentB);
    }

    // --- ComposeSurface: Glass -------------------------------------------------------------------

    [Theory]
    [InlineData(0.35)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void ComposeSurface_Glass_AlphaNeverExceedsAmbientAlpha(double opacity)
    {
        var ambient = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Ambient, opacity);
        var glass = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Glass, opacity);

        Assert.True(glass.A <= ambient.A, $"Glass alpha {glass.A} exceeded Ambient alpha {ambient.A} at opacity {opacity}");
    }

    [Theory]
    [InlineData(0.35)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void ComposeSurface_Glass_AlphaNeverBelowMinSurfaceOpacityFloor(double opacity)
    {
        var glass = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Glass, opacity);

        Assert.True(glass.A >= SurfaceComposer.AlphaFromOpacity(SurfaceComposer.MinSurfaceOpacity));
    }

    // --- ComposeSurface: highContrast forces alpha=255 for every style ---------------------------

    [Theory]
    [InlineData(PanelSurfaceStyle.Solid)]
    [InlineData(PanelSurfaceStyle.Ambient)]
    [InlineData(PanelSurfaceStyle.Glass)]
    public void ComposeSurface_HighContrast_AlwaysFullyOpaque(PanelSurfaceStyle style)
    {
        var result = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, style, 0.35, highContrast: true);

        Assert.Equal(255, result.A);
    }

    // --- AccentTint ------------------------------------------------------------------------------

    [Fact]
    public void AccentTint_AlphaZero_KeepsRgbChangesAlphaToZero()
    {
        var result = SurfaceComposer.AccentTint(AccentA, 0.0);

        Assert.Equal(0, result.A);
        Assert.Equal(AccentA.R, result.R);
        Assert.Equal(AccentA.G, result.G);
        Assert.Equal(AccentA.B, result.B);
    }

    [Fact]
    public void AccentTint_AlphaOne_IsFullyOpaque()
    {
        var result = SurfaceComposer.AccentTint(AccentA, 1.0);

        Assert.Equal(255, result.A);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    public void AccentTint_OutOfRangeOrNaNAlpha_DoesNotThrow(double alpha)
    {
        var result = SurfaceComposer.AccentTint(AccentA, alpha);

        Assert.True(result.A is >= 0 and <= 255);
    }
}
