namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test calls <see cref="Advance"/>, and whose
/// timers fire synchronously, on the test thread, from inside <see cref="Advance"/> — so code that defers
/// work to a timer (e.g. <c>DeferredFailureLog</c>) can be driven deterministically, without sleeps.
/// </summary>
/// <remarks>
/// Thread-safe for code under test that creates/arms timers from its own threads while the test thread
/// advances the clock (<c>StaIconLoadQueueThreadingTests</c>): the clock and the timer list are guarded,
/// and callbacks always run on the thread calling <see cref="Advance"/>, outside any internal lock.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    /// <summary>Gets every timer created so far, in creation order (a snapshot).</summary>
    public IReadOnlyList<ManualTimer> Timers
    {
        get
        {
            lock (_gate) return [.. _timers];
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(Interlocked.Read(ref _ticks));

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="by"/> and fires, synchronously, every timer that became
    /// due (one-shot semantics: a fired timer is disarmed before its callback runs, so the callback may
    /// re-arm it). <see cref="TimeSpan.Zero"/> fires the timers that are due right now.
    /// </summary>
    /// <param name="by">How far to move the clock.</param>
    public void Advance(TimeSpan by)
    {
        var now = Interlocked.Add(ref _ticks, by.Ticks);

        ManualTimer[] timers;
        lock (_gate) timers = [.. _timers];

        foreach (var timer in timers)
            timer.FireIfDue(now);
    }

    /// <summary>A manually driven, one-shot <see cref="ITimer"/> created by <see cref="ManualTimeProvider"/>.</summary>
    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _timerGate = new();
        private long? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// Initializes a new instance of the <see cref="ManualTimer"/> class.
        /// </summary>
        /// <param name="owner">The provider whose clock this timer follows.</param>
        /// <param name="callback">What to run when the timer fires.</param>
        /// <param name="state">The state passed to <paramref name="callback"/>.</param>
        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>Gets how many times the timer was armed with a finite due time.</summary>
        public int ArmCount { get; private set; }

        /// <summary>Gets the due time of the latest arming, relative to the clock at that moment.</summary>
        public TimeSpan? LastDueTime { get; private set; }

        /// <summary>Gets a value indicating whether the timer was disposed.</summary>
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_timerGate)
            {
                if (IsDisposed) throw new ObjectDisposedException(nameof(ManualTimer));

                _period = period;

                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _dueAt = null;
                    return true;
                }

                ArmCount++;
                LastDueTime = dueTime;
                _dueAt = _owner.GetTimestamp() + dueTime.Ticks;
                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_timerGate)
            {
                IsDisposed = true;
                _dueAt = null;
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        /// <summary>
        /// Fires the callback if the timer is armed and due at <paramref name="now"/> (L8: a non-<see
        /// cref="Timeout.InfiniteTimeSpan"/> period re-arms the timer for <c>now + period</c> BEFORE the
        /// callback runs — same one-shot-per-<see cref="ManualTimeProvider.Advance"/>-call semantics as
        /// before, but now genuinely periodic across repeated <c>Advance</c> calls, so production code that
        /// relies on <see cref="TimerCallback"/> firing again on its own (e.g. <c>StaIconLoadQueue</c>'s
        /// watchdog) can be exercised without the test manually re-arming it every time).
        /// </summary>
        /// <param name="now">The provider's current timestamp.</param>
        internal void FireIfDue(long now)
        {
            TimeSpan period;
            lock (_timerGate)
            {
                if (_dueAt is not { } due || due > now) return;

                period = _period;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : now + period.Ticks;
            }

            _callback(_state);
        }
    }
}
