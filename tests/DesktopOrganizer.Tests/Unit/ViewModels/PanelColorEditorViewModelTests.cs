using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="PanelColorEditorViewModel"/> (ADR-042): choosing an accent (preset or custom)
/// writes both colours through the single <c>setBoth</c> delegate deriving text from the CURRENT
/// <see cref="PanelThemeMode"/>, switching Mode writes text only (re-deriving it for the current accent),
/// the Advanced card's raw accent/text setters each write exactly one colour independently, "Reset to
/// defaults" applies <see cref="PanelColorPresets.Default"/>'s Dark pair (and any supplied extra reset
/// action), and a cancelled picker changes nothing.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelColorEditorViewModelTests
{
    private readonly Mock<IColorPickerService> _colorPickerMock = new();
    private readonly List<string> _setAccentCalls = [];
    private readonly List<string> _setTextCalls = [];
    private readonly List<(string Accent, string Text)> _setBothCalls = [];
    private int _resetOverrideCalls;
    private string _accent = PanelColorPresets.Blue.AccentHex;
    private string _text = PanelColorPresets.Blue.DarkModeTextHex; // #FFFFFF -> Mode.Dark

    /// <summary>Verifies that <see cref="PanelColorEditorViewModel.Presets"/> exposes exactly the twelve presets, in <see cref="PanelColorPresets.All"/>'s order.</summary>
    [Fact]
    public void Presets_ExposesAllTwelveInOrder()
    {
        var sut = BuildSut();

        Assert.Equal(PanelColorPresets.All.Select(p => p.Id), sut.Presets.Select(p => p.Preset.Id));
    }

    /// <summary>
    /// Verifies that <see cref="PanelColorEditorViewModel.SwatchItems"/> (release-polish-1.0 visual review
    /// item 1) is exactly <see cref="PanelColorEditorViewModel.Presets"/> followed by a single trailing
    /// <see cref="CustomSwatchSlotMarker"/> sentinel — the swatch grid's own flat 13-cell item source.
    /// </summary>
    [Fact]
    public void SwatchItems_IsAllPresetsPlusOneTrailingCustomSlotMarker()
    {
        var sut = BuildSut();

        Assert.Equal(13, sut.SwatchItems.Count);
        Assert.Equal(sut.Presets, sut.SwatchItems.Take(12).Cast<ColorPresetOption>());
        Assert.IsType<CustomSwatchSlotMarker>(sut.SwatchItems[^1]);
    }

    /// <summary>A text colour with luminance at/above the threshold (e.g. white) derives <see cref="PanelThemeMode.Dark"/>.</summary>
    [Fact]
    public void Mode_WhiteText_IsDark()
    {
        _text = "#FFFFFF";
        var sut = BuildSut();

        Assert.Equal(PanelThemeMode.Dark, sut.Mode);
    }

    /// <summary>A text colour with luminance below the threshold (e.g. near-black) derives <see cref="PanelThemeMode.Light"/>.</summary>
    [Fact]
    public void Mode_DarkText_IsLight()
    {
        _text = PanelColorPresets.Blue.LightModeTextHex; // #172F4B — dark text implies a light surface.
        var sut = BuildSut();

        Assert.Equal(PanelThemeMode.Light, sut.Mode);
    }

    /// <summary>When the current accent exactly matches a preset, that preset's option is selected and no other is.</summary>
    [Fact]
    public void MatchingAccent_SelectsExactlyOnePreset()
    {
        _accent = PanelColorPresets.Red.AccentHex;
        var sut = BuildSut();

        Assert.Equal(PanelColorPresets.Red, sut.SelectedPreset);
        Assert.Single(sut.Presets, p => p.IsSelected);
        Assert.True(sut.Presets.Single(p => p.Preset.Id == "Red").IsSelected);
    }

    /// <summary>A custom (non-preset) accent selects no preset at all.</summary>
    [Fact]
    public void CustomAccent_SelectsNoPreset()
    {
        _accent = "#123456";
        var sut = BuildSut();

        Assert.Null(sut.SelectedPreset);
        Assert.DoesNotContain(sut.Presets, p => p.IsSelected);
    }

    /// <summary>Applying a preset swatch in Dark mode writes the preset's accent and its <see cref="PanelColorPreset.DarkModeTextHex"/> through <c>setBoth</c> exactly once.</summary>
    [Fact]
    public void ApplyPreset_InDarkMode_WritesAccentAndDarkModeText()
    {
        var sut = BuildSut(); // starts Dark (white text)
        var violet = sut.Presets.Single(p => p.Preset.Id == "Violet");

        violet.ApplyCommand.Execute(null);

        Assert.Equal([(PanelColorPresets.Violet.AccentHex, PanelColorPresets.Violet.DarkModeTextHex)], _setBothCalls);
        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setTextCalls);
    }

    /// <summary>Applying a preset swatch in Light mode writes the preset's accent and its <see cref="PanelColorPreset.LightModeTextHex"/>.</summary>
    [Fact]
    public void ApplyPreset_InLightMode_WritesAccentAndLightModeText()
    {
        _text = PanelThemeRules.DefaultLightModeText; // Mode.Light
        var sut = BuildSut();
        var violet = sut.Presets.Single(p => p.Preset.Id == "Violet");

        violet.ApplyCommand.Execute(null);

        Assert.Equal([(PanelColorPresets.Violet.AccentHex, PanelColorPresets.Violet.LightModeTextHex)], _setBothCalls);
    }

    /// <summary>Applying a preset re-selects it (via the VM's own post-apply <c>Refresh()</c>) once the backing colours reflect the write.</summary>
    [Fact]
    public void ApplyPreset_RefreshesSelection()
    {
        var sut = BuildSut();
        var violet = sut.Presets.Single(p => p.Preset.Id == "Violet");

        // The fakes above don't route setBoth back into _accent/_text automatically — do it explicitly,
        // mirroring what a real owner's ApplyColors/SetDefaultColors implementation does before this VM's
        // own Refresh() call reads the colours back.
        violet.ApplyCommand.Execute(null);
        _accent = PanelColorPresets.Violet.AccentHex;
        _text = PanelColorPresets.Violet.DarkModeTextHex;
        sut.Refresh();

        Assert.True(violet.IsSelected);
        Assert.Equal(PanelColorPresets.Violet, sut.SelectedPreset);
    }

    /// <summary>Choosing a CUSTOM accent (the swatch grid's "Custom…" cell) writes the picked accent plus the current mode's DEFAULT text (no preset match) through <c>setBoth</c>.</summary>
    [Fact]
    public void PickCustomAccent_PickedColor_WritesAccentAndDefaultModeText()
    {
        _colorPickerMock
            .Setup(p => p.PickColor(PanelColorPresets.Blue.AccentHex, PanelColorPresets.CustomDialogSeed))
            .Returns("#ABCDEF");
        var sut = BuildSut(); // Dark mode

        sut.PickCustomAccentCommand.Execute(null);

        Assert.Equal([("#ABCDEF", PanelThemeRules.DefaultDarkModeText)], _setBothCalls);
        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setTextCalls);
    }

    /// <summary>A cancelled ("Custom…") accent picker (<see langword="null"/> result) writes nothing.</summary>
    [Fact]
    public void PickCustomAccent_Cancelled_WritesNothing()
    {
        _colorPickerMock
            .Setup(p => p.PickColor(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns((string?)null);
        var sut = BuildSut();

        sut.PickCustomAccentCommand.Execute(null);

        Assert.Empty(_setBothCalls);
    }

    /// <summary>Re-picking the SAME accent colour the editor already holds must not call the setter — an unchanged edit is a no-op.</summary>
    [Fact]
    public void PickCustomAccent_UnchangedColor_WritesNothing()
    {
        _colorPickerMock
            .Setup(p => p.PickColor(PanelColorPresets.Blue.AccentHex, PanelColorPresets.CustomDialogSeed))
            .Returns(PanelColorPresets.Blue.AccentHex);
        var sut = BuildSut();

        sut.PickCustomAccentCommand.Execute(null);

        Assert.Empty(_setBothCalls);
    }

    /// <summary>Switching Mode while the accent matches a preset writes ONLY the text colour, re-derived for the new mode.</summary>
    [Fact]
    public void SetMode_AccentMatchesPreset_WritesOnlyText()
    {
        _accent = PanelColorPresets.Red.AccentHex;
        _text = PanelColorPresets.Red.DarkModeTextHex;
        var sut = BuildSut();

        sut.SetModeCommand.Execute(PanelThemeMode.Light);

        Assert.Equal([PanelColorPresets.Red.LightModeTextHex], _setTextCalls);
        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setBothCalls);
    }

    /// <summary>Switching Mode while the accent matches no preset writes the mode's own default text.</summary>
    [Fact]
    public void SetMode_CustomAccent_WritesDefaultModeText()
    {
        _accent = "#123456";
        var sut = BuildSut();

        sut.SetModeCommand.Execute(PanelThemeMode.Light);

        Assert.Equal([PanelThemeRules.DefaultLightModeText], _setTextCalls);
    }

    /// <summary>"Custom…" for the text colour (Advanced card) writes only the text colour — a raw, mode-independent edit.</summary>
    [Fact]
    public void PickCustomText_PickedColor_WritesTextOnly()
    {
        _colorPickerMock
            .Setup(p => p.PickColor(PanelColorPresets.Blue.DarkModeTextHex, PanelColorPresets.CustomDialogSeed))
            .Returns("#112233");
        var sut = BuildSut();

        sut.PickCustomTextCommand.Execute(null);

        Assert.Equal(["#112233"], _setTextCalls);
        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setBothCalls);
    }

    /// <summary>A cancelled ("Custom…") text picker writes nothing.</summary>
    [Fact]
    public void PickCustomText_Cancelled_WritesNothing()
    {
        _colorPickerMock
            .Setup(p => p.PickColor(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns((string?)null);
        var sut = BuildSut();

        sut.PickCustomTextCommand.Execute(null);

        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setTextCalls);
    }

    /// <summary>Re-picking the SAME text colour the editor already holds must not call the setter.</summary>
    [Fact]
    public void PickCustomText_UnchangedColor_WritesNothing()
    {
        _colorPickerMock
            .Setup(p => p.PickColor(PanelColorPresets.Blue.DarkModeTextHex, PanelColorPresets.CustomDialogSeed))
            .Returns(PanelColorPresets.Blue.DarkModeTextHex);
        var sut = BuildSut();

        sut.PickCustomTextCommand.Execute(null);

        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setTextCalls);
    }

    /// <summary>The Advanced card's raw <see cref="PanelColorEditorViewModel.AccentColor"/> setter writes ONLY the accent, independent of mode.</summary>
    [Fact]
    public void RawAccentColorSetter_WritesAccentOnlyIndependentOfMode()
    {
        var sut = BuildSut();

        sut.AccentColor = "#654321";

        Assert.Equal(["#654321"], _setAccentCalls);
        Assert.Empty(_setTextCalls);
        Assert.Empty(_setBothCalls);
    }

    /// <summary>The Advanced card's raw <see cref="PanelColorEditorViewModel.TextColor"/> setter writes ONLY the text, independent of accent/preset.</summary>
    [Fact]
    public void RawTextColorSetter_WritesTextOnlyIndependentOfAccent()
    {
        var sut = BuildSut();

        sut.TextColor = "#654321";

        Assert.Equal(["#654321"], _setTextCalls);
        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setBothCalls);
    }

    /// <summary>M7: with no reset override supplied (e.g. <c>GlobalDefaultsViewModel</c>), "Reset to defaults" applies <see cref="PanelColorPresets.Default"/>'s Dark pair through <c>setBoth</c>, regardless of the mode/accent in effect beforehand.</summary>
    [Fact]
    public void Reset_NoOverrideSupplied_AppliesDefaultPresetDarkPair()
    {
        _accent = "#123456";
        _text = PanelThemeRules.DefaultLightModeText;
        var sut = BuildSut();

        sut.ResetCommand.Execute(null);

        Assert.Equal([(PanelColorPresets.Default.AccentHex, PanelColorPresets.Default.DarkModeTextHex)], _setBothCalls);
    }

    /// <summary>M7: when a reset override is supplied (e.g. <c>PanelCustomizeViewModel</c>), "Reset to defaults" invokes ONLY that override — the default <c>setBoth</c>-based reset never runs.</summary>
    [Fact]
    public void Reset_WithOverrideSupplied_InvokesOverrideInsteadOfDefault()
    {
        var sut = BuildSut(withResetOverride: true);

        sut.ResetCommand.Execute(null);

        Assert.Equal(1, _resetOverrideCalls);
        Assert.Empty(_setBothCalls);
    }

    /// <summary>"Reset to defaults" never throws when no reset override was supplied (e.g. <c>GlobalDefaultsViewModel</c>).</summary>
    [Fact]
    public void Reset_NoOverrideSupplied_DoesNotThrow()
    {
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.ResetCommand.Execute(null));

        Assert.Null(exception);
    }

    /// <summary>UX#1: <see cref="PanelColorEditorViewModel.IsCustomSelected"/> is <see langword="true"/> when the current accent matches no preset.</summary>
    [Fact]
    public void IsCustomSelected_CustomAccent_IsTrue()
    {
        _accent = "#123456";
        var sut = BuildSut();

        Assert.True(sut.IsCustomSelected);
    }

    /// <summary>UX#1: <see cref="PanelColorEditorViewModel.IsCustomSelected"/> is <see langword="false"/> when the current accent matches a preset.</summary>
    [Fact]
    public void IsCustomSelected_PresetAccent_IsFalse()
    {
        _accent = PanelColorPresets.Red.AccentHex;
        var sut = BuildSut();

        Assert.False(sut.IsCustomSelected);
    }

    /// <summary>UX#1: applying a preset flips <see cref="PanelColorEditorViewModel.IsCustomSelected"/> from <see langword="true"/> to <see langword="false"/> once the applied colours are read back via <see cref="PanelColorEditorViewModel.Refresh"/>.</summary>
    [Fact]
    public void IsCustomSelected_AfterApplyingPreset_BecomesFalse()
    {
        _accent = "#123456";
        var sut = BuildSut();
        Assert.True(sut.IsCustomSelected);

        var violet = sut.Presets.Single(p => p.Preset.Id == "Violet");
        violet.ApplyCommand.Execute(null);
        _accent = PanelColorPresets.Violet.AccentHex;
        _text = PanelColorPresets.Violet.DarkModeTextHex;
        sut.Refresh();

        Assert.False(sut.IsCustomSelected);
    }

    /// <summary>m11: re-applying the SAME accent the editor already holds (case-insensitive) writes nothing.</summary>
    [Fact]
    public void ApplyAccent_UnchangedAccent_WritesNothing()
    {
        var sut = BuildSut(); // starts on PanelColorPresets.Blue.AccentHex

        sut.ApplyAccent(PanelColorPresets.Blue.AccentHex.ToLowerInvariant());

        Assert.Empty(_setBothCalls);
    }

    /// <summary>M7: <see cref="PanelColorEditorViewModel.ResetDescriptionKey"/> defaults to the generic "Reset to defaults" key when the host does not supply an override.</summary>
    [Fact]
    public void ResetDescriptionKey_DefaultsToGenericKey()
    {
        var sut = BuildSut();

        Assert.Equal("Common_ResetToDefaults_Button", sut.ResetDescriptionKey);
    }

    /// <summary>M7: <see cref="PanelColorEditorViewModel.ResetDescriptionKey"/> exposes whatever key the host supplied.</summary>
    [Fact]
    public void ResetDescriptionKey_UsesHostSuppliedKey()
    {
        var sut = new PanelColorEditorViewModel(
            _colorPickerMock.Object,
            () => _accent,
            v => _setAccentCalls.Add(v),
            () => _text,
            v => _setTextCalls.Add(v),
            (accent, text) => _setBothCalls.Add((accent, text)),
            resetDescriptionKey: "Customize_ResetColors_Description");

        Assert.Equal("Customize_ResetColors_Description", sut.ResetDescriptionKey);
    }

    /// <summary>M3: switching to the mode already in effect writes nothing.</summary>
    [Fact]
    public void SetMode_SameMode_WritesNothing()
    {
        var sut = BuildSut(); // starts Dark (white text)

        sut.SetModeCommand.Execute(PanelThemeMode.Dark);

        Assert.Empty(_setTextCalls);
        Assert.Empty(_setAccentCalls);
        Assert.Empty(_setBothCalls);
    }

    private PanelColorEditorViewModel BuildSut(bool withResetOverride = false) => new(
        _colorPickerMock.Object,
        () => _accent,
        v => _setAccentCalls.Add(v),
        () => _text,
        v => _setTextCalls.Add(v),
        (accent, text) => _setBothCalls.Add((accent, text)),
        withResetOverride ? () => _resetOverrideCalls++ : null);
}
