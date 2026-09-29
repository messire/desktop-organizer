using System.Runtime.InteropServices;
using System.Windows.Threading;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// A dedicated background STA thread running a WPF <see cref="Dispatcher"/> message pump (ADR-046 D-1,
/// docs/PLANS/background-icon-loading.md §5.2) that <see cref="StaIconLoadQueue"/> posts icon jobs onto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apartment.</b> <see cref="Thread.SetApartmentState"/> before <see cref="Thread.Start()"/> makes the
/// CLR call <c>CoInitializeEx(COINIT_APARTMENTTHREADED)</c> when the thread starts and
/// <c>CoUninitialize</c> when it ends. <c>OleInitialize</c> is not needed: nothing that runs here uses
/// OLE drag-and-drop, the clipboard or in-place activation — only <c>SHGetFileInfo</c>,
/// <c>SHCreateItemFromParsingName</c>/<c>IShellItemImageFactory</c>, <c>SHGetImageList</c>/<c>IImageList</c>
/// and <c>IShellLinkW</c>/<c>IPersistFile</c>, all of which document plain COM initialization.
/// </para>
/// <para>
/// <b>Pump.</b> <see cref="Dispatcher.Run"/>, not a <c>WaitHandle</c> loop: a CLR wait on an STA thread
/// only pumps COM calls and sent messages, so a third-party icon handler's POSTED messages (hidden windows,
/// <c>WM_TIMER</c>) would never be dispatched.
/// </para>
/// <para>
/// <b>Non-reentrancy (contract of <see cref="IStaWorker.TryPost"/>).</b> Work items live in this class's
/// own FIFO, not in the dispatcher's queue; the dispatcher only ever holds a single "drain one item"
/// operation. If an item pumps a nested frame (<c>Dispatcher.PushFrame</c>, or a COM modal loop
/// dispatching our hidden window's messages) and the nested pump reaches that drain operation, the drain
/// sees <see cref="_executing"/> and returns without starting anything; the outer item re-schedules the
/// drain once it has unwound. Nothing spins: the deferred drain is not re-posted from inside the nested
/// pump.
/// </para>
/// <para>
/// <b>Abandon / Dispose.</b> Both stop accepting work, discard every queued-but-unstarted item and post a
/// dispatcher shutdown (never blocking on the worker). A worker stuck inside a native call keeps its thread
/// until that call returns; the pending shutdown then ends <see cref="Dispatcher.Run"/> and the thread
/// exits by itself. <see cref="Dispose"/> additionally joins the thread for a bounded time. The thread is a
/// background thread, so a call that never returns cannot hold the process open.
/// </para>
/// <para>
/// <b>Unexpected pump exit (review M2).</b> The pump can also end WITHOUT <see cref="Abandon"/>/<see cref="Dispose"/>:
/// a work item (or a third-party handler running inside it) shuts the dispatcher down or posts
/// <c>WM_QUIT</c>, or <see cref="Dispatcher.Run"/> itself throws. The thread's own <see langword="finally"/>
/// then marks the worker as stopping and discards whatever was still queued, and every later
/// <see cref="TryPost"/> returns <see langword="false"/>; <see cref="TryPost"/> also refuses up front once the
/// dispatcher has started shutting down, and treats an <see cref="DispatcherOperationStatus.Aborted"/> drain
/// operation as a refusal — so work is never silently accepted by a dead pump. Items accepted in the instant
/// before the pump died are discarded exactly like on <see cref="Abandon"/>.
/// </para>
/// </remarks>
internal sealed class StaWorkerThread : IStaWorker
{
    /// <summary>Default bound for <see cref="Dispose"/>'s <c>Join</c> (plan §5.2.3).</summary>
    internal static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private readonly Thread _thread;
    private readonly TimeSpan _joinTimeout;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guarded by _gate.
    private Dispatcher? _dispatcher;
    private bool _drainScheduled;
    private bool _abandoned;
    private bool _stopping;

    // Worker thread only.
    private bool _executing;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaWorkerThread"/> class and starts its thread. Never
    /// waits for the thread to come up: work posted before the pump is ready is buffered and drained as
    /// soon as it is (plan §5.2.1, "asynchronous start" option) — the caller may be the UI thread.
    /// </summary>
    /// <param name="name">Thread name (diagnostics only).</param>
    public StaWorkerThread(string name)
        : this(name, DefaultJoinTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StaWorkerThread"/> class with an explicit
    /// <see cref="Dispose"/> join bound (tests).
    /// </summary>
    /// <param name="name">Thread name (diagnostics only).</param>
    /// <param name="joinTimeout">How long <see cref="Dispose"/> waits for the thread to exit.</param>
    internal StaWorkerThread(string name, TimeSpan joinTimeout)
    {
        _joinTimeout = joinTimeout;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <inheritdoc />
    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <inheritdoc />
    public bool IsAbandoned
    {
        get
        {
            lock (_gate) return _abandoned;
        }
    }

    /// <summary>
    /// Gets a task that completes once the worker thread has left its message pump and is about to exit
    /// (set from the thread's own <see langword="finally"/>). Test seam: proves an abandoned or disposed
    /// worker really goes away without anyone joining it.
    /// </summary>
    internal Task Completion => _completion.Task;

    /// <inheritdoc />
    public bool TryPost(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Dispatcher? scheduleOn = null;
        lock (_gate)
        {
            if (_abandoned || _stopping) return false;

            // The pump is going away (a work item shut the dispatcher down, WM_QUIT, ...) but Run's finally has
            // not flagged it yet: refuse now instead of queuing behind a drain that will never run (M2).
            if (_dispatcher is { HasShutdownStarted: true })
            {
                _stopping = true;
                _pending.Clear();
                return false;
            }

            _pending.Enqueue(work);
            if (_dispatcher is not null && !_drainScheduled)
            {
                _drainScheduled = true;
                scheduleOn = _dispatcher;
            }
        }

        if (scheduleOn is not null && !TryScheduleDrain(scheduleOn))
        {
            // The dispatcher refused the operation (it is going away). Report it like a stopping worker;
            // the item is dropped with the rest of the queue by the shutdown path.
            lock (_gate) _stopping = true;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Abandon()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            if (_abandoned) return;
            _abandoned = true;
            _pending.Clear();
            dispatcher = _dispatcher;
        }

        // If the thread is idle, this ends its pump right away; if it is stuck in a native call, the
        // shutdown simply waits in the queue until that call returns.
        RequestShutdown(dispatcher);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>BeginInvokeShutdown(Send)</c> + <c>Join(joinTimeout)</c>; never throws. Calling it from the worker
    /// thread itself skips the join (a thread cannot join itself).
    /// </remarks>
    public void Dispose()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            _stopping = true;
            _pending.Clear();
            dispatcher = _dispatcher;
        }

        RequestShutdown(dispatcher);

        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return;

        try
        {
            _thread.Join(_joinTimeout);
        }
        catch (Exception)
        {
            // ThreadStateException (never started) or ThreadInterruptedException: Dispose never throws.
        }
    }

    private static void RequestShutdown(Dispatcher? dispatcher)
    {
        if (dispatcher is null) return; // Not pumping yet: Run() sees the flag and never starts the pump.

        try
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch (Exception)
        {
            // Already shutting down / shut down: nothing left to stop.
        }
    }

    private static void ReleaseApartmentWrappers()
    {
        try
        {
            // R7-7: release, on THIS thread, every RCW that still belongs to this apartment before COM is
            // uninitialized — otherwise a finalizer would later try to reach a dead STA.
            Marshal.CleanupUnusedObjectsInCurrentContext();
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private void Run()
    {
        Dispatcher? dispatcher = null;
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;

            // Belt and braces: Drain already catches everything a work item throws, but an exception
            // escaping Dispatcher.Run on this non-UI thread would terminate the process (C-5).
            dispatcher.UnhandledException += OnDispatcherUnhandledException;

            bool drainNow;
            lock (_gate)
            {
                if (_abandoned || _stopping) return;

                _dispatcher = dispatcher;
                drainNow = _pending.Count > 0;
                _drainScheduled = drainNow;
            }

            if (drainNow) TryScheduleDrain(dispatcher);

            Dispatcher.Run();
        }
        catch (Exception)
        {
            // C-5: nothing may escape a worker thread's delegate.
        }
        finally
        {
            // M2: however the pump ended — Abandon/Dispose, a work item shutting the dispatcher down, WM_QUIT
            // or an exception — this worker is dead from here on: refuse further work (TryPost -> false, the
            // owner replaces the worker) and drop items that will never run.
            lock (_gate)
            {
                _stopping = true;
                _pending.Clear();
            }

            ShutDownOwnDispatcher(dispatcher);
            ReleaseApartmentWrappers();
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// Runs at most ONE queued item, then re-schedules itself if more are queued, so the pump dispatches
    /// window messages between items. Refuses to start an item while another is executing further up this
    /// same stack (nested pump) — see the class remarks.
    /// </summary>
    private void Drain()
    {
        Action? work;
        lock (_gate)
        {
            _drainScheduled = false;

            // Nested pump: the item currently executing further up the stack re-schedules on its way out.
            if (_executing) return;

            if (_abandoned || _stopping || !_pending.TryDequeue(out work)) return;
        }

        _executing = true;
        try
        {
            work();
        }
        catch (Exception)
        {
            // A job's own failure must not take the worker (or, through Dispatcher.Run, the process) down.
            // StaIconLoadQueue already turns job exceptions into Failed results; this is the last line.
        }
        finally
        {
            _executing = false;
        }

        Dispatcher? rescheduleOn = null;
        lock (_gate)
        {
            if (!_abandoned && !_stopping && _pending.Count > 0 && !_drainScheduled && _dispatcher is not null)
            {
                _drainScheduled = true;
                rescheduleOn = _dispatcher;
            }
        }

        if (rescheduleOn is not null && !TryScheduleDrain(rescheduleOn))
        {
            lock (_gate) _stopping = true;
        }
    }

    /// <summary>
    /// Posts one <see cref="Drain"/> operation. <see langword="false"/> when the dispatcher is shutting down or
    /// shut down: <see cref="Dispatcher.BeginInvoke(DispatcherPriority, Delegate)"/> does not throw then, it hands
    /// back an operation that is already <see cref="DispatcherOperationStatus.Aborted"/> and will never run (M2).
    /// </summary>
    /// <param name="dispatcher">The worker's own dispatcher.</param>
    /// <returns><see langword="true"/> when the drain operation is queued and will run.</returns>
    private bool TryScheduleDrain(Dispatcher dispatcher)
    {
        try
        {
            if (dispatcher.HasShutdownStarted) return false;

            var operation = dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)Drain);
            return operation.Status != DispatcherOperationStatus.Aborted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) => e.Handled = true;

    private void ShutDownOwnDispatcher(Dispatcher? dispatcher)
    {
        if (dispatcher is null) return;

        try
        {
            // Dispatcher.Run normally returns only after shutdown finished; this covers the early-return path
            // (abandoned/disposed before the pump started) so the hidden message window is released (R7-12).
            if (!dispatcher.HasShutdownFinished) dispatcher.InvokeShutdown();
        }
        catch (Exception)
        {
            // Best effort: the thread is exiting anyway.
        }
        finally
        {
            dispatcher.UnhandledException -= OnDispatcherUnhandledException;
        }
    }
}
