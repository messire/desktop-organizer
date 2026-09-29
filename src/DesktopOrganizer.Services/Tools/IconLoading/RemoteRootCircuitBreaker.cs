using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Per-network-root circuit breaker (ADR-046 D-3): a root that proves slow or gets abandoned by the
/// watchdog is quarantined for <see cref="_rootQuarantine"/>, so every OTHER job/probe against the same
/// root fails fast instead of independently paying for its own SMB timeout.
/// </summary>
/// <remarks>
/// <para>Per root, a classic three-state breaker:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Closed</b> — calls pass. The root still reads as open while any in-flight call against it has already run
/// for <see cref="_slowThreshold"/> (concurrent callers must not queue up behind it).
/// </description></item>
/// <item><description>
/// <b>Open</b> — a call completed slowly or was reported stuck: <see cref="IsOpen"/> is <see langword="true"/>
/// until the quarantine ends.
/// </description></item>
/// <item><description>
/// <b>Half-open</b> — the quarantine has ended. The FIRST <see cref="BeginCall"/> becomes the single trial call;
/// while it is in flight every other caller still sees the root as open. The trial completing faster than the
/// slow threshold closes the root; completing slowly or being reported stuck re-opens it for another full
/// quarantine. <see cref="IsOpen"/> itself reserves nothing, so a caller that only asks (without
/// <see cref="BeginCall"/>) is never counted as the trial.
/// </description></item>
/// </list>
/// <para>
/// A call the watchdog abandons is dropped from the in-flight set by <see cref="ReportStuck"/> (review M8) — it
/// can never hold the root open past its quarantine — and its scope's late <see cref="IDisposable.Dispose"/>,
/// if the stuck native call ever returns, is ignored.
/// </para>
/// </remarks>
internal sealed class RemoteRootCircuitBreaker : IRemoteRootCircuitBreaker
{
    private readonly TimeSpan _slowThreshold;
    private readonly TimeSpan _rootQuarantine;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteRootCircuitBreaker"/> class with the production
    /// thresholds (<c>SlowThreshold</c> 2 s, <c>RootQuarantine</c> 60 s) and the real clock.
    /// </summary>
    public RemoteRootCircuitBreaker()
        : this(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteRootCircuitBreaker"/> class with explicit
    /// thresholds and clock. Internal — used by tests to exercise the breaker without real wall-clock
    /// delays (<c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s "Wall-clock тесты" rule).
    /// </summary>
    /// <param name="slowThreshold">A call (or unfinished in-flight call) running at least this long opens the root.</param>
    /// <param name="rootQuarantine">How long an opened root stays open.</param>
    /// <param name="timeProvider">Clock used for every timing decision.</param>
    internal RemoteRootCircuitBreaker(TimeSpan slowThreshold, TimeSpan rootQuarantine, TimeProvider timeProvider)
    {
        _slowThreshold = slowThreshold;
        _rootQuarantine = rootQuarantine;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public bool IsOpen(string rootKey)
    {
        lock (_gate)
        {
            if (!_roots.TryGetValue(rootKey, out var state)) return false;

            var now = _timeProvider.GetUtcNow();
            if (state.QuarantinedUntil is { } until)
            {
                if (now < until) return true;

                // Half-open: exactly one trial call at a time.
                if (state.Trial is not null) return true;
            }

            return OldestInFlight(state) is { } oldest && now - oldest.StartedAt >= _slowThreshold;
        }
    }

    /// <inheritdoc />
    public IDisposable BeginCall(string rootKey)
    {
        lock (_gate)
        {
            var state = GetOrCreate(rootKey);
            var now = _timeProvider.GetUtcNow();
            var call = new CallScope(this, rootKey, now);
            state.InFlight.Add(call);

            if (state.QuarantinedUntil is { } until && now >= until && state.Trial is null)
            {
                state.Trial = call;
            }

            return call;
        }
    }

    /// <inheritdoc />
    public void ReportStuck(string rootKey)
    {
        lock (_gate)
        {
            var state = GetOrCreate(rootKey);

            // The watchdog abandons the call that has been running longest: every stuck call has run past the
            // job timeout, so the oldest in-flight call against the root is always one of them. Dropping it
            // (rather than leaving it in the in-flight set forever) is what lets the root leave "open" when
            // the quarantine ends.
            if (OldestInFlight(state) is { } stuck)
            {
                stuck.Abandoned = true;
                state.InFlight.Remove(stuck);
            }

            // A stuck trial is a failed trial; a stuck ordinary call opens the root. Either way: open now.
            state.Trial = null;
            state.QuarantinedUntil = _timeProvider.GetUtcNow() + _rootQuarantine;
        }
    }

    private static CallScope? OldestInFlight(RootState state)
    {
        CallScope? oldest = null;
        foreach (var call in state.InFlight)
        {
            if (oldest is null || call.StartedAt < oldest.StartedAt) oldest = call;
        }

        return oldest;
    }

    private void ReportCompleted(CallScope call)
    {
        lock (_gate)
        {
            // A call ReportStuck already gave up on: its late return proves nothing about the root any more.
            if (call.Abandoned) return;

            var state = GetOrCreate(call.RootKey);
            if (!state.InFlight.Remove(call)) return;

            var now = _timeProvider.GetUtcNow();
            var wasTrial = ReferenceEquals(state.Trial, call);
            if (wasTrial) state.Trial = null;

            if (now - call.StartedAt >= _slowThreshold)
            {
                // Slow ordinary call or failed trial: (re-)open for a full quarantine.
                state.QuarantinedUntil = now + _rootQuarantine;
            }
            else if (wasTrial)
            {
                // Successful trial: the root is healthy again.
                state.QuarantinedUntil = null;
            }
        }
    }

    private RootState GetOrCreate(string rootKey)
    {
        if (_roots.TryGetValue(rootKey, out var state)) return state;
        state = new RootState();
        _roots[rootKey] = state;
        return state;
    }

    /// <summary>Per-root mutable state, guarded by the breaker's own <see cref="_gate"/>.</summary>
    private sealed class RootState
    {
        /// <summary>Gets the calls currently in flight against this root (abandoned ones removed).</summary>
        public List<CallScope> InFlight { get; } = [];

        /// <summary>
        /// Gets or sets the end of the root's quarantine. Set and in the future: open; set and in the past:
        /// half-open (the next <see cref="BeginCall"/> is the trial); <see langword="null"/>: closed (a
        /// successful trial clears it).
        /// </summary>
        public DateTimeOffset? QuarantinedUntil { get; set; }

        /// <summary>Gets or sets the half-open trial call currently in flight, if any.</summary>
        public CallScope? Trial { get; set; }
    }

    /// <summary>
    /// The <see cref="IDisposable"/> handed back by <see cref="BeginCall"/> — one in-flight call; disposing it
    /// reports completion. <see cref="Abandoned"/> is guarded by the owner's <see cref="_gate"/>.
    /// </summary>
    private sealed class CallScope(RemoteRootCircuitBreaker owner, string rootKey, DateTimeOffset startedAt) : IDisposable
    {
        private int _disposed;

        /// <summary>Gets the root key the call is against.</summary>
        public string RootKey => rootKey;

        /// <summary>Gets the time the call began.</summary>
        public DateTimeOffset StartedAt => startedAt;

        /// <summary>Gets or sets a value indicating whether <see cref="ReportStuck"/> gave up on this call.</summary>
        public bool Abandoned { get; set; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.ReportCompleted(this);
        }
    }
}
