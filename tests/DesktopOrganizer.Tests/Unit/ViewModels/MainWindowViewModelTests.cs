using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="MainWindowViewModel.RemovePanelCommand"/> (<c>RemovePanel</c>).
/// Covers the confirmation gate added in front of the irreversible
/// <see cref="IWindowManager.RemoveChildWindow(Guid, bool)"/> call (fences-ux-parity-reviewer finding
/// #2): panel deletion moves app-managed item copies past the Recycle Bin (<c>File.Delete</c> /
/// <c>Directory.Delete</c>), so it must never happen without an explicit user confirmation.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class MainWindowViewModelTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IAutostartService> _autostartMock = new();
    private readonly Mock<IPageManager> _pageManagerMock = new();
    private readonly Mock<ISystemIconsPanelManager> _systemIconsPanelManagerMock = new();
    private readonly Mock<IConfirmationService> _confirmationMock = new();
    private readonly Mock<ILayoutSnapshotService> _layoutSnapshotServiceMock = new();
    private readonly Mock<IPanelItemStorageService> _itemStorageMock = new();
    private readonly Mock<IDesktopIconsService> _desktopIconsMock = new();

    private MainWindowViewModel BuildSut()
    {
        _pageManagerMock.Setup(p => p.Pages).Returns([]);
        _layoutSnapshotServiceMock.Setup(s => s.Snapshots).Returns([]);

        // MainWindowViewModel's constructor subscribes to Model.PropertyChanged (finalization MAJOR
        // M-3: SystemIconsVirtualDesktopFeatureIneffective forwarding) — an un-stubbed Mock<T> property
        // of reference type returns null, which would NRE there.
        _systemIconsPanelManagerMock.Setup(m => m.Model).Returns(new SystemIconsPanelModel());

        var layoutSnapshots = new LayoutSnapshotsViewModel(_layoutSnapshotServiceMock.Object, _confirmationMock.Object);

        return new MainWindowViewModel(
            _windowManagerMock.Object,
            _appSettingsMock.Object,
            _autostartMock.Object,
            _pageManagerMock.Object,
            layoutSnapshots,
            _systemIconsPanelManagerMock.Object,
            _confirmationMock.Object,
            _itemStorageMock.Object,
            _desktopIconsMock.Object);
    }

    [Fact]
    public void RemovePanel_ConfirmationGranted_CallsRemoveChildWindow()
    {
        // Arrange
        var sut = BuildSut();
        var panel = new ChildWindowModel { FilePaths = ["C:\\managed\\a.txt"] };
        sut.SelectedPanel = panel;
        _itemStorageMock.Setup(s => s.IsManaged("C:\\managed\\a.txt")).Returns(true);

        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);

        // Act
        sut.RemovePanelCommand.Execute(null);

        // Assert
        _windowManagerMock.Verify(
            w => w.RemoveChildWindow(panel.Id, true),
            Times.Once);
    }

    [Fact]
    public void RemovePanel_ConfirmationDeclined_DoesNotCallRemoveChildWindow()
    {
        // Arrange
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(false);

        // Act
        sut.RemovePanelCommand.Execute(null);

        // Assert
        _windowManagerMock.Verify(
            w => w.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public void RemovePanel_NoSelectedPanel_NeverAsksForConfirmation()
    {
        // Arrange — CanRemovePanel already guards the command, but RemovePanel itself must also
        // no-op defensively if invoked with no selection.
        var sut = BuildSut();

        // Act
        sut.RemovePanelCommand.Execute(null);

        // Assert
        _confirmationMock.Verify(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()), Times.Never);
        _windowManagerMock.Verify(w => w.RemoveChildWindow(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void RemovePanel_HasManagedFiles_UsesWarningIconAndDataLossText()
    {
        // Arrange
        var sut = BuildSut();
        var panel = new ChildWindowModel { FilePaths = ["C:\\managed\\a.txt"] };
        sut.SelectedPanel = panel;
        _itemStorageMock.Setup(s => s.IsManaged("C:\\managed\\a.txt")).Returns(true);

        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);

        // Act
        sut.RemovePanelCommand.Execute(null);

        // Assert
        _confirmationMock.Verify(
            c => c.Confirm(
                It.Is<string>(m => m.Contains("deleted from disk")),
                It.IsAny<string>(),
                MessageBoxImage.Warning),
            Times.Once);
    }

    [Fact]
    public void RemovePanel_NoManagedFiles_UsesNeutralTextWithoutWarningIcon()
    {
        // Arrange
        var sut = BuildSut();
        var panel = new ChildWindowModel { FilePaths = ["C:\\desktop\\shortcut.lnk"] };
        sut.SelectedPanel = panel;
        _itemStorageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(false);

        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);

        // Act
        sut.RemovePanelCommand.Execute(null);

        // Assert — neutral overload (default Question icon), not the Warning one.
        _confirmationMock.Verify(
            c => c.Confirm(
                It.Is<string>(m => !m.Contains("deleted from disk")),
                It.IsAny<string>(),
                MessageBoxImage.Question),
            Times.Once);
        _confirmationMock.Verify(
            c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), MessageBoxImage.Warning),
            Times.Never);
    }

    [Fact]
    public void DesktopIconsVisible_Get_DelegatesToDesktopIconsService()
    {
        // Arrange
        _desktopIconsMock.Setup(d => d.AreIconsVisible).Returns(true);
        var sut = BuildSut();

        // Act & Assert
        Assert.True(sut.DesktopIconsVisible);
    }

    [Fact]
    public void DesktopIconsVisible_SetToDifferentValue_CallsSetIconsVisibleAndRaisesPropertyChanged()
    {
        // Arrange
        _desktopIconsMock.Setup(d => d.AreIconsVisible).Returns(true);
        var sut = BuildSut();

        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, args) => raisedProperties.Add(args.PropertyName);

        // Act
        sut.DesktopIconsVisible = false;

        // Assert
        _desktopIconsMock.Verify(d => d.SetIconsVisible(false), Times.Once);
        Assert.Contains(nameof(MainWindowViewModel.DesktopIconsVisible), raisedProperties);
    }

    [Fact]
    public void DesktopIconsVisible_SetToSameValue_DoesNotCallSetIconsVisible()
    {
        // Arrange
        _desktopIconsMock.Setup(d => d.AreIconsVisible).Returns(true);
        var sut = BuildSut();

        // Act
        sut.DesktopIconsVisible = true;

        // Assert
        _desktopIconsMock.Verify(d => d.SetIconsVisible(It.IsAny<bool>()), Times.Never);
    }

    // ── ADR-014 Block B1: SelectedPanelSortMode / SelectedPanelSupportsManualOrder ─────────────────

    [Fact]
    public void SelectedPanelSortMode_Get_ReturnsSelectedPanelsSortMode()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { SortMode = PanelSortMode.Name };
        sut.SelectedPanel = panel;

        Assert.Equal(PanelSortMode.Name, sut.SelectedPanelSortMode);
    }

    [Fact]
    public void SelectedPanelSortMode_Get_NoSelectedPanel_DefaultsToManual()
    {
        var sut = BuildSut();

        Assert.Equal(PanelSortMode.Manual, sut.SelectedPanelSortMode);
    }

    [Fact]
    public void SelectedPanelSortMode_Set_WritesToModelAndSavesWindowsState()
    {
        // Same persistence pattern as SelectedPanelViewMode/SelectedPanelTitle (SetSelectedPanel).
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        sut.SelectedPanelSortMode = PanelSortMode.Name;

        Assert.Equal(PanelSortMode.Name, panel.SortMode);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    [Fact]
    public void SelectedPanelSortMode_Set_RaisesPropertyChanged()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.SelectedPanelSortMode = PanelSortMode.Name;

        Assert.Contains(nameof(MainWindowViewModel.SelectedPanelSortMode), raisedProperties);
    }

    [Fact]
    public void SelectedPanelSupportsManualOrder_PanelWithSourceFolderPath_IsFalse()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { SourceFolderPath = @"C:\fake\portal" };
        sut.SelectedPanel = panel;

        Assert.False(sut.SelectedPanelSupportsManualOrder);
    }

    [Fact]
    public void SelectedPanelSupportsManualOrder_PanelWithoutSourceFolderPath_IsTrue()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        Assert.True(sut.SelectedPanelSupportsManualOrder);
    }

    [Fact]
    public void SelectedPanelSupportsManualOrder_NoSelectedPanel_IsFalse()
    {
        var sut = BuildSut();

        Assert.False(sut.SelectedPanelSupportsManualOrder);
    }

    [Fact]
    public void SelectedPanel_Changed_RaisesPropertyChangedForBothNewSortProperties()
    {
        // Regression guard: RaiseSelectedPanelProperties must include both new properties, or the
        // settings UI silently shows stale sort-order state after switching the selected panel — the
        // plan explicitly flags this as a typical mistake in this method.
        var sut = BuildSut();
        var panel = new ChildWindowModel();

        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.SelectedPanel = panel;

        Assert.Contains(nameof(MainWindowViewModel.SelectedPanelSortMode), raisedProperties);
        Assert.Contains(nameof(MainWindowViewModel.SelectedPanelSupportsManualOrder), raisedProperties);
    }
}
