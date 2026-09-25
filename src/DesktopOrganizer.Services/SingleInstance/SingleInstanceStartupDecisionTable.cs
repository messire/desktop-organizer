using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Truth table mapping the outcome of a completed handshake to the
/// <see cref="SingleInstanceStartupDecision"/> the process entry point must act on (ADR-023 D-4/D-5/
/// D-7/D-8). Pure — takes the already-settled outcome as input, performs no handshake steps itself.
/// </summary>
public static class SingleInstanceStartupDecisionTable
{
    /// <summary>
    /// Maps a completed handshake outcome to the decision the process entry point must act on.
    /// </summary>
    /// <param name="acquired">
    /// Whether this process ended up owning the guard mutex. When <see langword="true"/> the process
    /// is primary regardless of <paramref name="serverStarted"/> — a failed pipe server means
    /// activation from future secondaries will not work, but the primary still starts in a degraded
    /// mode rather than refusing to run (ADR-023 D-4).
    /// </param>
    /// <param name="serverStarted">
    /// Whether <c>ISingleInstanceCoordinator.TryBeginServing</c> succeeded. Only meaningful when
    /// <paramref name="acquired"/> is <see langword="true"/>; carried through for the caller's
    /// diagnostics (e.g. <c>SingleInstancePrimaryAcquired.serverStarted</c>) — it does not change
    /// <see cref="SingleInstanceStartupDecision.Action"/>.
    /// </param>
    /// <param name="last">
    /// Result of the most recent <c>TrySend</c>, or <see langword="null"/> if none was ever attempted
    /// (guard was free on the very first try).
    /// </param>
    /// <param name="attempts">Number of handshake attempts performed.</param>
    /// <param name="elapsedMs">Total handshake duration in milliseconds.</param>
    public static SingleInstanceStartupDecision Resolve(bool acquired, bool serverStarted, ActivationSendResult? last, int attempts, long elapsedMs)
    {
        if (acquired)
        {
            return new SingleInstanceStartupDecision(
                SingleInstanceStartupAction.RunAsPrimary,
                SingleInstanceRole.Primary,
                last,
                attempts,
                elapsedMs);
        }

        var action = last switch
        {
            ActivationSendResult.Delivered => SingleInstanceStartupAction.ExitAfterActivation,
            ActivationSendResult.OtherSession => SingleInstanceStartupAction.ExitOtherSession,

            // Budget exhausted without a successful delivery: no listener, timed out, protocol
            // mismatch, or an unexpected failure — none of these can be resolved by staying up
            // (ADR-023 D-7: no writable secondary instance under any of these conditions).
            _ => SingleInstanceStartupAction.ExitActivationFailed,
        };

        return new SingleInstanceStartupDecision(action, SingleInstanceRole.Secondary, last, attempts, elapsedMs);
    }
}
