using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="PanelItemStorageService"/>.
/// </summary>
/// <remarks>
/// Covers the Move semantics that replaced the earlier Copy-based Import/ImportDirectory: dropping a
/// file/folder into a panel now moves the original into app-managed storage (declutters the source
/// immediately) instead of leaving a copy behind, and "Remove from panel" moves a managed item back to
/// its original location via <see cref="PanelItemStorageService.Restore(string, bool)"/>. The regression this guards
/// against — a panel item breaking because it referenced the original path instead of an independent
/// copy — is why <see cref="PanelItemStorageService.MoveIn(string, bool)"/>/<see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/>
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
    // ── D1: MoveIn same-volume ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveIn(string, bool)"/> on the same volume
    /// removes the source file and creates a managed copy with identical content (D1).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/> on the same
    /// volume moves the whole source folder and ignores the cross-volume file-count safety cap (D2).
    /// </summary>
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

    /// <summary>
    /// Verifies that a forced cross-volume <see cref="PanelItemStorageService.MoveIn(string, bool)"/>
    /// copies the file to managed storage first and only deletes the original source after the copy is
    /// verified complete (D3).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.Restore(string, bool)"/> moves a managed file
    /// back to its original location and removes the managed copy (D6).
    /// </summary>
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

    /// <summary>
    /// Verifies that restoring to a path already occupied by another file lands the restored file under
    /// a collision-free name instead of overwriting the pre-existing file (D7).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.Restore(string, bool)"/> recreates the original
    /// parent directory if it was deleted before the restore happens (D8).
    /// </summary>
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

    /// <summary>
    /// Verifies that restoring a managed path with no manifest entry (a legacy item imported before
    /// Move semantics existed) returns <see cref="RestoreOutcome.OriginalPathUnknown"/> and leaves the
    /// managed copy untouched (D9).
    /// </summary>
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

    /// <summary>
    /// Verifies that restoring a path outside the managed storage root is a no-op that returns
    /// <see cref="RestoreOutcome.NotManaged"/> and leaves the file untouched (D10).
    /// </summary>
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

    /// <summary>
    /// Verifies that a forced cross-volume <see cref="PanelItemStorageService.Restore(string, bool)"/>
    /// copies the managed file back to its original location, deletes the managed copy, and cleans up
    /// the manifest entry so a second restore attempt reports <see cref="RestoreOutcome.OriginalPathUnknown"/> (M2).
    /// </summary>
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
    /// Locks the managed file itself so <c>forceCrossVolume: true</c> — which skips <see cref="File.Move(string, string)"/>
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

    /// <summary>
    /// MINOR 3 (release-polish 1.0 final review): the MANAGED copy (the cross-volume restore's source)
    /// being read-only must not turn an otherwise-successful copy into a reported failure — the read-only
    /// attribute is cleared before the delete, mirroring <c>PanelItemsUninstallRestorer</c>'s identical fix
    /// (<c>DeleteVerifiedSourceOrWarn</c>). Before the fix, <see cref="File.Delete(string)"/> threw, which
    /// propagated out to <see cref="PanelItemStorageService.Restore(string, bool)"/>'s own catch and
    /// reported <see cref="RestoreOutcome.Failed"/> even though the copy had already landed at the original
    /// location — leaving the item duplicated at both places with the manifest entry never released.
    /// </summary>
    [Fact]
    public void Restore_CrossVolumeFallback_File_ReadOnlyManagedSource_StillDeletesAndReturnsRestored()
    {
        var (service, root) = BuildService();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrigDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "ReadOnly.lnk");
        const string content = "read-only-cross-volume-restore";
        File.WriteAllText(source, content);

        string? managed = null;
        try
        {
            managed = service.MoveIn(source);
            Assert.NotNull(managed);
            File.SetAttributes(managed!, File.GetAttributes(managed!) | FileAttributes.ReadOnly);

            var outcome = service.Restore(managed!, forceCrossVolume: true);

            Assert.Equal(RestoreOutcome.Restored, outcome);
            Assert.True(File.Exists(source));
            Assert.Equal(content, File.ReadAllText(source));
            Assert.False(File.Exists(managed!), "the read-only managed source must still be deleted after a verified copy.");

            // Manifest entry released: a second Restore attempt reports OriginalPathUnknown.
            var secondOutcome = service.Restore(managed!, forceCrossVolume: true);
            Assert.Equal(RestoreOutcome.OriginalPathUnknown, secondOutcome);
        }
        finally
        {
            // File.Copy preserves attributes, so the RESTORED copy at `source` also inherited ReadOnly from
            // the managed copy — clear it (on both possible surviving locations) before cleanup, or the test
            // host's own directory delete throws UnauthorizedAccessException (not caught by SafeDeleteDirectory,
            // which only swallows IOException).
            if (managed is not null && File.Exists(managed)) File.SetAttributes(managed, File.GetAttributes(managed) & ~FileAttributes.ReadOnly);
            if (File.Exists(source)) File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that a forced cross-volume <see cref="PanelItemStorageService.Restore(string, bool)"/>
    /// on a managed directory copies the whole folder back to its original location and deletes the
    /// managed copy (M2).
    /// </summary>
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

    /// <summary>
    /// Verifies that the on-disk manifest survives across separate <see cref="PanelItemStorageService"/>
    /// instances (simulating an app restart), so a second instance can still restore an item moved in by
    /// the first (D14).
    /// </summary>
    [Fact]
    public void Manifest_PersistsAcrossServiceInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        var firstInstance = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
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
            var secondInstance = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.IsManaged"/> returns true for a path nested
    /// inside the storage root.
    /// </summary>
    [Fact]
    public void IsManaged_PathInsideStorageRoot_ReturnsTrue()
    {
        var (service, root) = BuildService();
        var path = Path.Combine(root, "whatever.lnk");
        Assert.True(service.IsManaged(path));
    }

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.IsManaged"/> returns false for a path outside
    /// the storage root.
    /// </summary>
    [Fact]
    public void IsManaged_PathOutsideStorageRoot_ReturnsFalse()
    {
        var (service, _) = BuildService();
        Assert.False(service.IsManaged(@"C:\Users\someone\Desktop\MyApp.lnk"));
    }

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.IsManaged"/> returns false for an empty path.
    /// </summary>
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
    /// treated as managed. That's dangerous because <see cref="PanelItemStorageService.ReleaseToRecycleBin"/> and
    /// <see cref="PanelItemStorageService.Restore(string, bool)"/> both gate destructive/relocating filesystem
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveIn(string, bool)"/> returns null for a
    /// source path that does not exist.
    /// </summary>
    [Fact]
    public void MoveIn_MissingSource_ReturnsNull()
    {
        var (service, _) = BuildService();
        var result = service.MoveIn(@"C:\Definitely\Does\Not\Exist\ghost.lnk");
        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that passing a directory to <see cref="PanelItemStorageService.MoveIn(string, bool)"/>
    /// returns null and leaves the directory untouched.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveIn(string, bool)"/> returns null for an
    /// empty path.
    /// </summary>
    [Fact]
    public void MoveIn_EmptyPath_ReturnsNull()
    {
        var (service, _) = BuildService();
        Assert.Null(service.MoveIn(string.Empty));
    }

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/> returns null
    /// for a source folder that does not exist.
    /// </summary>
    [Fact]
    public void MoveInDirectory_MissingSource_ReturnsNull()
    {
        var (service, _) = BuildService();
        Assert.Null(service.MoveInDirectory(@"C:\Definitely\Does\Not\Exist\ghostfolder"));
    }

    /// <summary>
    /// Verifies that passing a plain file to <see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/>
    /// returns null and leaves the file untouched.
    /// </summary>
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

    /// <summary>
    /// Verifies that two source files that share the same file name from different origin folders
    /// produce two distinct, independently readable managed destinations.
    /// </summary>
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

    // ── ReleaseToRecycleBin (ADR-035) ──────────────────────────────────────────

    /// <summary>
    /// Only managed paths go to the shell — in ONE batch, with no owner window — and a path outside
    /// managed storage is never touched nor reported.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_PassesOnlyManagedPaths_InSingleBatch()
    {
        var native = CreateRecyclingNativeApi();
        var (service, root) = BuildService(native);
        var sourceA = NewTempSourceFile("a");
        var sourceB = NewTempSourceFile("b");
        var untouchable = Path.Combine(Path.GetTempPath(), $"DOrgUntouchable_{Guid.NewGuid():N}.txt");
        File.WriteAllText(untouchable, "must survive");
        try
        {
            var managedA = service.MoveIn(sourceA)!;
            var managedB = service.MoveIn(sourceB)!;

            var outcome = service.ReleaseToRecycleBin([managedA, untouchable, managedB]);

            native.Verify(
                n => n.RecycleItems(
                    It.Is<string[]>(paths => paths.Length == 2
                        && paths.Contains(managedA, StringComparer.OrdinalIgnoreCase)
                        && paths.Contains(managedB, StringComparer.OrdinalIgnoreCase)),
                    IntPtr.Zero),
                Times.Once);
            native.Verify(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()), Times.Once);
            Assert.True(File.Exists(untouchable), "A path outside managed storage must never be touched.");
            Assert.Equal([managedA, managedB], outcome.Recycled);
            Assert.Empty(outcome.Remaining);
            Assert.True(outcome.IsComplete);
            Assert.False(outcome.Cancelled);
        }
        finally
        {
            SafeDeleteFile(sourceA);
            SafeDeleteFile(sourceB);
            SafeDeleteFile(untouchable);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Success: the recycled file and folder are gone, their manifest entries are removed and the
    /// now-empty per-item GUID wrapper directories are cleaned up.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_ShellRecyclesEverything_ClearsManifestAndGuidDirectories()
    {
        var (service, root) = BuildService();
        var sourceFile = NewTempSourceFile();
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgFolder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "f.txt"), "x");
        try
        {
            var managedFile = service.MoveIn(sourceFile)!;
            var managedDir = service.MoveInDirectory(sourceDir)!;
            Assert.Equal(2, ReadManifest(root).Count);

            var outcome = service.ReleaseToRecycleBin([managedFile, managedDir]);

            Assert.True(outcome.IsComplete);
            Assert.Equal(2, outcome.Recycled.Count);
            Assert.False(File.Exists(managedFile));
            Assert.False(Directory.Exists(managedDir));
            Assert.Empty(ReadManifest(root));
            Assert.False(Directory.Exists(Path.GetDirectoryName(managedFile)), "Empty GUID wrapper of the file must be removed.");
            Assert.False(Directory.Exists(Path.GetDirectoryName(managedDir)), "Empty GUID wrapper of the folder must be removed.");
            Assert.True(Directory.Exists(root), "The storage root itself must never be deleted.");
        }
        finally
        {
            SafeDeleteFile(sourceFile);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Failure: the shell reports failure and recycles nothing. Nothing may be deleted by any fallback
    /// — every file stays on disk, the manifest is intact (Restore still works) and everything is
    /// reported as remaining.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_ShellFailsAndRecyclesNothing_FilesAndManifestIntact()
    {
        var native = new Mock<INativeApi>();
        native.Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>())).Returns(false);
        var (service, root) = BuildService(native);
        var sourceFile = NewTempSourceFile("keep me");
        var sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgFolder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "f.txt"), "keep me too");
        try
        {
            var managedFile = service.MoveIn(sourceFile)!;
            var managedDir = service.MoveInDirectory(sourceDir)!;

            var outcome = service.ReleaseToRecycleBin([managedFile, managedDir]);

            Assert.False(outcome.IsComplete);
            Assert.True(outcome.Cancelled);
            Assert.Empty(outcome.Recycled);
            Assert.Equal([managedFile, managedDir], outcome.Remaining);
            Assert.Equal("keep me", File.ReadAllText(managedFile));
            Assert.Equal("keep me too", File.ReadAllText(Path.Combine(managedDir, "f.txt")));
            Assert.Equal(2, ReadManifest(root).Count);

            // The manifest entry survived, so the item can still go back to where it came from.
            Assert.Equal(RestoreOutcome.Restored, service.Restore(managedFile));
            Assert.True(File.Exists(sourceFile));
        }
        finally
        {
            SafeDeleteFile(sourceFile);
            SafeDeleteDirectory(sourceDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Partial success (e.g. the second item is locked, or the user declined the "delete permanently?"
    /// prompt for it): the outcome is decided per item by what still exists, and only the recycled
    /// item's manifest entry and GUID directory are removed.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_PartialSuccess_OnlyRecycledItemIsForgotten()
    {
        string? recycledTarget = null;
        var native = new Mock<INativeApi>();
        native.Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Returns((string[] paths, IntPtr _) =>
            {
                recycledTarget = paths[0];
                SimulateRecycle(paths[0]);
                return false; // fAnyOperationsAborted
            });
        var (service, root) = BuildService(native);
        var sourceA = NewTempSourceFile("a");
        var sourceB = NewTempSourceFile("b");
        try
        {
            var managedA = service.MoveIn(sourceA)!;
            var managedB = service.MoveIn(sourceB)!;

            var outcome = service.ReleaseToRecycleBin([managedA, managedB]);

            Assert.Equal(managedA, recycledTarget, StringComparer.OrdinalIgnoreCase);
            Assert.Equal([managedA], outcome.Recycled);
            Assert.Equal([managedB], outcome.Remaining);
            Assert.True(outcome.Cancelled);
            Assert.False(outcome.IsComplete);

            var manifest = ReadManifest(root);
            Assert.False(manifest.ContainsKey(managedA));
            Assert.True(manifest.ContainsKey(managedB));
            Assert.False(Directory.Exists(Path.GetDirectoryName(managedA)));
            Assert.True(File.Exists(managedB));
        }
        finally
        {
            SafeDeleteFile(sourceA);
            SafeDeleteFile(sourceB);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Only unmanaged paths (or none): the shell is never called and the outcome is empty/complete.
    /// Covers both files and directories outside managed storage.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_NoManagedPaths_NeverCallsShell()
    {
        var native = CreateRecyclingNativeApi();
        var (service, root) = BuildService(native);
        var untouchableFile = Path.Combine(Path.GetTempPath(), $"DOrgUntouchable_{Guid.NewGuid():N}.txt");
        var untouchableDir = Path.Combine(Path.GetTempPath(), $"DOrgUntouchable_{Guid.NewGuid():N}");
        File.WriteAllText(untouchableFile, "must survive");
        Directory.CreateDirectory(untouchableDir);
        try
        {
            var outcome = service.ReleaseToRecycleBin([untouchableFile, untouchableDir, string.Empty]);
            var empty = service.ReleaseToRecycleBin([]);

            native.Verify(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()), Times.Never);
            Assert.True(File.Exists(untouchableFile));
            Assert.True(Directory.Exists(untouchableDir));
            Assert.Empty(outcome.Recycled);
            Assert.Empty(outcome.Remaining);
            Assert.True(outcome.IsComplete);
            Assert.True(empty.IsComplete);
        }
        finally
        {
            SafeDeleteFile(untouchableFile);
            SafeDeleteDirectory(untouchableDir);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M8 (release-hardening review): <see cref="PanelItemStorageService.IsManaged"/> treats the storage
    /// root itself, and any per-item GUID wrapper directory immediately below it, as "managed" (by
    /// design, for other callers) — but <see cref="PanelItemStorageService.ReleaseToRecycleBin"/> must
    /// never send either to the shell: recycling the root would destroy every managed item and the
    /// manifest in one shot, and recycling a bare wrapper directory would delete the real item's content
    /// while leaving a stale, unremovable manifest entry behind (its key is the nested leaf path, never
    /// the wrapper itself).
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_StorageRootOrGuidWrapper_SkippedNotRecycled()
    {
        var native = CreateRecyclingNativeApi();
        var (service, root) = BuildService(native);
        var source = NewTempSourceFile();
        try
        {
            var managed = service.MoveIn(source)!;
            var itemDir = Path.GetDirectoryName(managed)!;

            var outcome = service.ReleaseToRecycleBin([root, itemDir]);

            native.Verify(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()), Times.Never);
            Assert.Empty(outcome.Recycled);
            Assert.Empty(outcome.Remaining);
            Assert.True(outcome.IsComplete);
            Assert.True(Directory.Exists(root), "The storage root itself must never be sent to the shell.");
            Assert.True(Directory.Exists(itemDir), "A bare per-item GUID wrapper directory must never be sent to the shell.");
            Assert.True(File.Exists(managed), "The real managed item must survive an attempt to recycle its container.");
            Assert.Single(ReadManifest(root));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M8 must not lock out pre-ADR-013 installs: a legacy flat-layout item is a FILE directly under
    /// the storage root (no per-item GUID directory). It is a single managed item and must still be
    /// recyclable, otherwise a panel holding one could never be removed.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_LegacyFlatLayoutFile_IsRecycled()
    {
        var native = CreateRecyclingNativeApi();
        var (service, root) = BuildService(native);
        Directory.CreateDirectory(root);
        var legacy = Path.Combine(root, $"Legacy_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(legacy, "legacy-flat-item");
        var manifestJson = JsonSerializer.Serialize(new Dictionary<string, string> { [legacy] = Path.Combine(Path.GetTempPath(), "Original.lnk") });
        File.WriteAllText(Path.Combine(root, ".manifest.json"), manifestJson);
        try
        {
            var outcome = service.ReleaseToRecycleBin([legacy]);

            native.Verify(n => n.RecycleItems(It.Is<string[]>(p => p.Length == 1 && p[0] == legacy), It.IsAny<IntPtr>()), Times.Once);
            Assert.Single(outcome.Recycled);
            Assert.Empty(outcome.Remaining);
            Assert.False(File.Exists(legacy));
            Assert.True(Directory.Exists(root), "The storage root itself must survive recycling a legacy flat item.");
            AssertManifestFileIsEmpty(root);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A managed path that is already gone is not sent to the shell (SHFileOperation would fail the
    /// whole batch on a missing source) and is reported as recycled — there's nothing left to keep.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_AlreadyMissingManagedPath_NotSentToShell_ReportedRecycled()
    {
        var native = CreateRecyclingNativeApi();
        var (service, root) = BuildService(native);
        var missing = Path.Combine(root, Guid.NewGuid().ToString("N"), "never-existed.lnk");

        var outcome = service.ReleaseToRecycleBin([missing]);

        Assert.Equal([missing], outcome.Recycled);
        Assert.True(outcome.IsComplete);
        native.Verify(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()), Times.Never);
        SafeDeleteDirectory(root);
    }

    /// <summary>
    /// The same managed path listed twice goes to the shell once (a duplicate would make the second
    /// delete fail) and is reported once.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_DuplicatePaths_SentOnce()
    {
        var native = CreateRecyclingNativeApi();
        var (service, root) = BuildService(native);
        var source = NewTempSourceFile();
        try
        {
            var managed = service.MoveIn(source)!;

            var outcome = service.ReleaseToRecycleBin([managed, managed.ToUpperInvariant()]);

            native.Verify(n => n.RecycleItems(It.Is<string[]>(p => p.Length == 1), IntPtr.Zero), Times.Once);
            Assert.Single(outcome.Recycled);
            Assert.True(outcome.IsComplete);
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A throwing <see cref="INativeApi"/> must not escape to the UI thread; the files stay put and are
    /// reported as remaining.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_NativeThrows_DoesNotThrow_FilesRemain()
    {
        var native = new Mock<INativeApi>();
        native.Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Throws(new InvalidOperationException("boom"));
        var (service, root) = BuildService(native);
        var source = NewTempSourceFile();
        try
        {
            var managed = service.MoveIn(source)!;

            var outcome = service.ReleaseToRecycleBin([managed]);

            Assert.Equal([managed], outcome.Remaining);
            Assert.True(outcome.Cancelled);
            Assert.True(File.Exists(managed));
            Assert.True(ReadManifest(root).ContainsKey(managed));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// DoD-1 of ADR-035 (source guard): no <c>Release*</c> method of the storage service may delete
    /// item content itself (<c>File.Delete</c>/<c>Directory.Delete</c>) — the Recycle Bin is the only
    /// way out — and the old hard-deleting <c>Release(string)</c> must not come back. The GUID wrapper
    /// cleanup it relies on may only ever delete an EMPTY directory (non-recursive).
    /// </summary>
    [Fact]
    public void SourceGuard_ReleaseMethods_NeverHardDeleteContent()
    {
        var sourcePath = Path.Combine(
            RepoRootLocator.Find(), "src", "DesktopOrganizer.Services", "Tools", "PanelItemStorageService.cs");
        var source = File.ReadAllText(sourcePath).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("public void Release(", source, StringComparison.Ordinal);

        var releaseMethods = System.Text.RegularExpressions.Regex.Matches(
            source, @"\n    (?:public|internal|private)[^\n(]*\bRelease\w*\(");
        Assert.NotEmpty(releaseMethods);

        foreach (System.Text.RegularExpressions.Match match in releaseMethods)
        {
            var body = ExtractMemberBody(source, match.Index);
            Assert.DoesNotContain("File.Delete(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Directory.Delete(", body, StringComparison.Ordinal);
        }

        var cleanupStart = source.IndexOf("    private void TryCleanupEmptyItemDirectory(", StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0);
        var cleanupBody = ExtractMemberBody(source, cleanupStart);
        Assert.DoesNotContain("recursive", cleanupBody, StringComparison.Ordinal);
    }

    // ── ForgetMovedOut (ADR-036, release-hardening-1.0.md §E) ────────────────

    /// <summary>
    /// The common case: an external drag-out (Explorer/the desktop) actually moved the file away — the
    /// caller (<c>ChildWindow</c>'s <c>MovedOutVerifier</c>) already confirmed the path is gone before
    /// calling this, so the manifest entry and the now-empty per-item GUID directory are both cleared.
    /// </summary>
    [Fact]
    public void ForgetMovedOut_PathGone_RemovesManifestEntryAndEmptyGuidDirectory()
    {
        var (service, root) = BuildService();
        var source = NewTempSourceFile();
        try
        {
            var managed = service.MoveIn(source)!;
            var itemDir = Path.GetDirectoryName(managed)!;
            Assert.Single(ReadManifest(root));

            // Simulate the external move: the file is gone from its managed path (moved by the shell),
            // exactly as MovedOutVerifier would have confirmed before calling ForgetMovedOut.
            File.Delete(managed);

            service.ForgetMovedOut(managed);

            Assert.Empty(ReadManifest(root));
            Assert.False(Directory.Exists(itemDir), "The now-empty per-item GUID directory must be cleaned up.");
            Assert.True(Directory.Exists(root), "The storage root itself must never be deleted.");
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A no-op when the path is still there: <see cref="PanelItemStorageService.ForgetMovedOut"/> must
    /// never touch the manifest for an item that hasn't actually left — a copy (Ctrl-drag), a cancelled
    /// drag (Esc), or a drop that landed somewhere that doesn't move the file all leave the source
    /// exactly as it was, and <c>ForgetMovedOut</c> is the ONLY gate that decides that (never deletes a
    /// file itself, either).
    /// </summary>
    [Fact]
    public void ForgetMovedOut_PathStillExists_NeverTouchesManifestOrFile()
    {
        var (service, root) = BuildService();
        var source = NewTempSourceFile("still here");
        try
        {
            var managed = service.MoveIn(source)!;

            service.ForgetMovedOut(managed);

            Assert.True(File.Exists(managed));
            Assert.Equal("still here", File.ReadAllText(managed));
            Assert.Single(ReadManifest(root));

            // Restore must still work — the manifest entry was left completely intact.
            Assert.Equal(RestoreOutcome.Restored, service.Restore(managed));
            Assert.True(File.Exists(source));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A path outside managed storage is never touched — <see cref="PanelItemStorageService.ForgetMovedOut"/>
    /// is a no-op for a referenced-in-place item, same as every other managed-only operation.
    /// </summary>
    [Fact]
    public void ForgetMovedOut_NotManagedPath_NoOp()
    {
        var (service, root) = BuildService();
        var untouchable = Path.Combine(Path.GetTempPath(), $"DOrgUntouchable_{Guid.NewGuid():N}.txt");
        File.WriteAllText(untouchable, "must survive");
        try
        {
            var exception = Record.Exception(() => service.ForgetMovedOut(untouchable));

            Assert.Null(exception);
            Assert.True(File.Exists(untouchable));
        }
        finally
        {
            SafeDeleteFile(untouchable);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// M8 (release-hardening review): mirrors the <see cref="PanelItemStorageService.ReleaseToRecycleBin"/>
    /// guard above — <see cref="PanelItemStorageService.ForgetMovedOut"/> must never act on the storage
    /// root itself or a bare per-item GUID wrapper directory, even though
    /// <see cref="PanelItemStorageService.IsManaged"/> reports both as managed.
    /// </summary>
    [Fact]
    public void ForgetMovedOut_StorageRootOrGuidWrapper_NoOp()
    {
        var (service, root) = BuildService();
        var source = NewTempSourceFile();
        try
        {
            var managed = service.MoveIn(source)!;
            var itemDir = Path.GetDirectoryName(managed)!;

            service.ForgetMovedOut(root);
            service.ForgetMovedOut(itemDir);

            Assert.Single(ReadManifest(root));
            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(itemDir));
            Assert.True(File.Exists(managed));
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    // ── F: manifest in-memory cache + write-through (phase-7 block F) ───────

    /// <summary>
    /// F-DoD-1: the manifest must be read from disk at most once per service instance, regardless
    /// of how many manifest-touching operations run in the same session. Before this block, every
    /// <c>MoveIn</c>/<c>Restore</c>/<c>ReleaseToRecycleBin</c> re-read the whole JSON file from disk; a
    /// multi-file drag&amp;drop paid that cost once per dropped file. Exercises well over the
    /// DoD's "&gt;= 50 operations" bar via a mix of MoveIn (write), Restore, and ReleaseToRecycleBin
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
                    var recycle = service.ReleaseToRecycleBin([managedPaths[i]]);
                    Assert.True(recycle.IsComplete);
                    Assert.False(File.Exists(managedPaths[i]));
                }
            }

            // 30 MoveIn + 30 Restore/ReleaseToRecycleBin = 60 manifest-touching operations, still a single disk read.
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
            var service = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
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
                    : new PanelItemStorageService(root, CreateRecyclingNativeApi().Object).Restore(managedPaths[i]!);
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
            var writer = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
            var managed = writer.MoveIn(source);
            Assert.NotNull(managed);

            var manifestJsonBefore = File.ReadAllText(manifestPath);

            // A second, fresh instance whose very first manifest access hits a locked file —
            // simulates AV/indexer holding an exclusive handle at the wrong moment.
            var reader = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveIn(string, bool)"/> stores each item under
    /// a per-item GUID directory directly beneath the storage root, keeping the original file name
    /// intact within that directory (ADR-013 Decision 2, B1).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveInDirectory(string, bool)"/> stores a moved
    /// folder under a per-item GUID directory, keeping the original folder name intact (ADR-013 Decision 2).
    /// </summary>
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

    /// <summary>
    /// Verifies that two source files sharing the same original file name each get their own per-item
    /// GUID directory, so both keep their original file name without collision.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.Restore(string, bool)"/> deletes the now-empty
    /// per-item GUID directory after restoring, while never deleting the storage root itself (B2).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.ReleaseToRecycleBin"/> deletes the now-empty
    /// per-item GUID directory after recycling, while never deleting the storage root itself (B2).
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_CleansUpTheNowEmptyPerItemGuidDirectory()
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

            service.ReleaseToRecycleBin([managed!]);

            Assert.False(Directory.Exists(itemDir), "The now-empty per-item GUID directory must be cleaned up.");
            Assert.True(Directory.Exists(root), "The storage root itself must never be deleted.");
        }
        finally
        {
            SafeDeleteFile(source);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.Restore(string, bool)"/> never deletes the
    /// per-item GUID directory if a stray, unmanaged file still lives alongside the restored item.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.ReleaseToRecycleBin"/> never deletes the per-item
    /// GUID directory if a stray, unmanaged file still lives alongside the recycled item.
    /// </summary>
    [Fact]
    public void ReleaseToRecycleBin_PerItemGuidDirectoryNotEmpty_IsNotDeleted()
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

            service.ReleaseToRecycleBin([managed!]);

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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.MoveIn(string, bool)"/> truncates an overly
    /// long base file name so the destination path stays within the 240-character guard, while
    /// preserving the file extension (B1).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.RenameManaged"/> moves the managed file to its
    /// new name within the same per-item directory and updates the manifest so a subsequent
    /// <see cref="PanelItemStorageService.Restore(string, bool)"/> places the file back under the new
    /// name in the original directory (ADR-013 Decision 5).
    /// </summary>
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

    /// <summary>
    /// Verifies that renaming a legacy flat-layout managed item (stored directly in the storage root,
    /// predating ADR-013 Decision 2) relocates it into a new per-item GUID directory rather than leaving
    /// it flat.
    /// </summary>
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
            var writer = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
            var managed = writer.MoveIn(source);
            Assert.NotNull(managed);

            var manifestJsonBefore = File.ReadAllText(manifestPath);

            // A second, fresh instance whose very first manifest access hits a locked file.
            var renamer = new PanelItemStorageService(root, CreateRecyclingNativeApi().Object);
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
    /// the (never-created) new path, so <see cref="PanelItemStorageService.Restore(string, bool)"/> against the old (still physically
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.RenameManaged"/> succeeds for a case-only rename
    /// (e.g. "Report.lnk" to "report.lnk").
    /// </summary>
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
    /// raw, untruncated name the caller asked for. Otherwise a later <see cref="PanelItemStorageService.Restore(string, bool)"/> would put
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.RenameManaged"/> returns
    /// <see cref="RenameOutcome.Failed"/> for a path outside the managed storage root, leaving it untouched.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.RenameManaged"/> rejects a requested new name
    /// containing a path traversal segment (<c>..\evil.lnk</c>) as <see cref="RenameOutcome.InvalidName"/>
    /// (defense-in-depth), without touching the file.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.RenameManaged"/> returns
    /// <see cref="RenameOutcome.NotFound"/> when the managed path passed in does not actually exist on disk.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> resolves the original
    /// source path to its live managed copy (ADR-009 Amendment 2026-09-22, P08).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> matches the original
    /// source path case-insensitively.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> returns false for an
    /// original path with no manifest entry.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> returns false when the
    /// manifest entry outlives its managed file (deleted outside this service), rather than handing back
    /// a stale path.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> is read-only and never
    /// mutates the on-disk manifest file, for both a hit and a miss.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> returns false for an
    /// empty or whitespace-only path.
    /// </summary>
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
    /// Verifies that <see cref="PanelItemStorageService.FindManagedCandidates"/> returns exactly the
    /// one live managed copy for an original path with a single manifest entry.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.FindManagedCandidates"/> returns an empty
    /// collection for an original path with no manifest entry.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.FindManagedCandidates"/> returns both live
    /// managed copies when the manifest ambiguously maps one original path to two entries (M1, P08 fix cycle 1).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.TryFindManagedPath"/> refuses to resolve
    /// (returns false) when the manifest ambiguously maps one original path to two live managed copies,
    /// rather than silently guessing the first one by enumeration order (CRITICAL/MAJOR M1).
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelItemStorageService.FindManagedCandidates"/> filters out a ghosted
    /// (deleted-without-Release) manifest entry, returning only the still-live candidate, and that
    /// <see cref="PanelItemStorageService.TryFindManagedPath"/> then resolves unambiguously to it.
    /// </summary>
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

    // ── helpers ──────────────────────────────────────────────────────────────
    private static (PanelItemStorageService Service, string Root) BuildService()
        => BuildService(CreateRecyclingNativeApi());

    private static (PanelItemStorageService Service, string Root) BuildService(Mock<INativeApi> nativeApi)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgStorage_{Guid.NewGuid():N}");
        return (new PanelItemStorageService(root, nativeApi.Object), root);
    }

    /// <summary>
    /// An <see cref="INativeApi"/> double whose <see cref="INativeApi.RecycleItems"/> behaves like a
    /// fully successful shell call: every given path disappears from disk (as if moved to the Recycle
    /// Bin) and <see langword="true"/> is returned. Never touches the real Recycle Bin.
    /// </summary>
    private static Mock<INativeApi> CreateRecyclingNativeApi()
    {
        var mock = new Mock<INativeApi>();
        mock.Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Returns((string[] paths, IntPtr _) =>
            {
                foreach (var path in paths) SimulateRecycle(path);
                return true;
            });
        return mock;
    }

    /// <summary>Makes <paramref name="path"/> vanish from disk, standing in for the shell moving it to the Recycle Bin.</summary>
    private static void SimulateRecycle(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static Dictionary<string, string> ReadManifest(string root)
    {
        var manifestPath = Path.Combine(root, ".manifest.json");
        if (!File.Exists(manifestPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? [];
        return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private static string NewTempSourceFile(string content = "x")
    {
        var source = Path.Combine(Path.GetTempPath(), $"DOrgSrc_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(source, content);
        return source;
    }

    /// <summary>Returns the text from <paramref name="start"/> to the member's closing <c>"\n    }\n"</c>.</summary>
    private static string ExtractMemberBody(string source, int start)
    {
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of the member.");
        return source[start..end];
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

    private static void AssertManifestFileIsEmpty(string root)
    {
        var manifestPath = Path.Combine(root, ".manifest.json");
        if (!File.Exists(manifestPath)) return;

        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath));
        Assert.True(manifest is null || manifest.Count == 0, "Failed move must not leave a dangling manifest entry.");
    }
}
