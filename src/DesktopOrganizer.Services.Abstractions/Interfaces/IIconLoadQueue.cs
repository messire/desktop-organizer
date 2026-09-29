namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Background icon-load queue (ADR-046): resolves <see cref="IconLoadRequest"/>s submitted through an
/// <see cref="IIconLoadGroup"/> off the UI thread, and delivers <see cref="IconLoadResult"/>s back to the
/// UI thread in batches. Registered as a DI singleton; see <see cref="IDisposable"/> for shutdown.
/// </summary>
public interface IIconLoadQueue : IDisposable
{
    /// <summary>
    /// Creates a new group — one panel's slice of the queue.
    /// </summary>
    /// <param name="priority">The group's initial scheduling priority.</param>
    /// <param name="deliveryContext">
    /// The <see cref="SynchronizationContext"/> results are delivered to (the caller's UI thread). Captured
    /// once, by reference; results are always <see cref="SynchronizationContext.Post"/>ed, never
    /// <see cref="SynchronizationContext.Send"/>.
    /// </param>
    /// <param name="onResults">
    /// Invoked on <paramref name="deliveryContext"/> with a batch (at most the queue's configured delivery
    /// batch size) of results carrying the group's current generation at the time each request was
    /// submitted. Never invoked after the group is disposed.
    /// </param>
    /// <returns>The new group.</returns>
    IIconLoadGroup CreateGroup(
        IconLoadPriority priority,
        SynchronizationContext deliveryContext,
        Action<IReadOnlyList<IconLoadResult>> onResults);
}
