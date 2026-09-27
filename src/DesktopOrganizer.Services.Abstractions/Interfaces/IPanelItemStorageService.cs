namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Outcome of a <see cref="IPanelItemStorageService.Restore"/> attempt.
/// </summary>
public enum RestoreOutcome
{
    /// <summary>The managed item was moved back to its original location and the manifest entry was removed.</summary>
    Restored,

    /// <summary>
    /// <c>storedPath</c> (see <see cref="IPanelItemStorageService.Restore"/>) is inside managed
    /// storage, but no original-path manifest entry exists for it (e.g. a legacy item imported before Move
    /// semantics existed). The managed copy is left untouched — callers should degrade to
    /// <see cref="IPanelItemStorageService.ReleaseToRecycleBin"/> for legacy behavior.
    /// </summary>
    OriginalPathUnknown,

    /// <summary>The given path isn't inside managed storage at all — nothing to restore, no-op.</summary>
    NotManaged,

    /// <summary>
    /// A filesystem operation failed while restoring (e.g. couldn't create the parent directory, or the
    /// move itself failed). The managed copy is left untouched — nothing is lost, but the item stays in
    /// managed storage instead of returning to its original location.
    /// </summary>
    Failed,
}

/// <summary>
/// Result of <see cref="IPanelItemStorageService.ReleaseToRecycleBin"/> (ADR-035). Which items were
/// actually recycled is decided by checking, after the shell call, which paths still exist on disk —
/// so this is accurate even when the shell operation only partially succeeded.
/// </summary>
/// <remarks>
/// <c>default(RecycleOutcome)</c> is a valid empty outcome (both lists empty, not cancelled), so a
/// loose test double returning the default value can never cause a <see cref="NullReferenceException"/>.
/// </remarks>
public readonly record struct RecycleOutcome
{
    private readonly IReadOnlyList<string>? _recycled;
    private readonly IReadOnlyList<string>? _remaining;

    private RecycleOutcome(IReadOnlyList<string> recycled, IReadOnlyList<string> remaining, bool cancelled)
    {
        _recycled = recycled;
        _remaining = remaining;
        Cancelled = cancelled;
    }

    /// <summary>
    /// Gets the managed paths (as passed in by the caller) that no longer exist on disk — moved to the
    /// Recycle Bin (or already gone before the call). Their manifest entries have been removed.
    /// </summary>
    public IReadOnlyList<string> Recycled => _recycled ?? [];

    /// <summary>
    /// Gets the managed paths (as passed in by the caller) that still exist on disk after the shell
    /// call — nothing was deleted for them and their manifest entries are intact. Non-empty means the
    /// caller must keep whatever references them (fail visibly, ADR-035).
    /// </summary>
    public IReadOnlyList<string> Remaining => _remaining ?? [];

    /// <summary>
    /// Gets a value indicating whether the shell reported the batch as not fully completed (the user
    /// declined the permanent-delete warning or cancelled the progress dialog, or the shell failed).
    /// Informational only — <see cref="Remaining"/> is the authoritative per-item result.
    /// </summary>
    public bool Cancelled { get; }

    /// <summary>Gets a value indicating whether every requested managed item is gone (<see cref="Remaining"/> is empty).</summary>
    public bool IsComplete => Remaining.Count == 0;

    /// <summary>Creates an outcome.</summary>
    /// <param name="recycled">See <see cref="Recycled"/>.</param>
    /// <param name="remaining">See <see cref="Remaining"/>.</param>
    /// <param name="cancelled">See <see cref="Cancelled"/>.</param>
    /// <returns>The outcome.</returns>
    public static RecycleOutcome Create(IReadOnlyList<string> recycled, IReadOnlyList<string> remaining, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(recycled);
        ArgumentNullException.ThrowIfNull(remaining);
        return new RecycleOutcome(recycled, remaining, cancelled);
    }
}

/// <summary>
/// Owns a private, per-app storage folder that panels move dropped files into, so a panel item
/// stops depending on the file's original location. This is what makes "drag a desktop shortcut
/// into a panel" behave like a real move — the desktop is decluttered immediately, and the panel
/// holds its own independent copy rather than a reference to a file the user might delete later.
/// </summary>
/// <remarks>
/// Folder Portals (<c>SourceFolderPath</c>) are unaffected entirely: they intentionally mirror a
/// live folder and must keep pointing at the real files, never through this service.
/// </remarks>
public interface IPanelItemStorageService
{
    /// <summary>
    /// Moves <paramref name="sourcePath"/> into the app-managed storage folder under a
    /// collision-safe generated name and returns the new path. Same-volume moves are an atomic
    /// <see cref="System.IO.File.Move(string, string)"/>. Cross-volume moves copy first, verify the
    /// copy succeeded, and only then delete the source — the source is <b>never</b> deleted unless
    /// the copy is confirmed complete. Returns <see langword="null"/> (never throws) if the source
    /// is missing, is a directory, or the operation fails for any reason (locked file, permissions,
    /// disk full) — the source is left untouched in that case and callers should fall back to
    /// referencing the original path rather than losing the drop entirely. On success, records the
    /// original path so a later <see cref="Restore"/> can move the item back.
    /// </summary>
    /// <param name="sourcePath">The original file path (e.g. a desktop shortcut being dropped).</param>
    string? MoveIn(string sourcePath);

    /// <summary>
    /// Recursively moves a dropped folder into the app-managed storage folder (preserving its
    /// internal structure) under a collision-safe generated name, and returns the new root path.
    /// Same-volume moves are an atomic <see cref="System.IO.Directory.Move(string, string)"/> —
    /// no safety cap applies, since nothing is actually copied. Cross-volume moves are subject to a
    /// safety cap (very large folders are left as a live reference instead — copying them would be
    /// slow and bloat disk usage); when under the cap, the folder is copied recursively, verified,
    /// and only then is the source deleted. Returns <see langword="null"/> (never throws) if the
    /// source is missing, isn't a directory, exceeds the safety cap on a cross-volume move, or the
    /// operation fails for any reason — the source is left fully intact in that case and callers
    /// should fall back to referencing the original path. On success, records the original path so
    /// a later <see cref="Restore"/> can move the item back.
    /// </summary>
    /// <param name="sourcePath">The original folder path.</param>
    string? MoveInDirectory(string sourcePath);

    /// <summary>
    /// Moves a previously moved-in item (<see cref="MoveIn"/>/<see cref="MoveInDirectory"/>) back to
    /// its original location, resolving a name collision at the destination if needed (Explorer-style
    /// <c>name (2).ext</c>) and recreating missing parent directories. Removes the manifest entry only
    /// after the move back succeeds. Never deletes or loses the managed copy: if anything goes wrong,
    /// the managed copy is left exactly as it was and a non-<see cref="RestoreOutcome.Restored"/>
    /// outcome is returned for the caller to handle.
    /// </summary>
    /// <param name="storedPath">A path previously returned by <see cref="MoveIn"/> or <see cref="MoveInDirectory"/>.</param>
    RestoreOutcome Restore(string storedPath);

    /// <summary>
    /// Moves previously moved-in files/folders (<see cref="MoveIn"/>/<see cref="MoveInDirectory"/>)
    /// to the Windows Recycle Bin in a single shell batch (ADR-035, amends ADR-010) — the only way this
    /// service ever gets rid of a managed item's content. Paths that aren't inside managed storage are
    /// ignored entirely (never touched, never reported). There is <b>no</b> fallback to a permanent
    /// delete: an item the shell couldn't recycle stays on disk, its manifest entry stays intact, and
    /// it is reported in <see cref="RecycleOutcome.Remaining"/>. Manifest entries are removed (and the
    /// now-empty per-item GUID directory cleaned up) only for items reported in
    /// <see cref="RecycleOutcome.Recycled"/>. Never throws. Must be called on the UI (STA) thread —
    /// the shell may show its own dialog (e.g. "this item is too big for the Recycle Bin").
    /// </summary>
    /// <param name="storedPaths">Paths previously returned by <see cref="MoveIn"/>/<see cref="MoveInDirectory"/> (unmanaged entries are ignored).</param>
    /// <returns>Which of the managed paths are gone and which are still on disk.</returns>
    RecycleOutcome ReleaseToRecycleBin(IReadOnlyList<string> storedPaths);

    /// <summary>
    /// True if <paramref name="path"/> is inside the app-managed storage folder, i.e. it was
    /// produced by <see cref="MoveIn"/>/<see cref="MoveInDirectory"/> rather than pointing at a
    /// user-owned location.
    /// </summary>
    /// <param name="path">The path to check.</param>
    bool IsManaged(string path);

    /// <summary>
    /// Renames a previously moved-in file or folder (<see cref="MoveIn"/>/<see cref="MoveInDirectory"/>)
    /// to <paramref name="newName"/> (a bare name — no directory component), keeping it inside its
    /// existing per-item storage directory. A legacy flat-layout item (moved in before per-item
    /// directories existed — see <see cref="MoveIn"/> remarks on storage layout) has no per-item
    /// directory of its own to rename within, so instead it is relocated into a brand-new one,
    /// curing the legacy layout for good.
    /// </summary>
    /// <remarks>
    /// <b>Invariant:</b> the manifest is always rewritten <i>before</i> the file/folder is physically
    /// moved — never after. <paramref name="storedPath"/>'s manifest entry (managed path → original
    /// path) is re-keyed to the new managed path, and the recorded original path has its own file
    /// name portion replaced with <paramref name="newName"/> (its directory portion is unchanged),
    /// so a later <see cref="Restore"/> puts the item back under its new name. If the manifest
    /// cache is currently untrusted, or the write-through to disk fails, the rename is aborted
    /// <i>before</i> any file-system mutation — the source is left completely untouched and
    /// <see langword="null"/> is returned. This mirrors the same "manifest before destructive step"
    /// discipline <see cref="MoveIn"/>/<see cref="MoveInDirectory"/> already use: losing the origin
    /// mapping would silently turn a future <see cref="Restore"/> into data loss.
    /// </remarks>
    /// <param name="storedPath">A path previously returned by <see cref="MoveIn"/>, <see cref="MoveInDirectory"/>, or this method.</param>
    /// <param name="newName">The desired new bare name (including extension for files).</param>
    /// <returns>
    /// A <see cref="RenameResult"/> describing what happened. Never throws. On any outcome other
    /// than <see cref="RenameOutcome.Renamed"/> the original file/folder is left exactly as it was:
    /// <see cref="RenameOutcome.InvalidName"/> when <paramref name="newName"/> is empty/whitespace or
    /// smuggles a path separator (defense in depth — real callers go through
    /// <see cref="IFileItemRenameService.Rename"/>, which already validates <paramref name="newName"/>
    /// before ever reaching here); <see cref="RenameOutcome.NotFound"/> when <paramref name="storedPath"/>
    /// doesn't exist on disk; <see cref="RenameOutcome.NameTaken"/> when a file/folder already exists
    /// at the resolved destination; <see cref="RenameOutcome.Failed"/> for anything else (not managed,
    /// the manifest cache is untrusted, the manifest write failed, or the physical move itself
    /// failed).
    /// </returns>
    RenameResult RenameManaged(string storedPath, string newName);

    /// <summary>
    /// Read-only lookup of the managed copy for a stale <paramref name="originalPath"/> — the reverse
    /// direction of the manifest from <see cref="Restore"/> (managed path → original path). Added for
    /// ADR-009 Amendment 2026-09-22: a panel's persisted <c>FilePaths</c> can end up pointing at a
    /// pre-Move original location (e.g. a legacy-imported <c>user.config</c> snapshot recorded before
    /// Move semantics existed, or a restored layout snapshot captured before a later Move) for an item
    /// that was, in fact, already moved into managed storage under a different, still-live path.
    /// Performs no writes of any kind — the manifest and the file system are only ever read.
    /// </summary>
    /// <param name="originalPath">The (possibly stale) original path recorded in a panel's saved state.</param>
    /// <param name="managedPath">
    /// The managed path currently on disk for <paramref name="originalPath"/>, or <see langword="null"/>
    /// if no manifest entry maps to it, or the recorded managed path no longer exists on disk (e.g. the
    /// item was since restored or recycled — see <see cref="Restore"/>/<see cref="ReleaseToRecycleBin"/>).
    /// </param>
    /// <returns><see langword="true"/> if a live managed copy was found; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <b>Ambiguity (M1 fix, P08 fix cycle 1, 2026-09-22):</b> the manifest is keyed by managed path,
    /// not by original path — nothing prevents two different manifest entries from recording the same
    /// original path. Delegates to <see cref="FindManagedCandidates"/> and only returns
    /// <see langword="true"/> when exactly one live candidate exists; an ambiguous manifest (2+ live
    /// candidates) returns <see langword="false"/> rather than arbitrarily picking one — the same
    /// "explicit refusal over probabilistic guessing" principle <see cref="RestoreOutcome.OriginalPathUnknown"/>
    /// already applies. This method alone cannot detect the OTHER half of M1 (a candidate that's
    /// unambiguous by manifest but already directly referenced by a different panel's own current
    /// <c>FilePaths</c>) — that requires visibility into every panel's current state at once, which this
    /// service does not have; see <c>WindowManager.ResolveStalePanelPaths</c>, which uses
    /// <see cref="FindManagedCandidates"/> directly for that cross-panel check instead of this method.
    /// </remarks>
    bool TryFindManagedPath(string originalPath, out string? managedPath);

    /// <summary>
    /// Returns every currently-live managed path whose manifest entry records
    /// <paramref name="originalPath"/> as its original location (case-insensitive) — the plural,
    /// disambiguation-aware counterpart to <see cref="TryFindManagedPath"/>. Added for M1 (P08 fix
    /// cycle 1, 2026-09-22): a caller with visibility into multiple panels' current file lists (see
    /// <c>WindowManager.ResolveStalePanelPaths</c>) needs the full candidate set — not just the first
    /// or only one — so it can exclude candidates already claimed by another panel before deciding
    /// whether what remains is unambiguous. Performs no writes of any kind, same as
    /// <see cref="TryFindManagedPath"/>.
    /// </summary>
    /// <param name="originalPath">The (possibly stale) original path recorded in a panel's saved state.</param>
    /// <returns>
    /// Every live managed path recorded against <paramref name="originalPath"/>, in manifest iteration
    /// order (not meaningful/stable — callers must not rely on ordering to break ties). Empty if
    /// <paramref name="originalPath"/> is null/blank/malformed, has no manifest entry, or every
    /// recorded managed path for it has since vanished from disk.
    /// </returns>
    IReadOnlyList<string> FindManagedCandidates(string originalPath);

    /// <summary>
    /// Manifest-only cleanup for a managed item that was moved out of the panel by an external drag
    /// (Explorer/the desktop — ADR-036, release-hardening-1.0.md §E), AFTER the caller has independently
    /// confirmed the path is actually gone (a UI-layer existence-recheck at increasing delays after the
    /// drag completed, since the OLE drop effect an OS-level drag-and-drop call returns is not
    /// trustworthy — Explorer's own optimized move frequently reports "no effect" even on success).
    /// Never deletes a file or folder itself — this method's own job is exactly the reverse of that: it
    /// removes the bookkeeping for a path that is <b>already</b> gone. A no-op if <paramref name="path"/>
    /// is not managed, or if it still exists on disk (the caller's own external-move confirmation is the
    /// only gate this method trusts — a still-existing path is always left completely untouched,
    /// manifest included).
    /// </summary>
    /// <param name="path">A path previously returned by <see cref="MoveIn"/>/<see cref="MoveInDirectory"/>, confirmed by the caller to no longer exist.</param>
    void ForgetMovedOut(string path);
}
