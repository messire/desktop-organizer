using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the pure model/storage logic behind <see cref="WindowManager.ClonePanelToPage"/>
/// (ADR-045, «Panel clone to page», backlog 1.x §8b):
/// <list type="bullet">
/// <item><see cref="WindowManager.BuildClonedFilePaths"/> — the "duplicate every item" half, extracted
/// <see langword="internal static"/> for the same reason <see cref="WindowManager.ReleaseManagedItems"/>
/// already is (see <see cref="WindowManagerTests"/>'s own class remarks): fully testable with a mocked
/// <see cref="IPanelItemStorageService"/>, no real WPF <c>ChildWindow</c> required.</item>
/// <item><see cref="WindowManager.ClonePanelToPage"/> itself, but ONLY the failure paths that return
/// before <c>CreateChildWindow</c> ever runs (<c>UnknownPage</c>, and any <c>BuildClonedFilePaths</c>
/// failure) — those never touch a real WPF window, so a fully mocked <see cref="WindowManager"/> instance
/// can exercise them end to end, including the "nothing was saved" guarantee.</item>
/// </list>
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WindowManagerCloneTests
{
    // ── BuildClonedFilePaths ──────────────────────────────────────────────────

    /// <summary>A non-managed path (an external file, or a shortcut left where it was) is carried over unchanged, never copied.</summary>
    [Fact]
    public void BuildClonedFilePaths_NonManagedPath_KeptAsIs_NeverCopied()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(@"C:\external\file.txt")).Returns(false);

        var plan = WindowManager.BuildClonedFilePaths([@"C:\external\file.txt"], storage.Object);

        Assert.True(plan.Success);
        Assert.Equal([@"C:\external\file.txt"], plan.FilePaths);
        Assert.Equal(0, plan.SkippedMissingItems);
        storage.Verify(s => s.CopyManaged(It.IsAny<string>()), Times.Never);
    }

    /// <summary>A managed path is duplicated via <see cref="IPanelItemStorageService.CopyManaged"/> and the clone's list gets the NEW copy path, not the source's.</summary>
    [Fact]
    public void BuildClonedFilePaths_ManagedPath_Copied_MapsToNewPath()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(@"C:\managed\a.lnk")).Returns(true);
        storage.Setup(s => s.CopyManaged(@"C:\managed\a.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Copied, @"C:\managed\new-guid\a.lnk"));

        var plan = WindowManager.BuildClonedFilePaths([@"C:\managed\a.lnk"], storage.Object);

        Assert.True(plan.Success);
        Assert.Equal([@"C:\managed\new-guid\a.lnk"], plan.FilePaths);
        Assert.Equal(0, plan.SkippedMissingItems);
    }

    /// <summary>
    /// A managed item whose file is already gone from disk is left out of the clone entirely (never a
    /// reference to nothing that two panels would both "own") and counted in <c>SkippedMissingItems</c> —
    /// the plan still succeeds.
    /// </summary>
    [Fact]
    public void BuildClonedFilePaths_ManagedPathSourceMissing_SkippedAndCounted_PlanStillSucceeds()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(@"C:\managed\gone.lnk")).Returns(true);
        storage.Setup(s => s.CopyManaged(@"C:\managed\gone.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.SourceMissing, null));

        var plan = WindowManager.BuildClonedFilePaths([@"C:\managed\gone.lnk"], storage.Object);

        Assert.True(plan.Success);
        Assert.Empty(plan.FilePaths);
        Assert.Equal(1, plan.SkippedMissingItems);
    }

    /// <summary>A copy failure on the k-th managed item discards every copy already made in THIS call (the first k-1), in order, and the plan fails with <see cref="PanelCloneOutcome.CopyFailed"/>.</summary>
    [Fact]
    public void BuildClonedFilePaths_CopyFailsOnThirdItem_DiscardsFirstTwoCopies_ReturnsCopyFailed()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(true);
        storage.Setup(s => s.CopyManaged(@"C:\managed\1.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Copied, @"C:\managed\copy1.lnk"));
        storage.Setup(s => s.CopyManaged(@"C:\managed\2.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Copied, @"C:\managed\copy2.lnk"));
        storage.Setup(s => s.CopyManaged(@"C:\managed\3.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Failed, null));

        var plan = WindowManager.BuildClonedFilePaths(
            [@"C:\managed\1.lnk", @"C:\managed\2.lnk", @"C:\managed\3.lnk"],
            storage.Object);

        Assert.False(plan.Success);
        Assert.Equal(PanelCloneOutcome.CopyFailed, plan.FailureOutcome);
        Assert.Empty(plan.FilePaths);
        storage.Verify(
            s => s.DiscardCopies(
                It.Is<IReadOnlyList<string>>(paths => paths.Count == 2
                    && paths[0] == @"C:\managed\copy1.lnk"
                    && paths[1] == @"C:\managed\copy2.lnk")),
            Times.Once);
    }

    /// <summary>A directory too large to duplicate maps to <see cref="PanelCloneOutcome.TooLarge"/> and rolls back every copy already made.</summary>
    [Fact]
    public void BuildClonedFilePaths_TooLarge_MapsToPanelCloneOutcomeTooLarge_AndRollsBack()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(It.IsAny<string>())).Returns(true);
        storage.Setup(s => s.CopyManaged(@"C:\managed\1.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Copied, @"C:\managed\copy1.lnk"));
        storage.Setup(s => s.CopyManaged(@"C:\managed\big"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.TooLarge, null));

        var plan = WindowManager.BuildClonedFilePaths([@"C:\managed\1.lnk", @"C:\managed\big"], storage.Object);

        Assert.False(plan.Success);
        Assert.Equal(PanelCloneOutcome.TooLarge, plan.FailureOutcome);
        storage.Verify(s => s.DiscardCopies(It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == @"C:\managed\copy1.lnk")), Times.Once);
    }

    /// <summary>Insufficient disk space maps to <see cref="PanelCloneOutcome.InsufficientSpace"/>.</summary>
    [Fact]
    public void BuildClonedFilePaths_InsufficientSpace_MapsToPanelCloneOutcomeInsufficientSpace()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(@"C:\managed\huge.lnk")).Returns(true);
        storage.Setup(s => s.CopyManaged(@"C:\managed\huge.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.InsufficientSpace, null));

        var plan = WindowManager.BuildClonedFilePaths([@"C:\managed\huge.lnk"], storage.Object);

        Assert.False(plan.Success);
        Assert.Equal(PanelCloneOutcome.InsufficientSpace, plan.FailureOutcome);
        storage.Verify(s => s.DiscardCopies(It.IsAny<IReadOnlyList<string>>()), Times.Once);
    }

    /// <summary>An empty source file list is a trivial success with an empty (non-null) clone list.</summary>
    [Fact]
    public void BuildClonedFilePaths_EmptySourceList_ReturnsEmptySuccessPlan()
    {
        var storage = new Mock<IPanelItemStorageService>();

        var plan = WindowManager.BuildClonedFilePaths([], storage.Object);

        Assert.True(plan.Success);
        Assert.Empty(plan.FilePaths);
        Assert.Equal(0, plan.SkippedMissingItems);
    }

    /// <summary>A mix of managed and non-managed paths preserves the source's own order in the clone's list.</summary>
    [Fact]
    public void BuildClonedFilePaths_MixOfManagedAndNonManaged_PreservesOrder()
    {
        var storage = new Mock<IPanelItemStorageService>();
        storage.Setup(s => s.IsManaged(@"C:\external\1.txt")).Returns(false);
        storage.Setup(s => s.IsManaged(@"C:\managed\2.lnk")).Returns(true);
        storage.Setup(s => s.IsManaged(@"C:\external\3.txt")).Returns(false);
        storage.Setup(s => s.CopyManaged(@"C:\managed\2.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Copied, @"C:\managed\copy2.lnk"));

        var plan = WindowManager.BuildClonedFilePaths(
            [@"C:\external\1.txt", @"C:\managed\2.lnk", @"C:\external\3.txt"],
            storage.Object);

        Assert.True(plan.Success);
        Assert.Equal([@"C:\external\1.txt", @"C:\managed\copy2.lnk", @"C:\external\3.txt"], plan.FilePaths);
    }

    // ── ClonePanelToPage: failure paths only (no real WPF ChildWindow involved) ──

    // NEW-3 fix (final review, panel clone to page, backlog 1.x) / M-5 fix (review 6.1): the "clone was
    // persisted, THEN a subscriber threw" rollback branch needs CreateChildWindowCore to SUCCEED first —
    // covered by the STA tests in the "rollback after persist" region below, which build one real
    // (never shown) ChildWindow for the clone.

    /// <summary>A blank destination page id is rejected before touching storage or persistence.</summary>
    [Fact]
    public void ClonePanelToPage_BlankPageId_ReturnsUnknownPage_NoStorageOrSaveCalls()
    {
        var manager = BuildWindowManager(out var appStateMock, out _, itemStorageMock: out var storageMock);
        var source = new ChildWindowModel { PageId = "default" };

        var result = manager.ClonePanelToPage(source, "   ");

        Assert.Equal(PanelCloneOutcome.UnknownPage, result.Outcome);
        Assert.Null(result.ClonedPanel);
        Assert.Equal(0, result.SkippedMissingItems);
        storageMock.Verify(s => s.IsManaged(It.IsAny<string>()), Times.Never);
        appStateMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>A page id that isn't one of <c>IPageManager.Pages</c>'s existing pages is rejected the same way.</summary>
    [Fact]
    public void ClonePanelToPage_PageNotInPages_ReturnsUnknownPage()
    {
        var manager = BuildWindowManager(out var appStateMock, out _, itemStorageMock: out _);
        var source = new ChildWindowModel { PageId = "default" };

        var result = manager.ClonePanelToPage(source, "no-such-page");

        Assert.Equal(PanelCloneOutcome.UnknownPage, result.Outcome);
        Assert.Null(result.ClonedPanel);
        appStateMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>
    /// A managed item copy failure surfaces as the matching <see cref="PanelCloneOutcome"/>, creates no
    /// clone, and never reaches the structural-save call — the whole operation is transactional.
    /// </summary>
    [Fact]
    public void ClonePanelToPage_ManagedItemCopyFails_ReturnsCopyFailed_NoCloneCreated_NeverSaves()
    {
        var manager = BuildWindowManager(out var appStateMock, out _, itemStorageMock: out var storageMock);
        storageMock.Setup(s => s.IsManaged(@"C:\managed\a.lnk")).Returns(true);
        storageMock.Setup(s => s.CopyManaged(@"C:\managed\a.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Failed, null));
        var source = new ChildWindowModel { PageId = "default", FilePaths = [@"C:\managed\a.lnk"] };

        var result = manager.ClonePanelToPage(source, "work");

        Assert.Equal(PanelCloneOutcome.CopyFailed, result.Outcome);
        Assert.Null(result.ClonedPanel);
        Assert.Equal(0, result.SkippedMissingItems);
        appStateMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>Same as above, for the <c>TooLarge</c> outcome specifically.</summary>
    [Fact]
    public void ClonePanelToPage_ManagedItemTooLarge_ReturnsTooLarge_NeverSaves()
    {
        var manager = BuildWindowManager(out var appStateMock, out _, itemStorageMock: out var storageMock);
        storageMock.Setup(s => s.IsManaged(@"C:\managed\big")).Returns(true);
        storageMock.Setup(s => s.CopyManaged(@"C:\managed\big"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.TooLarge, null));
        var source = new ChildWindowModel { PageId = "default", FilePaths = [@"C:\managed\big"] };

        var result = manager.ClonePanelToPage(source, "work");

        Assert.Equal(PanelCloneOutcome.TooLarge, result.Outcome);
        appStateMock.Verify(s => s.SaveNow(), Times.Never);
    }

    /// <summary>
    /// H-1 fix (code review, panel clone to page): once every managed item copy has already succeeded,
    /// anything that throws afterward — here, <c>CreateChildWindow</c>'s own monitor/window resolution
    /// against a service provider that was never set up to produce a live <c>ChildWindow</c> or a real
    /// monitor — must roll back EVERY copy <c>BuildClonedFilePaths</c> already made via
    /// <see cref="IPanelItemStorageService.DiscardCopies"/>, report <see cref="PanelCloneOutcome.CopyFailed"/>,
    /// and never reach the structural <c>SaveWindowsStateNow</c> call — the clone is either complete, or
    /// none of it happened, exactly like a mid-copy failure.
    /// </summary>
    [Fact]
    public void ClonePanelToPage_CreateChildWindowThrowsAfterCopiesSucceeded_DiscardsCopies_ReturnsCopyFailed_NeverSaves()
    {
        var manager = BuildWindowManager(out var appStateMock, out _, itemStorageMock: out var storageMock);
        storageMock.Setup(s => s.IsManaged(@"C:\managed\a.lnk")).Returns(true);
        storageMock.Setup(s => s.CopyManaged(@"C:\managed\a.lnk"))
            .Returns(new ManagedCopyResult(ManagedCopyOutcome.Copied, @"C:\managed\copy1.lnk"));
        var source = new ChildWindowModel { PageId = "default", FilePaths = [@"C:\managed\a.lnk"] };

        // Pробел в тестах (final review, panel clone to page, backlog 1.x): CreateChildWindowCore throws
        // before OnChildWindowAdded/OnChildWindowRemoved are ever wired up to fire for this clone (M-B fix
        // remarks on WindowManager.ClonePanelToPage) — neither subscriber below may ever observe this
        // clone, ghost or otherwise.
        var addedRaisedCount = 0;
        var removedRaisedCount = 0;
        manager.OnChildWindowAdded += (_, _) => addedRaisedCount++;
        manager.OnChildWindowRemoved += (_, _) => removedRaisedCount++;

        // BuildWindowManager's IServiceProvider/IMonitorService mocks are never set up to hand back a real
        // ChildWindow or MonitorInfo — CreateChildWindow's own ResolveMonitorPlacement/DI resolution throws
        // once it actually runs, which it never does for any of the failure-path tests above (they all
        // return before CreateChildWindow is even reached).
        var result = manager.ClonePanelToPage(source, "work");

        Assert.Equal(PanelCloneOutcome.CopyFailed, result.Outcome);
        Assert.Null(result.ClonedPanel);
        Assert.Equal(0, result.SkippedMissingItems);
        storageMock.Verify(
            s => s.DiscardCopies(It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == @"C:\managed\copy1.lnk")),
            Times.Once);
        appStateMock.Verify(s => s.SaveNow(), Times.Never);
        Assert.Equal(0, addedRaisedCount);
        Assert.Equal(0, removedRaisedCount);
    }

    // ── ClonePanelToPage: rollback after persist (real, never-shown ChildWindow; STA) ──

    /// <summary>
    /// NEW-3: an <see cref="WindowManager.OnChildWindowAdded"/> subscriber throwing AFTER the clone was
    /// persisted unwinds it — the owed <see cref="WindowManager.OnChildWindowRemoved"/> is raised once and the
    /// unwound state is saved a second time, so the clone does not come back on the next load.
    /// </summary>
    [Fact]
    public void ClonePanelToPage_AddedSubscriberThrowsAfterPersist_RaisesRemoved_ResavesUnwoundState()
    {
        StaTestRunner.Run(() =>
        {
            using var harness = CloneRollbackHarness.Build();
            var removedRaised = 0;
            harness.Manager.OnChildWindowAdded += (_, _) => throw new InvalidOperationException("added subscriber");
            harness.Manager.OnChildWindowRemoved += (_, _) => removedRaised++;

            var result = harness.Manager.ClonePanelToPage(CloneRollbackHarness.BuildSource(), "work");

            Assert.Equal(PanelCloneOutcome.CopyFailed, result.Outcome);
            Assert.Null(result.ClonedPanel);
            Assert.Equal(1, removedRaised);
            harness.ApplicationState.Verify(s => s.SaveNow(), Times.Exactly(2));
        });
    }

    /// <summary>
    /// M-5 (review 6.1): when the owed <see cref="WindowManager.OnChildWindowRemoved"/> subscriber ALSO
    /// throws, the exception stays inside <see cref="WindowManager.ClonePanelToPage"/> — the unwound state is
    /// still re-saved, both failures are logged, and the caller still gets
    /// <see cref="PanelCloneOutcome.CopyFailed"/> instead of an exception escaping from inside the rollback.
    /// </summary>
    [Fact]
    public void ClonePanelToPage_RemovedSubscriberThrowsDuringRollback_StillResavesLogsAndReturnsCopyFailed()
    {
        StaTestRunner.Run(() =>
        {
            using var harness = CloneRollbackHarness.Build();
            harness.Manager.OnChildWindowAdded += (_, _) => throw new InvalidOperationException("added subscriber");
            harness.Manager.OnChildWindowRemoved += (_, _) => throw new InvalidOperationException("removed subscriber");

            var result = default(PanelCloneResult);
            var ex = Record.Exception(() => result = harness.Manager.ClonePanelToPage(CloneRollbackHarness.BuildSource(), "work"));

            Assert.Null(ex);
            Assert.Equal(PanelCloneOutcome.CopyFailed, result.Outcome);
            harness.ApplicationState.Verify(s => s.SaveNow(), Times.Exactly(2));
            harness.Log.Verify(
                l => l.Error("WindowManager", "PanelCloneRemovedHandlerFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
                Times.Once);
            harness.Log.Verify(
                l => l.Error("WindowManager", "PanelCloneCreateFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
                Times.Once);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fully mocked <see cref="WindowManager"/> instance — safe for the failure paths above
    /// because none of them ever reach <c>CreateChildWindow</c> (the only place a real WPF
    /// <c>ChildWindow</c> is resolved through DI), and the source panel is never registered via
    /// <c>CreateChildWindow</c> either, so <c>ClonePanelToPage</c>'s own <c>SyncEachIsolated</c> pre-step
    /// (which touches a live WPF window) finds nothing to sync and is skipped.
    /// </summary>
    private static WindowManager BuildWindowManager(
        out Mock<IApplicationStateService> applicationStateServiceMock,
        out Mock<IPageManager> pageManagerMock,
        out Mock<IPanelItemStorageService> itemStorageMock)
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var monitorServiceMock = new Mock<IMonitorService>();
        itemStorageMock = new Mock<IPanelItemStorageService>();
        applicationStateServiceMock = new Mock<IApplicationStateService>();
        applicationStateServiceMock.SetupGet(s => s.Panels).Returns([]);

        pageManagerMock = new Mock<IPageManager>();
        pageManagerMock.SetupGet(p => p.Pages).Returns(["default", "work"]);
        serviceProviderMock.Setup(p => p.GetService(typeof(IPageManager))).Returns(pageManagerMock.Object);

        // H-1 fix (code review, panel clone to page): ClonePanelToPage now resolves the clone's title
        // (which needs ILocalizationService) BEFORE calling BuildClonedFilePaths — even for the
        // CopyFailed/TooLarge tests below, which never used to reach title resolution at all.
        serviceProviderMock.Setup(p => p.GetService(typeof(ILocalizationService))).Returns(TestLocalization.English);

        var appSettingsMock = new Mock<IAppSettings>();
        appSettingsMock.SetupGet(s => s.GridSize).Returns(1);

        return new WindowManager(
            serviceProviderMock.Object,
            monitorServiceMock.Object,
            itemStorageMock.Object,
            applicationStateServiceMock.Object,
            appSettingsMock.Object,
            FakeDesktopScale.Uniform().Object);
    }

    /// <summary>
    /// A <see cref="WindowManager"/> whose DI hands out real <see cref="ChildWindow"/>s (never shown: the
    /// clone goes to a page other than the current one), built against a swapped-in reduced resource chain
    /// restored on <see cref="Dispose"/> — the same discipline as <c>ChildWindowInitializeTests</c>'s own
    /// harness (duplicated rather than shared, per that class's remarks). Must be built and disposed on the
    /// same STA thread (<see cref="StaTestRunner"/>).
    /// </summary>
    private sealed class CloneRollbackHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private CloneRollbackHarness(
            Application app,
            ResourceDictionary? originalResources,
            bool ownsApplication,
            WindowManager manager,
            Mock<IApplicationStateService> applicationState,
            Mock<IAppLog> log)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Manager = manager;
            ApplicationState = applicationState;
            Log = log;
        }

        public WindowManager Manager { get; }

        public Mock<IApplicationStateService> ApplicationState { get; }

        public Mock<IAppLog> Log { get; }

        public static ChildWindowModel BuildSource() => new()
        {
            PageId = "default",
            Title = "Notes",
            Position = new System.Drawing.Point(100, 100),
            Size = new System.Drawing.Size(300, 200),
        };

        public static CloneRollbackHarness Build()
        {
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            TestThemeResources.MergeReduced(app);
            app.Resources["LayoutEditMode"] = Mock.Of<ILayoutEditModeService>();

            var monitor = new MonitorInfo("DISPLAY1", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), true);
            var monitorService = new Mock<IMonitorService>();
            monitorService.Setup(m => m.FindMonitorAt(It.IsAny<Point>())).Returns(monitor);
            monitorService.Setup(m => m.FindByDeviceName(It.IsAny<string>())).Returns(monitor);
            monitorService.Setup(m => m.GetPrimaryMonitor()).Returns(monitor);

            var pageManager = new Mock<IPageManager>();
            pageManager.SetupGet(p => p.Pages).Returns(["default", "work"]);
            pageManager.SetupGet(p => p.CurrentPage).Returns("default");

            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider.Setup(p => p.GetService(typeof(IPageManager))).Returns(pageManager.Object);
            serviceProvider.Setup(p => p.GetService(typeof(ILocalizationService))).Returns(TestLocalization.English);
            serviceProvider.Setup(p => p.GetService(typeof(ChildWindow))).Returns(() => BuildChildWindow(app, monitorService.Object));

            var applicationState = new Mock<IApplicationStateService>();
            applicationState.SetupGet(s => s.Panels).Returns([]);

            var appSettings = new Mock<IAppSettings>();
            appSettings.SetupGet(s => s.GridSize).Returns(1);

            var log = new Mock<IAppLog>();
            var manager = new WindowManager(
                serviceProvider.Object,
                monitorService.Object,
                Mock.Of<IPanelItemStorageService>(),
                applicationState.Object,
                appSettings.Object,
                FakeDesktopScale.Uniform().Object,
                log.Object);

            return new CloneRollbackHarness(app, originalResources, ownsApplication, manager, applicationState, log);
        }

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }

        private static ChildWindow BuildChildWindow(Application app, IMonitorService monitorService)
        {
            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((System.Windows.Media.ImageSource)null!);

            var viewModel = new ChildWindowViewModel(
                iconProvider.Object,
                Mock.Of<IFileLauncher>(),
                Mock.Of<IFolderWatcherFactory>(),
                Mock.Of<IPanelItemStorageService>(),
                Mock.Of<IFileItemRenameService>(),
                Mock.Of<IAppSettings>(),
                Mock.Of<IPanelSelectionCoordinator>(),
                Mock.Of<IGestureDiagnostics>());

            var appSettings = new Mock<IAppSettings>();
            appSettings.SetupProperty(s => s.NativePanelSizing, false);
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, true);
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, string.Empty);

            return new ChildWindow(
                viewModel,
                Mock.Of<IWindowManager>(),
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                Mock.Of<IGestureDiagnostics>(),
                Mock.Of<INativePanelSizingService>(),
                (ILayoutEditModeService)app.Resources["LayoutEditMode"],
                monitorService,
                Mock.Of<IConfirmationService>(),
                Mock.Of<IPanelCustomizeService>(),
                Mock.Of<IPanelItemDragOutSession>(),
                FakeDesktopScale.Uniform().Object);
        }
    }
}
