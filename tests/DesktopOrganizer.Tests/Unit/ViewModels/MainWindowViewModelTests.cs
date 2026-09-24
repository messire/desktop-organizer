using System.ComponentModel;
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
    private readonly Mock<ILayoutEditModeService> _layoutEditModeMock = new();

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
            _desktopIconsMock.Object,
            _layoutEditModeMock.Object);
    }

    // ── CRITICAL-1 regression: AddPanelCommand must not seed a new panel with the legacy, near-invisible
    // #1E1E1E default accent — see AccentPalette.ResolveDefaultAccent. ────────────────────────────────
    [Fact]
    public void AddPanel_DefaultBackgroundColorIsLegacyNearBlack_SeedsNeutralAccentInstead()
    {
        // Arrange
        _appSettingsMock.SetupProperty(a => a.DefaultBackgroundColor, "#1E1E1E");
        var sut = BuildSut();
        ChildWindowModel? created = null;
        _windowManagerMock
            .Setup(w => w.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        // Act
        sut.AddPanelCommand.Execute(null);

        // Assert
        Assert.NotNull(created);
        Assert.Equal(DesktopOrganizer.UI.Theming.AccentPalette.Neutral, created!.AccentColor);
    }

    [Fact]
    public void AddPanel_DefaultBackgroundColorIsUserChosen_SeedsThatValueVerbatim()
    {
        // Arrange
        _appSettingsMock.SetupProperty(a => a.DefaultBackgroundColor, "#112233");
        var sut = BuildSut();
        ChildWindowModel? created = null;
        _windowManagerMock
            .Setup(w => w.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        // Act
        sut.AddPanelCommand.Execute(null);

        // Assert
        Assert.NotNull(created);
        Assert.Equal("#112233", created!.AccentColor);
    }

    // ── ADR-028/ADR-029 (docs/PLANS/workspace-grid-migration.md): AddPanel seeds new panels from the
    // global DefaultPanelGeometryMode/DefaultIconLayoutPolicy settings — existing panels are never
    // affected by these settings. ───────────────────────────────────────────────────────────────────
    [Fact]
    public void AddPanel_DefaultPanelGeometryModeIsGridAligned_SeedsGridAligned()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultPanelGeometryMode, "GridAligned");
        var sut = BuildSut();
        ChildWindowModel? created = null;
        _windowManagerMock
            .Setup(w => w.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        sut.AddPanelCommand.Execute(null);

        Assert.NotNull(created);
        Assert.Equal(PanelGeometryMode.GridAligned, created!.GeometryMode);
    }

    [Fact]
    public void AddPanel_DefaultPanelGeometryModeUnrecognisedValue_FallsBackToLegacyFreeform()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultPanelGeometryMode, "not-a-real-value");
        var sut = BuildSut();
        ChildWindowModel? created = null;
        _windowManagerMock
            .Setup(w => w.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        sut.AddPanelCommand.Execute(null);

        Assert.NotNull(created);
        Assert.Equal(PanelGeometryMode.LegacyFreeform, created!.GeometryMode);
    }

    [Fact]
    public void AddPanel_DefaultIconLayoutPolicyIsFixedGap_SeedsFixedGap()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultIconLayoutPolicy, "FixedGap");
        var sut = BuildSut();
        ChildWindowModel? created = null;
        _windowManagerMock
            .Setup(w => w.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(m => created = m);

        sut.AddPanelCommand.Execute(null);

        Assert.NotNull(created);
        Assert.Equal(IconLayoutPolicy.FixedGap, created!.IconLayoutPolicy);
    }

    // ── P06 (docs/fix-prompts/06-fit-content.md): MainWindow.xaml's "Defaults for new panels" section
    // needed DefaultPanelGeometryMode/DefaultIconLayoutPolicy forwarded from GlobalDefaultsViewModel the
    // same way DefaultIconSize/DefaultBackgroundColor already were — these two properties existed on
    // GlobalDefaultsViewModel/IAppSettings since docs/PLANS/workspace-grid-migration.md §10 but had no
    // MainWindowViewModel-level forwarding (and therefore no settings-window control) until this pass. ──

    [Fact]
    public void DefaultPanelGeometryMode_RoundTripsThroughGlobalDefaults()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultPanelGeometryMode, "LegacyFreeform");
        var sut = BuildSut();

        sut.DefaultPanelGeometryMode = PanelGeometryMode.GridAligned;

        Assert.Equal(PanelGeometryMode.GridAligned, sut.DefaultPanelGeometryMode);
        Assert.Equal(PanelGeometryMode.GridAligned, sut.GlobalDefaults.DefaultPanelGeometryMode);
    }

    [Fact]
    public void DefaultPanelGeometryMode_Set_RaisesPropertyChanged_OnRootViewModel()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultPanelGeometryMode, "LegacyFreeform");
        var sut = BuildSut();
        var raised = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DefaultPanelGeometryMode)) raised = true;
        };

        sut.DefaultPanelGeometryMode = PanelGeometryMode.GridAligned;

        Assert.True(raised, "MainWindowViewModel must re-raise GlobalDefaultsViewModel's PropertyChanged verbatim (see its class remarks).");
    }

    [Fact]
    public void PanelGeometryModeOptions_ContainsBothEnumMembers()
    {
        var sut = BuildSut();

        Assert.Equal(Enum.GetValues<PanelGeometryMode>(), sut.PanelGeometryModeOptions);
    }

    [Fact]
    public void DefaultIconLayoutPolicy_RoundTripsThroughGlobalDefaults()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultIconLayoutPolicy, "LegacyStretch");
        var sut = BuildSut();

        sut.DefaultIconLayoutPolicy = IconLayoutPolicy.FixedGap;

        Assert.Equal(IconLayoutPolicy.FixedGap, sut.DefaultIconLayoutPolicy);
        Assert.Equal(IconLayoutPolicy.FixedGap, sut.GlobalDefaults.DefaultIconLayoutPolicy);
    }

    [Fact]
    public void IconLayoutPolicyOptions_ContainsBothEnumMembers()
    {
        var sut = BuildSut();

        Assert.Equal(Enum.GetValues<IconLayoutPolicy>(), sut.IconLayoutPolicyOptions);
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
    public void RemovePanel_StalePathRemappedToManagedOnSync_UsesWarningTextNotNeutral()
    {
        // Regression for CRITICAL C1 (P08 fix cycle 1, 2026-09-22): before a panel's first save,
        // Model.FilePaths can still hold a stale pre-Move original path even though the live
        // ChildWindowViewModel has already remapped it to the managed copy in memory (ADR-009
        // Amendment). SyncAllWindowsToModels() must run — and its effect must already be visible —
        // before hasManagedFiles is computed, or this dialog would wrongly show the neutral
        // "nothing lost" text right before RemoveChildWindow's own SyncToModel()+Release() silently
        // deletes the by-then-managed file past the Recycle Bin.
        var sut = BuildSut();
        var panel = new ChildWindowModel { FilePaths = ["C:\\stale\\original.lnk"] };
        sut.SelectedPanel = panel;

        _itemStorageMock.Setup(s => s.IsManaged("C:\\stale\\original.lnk")).Returns(false);
        _itemStorageMock.Setup(s => s.IsManaged("C:\\managed\\original.lnk")).Returns(true);
        _windowManagerMock
            .Setup(w => w.SyncAllWindowsToModels())
            .Callback(() => panel.FilePaths = ["C:\\managed\\original.lnk"]);

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
        _windowManagerMock.Verify(w => w.SyncAllWindowsToModels(), Times.Once);
    }

    // ── CRITICAL-1 (P08 fix cycle 2, 2026-09-22): RemovePage must consult IPageManager.RemovePage's own
    // bool outcome instead of unconditionally removing pageId from Pages regardless of it. Before this
    // fix, PageManagerService.RemovePage's own confirm gate (ADR-010 Decision 2 / ADR-009 Amendment 2)
    // could decline the removal (user hits Cancel) while this ViewModel's Pages.Remove(pageId) still ran
    // unconditionally right after — the page (and every panel on it, still fully alive) vanished from
    // the UI's page switcher until the app was restarted, even though nothing was actually removed. ────

    [Fact]
    public void RemovePage_PageManagerRemoves_ResyncsPagesFromPageManager()
    {
        // Arrange — BuildSut() itself seeds _pageManagerMock.Pages with [] (consumed once by the
        // constructor's own SyncPages() call), so "work" is seeded afterwards via the ViewModel's own
        // public SyncPages(), the same API RemovePage itself must call on success.
        var sut = BuildSut();
        _pageManagerMock.Setup(p => p.Pages).Returns(["default", "work"]);
        sut.SyncPages();
        Assert.Contains("work", sut.Pages);

        _pageManagerMock.Setup(p => p.RemovePage("work")).Returns(true);
        _pageManagerMock.Setup(p => p.Pages).Returns(["default"]);

        // Act
        sut.RemovePageCommand.Execute("work");

        // Assert
        _pageManagerMock.Verify(p => p.RemovePage("work"), Times.Once);
        Assert.DoesNotContain("work", sut.Pages);
        Assert.Contains("default", sut.Pages);
    }

    [Fact]
    public void RemovePage_PageManagerDeclines_LeavesPagesUntouched()
    {
        // Arrange
        var sut = BuildSut();
        _pageManagerMock.Setup(p => p.Pages).Returns(["default", "work"]);
        sut.SyncPages();

        _pageManagerMock.Setup(p => p.RemovePage("work")).Returns(false);

        // Act
        sut.RemovePageCommand.Execute("work");

        // Assert
        _pageManagerMock.Verify(p => p.RemovePage("work"), Times.Once);
        Assert.Contains("work", sut.Pages);
        Assert.Equal(2, sut.Pages.Count);
    }

    [Fact]
    public void RemovePage_DefaultPageId_NeverCallsPageManager()
    {
        // Arrange — CanRemovePage already guards the command, but RemovePage itself must also no-op
        // defensively if invoked directly with "default" (mirrors RemovePanel_NoSelectedPanel's own
        // defensive-guard test above).
        var sut = BuildSut();

        // Act
        sut.RemovePageCommand.Execute("default");

        // Assert
        _pageManagerMock.Verify(p => p.RemovePage(It.IsAny<string>()), Times.Never);
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

    // ── ADR-025 D-10 (commit C7): SelectedPanelAccentColor/SurfaceOpacity/SurfaceStyle facades ─────

    [Fact]
    public void SelectedPanelAccentColor_Get_DelegatesToSelectedPanelSettings()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { AccentColor = "#3B82F6" };
        sut.SelectedPanel = panel;

        Assert.Equal("#3B82F6", sut.SelectedPanelAccentColor);
    }

    [Fact]
    public void SelectedPanelAccentColor_Set_WritesToModelAndSavesWindowsState()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        sut.SelectedPanelAccentColor = "#A855F7";

        Assert.Equal("#A855F7", panel.AccentColor);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    [Fact]
    public void SelectedPanelSurfaceOpacity_Get_DelegatesToSelectedPanelSettings()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { SurfaceOpacity = 0.42 };
        sut.SelectedPanel = panel;

        Assert.Equal(0.42, sut.SelectedPanelSurfaceOpacity);
    }

    [Fact]
    public void SelectedPanelSurfaceOpacity_Set_WritesToModelAndSavesWindowsState()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        sut.SelectedPanelSurfaceOpacity = 0.6;

        Assert.Equal(0.6, panel.SurfaceOpacity);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    [Fact]
    public void SelectedPanelSurfaceStyle_Get_DelegatesToSelectedPanelSettings()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { SurfaceStyle = PanelSurfaceStyle.Glass };
        sut.SelectedPanel = panel;

        Assert.Equal(PanelSurfaceStyle.Glass, sut.SelectedPanelSurfaceStyle);
    }

    [Fact]
    public void SelectedPanelSurfaceStyle_Set_WritesToModelAndSavesWindowsState()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        sut.SelectedPanelSurfaceStyle = PanelSurfaceStyle.Solid;

        Assert.Equal(PanelSurfaceStyle.Solid, panel.SurfaceStyle);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    [Fact]
    public void SurfaceStyles_ContainsAllPanelSurfaceStyleValues()
    {
        var sut = BuildSut();

        Assert.Equal(Enum.GetValues<PanelSurfaceStyle>(), sut.SurfaceStyles);
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

    // ── docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #3: Edit Layout toggle + Lock layout forwarding ──

    [Fact]
    public void IsEditingLayout_Get_DelegatesToLayoutEditModeService()
    {
        _layoutEditModeMock.Setup(s => s.IsEditing).Returns(true);
        var sut = BuildSut();

        Assert.True(sut.IsEditingLayout);
    }

    [Fact]
    public void IsEditingLayout_SetTrue_CallsTryEnterEditLayoutWithSettingsWindowSource()
    {
        var sut = BuildSut();

        sut.IsEditingLayout = true;

        _layoutEditModeMock.Verify(s => s.TryEnterEditLayout(LayoutModeChangeSource.SettingsWindow), Times.Once);
        _layoutEditModeMock.Verify(s => s.ExitEditLayout(It.IsAny<LayoutModeChangeSource>()), Times.Never);
    }

    [Fact]
    public void IsEditingLayout_SetFalse_CallsExitEditLayoutWithSettingsWindowSource()
    {
        var sut = BuildSut();

        sut.IsEditingLayout = false;

        _layoutEditModeMock.Verify(s => s.ExitEditLayout(LayoutModeChangeSource.SettingsWindow), Times.Once);
        _layoutEditModeMock.Verify(s => s.TryEnterEditLayout(It.IsAny<LayoutModeChangeSource>()), Times.Never);
    }

    [Fact]
    public void IsLayoutLocked_Get_DelegatesToLayoutEditModeService()
    {
        _layoutEditModeMock.Setup(s => s.IsLayoutLocked).Returns(true);
        var sut = BuildSut();

        Assert.True(sut.IsLayoutLocked);
    }

    [Fact]
    public void IsLayoutLocked_Set_WritesToLayoutEditModeService()
    {
        var sut = BuildSut();

        sut.IsLayoutLocked = true;

        _layoutEditModeMock.VerifySet(s => s.IsLayoutLocked = true, Times.Once);
    }

    [Fact]
    public void CanEnterEditLayout_DelegatesToLayoutEditModeService()
    {
        _layoutEditModeMock.Setup(s => s.CanEnterEditLayout).Returns(false);
        var sut = BuildSut();

        Assert.False(sut.CanEnterEditLayout);
    }

    [Fact]
    public void ToggleEditLayoutCommand_Execute_CallsToggleWithSettingsWindowSource()
    {
        var sut = BuildSut();

        sut.ToggleEditLayoutCommand.Execute(null);

        _layoutEditModeMock.Verify(s => s.Toggle(LayoutModeChangeSource.SettingsWindow), Times.Once);
    }

    [Fact]
    public void LayoutEditModeService_IsEditingChanged_RaisesIsEditingLayoutPropertyChanged()
    {
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        _layoutEditModeMock.Raise(s => s.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.IsEditing)));

        Assert.Contains(nameof(MainWindowViewModel.IsEditingLayout), raisedProperties);
    }

    [Fact]
    public void LayoutEditModeService_IsLayoutLockedChanged_RaisesIsLayoutLockedAndCanEnterEditLayoutPropertyChanged()
    {
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        _layoutEditModeMock.Raise(s => s.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.IsLayoutLocked)));

        Assert.Contains(nameof(MainWindowViewModel.IsLayoutLocked), raisedProperties);
        Assert.Contains(nameof(MainWindowViewModel.CanEnterEditLayout), raisedProperties);
    }

    [Fact]
    public void LayoutEditModeService_UnrelatedPropertyChanged_DoesNotRaiseAnything()
    {
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        _layoutEditModeMock.Raise(s => s.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.Mode)));

        Assert.Empty(raisedProperties);
    }

    // ── docs/PLANS/phase-6-edit-layout-ux.md §1 D-2: NormalModeLayoutGestures forwarding ────────────

    [Fact]
    public void NormalModeLayoutGestures_Get_ParsesPersistedSetting()
    {
        _appSettingsMock.SetupProperty(a => a.NormalModeLayoutGestures, "MoveAndResize");
        var sut = BuildSut();

        Assert.Equal(NormalModeLayoutGestures.MoveAndResize, sut.NormalModeLayoutGestures);
    }

    [Fact]
    public void NormalModeLayoutGestures_Get_GarbageOrEmptyPersistedValue_DefaultsToMoveOnly()
    {
        _appSettingsMock.SetupProperty(a => a.NormalModeLayoutGestures, "not-a-real-value");
        var sut = BuildSut();

        Assert.Equal(NormalModeLayoutGestures.MoveOnly, sut.NormalModeLayoutGestures);
    }

    [Fact]
    public void NormalModeLayoutGestures_Set_WritesEnumNameAndSaves()
    {
        _appSettingsMock.SetupProperty(a => a.NormalModeLayoutGestures, "MoveOnly");
        var sut = BuildSut();

        sut.NormalModeLayoutGestures = NormalModeLayoutGestures.None;

        Assert.Equal("None", _appSettingsMock.Object.NormalModeLayoutGestures);
        _appSettingsMock.Verify(a => a.Save(), Times.Once);
    }

    [Fact]
    public void NormalModeLayoutGesturesOptions_ContainsAllEnumValues()
    {
        var sut = BuildSut();

        Assert.Equal(Enum.GetValues<NormalModeLayoutGestures>(), sut.NormalModeLayoutGesturesOptions);
    }

    // ── P03 §2.5: "Align all panels to workspace grid" (ADR-028 «Явная команда») ──────────────────

    [Fact]
    public void AlignAllPanels_Confirmed_RunsTheCommandForEveryPanel()
    {
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);
        var sut = BuildSut();

        sut.AlignAllPanelsCommand.Execute(null);

        _windowManagerMock.Verify(w => w.AlignPanelsToWorkspaceGrid(null), Times.Once);
    }

    [Fact]
    public void AlignAllPanels_Declined_DoesNothing()
    {
        // A bulk re-layout of every panel with no single-step undo must never happen unconfirmed.
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(false);
        var sut = BuildSut();

        sut.AlignAllPanelsCommand.Execute(null);

        _windowManagerMock.Verify(w => w.AlignPanelsToWorkspaceGrid(It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public void AlignAllPanels_LayoutLocked_IsDisabledAndRefusesToRun()
    {
        // ADR-026 gating: an explicit command is blocked by the layout lock (and only by it).
        _layoutEditModeMock.SetupGet(l => l.IsLayoutLocked).Returns(true);
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);
        var sut = BuildSut();

        Assert.False(sut.AlignAllPanelsCommand.CanExecute(null));

        sut.AlignAllPanelsCommand.Execute(null);

        _windowManagerMock.Verify(w => w.AlignPanelsToWorkspaceGrid(It.IsAny<Guid?>()), Times.Never);
        _confirmationMock.Verify(
            c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()),
            Times.Never);
    }

    [Fact]
    public void AlignAllPanels_NotLocked_IsEnabled()
    {
        _layoutEditModeMock.SetupGet(l => l.IsLayoutLocked).Returns(false);
        var sut = BuildSut();

        Assert.True(sut.AlignAllPanelsCommand.CanExecute(null));
    }

    // ── M-2 fix-cycle 1: "Align all" is scoped to the CURRENT page, and says so ──────────────────────

    [Fact]
    public void AlignAllPanels_ConfirmationMessage_MentionsCurrentPageAndItsOwnPanelCount()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("page-2");
        string? capturedMessage = null;
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Callback<string, string, MessageBoxImage>((message, _, _) => capturedMessage = message)
            .Returns(true);
        var sut = BuildSut();
        sut.Panels.Add(new ChildWindowModel { PageId = "page-2" });
        sut.Panels.Add(new ChildWindowModel { PageId = "page-2" });
        sut.Panels.Add(new ChildWindowModel { PageId = "page-1" }); // a different page -- must not be counted

        sut.AlignAllPanelsCommand.Execute(null);

        Assert.NotNull(capturedMessage);
        Assert.Contains("current page", capturedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 panels", capturedMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlignAllPanels_ConfirmationMessage_SingularForExactlyOnePanel()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("page-1");
        string? capturedMessage = null;
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Callback<string, string, MessageBoxImage>((message, _, _) => capturedMessage = message)
            .Returns(true);
        var sut = BuildSut();
        sut.Panels.Add(new ChildWindowModel { PageId = "page-1" });

        sut.AlignAllPanelsCommand.Execute(null);

        Assert.NotNull(capturedMessage);
        Assert.Contains("1 panel)", capturedMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── P03-L3 (baseline M8): a new panel asks for a free slot before it is created ────────────────

    [Fact]
    public void AddPanel_AsksForAFreeSlotBeforeCreatingTheWindow()
    {
        var sut = BuildSut();
        var order = new List<string>();
        _windowManagerMock
            .Setup(w => w.PlaceNewPanelOnFreeSlot(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(_ => order.Add("place"));
        _windowManagerMock
            .Setup(w => w.CreateChildWindow(It.IsAny<ChildWindowModel>()))
            .Callback<ChildWindowModel>(_ => order.Add("create"));

        sut.AddPanelCommand.Execute(null);

        Assert.Equal(["place", "create"], order);
    }
}
