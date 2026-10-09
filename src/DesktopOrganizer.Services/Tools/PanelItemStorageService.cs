using System.IO;
using System.IO.Enumeration;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal sealed class PanelItemStorageService : IPanelItemStorageService
{
    /// <summary>
    /// Name of the manifest file at the root of managed storage. Internal (not private) so
    /// <see cref="PanelItemsUninstallRestorer"/> — the <c>--restore-panel-items</c> uninstall command,
    /// same assembly — reads/removes the exact same file without duplicating the literal.
    /// </summary>
    internal const string ManifestFileName = ".manifest.json";

    // Safety cap for cross-volume MoveInDirectory — recursively copying an arbitrarily large folder
    // (someone's entire "Downloads") on a single drag&drop would be slow and double disk usage for no
    // benefit. Past this, the folder is left as a live reference instead (see MoveInDirectory remarks).
    // Same-volume moves are an atomic Directory.Move and are never subject to this cap.
    private const long MaxDirectoryImportBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
    private const int MaxDirectoryImportFileCount = 20_000;

    // Soft cap on the full destination path length. Windows' legacy MAX_PATH (260) is the hard
    // constraint; 240 leaves headroom for Restore's own path composition (parent directory,
    // Explorer-style " (2)" collision suffix) without itself running the guid-directory scheme's
    // hard limits. See ADR-013 Decision 2, trade-off.
    private const int MaxDestinationPathLength = 240;

    // A directory a shell command created next to a managed item is adopted only once it has not changed for this
    // long (see IsSettled) — "Extract to folder" keeps adding children to it for a while.
    private static readonly TimeSpan CreatedDirectoryQuietPeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// M-B (release-polish 1.0 final review): DOS wildcard name patterns (<see cref="IsExcludedFromAdoption"/>)
    /// of files common desktop apps and the shell write next to a document while it's open, or as their own
    /// bookkeeping — never something the user asked for through a shell command. <c>"~$*"</c> is Microsoft
    /// Office's lock file convention (<c>~$Document.docx</c>); <c>".~lock.*#"</c> is LibreOffice/OpenOffice's
    /// (<c>.~lock.Document.docx#</c>); <c>"*.tmp"</c> covers the generic save-as-temp-then-rename pattern many
    /// editors use; <c>desktop.ini</c>/<c>Thumbs.db</c> are shell/Explorer bookkeeping.
    /// </summary>
    private static readonly string[] ExcludedAdoptionNamePatterns =
    [
        "~$*",
        ".~lock.*#",
        "*.tmp",
        "desktop.ini",
        "Thumbs.db",
    ];

    private readonly string _storageRoot;
    private readonly string _manifestPath;
    private readonly object _manifestLock = new();
    private readonly INativeApi _nativeApi;
    private readonly IAppLog? _appLog;

    /// <summary>
    /// MINOR 10 (release-polish 1.0 final review): paths <see cref="TryAdoptCreatedEntry"/> has already
    /// logged a deferral warning for, so a still-open entry retried across every liveness tick for up to
    /// ~2 minutes (<c>ChildWindowViewModel.SiblingAdoptionWatchTicks</c>) logs only ONCE instead of once
    /// per tick. Cleared once an entry actually adopts; a permanently-stuck entry (never adopted, watch
    /// eventually expires) leaves one small stray key behind — bounded by how many such entries a session
    /// ever sees, accepted as negligible. Used only from the UI thread, not guarded by <see cref="_manifestLock"/> (see the threading contract at <c>_manifestCache</c>).
    /// </summary>
    private readonly HashSet<string> _deferredAdoptionWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// M-3 fix (code review, panel clone to page): every managed path this instance has itself produced
    /// via <see cref="CopyManaged"/>, for the lifetime of this (singleton) instance — guarded by
    /// <see cref="_manifestLock"/>, same as the manifest cache it lives alongside. <see cref="DiscardCopies"/>
    /// only ever deletes a path found in this set (removing it once deleted), so a caller that mistakenly
    /// hands it an arbitrary managed path — one that was never a clone copy, e.g. a real already-adopted
    /// item from <see cref="MoveIn(string)"/>/<see cref="MoveInDirectory(string)"/>, or a copy from an
    /// entirely different, already-finished operation — can never have it irrevocably deleted outside the
    /// Recycle Bin. A successfully discarded copy is removed from this set; a copy that is never discarded
    /// (the clone that created it succeeded) lingers here for the rest of the process — bounded by how many
    /// successful clones a session ever makes, accepted as negligible (same trade-off as
    /// <see cref="_deferredAdoptionWarned"/> above).
    /// </summary>
    private readonly HashSet<string> _copiesThisSession = new(StringComparer.OrdinalIgnoreCase);

    // Authoritative in-memory copy of the manifest, lazily loaded from disk on first access and
    // kept in sync via write-through on every mutation (see the "Manifest" region below). This
    // service is registered as a DI singleton. Threading contract: Restore and GetManifestEntries may
    // also be called from a background thread (ManagedStorageOrphansViewModel.TryReturn runs Restore on
    // the thread pool) — they only touch the manifest, and every manifest access goes through
    // _manifestLock. They must not use the UI-thread-only state (_deferredAdoptionWarned, used by the
    // sibling-adoption path). Every other member is used from the UI thread only.
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
    /// The log this instance writes failures to: the one explicitly supplied (DI resolves
    /// <see cref="IAppLog"/> automatically since it's registered — see
    /// <see cref="DesktopOrganizer.Services.Configuration.DiConfig"/>), falling back to the process-wide
    /// ambient <see cref="AppLog.Current"/> for tests that construct this type directly without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageService"/> class, using the
    /// real per-user app-data folder as storage root (<see cref="Storage.AppStorageRoot.Default"/> —
    /// Debug and Release builds resolve to different folders, see <see cref="Storage.AppStorageRoot"/>).
    /// </summary>
    /// <param name="nativeApi">Shell access used by <see cref="ReleaseToRecycleBin"/> (Recycle Bin, ADR-035).</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public PanelItemStorageService(INativeApi nativeApi, IAppLog? appLog = null)
        : this(Path.Combine(Storage.AppStorageRoot.Default, "PanelItems"), nativeApi, appLog)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageService"/> class with an
    /// explicit storage root. Internal — used by tests to point at a disposable temp folder
    /// instead of the real per-user app-data location (and a mocked <see cref="INativeApi"/> instead
    /// of the real Recycle Bin).
    /// </summary>
    /// <param name="storageRoot">The folder moved-in files are stored under.</param>
    /// <param name="nativeApi">Shell access used by <see cref="ReleaseToRecycleBin"/>.</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    internal PanelItemStorageService(string storageRoot, INativeApi nativeApi, IAppLog? appLog = null)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);

        _storageRoot = storageRoot;
        _manifestPath = Path.Combine(_storageRoot, ManifestFileName);
        _nativeApi = nativeApi;
        _appLog = appLog;
    }

    /// <inheritdoc />
    public string? MoveIn(string sourcePath) => MoveIn(sourcePath, forceCrossVolume: false);

    /// <inheritdoc />
    public string? MoveInDirectory(string sourcePath) => MoveInDirectory(sourcePath, forceCrossVolume: false);

    /// <inheritdoc />
    public RestoreOutcome Restore(string storedPath) => Restore(storedPath, forceCrossVolume: false);

    /// <inheritdoc />
    /// <remarks>
    /// ADR-035: this method never deletes item content itself — the only destructive step is the
    /// single <see cref="INativeApi.RecycleItems"/> batch. What actually went to the Recycle Bin is
    /// decided afterwards by checking which paths still exist, so a partially-completed shell
    /// operation (locked file, user declined the "delete permanently?" prompt, path too long for
    /// <c>SHFileOperation</c>) is reported item by item. The only things removed directly are the
    /// manifest entries and the empty per-item GUID wrapper directories of recycled items.
    /// </remarks>
    public RecycleOutcome ReleaseToRecycleBin(IReadOnlyList<string> storedPaths)
    {
        ArgumentNullException.ThrowIfNull(storedPaths);

        // (caller's path, normalized full path) for every distinct managed path.
        var managed = new List<(string Original, string Full)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in storedPaths)
        {
            if (!IsManaged(path)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                continue; // IsManaged already rejects malformed paths; belt-and-suspenders.
            }

            // M8 (release-hardening review): IsManaged(path) alone accepts the storage root itself and
            // any of its descendants (by design — other callers, e.g. IsManaged(_storageRoot), rely on
            // that). Recycling must never be handed the root or a bare per-item GUID wrapper directory
            // directly — only an actual item strictly below one.
            if (!IsStrictlyManagedItem(full)) continue;

            if (seen.Add(full)) managed.Add((path, full));
        }

        if (managed.Count == 0) return RecycleOutcome.Create([], [], cancelled: false);

        // Only paths that exist go to the shell: SHFileOperation fails the whole batch on a missing
        // source. A path that is already gone has nothing left to lose and is reported as recycled.
        var toRecycle = managed.Where(m => PathExists(m.Full)).Select(m => m.Full).ToArray();

        var shellReportedSuccess = true;
        if (toRecycle.Length > 0)
        {
            try
            {
                shellReportedSuccess = _nativeApi.RecycleItems(toRecycle, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                // INativeApi.RecycleItems is documented never to throw; a test double or future
                // implementation that does must still not take the UI thread down.
                shellReportedSuccess = false;
                Log.Error(
                    "PanelItemStorageService",
                    "RecycleItemsThrew",
                    ex,
                    new KeyValuePair<string, object?>("count", toRecycle.Length));
            }
        }

        var recycled = new List<string>();
        var remaining = new List<string>();
        var recycledFull = new List<string>();
        foreach (var (original, full) in managed)
        {
            if (PathExists(full))
            {
                remaining.Add(original);
            }
            else
            {
                recycled.Add(original);
                recycledFull.Add(full);
            }
        }

        RemoveManifestEntries(recycledFull);
        foreach (var full in recycledFull) TryCleanupEmptyItemDirectory(full);

        if (remaining.Count > 0 || !shellReportedSuccess)
        {
            Log.Warn(
                "PanelItemStorageService",
                "RecycleIncomplete",
                new KeyValuePair<string, object?>("requested", managed.Count),
                new KeyValuePair<string, object?>("recycled", recycled.Count),
                new KeyValuePair<string, object?>("remaining", remaining.Count),
                new KeyValuePair<string, object?>("shellReportedSuccess", shellReportedSuccess));
        }

        return RecycleOutcome.Create(recycled, remaining, cancelled: !shellReportedSuccess);
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
                Log.Warn(
                    "PanelItemStorageService",
                    "RenameManagedManifestWriteFailed",
                    new KeyValuePair<string, object?>("path", storedPath));
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
                Log.Error(
                    "PanelItemStorageService",
                    "RenameManagedMoveFailed",
                    ex,
                    new KeyValuePair<string, object?>("path", storedPath),
                    new KeyValuePair<string, object?>("newPath", newPath));
                return new RenameResult(RenameOutcome.Failed, null);
            }

            // Physical move succeeded — only now is it safe to drop the OLD manifest entry; the item
            // is fully described by the new entry TryRenameManifestEntry already wrote above.
            if (hadOldEntry) RemoveManifestEntry(fullStored);

            return new RenameResult(RenameOutcome.Renamed, newPath);
        }
        catch (Exception ex)
        {
            Log.Error(
                "PanelItemStorageService",
                "RenameManagedFailed",
                ex,
                new KeyValuePair<string, object?>("path", storedPath));
            return new RenameResult(RenameOutcome.Failed, null);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyValuePair<string, string>> GetManifestEntries()
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            return trusted ? [.. manifest] : [];
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

    /// <inheritdoc />
    public void ForgetMovedOut(string path)
    {
        if (!IsManaged(path)) return;

        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return; // malformed path — IsManaged already rejects most of these; belt-and-suspenders.
        }

        // M8 (release-hardening review): see the identical guard in ReleaseToRecycleBin — never forget
        // the storage root or a bare per-item GUID wrapper directory itself, only an actual item
        // strictly below one.
        if (!IsStrictlyManagedItem(full)) return;

        // The only gate this method trusts: if the path is still there, "moved out" hasn't actually
        // happened (a copy, a cancelled drag, a same-panel drop) — never touch the manifest for an item
        // that's still really there.
        if (PathExists(full)) return;

        RemoveManifestEntry(full);
        TryCleanupEmptyItemDirectory(full);
    }

    /// <inheritdoc />
    public ManagedItemReconcileResult ReconcileExternalChange(string storedPath) => Reconcile(storedPath, entriesBefore: null);

    /// <inheritdoc />
    public ManagedItemReconcileResult ReconcileExternalChange(string storedPath, IReadOnlyCollection<string> entriesBefore)
    {
        ArgumentNullException.ThrowIfNull(entriesBefore);
        return Reconcile(storedPath, entriesBefore);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetItemDirectoryEntries(string storedPath)
    {
        try
        {
            if (!TryGetPerItemLayout(storedPath, out _, out var itemDir)) return [];
            return Directory.Exists(itemDir) ? NormalizeEntries(Directory.GetFileSystemEntries(itemDir)) : [];
        }
        catch (Exception ex)
        {
            Log.Error(
                "PanelItemStorageService",
                "ItemDirectorySnapshotFailed",
                ex,
                new KeyValuePair<string, object?>("path", storedPath));
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Recycle Bin call is the same one <see cref="ReleaseToRecycleBin"/> makes (<see cref="INativeApi.RecycleItems"/>
    /// with no owner window, ADR-035): <c>FOF_ALLOWUNDO</c>, no confirmation, <c>FOF_WANTNUKEWARNING</c>. Holding
    /// Shift in the shell menu (Explorer's "delete permanently") does not bypass the Recycle Bin here — a managed
    /// item is never destroyed for good by this application.
    /// </remarks>
    public ManagedRecycleResult RecycleManagedItem(string storedPath)
    {
        var notManaged = new ManagedRecycleResult(ManagedRecycleKind.NotManaged, null);
        if (!IsManaged(storedPath)) return notManaged;

        string full;
        try
        {
            full = Path.GetFullPath(storedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return notManaged; // malformed — IsManaged already rejects most of these.
        }

        // Same M8 guard as ReleaseToRecycleBin: never the storage root or a bare per-item GUID directory.
        if (!IsStrictlyManagedItem(full)) return notManaged;

        if (!PathExists(full))
        {
            // Nothing left to recycle — just release the bookkeeping.
            RemoveManifestEntry(full);
            TryCleanupEmptyItemDirectory(full);
            return new ManagedRecycleResult(ManagedRecycleKind.Recycled, null);
        }

        // MINOR 4 (release-polish 1.0 final review): never restore-then-recycle onto a removable/network
        // original location — its Recycle Bin (if it even has one on this machine) isn't guaranteed to
        // exist, so the shell's own recycle call could permanently delete the file instead. Peek at the
        // recorded origin BEFORE attempting the restore at all — an untrusted manifest or no recorded
        // origin (a legacy item) skips the restore attempt exactly like "restore impossible" already did.
        var (recycleOriginal, recycleOriginalTrusted) = ReadManifestEntry(full);
        var restore = RestoreOutcome.OriginalPathUnknown;
        string? restoredPath = null;
        if (recycleOriginalTrusted && recycleOriginal is not null && ManagedStorageFileOps.IsFixedLocalDrive(recycleOriginal))
        {
            restore = Restore(full, forceCrossVolume: false, out restoredPath);
        }

        if (restore == RestoreOutcome.Restored && restoredPath is not null)
        {
            return RecycleRestoredItem(restoredPath);
        }

        // Restore impossible or skipped (legacy item without an original path, an untrusted manifest, a
        // non-fixed-local original drive, the original location cannot be recreated, the
        // move failed — the managed copy is untouched in every such case): recycle it where it is, exactly what the
        // shell's own Delete would have done. A later "Restore" from the Recycle Bin then lands in managed storage
        // (reconciled away by the panel) — accepted for this rare path, logged.
        Log.Warn(
            "PanelItemStorageService",
            "RecycleManagedFromStorage",
            new KeyValuePair<string, object?>("restoreOutcome", restore.ToString()));
        var outcome = ReleaseToRecycleBin([storedPath]);
        return outcome.IsComplete
            ? new ManagedRecycleResult(ManagedRecycleKind.Recycled, null)
            : new ManagedRecycleResult(ManagedRecycleKind.Kept, storedPath);
    }

    /// <inheritdoc />
    public ManagedCopyResult CopyManaged(string storedPath)
    {
        var notManaged = new ManagedCopyResult(ManagedCopyOutcome.NotManaged, null);
        if (!IsManaged(storedPath)) return notManaged;

        string full;
        try
        {
            full = Path.GetFullPath(storedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return notManaged; // malformed — IsManaged already rejects most of these.
        }

        // Same M8 guard as ReleaseToRecycleBin/ForgetMovedOut: never the storage root or a bare per-item
        // GUID wrapper directory itself, only an actual item strictly below one.
        if (!IsStrictlyManagedItem(full)) return notManaged;

        var isDirectory = Directory.Exists(full);
        var isFile = !isDirectory && File.Exists(full);
        if (!isDirectory && !isFile) return new ManagedCopyResult(ManagedCopyOutcome.SourceMissing, null);

        // M-2 fix (code review, panel clone to page): a legacy item with no manifest entry of its own (a
        // TRUSTED read that found no entry) copies as a legacy item too — its copy's manifest state
        // mirrors the source's, same as every other appearance/behavior field a clone duplicates (see the
        // interface doc remarks). Only an UNTRUSTED manifest read (a transient disk failure) blocks the
        // clone entirely: it means the source's own manifest state — legacy or not — could not actually be
        // determined at all, not that an entry was confirmed absent.
        var (original, trusted) = ReadManifestEntry(full);
        if (!trusted) return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);

        string? destPath = null;
        try
        {
            if (isDirectory)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(full, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    Log.Error(
                        "PanelItemStorageService",
                        "CopyManagedEnumerateFailed",
                        ex,
                        new KeyValuePair<string, object?>("path", storedPath));
                    return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
                }

                if (files.Length > MaxDirectoryImportFileCount) return new ManagedCopyResult(ManagedCopyOutcome.TooLarge, null);

                long totalBytes = 0;
                try
                {
                    foreach (var file in files)
                    {
                        totalBytes += new FileInfo(file).Length;
                        if (totalBytes > MaxDirectoryImportBytes) return new ManagedCopyResult(ManagedCopyOutcome.TooLarge, null);
                    }
                }
                catch (Exception ex)
                {
                    // A file vanished/became inaccessible mid-scan — bail out rather than copy a partial view.
                    Log.Error(
                        "PanelItemStorageService",
                        "CopyManagedSizeFailed",
                        ex,
                        new KeyValuePair<string, object?>("path", storedPath));
                    return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
                }

                if (!HasSufficientFreeSpace(full, totalBytes)) return new ManagedCopyResult(ManagedCopyOutcome.InsufficientSpace, null);

                destPath = BuildDestinationDirectoryPath(full);
                Directory.CreateDirectory(_storageRoot);

                if (!ManagedStorageFileOps.TryCopyDirectoryVerified(full, destPath))
                {
                    ManagedStorageFileOps.TryDeletePartialDirectory(destPath);
                    TryCleanupEmptyItemDirectory(destPath);
                    Log.Warn(
                        "PanelItemStorageService",
                        "CopyManagedVerifiedCopyFailed",
                        new KeyValuePair<string, object?>("path", storedPath));
                    return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
                }
            }
            else
            {
                long size;
                try
                {
                    size = new FileInfo(full).Length;
                }
                catch (Exception ex)
                {
                    Log.Error(
                        "PanelItemStorageService",
                        "CopyManagedSizeFailed",
                        ex,
                        new KeyValuePair<string, object?>("path", storedPath));
                    return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
                }

                if (!HasSufficientFreeSpace(full, size)) return new ManagedCopyResult(ManagedCopyOutcome.InsufficientSpace, null);

                destPath = BuildDestinationPath(full);
                Directory.CreateDirectory(_storageRoot);

                if (!ManagedStorageFileOps.TryCopyFileVerified(full, destPath))
                {
                    ManagedStorageFileOps.TryDeletePartialFile(destPath);
                    TryCleanupEmptyItemDirectory(destPath);
                    Log.Warn(
                        "PanelItemStorageService",
                        "CopyManagedVerifiedCopyFailed",
                        new KeyValuePair<string, object?>("path", storedPath));
                    return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
                }
            }

            // Unlike MoveIn/MoveInDirectory, the copy happens BEFORE the manifest write: nothing here is
            // destructive to the source, so there is no "manifest before the destructive step" invariant
            // to protect. If the manifest write fails, the fresh duplicate (never the original) is
            // removed instead. M-2: a legacy source with no manifest entry of its own (see the guard above)
            // needs no manifest write at all — the copy is legacy too, exactly like the source.
            if (original is not null && !WriteManifestEntry(destPath, original))
            {
                if (isDirectory) ManagedStorageFileOps.TryDeletePartialDirectory(destPath);
                else ManagedStorageFileOps.TryDeletePartialFile(destPath);
                TryCleanupEmptyItemDirectory(destPath);
                Log.Warn(
                    "PanelItemStorageService",
                    "CopyManagedManifestWriteFailed",
                    new KeyValuePair<string, object?>("path", storedPath));
                return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
            }

            // M-3: only a path recorded here is ever eligible for PanelItemStorageService.DiscardCopies.
            lock (_manifestLock) _copiesThisSession.Add(destPath);

            return new ManagedCopyResult(ManagedCopyOutcome.Copied, destPath);
        }
        catch (Exception ex)
        {
            if (destPath is not null)
            {
                if (isDirectory) ManagedStorageFileOps.TryDeletePartialDirectory(destPath);
                else ManagedStorageFileOps.TryDeletePartialFile(destPath);
                RemoveManifestEntry(destPath);
                TryCleanupEmptyItemDirectory(destPath);
            }

            Log.Error(
                "PanelItemStorageService",
                "CopyManagedFailed",
                ex,
                new KeyValuePair<string, object?>("path", storedPath));
            return new ManagedCopyResult(ManagedCopyOutcome.Failed, null);
        }
    }

    /// <inheritdoc />
    public void DiscardCopies(IReadOnlyList<string> copyPaths)
    {
        ArgumentNullException.ThrowIfNull(copyPaths);
        if (copyPaths.Count == 0) return;

        var toRemoveFromManifest = new List<string>();

        foreach (var copyPath in copyPaths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(copyPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                continue; // malformed — cannot be one of ours.
            }

            // Only an actual per-item managed item this service itself could have produced via
            // CopyManaged (never the storage root, a bare per-item GUID wrapper, or a legacy flat-layout
            // item, which CopyManaged never creates) is eligible for direct deletion here.
            if (!IsStrictlyManagedItem(full) || !TryGetPerItemLayout(full, out _, out _)) continue;

            // M-3 fix (code review, panel clone to page): defense in depth beyond the structural guard
            // above — only a path THIS instance actually produced via CopyManaged (see _copiesThisSession's
            // own remarks) is eligible for deletion here at all, never an arbitrary (if syntactically valid)
            // per-item managed path a caller mistakenly hands in.
            bool isThisSessionCopy;
            lock (_manifestLock) isThisSessionCopy = _copiesThisSession.Contains(full);
            if (!isThisSessionCopy) continue;

            try
            {
                var isDirectory = Directory.Exists(full);

                // H-2 fix (code review, panel clone to page): CopyManaged carries the ReadOnly attribute
                // over from the source (File.Copy/a verbatim directory copy) — clear it first, the same
                // guard ManagedStorageFileOps.TryDeletePartialDirectory/TryDeletePartialFile already apply,
                // so a read-only copy (e.g. of a read-only source document) doesn't fail this delete.
                ManagedStorageFileOps.ClearReadOnlyRecursive(full, isDirectory);

                if (isDirectory) Directory.Delete(full, recursive: true);
                else if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                // Left exactly as it is — including its manifest entry, so the orphan stays findable —
                // rather than losing track of a copy this method failed to remove.
                Log.Error(
                    "PanelItemStorageService",
                    "DiscardCopyDeleteFailed",
                    ex,
                    new KeyValuePair<string, object?>("path", copyPath));
                continue;
            }

            TryCleanupEmptyItemDirectory(full);
            toRemoveFromManifest.Add(full);
            lock (_manifestLock) _copiesThisSession.Remove(full);
        }

        RemoveManifestEntries(toRemoveFromManifest);
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
                Log.Warn(
                    "PanelItemStorageService",
                    "MoveInManifestWriteFailed",
                    new KeyValuePair<string, object?>("path", sourcePath));
                return null;
            }

            if (!forceCrossVolume && ManagedStorageFileOps.SameVolume(fullSource, _storageRoot))
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
                if (!ManagedStorageFileOps.TryCopyFileVerified(fullSource, destPath))
                {
                    ManagedStorageFileOps.TryDeletePartialFile(destPath);
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

            Log.Error(
                "PanelItemStorageService",
                "MoveInFailed",
                ex,
                new KeyValuePair<string, object?>("path", sourcePath));
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

            if (!forceCrossVolume && ManagedStorageFileOps.SameVolume(fullSource, _storageRoot))
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
                    Log.Warn(
                        "PanelItemStorageService",
                        "MoveInDirectoryManifestWriteFailed",
                        new KeyValuePair<string, object?>("path", sourcePath));
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
                    Log.Error(
                        "PanelItemStorageService",
                        "MoveInDirectoryEnumerateFailed",
                        ex,
                        new KeyValuePair<string, object?>("path", sourcePath));
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
                    Log.Error(
                        "PanelItemStorageService",
                        "MoveInDirectorySizeFailed",
                        ex,
                        new KeyValuePair<string, object?>("path", sourcePath));
                    return null;
                }

                // Manifest entry recorded before any copying starts — see MoveIn remarks. Rolled
                // back below on any failure to complete the verified copy. If the manifest write
                // itself fails, abort before any copying (and the eventual source deletion) starts.
                if (!WriteManifestEntry(destRoot, fullSource))
                {
                    RemoveManifestEntry(destRoot);
                    Log.Warn(
                        "PanelItemStorageService",
                        "MoveInDirectoryManifestWriteFailed",
                        new KeyValuePair<string, object?>("path", sourcePath));
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
                        // same discipline as MoveIn/Restore, see ManagedStorageFileOps.TryCopyFileVerified remarks.
                        if (!ManagedStorageFileOps.TryCopyFileVerified(file, destFile))
                        {
                            Log.Warn(
                                "PanelItemStorageService",
                                "MoveInDirectoryVerifiedCopyFailed",
                                new KeyValuePair<string, object?>("file", file),
                                new KeyValuePair<string, object?>("path", sourcePath));
                            ManagedStorageFileOps.TryDeletePartialDirectory(destRoot);
                            RemoveManifestEntry(destRoot);
                            TryCleanupEmptyItemDirectory(destRoot);
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(
                        "PanelItemStorageService",
                        "MoveInDirectoryCopyFailed",
                        ex,
                        new KeyValuePair<string, object?>("path", sourcePath));
                    ManagedStorageFileOps.TryDeletePartialDirectory(destRoot);
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

            Log.Error(
                "PanelItemStorageService",
                "MoveInDirectoryFailed",
                ex,
                new KeyValuePair<string, object?>("path", sourcePath));
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
    internal RestoreOutcome Restore(string storedPath, bool forceCrossVolume) => Restore(storedPath, forceCrossVolume, out _);

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
    /// M-2 (ADR-045): true when copying <paramref name="requiredBytes"/> onto the volume that hosts
    /// <paramref name="anchorPath"/> (the managed storage root — every <see cref="CopyManaged"/>
    /// destination lives there) would leave at least a 5% safety margin of free space. Conservatively
    /// returns <see langword="true"/> (never blocks the copy on this check alone) when the volume cannot
    /// be inspected — the verified copy itself is still the authoritative gate against actually running
    /// out of space.
    /// </summary>
    /// <param name="anchorPath">A path on the volume the copy will land on.</param>
    /// <param name="requiredBytes">The total size, in bytes, about to be copied.</param>
    private static bool HasSufficientFreeSpace(string anchorPath, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(anchorPath);
            if (string.IsNullOrEmpty(root)) return true;

            var required = (long)(requiredBytes * 1.05);
            return new DriveInfo(root).AvailableFreeSpace >= required;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Volume detection, ResolveCollisionFreeName, and the copy-then-verify cross-volume helpers
    // (TryCopyFileVerified/TryCopyDirectoryVerified/TryDeletePartialFile/TryDeletePartialDirectory)
    // used to live here as private statics; extracted to ManagedStorageFileOps so
    // PanelItemsUninstallRestorer (--restore-panel-items) shares the exact same verify-before-delete
    // discipline instead of a second, drift-prone copy.

    // ── Manifest (managed path -> original path): static helper ─────────────
    private static Dictionary<string, string> CreateEmptyManifest() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see langword="true"/> if a file or directory exists at <paramref name="fullPath"/>. Used by
    /// <see cref="ReleaseToRecycleBin"/> to decide per item what the shell actually recycled.
    /// </summary>
    private static bool PathExists(string fullPath) => File.Exists(fullPath) || Directory.Exists(fullPath);

    /// <summary>
    /// Normalizes directory entries (full path, no trailing separator) for case-insensitive set comparison; an
    /// entry that cannot be normalized is dropped.
    /// </summary>
    private static List<string> NormalizeEntries(IEnumerable<string> entries)
    {
        var normalized = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            try
            {
                normalized.Add(Path.GetFullPath(entry).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Malformed — cannot be one of ours.
            }
        }

        return normalized;
    }

    /// <summary>
    /// Whether an entry a shell command created looks finished, so moving it cannot break whoever is writing it:
    /// a FILE must be openable exclusively (nobody holds it open — an archiver or the zip folder writing on a
    /// background thread does); a DIRECTORY (e.g. "Extract to folder") must not have changed for
    /// <see cref="CreatedDirectoryQuietPeriod"/> — a moved half-extracted folder would split the extraction.
    /// </summary>
    /// <param name="entry">Full path of the entry.</param>
    /// <param name="isDirectory">Whether it is a directory.</param>
    /// <returns><see langword="true"/> when it is safe to adopt now.</returns>
    private static bool IsSettled(string entry, bool isDirectory)
    {
        if (isDirectory)
        {
            return DateTime.UtcNow - GetMaxLastWriteTimeUtc(entry) >= CreatedDirectoryQuietPeriod;
        }

        try
        {
            using var probe = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// MINOR 1 (release-polish 1.0 final review): the latest <c>LastWriteTimeUtc</c> anywhere in
    /// <paramref name="directory"/>'s tree, not just the top-level folder's own timestamp. "Extract to
    /// folder" and similar bulk operations keep touching FILES deep inside the tree without necessarily
    /// bumping the top folder's own write time again after its first child is created — checking only the
    /// top level could therefore report "settled" (quiet for 2s) while a background thread is still
    /// actively writing a file several levels down. Best-effort: a child that vanishes/becomes
    /// inaccessible mid-scan (the same background writer moving on) is skipped rather than aborting the
    /// whole check, falling back to whatever timestamp was already found.
    /// </summary>
    /// <param name="directory">The directory tree to scan.</param>
    private static DateTime GetMaxLastWriteTimeUtc(string directory)
    {
        var max = Directory.GetLastWriteTimeUtc(directory);

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var entryTime = File.GetLastWriteTimeUtc(entry); // valid for both files and directories.
                    if (entryTime > max) max = entryTime;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Vanished/inaccessible between the enumeration and here — skip it, keep scanning.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumeration itself failed partway (a child directory disappearing mid-walk) — the timestamp
            // found so far is still a safe, if incomplete, answer.
        }

        return max;
    }

    /// <summary>
    /// M-B (release-polish 1.0 final review): whether <paramref name="entry"/> looks like something a
    /// third-party app or the shell wrote next to a managed item on its own — never something the user
    /// asked for via the invoked shell command — and must therefore never be silently adopted as a new
    /// panel item.
    /// </summary>
    /// <param name="entry">Full path of the created entry.</param>
    /// <param name="isDirectory">Whether <paramref name="entry"/> is a directory.</param>
    /// <returns><see langword="true"/> when the entry must never be adopted.</returns>
    private static bool IsExcludedFromAdoption(string entry, bool isDirectory)
    {
        if (!isDirectory)
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary)) != 0)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Vanished/became inaccessible between the caller's own existence check and here — not
                // excluded by attributes; the normal flow (which already re-checks existence right after)
                // handles it.
            }
        }

        var name = Path.GetFileName(entry);
        foreach (var pattern in ExcludedAdoptionNamePatterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) return true;
        }

        return false;
    }

    // ── Shell-command reconcile, sibling adoption and "Delete" from the shell menu (private) ──────

    /// <summary>
    /// Shared core of both <see cref="ReconcileExternalChange(string)"/> overloads — see the interface for the
    /// full decision table. <paramref name="entriesBefore"/> is <see langword="null"/> for the snapshot-less
    /// (liveness-tick) variant, which never infers a rename and never adopts anything.
    /// </summary>
    /// <param name="storedPath">The managed path a panel references.</param>
    /// <param name="entriesBefore">The per-item directory's entries before the shell command, or <see langword="null"/>.</param>
    /// <returns>What was concluded.</returns>
    private ManagedItemReconcileResult Reconcile(string storedPath, IReadOnlyCollection<string>? entriesBefore)
    {
        var unchanged = new ManagedItemReconcileResult(ManagedItemReconcileKind.Unchanged, null);

        try
        {
            // Only a per-item GUID directory ({root}\{guid}\{name}) speaks for this one item. A legacy flat-layout
            // item's "directory" is the shared storage root, and anything deeper is content inside a managed folder,
            // not a panel item — in both cases the siblings belong to something else and must never be read as
            // "the renamed item" (or adopted).
            if (!TryGetPerItemLayout(storedPath, out var full, out var itemDir)) return unchanged;

            var itemExists = PathExists(full);

            // Still there and nothing to diff against: whatever the shell command did (Copy, Open, Properties, a
            // cancelled Delete), the item itself is intact — never touch the manifest for it.
            if (itemExists && entriesBefore is null) return unchanged;

            var entries = Directory.Exists(itemDir) ? NormalizeEntries(Directory.GetFileSystemEntries(itemDir)) : [];

            if (entriesBefore is null)
            {
                // No snapshot: only "completely gone" is certain. Any leftover entry may be the renamed item OR
                // something a shell command created next to it — never guess.
                return entries.Count == 0 ? ReleaseVanishedItem(full) : unchanged;
            }

            var before = new HashSet<string>(NormalizeEntries(entriesBefore), StringComparer.OrdinalIgnoreCase);
            var created = entries
                .Where(entry => !before.Contains(entry) && !string.Equals(entry, full, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (itemExists)
            {
                // The command created entries NEXT TO the item ("Create shortcut", "Send to > Compressed folder",
                // an archiver): show them in the panel as items of their own instead of letting them sit invisibly in
                // the hidden per-item directory, where a later rename inference could mistake one for the item.
                return created.Count == 0
                    ? unchanged
                    : unchanged with { AdoptedPaths = AdoptCreatedEntries(full, created) };
            }

            switch (created.Count)
            {
                case 0:
                    // Deleted (Recycle Bin, Shift+Delete) or moved away (Cut + Paste).
                    return ReleaseVanishedItem(full);

                case 1:
                    var newPath = created[0];
                    if (!TryReKeyManifestEntry(full, newPath))
                    {
                        // The item on disk is already renamed — the panel must follow it regardless. Only the origin
                        // mapping is at risk: a later Restore reports OriginalPathUnknown and degrades to the Recycle Bin.
                        Log.Warn(
                            "PanelItemStorageService",
                            "ReconcileManifestWriteFailed",
                            new KeyValuePair<string, object?>("path", storedPath));
                    }

                    return new ManagedItemReconcileResult(ManagedItemReconcileKind.Renamed, newPath);

                default:
                    // Vanished, and two or more new entries: cannot be attributed to a single rename — leave the item
                    // as it is (it keeps showing as missing) rather than guess.
                    return unchanged;
            }
        }
        catch (Exception ex)
        {
            Log.Error(
                "PanelItemStorageService",
                "ReconcileExternalChangeFailed",
                ex,
                new KeyValuePair<string, object?>("path", storedPath));
            return unchanged;
        }
    }

    /// <summary>
    /// Resolves the per-item layout of <paramref name="storedPath"/>: succeeds only for a single managed item
    /// directly inside its own per-item GUID directory (<c>{root}\{guid}\{name}</c>).
    /// </summary>
    /// <param name="storedPath">The path to resolve.</param>
    /// <param name="full">Receives the normalized full item path.</param>
    /// <param name="itemDir">Receives the normalized per-item directory.</param>
    /// <returns><see langword="true"/> for a per-item managed item.</returns>
    private bool TryGetPerItemLayout(string storedPath, out string full, out string itemDir)
    {
        full = string.Empty;
        itemDir = string.Empty;
        if (!IsManaged(storedPath)) return false;

        var candidate = Path.GetFullPath(storedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsStrictlyManagedItem(candidate)) return false;

        var directory = Path.GetDirectoryName(candidate);
        var root = Path.GetFullPath(_storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(directory)
            || !string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        full = candidate;
        itemDir = directory;
        return true;
    }

    /// <summary>
    /// Releases the bookkeeping of a vanished item — same cleanup as <see cref="ForgetMovedOut"/>: its manifest
    /// entry, and its per-item directory when that is empty.
    /// </summary>
    private ManagedItemReconcileResult ReleaseVanishedItem(string full)
    {
        RemoveManifestEntry(full);
        TryCleanupEmptyItemDirectory(full);
        return new ManagedItemReconcileResult(ManagedItemReconcileKind.Deleted, null);
    }

    /// <summary>
    /// Adopts every entry of <paramref name="created"/> that is ready (see <see cref="IsSettled"/>) as a new managed
    /// item — see <see cref="TryAdoptCreatedEntry"/>.
    /// </summary>
    /// <param name="fullStored">The (still existing) item the entries were created next to.</param>
    /// <param name="created">Full paths of the created entries.</param>
    /// <returns>The new managed paths of the adopted entries.</returns>
    private List<string> AdoptCreatedEntries(string fullStored, List<string> created)
    {
        // The created entries "come from" wherever the item itself came from: that is where a later
        // Restore puts them back (MINOR 7 fix — this used to say "Remove from panel", a method that
        // doesn't exist). A legacy item with no recorded origin still gives them none and is adopted
        // anyway (moved out of the item's directory and shown), behaving like any legacy item — Restore
        // reports OriginalPathUnknown for it, same as it always has.
        //
        // MINOR 2 (release-polish 1.0 final review): an UNTRUSTED manifest (a transient disk read
        // failure) is handled differently from "no entry" — defer adoption entirely rather than adopting
        // permanently with no recorded origin. The caller (ProcessPendingSiblingAdoption) simply retries
        // on the next liveness tick once the manifest becomes readable again, same as an entry that is
        // still being written (see IsSettled).
        var (original, trusted) = ReadManifestEntry(fullStored);
        if (!trusted) return [];

        var originalDir = original is null ? null : Path.GetDirectoryName(original);

        var adopted = new List<string>(created.Count);
        foreach (var entry in created)
        {
            if (TryAdoptCreatedEntry(entry, originalDir) is { } managedPath) adopted.Add(managedPath);
        }

        return adopted;
    }

    /// <summary>
    /// Moves one created entry into a fresh per-item directory of its own (same volume — both live under the
    /// storage root, so this is an atomic rename), recording <c>{originalDir}\{name}</c> as its original path.
    /// The manifest entry is written BEFORE the move and rolled back if the move fails — the same "manifest before
    /// the destructive step" discipline as <see cref="MoveIn(string)"/>.
    /// </summary>
    /// <param name="entry">Full path of the created entry.</param>
    /// <param name="originalDir">Directory recorded as the entry's origin, or <see langword="null"/> when unknown.</param>
    /// <returns>The new managed path, or <see langword="null"/> when the entry is not ready, excluded, or could not be moved.</returns>
    private string? TryAdoptCreatedEntry(string entry, string? originalDir)
    {
        string? destination = null;
        try
        {
            var isDirectory = Directory.Exists(entry);
            if (!isDirectory && !File.Exists(entry)) return null;
            if (!IsSettled(entry, isDirectory)) return null; // still being written — the caller retries later.

            // M-B (release-polish 1.0 final review): never adopt something the user did not knowingly
            // create through the shell command they picked — a third-party app's lock/backup file written
            // directly next to the managed item while it's open (LibreOffice's ".~lock.<name>#", Office's
            // "~$<name>"), a generic editor's ".tmp" save-as-temp-then-rename artifact, or a shell-written
            // desktop.ini/Thumbs.db. Left in place, untouched, forever (from this method's point of view —
            // the caller's bounded watch simply keeps not adopting it until the watch expires).
            if (IsExcludedFromAdoption(entry, isDirectory)) return null;

            destination = isDirectory ? BuildDestinationDirectoryPath(entry) : BuildDestinationPath(entry);

            if (originalDir is not null
                && !WriteManifestEntry(destination, Path.Combine(originalDir, Path.GetFileName(entry))))
            {
                RemoveManifestEntry(destination);
                Log.Warn(
                    "PanelItemStorageService",
                    "AdoptManifestWriteFailed",
                    new KeyValuePair<string, object?>("path", entry));
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (isDirectory)
            {
                Directory.Move(entry, destination);
            }
            else
            {
                File.Move(entry, destination);
            }

            // MINOR 10: an entry that previously deferred and is now adopted no longer needs its
            // one-warning-per-entry bookkeeping.
            _deferredAdoptionWarned.Remove(entry);
            return destination;
        }
        catch (Exception ex)
        {
            if (destination is not null)
            {
                RemoveManifestEntry(destination);
                TryCleanupEmptyItemDirectory(destination);
            }

            // MINOR 10 (release-polish 1.0 final review): typically a sharing violation — the entry is
            // still open somewhere and the caller (ProcessPendingSiblingAdoption) retries automatically on
            // every liveness tick for up to ~2 minutes. Logging every single retry would spam the
            // diagnostics log for something that resolves itself; only the FIRST deferral for a given
            // entry is logged.
            if (_deferredAdoptionWarned.Add(entry))
            {
                Log.Warn(
                    "PanelItemStorageService",
                    "AdoptCreatedEntryDeferred",
                    new KeyValuePair<string, object?>("path", entry),
                    new KeyValuePair<string, object?>("exception", ex.GetType().Name));
            }

            return null;
        }
    }

    /// <summary>
    /// Recycles an item <see cref="Restore(string)"/> just moved back to <paramref name="restoredPath"/>. When the
    /// shell did not recycle it, the item is moved back into managed storage so the panel keeps it.
    /// </summary>
    /// <param name="restoredPath">The item's path at its original location.</param>
    /// <returns>The outcome.</returns>
    private ManagedRecycleResult RecycleRestoredItem(string restoredPath)
    {
        try
        {
            _nativeApi.RecycleItems([restoredPath], IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // Documented never to throw; a test double or future implementation that does must still not take the
            // UI thread down. What still exists afterwards is the authoritative answer either way.
            Log.Error("PanelItemStorageService", "RecycleItemsThrew", ex, new KeyValuePair<string, object?>("count", 1));
        }

        if (!PathExists(restoredPath)) return new ManagedRecycleResult(ManagedRecycleKind.Recycled, null);

        // Cancelled / declined / failed: the user did not get a deletion, so the panel must not lose the item.
        var readopted = Directory.Exists(restoredPath) ? MoveInDirectory(restoredPath) : MoveIn(restoredPath);
        Log.Warn(
            "PanelItemStorageService",
            "RecycleManagedDeclined",
            new KeyValuePair<string, object?>("readopted", readopted is not null));
        return new ManagedRecycleResult(ManagedRecycleKind.Kept, readopted ?? restoredPath);
    }

    /// <summary>
    /// Core of <see cref="Restore(string, bool)"/> that also reports WHERE the item landed — the original path, or
    /// its Explorer-style collision-free variant (<c>name (2).ext</c>). Used by <see cref="RecycleManagedItem"/>,
    /// which recycles the item from exactly there.
    /// </summary>
    /// <param name="storedPath">Full path of the managed item to restore, inside managed storage.</param>
    /// <param name="forceCrossVolume">See <see cref="Restore(string, bool)"/>.</param>
    /// <param name="restoredPath">Receives the item's new path when <see cref="RestoreOutcome.Restored"/> is returned; otherwise <see langword="null"/>.</param>
    /// <returns>The outcome.</returns>
    private RestoreOutcome Restore(string storedPath, bool forceCrossVolume, out string? restoredPath)
    {
        restoredPath = null;
        if (!IsManaged(storedPath)) return RestoreOutcome.NotManaged;

        var fullStored = Path.GetFullPath(storedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // An untrusted manifest read is folded into the same OriginalPathUnknown outcome as "no entry" —
        // Restore has always degraded that way for a legacy item and callers already treat both the same
        // (fall back to ReleaseToRecycleBin).
        var (original, _) = ReadManifestEntry(fullStored);
        if (original is null) return RestoreOutcome.OriginalPathUnknown;

        try
        {
            var targetDir = Path.GetDirectoryName(original);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            var finalTarget = ManagedStorageFileOps.ResolveCollisionFreeName(original);
            var isDirectory = Directory.Exists(fullStored);

            // Cross-volume Move isn't supported by the OS — fall back to the same
            // copy-then-delete-after-verified-success discipline used by MoveInDirectory/MoveIn.
            // Shared by both the real cross-volume path (IOException from Move) and the
            // forceCrossVolume test hook, so the fallback logic itself is never duplicated.
            bool CopyThenDeleteDirectory()
            {
                if (!ManagedStorageFileOps.TryCopyDirectoryVerified(fullStored, finalTarget))
                {
                    ManagedStorageFileOps.TryDeletePartialDirectory(finalTarget);
                    return false;
                }

                DeleteVerifiedCrossVolumeSourceOrWarn(fullStored, finalTarget, isDirectory: true);
                return true;
            }

            bool CopyThenDeleteFile()
            {
                if (!ManagedStorageFileOps.TryCopyFileVerified(fullStored, finalTarget))
                {
                    ManagedStorageFileOps.TryDeletePartialFile(finalTarget);
                    return false;
                }

                DeleteVerifiedCrossVolumeSourceOrWarn(fullStored, finalTarget, isDirectory: false);
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
            restoredPath = finalTarget;
            return RestoreOutcome.Restored;
        }
        catch (Exception ex)
        {
            Log.Error(
                "PanelItemStorageService",
                "RestoreFailed",
                ex,
                new KeyValuePair<string, object?>("path", storedPath));
            return RestoreOutcome.Failed;
        }
    }

    /// <summary>
    /// MINOR 3 (release-polish 1.0 final review): deletes a cross-volume <see cref="Restore(string, bool, out string?)"/>
    /// source whose copy at <paramref name="targetPath"/> has already been verified byte-for-byte — mirrors
    /// <c>PanelItemsUninstallRestorer.DeleteVerifiedSourceOrWarn</c>'s exact discipline. Clears any read-only
    /// attribute first (recursively, for a directory) — a read-only source used to make
    /// <see cref="Directory.Delete(string, bool)"/>/<see cref="File.Delete(string)"/> throw here, which
    /// propagated out to <see cref="Restore(string, bool, out string?)"/>'s own <see langword="catch"/> and
    /// reported <see cref="RestoreOutcome.Failed"/> even though the copy had already succeeded — leaving the
    /// item duplicated at both the original location and managed storage, with the manifest entry never
    /// removed. If the delete still fails after clearing read-only (e.g. the source is open elsewhere), the
    /// copy at <paramref name="targetPath"/> is nonetheless complete and correct: this logs a warning
    /// instead of throwing, so the caller still reports <see cref="RestoreOutcome.Restored"/> — the source
    /// item is left in place under managed storage, but is never counted as lost or duplicated.
    /// </summary>
    /// <param name="sourcePath">The already-copied managed-storage source.</param>
    /// <param name="targetPath">The verified copy destination.</param>
    /// <param name="isDirectory">Whether <paramref name="sourcePath"/> is a directory.</param>
    private void DeleteVerifiedCrossVolumeSourceOrWarn(string sourcePath, string targetPath, bool isDirectory)
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
                "PanelItemStorageService",
                "VerifiedCopySourceDeleteFailed",
                new KeyValuePair<string, object?>("source", sourcePath),
                new KeyValuePair<string, object?>("target", targetPath),
                new KeyValuePair<string, object?>("exception", ex.Message));
        }
    }

    /// <summary>
    /// M8 (release-hardening review): true when <paramref name="fullPath"/> — already normalized the
    /// same way <see cref="IsManaged"/> normalizes its input — is a single managed item: either strictly
    /// BELOW a per-item GUID directory (<c>{storageRoot}{guid}{name}</c>) or a legacy flat-layout FILE
    /// directly under <see cref="_storageRoot"/> (pre-ADR-013 installs still hold those and must stay
    /// deletable). <see cref="IsManaged"/> itself deliberately accepts the storage root
    /// and any of its descendants (other callers rely on that, e.g. <c>IsManaged(_storageRoot)</c> is
    /// <see langword="true"/>), but <see cref="ReleaseToRecycleBin"/>/<see cref="ForgetMovedOut"/> must
    /// reject the root itself and a bare <c>{guid}</c> wrapper directory — recycling/forgetting either
    /// would destroy the storage layout's own structure instead of a single managed item.
    /// </summary>
    private bool IsStrictlyManagedItem(string fullPath)
    {
        var root = Path.GetFullPath(_storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return false;

        var rootWithSep = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return false;

        var relative = fullPath[rootWithSep.Length..];
        var sepIndex = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (sepIndex > 0) return true;
        if (sepIndex == 0) return false;

        // Depth 1: a legacy flat-layout item is a file (or already gone); a bare "{guid}" wrapper is a
        // directory and must never be recycled as if it were an item.
        return !Directory.Exists(fullPath);
    }

    // ── Per-item GUID directory cleanup ─────────────────────────────────────

    /// <summary>
    /// Deletes the per-item GUID directory a moved-in file/folder lived under (see
    /// <see cref="BuildDestinationPath"/>/<see cref="BuildDestinationDirectoryPath"/>) once it's
    /// orphaned by a successful <see cref="Restore(string)"/>/<see cref="ReleaseToRecycleBin"/> — <b>only</b> if
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
            Log.Error(
                "PanelItemStorageService",
                "ItemDirectoryCleanupFailed",
                ex,
                new KeyValuePair<string, object?>("path", fullStoredPath));
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

    /// <summary>
    /// MINOR 2 (release-polish 1.0 final review): reads a manifest entry, distinguishing "no entry
    /// recorded" (<c>Trusted: true</c>, <c>Original: null</c> — a legacy item, or an entry that genuinely
    /// doesn't exist) from "the manifest cache is currently untrusted" (<c>Trusted: false</c> — a transient
    /// disk read failure). The two used to collapse into the same <see langword="null"/> return, which let
    /// <see cref="AdoptCreatedEntries"/> permanently adopt a sibling with no recorded origin during a
    /// transient failure, instead of deferring until the manifest becomes readable again.
    /// </summary>
    /// <param name="managedPath">The manifest key to look up.</param>
    /// <returns>The recorded original path (or <see langword="null"/> for no entry) and whether the read is trustworthy.</returns>
    private (string? Original, bool Trusted) ReadManifestEntry(string managedPath)
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return (null, false);
            return (manifest.TryGetValue(managedPath, out var original) ? original : null, true);
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
    /// Batch counterpart of <see cref="RemoveManifestEntry"/>: removes every given key and writes the
    /// manifest to disk at most once (a multi-item recycle would otherwise rewrite it per item).
    /// </summary>
    private void RemoveManifestEntries(IReadOnlyCollection<string> managedPaths)
    {
        if (managedPaths.Count == 0) return;

        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return;

            var changed = false;
            foreach (var path in managedPaths) changed |= manifest.Remove(path);
            if (changed) WriteManifestUnlocked(manifest);
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
    /// Re-keys the manifest entry of an item that was ALREADY renamed on disk by someone else (a shell
    /// command — see <see cref="ReconcileExternalChange(string, IReadOnlyCollection{string})"/>): <paramref name="oldManagedPath"/>'s entry moves to
    /// <paramref name="newManagedPath"/>, and the recorded original path gets the new file name, exactly as
    /// <see cref="RenameManaged"/> records it. Unlike <see cref="TryRenameManifestEntry"/> this is a single
    /// atomic write — there is no physical move left to fail, so no "add first, remove later" sequencing is
    /// needed.
    /// </summary>
    /// <param name="oldManagedPath">The item's previous managed path (its current manifest key, if any).</param>
    /// <param name="newManagedPath">The item's managed path on disk now.</param>
    /// <returns>
    /// <see langword="true"/> when the manifest now describes <paramref name="newManagedPath"/> (or there was no
    /// entry to move — a legacy item). <see langword="false"/> when the cache is untrusted or the write failed;
    /// the in-memory cache is rolled back to match disk.
    /// </returns>
    private bool TryReKeyManifestEntry(string oldManagedPath, string newManagedPath)
    {
        lock (_manifestLock)
        {
            var (manifest, trusted) = EnsureManifestCacheUnlocked();
            if (!trusted) return false;
            if (!manifest.TryGetValue(oldManagedPath, out var original)) return true;

            var originalDir = Path.GetDirectoryName(original);
            var newName = Path.GetFileName(newManagedPath);
            var newOriginal = string.IsNullOrEmpty(originalDir) ? newName : Path.Combine(originalDir, newName);

            manifest.Remove(oldManagedPath);
            manifest[newManagedPath] = newOriginal;
            if (WriteManifestUnlocked(manifest)) return true;

            manifest.Remove(newManagedPath);
            manifest[oldManagedPath] = original;
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
            Log.Error(
                "PanelItemStorageService",
                "ManifestReadFailed",
                ex,
                new KeyValuePair<string, object?>("path", _manifestPath));
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
            Log.Error(
                "PanelItemStorageService",
                "ManifestWriteFailed",
                ex,
                new KeyValuePair<string, object?>("path", _manifestPath));
            return false;
        }
    }
}
