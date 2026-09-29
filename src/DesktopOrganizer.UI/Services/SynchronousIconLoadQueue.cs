using System.IO;
using System.Threading;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Fallback <see cref="IIconLoadQueue"/> (ADR-046 D-13) used whenever no real queue was supplied through
/// DI — e.g. an older test's <c>new ChildWindowViewModel(...)</c> built with fewer constructor arguments,
/// or any headless caller with no live UI <see cref="SynchronizationContext"/>. Resolves every request
/// inline, synchronously, exactly like the pre-ADR-046 code did, and hands the result back to
/// <c>onResults</c> from inside the very same <see cref="IIconLoadGroup.Submit"/> call — no posting, no
/// background thread, no <see cref="SynchronizationContext"/> capture needed at all. Existing tests that
/// construct a ViewModel without an explicit queue therefore keep seeing the old, fully synchronous
/// behavior without any changes of their own.
/// </summary>
/// <param name="iconProvider">Resolves each request's icon, exactly as the pre-ADR-046 code did.</param>
internal sealed class SynchronousIconLoadQueue(IIconProvider iconProvider) : IIconLoadQueue
{
    /// <inheritdoc />
    public IIconLoadGroup CreateGroup(
        IconLoadPriority priority, SynchronizationContext deliveryContext, Action<IReadOnlyList<IconLoadResult>> onResults)
        => new SynchronousIconLoadGroup(iconProvider, priority, onResults);

    /// <inheritdoc />
    public void Dispose()
    {
        // No resources of its own: every Submit call already ran and delivered its results inline before
        // returning, so there is never anything left in flight to tear down.
    }

    /// <summary>
    /// The <see cref="IIconLoadGroup"/> half of <see cref="SynchronousIconLoadQueue"/>: resolves every
    /// submitted request immediately, on the calling thread, and reports it as <see cref="IconLoadOutcome.Loaded"/>.
    /// </summary>
    private sealed class SynchronousIconLoadGroup(
        IIconProvider iconProvider, IconLoadPriority priority, Action<IReadOnlyList<IconLoadResult>> onResults)
        : IIconLoadGroup
    {
        private bool _disposed;

        /// <inheritdoc />
        public IconLoadPriority Priority { get; set; } = priority;

        /// <inheritdoc />
        public int Generation { get; private set; }

        /// <inheritdoc />
        public void Submit(IReadOnlyList<IconLoadRequest> requests)
        {
            // A disposed fallback group behaves like a disposed real one (D-8): no further delivery.
            if (_disposed || requests.Count == 0) return;

            var generation = Generation;
            var results = new IconLoadResult[requests.Count];
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var size = IconPixelSize.Snap(request.PixelSize);
                var icon = iconProvider.GetIcon(request.Path, size);

                // Same existence/directory-ness computation the pre-ADR-046 FileItem constructor and
                // BaseViewModel.RefreshItemsLiveness made inline — File.Exists first (cheaper, and a file
                // never also reports Directory.Exists) matches FileItem.ComputeExists exactly.
                var isFile = File.Exists(request.Path);
                var exists = isFile || Directory.Exists(request.Path);
                results[i] = new IconLoadResult(request.Path, size, generation, IconLoadOutcome.Loaded, icon, exists, exists && !isFile);
            }

            onResults(results);
        }

        /// <inheritdoc />
        public void CancelPending()
        {
            // Nothing is ever actually pending (Submit already delivered before returning) — only the
            // generation itself needs to move, so a caller comparing IconLoadResult.Generation against a
            // stale value it captured earlier still behaves correctly.
            if (_disposed) return;
            Generation++;
        }

        /// <inheritdoc />
        public void Dispose() => _disposed = true;
    }
}
