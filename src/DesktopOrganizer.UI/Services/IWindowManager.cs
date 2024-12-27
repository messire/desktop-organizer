using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

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
    /// Triggered when a child window is added.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowAdded;

    /// <summary>
    /// Triggered when a child window is removed.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowRemoved;

    /// <summary>
    /// Triggered when a child window is modified.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowModified;

    /// <summary>
    /// Shows or activates the main application window.
    /// </summary>
    void StartMainWindow();

    /// <summary>
    /// Creates a new child window.
    /// </summary>
    /// <param name="model">The model for the child window.</param>
    /// <returns>The created child window.</returns>
    ChildWindow CreateChildWindow(ChildWindowModel model);

    /// <summary>
    /// Removes a child window.
    /// </summary>
    /// <param name="windowId">The ID of the window to remove.</param>
    void RemoveChildWindow(Guid windowId);

    /// <summary>
    /// Gets all child windows.
    /// </summary>
    /// <returns>A collection of all child windows.</returns>
    IEnumerable<ChildWindowModel> GetAllChildWindows();

    /// <summary>
    /// Saves the state of all windows.
    /// </summary>
    void SaveWindowsState();

    /// <summary>
    /// Loads the state of all windows.
    /// </summary>
    void LoadWindowsState();
}
