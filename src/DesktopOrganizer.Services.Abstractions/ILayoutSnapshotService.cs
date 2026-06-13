using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Manages named layout snapshots: capture, restore, delete and persist.
/// </summary>
public interface ILayoutSnapshotService
{
    /// <summary>
    /// Read-only ordered list of all stored snapshots (newest first).
    /// </summary>
    IReadOnlyList<LayoutSnapshot> Snapshots { get; }

    /// <summary>
    /// Captures the current desktop layout under the given name.
    /// If the snapshot limit (10) is reached the oldest snapshot is deleted first.
    /// </summary>
    /// <param name="name">Display name for the snapshot.</param>
    /// <returns>The newly created snapshot.</returns>
    LayoutSnapshot Capture(string name);

    /// <summary>
    /// Replaces the current desktop layout with the panels stored in the specified snapshot.
    /// All current panels are removed before the snapshot panels are recreated.
    /// </summary>
    /// <param name="snapshotId">Identifier of the snapshot to restore.</param>
    void Restore(string snapshotId);

    /// <summary>
    /// Permanently deletes the snapshot with the given identifier.
    /// </summary>
    /// <param name="snapshotId">Identifier of the snapshot to delete.</param>
    void Delete(string snapshotId);

    /// <summary>
    /// Persists the current snapshot list to storage.
    /// </summary>
    void Save();

    /// <summary>
    /// Loads the snapshot list from storage.
    /// </summary>
    void Load();
}
