namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// A dedicated STA thread running a message pump (ADR-046 D-1) that background icon-load jobs are queued
/// onto — some third-party shell icon handlers are STA-only and expect a pumping apartment (outgoing COM
/// calls, hidden windows, timers), which a plain <c>Task.Run</c>/MTA thread cannot provide.
/// </summary>
internal interface IStaWorker : IDisposable
{
    /// <summary>Gets the worker's own managed thread id (diagnostics/tests).</summary>
    int ManagedThreadId { get; }

    /// <summary>Gets a value indicating whether <see cref="Abandon"/> has been called on this worker.</summary>
    bool IsAbandoned { get; }

    /// <summary>
    /// Queues <paramref name="work"/> to run inside the worker's own message pump. Never blocks the
    /// caller. The worker runs at most one queued item at a time: if <paramref name="work"/> itself pumps
    /// (a nested <c>Dispatcher.PushFrame</c>, e.g. from an outgoing COM call re-entering the message loop),
    /// the NEXT queued item must not start on that same nested stack — this non-reentrancy guard is part
    /// of the contract, not left to callers. Items run in FIFO order. An exception escaping
    /// <paramref name="work"/> is swallowed by the worker (it stays alive); callers own their own error
    /// reporting. Callable from any thread.
    /// </summary>
    /// <param name="work">The work to run on the worker thread.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="work"/> was queued. <see langword="false"/> means the worker is
    /// DEAD for good — <see cref="IsAbandoned"/>, disposed, or its message pump ended on its own (a work item
    /// shut the dispatcher down or posted <c>WM_QUIT</c>, or the pump threw): <paramref name="work"/> will never
    /// run, and neither will anything posted later. The caller must dispose this instance, create a new worker
    /// and route the job there (or fail it). Items already accepted but not yet started when the pump dies are
    /// discarded without running, exactly as on <see cref="Abandon"/>.
    /// </returns>
    bool TryPost(Action work);

    /// <summary>
    /// Detaches this worker from a native call it appears stuck in (watchdog timeout): it accepts no more
    /// work from this point on, and items queued but not yet started are discarded (never run). If the
    /// stuck call ever returns, the thread shuts its own message pump down and exits on its own — this call
    /// itself never blocks the caller. Callable from any thread.
    /// </summary>
    /// <remarks>
    /// The item that was executing still runs to completion on the abandoned thread whenever its native
    /// call returns — the caller must recognise and ignore that late completion itself.
    /// </remarks>
    void Abandon();
}
