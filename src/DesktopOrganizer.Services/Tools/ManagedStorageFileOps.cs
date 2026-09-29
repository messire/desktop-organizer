using System.IO;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Static file-system primitives shared by every command that moves items into/out of the
/// app-managed <c>PanelItems</c> storage folder: <see cref="PanelItemStorageService"/> (the live
/// panel drag&amp;drop path) and <see cref="PanelItemsUninstallRestorer"/> (the one-shot
/// <c>--restore-panel-items</c> uninstall path). Extracted so both share the exact same
/// verify-before-delete discipline instead of two copies drifting apart over time.
/// </summary>
internal static class ManagedStorageFileOps
{
    /// <summary>
    /// Finds the first free Explorer-style name at <paramref name="desiredPath"/>: the path itself
    /// if nothing occupies it, otherwise <c>name (2).ext</c>, <c>name (3).ext</c>, … for files, or
    /// <c>name (2)</c>, <c>name (3)</c>, … for directories.
    /// </summary>
    /// <param name="desiredPath">The first choice destination path.</param>
    public static string ResolveCollisionFreeName(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath)) return desiredPath;

        var dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var nameNoExt = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>True if <paramref name="pathA"/> and <paramref name="pathB"/> resolve to the same drive/volume root.</summary>
    /// <param name="pathA">First path.</param>
    /// <param name="pathB">Second path.</param>
    public static bool SameVolume(string pathA, string pathB)
    {
        try
        {
            var rootA = Path.GetPathRoot(pathA);
            var rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
            return !string.IsNullOrEmpty(rootA)
                && !string.IsNullOrEmpty(rootB)
                && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // If we can't tell, be conservative and treat it as cross-volume so the
            // verify-before-delete path is used.
            return false;
        }
    }

    /// <summary>
    /// Copies a single file and verifies the destination matches before the caller is allowed to
    /// delete the source. Returns <see langword="false"/> (leaving the source untouched) on any
    /// failure or verification mismatch. Verification compares actual content, not just file length
    /// — a length-only check would let a silently truncated-then-padded or bit-flipped copy through,
    /// after which the (only) source copy would be deleted.
    /// </summary>
    /// <param name="sourcePath">The file to copy.</param>
    /// <param name="destPath">The destination path.</param>
    public static bool TryCopyFileVerified(string sourcePath, string destPath)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(sourcePath, destPath, overwrite: false);

            if (!File.Exists(destPath)) return false;

            // Cheap fast-fail before paying for a full-content comparison of both files.
            if (new FileInfo(sourcePath).Length != new FileInfo(destPath).Length) return false;

            return FilesHaveIdenticalContent(sourcePath, destPath);
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                "ManagedStorageFileOps",
                "VerifiedCopyFailed",
                ex,
                new KeyValuePair<string, object?>("source", sourcePath),
                new KeyValuePair<string, object?>("dest", destPath));
            return false;
        }
    }

    /// <summary>
    /// Recursively copies a directory tree and verifies every file arrived intact before the
    /// caller is allowed to delete the source. Returns <see langword="false"/> (leaving the source
    /// untouched) on any failure.
    /// </summary>
    /// <param name="sourceRoot">The directory tree to copy.</param>
    /// <param name="destRoot">The destination root.</param>
    public static bool TryCopyDirectoryVerified(string sourceRoot, string destRoot)
    {
        try
        {
            Directory.CreateDirectory(destRoot);

            // MINOR 2 (release-polish 1.0 code review, pre-existing gap): recreate every subdirectory —
            // including ones with no files directly in them — before copying files below. Copying files
            // alone only ever creates a destination directory that happens to contain at least one file
            // (via TryCopyFileVerified's own Directory.CreateDirectory(destDir)); a source subdirectory
            // that is empty, or contains only further empty subdirectories, would otherwise silently
            // vanish from the copy.
            foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativeDirectory = Path.GetRelativePath(sourceRoot, directory);
                Directory.CreateDirectory(Path.Combine(destRoot, relativeDirectory));
            }

            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var destFile = Path.Combine(destRoot, relative);
                if (!TryCopyFileVerified(file, destFile)) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                "ManagedStorageFileOps",
                "VerifiedDirectoryCopyFailed",
                ex,
                new KeyValuePair<string, object?>("source", sourceRoot),
                new KeyValuePair<string, object?>("dest", destRoot));
            return false;
        }
    }

    /// <summary>Best-effort cleanup of a partially-written destination file after a failed verified copy.</summary>
    /// <param name="destPath">The partial file to remove, if it exists.</param>
    public static void TryDeletePartialFile(string destPath)
    {
        try
        {
            if (File.Exists(destPath))
            {
                // H-2 fix (code review, panel clone to page): File.Copy carries the ReadOnly attribute over
                // from the source (e.g. a read-only document) — clear it first so this best-effort cleanup
                // doesn't itself fail on the very attribute it copied verbatim. Same guard
                // TryDeletePartialDirectory already applies for the directory case.
                ClearReadOnlyRecursive(destPath, isDirectory: false);
                File.Delete(destPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                "ManagedStorageFileOps",
                "PartialCopyCleanupFailed",
                ex,
                new KeyValuePair<string, object?>("path", destPath));
        }
    }

    /// <summary>Best-effort cleanup of a partially-written destination directory tree after a failed verified copy.</summary>
    /// <param name="destRoot">The partial directory to remove, if it exists.</param>
    public static void TryDeletePartialDirectory(string destRoot)
    {
        try
        {
            if (Directory.Exists(destRoot))
            {
                // MINOR 6 (release-polish 1.0 final review): a partially-copied tree can itself contain a
                // read-only file/folder copied verbatim from the source (e.g. a read-only document) — clear
                // it first so this best-effort cleanup doesn't itself fail on the very thing it's trying to
                // remove.
                ClearReadOnlyRecursive(destRoot, isDirectory: true);
                Directory.Delete(destRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                "ManagedStorageFileOps",
                "PartialCopyCleanupFailed",
                ex,
                new KeyValuePair<string, object?>("path", destRoot));
        }
    }

    /// <summary>
    /// MINOR 4 (release-polish 1.0 final review): true if <paramref name="fullPath"/>'s drive is a fixed
    /// local one (<see cref="DriveType.Fixed"/>) — false for removable media (a USB stick), a network share,
    /// a CD/DVD drive, RAM disk, or anything <see cref="DriveInfo"/> can't resolve (a malformed root, or a
    /// UNC-style path it can't parse). Shared by <see cref="PanelItemsUninstallRestorer"/> (an item's
    /// recorded original location must be a fixed local drive to be restored there instead of falling back
    /// to the desktop) and <see cref="PanelItemStorageService.RecycleManagedItem"/> (restoring-then-recycling
    /// onto a removable/network original would recycle into a Recycle Bin that typically doesn't exist
    /// there, permanently deleting the file instead).
    /// </summary>
    /// <param name="fullPath">The path whose drive is checked.</param>
    public static bool IsFixedLocalDrive(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception)
        {
            // Conservatively treated as "not a fixed local drive" — every caller's fallback for this case
            // is always safe (the desktop, or recycling from managed storage).
            return false;
        }
    }

    /// <summary>
    /// MINOR 2 (release-polish 1.0 code review): clears <see cref="FileAttributes.ReadOnly"/> on
    /// <paramref name="path"/> — and, for a directory, every file and subdirectory beneath it — so a
    /// subsequent delete of a source whose copy has already been verified never fails purely because the
    /// source (or something under it) is read-only. Best-effort throughout: any failure clearing an
    /// individual attribute is swallowed here — the caller's own delete attempt (and its own error
    /// handling) is what ultimately decides success or failure; this only removes one specific obstacle.
    /// </summary>
    /// <param name="path">The file or directory to clear.</param>
    /// <param name="isDirectory">Whether <paramref name="path"/> is a directory.</param>
    public static void ClearReadOnlyRecursive(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    ClearReadOnlyAttribute(entry);
                }
            }
            catch (Exception)
            {
                // Best-effort enumeration — an inaccessible subtree just means fewer attributes get
                // cleared; the caller's own delete attempt still runs and reports its own outcome.
            }
        }

        ClearReadOnlyAttribute(path);
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception)
        {
            // Best-effort — see ClearReadOnlyRecursive remarks.
        }
    }

    /// <summary>
    /// Byte-for-byte comparison, not a hash: bails on the first differing byte instead of always
    /// reading both files to completion. This is a corruption check on a same-machine copy, not an
    /// adversarial integrity check, so a cryptographic hash buys nothing here.
    /// </summary>
    private static bool FilesHaveIdenticalContent(string sourcePath, string destPath)
    {
        const int BufferSize = 81920; // matches Stream.CopyTo's own default buffer size

        using var sourceStream = File.OpenRead(sourcePath);
        using var destStream = File.OpenRead(destPath);

        var sourceBuffer = new byte[BufferSize];
        var destBuffer = new byte[BufferSize];

        while (true)
        {
            // MINOR 5 (release-polish 1.0 final review): Stream.Read is permitted to return fewer bytes
            // than requested even when more remain (a network share, a slow/interrupted read) — a plain
            // single Read call here could misalign the two streams' buffers and either report a false
            // mismatch (comparing byte N of one against byte N-k of the other) or, worse, a false match.
            // ReadAtLeast keeps reading until BufferSize bytes are collected or the stream is exhausted.
            var sourceRead = sourceStream.ReadAtLeast(sourceBuffer, BufferSize, throwOnEndOfStream: false);
            var destRead = destStream.ReadAtLeast(destBuffer, BufferSize, throwOnEndOfStream: false);

            if (sourceRead != destRead) return false;
            if (sourceRead == 0) return true; // both streams exhausted at the same length

            if (!sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(destBuffer.AsSpan(0, destRead)))
                return false;
        }
    }
}
