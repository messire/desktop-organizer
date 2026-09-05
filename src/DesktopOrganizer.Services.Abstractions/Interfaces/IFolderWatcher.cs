namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Watches a single folder for file-system changes and raises <see cref="Changed"/>
/// on each modification. Obtain an instance from <see cref="IFolderWatcherFactory"/>.
/// </summary>
public interface IFolderWatcher : IDisposable
{
    /// <summary>
    /// Raised when a file or directory inside the watched folder is created, deleted,
    /// renamed, or when a resync is required due to a buffer overflow or watcher error.
    /// </summary>
    event EventHandler<FolderChangeEventArgs> Changed;

    /// <summary>
    /// Starts watching <paramref name="folderPath"/> for changes.
    /// Calling <see cref="Start"/> again replaces the currently watched folder.
    /// </summary>
    /// <param name="folderPath">The absolute path of the folder to watch.</param>
    void Start(string folderPath);

    /// <summary>
    /// Stops watching for changes without disposing the watcher.
    /// </summary>
    void Stop();
}
