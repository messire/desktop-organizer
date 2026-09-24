using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="PanelItemStorageService"/>.
/// </summary>
/// <remarks>
/// Covers the Move semantics that replaced the earlier Copy-based Import/ImportDirectory: dropping a
/// file/folder into a panel now moves the original into app-managed storage (declutters the source
/// immediately) instead of leaving a copy behind, and "Remove from panel" moves a managed item back to
/// its original location via <see cref="PanelItemStorageService.Restore"/>. The regression this guards
/// against — a panel item breaking because it referenced the original path instead of an independent
/// copy — is why <see cref="PanelItemStorageService.MoveIn"/>/<see cref="PanelItemStorageService.MoveInDirectory"/>
/// still produce a storage-owned copy of the item, same as the old Import did; the difference is the
/// source is now moved (removed), not just copied.
///
/// Cross-volume behavior (D3-D5, D14 partially) can't be reproduced deterministically in CI, which
/// typically has a single volume. These tests use the internal <c>forceCrossVolume</c> test hook on
/// <see cref="PanelItemStorageService.MoveIn(string, bool)"/>/<see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/>
/// to exercise the copy-then-delete-after-verified-success branch regardless of the actual volume
/// layout of the test machine. Real multi-volume behavior is manual verify (Win10/Win11) — see the
/// plan's Definition of Done.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class PanelItemStorageServiceTests
{
    private static (PanelItemStorageService Service, string Root) BuildService()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        return (new PanelItemStorageService(root), root);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle from a test failure shouldn't mask the
            // real assertion failure.
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

    // ── D1: MoveIn same-volume ──────────────────────────────────────────────

    [Fact]
    public void MoveIn_SameVolume_RemovesSourceAndCreatesManaged()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        const string content = "shortcut-content";
        File.WriteAllText(source, content);

        try
        {
            var result = service.MoveIn(source);

            Assert.NotNull(result);
            Assert.StartsWith(root, result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result), "Managed copy must exist after MoveIn.");
            Assert.False(File.Exists(source), "Source must be removed by a same-volume move.");
            Assert.Equal(content, File.ReadAllText(result!));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    // ── D2: MoveInDirectory same-volume, cap ignored ────────────────────────

    [Fact]
    public void MoveInDirectory_SameVolume_MovesAndIgnoresCap()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgFolder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);

        // More files than MaxDirectoryImportFileCount would allow on the cross-volume path — proves
        // the same-volume Directory.Move branch doesn't even look at the safety cap.
        const int fileCount = 25;
        for (var i = 0; i < fileCount; i++)
        {
            File.WriteAllText(Path.Combine(source, $"file{i}.txt"), $"content-{i}");
        }

        try
        {
            var result = service.MoveInDirectory(source);

            Assert.NotNull(result);
            Assert.StartsWith(root, result, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(source), "Source folder must be removed by a same-volume move.");
            Assert.Equal(fileCount, Directory.GetFiles(result!).Length);
            Assert.Equal("content-0", File.ReadAllText(Path.Combine(result!, "file0.txt")));
        }
        finally
        {
            SafeDeleteDirectory(source);
            SafeDeleteDirectory(root);
        }
    }

    // ── D3: Cross-volume copy-then-delete-after-verified-success ───────────

    [Fact]
    public void MoveIn_CrossVolume_CopiesThenDeletesOriginalAfterSuccess()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        const string content = "cross-volume-content";
        File.WriteAllText(source, content);

        try
        {
            var result = service.MoveIn(source, forceCrossVolume: true);

            Assert.NotNull(result);
            Assert.True(File.Exists(result), "Managed copy must exist after a successful cross-volume move.");
            Assert.Equal(content, File.ReadAllText(result!));
            Assert.False(File.Exists(source), "Source must only be deleted after the copy is verified complete.");
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    // ── D4: Cross-volume copy failure (file) — original survives ───────────

    /// <summary>
    /// Simulates a copy failure by holding an exclusive lock on the source file, so
    /// <see cref="File.Copy(string, string)"/> throws a sharing-violation <see cref="IOException"/>
    /// mid-operation — deterministic without relying on a second real volume. The critical assertion
    /// is that the source is untouched: this is the "never delete unless the copy is verified"
    /// invariant that prevents real user data loss.
    /// </summary>
    [Fact]
    public void MoveIn_CrossVolumeCopyFails_OriginalSurvivesAndReturnsNull()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        const string content = "must-survive";
        File.WriteAllText(source, content);

        try
        {
            using (var lockHandle = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = service.MoveIn(source, forceCrossVolume: true);

                Assert.Null(result);
                lockHandle.Close();
            }

            Assert.True(File.Exists(source), "Original must survive a failed cross-volume copy.");
            Assert.Equal(content, File.ReadAllText(source));

            // No partial copy should be left behind in storage.
            if (Directory.Exists(root))
            {
                var leftoverFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.True(leftoverFiles.Length == 0, $"Expected no partial copy, found: {string.Join(", ", leftoverFiles)}");
            }
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    // ── D5: Cross-volume copy failure (directory) — original survives ──────

    /// <summary>
    /// Locks one file inside the source folder so the recursive copy aborts partway through — proves
    /// the whole original folder (all files, not just the locked one) survives a mid-copy failure,
    /// and the partial managed copy is cleaned up.
    /// </summary>
    [Fact]
    public void MoveInDirectory_CrossVolumeCopyFails_OriginalSurvives()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgFolder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        var fileA = Path.Combine(source, "a.txt");
        var fileB = Path.Combine(source, "b.txt");
        File.WriteAllText(fileA, "content-a");
        File.WriteAllText(fileB, "content-b");

        try
        {
            string? result;
            using (var lockHandle = new FileStream(fileB, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = service.MoveInDirectory(source, forceCrossVolume: true);
                lockHandle.Close();
            }

            Assert.Null(result);
            Assert.True(Directory.Exists(source), "Original folder must survive a failed cross-volume copy.");
            Assert.True(File.Exists(fileA), "Every original file must survive, not just the locked one.");
            Assert.True(File.Exists(fileB));
            Assert.Equal("content-a", File.ReadAllText(fileA));
            Assert.Equal("content-b", File.ReadAllText(fileB));
        }
        finally
        {
            SafeDeleteDirectory(source);
            SafeDeleteDirectory(root);
        }
    }

    // ── D6: Restore to original location ────────────────────────────────────

    [Fact]
    public void Restore_ManagedFile_ReturnsToOriginalLocation()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "restore-me";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var outcome = service.Restore(managed!);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(source), "File must be back at its original location.");
            Assert.Equal(content, File.ReadAllText(source));
            Assert.False(File.Exists(managed!), "Managed copy must no longer exist after Restore.");
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── D7: Restore collision handling ──────────────────────────────────────

    [Fact]
    public void Restore_TargetNameTaken_UsesCollisionFreeName()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        File.WriteAllText(source, "original-content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            // Something else now occupies the original path (e.g. the user recreated a shortcut there).
            File.WriteAllText(source, "someone-elses-file");

            var outcome = service.Restore(managed!);
            var expectedCollisionPath = Path.Combine(sourceDir, "MyApp (2).lnk");

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(source), "Pre-existing file at the original path must not be touched.");
            Assert.Equal("someone-elses-file", File.ReadAllText(source));
            Assert.True(File.Exists(expectedCollisionPath), "Restored file must land at a collision-free name.");
            Assert.Equal("original-content", File.ReadAllText(expectedCollisionPath));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── D8: Restore recreates missing parent directory ──────────────────────

    [Fact]
    public void Restore_MissingParentDir_RecreatesAndRestores()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "content";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            // The original parent folder is gone entirely by the time the user restores.
            Directory.Delete(sourceDir, recursive: true);
            Assert.False(Directory.Exists(sourceDir));

            var outcome = service.Restore(managed!);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(Directory.Exists(sourceDir), "Missing parent directory must be recreated.");
            Assert.True(File.Exists(source));
            Assert.Equal(content, File.ReadAllText(source));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── D9: Restore with no manifest entry (legacy/ADR-B) ───────────────────

    [Fact]
    public void Restore_NoManifestEntry_ReturnsUnknownAndKeepsFile()
    {
        var (service, root) = BuildService();
        Directory.CreateDirectory(root);
        var orphan = Path.Combine(root, "orphan.lnk");
        File.WriteAllText(orphan, "legacy-item");

        try
        {
            // No MoveIn call happened for this path, so no manifest entry exists — simulates a
            // legacy managed item imported before Move semantics existed (ADR-B).
            var outcome = service.Restore(orphan);

            Assert.Equal(RestoreOutcome.OriginalPathUnknown, outcome);
            Assert.True(File.Exists(orphan), "Managed copy must be left untouched when original path is unknown.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── D10: Restore on a non-managed path ──────────────────────────────────

    [Fact]
    public void Restore_PathOutsideStorage_NoOp()
    {
        var (service, _) = BuildService();
        var outside = Path.Combine(Path.GetTempPath(), $"DOrgOutside_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(outside, "not-managed");

        try
        {
            var outcome = service.Restore(outside);

            Assert.Equal(RestoreOutcome.NotManaged, outcome);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            SafeDeleteFile(outside);
        }
    }

    // ── D11: Restore filesystem failure — managed copy kept ─────────────────

    /// <summary>
    /// Locks the managed copy so both the same-volume <see cref="File.Move(string, string)"/> and the
    /// cross-volume copy-then-delete fallback fail — proves the managed copy is never lost when the
    /// filesystem operation fails.
    /// </summary>
    [Fact]
    public void Restore_MoveFails_KeepsManagedCopy()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "must-not-be-lost";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            RestoreOutcome outcome;
            using (var lockHandle = new FileStream(managed!, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                outcome = service.Restore(managed!);
                lockHandle.Close();
            }

            Assert.Equal(RestoreOutcome.Failed, outcome);
            Assert.True(File.Exists(managed!), "Managed copy must survive a failed restore.");
            Assert.Equal(content, File.ReadAllText(managed!));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── M2: cross-volume Restore fallback — now covered via the internal forceCrossVolume hook ───
    //
    // PanelItemStorageService.Restore(string, bool) added an internal `forceCrossVolume` escape hatch
    // (mirroring MoveIn(string, bool)/MoveInDirectory(string, bool)) that skips the same-volume
    // File.Move/Directory.Move fast path entirely and runs the copy-then-delete-after-verified-success
    // branch directly — deterministic in a single-volume CI environment. Real multi-volume behavior
    // remains manual verify (Win10/Win11) — see the plan's section 7.

    [Fact]
    public void Restore_CrossVolumeFallback_File_CopiesThenDeletesAndReturnsRestored()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "cross-volume-restore";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var outcome = service.Restore(managed!, forceCrossVolume: true);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(source), "File must be back at its original location via copy-then-delete.");
            Assert.Equal(content, File.ReadAllText(source));
            Assert.False(File.Exists(managed!), "Managed copy must be deleted after a verified copy-then-delete restore.");

            // Manifest entry removed: a second Restore attempt against the same (now-deleted) managed
            // path can no longer find an original path, proving the manifest entry was cleaned up.
            var secondOutcome = service.Restore(managed!, forceCrossVolume: true);
            Assert.Equal(RestoreOutcome.OriginalPathUnknown, secondOutcome);
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Locks the managed file itself so <c>forceCrossVolume: true</c> — which skips <see cref="File.Move"/>
    /// entirely and goes straight to the copy-then-delete branch — fails at <c>File.Copy</c> (needs read
    /// access to the source). Proves the managed copy is left intact and its manifest entry survives.
    /// </summary>
    [Fact]
    public void Restore_CrossVolumeFallback_File_CopyFails_LeavesManagedCopyIntact()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "must-survive-cross-volume-fail";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            RestoreOutcome outcome;
            using (var lockHandle = new FileStream(managed!, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                outcome = service.Restore(managed!, forceCrossVolume: true);
                lockHandle.Close();
            }

            Assert.Equal(RestoreOutcome.Failed, outcome);
            Assert.True(File.Exists(managed!), "Managed copy must survive a failed cross-volume copy.");
            Assert.Equal(content, File.ReadAllText(managed!));
            Assert.False(File.Exists(source), "Original must not reappear when the cross-volume copy failed.");

            // Manifest entry must still be present — a subsequent unlocked Restore finds it and succeeds.
            var retryOutcome = service.Restore(managed!, forceCrossVolume: true);
            Assert.Equal(RestoreOutcome.Restored, retryOutcome);
            Assert.True(File.Exists(source));
            Assert.Equal(content, File.ReadAllText(source));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_CrossVolumeFallback_Directory_CopiesThenDeletesAndReturnsRestored()
    {
        var (service, root) = BuildService();
        var sourceParent = Path.Combine(Path.GetTempPath(), $"DOrgOrigParent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceParent);
        var source = Path.Combine(sourceParent, "MyFolder");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "f.txt"), "dir-content");

        try
        {
            var managed = service.MoveInDirectory(source);
            Assert.NotNull(managed);

            var outcome = service.Restore(managed!, forceCrossVolume: true);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(Directory.Exists(source));
            Assert.Equal("dir-content", File.ReadAllText(Path.Combine(source, "f.txt")));
            Assert.False(Directory.Exists(managed!), "Managed copy must be deleted after a verified copy-then-delete restore.");
        }
        finally
        {
            SafeDeleteDirectory(sourceParent);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Directory counterpart of <see cref="Restore_CrossVolumeFallback_File_CopyFails_LeavesManagedCopyIntact"/>:
    /// locks a file inside the managed directory so the recursive verified copy aborts partway through.
    /// </summary>
    [Fact]
    public void Restore_CrossVolumeFallback_Directory_CopyFails_LeavesManagedCopyIntact()
    {
        var (service, root) = BuildService();
        var sourceParent = Path.Combine(Path.GetTempPath(), $"DOrgOrigParent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceParent);
        var source = Path.Combine(sourceParent, "MyFolder");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "f.txt"), "dir-content");

        try
        {
            var managed = service.MoveInDirectory(source);
            Assert.NotNull(managed);
            var managedInnerFile = Directory.GetFiles(managed!, "*", SearchOption.AllDirectories).Single();

            RestoreOutcome outcome;
            using (var lockHandle = new FileStream(managedInnerFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                outcome = service.Restore(managed!, forceCrossVolume: true);
                lockHandle.Close();
            }

            Assert.Equal(RestoreOutcome.Failed, outcome);
            Assert.True(Directory.Exists(managed!), "Managed copy must survive a failed cross-volume directory copy.");
            Assert.False(Directory.Exists(source), "Original directory must not reappear when the copy failed.");

            // Manifest entry must still be present — a subsequent unlocked Restore finds it and succeeds.
            var retryOutcome = service.Restore(managed!, forceCrossVolume: true);
            Assert.Equal(RestoreOutcome.Restored, retryOutcome);
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            SafeDeleteDirectory(sourceParent);
            SafeDeleteDirectory(root);
        }
    }

    // ── D14: Manifest survives across service instances ─────────────────────

    [Fact]
    public void Manifest_PersistsAcrossServiceInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        var firstInstance = new PanelItemStorageService(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        const string content = "persisted";
        File.WriteAllText(source, content);

        try
        {
            var managed = firstInstance.MoveIn(source);
            Assert.NotNull(managed);

            // Simulate an app restart: a brand-new service instance over the same storage root,
            // sharing no in-memory state with the first.
            var secondInstance = new PanelItemStorageService(root);
            var outcome = secondInstance.Restore(managed!);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(source));
            Assert.Equal(content, File.ReadAllText(source));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── IsManaged ────────────────────────────────────────────────────────────

    [Fact]
    public void IsManaged_PathInsideStorageRoot_ReturnsTrue()
    {
        var (service, root) = BuildService();
        var path = Path.Combine(root, "whatever.lnk");
        Assert.True(service.IsManaged(path));
    }

    [Fact]
    public void IsManaged_PathOutsideStorageRoot_ReturnsFalse()
    {
        var (service, _) = BuildService();
        Assert.False(service.IsManaged(@"C:\Users\someone\Desktop\MyApp.lnk"));
    }

    [Fact]
    public void IsManaged_EmptyPath_ReturnsFalse()
    {
        var (service, _) = BuildService();
        Assert.False(service.IsManaged(string.Empty));
    }

    /// <summary>
    /// Regression for C1 (CRITICAL, found by code-quality-reviewer): <c>IsManaged</c> used to compare
    /// paths with a raw <c>string.StartsWith(root)</c>, so a sibling directory that merely shares the
    /// storage root as a string prefix — e.g. <c>...\PanelItems2\foo.txt</c> or
    /// <c>...\PanelItems_backup\bar.txt</c> when the root is <c>...\PanelItems</c> — was incorrectly
    /// treated as managed. That's dangerous because <see cref="PanelItemStorageService.Release"/> and
    /// <see cref="PanelItemStorageService.Restore"/> both gate destructive/relocating filesystem
    /// operations on <c>IsManaged</c>: a false positive here could delete or move a directory the app
    /// doesn't own. The fix compares on a path-segment boundary (exact root match, or root + separator
    /// prefix) instead of a bare string prefix.
    /// </summary>
    [Fact]
    public void IsManaged_SiblingPathWithSharedPrefix_ReturnsFalse()
    {
        var (service, root) = BuildService();
        var siblingWithSuffix = root + "2";
        var siblingWithBackupSuffix = root + "_backup";

        try
        {
            Assert.False(
                service.IsManaged(Path.Combine(siblingWithSuffix, "foo.txt")),
                "A sibling folder that only shares a string prefix with the storage root must not be managed.");
            Assert.False(
                service.IsManaged(Path.Combine(siblingWithBackupSuffix, "bar.txt")),
                "A sibling folder with a shared string prefix (e.g. a '_backup' suffix) must not be managed.");
            Assert.True(
                service.IsManaged(Path.Combine(root, "something")),
                "A path genuinely nested inside the storage root must still be managed.");
            Assert.True(
                service.IsManaged(root),
                "The storage root itself must be considered managed (exact-match boundary case).");
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(siblingWithSuffix);
            SafeDeleteDirectory(siblingWithBackupSuffix);
        }
    }

    // ── MoveIn / MoveInDirectory — validation ───────────────────────────────

    [Fact]
    public void MoveIn_MissingSource_ReturnsNull()
    {
        var (service, _) = BuildService();
        var result = service.MoveIn(@"C:\Definitely\Does\Not\Exist\ghost.lnk");
        Assert.Null(result);
    }

    [Fact]
    public void MoveIn_Directory_ReturnsNull()
    {
        var (service, root) = BuildService();
        var dir = Path.Combine(Path.GetTempPath(), $"DOrgDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = service.MoveIn(dir);
            Assert.Null(result);
            Assert.True(Directory.Exists(dir), "A directory passed to MoveIn must be left untouched.");
        }
        finally
        {
            SafeDeleteDirectory(dir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveIn_EmptyPath_ReturnsNull()
    {
        var (service, _) = BuildService();
        Assert.Null(service.MoveIn(string.Empty));
    }

    [Fact]
    public void MoveInDirectory_MissingSource_ReturnsNull()
    {
        var (service, _) = BuildService();
        Assert.Null(service.MoveInDirectory(@"C:\Definitely\Does\Not\Exist\ghostfolder"));
    }

    [Fact]
    public void MoveInDirectory_PlainFile_ReturnsNull()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.txt");
        File.WriteAllText(source, "x");
        try
        {
            var result = service.MoveInDirectory(source);
            Assert.Null(result);
            Assert.True(File.Exists(source), "A plain file passed to MoveInDirectory must be left untouched.");
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveIn_TwoFilesWithSameName_ProduceDistinctDestinations()
    {
        var (service, root) = BuildService();
        var dirA = Path.Combine(Path.GetTempPath(), $"DOrgA_{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"DOrgB_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var sourceA = Path.Combine(dirA, "MyApp.lnk");
        var sourceB = Path.Combine(dirB, "MyApp.lnk");
        File.WriteAllText(sourceA, "A");
        File.WriteAllText(sourceB, "B");

        try
        {
            var resultA = service.MoveIn(sourceA);
            var resultB = service.MoveIn(sourceB);

            Assert.NotEqual(resultA, resultB);
            Assert.Equal("A", File.ReadAllText(resultA!));
            Assert.Equal("B", File.ReadAllText(resultB!));
        }
        finally
        {
            SafeDeleteDirectory(dirA);
            SafeDeleteDirectory(dirB);
            SafeDeleteDirectory(root);
        }
    }

    // ── Release ──────────────────────────────────────────────────────────────

    [Fact]
    public void Release_ManagedFile_DeletesIt()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "x");
        try
        {
            var managed = service.MoveIn(source)!;
            Assert.True(File.Exists(managed));

            service.Release(managed);

            Assert.False(File.Exists(managed));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Safety net: <see cref="PanelItemStorageService.Release"/> must never delete a file the
    /// service doesn't own — e.g. a plain reference path from a Folder Portal or a legacy panel item
    /// that still points straight at a user file.
    /// </summary>
    [Fact]
    public void Release_PathOutsideStorageRoot_DoesNotDeleteFile()
    {
        var (service, _) = BuildService();
        var untouchable = Path.Combine(Path.GetTempPath(), $"DOrgUntouchable_{Guid.NewGuid():N}.txt");
        File.WriteAllText(untouchable, "must survive");
        try
        {
            service.Release(untouchable);
            Assert.True(File.Exists(untouchable));
        }
        finally
        {
            SafeDeleteFile(untouchable);
        }
    }

    [Fact]
    public void Release_NonExistentManagedPath_DoesNotThrow()
    {
        var (service, root) = BuildService();
        var path = Path.Combine(root, "never-existed.lnk");
        var exception = Record.Exception(() => service.Release(path));
        Assert.Null(exception);
    }

    [Fact]
    public void Release_ManagedDirectory_DeletesItRecursively()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgFolder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "f.txt"), "x");
        try
        {
            var managed = service.MoveInDirectory(source)!;
            Assert.True(Directory.Exists(managed));

            service.Release(managed);

            Assert.False(Directory.Exists(managed));
        }
        finally
        {
            SafeDeleteDirectory(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Same safety guarantee as <see cref="Release_PathOutsideStorageRoot_DoesNotDeleteFile"/>,
    /// for directories — must never recursively delete a real folder the app doesn't own.
    /// </summary>
    [Fact]
    public void Release_DirectoryOutsideStorageRoot_DoesNotDeleteIt()
    {
        var (service, _) = BuildService();
        var untouchable = Path.Combine(Path.GetTempPath(), $"DOrgUntouchable_{Guid.NewGuid():N}");
        Directory.CreateDirectory(untouchable);
        File.WriteAllText(Path.Combine(untouchable, "f.txt"), "must survive");
        try
        {
            service.Release(untouchable);
            Assert.True(Directory.Exists(untouchable));
        }
        finally
        {
            SafeDeleteDirectory(untouchable);
        }
    }

    // ── F: manifest in-memory cache + write-through (phase-7 block F) ───────

    /// <summary>
    /// F-DoD-1: the manifest must be read from disk at most once per service instance, regardless
    /// of how many manifest-touching operations run in the same session. Before this block, every
    /// <c>MoveIn</c>/<c>Restore</c>/<c>Release</c> re-read the whole JSON file from disk; a
    /// multi-file drag&amp;drop paid that cost once per dropped file. Exercises well over the
    /// DoD's "&gt;= 50 operations" bar via a mix of MoveIn (write), Restore, and Release
    /// (read+write) — all should share the single lazily-loaded cache.
    /// </summary>
    [Fact]
    public void Manifest_ManyOperationsInOneSession_ReadsDiskAtMostOnce()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);

        const int fileCount = 30;
        var managedPaths = new List<string>();

        try
        {
            Assert.Equal(0, service.ManifestDiskReadCount);

            for (var i = 0; i < fileCount; i++)
            {
                var source = Path.Combine(sourceDir, $"item{i}.lnk");
                File.WriteAllText(source, $"content-{i}");

                var managed = service.MoveIn(source);
                Assert.NotNull(managed);
                managedPaths.Add(managed!);
            }

            // First MoveIn's WriteManifestEntry populated the cache from disk exactly once; every
            // subsequent MoveIn in this loop must have reused it.
            Assert.Equal(1, service.ManifestDiskReadCount);

            for (var i = 0; i < fileCount; i++)
            {
                if (i % 2 == 0)
                {
                    var outcome = service.Restore(managedPaths[i]);
                    Assert.Equal(RestoreOutcome.Restored, outcome);
                }
                else
                {
                    service.Release(managedPaths[i]);
                    Assert.False(File.Exists(managedPaths[i]));
                }
            }

            // 30 MoveIn + 30 Restore/Release = 60 manifest-touching operations, still a single disk read.
            Assert.Equal(1, service.ManifestDiskReadCount);
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// F-DoD-3: the manifest cache must not change the on-disk JSON shape. A manifest file written
    /// entirely by hand (no service instance involved) in the pre-existing flat
    /// <c>Dictionary&lt;string,string&gt;</c> shape must still be read correctly by a fresh service
    /// instance — proves format compatibility survives the switch to an in-memory cache.
    /// </summary>
    [Fact]
    public void Manifest_PreExistingFileFormat_IsReadCorrectlyByFreshInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);

        var managedPath = Path.Combine(root, $"PreExisting_{Guid.NewGuid():N}.lnk");
        var originalPath = Path.Combine(sourceDir, "Original.lnk");
        File.WriteAllText(managedPath, "legacy-managed-content");

        var manifestJson = JsonSerializer.Serialize(new Dictionary<string, string> { [managedPath] = originalPath });
        File.WriteAllText(Path.Combine(root, ".manifest.json"), manifestJson);

        try
        {
            var service = new PanelItemStorageService(root);
            var outcome = service.Restore(managedPath);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(originalPath));
            Assert.Equal("legacy-managed-content", File.ReadAllText(originalPath));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// F-DoD-5 (file): the manifest entry is now written before the destructive copy/delete step
    /// in <see cref="PanelItemStorageService.MoveIn(string, bool)"/> (rather than after), so a crash
    /// between the two can't lose the original path. On a <em>failed</em> copy, that early write
    /// must be rolled back — otherwise the manifest would carry a dangling entry pointing at a
    /// managed file that was never actually created.
    /// </summary>
    [Fact]
    public void MoveIn_CrossVolumeCopyFails_DoesNotLeaveManifestEntryBehind()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "must-survive");

        try
        {
            using (var lockHandle = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = service.MoveIn(source, forceCrossVolume: true);
                Assert.Null(result);
                lockHandle.Close();
            }

            AssertManifestFileIsEmpty(root);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// F-DoD-5 (directory) — same rollback guarantee as
    /// <see cref="MoveIn_CrossVolumeCopyFails_DoesNotLeaveManifestEntryBehind"/>, for
    /// <see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/>.
    /// </summary>
    [Fact]
    public void MoveInDirectory_CrossVolumeCopyFails_DoesNotLeaveManifestEntryBehind()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgFolder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        var fileA = Path.Combine(source, "a.txt");
        var fileB = Path.Combine(source, "b.txt");
        File.WriteAllText(fileA, "content-a");
        File.WriteAllText(fileB, "content-b");

        try
        {
            using (var lockHandle = new FileStream(fileB, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = service.MoveInDirectory(source, forceCrossVolume: true);
                Assert.Null(result);
                lockHandle.Close();
            }

            AssertManifestFileIsEmpty(root);
        }
        finally
        {
            SafeDeleteDirectory(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// F-DoD-6: all manifest cache mutations happen under <c>_manifestLock</c>. Hammers
    /// <see cref="PanelItemStorageService.MoveIn(string)"/> from 100 concurrent tasks against the
    /// same service instance and proves every one of them is later restorable — if the lock were
    /// missing or too narrow, a lost update to the in-memory dictionary would make some entries
    /// silently disappear and <c>Restore</c> would degrade to <see cref="RestoreOutcome.OriginalPathUnknown"/>.
    /// </summary>
    [Fact]
    public void Manifest_ConcurrentMoveIn_DoesNotLoseEntries()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);

        const int count = 100;
        var sources = new string[count];
        for (var i = 0; i < count; i++)
        {
            sources[i] = Path.Combine(sourceDir, $"item{i}.lnk");
            File.WriteAllText(sources[i], $"content-{i}");
        }

        try
        {
            var managedPaths = new string?[count];
            Parallel.For(0, count, i => managedPaths[i] = service.MoveIn(sources[i]));

            Assert.All(managedPaths, p => Assert.True(p is not null, "Every concurrent MoveIn must succeed."));

            // Restoring through the SAME service instance only proves the in-memory cache is
            // internally consistent — it can't catch a cache/disk desync (e.g. a lost
            // write-through under contention would still "restore" fine from the in-memory
            // dictionary while the file on disk silently missed the entry). Restore half through
            // a brand-new instance backed by the same storageRoot, forcing every one of those
            // reads through the on-disk manifest.
            for (var i = 0; i < managedPaths.Length; i++)
            {
                var outcome = i % 2 == 0
                    ? service.Restore(managedPaths[i]!)
                    : new PanelItemStorageService(root).Restore(managedPaths[i]!);
                Assert.Equal(RestoreOutcome.Restored, outcome);
            }
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// C1 regression: a transient read failure (e.g. antivirus/indexer briefly holding an
    /// exclusive handle on <c>.manifest.json</c> right after the atomic
    /// <see cref="File.Move(string, string, bool)"/> in <c>WriteManifestUnlocked</c>) must NOT
    /// permanently poison the in-memory cache with an empty snapshot, and must NOT be allowed to
    /// write-through that empty snapshot to disk — either of which would silently destroy every
    /// real origin-path mapping already on disk. Locks the manifest file exclusively, forces a
    /// read through it (failing), releases the lock, and asserts both that (a) the next Restore
    /// succeeds (the cache retried rather than sticking with the poisoned empty snapshot) and
    /// (b) the manifest file on disk still has its real entries — i.e. nothing wrote an empty
    /// snapshot over it while the lock was held.
    /// </summary>
    [Fact]
    public void Manifest_TransientReadFailure_DoesNotPoisonCacheOrDestroyDiskManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(root, ".manifest.json");

        var source = Path.Combine(sourceDir, "real-mapping.lnk");
        File.WriteAllText(source, "must-survive");

        try
        {
            // Populate a real, on-disk manifest entry via a fresh service instance (no cache yet).
            var writer = new PanelItemStorageService(root);
            var managed = writer.MoveIn(source);
            Assert.NotNull(managed);

            var manifestJsonBefore = File.ReadAllText(manifestPath);

            // A second, fresh instance whose very first manifest access hits a locked file —
            // simulates AV/indexer holding an exclusive handle at the wrong moment.
            var reader = new PanelItemStorageService(root);
            using (var lockHandle = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var outcomeDuringLock = reader.Restore(managed!);
                Assert.Equal(RestoreOutcome.OriginalPathUnknown, outcomeDuringLock);
                lockHandle.Close();
            }

            // (b) Nothing wrote an empty/poisoned snapshot to disk while the lock was held.
            Assert.Equal(manifestJsonBefore, File.ReadAllText(manifestPath));

            // (a) The next attempt retries the read from scratch (cache was never poisoned) and
            // now succeeds since the lock is gone.
            var outcomeAfterUnlock = reader.Restore(managed!);
            Assert.Equal(RestoreOutcome.Restored, outcomeAfterUnlock);
            Assert.True(File.Exists(source));
            Assert.Equal("must-survive", File.ReadAllText(source));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── B1: new {guid}\{originalName} storage layout (ADR-013 Decision 2) ───

    [Fact]
    public void MoveIn_ProducesPerItemGuidDirectory_FileNameIsOriginal()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyReport.docx");
        File.WriteAllText(source, "x");

        try
        {
            var result = service.MoveIn(source);

            Assert.NotNull(result);
            Assert.Equal("MyReport.docx", Path.GetFileName(result));

            var itemDir = Path.GetDirectoryName(result)!;
            var itemDirName = Path.GetFileName(itemDir);
            Assert.True(Guid.TryParseExact(itemDirName, "N", out _), $"Expected parent directory '{itemDirName}' to be a bare GUID.");

            var itemDirParent = Path.GetFullPath(Path.GetDirectoryName(itemDir)!).TrimEnd(Path.DirectorySeparatorChar);
            var expectedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(expectedRoot, itemDirParent, ignoreCase: true);
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveInDirectory_ProducesPerItemGuidDirectory_FolderNameIsOriginal()
    {
        var (service, root) = BuildService();
        var sourceParent = Path.Combine(Path.GetTempPath(), $"DOrgOrigParent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceParent);
        var source = Path.Combine(sourceParent, "MyFolder");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "f.txt"), "x");

        try
        {
            var result = service.MoveInDirectory(source);

            Assert.NotNull(result);
            Assert.Equal("MyFolder", Path.GetFileName(result));

            var itemDirName = Path.GetFileName(Path.GetDirectoryName(result));
            Assert.True(Guid.TryParseExact(itemDirName, "N", out _), $"Expected parent directory '{itemDirName}' to be a bare GUID.");
        }
        finally
        {
            SafeDeleteDirectory(sourceParent);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveIn_TwoFilesWithSameOriginalName_UseDistinctGuidDirectories_BothKeepOriginalName()
    {
        var (service, root) = BuildService();
        var dirA = Path.Combine(Path.GetTempPath(), $"DOrgA_{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"DOrgB_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var sourceA = Path.Combine(dirA, "Report.docx");
        var sourceB = Path.Combine(dirB, "Report.docx");
        File.WriteAllText(sourceA, "A");
        File.WriteAllText(sourceB, "B");

        try
        {
            var resultA = service.MoveIn(sourceA);
            var resultB = service.MoveIn(sourceB);

            Assert.NotNull(resultA);
            Assert.NotNull(resultB);
            Assert.Equal("Report.docx", Path.GetFileName(resultA));
            Assert.Equal("Report.docx", Path.GetFileName(resultB));
            Assert.NotEqual(Path.GetDirectoryName(resultA), Path.GetDirectoryName(resultB));
            Assert.Equal("A", File.ReadAllText(resultA!));
            Assert.Equal("B", File.ReadAllText(resultB!));
        }
        finally
        {
            SafeDeleteDirectory(dirA);
            SafeDeleteDirectory(dirB);
            SafeDeleteDirectory(root);
        }
    }

    // ── B2: orphaned empty per-item GUID directory cleanup ───────────────────

    [Fact]
    public void Restore_CleansUpTheNowEmptyPerItemGuidDirectory()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        File.WriteAllText(source, "x");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);
            var itemDir = Path.GetDirectoryName(managed)!;
            Assert.True(Directory.Exists(itemDir));

            var outcome = service.Restore(managed!);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.False(Directory.Exists(itemDir), "The now-empty per-item GUID directory must be cleaned up.");
            Assert.True(Directory.Exists(root), "The storage root itself must never be deleted.");
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Release_CleansUpTheNowEmptyPerItemGuidDirectory()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "x");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);
            var itemDir = Path.GetDirectoryName(managed)!;
            Assert.True(Directory.Exists(itemDir));

            service.Release(managed!);

            Assert.False(Directory.Exists(itemDir), "The now-empty per-item GUID directory must be cleaned up.");
            Assert.True(Directory.Exists(root), "The storage root itself must never be deleted.");
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_PerItemGuidDirectoryNotEmpty_IsNotDeleted()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "MyApp.lnk");
        File.WriteAllText(source, "x");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);
            var itemDir = Path.GetDirectoryName(managed)!;

            // Something else (not tracked by the manifest) also lives in the per-item directory —
            // simulates any stray file ending up alongside the managed item.
            File.WriteAllText(Path.Combine(itemDir, "stray.txt"), "leave me alone");

            var outcome = service.Restore(managed!);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(Directory.Exists(itemDir), "A non-empty per-item directory must never be deleted.");
            Assert.True(File.Exists(Path.Combine(itemDir, "stray.txt")));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Release_PerItemGuidDirectoryNotEmpty_IsNotDeleted()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "x");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);
            var itemDir = Path.GetDirectoryName(managed)!;
            File.WriteAllText(Path.Combine(itemDir, "stray.txt"), "leave me alone");

            service.Release(managed!);

            Assert.True(Directory.Exists(itemDir), "A non-empty per-item directory must never be deleted.");
            Assert.True(File.Exists(Path.Combine(itemDir, "stray.txt")));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Regression guard for the cleanup guard itself: a legacy flat-layout item (its "directory" IS
    /// the storage root — see <see cref="PanelItemStorageService.RenameManaged"/> remarks on
    /// <c>isLegacyFlat</c>) must never cause the storage root itself to be deleted, even once it
    /// becomes the last item there.
    /// </summary>
    [Fact]
    public void Restore_LegacyFlatItem_NeverDeletesStorageRoot()
    {
        var (service, root) = BuildService();
        Directory.CreateDirectory(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Legacy.lnk");
        File.WriteAllText(source, "legacy-content");

        // Simulate a legacy flat-layout managed item: file lives directly in root, with a hand-written
        // manifest entry (no per-item GUID directory — this predates ADR-013 Decision 2).
        var legacyManaged = Path.Combine(root, $"Legacy_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(legacyManaged, "legacy-content");
        var manifest = new Dictionary<string, string> { [legacyManaged] = source };
        File.WriteAllText(Path.Combine(root, ".manifest.json"), JsonSerializer.Serialize(manifest));

        try
        {
            var outcome = service.Restore(legacyManaged);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(Directory.Exists(root), "The storage root itself must never be deleted.");
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── B1: truncate-guard on destination path length (>240 chars) ──────────

    [Fact]
    public void MoveIn_LongFileName_TruncatesBaseNamePreservingExtension()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgLongOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var longBaseName = new string('a', 250);
        var source = Path.Combine(sourceDir, longBaseName + ".txt");
        File.WriteAllText(source, "long-name-content");

        try
        {
            var result = service.MoveIn(source);

            Assert.NotNull(result);
            Assert.True(result!.Length <= 240, $"Destination path must respect the 240-char guard, was {result.Length} chars: '{result}'.");
            Assert.EndsWith(".txt", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result));
            Assert.Equal("long-name-content", File.ReadAllText(result));

            var truncatedBase = Path.GetFileNameWithoutExtension(result);
            Assert.True(truncatedBase.Length < longBaseName.Length, "Base name must actually have been shortened.");
            Assert.StartsWith(truncatedBase, longBaseName, StringComparison.Ordinal);
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    // ── C1/C2: RenameManaged (ADR-013 Decision 5) ────────────────────────────

    [Fact]
    public void RenameManaged_HappyPath_MovesFileAndUpdatesManifestOriginalName()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Old.lnk");
        File.WriteAllText(source, "content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);
            var itemDir = Path.GetDirectoryName(managed)!;

            var renameResult = service.RenameManaged(managed!, "New.lnk");
            var newPath = renameResult.NewPath;

            Assert.Equal(RenameOutcome.Renamed, renameResult.Outcome);
            Assert.NotNull(newPath);
            Assert.Equal("New.lnk", Path.GetFileName(newPath));
            Assert.Equal(itemDir, Path.GetDirectoryName(newPath), ignoreCase: true);
            Assert.True(File.Exists(newPath));
            Assert.False(File.Exists(managed!), "Old managed path must no longer exist after rename.");
            Assert.Equal("content", File.ReadAllText(newPath!));

            // Manifest key was re-keyed to the new managed path, and the recorded original path now
            // carries the new name in the SAME original directory — proven by round-tripping via Restore.
            var outcome = service.Restore(newPath!);
            var expectedOriginal = Path.Combine(sourceDir, "New.lnk");

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(expectedOriginal), "Restore after rename must place the file back under its NEW name.");
            Assert.Equal("content", File.ReadAllText(expectedOriginal));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void RenameManaged_LegacyFlatItem_RelocatesIntoNewGuidDirectory()
    {
        var (service, root) = BuildService();
        Directory.CreateDirectory(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Legacy.lnk");
        File.WriteAllText(source, "legacy-content");

        var legacyManaged = Path.Combine(root, $"Legacy_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(legacyManaged, "legacy-content");
        var manifest = new Dictionary<string, string> { [legacyManaged] = source };
        File.WriteAllText(Path.Combine(root, ".manifest.json"), JsonSerializer.Serialize(manifest));

        try
        {
            var renameResult = service.RenameManaged(legacyManaged, "Cured.lnk");
            var newPath = renameResult.NewPath;

            Assert.Equal(RenameOutcome.Renamed, renameResult.Outcome);
            Assert.NotNull(newPath);
            Assert.Equal("Cured.lnk", Path.GetFileName(newPath));

            var newItemDir = Path.GetDirectoryName(newPath)!;
            Assert.False(
                string.Equals(
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(newItemDir).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase),
                "Renaming a legacy flat item must relocate it into a NEW per-item directory, not leave it directly in the storage root.");
            Assert.True(Guid.TryParseExact(Path.GetFileName(newItemDir), "N", out _));

            Assert.True(File.Exists(newPath));
            Assert.False(File.Exists(legacyManaged), "Legacy flat file must have moved out of the storage root.");

            var outcome = service.Restore(newPath!);
            var expectedOriginal = Path.Combine(sourceDir, "Cured.lnk");
            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(expectedOriginal));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Mirrors <see cref="Manifest_TransientReadFailure_DoesNotPoisonCacheOrDestroyDiskManifest"/>:
    /// a manifest cache that fails to load (the manifest file is exclusively locked by someone else)
    /// must abort the rename entirely, before any file-system mutation — see ADR-013 Decision 5 /
    /// <see cref="IPanelItemStorageService.RenameManaged"/> remarks.
    /// </summary>
    [Fact]
    public void RenameManaged_ManifestCacheUntrusted_AbortsAndLeavesFileAndManifestUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(root, ".manifest.json");

        var source = Path.Combine(sourceDir, "Old.lnk");
        File.WriteAllText(source, "must-not-move");

        try
        {
            // Populate a real on-disk manifest entry via a fresh, unrelated service instance.
            var writer = new PanelItemStorageService(root);
            var managed = writer.MoveIn(source);
            Assert.NotNull(managed);

            var manifestJsonBefore = File.ReadAllText(manifestPath);

            // A second, fresh instance whose very first manifest access hits a locked file.
            var renamer = new PanelItemStorageService(root);
            RenameResult result;
            using (var lockHandle = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = renamer.RenameManaged(managed!, "New.lnk");
                lockHandle.Close();
            }

            Assert.Equal(RenameOutcome.Failed, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(managed!), "Managed file must be untouched when the manifest write aborts.");
            Assert.Equal("must-not-move", File.ReadAllText(managed!));
            Assert.Equal(manifestJsonBefore, File.ReadAllText(manifestPath));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M3 regression (found by code-quality-reviewer): a name collision must surface as the specific
    /// <see cref="RenameOutcome.NameTaken"/>, not the generic <see cref="RenameOutcome.Failed"/> —
    /// see <see cref="IPanelItemStorageService.RenameManaged"/> remarks and DoD §11.
    /// </summary>
    [Fact]
    public void RenameManaged_DestinationNameAlreadyTaken_ReturnsNameTakenAndFileUntouched()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "A.lnk");
        File.WriteAllText(source, "content-a");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);
            var itemDir = Path.GetDirectoryName(managed)!;

            // Something else already occupies the desired name inside the SAME per-item directory.
            var takenPath = Path.Combine(itemDir, "Taken.lnk");
            File.WriteAllText(takenPath, "already-here");

            var result = service.RenameManaged(managed!, "Taken.lnk");

            Assert.Equal(RenameOutcome.NameTaken, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(managed!), "Source must be untouched on a collision.");
            Assert.Equal("content-a", File.ReadAllText(managed!));
            Assert.Equal("already-here", File.ReadAllText(takenPath));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M2 regression (found by code-quality-reviewer): if the physical move fails AFTER the
    /// manifest has already been updated for the rename, the item must remain fully restorable
    /// under its OLD managed path — no manual/rollback re-key that can itself fail and strand the
    /// manifest is involved anymore (see <see cref="PanelItemStorageService"/> "TryRenameManifestEntry"
    /// remarks). Before the fix, a failed rollback write here could leave the manifest keyed only to
    /// the (never-created) new path, so <see cref="Restore"/> against the old (still physically
    /// present, unchanged) managed path would return <see cref="RestoreOutcome.OriginalPathUnknown"/>
    /// — which calling code treats as "remove from panel", i.e. permanent data loss.
    /// </summary>
    [Fact]
    public void RenameManaged_MoveFailsAfterManifestUpdated_OldPathStillRestorable()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Old.lnk");
        const string content = "must-not-be-lost";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            RenameResult result;
            using (var lockHandle = new FileStream(managed!, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // File.Move needs exclusive access to the source — this lock makes the physical
                // move fail AFTER TryRenameManifestEntry has already added the new manifest entry.
                result = service.RenameManaged(managed!, "New.lnk");
                lockHandle.Close();
            }

            Assert.Equal(RenameOutcome.Failed, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(managed!), "Managed file must still be at its OLD path — the move never happened.");
            Assert.Equal(content, File.ReadAllText(managed!));

            // The critical assertion: the OLD manifest entry must still be valid — proving the item
            // is still restorable, not silently orphaned.
            var outcome = service.Restore(managed!);
            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(source), "File must be restorable to its original location after a failed rename.");
            Assert.Equal(content, File.ReadAllText(source));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void RenameManaged_CaseOnlyRename_Succeeds()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Report.lnk");
        File.WriteAllText(source, "content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var result = service.RenameManaged(managed!, "report.lnk");

            Assert.Equal(RenameOutcome.Renamed, result.Outcome);
            Assert.NotNull(result.NewPath);
            Assert.Equal("report.lnk", Path.GetFileName(result.NewPath));
            Assert.True(File.Exists(result.NewPath));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// N4 regression (found by code-quality-reviewer): when the truncate-guard shortens the
    /// requested name because the destination path would otherwise exceed the soft path-length cap,
    /// the manifest must record the SAME (truncated) name that actually landed on disk — not the
    /// raw, untruncated name the caller asked for. Otherwise a later <see cref="Restore"/> would put
    /// the file back under a name that was never actually used for the managed copy.
    /// </summary>
    [Fact]
    public void RenameManaged_LongNameTruncated_ManifestRecordsTruncatedNameMatchingDisk()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Old.lnk");
        const string content = "content";
        File.WriteAllText(source, content);

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var longName = new string('b', 250) + ".lnk";
            var result = service.RenameManaged(managed!, longName);

            Assert.Equal(RenameOutcome.Renamed, result.Outcome);
            Assert.NotNull(result.NewPath);
            var actualOnDiskName = Path.GetFileName(result.NewPath);
            Assert.True(actualOnDiskName.Length < longName.Length, "Name must actually have been truncated.");
            Assert.True(File.Exists(result.NewPath));

            // Restore must put the file back under the SAME truncated name that's on disk — proving
            // the manifest's recorded original-name portion matches, not the raw untruncated input.
            var outcome = service.Restore(result.NewPath!);
            var expectedOriginal = Path.Combine(sourceDir, actualOnDiskName);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(expectedOriginal), $"Expected restored file at '{expectedOriginal}' (matching the truncated on-disk name).");
            Assert.Equal(content, File.ReadAllText(expectedOriginal));
        }
        finally
        {
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void RenameManaged_PathNotManaged_ReturnsFailed()
    {
        var (service, _) = BuildService();
        var outside = Path.Combine(Path.GetTempPath(), $"DOrgOutside_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(outside, "not-managed");

        try
        {
            var result = service.RenameManaged(outside, "New.lnk");

            Assert.Equal(RenameOutcome.Failed, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            SafeDeleteFile(outside);
        }
    }

    [Fact]
    public void RenameManaged_NewNameContainsPathSeparator_ReturnsInvalidName()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var result = service.RenameManaged(managed!, @"..\evil.lnk");

            Assert.Equal(RenameOutcome.InvalidName, result.Outcome);
            Assert.Null(result.NewPath);
            Assert.True(File.Exists(managed!), "A defense-in-depth reject must not touch the file.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void RenameManaged_StoredPathDoesNotExistOnDisk_ReturnsNotFound()
    {
        var (service, root) = BuildService();
        Directory.CreateDirectory(root);
        var ghostGuidDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ghostGuidDir);
        var ghost = Path.Combine(ghostGuidDir, "ghost.lnk"); // never actually created

        try
        {
            var result = service.RenameManaged(ghost, "New.lnk");
            Assert.Equal(RenameOutcome.NotFound, result.Outcome);
            Assert.Null(result.NewPath);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── TryFindManagedPath (ADR-009 Amendment 2026-09-22, P08 stale-path resolution) ────────────

    [Fact]
    public void TryFindManagedPath_OriginalHasLiveManagedCopy_ReturnsTrueAndManagedPath()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgStalePath_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "shortcut-content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var found = service.TryFindManagedPath(source, out var managedPath);

            Assert.True(found);
            Assert.Equal(managed, managedPath, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryFindManagedPath_IsCaseInsensitive()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgStalePathCase_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "content");

        try
        {
            service.MoveIn(source);

            var found = service.TryFindManagedPath(source.ToUpperInvariant(), out var managedPath);

            Assert.True(found);
            Assert.NotNull(managedPath);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryFindManagedPath_NoManifestEntry_ReturnsFalse()
    {
        var (service, root) = BuildService();

        try
        {
            var found = service.TryFindManagedPath(@"C:\never\dropped.lnk", out var managedPath);

            Assert.False(found);
            Assert.Null(managedPath);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryFindManagedPath_ManagedCopyMissingFromDisk_ReturnsFalse()
    {
        // A manifest entry can outlive its managed file if it was deleted by hand outside this
        // service (or by a bug elsewhere) — TryFindManagedPath must not hand back a path that would
        // just trade one broken icon/launch target for another.
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgStalePathGhost_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            File.Delete(managed!); // simulate the managed copy vanishing without going through Release

            var found = service.TryFindManagedPath(source, out var managedPath);

            Assert.False(found);
            Assert.Null(managedPath);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryFindManagedPath_IsReadOnly_DoesNotMutateManifestFile()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgStalePathReadOnly_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "content");

        try
        {
            service.MoveIn(source);

            var manifestPath = Path.Combine(root, ".manifest.json");
            var before = File.ReadAllText(manifestPath);

            service.TryFindManagedPath(source, out _);
            service.TryFindManagedPath("some/other/nonexistent/path.lnk", out _);

            var after = File.ReadAllText(manifestPath);
            Assert.Equal(before, after);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryFindManagedPath_EmptyOrWhitespacePath_ReturnsFalse()
    {
        var (service, root) = BuildService();

        try
        {
            Assert.False(service.TryFindManagedPath(string.Empty, out var managedPath1));
            Assert.Null(managedPath1);

            Assert.False(service.TryFindManagedPath("   ", out var managedPath2));
            Assert.Null(managedPath2);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── FindManagedCandidates / TryFindManagedPath ambiguity (M1 fix, P08 fix cycle 1, 2026-09-22) ──

    /// <summary>
    /// Directly seeds a manifest recording <paramref name="originalPath"/> against two DIFFERENT,
    /// both-live managed paths — a scenario <see cref="PanelItemStorageService"/>'s own public API
    /// can never produce on its own (each <c>MoveIn</c> call consumes its unique source), but one the
    /// manifest's plain <c>Dictionary&lt;managedPath, originalPath&gt;</c> shape does not prevent, and
    /// which a manually-edited/legacy-imported manifest could contain.
    /// </summary>
    private static (string ManagedA, string ManagedB) SeedAmbiguousManifest(string root, string originalPath)
    {
        Directory.CreateDirectory(root);
        var managedA = Path.Combine(root, "candidate-a.lnk");
        var managedB = Path.Combine(root, "candidate-b.lnk");
        File.WriteAllText(managedA, "a");
        File.WriteAllText(managedB, "b");

        var manifest = new Dictionary<string, string> { [managedA] = originalPath, [managedB] = originalPath };
        File.WriteAllText(Path.Combine(root, ".manifest.json"), JsonSerializer.Serialize(manifest));

        return (managedA, managedB);
    }

    [Fact]
    public void FindManagedCandidates_SingleLiveCandidate_ReturnsExactlyThatOne()
    {
        var (service, root) = BuildService();
        var source = Path.Combine(Path.GetTempPath(), $"DOrgCandidateSingle_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, "content");

        try
        {
            var managed = service.MoveIn(source);
            Assert.NotNull(managed);

            var candidates = service.FindManagedCandidates(source);

            Assert.Equal([managed], candidates, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void FindManagedCandidates_NoManifestEntry_ReturnsEmpty()
    {
        var (service, root) = BuildService();

        try
        {
            Assert.Empty(service.FindManagedCandidates(@"C:\never\dropped.lnk"));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void FindManagedCandidates_AmbiguousManifest_ReturnsBothLiveCandidates()
    {
        var (service, root) = BuildService();
        const string originalPath = @"C:\fake\ambiguous-original.lnk";

        try
        {
            var (managedA, managedB) = SeedAmbiguousManifest(root, originalPath);

            var candidates = service.FindManagedCandidates(originalPath);

            Assert.Equal(2, candidates.Count);
            Assert.Contains(managedA, candidates, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(managedB, candidates, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryFindManagedPath_AmbiguousManifest_TwoLiveCandidatesForSameOriginal_ReturnsFalse()
    {
        // CRITICAL/MAJOR M1: picking the first live candidate by dictionary enumeration order (the
        // pre-fix behavior) risked silently resolving to a managed copy actually owned by a different
        // panel. An ambiguous manifest must resolve to nothing, exactly like
        // TryFindManagedPath_NoManifestEntry_ReturnsFalse — explicit refusal over a guess.
        var (service, root) = BuildService();
        const string originalPath = @"C:\fake\ambiguous-original.lnk";

        try
        {
            SeedAmbiguousManifest(root, originalPath);

            var found = service.TryFindManagedPath(originalPath, out var managedPath);

            Assert.False(found);
            Assert.Null(managedPath);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void FindManagedCandidates_OneOfTwoManifestEntriesGhosted_ReturnsOnlyTheLiveOne()
    {
        // Not ambiguous in practice once the dead entry is filtered out — same "verify it still
        // resolves on disk" discipline as TryFindManagedPath_ManagedCopyMissingFromDisk_ReturnsFalse.
        var (service, root) = BuildService();
        const string originalPath = @"C:\fake\one-ghosted-original.lnk";

        try
        {
            var (managedA, managedB) = SeedAmbiguousManifest(root, originalPath);
            File.Delete(managedB); // simulate managedB vanishing without going through Release

            var candidates = service.FindManagedCandidates(originalPath);

            Assert.Equal([managedA], candidates, StringComparer.OrdinalIgnoreCase);
            Assert.True(service.TryFindManagedPath(originalPath, out var managedPath));
            Assert.Equal(managedA, managedPath, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static void AssertManifestFileIsEmpty(string root)
    {
        var manifestPath = Path.Combine(root, ".manifest.json");
        if (!File.Exists(manifestPath)) return;

        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath));
        Assert.True(manifest is null || manifest.Count == 0, "Failed move must not leave a dangling manifest entry.");
    }
}
