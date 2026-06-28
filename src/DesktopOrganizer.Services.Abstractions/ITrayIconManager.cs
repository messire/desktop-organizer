namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Managing the application icon in the system tray.
/// </summary>
public interface ITrayIconManager : IDisposable
{
    /// <summary>
    /// Event triggered when the main application window should be displayed.
    /// </summary>
    event EventHandler? OnShowMainWindowRequested;

    /// <summary>
    /// Event triggered when the application is requested to exit.
    /// </summary>
    event EventHandler? OnExitRequested;

    /// <summary>
    /// Initializes the system tray icon.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Updates the context menu dynamically based on the application state.
    /// </summary>
    /// <param name="isMainWindowVisible">Indicates whether the main window is currently visible.</param>
    void UpdateContextMenu(bool isMainWindowVisible);
}
