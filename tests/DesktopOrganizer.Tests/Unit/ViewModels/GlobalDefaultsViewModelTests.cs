using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="GlobalDefaultsViewModel"/>'s ADR-011 Amendment (2026-09-24) hook: changing
/// <see cref="GlobalDefaultsViewModel.GridSize"/> must re-align every currently open panel onto the new
/// grid via <see cref="IWindowManager.NormalizePanelsToGrid"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class GlobalDefaultsViewModelTests
{
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();

    /// <summary>
    /// Verifies that changing <see cref="GlobalDefaultsViewModel.GridSize"/> to a different value
    /// normalizes every open panel onto the new grid and saves the setting.
    /// </summary>
    [Fact]
    public void GridSize_ChangedToADifferentValue_NormalizesOpenPanelsAndSaves()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 32);
        var sut = BuildSut();

        sut.GridSize = 48;

        Assert.Equal(48, _appSettingsMock.Object.GridSize);
        _appSettingsMock.Verify(s => s.Save(), Times.Once);
        _windowManagerMock.Verify(w => w.NormalizePanelsToGrid(), Times.Once);
    }

    /// <summary>
    /// Verifies that re-assigning <see cref="GlobalDefaultsViewModel.GridSize"/> to its current value
    /// never normalizes panels or saves settings.
    /// </summary>
    [Fact]
    public void GridSize_SetToTheSameValue_NeverNormalizesOrSaves()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 32);
        var sut = BuildSut();

        sut.GridSize = 32;

        _appSettingsMock.Verify(s => s.Save(), Times.Never);
        _windowManagerMock.Verify(w => w.NormalizePanelsToGrid(), Times.Never);
    }

    /// <summary>
    /// Verifies that setting <see cref="GlobalDefaultsViewModel.GridSize"/> multiple times, including a
    /// same-value no-op, normalizes panels exactly once per actual value change.
    /// </summary>
    [Fact]
    public void GridSize_ChangedMultipleTimes_NormalizesOncePerActualChange()
    {
        _appSettingsMock.SetupProperty(s => s.GridSize, 1);
        var sut = BuildSut();

        sut.GridSize = 32;
        sut.GridSize = 32; // no-op, same value again.
        sut.GridSize = 48;

        _windowManagerMock.Verify(w => w.NormalizePanelsToGrid(), Times.Exactly(2));
    }

    private GlobalDefaultsViewModel BuildSut() => new(_appSettingsMock.Object, _windowManagerMock.Object);
}
