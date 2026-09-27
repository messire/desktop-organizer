using System.ComponentModel;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="PanelCustomizeViewModel"/> (docs/prompts/settings-window-redesign.md D-3,
/// WP-B, and its FIX-B fix-cycle): live forwarding to its <see cref="PanelCustomizeViewModel.Target"/>,
/// retargeting to a different panel, reacting to changes made to the target from elsewhere (M-2), Layout
/// gating while the layout is locked (M-1), Folder Portal Browse/Clear (D-4, via the extracted
/// <see cref="IFolderPickerService"/> seam), and the Content/Layout section gating rules (Sort order
/// disabled for a portal, Auto-size to content enabled only in Grid view).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCustomizeViewModelTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<ILayoutEditModeService> _layoutEditModeMock = new();
    private readonly Mock<IFolderPickerService> _folderPickerMock = new();
    private readonly Mock<IColorPickerService> _colorPickerMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();

    // docs/PLANS/localization.md R-5: a real LocalizationService fixed to English, independent of the
    // test-runner machine's own culture — every pre-existing assertion below expects English text.
    private readonly ILocalizationService _localization = TestLocalization.English;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCustomizeViewModelTests"/> class, unlocking the
    /// layout by default (see the class remarks).
    /// </summary>
    public PanelCustomizeViewModelTests()
    {
        // Unlocked by default — most tests aren't exercising the M-1 gating behavior and would otherwise
        // have every Layout-section setter silently no-op against Moq's default `false` for an
        // unconfigured bool property.
        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(true);
    }

    // ── Target / retargeting ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.WindowTitle"/> reflects the current
    /// <see cref="PanelCustomizeViewModel.Target"/>'s title.
    /// </summary>
    [Fact]
    public void WindowTitle_ReflectsTargetTitle()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { Title = "Downloads" };

        sut.Target = model;

        Assert.Equal("Customize \u201cDownloads\u201d", sut.WindowTitle);
    }

    /// <summary>
    /// Verifies that editing <see cref="PanelCustomizeViewModel.Appearance"/>'s title updates
    /// <see cref="PanelCustomizeViewModel.WindowTitle"/> and raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// for it.
    /// </summary>
    [Fact]
    public void WindowTitle_UpdatesWhenAppearanceTitleChanges()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { Title = "Old" };

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        sut.Appearance.SelectedPanelTitle = "New";

        Assert.Equal("Customize \u201cNew\u201d", sut.WindowTitle);
        Assert.Contains(nameof(PanelCustomizeViewModel.WindowTitle), raised);
    }

    /// <summary>
    /// Verifies that reassigning <see cref="PanelCustomizeViewModel.Target"/> to a different panel
    /// re-targets <see cref="PanelCustomizeViewModel.Appearance"/> to that panel, and that further
    /// edits through <see cref="PanelCustomizeViewModel.Appearance"/> no longer touch the previous
    /// (now unreferenced) target.
    /// </summary>
    [Fact]
    public void Target_Reassigned_RetargetsAppearance_OldTargetNoLongerAffected()
    {
        var sut = BuildSut();
        var oldModel = new ChildWindowModel { Title = "Old" };
        var newModel = new ChildWindowModel { Title = "New" };

        sut.Target = oldModel;
        sut.Target = newModel;

        sut.Appearance.SelectedPanelTitle = "Edited";

        Assert.Equal("Edited", newModel.Title);
        Assert.Equal("Old", oldModel.Title); // untouched — Appearance no longer points at it
        Assert.Equal("Customize \u201cEdited\u201d", sut.WindowTitle);
    }

    /// <summary>
    /// Verifies that mutating a previous target after retargeting away from it has no effect on the
    /// dialog's current state (defends against a stale reference surviving the reassignment).
    /// </summary>
    [Fact]
    public void Target_Reassigned_MutatingOldTargetAfterwardsDoesNotAffectDialog()
    {
        var sut = BuildSut();
        var oldModel = new ChildWindowModel { Title = "Old" };
        var newModel = new ChildWindowModel { Title = "New" };

        sut.Target = oldModel;
        sut.Target = newModel;

        oldModel.Title = "Mutated After Retarget";

        Assert.Equal("Customize \u201cNew\u201d", sut.WindowTitle);
        Assert.Equal("New", sut.Appearance.SelectedPanelTitle);
    }

    /// <summary>
    /// Verifies that assigning the same instance again to <see cref="PanelCustomizeViewModel.Target"/>
    /// is a no-op that does not re-raise property-changed notifications.
    /// </summary>
    [Fact]
    public void Target_ReassignedSameInstance_IsNoOp()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { Title = "Same" };
        sut.Target = model;

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        sut.Target = model;

        Assert.False(raised);
    }

    /// <summary>
    /// Verifies that after retargeting away from a panel, further changes made directly to that OLD
    /// panel's model (e.g. from its own context menu) no longer raise notifications on this dialog
    /// (FIX-B fix-cycle MAJOR finding M-2: the old subscription must be torn down on retarget).
    /// </summary>
    [Fact]
    public void Target_Reassigned_UnsubscribesFromPreviousTarget()
    {
        var sut = BuildSut();
        var oldModel = new ChildWindowModel();
        var newModel = new ChildWindowModel();
        sut.Target = oldModel;
        sut.Target = newModel;

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        oldModel.ContentFilter = PanelContentFilter.HideShortcuts;

        Assert.False(raised);
    }

    // ── M-2: reacting to changes made elsewhere (CM, rename, snapshot restore) ─────────────────────

    /// <summary>
    /// Verifies that changing <see cref="ChildWindowModel.Title"/> directly on the target (as the
    /// panel's own inline rename would) updates <see cref="PanelCustomizeViewModel.WindowTitle"/> —
    /// the dialog must not go stale while open (FIX-B fix-cycle MAJOR finding M-2).
    /// </summary>
    [Fact]
    public void ModelTitleChangedDirectly_UpdatesWindowTitle()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { Title = "Old" };
        sut.Target = model;

        model.Title = "Renamed From Panel";

        Assert.Equal("Customize \u201cRenamed From Panel\u201d", sut.WindowTitle);
    }

    /// <summary>
    /// Verifies that changing <see cref="ChildWindowModel.ContentFilter"/> directly on the target
    /// updates <see cref="PanelCustomizeViewModel.ContentFilter"/> and raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> for it (M-2).
    /// </summary>
    [Fact]
    public void ModelContentFilterChangedDirectly_UpdatesDialog()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        model.ContentFilter = PanelContentFilter.FoldersOnly;

        Assert.Equal(PanelContentFilter.FoldersOnly, sut.ContentFilter);
        Assert.Contains(nameof(PanelCustomizeViewModel.ContentFilter), raised);
    }

    /// <summary>
    /// Verifies that assigning <see cref="ChildWindowModel.SourceFolderPath"/> directly on the target
    /// (bypassing <see cref="PanelCustomizeViewModel.BrowseFolderCommand"/>/<see cref="PanelCustomizeViewModel.ClearFolderCommand"/>)
    /// still updates <see cref="PanelCustomizeViewModel.IsFolderPortal"/>/<see cref="PanelCustomizeViewModel.FolderPortalPath"/>/
    /// <see cref="PanelCustomizeViewModel.SortModeDescription"/> (M-2).
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathChangedDirectly_UpdatesDialog()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        model.SourceFolderPath = @"C:\fake\portal";

        Assert.True(sut.IsFolderPortal);
        Assert.Equal(@"C:\fake\portal", sut.FolderPortalPath);
        Assert.Equal("Folder Portal panels always sort by name.", sut.SortModeDescription);
    }

    // ── Content: ContentFilter ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that setting <see cref="PanelCustomizeViewModel.ContentFilter"/> writes through to the
    /// target and persists via <see cref="IWindowManager.SaveWindowsState"/>.
    /// </summary>
    [Fact]
    public void ContentFilter_Set_WritesTargetAndSaves()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        sut.ContentFilter = PanelContentFilter.HideShortcuts;

        Assert.Equal(PanelContentFilter.HideShortcuts, model.ContentFilter);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    // ── Content: Sort order description (minor finding 9) ────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.SortModeDescription"/> shows a neutral
    /// explanation for a non-portal panel, and the Folder-Portal-specific text only once it becomes a
    /// portal (FIX-B fix-cycle minor finding 9 — this used to show the portal text unconditionally).
    /// </summary>
    [Fact]
    public void SortModeDescription_NeutralForNonPortal_PortalTextOncePortal()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        Assert.Equal("Manual keeps the order you arrange; By name sorts alphabetically.", sut.SortModeDescription);

        model.SourceFolderPath = @"C:\fake\portal";

        Assert.Equal("Folder Portal panels always sort by name.", sut.SortModeDescription);
    }

    // ── Content: Folder Portal (Browse/Clear, D-4) ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.IsFolderPortal"/>/<see cref="PanelCustomizeViewModel.FolderPortalPath"/>
    /// reflect the target's <see cref="ChildWindowModel.SourceFolderPath"/>.
    /// </summary>
    [Fact]
    public void IsFolderPortalAndFolderPortalPath_ReflectTargetSourceFolderPath()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        Assert.False(sut.IsFolderPortal);
        Assert.Equal(string.Empty, sut.FolderPortalPath);

        model.SourceFolderPath = @"C:\Users\me\Desktop";

        Assert.True(sut.IsFolderPortal);
        Assert.Equal(@"C:\Users\me\Desktop", sut.FolderPortalPath);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.BrowseFolderCommand"/> uses
    /// <see cref="IFolderPickerService.PickFolder"/> (FIX-B fix-cycle minor finding 5), passing the
    /// panel's current portal path as the initial folder, and assigns the picked path.
    /// </summary>
    [Fact]
    public void BrowseFolderCommand_PickedPath_SetsSourceFolderPath()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { SourceFolderPath = @"C:\old\portal" };
        sut.Target = model;
        _folderPickerMock.Setup(f => f.PickFolder(It.IsAny<string>(), @"C:\old\portal")).Returns(@"C:\new\portal");

        sut.BrowseFolderCommand.Execute(null);

        Assert.Equal(@"C:\new\portal", model.SourceFolderPath);
    }

    /// <summary>
    /// Verifies that cancelling the folder picker (a <see langword="null"/> result) leaves the target
    /// untouched.
    /// </summary>
    [Fact]
    public void BrowseFolderCommand_Cancelled_DoesNotChangeSourceFolderPath()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { SourceFolderPath = @"C:\old\portal" };
        sut.Target = model;
        _folderPickerMock.Setup(f => f.PickFolder(It.IsAny<string>(), It.IsAny<string?>())).Returns((string?)null);

        sut.BrowseFolderCommand.Execute(null);

        Assert.Equal(@"C:\old\portal", model.SourceFolderPath);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.ClearFolderCommand"/> clears the target's
    /// <see cref="ChildWindowModel.SourceFolderPath"/>, and updates
    /// <see cref="PanelCustomizeViewModel.IsFolderPortal"/>. Unlike every other setter in this class,
    /// clearing the Folder Portal must NOT itself call <see cref="IWindowManager.SaveWindowsState"/>
    /// (FIX-B fix-cycle minor finding 6 — <c>ChildWindowViewModel</c> already persists this change via
    /// its own <c>FilesChanged</c> reaction, so an explicit second call here would just duplicate it).
    /// </summary>
    [Fact]
    public void ClearFolderCommand_Execute_ClearsSourceFolderPath_DoesNotSaveItself()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { SourceFolderPath = @"C:\fake\portal" };
        sut.Target = model;
        Assert.True(sut.IsFolderPortal);

        sut.ClearFolderCommand.Execute(null);

        Assert.Null(model.SourceFolderPath);
        Assert.False(sut.IsFolderPortal);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.ClearFolderCommand"/> cannot execute when the
    /// panel isn't a Folder Portal, but can once it is.
    /// </summary>
    [Fact]
    public void ClearFolderCommand_CanExecute_FalseWhenNotPortal_TrueWhenPortal()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        Assert.False(sut.ClearFolderCommand.CanExecute(null));

        model.SourceFolderPath = @"C:\fake\portal";
        Assert.True(sut.ClearFolderCommand.CanExecute(null));
    }

    /// <summary>
    /// Verifies that re-clearing an already-unbound panel is a harmless no-op.
    /// </summary>
    [Fact]
    public void ClearFolderCommand_Execute_AlreadyCleared_DoesNotThrow()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.Target = model;

        sut.ClearFolderCommand.Execute(null);

        Assert.Null(model.SourceFolderPath);
    }

    // ── Content: Sort order disabled for a Folder Portal ─────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SelectedPanelSettingsViewModel.SelectedPanelSupportsManualOrder"/> (the
    /// Sort-order selector's <c>IsEnabled</c> binding source) is <see langword="false"/> while the
    /// dialog's target is a Folder Portal, and <see langword="true"/> once
    /// <see cref="PanelCustomizeViewModel.ClearFolderCommand"/> clears it — exercised through the
    /// dialog's real Clear code path (settings-window-redesign.md D-4), not a raw model mutation.
    /// </summary>
    [Fact]
    public void SortOrderSupportsManualOrder_FalseForPortal_TrueAfterClearFolderCommand()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { SourceFolderPath = @"C:\fake\portal" };
        sut.Target = model;

        Assert.False(sut.Appearance.SelectedPanelSupportsManualOrder);

        sut.ClearFolderCommand.Execute(null);

        Assert.True(sut.Appearance.SelectedPanelSupportsManualOrder);
    }

    // ── Layout: IconLayoutPolicy ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that setting <see cref="PanelCustomizeViewModel.IconLayoutPolicy"/> writes through to
    /// the target and persists via <see cref="IWindowManager.SaveWindowsState"/>.
    /// </summary>
    [Fact]
    public void IconLayoutPolicy_Set_WritesTargetAndSaves()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch };
        sut.Target = model;

        sut.IconLayoutPolicy = IconLayoutPolicy.FixedGap;

        Assert.Equal(IconLayoutPolicy.FixedGap, model.IconLayoutPolicy);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    /// <summary>
    /// Verifies that setting <see cref="PanelCustomizeViewModel.IconLayoutPolicy"/> while the layout is
    /// locked is silently ignored (FIX-B fix-cycle MAJOR finding M-1 — the panel's own context menu
    /// disables this same control under <c>IsLayoutLocked</c>, ADR-026).
    /// </summary>
    [Fact]
    public void IconLayoutPolicy_SetWhileLocked_IsIgnored()
    {
        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(false);
        var sut = BuildSut();
        var model = new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch };
        sut.Target = model;

        sut.IconLayoutPolicy = IconLayoutPolicy.FixedGap;

        Assert.Equal(IconLayoutPolicy.LegacyStretch, model.IconLayoutPolicy);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.CanChangeLayout"/> reacts to
    /// <see cref="ILayoutEditModeService"/>'s <c>PropertyChanged</c> — e.g. the tray icon's "Lock layout"
    /// toggle flipping while this dialog is open (FIX-B fix-cycle MAJOR finding M-1).
    /// </summary>
    [Fact]
    public void CanChangeLayout_ReactsToLayoutEditModeChange()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel();
        Assert.True(sut.CanChangeLayout);

        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(false);
        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        _layoutEditModeMock.Raise(l => l.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.CanEnterEditLayout)));

        Assert.False(sut.CanChangeLayout);
        Assert.Contains(nameof(PanelCustomizeViewModel.CanChangeLayout), raised);
        Assert.Contains(nameof(PanelCustomizeViewModel.IconLayoutDescription), raised);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.IconLayoutDescription"/> shows the lock reason
    /// while layout changes are disallowed (FIX-B fix-cycle MAJOR finding M-1 — a description, not only
    /// a tooltip).
    /// </summary>
    [Fact]
    public void IconLayoutDescription_ShowsLockReason_WhileLayoutLocked()
    {
        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(false);
        var sut = BuildSut();
        sut.Target = new ChildWindowModel();

        Assert.Equal("Unavailable while the layout is locked.", sut.IconLayoutDescription);
    }

    // ── Layout: Auto-size to content, enabled only in Grid view ─────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.CanAutoSizeToContent"/> is <see langword="true"/>
    /// only while the target is in <see cref="PanelViewMode.Grid"/>.
    /// </summary>
    [Fact]
    public void CanAutoSizeToContent_TrueOnlyInGridView()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { ViewMode = PanelViewMode.List };
        sut.Target = model;

        Assert.False(sut.CanAutoSizeToContent);

        sut.Appearance.SelectedPanelViewMode = PanelViewMode.Grid;
        Assert.True(sut.CanAutoSizeToContent);

        sut.Appearance.SelectedPanelViewMode = PanelViewMode.List;
        Assert.False(sut.CanAutoSizeToContent);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.CanAutoSizeToContent"/> raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> when the view mode changes.
    /// </summary>
    [Fact]
    public void CanAutoSizeToContent_RaisesPropertyChanged_WhenViewModeChanges()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { ViewMode = PanelViewMode.List };

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        sut.Appearance.SelectedPanelViewMode = PanelViewMode.Grid;

        Assert.Contains(nameof(PanelCustomizeViewModel.CanAutoSizeToContent), raised);
        Assert.Contains(nameof(PanelCustomizeViewModel.AutoSizeToContentDescription), raised);
    }

    /// <summary>
    /// Verifies that setting <see cref="PanelCustomizeViewModel.IsAutoSizeToContent"/> writes
    /// <see cref="PanelSizingMode.AutoContent"/>/<see cref="PanelSizingMode.Manual"/> onto the target
    /// and persists via <see cref="IWindowManager.SaveWindowsState"/>.
    /// </summary>
    [Fact]
    public void IsAutoSizeToContent_Set_WritesSizingModeAndSaves()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, SizingMode = PanelSizingMode.Manual };
        sut.Target = model;

        sut.IsAutoSizeToContent = true;
        Assert.Equal(PanelSizingMode.AutoContent, model.SizingMode);

        sut.IsAutoSizeToContent = false;
        Assert.Equal(PanelSizingMode.Manual, model.SizingMode);

        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Exactly(2));
    }

    /// <summary>
    /// Verifies that setting <see cref="PanelCustomizeViewModel.IsAutoSizeToContent"/> while the layout
    /// is locked is silently ignored, even in Grid view (FIX-B fix-cycle MAJOR finding M-1).
    /// </summary>
    [Fact]
    public void IsAutoSizeToContent_SetWhileLocked_IsIgnored()
    {
        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(false);
        var sut = BuildSut();
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, SizingMode = PanelSizingMode.Manual };
        sut.Target = model;

        sut.IsAutoSizeToContent = true;

        Assert.Equal(PanelSizingMode.Manual, model.SizingMode);
        _windowManagerMock.Verify(w => w.SaveWindowsState(), Times.Never);
    }

    /// <summary>
    /// Verifies <see cref="PanelCustomizeViewModel.CanToggleAutoSizeToContent"/> requires both the
    /// layout-lock gate and the Grid-view gate (FIX-B fix-cycle MAJOR finding M-1).
    /// </summary>
    /// <param name="canChangeLayout">The value <see cref="ILayoutEditModeService.CanEnterEditLayout"/> is stubbed to return.</param>
    /// <param name="viewMode">The target panel's <see cref="ChildWindowModel.ViewMode"/>.</param>
    /// <param name="expected">The expected resulting <see cref="PanelCustomizeViewModel.CanToggleAutoSizeToContent"/>.</param>
    [Theory]
    [InlineData(true, PanelViewMode.Grid, true)]
    [InlineData(true, PanelViewMode.List, false)]
    [InlineData(false, PanelViewMode.Grid, false)]
    [InlineData(false, PanelViewMode.List, false)]
    public void CanToggleAutoSizeToContent_RequiresBothGates(bool canChangeLayout, PanelViewMode viewMode, bool expected)
    {
        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(canChangeLayout);
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { ViewMode = viewMode };

        Assert.Equal(expected, sut.CanToggleAutoSizeToContent);
    }

    /// <summary>
    /// Verifies that <see cref="PanelCustomizeViewModel.AutoSizeToContentDescription"/> prioritizes the
    /// layout-lock reason over the Grid-view-only reason when both apply (FIX-B fix-cycle MAJOR finding
    /// M-1).
    /// </summary>
    [Fact]
    public void AutoSizeToContentDescription_LockReasonTakesPriorityOverViewModeReason()
    {
        _layoutEditModeMock.SetupGet(l => l.CanEnterEditLayout).Returns(false);
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { ViewMode = PanelViewMode.List };

        Assert.Equal("Unavailable while the layout is locked.", sut.AutoSizeToContentDescription);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that after <see cref="PanelCustomizeViewModel.Dispose"/>, further edits through
    /// <see cref="PanelCustomizeViewModel.Appearance"/> no longer raise
    /// <see cref="PanelCustomizeViewModel.WindowTitle"/> change notifications (the subscription that
    /// forwards them was torn down).
    /// </summary>
    [Fact]
    public void Dispose_ThenAppearanceTitleChanges_DoesNotRaiseWindowTitleChanged()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { Title = "Old" };

        sut.Dispose();

        var raised = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelCustomizeViewModel.WindowTitle)) raised = true;
        };

        sut.Appearance.SelectedPanelTitle = "New";

        Assert.False(raised);
    }

    /// <summary>
    /// Verifies that after <see cref="PanelCustomizeViewModel.Dispose"/>, a change made directly to the
    /// target model no longer raises notifications on this dialog (M-2 unsubscribe).
    /// </summary>
    [Fact]
    public void Dispose_ThenModelChanges_DoesNotRaisePropertyChanged()
    {
        var sut = BuildSut();
        var model = new ChildWindowModel { Title = "Old" };
        sut.Target = model;

        sut.Dispose();

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        model.Title = "New";

        Assert.False(raised);
    }

    /// <summary>
    /// Verifies that after <see cref="PanelCustomizeViewModel.Dispose"/>, a subsequent
    /// <see cref="ILayoutEditModeService"/> change no longer raises notifications on this dialog.
    /// </summary>
    [Fact]
    public void Dispose_ThenLayoutEditModeChanges_DoesNotRaisePropertyChanged()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel();

        sut.Dispose();

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        _layoutEditModeMock.Raise(l => l.PropertyChanged += null, new PropertyChangedEventArgs(nameof(ILayoutEditModeService.CanEnterEditLayout)));

        Assert.False(raised);
    }

    // ── Localization: hot culture switch (docs/PLANS/localization.md §2.9) ──────────────────────

    /// <summary>
    /// Verifies that switching the language while the dialog is open re-raises
    /// <see cref="PanelCustomizeViewModel.WindowTitle"/> and every localized description property, with
    /// their Russian text (docs/PLANS/localization.md §2.9 "re-raise rule").
    /// </summary>
    [Fact]
    public void CultureChanged_RaisesWindowTitleAndDescriptions()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { Title = "Downloads" };

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        _localization.ApplyLanguage("ru");

        Assert.Equal("Настройка «Downloads»", sut.WindowTitle);
        Assert.Equal("Вручную сохраняет порядок, который вы задали сами; По имени сортирует по алфавиту.", sut.SortModeDescription);
        Assert.Contains(nameof(PanelCustomizeViewModel.WindowTitle), raised);
        Assert.Contains(nameof(PanelCustomizeViewModel.SortModeDescription), raised);
        Assert.Contains(nameof(PanelCustomizeViewModel.IconLayoutDescription), raised);
        Assert.Contains(nameof(PanelCustomizeViewModel.AutoSizeToContentDescription), raised);
    }

    /// <summary>
    /// Verifies that after <see cref="PanelCustomizeViewModel.Dispose"/>, a subsequent language switch no
    /// longer raises notifications on this dialog (the <see cref="ILocalizationService.CultureChanged"/>
    /// subscription was torn down).
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromCultureChanged()
    {
        var sut = BuildSut();
        sut.Target = new ChildWindowModel { Title = "Downloads" };

        sut.Dispose();

        var raised = false;
        sut.PropertyChanged += (_, _) => raised = true;

        _localization.ApplyLanguage("ru");

        Assert.False(raised);
    }

    private PanelCustomizeViewModel BuildSut() =>
        new(_windowManagerMock.Object, _layoutEditModeMock.Object, _folderPickerMock.Object, _localization, _colorPickerMock.Object, _appSettingsMock.Object);
}
