namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Tunable constants for <see cref="StaIconLoadQueue"/> (ADR-046). Not a settings surface (CLAUDE.md
/// persistence freeze, plan C-1) — the public constructor always uses <see cref="Default"/>; the
/// per-value internal constructor exists solely so tests can shrink these numbers to something a unit
/// test can actually hit (e.g. <see cref="MaxPendingJobs"/> of 3 instead of 20 000).
/// </summary>
/// <param name="StuckJobTimeout">A job running longer than this is abandoned by the watchdog (D-4). Default 5 s.</param>
/// <param name="WatchdogPeriod">How often the watchdog scans for a stuck job (D-4). Default 500 ms.</param>
/// <param name="DeliveryBatchSize">Results buffered per group before an immediate flush (D-9). Default 64.</param>
/// <param name="DeliveryFlushInterval">Maximum time a partial batch waits before flushing (D-9). Default 16 ms.</param>
/// <param name="PathQuarantine">How long a Local-lane path is skipped after a watchdog abandon (D-4). Default 5 min.</param>
/// <param name="MaxAbandonedWorkersPerLane">Abandoned-but-not-yet-returned workers a lane tolerates before disabling itself (D-4). Default 2.</param>
/// <param name="MaxPendingJobs">Back-pressure cap on distinct <c>(path, size)</c> jobs in flight (D-10). Default 20 000.</param>
internal sealed record IconLoadQueueOptions(
    TimeSpan StuckJobTimeout,
    TimeSpan WatchdogPeriod,
    int DeliveryBatchSize,
    TimeSpan DeliveryFlushInterval,
    TimeSpan PathQuarantine,
    int MaxAbandonedWorkersPerLane,
    int MaxPendingJobs)
{
    /// <summary>Gets the production defaults — see each parameter's own remarks above.</summary>
    public static IconLoadQueueOptions Default { get; } = new(
        StuckJobTimeout: TimeSpan.FromSeconds(5),
        WatchdogPeriod: TimeSpan.FromMilliseconds(500),
        DeliveryBatchSize: 64,
        DeliveryFlushInterval: TimeSpan.FromMilliseconds(16),
        PathQuarantine: TimeSpan.FromMinutes(5),
        MaxAbandonedWorkersPerLane: 2,
        MaxPendingJobs: 20_000);
}
