namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Abstracts the Windows Forms system tray icon (<c>NotifyIcon</c>) behind a testable seam (still
/// WinForms-typed).
/// </summary>
public interface INotifyIconAdapter : IDisposable
{
    /// <summary>
    /// Shows a balloon tip notification from the tray icon.
    /// </summary>
    /// <param name="timeout">Requested display duration, in milliseconds. Modern Windows versions may ignore this and use their own duration.</param>
    /// <param name="title">Balloon tip title.</param>
    /// <param name="text">Balloon tip body text.</param>
    /// <param name="icon">Icon shown alongside the balloon tip.</param>
    void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon);

    /// <summary>
    /// Sets the tray icon's image from an icon file on disk. Disposes the previously set icon.
    /// </summary>
    /// <param name="iconPath">Full path of the <c>.ico</c> file to display.</param>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist or cannot be read as an icon.</exception>
    public void SetIcon(string iconPath);

    /// <summary>
    /// Sets the tooltip text Windows shows when the pointer rests on the tray icon.
    /// </summary>
    /// <param name="text">The tooltip text (truncated to 127 characters, the Win32 limit, if longer).</param>
    void SetToolTip(string text);

    /// <summary>
    /// Sets the context menu shown when the user right-clicks the tray icon. The adapter does not take
    /// ownership of the menu and does not dispose it.
    /// </summary>
    /// <param name="menu">The context menu to attach.</param>
    void SetContextMenu(ContextMenuStrip menu);

    /// <summary>
    /// Raised when the user double-clicks the tray icon.
    /// </summary>
    event EventHandler DoubleClick;
}
