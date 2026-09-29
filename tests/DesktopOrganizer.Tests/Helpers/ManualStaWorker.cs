using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Fake <see cref="IStaWorker"/> for <c>StaIconLoadQueueTests</c> (ADR-046): posted work accumulates in a
/// FIFO queue instead of running on a real thread — the test decides exactly when (and whether) it runs by
/// calling <see cref="RunNext"/>/<see cref="RunAll"/>. Because <c>StaIconLoadQueue</c> dequeues a job and
/// marks it as the lane's current job (the watchdog's target) BEFORE ever posting to the worker, a "stuck"
/// job needs no special handling here at all: simply never call <see cref="RunNext"/> for it — see
/// <see cref="SimulateStuck"/>'s remarks.
/// </summary>
internal sealed class ManualStaWorker : IStaWorker
{
    private readonly Queue<Action> _pending = new();
    private bool _stuck;

    /// <inheritdoc />
    public int ManagedThreadId { get; } = Environment.CurrentManagedThreadId;

    /// <inheritdoc />
    public bool IsAbandoned { get; private set; }

    /// <summary>Gets a value indicating whether <see cref="IDisposable.Dispose"/> was called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gets how many work items are currently queued, unrun.</summary>
    public int PendingCount => _pending.Count;

    /// <inheritdoc />
    public bool TryPost(Action work)
    {
        if (IsAbandoned) return false;
        _pending.Enqueue(work);
        return true;
    }

    /// <inheritdoc />
    public void Abandon() => IsAbandoned = true;

    /// <inheritdoc />
    public void Dispose() => IsDisposed = true;

    /// <summary>
    /// Marks this worker as "stuck in a native call" for test readability — purely documentation/misuse
    /// guard, not required for a watchdog test to pass. <c>StaIconLoadQueue</c> marks a job as the lane's
    /// current job (and starts its clock) at dispatch time, before ever calling <see cref="TryPost"/> — so
    /// simulating "the worker never comes back" is just a matter of the test never calling
    /// <see cref="RunNext"/> for that posted item at all. Calling <see cref="RunNext"/> after this throws,
    /// to catch a test author accidentally "unsticking" a job the test meant to leave hanging.
    /// </summary>
    public void SimulateStuck() => _stuck = true;

    /// <summary>Runs the next queued work item on the calling (test) thread. Returns <see langword="false"/> when nothing is queued.</summary>
    public bool RunNext()
    {
        if (_stuck) throw new InvalidOperationException($"{nameof(ManualStaWorker)}.{nameof(RunNext)} called after {nameof(SimulateStuck)} — see its remarks.");
        if (_pending.Count == 0) return false;

        _pending.Dequeue()();
        return true;
    }

    /// <summary>Runs every currently queued work item, including ones queued by an earlier item in this same call (e.g. a lane dispatching its next job).</summary>
    /// <returns>How many items ran.</returns>
    public int RunAll()
    {
        var count = 0;
        while (RunNext()) count++;
        return count;
    }
}
