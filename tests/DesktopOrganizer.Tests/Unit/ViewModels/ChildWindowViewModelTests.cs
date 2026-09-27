using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="ChildWindowViewModel"/>'s Move-semantics glue: the
/// <see cref="ChildWindowViewModel.AddDroppedFiles"/> fallback-to-original-path behavior when
/// <see cref="IPanelItemStorageService.MoveIn"/>/<see cref="IPanelItemStorageService.MoveInDirectory"/>
/// return <see langword="null"/> (D13 — see docs/PLANS/drag-drop-move-semantics.md), and the rest of the
/// panel ViewModel behavior (drag-out glue, inline rename, portals, liveness). The native shell context menu
/// glue has its own fixture, <c>ChildWindowViewModelShellMenuTests</c>.
/// </summary>
/// <remarks>
/// <see cref="ChildWindowViewModel"/>'s constructor resolves view templates via
/// <see cref="System.Windows.Application.Current"/>.FindResource, so these tests need a live WPF
/// <see cref="System.Windows.Application"/> with the app's real template resource dictionary merged
/// in (same dictionary <c>App.xaml</c> merges) — otherwise construction throws
/// <see cref="System.Windows.ResourceReferenceKeyNotFoundException"/>. <see cref="IIconProvider"/>,
/// <see cref="IFileLauncher"/>, <see cref="IFolderWatcherFactory"/> and
/// <see cref="IPanelItemStorageService"/> are mocked — no real shell/file-system access beyond the
/// plain <see cref="System.IO"/> calls the VM itself makes (e.g. <see cref="Directory.Exists"/> for
/// the file-vs-folder branch in <see cref="ChildWindowViewModel.AddDroppedFiles"/>).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Mock<IIconProvider> _iconProviderMock = new();
    private readonly Mock<IFileLauncher> _fileLauncherMock = new();
    private readonly Mock<IFolderWatcherFactory> _watcherFactoryMock = new();
    private readonly Mock<IPanelItemStorageService> _storageMock = new();
    private readonly Mock<IFileItemRenameService> _renameServiceMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IPanelSelectionCoordinator> _selectionCoordinatorMock = new();
    private readonly Mock<IGestureDiagnostics> _diagnosticsMock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModelTests"/> class, ensuring a live
    /// WPF <see cref="Application"/> exists and seeding the default icon provider and move-hint mocks.
    /// </summary>
    public ChildWindowViewModelTests()
    {
        EnsureWpfApplication();
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((System.Windows.Media.ImageSource)null!);

        // Default: the one-time Move-semantics hint hasn't been shown yet. Individual tests that
        // care about the already-shown case override this via SetupProperty again.
        _appSettingsMock.SetupProperty(s => s.HasShownMoveHint, false);
    }

    // ── D13: AddDroppedFiles fallback when MoveIn/MoveInDirectory return null ────────────────────

    /// <summary>
    /// Verifies that when <see cref="IPanelItemStorageService.MoveIn"/> returns <c>null</c> for a
    /// dropped file, <see cref="ChildWindowViewModel.AddDroppedFiles"/> falls back to referencing the
    /// original source path in place.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_MoveInReturnsNull_FallsBackToOriginalPath()
    {
        // Arrange — a path that is NOT a directory, so AddDroppedFiles takes the MoveIn (file) branch.
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");

        _storageMock.Setup(s => s.MoveIn(source)).Returns((string?)null);

        using var sut = BuildSut();

        try
        {
            // Act
            sut.AddDroppedFiles([source]);

            // Assert — referencing fallback: the item keeps the original path.
            var item = Assert.Single(sut.AllItems);
            Assert.Equal(source, item.Path);
            _storageMock.Verify(s => s.MoveIn(source), Times.Once);
            _storageMock.Verify(s => s.MoveInDirectory(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>
    /// Verifies that when <see cref="IPanelItemStorageService.MoveInDirectory"/> returns <c>null</c> for
    /// a dropped folder, <see cref="ChildWindowViewModel.AddDroppedFiles"/> falls back to referencing the
    /// original source folder path in place.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_MoveInDirectoryReturnsNull_FallsBackToOriginalPath()
    {
        // Arrange — a real temp directory, so AddDroppedFiles takes the MoveInDirectory (folder) branch
        // (the VM calls System.IO.Directory.Exists directly to decide file vs folder).
        var source = NewTempDirPath();

        _storageMock.Setup(s => s.MoveInDirectory(source)).Returns((string?)null);

        using var sut = BuildSut();

        try
        {
            // Act
            sut.AddDroppedFiles([source]);

            // Assert — referencing fallback: the item keeps the original folder path.
            var item = Assert.Single(sut.AllItems);
            Assert.Equal(source, item.Path);
            _storageMock.Verify(s => s.MoveInDirectory(source), Times.Once);
            _storageMock.Verify(s => s.MoveIn(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that when <see cref="IPanelItemStorageService.MoveIn"/> returns a managed path,
    /// <see cref="ChildWindowViewModel.AddDroppedFiles"/> uses that managed path instead of the original
    /// source path.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_MoveInSucceeds_UsesManagedPath()
    {
        // Arrange — positive control: when MoveIn returns a managed path, the VM must use it, not
        // the original source path.
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");

        _storageMock.Setup(s => s.MoveIn(source)).Returns(managedPath);

        using var sut = BuildSut();

        try
        {
            // Act
            sut.AddDroppedFiles([source]);

            // Assert
            var item = Assert.Single(sut.AllItems);
            Assert.Equal(managedPath, item.Path);
            Assert.NotEqual(source, item.Path);
        }
        finally
        {
            File.Delete(source);
        }
    }

    // ── Move-hint: MoveHintRequested (one-time, guarded by IAppSettings.HasShownMoveHint) ───────

    /// <summary>
    /// Verifies that when a dropped file is genuinely moved and the one-time hint has not been shown
    /// yet, <see cref="ChildWindowViewModel.MoveHintRequested"/> fires exactly once and
    /// <see cref="IAppSettings.HasShownMoveHint"/> is persisted.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_FileActuallyMoved_HintNotShownYet_RaisesMoveHintOnceAndPersists()
    {
        // Arrange — MoveIn returns a genuinely different, managed path, so this is a real move.
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");

        _storageMock.Setup(s => s.MoveIn(source)).Returns(managedPath);
        _storageMock.Setup(s => s.IsManaged(managedPath)).Returns(true);

        using var sut = BuildSut();
        var raisedCount = 0;
        sut.MoveHintRequested += (_, _) => raisedCount++;

        try
        {
            // Act
            sut.AddDroppedFiles([source]);

            // Assert
            Assert.Equal(1, raisedCount);
            Assert.True(_appSettingsMock.Object.HasShownMoveHint);
            _appSettingsMock.Verify(s => s.Save(), Times.Once);
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>
    /// Verifies that when a dropped file is genuinely moved but the one-time hint already fired in a
    /// past session, <see cref="ChildWindowViewModel.MoveHintRequested"/> does not fire again and
    /// settings are not re-saved.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_FileActuallyMoved_HintAlreadyShown_DoesNotRaiseOrSave()
    {
        // Arrange — same "real move" as above, but the one-time hint already fired in a past session.
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");

        _storageMock.Setup(s => s.MoveIn(source)).Returns(managedPath);
        _storageMock.Setup(s => s.IsManaged(managedPath)).Returns(true);
        _appSettingsMock.SetupProperty(s => s.HasShownMoveHint, true);

        using var sut = BuildSut();
        var raised = false;
        sut.MoveHintRequested += (_, _) => raised = true;

        try
        {
            // Act
            sut.AddDroppedFiles([source]);

            // Assert
            Assert.False(raised);
            _appSettingsMock.Verify(s => s.Save(), Times.Never);
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>
    /// Verifies that when a dropped file falls back to its original path (no real move happened),
    /// <see cref="ChildWindowViewModel.MoveHintRequested"/> does not fire even though the hint has not
    /// been shown yet.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_NothingActuallyMoved_HintNotShownYet_DoesNotRaise()
    {
        // Arrange — MoveIn fails and falls back to referencing the original path in place: no move
        // happened, so the hint must not fire even though HasShownMoveHint is still false.
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");

        _storageMock.Setup(s => s.MoveIn(source)).Returns((string?)null);

        using var sut = BuildSut();
        var raised = false;
        sut.MoveHintRequested += (_, _) => raised = true;

        try
        {
            // Act
            sut.AddDroppedFiles([source]);

            // Assert
            Assert.False(raised);
            Assert.False(_appSettingsMock.Object.HasShownMoveHint);
            _appSettingsMock.Verify(s => s.Save(), Times.Never);
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>
    /// Verifies that when a single drop contains multiple paths and only one of them is genuinely
    /// moved, <see cref="ChildWindowViewModel.MoveHintRequested"/> fires exactly once for the whole
    /// drop, not once per moved file.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_MultiplePathsOnlyOneActuallyMoved_RaisesMoveHintExactlyOnce()
    {
        // Arrange — two dropped paths in a single call: the first falls back (no move), the second is
        // a genuine move. The hint must fire once for the whole drop, not once per moved file.
        var sourceA = NewTempFilePath();
        var sourceB = NewTempFilePath();
        File.WriteAllText(sourceA, "a");
        File.WriteAllText(sourceB, "b");
        var managedB = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");

        _storageMock.Setup(s => s.MoveIn(sourceA)).Returns((string?)null);
        _storageMock.Setup(s => s.MoveIn(sourceB)).Returns(managedB);
        _storageMock.Setup(s => s.IsManaged(managedB)).Returns(true);

        using var sut = BuildSut();
        var raisedCount = 0;
        sut.MoveHintRequested += (_, _) => raisedCount++;

        try
        {
            // Act
            sut.AddDroppedFiles([sourceA, sourceB]);

            // Assert
            Assert.Equal(1, raisedCount);
            _appSettingsMock.Verify(s => s.Save(), Times.Once);
        }
        finally
        {
            File.Delete(sourceA);
            File.Delete(sourceB);
        }
    }

    // ── ADR-036 (release-hardening-1.0.md §E): drag-out transfer / external-move VM glue ─────────

    /// <summary>
    /// <see cref="ChildWindowViewModel.AddTransferredItem"/> adds a reference to the given path without
    /// ever calling <see cref="IPanelItemStorageService.MoveIn"/>/<c>MoveInDirectory</c> — the managed
    /// copy (if any) is shared storage already owned by the source panel's own manifest entry.
    /// </summary>
    [Fact]
    public void AddTransferredItem_AddsReference_NeverCallsMoveIn()
    {
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        using var sut = BuildSut();

        sut.AddTransferredItem(managedPath);

        var item = Assert.Single(sut.AllItems);
        Assert.Equal(managedPath, item.Path);
        _storageMock.Verify(s => s.MoveIn(It.IsAny<string>()), Times.Never);
        _storageMock.Verify(s => s.MoveInDirectory(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A Folder Portal never accepts a transferred item — defense in depth mirroring
    /// <c>ChildWindow.OnDrop</c>'s own portal gate, which already refuses the drop before this is reached.
    /// </summary>
    [Fact]
    public void AddTransferredItem_FolderPortal_DoesNotAddItem()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });

        try
        {
            sut.AddTransferredItem(Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt"));

            Assert.Empty(sut.AllItems);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="ChildWindowViewModel.RemoveTransferredItem"/> removes the item from this panel without
    /// ever calling <see cref="IPanelItemStorageService.Restore"/>/<see cref="IPanelItemStorageService.ReleaseToRecycleBin"/>
    /// — the underlying managed copy now belongs to the target panel's own reference.
    /// </summary>
    [Fact]
    public void RemoveTransferredItem_RemovesItem_NeverRestoresOrRecycles()
    {
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        using var sut = BuildSut();
        sut.AddFiles([managedPath]);
        var item = Assert.Single(sut.AllItems);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;

        sut.RemoveTransferredItem(item);

        Assert.Empty(sut.AllItems);
        Assert.Equal(1, filesChanged);
        _storageMock.Verify(s => s.Restore(It.IsAny<string>()), Times.Never);
        _storageMock.Verify(s => s.ReleaseToRecycleBin(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    /// <summary>
    /// <see cref="ChildWindowViewModel.RemoveMovedOutItem"/> removes the item and tells the storage
    /// service to forget it (manifest-only cleanup) — the file itself is already gone by the shell's own
    /// hand, confirmed by the caller before this is ever invoked.
    /// </summary>
    [Fact]
    public void RemoveMovedOutItem_RemovesItem_AndForgetsManagedPath()
    {
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        using var sut = BuildSut();
        sut.AddFiles([managedPath]);
        var item = Assert.Single(sut.AllItems);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;

        sut.RemoveMovedOutItem(item);

        Assert.Empty(sut.AllItems);
        Assert.Equal(1, filesChanged);
        _storageMock.Verify(s => s.ForgetMovedOut(managedPath), Times.Once);
    }

    /// <summary>
    /// ADR-036 §E: a raw dropped path that's already inside app-managed storage (e.g. dragged directly
    /// out of <c>PanelItems</c> via Explorer, bypassing the in-app drag-out session) is rejected outright
    /// by <see cref="ChildWindowViewModel.AddDroppedFiles"/> — never re-<c>MoveIn</c>'d, which would
    /// orphan whichever panel's manifest entry already owns it.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_ManagedPathWithoutActiveSession_Rejected()
    {
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        _storageMock.Setup(s => s.IsManaged(managedPath)).Returns(true);
        using var sut = BuildSut();

        sut.AddDroppedFiles([managedPath]);

        Assert.Empty(sut.AllItems);
        _storageMock.Verify(s => s.MoveIn(It.IsAny<string>()), Times.Never);
        _storageMock.Verify(s => s.MoveInDirectory(It.IsAny<string>()), Times.Never);
    }

    // ── D3 (Block D, docs/PLANS/phase-7-performance-hardening.md): Model.PropertyChanged
    // subscription lifecycle — no leak past Dispose, no double subscription on repeated LoadFromModel.

    /// <summary>
    /// Verifies that after <see cref="ChildWindowViewModel.Dispose"/>, subsequent changes to a model
    /// that outlives the disposed view model no longer raise <see cref="ChildWindowViewModel.FilesChanged"/>.
    /// </summary>
    [Fact]
    public void Dispose_ThenModelContentFilterChanges_DoesNotRaiseFilesChanged()
    {
        // Arrange
        var model = new ChildWindowModel();
        var sut = BuildSut();
        sut.LoadFromModel(model);

        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        // Act — dispose, then mutate the model that outlives the disposed ViewModel (the scenario
        // from the audit: ChildWindowModel is kept alive in WindowManager's state/snapshot lists
        // after the panel that owned this ViewModel is closed).
        sut.Dispose();
        model.ContentFilter = PanelContentFilter.HideAll;

        // Assert — the disposed ViewModel's handler must no longer run.
        Assert.False(filesChangedRaised);
    }

    /// <summary>
    /// Verifies that after <see cref="ChildWindowViewModel.Dispose"/>, subsequent changes to the
    /// model's view mode no longer update <see cref="ChildWindowViewModel.CurrentViewTemplate"/>.
    /// </summary>
    [Fact]
    public void Dispose_ThenModelViewModeChanges_DoesNotChangeCurrentViewTemplate()
    {
        // Arrange
        var model = new ChildWindowModel();
        var sut = BuildSut();
        sut.LoadFromModel(model);
        var templateBeforeDispose = sut.CurrentViewTemplate;

        // Act
        sut.Dispose();
        model.ViewMode = model.ViewMode == PanelViewMode.List ? PanelViewMode.Grid : PanelViewMode.List;

        // Assert — OnModelPropertyChanged (which would call ApplyViewMode) must not run post-Dispose.
        Assert.Same(templateBeforeDispose, sut.CurrentViewTemplate);
    }

    /// <summary>
    /// Verifies that re-binding via <see cref="ChildWindowViewModel.LoadFromModel"/> to a different
    /// model unsubscribes from the first model, so subsequent changes to the stale model no longer
    /// raise <see cref="ChildWindowViewModel.FilesChanged"/>.
    /// </summary>
    [Fact]
    public void LoadFromModel_CalledWithDifferentModel_StaleModelChangesNoLongerRaiseFilesChanged()
    {
        // Arrange — a panel re-bound to a different model (defensive scenario; the production caller
        // always constructs a fresh ViewModel per panel, but the Model setter must be safe either way).
        var firstModel = new ChildWindowModel();
        var secondModel = new ChildWindowModel();
        using var sut = BuildSut();

        sut.LoadFromModel(firstModel);
        sut.LoadFromModel(secondModel);

        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        // Act — mutate the stale, no-longer-bound first model.
        firstModel.ContentFilter = PanelContentFilter.HideAll;

        // Assert — must not still be subscribed to the first model.
        Assert.False(filesChangedRaised);
    }

    /// <summary>
    /// Verifies that after re-binding to a different model, the view model is subscribed to that model
    /// exactly once, so a subsequent change raises <see cref="ChildWindowViewModel.FilesChanged"/>
    /// exactly once.
    /// </summary>
    [Fact]
    public void LoadFromModel_CalledWithDifferentModel_CurrentModelChangeRaisesFilesChangedExactlyOnce()
    {
        // Arrange
        var firstModel = new ChildWindowModel();
        var secondModel = new ChildWindowModel();
        using var sut = BuildSut();

        sut.LoadFromModel(firstModel);
        sut.LoadFromModel(secondModel);

        var filesChangedCount = 0;
        sut.FilesChanged += (_, _) => filesChangedCount++;

        // Act
        secondModel.ContentFilter = PanelContentFilter.HideAll;

        // Assert — exactly one subscription on the current model, never two.
        Assert.Equal(1, filesChangedCount);
    }

    /// <summary>
    /// Regression guard (code-quality review, Phase 7 Block D follow-up, MAJOR-6): verifies that
    /// re-binding to a different model replaces the file set rather than accumulating onto whatever was
    /// already loaded.
    /// </summary>
    [Fact]
    public void LoadFromModel_CalledWithDifferentModel_DropsFirstModelsFiles()
    {
        // Regression guard (code-quality review, Phase 7 Block D follow-up, MAJOR-6): re-binding to a
        // different model must REPLACE the file set, not accumulate onto whatever was already loaded.
        // LoadFromModel previously called AddFiles (additive) instead of ReplaceAll, so a second call
        // left the panel showing the union of both models' files — and SyncToModel would then persist
        // that union back onto the second model's FilePaths.
        var firstModel = new ChildWindowModel { FilePaths = [@"C:\fake\first.txt"] };
        var secondModel = new ChildWindowModel { FilePaths = [@"C:\fake\second.txt"] };
        using var sut = BuildSut();

        sut.LoadFromModel(firstModel);
        sut.LoadFromModel(secondModel);

        var item = Assert.Single(sut.AllItems);
        Assert.Equal(@"C:\fake\second.txt", item.Path);
    }

    // ── ADR-009 Amendment 2026-09-22 (P08): stale-path self-heal on LoadFromModel ───────────────

    /// <summary>
    /// Regression for P00 baseline finding F-9/H2: verifies that when a stale path has a live managed
    /// copy known to <see cref="IPanelItemStorageService.TryFindManagedPath"/>,
    /// <see cref="ChildWindowViewModel.LoadFromModel"/> resolves the item to that managed path instead
    /// of building a permanently-broken <see cref="FileItem"/>.
    /// </summary>
    [Fact]
    public void LoadFromModel_StalePathWithLiveManagedCopy_ResolvesToManagedPath()
    {
        // Regression for P00 baseline finding F-9/H2: 20 of 26 real-world panel items pointed at a
        // pre-Move original desktop path instead of their managed copy. When the storage service's
        // manifest still knows the managed copy, LoadFromModel must resolve to it instead of building
        // a permanently-broken FileItem.
        var stalePath = @"C:\fake\moved-away.lnk";
        var managedPath = NewTempFilePath();
        File.WriteAllText(managedPath, "managed content");

        var outManagedPath = managedPath;
        _storageMock.Setup(s => s.TryFindManagedPath(stalePath, out outManagedPath)).Returns(true);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { FilePaths = [stalePath] });

            var item = Assert.Single(sut.AllItems);
            Assert.Equal(managedPath, item.Path);
            Assert.True(item.Exists);
        }
        finally
        {
            File.Delete(managedPath);
        }
    }

    /// <summary>
    /// Verifies that when a stale path has no live managed copy, <see cref="ChildWindowViewModel.LoadFromModel"/>
    /// leaves the item exactly as it was — still shown, still marked non-existent — never dropped or
    /// silently replaced.
    /// </summary>
    [Fact]
    public void LoadFromModel_StalePathWithNoManagedCopy_KeepsOriginalPath()
    {
        // No manifest entry (or manifest entry with no live managed copy) — the item must stay
        // exactly as it was (still shown, still greyed out via Exists == false), never dropped or
        // silently replaced with something invented.
        var stalePath = @"C:\fake\never-moved.lnk";
        var outManagedPath = (string?)null;
        _storageMock.Setup(s => s.TryFindManagedPath(stalePath, out outManagedPath)).Returns(false);

        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { FilePaths = [stalePath] });

        var item = Assert.Single(sut.AllItems);
        Assert.Equal(stalePath, item.Path);
        Assert.False(item.Exists);
    }

    /// <summary>
    /// CRITICAL C1 fix, part (b) (P08 fix cycle 1, 2026-09-22): verifies that a stale-path remap is
    /// written back to <see cref="ChildWindowModel.FilePaths"/> immediately in memory, and that
    /// <see cref="ChildWindowViewModel.FilesChanged"/> fires so the fix survives a crash before any
    /// later save.
    /// </summary>
    [Fact]
    public void LoadFromModel_StalePathWithLiveManagedCopy_WritesRemapBackToModelFilePathsImmediately()
    {
        // CRITICAL C1 fix, part (b) (P08 fix cycle 1, 2026-09-22): the remap must be visible on
        // Model.FilePaths right away, in memory — not only after some unrelated future SyncToModel()
        // call — or code that reads Model.FilePaths directly in between (e.g.
        // MainWindowViewModel.RemovePanel's "does this panel have anything to lose" check) still sees
        // the stale, pre-remap path.
        var stalePath = @"C:\fake\moved-away.lnk";
        var managedPath = NewTempFilePath();
        File.WriteAllText(managedPath, "managed content");

        var outManagedPath = managedPath;
        _storageMock.Setup(s => s.TryFindManagedPath(stalePath, out outManagedPath)).Returns(true);

        var model = new ChildWindowModel { FilePaths = [stalePath] };
        using var sut = BuildSut();

        var filesChangedCount = 0;
        sut.FilesChanged += (_, _) => filesChangedCount++;

        try
        {
            sut.LoadFromModel(model);

            // Assert — already fixed up on the model by the time LoadFromModel returns, and a
            // (debounced) save was requested via FilesChanged so it survives a crash before any
            // unrelated future save trigger fires.
            Assert.Equal(managedPath, Assert.Single(model.FilePaths));
            Assert.Equal(1, filesChangedCount);
        }
        finally
        {
            File.Delete(managedPath);
        }
    }

    /// <summary>
    /// Verifies that when no remap happens (no live managed copy), <see cref="ChildWindowModel.FilePaths"/>
    /// is left untouched and no save is requested for the no-op.
    /// </summary>
    [Fact]
    public void LoadFromModel_StalePathWithNoManagedCopy_DoesNotMutateModelFilePathsOrRaiseFilesChanged()
    {
        // No remap happened (no live managed copy) — Model.FilePaths must be left exactly as it was,
        // and no save should be requested for a no-op.
        var stalePath = @"C:\fake\never-moved.lnk";
        var outManagedPath = (string?)null;
        _storageMock.Setup(s => s.TryFindManagedPath(stalePath, out outManagedPath)).Returns(false);

        var model = new ChildWindowModel { FilePaths = [stalePath] };
        using var sut = BuildSut();

        var filesChangedRaised = false;
        sut.LoadFromModel(model);
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        Assert.Equal(stalePath, Assert.Single(model.FilePaths));
        Assert.False(filesChangedRaised);
    }

    /// <summary>
    /// Verifies that the read-time stale-path remap never consults
    /// <see cref="IPanelItemStorageService.TryFindManagedPath"/> for a path that already exists on disk.
    /// </summary>
    [Fact]
    public void LoadFromModel_PathAlreadyExists_NeverConsultsStorageForResolution()
    {
        // The read-time remap only ever looks at paths that are already missing — an existing item
        // must never pay the manifest-scan cost, and must never be "resolved" away from its own path.
        var existingPath = NewTempFilePath();
        File.WriteAllText(existingPath, "content");

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { FilePaths = [existingPath] });

            var item = Assert.Single(sut.AllItems);
            Assert.Equal(existingPath, item.Path);
            _storageMock.Verify(
                s => s.TryFindManagedPath(It.IsAny<string>(), out It.Ref<string?>.IsAny),
                Times.Never);
        }
        finally
        {
            File.Delete(existingPath);
        }
    }

    // ── P08: RefreshItemsLiveness re-resolves the icon when a missing file reappears ────────────

    /// <summary>
    /// Regression for P00 baseline finding H3: verifies that when a missing file reappears,
    /// <see cref="BaseViewModel.RefreshItemsLiveness"/> re-resolves its icon via
    /// <see cref="IIconProvider.GetIcon(string, int)"/> instead of leaving it stuck on the placeholder.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_ItemReappears_ReResolvesIconViaProvider()
    {
        // Regression for P00 baseline finding H3: IconProvider never caches its placeholder for a
        // missing path, but nothing previously called GetIcon again once the file came back — the
        // item stayed permanently stuck on the placeholder even after Exists flipped back to true.
        var path = NewTempFilePath(); // does not exist yet

        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { FilePaths = [path] });

        var item = Assert.Single(sut.AllItems);
        Assert.False(item.Exists);

        try
        {
            File.WriteAllText(path, "now exists");

            var freshIcon = new System.Windows.Media.DrawingImage();
            _iconProviderMock.Setup(p => p.GetIcon(path, It.IsAny<int>())).Returns(freshIcon);

            sut.RefreshItemsLiveness();

            Assert.True(item.Exists);
            Assert.Same(freshIcon, item.Icon);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that <see cref="BaseViewModel.RefreshItemsLiveness"/> does not re-call
    /// <see cref="IIconProvider.GetIcon(string, int)"/> for an item that stays missing across ticks.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_ItemStaysMissing_NeverCallsIconProviderAgain()
    {
        // No transition (still missing) — no reason to re-hit the icon provider at all.
        var path = @"C:\fake\still-missing.lnk";

        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { FilePaths = [path] });
        _iconProviderMock.Invocations.Clear();

        sut.RefreshItemsLiveness();

        var item = Assert.Single(sut.AllItems);
        Assert.False(item.Exists);
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="BaseViewModel.RefreshItemsLiveness"/> does not re-call
    /// <see cref="IIconProvider.GetIcon(string, int)"/> for an item that stays present across ticks.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_ItemStaysPresent_NeverCallsIconProviderAgain()
    {
        // An item that was already present and stays present must not pay a repeated GetIcon call on
        // every liveness tick — see BaseViewModel.RefreshItemsLiveness remarks on the responsiveness
        // trade-off (P00 baseline: ~2.8ms/item synchronous on the UI thread).
        var path = NewTempFilePath();
        File.WriteAllText(path, "content");

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { FilePaths = [path] });
            _iconProviderMock.Invocations.Clear();

            sut.RefreshItemsLiveness();

            var item = Assert.Single(sut.AllItems);
            Assert.True(item.Exists);
            _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── M3 (P08 fix cycle 1, 2026-09-22): bounded placeholder retry for an item that Exists but is
    // still showing the icon pipeline's own placeholder (a transient SHGetFileInfo failure, e.g.
    // during autostart before the shell has finished initializing) ─────────────────────────────────

    /// <summary>
    /// M3 regression (P08 fix cycle 1, 2026-09-22): verifies that an item which exists but still shows
    /// the icon pipeline's placeholder (a transient <c>SHGetFileInfo</c> failure) is retried with backoff
    /// by <see cref="BaseViewModel.RefreshItemsLiveness"/> until it resolves the real icon.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_ItemExistsButIconIsPlaceholder_RetriesWithBackoff_ThenResolvesRealIcon()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "content");
        var placeholder = new System.Windows.Media.DrawingImage();
        var realIcon = new System.Windows.Media.DrawingImage();

        _iconProviderMock.Setup(p => p.IsPlaceholder(placeholder)).Returns(true);
        _iconProviderMock.Setup(p => p.IsPlaceholder(realIcon)).Returns(false);
        _iconProviderMock.Setup(p => p.GetIcon(path, It.IsAny<int>())).Returns(placeholder);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { FilePaths = [path] });
            var item = Assert.Single(sut.AllItems);
            Assert.Same(placeholder, item.Icon);
            _iconProviderMock.Invocations.Clear();

            // Tick 1: first retry attempt — provider still returns the placeholder.
            sut.RefreshItemsLiveness();
            _iconProviderMock.Verify(p => p.GetIcon(path, It.IsAny<int>()), Times.Once);
            Assert.Same(placeholder, item.Icon);

            // Tick 2: backoff — must NOT re-hit the provider on every single tick.
            sut.RefreshItemsLiveness();
            _iconProviderMock.Verify(p => p.GetIcon(path, It.IsAny<int>()), Times.Once);

            // Tick 3: backoff elapsed — second attempt; the provider now succeeds.
            _iconProviderMock.Setup(p => p.GetIcon(path, It.IsAny<int>())).Returns(realIcon);
            sut.RefreshItemsLiveness();

            _iconProviderMock.Verify(p => p.GetIcon(path, It.IsAny<int>()), Times.Exactly(2));
            Assert.Same(realIcon, item.Icon);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that when the icon placeholder never resolves,
    /// <see cref="BaseViewModel.RefreshItemsLiveness"/> stops retrying after three attempts
    /// rather than hitting the icon provider on every future tick.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_PlaceholderPersistsForever_StopsRetryingAfterThreeAttempts()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "content");
        var placeholder = new System.Windows.Media.DrawingImage();

        _iconProviderMock.Setup(p => p.IsPlaceholder(placeholder)).Returns(true);
        _iconProviderMock.Setup(p => p.GetIcon(path, It.IsAny<int>())).Returns(placeholder);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { FilePaths = [path] });
            _iconProviderMock.Invocations.Clear();

            // 1-then-2-tick backoff schedule: attempts land on ticks 1, 3, and 6.
            for (var tick = 0; tick < 6; tick++)
            {
                sut.RefreshItemsLiveness();
            }

            _iconProviderMock.Verify(p => p.GetIcon(path, It.IsAny<int>()), Times.Exactly(3));

            // Bounded, not merely "slow": further ticks never attempt again.
            sut.RefreshItemsLiveness();
            sut.RefreshItemsLiveness();
            _iconProviderMock.Verify(p => p.GetIcon(path, It.IsAny<int>()), Times.Exactly(3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── A6: SelectedFile survives a ReplaceFileItems rebuild (ADR-012 Reset side effect) ───────────

    /// <summary>
    /// Verifies that when a content filter change triggers a rebuild but the selected file is still in
    /// the new filtered set, <see cref="BaseViewModel.SelectedFile"/> remains the same instance.
    /// </summary>
    [Fact]
    public void ContentFilterChange_SelectedFileStillInNewSet_RemainsSelected()
    {
        var pathA = @"C:\fake\A.txt";
        var pathB = @"C:\fake\B.lnk";
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.AddFiles([pathA, pathB]);

        var itemA = sut.FileItems.Single(f => f.Path == pathA);
        sut.SelectedFile = itemA;

        // HideShortcuts drops B.lnk but keeps A.txt — the rebuild this triggers must not silently
        // clear the selection of an item that's still present in the new (filtered) set.
        sut.Model.ContentFilter = PanelContentFilter.HideShortcuts;

        Assert.NotNull(sut.SelectedFile);
        Assert.Same(itemA, sut.SelectedFile);
    }

    /// <summary>
    /// Verifies that selection restoration after a rebuild matches the selected file's path
    /// case-insensitively, per <c>ReplaceFileItems</c>'s comparison rule.
    /// </summary>
    [Fact]
    public void ContentFilterChange_SelectedFileMatchedCaseInsensitively()
    {
        var pathA = @"C:\fake\A.txt";
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.AddFiles([pathA]);

        var itemA = sut.FileItems.Single(f => f.Path == pathA);
        sut.SelectedFile = itemA;

        // Trigger a rebuild that keeps the same (ShowAll) filter — still goes through
        // BaseViewModel.ReplaceFileItems's case-insensitive path match.
        sut.AddFiles([@"C:\fake\B.txt"]);

        Assert.NotNull(sut.SelectedFile);
        Assert.Equal(pathA, sut.SelectedFile!.Path, ignoreCase: true);
    }

    /// <summary>
    /// Verifies that when a content filter change removes the selected file from the new filtered set,
    /// <see cref="BaseViewModel.SelectedFile"/> is cleared to <c>null</c>.
    /// </summary>
    [Fact]
    public void ContentFilterChange_SelectedFileNoLongerInNewSet_ClearsSelection()
    {
        var pathA = @"C:\fake\A.txt";
        var pathB = @"C:\fake\B.lnk";
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.AddFiles([pathA, pathB]);

        var itemB = sut.FileItems.Single(f => f.Path == pathB);
        sut.SelectedFile = itemB;

        // HideShortcuts drops B.lnk (the currently selected item) from the new set entirely.
        sut.Model.ContentFilter = PanelContentFilter.HideShortcuts;

        Assert.Null(sut.SelectedFile);
    }

    // ── C3: BeginRenameCommand / CommitRenameCommand / CancelRenameCommand ──────────────────────

    /// <summary>
    /// Verifies that executing <see cref="ChildWindowViewModel.BeginRenameCommand"/> with an explicit
    /// item sets <see cref="ChildWindowViewModel.EditingItem"/> and pre-fills
    /// <see cref="ChildWindowViewModel.EditingName"/> from its display name.
    /// </summary>
    [Fact]
    public void BeginRenameCommand_SetsEditingItemAndEditingName()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);

            Assert.Same(item, sut.EditingItem);
            Assert.Equal(item.DisplayName, sut.EditingName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that executing <see cref="ChildWindowViewModel.BeginRenameCommand"/> with a <c>null</c>
    /// parameter falls back to editing <see cref="BaseViewModel.SelectedFile"/>.
    /// </summary>
    [Fact]
    public void BeginRenameCommand_NullParameter_FallsBackToSelectedFile()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);
            sut.SelectedFile = item;

            sut.BeginRenameCommand.Execute(null);

            Assert.Same(item, sut.EditingItem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a successful commit relocates the item to its new path, raises
    /// <see cref="ChildWindowViewModel.FilesChanged"/>, and closes the rename editor.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_Success_RelocatesItemRaisesFilesChangedAndClosesEditor()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        var newPath = Path.Combine(Path.GetDirectoryName(path)!, "Renamed.txt");
        _renameServiceMock.Setup(r => r.Rename(path, "Renamed.txt"))
            .Returns(new RenameResult(RenameOutcome.Renamed, newPath));

        using var sut = BuildSut();
        var filesChangedCount = 0;
        sut.FilesChanged += (_, _) => filesChangedCount++;

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "Renamed.txt";
            sut.CommitRenameCommand.Execute(null);

            Assert.Null(sut.EditingItem);
            Assert.Null(sut.RenameError);
            Assert.Equal(newPath, item.Path);
            Assert.Equal(1, filesChangedCount);
            _renameServiceMock.Verify(r => r.Rename(path, "Renamed.txt"), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that committing a blank (whitespace-only) name closes the editor without invoking
    /// <see cref="IFileItemRenameService.Rename"/> or touching the item.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_EmptyName_ClosesEditorWithoutCallingRenameService()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        using var sut = BuildSut();
        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "   ";
            sut.CommitRenameCommand.Execute(null);

            Assert.Null(sut.EditingItem);
            Assert.False(filesChangedRaised);
            Assert.Equal(path, item.Path); // untouched
            _renameServiceMock.Verify(r => r.Rename(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that committing a name identical to the pre-filled value closes the editor without
    /// invoking <see cref="IFileItemRenameService.Rename"/>.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_NameUnchanged_ClosesEditorWithoutCallingRenameService()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        using var sut = BuildSut();
        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = item.DisplayName; // no change from the pre-filled value
            sut.CommitRenameCommand.Execute(null);

            Assert.Null(sut.EditingItem);
            Assert.False(filesChangedRaised);
            _renameServiceMock.Verify(r => r.Rename(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// On a failed rename, <see cref="ChildWindowViewModel.RenameError"/> is set and the file
    /// collection is left completely untouched. Per the actual implementation (not just the
    /// "expected" editor-closes-on-any-outcome assumption), <see cref="ChildWindowViewModel.EditingItem"/>
    /// is deliberately left set to the item being edited — see <c>CommitRename</c>'s doc comment
    /// ("Keep the editor open so the user can correct the name.").
    /// </summary>
    /// <param name="outcome">The failed <see cref="RenameOutcome"/> the rename service reports.</param>
    [Theory]
    [InlineData(RenameOutcome.NameTaken)]
    [InlineData(RenameOutcome.InvalidName)]
    [InlineData(RenameOutcome.Failed)]
    public void CommitRenameCommand_Failure_SetsRenameErrorKeepsEditorOpenAndLeavesCollectionUntouched(RenameOutcome outcome)
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        _renameServiceMock.Setup(r => r.Rename(path, "Bad Name.txt"))
            .Returns(new RenameResult(outcome, null));

        using var sut = BuildSut();
        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "Bad Name.txt";
            sut.CommitRenameCommand.Execute(null);

            Assert.Equal(outcome, sut.RenameError);
            Assert.Same(item, sut.EditingItem); // editor stays open on failure
            Assert.Equal(path, item.Path); // untouched
            Assert.False(filesChangedRaised);
            var stillTracked = Assert.Single(sut.AllItems);
            Assert.Same(item, stillTracked);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that executing <see cref="ChildWindowViewModel.CancelRenameCommand"/> closes the editor
    /// and leaves the item and the file system untouched.
    /// </summary>
    [Fact]
    public void CancelRenameCommand_ClosesEditorWithoutTouchingFileSystem()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "SomeNewName.txt";
            sut.CancelRenameCommand.Execute(null);

            Assert.Null(sut.EditingItem);
            Assert.Equal(path, item.Path);
            _renameServiceMock.Verify(r => r.Rename(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── R10/C3: rename in a Folder Portal panel is allowed ──────────────────────────────────────

    /// <summary>
    /// Verifies that in a Folder Portal panel <see cref="ChildWindowViewModel.BeginRenameCommand"/> and
    /// <see cref="ChildWindowViewModel.CommitRenameCommand"/> can always execute.
    /// </summary>
    [Fact]
    public void BeginRenameAndCommitRename_AlwaysCanExecute_InPortal()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            Assert.True(sut.IsPortal);

            Assert.True(sut.BeginRenameCommand.CanExecute(null));
            Assert.True(sut.CommitRenameCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that committing a rename for an item inside a Folder Portal panel succeeds and relocates
    /// the item to its new path.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_InPortalPanel_RenamesSuccessfully()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        var itemPath = Path.Combine(portalDir, "Item.txt");
        File.WriteAllText(itemPath, "portal-content");
        var newPath = Path.Combine(portalDir, "Renamed.txt");
        _renameServiceMock.Setup(r => r.Rename(itemPath, "Renamed.txt"))
            .Returns(new RenameResult(RenameOutcome.Renamed, newPath));

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            Assert.True(sut.IsPortal);

            var item = sut.FileItems.Single(f => f.Path == itemPath);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "Renamed.txt";
            sut.CommitRenameCommand.Execute(null);

            Assert.Equal(newPath, item.Path);
            Assert.Null(sut.EditingItem);
            _renameServiceMock.Verify(r => r.Rename(itemPath, "Renamed.txt"), Times.Once);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── .lnk display/rename parity: DisplayName hides .lnk, CommitRename re-attaches it ─────────

    /// <summary>
    /// <see cref="FileItem.DisplayName"/> hides a shortcut's <c>.lnk"</c>, so the private
    /// <c>BeginRename</c> method — which pre-fills <see cref="ChildWindowViewModel.EditingName"/> from
    /// it — must not show the extension either.
    /// </summary>
    [Fact]
    public void BeginRenameCommand_Shortcut_PreFillsEditingNameWithoutLnkExtension()
    {
        var path = NewTempFilePath().Replace(".txt", ".lnk", StringComparison.Ordinal);
        File.WriteAllText(path, "x");
        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);
            Assert.True(item.IsShortcut);

            sut.BeginRenameCommand.Execute(item);

            Assert.Equal(item.DisplayName, sut.EditingName);
            Assert.DoesNotContain(".lnk", sut.EditingName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// CRITICAL regression guard: <see cref="ChildWindowViewModel.EditingName"/> for a shortcut never
    /// includes <c>.lnk</c> (see <see cref="FileItem.DisplayName"/>), so the private <c>CommitRename</c>
    /// method must re-attach <c>.lnk</c> before calling
    /// <see cref="IFileItemRenameService.Rename"/> — otherwise committing a rename with an unchanged
    /// extension-less name would physically strip <c>.lnk</c> from the file on disk and break the
    /// shortcut. This asserts the exact string passed to the rename service ends with <c>.lnk</c>.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_Shortcut_ReattachesLnkExtensionBeforeCallingRenameService()
    {
        var path = NewTempFilePath().Replace(".txt", ".lnk", StringComparison.Ordinal);
        File.WriteAllText(path, "x");
        var newPath = Path.Combine(Path.GetDirectoryName(path)!, "NewChrome.lnk");
        _renameServiceMock.Setup(r => r.Rename(path, "NewChrome.lnk"))
            .Returns(new RenameResult(RenameOutcome.Renamed, newPath));

        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);
            Assert.True(item.IsShortcut);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "NewChrome"; // no .lnk typed — matches what the user actually sees/edits
            sut.CommitRenameCommand.Execute(null);

            Assert.Null(sut.RenameError);
            Assert.Equal(newPath, item.Path);
            _renameServiceMock.Verify(r => r.Rename(path, "NewChrome.lnk"), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// If the user explicitly types <c>.lnk</c> themselves, the private <c>CommitRename</c> method
    /// must not double it up into <c>NewChrome.lnk.lnk</c>.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_Shortcut_UserTypedLnkExplicitly_DoesNotDoubleExtension()
    {
        var path = NewTempFilePath().Replace(".txt", ".lnk", StringComparison.Ordinal);
        File.WriteAllText(path, "x");
        var newPath = Path.Combine(Path.GetDirectoryName(path)!, "NewChrome.lnk");
        _renameServiceMock.Setup(r => r.Rename(path, "NewChrome.lnk"))
            .Returns(new RenameResult(RenameOutcome.Renamed, newPath));

        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "NewChrome.lnk"; // user typed the extension explicitly
            sut.CommitRenameCommand.Execute(null);

            _renameServiceMock.Verify(r => r.Rename(path, "NewChrome.lnk"), Times.Once);
            _renameServiceMock.Verify(r => r.Rename(It.IsAny<string>(), "NewChrome.lnk.lnk"), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A non-shortcut rename is unaffected by the shortcut-extension re-attach logic — the name passed
    /// to the rename service is exactly what the user typed, with no extension appended.
    /// </summary>
    [Fact]
    public void CommitRenameCommand_NonShortcut_DoesNotGainLnkExtension()
    {
        var path = NewTempFilePath();
        File.WriteAllText(path, "x");
        var newPath = Path.Combine(Path.GetDirectoryName(path)!, "Renamed.txt");
        _renameServiceMock.Setup(r => r.Rename(path, "Renamed.txt"))
            .Returns(new RenameResult(RenameOutcome.Renamed, newPath));

        using var sut = BuildSut();

        try
        {
            sut.AddFiles([path]);
            var item = Assert.Single(sut.AllItems);
            Assert.False(item.IsShortcut);

            sut.BeginRenameCommand.Execute(item);
            sut.EditingName = "Renamed.txt";
            sut.CommitRenameCommand.Execute(null);

            _renameServiceMock.Verify(r => r.Rename(path, "Renamed.txt"), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── ADR-014 Decision 2/3: SortMode.Name projection ──────────────────────────────────────────

    /// <summary>
    /// Verifies that switching to <see cref="PanelSortMode.Name"/> sorts
    /// <see cref="BaseViewModel.FileItems"/> by name while leaving the persisted manual order in
    /// <see cref="BaseViewModel.AllItems"/> untouched (ADR-014 Decision 2).
    /// </summary>
    [Fact]
    public void SortModeName_SortsFileItems_ButLeavesAllItemsUnsorted()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.AddFiles([@"C:\fake\file10.txt", @"C:\fake\file2.txt", @"C:\fake\file1.txt"]);

        var originalAllItemsOrder = sut.AllItems.Select(f => f.Path).ToList();

        sut.Model.SortMode = PanelSortMode.Name;

        Assert.Equal(
            [@"C:\fake\file1.txt", @"C:\fake\file2.txt", @"C:\fake\file10.txt"],
            sut.FileItems.Select(f => f.Path));

        // AllItems (the persisted manual order) must remain untouched by the Name projection — it's
        // non-mutating (ADR-014 Decision 2).
        Assert.Equal(originalAllItemsOrder, sut.AllItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that switching from <see cref="PanelSortMode.Name"/> back to
    /// <see cref="PanelSortMode.Manual"/> restores the exact prior manual order of
    /// <see cref="BaseViewModel.FileItems"/>.
    /// </summary>
    [Fact]
    public void SortModeName_ThenBackToManual_RestoresExactPriorFileItemsOrder()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.AddFiles([@"C:\fake\file10.txt", @"C:\fake\file2.txt", @"C:\fake\file1.txt"]);

        var originalOrder = sut.FileItems.Select(f => f.Path).ToList();

        sut.Model.SortMode = PanelSortMode.Name;
        sut.Model.SortMode = PanelSortMode.Manual;

        Assert.Equal(originalOrder, sut.FileItems.Select(f => f.Path));
    }

    // ── ADR-014: SortMode.Name × ContentFilter interplay ─────────────────────────────────────────

    /// <summary>
    /// Verifies that under <see cref="PanelSortMode.Name"/>, an active
    /// <see cref="PanelContentFilter.HideShortcuts"/> filter sorts only the filtered (non-shortcut)
    /// subset.
    /// </summary>
    [Fact]
    public void SortModeName_WithHideShortcutsFilter_SortsTheFilteredSubset()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
        sut.AddFiles([@"C:\fake\file10.txt", @"C:\fake\file2.txt", @"C:\fake\shortcut.lnk"]);

        sut.Model.ContentFilter = PanelContentFilter.HideShortcuts;

        Assert.Equal(
            [@"C:\fake\file2.txt", @"C:\fake\file10.txt"],
            sut.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that under <see cref="PanelSortMode.Name"/>, an active
    /// <see cref="PanelContentFilter.FoldersOnly"/> filter sorts the visible folders using natural
    /// (case-insensitive alphabetical) order.
    /// </summary>
    [Fact]
    public void SortModeName_WithFoldersOnlyFilter_SortsVisibleFoldersNaturally()
    {
        var parent = NewTempDirPath();
        var folderZebra = Path.Combine(parent, "Zebra");
        var folderApple = Path.Combine(parent, "Apple");
        Directory.CreateDirectory(folderZebra);
        Directory.CreateDirectory(folderApple);
        var filePath = Path.Combine(parent, "file.txt");
        File.WriteAllText(filePath, "x");

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
            sut.AddFiles([folderZebra, folderApple, filePath]);

            sut.Model.ContentFilter = PanelContentFilter.FoldersOnly;

            Assert.Equal([folderApple, folderZebra], sut.FileItems.Select(f => f.Path));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that under <see cref="PanelSortMode.Name"/>, an active
    /// <see cref="PanelContentFilter.HideAll"/> filter results in an empty
    /// <see cref="BaseViewModel.FileItems"/> list without throwing.
    /// </summary>
    [Fact]
    public void SortModeName_WithHideAllFilter_ResultsInEmptyListWithoutExceptions()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
        sut.AddFiles([@"C:\fake\a.txt", @"C:\fake\b.txt"]);

        sut.Model.ContentFilter = PanelContentFilter.HideAll;

        Assert.Empty(sut.FileItems);
    }

    // ── ADR-014: AddFiles while sorted by name ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that adding a new file while <see cref="PanelSortMode.Name"/> is active inserts it at
    /// its alphabetical position in <see cref="BaseViewModel.FileItems"/>, rather than appending
    /// it at the end.
    /// </summary>
    [Fact]
    public void AddFiles_WhileSortModeIsName_NewItemInsertedAtAlphabeticalPosition_NotAppended()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
        sut.AddFiles([@"C:\fake\a.txt", @"C:\fake\z.txt"]);

        sut.AddFiles([@"C:\fake\m.txt"]);

        Assert.Equal(
            [@"C:\fake\a.txt", @"C:\fake\m.txt", @"C:\fake\z.txt"],
            sut.FileItems.Select(f => f.Path));
    }

    // ── ADR-014 Decision 6: ReorderItem auto-switches Name -> Manual ─────────────────────────────

    /// <summary>
    /// Verifies that dragging an item while <see cref="PanelSortMode.Name"/> is active switches
    /// <see cref="ChildWindowModel.SortMode"/> to <see cref="PanelSortMode.Manual"/> and materializes
    /// the currently-sorted visible order into <see cref="BaseViewModel.AllItems"/>, with the
    /// dragged item placed at its drop position.
    /// </summary>
    [Fact]
    public void ReorderItem_FromNameMode_SwitchesToManual_MaterializesOrderWithMovedItemInPlace()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
        sut.AddFiles([@"C:\fake\b.txt", @"C:\fake\a.txt", @"C:\fake\c.txt"]);

        // Sorted visible order at this point: a, b, c.
        Assert.Equal(
            [@"C:\fake\a.txt", @"C:\fake\b.txt", @"C:\fake\c.txt"],
            sut.FileItems.Select(f => f.Path));

        var itemC = sut.FileItems.Single(f => f.Path == @"C:\fake\c.txt");

        // Drop c at the very front (gap 0).
        sut.ReorderItem(itemC, 0);

        Assert.Equal(PanelSortMode.Manual, sut.Model.SortMode);
        Assert.Equal(
            [@"C:\fake\c.txt", @"C:\fake\a.txt", @"C:\fake\b.txt"],
            sut.AllItems.Select(f => f.Path));
        Assert.Equal(
            [@"C:\fake\c.txt", @"C:\fake\a.txt", @"C:\fake\b.txt"],
            sut.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// ADR-014 Decision 6/8: verifies that the Name-to-Manual sort mode switch and the actual item move
    /// triggered by a single drag-drop gesture surface as exactly one
    /// <see cref="ChildWindowViewModel.FilesChanged"/> notification, not two.
    /// </summary>
    [Fact]
    public void ReorderItem_FromNameMode_RealMove_RaisesFilesChangedExactlyOnce()
    {
        // ADR-014 Decision 6/8: the SortMode -> Manual switch and the actual item move are one
        // logical user action (a single drag-drop gesture) and must surface as a single FilesChanged
        // notification — not one from the SortMode property change and a second from the move.
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
        sut.AddFiles([@"C:\fake\b.txt", @"C:\fake\a.txt", @"C:\fake\c.txt"]);

        var itemC = sut.FileItems.Single(f => f.Path == @"C:\fake\c.txt");

        var filesChangedCount = 0;
        sut.FilesChanged += (_, _) => filesChangedCount++;

        sut.ReorderItem(itemC, 0);

        Assert.Equal(1, filesChangedCount);
    }

    /// <summary>
    /// Verifies that even when the drop gap is a no-op for the underlying item move, the Name-to-Manual
    /// sort mode switch is itself a real, persistable state change that still raises
    /// <see cref="ChildWindowViewModel.FilesChanged"/> exactly once.
    /// </summary>
    [Fact]
    public void ReorderItem_FromNameMode_NoOpDrop_StillSwitchesToManual_RaisesFilesChangedExactlyOnce()
    {
        // Even when the drop gap is a no-op for MoveItem (dropping an item back where it already
        // visually is), the Name -> Manual switch itself is a real, persistable state change and must
        // still raise FilesChanged exactly once.
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SortMode = PanelSortMode.Name });
        sut.AddFiles([@"C:\fake\a.txt", @"C:\fake\b.txt"]);

        var itemA = sut.FileItems.Single(f => f.Path == @"C:\fake\a.txt"); // vFrom == 0

        var filesChangedCount = 0;
        sut.FilesChanged += (_, _) => filesChangedCount++;

        sut.ReorderItem(itemA, 0); // gap == vFrom -> MoveItem no-op

        Assert.Equal(PanelSortMode.Manual, sut.Model.SortMode);
        Assert.Equal(1, filesChangedCount);
    }

    // ── ReorderItem while already Manual ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that reordering an item while already in <see cref="PanelSortMode.Manual"/> leaves the
    /// sort mode unchanged and raises <see cref="ChildWindowViewModel.FilesChanged"/> exactly once for
    /// a real move.
    /// </summary>
    [Fact]
    public void ReorderItem_InManualMode_DoesNotChangeSortMode_RaisesFilesChangedOnceOnRealMove()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel()); // Manual by default
        sut.AddFiles([@"C:\fake\a.txt", @"C:\fake\b.txt", @"C:\fake\c.txt"]);

        var itemA = sut.FileItems.Single(f => f.Path == @"C:\fake\a.txt");

        var filesChangedCount = 0;
        sut.FilesChanged += (_, _) => filesChangedCount++;

        sut.ReorderItem(itemA, 3); // move to the end

        Assert.Equal(PanelSortMode.Manual, sut.Model.SortMode);
        Assert.Equal(1, filesChangedCount);
        Assert.Equal(
            [@"C:\fake\b.txt", @"C:\fake\c.txt", @"C:\fake\a.txt"],
            sut.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that reordering an item to a gap that is a no-op while already in
    /// <see cref="PanelSortMode.Manual"/> does not raise <see cref="ChildWindowViewModel.FilesChanged"/>.
    /// </summary>
    [Fact]
    public void ReorderItem_InManualMode_NoOpDrop_DoesNotRaiseFilesChanged()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.AddFiles([@"C:\fake\a.txt", @"C:\fake\b.txt"]);

        var itemA = sut.FileItems.Single(f => f.Path == @"C:\fake\a.txt"); // vFrom == 0

        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        sut.ReorderItem(itemA, 0); // gap immediately before its own position -> no-op

        Assert.False(filesChangedRaised);
    }

    // ── ADR-014 Decision 7: Folder Portal sort behavior ──────────────────────────────────────────

    /// <summary>
    /// Verifies that in a Folder Portal panel, <see cref="ChildWindowViewModel.EffectiveSortMode"/> is
    /// always forced to <see cref="PanelSortMode.Name"/>, even though the raw
    /// <see cref="ChildWindowModel.SortMode"/> stays <see cref="PanelSortMode.Manual"/>.
    /// </summary>
    [Fact]
    public void EffectiveSortMode_PortalPanel_AlwaysName_EvenWhenModelSortModeIsManual()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir, SortMode = PanelSortMode.Manual });

            Assert.True(sut.IsPortal);
            Assert.Equal(PanelSortMode.Manual, sut.Model.SortMode); // raw value left untouched
            Assert.Equal(PanelSortMode.Name, sut.EffectiveSortMode); // projection is forced to Name
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── ui-polishing-1 §4.3 (CRITICAL-2 risk): IsSortedByName must be raised together with
    //    EffectiveSortMode on every single path that changes what either projects to — verified here
    //    for all such paths found via `grep EffectiveSortMode` in ChildWindowViewModel.cs. ────────────

    /// <summary>
    /// Verifies that loading a model that puts the panel into Folder Portal mode raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for both
    /// <see cref="ChildWindowViewModel.EffectiveSortMode"/> and <see cref="ChildWindowViewModel.IsSortedByName"/>.
    /// </summary>
    [Fact]
    public void LoadFromModel_EntersPortal_RaisesBothEffectiveSortModeAndIsSortedByName()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var raised = new List<string>();
            sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir, SortMode = PanelSortMode.Manual });

            Assert.Contains(nameof(ChildWindowViewModel.EffectiveSortMode), raised);
            Assert.Contains(nameof(ChildWindowViewModel.IsSortedByName), raised);
            Assert.True(sut.IsSortedByName); // portal forces EffectiveSortMode to Name
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── settings-window-redesign.md D-4: Model.SourceFolderPath is now the single, directly
    //    assignable trigger for attaching/detaching a Folder Portal — the "Customize panel…" dialog
    //    (WP-B) only ever sets this property; ChildWindowViewModel.HandleSourceFolderPathChanged reacts
    //    to it via OnModelPropertyChanged. This replaces the previous SelectSourceFolder()/
    //    SelectSourceFolderCommand, which opened `Microsoft.Win32.OpenFolderDialog` inline with no
    //    injectable seam and was therefore never independently unit-testable — the property-driven
    //    reaction below has no such seam problem. ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that assigning <see cref="ChildWindowModel.SourceFolderPath"/> on an already-loaded
    /// (non-portal) panel attaches a watcher, immediately mirrors the folder's contents, and flips
    /// <see cref="ChildWindowViewModel.IsPortal"/> to <see langword="true"/>.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathAssigned_OnLoadedPanel_AttachesWatcherAndMirrorsFolder()
    {
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "a.txt"), "a");
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var model = new ChildWindowModel();
            sut.LoadFromModel(model);
            Assert.False(sut.IsPortal);

            var filesChangedRaised = false;
            sut.FilesChanged += (_, _) => filesChangedRaised = true;

            model.SourceFolderPath = portalDir;

            Assert.True(sut.IsPortal);
            Assert.True(filesChangedRaised);
            Assert.Equal(Path.Combine(portalDir, "a.txt"), Assert.Single(sut.FileItems).Path);
            watcherMock.Verify(w => w.Start(portalDir), Times.Once);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that clearing <see cref="ChildWindowModel.SourceFolderPath"/> back to <see langword="null"/>
    /// on a live Folder Portal panel detaches (disposes) the watcher and flips
    /// <see cref="ChildWindowViewModel.IsPortal"/> back to <see langword="false"/>.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathCleared_OnPortalPanel_DetachesWatcher()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var model = new ChildWindowModel { SourceFolderPath = portalDir };
            sut.LoadFromModel(model);
            Assert.True(sut.IsPortal);

            model.SourceFolderPath = null;

            Assert.False(sut.IsPortal);
            watcherMock.Verify(w => w.Dispose(), Times.Once);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies P-B4 (docs/PLANS/settings-window-redesign.md §3.2): a panel that had its own managed
    /// items before becoming a Folder Portal gets those items back — not the folder's mirrored
    /// contents — once <see cref="ChildWindowModel.SourceFolderPath"/> is cleared. Regression guard for
    /// the pre-dialog behavior, where clearing left the mirrored folder items in
    /// <see cref="BaseViewModel.FileItems"/>, so the next <c>SyncToModel()</c> silently overwrote
    /// <see cref="ChildWindowModel.FilePaths"/> with them and orphaned the panel's real items.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathCleared_OnPortalPanel_RestoresPanelsOwnFilePaths()
    {
        var ownedFile = NewTempFilePath();
        File.WriteAllText(ownedFile, "owned");
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "mirrored.txt"), "mirrored");
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var model = new ChildWindowModel { FilePaths = [ownedFile] };
            sut.LoadFromModel(model);
            Assert.Equal(ownedFile, Assert.Single(sut.FileItems).Path);

            model.SourceFolderPath = portalDir;
            Assert.Equal(Path.Combine(portalDir, "mirrored.txt"), Assert.Single(sut.FileItems).Path);

            model.SourceFolderPath = null;

            Assert.Equal(ownedFile, Assert.Single(sut.FileItems).Path);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
            File.Delete(ownedFile);
        }
    }

    /// <summary>
    /// Verifies that assigning <see cref="ChildWindowModel.SourceFolderPath"/> to enter a Folder Portal
    /// raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for both
    /// <see cref="ChildWindowViewModel.EffectiveSortMode"/> and <see cref="ChildWindowViewModel.IsSortedByName"/>
    /// — the same CRITICAL-2 contract already verified for <see cref="ChildWindowViewModel.LoadFromModel"/>
    /// above, now also covering the live property-change path.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathAssigned_RaisesBothEffectiveSortModeAndIsSortedByName()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var model = new ChildWindowModel();
            sut.LoadFromModel(model);

            var raised = new List<string>();
            sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            model.SourceFolderPath = portalDir;

            Assert.Contains(nameof(ChildWindowViewModel.EffectiveSortMode), raised);
            Assert.Contains(nameof(ChildWindowViewModel.IsSortedByName), raised);
            Assert.True(sut.IsSortedByName);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that assigning a non-existent folder path behaves like "no portal": no watcher is
    /// created and <see cref="ChildWindowViewModel.IsPortal"/> stays <see langword="false"/>.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathAssigned_PathDoesNotExist_DoesNotAttachWatcherOrBecomePortal()
    {
        using var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.LoadFromModel(model);

        model.SourceFolderPath = @"C:\this\path\does\not\exist\at\all";

        Assert.False(sut.IsPortal);
        _watcherFactoryMock.Verify(f => f.Create(), Times.Never);
    }

    // ── FIX round 2 MINOR-1: snapshot Model.FilePaths onto the pre-portal item set BEFORE the mirrored
    //    folder contents overwrite AllItems, so an item added since the last save is not silently
    //    dropped if SourceFolderPath is cleared again inside the same debounce window. ──────────────────

    /// <summary>
    /// Verifies that transitioning a panel from non-portal to portal snapshots
    /// <see cref="ChildWindowModel.FilePaths"/> onto the panel's CURRENT in-memory item set — including
    /// an item added live since <see cref="ChildWindowViewModel.LoadFromModel"/> that was never synced
    /// back via <c>ChildWindow.SyncToModel</c> — before the mirrored folder contents replace it.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathAssigned_FromNonPortal_SnapshotsCurrentItemsOntoFilePaths()
    {
        var ownedFile = NewTempFilePath();
        File.WriteAllText(ownedFile, "owned");
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "mirrored.txt"), "mirrored");
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var model = new ChildWindowModel(); // FilePaths starts empty.
            sut.LoadFromModel(model);

            // An item added live (e.g. drag-drop) after load — nothing has synced it back to
            // Model.FilePaths yet, since that only happens on window close or a debounced save.
            sut.AddFiles([ownedFile]);
            Assert.Empty(model.FilePaths);

            model.SourceFolderPath = portalDir;

            Assert.Equal([ownedFile], model.FilePaths);
            Assert.Equal(Path.Combine(portalDir, "mirrored.txt"), Assert.Single(sut.FileItems).Path);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
            File.Delete(ownedFile);
        }
    }

    /// <summary>
    /// Verifies that re-assigning the SAME already-attached portal folder — a no-op at the
    /// <see cref="ChildWindowModel.SourceFolderPath"/> property level, since <c>SetField</c>'s equality
    /// guard means the private handler reacting to it never even runs again — leaves
    /// <see cref="ChildWindowModel.FilePaths"/> completely untouched, i.e. the MINOR-1 snapshot only ever
    /// fires for a genuine non-portal -&gt; portal transition.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathAssigned_SameFolderAgain_DoesNotTouchFilePaths()
    {
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "mirrored.txt"), "mirrored");
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            var model = new ChildWindowModel { SourceFolderPath = portalDir };
            sut.LoadFromModel(model);
            model.FilePaths = ["C:\\sentinel\\untouched.txt"];

            model.SourceFolderPath = portalDir; // Same value — SetField no-ops, handler never re-runs.

            Assert.Equal(["C:\\sentinel\\untouched.txt"], model.FilePaths);
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression guard for FIX round 2 MAJOR-1: <see cref="ChildWindowModel.FilePaths"/> becoming an
    /// INPC-backed property must not create a feedback loop — <see cref="ChildWindowViewModel"/> has no
    /// <c>OnModelPropertyChanged</c> case for it, so assigning a fresh <see cref="ChildWindowModel.FilePaths"/>
    /// list (as <c>ChildWindow.SyncToModel</c> does on every close/save) must not itself trigger
    /// <see cref="ChildWindowViewModel.FilesChanged"/> or rebuild <see cref="BaseViewModel.FileItems"/>.
    /// </summary>
    [Fact]
    public void ModelFilePathsAssigned_DoesNotRaiseFilesChangedOrRebuildItems()
    {
        using var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.LoadFromModel(model);

        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;
        var fileItemsResetRaised = false;
        sut.FileItems.CollectionChanged += (_, _) => fileItemsResetRaised = true;

        model.FilePaths = ["C:\\fake\\a.txt", "C:\\fake\\b.txt"];

        Assert.False(filesChangedRaised);
        Assert.False(fileItemsResetRaised);
    }

    // ── FIX round 2 MINOR-2: clearing a portal path that never actually attached (e.g. it pointed at a
    //    missing folder) must still raise FilesChanged, so the clear itself gets persisted. ────────────

    /// <summary>
    /// Verifies that clearing <see cref="ChildWindowModel.SourceFolderPath"/> back to blank, after it had
    /// pointed at a folder that never existed (so no watcher was ever attached), still raises
    /// <see cref="ChildWindowViewModel.FilesChanged"/> — the clear itself is a real state change that
    /// needs to be persisted, even though there is no watcher to detach.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathCleared_AfterBrokenPortalPath_RaisesFilesChanged()
    {
        using var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.LoadFromModel(model);

        model.SourceFolderPath = @"C:\this\path\does\not\exist\at\all";
        Assert.False(sut.IsPortal);

        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        model.SourceFolderPath = null;

        Assert.True(filesChangedRaised);
        _watcherFactoryMock.Verify(f => f.Create(), Times.Never);
    }

    /// <summary>
    /// Verifies that clearing <see cref="ChildWindowModel.SourceFolderPath"/> when it was ALREADY blank
    /// (the genuinely idempotent case — e.g. a re-entrant call reacting to
    /// <see cref="ChildWindowViewModel.LoadFromModel"/>) still does not raise
    /// <see cref="ChildWindowViewModel.FilesChanged"/>, preserving the original idempotency guard this
    /// fix must not regress.
    /// </summary>
    [Fact]
    public void ModelSourceFolderPathCleared_AlreadyBlank_DoesNotRaiseFilesChanged()
    {
        using var sut = BuildSut();
        var model = new ChildWindowModel();
        sut.LoadFromModel(model);

        var filesChangedRaised = false;
        sut.FilesChanged += (_, _) => filesChangedRaised = true;

        // Blank -> blank: SetField still raises (a different string value than the null default), so the
        // handler runs once more — but both the pre- and post-transition state are "no portal", so this
        // must still be treated as idempotent.
        model.SourceFolderPath = string.Empty;

        Assert.False(filesChangedRaised);
    }

    /// <summary>
    /// Verifies that changing <see cref="ChildWindowModel.SortMode"/> directly raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for both
    /// <see cref="ChildWindowViewModel.EffectiveSortMode"/> and <see cref="ChildWindowViewModel.IsSortedByName"/>.
    /// </summary>
    [Fact]
    public void ModelSortModeChanged_RaisesBothEffectiveSortModeAndIsSortedByName()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel()); // Manual by default

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        sut.Model.SortMode = PanelSortMode.Name;

        Assert.Contains(nameof(ChildWindowViewModel.EffectiveSortMode), raised);
        Assert.Contains(nameof(ChildWindowViewModel.IsSortedByName), raised);
        Assert.True(sut.IsSortedByName);
    }

    /// <summary>
    /// Verifies that executing <see cref="ChildWindowViewModel.ToggleSortModeCommand"/> switches the
    /// model to <see cref="PanelSortMode.Name"/> and raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for both
    /// <see cref="ChildWindowViewModel.EffectiveSortMode"/> and <see cref="ChildWindowViewModel.IsSortedByName"/>.
    /// </summary>
    [Fact]
    public void ToggleSortModeCommand_RaisesBothEffectiveSortModeAndIsSortedByName()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel()); // Manual by default

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        sut.ToggleSortModeCommand.Execute(null);

        Assert.Contains(nameof(ChildWindowViewModel.EffectiveSortMode), raised);
        Assert.Contains(nameof(ChildWindowViewModel.IsSortedByName), raised);
        Assert.True(sut.IsSortedByName);
        Assert.Equal(PanelSortMode.Name, sut.Model.SortMode);
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowViewModel.IsSortedByName"/> tracks
    /// <see cref="ChildWindowViewModel.EffectiveSortMode"/> as the model toggles between
    /// <see cref="PanelSortMode.Manual"/> and <see cref="PanelSortMode.Name"/>.
    /// </summary>
    [Fact]
    public void IsSortedByName_ReflectsEffectiveSortMode_ManualAndName()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());

        Assert.False(sut.IsSortedByName);

        sut.Model.SortMode = PanelSortMode.Name;
        Assert.True(sut.IsSortedByName);

        sut.Model.SortMode = PanelSortMode.Manual;
        Assert.False(sut.IsSortedByName);
    }

    /// <summary>
    /// Verifies that calling <see cref="ChildWindowViewModel.ReorderItem"/> in a Folder Portal panel is
    /// a no-op: it neither raises <see cref="ChildWindowViewModel.FilesChanged"/> nor reorders
    /// <see cref="BaseViewModel.AllItems"/>.
    /// </summary>
    [Fact]
    public void ReorderItem_PortalPanel_IsNoOp_DoesNotRaiseFilesChangedOrReorderAllItems()
    {
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "a.txt"), "a");
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            var item = Assert.Single(sut.FileItems);
            var originalOrder = sut.AllItems.Select(f => f.Path).ToList();

            var filesChangedRaised = false;
            sut.FilesChanged += (_, _) => filesChangedRaised = true;

            sut.ReorderItem(item, 0);

            Assert.False(filesChangedRaised);
            Assert.Equal(originalOrder, sut.AllItems.Select(f => f.Path));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowViewModel.SetSortModeCommand"/> cannot execute for a Folder
    /// Portal panel, but can execute for a regular panel.
    /// </summary>
    [Fact]
    public void SetSortModeCommand_CanExecute_FalseForPortal_TrueOtherwise()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel());
            Assert.True(sut.SetSortModeCommand.CanExecute(PanelSortMode.Name));

            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            Assert.True(sut.IsPortal);
            Assert.False(sut.SetSortModeCommand.CanExecute(PanelSortMode.Manual));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that executing <see cref="ChildWindowViewModel.SetSortModeCommand"/> writes the given
    /// value to <see cref="ChildWindowModel.SortMode"/>.
    /// </summary>
    [Fact]
    public void SetSortModeCommand_Execute_WritesModelSortMode()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());

        sut.SetSortModeCommand.Execute(PanelSortMode.Name);

        Assert.Equal(PanelSortMode.Name, sut.Model.SortMode);
    }

    // ── P06 (docs/fix-prompts/06-fit-content.md): SetIconLayoutPolicyCommand ────────────────────────

    /// <summary>
    /// Verifies that executing <see cref="ChildWindowViewModel.SetIconLayoutPolicyCommand"/> writes the
    /// given value to <see cref="ChildWindowModel.IconLayoutPolicy"/>.
    /// </summary>
    [Fact]
    public void SetIconLayoutPolicyCommand_Execute_WritesModelIconLayoutPolicy()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch });

        sut.SetIconLayoutPolicyCommand.Execute(IconLayoutPolicy.FixedGap);

        Assert.Equal(IconLayoutPolicy.FixedGap, sut.Model.IconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowViewModel.SetIconLayoutPolicyCommand"/> can always execute,
    /// including for a Folder Portal panel — unlike <see cref="ChildWindowViewModel.SetSortModeCommand"/>,
    /// icon layout policy has nothing to do with manual item ordering.
    /// </summary>
    [Fact]
    public void SetIconLayoutPolicyCommand_CanExecute_AlwaysTrue_EvenForAPortal()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            Assert.True(sut.IsPortal);

            // Unlike SetSortModeCommand, IconLayoutPolicy has nothing to do with manual item ordering —
            // it stays available for a Folder Portal panel (ADR-029 Решение 5 does not gate it on
            // portal-ness at all, only on Grid vs List mode, which this command doesn't check either —
            // see ChildWindow.xaml's own "Icon layout" MenuItem, gated only by CanEnterEditLayout).
            Assert.True(sut.SetIconLayoutPolicyCommand.CanExecute(IconLayoutPolicy.FixedGap));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── ADR-014 Decision 7 (fixes §1.2): Folder Portal watcher tick preserves alphabetical order ───

    /// <summary>
    /// Verifies that when the folder watcher reports a new file in a Folder Portal panel, the item is
    /// inserted at its alphabetical position in <see cref="BaseViewModel.FileItems"/>, not
    /// appended at the end.
    /// </summary>
    [Fact]
    public void Portal_WatcherCreatedEvent_NewFileInsertedAtAlphabeticalPosition_NotAppended()
    {
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "B.txt"), "b");
        File.WriteAllText(Path.Combine(portalDir, "C.txt"), "c");

        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            Assert.Equal(
                [Path.Combine(portalDir, "B.txt"), Path.Combine(portalDir, "C.txt")],
                sut.FileItems.Select(f => f.Path));

            var newFilePath = Path.Combine(portalDir, "A.txt");
            File.WriteAllText(newFilePath, "a");

            // Simulate the FileSystemWatcher's "created" callback the way OnFolderChanged consumes it.
            // CRITICAL C-1 fix (P08 fix cycle 2): OnFolderChanged now marshals fire-and-forget via
            // UiDispatch.TryBeginInvoke rather than blocking, so the dispatched mutation is only QUEUED
            // by the time Raise returns — FlushDispatcherQueue() ensures the queued action actually runs
            // before the assertion below reads sut.FileItems.
            watcherMock.Raise(w => w.Changed += null, watcherMock.Object, new FolderChangeEventArgs(FolderChangeKind.Created, newFilePath));
            FlushDispatcherQueue(Application.Current!.Dispatcher);

            Assert.Equal(
                [newFilePath, Path.Combine(portalDir, "B.txt"), Path.Combine(portalDir, "C.txt")],
                sut.FileItems.Select(f => f.Path));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── CRITICAL C-1 (P08 fix cycle 2): OnFolderChanged must never block its FolderWatcher caller ──

    /// <summary>
    /// Pins the actual defect behind CRITICAL C-1: <c>OnFolderChanged</c> used to marshal onto the UI
    /// thread via the BLOCKING <see cref="UiDispatch.TryInvoke"/> from inside <c>FolderWatcher</c>'s own
    /// debounce callback thread. If that callback thread is holding <c>FolderWatcher</c>'s debounce lock
    /// while the blocking <c>Invoke</c> waits for the UI thread, and the UI thread's very next action is
    /// closing this panel (<c>DetachWatcher</c> → <c>IFolderWatcher.Dispose</c>/<c>Stop</c> → the SAME
    /// lock), the two threads deadlock the whole application.
    /// </summary>
    /// <remarks>
    /// Reproducing the exact lock is <c>FolderWatcher</c>'s own concern (out of scope for this ViewModel-
    /// level fixture — see its own tests), but the property this test CAN and must pin at this layer is
    /// the one the fix actually guarantees: raising <c>Changed</c> on a genuine background thread returns
    /// promptly, regardless of whether <see cref="Application.Current"/>'s dispatcher is currently pumping
    /// — which is exactly the state most fixtures in this file leave it in (see
    /// <c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s "live but non-pumping owner" hazard).
    /// Against the pre-fix (blocking <see cref="UiDispatch.TryInvoke"/>) code, this callback thread would
    /// hang until <see cref="Timeout"/> elapses, because nothing on this non-pumping dispatcher ever
    /// services the queued Invoke — i.e. this test would fail (time out) against that code.
    /// </remarks>
    [Fact]
    public void OnFolderChanged_RaisedOnABackgroundThread_ReturnsBeforeTheUiThreadServicesIt()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });

            using var raised = new ManualResetEventSlim(initialState: false);
            var worker = new Thread(() =>
            {
                watcherMock.Raise(
                    w => w.Changed += null,
                    watcherMock.Object,
                    new FolderChangeEventArgs(FolderChangeKind.ResyncRequired, portalDir));
                raised.Set();
            })
            {
                IsBackground = true,
            };
            worker.Start();

            var completedPromptly = raised.Wait(Timeout);
            worker.Join(Timeout);

            Assert.True(
                completedPromptly,
                "OnFolderChanged must not block the FolderWatcher callback thread waiting for the UI thread.");
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    /// <summary>
    /// UiDispatch fix-cycle verification (MAJOR): a folder-change that was already queued (fire-and-forget)
    /// when the panel was closed must be a no-op once it finally runs — otherwise a ResyncRequired would
    /// re-enumerate the folder and rebuild the item list on a DISPOSED view-model, mid-close.
    /// </summary>
    [Fact]
    public void OnFolderChanged_QueuedBeforeDispose_DoesNothingAfterDispose()
    {
        var portalDir = NewTempDirPath();
        File.WriteAllText(Path.Combine(portalDir, "late.txt"), "x");
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        var sut = BuildSut();
        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            FlushDispatcherQueue(Application.Current!.Dispatcher);
            var countBeforeDispose = sut.FileItems.Count;

            // Capture the handler BEFORE Dispose detaches it — this models a debounce tick that fired on
            // the watcher thread a moment before the UI thread closed the panel.
            EventHandler<FolderChangeEventArgs>? captured = null;
            watcherMock.SetupAdd(w => w.Changed += It.IsAny<EventHandler<FolderChangeEventArgs>>())
                .Callback<EventHandler<FolderChangeEventArgs>>(h => captured = h);
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            FlushDispatcherQueue(Application.Current.Dispatcher);
            Assert.NotNull(captured);

            sut.FileItems.Clear();
            sut.Dispose();

            captured!(watcherMock.Object, new FolderChangeEventArgs(FolderChangeKind.ResyncRequired, portalDir));
            FlushDispatcherQueue(Application.Current.Dispatcher);

            Assert.Empty(sut.FileItems);
            Assert.True(countBeforeDispose >= 1); // the folder really had content to resync
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── MAJOR M-1 (P08 fix cycle 2): RefreshExistence single-flight guard ───────────────────────────

    /// <summary>
    /// Verifies that the first <see cref="ChildWindowViewModel.RefreshExistence"/> tick with no refresh
    /// already pending queues one and marks <see cref="ChildWindowViewModel.IsExistenceRefreshPending"/>.
    /// </summary>
    [Fact]
    public void RefreshExistence_WhenNoRefreshIsPending_QueuesOneAndMarksItPending()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());

        sut.RefreshExistence(null);

        Assert.True(sut.IsExistenceRefreshPending, "the first tick must mark a refresh as pending.");
    }

    /// <summary>
    /// Pins the actual MAJOR M-1 fix: <c>RefreshExistence</c> used to queue a brand-new blocking
    /// <see cref="UiDispatch.TryInvoke"/> operation on EVERY 3-second tick regardless of whether an
    /// earlier one had been serviced yet — against a busy/blocked UI thread, that accumulates one occupied
    /// thread-pool thread per tick and can starve the pool (see
    /// <c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s "second, unrelated flake"). Ten ticks
    /// fired back-to-back — none of them from a pumped dispatcher, so the first one's dispatched action
    /// never actually runs during this synchronous test method — must still leave the single-flight flag
    /// in exactly its pending state, never queuing a second (or third, …) operation on top of the first.
    /// </summary>
    [Fact]
    public void RefreshExistence_WhileARefreshIsAlreadyPending_NeverAccumulatesMoreThanOnePendingRefresh()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());

        sut.RefreshExistence(null);
        Assert.True(sut.IsExistenceRefreshPending);

        for (var i = 0; i < 9; i++)
            sut.RefreshExistence(null);

        Assert.True(
            sut.IsExistenceRefreshPending,
            "still exactly one refresh pending after 10 ticks — never reset/corrupted by the later, skipped ticks.");
    }

    /// <summary>
    /// Verifies that a <see cref="ChildWindowViewModel.RefreshExistence"/> tick arriving after
    /// <see cref="ChildWindowViewModel.Dispose"/> early-returns without throwing and without claiming
    /// the single-flight pending slot.
    /// </summary>
    [Fact]
    public void RefreshExistence_AfterDispose_NeverTouchesThePendingFlag()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());
        sut.Dispose();

        var exception = Record.Exception(() => sut.RefreshExistence(null));

        Assert.Null(exception);
        Assert.False(
            sut.IsExistenceRefreshPending,
            "a tick arriving after Dispose() must early-return before claiming the single-flight slot.");
    }

    // ── ui-polishing-1 §2.4 Block A2: IPanelSelectionCoordinator integration ─────────────────────

    /// <summary>
    /// Verifies that setting <see cref="BaseViewModel.SelectedFile"/> to a non-null item notifies
    /// <see cref="IPanelSelectionCoordinator"/> of the claim.
    /// </summary>
    [Fact]
    public void SelectedFile_SetToNonNull_NotifiesCoordinator()
    {
        using var sut = BuildSut();

        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;

        _selectionCoordinatorMock.Verify(c => c.NotifySelected(sut), Times.Once);
    }

    /// <summary>
    /// Verifies that clearing <see cref="BaseViewModel.SelectedFile"/> to <c>null</c> does not
    /// notify <see cref="IPanelSelectionCoordinator"/>, since clearing is not a selection claim.
    /// </summary>
    [Fact]
    public void SelectedFile_SetToNull_DoesNotNotifyCoordinator()
    {
        // A panel clearing its own selection is not "this panel claiming the selection" — see
        // ChildWindowViewModel.OnSelectedFileChanged's remarks.
        using var sut = BuildSut();
        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;
        _selectionCoordinatorMock.Invocations.Clear();

        sut.SelectedFile = null;

        _selectionCoordinatorMock.Verify(c => c.NotifySelected(It.IsAny<object>()), Times.Never);
    }

    /// <summary>
    /// Verifies that when <see cref="IPanelSelectionCoordinator.SelectionClaimed"/> fires for a
    /// different owner, <see cref="BaseViewModel.SelectedFile"/> is cleared.
    /// </summary>
    [Fact]
    public void SelectionClaimed_FromAnotherOwner_ClearsSelectedFile()
    {
        using var sut = BuildSut();
        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;

        _selectionCoordinatorMock.Raise(c => c.SelectionClaimed += null, _selectionCoordinatorMock.Object, new object());

        Assert.Null(sut.SelectedFile);
    }

    /// <summary>
    /// Verifies that when <see cref="IPanelSelectionCoordinator.SelectionClaimed"/> fires for this same
    /// view model as owner, <see cref="BaseViewModel.SelectedFile"/> is left untouched.
    /// </summary>
    [Fact]
    public void SelectionClaimed_FromSelf_DoesNotClearSelectedFile()
    {
        using var sut = BuildSut();
        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;

        // The coordinator would never actually raise this for the same owner that just claimed it
        // (NotifySelected is idempotent for a repeat of the current owner), but the ViewModel's own
        // "owner == this" guard in OnSelectionClaimed must independently hold too — defense in depth.
        _selectionCoordinatorMock.Raise(c => c.SelectionClaimed += null, _selectionCoordinatorMock.Object, sut);

        Assert.Same(item, sut.SelectedFile);
    }

    /// <summary>
    /// Verifies that <see cref="ChildWindowViewModel.Dispose"/> unsubscribes from
    /// <see cref="IPanelSelectionCoordinator.SelectionClaimed"/>, so a later claim from another owner
    /// does not touch the disposed instance or throw.
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromSelectionClaimed()
    {
        var coordinator = new PanelSelectionCoordinator();
        var sut = BuildSut(coordinator);
        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;

        sut.Dispose();

        // After Dispose, a claim from another owner must not resurrect a live subscription that would
        // otherwise touch the disposed ViewModel's state (SelectedFile is already null via Dispose's
        // own cleanup path not touching it — this asserts no exception/leaked handler either).
        var exception = Record.Exception(() => coordinator.NotifySelected(new object()));
        Assert.Null(exception);
    }

    /// <summary>
    /// Integration-style (ui-polishing-1 §2.4 Block A2 DoD 4): verifies that two
    /// <see cref="ChildWindowViewModel"/> panels sharing a real <see cref="PanelSelectionCoordinator"/>
    /// clear each other's selection whenever one selects an item.
    /// </summary>
    [Fact]
    public void TwoPanels_SharingCoordinator_SelectingInOneClearsTheOther()
    {
        // Integration-style: a real (non-mocked) coordinator shared by two independent panel
        // ViewModels — ui-polishing-1 §2.4 Block A2 DoD 4 ("selecting in panel #1 clears panel #2").
        var coordinator = new PanelSelectionCoordinator();
        using var panel1 = BuildSut(coordinator);
        using var panel2 = BuildSut(coordinator);

        var item1 = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        var item2 = new FileItem(@"C:\fake\B.txt", null, displayName: null);

        panel1.SelectedFile = item1;
        panel2.SelectedFile = item2;

        // Selecting in panel2 must have cleared panel1's selection, and panel2 keeps its own.
        Assert.Null(panel1.SelectedFile);
        Assert.Same(item2, panel2.SelectedFile);

        // And selecting back in panel1 clears panel2 in turn.
        panel1.SelectedFile = item1;

        Assert.Same(item1, panel1.SelectedFile);
        Assert.Null(panel2.SelectedFile);
    }

    /// <summary>
    /// Regression guard for ui-polishing-1 §2.4 Block A2 risk R3: verifies that repeatedly bouncing
    /// selection between two panels sharing one real <see cref="PanelSelectionCoordinator"/> terminates
    /// normally instead of recursing into a stack overflow.
    /// </summary>
    [Fact]
    public void TwoPanels_SharingCoordinator_NoRecursionOrStackOverflow()
    {
        // Regression guard for ui-polishing-1 §2.4 Block A2 risk R3: NotifySelected is called from
        // inside the SelectedFile setter, and every subscriber's reaction to SelectionClaimed calls
        // that same setter again (to clear). Repeatedly bouncing selection between two panels sharing
        // one real coordinator must terminate normally, not blow the stack.
        var coordinator = new PanelSelectionCoordinator();
        using var panel1 = BuildSut(coordinator);
        using var panel2 = BuildSut(coordinator);

        var item1 = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        var item2 = new FileItem(@"C:\fake\B.txt", null, displayName: null);

        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                panel1.SelectedFile = item1;
                panel2.SelectedFile = item2;
            }
        });

        Assert.Null(exception);
        Assert.Null(panel1.SelectedFile);
        Assert.Same(item2, panel2.SelectedFile);
    }

    // ── ui-polishing-1 §5.6 DoD 4: cross-panel-type selection (fence panel <-> sidebar) ──────────

    /// <summary>
    /// Integration-style (ui-polishing-1 Block D, DoD 4): verifies that a fence panel
    /// (<see cref="ChildWindowViewModel"/>) and the system icons sidebar (<see cref="SystemIconsPanelViewModel"/>)
    /// sharing a real <see cref="PanelSelectionCoordinator"/> clear each other's selection when either
    /// selects an item.
    /// </summary>
    [Fact]
    public void TwoPanels_SharingCoordinator_ChildWindowAndSystemIconsPanel_SelectingInOneClearsTheOther()
    {
        // Integration-style: a real (non-mocked) coordinator shared by a fence panel
        // (ChildWindowViewModel) and the system icons sidebar (SystemIconsPanelViewModel) —
        // ui-polishing-1 Block D, DoD 4: selecting in either clears the other.
        var coordinator = new PanelSelectionCoordinator();
        using var fencePanel = BuildSut(coordinator);

        var sidebarModel = new SystemIconsPanelModel();
        sidebarModel.Items.Clear();
        sidebarModel.Items.Add(new SystemIconItemModel { Kind = SystemIconKind.ThisPc, IsVisible = true, Order = 0 });
        using var sidebarPanel = new SystemIconsPanelViewModel(sidebarModel, Mock.Of<ISystemShellService>(), coordinator);

        var fenceItem = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        var sidebarItem = sidebarModel.Items[0];

        fencePanel.SelectedFile = fenceItem;
        sidebarPanel.SelectedItem = sidebarItem;

        // Selecting in the sidebar must have cleared the fence panel's selection, and the sidebar
        // keeps its own.
        Assert.Null(fencePanel.SelectedFile);
        Assert.Same(sidebarItem, sidebarPanel.SelectedItem);

        // And selecting back in the fence panel clears the sidebar in turn.
        fencePanel.SelectedFile = fenceItem;

        Assert.Same(fenceItem, fencePanel.SelectedFile);
        Assert.Null(sidebarPanel.SelectedItem);
    }

    // ── release-polish 1.0: icons are requested at the panel's own pixel size (IconSize x DPI) ──────

    /// <summary>
    /// Verifies that a list-view panel requests its icons at the list template's fixed 16 DIP size (scale 1.0).
    /// </summary>
    [Fact]
    public void LoadFromModel_ListView_RequestsIconsAtListIconSize()
    {
        const string path = @"C:\fake\list-item.txt";
        using var sut = BuildSut();

        sut.LoadFromModel(new ChildWindowModel { FilePaths = [path], ViewMode = PanelViewMode.List, IconSize = 96 });

        _iconProviderMock.Verify(p => p.GetIcon(path, 16), Times.Once);
        _iconProviderMock.Verify(p => p.GetIcon(path, It.Is<int>(size => size != 16)), Times.Never);
    }

    /// <summary>
    /// Verifies that a grid-view panel requests its icons at its own <see cref="ChildWindowModel.IconSize"/>
    /// multiplied by the pushed DPI scale and snapped up to a shell size bucket (48 DIP at 150 % → 72 px).
    /// </summary>
    [Fact]
    public void LoadFromModel_GridView_RequestsIconSizeTimesDpiScale()
    {
        const string path = @"C:\fake\grid-item.txt";
        using var sut = BuildSut();
        sut.IconRenderScale = 1.5;

        sut.LoadFromModel(new ChildWindowModel { FilePaths = [path], ViewMode = PanelViewMode.Grid, IconSize = 48 });

        _iconProviderMock.Verify(p => p.GetIcon(path, 72), Times.Once);
        _iconProviderMock.Verify(p => p.GetIcon(path, It.Is<int>(size => size != 72)), Times.Never);
    }

    /// <summary>
    /// Verifies that changing the panel's <see cref="ChildWindowModel.IconSize"/> re-fetches every item's icon
    /// at the new size and swaps it into the bound <see cref="FileItem.Icon"/> — once the debounce window
    /// (M3, release-polish 1.0 review) elapses; the re-fetch itself is deferred, not immediate.
    /// </summary>
    [Fact]
    public void IconSizeChange_CrossingABucket_RefetchesAndReplacesEveryIconAfterDebounceWindow()
    {
        const string pathA = @"C:\fake\a.txt";
        const string pathB = @"C:\fake\b.txt";
        var largeIcon = new System.Windows.Media.DrawingImage();
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>(), 96)).Returns(largeIcon);

        var timer = new FakeDebounceTimer();
        using var sut = BuildSut(iconResizeDebounceTimerProvider: () => timer);
        var model = new ChildWindowModel { FilePaths = [pathA, pathB], ViewMode = PanelViewMode.Grid, IconSize = 48 };
        sut.LoadFromModel(model);
        _iconProviderMock.Invocations.Clear();

        model.IconSize = 96;

        // Not yet — only the debounce window's timer was (re)armed so far.
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.Equal(1, timer.StartCount);

        timer.Fire(); // simulates the debounce window elapsing

        _iconProviderMock.Verify(p => p.GetIcon(pathA, 96), Times.Once);
        _iconProviderMock.Verify(p => p.GetIcon(pathB, 96), Times.Once);
        Assert.All(sut.AllItems, item => Assert.Same(largeIcon, item.Icon));
    }

    /// <summary>
    /// Verifies that an <see cref="ChildWindowModel.IconSize"/> change that stays inside the same size bucket
    /// (the slider is continuous) does not arm the debounce timer or re-fetch anything.
    /// </summary>
    [Fact]
    public void IconSizeChange_WithinTheSameBucket_DoesNotRefetch()
    {
        const string path = @"C:\fake\same-bucket.txt";
        var timer = new FakeDebounceTimer();
        using var sut = BuildSut(iconResizeDebounceTimerProvider: () => timer);
        var model = new ChildWindowModel { FilePaths = [path], ViewMode = PanelViewMode.Grid, IconSize = 42 };
        sut.LoadFromModel(model);
        _iconProviderMock.Invocations.Clear();

        model.IconSize = 47.5; // 42 → 48 px bucket, 47.5 → still the 48 px bucket

        Assert.Equal(0, timer.StartCount);
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// Verifies that several <see cref="ChildWindowModel.IconSize"/> bucket crossings inside the debounce
    /// window (M3, release-polish 1.0 review — the live slider dragging across 8+ buckets) collapse into
    /// exactly one re-fetch, at whatever size was current when the window finally elapsed.
    /// </summary>
    [Fact]
    public void IconSizeChange_SeveralBucketCrossingsWithinDebounceWindow_ResultInExactlyOneRefetch()
    {
        const string path = @"C:\fake\dragged.txt";
        var timer = new FakeDebounceTimer();
        using var sut = BuildSut(iconResizeDebounceTimerProvider: () => timer);
        var model = new ChildWindowModel { FilePaths = [path], ViewMode = PanelViewMode.Grid, IconSize = 16 };
        sut.LoadFromModel(model);
        _iconProviderMock.Invocations.Clear();

        // Simulates dragging the slider across several buckets — each re-arms the same debounce timer
        // rather than firing its own re-fetch.
        model.IconSize = 32;
        model.IconSize = 48;
        model.IconSize = 64;
        model.IconSize = 96;

        Assert.Equal(4, timer.StartCount); // every crossing (re)armed the timer...
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never); // ...but nothing fetched yet.

        timer.Fire();

        // ...and exactly one re-fetch happens, at the LAST size the slider landed on.
        _iconProviderMock.Verify(p => p.GetIcon(path, 96), Times.Once);
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.Is<int>(size => size != 96)), Times.Never);
    }

    /// <summary>
    /// Verifies that a new item added WHILE a debounced icon re-fetch is still pending (M3, release-polish
    /// 1.0 review) is fetched at the CURRENT (already-updated) pixel size right away — never a stale,
    /// pre-debounce one — since <see cref="BaseViewModel.RequestedIconPixelSize"/> updates synchronously
    /// even though the existing items' re-fetch is deferred.
    /// </summary>
    [Fact]
    public void AddFiles_WhileIconResizeDebouncePending_UsesTheNewCurrentPixelSize()
    {
        const string existingPath = @"C:\fake\existing.txt";
        const string newPath = @"C:\fake\new.txt";
        var timer = new FakeDebounceTimer();
        using var sut = BuildSut(iconResizeDebounceTimerProvider: () => timer);
        var model = new ChildWindowModel { FilePaths = [existingPath], ViewMode = PanelViewMode.Grid, IconSize = 48 };
        sut.LoadFromModel(model);
        _iconProviderMock.Invocations.Clear();

        model.IconSize = 96; // arms the debounce timer; existingPath is NOT re-fetched yet

        sut.AddFiles([newPath]);

        // The new item is fetched at the new size immediately, without waiting for the debounce window.
        _iconProviderMock.Verify(p => p.GetIcon(newPath, 96), Times.Once);
    }

    /// <summary>
    /// Verifies that switching list → grid re-fetches icons at the grid size once the debounce window
    /// (M3, release-polish 1.0 review) elapses.
    /// </summary>
    [Fact]
    public void ViewModeChange_ListToGrid_RefetchesAtGridIconSizeAfterDebounceWindow()
    {
        const string path = @"C:\fake\view-switch.txt";
        var timer = new FakeDebounceTimer();
        using var sut = BuildSut(iconResizeDebounceTimerProvider: () => timer);
        var model = new ChildWindowModel { FilePaths = [path], ViewMode = PanelViewMode.List, IconSize = 64 };
        sut.LoadFromModel(model);
        _iconProviderMock.Invocations.Clear();

        model.ViewMode = PanelViewMode.Grid;
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);

        timer.Fire();

        _iconProviderMock.Verify(p => p.GetIcon(path, 64), Times.Once);
    }

    /// <summary>
    /// Verifies that a DPI change pushed by the window (moving the panel to a 200 % monitor) re-fetches icons at
    /// the new pixel size once the debounce window (M3, release-polish 1.0 review) elapses, and that invalid
    /// scale values are ignored (and never arm the timer).
    /// </summary>
    [Fact]
    public void IconRenderScaleChange_RefetchesAtScaledSizeAfterDebounceWindow_AndIgnoresInvalidValues()
    {
        const string path = @"C:\fake\dpi.txt";
        var timer = new FakeDebounceTimer();
        using var sut = BuildSut(iconResizeDebounceTimerProvider: () => timer);
        sut.LoadFromModel(new ChildWindowModel { FilePaths = [path], ViewMode = PanelViewMode.Grid, IconSize = 48 });
        _iconProviderMock.Invocations.Clear();

        sut.IconRenderScale = double.NaN;
        sut.IconRenderScale = 0;
        Assert.Equal(0, timer.StartCount);
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);

        sut.IconRenderScale = 2.0;
        Assert.Equal(1, timer.StartCount);
        _iconProviderMock.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);

        timer.Fire();

        _iconProviderMock.Verify(p => p.GetIcon(path, 96), Times.Once);
        Assert.Equal(2.0, sut.IconRenderScale);
    }

    /// <summary>
    /// Verifies that items added after a size change (drop, portal resync, reappearance) use the panel's current
    /// pixel size rather than the default one.
    /// </summary>
    [Fact]
    public void AddDroppedFiles_AfterSizeChange_UsesCurrentPixelSize()
    {
        var source = NewTempFilePath();
        File.WriteAllText(source, "content");
        _storageMock.Setup(s => s.MoveIn(source)).Returns((string?)null);

        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 32 });

        try
        {
            sut.AddDroppedFiles([source]);

            _iconProviderMock.Verify(p => p.GetIcon(source, 32), Times.Once);
        }
        finally
        {
            File.Delete(source);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The VM constructor resolves <c>ListViewTemplate</c>/<c>GridViewTemplate</c> via
    /// <see cref="System.Windows.Application.Current"/>. There's no WPF host in the test process by
    /// default (no <c>UseWPF</c> app bootstrap), so a minimal <see cref="System.Windows.Application"/>
    /// is created once per test run, mirroring what <c>App.xaml</c> does in production.
    /// </summary>
    /// <remarks>
    /// M-2 fix (P08 fix cycle 2, sta-test-conventions.md "live but non-pumping owner"): delegates to
    /// <see cref="WpfApplicationHost.EnsureApplication"/> — the one place the real merge chain
    /// <c>App.xaml.cs</c>'s own <c>LoadResourceDictionaries()</c> builds is defined — instead of hand-
    /// rolling a throwaway <see cref="System.Windows.Application"/> with only <c>Templates.xaml</c>
    /// merged in. Whichever call across the whole <c>dotnet test</c> process gets here first still owns
    /// <see cref="System.Windows.Application.Current"/> for the rest of the run (see that class's own
    /// remarks); this method no longer duplicates that decision with its own "was it null before" check.
    /// </remarks>
    private static void EnsureWpfApplication() => WpfApplicationHost.EnsureApplication();

    /// <summary>
    /// Ensures every operation already queued on <paramref name="dispatcher"/> at Normal priority or
    /// above — in particular, whatever <c>ChildWindowViewModel.OnFolderChanged</c> just queued via the
    /// fire-and-forget <see cref="UiDispatch.TryBeginInvoke"/> (CRITICAL C-1 fix, P08 fix cycle 2) — has
    /// run, before returning.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="dispatcher"/> explicitly (always <c>Application.Current!.Dispatcher</c> at
    /// the call site) rather than assuming <see cref="Dispatcher.CurrentDispatcher"/> — i.e. THIS xUnit
    /// thread's own — because WHICH thread ends up owning <see cref="Application.Current"/> for the whole
    /// `dotnet test` process depends on whichever WPF-touching fixture across the ENTIRE assembly happens
    /// to run first (see <see cref="WpfApplicationHost"/>'s own remarks): it might be this very
    /// constructor's own <see cref="EnsureWpfApplication"/> call, or it might be a dedicated
    /// <c>StaTestRunner</c>-owned worker thread from a completely different test class that got there
    /// first. Two different drain mechanisms follow from that:
    /// <list type="bullet">
    /// <item><description>
    /// Same thread (<see cref="Dispatcher.CheckAccess"/> true — this xUnit thread owns
    /// <see cref="Application.Current"/> itself): it is NOT otherwise pumping at all, so a blocking wait
    /// here would deadlock; push a nested <see cref="DispatcherFrame"/> instead, which re-enters the
    /// dispatcher's own queue processing synchronously, in place.
    /// </description></item>
    /// <item><description>
    /// A different thread (some other fixture's <c>StaTestRunner</c>-owned worker): that thread is, by
    /// `StaTestRunner`'s own contract, ALREADY pumping forever, so a plain blocking wait for a
    /// low-priority marker to run is both safe (this thread isn't the one that needs to pump) and
    /// correct.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static void FlushDispatcherQueue(Dispatcher dispatcher)
    {
        if (dispatcher.CheckAccess())
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            return;
        }

        using var completed = new ManualResetEventSlim(initialState: false);
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => completed.Set()));

        if (!completed.Wait(Timeout))
        {
            throw new TimeoutException(
                $"{nameof(ChildWindowViewModelTests)}.{nameof(FlushDispatcherQueue)}: dispatcher did not drain within {Timeout}.");
        }
    }

    private static string NewTempFilePath() => Path.Combine(Path.GetTempPath(), $"DOrgVmTest_{Guid.NewGuid():N}.txt");

    private static string NewTempDirPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DOrgVmTestDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private ChildWindowViewModel BuildSut(
        IPanelSelectionCoordinator? selectionCoordinator = null,
        Func<IDebounceTimer?>? iconResizeDebounceTimerProvider = null) => new(
        _iconProviderMock.Object,
        _fileLauncherMock.Object,
        _watcherFactoryMock.Object,
        _storageMock.Object,
        _renameServiceMock.Object,
        _appSettingsMock.Object,
        selectionCoordinator ?? _selectionCoordinatorMock.Object,
        _diagnosticsMock.Object,
        iconResizeDebounceTimerProvider);

    /// <summary>
    /// Minimal <see cref="IDebounceTimer"/> test double for the icon-resize debounce (M3, release-polish
    /// 1.0 review) — mirrors <c>SaveSchedulerTests</c>' own private fake: "last <see cref="Start"/> wins"
    /// semantics (each call replaces the previously-armed callback rather than queuing a second one),
    /// with no actual timing involved.
    /// </summary>
    private sealed class FakeDebounceTimer : IDebounceTimer
    {
        private Action? _callback;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public void Start(TimeSpan window, Action callback)
        {
            StartCount++;
            _callback = callback;
        }

        public void Stop()
        {
            StopCount++;
            _callback = null;
        }

        /// <summary>Simulates the debounce window elapsing: invokes and clears the armed callback, if any.</summary>
        public void Fire()
        {
            var callback = _callback;
            _callback = null;
            callback?.Invoke();
        }
    }
}
