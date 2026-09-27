using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Failure reporting for code that must not write a log entry itself — first and foremost the
/// <c>WH_MOUSE_LL</c> callback (ADR-003/ADR-012). <see cref="Record"/> only stores the exception and bumps
/// a counter; the actual <see cref="IAppLog.Error"/> call (stack-trace formatting, scrubbing, enqueueing,
/// and — on the very first write of the session — starting the log writer thread) runs later on a
/// thread-pool timer, at most once per <see cref="MinInterval"/>, and carries the number of failures it
/// stands for.
/// </summary>
/// <remarks>
/// <para>
/// Why: Windows silently uninstalls a low-level hook whose callback exceeds
/// <c>HKCU\Control Panel\Desktop\LowLevelHooksTimeout</c> (300 ms by default). Logging from inside the
/// callback on every failure could cost exactly that on a bad day, and a failure that repeats on every
/// mouse move would also flood the log.
/// </para>
/// <para>
/// The first failure is written as soon as the thread pool picks the timer up; later ones are batched
/// into one entry per <see cref="MinInterval"/>. The entry carries the most recent exception and an
/// <c>occurrences</c> field with the batch size.
/// </para>
/// </remarks>
internal sealed class DeferredFailureLog : IDisposable
{
    private readonly Func<IAppLog> _log;
    private readonly string _source;
    private readonly string _evt;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    private Exception? _lastFailure;
    private int _pending;
    private int _flushScheduled;
    private int _flushedOnce;
    private long _lastFlushTimestamp;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeferredFailureLog"/> class.
    /// </summary>
    /// <param name="log">
    /// Resolves the log at flush time (so an ambient <c>AppLog.Current</c> fallback installed after
    /// construction is still honoured). Never invoked from <see cref="Record"/>.
    /// </param>
    /// <param name="source">The <c>source</c> of the written entry.</param>
    /// <param name="evt">The <c>evt</c> of the written entry.</param>
    /// <param name="minInterval">Minimum time between two written entries.</param>
    /// <param name="time">Clock and timer factory; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    public DeferredFailureLog(Func<IAppLog> log, string source, string evt, TimeSpan minInterval, TimeProvider? time = null)
    {
        _log = log;
        _source = source;
        _evt = evt;
        MinInterval = minInterval;
        _time = time ?? TimeProvider.System;

        // Created dormant; Record arms it. Owned (and disposed) by this instance.
        _timer = _time.CreateTimer(
            static state => ((DeferredFailureLog)state!).Flush(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets the minimum time between two written entries.</summary>
    public TimeSpan MinInterval { get; }

    /// <summary>
    /// Records one failure. Safe to call from a <c>WH_MOUSE_LL</c> callback: O(1), no I/O, no
    /// formatting, no allocation, never throws. At most one timer re-arm per batch.
    /// </summary>
    /// <param name="ex">The exception that was caught.</param>
    public void Record(Exception ex)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            Volatile.Write(ref _lastFailure, ex);
            Interlocked.Increment(ref _pending);

            // Only the first failure of a batch arms the timer; the rest just count.
            if (Interlocked.Exchange(ref _flushScheduled, 1) != 0) return;

            _timer.Change(DelayUntilNextFlush(), Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // Deliberately empty: this runs inside the hook callback, where nothing may escape — and
            // there is nothing safe left to report the reporting failure to. Worst case one batch of
            // failures goes unlogged.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _timer.Dispose();
        Flush();
    }

    /// <summary>
    /// Writes the pending batch (if any) as one entry. Runs on the timer (thread pool); also called by
    /// <see cref="Dispose"/> so a batch recorded just before shutdown is not lost. Never throws.
    /// </summary>
    internal void Flush()
    {
        // Re-arm first, so a failure landing while this flush runs schedules the next batch.
        Volatile.Write(ref _flushScheduled, 0);

        var count = Interlocked.Exchange(ref _pending, 0);
        if (count == 0) return;

        Volatile.Write(ref _lastFlushTimestamp, _time.GetTimestamp());
        Volatile.Write(ref _flushedOnce, 1);

        try
        {
            _log().Error(
                _source,
                _evt,
                Volatile.Read(ref _lastFailure),
                new KeyValuePair<string, object?>("occurrences", count));
        }
        catch (Exception)
        {
            // The log itself failed; a diagnostics problem must never become a crash on a pool thread.
        }
    }

    private TimeSpan DelayUntilNextFlush()
    {
        if (Volatile.Read(ref _flushedOnce) == 0) return TimeSpan.Zero;

        var elapsed = _time.GetElapsedTime(Volatile.Read(ref _lastFlushTimestamp));
        return elapsed >= MinInterval ? TimeSpan.Zero : MinInterval - elapsed;
    }
}
