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
/// Unit tests for <see cref="MainWindowViewModel.CopyPanelToPageCommand"/> (<c>CopyPanelToPage</c>/
/// <c>CanCopyPanelToPage</c>, ADR-045 «Panel clone to page», backlog 1.x §8c).
/// </summary>
/// <remarks>
/// H-1/M-E fixes (code review, panel clone to page, backlog 1.x wave 3 review): the busy cursor, the
/// failure notification and the stale-destination-page silent re-sync all moved out of
/// <c>MainWindowViewModel.CopyPanelToPage</c> into the shared <see cref="IPanelCloneFlow"/> (mocked here) —
/// those behaviors are now covered by <c>PanelCloneFlowTests</c> instead. This file only covers what
/// remains <see cref="MainWindowViewModel"/>'s own concern: <c>CanExecute</c>, delegating to
/// <see cref="IPanelCloneFlow.CopyToPage"/> with the right arguments, and what happens to
/// <see cref="MainWindowViewModel.SelectedPanel"/>/<see cref="MainWindowViewModel.PanelsOnCurrentPage"/>/
/// <see cref="MainWindowViewModel.Status"/> for each possible <see cref="PanelCloneResult"/>.
/// <para>
/// Kept in its own file (rather than added to <c>MainWindowViewModelTests.cs</c>) per the backlog 1.x
/// orchestration split: a separate agent is concurrently editing the rename-command region of that file.
/// Builds its own minimal <see cref="MainWindowViewModel"/> fixture rather than sharing the other file's
/// private <c>BuildSut</c> helper, for the same reason.
/// </para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class MainWindowViewModelCopyPanelTests
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
    private readonly Mock<IPanelCloneFlow> _panelCloneFlowMock = new();
    private readonly Mock<IStatusNotifier> _statusNotifierMock = new();
    private readonly Mock<IDesktopIconsService> _desktopIconsMock = new();

    // A real English LocalizationService — Status assertions below check actual RESX text for
    // Panel_CopiedToPage_Message_Format, same convention MainWindowViewModelTests already uses
    // (docs/PLANS/localization.md R-5).
    private readonly ILocalizationService _localization = TestLocalization.English;

    // ── CanCopyPanelToPage ───────────────────────────────────────────────────

    /// <summary>No selected panel means the command can never execute, regardless of the destination page id.</summary>
    [Fact]
    public void CanCopyPanelToPage_NoSelectedPanel_ReturnsFalse()
    {
        var sut = BuildSut();

        Assert.False(sut.CopyPanelToPageCommand.CanExecute("default"));
    }

    /// <summary>
    /// Unlike <see cref="MainWindowViewModel.MovePanelToPageCommand"/>, copying a panel onto its OWN
    /// current page is a valid, expected destination — it is how a same-page duplicate is requested.
    /// </summary>
    [Fact]
    public void CanCopyPanelToPage_SelectedPanel_CurrentPageIsAllowed_ReturnsTrue()
    {
        var sut = BuildSut();
        sut.SelectedPanel = new ChildWindowModel { PageId = "default" };

        Assert.True(sut.CopyPanelToPageCommand.CanExecute("default"));
    }

    /// <summary>A blank/whitespace destination id never allows execution.</summary>
    /// <param name="pageId">The blank/whitespace destination page id under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanCopyPanelToPage_BlankPageId_ReturnsFalse(string? pageId)
    {
        var sut = BuildSut();
        sut.SelectedPanel = new ChildWindowModel { PageId = "default" };

        Assert.False(sut.CopyPanelToPageCommand.CanExecute(pageId));
    }

    /// <summary>
    /// Documents the CURRENT behavior precisely: <c>CanCopyPanelToPage</c> does not itself validate that
    /// the destination id is one of <see cref="IPageManager"/>'s existing pages — it mirrors
    /// <see cref="MainWindowViewModel.MovePanelToPageCommand"/>'s own <c>CanExecute</c>, which has the
    /// same shape (see <c>MainWindowViewModelTests.MovePanelToPageCommand_CanExecute_FalseWithoutSelectionOrForSamePage</c>).
    /// The "known destination page" guard lives one layer down, inside <see cref="IPanelCloneFlow.CopyToPage"/>
    /// (via <see cref="IWindowManager.ClonePanelToPage"/>'s own <see cref="PanelCloneOutcome.UnknownPage"/>).
    /// </summary>
    [Fact]
    public void CanCopyPanelToPage_UnknownPageId_CurrentlyReturnsTrue_ValidatedLaterByPanelCloneFlow()
    {
        var sut = BuildSut();
        sut.SelectedPanel = new ChildWindowModel { PageId = "default" };

        Assert.True(sut.CopyPanelToPageCommand.CanExecute("no-such-page"));
    }

    // ── Execute: wiring ──────────────────────────────────────────────────────

    /// <summary>Delegates to <see cref="IPanelCloneFlow.CopyToPage"/> with the selected panel and the given destination id.</summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_CallsPanelCloneFlow()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "work", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, new ChildWindowModel { PageId = "work" }, 0));

        sut.CopyPanelToPageCommand.Execute("work");

        _panelCloneFlowMock.Verify(f => f.CopyToPage(panel, "work", It.IsAny<Action>()), Times.Once);
    }

    // ── Execute: Cloned outcome ──────────────────────────────────────────────

    /// <summary>A successful copy onto the CURRENT page selects the newly created clone, so the user immediately sees what was just created.</summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_ClonedOnCurrentPage_SelectsClone()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;
        var clone = new ChildWindowModel { PageId = "default", Title = "Notes — copy" };
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "default", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, clone, 0));

        sut.CopyPanelToPageCommand.Execute("default");

        Assert.Same(clone, sut.SelectedPanel);
    }

    /// <summary>A successful copy onto a DIFFERENT (currently hidden) page leaves the existing selection alone.</summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_ClonedOnOtherPage_LeavesSelectionUnchanged()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var panel = new ChildWindowModel { PageId = "default" };

        // RefreshPanelsOnCurrentPage (called on every Cloned outcome, regardless of destination page)
        // clears SelectedPanel if it isn't found among Panels' own current-page entries — Panels is only
        // populated by SyncPanels()/GetAllChildWindows(), so the selected panel must be seeded through
        // there first, same as MainWindowViewModelTests' own PanelsOnCurrentPage_* fixtures.
        _windowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([panel]);
        var sut = BuildSut();
        sut.SyncPanels();
        sut.SelectedPanel = panel;
        var clone = new ChildWindowModel { PageId = "work" };
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "work", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, clone, 0));

        sut.CopyPanelToPageCommand.Execute("work");

        Assert.Same(panel, sut.SelectedPanel);
    }

    /// <summary>
    /// M-E fix (code review, panel clone to page, backlog 1.x wave 3 review); phase 6.3 (design-system-
    /// 1x.md §2.8) moved this off <c>PanelPageStatusMessage</c> onto <see cref="IStatusNotifier"/>: a
    /// successful copy shows a brief, NON-blocking <see cref="StatusSeverity.Success"/> status naming the
    /// destination page — replacing the previous MODAL success <see cref="IConfirmationService.Notify"/>.
    /// </summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_Cloned_ShowsSuccessStatus()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;
        var clone = new ChildWindowModel { PageId = "default" };
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "default", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, clone, 0));

        sut.CopyPanelToPageCommand.Execute("default");

        _statusNotifierMock.Verify(
            n => n.Show(
                StatusSeverity.Success,
                _localization.Format("Panel_CopiedToPage_Message_Format", "Default"),
                _localization.GetString("Panel_CopiedToPage_Title")),
            Times.Once);
        _confirmationMock.Verify(
            c => c.Notify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxImage>()),
            Times.Never);
    }

    /// <summary>
    /// <see cref="PanelCloneResult.SkippedMissingItems"/> is expected, recoverable behavior — it does not
    /// change the ordinary success status into an alarming one; the count itself is never surfaced via
    /// <see cref="IConfirmationService.Notify"/>, only logged (unchanged from before the UX review — see
    /// <see cref="IWindowManager.ClonePanelToPage"/>'s own remarks).
    /// </summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_ClonedWithSkippedMissingItems_SetsOrdinaryStatusOnly()
    {
        _pageManagerMock.SetupGet(p => p.CurrentPage).Returns("default");
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;
        var clone = new ChildWindowModel { PageId = "default" };
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "default", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, clone, SkippedMissingItems: 3));

        sut.CopyPanelToPageCommand.Execute("default");

        _statusNotifierMock.Verify(
            n => n.Show(
                StatusSeverity.Success,
                _localization.Format("Panel_CopiedToPage_Message_Format", "Default"),
                _localization.GetString("Panel_CopiedToPage_Title")),
            Times.Once);
    }

    // ── Execute: non-Cloned outcomes (failure handling itself lives in IPanelCloneFlow — see PanelCloneFlowTests) ──

    /// <summary>
    /// A non-<c>Cloned</c> outcome (the mocked <see cref="IPanelCloneFlow"/> already "handled" it, e.g. by
    /// notifying or re-syncing <see cref="MainWindowViewModel.Pages"/> internally) never touches
    /// <see cref="MainWindowViewModel.SelectedPanel"/> and never shows a new <see cref="IStatusNotifier"/> status.
    /// </summary>
    /// <param name="outcome">The non-<c>Cloned</c> outcome under test.</param>
    [Theory]
    [InlineData(PanelCloneOutcome.TooLarge)]
    [InlineData(PanelCloneOutcome.InsufficientSpace)]
    [InlineData(PanelCloneOutcome.CopyFailed)]
    public void CopyPanelToPageCommand_Execute_FailureOutcome_DoesNotChangeSelectionOrStatus(PanelCloneOutcome outcome)
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "work", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(outcome, null, 0));

        sut.CopyPanelToPageCommand.Execute("work");

        Assert.Same(panel, sut.SelectedPanel);
        _statusNotifierMock.Verify(
            n => n.Show(It.IsAny<StatusSeverity>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// A <see cref="PanelCloneOutcome.UnknownPage"/> outcome invokes the <c>onUnknownPage</c> callback this
    /// ViewModel passed to <see cref="IPanelCloneFlow.CopyToPage"/> — <see cref="MainWindowViewModel.SyncPages"/>
    /// (mirroring <c>OnPagesChanged</c>'s own silent re-sync, since the outcome means this ViewModel's own
    /// <see cref="MainWindowViewModel.Pages"/> was already stale by the time the flow ran).
    /// </summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_UnknownPageOutcome_InvokesCallback_SyncsPages()
    {
        var sut = BuildSut();
        var panel = new ChildWindowModel { PageId = "default" };
        sut.SelectedPanel = panel;
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(panel, "gone", It.IsAny<Action>()))
            .Callback<ChildWindowModel, string, Action>((_, _, onUnknownPage) => onUnknownPage())
            .Returns(new PanelCloneResult(PanelCloneOutcome.UnknownPage, null, 0));

        // The stale page no longer exists by the time the callback runs — SyncPages() must pick that up
        // from IPageManager.PageDescriptors, the same source OnPagesChanged already re-syncs from.
        _pageManagerMock.Setup(p => p.PageDescriptors).Returns([new PageDescriptor("default", "Default")]);

        sut.CopyPanelToPageCommand.Execute("gone");

        Assert.Single(sut.Pages);
        Assert.Equal("default", sut.Pages[0].Id);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private MainWindowViewModel BuildSut()
    {
        _pageManagerMock.Setup(p => p.Pages).Returns([]);
        _pageManagerMock.Setup(p => p.PageDescriptors).Returns([]);
        _layoutSnapshotServiceMock.Setup(s => s.Snapshots).Returns([]);
        _systemIconsPanelManagerMock.Setup(m => m.Model).Returns(new SystemIconsPanelModel());

        var layoutSnapshots = new LayoutSnapshotsViewModel(
            _layoutSnapshotServiceMock.Object,
            _confirmationMock.Object,
            _inputDialogServiceMock.Object,
            _localization,
            Mock.Of<DesktopOrganizer.Services.Abstractions.Storage.IApplicationStateStore>(st => st.IsWritable));

        var about = new AboutViewModel(_updateCheckServiceMock.Object, _linkLauncherMock.Object, _localization);

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
            _localization,
            CultureInfo.GetCultureInfo("en"),
            about,
            _colorPickerMock.Object,
            _panelCloneFlowMock.Object,
            _statusNotifierMock.Object,
            _desktopIconsMock.Object);
    }
}
