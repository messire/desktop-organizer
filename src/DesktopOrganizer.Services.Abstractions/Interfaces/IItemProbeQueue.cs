namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Off-UI-thread existence/directory-ness probing for panel liveness checks (ADR-046, 7c) — replaces the
/// per-item <c>File.Exists</c>/<c>Directory.Exists</c> calls <c>ChildWindowViewModel.RefreshExistence</c>
/// used to make directly on the UI thread.
/// </summary>
public interface IItemProbeQueue
{
    /// <summary>
    /// Probes every path in <paramref name="paths"/>. Runs off the calling thread; the returned task's
    /// continuation resumes on the caller's <see cref="SynchronizationContext"/> (ordinary <c>await</c>
    /// semantics) — never faults except with an <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    /// <param name="paths">The paths to probe.</param>
    /// <param name="cancellationToken">Cancels the whole batch.</param>
    /// <returns>
    /// Results in the same order as <paramref name="paths"/> — one <see cref="PathProbeResult"/> per input path.
    /// </returns>
    Task<IReadOnlyList<PathProbeResult>> ProbeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}
