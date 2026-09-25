using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// <see cref="EventWaitHandle"/>-backed <see cref="IShutdownRequestListener"/>. Waits on a named event
/// using a single-shot <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object?, int, bool)"/>
/// registration — no dedicated thread, no polling, and at most one <see cref="ShutdownRequested"/> raise
/// per process lifetime.
/// </summary>
internal sealed class InstallerShutdownRequestListener : IShutdownRequestListener
{
    private readonly string _eventName;
    private readonly IDiagnosticsLog _diagnostics;

    private EventWaitHandle? _handle;
    private RegisteredWaitHandle? _registeredWait;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallerShutdownRequestListener"/> class.
    /// </summary>
    /// <param name="eventName">
    /// The OS event name (see <see cref="SingleInstanceNaming.InstallerShutdownRequestEventName"/> for
    /// the production value). Taken through the constructor — rather than hardcoded — so tests can use a
    /// unique, per-test name and never collide with a real installer or with each other.
    /// </param>
    /// <param name="diagnostics">Sink for logging a failed <see cref="Start"/> — never rethrown.</param>
    public InstallerShutdownRequestListener(string eventName, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _eventName = eventName;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public event EventHandler? ShutdownRequested;

    /// <inheritdoc />
    public void Start()
    {
        if (_started || _disposed)
        {
            // Idempotent by contract: a second Start() (or one arriving after Dispose()) is a no-op,
            // not an error.
            return;
        }

        try
        {
            // ResetMode is irrelevant once signaled, because of executeOnlyOnce below (see its remarks) —
            // AutoReset is used only so that a SECOND, unrelated process opening this same name later
            // (which never happens on any reachable path today, since only one Primary ever runs) would
            // not find it permanently signaled.
            _handle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, _eventName, out var createdNew);

            if (!createdNew)
            {
                // Not a fatal condition — WaitOne/RegisterWaitForSingleObject work the same either way —
                // but it means some other principal (never expected in production: only one Primary per
                // session ever names this event) already had a handle with this exact name open. Worth a
                // diagnostics line since it is the one observable sign of a naming collision.
                _diagnostics.Write(
                    "session",
                    "InstallerShutdownListenerEventPreexisted",
                    null);
            }

            // executeOnlyOnce: true — the installer's SetEvent is a request to shut down, and this process
            // is going to do exactly that (App.xaml.cs's ShutdownRequested handler calls Shutdown()) the
            // very first time it fires. There is nothing a second signal could still accomplish: by the
            // time one could arrive, either this process is already tearing down (Dispose() below has
            // unregistered the wait) or it is not — no re-arming needed. A single-shot registration also
            // means the OS thread pool reclaims this wait's slot immediately after the one callback it
            // will ever run, instead of holding it open for the rest of the process's life.
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _handle,
                static (state, _) => ((InstallerShutdownRequestListener)state!).OnSignaled(),
                this,
                millisecondsTimeOutInterval: -1,
                executeOnlyOnce: true);

            _started = true;
        }
        catch (Exception ex)
        {
            // Never throws out of Start() (per the interface contract): a listener that could not be
            // created must simply leave the installer's own fallback path (its "close it manually, then
            // click Retry" dialog, plus Restart Manager's belt-and-braces file-handle check) as the only
            // way to close this running instance — not take down application startup. No free-text
            // exception message is logged, only the closed set of exception type names (same privacy
            // discipline as SingleInstanceCoordinator.WriteSessionEvent).
            _diagnostics.Write(
                "session",
                "InstallerShutdownListenerStartFailed",
                null,
                new KeyValuePair<string, object?>("exceptionType", ex.GetType().Name));

            _handle?.Dispose();
            _handle = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registeredWait is not null)
        {
            // Passing a real WaitHandle (rather than null) makes this a SYNCHRONOUS unregister: the OS
            // signals it only once any callback already in flight has finished, so — bounded by the
            // timeout below — Dispose() cannot return while OnSignaled() is still mid-flight on a
            // thread-pool thread. Without this, a signal racing exactly with Dispose() could still raise
            // ShutdownRequested from a callback that started just before Unregister but observes the
            // event after this method returns. The timeout is a safety bound only (O-5): if it is ever
            // hit, OnSignaled's own inner try/catch around the diagnostics write is what keeps a
            // just-then-disposed IDiagnosticsLog from turning into an unhandled exception on that thread.
            using var unregistered = new ManualResetEvent(false);
            _registeredWait.Unregister(unregistered);
            unregistered.WaitOne(TimeSpan.FromSeconds(2));
            _registeredWait = null;
        }

        _handle?.Dispose();
        _handle = null;
    }

    private void OnSignaled()
    {
        try
        {
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // This callback runs on a thread-pool wait-callback thread with no caller to propagate to —
            // an escaping exception here would be an unhandled exception on a thread-pool thread, which
            // kills the whole process. A subscriber's bug must degrade to "this shutdown request was
            // missed", never to a crash.
            TryWriteDiagnostics(ex);
        }
    }

    /// <summary>
    /// O-5: isolates the diagnostics write itself behind its own try/catch. This callback can, in the
    /// narrow race Dispose()'s 2-second Unregister timeout exists to bound, still be mid-flight after
    /// <see cref="Dispose"/> has returned — at which point <see cref="_diagnostics"/> (owned by the same
    /// DI container that is tearing this listener down) may already be disposed. Losing this one
    /// diagnostics line to that race is acceptable; letting an <see cref="ObjectDisposedException"/> from
    /// writing it escape a thread-pool callback and kill the process is not.
    /// </summary>
    private void TryWriteDiagnostics(Exception ex)
    {
        try
        {
            _diagnostics.Write(
                "session",
                "InstallerShutdownListenerCallbackFailed",
                null,
                new KeyValuePair<string, object?>("exceptionType", ex.GetType().Name));
        }
        catch (ObjectDisposedException)
        {
            // See remarks above — dropped, not rethrown.
        }
    }
}
