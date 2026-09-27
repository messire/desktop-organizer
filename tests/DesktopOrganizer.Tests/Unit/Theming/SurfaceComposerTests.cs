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

    /// <summary>The app's own default text colour (<c>Color.Text.Primary</c>, <c>#DDDDDD</c>) — used as a stand-in <c>text</c> argument wherever a test only cares about the accent-luminance branch of <see cref="SurfaceComposer.ResolveNeutralBase"/> (MAJOR-2 fix, review round, orchestrator decision "variant A").</summary>
    private static readonly Color DefaultText = Color.FromRgb(0xDD, 0xDD, 0xDD);

    // --- ResolveNeutralBase theory data (docs/PLANS/color-presets-and-size-step.md Q-1, variant B) -------
    // Declared here, with this class's other static members, per StyleCop's static-before-instance member
    // ordering (SA1204) — not next to the [Theory]/[Fact] methods that consume them further down.

    /// <summary>
    /// Provides every one of <see cref="AccentPalette"/>'s legacy accents (Neutral + all six legacy
    /// <c>Presets</c>) as theory data — every one of these, paired with the app's own light default text,
    /// must keep resolving to <see cref="SurfaceComposer.DarkNeutralBase"/>, unchanged, after ADR-042.
    /// </summary>
    public static IEnumerable<object[]> LegacyDarkAccentHexValues()
    {
        yield return new object[] { AccentPalette.Neutral };
        foreach (var preset in AccentPalette.Presets)
            yield return new object[] { preset };
    }

    /// <summary>
    /// The twelve <see cref="PanelColorPresets"/> accents (ADR-042), each paired with its OWN
    /// <see cref="PanelColorPreset.LightModeTextHex"/> — every one of these pairs must resolve to
    /// <see cref="SurfaceComposer.LightNeutralBase"/>, since <see cref="PanelThemeRules.IsLightSurface"/>
    /// only ever looks at the text colour (paired with a DIFFERENT, light text instead, the SAME accent
    /// must resolve dark — see
    /// <see cref="SurfaceComposerTests.ResolveNeutralBase_LightAccentPairedWithLightText_ReturnsDarkNeutralBase"/>).
    /// Raw hex, not a reference to <c>DesktopOrganizer.UI.Theming.PanelColorPresets</c>'s own fields, to
    /// keep this theory data self-contained.
    /// </summary>
    public static IEnumerable<object[]> NewLightAccentHexValues()
    {
        yield return new object[] { "#D9534F", "#4A1F1E" }; // Red
        yield return new object[] { "#E0812F", "#4B2A0C" }; // Orange
        yield return new object[] { "#D6A324", "#453309" }; // Amber
        yield return new object[] { "#3F7FD9", "#172F4B" }; // Blue
        yield return new object[] { "#7A5BE7", "#2B2350" }; // Violet
    }

    // --- ClampOpacity -------------------------------------------------------------------

    /// <summary>
    /// Verifies that non-finite opacity input returns <see cref="SurfaceComposer.MinSurfaceOpacity"/>.
    /// </summary>
    /// <param name="value">The non-finite opacity value under test.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampOpacity_NonFiniteInput_ReturnsMinSurfaceOpacity(double value)
    {
        Assert.Equal(SurfaceComposer.MinSurfaceOpacity, SurfaceComposer.ClampOpacity(value));
    }

    /// <summary>
    /// Verifies that finite opacity input is clamped to the [<see cref="SurfaceComposer.MinSurfaceOpacity"/>,
    /// <see cref="SurfaceComposer.MaxSurfaceOpacity"/>] range.
    /// </summary>
    /// <param name="value">The opacity value under test.</param>
    /// <param name="expected">The expected clamped opacity value.</param>
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

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.ClampOpacity"/> is monotonically non-decreasing across an
    /// increasing sequence of inputs.
    /// </summary>
    [Fact]
    public void ClampOpacity_IsMonotonicallyNonDecreasing()
    {
        double[] inputs = { -10, 0, 0.1, 0.35, 0.5, 0.75, 1.0, 2.0, 10 };
        var results = Array.ConvertAll(inputs, SurfaceComposer.ClampOpacity);

        for (var i = 1; i < results.Length; i++)
            Assert.True(results[i] >= results[i - 1], $"ClampOpacity not monotonic at index {i}");
    }

    // --- AlphaFromOpacity -----------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.AlphaFromOpacity"/> produces the expected rounded byte alpha.
    /// </summary>
    /// <param name="opacity">The opacity value to convert.</param>
    /// <param name="expected">The expected alpha channel byte.</param>
    [Theory]
    [InlineData(1.0, 255)]
    [InlineData(0.0, 89)] // clamps to MinSurfaceOpacity (0.35) -> round(0.35*255) = 89
    [InlineData(0.35, 89)]
    public void AlphaFromOpacity_ProducesRoundedByteAlpha(double opacity, byte expected)
    {
        Assert.Equal(expected, SurfaceComposer.AlphaFromOpacity(opacity));
    }

    /// <summary>
    /// Verifies that a NaN opacity value uses the <see cref="SurfaceComposer.MinSurfaceOpacity"/> floor.
    /// </summary>
    [Fact]
    public void AlphaFromOpacity_NaN_UsesMinSurfaceOpacityFloor()
    {
        Assert.Equal(SurfaceComposer.AlphaFromOpacity(SurfaceComposer.MinSurfaceOpacity), SurfaceComposer.AlphaFromOpacity(double.NaN));
    }

    // --- WithAlpha --------------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.WithAlpha"/> replaces only the alpha channel, leaving
    /// the RGB channels unchanged.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.Mix"/> with a ratio of zero returns the first color unchanged.
    /// </summary>
    [Fact]
    public void Mix_RatioZero_ReturnsFirstColor()
    {
        var result = SurfaceComposer.Mix(NeutralBase, AccentA, 0.0);

        Assert.Equal(NeutralBase, result);
    }

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.Mix"/> with a ratio of one returns the second color's RGB.
    /// </summary>
    [Fact]
    public void Mix_RatioOne_ReturnsSecondColorRgb()
    {
        var result = SurfaceComposer.Mix(NeutralBase, AccentA, 1.0);

        Assert.Equal(AccentA.R, result.R);
        Assert.Equal(AccentA.G, result.G);
        Assert.Equal(AccentA.B, result.B);
    }

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.Mix"/> clamps out-of-range ratios to the first or second color.
    /// </summary>
    /// <param name="ratio">The out-of-range mix ratio under test.</param>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Mix_RatioOutOfRange_IsClamped(double ratio)
    {
        var result = SurfaceComposer.Mix(NeutralBase, AccentA, ratio);

        Assert.True(result == NeutralBase || (result.R == AccentA.R && result.G == AccentA.G && result.B == AccentA.B));
    }

    // --- ComposeSurface: Solid ignores accent (DoD "accent is not a full-panel fill") -----------

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Solid"/> produces the same composed color regardless of accent.
    /// </summary>
    [Fact]
    public void ComposeSurface_Solid_DoesNotDependOnAccent()
    {
        var withAccentA = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Solid, 0.8);
        var withAccentB = SurfaceComposer.ComposeSurface(NeutralBase, AccentB, PanelSurfaceStyle.Solid, 0.8);

        Assert.Equal(withAccentA, withAccentB);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Solid"/>'s composed alpha matches
    /// <see cref="SurfaceComposer.AlphaFromOpacity"/> for the requested opacity.
    /// </summary>
    [Fact]
    public void ComposeSurface_Solid_AlphaMatchesSurfaceOpacity()
    {
        var result = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Solid, 0.8);

        Assert.Equal(SurfaceComposer.AlphaFromOpacity(0.8), result.A);
    }

    // --- ComposeSurface: Ambient mixes accent, alpha == AlphaFromOpacity(o) ----------------------

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Ambient"/>'s composed alpha equals
    /// <see cref="SurfaceComposer.AlphaFromOpacity"/> for the requested opacity.
    /// </summary>
    [Fact]
    public void ComposeSurface_Ambient_AlphaEqualsAlphaFromOpacity()
    {
        var result = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Ambient, 0.8);

        Assert.Equal(SurfaceComposer.AlphaFromOpacity(0.8), result.A);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Ambient"/> produces different composed colors for
    /// different accents.
    /// </summary>
    [Fact]
    public void ComposeSurface_Ambient_DependsOnAccent()
    {
        var withAccentA = SurfaceComposer.ComposeSurface(NeutralBase, AccentA, PanelSurfaceStyle.Ambient, 0.8);
        var withAccentB = SurfaceComposer.ComposeSurface(NeutralBase, AccentB, PanelSurfaceStyle.Ambient, 0.8);

        Assert.NotEqual(withAccentA, withAccentB);
    }

    // --- ComposeSurface: Glass -------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Glass"/>'s composed alpha never exceeds
    /// <see cref="PanelSurfaceStyle.Ambient"/>'s alpha at the same opacity.
    /// </summary>
    /// <param name="opacity">The surface opacity under test.</param>
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

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Glass"/>'s composed alpha never falls below the alpha
    /// corresponding to <see cref="SurfaceComposer.MinSurfaceOpacity"/>.
    /// </summary>
    /// <param name="opacity">The surface opacity under test.</param>
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

    /// <summary>
    /// Verifies that <c>highContrast:true</c> forces a fully opaque composed surface for every style.
    /// </summary>
    /// <param name="style">The surface style under test.</param>
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

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.AccentTint"/> with alpha zero keeps the RGB channels but
    /// sets alpha to zero.
    /// </summary>
    [Fact]
    public void AccentTint_AlphaZero_KeepsRgbChangesAlphaToZero()
    {
        var result = SurfaceComposer.AccentTint(AccentA, 0.0);

        Assert.Equal(0, result.A);
        Assert.Equal(AccentA.R, result.R);
        Assert.Equal(AccentA.G, result.G);
        Assert.Equal(AccentA.B, result.B);
    }

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.AccentTint"/> with alpha one is fully opaque.
    /// </summary>
    [Fact]
    public void AccentTint_AlphaOne_IsFullyOpaque()
    {
        var result = SurfaceComposer.AccentTint(AccentA, 1.0);

        Assert.Equal(255, result.A);
    }

    /// <summary>
    /// Verifies that out-of-range or NaN alpha values do not throw when passed to
    /// <see cref="SurfaceComposer.AccentTint"/>.
    /// </summary>
    /// <param name="alpha">The out-of-range or NaN alpha value under test.</param>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    public void AccentTint_OutOfRangeOrNaNAlpha_DoesNotThrow(double alpha)
    {
        var exception = Record.Exception(() => SurfaceComposer.AccentTint(AccentA, alpha));

        Assert.Null(exception);
    }

    // --- ResolveNeutralBase (ADR-042: derived from the TEXT colour's own PanelThemeMode, not an
    // (accent, text) pair) ------------------------------------------------------------------------------
    // Theory data (LegacyDarkAccentHexValues/NewLightAccentHexValues) lives in this class's own
    // static-member block near the top, per StyleCop's static-before-instance ordering (SA1204).

    /// <summary>
    /// Verifies that every legacy accent, paired with the app's own light default text, still resolves to
    /// <see cref="SurfaceComposer.DarkNeutralBase"/>, byte-identical — ADR-042 does not read the accent at
    /// all, so this also proves the accent parameter is irrelevant to the result.
    /// </summary>
    /// <param name="accentHex">The legacy accent hex value under test.</param>
    [Theory]
    [MemberData(nameof(LegacyDarkAccentHexValues))]
    public void ResolveNeutralBase_LegacyDarkAccent_ReturnsDarkNeutralBase(string accentHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;

        var result = SurfaceComposer.ResolveNeutralBase(accent, DefaultText);

        Assert.Equal(SurfaceComposer.DarkNeutralBase, result);
        Assert.Equal(NeutralBase, result); // NeutralBase == #1E1E1E, this class's own pre-existing constant
    }

    /// <summary>
    /// Verifies that every one of the sampled <see cref="PanelColorPresets"/> accents — paired with its
    /// OWN light-mode dark text — resolves to <see cref="SurfaceComposer.LightNeutralBase"/> (ADR-042:
    /// the text colour's own <see cref="PanelThemeMode"/> decides this, not the accent).
    /// </summary>
    /// <param name="accentHex">The accent hex value under test (irrelevant to the result — see remarks above).</param>
    /// <param name="textHex">The paired dark (light-mode) text hex value under test.</param>
    [Theory]
    [MemberData(nameof(NewLightAccentHexValues))]
    public void ResolveNeutralBase_NewLightAccent_ReturnsLightNeutralBase(string accentHex, string textHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;
        var text = (Color)ColorConverter.ConvertFromString(textHex)!;

        var result = SurfaceComposer.ResolveNeutralBase(accent, text);

        Assert.Equal(SurfaceComposer.LightNeutralBase, result);
    }

    /// <summary>
    /// ADR-042: verifies that the SAME light-mode-capable accents, when paired with the app's own DARK
    /// default text (<c>#DDDDDD</c>) instead of a light-mode dark text, resolve to
    /// <see cref="SurfaceComposer.DarkNeutralBase"/> — proving the decision is a property of the TEXT
    /// colour alone, not the accent.
    /// </summary>
    /// <param name="accentHex">The accent hex value under test (irrelevant to the result).</param>
    [Theory]
    [InlineData("#D9534F")] // Red
    [InlineData("#E0812F")] // Orange
    [InlineData("#D6A324")] // Amber
    [InlineData("#3F7FD9")] // Blue
    [InlineData("#7A5BE7")] // Violet
    public void ResolveNeutralBase_LightAccentPairedWithLightText_ReturnsDarkNeutralBase(string accentHex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(accentHex)!;

        var result = SurfaceComposer.ResolveNeutralBase(accent, DefaultText);

        Assert.Equal(SurfaceComposer.DarkNeutralBase, result);
    }

    /// <summary>
    /// Verifies that every ADR-042 preset's <see cref="PanelColorPreset.LightModeTextHex"/> contrasts
    /// STRICTLY BETTER against <see cref="SurfaceComposer.LightNeutralBase"/> than against
    /// <see cref="SurfaceComposer.DarkNeutralBase"/> (M1: the rule <see cref="PanelThemeRules.FromTextColor(Color)"/>
    /// itself now uses), while the app's own legacy default text does the opposite.
    /// </summary>
    [Fact]
    public void ResolveNeutralBase_ContrastRule_SeparatesLegacyFromPresetLightModeText()
    {
        var legacyContrastOnDark = ContrastCalculator.ContrastRatio(DefaultText, SurfaceComposer.DarkNeutralBase);
        var legacyContrastOnLight = ContrastCalculator.ContrastRatio(DefaultText, SurfaceComposer.LightNeutralBase);
        Assert.True(legacyContrastOnDark > legacyContrastOnLight, "#DDDDDD should contrast better on the dark base.");

        foreach (var preset in PanelColorPresets.All)
        {
            var text = (Color)ColorConverter.ConvertFromString(preset.LightModeTextHex)!;
            var contrastOnLight = ContrastCalculator.ContrastRatio(text, SurfaceComposer.LightNeutralBase);
            var contrastOnDark = ContrastCalculator.ContrastRatio(text, SurfaceComposer.DarkNeutralBase);
            Assert.True(contrastOnLight > contrastOnDark, $"{preset.Id} LightModeTextHex {preset.LightModeTextHex} should contrast better on the light base.");
        }
    }

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.DarkNeutralBase"/> is byte-identical to the single neutral
    /// base this app shipped before ADR-042 (<c>#1E1E1E</c>, this class's own <see cref="NeutralBase"/> constant).
    /// </summary>
    [Fact]
    public void DarkNeutralBase_IsByteIdenticalToPreAdr042SingleBase()
    {
        Assert.Equal(NeutralBase, SurfaceComposer.DarkNeutralBase);
    }

    /// <summary>
    /// Verifies that <see cref="SurfaceComposer.LightNeutralBase"/>'s own WCAG relative luminance is itself
    /// well above <see cref="SurfaceComposer.DarkNeutralBase"/>'s — i.e. it really is a "light" base.
    /// </summary>
    [Fact]
    public void LightNeutralBase_OwnLuminance_IsWellAboveDarkNeutralBase()
    {
        var lightLuminance = ContrastCalculator.RelativeLuminance(SurfaceComposer.LightNeutralBase);
        var darkLuminance = ContrastCalculator.RelativeLuminance(SurfaceComposer.DarkNeutralBase);

        Assert.True(lightLuminance > darkLuminance + 0.5);
    }

    /// <summary>
    /// ADR-042: verifies that <see cref="SurfaceComposer.ResolveNeutralBase"/> ignores its
    /// <c>accent</c> parameter entirely — two wildly different accents, paired with the SAME text colour,
    /// always resolve to the same neutral base.
    /// </summary>
    [Fact]
    public void ResolveNeutralBase_IgnoresAccent_SameTextDifferentAccentsResolveSame()
    {
        var withAccentA = SurfaceComposer.ResolveNeutralBase(AccentA, DefaultText);
        var withAccentB = SurfaceComposer.ResolveNeutralBase(AccentB, DefaultText);

        Assert.Equal(withAccentA, withAccentB);
    }
}
