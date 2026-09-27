using System.IO;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for the panel side of the native shell context menu:
/// <see cref="ChildWindowViewModel.ShowShellContextMenu"/> (selection, forwarding to
/// <see cref="IFileLauncher.ShowContextMenu"/>, the shell "Rename" hand-off to the inline rename) and
/// <see cref="ChildWindowViewModel.ReconcileAfterShellCommand"/> (following a rename/delete the shell
/// performed directly on a managed file).
/// </summary>
/// <remarks>
/// The native menu itself is always mocked (<see cref="IFileLauncher"/>); the shell's effect on disk is
/// simulated with plain file-system calls. Most tests mock <see cref="IPanelItemStorageService"/>; the
/// end-to-end tests use the real <see cref="PanelItemStorageService"/> on a throw-away temp storage root
/// with a mocked <see cref="INativeApi"/>. <see cref="ChildWindowViewModel"/>'s constructor resolves view
/// templates from <see cref="System.Windows.Application.Current"/>, hence <see cref="WpfApplicationHost"/>.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public sealed class ChildWindowViewModelShellMenuTests : IDisposable
{
    private static readonly IntPtr OwnerHwnd = new(0x1234);

    private readonly Mock<IIconProvider> _iconProviderMock = new();
    private readonly Mock<IFileLauncher> _fileLauncherMock = new();
    private readonly Mock<IFolderWatcherFactory> _watcherFactoryMock = new();
    private readonly Mock<IPanelItemStorageService> _storageMock = new();
    private readonly Mock<IFileItemRenameService> _renameServiceMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IPanelSelectionCoordinator> _selectionCoordinatorMock = new();
    private readonly Mock<IGestureDiagnostics> _diagnosticsMock = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"DOrgShellMenu_{Guid.NewGuid():N}");

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModelShellMenuTests"/> class.
    /// </summary>
    public ChildWindowViewModelShellMenuTests()
    {
        WpfApplicationHost.EnsureApplication();
        Directory.CreateDirectory(_tempDir);
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((ImageSource)null!);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Right-click selects the item first and hands its path, the owner window and the physical cursor
    /// position to the native menu unchanged.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_SelectsItemAndForwardsPathOwnerAndCoordinates()
    {
        var path = NewFile("a.txt");
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        _fileLauncherMock.Setup(l => l.ShowContextMenu(path, OwnerHwnd, 150, 300, false)).Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Dismissed, null));

        sut.ShowShellContextMenu(item, OwnerHwnd, 150, 300);

        Assert.Same(item, sut.SelectedFile);
        _fileLauncherMock.Verify(l => l.ShowContextMenu(path, OwnerHwnd, 150, 300, false), Times.Once);
    }

    /// <summary>
    /// The shell's own "Rename" is handed to the panel's inline rename (the same editor F2 opens) instead of
    /// touching the file.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_RenameRequested_BeginsInlineRename()
    {
        var path = NewFile("report.txt");
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        SetupMenu(ShellContextMenuOutcome.RenameRequested);

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        Assert.Same(item, sut.EditingItem);
        Assert.Equal(item.DisplayName, sut.EditingName);
        _storageMock.Verify(s => s.ReconcileExternalChange(It.IsAny<string>()), Times.Never);
        _renameServiceMock.Verify(r => r.Rename(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A menu that was not shown or dismissed changed nothing on disk — no reconcile runs.
    /// </summary>
    /// <param name="outcome">The non-invoking outcome.</param>
    [Theory]
    [InlineData(ShellContextMenuOutcome.NotShown)]
    [InlineData(ShellContextMenuOutcome.Dismissed)]
    public void ShowShellContextMenu_NotInvoked_DoesNotReconcile(ShellContextMenuOutcome outcome)
    {
        using var sut = BuildSut();
        sut.AddFiles([Path.Combine(_tempDir, "missing.txt")]);
        var item = Assert.Single(sut.AllItems);
        SetupMenu(outcome);

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        _storageMock.Verify(s => s.ReconcileExternalChange(It.IsAny<string>()), Times.Never);
        Assert.Null(sut.EditingItem);
    }

    /// <summary>
    /// After an invoked command, a missing item the storage service reports as renamed is relocated in
    /// place (same <see cref="FileItem"/> instance, so selection/UI state survive) and the change is
    /// persisted through <see cref="ChildWindowViewModel.FilesChanged"/> exactly once.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_RenamedItem_RelocatesInPlaceAndRaisesFilesChangedOnce()
    {
        var oldPath = Path.Combine(_tempDir, "old.txt");
        var newPath = NewFile("new.txt");
        using var sut = BuildSut();
        sut.AddFiles([oldPath]);
        var item = Assert.Single(sut.AllItems);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;
        SetupMenu(ShellContextMenuOutcome.Invoked);
        _storageMock.Setup(s => s.ReconcileExternalChange(oldPath))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Renamed, newPath));

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        Assert.Same(item, Assert.Single(sut.AllItems));
        Assert.Equal(newPath, item.Path);
        Assert.True(item.Exists, "The existence refresh runs right after the reconcile.");
        Assert.Equal(1, filesChanged);
    }

    /// <summary>
    /// After an invoked command, a missing item the storage service reports as deleted leaves the panel.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_DeletedItem_RemovesItemAndRaisesFilesChangedOnce()
    {
        var path = Path.Combine(_tempDir, "deleted.txt");
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;
        SetupMenu(ShellContextMenuOutcome.Invoked);
        _storageMock.Setup(s => s.ReconcileExternalChange(path))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Deleted, null));

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        Assert.Empty(sut.AllItems);
        Assert.Empty(sut.FileItems);
        Assert.Equal(1, filesChanged);
    }

    /// <summary>
    /// Only missing items are reconciled; an unchanged verdict keeps the item (still missing) and persists
    /// nothing.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_OnlyMissingItemsAreReconciled_UnchangedKeepsItem()
    {
        var existing = NewFile("here.txt");
        var missing = Path.Combine(_tempDir, "missing.txt");
        using var sut = BuildSut();
        sut.AddFiles([existing, missing]);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;
        SetupMenu(ShellContextMenuOutcome.Invoked);

        sut.ShowShellContextMenu(sut.AllItems[0], OwnerHwnd, 0, 0);

        _storageMock.Verify(s => s.ReconcileExternalChange(existing), Times.Never);
        _storageMock.Verify(s => s.ReconcileExternalChange(missing), Times.Once);
        Assert.Equal(2, sut.AllItems.Count);
        Assert.Equal(0, filesChanged);
    }

    /// <summary>
    /// A Folder Portal mirrors a real folder through its watcher: the reconcile is skipped there.
    /// </summary>
    [Fact]
    public void ReconcileAfterShellCommand_Portal_NeverCallsStorage()
    {
        var portalDir = Path.Combine(_tempDir, "portal");
        Directory.CreateDirectory(portalDir);
        File.WriteAllText(Path.Combine(portalDir, "p.txt"), "x");
        _watcherFactoryMock.Setup(f => f.Create()).Returns(new Mock<IFolderWatcher>().Object);
        using var sut = BuildSut();
        sut.LoadFromModel(new ChildWindowModel { SourceFolderPath = portalDir });
        Assert.True(sut.IsPortal);
        File.Delete(Path.Combine(portalDir, "p.txt"));

        sut.ReconcileAfterShellCommand();

        _storageMock.Verify(s => s.ReconcileExternalChange(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// End to end with the real storage service on a temp root: the shell renames the managed copy inside
    /// its per-item folder; after the menu the panel item points at the new file and the manifest follows,
    /// so "restore to original location" still works under the new name.
    /// </summary>
    [Fact]
    public void EndToEnd_ShellRenamesManagedFile_ItemAndManifestFollow()
    {
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), new Mock<INativeApi>().Object);
        var source = NewFile("Invoice.pdf");
        var managed = storage.MoveIn(source)!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        var item = Assert.Single(sut.AllItems);

        var renamed = Path.Combine(Path.GetDirectoryName(managed)!, "Invoice 2026.pdf");
        _fileLauncherMock.Setup(l => l.ShowContextMenu(managed, OwnerHwnd, 10, 20, true))
            .Callback(() => File.Move(managed, renamed)) // what the shell's Rename would have done
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Invoked, null));

        sut.ShowShellContextMenu(item, OwnerHwnd, 10, 20);

        Assert.Equal(renamed, item.Path, ignoreCase: true);
        Assert.Equal(RestoreOutcome.Restored, storage.Restore(item.Path));
        Assert.True(File.Exists(Path.Combine(_tempDir, "Invoice 2026.pdf")));
    }

    /// <summary>
    /// End to end with the real storage service: the shell deletes the managed copy; after the menu the item
    /// is gone from the panel and its per-item folder is cleaned up.
    /// </summary>
    [Fact]
    public void EndToEnd_ShellDeletesManagedFile_ItemRemovedAndItemFolderCleanedUp()
    {
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), new Mock<INativeApi>().Object);
        var managed = storage.MoveIn(NewFile("Old.lnk"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        var item = Assert.Single(sut.AllItems);

        _fileLauncherMock.Setup(l => l.ShowContextMenu(managed, OwnerHwnd, 0, 0, true))
            .Callback(() => File.Delete(managed)) // what the shell's Delete would have done
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Invoked, null));

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        Assert.Empty(sut.AllItems);
        Assert.False(Directory.Exists(itemDir));
    }

    // ── M5 (release-polish 1.0 review): shell verbs vs managed storage ───────────────────────────────

    /// <summary>
    /// Only an app-managed item asks the menu to intercept "Delete"; a referenced-in-place item keeps the shell's
    /// own Delete.
    /// </summary>
    /// <param name="managed">Whether the storage service reports the item as managed.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShowShellContextMenu_InterceptsDeleteOnlyForManagedItems(bool managed)
    {
        var path = NewFile("item.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(managed);
        using var sut = BuildSut();
        sut.AddFiles([path]);
        SetupMenu(ShellContextMenuOutcome.Dismissed);

        sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);

        _fileLauncherMock.Verify(l => l.ShowContextMenu(path, OwnerHwnd, 0, 0, managed), Times.Once);
    }

    /// <summary>
    /// M5a: the intercepted Delete of a managed item goes through <see cref="IPanelItemStorageService.RecycleManagedItem"/>
    /// (back to the original location, recycled from there); a recycled item leaves the panel and the change is
    /// persisted once.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_DeleteRequested_Recycled_RemovesItem()
    {
        var path = NewFile("managed.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        _storageMock.Setup(s => s.RecycleManagedItem(path)).Returns(new ManagedRecycleResult(ManagedRecycleKind.Recycled, null));
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;
        SetupMenu(ShellContextMenuOutcome.DeleteRequested);

        sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);

        _storageMock.Verify(s => s.RecycleManagedItem(path), Times.Once);
        Assert.Empty(sut.AllItems);
        Assert.Equal(1, filesChanged);
    }

    /// <summary>
    /// M5a: when the shell did not recycle the item, it stays in the panel — following it to the path the storage
    /// service moved it back to.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_DeleteRequested_Kept_FollowsNewPath()
    {
        var path = NewFile("managed.txt");
        var newPath = NewFile("readopted.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        _storageMock.Setup(s => s.RecycleManagedItem(path)).Returns(new ManagedRecycleResult(ManagedRecycleKind.Kept, newPath));
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        SetupMenu(ShellContextMenuOutcome.DeleteRequested);

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        Assert.Same(item, Assert.Single(sut.AllItems));
        Assert.Equal(newPath, item.Path);
    }

    /// <summary>
    /// M5c: the managed target is reconciled against the directory snapshot taken BEFORE the menu, even though it
    /// still exists, and entries the command created are added to the panel as new items (FilesChanged once).
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_ManagedTarget_ReconcilesWithSnapshot_AndAddsAdoptedItems()
    {
        var path = NewFile("report.txt");
        var adopted = NewFile("report.zip");
        IReadOnlyList<string> snapshot = [path];
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        _storageMock.Setup(s => s.GetItemDirectoryEntries(path)).Returns(snapshot);
        _storageMock
            .Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null) { AdoptedPaths = [adopted] });
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;
        SetupMenu(ShellContextMenuOutcome.Invoked);

        sut.ShowShellContextMenu(sut.AllItems[0], OwnerHwnd, 0, 0);

        Assert.Equal(new[] { path, adopted }, sut.AllItems.Select(i => i.Path));
        Assert.Equal(1, filesChanged);
        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
    }

    /// <summary>
    /// M5b: Cut in the shell menu + Paste elsewhere happens after the menu closed. The item's present → missing
    /// transition is reconciled (snapshot-less) once confirmed across two consecutive liveness ticks (M-C, release-
    /// polish 1.0 final review — see <see cref="ChildWindowViewModel.ProcessPendingVanishedManagedItems"/>): the
    /// item leaves the panel only on the second tick, never the first.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_ManagedItemVanished_IsReconciledAndRemovedOnSecondTick()
    {
        var path = NewFile("cut-me.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        _storageMock.Setup(s => s.ReconcileExternalChange(path)).Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Deleted, null));
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var filesChanged = 0;
        sut.FilesChanged += (_, _) => filesChanged++;
        File.Delete(path);

        // First tick: only confirms the transition — never reconciles yet.
        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness();

        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
        Assert.Single(sut.AllItems);
        Assert.Equal(0, filesChanged);

        // Second tick: still missing — now reconciled and removed.
        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness();

        Assert.Empty(sut.AllItems);
        Assert.Equal(1, filesChanged);
    }

    /// <summary>
    /// M-C (release-polish 1.0 final review): a fast external save-by-recreate — the file reappears at the
    /// SAME path within a single liveness tick — must never be reconciled/released at all: the two-tick
    /// confirmation exists specifically to tell this apart from a genuine Cut+Paste-elsewhere.
    /// </summary>
    [Fact]
    public void ProcessPendingVanishedManagedItems_ItemReappearsWithinOneTick_NeverReconciled()
    {
        var path = NewFile("recreated.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        File.Delete(path);

        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness(); // transitions to missing, added to the pending set.
        Assert.False(item.Exists);

        // The external app's recreate lands before the next tick.
        File.WriteAllText(path, "recreated");

        sut.ProcessPendingVanishedManagedItems(); // must see it back and simply stop watching it.
        sut.RefreshItemsLiveness();

        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
        Assert.Same(item, Assert.Single(sut.AllItems));
        Assert.True(item.Exists);
    }

    /// <summary>
    /// M5b stays cheap: only the present → missing TRANSITION of a MANAGED item is reconciled — never a stable
    /// present item, never an item that was already missing, never a referenced-in-place original.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_OnlyTransitionOfManagedItemsIsReconciled()
    {
        var present = NewFile("present.txt");
        var alreadyMissing = Path.Combine(_tempDir, "already-missing.txt");
        var unmanaged = NewFile("unmanaged.txt");
        _storageMock.Setup(s => s.IsManaged(present)).Returns(true);
        _storageMock.Setup(s => s.IsManaged(alreadyMissing)).Returns(true);
        using var sut = BuildSut();
        sut.AddFiles([present, alreadyMissing, unmanaged]);
        File.Delete(unmanaged);

        sut.RefreshItemsLiveness();
        sut.RefreshItemsLiveness();

        _storageMock.Verify(s => s.ReconcileExternalChange(It.IsAny<string>()), Times.Never);
        Assert.Equal(3, sut.AllItems.Count);
    }

    /// <summary>
    /// End to end, M5b with the real storage service: the managed copy is moved away behind the panel's back (Cut +
    /// Paste); confirmed across two consecutive liveness ticks (M-C, release-polish 1.0 final review), the item is
    /// gone from the panel and its manifest entry released — but not yet after only the first tick.
    /// </summary>
    [Fact]
    public void EndToEnd_CutPasteAfterMenu_LivenessTickRemovesGhostAndReleasesManifest()
    {
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), new Mock<INativeApi>().Object);
        var managed = storage.MoveIn(NewFile("Moved.txt"))!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        File.Move(managed, Path.Combine(_tempDir, "Pasted.txt"));

        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness();

        Assert.Single(sut.AllItems);
        Assert.True(Directory.Exists(Path.GetDirectoryName(managed)), "must not release the per-item directory after only one tick.");

        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness();

        Assert.Empty(sut.AllItems);
        Assert.Empty(storage.FindManagedCandidates(Path.Combine(_tempDir, "Moved.txt")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(managed)));
    }

    /// <summary>
    /// End to end, M-C with the real storage service: an external editor's save-by-recreate (delete then
    /// rewrite the same path within one tick) must never trigger a release — the per-item directory and
    /// manifest entry survive, and the SAME <see cref="FileItem"/> keeps tracking it.
    /// </summary>
    [Fact]
    public void EndToEnd_SaveByRecreateWithinOneTick_NeverReleased()
    {
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), new Mock<INativeApi>().Object);
        var managed = storage.MoveIn(NewFile("Document.txt"))!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        var item = Assert.Single(sut.AllItems);

        File.Delete(managed); // the editor's "delete the original" half of a save-by-recreate.
        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness();
        Assert.False(item.Exists);
        Assert.True(Directory.Exists(Path.GetDirectoryName(managed)), "must not release while still within the confirmation window.");

        File.WriteAllText(managed, "recreated"); // ...and the "rewrite it back" half, before the next tick.
        sut.ProcessPendingVanishedManagedItems();
        sut.RefreshItemsLiveness();

        Assert.Same(item, Assert.Single(sut.AllItems));
        Assert.True(item.Exists);
        Assert.True(Directory.Exists(Path.GetDirectoryName(managed)));
        Assert.Equal(RestoreOutcome.Restored, storage.Restore(managed));
    }

    /// <summary>
    /// End to end, M5c with the real storage service: "Create shortcut" writes a new file next to the managed copy;
    /// after the menu the panel shows it as a new item living in a per-item directory of its own, and the original
    /// item is untouched.
    /// </summary>
    [Fact]
    public void EndToEnd_ShellCreatesSibling_ItIsAdoptedAsNewPanelItem()
    {
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), new Mock<INativeApi>().Object);
        var managed = storage.MoveIn(NewFile("Tool.exe"))!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        var item = Assert.Single(sut.AllItems);
        var created = Path.Combine(Path.GetDirectoryName(managed)!, "Tool.exe - Shortcut.lnk");
        _fileLauncherMock.Setup(l => l.ShowContextMenu(managed, OwnerHwnd, 0, 0, true))
            .Callback(() => File.WriteAllText(created, "lnk")) // what "Create shortcut" would have done
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Invoked, null));

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        Assert.Equal(2, sut.AllItems.Count);
        Assert.Equal(managed, item.Path);
        var adopted = sut.AllItems[1].Path;
        Assert.Equal("Tool.exe - Shortcut.lnk", Path.GetFileName(adopted));
        Assert.True(storage.IsManaged(adopted));
        Assert.NotEqual(Path.GetDirectoryName(managed), Path.GetDirectoryName(adopted), StringComparer.OrdinalIgnoreCase);
        Assert.False(File.Exists(created));
    }

    /// <summary>
    /// End to end, M5c: an entry the command is still writing when the menu returns (a background archiver) is
    /// picked up by a later liveness tick once it was closed.
    /// </summary>
    [Fact]
    public void EndToEnd_SiblingStillBeingWritten_IsAdoptedOnALaterTick()
    {
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), new Mock<INativeApi>().Object);
        var managed = storage.MoveIn(NewFile("Photos.txt"))!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        var archive = Path.Combine(Path.GetDirectoryName(managed)!, "Photos.zip");
        FileStream? writer = null;
        _fileLauncherMock.Setup(l => l.ShowContextMenu(managed, OwnerHwnd, 0, 0, true))
            .Callback(() => writer = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Invoked, null));

        try
        {
            sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);
            Assert.Single(sut.AllItems);

            sut.ProcessPendingSiblingAdoption(); // still open: not yet
            Assert.Single(sut.AllItems);
        }
        finally
        {
            writer?.Dispose();
        }

        sut.ProcessPendingSiblingAdoption();

        Assert.Equal(2, sut.AllItems.Count);
        Assert.Equal("Photos.zip", Path.GetFileName(sut.AllItems[1].Path));
    }

    /// <summary>
    /// End to end, M5a with the real storage service: the intercepted Delete restores the managed copy to its
    /// original location and recycles it from there (mocked Recycle Bin); the item leaves the panel.
    /// </summary>
    [Fact]
    public void EndToEnd_DeleteFromShellMenu_RecyclesFromOriginalLocation()
    {
        var nativeMock = new Mock<INativeApi>();
        string[]? recycled = null;
        nativeMock.Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Callback<string[], IntPtr>((paths, _) =>
            {
                recycled = paths;
                foreach (var p in paths) File.Delete(p);
            })
            .Returns(true);
        var storage = new PanelItemStorageService(Path.Combine(_tempDir, "store"), nativeMock.Object);
        var source = NewFile("Old.lnk");
        var managed = storage.MoveIn(source)!;
        using var sut = BuildSut(storage);
        sut.AddFiles([managed]);
        _fileLauncherMock.Setup(l => l.ShowContextMenu(managed, OwnerHwnd, 0, 0, true)).Returns(new ShellContextMenuResult(ShellContextMenuOutcome.DeleteRequested, null));

        sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);

        Assert.Empty(sut.AllItems);
        Assert.NotNull(recycled);
        Assert.Equal(source, Assert.Single(recycled!), ignoreCase: true);
        Assert.False(Directory.Exists(Path.GetDirectoryName(managed)));
    }

    // ── M-A (release-polish 1.0 final review): re-entrancy during recycle's modal UI ─────────────────

    /// <summary>
    /// A re-entrant <see cref="ChildWindowViewModel.ShowShellContextMenu"/> call arriving while this panel
    /// already has a shell operation's own modal loop in progress (simulated here by having the mocked
    /// native call itself re-enter <see cref="ChildWindowViewModel.ShowShellContextMenu"/>, standing in for
    /// a queued dispatcher operation reaching back in through the real modal loop's nested message pump) is
    /// dropped outright — the native menu is shown exactly once, not twice.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_ReentrantCallWhileShellOperationInProgress_IsDroppedAndNativeMenuShownOnce()
    {
        var path = NewFile("a.txt");
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        _fileLauncherMock
            .Setup(l => l.ShowContextMenu(path, OwnerHwnd, 0, 0, false))
            .Callback(() => sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0))
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Dismissed, null));

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        _fileLauncherMock.Verify(l => l.ShowContextMenu(path, OwnerHwnd, 0, 0, false), Times.Once);
    }

    /// <summary>
    /// M-A fix: when a re-entrant liveness tick already removed the item from the panel WHILE
    /// <see cref="IPanelItemStorageService.RecycleManagedItem"/>'s own modal loop was still showing (here,
    /// simulated directly via <see cref="BaseViewModel.RemoveFileByPath"/> from the mock's callback — the
    /// scenario this fix's <c>_shellOperationDepth</c> guard now prevents from happening for real), the
    /// item the storage service moved back to <c>Kept</c>'s path is no longer silently stranded
    /// (<see cref="BaseViewModel.TryRenameItem"/> would have no-op'd on an untracked item) — it is added
    /// back to the panel as a fresh item instead.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_DeleteRequested_Kept_ItemRemovedDuringRecycleCall_ReAddsAsNewItem()
    {
        var path = NewFile("managed.txt");
        var newPath = NewFile("readopted.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        using var sut = BuildSut();
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);
        _storageMock
            .Setup(s => s.RecycleManagedItem(path))
            .Callback(() => sut.RemoveFileByPath(path))
            .Returns(new ManagedRecycleResult(ManagedRecycleKind.Kept, newPath));
        SetupMenu(ShellContextMenuOutcome.DeleteRequested);

        sut.ShowShellContextMenu(item, OwnerHwnd, 0, 0);

        var readded = Assert.Single(sut.AllItems);
        Assert.NotSame(item, readded);
        Assert.Equal(newPath, readded.Path);
    }

    /// <summary>
    /// M-A fix: <see cref="ChildWindowViewModel.ProcessPendingSiblingAdoption"/> must not consume a tick of
    /// its watch budget while a shell operation's own modal loop is mid-flight on this panel (simulated
    /// here via <see cref="IPanelItemStorageService.RecycleManagedItem"/>'s own callback, standing in for a
    /// liveness tick sneaking in through the nested message pump).
    /// </summary>
    [Fact]
    public void ProcessPendingSiblingAdoption_ReentrantDuringDeleteManagedItem_DoesNotConsumeAWatchTick()
    {
        var target = NewFile("target.txt");
        var toDelete = NewFile("to-delete.txt");
        var sibling = NewFile("target.zip"); // pretend adoption candidate for the pending watch below.
        _storageMock.Setup(s => s.IsManaged(target)).Returns(true);
        _storageMock.Setup(s => s.IsManaged(toDelete)).Returns(true);
        _storageMock.Setup(s => s.GetItemDirectoryEntries(target)).Returns((IReadOnlyList<string>)[target]);
        using var sut = BuildSut();
        sut.AddFiles([target, toDelete]);

        // Arms a pending sibling watch for `target` via a normal Invoked command.
        SetupMenu(ShellContextMenuOutcome.Invoked, verb: "copy");
        sut.ShowShellContextMenu(sut.AllItems.First(i => i.Path == target), OwnerHwnd, 0, 0);

        _storageMock
            .Setup(s => s.RecycleManagedItem(toDelete))
            .Callback(() => sut.ProcessPendingSiblingAdoption())
            .Returns(new ManagedRecycleResult(ManagedRecycleKind.Recycled, null));
        SetupMenu(ShellContextMenuOutcome.DeleteRequested);

        sut.ShowShellContextMenu(sut.AllItems.First(i => i.Path == toDelete), OwnerHwnd, 0, 0);

        // ReconcileExternalChange(target, snapshot) fires exactly ONCE — the initial ShowShellContextMenu
        // call's own immediate diff, which always runs. Without the guard, the reentrant
        // ProcessPendingSiblingAdoption() call from inside RecycleManagedItem's callback would have
        // consumed a watch tick and called it a SECOND time.
        _storageMock.Verify(s => s.ReconcileExternalChange(target, It.IsAny<IReadOnlyCollection<string>>()), Times.Once);
    }

    // ── M-B (release-polish 1.0 final review): sibling adoption watch is verb-gated ───────────────────

    /// <summary>
    /// A non-file-creating verb (e.g. "Open") never arms the sibling-adoption watch: a file that shows up
    /// next to the item afterward is never adopted, even though the immediate post-command snapshot diff
    /// (which stays unconditional for every invoked verb) already ran.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_NonCreatingVerb_NeverArmsSiblingWatch()
    {
        var path = NewFile("report.txt");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        IReadOnlyList<string> snapshot = [path];
        _storageMock.Setup(s => s.GetItemDirectoryEntries(path)).Returns(snapshot);
        _storageMock
            .Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null));
        using var sut = BuildSut();
        sut.AddFiles([path]);
        SetupMenu(ShellContextMenuOutcome.Invoked, verb: "open");

        sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);

        // A sibling created right after the (non-creating) command returned would normally be picked up by
        // the watch on the next tick — verify there is nothing to pick up because no watch was armed.
        _storageMock.Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null) { AdoptedPaths = [NewFile("report.pdf")] });

        sut.ProcessPendingSiblingAdoption();

        Assert.Single(sut.AllItems); // nothing adopted — the watch was never armed.
    }

    /// <summary>
    /// A file-creating verb (e.g. "Copy", not one of the non-creating verbs) still arms the watch exactly as
    /// before this fix — a created sibling is adopted on a later tick.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_CreatingVerb_StillArmsSiblingWatch()
    {
        var path = NewFile("report.txt");
        var adopted = NewFile("report.zip");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        IReadOnlyList<string> snapshot = [path];
        _storageMock.Setup(s => s.GetItemDirectoryEntries(path)).Returns(snapshot);
        _storageMock
            .Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null));
        using var sut = BuildSut();
        sut.AddFiles([path]);
        SetupMenu(ShellContextMenuOutcome.Invoked, verb: "copy");

        sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);

        _storageMock.Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null) { AdoptedPaths = [adopted] });

        sut.ProcessPendingSiblingAdoption();

        Assert.Equal(new[] { path, adopted }, sut.AllItems.Select(i => i.Path));
    }

    /// <summary>
    /// An unreported verb (<see langword="null"/> — a shell extension that doesn't implement
    /// <c>GCS_VERBW</c>) is not treated as non-creating: the watch still arms, same as before this fix.
    /// </summary>
    [Fact]
    public void ShowShellContextMenu_Invoked_UnreportedVerb_StillArmsSiblingWatch()
    {
        var path = NewFile("report.txt");
        var adopted = NewFile("report.zip");
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        IReadOnlyList<string> snapshot = [path];
        _storageMock.Setup(s => s.GetItemDirectoryEntries(path)).Returns(snapshot);
        _storageMock
            .Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null));
        using var sut = BuildSut();
        sut.AddFiles([path]);
        SetupMenu(ShellContextMenuOutcome.Invoked, verb: null);

        sut.ShowShellContextMenu(Assert.Single(sut.AllItems), OwnerHwnd, 0, 0);

        _storageMock.Setup(s => s.ReconcileExternalChange(path, snapshot))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null) { AdoptedPaths = [adopted] });

        sut.ProcessPendingSiblingAdoption();

        Assert.Equal(new[] { path, adopted }, sut.AllItems.Select(i => i.Path));
    }

    private void SetupMenu(ShellContextMenuOutcome outcome, string? verb = null)
        => _fileLauncherMock
            .Setup(l => l.ShowContextMenu(It.IsAny<string>(), It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(new ShellContextMenuResult(outcome, verb));

    private string NewFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "content");
        return path;
    }

    private ChildWindowViewModel BuildSut(IPanelItemStorageService? storage = null) => new(
        _iconProviderMock.Object,
        _fileLauncherMock.Object,
        _watcherFactoryMock.Object,
        storage ?? _storageMock.Object,
        _renameServiceMock.Object,
        _appSettingsMock.Object,
        _selectionCoordinatorMock.Object,
        _diagnosticsMock.Object);
}
