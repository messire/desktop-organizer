using System.Diagnostics.CodeAnalysis;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Enums;

namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Callback delegate for <see cref="INativeApi.EnumWindows"/>.
/// </summary>
/// <param name="hwnd">Handle of the enumerated top-level window.</param>
/// <param name="lParam">The application-defined value passed through from <see cref="INativeApi.EnumWindows"/>.</param>
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
/// <param name="nCode">Hook code identifying how to process the message (<c>HC_ACTION</c> when non-negative).</param>
/// <param name="wParam">Identifier of the mouse message being processed.</param>
/// <param name="lParam">Pointer to an <c>MSLLHOOKSTRUCT</c> with the message details.</param>
internal delegate IntPtr LowLevelMouseCallback(int nCode, IntPtr wParam, IntPtr lParam);

/// <summary>
/// Window procedure callback for the hidden window created by
/// <see cref="INativeApi.CreateMessageWindow"/> (<c>WNDPROC</c>).
/// </summary>
/// <remarks>Must be kept alive by the caller for the whole lifetime of the window.</remarks>
/// <param name="hwnd">Handle of the window receiving the message.</param>
/// <param name="msg">The message identifier.</param>
/// <param name="wParam">The first message-specific parameter (<c>wParam</c>).</param>
/// <param name="lParam">The second message-specific parameter (<c>lParam</c>).</param>
internal delegate IntPtr NativeWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

/// <summary>
/// Utilities provided access to native functions via P/Invoke.
/// </summary>
internal interface INativeApi
{
    /// <summary>
    /// Retrieves file information, including its icon, using Shell API (<c>SHGetFileInfo</c>).
    /// </summary>
    /// <param name="filePath">Full path of the file or folder to query.</param>
    /// <param name="fileAttributes">File attributes to assume, used together with <c>SHGFI_USEFILEATTRIBUTES</c>.</param>
    /// <param name="fileInfo">Receives the queried shell file information.</param>
    /// <param name="fileInfoSize">Size, in bytes, of <paramref name="fileInfo"/>.</param>
    /// <param name="flags">Combination of the <c>SHGFI_*</c> flags controlling what is retrieved.</param>
    IntPtr GetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    /// <summary>
    /// Destroys icon and releases its resources (<c>DestroyIcon</c>).
    /// </summary>
    /// <param name="iconHandle">Handle of the icon to destroy.</param>
    bool DestroyIcon(IntPtr iconHandle);

    /// <summary>
    /// Finds a top-level window by class name and/or window name (<c>FindWindow</c>).
    /// </summary>
    /// <param name="className">Class name to match.</param>
    /// <param name="windowName">Window title to match, or <see langword="null"/> to match any title.</param>
    IntPtr FindWindow(string className, string? windowName);

    /// <summary>
    /// Finds a child window by class name, starting the search after <paramref name="childAfter"/>
    /// (<c>FindWindowEx</c>).
    /// </summary>
    /// <param name="parent">
    /// Parent whose children are searched, or <see cref="IntPtr.Zero"/> to search top-level windows
    /// (used for the WorkerW lookup).
    /// </param>
    /// <param name="childAfter">Child window to start the search after, or <see cref="IntPtr.Zero"/> to start from the first child.</param>
    /// <param name="className">Class name to match.</param>
    /// <param name="windowName">Window title to match, or <see langword="null"/> to match any title.</param>
    IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    /// <summary>
    /// Sends a message to a window and waits for it with an upper bound (<c>SendMessageTimeoutW</c> with
    /// <c>SMTO_NORMAL | SMTO_ABORTIFHUNG</c>). There deliberately is no plain, unbounded
    /// <c>SendMessage</c> on this façade: every cross-process send from the UI thread must be able to
    /// give up on a hung target (release-hardening review M9).
    /// </summary>
    /// <param name="hwnd">Handle of the destination window. <see cref="IntPtr.Zero"/> answers <see langword="false"/>.</param>
    /// <param name="msg">The message to send.</param>
    /// <param name="wParam">The first message-specific parameter (<c>wParam</c>).</param>
    /// <param name="lParam">The second message-specific parameter (<c>lParam</c>).</param>
    /// <param name="timeoutMs">Maximum time to wait, in milliseconds.</param>
    /// <param name="result">Receives the message result when delivered; <see cref="IntPtr.Zero"/> otherwise.</param>
    /// <returns>
    /// <see langword="true"/> when the target processed the message; <see langword="false"/> on timeout,
    /// when the target is hung (abandoned immediately), or on any other failure. Never throws.
    /// </returns>
    bool SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs, out IntPtr result);

    /// <summary>
    /// Enumerates all top-level windows, invoking <paramref name="callback"/> for each (<c>EnumWindows</c>).
    /// </summary>
    /// <param name="callback">Invoked once per top-level window; return <see langword="false"/> to stop enumeration early.</param>
    /// <param name="lParam">Application-defined value passed through unchanged to <paramref name="callback"/>.</param>
    bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    /// <summary>
    /// Changes the parent window of the specified child window (<c>SetParent</c>).
    /// </summary>
    /// <param name="child">Handle of the window whose parent is changed.</param>
    /// <param name="newParent">Handle of the new parent window, or <see cref="IntPtr.Zero"/> for the desktop window.</param>
    IntPtr SetParent(IntPtr child, IntPtr newParent);

    /// <summary>
    /// Returns the parent window of <paramref name="hwnd"/>, or <see cref="IntPtr.Zero"/> when the
    /// window is top-level (or the handle is invalid).
    /// </summary>
    /// <param name="hwnd">Window whose parent is requested.</param>
    IntPtr GetParent(IntPtr hwnd);

    /// <summary>
    /// Returns the window class name of <paramref name="hwnd"/>, or an empty string on failure.
    /// </summary>
    /// <param name="hwnd">Window whose class name is requested.</param>
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
    /// Retrieves a value associated with the specified window, e.g. its style or extended style
    /// (<c>GetWindowLongPtr</c>).
    /// </summary>
    /// <param name="hwnd">Window whose value is retrieved.</param>
    /// <param name="index">Zero-based offset, or one of the <c>GWL_*</c> constants, identifying the value to retrieve.</param>
    IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>
    /// Sets a value associated with the specified window, e.g. its style or extended style
    /// (<c>SetWindowLongPtr</c>).
    /// </summary>
    /// <param name="hwnd">Window whose value is set.</param>
    /// <param name="index">Zero-based offset, or one of the <c>GWL_*</c> constants, identifying the value to set.</param>
    /// <param name="newLong">The replacement value.</param>
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
    /// Sets the show state of a window (e.g. hide/show).
    /// </summary>
    /// <param name="hwnd">Window handle.</param>
    /// <param name="command">One of the SW_* constants.</param>
    bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>
    /// Returns whether the specified window is currently visible.
    /// </summary>
    /// <param name="hwnd">Window to query.</param>
    bool IsWindowVisible(IntPtr hwnd);

    /// <summary>
    /// Returns whether the user (or policy) turned desktop icons off in Explorer itself: the
    /// "View &gt; Show desktop icons" setting (<c>HKCU\…\Explorer\Advanced\HideIcons = 1</c>) or the
    /// "Hide and disable all items on the desktop" policy (<c>HKCU\…\Policies\Explorer\NoDesktop = 1</c>).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when either is on. <see langword="false"/> when both are off, absent, or
    /// unreadable. Never throws.
    /// </returns>
    /// <remarks>
    /// That state belongs to the user, not to this application: startup self-heal must never "repair"
    /// it (release-hardening review L5). A registry read, not P/Invoke, but kept behind this façade so
    /// the decision logic stays unit-testable.
    /// </remarks>
    bool IsExplorerDesktopIconsSettingOff();

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
    /// <param name="knownFolderId">The known-folder or CLSID GUID to resolve.</param>
    IntPtr GetKnownFolderPidl(Guid knownFolderId);

    /// <summary>
    /// Frees a PIDL previously obtained from <see cref="GetKnownFolderPidl"/> (CoTaskMemFree).
    /// </summary>
    /// <param name="pidl">The PIDL to free.</param>
    void FreePidl(IntPtr pidl);

    /// <summary>
    /// Returns the native shell icon for the given absolute PIDL, or <see langword="null"/> on failure.
    /// The underlying HICON is destroyed before returning.
    /// </summary>
    /// <param name="pidl">Absolute PIDL of the shell item whose icon is requested.</param>
    /// <param name="sizePx">
    /// Size hint: 16 or less selects the system small icon, anything larger the system large icon; the
    /// returned image is not scaled to this value.
    /// </param>
    System.Windows.Media.ImageSource? GetShellIconByPidl(IntPtr pidl, int sizePx);

    /// <summary>
    /// Shell-executes the item identified by the given absolute PIDL. Returns <see langword="false"/> on failure.
    /// </summary>
    /// <param name="pidl">Absolute PIDL of the shell item to execute.</param>
    /// <param name="ownerHwnd">Owner window for any UI the shell may show (e.g. an elevation prompt).</param>
    bool ShellExecutePidl(IntPtr pidl, IntPtr ownerHwnd);

    /// <summary>
    /// Shows the native shell context menu for the given absolute PIDL at the supplied screen coordinates.
    /// </summary>
    /// <remarks>
    /// Same tracking core as <see cref="ShowShellContextMenuForPath"/> (hidden foreground helper window as
    /// the menu owner, <c>IContextMenu2</c>/<c>IContextMenu3</c> message forwarding), with <c>CMF_NORMAL</c>
    /// (+ <c>CMF_EXTENDEDVERBS</c> while Shift is held) and no rename interception. Never throws.
    /// </remarks>
    /// <param name="pidl">Absolute PIDL of the shell item the menu is shown for.</param>
    /// <param name="ownerHwnd">The window the menu is opened from; the menu owner only when the helper window cannot be created.</param>
    /// <param name="screenX">Horizontal screen coordinate, in physical pixels, where the menu is shown.</param>
    /// <param name="screenY">Vertical screen coordinate, in physical pixels, where the menu is shown.</param>
    void ShowShellContextMenu(IntPtr pidl, IntPtr ownerHwnd, int screenX, int screenY);

    /// <summary>
    /// Shows the native shell context menu of a file-system item — the same menu Explorer shows for it —
    /// at the supplied screen coordinates, and invokes the command the user picks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be called on the UI (STA) thread. The path is parsed into a PIDL (<c>SHParseDisplayName</c>),
    /// bound to its parent <c>IShellFolder</c> (<c>SHBindToParent</c>) and asked for its <c>IContextMenu</c>
    /// (<c>GetUIObjectOf</c>). The menu is built with <c>CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME</c>
    /// (+ <c>CMF_EXTENDEDVERBS</c> while Shift is held) and tracked from a hidden, process-owned helper
    /// window that is made foreground for the duration of the menu (the panels are
    /// <c>WS_EX_NOACTIVATE</c>, and a menu whose owner is not foreground never dismisses on an outside
    /// click); the helper forwards <c>WM_INITMENUPOPUP</c>/<c>WM_DRAWITEM</c>/<c>WM_MEASUREITEM</c>/
    /// <c>WM_MENUCHAR</c> to <c>IContextMenu3::HandleMenuMsg2</c> (or <c>IContextMenu2::HandleMenuMsg</c>) so
    /// owner-drawn and lazily-filled submenus ("Open with", "Send to", "New") work.
    /// </para>
    /// <para>
    /// The shell's own "rename" verb is never invoked (it needs Explorer's view to host an editor) — it is
    /// reported as <see cref="ShellContextMenuOutcome.RenameRequested"/> instead. With
    /// <paramref name="interceptDelete"/> the canonical <c>delete</c> verb is likewise reported as
    /// <see cref="ShellContextMenuOutcome.DeleteRequested"/> and NOT invoked (the caller recycles the item
    /// itself — for an app-managed item from its original location, so a later "Restore" from the Recycle
    /// Bin puts it somewhere the user can see). Both verbs are recognized through <c>GetCommandString(GCS_VERBW)</c>;
    /// a handler that reports no canonical verb is invoked as usual. Every failure degrades to
    /// <see cref="ShellContextMenuOutcome.NotShown"/> with a diagnostics entry carrying only the failing stage,
    /// the item's extension and the <c>HRESULT</c>, never the path. Never throws.
    /// </para>
    /// </remarks>
    /// <param name="path">Full path of an existing file or folder.</param>
    /// <param name="ownerHwnd">
    /// The window the menu is opened from. Used as the menu owner only when the helper window cannot be
    /// created.
    /// </param>
    /// <param name="screenX">Horizontal screen coordinate, in physical pixels, where the menu is shown.</param>
    /// <param name="screenY">Vertical screen coordinate, in physical pixels, where the menu is shown.</param>
    /// <param name="interceptDelete">Report the <c>delete</c> verb instead of invoking it.</param>
    /// <returns>How the menu ended, plus the invoked command's canonical verb; see <see cref="ShellContextMenuResult"/>.</returns>
    ShellContextMenuResult ShowShellContextMenuForPath(string path, IntPtr ownerHwnd, int screenX, int screenY, bool interceptDelete);

    /// <summary>
    /// Whether <paramref name="hwnd"/> is this process's hidden shell-menu helper window — the never-shown
    /// top-level window that owns the tracked shell context menus and, per the <c>TrackPopupMenu</c> foreground
    /// pattern (KB 135788), stays the foreground window after a menu closed.
    /// </summary>
    /// <remarks>
    /// Lets foreground-window heuristics (e.g. "which virtual desktop is the user looking at") ignore it: the
    /// helper was never shown, so it has no meaningful virtual-desktop assignment. Never throws.
    /// </remarks>
    /// <param name="hwnd">The window to test.</param>
    /// <returns><see langword="true"/> for the helper window; <see langword="false"/> otherwise (including <see cref="IntPtr.Zero"/>).</returns>
    bool IsOwnHelperWindow(IntPtr hwnd);

    /// <summary>
    /// Queries the Recycle Bin contents across all drives.
    /// </summary>
    RecycleBinNativeState QueryRecycleBin();

    /// <summary>
    /// Moves the given paths to the Recycle Bin (FO_DELETE | FOF_ALLOWUNDO).
    /// </summary>
    /// <param name="paths">Full paths of the files/folders to move to the Recycle Bin.</param>
    /// <param name="ownerHwnd">
    /// Owner window for <c>SHFileOperation</c>. The flags are <c>FOF_ALLOWUNDO | FOF_NOCONFIRMATION |
    /// FOF_NOERRORUI | FOF_WANTNUKEWARNING</c>: an ordinary recycle shows no UI, but an item that cannot
    /// go to the Recycle Bin (too large, drive without one) makes the shell ask before deleting it for
    /// good instead of silently destroying it (ADR-035).
    /// </param>
    bool SendToRecycleBin(string[] paths, IntPtr ownerHwnd);

    /// <summary>
    /// Moves the given paths to the Recycle Bin in a single <c>SHFileOperationW</c> batch
    /// (<c>FO_DELETE</c> with <c>FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_WANTNUKEWARNING</c>,
    /// ADR-035). Same flags as <see cref="SendToRecycleBin"/> (used by the System Icons panel);
    /// this variant is for app-managed panel items that must NEVER be destroyed permanently: with
    /// <c>FOF_WANTNUKEWARNING</c> the shell asks the user before deleting an item that can't go to the
    /// Recycle Bin (too large, drive without a Recycle Bin) instead of silently nuking it, and the
    /// progress dialog is kept (no <c>FOF_SILENT</c>) so a long operation can be cancelled.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI (STA) thread — the shell may show UI. The return value only reflects
    /// what the shell reported; callers must decide what was actually recycled by checking which
    /// paths still exist afterwards (the operation can partially succeed). Never throws.
    /// </remarks>
    /// <param name="paths">Full paths of the files/folders to recycle. Must exist; must not be <c>\\?\</c> long paths.</param>
    /// <param name="ownerHwnd">Owner window for any shell UI, or <see cref="IntPtr.Zero"/>.</param>
    /// <returns>
    /// <see langword="true"/> if the shell reported success with no aborted operations; otherwise
    /// <see langword="false"/> (error, user declined the permanent-delete warning, or cancelled).
    /// </returns>
    bool RecycleItems(string[] paths, IntPtr ownerHwnd);

    // ── Low-level mouse hook (WH_MOUSE_LL), see ADR-003 / ADR-012 ────────────────────────────────

    /// <summary>
    /// Returns the module handle of <paramref name="moduleName"/>, or of the current process when
    /// <see langword="null"/> is passed.
    /// </summary>
    /// <param name="moduleName">Name of the module to look up, or <see langword="null"/> for the current process.</param>
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
    /// <param name="hookHandle">Handle of the hook to remove.</param>
    bool UnhookWindowsHookEx(IntPtr hookHandle);

    /// <summary>
    /// Passes the hook information to the next hook procedure in the chain.
    /// </summary>
    /// <param name="hookHandle">The handle returned by <c>SetWindowsHookEx</c> (ignored by modern Windows).</param>
    /// <param name="nCode">Hook code passed to the current hook procedure.</param>
    /// <param name="wParam">Identifier of the message being processed.</param>
    /// <param name="lParam">Pointer to the message-specific data, forwarded unchanged.</param>
    IntPtr CallNextHookEx(IntPtr hookHandle, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Reads the cursor position out of an <c>MSLLHOOKSTRUCT</c> pointed to by the hook's
    /// <c>lParam</c>. Screen coordinates, physical pixels.
    /// </summary>
    /// <param name="lParam">The hook's <c>lParam</c>; a pointer to an <c>MSLLHOOKSTRUCT</c>.</param>
    System.Drawing.Point ReadMouseHookPoint(IntPtr lParam);

    /// <summary>
    /// Returns the window at the given screen point, or <see cref="IntPtr.Zero"/>.
    /// </summary>
    /// <param name="screenX">Horizontal screen coordinate, in physical pixels.</param>
    /// <param name="screenY">Vertical screen coordinate, in physical pixels.</param>
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
    /// <param name="listView"><c>SysListView32</c> handle of Explorer's desktop list view.</param>
    /// <param name="screenX">Horizontal screen coordinate, in physical pixels, to test.</param>
    /// <param name="screenY">Vertical screen coordinate, in physical pixels, to test.</param>
    bool IsListViewPointEmpty(IntPtr listView, int screenX, int screenY);

    // ── Monitors & hidden broadcast window ──────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates the attached display monitors. Rectangles are physical pixels (the process is
    /// PerMonitorV2, so they are never DPI-virtualized). Each entry also carries the monitor's effective
    /// DPI (<c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>), or 0 when that is unavailable.
    /// Returns an empty list on failure.
    /// </summary>
    IReadOnlyList<NativeMonitorInfo> GetMonitorInfos();

    /// <summary>
    /// System (primary monitor) DPI scale — 1.0 at 96 DPI (<see cref="DesktopScaleModel.ScaleFromDpi"/>).
    /// Returns 1.0 on failure.
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
    /// <param name="window">The window (and class atom) to destroy/unregister.</param>
    void DestroyMessageWindow(NativeMessageWindow window);

    /// <summary>
    /// Default window procedure (<c>DefWindowProc</c>) for messages the caller does not handle.
    /// </summary>
    /// <param name="hwnd">Handle of the window receiving the message.</param>
    /// <param name="msg">The message identifier.</param>
    /// <param name="wParam">The first message-specific parameter (<c>wParam</c>).</param>
    /// <param name="lParam">The second message-specific parameter (<c>lParam</c>).</param>
    IntPtr DefaultWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ── Explorer restart recovery (ADR-034) ─────────────────────────────────────────────────────

    /// <summary>
    /// Registers (or looks up) a system-wide unique window message (<c>RegisterWindowMessage</c>), e.g.
    /// the shell's <c>"TaskbarCreated"</c> broadcast.
    /// </summary>
    /// <param name="messageName">The message name.</param>
    /// <returns>The message id (in the <c>0xC000</c>–<c>0xFFFF</c> range), or 0 on failure. Never throws.</returns>
    int RegisterWindowMessage(string messageName);

    /// <summary>
    /// Lets <paramref name="msg"/> through the UIPI message filter of <paramref name="hwnd"/>
    /// (<c>ChangeWindowMessageFilterEx(MSGFLT_ALLOW)</c>). Needed for <c>TaskbarCreated</c> to reach an
    /// elevated process from a non-elevated Explorer.
    /// </summary>
    /// <param name="hwnd">A window owned by the calling thread.</param>
    /// <param name="msg">The message to allow.</param>
    /// <returns><see langword="true"/> on success. Best effort — never throws.</returns>
    bool AllowWindowMessage(IntPtr hwnd, int msg);

    /// <summary>
    /// Returns whether <paramref name="hwnd"/> names an existing window (<c>IsWindow</c>).
    /// <see cref="IntPtr.Zero"/> answers <see langword="false"/>. Never throws.
    /// </summary>
    /// <param name="hwnd">Window handle to test.</param>
    bool IsWindow(IntPtr hwnd);

    /// <summary>
    /// Returns the real parent of <paramref name="hwnd"/> (<c>GetAncestor(GA_PARENT)</c>) — unlike
    /// <see cref="GetParent"/>, never the owner of a <c>WS_POPUP</c> window (WPF panels are popups with
    /// a hidden owner, so <c>GetParent</c> would report that owner, not <c>WorkerW</c>). For a top-level
    /// window this is the desktop window; <see cref="IntPtr.Zero"/> on failure. Never throws.
    /// </summary>
    /// <param name="hwnd">Window whose parent is requested.</param>
    IntPtr GetAncestorParent(IntPtr hwnd);

    /// <summary>
    /// Returns the root window of <paramref name="hwnd"/> (<c>GetAncestor(GA_ROOT)</c>) — the top-level
    /// window at the end of its parent chain, never an owner. For a top-level window (a panel on the
    /// Windows 11 24H2+ layout) that is <paramref name="hwnd"/> itself; for a panel embedded into
    /// <c>WorkerW</c> it is the shell's top-level window whose DPI every embedded panel shares
    /// (release-hardening F, <see cref="DesktopLayerMode"/>). <see cref="IntPtr.Zero"/> on failure. Never throws.
    /// </summary>
    /// <param name="hwnd">Window whose root is requested.</param>
    IntPtr GetRootWindow(IntPtr hwnd);

    /// <summary>
    /// Returns the id of the process that created <paramref name="hwnd"/> (<c>GetWindowThreadProcessId</c>).
    /// </summary>
    /// <param name="hwnd">Window to query.</param>
    /// <returns>The process id, or <c>0</c> for <see cref="IntPtr.Zero"/>/a dead handle. Never throws.</returns>
    /// <remarks>
    /// Read-only, in-kernel, sends no message to the target window — safe to call from inside a window
    /// procedure (ADR-034 amendment 2026-10-09: the desktop-band walk runs inside <c>WM_WINDOWPOSCHANGING</c>).
    /// </remarks>
    uint GetWindowProcessId(IntPtr hwnd);

    /// <summary>
    /// Reads the Z-order half of the <c>WINDOWPOS</c> structure a <c>WM_WINDOWPOSCHANGING</c> message's
    /// <c>lParam</c> points to.
    /// </summary>
    /// <param name="windowPos">
    /// The message's <c>lParam</c> (a pointer to <c>WINDOWPOS</c>), valid by the contract of
    /// <c>WM_WINDOWPOSCHANGING</c> — only that message's own <c>lParam</c> may be passed, never any other pointer.
    /// </param>
    /// <param name="insertAfter">The proposed <c>hwndInsertAfter</c>.</param>
    /// <param name="flags">The proposed <c>SWP_*</c> flags.</param>
    /// <returns>
    /// <see langword="false"/> for a null pointer; <see langword="true"/> otherwise. There is no "unreadable
    /// structure" outcome: the pointer is not validated (an invalid one is a caller bug that no managed
    /// <c>catch</c> could absorb).
    /// </returns>
    bool TryReadWindowPosZOrder(IntPtr windowPos, out IntPtr insertAfter, out uint flags);

    /// <summary>
    /// Overwrites <c>hwndInsertAfter</c> and <c>flags</c> of the <c>WINDOWPOS</c> structure a
    /// <c>WM_WINDOWPOSCHANGING</c> message's <c>lParam</c> points to — the documented way for a window to
    /// veto or redirect a Z-order change of itself before it happens. Position and size are left untouched.
    /// </summary>
    /// <param name="windowPos">
    /// The message's <c>lParam</c> (a pointer to <c>WINDOWPOS</c>), valid by the contract of
    /// <c>WM_WINDOWPOSCHANGING</c>. Null is a no-op; any other pointer is written without validation.
    /// </param>
    /// <param name="insertAfter">The new <c>hwndInsertAfter</c>.</param>
    /// <param name="flags">The new <c>SWP_*</c> flags.</param>
    void WriteWindowPosZOrder(IntPtr windowPos, IntPtr insertAfter, uint flags);

    /// <summary>
    /// Starts (or restarts) a <c>WM_TIMER</c> timer on <paramref name="hwnd"/> (<c>SetTimer</c> without a
    /// <c>TIMERPROC</c>) — the tick arrives through that window's own window procedure, on its thread.
    /// </summary>
    /// <param name="hwnd">A window owned by the calling thread.</param>
    /// <param name="timerId">Non-zero timer id, delivered as the <c>WM_TIMER</c> <c>wParam</c>.</param>
    /// <param name="intervalMs">Interval in milliseconds.</param>
    /// <returns><see langword="true"/> on success. Never throws.</returns>
    bool SetTimer(IntPtr hwnd, IntPtr timerId, uint intervalMs);

    /// <summary>
    /// Stops a timer started with <see cref="SetTimer"/> (<c>KillTimer</c>). Never throws.
    /// </summary>
    /// <param name="hwnd">The window the timer was started on.</param>
    /// <param name="timerId">The timer id passed to <see cref="SetTimer"/>.</param>
    /// <returns><see langword="true"/> when a timer was stopped.</returns>
    bool KillTimer(IntPtr hwnd, IntPtr timerId);

    // ── System theme and window frame (design system 1.x, phase 6.1, ADR-047 D-2) ────────────────

    /// <summary>
    /// Reads a <c>REG_DWORD</c> value under <c>HKEY_CURRENT_USER</c> (read-only access; managed
    /// <c>Microsoft.Win32.Registry</c>, no P/Invoke).
    /// </summary>
    /// <param name="keyPath">Sub-key path below <c>HKCU</c>, e.g. <c>Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>.</param>
    /// <param name="valueName">Value name.</param>
    /// <returns>The value, or <see langword="null"/> when the key/value is absent, of another type, or unreadable. Never throws.</returns>
    int? ReadCurrentUserDword(string keyPath, string valueName);

    /// <summary>
    /// Returns whether a high-contrast theme is currently active (<c>SPI_GETHIGHCONTRAST</c>,
    /// <c>HCF_HIGHCONTRASTON</c>), queried live — not a value cached at startup.
    /// </summary>
    /// <returns><see langword="true"/> when high contrast is on; <see langword="false"/> when it is off or the query failed. Never throws.</returns>
    bool IsHighContrastOn();

    /// <summary>
    /// Returns the current value of a Win32 system colour (<c>GetSysColor</c>), queried live — after a
    /// <c>WM_SYSCOLORCHANGE</c> (e.g. switching one high-contrast theme to another) the new value is
    /// returned, unlike WPF's cached <c>SystemColors</c>.
    /// </summary>
    /// <param name="index">The <c>COLOR_*</c> index, e.g. <c>COLOR_WINDOW</c> (5).</param>
    /// <returns>The colour as a <c>COLORREF</c> (<c>0x00BBGGRR</c>); 0 when the query failed. Never throws.</returns>
    int GetSystemColor(int index);

    /// <summary>
    /// Returns the size of the system message font (<c>SPI_GETNONCLIENTMETRICS</c>,
    /// <c>lfMessageFont.lfHeight</c>) converted to device-independent units (1/96 inch) at the system DPI —
    /// the same quantity WPF exposes as <c>SystemFonts.MessageFontSize</c>, but queried live rather than
    /// from WPF's cache.
    /// </summary>
    /// <returns>The font size in DIPs; 0 when the query failed. Never throws.</returns>
    double GetMessageFontSize();

    /// <summary>
    /// Sets a 4-byte (<c>BOOL</c>/<c>DWORD</c>/enum-valued) DWM window attribute
    /// (<c>DwmSetWindowAttribute</c>), e.g. <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>. Which attribute id is
    /// valid on which Windows build is the caller's decision — this is a thin wrapper.
    /// </summary>
    /// <param name="hwnd">Top-level window handle.</param>
    /// <param name="attribute">The <c>DWMWINDOWATTRIBUTE</c> id.</param>
    /// <param name="value">The 4-byte value to set.</param>
    /// <returns>
    /// The <c>HRESULT</c> (≥ 0 = success). A missing <c>dwmapi.dll</c>/export or any marshalling failure is
    /// reported as a failure <c>HRESULT</c>, never as an exception.
    /// </returns>
    int SetDwmWindowAttribute(IntPtr hwnd, int attribute, int value);

    /// <summary>
    /// Calls the undocumented <c>uxtheme.dll</c> export ordinal 135 — <c>SetPreferredAppMode</c> on
    /// Windows 10 1903 (build 18362) and later — which selects whether the classic Win32 popup menus
    /// (<c>TrackPopupMenuEx</c>: the shell context menu of an icon, a window's system menu) of THIS process
    /// are drawn light or dark (design system 1.x, phase 6.4b, ADR-047 D-11). Which build the ordinal is
    /// valid on is the caller's decision (<c>NativeMenuTheming</c> gates it) — this is a thin wrapper. The
    /// export is resolved by ordinal through <c>GetProcAddress</c> from the System32 copy of the DLL; it is
    /// never called when it cannot be resolved.
    /// </summary>
    /// <param name="mode">The <c>PreferredAppMode</c> value: 0 = Default, 1 = AllowDark, 2 = ForceDark, 3 = ForceLight.</param>
    /// <returns><see langword="true"/> when the export was found and called; <see langword="false"/> otherwise. Never throws.</returns>
    bool TrySetPreferredAppMode(int mode);

    /// <summary>
    /// Calls the undocumented <c>uxtheme.dll</c> export ordinal 136 — <c>FlushMenuThemes</c> — so menus
    /// opened after a <see cref="TrySetPreferredAppMode"/> change pick the new light/dark menu theme up
    /// instead of the cached one. Same gating/resolution rules as <see cref="TrySetPreferredAppMode"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the export was found and called; <see langword="false"/> otherwise. Never throws.</returns>
    bool TryFlushMenuThemes();

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
    /// <remarks>
    /// Called from hot window-procedure paths: a failure is logged at most once per handle per minute
    /// (with the number of suppressed repeats), so a dead HWND cannot flood the log. Never throws.
    /// </remarks>
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
    /// Per-window DPI scale (<c>GetDpiForWindow</c> through <see cref="DesktopScaleModel.ScaleFromDpi"/>), falling back to
    /// <see cref="GetSystemDpiScale"/> when the window DPI is unavailable (invalid handle, or a
    /// pre-1607 Windows where the export does not exist).
    /// </summary>
    /// <param name="hwnd">Window whose DPI is requested.</param>
    /// <returns>Scale factor, 1.0 at 96 DPI. Never returns 0 and never throws.</returns>
    double GetDpiScaleForWindow(IntPtr hwnd);

    /// <summary>
    /// Asks <c>DefWindowProc</c>'s modal move/size loop running for <paramref name="hwnd"/> to stop
    /// (ADR-022 phase 0-B, G-2): sends <c>WM_CANCELMODE</c> to the window — whose default processing
    /// releases the mouse capture the loop runs on — and, if the window still owns the capture afterwards,
    /// calls <c>ReleaseCapture</c> directly. The loop notices the lost capture and exits on its next
    /// iteration (the caller is typically still nested inside it); <c>WM_EXITSIZEMOVE</c> follows then.
    /// </summary>
    /// <param name="hwnd">The window whose sizing loop is to be cancelled.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="hwnd"/> owned the mouse capture (i.e. a loop was running
    /// for it on the calling thread) and the cancel request was issued; <see langword="false"/> otherwise
    /// (zero handle, no capture, or a failure). Never throws.
    /// </returns>
    /// <remarks>
    /// Must be called on the thread that owns <paramref name="hwnd"/> (the WPF UI thread): <c>GetCapture</c>
    /// is per-thread and <c>SendMessage</c> to a same-thread window is a direct, synchronous call.
    /// MANUAL VERIFY (Win10/Win11): whether the loop keeps the last live-resize frame or restores the
    /// starting rectangle on a capture loss is <c>DefWindowProc</c>'s business and not observable headless.
    /// </remarks>
    bool CancelSizingLoop(IntPtr hwnd);

    // ── High-resolution panel item icons (IShellItemImageFactory / system image list) ───────────
    // Kept as one contiguous block (with its own region in NativeApi.cs) so it merges cleanly with
    // unrelated shell work (e.g. the context-menu code above).

    /// <summary>
    /// Renders the shell's own icon for an existing file-system item as a bitmap
    /// (<c>SHCreateItemFromParsingName</c> → <c>IShellItemImageFactory::GetImage</c> with
    /// <c>SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK</c>). This is what Explorer shows: a <c>.lnk</c>
    /// resolves to its target's icon, an <c>.exe</c>/<c>.ico</c> to its embedded icon, a folder to its
    /// <c>desktop.ini</c> custom icon — at the requested size instead of the 32 px shell icon.
    /// </summary>
    /// <remarks>
    /// Expected on an STA thread — since ADR-046 the background icon worker's, previously the UI thread:
    /// some third-party icon handlers are apartment-threaded. Works on an MTA thread for the built-in
    /// handlers. Never throws — a COM/marshalling exception is reported
    /// as its <see cref="Exception.HResult"/>.
    /// </remarks>
    /// <param name="path">Full path of an existing file or folder.</param>
    /// <param name="sizePx">Requested square size in physical pixels. The result may be larger (<c>SIIGBF_BIGGERSIZEOK</c>).</param>
    /// <param name="bitmapHandle">
    /// Receives the <c>HBITMAP</c> on success, <see cref="IntPtr.Zero"/> otherwise. The caller owns it and
    /// must release it through <see cref="DeleteBitmap"/>.
    /// </param>
    /// <returns>The <c>HRESULT</c> of the failing step, or 0 (<c>S_OK</c>) on success.</returns>
    int GetShellItemImage(string path, int sizePx, out IntPtr bitmapHandle);

    /// <summary>
    /// Copies the pixels of a 32-bpp <c>HBITMAP</c> (e.g. from <see cref="GetShellItemImage"/>) into a
    /// managed, top-down BGRA buffer (<c>GetObject</c> + <c>GetDIBits</c>). Does not take ownership of the
    /// handle. Never throws.
    /// </summary>
    /// <param name="bitmapHandle">The bitmap to read. Must not be selected into a device context.</param>
    /// <param name="pixels">Receives the pixel data on success; <see langword="default"/> otherwise.</param>
    /// <returns><see langword="true"/> when the whole bitmap was read.</returns>
    bool TryReadBitmapPixels(IntPtr bitmapHandle, out NativeBitmapPixels pixels);

    /// <summary>
    /// Releases a bitmap handle (<c>DeleteObject</c>). <see cref="IntPtr.Zero"/> answers <see langword="false"/>.
    /// Never throws.
    /// </summary>
    /// <param name="bitmapHandle">The <c>HBITMAP</c> to delete.</param>
    /// <returns><see langword="true"/> when the handle was released.</returns>
    bool DeleteBitmap(IntPtr bitmapHandle);

    /// <summary>
    /// Extracts icon <paramref name="iconIndex"/> of one of the shell's system image lists
    /// (<c>SHGetImageList</c> → <c>ImageList_GetIcon(ILD_TRANSPARENT)</c>). The index comes from
    /// <see cref="GetFileInfo"/> with <c>SHGFI_SYSICONINDEX</c>.
    /// </summary>
    /// <remarks>Same threading expectation as <see cref="GetShellItemImage"/>. Never throws.</remarks>
    /// <param name="imageList">
    /// One of the <c>SHIL_*</c> constants: <c>SHIL_LARGE</c> = 0 (32 px), <c>SHIL_SMALL</c> = 1 (16 px),
    /// <c>SHIL_EXTRALARGE</c> = 2 (48 px), <c>SHIL_JUMBO</c> = 4 (256 px). Sizes are at 100 % system scale.
    /// </param>
    /// <param name="iconIndex">Index into the system image list.</param>
    /// <returns>
    /// A new <c>HICON</c> the caller must release through <see cref="DestroyIcon"/>, or
    /// <see cref="IntPtr.Zero"/> on failure.
    /// </returns>
    IntPtr GetSystemImageListIcon(int imageList, int iconIndex);

    // ── Background icon loading (ADR-046, 7a-N) ─────────────────────────────────────────────────

    /// <summary>
    /// Reads a <c>.lnk</c>'s RAW stored target path — <c>IShellLinkW::GetPath(SLGP_RAWPATH)</c> after
    /// <c>IPersistFile::Load(STGM_READ)</c> — WITHOUT resolving the shortcut (<c>IShellLinkW::Resolve</c>
    /// is never called): no network access, no "fix broken shortcut" shell UI. Environment variables in
    /// the stored target are expanded. COM objects (RCWs) are released deterministically
    /// (<c>Marshal.ReleaseComObject</c> in a <c>finally</c>) on the calling thread.
    /// </summary>
    /// <remarks>Must run on an STA thread. Never throws — any failure is reported as a <see langword="false"/> return.</remarks>
    /// <param name="shortcutPath">Full path of an existing <c>.lnk</c> file.</param>
    /// <param name="targetPath">The raw target path on success.</param>
    /// <returns><see langword="true"/> when a path-based target was read; <see langword="false"/> for a PIDL-only link or any failure.</returns>
    bool TryGetShortcutRawTarget(string shortcutPath, [NotNullWhen(true)] out string? targetPath);

    /// <summary>
    /// Maps <c>GetDriveTypeW</c> for <paramref name="rootPath"/> to <see cref="DriveKind"/>.
    /// </summary>
    /// <param name="rootPath">Drive root, e.g. <c>"Z:\\"</c>.</param>
    /// <returns>The drive's kind, or <see cref="DriveKind.Unknown"/> on failure. Never throws.</returns>
    DriveKind GetDriveKind(string rootPath);
}

/// <summary>
/// Pixels of a native bitmap copied into managed memory by <see cref="INativeApi.TryReadBitmapPixels"/>:
/// 32 bits per pixel, byte order B, G, R, A, rows top-down, stride = <see cref="Width"/> × 4. The alpha
/// channel is passed through as-is — whether it is premultiplied (or present at all) is decided by the
/// consumer (<c>IconFactory</c>).
/// </summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Bgra">Pixel buffer, <c>Width × Height × 4</c> bytes.</param>
internal readonly record struct NativeBitmapPixels(int Width, int Height, ReadOnlyMemory<byte> Bgra)
{
    /// <summary>Gets the number of bytes per row.</summary>
    public int Stride => Width * 4;
}

/// <summary>
/// Native Recycle Bin query result.
/// </summary>
internal readonly record struct RecycleBinNativeState(long ItemCount, long TotalSize);

/// <summary>
/// A display monitor as reported by <c>GetMonitorInfo</c>. Rectangles are physical pixels;
/// conversion to WPF device-independent units is the caller's job.
/// </summary>
/// <param name="DeviceName">Stable device name (e.g. <c>\\.\DISPLAY1</c>).</param>
/// <param name="Bounds">Full monitor rectangle, physical pixels.</param>
/// <param name="WorkArea">Work area, physical pixels.</param>
/// <param name="IsPrimary">Whether this is the primary monitor.</param>
/// <param name="EffectiveDpi">
/// Effective DPI of the monitor (<c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>); 0 when unknown (API missing,
/// call failed, or a caller that does not supply it) — consumers then substitute the system scale.
/// </param>
internal readonly record struct NativeMonitorInfo(
    string DeviceName,
    System.Drawing.Rectangle Bounds,
    System.Drawing.Rectangle WorkArea,
    bool IsPrimary,
    uint EffectiveDpi = 0);

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
