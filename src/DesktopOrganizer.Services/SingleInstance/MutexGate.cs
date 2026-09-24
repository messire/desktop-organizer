namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// <see cref="System.Threading.Mutex"/>-backed <see cref="IMutexGate"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread affinity (ADR-023 R-5): a <see cref="System.Threading.Mutex"/> is owned by the OS thread
/// that acquired it, not by the process.</b> <see cref="TryAcquire"/> and <see cref="Release"/> on a
/// given instance MUST both be called from the same thread — in production, the application's single
/// main STA thread (<c>Program.Main</c> and <c>App.OnExit</c> run on that same thread). Calling
/// <see cref="Release"/> from a different thread than the one that acquired the mutex throws
/// <see cref="ApplicationException"/>, which this type swallows (see below), but the mutex will then
/// simply not be released early — callers must not rely on this type to enforce thread affinity, only
/// to degrade safely when it is violated.
/// </para>
/// </remarks>
internal sealed class MutexGate : IMutexGate
{
    private readonly Mutex _mutex;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MutexGate"/> class wrapping a named mutex.
    /// Creation itself never blocks and never throws for "already exists" — that is exactly the
    /// case <see cref="TryAcquire"/> is meant to probe.
    /// </summary>
    /// <param name="name">The OS mutex name (see <see cref="SingleInstanceNaming"/>).</param>
    public MutexGate(string name)
    {
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    /// <inheritdoc />
    public bool IsOwned { get; private set; }

    /// <inheritdoc />
    public bool WasAbandoned { get; private set; }

    /// <inheritdoc />
    public bool TryAcquire()
    {
        if (_disposed) return false;

        try
        {
            // Zero timeout: a probe, never a wait. The handshake's own retry/backoff schedule
            // (SingleInstanceHandshakePolicy) is what turns this into a bounded polling loop.
            IsOwned = _mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner process died while holding the mutex. We now legitimately own it —
            // this is the expected "kill primary in Task Manager" recovery path (ADR-023 R-2).
            IsOwned = true;
            WasAbandoned = true;
        }

        return IsOwned;
    }

    /// <inheritdoc />
    public void Release()
    {
        if (!IsOwned) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not fatal: most commonly "called from a different thread than the one that acquired
            // it" (thread-affinity violation) — the OS reclaims the mutex when the process exits, and
            // the next process to probe it will legitimately see AbandonedMutexException
            // (ADR-023 D-9/R-5).
        }
        finally
        {
            IsOwned = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release();
        _mutex.Dispose();
    }
}
