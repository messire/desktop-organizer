using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Pure state machine for the single-instance handshake schedule (ADR-023 D-6): alternates
/// "try to acquire ownership" / "try to send the activation to whoever owns it", bounded by a total
/// time budget. Contains no <see cref="Thread.Sleep"/> or clock reads — delays are returned to the
/// caller via <paramref name="delay"/> in <see cref="NextStep"/>, and elapsed time is an explicit
/// parameter, so the caller supplies real or fake time (unit tests never actually wait).
/// </summary>
/// <remarks>
/// <para>
/// Driving loop (the coordinator that consumes this policy is out of scope for this commit, but this
/// is the contract it must follow):
/// </para>
/// <code>
/// attempt = 0; last = null;
/// while (true)
/// {
///     step = policy.NextStep(attempt, elapsedMs(), last, out delay);
///     if (step == GiveUp) break;
///     wait(delay); // no-op when delay == TimeSpan.Zero
///     if (step == TryAcquire)
///     {
///         attempt++;
///         if (guard.TryAcquire()) { /* RunAsPrimary, stop looping */ break; }
///         last = null; // this attempt's send is still pending
///         continue;
///     }
///     // step == TrySend
///     last = client.TrySend(request, connectTimeout);
/// }
/// </code>
/// <para>
/// <c>attempt</c> counts completed <see cref="HandshakeStep.TryAcquire"/> calls. <c>last</c> is
/// <see langword="null"/> in exactly two situations: before the very first attempt
/// (<c>attempt == 0</c>), and right after an acquire has failed but its paired send has not run yet
/// (<c>attempt &gt; 0</c>) — <see cref="NextStep"/> tells these apart using <c>attempt</c>. Once a
/// send completes, <c>last</c> becomes non-null and stays that way until the next acquire is
/// attempted.
/// </para>
/// </remarks>
public sealed class SingleInstanceHandshakePolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _budget;
    private readonly IReadOnlyList<TimeSpan> _backoff;

    /// <summary>Step the driving loop should take next.</summary>
    public enum HandshakeStep
    {
        /// <summary>Attempt to acquire ownership of the guard mutex.</summary>
        TryAcquire,

        /// <summary>Send the activation request to whoever currently owns the guard mutex.</summary>
        TrySend,

        /// <summary>Stop the handshake — the budget or attempt count is exhausted, or the last send result is terminal.</summary>
        GiveUp,
    }

    public SingleInstanceHandshakePolicy(int maxAttempts, TimeSpan connectTimeout, TimeSpan budget, IReadOnlyList<TimeSpan> backoff)
    {
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentOutOfRangeException.ThrowIfNegative(connectTimeout.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegative(budget.Ticks);

        _maxAttempts = maxAttempts;
        _connectTimeout = connectTimeout;
        _budget = budget;
        _backoff = backoff;
    }

    /// <summary>
    /// ADR-023 D-6 default schedule: 6 attempts, 300 ms connect timeout per attempt, 3000 ms total
    /// budget, backoff 0/50/100/200/400/800 ms between attempts.
    /// </summary>
    public static SingleInstanceHandshakePolicy Default { get; } = new(
        maxAttempts: 6,
        connectTimeout: TimeSpan.FromMilliseconds(300),
        budget: TimeSpan.FromMilliseconds(3000),
        backoff:
        [
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(800),
        ]);

    /// <summary>Connect timeout the caller should apply to each individual <c>TrySend</c> call.</summary>
    public TimeSpan ConnectTimeout => _connectTimeout;

    /// <summary>
    /// Decides the next step of the handshake. Never throws for valid enum inputs — a
    /// misconfigured/exhausted policy degrades to <see cref="HandshakeStep.GiveUp"/> rather than
    /// indexing out of range.
    /// </summary>
    /// <param name="attempt">Number of <see cref="HandshakeStep.TryAcquire"/> attempts completed so far.</param>
    /// <param name="elapsedMs">Milliseconds elapsed since the handshake started.</param>
    /// <param name="last">
    /// Result of the send paired with attempt <paramref name="attempt"/>, or <see langword="null"/>
    /// if that attempt's send has not run yet (including before the very first attempt).
    /// </param>
    /// <param name="delay">
    /// How long the caller should wait before performing the returned step. Always
    /// <see cref="TimeSpan.Zero"/> for <see cref="HandshakeStep.TrySend"/> and
    /// <see cref="HandshakeStep.GiveUp"/> — the schedule only pauses between attempts, never between
    /// an attempt's failed acquire and its paired send (ADR-023 D-6).
    /// </param>
    public HandshakeStep NextStep(int attempt, long elapsedMs, ActivationSendResult? last, out TimeSpan delay)
    {
        if (last is null)
        {
            if (attempt <= 0)
            {
                // Very first call: nothing has happened yet.
                if (_maxAttempts <= 0)
                {
                    delay = TimeSpan.Zero;
                    return HandshakeStep.GiveUp;
                }

                delay = BackoffFor(0);
                return HandshakeStep.TryAcquire;
            }

            // `attempt`'s acquire already failed; its paired send is unconditional and unpaused.
            delay = TimeSpan.Zero;
            return HandshakeStep.TrySend;
        }

        // `attempt`'s send just completed with result `last` — decide whether to continue.
        if (last is ActivationSendResult.Delivered or ActivationSendResult.OtherSession)
        {
            delay = TimeSpan.Zero;
            return HandshakeStep.GiveUp;
        }

        if (attempt >= _maxAttempts || elapsedMs >= _budget.TotalMilliseconds)
        {
            delay = TimeSpan.Zero;
            return HandshakeStep.GiveUp;
        }

        delay = BackoffFor(attempt);
        return HandshakeStep.TryAcquire;
    }

    private TimeSpan BackoffFor(int completedAttempts)
    {
        if (_backoff.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var index = Math.Min(completedAttempts, _backoff.Count - 1);
        return _backoff[index];
    }
}
