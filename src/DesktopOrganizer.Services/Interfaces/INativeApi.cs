namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Callback delegate for <see cref="INativeApi.EnumWindows"/>.
/// </summary>
internal delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

/// <summary>
/// Utilities provided access to native functions via P/Invoke.
/// </summary>
internal interface INativeApi
{
    /// <summary>
    /// Retrieves file information, including its icon, using Shell API.
    /// </summary>
    IntPtr GetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    /// <summary>
    /// Destroys icon and releases its resources.
    /// </summary>
    bool DestroyIcon(IntPtr iconHandle);

    /// <summary>
    /// Finds a top-level window by class name and/or window name.
    /// </summary>
    IntPtr FindWindow(string className, string? windowName);

    /// <summary>
    /// Finds a child window by class name, starting the search after <paramref name="childAfter"/>.
    /// </summary>
    IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    /// <summary>
    /// Sends a message to a window.
    /// </summary>
    IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Enumerates all top-level windows, invoking <paramref name="callback"/> for each.
    /// </summary>
    bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    /// <summary>
    /// Changes the parent window of the specified child window.
    /// </summary>
    IntPtr SetParent(IntPtr child, IntPtr newParent);

    /// <summary>
    /// Retrieves extended window style flags.
    /// </summary>
    IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>
    /// Sets extended window style flags.
    /// </summary>
    IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    /// <summary>
    /// Returns the current cursor position in screen coordinates.
    /// </summary>
    System.Drawing.Point GetCursorPos();

    /// <summary>
    /// Opens the Windows shell "Properties" dialog for the given file or folder.
    /// </summary>
    /// <param name="path">Full path of the file or folder.</param>
    /// <returns><see langword="true"/> if the dialog was invoked successfully.</returns>
    bool ShowFileProperties(string path);

    /// <summary>
    /// Sets the show state of a window (e.g. hide/show).
    /// </summary>
    /// <param name="hwnd">Window handle.</param>
    /// <param name="command">One of the SW_* constants.</param>
    bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>
    /// Returns whether the specified window is currently visible.
    /// </summary>
    bool IsWindowVisible(IntPtr hwnd);
}
