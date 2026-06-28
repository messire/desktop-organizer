using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal sealed class PanelItemStorageService : IPanelItemStorageService
{
    // Safety cap for cross-volume MoveInDirectory — recursively copying an arbitrarily large folder
    // (someone's entire "Downloads") on a single drag&drop would be slow and double disk usage for no
    // benefit. Past this, the folder is left as a live reference instead (see MoveInDirectory remarks).
    // Same-volume moves are an atomic Directory.Move and are never subject to this cap.
    private const long MaxDirectoryImportBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
    private const int MaxDirectoryImportFileCount = 20_000;

    private const string ManifestFileName = ".manifest.json";

    private readonly string _storageRoot;
    private readonly string _manifestPath;
    private readonly object _manifestLock = new();

    // Authoritative in-memory copy of the manifest, lazily loaded from disk on first access and
    // kept in sync via write-through on every mutation (see the "Manifest" region below). This
    // service is registered as a DI singleton and used exclusively from the UI thread, but all
    // access still goes through _manifestLock as a cheap belt-and-suspenders guard.
    private Dictionary<string, string>? _manifestCache;

    /// <summary>
    /// Number of times the manifest has actually been read from disk. Internal test
    /// instrumentation only — never used by production logic — to prove the in-memory cache
    /// avoids re-reading the file on every operation within a session.
    /// </summary>
    internal int ManifestDiskReadCount { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageService"/> class, using the
    /// real per-user app-data folder as storage root.
    /// </summary>
    public PanelItemStorageService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopOrganizer",
            "PanelItems"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageService"/> class with an
    /// explicit storage root. Internal — used by tests to point at a disposable temp folder
    /// instead of the real per-user app-data location.
    /// </summary>
    /// <param name="storageRoot">The folder moved-in files are stored under.</param>
    internal PanelItemStorageService(string storageRoot)
    {
        _storageRoot = storageRoot;
        _manifestPath = Path.Combine(_storageRoot, ManifestFileName);
    }

    /// <inheritdoc />
    public string? MoveIn(string sourcePath) => MoveIn(sourcePath, forceCrossVolume: false);

    /// <summary>
    /// Same as <see cref="MoveIn(string)"/>, but with an internal-only escape hatch for tests: when
    /// <paramref name="forceCrossVolume"/> is <see langword="true"/>, the same-volume fast path is
    /// skipped even if source and storage root are on the same drive, forcing the
    /// copy-then-delete-after-verified-success branch. Real callers always go through the
    /// single-argument overload; this only exists because cross-volume behavior can't be
    /// deterministically reproduced in CI (which typically has a single volume).
    /// </summary>
    internal string? MoveIn(string sourcePath, bool forceCrossVolume)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;

        // Directories are handled by MoveInDirectory — see IPanelItemStorageService remarks.
        if (Directory.Exists(sourcePath)) return null;
        if (!File.Exists(sourcePath)) return null;

        string? destPath = null;
        try
        {
            Directory.CreateDirectory(_storageRoot);

            var fullSource = Path.GetFullPath(sourcePath);
            destPath = BuildDestinationPath(fullSource);

            // The manifest entry is written before the destructive step (File.Move / File.Delete
            // of the original) rather than after: destPath is already known at this point, and a
            // crash between "the file is moved" and "the manifest records where it came from"
            // would otherwise silently lose the original path — by ADR-009 that turns a later
            // "Remove from panel" into a permanent delete instead of a restore. If the move/copy
            // below fails, the entry is rolled back (see the catch below and the copy-failure
            // branch), so on any non-success outcome the manifest ends up exactly as it would
            // have with the old after-the-fact write. If the manifest write itself fails (or the
            // cache is currently untrusted — see EnsureManifestCacheUnlocked), the destructive
            // step below must never run: abort here and leave the source untouched.
            if (!WriteManifestEntry(destPath, fullSource))
            {
                RemoveManifestEntry(destPath);
                Console.Error.WriteLine($"PanelItemStorageService.MoveIn: manifest write failed, aborting move of '{sourcePath}'");
                return null;
            }

            if (!forceCrossVolume && SameVolume(fullSource, _storageRoot))
            {
                // Atomic rename — nothing is copied, so there's no partial-copy window to guard.
                File.Move(fullSource, destPath);
            }
            else
            {
                if (!TryCopyFileVerified(fullSource, destPath))
                {
                    TryDeletePartialFile(destPath);
                    RemoveManifestEntry(destPath);
                    return null;
                }

                // Copy verified complete — only now is it safe to remove the original.
                File.Delete(fullSource);
            }

            return destPath;
        }
        catch (Exception ex)
        {
            if (destPath is not null) RemoveManifestEntry(destPath);
            Console.Error.WriteLine($"PanelItemStorageService.MoveIn failed for '{sourcePath}': {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public string? MoveInDirectory(string sourcePath) => MoveInDirectory(sourcePath, forceCrossVolume: false);

    /// <summary>
    /// Same as <see cref="MoveInDirectory(string)"/>, but with an internal-only escape hatch for
    /// tests — see <see cref="MoveIn(string, bool)"/> remarks.
    /// </summary>
    internal string? MoveInDirectory(string sourcePath, bool forceCrossVolume)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        if (!Directory.Exists(sourcePath)) return null;

        string? destRoot = null;
        try
        {
            var fullSource = Path.GetFullPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            destRoot = BuildDestinationDirectoryPath(fullSource);

            if (!forceCrossVolume && SameVolume(fullSource, _storageRoot))
            {
                // Atomic rename — no safety cap: nothing is copied, so there's no disk-bloat or
                // slowness concern regardless of folder size.
                Directory.CreateDirectory(_storageRoot);

                // See MoveIn remarks: the manifest entry is written before the destructive move so
                // a crash between "move" and "record origin" can't lose the original path. Rolled
                // back by the catch below if the move itself fails; if the manifest write itself
                // fails, abort before the destructive Directory.Move ever runs.
                if (!WriteManifestEntry(destRoot, fullSource))
                {
                    RemoveManifestEntry(destRoot);
                    Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory: manifest write failed, aborting move of '{sourcePath}'");
                    return null;
                }

                Directory.Move(fullSource, destRoot);
            }
            else
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(fullSource, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory failed to enumerate '{sourcePath}': {ex.Message}");
                    return null;
                }

                if (files.Length > MaxDirectoryImportFileCount) return null;

                long totalBytes = 0;
                try
                {
                    foreach (var file in files)
                    {
                        totalBytes += new FileInfo(file).Length;
                        if (totalBytes > MaxDirectoryImportBytes) return null;
                    }
                }
                catch (Exception ex)
                {
                    // A file vanished/became inaccessible mid-scan — bail out rather than move a partial view.
                    Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory failed to size '{sourcePath}': {ex.Message}");
                    return null;
                }

                // Manifest entry recorded before any copying starts — see MoveIn remarks. Rolled
                // back below on any failure to complete the verified copy. If the manifest write
                // itself fails, abort before any copying (and the eventual source deletion) starts.
                if (!WriteManifestEntry(destRoot, fullSource))
                {
                    RemoveManifestEntry(destRoot);
                    Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory: manifest write failed, aborting move of '{sourcePath}'");
                    return null;
                }

                try
                {
                    Directory.CreateDirectory(destRoot);

                    foreach (var file in files)
                    {
                        var relative = Path.GetRelativePath(fullSource, file);
                        var destFile = Path.Combine(destRoot, relative);

                        // Per-file verify (size check against source) before any deletion is allowed —
                        // same discipline as MoveIn/Restore, see TryCopyFileVerified remarks.
                        if (!TryCopyFileVerified(file, destFile))
                        {
                            Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory verified copy failed for '{file}' while copying '{sourcePath}'");
                            TryDeletePartialDirectory(destRoot);
                            RemoveManifestEntry(destRoot);
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory failed to copy '{sourcePath}': {ex.Message}");
                    TryDeletePartialDirectory(destRoot);
                    RemoveManifestEntry(destRoot);
                    return null;
                }

                // Copy verified complete (every file above either copied or the whole operation
                // bailed out) — only now is it safe to remove the original tree.
                Directory.Delete(fullSource, recursive: true);
            }

            return destRoot;
        }
        catch (Exception ex)
        {
            if (destRoot is not null) RemoveManifestEntry(destRoot);
            Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory failed for '{sourcePath}': {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public RestoreOutcome Restore(string storedPath) => Restore(storedPath, forceCrossVolume: false);

    /// <summary>
    /// Same as <see cref="Restore(string)"/>, but with an internal-only escape hatch for tests: when
    /// <paramref name="forceCrossVolume"/> is <see langword="true"/>, the same-volume fast path
    /// (<see cref="File.Move(string, string)"/>/<see cref="Directory.Move(string, string)"/>) is
    /// skipped entirely and the copy-then-delete-after-verified-success branch runs directly —
    /// see <see cref="MoveIn(string, bool)"/> remarks for why this exists.
    /// </summary>
    internal RestoreOutcome Restore(string storedPath, bool forceCrossVolume)
    {
        if (!IsManaged(storedPath)) return RestoreOutcome.NotManaged;

        var fullStored = Path.GetFullPath(storedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var original = ReadManifestEntry(fullStored);
        if (original is null) return RestoreOutcome.OriginalPathUnknown;

        try
        {
            var targetDir = Path.GetDirectoryName(original);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            var finalTarget = ResolveCollisionFreeName(original);
            var isDirectory = Directory.Exists(fullStored);

            // Cross-volume Move isn't supported by the OS — fall back to the same
            // copy-then-delete-after-verified-success discipline used by MoveInDirectory/MoveIn.
            // Shared by both the real cross-volume path (IOException from Move) and the
            // forceCrossVolume test hook, so the fallback logic itself is never duplicated.
            bool CopyThenDeleteDirectory()
            {
                if (!TryCopyDirectoryVerified(fullStored, finalTarget))
                {
                    TryDeletePartialDirectory(finalTarget);
                    return false;
                }

                Directory.Delete(fullStored, recursive: true);
                return true;
            }

            bool CopyThenDeleteFile()
            {
                if (!TryCopyFileVerified(fullStored, finalTarget))
                {
                    TryDeletePartialFile(finalTarget);
                    return false;
                }

                File.Delete(fullStored);
                return true;
            }

            if (isDirectory)
            {
                if (forceCrossVolume)
                {
                    if (!CopyThenDeleteDirectory()) return RestoreOutcome.Failed;
                }
                else
                {
                    try
                    {
                        Directory.Move(fullStored, finalTarget);
                    }
                    catch (IOException)
                    {
                        if (!CopyThenDeleteDirectory()) return RestoreOutcome.Failed;
                    }
                }
            }
            else
            {
                if (forceCrossVolume)
                {
                    if (!CopyThenDeleteFile()) return RestoreOutcome.Failed;
                }
                else
                {
                    try
                    {
                        File.Move(fullStored, finalTarget);
                    }
                    catch (IOException)
                    {
                        if (!CopyThenDeleteFile()) return RestoreOutcome.Failed;
                    }
                }
            }

            RemoveManifestEntry(fullStored);
            return RestoreOutcome.Restored;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService.Restore failed for '{storedPath}': {ex.Message}");
            return RestoreOutcome.Failed;
        }
    }

    /// <inheritdoc />
    public void Release(string storedPath)
    {
        if (!IsManaged(storedPath)) return;

        try
        {
            if (Directory.Exists(storedPath)) Directory.Delete(storedPath, recursive: true);
            else if (File.Exists(storedPath)) File.Delete(storedPath);

            RemoveManifestEntry(Path.GetFullPath(storedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService.Release failed for '{storedPath}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public bool IsManaged(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_storageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // The manifest file itself is a storage implementation detail, never a panel item.
            if (string.Equals(full, _manifestPath, StringComparison.OrdinalIgnoreCase)) return false;

            // Compare on a path-segment boundary, not a raw string prefix: "...\PanelItems2\foo.txt"
            // or "...\PanelItems_backup" must NOT be treated as managed just because "...\PanelItems"
            // is a string-prefix of them. Either the path IS the root itself, or it starts with
            // root + separator.
            var rootWithSep = root + Path.DirectorySeparatorChar;
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Malformed path (illegal characters, etc.) — definitely not ours.
            return false;
        }
    }

    // ── Destination naming ──────────────────────────────────────────────────

    private string BuildDestinationPath(string fullSourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(fullSourcePath);
        var ext = Path.GetExtension(fullSourcePath);
        if (name.Length > 40) name = name[..40];

        // A GUID suffix guarantees a collision-free destination in one shot — no need for a
        // check-then-create loop (which would itself be racy) or an "(2)"-style counter.
        return Path.Combine(_storageRoot, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private string BuildDestinationDirectoryPath(string fullSourcePath)
    {
        var name = Path.GetFileName(fullSourcePath);
        if (string.IsNullOrEmpty(name)) name = "folder";
        if (name.Length > 40) name = name[..40];

        return Path.Combine(_storageRoot, $"{name}_{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Finds the first free Explorer-style name at <paramref name="desiredPath"/>: the path itself
    /// if nothing occupies it, otherwise <c>name (2).ext</c>, <c>name (3).ext</c>, … for files, or
    /// <c>name (2)</c>, <c>name (3)</c>, … for directories.
    /// </summary>
    private static string ResolveCollisionFreeName(string desiredPath)
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

    // ── Volume detection ────────────────────────────────────────────────────

    private static bool SameVolume(string fullSourcePath, string storageRoot)
    {
        try
        {
            var sourceRoot = Path.GetPathRoot(fullSourcePath);
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(storageRoot));
            return !string.IsNullOrEmpty(sourceRoot)
                && !string.IsNullOrEmpty(targetRoot)
                && string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // If we can't tell, be conservative and treat it as cross-volume so the
            // verify-before-delete path is used.
            return false;
        }
    }

    // ── Copy-then-verify helpers (cross-volume path) ────────────────────────

    /// <summary>
    /// Copies a single file and verifies the destination matches before the caller is allowed to
    /// delete the source. Returns <see langword="false"/> (leaving the source untouched) on any
    /// failure or verification mismatch.
    /// </summary>
    private static bool TryCopyFileVerified(string sourcePath, string destPath)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(sourcePath, destPath, overwrite: false);

            if (!File.Exists(destPath)) return false;

            var sourceLength = new FileInfo(sourcePath).Length;
            var destLength = new FileInfo(destPath).Length;
            return sourceLength == destLength;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService: verified copy failed '{sourcePath}' -> '{destPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Recursively copies a directory tree and verifies every file arrived intact before the
    /// caller is allowed to delete the source. Returns <see langword="false"/> (leaving the source
    /// untouched) on any failure.
    /// </summary>
    private static bool TryCopyDirectoryVerified(string sourceRoot, string destRoot)
    {
        try
        {
            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);

            Directory.CreateDirectory(destRoot);

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
            Console.Error.WriteLine($"PanelItemStorageService: verified directory copy failed '{sourceRoot}' -> '{destRoot}': {ex.Message}");
            return false;
        }
    }

    private static void TryDeletePartialFile(string destPath)
    {
        try
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService: failed to clean up partial copy '{destPath}': {ex.Message}");
        }
    }

    private static void TryDeletePartialDirectory(string destRoot)
    {
        try
        {
            if (Directory.Exists(destRoot)) Directory.Delete(destRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService: failed to clean up partial copy '{destRoot}': {ex.Message}");
        }
    }

    // ── Manifest (managed path -> original path) ────────────────────────────

    /// <summary>
    /// Writes/overwrites a manifest entry. Returns <see langword="false"/> if the manifest cache
    /// is currently untrusted (see <see cref="EnsureManifestCacheUnlocked"/>) or if the
    /// write-through to disk failed — in either case nothing was persisted, and callers that are
    /// about to perform a destructive step (deleting/moving away the original) MUST treat that as
    /// "abort, leave the source untouched" per <see cref="IPanelItemStorageService"/>.
    /// </summary>
    private bool WriteManifestEntry(string managedPath, string originalPath)
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return false;

            manifest[managedPath] = originalPath;
            return WriteManifestUnlocked(manifest);
        }
    }

    private string? ReadManifestEntry(string managedPath)
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return null;
            return manifest.TryGetValue(managedPath, out var original) ? original : null;
        }
    }

    private void RemoveManifestEntry(string managedPath)
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return;
            if (manifest.Remove(managedPath)) WriteManifestUnlocked(manifest);
        }
    }

    /// <summary>
    /// Returns the in-memory manifest cache, populating it from disk on first access, plus a
    /// <c>Trusted</c> flag. Must be called under <see cref="_manifestLock"/>.
    /// <para>
    /// Once a read has succeeded (file absent, or file present and parsed), the result is cached
    /// on <see cref="_manifestCache"/> and is the sole source of truth for the lifetime of this
    /// service instance — the file on disk is only ever written (write-through), never re-read,
    /// avoiding the "re-read the whole file on every operation" cost that a drag&amp;drop of N
    /// files used to pay N times over.
    /// </para>
    /// <para>
    /// If the read throws (e.g. a transient sharing violation from an AV/indexer holding an
    /// exclusive handle right after the atomic <see cref="File.Move(string, string, bool)"/> in
    /// <see cref="WriteManifestUnlocked"/>, or a genuinely corrupt file), the failure is treated
    /// as untrusted: <see cref="_manifestCache"/> is deliberately left unset so the very next
    /// call retries the read from scratch, and an empty, throwaway dictionary is returned with
    /// <c>Trusted = false</c>. Callers MUST NOT let an untrusted snapshot reach disk via
    /// write-through — doing so would permanently poison this singleton's cache with an empty
    /// manifest and, on the next mutation, overwrite the real manifest file on disk with it,
    /// destroying every origin-path mapping that was on it.
    /// </para>
    /// </summary>
    private (Dictionary<string, string> Manifest, bool Trusted) EnsureManifestCacheUnlocked()
    {
        if (_manifestCache is not null) return (_manifestCache, true);

        if (!File.Exists(_manifestPath))
        {
            // Legitimate empty state — there is nothing to fail to read, so this is safe to cache.
            _manifestCache = CreateEmptyManifest();
            ManifestDiskReadCount++;
            return (_manifestCache, true);
        }

        try
        {
            var json = File.ReadAllText(_manifestPath);
            ManifestDiskReadCount++;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            _manifestCache = loaded is null
                ? CreateEmptyManifest()
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            return (_manifestCache, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService: failed to read manifest '{_manifestPath}': {ex.Message}");
            return (CreateEmptyManifest(), false);
        }
    }

    private static Dictionary<string, string> CreateEmptyManifest() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes <paramref name="manifest"/> to disk (write-through). <paramref name="manifest"/> is
    /// always the same instance as <see cref="_manifestCache"/> — callers mutate the cache in
    /// place before calling this, so the cache never drifts from what's on disk on the happy
    /// path. Returns <see langword="false"/> if the write itself failed (disk full, file locked,
    /// no permissions, …) — the in-memory cache may still have been mutated by the caller, but
    /// nothing changed on disk.
    /// </summary>
    private bool WriteManifestUnlocked(Dictionary<string, string> manifest)
    {
        try
        {
            Directory.CreateDirectory(_storageRoot);

            var tempPath = _manifestPath + $".tmp{Guid.NewGuid():N}";
            var json = JsonSerializer.Serialize(manifest);
            File.WriteAllText(tempPath, json);

            // Atomic swap: File.Move with overwrite avoids ever leaving a half-written manifest on disk.
            File.Move(tempPath, _manifestPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService: failed to write manifest '{_manifestPath}': {ex.Message}");
            return false;
        }
    }
}
