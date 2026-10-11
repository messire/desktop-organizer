namespace DesktopOrganizer.Services.Abstractions.Storage;

/// <summary>
/// Advisory, process-lifetime lock over the root of the on-disk application storage
/// (the storage root: Inno channel <c>%LOCALAPPDATA%\DesktopOrganizer\</c>, Store channel
/// <c>%USERPROFILE%\DesktopOrganizer\</c>), closing ADR-023 R-15. Deliberately taken on the
/// storage root rather than on <c>State\</c> alone (ADR-024 D-8), because it conceptually covers
/// both <see cref="IApplicationStateStore"/>'s <c>State\</c> directory and
/// <c>PanelItems\.manifest.json</c> — the two surfaces R-15 named as shared, concurrently-written
/// state between two instances of the app.
/// </summary>
/// <remarks>
/// <b>Advisory, not mandatory:</b> nothing on disk enforces this lock; it only protects two
/// processes that both cooperate by calling <see cref="TryAcquire"/> before writing. A held lock is
/// released implicitly when the process exits — even a hard kill — because it is backed by an OS
/// file handle opened with <see cref="System.IO.FileShare.None"/>; no explicit unlock protocol is
/// needed, which is exactly why <see cref="IDisposable"/> is sufficient here.
/// <para>
/// <b>A failed acquire must degrade, never crash the process.</b> By the time
/// <see cref="TryAcquire"/> is called (ADR-024 D-11: after single-instance handshake / <c>RunAsPrimary</c>,
/// before <see cref="IApplicationStateStore.Load"/>), this process has already been confirmed as the
/// sole primary instance; terminating it because of a stuck lock file would be worse than running
/// degraded. On failure, the intended caller behavior is: loading still proceeds normally, but the
/// application-state store falls back to a read-only session — see
/// <see cref="IApplicationStateStore.IsWritable"/> — so every subsequent
/// <see cref="IApplicationStateStore.Save"/> becomes a no-op
/// (<see cref="StateSaveResult.SkippedReadOnly"/>) instead of risking a write that races a second
/// process which may genuinely hold the lock.
/// </para>
/// </remarks>
public interface IStorageLock : IDisposable
{
    /// <summary>
    /// Attempts to acquire the lock. Implementations are expected to retry a small, bounded number
    /// of times internally (ADR-024 D-8: 3 attempts × 150 ms) before giving up — callers should treat
    /// a single call as already representing that whole bounded attempt, not retry it themselves.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the lock is now held by this process; <see langword="false"/> if
    /// another process already holds it, or the underlying file could not be opened for any other
    /// reason (permissions, a stuck handle, etc.). Never throws.
    /// </returns>
    bool TryAcquire();

    /// <summary>Whether this instance currently holds the lock.</summary>
    bool IsHeld { get; }
}
