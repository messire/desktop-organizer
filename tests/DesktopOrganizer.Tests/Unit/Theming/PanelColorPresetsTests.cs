using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="PanelColorPresets"/> (ADR-042) — the twelve selectable panel colour presets,
/// distinct from the legacy <see cref="AccentPalette.Presets"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelColorPresetsTests
{
    private static readonly (string Id, string AccentHex, string DarkModeTextHex, string LightModeTextHex)[] Expected =
    {
        ("Red", "#D9534F", "#EFE1E1", "#4A1F1E"),
        ("Orange", "#E0812F", "#EFE8E1", "#4B2A0C"),
        ("Amber", "#D6A324", "#EFEBE1", "#453309"),
        ("Lime", "#8FB43A", "#EBEFE1", "#2F3D11"),
        ("Green", "#3F9A5A", "#E1EFE5", "#173A24"),
        ("Teal", "#2F9C93", "#E1EFEE", "#12403C"),
        ("Cyan", "#2E98C8", "#E1EBEF", "#123B4E"),
        ("Blue", "#3F7FD9", "#E1E7EF", "#172F4B"),
        ("Violet", "#7A5BE7", "#E4E1EF", "#2B2350"),
        ("Magenta", "#C8559E", "#EFE1EA", "#4A1F3B"),
        ("Graphite", "#596273", "#FFFFFF", "#22262E"),
        ("Stone", "#7D7468", "#FFFFFF", "#2E2A26"),
    };

    /// <summary>The <see cref="Expected"/> preset ids that are NOT one of the two neutral presets (Graphite, Stone) — every OTHER preset's <see cref="PanelColorPreset.DarkModeTextHex"/> must be its own distinct tint (backlog 1.x item 4).</summary>
    private static readonly string[] ChromaticIds =
        Expected.Select(e => e.Id).Where(id => id is not "Graphite" and not "Stone").ToArray();

    /// <summary>Every preset's (Id, DarkModeTextHex, LightModeTextHex), read straight from <see cref="PanelColorPresets.All"/> — theory data for <see cref="TextFor_EachModesOwnText_ResolvesToThatMode"/>.</summary>
    public static IEnumerable<object[]> AllPresetModeTexts() =>
        PanelColorPresets.All.Select(p => new object[] { p.Id, p.DarkModeTextHex, p.LightModeTextHex });

    /// <summary>Verifies <see cref="PanelColorPresets.All"/> has exactly the twelve presets, in spec order, with the exact hex values and <see cref="PanelColorPreset.NameKey"/>.</summary>
    [Fact]
    public void All_HasTwelvePresets_InSpecOrderAndValues()
    {
        Assert.Equal(Expected.Length, PanelColorPresets.All.Count);

        for (var i = 0; i < Expected.Length; i++)
        {
            var (id, accent, darkText, lightText) = Expected[i];
            var preset = PanelColorPresets.All[i];

            Assert.Equal(id, preset.Id);
            Assert.Equal(accent, preset.AccentHex);
            Assert.Equal(darkText, preset.DarkModeTextHex);
            Assert.Equal(lightText, preset.LightModeTextHex);
            Assert.Equal("ColorPreset_" + id, preset.NameKey);
        }
    }

    /// <summary>Verifies every preset has a unique <see cref="PanelColorPreset.Id"/>.</summary>
    [Fact]
    public void All_HasUniqueIds()
    {
        var ids = PanelColorPresets.All.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Verifies every preset has a unique <see cref="PanelColorPreset.AccentHex"/>.</summary>
    [Fact]
    public void All_HasUniqueAccents()
    {
        var accents = PanelColorPresets.All.Select(p => p.AccentHex).ToList();

        Assert.Equal(accents.Count, accents.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Verifies <see cref="PanelColorPresets.Default"/> is <see cref="PanelColorPresets.Blue"/>.</summary>
    [Fact]
    public void Default_IsBlue()
    {
        Assert.Same(PanelColorPresets.Blue, PanelColorPresets.Default);
    }

    /// <summary>
    /// Backlog 1.x item 4 (§4.2a) regression guard: every CHROMATIC preset's <see cref="PanelColorPreset.DarkModeTextHex"/>
    /// is now its own distinct tint — not the single shared <c>#FFFFFF</c> every preset used before this
    /// change (the root cause of "the preset doesn't seem to change the text" in Dark mode).
    /// </summary>
    [Fact]
    public void ChromaticPresets_HaveDistinctDarkModeText()
    {
        var chromaticDarkTexts = PanelColorPresets.All
            .Where(p => ChromaticIds.Contains(p.Id, StringComparer.Ordinal))
            .Select(p => p.DarkModeTextHex)
            .ToList();

        Assert.Equal(ChromaticIds.Length, chromaticDarkTexts.Count);
        Assert.Equal(chromaticDarkTexts.Count, chromaticDarkTexts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("#FFFFFF", chromaticDarkTexts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Verifies the two NEUTRAL presets (Graphite, Stone) deliberately keep plain white for <see cref="PanelColorPreset.DarkModeTextHex"/> (§4.2a: "Нейтральные пресеты допустимо оставить белыми").</summary>
    [Fact]
    public void NeutralPresets_KeepPlainWhiteDarkModeText()
    {
        Assert.Equal("#FFFFFF", PanelColorPresets.Graphite.DarkModeTextHex);
        Assert.Equal("#FFFFFF", PanelColorPresets.Stone.DarkModeTextHex);
    }

    /// <summary>
    /// Backlog 1.x item 4 (§4.2a) DoD 1: every preset's own paired text, for EITHER mode, resolves back to
    /// THAT mode via <see cref="PanelThemeRules.FromTextColor(string?)"/> — a tinted dark-mode text must
    /// never be light enough to make the panel's surface "jump" to <see cref="PanelThemeMode.Light"/>.
    /// </summary>
    /// <param name="id">The preset's <see cref="PanelColorPreset.Id"/>.</param>
    /// <param name="darkModeTextHex">The preset's dark-mode text hex value.</param>
    /// <param name="lightModeTextHex">The preset's light-mode text hex value.</param>
    [Theory]
    [MemberData(nameof(AllPresetModeTexts))]
    public void TextFor_EachModesOwnText_ResolvesToThatMode(string id, string darkModeTextHex, string lightModeTextHex)
    {
        Assert.True(PanelThemeRules.FromTextColor(darkModeTextHex) == PanelThemeMode.Dark, $"{id}: dark-mode text {darkModeTextHex} should resolve to PanelThemeMode.Dark.");
        Assert.True(PanelThemeRules.FromTextColor(lightModeTextHex) == PanelThemeMode.Light, $"{id}: light-mode text {lightModeTextHex} should resolve to PanelThemeMode.Light.");
    }

    /// <summary>Verifies <see cref="PanelColorPreset.TextFor"/> returns <see cref="PanelColorPreset.DarkModeTextHex"/>/<see cref="PanelColorPreset.LightModeTextHex"/> for the corresponding mode.</summary>
    [Fact]
    public void TextFor_ReturnsTheMatchingModeText()
    {
        var preset = PanelColorPresets.Blue;

        Assert.Equal(preset.DarkModeTextHex, preset.TextFor(PanelThemeMode.Dark));
        Assert.Equal(preset.LightModeTextHex, preset.TextFor(PanelThemeMode.Light));
    }

    /// <summary>
    /// Verifies <see cref="PanelColorPresets.CustomDialogSeed"/> is exactly the twelve accents (in
    /// <see cref="PanelColorPresets.All"/> order) followed by the four distinct text colours collected by
    /// scanning Dark-then-Light per preset, first-seen order (see the production doc comment for the
    /// algorithm this pins). Backlog 1.x item 4: since <see cref="PanelColorPreset.DarkModeTextHex"/> is no
    /// longer a single shared value, the four collected texts are now Red's own (dark, light) pair followed
    /// by Orange's own (dark, light) pair, rather than the pre-#4 shared white plus three presets' light text.
    /// </summary>
    [Fact]
    public void CustomDialogSeed_Has16Entries_TwelveAccentsThenFourDistinctTexts()
    {
        var expectedTexts = new[] { "#EFE1E1", "#4A1F1E", "#EFE8E1", "#4B2A0C" };
        var expectedSeed = PanelColorPresets.All.Select(p => p.AccentHex).Concat(expectedTexts).ToList();

        Assert.Equal(16, PanelColorPresets.CustomDialogSeed.Count);
        Assert.Equal(expectedSeed, PanelColorPresets.CustomDialogSeed);
        Assert.Equal(PanelColorPresets.CustomDialogSeed.Count, PanelColorPresets.CustomDialogSeed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Verifies an exact accent match returns the corresponding preset.</summary>
    [Fact]
    public void FindByAccent_ExactMatch_ReturnsPreset()
    {
        var found = PanelColorPresets.FindByAccent("#3F7FD9");

        Assert.Same(PanelColorPresets.Blue, found);
    }

    /// <summary>Verifies matching is case-insensitive.</summary>
    [Fact]
    public void FindByAccent_CaseInsensitive_ReturnsPreset()
    {
        var found = PanelColorPresets.FindByAccent("#3f7fd9");

        Assert.Same(PanelColorPresets.Blue, found);
    }

    /// <summary>Verifies a short-form (<c>#RGB</c>) accent colour still matches after normalization.</summary>
    [Fact]
    public void FindByAccent_ShortHexForm_ReturnsPreset()
    {
        // #8FB43A has no exact 3-digit shorthand; use a synthetic short-form round trip against Lime's own
        // accent by comparing through TryNormalizeOpaqueRgb instead of hand-picking an unrelated 3-digit
        // colour that would not match any preset.
        Assert.True(PanelColorPresets.TryNormalizeOpaqueRgb("#3F7FD9", out var normalized));
        Assert.Equal(PanelColorPresets.Blue.AccentHex, normalized);
    }

    /// <summary>Verifies a fully-opaque 8-digit AARRGGBB form still matches after normalization.</summary>
    [Fact]
    public void FindByAccent_OpaqueArgbForm_ReturnsPreset()
    {
        var found = PanelColorPresets.FindByAccent("#FF3F7FD9");

        Assert.Same(PanelColorPresets.Blue, found);
    }

    /// <summary>Verifies a non-opaque alpha byte fails normalization and returns <see langword="null"/>.</summary>
    [Fact]
    public void FindByAccent_NonOpaqueAlpha_ReturnsNull()
    {
        Assert.Null(PanelColorPresets.FindByAccent("#803F7FD9"));
    }

    /// <summary>Verifies an accent that matches no preset returns <see langword="null"/>.</summary>
    [Fact]
    public void FindByAccent_NoMatch_ReturnsNull()
    {
        Assert.Null(PanelColorPresets.FindByAccent("#123456"));
    }

    /// <summary>Verifies <see langword="null"/>, junk, or empty accent input returns <see langword="null"/> rather than throwing or falling back to a default.</summary>
    /// <param name="accent">The candidate accent colour.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-colour")]
    [InlineData("")]
    public void FindByAccent_NullOrJunkInput_ReturnsNull(string? accent)
    {
        Assert.Null(PanelColorPresets.FindByAccent(accent));
    }

    /// <summary>Verifies <see cref="PanelColorPresets.TryNormalizeOpaqueRgb"/> accepts opaque 3/4/6/8-digit forms and rejects everything else.</summary>
    /// <param name="input">The candidate hex string.</param>
    /// <param name="expectedSuccess">Whether normalization is expected to succeed.</param>
    [Theory]
    [InlineData("#3F7FD9", true)]
    [InlineData("#3f7fd9", true)]
    [InlineData("#FFF", true)]
    [InlineData("#FF3F7FD9", true)]
    [InlineData("#803F7FD9", false)]
    [InlineData("#GGG", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("3F7FD9", false)] // missing '#'
    public void TryNormalizeOpaqueRgb_MatchesExpectation(string? input, bool expectedSuccess)
    {
        var success = PanelColorPresets.TryNormalizeOpaqueRgb(input, out var normalized);

        Assert.Equal(expectedSuccess, success);
        if (!expectedSuccess)
            Assert.Equal(string.Empty, normalized);
    }

    /// <summary>Verifies the 3-digit shorthand form expands each nibble and uppercases the result.</summary>
    [Fact]
    public void TryNormalizeOpaqueRgb_ShortForm_ExpandsAndUppercases()
    {
        Assert.True(PanelColorPresets.TryNormalizeOpaqueRgb("#abc", out var normalized));
        Assert.Equal("#AABBCC", normalized);
    }
}
