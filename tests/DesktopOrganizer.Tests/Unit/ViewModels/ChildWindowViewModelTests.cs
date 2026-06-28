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
    private readonly Mock<IAppSettings> _appSettingsMock = new();

    public ChildWindowViewModelTests()
    {
        EnsureWpfApplication();
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((System.Windows.Media.ImageSource?)null);

        // Default: the one-time Move-semantics hint hasn't been shown yet. Individual tests that
        // care about the already-shown case override this via SetupProperty again.
        _appSettingsMock.SetupProperty(s => s.HasShownMoveHint, false);
    }

    /// <summary>
    /// The VM constructor resolves <c>ListViewTemplate</c>/<c>WrapViewTemplate</c> via
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

    private ChildWindowViewModel BuildSut() => new(
        _iconProviderMock.Object,
        _fileLauncherMock.Object,
        _watcherFactoryMock.Object,
        _storageMock.Object,
        _appSettingsMock.Object);

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
        model.ViewMode = model.ViewMode == PanelViewMode.List ? PanelViewMode.Wrap : PanelViewMode.List;

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
}
