using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="PanelThemeRules"/> (ADR-042, release-polish-1.0 review round M1/M2) — the
/// explicit Light/Dark mode derived from a panel's stored <c>Model.TextColor</c> under the FROZEN
/// persistence schema (ADR-024).
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelThemeRulesTests
{
    /// <summary>Provides every preset's <see cref="PanelColorPreset.LightModeTextHex"/> as theory data.</summary>
    public static IEnumerable<object[]> AllPresetLightModeTextValues() =>
        PanelColorPresets.All.Select(p => new object[] { p.LightModeTextHex });

    /// <summary>Verifies <see cref="PanelThemeRules.DefaultDarkModeText"/>/<see cref="PanelThemeRules.DefaultLightModeText"/> stay pinned to their documented hex values.</summary>
    [Fact]
    public void DefaultTextConstants_ArePinned()
    {
        Assert.Equal("#FFFFFF", PanelThemeRules.DefaultDarkModeText);
        Assert.Equal("#1B1B1B", PanelThemeRules.DefaultLightModeText);
    }

    /// <summary>The app's legacy light-on-dark defaults (<c>#DDDDDD</c>/<c>#FFFFFF</c>) resolve <see cref="PanelThemeMode.Dark"/>.</summary>
    /// <param name="textHex">The legacy text colour under test.</param>
    [Theory]
    [InlineData("#DDDDDD")]
    [InlineData("#FFFFFF")]
    public void FromTextColor_LegacyLightText_ResolvesDark(string textHex)
    {
        Assert.Equal(PanelThemeMode.Dark, PanelThemeRules.FromTextColor(textHex));
    }

    /// <summary>M1: representative mid-grey/saturated colours that contrast BETTER against the dark base than the light one all resolve <see cref="PanelThemeMode.Dark"/> — the fixed 0.5 luminance threshold used to mis-classify some of these as Light.</summary>
    /// <param name="textHex">The candidate text colour hex string.</param>
    [Theory]
    [InlineData("#808080")]
    [InlineData("#A0A0A0")]
    [InlineData("#FFA500")]
    [InlineData("#FF0000")]
    public void FromTextColor_MidToneAndSaturatedColours_ResolveDark(string textHex)
    {
        Assert.Equal(PanelThemeMode.Dark, PanelThemeRules.FromTextColor(textHex));
    }

    /// <summary><see cref="PanelThemeRules.DefaultLightModeText"/> (<c>#1B1B1B</c>) resolves <see cref="PanelThemeMode.Light"/>.</summary>
    [Fact]
    public void FromTextColor_DefaultLightModeText_ResolvesLight()
    {
        Assert.Equal(PanelThemeMode.Light, PanelThemeRules.FromTextColor(PanelThemeRules.DefaultLightModeText));
    }

    /// <summary>Every one of the twelve <see cref="PanelColorPresets"/>' <see cref="PanelColorPreset.LightModeTextHex"/> values resolves <see cref="PanelThemeMode.Light"/>.</summary>
    /// <param name="lightModeTextHex">The preset's light-mode text hex value under test.</param>
    [Theory]
    [MemberData(nameof(AllPresetLightModeTextValues))]
    public void FromTextColor_EveryPresetLightModeText_ResolvesLight(string lightModeTextHex)
    {
        Assert.Equal(PanelThemeMode.Light, PanelThemeRules.FromTextColor(lightModeTextHex));
    }

    /// <summary>Every one of the twelve <see cref="PanelColorPresets"/>' shared <see cref="PanelColorPreset.DarkModeTextHex"/> (<c>#FFFFFF</c>) resolves <see cref="PanelThemeMode.Dark"/>.</summary>
    [Fact]
    public void FromTextColor_EveryPresetDarkModeText_ResolvesDark()
    {
        Assert.All(PanelColorPresets.All, p => Assert.Equal(PanelThemeMode.Dark, PanelThemeRules.FromTextColor(p.DarkModeTextHex)));
    }

    /// <summary>
    /// M2: <see cref="PanelThemeRules.FromTextColor(string?)"/> parses ONLY via
    /// <c>HexBrushConverter.TryParseColor</c> — <see langword="null"/>, a named colour
    /// (<c>"Black"</c>), and a <c>"ContextColor …"</c> ICC-profile reference are all rejected by that parser
    /// and resolve to <see cref="PanelThemeMode.Dark"/> — never throws.
    /// </summary>
    /// <param name="textHex">The candidate text colour hex string.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-colour")]
    [InlineData("#GGGGGG")]
    [InlineData("Black")]
    [InlineData("ContextColor file://C:/nope.icc 1.0,0.5,0.5,0.5")]
    public void FromTextColor_InvalidInput_ResolvesDark_DoesNotThrow(string? textHex)
    {
        var exception = Record.Exception(() => PanelThemeRules.FromTextColor(textHex));

        Assert.Null(exception);
        Assert.Equal(PanelThemeMode.Dark, PanelThemeRules.FromTextColor(textHex));
    }

    /// <summary>
    /// M2: a valid 8-digit ARGB hex string (<c>#80000000</c> — translucent black) IS parsed by
    /// <c>HexBrushConverter.TryParseColor</c> (same as the renderer) — its RGB channels
    /// (pure black, alpha ignored by <see cref="ContrastCalculator.RelativeLuminance"/>) resolve
    /// <see cref="PanelThemeMode.Light"/>, exactly like the fully-opaque <c>#000000</c> would.
    /// </summary>
    [Fact]
    public void FromTextColor_TranslucentArgbHex_ParsesAndResolvesLight()
    {
        Assert.Equal(PanelThemeMode.Light, PanelThemeRules.FromTextColor("#80000000"));
    }

    /// <summary>Verifies the <see cref="Color"/> overload agrees with the string overload for a representative pair.</summary>
    [Fact]
    public void FromTextColor_ColorOverload_AgreesWithStringOverload()
    {
        var darkColor = (Color)ColorConverter.ConvertFromString("#FFFFFF")!;
        var lightColor = (Color)ColorConverter.ConvertFromString("#1B1B1B")!;

        Assert.Equal(PanelThemeMode.Dark, PanelThemeRules.FromTextColor(darkColor));
        Assert.Equal(PanelThemeMode.Light, PanelThemeRules.FromTextColor(lightColor));
    }

    /// <summary>
    /// A text colour whose contrast against <see cref="SurfaceComposer.DarkNeutralBase"/> is at least as
    /// good as its contrast against <see cref="SurfaceComposer.LightNeutralBase"/> resolves
    /// <see cref="PanelThemeMode.Dark"/> — the documented "not strictly greater on light" rule, exercised
    /// directly against a colour picked to sit near the crossover (a mid-grey close to, but on the dark
    /// side of, the point where the two contrast ratios are equal) rather than hunting for an exact
    /// floating-point tie.
    /// </summary>
    [Fact]
    public void FromTextColor_NearCrossoverOnTheDarkSide_ResolvesDark()
    {
        var color = Color.FromRgb(0x8C, 0x8C, 0x8C); // #8C8C8C — just past the light/dark crossover grey (#7B7B7B).
        var contrastOnLight = ContrastCalculator.ContrastRatio(color, SurfaceComposer.LightNeutralBase);
        var contrastOnDark = ContrastCalculator.ContrastRatio(color, SurfaceComposer.DarkNeutralBase);
        Assert.True(contrastOnLight <= contrastOnDark, $"Fixture drifted: light={contrastOnLight:F3}, dark={contrastOnDark:F3}.");

        Assert.Equal(PanelThemeMode.Dark, PanelThemeRules.FromTextColor(color));
    }

    /// <summary><see cref="PanelThemeRules.IsLightSurface"/> agrees with <see cref="PanelThemeRules.FromTextColor(Color)"/> for representative Light/Dark colours.</summary>
    /// <param name="textHex">The candidate text colour hex string.</param>
    /// <param name="expectedLight">Whether <paramref name="textHex"/> is expected to resolve <see cref="PanelThemeMode.Light"/>.</param>
    [Theory]
    [InlineData("#FFFFFF", false)]
    [InlineData("#1B1B1B", true)]
    public void IsLightSurface_AgreesWithFromTextColor(string textHex, bool expectedLight)
    {
        var color = (Color)ColorConverter.ConvertFromString(textHex)!;

        Assert.Equal(expectedLight, PanelThemeRules.IsLightSurface(color));
    }

    /// <summary><see cref="PanelThemeRules.DefaultTextFor"/> returns the matching default for each mode.</summary>
    [Fact]
    public void DefaultTextFor_ReturnsTheMatchingDefault()
    {
        Assert.Equal(PanelThemeRules.DefaultDarkModeText, PanelThemeRules.DefaultTextFor(PanelThemeMode.Dark));
        Assert.Equal(PanelThemeRules.DefaultLightModeText, PanelThemeRules.DefaultTextFor(PanelThemeMode.Light));
    }
}
