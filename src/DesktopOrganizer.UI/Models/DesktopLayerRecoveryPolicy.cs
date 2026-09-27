namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure, clock-free policy for shell-restart recovery (ADR-034): coalesces bursts of restart signals
/// into one recovery, versions every recovery with a generation counter so a newer signal supersedes
/// an in-flight one, and bounds the retries spent waiting for Explorer's <c>WorkerW</c> to appear.
/// </summary>
/// <remarks>
/// <para>
/// The caller owns time: it asks <see cref="OnRestartSignal"/> how long to wait, schedules the attempt
/// itself, and reports back through <see cref="TryBeginAttempt"/>/<see cref="OnAttemptCompleted"/>. That
/// keeps this class deterministic and unit-testable without timers or a dispatcher.
/// </para>
/// <para>
/// <b>Coalescing</b> is a trailing debounce: every signal starts a new generation and (re)arms the
/// <see cref="CoalesceWindow"/>, so only the last signal of a burst ever runs an attempt — both
/// detection paths (<c>TaskbarCreated</c> and the <c>Progman</c> poll) firing for one restart, or
/// Explorer restarting several times in a row, cost one recovery.
/// </para>
/// <para>
/// <b>Bounded retries</b>: at most <see cref="MaxAttempts"/> attempts per generation, spaced by
/// <see cref="RetryInterval"/> (default 6 × 500 ms ≈ 2.5 s after the first attempt, ≈ 4 s after the
/// signal). Retrying never loops forever: once the budget is spent the policy answers
/// <see cref="DesktopLayerRecoveryStep.Exhausted"/> and goes idle until the next signal.
/// </para>
/// <para>Not thread-safe by design: it lives on the UI thread, like every caller.</para>
/// </remarks>
public sealed class DesktopLayerRecoveryPolicy
{
    /// <summary>Default <see cref="MaxAttempts"/>.</summary>
    public const int DefaultMaxAttempts = 6;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopLayerRecoveryPolicy"/> class.
    /// </summary>
    /// <param name="maxAttempts">Attempts allowed per generation; values below 1 are treated as 1.</param>
    public DesktopLayerRecoveryPolicy(int maxAttempts = DefaultMaxAttempts)
    {
        MaxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>Gets the quiet period after the last restart signal before the first attempt (1500 ms).</summary>
    public static TimeSpan CoalesceWindow { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Gets the delay between two attempts of the same generation (500 ms).</summary>
    public static TimeSpan RetryInterval { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets the maximum number of attempts per generation.</summary>
    public int MaxAttempts { get; }

    /// <summary>Gets the current generation — incremented by every <see cref="OnRestartSignal"/>.</summary>
    public int Generation { get; private set; }

    /// <summary>Gets the number of attempts already begun in the current generation.</summary>
    public int AttemptsMade { get; private set; }

    /// <summary>Gets a value indicating whether a recovery is scheduled or in progress.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Records a restart signal: starts a new generation (superseding any scheduled or in-flight
    /// attempt of the previous one) and returns the delay the caller must wait before the first
    /// attempt of this generation.
    /// </summary>
    /// <returns><see cref="CoalesceWindow"/>.</returns>
    public TimeSpan OnRestartSignal()
    {
        Generation++;
        AttemptsMade = 0;
        IsActive = true;
        return CoalesceWindow;
    }

    /// <summary>
    /// Asks whether a scheduled attempt may run. Answers <see langword="false"/> for a stale
    /// generation, when no recovery is active, or when the attempt budget is spent.
    /// </summary>
    /// <param name="generation">The generation the attempt was scheduled for.</param>
    /// <returns><see langword="true"/> when the caller should run the attempt now.</returns>
    public bool TryBeginAttempt(int generation)
    {
        if (!IsActive || generation != Generation || AttemptsMade >= MaxAttempts) return false;

        AttemptsMade++;
        return true;
    }

    /// <summary>
    /// Reports the result of an attempt and returns what to do next.
    /// </summary>
    /// <param name="generation">The generation the attempt belonged to.</param>
    /// <param name="needsRetry">Whether another attempt could still improve the outcome.</param>
    /// <returns>The next step — see <see cref="DesktopLayerRecoveryStep"/>.</returns>
    public DesktopLayerRecoveryStep OnAttemptCompleted(int generation, bool needsRetry)
    {
        if (!IsActive || generation != Generation) return DesktopLayerRecoveryStep.Superseded;

        if (!needsRetry)
        {
            IsActive = false;
            return DesktopLayerRecoveryStep.Completed;
        }

        if (AttemptsMade < MaxAttempts) return DesktopLayerRecoveryStep.Retry;

        IsActive = false;
        return DesktopLayerRecoveryStep.Exhausted;
    }

    /// <summary>
    /// Abandons any active recovery (application shutdown). Scheduled attempts then find
    /// <see cref="TryBeginAttempt"/> answering <see langword="false"/>.
    /// </summary>
    public void Cancel()
    {
        Generation++;
        IsActive = false;
    }
}
