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
    /// Returns the parent window of <paramref name="hwnd"/>, or <see cref="IntPtr.Zero"/> when the
    /// window is top-level (or the handle is invalid).
    /// </summary>
    IntPtr GetParent(IntPtr hwnd);

    /// <summary>
    /// Returns the window class name of <paramref name="hwnd"/>, or an empty string on failure.
    /// </summary>
    string GetWindowClassName(IntPtr hwnd);

    /// <summary>
    /// Retrieves a handle to a window that has the specified relationship to <paramref name="hwnd"/>
    /// (<c>GetWindow</c>).
    /// </summary>
    /// <param name="hwnd">Window whose relative is requested.</param>
    /// <param name="command">
    /// One of the <c>GW_*</c> constants. The two Z-order ones are <c>GW_HWNDNEXT</c> = 2 (the sibling
    /// <b>below</b> <paramref name="hwnd"/> in the Z-order) and <c>GW_HWNDPREV</c> = 3 (the sibling
    /// <b>above</b> it). "Prev/next" refer to positions in the sibling list, which is ordered
    /// top-to-bottom — so <c>GW_HWNDPREV</c> is the higher window, not the older one.
    /// </param>
    /// <returns>
    /// The requested window, or <see cref="IntPtr.Zero"/> when no such window exists (e.g.
    /// <c>GW_HWNDPREV</c> on the top-most sibling) or the handle is invalid.
    /// </returns>
    IntPtr GetWindow(IntPtr hwnd, int command);

    /// <summary>
    /// Retrieves extended window style flags.
    /// </summary>
    IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>
    /// Sets extended window style flags.
    /// </summary>
    IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    /// <summary>
    /// Changes the size, position and Z-order of a window.
    /// </summary>
    /// <param name="hwnd">Handle of the window to reposition.</param>
    /// <param name="hwndInsertAfter">
    /// Window to place <paramref name="hwnd"/> behind, or one of the <c>HWND_*</c> pseudo-handles
    /// (<c>HWND_BOTTOM</c> = 1, <c>HWND_TOP</c> = 0, <c>HWND_TOPMOST</c> = -1, <c>HWND_NOTOPMOST</c> = -2).
    /// Ignored when <paramref name="flags"/> contains <c>SWP_NOZORDER</c>.
    /// </param>
    /// <param name="x">New left edge, in screen pixels. Ignored with <c>SWP_NOMOVE</c>.</param>
    /// <param name="y">New top edge, in screen pixels. Ignored with <c>SWP_NOMOVE</c>.</param>
    /// <param name="cx">New width, in pixels. Ignored with <c>SWP_NOSIZE</c>.</param>
    /// <param name="cy">New height, in pixels. Ignored with <c>SWP_NOSIZE</c>.</param>
    /// <param name="flags">Combination of the <c>SWP_*</c> constants.</param>
    /// <returns><see langword="true"/> when the call succeeded.</returns>
    bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

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

    /// <summary>
    /// Returns the handle of the window the user is currently working with,
    /// or <see cref="IntPtr.Zero"/> if there is none.
    /// </summary>
    IntPtr GetForegroundWindow();

    /// <summary>
    /// Returns the absolute PIDL for the given known-folder / CLSID GUID, or <see cref="IntPtr.Zero"/> on failure.
    /// Caller must release it via <see cref="FreePidl"/>.
    /// </summary>
    IntPtr GetKnownFolderPidl(Guid knownFolderId);

    /// <summary>
    /// Frees a PIDL previously obtained from <see cref="GetKnownFolderPidl"/> (CoTaskMemFree).
    /// </summary>
    void FreePidl(IntPtr pidl);

    /// <summary>
    /// Returns the native shell icon for the given absolute PIDL, or <see langword="null"/> on failure.
    /// The underlying HICON is destroyed before returning.
    /// </summary>
    System.Windows.Media.ImageSource? GetShellIconByPidl(IntPtr pidl, int sizePx);

    /// <summary>
    /// Shell-executes the item identified by the given absolute PIDL. Returns <see langword="false"/> on failure.
    /// </summary>
    bool ShellExecutePidl(IntPtr pidl, IntPtr ownerHwnd);

    /// <summary>
    /// Shows the native shell context menu for the given absolute PIDL at the supplied screen coordinates.
    /// </summary>
    void ShowShellContextMenu(IntPtr pidl, IntPtr ownerHwnd, int screenX, int screenY);

    /// <summary>
    /// Queries the Recycle Bin contents across all drives.
    /// </summary>
    RecycleBinNativeState QueryRecycleBin();

    /// <summary>
    /// Moves the given paths to the Recycle Bin (FO_DELETE | FOF_ALLOWUNDO).
    /// </summary>
    bool SendToRecycleBin(string[] paths, IntPtr ownerHwnd);
}

/// <summary>
/// Native Recycle Bin query result.
/// </summary>
internal readonly record struct RecycleBinNativeState(long ItemCount, long TotalSize);
