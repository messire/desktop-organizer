using System.IO;
using System.Threading;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Fallback <see cref="IItemProbeQueue"/> (ADR-046 D-13) used whenever no real probe queue was supplied
/// through DI — see <see cref="SynchronousIconLoadQueue"/>'s remarks for why this exists. Probes every
/// path inline, synchronously, on the calling thread — exactly the <c>File.Exists</c>/<c>Directory.Exists</c>
/// pair the pre-ADR-046 <c>ChildWindowViewModel.RefreshExistence</c> made directly — and returns an
/// already-completed task, so existing tests observe the same synchronous timing as before.
/// </summary>
internal sealed class SynchronousItemProbeQueue : IItemProbeQueue
{
    /// <inheritdoc />
    public Task<IReadOnlyList<PathProbeResult>> ProbeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new PathProbeResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var isFile = File.Exists(path);
            var exists = isFile || Directory.Exists(path);

            // IsKnown is always true here: there is no circuit breaker in the synchronous fallback, so
            // every probe is a genuine (if potentially slow, on a UNC path) file-system observation —
            // matching the legacy File.Exists semantics PathProbeResult itself documents.
            results[i] = new PathProbeResult(path, exists, exists && !isFile, IsKnown: true);
        }

        return Task.FromResult<IReadOnlyList<PathProbeResult>>(results);
    }
}
