namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// One managed-storage item that no panel references any more (ADR-009 amendment 2026-10-10).
/// </summary>
/// <param name="ManagedPath">Full path of the item inside managed storage (<c>PanelItems\{guid}\{name}</c>).</param>
/// <param name="OriginalPath">The path the manifest remembers it came from (where Restore puts it back).</param>
/// <param name="Name">File or folder name, for display.</param>
/// <param name="IsDirectory">Whether the item is a folder.</param>
/// <param name="StoredAt">When the item was moved into storage (UTC) — best effort, see <see cref="IManagedStorageOrphanScanner"/>.</param>
public sealed record ManagedStorageOrphan(string ManagedPath, string OriginalPath, string Name, bool IsDirectory, DateTime StoredAt);

/// <summary>
/// Finds managed-storage items that are recorded in the manifest and still exist on disk, yet are referenced
/// by no panel (e.g. a panel deleted in a read-only session, a failed restore, an old incident) — such items
/// are invisible to the user otherwise. Read-only: never deletes, moves or rewrites anything; returning an
/// item to its original place is the explicit user action <see cref="IPanelItemStorageService.Restore"/>.
/// </summary>
public interface IManagedStorageOrphanScanner
{
    /// <summary>
    /// Scans the manifest. An item counts as referenced — and is therefore NOT reported — when any of
    /// <paramref name="referencedPaths"/> equals its managed path, lies inside it, or equals its recorded
    /// original path (a stale panel/snapshot entry that startup stale-path healing would re-bind to the item).
    /// </summary>
    /// <param name="referencedPaths">Every path the live panels and the layout snapshots list (managed or not).</param>
    /// <param name="cancellationToken">Cancels the scan (<see cref="OperationCanceledException"/>).</param>
    /// <returns>Orphans, newest first. Empty when the manifest is unreadable or has none.</returns>
    IReadOnlyList<ManagedStorageOrphan> Scan(IReadOnlyCollection<string> referencedPaths, CancellationToken cancellationToken = default);
}
