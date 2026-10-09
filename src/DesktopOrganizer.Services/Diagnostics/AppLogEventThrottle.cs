using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Per-event, per-session cap on repeated entries in the always-on <see cref="IAppLog"/> (ADR-033). The first
/// <see cref="LimitPerEvent"/> occurrences of an event name are written; the next one is replaced by a single
/// <see cref="SuppressedEvent"/> Warn (when <c>announceSuppression</c> is on) that names the event and the limit;
/// every later occurrence is only counted (<see cref="SuppressedCount"/>). The budget lives as long as the
/// instance — for the DI singletons and process-wide statics that own one, that is the session.
/// </summary>
/// <remarks>
/// <para>
/// Why: the app log is always on (no <c>--diag</c>) and an <see cref="IAppLog.Error"/> entry carries a scrubbed
/// stack trace. A failure that repeats per icon, per probe or per window message would otherwise fill the log
/// with identical entries. The first few occurrences carry all the information there is; the notice tells the
/// reader that the silence afterwards is deliberate.
/// </para>
/// <para>
/// Thread-safe (one short lock around a dictionary lookup; the log call itself runs outside the lock). Never
/// throws: an exception from the log is swallowed, like every other diagnostics failure. Not for window
/// procedures or <c>WH_MOUSE_LL</c> callbacks — an admitted entry is written synchronously.
/// </para>
/// </remarks>
internal sealed class AppLogEventThrottle
{
    /// <summary>Default number of entries written per event name (ADR-audit review cycle 1, G4).</summary>
    internal const int DefaultLimitPerEvent = 5;

    /// <summary>Event name of the one-time "further occurrences suppressed" notice.</summary>
    internal const string SuppressedEvent = "LogEventSuppressed";

    private readonly object _gate = new ();
    private readonly Dictionary<string, int> _counts = new (StringComparer.Ordinal);
    private readonly bool _announceSuppression;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppLogEventThrottle"/> class.
    /// </summary>
    /// <param name="limitPerEvent">How many occurrences of one event name are written; must be positive.</param>
    /// <param name="announceSuppression">
    /// Whether the first suppressed occurrence writes the <see cref="SuppressedEvent"/> notice. Off for
    /// "once per session" diagnostics, where a notice would only double the entry.
    /// </param>
    public AppLogEventThrottle(int limitPerEvent = DefaultLimitPerEvent, bool announceSuppression = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPerEvent);

        LimitPerEvent = limitPerEvent;
        _announceSuppression = announceSuppression;
    }

    private enum Decision
    {
        Write,
        Announce,
        Suppress,
    }

    /// <summary>Gets how many occurrences of one event name are written.</summary>
    public int LimitPerEvent { get; }

    /// <summary>Writes a Warn entry unless the event's budget is spent.</summary>
    /// <param name="log">The target log; <see langword="null"/> writes nothing and consumes no budget.</param>
    /// <param name="source">The entry's source.</param>
    /// <param name="evt">The entry's event name — the budget key.</param>
    /// <param name="data">The entry's structured fields.</param>
    public void Warn(IAppLog? log, string source, string evt, params KeyValuePair<string, object?>[] data)
    {
        if (log is null) return;

        try
        {
            switch (Admit(evt))
            {
                case Decision.Write:
                    log.Warn(source, evt, data);
                    break;
                case Decision.Announce:
                    Announce(log, source, evt);
                    break;
            }
        }
        catch (Exception)
        {
            // A diagnostics failure must never surface to the caller (IAppLog contract).
        }
    }

    /// <summary>Writes an Error entry unless the event's budget is spent.</summary>
    /// <param name="log">The target log; <see langword="null"/> writes nothing and consumes no budget.</param>
    /// <param name="source">The entry's source.</param>
    /// <param name="evt">The entry's event name — the budget key.</param>
    /// <param name="ex">The exception, if any.</param>
    /// <param name="data">The entry's structured fields.</param>
    public void Error(IAppLog? log, string source, string evt, Exception? ex, params KeyValuePair<string, object?>[] data)
    {
        if (log is null) return;

        try
        {
            switch (Admit(evt))
            {
                case Decision.Write:
                    log.Error(source, evt, ex, data);
                    break;
                case Decision.Announce:
                    Announce(log, source, evt);
                    break;
            }
        }
        catch (Exception)
        {
            // A diagnostics failure must never surface to the caller (IAppLog contract).
        }
    }

    /// <summary>How many occurrences of <paramref name="evt"/> were not written (the notice included).</summary>
    /// <param name="evt">The event name.</param>
    /// <returns>The number of suppressed occurrences; 0 for an unknown event.</returns>
    public int SuppressedCount(string evt)
    {
        lock (_gate)
        {
            return _counts.TryGetValue(evt, out var count) ? Math.Max(0, count - LimitPerEvent) : 0;
        }
    }

    private void Announce(IAppLog log, string source, string evt)
        => log.Warn(
            source,
            SuppressedEvent,
            new KeyValuePair<string, object?>("event", evt),
            new KeyValuePair<string, object?>("limit", LimitPerEvent));

    private Decision Admit(string evt)
    {
        int count;
        lock (_gate)
        {
            _counts.TryGetValue(evt, out count);

            // Saturate instead of overflowing on a pathological, session-long failure loop.
            if (count < int.MaxValue) _counts[evt] = ++count;
        }

        if (count <= LimitPerEvent) return Decision.Write;
        return count == LimitPerEvent + 1 && _announceSuppression ? Decision.Announce : Decision.Suppress;
    }
}
