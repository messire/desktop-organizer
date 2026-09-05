namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Observes mouse gestures on the desktop via a global low-level hook:
/// double-click on the empty desktop and right-button lasso.
/// </summary>
public interface IDesktopGestureService : IDisposable
{
    /// <summary>
    /// Raised when the user double-clicks an empty area of the desktop.
    /// </summary>
    event EventHandler? DesktopDoubleClicked;

    /// <summary>
    /// Raised when the user finishes a right-button lasso drag on the desktop.
    /// </summary>
    event EventHandler<DesktopLassoEventArgs>? LassoCompleted;

    /// <summary>
    /// Installs the global mouse hook. Must be called from the UI thread (needs a message loop).
    /// </summary>
    void Start();
}
