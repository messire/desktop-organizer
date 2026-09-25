using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="AccentPalette"/> — normalization, junk-input handling, and the preset
/// list (docs/PLANS/phase-5-ambient-accent-ui.md §3.1).
/// </summary>
[Trait(UnitTests, "UI")]
public class AccentPaletteTests
{
    /// <summary>
    /// Verifies that <see cref="AccentPalette.Default"/> equals <see cref="AccentPalette.Neutral"/>.
    /// </summary>
    [Fact]
    public void Default_IsNeutral()
    {
        Assert.Equal(AccentPalette.Neutral, AccentPalette.Default);
    }

    /// <summary>
    /// Verifies that junk, empty, whitespace-only, or malformed hex input normalizes to
    /// <see cref="AccentPalette.Default"/>.
    /// </summary>
    /// <param name="input">The junk or malformed input to normalize.</param>
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

    /// <summary>
    /// Verifies that <see cref="AccentPalette.Normalize"/> never throws on arbitrary garbage, including
    /// control characters and non-printable Unicode.
    /// </summary>
    [Fact]
    public void Normalize_NeverThrows_OnArbitraryGarbage()
    {
        var exception = Record.Exception(() => AccentPalette.Normalize("\0￿\t<>"));

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that valid three- or six-digit hex input (with any casing or surrounding whitespace)
    /// normalizes to a canonical uppercase six-digit hex string.
    /// </summary>
    /// <param name="input">The hex string to normalize.</param>
    /// <param name="expected">The expected canonical uppercase six-digit hex string.</param>
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

    /// <summary>
    /// Verifies that eight-digit ARGB hex input normalizes to its RGB portion with the alpha byte dropped,
    /// rather than being rejected and replaced by <see cref="AccentPalette.Default"/> (MAJOR-1 regression).
    /// </summary>
    /// <param name="input">The eight-digit ARGB hex string to normalize.</param>
    /// <param name="expected">The expected canonical six-digit RGB hex string.</param>
    [Theory]
    [InlineData("#CC3D8BFF", "#3D8BFF")] // #AARRGGBB
    [InlineData("#cc3d8bff", "#3D8BFF")]
    [InlineData("#FF000000", "#000000")]
    public void Normalize_EightDigitArgbHex_ReturnsCanonicalSixDigitRgb_AlphaDropped(string input, string expected)
    {
        Assert.Equal(expected, AccentPalette.Normalize(input));
    }

    /// <summary>
    /// Verifies that four-digit ARGB hex input normalizes to a canonical six-digit RGB hex string, with the
    /// alpha nibble dropped and the R/G/B nibbles doubled.
    /// </summary>
    /// <param name="input">The four-digit ARGB hex string to normalize.</param>
    /// <param name="expected">The expected canonical six-digit RGB hex string.</param>
    [Theory]
    [InlineData("#8123", "#112233")] // #ARGB: alpha '8' dropped, R/G/B nibbles doubled
    [InlineData("#F000", "#000000")]
    public void Normalize_FourDigitArgbHex_ReturnsCanonicalSixDigitRgb_AlphaDropped(string input, string expected)
    {
        Assert.Equal(expected, AccentPalette.Normalize(input));
    }

    /// <summary>
    /// Verifies that the legacy near-black default accent value resolves to <see cref="AccentPalette.Neutral"/>.
    /// </summary>
    /// <param name="legacyDefault">The legacy default hex string, with any casing or surrounding whitespace.</param>
    [Theory]
    [InlineData("#1E1E1E")]
    [InlineData(" #1e1e1e ")]
    public void ResolveDefaultAccent_LegacyNearBlackDefault_ReturnsNeutral(string legacyDefault)
    {
        Assert.Equal(AccentPalette.Neutral, AccentPalette.ResolveDefaultAccent(legacyDefault));
    }

    /// <summary>
    /// Verifies that a genuine user-chosen accent value resolves verbatim rather than being treated as
    /// the legacy default.
    /// </summary>
    /// <param name="userValue">The user-chosen hex string.</param>
    [Theory]
    [InlineData("#112233")]
    [InlineData("#3B82F6")]
    public void ResolveDefaultAccent_UserChosenValue_ReturnsItVerbatim(string userValue)
    {
        Assert.Equal(userValue, AccentPalette.ResolveDefaultAccent(userValue));
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> accent value resolves to <see cref="AccentPalette.Neutral"/>.
    /// </summary>
    [Fact]
    public void ResolveDefaultAccent_Null_ReturnsNeutral()
    {
        Assert.Equal(AccentPalette.Neutral, AccentPalette.ResolveDefaultAccent(null));
    }

    /// <summary>
    /// Verifies that <see cref="AccentPalette.Presets"/> contains exactly six entries and excludes
    /// <see cref="AccentPalette.Neutral"/>.
    /// </summary>
    [Fact]
    public void Presets_ContainsExactlySixEntries_ExcludingNeutral()
    {
        Assert.Equal(6, AccentPalette.Presets.Count);
        Assert.DoesNotContain(AccentPalette.Neutral, AccentPalette.Presets);
    }

    /// <summary>
    /// Verifies that <see cref="AccentPalette.Presets"/> contains each named preset constant.
    /// </summary>
    /// <param name="preset">The named preset accent hex constant under test.</param>
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

    /// <summary>
    /// Verifies that <see cref="AccentPalette.IsPreset"/> returns <see langword="true"/> for each named
    /// preset color.
    /// </summary>
    /// <param name="preset">The named preset accent hex constant under test.</param>
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

    /// <summary>
    /// Verifies that <see cref="AccentPalette.IsPreset"/> returns <see langword="false"/> for
    /// <see cref="AccentPalette.Neutral"/>, since neutral is not a preset.
    /// </summary>
    [Fact]
    public void IsPreset_Neutral_ReturnsFalse()
    {
        Assert.False(AccentPalette.IsPreset(AccentPalette.Neutral));
    }

    /// <summary>
    /// Verifies that <see cref="AccentPalette.IsPreset"/> returns <see langword="false"/> for
    /// <see langword="null"/>, empty, garbage, or non-preset hex input.
    /// </summary>
    /// <param name="input">The non-preset input under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("#000000")]
    public void IsPreset_NonPresetInput_ReturnsFalse(string? input)
    {
        Assert.False(AccentPalette.IsPreset(input));
    }

    /// <summary>
    /// Verifies that <see cref="AccentPalette.IsPreset"/> matches a preset color regardless of casing.
    /// </summary>
    [Fact]
    public void IsPreset_CaseInsensitiveMatch_ReturnsTrue()
    {
        Assert.True(AccentPalette.IsPreset(AccentPalette.Blue.ToLowerInvariant()));
    }
}
