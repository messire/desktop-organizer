using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Default implementation of <see cref="IFolderWatcherFactory"/>.
/// Creates a new <see cref="FolderWatcher"/> on each call to <see cref="Create"/>.
/// </summary>
internal sealed class FolderWatcherFactory : IFolderWatcherFactory
{
    /// <inheritdoc/>
    public IFolderWatcher Create() => new FolderWatcher();
}
