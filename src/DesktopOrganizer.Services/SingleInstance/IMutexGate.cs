namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Non-blocking wrapper around a single named <see cref="System.Threading.Mutex"/> used as a
/// single-instance guard or detection object (ADR-023 D-1). Never waits — <see cref="TryAcquire"/> is
/// a zero-timeout probe, never a blocking <c>WaitOne()</c> (ADR-023 R-4: no unbounded waits anywhere
/// in the handshake).
/// </summary>
internal interface IMutexGate : IDisposable
{
    /// <summary>
    /// Whether this instance currently owns the underlying mutex (set by a successful
    /// <see cref="TryAcquire"/>, cleared by <see cref="Release"/>).
    /// </summary>
    bool IsOwned { get; }

    /// <summary>
    /// Whether ownership was ever taken over from a process that died while holding the mutex
    /// (<c>AbandonedMutexException</c>, ADR-023 R-2). Sticky once set: it records how this instance
    /// came to own the mutex, not whether it still does — the value survives <see cref="Release"/> so
    /// the handshake can report it as <c>SingleInstancePrimaryAcquired.wasAbandoned</c> (a crashed
    /// previous primary is exactly the situation worth seeing in a diagnostics log).
    /// </summary>
    bool WasAbandoned { get; }

    /// <summary>
    /// Attempts to take ownership without blocking. An <c>AbandonedMutexException</c> (previous owner
    /// process died while holding it) is treated as a successful, legitimate acquisition
    /// (ADR-023 R-2).
    /// </summary>
    /// <returns><see langword="true"/> if this instance now owns the mutex.</returns>
    bool TryAcquire();

    /// <summary>
    /// Releases ownership if held. Never throws — a failed release is not fatal, the OS reclaims the
    /// mutex when the process exits (ADR-023 D-9/R-5).
    /// </summary>
    void Release();
}
