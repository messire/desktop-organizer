using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="PanelItemStorageService.ReconcileExternalChange(string)"/> /
/// <see cref="PanelItemStorageService.ReconcileExternalChange(string, IReadOnlyCollection{string})"/> — the manifest
/// side of the post-menu reconcile that follows a native shell context-menu command (Rename, Delete, Cut, "Create
/// shortcut", …) acting directly on a managed item under <c>{storageRoot}\{guid}\{name}</c> — plus
/// <see cref="PanelItemStorageService.GetItemDirectoryEntries"/> and <see cref="PanelItemStorageService.RecycleManagedItem"/>.
/// </summary>
/// <remarks>
/// Every test runs against a real, throw-away storage root under the temp directory and a mocked
/// <see cref="INativeApi"/> (the service only needs it for the Recycle Bin, which these tests never reach).
/// The shell's own action is simulated with plain <see cref="File"/>/<see cref="Directory"/> calls.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class PanelItemStorageServiceReconcileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DOrgReconcile_{Guid.NewGuid():N}");
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgReconcileSrc_{Guid.NewGuid():N}");
    private readonly PanelItemStorageService _sut;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageServiceReconcileTests"/> class with a
    /// fresh storage root and source folder.
    /// </summary>
    public PanelItemStorageServiceReconcileTests()
    {
        Directory.CreateDirectory(_sourceDir);
        _sut = new PanelItemStorageService(_root, new Mock<INativeApi>().Object);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SafeDeleteDirectory(_root);
        SafeDeleteDirectory(_sourceDir);
    }

    /// <summary>
    /// The shell renamed the managed file in place: the single entry left in the per-item directory is
    /// reported as the new path, the manifest entry moves to it, and the recorded original path carries the
    /// new name — so a later Restore puts the item back under the name the user gave it.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_FileRenamedInPlace_ReturnsRenamedAndReKeysManifest()
    {
        var source = NewSourceFile("Report.txt");
        var managed = _sut.MoveIn(source)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var renamed = Path.Combine(Path.GetDirectoryName(managed)!, "Budget.txt");
        File.Move(managed, renamed);

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Equal(ManagedItemReconcileKind.Renamed, result.Kind);
        Assert.Equal(renamed, result.NewPath, ignoreCase: true);
        var manifest = ReadManifest();
        var entry = Assert.Single(manifest);
        Assert.Equal(renamed, entry.Key, ignoreCase: true);
        Assert.Equal(Path.Combine(_sourceDir, "Budget.txt"), entry.Value, ignoreCase: true);

        // End-to-end: Restore now works against the NEW managed path and uses the new name.
        Assert.Equal(RestoreOutcome.Restored, _sut.Restore(renamed));
        Assert.True(File.Exists(Path.Combine(_sourceDir, "Budget.txt")));
    }

    /// <summary>
    /// A renamed managed FOLDER is recognized the same way (the single entry is a directory).
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_FolderRenamedInPlace_ReturnsRenamed()
    {
        var sourceFolder = Path.Combine(_sourceDir, "Photos");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "a.jpg"), "x");
        var managed = _sut.MoveInDirectory(sourceFolder)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var renamed = Path.Combine(Path.GetDirectoryName(managed)!, "Holiday");
        Directory.Move(managed, renamed);

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Equal(ManagedItemReconcileKind.Renamed, result.Kind);
        Assert.Equal(renamed, result.NewPath, ignoreCase: true);
        Assert.Equal(Path.Combine(_sourceDir, "Holiday"), Assert.Single(ReadManifest()).Value, ignoreCase: true);
    }

    /// <summary>
    /// The shell deleted the managed file (Recycle Bin / Shift+Delete) or moved it away (Cut + Paste): the
    /// per-item directory is empty, so the item is reported deleted, its manifest entry released and the
    /// empty per-item directory cleaned up — the storage root itself stays.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_FileDeleted_ReturnsDeletedReleasesManifestAndRemovesItemDirectory()
    {
        var managed = _sut.MoveIn(NewSourceFile("Notes.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        File.Delete(managed);

        var result = _sut.ReconcileExternalChange(managed);

        Assert.Equal(ManagedItemReconcileKind.Deleted, result.Kind);
        Assert.Null(result.NewPath);
        Assert.Empty(ReadManifest());
        Assert.False(Directory.Exists(itemDir));
        Assert.True(Directory.Exists(_root));
    }

    /// <summary>
    /// The per-item directory itself is gone as well — still a deletion, manifest released.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_ItemDirectoryMissing_ReturnsDeleted()
    {
        var managed = _sut.MoveIn(NewSourceFile("Gone.txt"))!;
        Directory.Delete(Path.GetDirectoryName(managed)!, recursive: true);

        var result = _sut.ReconcileExternalChange(managed);

        Assert.Equal(ManagedItemReconcileKind.Deleted, result.Kind);
        Assert.Empty(ReadManifest());
    }

    /// <summary>
    /// The item is still there (Open, Copy, Properties, a cancelled Delete): nothing is touched.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_ItemStillExists_ReturnsUnchangedAndKeepsManifest()
    {
        var managed = _sut.MoveIn(NewSourceFile("Keep.txt"))!;

        var result = _sut.ReconcileExternalChange(managed);

        Assert.Equal(ManagedItemReconcileKind.Unchanged, result.Kind);
        Assert.Equal(managed, Assert.Single(ReadManifest()).Key, ignoreCase: true);
    }

    /// <summary>
    /// Two or more NEW entries in the per-item directory cannot be attributed to a single rename: the item is
    /// left alone (it keeps showing as missing) and the manifest is not touched — with or without a snapshot.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_TwoNewEntriesInItemDirectory_ReturnsUnchanged()
    {
        var managed = _sut.MoveIn(NewSourceFile("One.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        File.Move(managed, Path.Combine(itemDir, "Two.txt"));
        File.WriteAllText(Path.Combine(itemDir, "Three.txt"), "x");

        Assert.Equal(ManagedItemReconcileKind.Unchanged, _sut.ReconcileExternalChange(managed, before).Kind);
        Assert.Equal(ManagedItemReconcileKind.Unchanged, _sut.ReconcileExternalChange(managed).Kind);
        Assert.Equal(managed, Assert.Single(ReadManifest()).Key, ignoreCase: true);
    }

    /// <summary>
    /// M5c, rename rule WITHOUT a snapshot (the liveness-tick path): a single entry left next to a vanished item
    /// — e.g. an archive a shell command created earlier — must NEVER be taken for the renamed item. Before the
    /// fix the item (and its manifest entry) was re-keyed to the <c>.zip</c>.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_NoSnapshot_SingleLeftoverEntry_NeverInfersRename()
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var zip = Path.Combine(Path.GetDirectoryName(managed)!, "Report.zip");
        File.WriteAllText(zip, "archive");
        File.Delete(managed); // e.g. Cut + Paste elsewhere

        var result = _sut.ReconcileExternalChange(managed);

        Assert.Equal(ManagedItemReconcileKind.Unchanged, result.Kind);
        Assert.Equal(managed, Assert.Single(ReadManifest()).Key, ignoreCase: true);
        Assert.True(File.Exists(zip));
    }

    /// <summary>
    /// M5c, rename rule WITH a snapshot: an entry that already existed before the command is never the renamed
    /// item — the ONE entry that was not in the snapshot is, even with the old sibling still next to it (the old
    /// "exactly one entry" rule answered Unchanged here, or — with only the sibling left — the wrong file).
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_RenameIgnoresPreExistingSibling()
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        File.WriteAllText(Path.Combine(itemDir, "Report.zip"), "archive"); // pre-existing leftover
        var before = _sut.GetItemDirectoryEntries(managed);
        var renamed = Path.Combine(itemDir, "Final.txt");
        File.Move(managed, renamed);

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Equal(ManagedItemReconcileKind.Renamed, result.Kind);
        Assert.Equal(renamed, result.NewPath, ignoreCase: true);
        Assert.Equal(Path.Combine(_sourceDir, "Final.txt"), ReadManifest()[renamed], ignoreCase: true);
    }

    /// <summary>
    /// M5c: the item vanished and NOTHING new appeared — only a pre-existing sibling is left. That is a deletion,
    /// never a rename to the sibling.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_ItemGoneOnlyPreExistingSiblingLeft_ReturnsDeleted()
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var zip = Path.Combine(Path.GetDirectoryName(managed)!, "Report.zip");
        File.WriteAllText(zip, "archive");
        var before = _sut.GetItemDirectoryEntries(managed);
        File.Delete(managed);

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Equal(ManagedItemReconcileKind.Deleted, result.Kind);
        Assert.Null(result.NewPath);
        Assert.Empty(ReadManifest());
        Assert.True(File.Exists(zip), "A sibling is never deleted by the reconcile.");
    }

    /// <summary>
    /// M5c, adoption: a command created an entry next to a still-existing item ("Create shortcut"). It is moved into
    /// a per-item directory of its own and registered with the item's original directory + its own name as its
    /// original path — so "Remove from panel" puts it next to where the item came from. The item is untouched.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_CreatedSiblingNextToExistingItem_IsAdopted()
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var shortcut = Path.Combine(itemDir, "Report.txt - Shortcut.lnk");
        File.WriteAllText(shortcut, "lnk");

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Equal(ManagedItemReconcileKind.Unchanged, result.Kind);
        var adopted = Assert.Single(result.AdoptedPaths);
        Assert.Equal("Report.txt - Shortcut.lnk", Path.GetFileName(adopted));
        Assert.NotEqual(itemDir, Path.GetDirectoryName(adopted), StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(adopted));
        Assert.False(File.Exists(shortcut));
        Assert.True(File.Exists(managed));
        Assert.Equal(new[] { managed }, _sut.GetItemDirectoryEntries(managed), StringComparer.OrdinalIgnoreCase);

        var manifest = ReadManifest();
        Assert.Equal(2, manifest.Count);
        Assert.Equal(Path.Combine(_sourceDir, "Report.txt - Shortcut.lnk"), manifest[adopted], ignoreCase: true);

        Assert.Equal(RestoreOutcome.Restored, _sut.Restore(adopted));
        Assert.True(File.Exists(Path.Combine(_sourceDir, "Report.txt - Shortcut.lnk")));
    }

    // ── M-B (release-polish 1.0 final review): sibling adoption skips files the user didn't create ──────

    /// <summary>
    /// M-B: a Hidden or System-attributed entry (e.g. <c>desktop.ini</c>/<c>Thumbs.db</c> carry both) created next to
    /// a managed item is never adopted — it is something the shell/a third-party app wrote on its own, not a "Create
    /// shortcut"/"Send to" result the user asked for.
    /// </summary>
    /// <param name="attributes">The attribute(s) the created entry carries.</param>
    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    [InlineData(FileAttributes.Temporary)]
    public void ReconcileExternalChange_Snapshot_CreatedEntryWithExcludedAttribute_IsNeverAdopted(FileAttributes attributes)
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var created = Path.Combine(itemDir, "companion.dat");
        File.WriteAllText(created, "x");
        File.SetAttributes(created, File.GetAttributes(created) | attributes);

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Empty(result.AdoptedPaths);
        Assert.True(File.Exists(created), "left exactly where it was — never moved into managed storage.");
        Assert.True(File.Exists(managed));
    }

    /// <summary>
    /// M-B: a created entry whose NAME matches a known lock/backup/shell-bookkeeping pattern (LibreOffice's
    /// <c>.~lock.*#</c>, Microsoft Office's <c>~$*</c>, a generic editor's <c>*.tmp</c>, <c>desktop.ini</c>,
    /// <c>Thumbs.db</c>) is never adopted, regardless of case.
    /// </summary>
    /// <param name="createdName">The excluded entry's bare file name.</param>
    [Theory]
    [InlineData(".~lock.Report.txt#")]
    [InlineData("~$Report.txt")]
    [InlineData("staging.TMP")]
    [InlineData("DESKTOP.INI")]
    [InlineData("thumbs.db")]
    public void ReconcileExternalChange_Snapshot_CreatedEntryWithExcludedName_IsNeverAdopted(string createdName)
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var created = Path.Combine(itemDir, createdName);
        File.WriteAllText(created, "x");

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Empty(result.AdoptedPaths);
        Assert.True(File.Exists(created), "left exactly where it was — never moved into managed storage.");
    }

    /// <summary>
    /// M-B: an ordinary created entry that matches none of the exclusions (e.g. a genuine "Create shortcut"
    /// result) is unaffected by the exclusion list — still adopted exactly as before this fix.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_CreatedEntryNotExcluded_IsStillAdopted()
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var created = Path.Combine(itemDir, "Report - Copy.txt");
        File.WriteAllText(created, "x");

        var result = _sut.ReconcileExternalChange(managed, before);

        Assert.Single(result.AdoptedPaths);
    }

    /// <summary>
    /// MINOR 2 (release-polish 1.0 final review): an UNTRUSTED manifest (a transient disk read failure — here,
    /// simulated with a corrupt on-disk manifest a fresh service instance fails to parse) must defer adoption
    /// entirely rather than adopting a created sibling with no recorded origin, which would have permanently lost
    /// the ability to trace it back to where it should be restored.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_UntrustedManifest_DefersAdoptionEntirely()
    {
        var managed = _sut.MoveIn(NewSourceFile("Report.txt"))!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var created = Path.Combine(itemDir, "Report.txt - Shortcut.lnk");
        File.WriteAllText(created, "lnk");

        // Corrupts the on-disk manifest so a FRESH instance's very first read fails and stays untrusted for
        // its whole lifetime (EnsureManifestCacheUnlocked never caches a failed read).
        File.WriteAllText(Path.Combine(_root, PanelItemStorageService.ManifestFileName), "{ not valid json");
        var untrusted = new PanelItemStorageService(_root, new Mock<INativeApi>().Object);

        var result = untrusted.ReconcileExternalChange(managed, before);

        Assert.Empty(result.AdoptedPaths);
        Assert.True(File.Exists(created), "deferred, not adopted — left exactly where it was.");
    }

    /// <summary>
    /// M5c, adoption waits for a writer: a created file still held open (an archiver / the zip folder writing it)
    /// is not moved; the next call with the same snapshot adopts it once it was closed.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_CreatedFileStillOpen_IsAdoptedOnlyAfterItIsClosed()
    {
        var managed = _sut.MoveIn(NewSourceFile("Photos.txt"))!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var archive = Path.Combine(Path.GetDirectoryName(managed)!, "Photos.zip");

        ManagedItemReconcileResult first;
        using (var writer = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.WriteByte(1);
            first = _sut.ReconcileExternalChange(managed, before);
        }

        var second = _sut.ReconcileExternalChange(managed, before);

        Assert.Empty(first.AdoptedPaths);
        Assert.Equal("Photos.zip", Path.GetFileName(Assert.Single(second.AdoptedPaths)));
    }

    /// <summary>
    /// M5c, adoption of a created DIRECTORY ("Extract to folder") waits until it stopped changing — a half-extracted
    /// folder must not be split.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_Snapshot_CreatedDirectory_IsAdoptedOnlyOnceQuiet()
    {
        var managed = _sut.MoveIn(NewSourceFile("Archive.zip"))!;
        var before = _sut.GetItemDirectoryEntries(managed);
        var extracted = Path.Combine(Path.GetDirectoryName(managed)!, "Archive");
        Directory.CreateDirectory(extracted);
        var innerFile = Path.Combine(extracted, "inner.txt");
        File.WriteAllText(innerFile, "x");

        var busy = _sut.ReconcileExternalChange(managed, before);

        // MINOR 1 (release-polish 1.0 final review): "quiet" is the max LastWriteTimeUtc over the WHOLE
        // tree, not just the top-level folder — backdating only the folder itself (leaving inner.txt's own
        // recent write time untouched) must still report busy.
        Directory.SetLastWriteTimeUtc(extracted, DateTime.UtcNow.AddMinutes(-1));
        var stillBusy = _sut.ReconcileExternalChange(managed, before);

        File.SetLastWriteTimeUtc(innerFile, DateTime.UtcNow.AddMinutes(-1));
        var quiet = _sut.ReconcileExternalChange(managed, before);

        Assert.Empty(busy.AdoptedPaths);
        Assert.Empty(stillBusy.AdoptedPaths);
        var adopted = Assert.Single(quiet.AdoptedPaths);
        Assert.True(File.Exists(Path.Combine(adopted, "inner.txt")));
    }

    /// <summary>
    /// The snapshot lists the per-item directory of a per-item managed item only.
    /// </summary>
    [Fact]
    public void GetItemDirectoryEntries_PerItemOnly()
    {
        var managed = _sut.MoveIn(NewSourceFile("Snap.txt"))!;
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "legacy.lnk");
        File.WriteAllText(legacy, "x");

        Assert.Equal(new[] { managed }, _sut.GetItemDirectoryEntries(managed), StringComparer.OrdinalIgnoreCase);
        Assert.Empty(_sut.GetItemDirectoryEntries(legacy));
        Assert.Empty(_sut.GetItemDirectoryEntries(Path.Combine(_sourceDir, "x.txt")));
        Assert.Empty(_sut.GetItemDirectoryEntries(string.Empty));
    }

    /// <summary>
    /// A path outside managed storage (a referenced-in-place original) is never reconciled, even when missing.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_NotManaged_ReturnsUnchanged()
    {
        var result = _sut.ReconcileExternalChange(Path.Combine(_sourceDir, "missing.txt"));

        Assert.Equal(ManagedItemReconcileKind.Unchanged, result.Kind);
    }

    /// <summary>
    /// A legacy flat-layout item (directly under the storage root, no per-item directory) must never read
    /// the storage root's other entries as "the renamed item" — even when exactly one other entry exists.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_LegacyFlatItem_ReturnsUnchangedEvenWithOneSibling()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "legacy.lnk");
        File.WriteAllText(Path.Combine(_root, "other.lnk"), "x");

        var result = _sut.ReconcileExternalChange(legacy);

        Assert.Equal(ManagedItemReconcileKind.Unchanged, result.Kind);
        Assert.True(File.Exists(Path.Combine(_root, "other.lnk")));
    }

    /// <summary>
    /// A path deeper than <c>{root}\{guid}\{name}</c> is content inside a managed folder, not a panel item —
    /// its siblings belong to that folder and must never be read as a rename.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_PathInsideManagedFolder_ReturnsUnchanged()
    {
        var sourceFolder = Path.Combine(_sourceDir, "Docs");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "inner.txt"), "x");
        var managedFolder = _sut.MoveInDirectory(sourceFolder)!;

        var result = _sut.ReconcileExternalChange(Path.Combine(managedFolder, "missing.txt"));

        Assert.Equal(ManagedItemReconcileKind.Unchanged, result.Kind);
        Assert.True(File.Exists(Path.Combine(managedFolder, "inner.txt")));
    }

    /// <summary>
    /// A legacy managed item without a manifest entry that was renamed in its per-item directory is still
    /// reported renamed (the panel must follow the file) — there is simply no entry to move.
    /// </summary>
    [Fact]
    public void ReconcileExternalChange_RenamedItemWithoutManifestEntry_ReturnsRenamed()
    {
        var itemDir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(itemDir);
        var oldPath = Path.Combine(itemDir, "old.txt");
        var newPath = Path.Combine(itemDir, "new.txt");
        File.WriteAllText(oldPath, "x");
        var before = _sut.GetItemDirectoryEntries(oldPath);
        File.Move(oldPath, newPath);

        var result = _sut.ReconcileExternalChange(oldPath, before);

        Assert.Equal(ManagedItemReconcileKind.Renamed, result.Kind);
        Assert.Equal(newPath, result.NewPath, ignoreCase: true);
        Assert.Empty(ReadManifest());
    }

    /// <summary>
    /// Null/blank/malformed input never throws and is never reconciled.
    /// </summary>
    /// <param name="path">The input path.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\bad|path\\x.txt")]
    public void ReconcileExternalChange_BlankOrMalformed_ReturnsUnchanged(string path)
    {
        Assert.Equal(ManagedItemReconcileKind.Unchanged, _sut.ReconcileExternalChange(path).Kind);
        Assert.Equal(ManagedItemReconcileKind.Unchanged, _sut.ReconcileExternalChange(path, []).Kind);
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; must not mask the real assertion failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private string NewSourceFile(string name)
    {
        var path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, "content");
        return path;
    }

    private Dictionary<string, string> ReadManifest()
    {
        var manifestPath = Path.Combine(_root, PanelItemStorageService.ManifestFileName);
        if (!File.Exists(manifestPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? [];
        return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
    }
}
