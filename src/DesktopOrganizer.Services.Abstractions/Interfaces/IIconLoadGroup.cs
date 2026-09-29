namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// One panel's slice of the background icon-load queue (ADR-046) — created through
/// <see cref="IIconLoadQueue.CreateGroup"/>. Every member is a UI-thread API and non-blocking: none of
/// them ever touches the file system or the shell.
/// </summary>
public interface IIconLoadGroup : IDisposable
{
    /// <summary>
    /// Gets or sets the group's current scheduling priority. Setting it re-prioritizes every job of this
    /// group still pending in the queue (already-dispatched jobs are unaffected).
    /// </summary>
    IconLoadPriority Priority { get; set; }

    /// <summary>
    /// Gets the group's current generation, incremented by every <see cref="CancelPending"/> call. Carried
    /// by every <see cref="IconLoadResult"/> this group's requests produce.
    /// </summary>
    int Generation { get; }

    /// <summary>
    /// Submits a batch of icon requests. Never touches the file system or the shell — resolving happens
    /// entirely on the queue's own background worker(s). Requests are served FIFO within the group,
    /// subject to <see cref="Priority"/> and the queue's cross-group round-robin.
    /// </summary>
    /// <param name="requests">The requests to submit, in the order they should be served.</param>
    void Submit(IReadOnlyList<IconLoadRequest> requests);

    /// <summary>
    /// Increments <see cref="Generation"/> and detaches every request submitted before this call: their
    /// in-flight fetch (if any) still runs to completion (another group/generation may still be waiting on
    /// the same underlying fetch), but no result for the old generation is ever delivered to this group.
    /// </summary>
    void CancelPending();
}
