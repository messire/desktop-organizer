using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="PanelItemStorageService.CopyManaged"/>/<see cref="PanelItemStorageService.DiscardCopies"/>
/// (ADR-045, «Panel clone to page», backlog 1.x §8a) — the storage half of "Copy panel to another page".
/// </summary>
/// <remarks>
/// The user's core requirement (backlog 1.x task): a clone must be fully independent from its source —
/// new GUID directory, own manifest entry, no shared references — so acting on one (recycle, restore,
/// delete) never affects the other. These tests exercise exactly that independence, plus the
/// transactional rollback contract (<see cref="PanelItemStorageService.DiscardCopies"/> undoes only the
/// copies it itself is handed).
///
/// <c>TooLarge</c>/<c>InsufficientSpace</c> are deliberately NOT exercised here against the real
/// filesystem — triggering them for real would need either 20,000+ real files (the directory
/// <c>MaxDirectoryImportFileCount</c> cap) or a multi-gigabyte payload / an artificially full volume
/// (<c>MaxDirectoryImportBytes</c> cap, <c>DriveInfo.AvailableFreeSpace</c>), both slow and/or
/// non-deterministic across machines — exactly the "don't write a fragile test for a native/filesystem
/// limit" case. The outcome MAPPING onto <c>PanelCloneOutcome</c> is covered deterministically via a
/// mocked <see cref="IPanelItemStorageService"/> in <c>WindowManagerCloneTests</c> instead.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class PanelItemStorageServiceCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DOrgCopyMgd_{Guid.NewGuid():N}");
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgCopyMgdSrc_{Guid.NewGuid():N}");
    private readonly Mock<INativeApi> _nativeMock = new();
    private readonly PanelItemStorageService _sut;

    /// <summary>Initializes a new instance of the <see cref="PanelItemStorageServiceCopyTests"/> class.</summary>
    public PanelItemStorageServiceCopyTests()
    {
        Directory.CreateDirectory(_sourceDir);
        _sut = new PanelItemStorageService(_root, _nativeMock.Object);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SafeDeleteDirectory(_root);
        SafeDeleteDirectory(_sourceDir);
    }

    // ── CopyManaged: success ─────────────────────────────────────────────────

    /// <summary>
    /// A managed FILE is duplicated into a brand-new per-item directory with the same bare name; the
    /// source is left completely untouched (still readable, unchanged content).
    /// </summary>
    [Fact]
    public void CopyManaged_File_CreatesNewPerItemDirectory_SameNameIdenticalContent_SourceUntouched()
    {
        var source = NewSourceFile("Invoice.pdf", "original-bytes");
        var managed = _sut.MoveIn(source)!;

        var result = _sut.CopyManaged(managed);

        Assert.Equal(ManagedCopyOutcome.Copied, result.Outcome);
        Assert.NotNull(result.CopyPath);
        Assert.NotEqual(managed, result.CopyPath, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(
            Path.GetDirectoryName(managed),
            Path.GetDirectoryName(result.CopyPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFileName(managed), Path.GetFileName(result.CopyPath));
        Assert.True(File.Exists(result.CopyPath));
        Assert.Equal("original-bytes", File.ReadAllText(result.CopyPath!));

        // Source of the copy (the managed item CopyManaged was called on) is read-only for the whole call.
        Assert.True(File.Exists(managed), "CopyManaged must never remove the item it copied.");
        Assert.Equal("original-bytes", File.ReadAllText(managed));
    }

    /// <summary>
    /// A managed DIRECTORY is duplicated recursively (every nested file) into a brand-new per-item
    /// directory; the source tree is left completely untouched.
    /// </summary>
    [Fact]
    public void CopyManaged_Directory_CreatesNewPerItemDirectory_RecursiveCopy_SourceUntouched()
    {
        var sourceFolder = Path.Combine(_sourceDir, "Photos");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "nested"));
        File.WriteAllText(Path.Combine(sourceFolder, "a.jpg"), "a-bytes");
        File.WriteAllText(Path.Combine(sourceFolder, "nested", "b.jpg"), "b-bytes");
        var managed = _sut.MoveInDirectory(sourceFolder)!;

        var result = _sut.CopyManaged(managed);

        Assert.Equal(ManagedCopyOutcome.Copied, result.Outcome);
        Assert.NotNull(result.CopyPath);
        Assert.NotEqual(managed, result.CopyPath, StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(result.CopyPath));
        Assert.Equal("a-bytes", File.ReadAllText(Path.Combine(result.CopyPath!, "a.jpg")));
        Assert.Equal("b-bytes", File.ReadAllText(Path.Combine(result.CopyPath!, "nested", "b.jpg")));

        Assert.True(Directory.Exists(managed), "CopyManaged must never remove the directory it copied.");
        Assert.Equal("a-bytes", File.ReadAllText(Path.Combine(managed, "a.jpg")));
        Assert.Equal("b-bytes", File.ReadAllText(Path.Combine(managed, "nested", "b.jpg")));
    }

    /// <summary>
    /// The copy's manifest entry records the SAME original path as the source's own entry (ADR-045) — both
    /// are live, independent entries; neither is a stand-in for the other.
    /// </summary>
    [Fact]
    public void CopyManaged_ManifestRecordsBothEntriesWithSameOriginalPath()
    {
        var source = NewSourceFile("Notes.txt");
        var managed = _sut.MoveIn(source)!;

        var result = _sut.CopyManaged(managed);

        var manifest = ReadManifest();
        Assert.Equal(2, manifest.Count);
        Assert.True(manifest.ContainsKey(managed));
        Assert.True(manifest.ContainsKey(result.CopyPath!));
        Assert.Equal(manifest[managed], manifest[result.CopyPath!], ignoreCase: true);
        Assert.Equal(source, manifest[managed], ignoreCase: true);
    }

    // ── CopyManaged: rejections that never touch anything ───────────────────

    /// <summary>A path outside managed storage is refused outright and never touched.</summary>
    [Fact]
    public void CopyManaged_NotManagedPath_ReturnsNotManaged_NoChanges()
    {
        var outside = NewSourceFile("outside.txt");

        var result = _sut.CopyManaged(outside);

        Assert.Equal(ManagedCopyOutcome.NotManaged, result.Outcome);
        Assert.Null(result.CopyPath);
        Assert.True(File.Exists(outside));
    }

    /// <summary>A managed path whose file/folder is already gone from disk is refused.</summary>
    [Fact]
    public void CopyManaged_SourceMissing_ReturnsSourceMissing()
    {
        var source = NewSourceFile("Gone.txt");
        var managed = _sut.MoveIn(source)!;
        File.Delete(managed); // vanished without going through this service (external delete).

        var result = _sut.CopyManaged(managed);

        Assert.Equal(ManagedCopyOutcome.SourceMissing, result.Outcome);
        Assert.Null(result.CopyPath);
    }

    /// <summary>
    /// M-2 fix (code review, panel clone to page): a legacy managed item imported before Move semantics
    /// existed has no manifest entry of its own — there is no original path to duplicate onto a copy, so
    /// the copy simply gets no manifest entry either (legacy too, exactly like the source) rather than
    /// refusing the clone entirely or inventing an original path for it.
    /// </summary>
    [Fact]
    public void CopyManaged_LegacyItemWithNoManifestEntry_CopiesAsLegacyItem_NoManifestEntryForCopy()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, $"Legacy_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(legacy, "legacy-content");

        // No manifest entry recorded for `legacy` — simulates a pre-per-item-directory import. M-2 fix
        // (code review, panel clone to page): this is a TRUSTED read that found no entry, so the copy
        // proceeds — the copy's own manifest state mirrors the source's (legacy, no entry).
        var result = _sut.CopyManaged(legacy);

        Assert.Equal(ManagedCopyOutcome.Copied, result.Outcome);
        Assert.NotNull(result.CopyPath);
        Assert.True(File.Exists(result.CopyPath), "The copy must actually be created on disk.");
        Assert.Equal("legacy-content", File.ReadAllText(result.CopyPath!));
        Assert.True(File.Exists(legacy), "The legacy source item itself must be left untouched.");
        Assert.False(ReadManifest().ContainsKey(result.CopyPath!), "The copy gets no manifest entry either — it is legacy too, exactly like the source.");
    }

    // ── CopyManaged: mid-operation failures roll back cleanly ────────────────

    /// <summary>
    /// If the manifest write for the new copy's entry fails, the just-created copy is deleted (a fresh
    /// duplicate, never the original) and <see cref="ManagedCopyOutcome.Failed"/> is returned — the source
    /// item and its own manifest entry are untouched.
    /// </summary>
    [Fact]
    public void CopyManaged_ManifestWriteFails_DeletesPartialCopy_ReturnsFailed()
    {
        var source = NewSourceFile("Report.docx", "report-bytes");
        var managed = _sut.MoveIn(source)!;

        // The manifest cache is already populated in memory from the MoveIn call above, so the
        // "read the source's own original path" step below still succeeds from cache — only the WRITE for
        // the new copy's entry needs to fail. Replacing the on-disk manifest FILE with a directory makes
        // WriteManifestUnlocked's own File.Move(tempPath, _manifestPath, overwrite: true) throw.
        var manifestPath = Path.Combine(_root, PanelItemStorageService.ManifestFileName);
        File.Delete(manifestPath);
        Directory.CreateDirectory(manifestPath);
        var directoriesBefore = Directory.GetDirectories(_root).Length;

        var result = _sut.CopyManaged(managed);

        Assert.Equal(ManagedCopyOutcome.Failed, result.Outcome);
        Assert.Null(result.CopyPath);
        Assert.True(File.Exists(managed), "The item CopyManaged was called on must survive a manifest write failure.");
        Assert.Equal("report-bytes", File.ReadAllText(managed));

        // No stray per-item directory left behind for the failed copy: only the source's own directory
        // (plus the manifest-file-replaced-by-a-directory itself) exist under root.
        Assert.Equal(directoriesBefore, Directory.GetDirectories(_root).Length);
    }

    /// <summary>
    /// A locked source file makes the verified copy fail (mirrors the existing MoveIn cross-volume copy
    /// failure tests) — the item CopyManaged was called on survives, and no partial copy is left behind.
    /// </summary>
    [Fact]
    public void CopyManaged_File_CopyFails_ReturnsFailed_SourceSurvives_NoPartialCopy()
    {
        var source = NewSourceFile("Locked.txt", "must-survive");
        var managed = _sut.MoveIn(source)!;
        var directoriesBefore = Directory.GetDirectories(_root).Length;

        ManagedCopyResult result;
        using (var lockHandle = new FileStream(managed, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = _sut.CopyManaged(managed);
            lockHandle.Close();
        }

        Assert.Equal(ManagedCopyOutcome.Failed, result.Outcome);
        Assert.Null(result.CopyPath);
        Assert.True(File.Exists(managed));
        Assert.Equal("must-survive", File.ReadAllText(managed));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(_root).Length);
    }

    /// <summary>
    /// Directory counterpart: a locked file inside the managed directory aborts the recursive verified
    /// copy partway through — the whole source directory survives and the partial copy is cleaned up.
    /// </summary>
    [Fact]
    public void CopyManaged_Directory_CopyFails_ReturnsFailed_SourceSurvives()
    {
        var sourceFolder = Path.Combine(_sourceDir, "Locked");
        Directory.CreateDirectory(sourceFolder);
        var fileA = Path.Combine(sourceFolder, "a.txt");
        var fileB = Path.Combine(sourceFolder, "b.txt");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");
        var managed = _sut.MoveInDirectory(sourceFolder)!;
        var managedFileB = Path.Combine(managed, "b.txt");
        var directoriesBefore = Directory.GetDirectories(_root).Length;

        ManagedCopyResult result;
        using (var lockHandle = new FileStream(managedFileB, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = _sut.CopyManaged(managed);
            lockHandle.Close();
        }

        Assert.Equal(ManagedCopyOutcome.Failed, result.Outcome);
        Assert.True(Directory.Exists(managed), "The directory CopyManaged was called on must survive a failed copy.");
        Assert.Equal("a", File.ReadAllText(Path.Combine(managed, "a.txt")));
        Assert.Equal("b", File.ReadAllText(managedFileB));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(_root).Length);
    }

    // ── Independence: acting on one copy never affects the other ────────────

    /// <summary>
    /// Recycling the COPY never touches the source: this is the user's core requirement for "Copy panel to
    /// another page" — deleting a clone's item must never delete the original panel's own item.
    /// </summary>
    [Fact]
    public void CopyManaged_ThenReleaseToRecycleBinOnCopy_DoesNotAffectSource()
    {
        SetupRecycle(deletes: true);
        var source = NewSourceFile("Shared.txt", "shared-bytes");
        var managed = _sut.MoveIn(source)!;
        var copy = _sut.CopyManaged(managed).CopyPath!;

        var outcome = _sut.ReleaseToRecycleBin([copy]);

        Assert.True(outcome.IsComplete);
        Assert.False(File.Exists(copy), "The copy must be gone.");
        Assert.True(File.Exists(managed), "Recycling the COPY must never touch the source.");
        Assert.Equal("shared-bytes", File.ReadAllText(managed));
        Assert.True(ReadManifest().ContainsKey(managed), "The source's own manifest entry must survive.");
        Assert.False(ReadManifest().ContainsKey(copy));
    }

    /// <summary>
    /// Restoring BOTH copies (they share the same recorded original path, ADR-045 "Known limitation") puts
    /// the first one back exactly where it came from and the second one under Explorer's own
    /// collision-free "(2)" name — proving the two restores are independent operations that never clobber
    /// each other.
    /// </summary>
    [Fact]
    public void CopyManaged_ThenRestoreBothCopies_SecondGetsCollisionSuffix()
    {
        var source = NewSourceFile("Duplicate.txt", "dup-bytes");
        var managed = _sut.MoveIn(source)!;
        var copy = _sut.CopyManaged(managed).CopyPath!;

        var firstRestore = _sut.Restore(managed);
        var secondRestore = _sut.Restore(copy);

        Assert.Equal(RestoreOutcome.Restored, firstRestore);
        Assert.Equal(RestoreOutcome.Restored, secondRestore);
        Assert.True(File.Exists(source));
        var collisionPath = Path.Combine(_sourceDir, "Duplicate (2).txt");
        Assert.True(File.Exists(collisionPath));
        Assert.Equal("dup-bytes", File.ReadAllText(source));
        Assert.Equal("dup-bytes", File.ReadAllText(collisionPath));
    }

    /// <summary>
    /// Once a copy exists, the manifest legitimately maps one original path to two live managed copies —
    /// <see cref="PanelItemStorageService.TryFindManagedPath"/> correctly refuses to guess (M1 principle,
    /// already covered generically in <c>PanelItemStorageServiceTests</c>), while
    /// <see cref="PanelItemStorageService.FindManagedCandidates"/> still reports both.
    /// </summary>
    [Fact]
    public void CopyManaged_MakesTryFindManagedPathAmbiguous_ButFindManagedCandidatesReturnsBoth()
    {
        var source = NewSourceFile("Ambiguous.txt");
        var managed = _sut.MoveIn(source)!;
        var copy = _sut.CopyManaged(managed).CopyPath!;

        var found = _sut.TryFindManagedPath(source, out var resolved);
        var candidates = _sut.FindManagedCandidates(source);

        Assert.False(found);
        Assert.Null(resolved);
        Assert.Equal(2, candidates.Count);
        Assert.Contains(managed, candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(copy, candidates, StringComparer.OrdinalIgnoreCase);
    }

    // ── DiscardCopies ─────────────────────────────────────────────────────────

    /// <summary>
    /// Removes exactly the copies it is handed (content + manifest entry); a second, un-discarded copy —
    /// and its manifest entry — are left completely intact.
    /// </summary>
    [Fact]
    public void DiscardCopies_RemovesOnlyGivenCopies_LeavesOtherCopyAndManifestIntact()
    {
        var sourceA = NewSourceFile("A.txt");
        var sourceB = NewSourceFile("B.txt");
        var managedA = _sut.MoveIn(sourceA)!;
        var managedB = _sut.MoveIn(sourceB)!;
        var copyA = _sut.CopyManaged(managedA).CopyPath!;
        var copyB = _sut.CopyManaged(managedB).CopyPath!;

        _sut.DiscardCopies([copyA]);

        Assert.False(File.Exists(copyA), "The discarded copy's content must be gone.");
        Assert.False(Directory.Exists(Path.GetDirectoryName(copyA)), "The discarded copy's now-empty GUID directory must be cleaned up.");
        Assert.False(ReadManifest().ContainsKey(copyA), "The discarded copy's manifest entry must be removed.");

        Assert.True(File.Exists(copyB), "An un-discarded copy must be left untouched.");
        Assert.True(ReadManifest().ContainsKey(copyB));
        Assert.True(File.Exists(managedA), "DiscardCopies must never touch the ORIGINAL managed item, only the copy.");
        Assert.True(File.Exists(managedB));
        Assert.True(ReadManifest().ContainsKey(managedA));
        Assert.True(ReadManifest().ContainsKey(managedB));
    }

    /// <summary>
    /// The storage root itself, a bare per-item GUID wrapper directory, and a path entirely outside
    /// managed storage are all left completely untouched — same M8-style guard
    /// <see cref="PanelItemStorageService.ReleaseToRecycleBin"/> already applies.
    /// </summary>
    [Fact]
    public void DiscardCopies_StorageRootAndGuidWrapperAndOutsidePath_NeverTouched()
    {
        var source = NewSourceFile("Wrapped.txt");
        var managed = _sut.MoveIn(source)!;
        var itemDir = Path.GetDirectoryName(managed)!;
        var outside = NewSourceFile("outside.txt");

        _sut.DiscardCopies([_root, itemDir, outside]);

        Assert.True(Directory.Exists(_root), "The storage root itself must never be deleted.");
        Assert.True(Directory.Exists(itemDir), "A bare per-item GUID wrapper directory must never be deleted directly.");
        Assert.True(File.Exists(managed), "The real managed item inside the wrapper must survive.");
        Assert.True(File.Exists(outside), "A path outside managed storage must never be touched.");
        Assert.True(ReadManifest().ContainsKey(managed));
    }

    /// <summary>
    /// A legacy flat-layout item (a file directly under the storage root, no per-item GUID directory) is
    /// not something <see cref="PanelItemStorageService.CopyManaged"/> itself ever produces, so it is left
    /// untouched even if a caller mistakenly hands it to <see cref="PanelItemStorageService.DiscardCopies"/>.
    /// </summary>
    [Fact]
    public void DiscardCopies_LegacyFlatLayoutItem_NeverTouched()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, $"Legacy_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(legacy, "legacy-content");
        var manifest = new Dictionary<string, string> { [legacy] = Path.Combine(_sourceDir, "Original.lnk") };
        File.WriteAllText(Path.Combine(_root, PanelItemStorageService.ManifestFileName), JsonSerializer.Serialize(manifest));

        _sut.DiscardCopies([legacy]);

        Assert.True(File.Exists(legacy));
        Assert.True(ReadManifest().ContainsKey(legacy));
    }

    /// <summary>
    /// M-3 fix (code review, panel clone to page): only ever deletes a path THIS instance actually produced
    /// via <see cref="PanelItemStorageService.CopyManaged"/> — a per-item managed item created through
    /// <see cref="PanelItemStorageService.MoveIn(string)"/> (a real, already-adopted panel item, never
    /// itself a clone copy) must never be deletable just because it happens to be a syntactically valid
    /// per-item managed path that a caller mistakenly hands to <see cref="PanelItemStorageService.DiscardCopies"/>.
    /// </summary>
    [Fact]
    public void DiscardCopies_PathNeverProducedByCopyManaged_NeverTouched()
    {
        var source = NewSourceFile("Real.txt");
        var managed = _sut.MoveIn(source)!;

        _sut.DiscardCopies([managed]);

        Assert.True(File.Exists(managed), "An item never produced by CopyManaged in this session must survive DiscardCopies.");
        Assert.True(ReadManifest().ContainsKey(managed));
    }

    /// <summary>An empty list is a pure no-op — never touches the manifest file at all.</summary>
    [Fact]
    public void DiscardCopies_EmptyList_NoOp()
    {
        var exception = Record.Exception(() => _sut.DiscardCopies([]));

        Assert.Null(exception);
    }

    /// <summary>
    /// A copy this method fails to delete (locked file) is left exactly as it is, manifest entry included,
    /// so the orphaned copy stays findable rather than becoming bookkeeping-less.
    /// </summary>
    [Fact]
    public void DiscardCopies_DeleteFails_LeavesManifestEntryIntact()
    {
        var source = NewSourceFile("Locked.txt");
        var managed = _sut.MoveIn(source)!;
        var copy = _sut.CopyManaged(managed).CopyPath!;

        using (var lockHandle = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _sut.DiscardCopies([copy]);
            lockHandle.Close();
        }

        Assert.True(File.Exists(copy), "A copy DiscardCopies failed to delete must be left in place.");
        Assert.True(ReadManifest().ContainsKey(copy), "Its manifest entry must be left in place too, so the orphan stays findable.");
    }

    /// <summary>
    /// H-2 fix (code review, panel clone to page): <see cref="PanelItemStorageService.CopyManaged"/> carries
    /// the source's ReadOnly attribute over onto the copy (<see cref="File.Copy(string, string)"/> preserves
    /// attributes) — <see cref="PanelItemStorageService.DiscardCopies"/> must still be able to delete such a
    /// copy (clearing the attribute first) instead of leaving it behind as an orphan.
    /// </summary>
    [Fact]
    public void DiscardCopies_ReadOnlyFileCopy_DeletesAndRemovesManifestEntry()
    {
        var source = NewSourceFile("ReadOnlyDoc.txt", "read-only-bytes");
        var managed = _sut.MoveIn(source)!;
        var copy = _sut.CopyManaged(managed).CopyPath!;
        File.SetAttributes(copy, File.GetAttributes(copy) | FileAttributes.ReadOnly);

        _sut.DiscardCopies([copy]);

        Assert.False(File.Exists(copy), "A read-only copy must still be deleted.");
        Assert.False(ReadManifest().ContainsKey(copy));
        Assert.False(Directory.Exists(Path.GetDirectoryName(copy)), "The discarded copy's now-empty GUID directory must be cleaned up.");
    }

    /// <summary>
    /// H-2 fix (code review, panel clone to page): same as
    /// <see cref="DiscardCopies_ReadOnlyFileCopy_DeletesAndRemovesManifestEntry"/>, but for a DIRECTORY copy
    /// containing a read-only file nested inside it — <c>Directory.Delete(recursive: true)</c> fails on a
    /// read-only child exactly the way <c>File.Delete</c> fails on a read-only file itself.
    /// </summary>
    [Fact]
    public void DiscardCopies_DirectoryCopyWithReadOnlyFileInside_DeletesAndRemovesManifestEntry()
    {
        var sourceFolder = Path.Combine(_sourceDir, "ReadOnlyPhotos");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "locked.jpg"), "locked-bytes");
        var managed = _sut.MoveInDirectory(sourceFolder)!;
        var copy = _sut.CopyManaged(managed).CopyPath!;
        var copiedFile = Path.Combine(copy, "locked.jpg");
        File.SetAttributes(copiedFile, File.GetAttributes(copiedFile) | FileAttributes.ReadOnly);

        _sut.DiscardCopies([copy]);

        Assert.False(Directory.Exists(copy), "A directory copy containing a read-only file must still be deleted.");
        Assert.False(ReadManifest().ContainsKey(copy));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
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

    private string NewSourceFile(string name, string content = "x")
    {
        var path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Dictionary<string, string> ReadManifest()
    {
        var manifestPath = Path.Combine(_root, PanelItemStorageService.ManifestFileName);
        if (!File.Exists(manifestPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? [];
        return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private void SetupRecycle(bool deletes)
        => _nativeMock
            .Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Callback<string[], IntPtr>((paths, _) =>
            {
                if (!deletes) return;

                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
            })
            .Returns(deletes);
}
