namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Creates independent <see cref="IFolderWatcher"/> instances.
/// Each Folder Portal owns its own watcher; the factory is a singleton.
/// </summary>
public interface IFolderWatcherFactory
{
    /// <summary>
    /// Creates a new, unstarted <see cref="IFolderWatcher"/>.
    /// The caller is responsible for calling <see cref="IFolderWatcher.Start"/> and
    /// for disposing the instance when no longer needed.
    /// </summary>
    /// <returns>A new <see cref="IFolderWatcher"/> instance.</returns>
    IFolderWatcher Create();
}
