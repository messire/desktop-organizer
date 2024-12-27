using System.Collections.ObjectModel;
using DesktopOrganizer.Core.Models;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using WindowState = System.Windows.WindowState;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Represents a child window with its properties.
/// </summary>
public class ChildWindowModel
{
    /// <summary>
    /// Gets or sets the unique identifier for the child window.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the title of the child window.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the position of the child window on the desktop.
    /// </summary>
    public Point Position { get; set; }

    /// <summary>
    /// Gets or sets the size of the child window.
    /// </summary>
    public Size Size { get; set; }

    /// <summary>
    /// Gets or sets whether the window is visible.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets the window state (normal, minimized, maximized).
    /// </summary>
    public WindowState WindowState { get; set; } = WindowState.Normal;

    /// <summary>
    /// Gets or sets the collection of file items displayed in the child window.
    /// </summary>
    public ObservableCollection<FileItem> FileItems { get; set; } = [];
}
