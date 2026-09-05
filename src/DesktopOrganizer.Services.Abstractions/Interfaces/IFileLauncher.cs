namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Launches files and folders through the Windows shell.
/// </summary>
public interface IFileLauncher
{
    /// <summary>
    /// Opens the file or folder with its default shell action (double-click behaviour).
    /// </summary>
    /// <param name="path">Full path of the file or folder.</param>
    void Open(string path);

    /// <summary>
    /// Opens the containing folder in Explorer, selecting the file when possible.
    /// </summary>
    /// <param name="path">Full path of the file or folder.</param>
    void OpenContainingFolder(string path);

    /// <summary>
    /// Shows the Windows shell "Properties" dialog for the file or folder.
    /// </summary>
    /// <param name="path">Full path of the file or folder.</param>
    void ShowProperties(string path);
}
