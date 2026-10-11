using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="IPanelItemsUninstallRestorer" />
internal sealed class PanelItemsUninstallRestorer : IPanelItemsUninstallRestorer
{
    private readonly string _storageRoot;
    private readonly string _manifestPath;
    private readonly string _desktopDirectory;
    private readonly IAppLog? _appLog;
    private readonly Func<string, string[]> _enumerateEntries;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemsUninstallRestorer"/> class for production
    /// use, against the real per-user <c>PanelItems</c> storage folder and the real desktop directory.
    /// </summary>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public PanelItemsUninstallRestorer(IAppLog? appLog = null)
        : this(
            Storage.AppStorageRoot.PanelItemsDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            appLog)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemsUninstallRestorer"/> class with an
    /// explicit storage root and desktop directory. Internal — used by tests to point at disposable
    /// temp folders instead of the real per-user app-data location and the real desktop, mirroring
    /// <see cref="PanelItemStorageService"/>'s own test constructor.
    /// </summary>
    /// <param name="storageRoot">The <c>PanelItems</c> folder to restore from.</param>
    /// <param name="desktopDirectory">The folder items with no usable original location are moved to.</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    /// <param name="enumerateEntries">
    /// Test seam: lists the entries of a directory (defaults to <see cref="Directory.GetFileSystemEntries(string)"/>).
    /// Lets tests simulate an unreadable directory deterministically, without relying on ACLs.
    /// </param>
    internal PanelItemsUninstallRestorer(
        string storageRoot,
        string desktopDirectory,
        IAppLog? appLog = null,
        Func<string, string[]>? enumerateEntries = null)
    {
        ArgumentNullException.ThrowIfNull(storageRoot);
        ArgumentNullException.ThrowIfNull(desktopDirectory);

        _storageRoot = storageRoot;
        _manifestPath = Path.Combine(_storageRoot, PanelItemStorageService.ManifestFileName);
        _desktopDirectory = desktopDirectory;
        _appLog = appLog;
        _enumerateEntries = enumerateEntries ?? Directory.GetFileSystemEntries;
    }

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log — mirrors <see cref="PanelItemStorageService.Log"/>.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public PanelItemsRestoreResult RestoreAll() => RestoreAll(forceCrossVolume: false);

    /// <summary>
    /// Test-only overload (release-polish 1.0 code review, MINOR 2): mirrors
    /// <see cref="PanelItemStorageService"/>'s own <c>forceCrossVolume</c> test hook, forcing every item
    /// onto the copy-verify-delete cross-volume path even when the source and target are on the same
    /// volume, so the "read-only source survives the copy-verified delete" handling can be exercised
    /// deterministically without a test environment that has two real volumes.
    /// </summary>
    /// <param name="forceCrossVolume">When <see langword="true"/>, forces the copy-then-delete path even if an item's source and target are on the same volume.</param>
    internal PanelItemsRestoreResult RestoreAll(bool forceCrossVolume)
    {
        if (!Directory.Exists(_storageRoot))
            return new PanelItemsRestoreResult(0, 0, "No managed PanelItems storage folder found — nothing to restore.");

        var manifestByPath = BuildManifestLookup(ReadManifestTolerant());
        var (items, enumerationFailures) = EnumerateManagedItems();

        var restored = 0;

        // ADR-041 §RestoreAll p.4: a directory whose content could not even be listed holds items that were
        // never seen, let alone restored — each such directory counts as one failed item, so the storage
        // root (and the unread user files in it) is kept and the run reports a partial failure.
        var failed = enumerationFailures;

        foreach (var (itemPath, isDirectory) in items)
        {
            if (!PathExists(itemPath, isDirectory)) continue; // vanished between enumeration and processing

            manifestByPath.TryGetValue(itemPath, out var originalPath);
            var target = ResolveTarget(itemPath, originalPath);

            if (TryMoveItem(itemPath, target, isDirectory, forceCrossVolume))
            {
                restored++;
            }
            else
            {
                failed++;
            }
        }

        CleanupEmptyGuidDirectories();

        if (failed == 0)
        {
            TryDeleteStorageRoot();
            var message = restored == 0
                ? "No managed items found — nothing to restore."
                : $"Restored {restored} item(s) to their original location or the desktop; removed the managed storage folder.";
            return new PanelItemsRestoreResult(restored, 0, message);
        }

        return new PanelItemsRestoreResult(
            restored,
            failed,
            $"Restored {restored} item(s); {failed} item(s) or location(s) could not be restored and remain under '{_storageRoot}' — see the diagnostics log.");
    }

    private static bool PathExists(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    /// <summary>Re-keys the raw manifest dictionary by full, normalized managed path for exact-match lookup.</summary>
    private static Dictionary<string, string> BuildManifestLookup(Dictionary<string, string> manifest)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (managedPath, originalPath) in manifest)
        {
            try
            {
                var full = Path.GetFullPath(managedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                lookup[full] = originalPath;
            }
            catch (Exception)
            {
                // Malformed manifest key — the corresponding item (if still on disk) is simply
                // treated as an orphan below.
            }
        }

        return lookup;
    }

    /// <summary>
    /// Decides where a single managed item ends up: its recorded original location if that directory
    /// still exists, is on a fixed local drive, and nothing already occupies the exact target path;
    /// otherwise the desktop, under a collision-free name. <paramref name="originalPath"/> is
    /// <see langword="null"/> for an orphan (present on disk, absent from the manifest), which always
    /// resolves to the desktop since there is no recorded original location to try.
    /// </summary>
    private string ResolveTarget(string itemPath, string? originalPath)
    {
        if (originalPath is not null)
        {
            try
            {
                var originalFull = Path.GetFullPath(originalPath);
                var originalDir = Path.GetDirectoryName(originalFull);

                if (!string.IsNullOrEmpty(originalDir)
                    && Directory.Exists(originalDir)
                    && ManagedStorageFileOps.IsFixedLocalDrive(originalFull)
                    && !File.Exists(originalFull)
                    && !Directory.Exists(originalFull))
                {
                    return originalFull;
                }
            }
            catch (Exception)
            {
                // Malformed recorded original path — fall through to the desktop.
            }
        }

        var desktopCandidate = Path.Combine(_desktopDirectory, Path.GetFileName(itemPath));
        return ManagedStorageFileOps.ResolveCollisionFreeName(desktopCandidate);
    }

    private bool TryMoveItem(string sourcePath, string targetPath, bool isDirectory, bool forceCrossVolume = false)
    {
        try
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            if (!forceCrossVolume && ManagedStorageFileOps.SameVolume(sourcePath, targetPath))
            {
                if (isDirectory) Directory.Move(sourcePath, targetPath);
                else File.Move(sourcePath, targetPath);
                return true;
            }

            // Cross-volume: copy first, verify, and only then delete the source — never the other way
            // around. Same discipline as PanelItemStorageService.MoveIn/Restore.
            if (isDirectory)
            {
                if (!ManagedStorageFileOps.TryCopyDirectoryVerified(sourcePath, targetPath))
                {
                    ManagedStorageFileOps.TryDeletePartialDirectory(targetPath);
                    return false;
                }

                return DeleteVerifiedSourceOrWarn(sourcePath, targetPath, isDirectory: true);
            }

            if (!ManagedStorageFileOps.TryCopyFileVerified(sourcePath, targetPath))
            {
                ManagedStorageFileOps.TryDeletePartialFile(targetPath);
                return false;
            }

            return DeleteVerifiedSourceOrWarn(sourcePath, targetPath, isDirectory: false);
        }
        catch (Exception ex)
        {
            Log.Error(
                "PanelItemsUninstallRestorer",
                "MoveItemFailed",
                ex,
                new KeyValuePair<string, object?>("source", sourcePath),
                new KeyValuePair<string, object?>("target", targetPath));
            return false;
        }
    }

    /// <summary>
    /// MINOR 2 (release-polish 1.0 code review): deletes a cross-volume source item whose copy at
    /// <paramref name="targetPath"/> has already been verified byte-for-byte. Clears any read-only
    /// attribute first (recursively, for a directory) — a read-only source used to make
    /// <see cref="Directory.Delete(string, bool)"/>/<see cref="File.Delete(string)"/> throw here, which
    /// this method's caller's own <c>catch</c> then counted as a failed item even though the copy had
    /// already succeeded, leaving the item duplicated at both locations (a re-run would then rename the
    /// desktop copy "(2)" instead of reusing it). If the delete still fails after clearing read-only
    /// (e.g. the source is open elsewhere), the copy is nonetheless complete and correct: this logs a
    /// warning and returns <see langword="true"/> (restored) rather than <see langword="false"/>
    /// (failed) — the source item is left in place under managed storage, but is never counted twice.
    /// </summary>
    /// <param name="sourcePath">The already-copied managed-storage source.</param>
    /// <param name="targetPath">The verified copy destination.</param>
    /// <param name="isDirectory">Whether <paramref name="sourcePath"/> is a directory.</param>
    private bool DeleteVerifiedSourceOrWarn(string sourcePath, string targetPath, bool isDirectory)
    {
        try
        {
            ManagedStorageFileOps.ClearReadOnlyRecursive(sourcePath, isDirectory);

            if (isDirectory) Directory.Delete(sourcePath, recursive: true);
            else File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warn(
                "PanelItemsUninstallRestorer",
                "VerifiedCopySourceDeleteFailed",
                new KeyValuePair<string, object?>("source", sourcePath),
                new KeyValuePair<string, object?>("target", targetPath),
                new KeyValuePair<string, object?>("exception", ex.Message));
        }

        return true;
    }

    /// <summary>
    /// Enumerates every leaf item under <see cref="_storageRoot"/>: a legacy flat-layout file directly
    /// under the root, or an item (file or directory) directly inside a per-item GUID directory (see
    /// <see cref="PanelItemStorageService.BuildDestinationPath"/>/<see cref="PanelItemStorageService.BuildDestinationDirectoryPath"/>).
    /// Never throws — an enumeration failure is logged and does not abort the whole run, but it is
    /// reported back as an unreadable location (<c>Failures</c>) so the caller treats it as an
    /// unrestored item and never deletes the storage root over content it could not read.
    /// </summary>
    private (List<(string Path, bool IsDirectory)> Items, int Failures) EnumerateManagedItems()
    {
        var result = new List<(string, bool)>();
        var failures = 0;

        string[] topEntries;
        try
        {
            topEntries = _enumerateEntries(_storageRoot);
        }
        catch (Exception ex)
        {
            Log.Error("PanelItemsUninstallRestorer", "EnumerateStorageRootFailed", ex, new KeyValuePair<string, object?>("path", _storageRoot));
            return (result, 1);
        }

        foreach (var entry in topEntries)
        {
            string full;
            try
            {
                full = Path.GetFullPath(entry).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                Log.Error("PanelItemsUninstallRestorer", "EnumerateEntryPathInvalid", ex, new KeyValuePair<string, object?>("path", entry));
                failures++;
                continue;
            }

            if (string.Equals(full, _manifestPath, StringComparison.OrdinalIgnoreCase)) continue;

            if (Directory.Exists(full))
            {
                string[] children;
                try
                {
                    children = _enumerateEntries(full);
                }
                catch (Exception ex)
                {
                    Log.Error("PanelItemsUninstallRestorer", "EnumerateItemDirectoryFailed", ex, new KeyValuePair<string, object?>("path", full));
                    failures++;
                    continue;
                }

                foreach (var child in children)
                {
                    string childFull;
                    try
                    {
                        childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("PanelItemsUninstallRestorer", "EnumerateEntryPathInvalid", ex, new KeyValuePair<string, object?>("path", child));
                        failures++;
                        continue;
                    }

                    result.Add((childFull, Directory.Exists(childFull)));
                }
            }
            else if (Path.GetFileName(full).StartsWith(PanelItemStorageService.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                // MINOR 1 (release-polish 1.0 code review): a crashed atomic manifest write leaves
                // ".manifest.json.tmp<guid>" siblings next to the real manifest (see
                // PanelItemStorageService's atomic-write temp suffix). These are never panel items —
                // treating them as legacy flat-layout files would move them to the desktop as visible,
                // meaningless clutter. Delete them here instead, best-effort, same as every other
                // cleanup in this class.
                TryDeleteManifestTempLeftover(full);
            }
            else
            {
                // Legacy flat-layout file directly under the storage root (pre-ADR-013 installs).
                result.Add((full, false));
            }
        }

        return (result, failures);
    }

    /// <summary>
    /// Deletes a stray <c>.manifest.json.tmp&lt;guid&gt;</c> leftover found directly under
    /// <see cref="_storageRoot"/> (see <see cref="EnumerateManagedItems"/>). Best-effort: a failure here
    /// does not change <see cref="RestoreAll()"/>'s reported restored/failed counts — the file was never a
    /// panel item to begin with, so leaving it behind is not a restore failure.
    /// </summary>
    private void TryDeleteManifestTempLeftover(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Error("PanelItemsUninstallRestorer", "ManifestTempLeftoverDeleteFailed", ex, new KeyValuePair<string, object?>("path", path));
        }
    }

    /// <summary>
    /// Deletes every now-empty per-item GUID directory directly under <see cref="_storageRoot"/> —
    /// best-effort, mirroring <see cref="PanelItemStorageService"/>'s own empty-directory cleanup.
    /// </summary>
    private void CleanupEmptyGuidDirectories()
    {
        string[] topDirectories;
        try
        {
            topDirectories = Directory.GetDirectories(_storageRoot);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var dir in topDirectories)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                Log.Error("PanelItemsUninstallRestorer", "GuidDirectoryCleanupFailed", ex, new KeyValuePair<string, object?>("path", dir));
            }
        }
    }

    /// <summary>
    /// Removes the entire <see cref="_storageRoot"/> folder (including the manifest) — only ever
    /// called once every item has been confirmed moved out (<c>failed == 0</c>). Best-effort: a failure
    /// here does not change <see cref="RestoreAll()"/>'s reported outcome, since every item was already
    /// successfully restored — this only loses the tidy-up of an already-empty folder.
    /// </summary>
    private void TryDeleteStorageRoot()
    {
        try
        {
            if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Error("PanelItemsUninstallRestorer", "StorageRootCleanupFailed", ex, new KeyValuePair<string, object?>("path", _storageRoot));
        }
    }

    /// <summary>
    /// Reads the manifest, tolerating a missing or corrupt file — a read failure is treated as an
    /// empty manifest, so every item on disk is simply treated as an orphan (desktop fallback) rather
    /// than aborting the whole restore.
    /// </summary>
    private Dictionary<string, string> ReadManifestTolerant()
    {
        if (!File.Exists(_manifestPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_manifestPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return loaded is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("PanelItemsUninstallRestorer", "ManifestReadFailed", ex, new KeyValuePair<string, object?>("path", _manifestPath));
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
