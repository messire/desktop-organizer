using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="GlobalDefaultsViewModel"/>'s <see cref="GlobalDefaultsViewModel.DefaultColors"/>
/// wiring (ADR-042): applying a preset writes both <see cref="GlobalDefaultsViewModel.DefaultBackgroundColor"/>/
/// <see cref="GlobalDefaultsViewModel.DefaultTextColor"/> with exactly one <see cref="IAppSettings.Save"/>
/// call, the pre-existing legacy default pair never highlights a preset, and
/// <see cref="GlobalDefaultsViewModel.ApplyFromPanel"/> picking up a panel whose accent already matches a
/// preset updates <see cref="PanelColorEditorViewModel.SelectedPreset"/> the same way a direct preset click
/// does. <c>MainWindowViewModel.DefaultColors</c> being the exact same instance as
/// <see cref="GlobalDefaultsViewModel.DefaultColors"/> is covered by
/// <c>MainWindowViewModelTests.DefaultColors_IsTheSameInstanceAsGlobalDefaultsDefaultColors</c> instead —
/// that assertion needs <c>MainWindowViewModel</c>'s own (much larger) constructor, already wired up in
/// that test class.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class GlobalDefaultsColorsTests
{
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IColorPickerService> _colorPickerMock = new();

    /// <summary>
    /// Verifies that applying a preset through <see cref="GlobalDefaultsViewModel.DefaultColors"/> writes
    /// BOTH <see cref="GlobalDefaultsViewModel.DefaultBackgroundColor"/>/<see cref="GlobalDefaultsViewModel.DefaultTextColor"/>
    /// (the preset's accent plus its text for the CURRENT mode — Dark here, since the starting default text
    /// is white) and saves exactly once — the same one-save batching <c>ApplyFromPanel</c> already has, per
    /// <see cref="GlobalDefaultsViewModel.DefaultColors"/>'s own remarks.
    /// </summary>
    [Fact]
    public void ApplyPreset_WritesBothDefaultColours_WithExactlyOneSave()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor, "#111111");
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor, "#FFFFFF");
        var sut = BuildSut();
        _appSettingsMock.Invocations.Clear();

        var violet = sut.DefaultColors.Presets.Single(p => p.Preset.Id == "Violet");
        violet.ApplyCommand.Execute(null);

        Assert.Equal(PanelColorPresets.Violet.AccentHex, sut.DefaultBackgroundColor);
        Assert.Equal(PanelColorPresets.Violet.DarkModeTextHex, sut.DefaultTextColor);
        _appSettingsMock.Verify(s => s.Save(), Times.Once);
    }

    /// <summary>
    /// Verifies that the pre-existing legacy default colour pair (<c>#1E1E1E</c>/<c>#DDDDDD</c> — the
    /// values every install shipped before this feature existed) never highlights any of the twelve
    /// presets.
    /// </summary>
    [Fact]
    public void LegacyDefaultColourPair_SelectsNoPreset()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor, "#1E1E1E");
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor, "#DDDDDD");
        var sut = BuildSut();

        Assert.Null(sut.DefaultColors.SelectedPreset);
        Assert.DoesNotContain(sut.DefaultColors.Presets, p => p.IsSelected);
    }

    /// <summary>
    /// Verifies that <see cref="GlobalDefaultsViewModel.ApplyFromPanel"/> picking up a panel whose accent
    /// already matches a preset updates <see cref="PanelColorEditorViewModel.SelectedPreset"/> too, exactly
    /// like a direct preset click would — both go through <see cref="GlobalDefaultsViewModel.DefaultColors"/>'s
    /// own <see cref="PanelColorEditorViewModel.Refresh"/>.
    /// </summary>
    [Fact]
    public void ApplyFromPanel_WithPresetAccent_UpdatesSelectedPreset()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor, "#000000");
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor, "#FFFFFE"); // deliberately NOT any preset's text.
        var sut = BuildSut();

        var panel = new ChildWindowModel
        {
            AccentColor = PanelColorPresets.Red.AccentHex,
            TextColor = PanelColorPresets.Red.DarkModeTextHex,
        };

        sut.ApplyFromPanel(panel);

        Assert.Equal(PanelColorPresets.Red, sut.DefaultColors.SelectedPreset);
    }

    /// <summary>
    /// Verifies that "Reset to defaults" applies <see cref="PanelColorPresets.Default"/>'s Dark pair to the
    /// new-panel defaults with no extra reset action (unlike <c>PanelCustomizeViewModel</c>, resetting the
    /// defaults' own opacity/surface style/icon size to themselves would be a meaningless no-op, so none is
    /// supplied) and never throws.
    /// </summary>
    [Fact]
    public void Reset_AppliesDefaultPresetDarkPair_AndDoesNotThrow()
    {
        _appSettingsMock.SetupProperty(s => s.DefaultBackgroundColor, "#123456");
        _appSettingsMock.SetupProperty(s => s.DefaultTextColor, "#654321");
        var sut = BuildSut();

        var exception = Record.Exception(() => sut.DefaultColors.ResetCommand.Execute(null));

        Assert.Null(exception);
        Assert.Equal(PanelColorPresets.Default.AccentHex, sut.DefaultBackgroundColor);
        Assert.Equal(PanelColorPresets.Default.DarkModeTextHex, sut.DefaultTextColor);
    }

    private GlobalDefaultsViewModel BuildSut() => new(_appSettingsMock.Object, _windowManagerMock.Object, _colorPickerMock.Object);
}
