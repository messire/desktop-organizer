using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="ChildWindowViewModel"/>'s "Move/Copy to page ▸" routing (UX review, backlog
/// 1.x item 8 — Fences parity, "Send Fence to Page") — <see cref="ChildWindowViewModel.Pages"/>'s sync
/// against <see cref="IPageManager.PageDescriptors"/>/<see cref="IPageManager.PagesChanged"/>, and
/// <see cref="ChildWindowViewModel.MovePanelToPageCommand"/>/<see cref="ChildWindowViewModel.CopyPanelToPageCommand"/>'s
/// <c>CanExecute</c>/routing into <see cref="IWindowManager"/>. Kept in its own file (rather than added to
/// the much larger <c>ChildWindowViewModelTests</c>) so it does not collide with concurrent, unrelated work
/// on that file.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowViewModelPageRoutingTests
{
    private readonly Mock<IIconProvider> _iconProviderMock = new();
    private readonly Mock<IFileLauncher> _fileLauncherMock = new();
    private readonly Mock<IFolderWatcherFactory> _watcherFactoryMock = new();
    private readonly Mock<IPanelItemStorageService> _storageMock = new();
    private readonly Mock<IFileItemRenameService> _renameServiceMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IPanelSelectionCoordinator> _selectionCoordinatorMock = new();
    private readonly Mock<IGestureDiagnostics> _diagnosticsMock = new();
    private readonly Mock<IWindowManager> _windowManagerMock = new();
    private readonly Mock<IPageManager> _pageManagerMock = new();
    private readonly Mock<IPanelCloneFlow> _panelCloneFlowMock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModelPageRoutingTests"/> class, ensuring
    /// a live WPF <see cref="System.Windows.Application"/> exists and seeding the default mocks.
    /// </summary>
    public ChildWindowViewModelPageRoutingTests()
    {
        WpfApplicationHost.EnsureApplication();
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((System.Windows.Media.ImageSource)null!);
        _appSettingsMock.SetupProperty(s => s.HasShownMoveHint, false);
        _pageManagerMock.SetupGet(m => m.PageDescriptors).Returns([new PageDescriptor("default", null)]);
    }

    /// <summary>The constructor populates <see cref="ChildWindowViewModel.Pages"/> from <see cref="IPageManager.PageDescriptors"/> when a page manager is supplied.</summary>
    [Fact]
    public void Constructor_WithPageManager_PopulatesPagesFromDescriptors()
    {
        _pageManagerMock.SetupGet(m => m.PageDescriptors)
            .Returns([new PageDescriptor("default", null), new PageDescriptor("work", "Work")]);

        using var sut = BuildSut();

        Assert.Equal(2, sut.Pages.Count);
        Assert.Equal("default", sut.Pages[0].Id);
        Assert.Equal("work", sut.Pages[1].Id);
        Assert.Equal("Work", sut.Pages[1].Name);
    }

    /// <summary>Without a page manager (some tests construct this type directly), <see cref="ChildWindowViewModel.Pages"/> stays permanently empty rather than throwing.</summary>
    [Fact]
    public void Constructor_WithoutPageManager_PagesStaysEmpty()
    {
        using var sut = BuildSut(includePageManager: false);

        Assert.Empty(sut.Pages);
    }

    /// <summary><see cref="IPageManager.PagesChanged"/> re-syncs <see cref="ChildWindowViewModel.Pages"/> from the (now different) <see cref="IPageManager.PageDescriptors"/>.</summary>
    [Fact]
    public void PagesChanged_ResyncsPages()
    {
        using var sut = BuildSut();
        Assert.Single(sut.Pages);

        _pageManagerMock.SetupGet(m => m.PageDescriptors)
            .Returns([new PageDescriptor("default", null), new PageDescriptor("work", "Work")]);
        _pageManagerMock.Raise(m => m.PagesChanged += null);

        Assert.Equal(2, sut.Pages.Count);
    }

    /// <summary><see cref="ChildWindowViewModel.Dispose"/> unsubscribes from <see cref="IPageManager.PagesChanged"/> — a later raise must not resync a disposed instance's <see cref="ChildWindowViewModel.Pages"/>.</summary>
    [Fact]
    public void Dispose_UnsubscribesFromPagesChanged()
    {
        var sut = BuildSut();
        Assert.Single(sut.Pages);
        sut.Dispose();

        _pageManagerMock.SetupGet(m => m.PageDescriptors)
            .Returns([new PageDescriptor("default", null), new PageDescriptor("work", "Work")]);
        _pageManagerMock.Raise(m => m.PagesChanged += null);

        // A disposed VM's Pages collection must stay exactly as it was at Dispose() time — proving the
        // PagesChanged subscription was actually removed, not merely that SyncPages happens to no-op.
        Assert.Single(sut.Pages);
    }

    /// <summary><see cref="ChildWindowViewModel.MovePanelToPageCommand"/> cannot execute for the panel's own current page.</summary>
    [Fact]
    public void MovePanelToPageCommand_CanExecute_False_ForCurrentPage()
    {
        using var sut = BuildSut();
        sut.Model.PageId = "default";

        Assert.False(sut.MovePanelToPageCommand.CanExecute("default"));
    }

    /// <summary><see cref="ChildWindowViewModel.MovePanelToPageCommand"/> can execute for a page other than the panel's own current one.</summary>
    [Fact]
    public void MovePanelToPageCommand_CanExecute_True_ForDifferentPage()
    {
        using var sut = BuildSut();
        sut.Model.PageId = "default";

        Assert.True(sut.MovePanelToPageCommand.CanExecute("work"));
    }

    /// <summary>A blank/whitespace destination id never allows <see cref="ChildWindowViewModel.MovePanelToPageCommand"/> to execute.</summary>
    /// <param name="pageId">The blank/whitespace destination page id under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MovePanelToPageCommand_CanExecute_False_ForBlankPageId(string? pageId)
    {
        using var sut = BuildSut();

        Assert.False(sut.MovePanelToPageCommand.CanExecute(pageId));
    }

    /// <summary>Without an <see cref="IWindowManager"/>, <see cref="ChildWindowViewModel.MovePanelToPageCommand"/> can never execute.</summary>
    [Fact]
    public void MovePanelToPageCommand_CanExecute_False_WhenNoWindowManager()
    {
        using var sut = BuildSut(includeWindowManager: false);
        sut.Model.PageId = "default";

        Assert.False(sut.MovePanelToPageCommand.CanExecute("work"));
    }

    /// <summary><see cref="ChildWindowViewModel.MovePanelToPageCommand"/> delegates to <see cref="IWindowManager.MovePanelToPage"/> for this panel's own <see cref="ChildWindowViewModel.Model"/>.</summary>
    [Fact]
    public void MovePanelToPageCommand_Execute_CallsWindowManagerMovePanelToPage()
    {
        using var sut = BuildSut();
        sut.Model.PageId = "default";

        sut.MovePanelToPageCommand.Execute("work");

        _windowManagerMock.Verify(m => m.MovePanelToPage(sut.Model, "work"), Times.Once);
    }

    /// <summary>A blank destination id never reaches <see cref="IWindowManager.MovePanelToPage"/> (defense in depth alongside <c>CanExecute</c>).</summary>
    [Fact]
    public void MovePanelToPageCommand_Execute_BlankPageId_DoesNotCallWindowManager()
    {
        using var sut = BuildSut();

        sut.MovePanelToPageCommand.Execute(" ");

        _windowManagerMock.Verify(m => m.MovePanelToPage(It.IsAny<ChildWindowModel>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>Unlike Move, <see cref="ChildWindowViewModel.CopyPanelToPageCommand"/> can execute for the panel's own current page (ADR-045 same-page duplicate).</summary>
    [Fact]
    public void CopyPanelToPageCommand_CanExecute_True_ForCurrentPage()
    {
        using var sut = BuildSut();
        sut.Model.PageId = "default";

        Assert.True(sut.CopyPanelToPageCommand.CanExecute("default"));
    }

    /// <summary>
    /// Without an <see cref="IPanelCloneFlow"/>, <see cref="ChildWindowViewModel.CopyPanelToPageCommand"/>
    /// can never execute (H-1 fix, code review, panel clone to page, backlog 1.x wave 3 review: gates on
    /// <see cref="IPanelCloneFlow"/> now, not <see cref="IWindowManager"/> — cloning itself is entirely
    /// the flow's own concern).
    /// </summary>
    [Fact]
    public void CopyPanelToPageCommand_CanExecute_False_WhenNoPanelCloneFlow()
    {
        using var sut = BuildSut(includePanelCloneFlow: false);

        Assert.False(sut.CopyPanelToPageCommand.CanExecute("work"));
    }

    /// <summary><see cref="ChildWindowViewModel.CopyPanelToPageCommand"/> delegates to <see cref="IPanelCloneFlow.CopyToPage"/> for this panel's own <see cref="ChildWindowViewModel.Model"/>.</summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_CallsPanelCloneFlow()
    {
        _panelCloneFlowMock
            .Setup(f => f.CopyToPage(It.IsAny<ChildWindowModel>(), "work", It.IsAny<Action>()))
            .Returns(new PanelCloneResult(PanelCloneOutcome.Cloned, new ChildWindowModel(), 0));

        using var sut = BuildSut();

        sut.CopyPanelToPageCommand.Execute("work");

        _panelCloneFlowMock.Verify(f => f.CopyToPage(sut.Model, "work", It.IsAny<Action>()), Times.Once);
    }

    /// <summary>
    /// H-1 fix (code review, panel clone to page, backlog 1.x wave 3 review): before this fix,
    /// <c>ChildWindowViewModel.CopyPanelToPage</c> called <see cref="IWindowManager.ClonePanelToPage"/>
    /// directly and discarded its result — a failure outcome never reached the user from this panel's own
    /// header menu. Wired against a REAL <see cref="PanelCloneFlow"/> (only its own <see cref="IWindowManager"/>/
    /// <see cref="IConfirmationService"/>/<see cref="IBusyIndicator"/> collaborators are mocked) to prove the
    /// fix end to end: a failure now surfaces a localized <see cref="IConfirmationService.Notify"/> call.
    /// </summary>
    [Fact]
    public void CopyPanelToPageCommand_Execute_Failure_Notifies()
    {
        var confirmationMock = new Mock<IConfirmationService>();
        var busyIndicatorMock = new Mock<IBusyIndicator>();
        busyIndicatorMock.Setup(b => b.Show()).Returns(Mock.Of<IDisposable>());
        var realFlow = new PanelCloneFlow(_windowManagerMock.Object, confirmationMock.Object, TestLocalization.English, busyIndicatorMock.Object);
        _windowManagerMock
            .Setup(m => m.ClonePanelToPage(It.IsAny<ChildWindowModel>(), "work"))
            .Returns(new PanelCloneResult(PanelCloneOutcome.CopyFailed, null, 0));

        using var sut = BuildSut(panelCloneFlow: realFlow);

        sut.CopyPanelToPageCommand.Execute("work");

        confirmationMock.Verify(
            c => c.Notify(
                TestLocalization.English.GetString("Panel_Copy_Failed_Message"),
                TestLocalization.English.GetString("Panel_Copy_Failed_Title"),
                MessageBoxImage.Warning),
            Times.Once);
    }

    private ChildWindowViewModel BuildSut(
        bool includeWindowManager = true,
        bool includePageManager = true,
        bool includePanelCloneFlow = true,
        IPanelCloneFlow? panelCloneFlow = null) => new(
        _iconProviderMock.Object,
        _fileLauncherMock.Object,
        _watcherFactoryMock.Object,
        _storageMock.Object,
        _renameServiceMock.Object,
        _appSettingsMock.Object,
        _selectionCoordinatorMock.Object,
        _diagnosticsMock.Object,
        iconResizeDebounceTimerProvider: null,
        windowManager: includeWindowManager ? _windowManagerMock.Object : null,
        pageManager: includePageManager ? _pageManagerMock.Object : null,
        panelCloneFlow: panelCloneFlow ?? (includePanelCloneFlow ? _panelCloneFlowMock.Object : null));
}
