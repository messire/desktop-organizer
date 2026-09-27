namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// What <see cref="IPanelItemStorageService.ReconcileExternalChange(string)"/> concluded about a managed item that
/// a shell command may have changed behind the storage service's back.
/// </summary>
public enum ManagedItemReconcileKind
{
    /// <summary>
    /// The item itself was not renamed or deleted: it still exists, is not a per-item managed item, or what is
    /// left in its per-item directory cannot be attributed to a single rename. Its manifest entry was not
    /// touched. Entries a shell command CREATED next to a still-existing item may have been adopted as new
    /// managed items — see <see cref="ManagedItemReconcileResult.AdoptedPaths"/>.
    /// </summary>
    Unchanged,

    /// <summary>
    /// The item vanished and exactly one entry that was NOT in the caller's before-command snapshot appeared in
    /// its per-item directory — the item was renamed in place. The manifest entry was re-keyed to
    /// <see cref="ManagedItemReconcileResult.NewPath"/>. Never concluded without a snapshot.
    /// </summary>
    Renamed,

    /// <summary>
    /// The item's per-item directory is empty or gone — the item was deleted (or moved away). Its manifest
    /// entry was released and the empty per-item directory removed.
    /// </summary>
    Deleted,
}

/// <summary>
/// Result of <see cref="IPanelItemStorageService.ReconcileExternalChange(string)"/> /
/// <see cref="IPanelItemStorageService.ReconcileExternalChange(string, IReadOnlyCollection{string})"/>.
/// </summary>
/// <param name="Kind">What was concluded.</param>
/// <param name="NewPath">The item's new managed path when <paramref name="Kind"/> is <see cref="ManagedItemReconcileKind.Renamed"/>; otherwise <see langword="null"/>.</param>
public readonly record struct ManagedItemReconcileResult(ManagedItemReconcileKind Kind, string? NewPath)
{
    private readonly IReadOnlyList<string>? _adoptedPaths;

    /// <summary>
    /// Gets the managed paths of entries a shell command created next to the item (e.g. "Create shortcut",
    /// "Send to &gt; Compressed folder", an archiver's "Add to archive") that were adopted as NEW managed items —
    /// each moved into a per-item directory of its own, with the item's original directory + the entry's name
    /// recorded as its original path. The caller shows them as new panel items. Never <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<string> AdoptedPaths
    {
        get => _adoptedPaths ?? [];
        init => _adoptedPaths = value;
    }
}
