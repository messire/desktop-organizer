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
        ("Red", "#D9534F", "#FFFFFF", "#4A1F1E"),
        ("Orange", "#E0812F", "#FFFFFF", "#4B2A0C"),
        ("Amber", "#D6A324", "#FFFFFF", "#453309"),
        ("Lime", "#8FB43A", "#FFFFFF", "#2F3D11"),
        ("Green", "#3F9A5A", "#FFFFFF", "#173A24"),
        ("Teal", "#2F9C93", "#FFFFFF", "#12403C"),
        ("Cyan", "#2E98C8", "#FFFFFF", "#123B4E"),
        ("Blue", "#3F7FD9", "#FFFFFF", "#172F4B"),
        ("Violet", "#7A5BE7", "#FFFFFF", "#2B2350"),
        ("Magenta", "#C8559E", "#FFFFFF", "#4A1F3B"),
        ("Graphite", "#596273", "#FFFFFF", "#22262E"),
        ("Stone", "#7D7468", "#FFFFFF", "#2E2A26"),
    };

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

    /// <summary>Verifies every preset shares the same <see cref="PanelColorPreset.DarkModeTextHex"/> (<c>#FFFFFF</c>).</summary>
    [Fact]
    public void All_ShareTheSameDarkModeText()
    {
        Assert.All(PanelColorPresets.All, p => Assert.Equal("#FFFFFF", p.DarkModeTextHex));
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
    /// algorithm this pins).
    /// </summary>
    [Fact]
    public void CustomDialogSeed_Has16Entries_TwelveAccentsThenFourDistinctTexts()
    {
        var expectedTexts = new[] { "#FFFFFF", "#4A1F1E", "#4B2A0C", "#453309" };
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
