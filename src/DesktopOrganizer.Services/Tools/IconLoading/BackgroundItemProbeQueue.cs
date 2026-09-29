using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Background liveness-probe queue (ADR-046 D-12, 7c) — the <see cref="IItemProbeQueue"/> a panel's
/// existence-liveness tick uses instead of calling <c>File.Exists</c>/<c>Directory.Exists</c> directly on
/// the UI thread. Runs entirely on the <see cref="ThreadPool"/> — deliberately NOT one of
/// <see cref="StaIconLoadQueue"/>'s dedicated STA lanes: a liveness probe never touches shell/COM, and
/// sharing nothing with the icon queue's lanes means a stuck network liveness probe can never stall icon
/// loading for the same panel (D-12's whole rationale). Bounded to
/// <see cref="DefaultMaxConcurrentBatches"/> concurrent batches (or an explicit, test-supplied cap) via a
/// <see cref="SemaphoreSlim"/>, so an unbounded number of panels ticking at once cannot flood the pool
/// with blocking SMB calls; the pool itself is never blocked waiting for a permit —
/// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> is itself awaited, not <c>.Wait()</c>.
/// </summary>
internal sealed class BackgroundItemProbeQueue : IItemProbeQueue
{
    /// <summary>Default cap on batches probing concurrently (D-12).</summary>
    internal const int DefaultMaxConcurrentBatches = 2;

    private readonly IPathProbe _pathProbe;
    private readonly IDiagnosticsLog _diagnosticsLog;
    private readonly SemaphoreSlim _batchThrottle;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundItemProbeQueue"/> class with the production
    /// concurrency cap (<see cref="DefaultMaxConcurrentBatches"/>).
    /// </summary>
    /// <param name="pathProbe">One cheap existence/directory-ness probe per path (D-11) — shared with <see cref="StaIconLoadQueue"/>'s circuit breaker (D-3).</param>
    /// <param name="diagnosticsLog">Structured diagnostics sink for a probe that unexpectedly threw.</param>
    public BackgroundItemProbeQueue(IPathProbe pathProbe, IDiagnosticsLog diagnosticsLog)
        : this(pathProbe, diagnosticsLog, DefaultMaxConcurrentBatches)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundItemProbeQueue"/> class with an explicit
    /// concurrency cap. Internal — lets tests shrink it to exercise the throttle deterministically.
    /// </summary>
    /// <param name="pathProbe">One cheap existence/directory-ness probe per path.</param>
    /// <param name="diagnosticsLog">Structured diagnostics sink.</param>
    /// <param name="maxConcurrentBatches">Maximum number of <see cref="ProbeAsync"/> batches running at once.</param>
    internal BackgroundItemProbeQueue(IPathProbe pathProbe, IDiagnosticsLog diagnosticsLog, int maxConcurrentBatches)
    {
        ArgumentNullException.ThrowIfNull(pathProbe);
        ArgumentNullException.ThrowIfNull(diagnosticsLog);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentBatches, 1);

        _pathProbe = pathProbe;
        _diagnosticsLog = diagnosticsLog;
        _batchThrottle = new SemaphoreSlim(maxConcurrentBatches, maxConcurrentBatches);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PathProbeResult>> ProbeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        if (paths.Count == 0) return [];

        // L-2 (review it.2): this is a Services-layer, thread-pool-only method with no UI-thread caller
        // context worth resuming on — ConfigureAwait(false) avoids capturing/restoring a SynchronizationContext
        // that no caller here actually needs (BaseViewModel's own await of this task already resumes on
        // ITS captured UI context regardless of what this method itself awaits internally).
        await _batchThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ProbeBatch(paths, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _batchThrottle.Release();
        }
    }

    private IReadOnlyList<PathProbeResult> ProbeBatch(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var results = new PathProbeResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = ProbeOne(paths[i]);
        }

        return results;
    }

    /// <summary>
    /// Probes a single path, degrading a completely unexpected <see cref="IPathProbe"/> failure to
    /// <c>(Exists: false, IsKnown: false)</c> rather than faulting the whole batch — <see cref="IPathProbe.Probe"/>'s
    /// own contract already says it never throws, so this is belt-and-suspenders, not the expected path.
    /// </summary>
    /// <param name="path">The path to probe.</param>
    private PathProbeResult ProbeOne(string path)
    {
        try
        {
            return _pathProbe.Probe(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogDiagnostic("ItemProbeFailed", new KeyValuePair<string, object?>("errorType", ex.GetType().Name));
            return new PathProbeResult(path, Exists: false, IsDirectory: false, IsKnown: false);
        }
    }

    private void LogDiagnostic(string name, params KeyValuePair<string, object?>[] payload)
    {
        if (_diagnosticsLog.IsEnabled) _diagnosticsLog.Write("liveness", name, null, payload);
    }
}
