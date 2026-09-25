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

    // Soft cap on the full destination path length. Windows' legacy MAX_PATH (260) is the hard
    // constraint; 240 leaves headroom for Restore's own path composition (parent directory,
    // Explorer-style " (2)" collision suffix) without itself running the guid-directory scheme's
    // hard limits. See ADR-013 Decision 2, trade-off.
    private const int MaxDestinationPathLength = 240;

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
    /// Gets the folder moved-in files are stored under. Internal test instrumentation — lets
    /// <c>AppStorageRootTests</c> pin the default constructor to <see cref="Storage.AppStorageRoot"/>.
    /// </summary>
    internal string StorageRoot => _storageRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageService"/> class, using the
    /// real per-user app-data folder as storage root (<see cref="Storage.AppStorageRoot.Default"/> —
    /// Debug and Release builds resolve to different folders, see <see cref="Storage.AppStorageRoot"/>).
    /// </summary>
    public PanelItemStorageService()
        : this(Path.Combine(Storage.AppStorageRoot.Default, "PanelItems"))
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

    /// <inheritdoc />
    public string? MoveInDirectory(string sourcePath) => MoveInDirectory(sourcePath, forceCrossVolume: false);

    /// <inheritdoc />
    public RestoreOutcome Restore(string storedPath) => Restore(storedPath, forceCrossVolume: false);

    /// <inheritdoc />
    public void Release(string storedPath)
    {
        if (!IsManaged(storedPath)) return;

        try
        {
            var fullStored = Path.GetFullPath(storedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (Directory.Exists(fullStored)) Directory.Delete(fullStored, recursive: true);
            else if (File.Exists(fullStored)) File.Delete(fullStored);

            RemoveManifestEntry(fullStored);
            TryCleanupEmptyItemDirectory(fullStored);
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

    /// <inheritdoc />
    public RenameResult RenameManaged(string storedPath, string newName)
    {
        if (!IsManaged(storedPath)) return new RenameResult(RenameOutcome.Failed, null);
        if (string.IsNullOrWhiteSpace(newName)) return new RenameResult(RenameOutcome.InvalidName, null);

        // Defense in depth: IFileItemRenameService already validates newName before ever calling
        // this method, but this is a public interface member on its own — a caller that skips that
        // validation must not be able to smuggle a path separator (or "..") into what's meant to be
        // a bare name and escape the item's own directory. GetInvalidFileNameChars() already
        // includes both separators.
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new RenameResult(RenameOutcome.InvalidName, null);

        string fullStored;
        try
        {
            fullStored = Path.GetFullPath(storedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return new RenameResult(RenameOutcome.Failed, null);
        }

        var isDirectory = Directory.Exists(fullStored);
        var isFile = !isDirectory && File.Exists(fullStored);
        if (!isDirectory && !isFile) return new RenameResult(RenameOutcome.NotFound, null);

        try
        {
            var itemDir = Path.GetDirectoryName(fullStored);
            if (string.IsNullOrEmpty(itemDir)) return new RenameResult(RenameOutcome.Failed, null);

            var root = Path.GetFullPath(_storageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // A legacy flat-layout item (moved in before per-item GUID directories existed — see
            // BuildDestinationPath/BuildDestinationDirectoryPath remarks) has the storage root
            // itself as its "directory". There's no per-item directory to rename within, so instead
            // it's relocated into a brand-new one — curing the legacy layout for good, per ADR-013
            // Decision 3.
            var isLegacyFlat = string.Equals(itemDir, root, StringComparison.OrdinalIgnoreCase);

            var nameBase = isFile ? Path.GetFileNameWithoutExtension(newName) : newName;
            var nameExt = isFile ? Path.GetExtension(newName) : string.Empty;

            string targetDir;
            if (isLegacyFlat)
            {
                targetDir = Path.Combine(_storageRoot, Guid.NewGuid().ToString("N"));
            }
            else
            {
                targetDir = itemDir;
            }

            var candidate = Path.Combine(targetDir, newName);
            var newPath = TruncateNameIfPathTooLong(candidate, targetDir, nameBase, nameExt);

            // Exact same path (same case, same directory) — nothing to do.
            if (!isLegacyFlat && string.Equals(newPath, fullStored, StringComparison.Ordinal))
                return new RenameResult(RenameOutcome.Renamed, fullStored);

            // A case-only rename of the same path (e.g. "Report.pdf" -> "report.pdf") must not be
            // rejected as a collision against itself — File/Directory.Exists is case-insensitive on
            // Windows and would otherwise always find "the file" at its own new (differently-cased)
            // path.
            var sameIgnoringCase = !isLegacyFlat
                && string.Equals(newPath, fullStored, StringComparison.OrdinalIgnoreCase);

            if (!sameIgnoringCase && (File.Exists(newPath) || Directory.Exists(newPath)))
                return new RenameResult(RenameOutcome.NameTaken, null); // collision at the resolved destination

            // The manifest name actually landed on disk may differ from the raw newName if the
            // truncate-guard above shortened it (see TruncateNameIfPathTooLong) — always record the
            // name that will actually be on disk (Path.GetFileName(newPath)), not the untruncated
            // newName, so a later Restore doesn't diverge from what's actually there (N4).
            var actualNewName = Path.GetFileName(newPath);

            // The manifest's new entry is added — leaving the OLD entry for fullStored untouched —
            // before the physical move; see TryRenameManifestEntry remarks / ADR-013 Decision 5 for
            // why this is no longer an atomic re-key. On failure (untrusted cache or a disk write
            // failure) this aborts before any file-system mutation happens.
            if (!TryRenameManifestEntry(fullStored, newPath, actualNewName, out var hadOldEntry))
            {
                Console.Error.WriteLine($"PanelItemStorageService.RenameManaged: manifest write failed, aborting rename of '{storedPath}'");
                return new RenameResult(RenameOutcome.Failed, null);
            }

            try
            {
                Directory.CreateDirectory(targetDir);
                if (isDirectory) Directory.Move(fullStored, newPath);
                else File.Move(fullStored, newPath);
            }
            catch (Exception ex)
            {
                // The physical move failed. Unlike the old "atomic re-key" scheme, no rollback is
                // needed: TryRenameManifestEntry above deliberately left the OLD entry for fullStored
                // in place untouched, so Restore(fullStored) still works exactly as it did before this
                // call. The NEW entry it added (if hadOldEntry) is now stray — it maps newPath to an
                // original path, but newPath was never actually created since the move failed — so
                // it's removed here as a best-effort cleanup; if that removal itself fails, the stray
                // entry is harmless (Restore is only ever invoked with a managed path that actually
                // exists on disk, so it can never be looked up — see TryRenameManifestEntry remarks).
                if (hadOldEntry) RemoveManifestEntry(newPath);
                Console.Error.WriteLine($"PanelItemStorageService.RenameManaged: move failed for '{storedPath}' -> '{newPath}': {ex.Message}");
                return new RenameResult(RenameOutcome.Failed, null);
            }

            // Physical move succeeded — only now is it safe to drop the OLD manifest entry; the item
            // is fully described by the new entry TryRenameManifestEntry already wrote above.
            if (hadOldEntry) RemoveManifestEntry(fullStored);

            return new RenameResult(RenameOutcome.Renamed, newPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService.RenameManaged failed for '{storedPath}': {ex.Message}");
            return new RenameResult(RenameOutcome.Failed, null);
        }
    }

    /// <inheritdoc />
    public bool TryFindManagedPath(string originalPath, out string? managedPath)
    {
        // M1 fix (P08 fix cycle 1, 2026-09-22): only ever report a candidate when it's the SINGLE live
        // one — see the interface doc remarks for why an ambiguous manifest must not arbitrarily pick
        // the first match by dictionary enumeration order.
        var candidates = FindManagedCandidates(originalPath);
        if (candidates.Count == 1)
        {
            managedPath = candidates[0];
            return true;
        }

        managedPath = null;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindManagedCandidates(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return [];

        string fullOriginal;
        try
        {
            fullOriginal = Path.GetFullPath(originalPath);
        }
        catch (Exception)
        {
            // Malformed path — definitely nothing to resolve.
            return [];
        }

        List<string>? candidates = null;

        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return [];

            foreach (var (candidateManagedPath, candidateOriginalPath) in manifest)
            {
                if (!string.Equals(candidateOriginalPath, fullOriginal, StringComparison.OrdinalIgnoreCase)) continue;

                // The manifest entry may be stale itself (e.g. the managed copy was since deleted by
                // hand, outside this service) — only report a path that actually still resolves to
                // something on disk, so callers never trade one broken path for another.
                if (!File.Exists(candidateManagedPath) && !Directory.Exists(candidateManagedPath)) continue;

                (candidates ??= []).Add(candidateManagedPath);
            }
        }

        return candidates ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Same as <see cref="MoveIn(string)"/>, but with an internal-only escape hatch for tests: when
    /// <paramref name="forceCrossVolume"/> is <see langword="true"/>, the same-volume fast path is
    /// skipped even if source and storage root are on the same drive, forcing the
    /// copy-then-delete-after-verified-success branch. Real callers always go through the
    /// single-argument overload; this only exists because cross-volume behavior can't be
    /// deterministically reproduced in CI (which typically has a single volume).
    /// </summary>
    /// <param name="sourcePath">Full path of the file to move into managed storage.</param>
    /// <param name="forceCrossVolume">When <see langword="true"/>, forces the copy-then-delete path even if source and storage root are on the same volume.</param>
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
                // destPath now lives under a per-item GUID directory (root\{guid}\{originalName}) —
                // that directory doesn't exist yet, and File.Move requires its parent to. Nothing is
                // copied here, so creating an empty directory ahead of an atomic rename is safe (no
                // partial-copy window to guard).
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Move(fullSource, destPath);
            }
            else
            {
                if (!TryCopyFileVerified(fullSource, destPath))
                {
                    TryDeletePartialFile(destPath);
                    RemoveManifestEntry(destPath);
                    TryCleanupEmptyItemDirectory(destPath);
                    return null;
                }

                // Copy verified complete — only now is it safe to remove the original.
                File.Delete(fullSource);
            }

            return destPath;
        }
        catch (Exception ex)
        {
            if (destPath is not null)
            {
                RemoveManifestEntry(destPath);
                TryCleanupEmptyItemDirectory(destPath);
            }

            Console.Error.WriteLine($"PanelItemStorageService.MoveIn failed for '{sourcePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Same as <see cref="MoveInDirectory(string)"/>, but with an internal-only escape hatch for
    /// tests — see <see cref="MoveIn(string, bool)"/> remarks.
    /// </summary>
    /// <param name="sourcePath">Full path of the directory to move into managed storage.</param>
    /// <param name="forceCrossVolume">When <see langword="true"/>, forces the copy-then-delete path even if source and storage root are on the same volume.</param>
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

                // destRoot now lives under a per-item GUID directory (root\{guid}\{originalName}).
                // Directory.Move requires the parent to exist but the destination itself to NOT
                // exist (it performs the rename) — so only the GUID directory is created here, never
                // destRoot itself.
                Directory.CreateDirectory(Path.GetDirectoryName(destRoot)!);
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
                            TryCleanupEmptyItemDirectory(destRoot);
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory failed to copy '{sourcePath}': {ex.Message}");
                    TryDeletePartialDirectory(destRoot);
                    RemoveManifestEntry(destRoot);
                    TryCleanupEmptyItemDirectory(destRoot);
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
            if (destRoot is not null)
            {
                RemoveManifestEntry(destRoot);
                TryCleanupEmptyItemDirectory(destRoot);
            }

            Console.Error.WriteLine($"PanelItemStorageService.MoveInDirectory failed for '{sourcePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Same as <see cref="Restore(string)"/>, but with an internal-only escape hatch for tests: when
    /// <paramref name="forceCrossVolume"/> is <see langword="true"/>, the same-volume fast path
    /// (<see cref="File.Move(string, string)"/>/<see cref="Directory.Move(string, string)"/>) is
    /// skipped entirely and the copy-then-delete-after-verified-success branch runs directly —
    /// see <see cref="MoveIn(string, bool)"/> remarks for why this exists.
    /// </summary>
    /// <param name="storedPath">Full path of the managed item to restore, inside managed storage.</param>
    /// <param name="forceCrossVolume">When <see langword="true"/>, forces the copy-then-delete path even if the original location and storage root are on the same volume.</param>
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
            TryCleanupEmptyItemDirectory(fullStored);
            return RestoreOutcome.Restored;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService.Restore failed for '{storedPath}': {ex.Message}");
            return RestoreOutcome.Failed;
        }
    }

    // ── Destination naming (static helpers) ─────────────────────────────────

    /// <summary>
    /// If <paramref name="candidate"/> exceeds <see cref="MaxDestinationPathLength"/>, truncates only
    /// <paramref name="baseName"/> (preserving <paramref name="ext"/> and the fixed
    /// <paramref name="parentDir"/> prefix) and rebuilds the path; otherwise returns
    /// <paramref name="candidate"/> unchanged. Shared by the "new item" naming above and by
    /// <see cref="RenameManaged"/>, which truncates against an existing per-item directory instead
    /// of a freshly generated one.
    /// <para>
    /// Two things beyond a plain <c>[..maxNameLength]</c> slice are handled so the truncated name is
    /// still a name Windows will actually create verbatim: (1) the cut point is nudged back one char
    /// if it would otherwise land inside a UTF-16 surrogate pair, so a truncated name never ends in
    /// a lone (invalid) high surrogate; (2) trailing spaces/dots left exposed by the cut are trimmed,
    /// since Windows silently strips those from the final path segment — for a directory (empty
    /// <paramref name="ext"/>) an untrimmed trailing dot/space would make the path this method
    /// returns diverge from the path actually created on disk once <see cref="Directory.CreateDirectory(string)"/>
    /// runs.
    /// </para>
    /// <para>
    /// <b>Known edge case:</b> the ≤<see cref="MaxDestinationPathLength"/> guarantee assumes
    /// <paramref name="parentDir"/> + <paramref name="ext"/> alone (the "fixed" part of the path that
    /// can't be shortened here) leave at least 1 character of budget for the name. In the extreme —
    /// an unusually deep/long per-user storage root combined with an unusually long file extension —
    /// that budget could itself already exceed the cap; <c>Math.Max(1, …)</c> below intentionally
    /// still produces a 1-character name rather than an empty/negative one in that case, but the
    /// overall path can then exceed the cap. This is considered acceptable: it requires an
    /// environmental path length pre-existing near the Windows MAX_PATH boundary regardless of
    /// anything this service does, which is already outside what a 240-char soft cap can fully
    /// guard against.
    /// </para>
    /// </summary>
    private static string TruncateNameIfPathTooLong(string candidate, string parentDir, string baseName, string ext)
    {
        if (candidate.Length <= MaxDestinationPathLength) return candidate;

        var fixedLength = candidate.Length - baseName.Length;
        var maxNameLength = Math.Max(1, MaxDestinationPathLength - fixedLength);
        var truncated = baseName.Length > maxNameLength ? baseName[..maxNameLength] : baseName;

        // Never split a UTF-16 surrogate pair: if the cut landed right after a high surrogate (i.e.
        // the low surrogate that completes it was cut off), drop the now-lone high surrogate too.
        if (truncated.Length > 0 && char.IsHighSurrogate(truncated[^1]))
            truncated = truncated[..^1];

        // Windows silently strips a trailing space or dot from the final path segment — trim it here
        // so the path this method returns matches what's actually created on disk.
        truncated = truncated.TrimEnd(' ', '.');
        if (truncated.Length == 0) truncated = "_"; // degenerate case: nothing survived trimming

        return Path.Combine(parentDir, truncated + ext);
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
    /// failure or verification mismatch. Verification compares actual content, not just file length
    /// — a length-only check would let a silently truncated-then-padded or bit-flipped copy through,
    /// after which the (only) source copy would be deleted.
    /// </summary>
    private static bool TryCopyFileVerified(string sourcePath, string destPath)
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
            Console.Error.WriteLine($"PanelItemStorageService: verified copy failed '{sourcePath}' -> '{destPath}': {ex.Message}");
            return false;
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
            var sourceRead = sourceStream.Read(sourceBuffer, 0, BufferSize);
            var destRead = destStream.Read(destBuffer, 0, BufferSize);

            if (sourceRead != destRead) return false;
            if (sourceRead == 0) return true; // both streams exhausted at the same length

            if (!sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(destBuffer.AsSpan(0, destRead)))
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

    // ── Manifest (managed path -> original path): static helper ─────────────
    private static Dictionary<string, string> CreateEmptyManifest() =>
        new(StringComparer.OrdinalIgnoreCase);

    // ── Per-item GUID directory cleanup ─────────────────────────────────────

    /// <summary>
    /// Deletes the per-item GUID directory a moved-in file/folder lived under (see
    /// <see cref="BuildDestinationPath"/>/<see cref="BuildDestinationDirectoryPath"/>) once it's
    /// orphaned by a successful <see cref="Restore(string)"/>/<see cref="Release"/> — <b>only</b> if
    /// that directory is a direct child of <see cref="_storageRoot"/> and is now empty. Never deletes
    /// <see cref="_storageRoot"/> itself, and never deletes a non-empty directory. A legacy flat-layout
    /// item (pre-GUID-directory) has no per-item directory to clean up — its parent already IS the
    /// storage root, which this deliberately never touches, so it's a safe no-op for those.
    /// </summary>
    /// <param name="fullStoredPath">
    /// The full, already-normalized path of the item that was just moved away/deleted (whether the
    /// item was a file or a directory — either way, its parent is the per-item GUID directory).
    /// </param>
    private void TryCleanupEmptyItemDirectory(string fullStoredPath)
    {
        try
        {
            var itemDir = Path.GetDirectoryName(fullStoredPath);
            if (string.IsNullOrEmpty(itemDir) || !Directory.Exists(itemDir)) return;

            var itemDirFull = Path.GetFullPath(itemDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetFullPath(_storageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Guard against ever deleting the storage root: only a directory whose OWN parent is the
            // root qualifies (i.e. itemDir must be exactly one level below root). This also correctly
            // skips legacy flat items, whose "item directory" as computed above already IS the root.
            var parentOfItemDir = Path.GetDirectoryName(itemDirFull);
            if (parentOfItemDir is null) return;

            var parentOfItemDirFull = Path.GetFullPath(parentOfItemDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(parentOfItemDirFull, root, StringComparison.OrdinalIgnoreCase)) return;

            if (Directory.EnumerateFileSystemEntries(itemDirFull).Any()) return; // not empty — leave it

            Directory.Delete(itemDirFull);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PanelItemStorageService: failed to clean up item directory for '{fullStoredPath}': {ex.Message}");
        }
    }

    // ── Destination naming ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a collision-free destination for a moved-in file: a per-item GUID directory holding
    /// the file under its <b>original</b> name — <c>{storageRoot}\{guid}\{originalName}{ext}</c>.
    /// The GUID guarantees collision-free placement in one shot (no racy check-then-create loop, no
    /// "(2)"-style counter) while leaving the file's own name untouched, so
    /// <see cref="System.IO.Path.GetFileName(string)"/> on the result is exactly the original name
    /// (see ADR-013 Decision 2). The base name is truncated — keeping the extension intact — only if
    /// the resulting full path would exceed <see cref="MaxDestinationPathLength"/>; otherwise it is
    /// never shortened.
    /// </summary>
    private string BuildDestinationPath(string fullSourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(fullSourcePath);
        var ext = Path.GetExtension(fullSourcePath);
        var guid = Guid.NewGuid().ToString("N");
        var itemDir = Path.Combine(_storageRoot, guid);

        var candidate = Path.Combine(itemDir, name + ext);
        return TruncateNameIfPathTooLong(candidate, itemDir, name, ext);
    }

    /// <summary>
    /// Directory counterpart of <see cref="BuildDestinationPath"/>: <c>{storageRoot}\{guid}\{originalFolderName}</c>.
    /// </summary>
    private string BuildDestinationDirectoryPath(string fullSourcePath)
    {
        var name = Path.GetFileName(fullSourcePath);
        if (string.IsNullOrEmpty(name)) name = "folder";
        var guid = Guid.NewGuid().ToString("N");
        var itemDir = Path.Combine(_storageRoot, guid);

        var candidate = Path.Combine(itemDir, name);
        return TruncateNameIfPathTooLong(candidate, itemDir, name, ext: string.Empty);
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
    /// Adds the new manifest entry for a <see cref="RenameManaged"/> destination, leaving the OLD
    /// entry for <paramref name="oldManagedPath"/> (if any) completely untouched — deliberately NOT
    /// an atomic re-key. Both entries can exist in the manifest simultaneously after this returns
    /// successfully; the caller (<see cref="RenameManaged"/>) is responsible for removing the old
    /// entry via <see cref="RemoveManifestEntry"/>, but only <b>after</b> the physical move that
    /// follows this call has actually succeeded.
    /// <para>
    /// This "add first, remove-old later" sequencing (rather than a single atomic write that both
    /// removes the old key and adds the new one) exists specifically so a physical move that fails
    /// AFTER this method returns can never strand the manifest without a valid entry for the item's
    /// current (still physically present, unchanged) path: the old key is simply still there, so
    /// <see cref="Restore(string)"/> against <paramref name="oldManagedPath"/> keeps working exactly as
    /// before, with no rollback logic needed at all. The "extra" new key this leaves behind in that
    /// failure case is harmless garbage: it maps a managed path that was never actually created
    /// (the move never happened) to an original path, and <see cref="Restore(string)"/> is only ever invoked
    /// by callers holding a managed path that actually exists on disk — that stray key can never be
    /// looked up. <see cref="RenameManaged"/> still makes a best-effort attempt to remove it anyway
    /// so it doesn't linger, but a failure to do so is not itself a data-loss risk (see ADR-012
    /// Block F / the code-quality-reviewer finding this replaces).
    /// </para>
    /// </summary>
    /// <param name="oldManagedPath">The item's current managed path (its existing manifest key, if any).</param>
    /// <param name="newManagedPath">The item's managed path after the rename (the new manifest key to add).</param>
    /// <param name="newName">
    /// The bare file/folder name that will actually land on disk (already truncated if the
    /// truncate-guard applied — see <see cref="TruncateNameIfPathTooLong"/>), substituted into the
    /// recorded original path.
    /// </param>
    /// <param name="hadOldEntry">
    /// <see langword="true"/> if <paramref name="oldManagedPath"/> had a manifest entry (in which case
    /// the new entry was written, and the caller must remove the old one once the physical move
    /// succeeds). <see langword="false"/> if there was no entry to begin with (e.g. a legacy item —
    /// see <see cref="RestoreOutcome.OriginalPathUnknown"/>), in which case nothing was written and
    /// there is nothing for the caller to do either way.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if it's safe to proceed to the physical move — either the new entry was
    /// successfully added, or there was no old entry to begin with (nothing to add).
    /// <see langword="false"/> if the manifest cache is currently untrusted or the write-through to
    /// disk failed: nothing was mutated on disk, and the caller MUST NOT perform the physical move.
    /// </returns>
    private bool TryRenameManifestEntry(string oldManagedPath, string newManagedPath, string newName, out bool hadOldEntry)
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted)
            {
                hadOldEntry = false;
                return false;
            }

            if (!manifest.TryGetValue(oldManagedPath, out var existingOriginal))
            {
                hadOldEntry = false;
                return true;
            }

            var originalDir = Path.GetDirectoryName(existingOriginal);
            var newOriginal = string.IsNullOrEmpty(originalDir) ? newName : Path.Combine(originalDir, newName);

            manifest[newManagedPath] = newOriginal;

            if (WriteManifestUnlocked(manifest))
            {
                hadOldEntry = true;
                return true;
            }

            // Roll back the in-memory mutation so the cache matches what's actually still on disk.
            // The old entry was never touched, so there's nothing to restore for it.
            manifest.Remove(newManagedPath);
            hadOldEntry = false;
            return false;
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
