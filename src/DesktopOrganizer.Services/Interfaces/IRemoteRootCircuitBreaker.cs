namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Per-network-root circuit breaker (ADR-046 D-3): once a root proves slow or stuck, every job and probe
/// against it is failed fast instead of each independently paying for its own SMB timeout. After the
/// quarantine the root is half-open: exactly one trial call (the first <see cref="BeginCall"/>) is let
/// through; it closes the root by completing under the slow threshold, or re-opens it for another full
/// quarantine by being slow or stuck.
/// </summary>
internal interface IRemoteRootCircuitBreaker
{
    /// <summary>
    /// Whether <paramref name="rootKey"/> is currently open (quarantined), half-open with its single trial
    /// call still in flight, OR an in-flight call against it has already run longer than the slow threshold
    /// (treated as open for every OTHER concurrent caller, even before that call itself reports
    /// completion/stuck). Asking reserves nothing: only <see cref="BeginCall"/> claims the half-open trial.
    /// </summary>
    /// <param name="rootKey">The root key, as returned by <see cref="IRemotePathClassifier"/>.</param>
    bool IsOpen(string rootKey);

    /// <summary>
    /// Marks the start of a call against <paramref name="rootKey"/>, for in-flight slow-call tracking; on a
    /// half-open root with no trial in flight, this call becomes the trial. Disposing the result reports the
    /// call's completion (and elapsed time) back to the breaker; disposing it more than once is a no-op.
    /// </summary>
    /// <param name="rootKey">The root key the call is about to touch.</param>
    /// <returns>A scope whose <see cref="IDisposable.Dispose"/> reports completion.</returns>
    IDisposable BeginCall(string rootKey);

    /// <summary>
    /// Reports that a call against <paramref name="rootKey"/> was abandoned by the watchdog (stuck longer
    /// than the job timeout) — opens the root for the full quarantine period immediately, regardless of
    /// the slow threshold used by ordinary <see cref="BeginCall"/> completions. The longest-running in-flight
    /// call against the root is taken to be the abandoned one (every stuck call has outlived the job timeout,
    /// so the oldest is always one of them): it is dropped from in-flight tracking so it cannot keep the root
    /// open past the quarantine, and its scope's late <see cref="IDisposable.Dispose"/>, should the stuck call
    /// ever return, is ignored. A stuck half-open trial counts as a failed trial.
    /// </summary>
    /// <param name="rootKey">The root key the abandoned call was against.</param>
    void ReportStuck(string rootKey);
}
