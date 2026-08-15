namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Managing the application icon in the system tray.
/// </summary>
public interface ITrayIconManager : IDisposable
{
    /// <summary>
    /// Event triggered when the main application window should be shown/activated (never hidden).
    /// Raised by the tray context menu's "Settings" item, which is an explicit "open" action.
    /// </summary>
    event EventHandler? OnShowMainWindowRequested;

    /// <summary>
    /// Event triggered when the main application window's visibility should be toggled — shown if
    /// hidden, hidden if currently visible. Raised by double-clicking the tray icon.
    /// </summary>
    event EventHandler? OnToggleMainWindowVisibilityRequested;

    /// <summary>
    /// Event triggered when the application is requested to exit.
    /// </summary>
    event EventHandler? OnExitRequested;

    /// <summary>
    /// Initializes the system tray icon.
    /// </summary>
    void Initialize();

    /// <summary>
    /// (Re)builds the tray context menu. The menu items are currently static (not dependent on
    /// application state), but this remains a distinct step from <see cref="Initialize"/> so future
    /// state-dependent items (if any) have a natural place to refresh from.
    /// </summary>
    /// <param name="isMainWindowVisible">Indicates whether the main window is currently visible.</param>
    void UpdateContextMenu(bool isMainWindowVisible);
}
