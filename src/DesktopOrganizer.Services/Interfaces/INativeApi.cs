using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Callback delegate for <see cref="INativeApi.EnumWindows"/>.
/// </summary>
internal delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

/// <summary>
/// Callback delegate for the low-level mouse hook installed by
/// <see cref="INativeApi.SetWindowsHookEx"/> (<c>WH_MOUSE_LL</c>, <c>HOOKPROC</c>).
/// </summary>
/// <remarks>
/// The instance passed to <see cref="INativeApi.SetWindowsHookEx"/> must be kept alive (rooted in a
/// field) by the caller for the whole lifetime of the hook — the native side holds only an unmanaged
/// thunk, which the GC does not track.
/// </remarks>
internal delegate IntPtr LowLevelMouseCallback(int nCode, IntPtr wParam, IntPtr lParam);

/// <summary>
/// Window procedure callback for the hidden window created by
/// <see cref="INativeApi.CreateMessageWindow"/> (<c>WNDPROC</c>).
/// </summary>
/// <remarks>Must be kept alive by the caller for the whole lifetime of the window.</remarks>
internal delegate IntPtr NativeWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

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
    /// Hands this process's right to set the foreground window over to another process
    /// (<c>AllowSetForegroundWindow</c>).
    /// </summary>
    /// <param name="processId">
    /// Target process id, or <c>ASFW_ANY</c> (<c>-1</c>) to allow any process to take the foreground
    /// until the next call that changes it. <c>ASFW_ANY</c> is what the single-instance activation
    /// client uses (ADR-023 D-8/R-13): a secondary process knows the primary only by pipe name, never
    /// by pid, and the grant is consumed within milliseconds by the primary's own
    /// <c>Window.Activate()</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the call succeeded. <see langword="false"/> means this process did
    /// not hold the right to give away in the first place (it is not in the foreground, the foreground
    /// is locked, or the target process no longer exists) — the caller's worst case is then the
    /// pre-existing behavior: the activated window flashes in the taskbar instead of coming forward.
    /// Never throws.
    /// </returns>
    bool AllowSetForegroundWindow(int processId);

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

    // ── Low-level mouse hook (WH_MOUSE_LL), see ADR-003 / ADR-012 ────────────────────────────────

    /// <summary>
    /// Returns the module handle of <paramref name="moduleName"/>, or of the current process when
    /// <see langword="null"/> is passed.
    /// </summary>
    IntPtr GetModuleHandle(string? moduleName);

    /// <summary>
    /// Installs a global hook procedure. The caller owns the returned handle and must release it
    /// through <see cref="UnhookWindowsHookEx"/>.
    /// </summary>
    /// <param name="hookId">One of the <c>WH_*</c> constants (this overload targets <c>WH_MOUSE_LL</c> = 14).</param>
    /// <param name="callback">Hook procedure; must stay rooted by the caller until unhooked.</param>
    /// <param name="moduleHandle">Module owning the procedure; for <c>WH_MOUSE_LL</c> the current module.</param>
    /// <param name="threadId">Thread to hook, or 0 for all threads on the desktop.</param>
    /// <returns>The hook handle, or <see cref="IntPtr.Zero"/> on failure.</returns>
    IntPtr SetWindowsHookEx(int hookId, LowLevelMouseCallback callback, IntPtr moduleHandle, uint threadId);

    /// <summary>
    /// Removes a hook previously installed with <see cref="SetWindowsHookEx"/>.
    /// </summary>
    bool UnhookWindowsHookEx(IntPtr hookHandle);

    /// <summary>
    /// Passes the hook information to the next hook procedure in the chain.
    /// </summary>
    IntPtr CallNextHookEx(IntPtr hookHandle, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Reads the cursor position out of an <c>MSLLHOOKSTRUCT</c> pointed to by the hook's
    /// <c>lParam</c>. Screen coordinates, physical pixels.
    /// </summary>
    System.Drawing.Point ReadMouseHookPoint(IntPtr lParam);

    /// <summary>
    /// Returns the window at the given screen point, or <see cref="IntPtr.Zero"/>.
    /// </summary>
    IntPtr WindowFromPoint(int screenX, int screenY);

    /// <summary>
    /// Returns the system double-click interval, in milliseconds.
    /// </summary>
    uint GetDoubleClickTime();

    /// <summary>
    /// Cross-process <c>LVM_HITTEST</c> against Explorer's desktop <c>SysListView32</c>: reports
    /// whether the given screen point hits no list item (i.e. bare desktop between the icons).
    /// </summary>
    /// <remarks>
    /// Fully defensive: any failure — insufficient rights, bitness mismatch, or a hung
    /// <c>explorer.exe</c> — returns <see langword="false"/> ("not empty"), never throws. The message
    /// is sent with a short timeout and <c>SMTO_ABORTIFHUNG</c>, because this runs inside the global
    /// mouse hook where a blocking call would stall input for the whole OS.
    /// </remarks>
    bool IsListViewPointEmpty(IntPtr listView, int screenX, int screenY);

    // ── Monitors & hidden broadcast window ──────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates the attached display monitors. Rectangles are physical pixels.
    /// Returns an empty list on failure.
    /// </summary>
    IReadOnlyList<NativeMonitorInfo> GetMonitorInfos();

    /// <summary>
    /// System (primary monitor) DPI scale — 1.0 at 96 DPI. Returns 1.0 on failure.
    /// </summary>
    double GetSystemDpiScale();

    /// <summary>
    /// Primary screen size in physical pixels (<c>SM_CXSCREEN</c>/<c>SM_CYSCREEN</c>).
    /// </summary>
    System.Drawing.Size GetPrimaryScreenSizePx();

    /// <summary>
    /// Registers a window class and creates a hidden top-level window used only to receive broadcast
    /// messages (<c>WM_DISPLAYCHANGE</c>/<c>WM_SETTINGCHANGE</c>).
    /// </summary>
    /// <param name="className">Unique class name to register.</param>
    /// <param name="windowProc">Window procedure; must stay rooted by the caller until destroyed.</param>
    /// <returns>
    /// The created window. On failure <see cref="NativeMessageWindow.IsValid"/> is
    /// <see langword="false"/> and <see cref="NativeMessageWindow.LastError"/> carries the Win32 error.
    /// </returns>
    NativeMessageWindow CreateMessageWindow(string className, NativeWindowProc windowProc);

    /// <summary>
    /// Destroys a window created by <see cref="CreateMessageWindow"/> and unregisters its class.
    /// Safe to call with a default/invalid value.
    /// </summary>
    void DestroyMessageWindow(NativeMessageWindow window);

    /// <summary>
    /// Default window procedure (<c>DefWindowProc</c>) for messages the caller does not handle.
    /// </summary>
    IntPtr DefaultWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ── Native panel sizing primitives (ADR-022 / Phase 2) ──────────────────────────────────────

    /// <summary>
    /// Window bounding rectangle (<c>GetWindowRect</c>). The result is ALWAYS in screen coordinates,
    /// regardless of whether the window is top-level or re-parented into <c>WorkerW</c> — which is why
    /// it is the calibration reference for the (undocumented for re-parented popups) coordinate space
    /// of <c>WM_SIZING</c> (Phase 2 plan R-3).
    /// </summary>
    /// <param name="hwnd">Window to measure.</param>
    /// <param name="rect">Receives the screen rectangle in physical pixels, or default on failure.</param>
    /// <returns><see langword="true"/> when the call succeeded.</returns>
    bool GetWindowRect(IntPtr hwnd, out NativeRectPx rect);

    /// <summary>
    /// Reads the <c>RECT</c> pointed to by the <c>lParam</c> of <c>WM_SIZING</c>/<c>WM_DPICHANGED</c>.
    /// Direct analogue of <see cref="ReadMouseHookPoint"/> — same technique, same level of abstraction.
    /// </summary>
    /// <param name="lParam">Message <c>lParam</c>; a pointer to a native <c>RECT</c>.</param>
    /// <returns>The rectangle, in the message's own coordinate space (physical pixels).</returns>
    NativeRectPx ReadRect(IntPtr lParam);

    /// <summary>
    /// Writes a <c>RECT</c> back into the memory pointed to by <paramref name="lParam"/>.
    /// <c>WM_SIZING</c> is answered by modifying the proposed rectangle in place.
    /// </summary>
    /// <param name="lParam">Message <c>lParam</c>; a pointer to a native <c>RECT</c>.</param>
    /// <param name="rect">Rectangle to store, in the message's coordinate space (physical pixels).</param>
    void WriteRect(IntPtr lParam, NativeRectPx rect);

    /// <summary>
    /// Per-window DPI scale (<c>GetDpiForWindow</c> / 96.0), falling back to
    /// <see cref="GetSystemDpiScale"/> when the window DPI is unavailable (invalid handle, or a
    /// pre-1607 Windows where the export does not exist).
    /// </summary>
    /// <param name="hwnd">Window whose DPI is requested.</param>
    /// <returns>Scale factor, 1.0 at 96 DPI. Never returns 0 and never throws.</returns>
    double GetDpiScaleForWindow(IntPtr hwnd);
}

/// <summary>
/// Native Recycle Bin query result.
/// </summary>
internal readonly record struct RecycleBinNativeState(long ItemCount, long TotalSize);

/// <summary>
/// A display monitor as reported by <c>GetMonitorInfo</c>. Rectangles are physical pixels;
/// conversion to WPF device-independent units is the caller's job.
/// </summary>
internal readonly record struct NativeMonitorInfo(
    string DeviceName,
    System.Drawing.Rectangle Bounds,
    System.Drawing.Rectangle WorkArea,
    bool IsPrimary);

/// <summary>
/// Handle pair of a hidden broadcast window plus the class atom that has to be unregistered with it.
/// </summary>
/// <param name="Handle">Window handle, or <see cref="IntPtr.Zero"/> when creation failed.</param>
/// <param name="ClassAtom">Registered class atom, or 0 when registration failed.</param>
/// <param name="LastError">Win32 error of the failing step, or 0 on success.</param>
internal readonly record struct NativeMessageWindow(IntPtr Handle, ushort ClassAtom, int LastError)
{
    /// <summary>
    /// Gets a value indicating whether the window was created successfully.
    /// </summary>
    public bool IsValid => Handle != IntPtr.Zero;

    /// <summary>
    /// Gets a value indicating whether anything at all needs to be released.
    /// </summary>
    public bool HasResources => Handle != IntPtr.Zero || ClassAtom != 0;
}
