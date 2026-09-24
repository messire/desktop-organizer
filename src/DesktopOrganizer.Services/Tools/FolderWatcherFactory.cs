using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Default implementation of <see cref="IFolderWatcherFactory"/>.
/// Creates a new <see cref="FolderWatcher"/> on each call to <see cref="Create"/>.
/// </summary>
/// <remarks>
/// <paramref name="diagnosticsLog"/> is resolved eagerly (not the <c>Func&lt;IDiagnosticsLog&gt;</c>
/// lazy-resolution pattern <c>DiConfig</c> uses for the single-instance/state-store singletons) because
/// <see cref="IFolderWatcherFactory"/> is only ever resolved by the primary process's own UI graph
/// (<c>ChildWindowViewModel</c>) — the same reasoning already applied to <c>IconHandler</c>'s
/// constructor: a secondary process (ADR-023 R-12) never reaches this far.
/// </remarks>
internal sealed class FolderWatcherFactory(IDiagnosticsLog diagnosticsLog) : IFolderWatcherFactory
{
    /// <inheritdoc/>
    public IFolderWatcher Create() => new FolderWatcher(diagnosticsLog);
}
