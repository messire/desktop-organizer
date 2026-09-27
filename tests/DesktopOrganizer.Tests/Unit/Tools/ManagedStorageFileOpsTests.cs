using System.IO;
using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="ManagedStorageFileOps"/> — the shared copy-verify-delete primitives behind
/// <see cref="PanelItemStorageService"/>'s cross-volume fallback and
/// <see cref="PanelItemsUninstallRestorer"/>'s uninstall-time restore.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class ManagedStorageFileOpsTests
{
    /// <summary>
    /// MINOR 2 (release-polish 1.0 code review, pre-existing gap): a source subdirectory that contains
    /// no files of its own — only further empty subdirectories — must still exist at the destination
    /// after a verified directory copy. Before the fix, only directories reached while copying an actual
    /// file were ever created at the destination, so an empty subtree silently vanished from the copy.
    /// </summary>
    [Fact]
    public void TryCopyDirectoryVerified_RecreatesEmptySubdirectories()
    {
        var source = Path.Combine(Path.GetTempPath(), $"DOrgOpsSource_{Guid.NewGuid():N}");
        var dest = Path.Combine(Path.GetTempPath(), $"DOrgOpsDest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "WithFile"));
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        Directory.CreateDirectory(Path.Combine(source, "Empty", "NestedEmpty"));
        File.WriteAllText(Path.Combine(source, "WithFile", "inner.txt"), "content");

        try
        {
            var result = ManagedStorageFileOps.TryCopyDirectoryVerified(source, dest);

            Assert.True(result);
            Assert.True(Directory.Exists(Path.Combine(dest, "WithFile")));
            Assert.Equal("content", File.ReadAllText(Path.Combine(dest, "WithFile", "inner.txt")));
            Assert.True(Directory.Exists(Path.Combine(dest, "Empty")), "An empty source subdirectory must still be recreated at the destination.");
            Assert.True(Directory.Exists(Path.Combine(dest, "Empty", "NestedEmpty")), "A nested empty subdirectory must also be recreated.");
        }
        finally
        {
            SafeDeleteDirectory(source);
            SafeDeleteDirectory(dest);
        }
    }

    /// <summary>
    /// MINOR 2: <see cref="ManagedStorageFileOps.ClearReadOnlyRecursive"/> clears the read-only attribute
    /// on a plain file.
    /// </summary>
    [Fact]
    public void ClearReadOnlyRecursive_File_ClearsReadOnlyAttribute()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgOpsFile_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "content");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        try
        {
            ManagedStorageFileOps.ClearReadOnlyRecursive(path, isDirectory: false);

            Assert.False((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            SafeDeleteFile(path);
        }
    }

    /// <summary>
    /// MINOR 2: <see cref="ManagedStorageFileOps.ClearReadOnlyRecursive"/> clears the read-only attribute
    /// on every file nested under a directory, not just the top-level entry.
    /// </summary>
    [Fact]
    public void ClearReadOnlyRecursive_Directory_ClearsAttributeOnNestedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgOpsDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Nested"));
        var nestedFile = Path.Combine(root, "Nested", "inner.txt");
        File.WriteAllText(nestedFile, "content");
        File.SetAttributes(nestedFile, File.GetAttributes(nestedFile) | FileAttributes.ReadOnly);

        try
        {
            ManagedStorageFileOps.ClearReadOnlyRecursive(root, isDirectory: true);

            Assert.False((File.GetAttributes(nestedFile) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            File.SetAttributes(nestedFile, File.GetAttributes(nestedFile) & ~FileAttributes.ReadOnly);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// MINOR 6 (release-polish 1.0 final review): a partially-copied directory tree can itself contain a
    /// read-only file copied verbatim from the source — the best-effort cleanup must clear it first rather
    /// than silently failing to remove the partial copy (leaving stray, undeletable debris under managed
    /// storage).
    /// </summary>
    [Fact]
    public void TryDeletePartialDirectory_ClearsReadOnlyBeforeDeleting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgOpsPartial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var readOnlyFile = Path.Combine(root, "readonly.txt");
        File.WriteAllText(readOnlyFile, "content");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);

        try
        {
            ManagedStorageFileOps.TryDeletePartialDirectory(root);

            Assert.False(Directory.Exists(root), "a read-only file inside a partial copy must not block cleanup.");
        }
        finally
        {
            if (File.Exists(readOnlyFile)) File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) & ~FileAttributes.ReadOnly);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// MINOR 4 (release-polish 1.0 final review): a path on the machine's local system drive (the temp
    /// directory every test in this project already uses) is reported as a fixed local drive.
    /// </summary>
    [Fact]
    public void IsFixedLocalDrive_PathOnLocalTempDrive_ReturnsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgOpsFixed_{Guid.NewGuid():N}.txt");

        Assert.True(ManagedStorageFileOps.IsFixedLocalDrive(path));
    }

    /// <summary>
    /// MINOR 4: a path <see cref="Path.GetPathRoot(string)"/> cannot resolve a drive root for is
    /// conservatively reported as NOT a fixed local drive — every caller's fallback for that answer is
    /// always safe (the desktop, or recycling from managed storage), never a data-loss risk.
    /// </summary>
    [Fact]
    public void IsFixedLocalDrive_PathWithNoResolvableRoot_ReturnsFalse()
    {
        Assert.False(ManagedStorageFileOps.IsFixedLocalDrive(string.Empty));
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
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
}
