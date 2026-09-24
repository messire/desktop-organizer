using System.IO;
using System.Threading;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Production <see cref="IStorageLock"/> (ADR-024 D-8, closing ADR-023 R-15): an OS-level advisory
/// lock over <see cref="StateStorePaths.LockFilePath"/>, backed by a <see cref="FileStream"/> opened
/// with <see cref="FileShare.None"/> and held open for as long as this instance is not disposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-handle, not per-process, semantics.</b> <see cref="FileShare.None"/> is enforced by the OS
/// per open <see cref="FileStream"/> handle, not per process — a second handle opened against the same
/// path collides exactly the same way whether it comes from a second process (the real-world FUS
/// scenario this closes) or a second <see cref="StorageFileLock"/> instance inside the very same test
/// process. That is what lets <c>StorageFileLockTests</c> exercise the real conflict without spawning a
/// second OS process.
/// </para>
/// <para>
/// <b>Never throws.</b> <see cref="TryAcquire"/> swallows every exception the underlying
/// <see cref="FileStream"/> open can raise (already-locked, permissions, a stuck handle left behind by
/// a crashed process, a missing storage root, …) and simply reports <see langword="false"/> — per
/// <see cref="IStorageLock.TryAcquire"/>'s contract, the caller (<c>Program.Main</c>, ADR-024 D-11) is
/// expected to degrade to a read-only session, never to crash the process over a lock it could not get.
/// </para>
/// </remarks>
internal sealed class StorageFileLock : IStorageLock
{
    private readonly string _lockFilePath;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly object _gate = new();

    private FileStream? _handle;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageFileLock"/> class for production use,
    /// against the real per-user storage root and the ADR-024 D-8 default retry schedule (3 attempts,
    /// 150 ms apart — at most 450 ms of blocking before <see cref="TryAcquire"/> gives up).
    /// </summary>
    public StorageFileLock()
        : this(new StateStorePaths(), maxAttempts: 3, retryDelay: TimeSpan.FromMilliseconds(150))
    {
    }

    /// <summary>
    /// Test-only overload with an explicit <see cref="StateStorePaths"/> (so tests point at a
    /// disposable temp folder instead of the real <c>%LOCALAPPDATA%\DesktopOrganizer\</c>, mirroring
    /// <see cref="JsonApplicationStateStore"/>'s test constructor) and an explicit retry schedule, so
    /// tests can pass <see cref="TimeSpan.Zero"/> instead of waiting out the real 450 ms budget.
    /// </summary>
    /// <param name="paths">Supplies <see cref="StateStorePaths.LockFilePath"/>.</param>
    /// <param name="maxAttempts">Total number of open attempts <see cref="TryAcquire"/> makes before giving up.</param>
    /// <param name="retryDelay">Delay between attempts (not applied after the final attempt).</param>
    internal StorageFileLock(StateStorePaths paths, int maxAttempts, TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegative(retryDelay.Ticks);

        _lockFilePath = paths.LockFilePath;
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay;
    }

    /// <inheritdoc />
    public bool IsHeld { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent while already held: a repeat call on an instance that holds the lock is a no-op that
    /// returns <see langword="true"/> without reopening the handle — this instance already owns the
    /// exclusive access it would be trying to (re-)acquire. Once disposed, an instance never reacquires
    /// — <see cref="Dispose"/> is meant to be terminal, matching the "held for the lifetime of the
    /// process" usage in <c>Program.Main</c> (ADR-024 D-11).
    /// </remarks>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (IsHeld) return true;

            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                if (TryOpenHandle())
                {
                    IsHeld = true;
                    return true;
                }

                if (attempt < _maxAttempts) Thread.Sleep(_retryDelay);
            }

            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _handle?.Dispose();
            _handle = null;
            IsHeld = false;
        }
    }

    /// <summary>Must be called under <see cref="_gate"/>. Never throws — see type remarks.</summary>
    private bool TryOpenHandle()
    {
        try
        {
            var directory = Path.GetDirectoryName(_lockFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // FileShare.None held open for the lifetime of this instance is the entire mechanism —
            // see type remarks on per-handle semantics. No explicit unlock protocol is needed; Dispose
            // (or the OS reclaiming the handle on a hard process kill) is the only release path.
            _handle = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception)
        {
            // Deliberately broad: any failure to open (already locked by another handle, permissions,
            // a missing/unreachable storage root, a stuck AV-held handle, ...) is exactly the "cannot
            // acquire" outcome IStorageLock.TryAcquire promises to report as false, never to throw.
            return false;
        }
    }
}
