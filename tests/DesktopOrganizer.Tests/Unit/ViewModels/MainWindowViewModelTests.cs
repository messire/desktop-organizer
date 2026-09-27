using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="MainWindowViewModel.RemovePanelCommand"/> (<c>RemovePanel</c>).
/// Covers the confirmation gate added in front of the irreversible
/// <see cref="IWindowManager.RemoveChildWindow(Guid, bool)"/> call (fences-ux-parity-reviewer finding
/// #2): panel deletion moves app-managed item copies to the Recycle Bin (ADR-035), so it must never
/// happen without an explicit user confirmation — and a blocked removal must be reported to the user.
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
    private readonly Mock<IInputDialogService> _inputDialogServiceMock = new();
    private readonly Mock<ILayoutSnapshotService> _layoutSnapshotServiceMock = new();
    private readonly Mock<IPanelItemStorageService> _itemStorageMock = new();
    private readonly Mock<ILayoutEditModeService> _layoutEditModeMock = new();
    private readonly Mock<IPanelCustomizeService> _panelCustomizeServiceMock = new();
    private readonly Mock<IUpdateCheckService> _updateCheckServiceMock = new();
    private readonly Mock<IExternalLinkLauncher> _linkLauncherMock = new();
    private readonly Mock<IColorPickerService> _colorPickerMock = new();

    // A real LocalizationService (English) rather than a mock: several tests below assert actual
    // confirmation-dialog text (e.g. "moved to the Recycle Bin", plural panel counts) — the same pattern the
    // localization work packages use throughout (docs/PLANS/localization.md R-5: tests fix culture
    // explicitly rather than relying on the machine's own culture).
    private readonly ILocalizationService _localization = TestLocalization.English;

    // ── CRITICAL-1 regression: AddPanelCommand must not seed a new panel with the legacy, near-invisible
    // #1E1E1E default accent — see AccentPalette.ResolveDefaultAccent. ────────────────────────────────

    /// <summary>
    /// CRITICAL-1 regression: verifies that when the legacy near-invisible <c>#1E1E1E</c> default
    /// background color is configured, <see cref="MainWindowViewModel.AddPanelCommand"/> seeds the new
    /// panel with the neutral accent instead.
    /// </summary>
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

    /// <summary>
    /// Verifies that when a user-chosen (non-legacy) default background color is configured,
    /// <see cref="MainWindowViewModel.AddPanelCommand"/> seeds the new panel with that value verbatim.
    /// </summary>
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

    /// <summary>
    /// Verifies that when the global default panel geometry mode is <see cref="PanelGeometryMode.GridAligned"/>,
    /// <see cref="MainWindowViewModel.AddPanelCommand"/> seeds the new panel with that mode.
    /// </summary>
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

    /// <summary>
    /// Verifies that an unrecognised persisted default panel geometry mode value falls back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> when seeding a new panel.
    /// </summary>
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

    /// <summary>
    /// Verifies that when the global default icon layout policy is <see cref="IconLayoutPolicy.FixedGap"/>,
    /// <see cref="MainWindowViewModel.AddPanelCommand"/> seeds the new panel with that policy.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.DefaultPanelGeometryMode"/> forwards its value
    /// through to <see cref="GlobalDefaultsViewModel.DefaultPanelGeometryMode"/> and reads it back
    /// verbatim.
    /// </summary>
    [Fact]
    public void DefaultPanelGeometryMode_RoundTripsThroughGlobalDefaults()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultPanelGeometryMode, "LegacyFreeform");
        var sut = BuildSut();

        sut.DefaultPanelGeometryMode = PanelGeometryMode.GridAligned;

        Assert.Equal(PanelGeometryMode.GridAligned, sut.DefaultPanelGeometryMode);
        Assert.Equal(PanelGeometryMode.GridAligned, sut.GlobalDefaults.DefaultPanelGeometryMode);
    }

    /// <summary>
    /// Verifies that setting <see cref="MainWindowViewModel.DefaultPanelGeometryMode"/> re-raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> on the root <see cref="MainWindowViewModel"/>
    /// itself, not only on the nested <see cref="GlobalDefaultsViewModel"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.PanelGeometryModeOptions"/> contains every member
    /// of <see cref="PanelGeometryMode"/>.
    /// </summary>
    [Fact]
    public void PanelGeometryModeOptions_ContainsBothEnumMembers()
    {
        var sut = BuildSut();

        Assert.Equal(Enum.GetValues<PanelGeometryMode>(), sut.PanelGeometryModeOptions);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.DefaultIconLayoutPolicy"/> forwards its value
    /// through to <see cref="GlobalDefaultsViewModel.DefaultIconLayoutPolicy"/> and reads it back
    /// verbatim.
    /// </summary>
    [Fact]
    public void DefaultIconLayoutPolicy_RoundTripsThroughGlobalDefaults()
    {
        _appSettingsMock.SetupProperty(a => a.DefaultIconLayoutPolicy, "LegacyStretch");
        var sut = BuildSut();

        sut.DefaultIconLayoutPolicy = IconLayoutPolicy.FixedGap;

        Assert.Equal(IconLayoutPolicy.FixedGap, sut.DefaultIconLayoutPolicy);
        Assert.Equal(IconLayoutPolicy.FixedGap, sut.GlobalDefaults.DefaultIconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.IconLayoutPolicyOptions"/> contains every member of
    /// <see cref="IconLayoutPolicy"/>.
    /// </summary>
    [Fact]
    public void IconLayoutPolicyOptions_ContainsBothEnumMembers()
    {
        var sut = BuildSut();

        Assert.Equal(Enum.GetValues<IconLayoutPolicy>(), sut.IconLayoutPolicyOptions);
    }

    /// <summary>
    /// Verifies that when the user confirms panel removal, <see cref="MainWindowViewModel.RemovePanelCommand"/>
    /// calls <see cref="IWindowManager.RemoveChildWindow(Guid, bool)"/> for the selected panel.
    /// </summary>
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
        _confirmationMock.Verify(
            c => c.Notify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()),
            Times.Never);
    }

    /// <summary>
    /// ADR-035 fail-visibly: when <see cref="IWindowManager.RemoveChildWindow(Guid, bool)"/> reports the
    /// removal as <see cref="PanelRemovalResult.Blocked"/> (some managed items could not be moved to the
    /// Recycle Bin, so the panel was kept), the user is told — once, with a warning and the number of
    /// items that stayed.
    /// </summary>
    [Fact]
    public void RemovePanel_RemovalBlocked_NotifiesUserWithRemainingCount()
    {
        // Arrange
        var sut = BuildSut();
        var panel = new ChildWindowModel { FilePaths = ["C:\\managed\\a.txt", "C:\\managed\\b.txt"] };
        sut.SelectedPanel = panel;
        _itemStorageMock.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(true);
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Returns(true);
        _windowManagerMock
            .Setup(w => w.RemoveChildWindow(panel.Id, true))
            .Returns(PanelRemovalResult.Blocked(["C:\\managed\\a.txt", "C:\\managed\\b.txt"]));

        // Act
        sut.RemovePanelCommand.Execute(null);

        // Assert
        _confirmationMock.Verify(
            c => c.Notify(
                It.Is<string>(m => m.StartsWith("2 ", StringComparison.Ordinal) && m.Contains("Recycle Bin")),
                _localization.GetString("Recycle_Failed_Title"),
                MessageBoxImage.Warning),
            Times.Once);
    }

    /// <summary>
    /// Verifies that when the user declines the confirmation, <see cref="MainWindowViewModel.RemovePanelCommand"/>
    /// never calls <see cref="IWindowManager.RemoveChildWindow(Guid, bool)"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that executing <see cref="MainWindowViewModel.RemovePanelCommand"/> with no selected
    /// panel is a defensive no-op that never asks for confirmation or removes a window.
    /// </summary>
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

    /// <summary>
    /// Verifies that when the selected panel has app-managed files, the removal confirmation dialog
    /// uses the data-loss warning text and <see cref="MessageBoxImage.Warning"/> icon.
    /// </summary>
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
                It.Is<string>(m => m.Contains("moved to the Recycle Bin")),
                It.IsAny<string>(),
                MessageBoxImage.Warning),
            Times.Once);
    }

    /// <summary>
    /// Verifies that when the selected panel has no app-managed files, the removal confirmation dialog
    /// uses the neutral text and <see cref="MessageBoxImage.Question"/> icon, never the warning icon.
    /// </summary>
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
                It.Is<string>(m => !m.Contains("moved to the Recycle Bin")),
                It.IsAny<string>(),
                MessageBoxImage.Question),
            Times.Once);
        _confirmationMock.Verify(
            c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), MessageBoxImage.Warning),
            Times.Never);
    }

    /// <summary>
    /// Regression for CRITICAL C1 (P08 fix cycle 1, 2026-09-22): verifies that
    /// <see cref="IWindowManager.SyncAllWindowsToModels"/> runs, and its remap to the managed path is
    /// already visible, before the "has managed files" check — so the confirmation dialog shows the
    /// warning text, not the neutral one, for a panel whose stale path was remapped on sync.
    /// </summary>
    [Fact]
    public void RemovePanel_StalePathRemappedToManagedOnSync_UsesWarningTextNotNeutral()
    {
        // Regression for CRITICAL C1 (P08 fix cycle 1, 2026-09-22): before a panel's first save,
        // Model.FilePaths can still hold a stale pre-Move original path even though the live
        // ChildWindowViewModel has already remapped it to the managed copy in memory (ADR-009
        // Amendment). SyncAllWindowsToModels() must run — and its effect must already be visible —
        // before hasManagedFiles is computed, or this dialog would wrongly show the neutral
        // "nothing lost" text right before RemoveChildWindow's own SyncToModel()+recycle silently
        // sends the by-then-managed file to the Recycle Bin.
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
                It.Is<string>(m => m.Contains("moved to the Recycle Bin")),
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

    /// <summary>
    /// Verifies that when <see cref="IPageManager.RemovePage"/> succeeds,
    /// <see cref="MainWindowViewModel.RemovePageCommand"/> resyncs <see cref="MainWindowViewModel.Pages"/>
    /// from <see cref="IPageManager.Pages"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that when <see cref="IPageManager.RemovePage"/> declines the removal,
    /// <see cref="MainWindowViewModel.RemovePageCommand"/> leaves <see cref="MainWindowViewModel.Pages"/>
    /// untouched.
    /// </summary>
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

    /// <summary>
    /// Verifies that executing <see cref="MainWindowViewModel.RemovePageCommand"/> with the default page
    /// id is a defensive no-op that never calls <see cref="IPageManager.RemovePage"/>.
    /// </summary>
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

    // ── Settings-window redesign (docs/prompts/settings-window-redesign.md): SelectedSection,
    // MovePanelToPageCommand, TakeDefaultsFromSelectedPanelCommand, CustomizePanelCommand ────────────

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.SelectedSection"/> defaults to
    /// <see cref="SettingsSection.General"/> (D-1: the last-open section is not persisted).
    /// </summary>
    [Fact]
    public void SelectedSection_Default_IsGeneral()
    {
        var sut = BuildSut();

        Assert.Equal(SettingsSection.General, sut.SelectedSection);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.SelectedSection"/> keeps whatever value was set for
    /// the lifetime of the instance (it survives the settings window being Hidden/re-Shown, since
    /// <see cref="MainWindowViewModel"/> is a DI singleton never recreated in that case) and raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> exactly once per actual change.
    /// </summary>
    [Fact]
    public void SelectedSection_SetToADifferentValue_PersistsWithinInstanceAndRaisesPropertyChangedOnce()
    {
        var sut = BuildSut();
        var raisedCount = 0;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedSection)) raisedCount++;
        };

        sut.SelectedSection = SettingsSection.Backup;
        sut.SelectedSection = SettingsSection.Backup; // no-op, same value again.

        Assert.Equal(SettingsSection.Backup, sut.SelectedSection);
        Assert.Equal(1, raisedCount);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.SelectSectionCommand"/> sets
    /// <see cref="MainWindowViewModel.SelectedSection"/> — the pinned "About" sidebar entry's own path.
    /// </summary>
    [Fact]
    public void SelectSectionCommand_Execute_SetsSelectedSection()
    {
        var sut = BuildSut();

        sut.SelectSectionCommand.Execute(SettingsSection.About);

        Assert.Equal(SettingsSection.About, sut.SelectedSection);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.MovePanelToPageCommand"/> delegates to
    /// <see cref="IWindowManager.MovePanelToPage"/> for the currently selected panel.
    /// </summary>
    [Fact]
    public void MovePanelToPageCommand_Execute_MovesSelectedPanelViaWindowManager()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;

        sut.MovePanelToPageCommand.Execute("work");

        _windowManagerMock.Verify(w => w.MovePanelToPage(panel, "work"), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.MovePanelToPageCommand"/> cannot execute when no
    /// panel is selected, or when the target page is the panel's own current page.
    /// </summary>
    [Fact]
    public void MovePanelToPageCommand_CanExecute_FalseWithoutSelectionOrForSamePage()
    {
        var sut = BuildSut();

        Assert.False(sut.MovePanelToPageCommand.CanExecute("work"));

        sut.SelectedPanel = new ChildWindowModel { PageId = "work" };

        Assert.False(sut.MovePanelToPageCommand.CanExecute("work"));
        Assert.True(sut.MovePanelToPageCommand.CanExecute("default"));
    }

    // ── PanelsOnCurrentPage (docs/PLANS/settings-window-redesign.md §2.3): the Panels section's actual
    // list source, rebuilt from Panels/CurrentPage since ChildWindowModel.PageId isn't itself INPC. ──

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> contains only the panels
    /// whose <see cref="ChildWindowModel.PageId"/> matches <see cref="MainWindowViewModel.CurrentPage"/>
    /// after <see cref="MainWindowViewModel.SyncPanels"/>.
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_AfterSyncPanels_ContainsOnlyPanelsOnCurrentPage()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var panelOnDefault = new ChildWindowModel { PageId = "default" };
        var panelOnWork = new ChildWindowModel { PageId = "work" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panelOnDefault, panelOnWork]);
        var sut = BuildSut();

        sut.SyncPanels();

        Assert.Equal([panelOnDefault], sut.PanelsOnCurrentPage);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> is rebuilt when
    /// <see cref="IPageManager.PageChanged"/> fires (switching pages via the Panels section's selector
    /// or <c>Ctrl+PgUp/PgDn</c>).
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_RebuiltOnPageChanged()
    {
        var currentPage = "default";
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns(() => currentPage);
        var panelOnDefault = new ChildWindowModel { PageId = "default" };
        var panelOnWork = new ChildWindowModel { PageId = "work" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panelOnDefault, panelOnWork]);
        var sut = BuildSut();
        sut.SyncPanels();
        Assert.Equal([panelOnDefault], sut.PanelsOnCurrentPage);

        currentPage = "work";
        _pageManagerMock.Raise(p => p.PageChanged += null, "work");

        Assert.Equal([panelOnWork], sut.PanelsOnCurrentPage);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> is rebuilt when a panel is
    /// added on the current page (<see cref="IWindowManager.OnChildWindowAdded"/>).
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_RebuiltOnPanelAdded()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var sut = BuildSut();
        Assert.Empty(sut.PanelsOnCurrentPage);

        var newPanel = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Raise(w => w.OnChildWindowAdded += null, _windowManagerMock.Object, newPanel);

        Assert.Equal([newPanel], sut.PanelsOnCurrentPage);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> is rebuilt when a panel is
    /// removed (<see cref="IWindowManager.OnChildWindowRemoved"/>).
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_RebuiltOnPanelRemoved()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var panel = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panel]);
        var sut = BuildSut();
        sut.SyncPanels();
        Assert.Equal([panel], sut.PanelsOnCurrentPage);

        _windowManagerMock.Raise(w => w.OnChildWindowRemoved += null, _windowManagerMock.Object, panel);

        Assert.Empty(sut.PanelsOnCurrentPage);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> is rebuilt after
    /// <see cref="MainWindowViewModel.MovePanelToPageCommand"/> moves the selected panel away from the
    /// current page.
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_RebuiltAfterMovePanelToPageCommand()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        _windowManagerMock
            .Setup(w => w.MovePanelToPage(It.IsAny<ChildWindowModel>(), It.IsAny<string>()))
            .Callback<ChildWindowModel, string>((model, pageId) => model.PageId = pageId);
        var panel = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panel]);
        var sut = BuildSut();
        sut.SyncPanels();
        sut.SelectedPanel = panel;
        Assert.Equal([panel], sut.PanelsOnCurrentPage);

        sut.MovePanelToPageCommand.Execute("work");

        Assert.Empty(sut.PanelsOnCurrentPage);
    }

    // ── M-7 fix-cycle (docs/prompts/settings-window-redesign.md): RefreshPanelsOnCurrentPage must not
    // lose SelectedPanel on every unrelated refresh, but must still clear it when the selected panel
    // genuinely leaves the current page's list. ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that adding another panel to the current page (which rebuilds
    /// <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> via <c>RefreshPanelsOnCurrentPage</c>) does
    /// not clear an already-selected panel that is still on that page.
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_RefreshAfterAddingAnotherPanel_KeepsSelectedPanel()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var existing = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([existing]);
        var sut = BuildSut();
        sut.SyncPanels();
        sut.SelectedPanel = existing;

        var another = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Raise(w => w.OnChildWindowAdded += null, _windowManagerMock.Object, another);

        Assert.Same(existing, sut.SelectedPanel);
        Assert.Equal(2, sut.PanelsOnCurrentPage.Count);
    }

    /// <summary>
    /// Verifies that switching to a different page — which removes the selected panel from
    /// <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> — clears <see cref="MainWindowViewModel.SelectedPanel"/>.
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_PageSwitchedAwayFromSelectedPanel_ClearsSelectedPanel()
    {
        var currentPage = "default";
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns(() => currentPage);
        var panel = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panel]);
        var sut = BuildSut();
        sut.SyncPanels();
        sut.SelectedPanel = panel;

        currentPage = "work";
        _pageManagerMock.Raise(p => p.PageChanged += null, "work");

        Assert.Null(sut.SelectedPanel);
    }

    // ── FIX round 2 MINOR-4: RefreshPanelsOnCurrentPage's in-place refresh must also preserve ORDER,
    //    matching Panels (the authoritative order) — a panel moved away and back must reappear at its
    //    Panels-order position, not appended at the end of PanelsOnCurrentPage. ─────────────────────────

    /// <summary>
    /// Verifies that a panel moved off the current page and then back onto it reappears at its
    /// <see cref="MainWindowViewModel.Panels"/>-order position in
    /// <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> — not appended at the end, which is what the
    /// pre-MINOR-4 "remove, then <c>Add()</c> whatever's missing" insertion pass produced.
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_PanelMovedAwayAndBack_ReappearsAtPanelsOrderPosition()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        _windowManagerMock
            .Setup(w => w.MovePanelToPage(It.IsAny<ChildWindowModel>(), It.IsAny<string>()))
            .Callback<ChildWindowModel, string>((model, pageId) => model.PageId = pageId);
        var panelA = new ChildWindowModel { PageId = "default" };
        var panelB = new ChildWindowModel { PageId = "default" };
        var panelC = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panelA, panelB, panelC]);
        var sut = BuildSut();
        sut.SyncPanels();
        Assert.Equal([panelA, panelB, panelC], sut.PanelsOnCurrentPage);

        sut.SelectedPanel = panelB;
        sut.MovePanelToPageCommand.Execute("work");
        Assert.Equal([panelA, panelC], sut.PanelsOnCurrentPage);

        sut.SelectedPanel = panelB;
        sut.MovePanelToPageCommand.Execute("default");

        Assert.Equal([panelA, panelB, panelC], sut.PanelsOnCurrentPage);
    }

    /// <summary>
    /// Verifies that when the upstream panel order changes (e.g. a layout-snapshot restore re-populating
    /// <see cref="IWindowManager.GetAllChildWindows"/> in a different order) for the SAME set of panels,
    /// <see cref="MainWindowViewModel.PanelsOnCurrentPage"/> is repositioned via
    /// <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Move(int, int)"/> — never a
    /// <c>Remove</c> followed by a re-<c>Add</c>/<c>Insert</c> for a panel that was already present. A
    /// live <c>ListBox</c>'s two-way <c>SelectedItem</c> binding clears its selection the moment its
    /// selected row's underlying item is removed, even momentarily — so this is the concrete mechanism
    /// "never detach the row holding the selection" (MINOR-4) actually protects against.
    /// </summary>
    [Fact]
    public void PanelsOnCurrentPage_PanelsReorderedUpstream_RepositionsExistingRowsViaMoveOnly()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var panelA = new ChildWindowModel { PageId = "default" };
        var panelB = new ChildWindowModel { PageId = "default" };
        var panelC = new ChildWindowModel { PageId = "default" };
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panelA, panelB, panelC]);
        var sut = BuildSut();
        sut.SyncPanels();
        Assert.Equal([panelA, panelB, panelC], sut.PanelsOnCurrentPage);

        sut.SelectedPanel = panelB;

        var actions = new List<NotifyCollectionChangedAction>();
        sut.PanelsOnCurrentPage.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Same three panels, reversed order — simulates an upstream re-sync (e.g. layout-snapshot
        // restore) rather than any panel actually leaving/joining the current page.
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panelC, panelB, panelA]);
        sut.SyncPanels();

        Assert.Equal([panelC, panelB, panelA], sut.PanelsOnCurrentPage);
        Assert.Same(panelB, sut.SelectedPanel);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, actions);
        Assert.Contains(NotifyCollectionChangedAction.Move, actions);
    }

    // ── SelectedPage (docs/PLANS/settings-window-redesign.md §2.3): two-way facade the Panels section's
    // page-selector ComboBox binds to, since CurrentPage itself is read-only. ──────────────────────────

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.SelectedPage"/> reads through to
    /// <see cref="MainWindowViewModel.CurrentPage"/>.
    /// </summary>
    [Fact]
    public void SelectedPage_Get_ReturnsCurrentPage()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("work");
        var sut = BuildSut();

        Assert.Equal("work", sut.SelectedPage);
    }

    /// <summary>
    /// Verifies that setting <see cref="MainWindowViewModel.SelectedPage"/> runs
    /// <see cref="MainWindowViewModel.SwitchPageCommand"/> for the given page identifier.
    /// </summary>
    [Fact]
    public void SelectedPage_Set_ExecutesSwitchPageCommand()
    {
        var sut = BuildSut();

        sut.SelectedPage = "work";

        _pageManagerMock.Verify(p => p.SwitchTo("work"), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.TakeDefaultsFromSelectedPanelCommand"/> copies the
    /// selected panel's appearance into <see cref="MainWindowViewModel.GlobalDefaults"/>.
    /// </summary>
    [Fact]
    public void TakeDefaultsFromSelectedPanelCommand_Execute_CopiesSelectedPanelIntoGlobalDefaults()
    {
        // Mock<T> properties are not stateful by default (a plain setter call is a no-op unless
        // SetupProperty gives it backing storage) — required here since the assertions below read the
        // values back through the same round-trip GlobalDefaultsViewModel.ApplyFromPanel writes them with.
        _appSettingsMock.SetupProperty(a => a.DefaultBackgroundColor);
        _appSettingsMock.SetupProperty(a => a.DefaultTextColor);
        _appSettingsMock.SetupProperty(a => a.DefaultIconSize);
        var sut = BuildSut();
        var panel = new ChildWindowModel { AccentColor = "#3B82F6", TextColor = "#EEEEEE", IconSize = 64 };
        sut.SelectedPanel = panel;

        sut.TakeDefaultsFromSelectedPanelCommand.Execute(null);

        Assert.Equal("#3B82F6", sut.DefaultBackgroundColor);
        Assert.Equal("#EEEEEE", sut.DefaultTextColor);
        Assert.Equal(64, sut.DefaultIconSize);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.TakeDefaultsFromSelectedPanelCommand"/> cannot
    /// execute when no panel is selected.
    /// </summary>
    [Fact]
    public void TakeDefaultsFromSelectedPanelCommand_CanExecute_FalseWithoutSelection()
    {
        var sut = BuildSut();

        Assert.False(sut.TakeDefaultsFromSelectedPanelCommand.CanExecute(null));
    }

    // ── ResetOneTimeHintsCommand / HasOneTimeHintsToReset (fix-cycle finding 7) ─────────────────────

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.ResetOneTimeHintsCommand"/> cannot execute — and
    /// <see cref="MainWindowViewModel.HasOneTimeHintsToReset"/> is <see langword="false"/> — when there is
    /// nothing to reset (<see cref="IAppSettings.HasShownMoveHint"/> is already <see langword="false"/>).
    /// </summary>
    [Fact]
    public void ResetOneTimeHintsCommand_CanExecute_FalseWhenNothingToReset()
    {
        _appSettingsMock.SetupGet(a => a.HasShownMoveHint).Returns(false);
        var sut = BuildSut();

        Assert.False(sut.ResetOneTimeHintsCommand.CanExecute(null));
        Assert.False(sut.HasOneTimeHintsToReset);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.ResetOneTimeHintsCommand"/> can execute — and
    /// <see cref="MainWindowViewModel.HasOneTimeHintsToReset"/> is <see langword="true"/> — when
    /// <see cref="IAppSettings.HasShownMoveHint"/> is currently <see langword="true"/>.
    /// </summary>
    [Fact]
    public void ResetOneTimeHintsCommand_CanExecute_TrueWhenSomethingToReset()
    {
        _appSettingsMock.SetupGet(a => a.HasShownMoveHint).Returns(true);
        var sut = BuildSut();

        Assert.True(sut.ResetOneTimeHintsCommand.CanExecute(null));
        Assert.True(sut.HasOneTimeHintsToReset);
    }

    /// <summary>
    /// Verifies that executing <see cref="MainWindowViewModel.ResetOneTimeHintsCommand"/> clears
    /// <see cref="IAppSettings.HasShownMoveHint"/>, saves, and refreshes
    /// <see cref="MainWindowViewModel.HasOneTimeHintsToReset"/> (and therefore the command's own
    /// <c>CanExecute</c>) so the button/description update immediately, without waiting for WPF's own
    /// <c>CommandManager</c> requery.
    /// </summary>
    [Fact]
    public void ResetOneTimeHintsCommand_Execute_ClearsHintAndSavesAndRefreshesCanExecute()
    {
        _appSettingsMock.SetupProperty(a => a.HasShownMoveHint, true);
        var sut = BuildSut();
        Assert.True(sut.ResetOneTimeHintsCommand.CanExecute(null));

        sut.ResetOneTimeHintsCommand.Execute(null);

        Assert.False(_appSettingsMock.Object.HasShownMoveHint);
        _appSettingsMock.Verify(a => a.Save(), Times.Once);
        Assert.False(sut.HasOneTimeHintsToReset);
        Assert.False(sut.ResetOneTimeHintsCommand.CanExecute(null));
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.HasOneTimeHintsToReset"/> changing raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> — the General section's disabled-reason text
    /// binds directly to it.
    /// </summary>
    [Fact]
    public void ResetOneTimeHintsCommand_Execute_RaisesHasOneTimeHintsToResetPropertyChanged()
    {
        _appSettingsMock.SetupProperty(a => a.HasShownMoveHint, true);
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.ResetOneTimeHintsCommand.Execute(null);

        Assert.Contains(nameof(MainWindowViewModel.HasOneTimeHintsToReset), raisedProperties);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.RefreshTransientState"/> (FIX round 2 MINOR-6) raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> for <see cref="MainWindowViewModel.HasOneTimeHintsToReset"/>
    /// — the one live-settings-derived property this ViewModel exposes with no notification source of its
    /// own — so a caller (<c>MainWindow.xaml.cs</c>'s <c>IsVisibleChanged</c>/<c>Activated</c> handlers) can
    /// force it to re-read <see cref="IAppSettings.HasShownMoveHint"/> without needing
    /// <see cref="MainWindowViewModel.ResetOneTimeHintsCommand"/> to have actually run.
    /// </summary>
    [Fact]
    public void RefreshTransientState_RaisesHasOneTimeHintsToResetPropertyChanged()
    {
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.RefreshTransientState();

        Assert.Contains(nameof(MainWindowViewModel.HasOneTimeHintsToReset), raisedProperties);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.CustomizePanelCommand"/> calls
    /// <see cref="IPanelCustomizeService.Show"/> with the given panel when one is passed explicitly.
    /// </summary>
    [Fact]
    public void CustomizePanelCommand_ExecuteWithExplicitPanel_CallsPanelCustomizeService()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();

        sut.CustomizePanelCommand.Execute(panel);

        _panelCustomizeServiceMock.Verify(s => s.Show(panel), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.CustomizePanelCommand"/> falls back to
    /// <see cref="MainWindowViewModel.SelectedPanel"/> when executed with a <see langword="null"/>
    /// parameter (the Panels section's "Customize…" button).
    /// </summary>
    [Fact]
    public void CustomizePanelCommand_ExecuteWithNullParameter_FallsBackToSelectedPanel()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel();
        sut.SelectedPanel = panel;

        sut.CustomizePanelCommand.Execute(null);

        _panelCustomizeServiceMock.Verify(s => s.Show(panel), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.CustomizePanelCommand"/> cannot execute when no
    /// panel is selected and none is passed explicitly.
    /// </summary>
    [Fact]
    public void CustomizePanelCommand_CanExecute_FalseWithoutAnyPanel()
    {
        var sut = BuildSut();

        Assert.False(sut.CustomizePanelCommand.CanExecute(null));
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.SnapToGridEnabled"/> toggling off sets
    /// <see cref="MainWindowViewModel.GridSize"/> to <c>1</c>, and toggling back on restores the step
    /// that was in effect before (docs/prompts/settings-window-redesign.md item 3).
    /// </summary>
    [Fact]
    public void SnapToGridEnabled_ToggleOffThenOn_RestoresPreviousGridSize()
    {
        _appSettingsMock.SetupProperty(a => a.GridSize, 32);
        var sut = BuildSut();

        sut.SnapToGridEnabled = false;
        Assert.Equal(1, sut.GridSize);

        sut.SnapToGridEnabled = true;
        Assert.Equal(32, sut.GridSize);
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.SnapToGridEnabled"/> defaults to restoring
    /// <c>16</c> the first time it is turned on from an initial <see cref="MainWindowViewModel.GridSize"/>
    /// of <c>1</c> (no prior non-1 step exists yet).
    /// </summary>
    [Fact]
    public void SnapToGridEnabled_TurnedOnWithNoPriorStep_DefaultsToSixteen()
    {
        _appSettingsMock.SetupProperty(a => a.GridSize, 1);
        var sut = BuildSut();

        sut.SnapToGridEnabled = true;

        Assert.Equal(16, sut.GridSize);
    }

    // ── docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #3: Edit Layout toggle + Lock layout forwarding ──
    // Per-panel appearance/sort tests (SelectedPanelSortMode/SelectedPanelSupportsManualOrder/
    // SelectedPanelAccentColor/SelectedPanelSurfaceOpacity/SelectedPanelSurfaceStyle/SurfaceStyles) moved
    // out with the properties themselves (fix-cycle finding 8, docs/prompts/settings-window-redesign.md):
    // that forwarding now lives only in the "Customize panel…" dialog's own PanelCustomizeViewModel/
    // SelectedPanelSettingsViewModel (FIX-B), which already has its own equivalent coverage — this class
    // no longer has anything left to forward.

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.IsLayoutLocked"/> reads through to
    /// <see cref="ILayoutEditModeService.IsLayoutLocked"/>.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_Get_DelegatesToLayoutEditModeService()
    {
        _layoutEditModeMock.Setup(s => s.IsLayoutLocked).Returns(true);
        var sut = BuildSut();

        Assert.True(sut.IsLayoutLocked);
    }

    /// <summary>
    /// Verifies that setting <see cref="MainWindowViewModel.IsLayoutLocked"/> writes the value to
    /// <see cref="ILayoutEditModeService.IsLayoutLocked"/>.
    /// </summary>
    [Fact]
    public void IsLayoutLocked_Set_WritesToLayoutEditModeService()
    {
        var sut = BuildSut();

        sut.IsLayoutLocked = true;

        _layoutEditModeMock.VerifySet(s => s.IsLayoutLocked = true, Times.Once);
    }

    /// <summary>
    /// Verifies that executing <see cref="MainWindowViewModel.ToggleEditLayoutCommand"/> calls
    /// <see cref="ILayoutEditModeService.Toggle"/> with <see cref="LayoutModeChangeSource.SettingsWindow"/>.
    /// </summary>
    [Fact]
    public void ToggleEditLayoutCommand_Execute_CallsToggleWithSettingsWindowSource()
    {
        var sut = BuildSut();

        sut.ToggleEditLayoutCommand.Execute(null);

        _layoutEditModeMock.Verify(s => s.Toggle(LayoutModeChangeSource.SettingsWindow), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.IsEditing"/> changing re-raises no
    /// <see cref="MainWindowViewModel"/> notification — the settings window no longer forwards
    /// <see cref="ILayoutEditModeService.IsEditing"/> at all (fix-cycle finding 8; that toggle moved out
    /// of this window entirely per docs/prompts/settings-window-redesign.md §3).
    /// </summary>
    [Fact]
    public void LayoutEditModeService_IsEditingChanged_RaisesNothing()
    {
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        _layoutEditModeMock.Raise(s => s.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.IsEditing)));

        Assert.Empty(raisedProperties);
    }

    /// <summary>
    /// Verifies that <see cref="ILayoutEditModeService.IsLayoutLocked"/> changing re-raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> for <see cref="MainWindowViewModel.IsLayoutLocked"/>.
    /// </summary>
    [Fact]
    public void LayoutEditModeService_IsLayoutLockedChanged_RaisesIsLayoutLockedPropertyChanged()
    {
        var sut = BuildSut();
        var raisedProperties = new List<string?>();
        sut.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        _layoutEditModeMock.Raise(s => s.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.IsLayoutLocked)));

        Assert.Contains(nameof(MainWindowViewModel.IsLayoutLocked), raisedProperties);
    }

    /// <summary>
    /// Verifies that an unrelated <see cref="ILayoutEditModeService"/> property change does not raise
    /// any <see cref="MainWindowViewModel"/> notification.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.NormalModeLayoutGestures"/> parses the persisted
    /// setting string into the corresponding <see cref="NormalModeLayoutGestures"/> value.
    /// </summary>
    [Fact]
    public void NormalModeLayoutGestures_Get_ParsesPersistedSetting()
    {
        _appSettingsMock.SetupProperty(a => a.NormalModeLayoutGestures, "MoveAndResize");
        var sut = BuildSut();

        Assert.Equal(NormalModeLayoutGestures.MoveAndResize, sut.NormalModeLayoutGestures);
    }

    /// <summary>
    /// Verifies that an unrecognised or empty persisted value falls back to
    /// <see cref="NormalModeLayoutGestures.MoveOnly"/> for <see cref="MainWindowViewModel.NormalModeLayoutGestures"/>.
    /// </summary>
    [Fact]
    public void NormalModeLayoutGestures_Get_GarbageOrEmptyPersistedValue_DefaultsToMoveOnly()
    {
        _appSettingsMock.SetupProperty(a => a.NormalModeLayoutGestures, "not-a-real-value");
        var sut = BuildSut();

        Assert.Equal(NormalModeLayoutGestures.MoveOnly, sut.NormalModeLayoutGestures);
    }

    /// <summary>
    /// Verifies that setting <see cref="MainWindowViewModel.NormalModeLayoutGestures"/> persists the
    /// enum member's name and saves settings.
    /// </summary>
    [Fact]
    public void NormalModeLayoutGestures_Set_WritesEnumNameAndSaves()
    {
        _appSettingsMock.SetupProperty(a => a.NormalModeLayoutGestures, "MoveOnly");
        var sut = BuildSut();

        sut.NormalModeLayoutGestures = NormalModeLayoutGestures.None;

        Assert.Equal("None", _appSettingsMock.Object.NormalModeLayoutGestures);
        _appSettingsMock.Verify(a => a.Save(), Times.Once);
    }

    // ── P03 §2.5: "Align all panels to workspace grid" (ADR-028 «Явная команда») ──────────────────

    /// <summary>
    /// Verifies that when the user confirms, <see cref="MainWindowViewModel.AlignAllPanelsCommand"/>
    /// runs the align-to-grid command for every panel.
    /// </summary>
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

    /// <summary>
    /// Verifies that when the user declines confirmation, <see cref="MainWindowViewModel.AlignAllPanelsCommand"/>
    /// does nothing.
    /// </summary>
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

    /// <summary>
    /// ADR-026 gating: verifies that <see cref="MainWindowViewModel.AlignAllPanelsCommand"/> is disabled
    /// and refuses to run while the layout is locked, without even asking for confirmation.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.AlignAllPanelsCommand"/> can execute while the
    /// layout is not locked.
    /// </summary>
    [Fact]
    public void AlignAllPanels_NotLocked_IsEnabled()
    {
        _layoutEditModeMock.SetupGet(l => l.IsLayoutLocked).Returns(false);
        var sut = BuildSut();

        Assert.True(sut.AlignAllPanelsCommand.CanExecute(null));
    }

    // ── M-2 fix-cycle 1: "Align all" is scoped to the CURRENT page, and says so ──────────────────────

    /// <summary>
    /// Verifies that the align-all confirmation message mentions the current page and only counts
    /// panels belonging to that page, not panels on other pages.
    /// </summary>
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

    /// <summary>
    /// Verifies that the align-all confirmation message uses singular phrasing ("1 panel") when the
    /// current page has exactly one panel.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.AddPanelCommand"/> calls
    /// <see cref="IWindowManager.PlaceNewPanelOnFreeSlot"/> before <see cref="IWindowManager.CreateChildWindow"/>.
    /// </summary>
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

    // ── Language picker (docs/PLANS/localization.md §2.9 contract C-5) ─────────────────────────────

    /// <summary>
    /// Verifies that setting <see cref="MainWindowViewModel.SelectedLanguageOption"/> to an option with a
    /// different <see cref="LanguageOption.Tag"/> writes and saves <see cref="IAppSettings.Language"/>
    /// and applies the new language via <see cref="ILocalizationService.ApplyLanguage"/>.
    /// </summary>
    [Fact]
    public void SelectedLanguageOption_Set_WritesSettingsSavesAndApplies()
    {
        var sut = BuildSut();
        var ruOption = sut.LanguageOptions.First(o => o.Tag == "ru");

        sut.SelectedLanguageOption = ruOption;

        _appSettingsMock.VerifySet(a => a.Language = "ru", Times.Once);
        _appSettingsMock.Verify(a => a.Save(), Times.Once);
        Assert.Equal(CultureInfo.GetCultureInfo("ru"), _localization.CurrentCulture);
        Assert.Same(ruOption, sut.SelectedLanguageOption);
    }

    /// <summary>
    /// Verifies that re-selecting the already-current <see cref="LanguageOption"/> is a no-op: no
    /// <see cref="IAppSettings.Save"/>, no re-apply.
    /// </summary>
    [Fact]
    public void SelectedLanguageOption_SameTag_NoSave()
    {
        var sut = BuildSut();
        var current = sut.SelectedLanguageOption;

        sut.SelectedLanguageOption = current;

        _appSettingsMock.Verify(a => a.Save(), Times.Never);
    }

    /// <summary>
    /// Fix cycle 1 NIT: setting <see cref="MainWindowViewModel.SelectedLanguageOption"/> to
    /// <see langword="null"/> — which <c>ComboBox.SelectedItem</c> bindings can do transiently — is a
    /// no-op rather than throwing <see cref="ArgumentNullException"/> out of the binding engine.
    /// </summary>
    [Fact]
    public void SelectedLanguageOption_SetNull_NoOp()
    {
        var sut = BuildSut();
        var current = sut.SelectedLanguageOption;

        sut.SelectedLanguageOption = null;

        Assert.Same(current, sut.SelectedLanguageOption);
        _appSettingsMock.Verify(a => a.Save(), Times.Never);
    }

    /// <summary>
    /// Fix cycle 1 NIT: the constructor's initial <see cref="MainWindowViewModel.SelectedLanguageOption"/>
    /// match against <see cref="ILocalizationService.LanguagePreference"/> is case-insensitive — a
    /// defense-in-depth companion to <c>LocalizationService</c>'s own casing normalization (which already
    /// makes this scenario unreachable through the REAL service; this test exercises the match directly
    /// via a mock returning an unnormalized <c>"RU"</c>, so a future <see cref="ILocalizationService"/>
    /// implementation that skipped normalization still gets the correct initial selection here).
    /// </summary>
    [Fact]
    public void SelectedLanguageOption_InitialSelection_MatchesLanguagePreferenceCaseInsensitively()
    {
        var localizationMock = new Mock<ILocalizationService>();
        localizationMock.SetupGet(l => l.LanguagePreference).Returns("RU");

        var sut = BuildSut(localization: localizationMock.Object);

        Assert.Equal("ru", sut.SelectedLanguageOption.Tag);
    }

    /// <summary>
    /// Fix cycle 1 MINOR-2: verifies that the "Follow Windows" <see cref="LanguageOption"/>'s
    /// <see cref="LanguageOption.DisplayName"/> names, in its own self-name, whichever language
    /// <paramref name="systemCultureTag"/> resolves to — driven by the explicit
    /// <see cref="CultureInfo"/> <see cref="BuildSut"/> passes to <c>MainWindowViewModel</c>'s
    /// constructor, not by <see cref="DesktopOrganizer.Localization.SystemCulture.OriginalUiCulture"/>
    /// (the actual test-runner machine's own OS language). This is what makes both the "ru" and "en"
    /// branches deterministically verifiable on any machine — the previous version of this test only
    /// ever exercised whichever branch happened to match the author's own machine.
    /// </summary>
    /// <param name="systemCultureTag">The <see cref="CultureInfo"/> tag <see cref="BuildSut"/> wires up as the "system" UI culture.</param>
    /// <param name="expectedNativeName">The expected embedded native language name for <paramref name="systemCultureTag"/>.</param>
    [Theory]
    [InlineData("en", "English")]
    [InlineData("ru", "Русский")]
    public void LanguageOptions_FollowWindowsDisplayName_NamesTheGivenSystemUiCultureNatively(
        string systemCultureTag, string expectedNativeName)
    {
        var sut = BuildSut(CultureInfo.GetCultureInfo(systemCultureTag));
        var followWindows = sut.LanguageOptions.First(o => o.Tag == string.Empty);

        var expected = _localization.Format("Language_FollowWindows_Format", expectedNativeName);

        Assert.Equal(expected, followWindows.DisplayName);
    }

    /// <summary>
    /// Verifies that the align-all confirmation message uses the Russian plural form when the UI
    /// language is Russian (docs/PLANS/localization.md L-8).
    /// </summary>
    [Fact]
    public void AlignAllConfirm_UsesRussianPlural()
    {
        _localization.ApplyLanguage("ru");
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("page-1");
        string? capturedMessage = null;
        _confirmationMock
            .Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()))
            .Callback<string, string, MessageBoxImage>((message, _, _) => capturedMessage = message)
            .Returns(true);
        var sut = BuildSut();
        sut.Panels.Add(new ChildWindowModel { PageId = "page-1" });
        sut.Panels.Add(new ChildWindowModel { PageId = "page-1" });

        sut.AlignAllPanelsCommand.Execute(null);

        Assert.NotNull(capturedMessage);
        Assert.Contains("2 панели", capturedMessage); // ru "Few" category for n=2.
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel.DefaultColors"/> is exactly
    /// <see cref="MainWindowViewModel.GlobalDefaults"/>' own <see cref="GlobalDefaultsViewModel.DefaultColors"/>
    /// instance (docs/PLANS/color-presets-and-size-step.md §3.7) — a plain forwarding property, not a
    /// second, independently-constructed <c>PanelColorEditorViewModel</c>.
    /// </summary>
    [Fact]
    public void DefaultColors_IsTheSameInstanceAsGlobalDefaultsDefaultColors()
    {
        var sut = BuildSut();

        Assert.Same(sut.GlobalDefaults.DefaultColors, sut.DefaultColors);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <param name="systemUiCulture">
    /// The <c>systemUiCulture</c> wired into <c>MainWindowViewModel</c>'s constructor (fix cycle 1
    /// MINOR-2) — defaults to English so every existing call site keeps its prior behavior unchanged;
    /// <see cref="LanguageOptions_FollowWindowsDisplayName_NamesTheGivenSystemUiCultureNatively"/> passes
    /// an explicit culture to exercise the "ru" branch deterministically.
    /// </param>
    /// <param name="localization">
    /// The <c>localization</c> wired into <c>MainWindowViewModel</c>'s own constructor — defaults to the
    /// shared real <see cref="_localization"/> field every other test relies on; <see cref="SelectedLanguageOption_InitialSelection_MatchesLanguagePreferenceCaseInsensitively"/>
    /// substitutes a mock to exercise a preference whose casing a real <see cref="LocalizationService"/>
    /// would already have normalized away (fix cycle 1 NIT). <see cref="LayoutSnapshotsViewModel"/> always
    /// gets the real <see cref="_localization"/>, regardless — it is unrelated to what that test verifies.
    /// </param>
    private MainWindowViewModel BuildSut(CultureInfo? systemUiCulture = null, ILocalizationService? localization = null)
    {
        _pageManagerMock.Setup(p => p.Pages).Returns([]);
        _layoutSnapshotServiceMock.Setup(s => s.Snapshots).Returns([]);

        // MainWindowViewModel's constructor subscribes to Model.PropertyChanged (finalization MAJOR
        // M-3: SystemIconsVirtualDesktopFeatureIneffective forwarding) — an un-stubbed Mock<T> property
        // of reference type returns null, which would NRE there.
        _systemIconsPanelManagerMock.Setup(m => m.Model).Returns(new SystemIconsPanelModel());

        // WP-C cross-package note: LayoutSnapshotsViewModel's constructor gained IInputDialogService
        // (D-10 contract) — this call site is a mechanical wiring fixup only, same category as
        // DiConfig.cs's one-line IInputDialogService registration; no MainWindowViewModel behavior here
        // changed.
        var layoutSnapshots = new LayoutSnapshotsViewModel(
            _layoutSnapshotServiceMock.Object,
            _confirmationMock.Object,
            _inputDialogServiceMock.Object,
            _localization);

        // Package G2 wiring fixup only (release-hardening-1.0.md §G2): MainWindowViewModel's constructor
        // gained an AboutViewModel parameter, same category as WP-C's own IInputDialogService fixup above
        // — no MainWindowViewModel behavior under test here changed.
        var about = new AboutViewModel(_updateCheckServiceMock.Object, _linkLauncherMock.Object, localization ?? _localization);

        return new MainWindowViewModel(
            _windowManagerMock.Object,
            _appSettingsMock.Object,
            _autostartMock.Object,
            _pageManagerMock.Object,
            layoutSnapshots,
            _systemIconsPanelManagerMock.Object,
            _confirmationMock.Object,
            _itemStorageMock.Object,
            _layoutEditModeMock.Object,
            _panelCustomizeServiceMock.Object,
            localization ?? _localization,
            systemUiCulture ?? CultureInfo.GetCultureInfo("en"),
            about,
            _colorPickerMock.Object);
    }
}
