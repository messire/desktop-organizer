using System.IO;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for <see cref="ChildWindowViewModel"/>'s Move-semantics glue: the
/// <see cref="ChildWindowViewModel.AddDroppedFiles"/> fallback-to-original-path behavior when
/// <see cref="IPanelItemStorageService.MoveIn"/>/<see cref="IPanelItemStorageService.MoveInDirectory"/>
/// return <see langword="null"/> (D13), and the <c>RemoveFileItem</c> degradation to
/// <see cref="IPanelItemStorageService.Release"/> when <see cref="IPanelItemStorageService.Restore"/>
/// reports <see cref="RestoreOutcome.OriginalPathUnknown"/> (D9, VM-part — see
/// docs/PLANS/drag-drop-move-semantics.md, ADR-B legacy-panel degradation).
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
    private readonly Mock<IIconProvider> _iconProviderMock = new();
    private readonly Mock<IFileLauncher> _fileLauncherMock = new();
    private readonly Mock<IFolderWatcherFactory> _watcherFactoryMock = new();
    private readonly Mock<IPanelItemStorageService> _storageMock = new();
    private readonly Mock<IFileItemRenameService> _renameServiceMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IPanelSelectionCoordinator> _selectionCoordinatorMock = new();

    public ChildWindowViewModelTests()
    {
        EnsureWpfApplication();
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((System.Windows.Media.ImageSource?)null);

        // Default: the one-time Move-semantics hint hasn't been shown yet. Individual tests that
        // care about the already-shown case override this via SetupProperty again.
        _appSettingsMock.SetupProperty(s => s.HasShownMoveHint, false);
    }

    /// <summary>
    /// The VM constructor resolves <c>ListViewTemplate</c>/<c>GridViewTemplate</c> via
    /// <see cref="System.Windows.Application.Current"/>. There's no WPF host in the test process by
    /// default (no <c>UseWPF</c> app bootstrap), so a minimal <see cref="System.Windows.Application"/>
    /// with the real Templates.xaml resource dictionary merged in is created once per test run,
    /// mirroring what <c>App.xaml</c> does in production.
    /// </summary>
    private static void EnsureWpfApplication()
    {
        if (System.Windows.Application.Current is not null) return;

        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("/DesktopOrganizer.UI;component/Resources/Templates.xaml", UriKind.Relative),
        });
    }

    private ChildWindowViewModel BuildSut(IPanelSelectionCoordinator? selectionCoordinator = null) => new(
        _iconProviderMock.Object,
        _fileLauncherMock.Object,
        _watcherFactoryMock.Object,
        _storageMock.Object,
        _renameServiceMock.Object,
        _appSettingsMock.Object,
        selectionCoordinator ?? _selectionCoordinatorMock.Object);

    private static string NewTempFilePath() => Path.Combine(Path.GetTempPath(), $"DOrgVmTest_{Guid.NewGuid():N}.txt");

    private static string NewTempDirPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DOrgVmTestDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── D13: AddDroppedFiles fallback when MoveIn/MoveInDirectory return null ────────────────────

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

    // ── D9 (VM-part): RemoveFileItem degrades to Release on OriginalPathUnknown (ADR-B) ─────────

    [Fact]
    public void RemoveFileItem_RestoreReturnsOriginalPathUnknown_CallsRelease()
    {
        // Arrange
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        using var sut = BuildSut();
        sut.AddFiles([managedPath]);
        var item = Assert.Single(sut.AllItems);

        _storageMock.Setup(s => s.Restore(managedPath)).Returns(RestoreOutcome.OriginalPathUnknown);

        // Act — RemoveFileCommand is the only public entry point to the private RemoveFileItem.
        sut.RemoveFileCommand.Execute(item);

        // Assert — legacy managed copy with no manifest entry degrades to the old Release behavior.
        _storageMock.Verify(s => s.Release(managedPath), Times.Once);
    }

    [Fact]
    public void RemoveFileItem_RestoreSucceeds_DoesNotCallRelease()
    {
        // Arrange
        var managedPath = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        using var sut = BuildSut();
        sut.AddFiles([managedPath]);
        var item = Assert.Single(sut.AllItems);

        _storageMock.Setup(s => s.Restore(managedPath)).Returns(RestoreOutcome.Restored);

        // Act
        sut.RemoveFileCommand.Execute(item);

        // Assert — successful restore must not additionally Release the (already-moved-back) item.
        _storageMock.Verify(s => s.Release(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(RestoreOutcome.NotManaged)]
    [InlineData(RestoreOutcome.Failed)]
    public void RemoveFileItem_RestoreReturnsNotManagedOrFailed_DoesNotCallRelease(RestoreOutcome outcome)
    {
        // Arrange — only OriginalPathUnknown triggers the ADR-B degradation; NotManaged is already a
        // no-op inside Restore, and Failed means the managed copy is intentionally left in storage
        // (logged, not lost) rather than force-released.
        var path = Path.Combine(Path.GetTempPath(), $"DOrgVmTestManaged_{Guid.NewGuid():N}.txt");
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);

        _storageMock.Setup(s => s.Restore(path)).Returns(outcome);

        // Act
        sut.RemoveFileCommand.Execute(item);

        // Assert
        _storageMock.Verify(s => s.Release(It.IsAny<string>()), Times.Never);
    }

    // ── D3 (Block D, docs/PLANS/phase-7-performance-hardening.md): Model.PropertyChanged
    // subscription lifecycle — no leak past Dispose, no double subscription on repeated LoadFromModel.

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

    // ── A6: SelectedFile survives a ReplaceFileItems rebuild (ADR-012 Reset side effect) ───────────

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

    // ── R10/C3: rename in a Folder Portal panel is allowed (unlike RemoveFileCommand) ──────────

    [Fact]
    public void RemoveFileCommand_CanExecute_FalseInPortal_ButBeginRenameAndCommitRename_AlwaysCanExecute()
    {
        var portalDir = NewTempDirPath();
        var watcherMock = new Mock<IFolderWatcher>();
        _watcherFactoryMock.Setup(f => f.Create()).Returns(watcherMock.Object);

        using var sut = BuildSut();

        try
        {
            sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
            Assert.True(sut.IsPortal);

            Assert.False(sut.RemoveFileCommand.CanExecute(null));
            Assert.True(sut.BeginRenameCommand.CanExecute(null));
            Assert.True(sut.CommitRenameCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

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
    /// <see cref="FileItem.DisplayName"/> hides a shortcut's <c>.lnk"</c>, so
    /// <see cref="ChildWindowViewModel.BeginRename"/> — which pre-fills
    /// <see cref="ChildWindowViewModel.EditingName"/> from it — must not show the extension either.
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
    /// includes <c>.lnk</c> (see <see cref="FileItem.DisplayName"/>), so
    /// <see cref="ChildWindowViewModel.CommitRename"/> must re-attach <c>.lnk</c> before calling
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
    /// If the user explicitly types <c>.lnk</c> themselves, <see cref="ChildWindowViewModel.CommitRename"/>
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

    // NOTE (ui-polishing-1 §4.3): SelectSourceFolder() — the third of the three call sites replaced
    // with NotifySortModeChanged() — is not independently unit-testable. It calls
    // `new Microsoft.Win32.OpenFolderDialog().ShowDialog()` inline with no injectable seam, and no
    // existing test in this suite exercises SelectSourceFolderCommand for exactly that reason (a real
    // dialog abstraction would need its own interface/DI seam — an architectural change out of scope
    // for this block). Its notification correctness is verified by code review: it is byte-for-byte
    // the same `NotifySortModeChanged();` call as the two paths covered above, at the same two
    // call-sites (entering and leaving the portal) that used to carry the CRITICAL-2 manual
    // `OnPropertyChanged(nameof(EffectiveSortMode))` calls before this change.

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

    [Fact]
    public void SetSortModeCommand_Execute_WritesModelSortMode()
    {
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel());

        sut.SetSortModeCommand.Execute(PanelSortMode.Name);

        Assert.Equal(PanelSortMode.Name, sut.Model.SortMode);
    }

    // ── ADR-014 Decision 7 (fixes §1.2): Folder Portal watcher tick preserves alphabetical order ───

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
            watcherMock.Raise(w => w.Changed += null, watcherMock.Object, new FolderChangeEventArgs(FolderChangeKind.Created, newFilePath));

            Assert.Equal(
                [newFilePath, Path.Combine(portalDir, "B.txt"), Path.Combine(portalDir, "C.txt")],
                sut.FileItems.Select(f => f.Path));
        }
        finally
        {
            Directory.Delete(portalDir, recursive: true);
        }
    }

    // ── ui-polishing-1 §2.4 Block A2: IPanelSelectionCoordinator integration ─────────────────────

    [Fact]
    public void SelectedFile_SetToNonNull_NotifiesCoordinator()
    {
        using var sut = BuildSut();

        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;

        _selectionCoordinatorMock.Verify(c => c.NotifySelected(sut), Times.Once);
    }

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

    [Fact]
    public void SelectionClaimed_FromAnotherOwner_ClearsSelectedFile()
    {
        using var sut = BuildSut();
        var item = new FileItem(@"C:\fake\A.txt", null, displayName: null);
        sut.SelectedFile = item;

        _selectionCoordinatorMock.Raise(c => c.SelectionClaimed += null, _selectionCoordinatorMock.Object, new object());

        Assert.Null(sut.SelectedFile);
    }

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
}
