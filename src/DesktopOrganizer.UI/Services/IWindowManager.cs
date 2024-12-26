namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages the application's windows.
/// </summary>
public interface IWindowManager
{
    /// <summary>
    /// Triggered when the visibility of the main window changes.
    /// </summary>
    event EventHandler<bool> OnMainWindowVisibilityChanged;

    /// <summary>
    /// Shows or activates the main application window.
    /// </summary>
    void ShowMainWindow();
}
