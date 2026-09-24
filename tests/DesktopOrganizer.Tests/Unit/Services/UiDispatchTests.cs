using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Regression tests for <see cref="UiDispatch"/> — the guarded background-thread → UI-thread marshal.
/// </summary>
/// <remarks>
/// <para>
/// These pin the exact defect behind an intermittent <c>Test Run Aborted</c> (a full
/// <c>dotnet test</c> run dying with ZERO failing tests, then passing on the next attempt). The crash
/// stack, captured with <c>--blame-crash</c>, was:
/// </para>
/// <code>
/// Unhandled exception. System.Threading.Tasks.TaskCanceledException: A task was canceled.
///    at System.Windows.Threading.DispatcherOperation.Wait(TimeSpan timeout)
///    at System.Windows.Threading.Dispatcher.Invoke(Action callback)
///    at DesktopOrganizer.UI.ViewModels.ChildWindowViewModel.RefreshExistence(Object state)
///    at System.Threading.TimerQueueTimer.Fire(Boolean isThreadPool)
///    at System.Threading.ThreadPoolWorkQueue.Dispatch()
/// </code>
/// <para>
/// i.e. a 3-second liveness tick marshalling onto an <see cref="System.Windows.Application.Current"/>
/// whose dispatcher had already been shut down. <see cref="ShutDownDispatcher_RawInvoke_Throws"/> is the
/// characterization half (it proves WPF really does rethrow at the background caller, so the guard is
/// not cargo cult) and <see cref="TryInvoke_OnAShutDownDispatcher_DropsTheCallbackInsteadOfThrowing"/>
/// is the regression half.
/// </para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class UiDispatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void TryInvoke_OnALiveDispatcher_RunsTheCallbackOnThatDispatchersThread()
    {
        using var host = new PumpingDispatcherHost();

        var ranOn = 0;
        var dispatched = UiDispatch.TryInvoke(host.Dispatcher, () => ranOn = Environment.CurrentManagedThreadId);

        Assert.True(dispatched);
        Assert.Equal(host.ThreadId, ranOn);
    }

    [Fact]
    public void TryInvoke_WithNoDispatcherAtAll_DropsTheCallbackInsteadOfThrowing()
    {
        // Application.Current is null in a headless host, so the caller passes null — the pre-existing
        // `app?.Dispatcher.Invoke(...)` null-conditional behaviour, now explicit and observable.
        var ran = false;

        var dispatched = UiDispatch.TryInvoke(null, () => ran = true);

        Assert.False(dispatched);
        Assert.False(ran);
    }

    /// <summary>
    /// Characterization of the ROOT CAUSE, not of our own code: an unguarded
    /// <see cref="Dispatcher.Invoke(Action)"/> onto a shut-down dispatcher does not silently no-op —
    /// it rethrows the aborted operation's cancellation at the calling thread. On a thread-pool timer
    /// thread that is an unhandled exception, which terminates the process.
    /// </summary>
    [Fact]
    public void ShutDownDispatcher_RawInvoke_Throws()
    {
        using var host = new PumpingDispatcherHost();
        host.Shutdown();

        Assert.ThrowsAny<OperationCanceledException>(() => host.Dispatcher.Invoke(() => { }));
    }

    [Fact]
    public void TryInvoke_OnAShutDownDispatcher_DropsTheCallbackInsteadOfThrowing()
    {
        using var host = new PumpingDispatcherHost();
        host.Shutdown();
        var ran = false;

        var dispatched = UiDispatch.TryInvoke(host.Dispatcher, () => ran = true);

        Assert.False(dispatched);
        Assert.False(ran);
    }

    [Fact]
    public void TryInvoke_WhenTheCallbackItselfThrows_StillPropagates()
    {
        // The guard must swallow ONLY the dispatcher's own shutdown cancellation. A genuine bug inside
        // the marshalled work has to keep surfacing, or this helper would turn every UI-thread failure
        // into an invisible no-op.
        using var host = new PumpingDispatcherHost();

        Assert.Throws<InvalidOperationException>(
            () => UiDispatch.TryInvoke(host.Dispatcher, () => throw new InvalidOperationException("boom")));
    }

    /// <summary>
    /// MINOR fix regression (P08 fix cycle 2): the shutdown-race catch in <see cref="UiDispatch.TryInvoke"/>
    /// must be scoped to the dispatcher actually being mid-/post-shutdown, not to the exception TYPE
    /// alone. Before the <see langword="when"/> filter was added, a callback that legitimately throws its
    /// OWN <see cref="OperationCanceledException"/> on a perfectly healthy, still-pumping dispatcher would
    /// have been silently swallowed and reported as "dispatched successfully" — indistinguishable from the
    /// unrelated shutdown race the catch exists for.
    /// </summary>
    /// <remarks>
    /// <see cref="Assert.ThrowsAny{T}"/>, not <see cref="Assert.Throws{T}"/>: WPF's own
    /// <c>DispatcherOperation.Wait</c> re-surfaces ANY <see cref="OperationCanceledException"/> escaping
    /// the invoked delegate as a <see cref="TaskCanceledException"/> — the exact same normalization the
    /// shutdown-race path relies on (see <see cref="ShutDownDispatcher_RawInvoke_Throws"/>) — so WPF itself
    /// gives no way to tell these two cases apart by exception type/instance alone. That is precisely why
    /// the fix keys off <see cref="Dispatcher.HasShutdownStarted"/>/<see cref="Dispatcher.HasShutdownFinished"/>
    /// instead: this dispatcher is still fully alive throughout the test, so the <see langword="when"/>
    /// filter must evaluate <see langword="false"/> and let the (wrapped) exception propagate regardless
    /// of its runtime type.
    /// </remarks>
    [Fact]
    public void TryInvoke_WhenTheCallbackThrowsOperationCanceledOnALiveDispatcher_StillPropagates()
    {
        using var host = new PumpingDispatcherHost();

        Assert.ThrowsAny<OperationCanceledException>(
            () => UiDispatch.TryInvoke(host.Dispatcher, () => throw new OperationCanceledException("not a shutdown race")));
    }

    // ── TryBeginInvoke — the fire-and-forget marshal (P08 fix cycle 2, C-1/M-1/M-3) ─────────────────

    [Fact]
    public void TryBeginInvoke_OnALiveDispatcher_EventuallyRunsTheCallbackOnThatDispatchersThread()
    {
        using var host = new PumpingDispatcherHost();
        using var ran = new ManualResetEventSlim(initialState: false);
        var ranOn = 0;

        var dispatched = UiDispatch.TryBeginInvoke(host.Dispatcher, () =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            ran.Set();
        });

        Assert.True(dispatched);
        Assert.True(ran.Wait(Timeout), "the queued callback must eventually run on the dispatcher's own thread.");
        Assert.Equal(host.ThreadId, ranOn);
    }

    [Fact]
    public void TryBeginInvoke_WithNoDispatcherAtAll_DropsTheCallbackInsteadOfThrowing()
    {
        var ran = false;

        var dispatched = UiDispatch.TryBeginInvoke(null, () => ran = true);

        Assert.False(dispatched);
        Assert.False(ran);
    }

    /// <summary>
    /// Mirrors <see cref="ShutDownDispatcher_RawInvoke_Throws"/>'s point for the fire-and-forget marshal:
    /// per <c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s comparison table, an unguarded
    /// <c>BeginInvoke</c> on a shut-down dispatcher already degrades to a dropped, <c>Aborted</c>
    /// operation instead of throwing — so <see cref="UiDispatch.TryBeginInvoke"/>'s job here is purely the
    /// cheap early-exit (no exception to guard against either way), unlike <see cref="TryInvoke"/>.
    /// </summary>
    [Fact]
    public void TryBeginInvoke_OnAShutDownDispatcher_DropsTheCallbackInsteadOfThrowing()
    {
        using var host = new PumpingDispatcherHost();
        host.Shutdown();
        var ran = false;

        var exception = Record.Exception(() => UiDispatch.TryBeginInvoke(host.Dispatcher, () => ran = true));

        Assert.Null(exception);
        Assert.False(ran);
    }

    /// <summary>
    /// CRITICAL C-1 / MAJOR M-3 regression, at the mechanism level: the whole reason
    /// <see cref="UiDispatch.TryBeginInvoke"/> exists is that the CALLER never waits for the target
    /// dispatcher, even while that dispatcher is busy servicing something else entirely. This is what
    /// makes it safe to call from a background thread that might itself be holding a lock the UI thread's
    /// NEXT action needs (<c>ChildWindowViewModel.OnFolderChanged</c> / <c>FolderWatcher</c>'s debounce
    /// lock), or that a bounded shutdown <c>Thread.Join</c> is waiting on (<c>App</c>'s activation wiring /
    /// <c>NamedPipeActivationServer.Stop()</c>) — see <see cref="UiDispatch.TryBeginInvoke"/>'s own remarks
    /// for both call sites. A blocking <see cref="TryInvoke"/> in the same setup would not return until the
    /// busy operation below is released — proving this test would actually fail against the pre-fix
    /// (<see cref="TryInvoke"/>-based) wiring.
    /// </summary>
    [Fact]
    public void TryBeginInvoke_ReturnsImmediately_EvenWhileTheDispatcherIsBusyWithAnEarlierOperation()
    {
        using var host = new PumpingDispatcherHost();
        using var busyStarted = new ManualResetEventSlim(initialState: false);
        using var releaseBusyOperation = new ManualResetEventSlim(initialState: false);

        // Occupies the dispatcher's queue with an operation that will not return until this test
        // explicitly releases it — simulating a busy/blocked UI thread.
        host.Dispatcher.BeginInvoke(() =>
        {
            busyStarted.Set();
            releaseBusyOperation.Wait(Timeout);
        });

        Assert.True(busyStarted.Wait(Timeout), "the dispatcher must actually be busy before this test proceeds.");

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var dispatched = UiDispatch.TryBeginInvoke(host.Dispatcher, () => { });
            stopwatch.Stop();

            Assert.True(dispatched);
            Assert.True(
                stopwatch.ElapsedMilliseconds < 1000,
                $"TryBeginInvoke must return immediately regardless of dispatcher business, took {stopwatch.ElapsedMilliseconds}ms.");
        }
        finally
        {
            releaseBusyOperation.Set();
        }
    }

    /// <summary>
    /// A private STA thread running a real <see cref="Dispatcher.Run"/> message pump, so
    /// <see cref="Dispatcher.InvokeShutdown"/> requested from the test thread can actually be serviced
    /// (on a non-pumping dispatcher that call would block forever).
    /// </summary>
    private sealed class PumpingDispatcherHost : IDisposable
    {
        private readonly Thread _thread;
        private bool _shutdown;

        public PumpingDispatcherHost()
        {
            using var ready = new ManualResetEventSlim(initialState: false);
            Dispatcher? dispatcher = null;

            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!ready.Wait(Timeout))
                throw new TimeoutException($"{nameof(PumpingDispatcherHost)}: the STA thread never reported its dispatcher.");

            Dispatcher = dispatcher!;
            ThreadId = _thread.ManagedThreadId;
        }

        public Dispatcher Dispatcher { get; }

        public int ThreadId { get; }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;

            // Blocks until the pump above has processed the shutdown request, so HasShutdownFinished is
            // already set by the time this returns — exactly the state the crashing production callback
            // found Application.Current.Dispatcher in.
            Dispatcher.InvokeShutdown();
            _thread.Join(Timeout);
        }

        public void Dispose() => Shutdown();
    }
}
