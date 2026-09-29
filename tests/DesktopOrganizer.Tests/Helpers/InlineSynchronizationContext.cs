namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// A <see cref="SynchronizationContext"/> whose <see cref="Post"/> runs the callback IMMEDIATELY, on the
/// calling thread — the opposite of <see cref="CollectingSynchronizationContext"/>. Used to reproduce a
/// caller whose delivery context re-enters the queue synchronously from inside <c>onResults</c> (M5:
/// <c>StaIconLoadQueue</c> must never hold its own internal lock while calling
/// <see cref="SynchronizationContext.Post"/>, or such a reentrant call would run its own bookkeeping
/// mid-way through the outer call's).
/// </summary>
internal sealed class InlineSynchronizationContext : SynchronizationContext
{
    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state) => d(state);

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state) => d(state);
}
