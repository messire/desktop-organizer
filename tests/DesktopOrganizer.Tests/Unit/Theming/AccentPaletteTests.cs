using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="AccentPalette"/> — normalization, junk-input handling, and the preset
/// list (docs/PLANS/phase-5-ambient-accent-ui.md §3.1).
/// </summary>
[Trait(UnitTests, "UI")]
public class AccentPaletteTests
{
    [Fact]
    public void Default_IsNeutral()
    {
        Assert.Equal(AccentPalette.Neutral, AccentPalette.Default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-colour")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("blue")]
    [InlineData("6B7280")] // missing leading '#'
    public void Normalize_JunkOrMissingInput_ReturnsDefault(string? input)
    {
        Assert.Equal(AccentPalette.Default, AccentPalette.Normalize(input));
    }

    [Fact]
    public void Normalize_NeverThrows_OnArbitraryGarbage()
    {
        var exception = Record.Exception(() => AccentPalette.Normalize("\0￿\t<>"));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#ABC", "#AABBCC")]
    [InlineData(" #AABBCC ", "#AABBCC")]
    [InlineData("#aabbcc", "#AABBCC")]
    [InlineData("#3B82F6", "#3B82F6")]
    public void Normalize_ValidHex_ReturnsCanonicalUppercaseSixDigit(string input, string expected)
    {
        Assert.Equal(expected, AccentPalette.Normalize(input));
    }

    // ── MAJOR-1 regression: ARGB (4-/8-digit) hex must normalize to its RGB portion, not be silently
    // rejected/replaced by Default — a v3 panel's ARGB backgroundColor (e.g. "#CC3D8BFF") must survive
    // migration into accentColor instead of turning into a generic grey. ─────────────────────────────
    [Theory]
    [InlineData("#CC3D8BFF", "#3D8BFF")] // #AARRGGBB
    [InlineData("#cc3d8bff", "#3D8BFF")]
    [InlineData("#FF000000", "#000000")]
    public void Normalize_EightDigitArgbHex_ReturnsCanonicalSixDigitRgb_AlphaDropped(string input, string expected)
    {
        Assert.Equal(expected, AccentPalette.Normalize(input));
    }

    [Theory]
    [InlineData("#8123", "#112233")] // #ARGB: alpha '8' dropped, R/G/B nibbles doubled
    [InlineData("#F000", "#000000")]
    public void Normalize_FourDigitArgbHex_ReturnsCanonicalSixDigitRgb_AlphaDropped(string input, string expected)
    {
        Assert.Equal(expected, AccentPalette.Normalize(input));
    }

    [Theory]
    [InlineData("#1E1E1E")]
    [InlineData(" #1e1e1e ")]
    public void ResolveDefaultAccent_LegacyNearBlackDefault_ReturnsNeutral(string legacyDefault)
    {
        Assert.Equal(AccentPalette.Neutral, AccentPalette.ResolveDefaultAccent(legacyDefault));
    }

    [Theory]
    [InlineData("#112233")]
    [InlineData("#3B82F6")]
    public void ResolveDefaultAccent_UserChosenValue_ReturnsItVerbatim(string userValue)
    {
        Assert.Equal(userValue, AccentPalette.ResolveDefaultAccent(userValue));
    }

    [Fact]
    public void ResolveDefaultAccent_Null_ReturnsNeutral()
    {
        Assert.Equal(AccentPalette.Neutral, AccentPalette.ResolveDefaultAccent(null));
    }

    [Fact]
    public void Presets_ContainsExactlySixEntries_ExcludingNeutral()
    {
        Assert.Equal(6, AccentPalette.Presets.Count);
        Assert.DoesNotContain(AccentPalette.Neutral, AccentPalette.Presets);
    }

    [Theory]
    [InlineData(AccentPalette.Blue)]
    [InlineData(AccentPalette.Violet)]
    [InlineData(AccentPalette.Amber)]
    [InlineData(AccentPalette.Green)]
    [InlineData(AccentPalette.Cyan)]
    [InlineData(AccentPalette.Coral)]
    public void Presets_ContainsEachNamedConstant(string preset)
    {
        Assert.Contains(preset, AccentPalette.Presets);
    }

    [Theory]
    [InlineData(AccentPalette.Blue)]
    [InlineData(AccentPalette.Violet)]
    [InlineData(AccentPalette.Amber)]
    [InlineData(AccentPalette.Green)]
    [InlineData(AccentPalette.Cyan)]
    [InlineData(AccentPalette.Coral)]
    public void IsPreset_PresetColor_ReturnsTrue(string preset)
    {
        Assert.True(AccentPalette.IsPreset(preset));
    }

    [Fact]
    public void IsPreset_Neutral_ReturnsFalse()
    {
        Assert.False(AccentPalette.IsPreset(AccentPalette.Neutral));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("#000000")]
    public void IsPreset_NonPresetInput_ReturnsFalse(string? input)
    {
        Assert.False(AccentPalette.IsPreset(input));
    }

    [Fact]
    public void IsPreset_CaseInsensitiveMatch_ReturnsTrue()
    {
        Assert.True(AccentPalette.IsPreset(AccentPalette.Blue.ToLowerInvariant()));
    }
}
