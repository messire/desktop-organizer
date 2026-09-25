using System.Windows.Threading;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Marshals a callback onto a WPF UI thread <em>from a background thread</em> (a
/// <see cref="System.Threading.Timer"/> tick, a <see cref="System.IO.FileSystemWatcher"/> callback, the
/// single-instance named-pipe accept thread, …) in a way that can never terminate the process.
/// </summary>
/// <remarks>
/// <para>
/// Two marshals, two different guarantees: <see cref="TryInvoke"/> blocks the caller until the work has
/// run (or been dropped), <see cref="TryBeginInvoke"/> never blocks the caller at all. Prefer
/// <see cref="TryBeginInvoke"/> by default — see its own remarks for the deadlock/shutdown-delay class of
/// bug that made blocking the wrong default for every current call site (P08 fix cycle 2).
/// </para>
/// <para>
/// Why this exists — <see cref="Dispatcher.Invoke(Action)"/> is NOT safe to call unguarded once its
/// target dispatcher has shut down. <c>Dispatcher.InvokeAsyncImpl</c> refuses to enqueue onto a
/// dispatcher whose <see cref="Dispatcher.HasShutdownFinished"/> is set: it marks the
/// <see cref="DispatcherOperation"/> <c>Aborted</c> and cancels its task source, and the synchronous
/// overload's <c>DispatcherOperation.Wait</c> then surfaces that cancellation as a
/// <see cref="System.Threading.Tasks.TaskCanceledException"/> thrown back at the CALLER. On the WPF
/// dispatcher thread an escaping exception merely reaches
/// <see cref="Dispatcher.UnhandledException"/>; on a thread-pool/timer thread there is no such hook —
/// .NET treats it as an unhandled exception and kills the whole process. That is exactly how a
/// 3-second <c>ChildWindowViewModel</c> liveness tick arriving after application (or, in the test
/// host, harness) shutdown turned into an intermittent "Test Run Aborted" with no failing test.
/// </para>
/// <para>
/// The guard is deliberately two-layered: the cheap
/// <see cref="Dispatcher.HasShutdownStarted"/>/<see cref="Dispatcher.HasShutdownFinished"/> probe
/// handles the common "already gone" case without paying for an exception, and the
/// <see cref="OperationCanceledException"/> catch closes the unavoidable race where shutdown begins
/// between that probe and the enqueue. Only the cancellation is swallowed — an exception thrown by
/// <c>action</c> itself still propagates, so this never masks a real UI-side bug.
/// </para>
/// </remarks>
internal static class UiDispatch
{
    /// <summary>
    /// Runs <paramref name="action"/> on <paramref name="dispatcher"/>'s thread, or degrades to a no-op
    /// when there is no live dispatcher left to run it on.
    /// </summary>
    /// <param name="dispatcher">
    /// The target UI dispatcher — typically <c>Application.Current?.Dispatcher</c>, hence nullable:
    /// there is no <c>Application</c> at all in a headless test host, nor after shutdown completes.
    /// </param>
    /// <param name="action">The UI-thread work to run.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="action"/> was dispatched and ran to completion;
    /// <see langword="false"/> when it was dropped because <paramref name="dispatcher"/> was
    /// <see langword="null"/> or shutting down/shut down.
    /// </returns>
    public static bool TryInvoke(Dispatcher? dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return false;

        try
        {
            dispatcher.Invoke(action);
            return true;
        }
        catch (OperationCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // Race only (see remarks): shutdown began between the probe above and the enqueue, so the
            // DispatcherOperation was aborted before running. TaskCanceledException derives from
            // OperationCanceledException, which is what Dispatcher.Invoke actually surfaces here.
            // Nothing ran, so there is nothing to report or retry — the next tick re-polls anyway.
            //
            // MINOR fix (P08 fix cycle 2 UiDispatch review): the `when` filter is load-bearing, not
            // decorative. Without it this catch block would also swallow a GENUINE
            // OperationCanceledException thrown BY <paramref name="action"/> itself (e.g. a cancellation
            // token the caller owns) as if it were a shutdown race, silently turning a real bug/expected
            // cancellation into an invisible no-op. Restricting the catch to the dispatcher actually being
            // mid-/post-shutdown keeps that class of exception propagating exactly like any other
            // exception thrown by <paramref name="action"/> — see this method's own "still propagates"
            // regression test.
            return false;
        }
    }

    /// <summary>
    /// Queues <paramref name="action"/> to run on <paramref name="dispatcher"/>'s thread and returns
    /// immediately — never blocks the calling thread, even while that dispatcher is busy or its owning
    /// thread never pumps at all — or degrades to a no-op when there is no live dispatcher left to queue
    /// onto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fire-and-forget counterpart to <see cref="TryInvoke"/>. Prefer this whenever the calling
    /// background thread has no need to wait for the marshalled work to actually run (the common case for
    /// UI notifications: "a file appeared", "re-check liveness", "an activation arrived") — and
    /// PARTICULARLY whenever the calling thread might itself be holding a lock, or be the one thing a
    /// shutdown sequence is waiting to join, that the UI thread could transitively need in order to
    /// service a blocking <see cref="Dispatcher.Invoke(Action)"/>. Two such call sites motivated this
    /// method (P08 fix cycle 2):
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>ChildWindowViewModel.OnFolderChanged</c> used to call <see cref="TryInvoke"/> from
    /// <c>FolderWatcher</c>'s debounce callback thread. If that callback thread is holding
    /// <c>FolderWatcher</c>'s own debounce lock while the blocking <c>Invoke</c> waits for the UI thread,
    /// and the UI thread's very next action is closing the panel (<c>DetachWatcher</c> →
    /// <c>FolderWatcher.Stop</c> → the SAME lock), the two threads deadlock the whole application.
    /// </description></item>
    /// <item><description>
    /// <c>App</c>'s single-instance activation wiring used to call <see cref="TryInvoke"/> from the
    /// named-pipe accept thread. If an activation arrives while <c>App.OnExit</c> is on the UI thread
    /// inside <c>ISingleInstanceCoordinator.CompleteShutdown</c>'s bounded
    /// <c>NamedPipeActivationServer.Stop().Join(2s)</c> — a plain <see cref="System.Threading.Thread.Join()"/>
    /// that does NOT pump the dispatcher — the accept thread's blocking <c>Invoke</c> cannot be serviced
    /// until that join gives up, needlessly delaying application exit by up to its full 2-second budget.
    /// </description></item>
    /// </list>
    /// <para>
    /// Unlike <see cref="TryInvoke"/>, no <see langword="try"/>/<see langword="catch"/> is needed here:
    /// per <c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s own comparison table,
    /// <see cref="Dispatcher.BeginInvoke(DispatcherPriority, Delegate)"/> on an already shut-down
    /// dispatcher marks the operation <c>Aborted</c> and returns normally — it never throws back at the
    /// caller the way the synchronous <see cref="Dispatcher.Invoke(Action)"/> does.
    /// </para>
    /// </remarks>
    /// <param name="dispatcher">
    /// The target UI dispatcher — typically <c>Application.Current?.Dispatcher</c>, hence nullable.
    /// </param>
    /// <param name="action">The UI-thread work to queue.</param>
    /// <param name="priority">
    /// The <see cref="DispatcherPriority"/> to queue at. Defaults to <see cref="DispatcherPriority.Normal"/>,
    /// matching <see cref="TryInvoke"/>'s own (fixed) priority.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="action"/> was successfully queued (this says nothing
    /// about whether it has run, or ever will — a dispatcher that never pumps simply never services it);
    /// <see langword="false"/> when it was dropped because <paramref name="dispatcher"/> was
    /// <see langword="null"/> or already shutting down/shut down.
    /// </returns>
    public static bool TryBeginInvoke(Dispatcher? dispatcher, Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return false;

        dispatcher.BeginInvoke(priority, action);
        return true;
    }
}
