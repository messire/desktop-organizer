using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="PanelCustomizeViewModel"/>'s <see cref="PanelCustomizeViewModel.Colors"/>
/// wiring (ADR-042): applying a preset writes both colours straight through to
/// <see cref="PanelCustomizeViewModel.Target"/> with exactly one <see cref="IWindowManager.SaveWindowsState"/>
/// call (via <see cref="SelectedPanelSettingsViewModel.ApplyColors"/>), retargeting to a panel whose colours
/// already match a preset updates <see cref="PanelColorEditorViewModel.SelectedPreset"/> without any
/// further action, "Reset to defaults" also resets the target's opacity/surface style/icon size to the
/// app's current new-panel defaults, and disposing the dialog stops <see cref="PanelCustomizeViewModel.Colors"/>
/// from reacting to further changes made directly to the (now detached) old target.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCustomizeColorsTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<ILayoutEditModeService> _layoutEditModeMock = new();
    private readonly Mock<IFolderPickerService> _folderPickerMock = new();
    private readonly Mock<IColorPickerService> _colorPickerMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly ILocalizationService _localization = TestLocalization.English;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeColorsTests"/> class, unlocking the
    /// layout by default (mirrors <c>PanelCustomizeViewModelTests</c>'s own constructor).
    /// </summary>
    public PanelCustomizeColorsTests() => _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(true);

    /// <summary>
    /// Verifies that applying a preset through <see cref="PanelCustomizeViewModel.Colors"/> writes BOTH
    /// colours to <see cref="PanelCustomizeViewModel.Target"/> (the preset's accent plus the preset's text
    /// for the target's CURRENT mode — Dark here, since the target starts with white text) and saves
    /// exactly once — not twice, the cost of writing the two colour setters independently.
    /// </summary>
    [Fact]
    public void ApplyPreset_WritesBothColoursToTarget_WithExactlyOneSave()
    {
        var sut = BuildSut();
        var target = new ChildWindowModel { AccentColor = "#111111", TextColor = "#FFFFFF" }; // Dark mode.
        sut.Target = target;
        _windowManagerMock.Invocations.Clear();

        var violet = sut.Colors.Presets.Single(p => p.Preset.Id == "Violet");
        violet.ApplyCommand.Execute(null);

        Assert.Equal(PanelColorPresets.Violet.AccentHex, target.AccentColor);
        Assert.Equal(PanelColorPresets.Violet.DarkModeTextHex, target.TextColor);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    /// <summary>
    /// Verifies that retargeting <see cref="PanelCustomizeViewModel.Target"/> to a panel whose accent
    /// already matches a preset updates <see cref="PanelColorEditorViewModel.SelectedPreset"/> accordingly,
    /// with no separate action required.
    /// </summary>
    [Fact]
    public void Retarget_ToPanelWithPresetAccent_UpdatesSelectedPreset()
    {
        var sut = BuildSut();

        sut.Target = new ChildWindowModel
        {
            AccentColor = PanelColorPresets.Red.AccentHex,
            TextColor = PanelColorPresets.Red.DarkModeTextHex,
        };

        Assert.Equal(PanelColorPresets.Red, sut.Colors.SelectedPreset);
    }

    /// <summary>
    /// M7: verifies that "Reset to defaults" resets the colour pair FROM <see cref="IAppSettings.DefaultBackgroundColor"/>/
    /// <see cref="IAppSettings.DefaultTextColor"/> (NOT <see cref="PanelColorPresets.Default"/>'s fixed
    /// pair — the Customize window mirrors the app's own current new-panel defaults) AND the target's
    /// opacity/surface style/icon size, all with exactly ONE <see cref="IWindowManager.SaveWindowsState"/>
    /// call (batched via <see cref="SelectedPanelSettingsViewModel.ApplyAppearance"/>).
    /// </summary>
    [Fact]
    public void Reset_AppliesDefaultColoursAndAppearanceFromSettings_WithExactlyOneSave()
    {
        _appSettingsMock.SetupGet(s => s.DefaultBackgroundColor).Returns("#334455");
        _appSettingsMock.SetupGet(s => s.DefaultTextColor).Returns("#AABBCC");
        _appSettingsMock.SetupGet(s => s.DefaultOpacity).Returns(0.65);
        _appSettingsMock.SetupGet(s => s.DefaultSurfaceStyle).Returns(nameof(PanelSurfaceStyle.Glass));
        _appSettingsMock.SetupGet(s => s.DefaultIconSize).Returns(64.0);
        var sut = BuildSut();
        var target = new ChildWindowModel { AccentColor = "#123456", TextColor = "#654321", SurfaceOpacity = 1.0, IconSize = 48 };
        sut.Target = target;
        _windowManagerMock.Invocations.Clear();

        sut.Colors.ResetCommand.Execute(null);

        Assert.Equal("#334455", target.AccentColor);
        Assert.Equal("#AABBCC", target.TextColor);
        Assert.Equal(0.65, target.SurfaceOpacity);
        Assert.Equal(PanelSurfaceStyle.Glass, target.SurfaceStyle);
        Assert.Equal(64.0, target.IconSize);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    /// <summary>
    /// M7: verifies <see cref="IAppSettings"/> is used strictly READ-ONLY by the Customize window's own
    /// "Reset to defaults" — a <see cref="MockBehavior.Strict"/> mock with only the five getters configured
    /// throws on any setter or <see cref="IAppSettings.Save"/> call, so this passing proves neither is ever
    /// invoked.
    /// </summary>
    [Fact]
    public void Reset_UsesAppSettingsStrictlyReadOnly_NeverWritesOrSaves()
    {
        var strictSettings = new Mock<IAppSettings>(MockBehavior.Strict);
        strictSettings.SetupGet(s => s.DefaultBackgroundColor).Returns("#334455");
        strictSettings.SetupGet(s => s.DefaultTextColor).Returns("#AABBCC");
        strictSettings.SetupGet(s => s.DefaultOpacity).Returns(0.65);
        strictSettings.SetupGet(s => s.DefaultSurfaceStyle).Returns(nameof(PanelSurfaceStyle.Glass));
        strictSettings.SetupGet(s => s.DefaultIconSize).Returns(64.0);
        var sut = new PanelCustomizeViewModel(
            _windowManagerMock.Object, _layoutEditModeMock.Object, _folderPickerMock.Object, _localization, _colorPickerMock.Object, strictSettings.Object);
        var target = new ChildWindowModel { AccentColor = "#123456", TextColor = "#654321" };
        sut.Target = target;

        var exception = Record.Exception(() => sut.Colors.ResetCommand.Execute(null));

        Assert.Null(exception);
        strictSettings.VerifyGet(s => s.DefaultBackgroundColor, Times.Once);
        strictSettings.VerifyGet(s => s.DefaultTextColor, Times.Once);
        strictSettings.VerifyGet(s => s.DefaultOpacity, Times.Once);
        strictSettings.VerifyGet(s => s.DefaultSurfaceStyle, Times.Once);
        strictSettings.VerifyGet(s => s.DefaultIconSize, Times.Once);
    }

    /// <summary>
    /// Verifies that once <see cref="PanelCustomizeViewModel.Dispose"/> has run, a colour change made
    /// directly on the previous <see cref="PanelCustomizeViewModel.Target"/> (bypassing this dialog
    /// entirely — e.g. the panel's own context menu) no longer reaches <see cref="PanelCustomizeViewModel.Colors"/>:
    /// <see cref="SelectedPanelSettingsViewModel.Dispose"/> already unsubscribes from that panel's
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> (FIX-B fix-cycle M-2),
    /// so <see cref="PanelCustomizeViewModel"/>'s own appearance-changed handler never fires and
    /// <see cref="PanelColorEditorViewModel.Refresh"/> is never called again.
    /// </summary>
    [Fact]
    public void Dispose_ThenTargetColourChangedElsewhere_NeverRefreshesColors()
    {
        var sut = BuildSut();
        var target = new ChildWindowModel { AccentColor = "#111111", TextColor = "#222222" };
        sut.Target = target;

        sut.Dispose();

        var raised = false;
        sut.Colors.PropertyChanged += (_, _) => raised = true;

        target.AccentColor = "#ABCDEF";

        Assert.False(raised);
    }

    private PanelCustomizeViewModel BuildSut() =>
        new(_windowManagerMock.Object, _layoutEditModeMock.Object, _folderPickerMock.Object, _localization, _colorPickerMock.Object, _appSettingsMock.Object);
}
