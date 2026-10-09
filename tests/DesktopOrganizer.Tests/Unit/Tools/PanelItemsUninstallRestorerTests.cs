using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="PanelItemsUninstallRestorer"/> — the <c>--restore-panel-items</c>
/// uninstall-time recovery path. Every scenario is exercised against a real disposable temp directory
/// standing in for both the <c>PanelItems</c> storage root and the user's desktop, same convention as
/// <see cref="DesktopOrganizer.Tests.Unit.Tools.PanelItemStorageServiceTests"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class PanelItemsUninstallRestorerTests
{
    // ── Empty storage ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a missing <c>PanelItems</c> storage folder is a no-op success — nothing to
    /// restore, nothing touched.
    /// </summary>
    [Fact]
    public void RestoreAll_StorageFolderMissing_IsNoOpSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgUninstall_{Guid.NewGuid():N}");
        var desktop = Path.Combine(Path.GetTempPath(), $"DOrgDesktop_{Guid.NewGuid():N}");
        var restorer = new PanelItemsUninstallRestorer(root, desktop);

        var result = restorer.RestoreAll();

        Assert.True(result.Success);
        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(Directory.Exists(root));
    }

    /// <summary>
    /// Verifies that a storage folder that exists but holds nothing (a stale, already-empty
    /// <c>PanelItems</c> directory) is also a no-op success, and the folder itself is removed.
    /// </summary>
    [Fact]
    public void RestoreAll_StorageFolderEmpty_IsNoOpSuccess_AndRemovesFolder()
    {
        var (restorer, root, _) = BuildRestorer();
        Directory.CreateDirectory(root);

        try
        {
            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(0, result.RestoredCount);
            Assert.Equal(0, result.FailedCount);
            Assert.False(Directory.Exists(root), "An empty storage folder must be removed on a fully successful run.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Original location exists ────────────────────────────────────────────

    /// <summary>
    /// Verifies that a managed item whose recorded original directory still exists, is on a fixed
    /// local drive, and has no collision is restored to that exact original location, and the storage
    /// folder is removed once everything succeeds.
    /// </summary>
    [Fact]
    public void RestoreAll_OriginalDirectoryExists_RestoresInPlace()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "restore-me";
        File.WriteAllText(source, content);

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);

            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(1, result.RestoredCount);
            Assert.True(File.Exists(source), "Item must be back at its original location.");
            Assert.Equal(content, File.ReadAllText(source));
            Assert.False(File.Exists(managed!), "Managed copy must no longer exist.");
            Assert.False(Directory.Exists(root), "Storage folder must be removed after a fully successful run.");
            Assert.Empty(Directory.Exists(desktop) ? Directory.GetFileSystemEntries(desktop) : []);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    /// <summary>
    /// Verifies that a whole managed directory (imported via <c>MoveInDirectory</c>) is restored in
    /// place, preserving its internal structure.
    /// </summary>
    [Fact]
    public void RestoreAll_OriginalDirectoryExists_RestoresManagedFolderInPlace()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var parentDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigParent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(parentDir);
        var source = Path.Combine(parentDir, "MyFolder");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "inner.txt"), "inner-content");

        try
        {
            var managed = storage.MoveInDirectory(source);
            Assert.NotNull(managed);

            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(1, result.RestoredCount);
            Assert.True(Directory.Exists(source));
            Assert.Equal("inner-content", File.ReadAllText(Path.Combine(source, "inner.txt")));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            SafeDeleteDirectory(source);
            SafeDeleteDirectory(parentDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Original location missing ───────────────────────────────────────────

    /// <summary>
    /// Verifies that a managed item whose recorded original directory no longer exists is moved to the
    /// desktop instead, under its original file name.
    /// </summary>
    [Fact]
    public void RestoreAll_OriginalDirectoryMissing_MovesToDesktop()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "content";
        File.WriteAllText(source, content);

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);

            Directory.Delete(sourceDir, recursive: true);

            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(1, result.RestoredCount);
            var desktopPath = Path.Combine(desktop, "MyApp.lnk");
            Assert.True(File.Exists(desktopPath), "Item must land on the desktop when its original directory is gone.");
            Assert.Equal(content, File.ReadAllText(desktopPath));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Collision at original location ──────────────────────────────────────

    /// <summary>
    /// Verifies that a managed item whose original directory exists but whose exact original path is
    /// now occupied by something else is redirected to the desktop rather than landing at a
    /// collision-suffixed name back in the original directory — deliberately different from
    /// <see cref="PanelItemStorageService.Restore(string, bool)"/>'s own in-place collision handling,
    /// per the uninstall-time restorer's contract.
    /// </summary>
    [Fact]
    public void RestoreAll_CollisionAtOriginalLocation_MovesToDesktopInstead()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        File.WriteAllText(source, "original-content");

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);

            // Someone/something re-created a file at the exact original path in the meantime.
            File.WriteAllText(source, "someone-elses-file");

            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(1, result.RestoredCount);
            Assert.Equal("someone-elses-file", File.ReadAllText(source));
            var desktopPath = Path.Combine(desktop, "MyApp.lnk");
            Assert.True(File.Exists(desktopPath), "Colliding item must be redirected to the desktop.");
            Assert.Equal("original-content", File.ReadAllText(desktopPath));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Collision on the desktop itself ─────────────────────────────────────

    /// <summary>
    /// Verifies that two managed items that both fall back to the desktop under the same file name
    /// (e.g. both had their original directories removed) land under distinct, collision-free names.
    /// </summary>
    [Fact]
    public void RestoreAll_CollisionOnDesktop_UsesUniqueName()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var sourceDirA = Path.Combine(Path.GetTempPath(), $"DOrgOrigDirA_{Guid.NewGuid():N}");
        var sourceDirB = Path.Combine(Path.GetTempPath(), $"DOrgOrigDirB_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDirA);
        Directory.CreateDirectory(sourceDirB);
        var sourceA = Path.Combine(sourceDirA, "Same.lnk");
        var sourceB = Path.Combine(sourceDirB, "Same.lnk");
        File.WriteAllText(sourceA, "content-a");
        File.WriteAllText(sourceB, "content-b");

        try
        {
            var managedA = storage.MoveIn(sourceA);
            var managedB = storage.MoveIn(sourceB);
            Assert.NotNull(managedA);
            Assert.NotNull(managedB);

            // Both original directories vanish — both items fall back to the desktop under the same name.
            Directory.Delete(sourceDirA, recursive: true);
            Directory.Delete(sourceDirB, recursive: true);

            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(2, result.RestoredCount);

            var first = Path.Combine(desktop, "Same.lnk");
            var second = Path.Combine(desktop, "Same (2).lnk");
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));

            var contents = new[] { File.ReadAllText(first), File.ReadAllText(second) };
            Assert.Contains("content-a", contents);
            Assert.Contains("content-b", contents);
        }
        finally
        {
            SafeDeleteDirectory(sourceDirA);
            SafeDeleteDirectory(sourceDirB);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Orphans (present on disk, absent from the manifest) ─────────────────

    /// <summary>
    /// Verifies that an item physically present under a per-item storage directory but missing from
    /// the manifest (e.g. a manifest write that never completed) is still picked up and moved to the
    /// desktop, since there is no recorded original location to try.
    /// </summary>
    [Fact]
    public void RestoreAll_OrphanItem_MovesToDesktop()
    {
        var (restorer, root, desktop) = BuildRestorer();
        var itemDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(itemDir);
        var orphanFile = Path.Combine(itemDir, "orphan.txt");
        File.WriteAllText(orphanFile, "orphan-content");

        try
        {
            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(1, result.RestoredCount);
            var desktopPath = Path.Combine(desktop, "orphan.txt");
            Assert.True(File.Exists(desktopPath), "An orphaned managed item must still be moved to the desktop.");
            Assert.Equal("orphan-content", File.ReadAllText(desktopPath));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Per-item failure does not abort the run ─────────────────────────────

    /// <summary>
    /// Verifies that a per-item failure (a locked file that cannot be moved) is logged and skipped
    /// rather than aborting the whole run: the other, unlocked item is still restored, the failure is
    /// reported in <see cref="PanelItemsRestoreResult.FailedCount"/>, and — since the run was not fully
    /// successful — the storage folder is left in place (holding only the failed item).
    /// </summary>
    [Fact]
    public void RestoreAll_OneItemLocked_RestoresTheOtherAndReportsFailure()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var okDir = Path.Combine(Path.GetTempPath(), $"DOrgOkDir_{Guid.NewGuid():N}");
        var lockedDir = Path.Combine(Path.GetTempPath(), $"DOrgLockedDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(okDir);
        Directory.CreateDirectory(lockedDir);
        var okSource = Path.Combine(okDir, "Ok.lnk");
        var lockedSource = Path.Combine(lockedDir, "Locked.lnk");
        File.WriteAllText(okSource, "ok-content");
        File.WriteAllText(lockedSource, "locked-content");

        try
        {
            var okManaged = storage.MoveIn(okSource);
            var lockedManaged = storage.MoveIn(lockedSource);
            Assert.NotNull(okManaged);
            Assert.NotNull(lockedManaged);

            using (new FileStream(lockedManaged!, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = restorer.RestoreAll();

                Assert.False(result.Success);
                Assert.Equal(1, result.RestoredCount);
                Assert.Equal(1, result.FailedCount);
            }

            Assert.True(File.Exists(okSource), "The unlocked item must still have been restored.");
            Assert.True(File.Exists(lockedManaged!), "The locked item must remain in managed storage, untouched.");
            Assert.True(Directory.Exists(root), "Storage folder must be left in place when a failure occurred.");
        }
        finally
        {
            SafeDeleteFile(okSource);
            SafeDeleteFile(lockedSource);
            SafeDeleteDirectory(okDir);
            SafeDeleteDirectory(lockedDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Enumeration failure must never lead to a root hard-delete (ADR-041 §RestoreAll p.4) ──

    /// <summary>
    /// Verifies that when the per-item GUID directory cannot be enumerated (so its content was never
    /// read), the run counts that as a failure, and the storage root — still holding the unread user
    /// file — is NOT deleted.
    /// </summary>
    [Fact]
    public void RestoreAll_ItemDirectoryEnumerationFails_ReportsFailureAndKeepsRoot()
    {
        var (storage, root) = BuildStorageService();
        var desktop = Path.Combine(Path.GetTempPath(), $"DOrgDesktop_{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgEnumDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Unreadable.txt");
        File.WriteAllText(source, "precious");

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);
            var guidDir = Path.GetDirectoryName(managed!)!;

            var restorer = new PanelItemsUninstallRestorer(
                root,
                desktop,
                appLog: null,
                enumerateEntries: path =>
                    string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), guidDir, StringComparison.OrdinalIgnoreCase)
                        ? throw new UnauthorizedAccessException("simulated access denied")
                        : Directory.GetFileSystemEntries(path));

            var result = restorer.RestoreAll();

            Assert.False(result.Success);
            Assert.True(result.FailedCount >= 1);
            Assert.Contains("item(s) or location(s) could not be restored", result.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(root), "The storage root must survive an enumeration failure.");
            Assert.True(File.Exists(managed!), "The unread user file must not be deleted.");
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    /// <summary>
    /// Verifies that when the storage root itself cannot be enumerated, the run reports a failure and
    /// keeps the root rather than treating "nothing enumerated" as "nothing to restore".
    /// </summary>
    [Fact]
    public void RestoreAll_StorageRootEnumerationFails_ReportsFailureAndKeepsRoot()
    {
        var (storage, root) = BuildStorageService();
        var desktop = Path.Combine(Path.GetTempPath(), $"DOrgDesktop_{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgEnumRootDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Unreadable.txt");
        File.WriteAllText(source, "precious");

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);

            var restorer = new PanelItemsUninstallRestorer(
                root,
                desktop,
                appLog: null,
                enumerateEntries: _ => throw new IOException("simulated root enumeration failure"));

            var result = restorer.RestoreAll();

            Assert.False(result.Success);
            Assert.Equal(0, result.RestoredCount);
            Assert.True(result.FailedCount >= 1);
            Assert.True(Directory.Exists(root), "The storage root must survive an enumeration failure.");
            Assert.True(File.Exists(managed!), "The unread user file must not be deleted.");
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Manifest temp leftovers (MINOR 1) ───────────────────────────────────

    /// <summary>
    /// Verifies that a crashed atomic manifest write's temp leftover
    /// (<c>.manifest.json.tmp&lt;guid&gt;</c>, directly under the storage root) is deleted rather than
    /// treated as a legacy flat-layout item and moved to the desktop.
    /// </summary>
    [Fact]
    public void RestoreAll_ManifestTempLeftoverAtRoot_IsDeletedNotMovedToDesktop()
    {
        var (restorer, root, desktop) = BuildRestorer();
        Directory.CreateDirectory(root);
        var tempLeftover = Path.Combine(root, $"{PanelItemStorageService.ManifestFileName}.tmp{Guid.NewGuid():N}");
        File.WriteAllText(tempLeftover, "{}");

        try
        {
            var result = restorer.RestoreAll();

            Assert.True(result.Success);
            Assert.Equal(0, result.RestoredCount);
            Assert.Equal(0, result.FailedCount);
            Assert.False(File.Exists(tempLeftover), "The manifest temp leftover must be deleted, not left behind.");
            Assert.False(
                Directory.Exists(desktop) && Directory.GetFileSystemEntries(desktop).Length > 0,
                "The manifest temp leftover must never be moved to the desktop.");
            Assert.False(Directory.Exists(root), "Storage folder must be removed once the leftover is cleaned up and nothing else remains.");
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── Verified cross-volume delete of a read-only/locked source (MINOR 2) ────

    /// <summary>
    /// Verifies that a read-only managed source file no longer causes a forced cross-volume restore to
    /// be counted as failed: the read-only attribute is cleared before the post-copy delete, the delete
    /// succeeds, and the item is restored (not left duplicated at both the managed source and the
    /// target).
    /// </summary>
    [Fact]
    public void RestoreAll_ForcedCrossVolume_ReadOnlySource_ClearsAttributeAndRestoresCleanly()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "read-only-content";
        File.WriteAllText(source, content);

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);
            Directory.Delete(sourceDir, recursive: true); // force the desktop fallback target

            File.SetAttributes(managed!, File.GetAttributes(managed!) | FileAttributes.ReadOnly);

            var result = restorer.RestoreAll(forceCrossVolume: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.RestoredCount);
            Assert.Equal(0, result.FailedCount);
            var desktopPath = Path.Combine(desktop, "MyApp.lnk");
            Assert.True(File.Exists(desktopPath));
            Assert.Equal(content, File.ReadAllText(desktopPath));
            Assert.False(File.Exists(managed!), "The read-only managed source must have been deleted after the verified copy, not left as a duplicate.");
            Assert.False(Directory.Exists(root), "Storage folder must be removed after a fully successful run.");
        }
        finally
        {
            // File.Copy preserves the source's attributes, so the desktop copy inherited ReadOnly too —
            // clear it here purely so cleanup itself can delete the file, independent of the fix above.
            ClearReadOnlyIfPresent(Path.Combine(desktop, "MyApp.lnk"));
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    /// <summary>
    /// Directory counterpart of <see cref="RestoreAll_ForcedCrossVolume_ReadOnlySource_ClearsAttributeAndRestoresCleanly"/>:
    /// the read-only attribute must be cleared recursively for every file nested under a managed
    /// directory before the post-copy delete is attempted.
    /// </summary>
    [Fact]
    public void RestoreAll_ForcedCrossVolume_ReadOnlyDirectorySource_ClearsAttributesRecursivelyAndRestoresCleanly()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var parentDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigParent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(parentDir);
        var source = Path.Combine(parentDir, "MyFolder");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "inner.txt"), "inner-content");

        try
        {
            var managed = storage.MoveInDirectory(source);
            Assert.NotNull(managed);
            Directory.Delete(parentDir, recursive: true); // force the desktop fallback target

            foreach (var file in Directory.GetFiles(managed!, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }

            var result = restorer.RestoreAll(forceCrossVolume: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.RestoredCount);
            Assert.Equal(0, result.FailedCount);
            var desktopPath = Path.Combine(desktop, "MyFolder");
            Assert.True(Directory.Exists(desktopPath));
            Assert.Equal("inner-content", File.ReadAllText(Path.Combine(desktopPath, "inner.txt")));
            Assert.False(Directory.Exists(managed!), "The read-only managed source directory must have been deleted after the verified copy.");
        }
        finally
        {
            // Same reason as the file test above: the desktop copy inherited ReadOnly from the source
            // via File.Copy, purely a test-cleanup concern.
            var desktopFolder = Path.Combine(desktop, "MyFolder");
            if (Directory.Exists(desktopFolder))
            {
                foreach (var file in Directory.GetFiles(desktopFolder, "*", SearchOption.AllDirectories))
                {
                    ClearReadOnlyIfPresent(file);
                }
            }

            SafeDeleteDirectory(parentDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    /// <summary>
    /// When the post-copy source delete fails even after clearing the read-only attribute (e.g. the
    /// managed source is locked open by something else), the item must still be counted as restored —
    /// the verified copy is complete and correct — rather than failed, which would otherwise leave it
    /// duplicated at both the managed source and the target and make a re-run rename the target "(2)".
    /// </summary>
    [Fact]
    public void RestoreAll_ForcedCrossVolume_SourceDeleteFailsAfterVerifiedCopy_CountsAsRestoredNotFailed()
    {
        var (storage, root) = BuildStorageService();
        var (restorer, _, desktop) = BuildRestorer(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Locked.lnk");
        const string content = "locked-content";
        File.WriteAllText(source, content);

        try
        {
            var managed = storage.MoveIn(source);
            Assert.NotNull(managed);
            Directory.Delete(sourceDir, recursive: true); // force the desktop fallback target

            using (new FileStream(managed!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = restorer.RestoreAll(forceCrossVolume: true);

                Assert.True(result.Success, result.Message);
                Assert.Equal(1, result.RestoredCount);
                Assert.Equal(0, result.FailedCount);
            }

            var desktopPath = Path.Combine(desktop, "Locked.lnk");
            Assert.True(File.Exists(desktopPath), "The verified copy must still have landed on the desktop.");
            Assert.Equal(content, File.ReadAllText(desktopPath));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(desktop);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static (PanelItemsUninstallRestorer Restorer, string Root, string Desktop) BuildRestorer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgUninstall_{Guid.NewGuid():N}");
        var desktop = Path.Combine(Path.GetTempPath(), $"DOrgDesktop_{Guid.NewGuid():N}");
        return (new PanelItemsUninstallRestorer(root, desktop), root, desktop);
    }

    private static (PanelItemsUninstallRestorer Restorer, string Root, string Desktop) BuildRestorer(string root)
    {
        var desktop = Path.Combine(Path.GetTempPath(), $"DOrgDesktop_{Guid.NewGuid():N}");
        return (new PanelItemsUninstallRestorer(root, desktop), root, desktop);
    }

    private static (PanelItemStorageService Service, string Root) BuildStorageService()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgUninstall_{Guid.NewGuid():N}");
        var nativeApi = new Mock<INativeApi>();
        return (new PanelItemStorageService(root, nativeApi.Object), root);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle from a test failure shouldn't mask the real
            // assertion failure.
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Test-cleanup helper: clears a lingering read-only attribute so a later delete can succeed.</summary>
    private static void ClearReadOnlyIfPresent(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (IOException)
        {
        }
    }
}
