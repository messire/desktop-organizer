namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Admits at most one log entry per window handle per <see cref="Interval"/>, so a native call that
/// keeps failing for the same dead <c>HWND</c> on a hot path (a window procedure, a sizing loop) writes
/// one line a minute instead of one line per message.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe (a single short lock). Memory is bounded: at most <see cref="MaxTrackedHandles"/> handles
/// are remembered; when that is reached, entries whose interval has already elapsed are dropped and, if
/// that frees nothing, the table is cleared — the worst case is one extra log line per handle, never
/// unbounded growth.
/// </para>
/// <para>
/// The limiter only decides; the caller writes the entry and should include
/// <c>suppressedSinceLast</c> so the log still says how many failures the silence stood for.
/// </para>
/// </remarks>
internal sealed class HandleLogRateLimiter
{
    /// <summary>Upper bound on the number of handles remembered at the same time.</summary>
    internal const int MaxTrackedHandles = 256;

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, Entry> _entries = [];
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="HandleLogRateLimiter"/> class.
    /// </summary>
    /// <param name="interval">Minimum time between two admitted entries for the same handle.</param>
    /// <param name="time">Clock; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    public HandleLogRateLimiter(TimeSpan interval, TimeProvider? time = null)
    {
        Interval = interval;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the minimum time between two admitted entries for the same handle.</summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Records one failure for <paramref name="hwnd"/> and decides whether it may be logged now.
    /// </summary>
    /// <param name="hwnd">The window handle the failure is about.</param>
    /// <param name="suppressedSinceLast">
    /// When admitted: how many failures for this handle were swallowed since the previous admitted one.
    /// Otherwise 0.
    /// </param>
    /// <returns><see langword="true"/> when the caller should write the log entry.</returns>
    public bool TryAdmit(IntPtr hwnd, out int suppressedSinceLast)
    {
        var now = _time.GetTimestamp();

        lock (_gate)
        {
            if (_entries.TryGetValue(hwnd, out var entry))
            {
                if (_time.GetElapsedTime(entry.LastAdmitted, now) < Interval)
                {
                    _entries[hwnd] = entry with { Suppressed = entry.Suppressed + 1 };
                    suppressedSinceLast = 0;
                    return false;
                }

                suppressedSinceLast = entry.Suppressed;
            }
            else
            {
                suppressedSinceLast = 0;
                if (_entries.Count >= MaxTrackedHandles) Prune(now);
            }

            _entries[hwnd] = new Entry(now, 0);
            return true;
        }
    }

    private void Prune(long now)
    {
        foreach (var (key, entry) in _entries.ToArray())
        {
            if (_time.GetElapsedTime(entry.LastAdmitted, now) >= Interval) _entries.Remove(key);
        }

        if (_entries.Count >= MaxTrackedHandles) _entries.Clear();
    }

    private readonly record struct Entry(long LastAdmitted, int Suppressed);
}
