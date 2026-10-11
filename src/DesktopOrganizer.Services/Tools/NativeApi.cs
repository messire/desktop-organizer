using System.Diagnostics.CodeAnalysis;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Enums;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal sealed partial class NativeApi : INativeApi, IDisposable
{
    /// <summary>
    /// <see cref="PackageProbeResultCode"/> when <c>GetCurrentPackageFullName</c> could not be called at all.
    /// </summary>
    internal const int PackageProbeUnavailableCode = -1;

    private const string ExplorerAdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerPoliciesKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    // Package-identity probe (Microsoft Store channel): evaluated once per process on first read, safe for
    // concurrent first readers. Static — AppStorageRoot needs the answer before DI exists.
    private static readonly Lazy<(bool IsPackaged, int ResultCode)> PackageProbe =
        new(RunPackageProbe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Per-HWND rate limit for the <c>GetWindowRectFailed</c> warning (release-hardening review L11):
    /// <see cref="GetWindowRect"/> runs from hot window-procedure paths, and a dead HWND must not be able
    /// to write one line per message.
    /// </summary>
    private readonly HandleLogRateLimiter _getWindowRectFailures;

    /// <summary>
    /// Rate limit for the <c>ListViewHitTestFailed</c> error, which can only be raised from inside the
    /// <c>WH_MOUSE_LL</c> callback and therefore must neither flood the log nor be written on that thread.
    /// </summary>
    private readonly HandleLogRateLimiter _hitTestFailures;

    private readonly IAppLog? _appLog;

    // Undocumented uxtheme menu-theme exports (design system 1.x, phase 6.4b, ADR-047 D-11): the System32
    // uxtheme.dll module, loaded (ref-counted) on the first TrySetPreferredAppMode/TryFlushMenuThemes call
    // and released in Dispose; IntPtr.Zero when it could not be loaded.
    private readonly Lazy<IntPtr> _uxTheme = new(LoadUxTheme, LazyThreadSafetyMode.ExecutionAndPublication);

    // Review L-2: 1 once Dispose released (or declined to load) the uxtheme reference. Guards the single
    // NativeLibrary.Free against a repeated Dispose, and keeps GetUxThemeExport from resolving an export of
    // a freed module — or lazily loading a new, never-freed reference — after disposal.
    private int _uxThemeReleased;

    // ── Native shell context menu state (UI thread only) ─────────────────────────────────────────
    // The hidden helper window that owns every tracked shell context menu (see EnsureShellMenuHost) and
    // its rooted window-procedure delegate — the native side holds only an unmanaged thunk.
    private NativeMessageWindow _shellMenuHost;
    private NativeWindowProc? _shellMenuHostProc;

    // IContextMenu3/IContextMenu2 of the menu currently inside TrackPopupMenuEx; null otherwise. Read by
    // ShellMenuHostWndProc to forward WM_INITMENUPOPUP/WM_DRAWITEM/WM_MEASUREITEM/WM_MENUCHAR.
    private NativeMethods.IContextMenu3? _activeMenu3;
    private NativeMethods.IContextMenu2? _activeMenu2;

    // Re-entrancy guard: never nest a second shell menu into the first one's modal loop.
    private bool _shellMenuActive;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeApi"/> class. Logs through the ambient
    /// <see cref="AppLog.Current"/> on the real clock.
    /// </summary>
    public NativeApi()
        : this(null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeApi"/> class with an explicit log and clock
    /// (tests).
    /// </summary>
    /// <param name="appLog">Log to write to; the ambient <see cref="AppLog.Current"/> when <see langword="null"/>.</param>
    /// <param name="timeProvider">Clock for log rate limiting; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    internal NativeApi(IAppLog? appLog, TimeProvider? timeProvider)
    {
        _appLog = appLog;
        _getWindowRectFailures = new HandleLogRateLimiter(TimeSpan.FromMinutes(1), timeProvider);
        _hitTestFailures = new HandleLogRateLimiter(TimeSpan.FromMinutes(1), timeProvider);
    }

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public IntPtr GetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags)
        => NativeMethods.ShGetFileInfo(filePath, fileAttributes, ref fileInfo, fileInfoSize, flags);

    /// <inheritdoc />
    public bool DestroyIcon(IntPtr iconHandle)
        => NativeMethods.DestroyIconByHandle(iconHandle);

    /// <inheritdoc />
    public IntPtr FindWindow(string className, string? windowName)
        => NativeMethods.FindWindowW(className, windowName);

    /// <inheritdoc />
    public IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName)
        => NativeMethods.FindWindowExW(parent, childAfter, className, windowName);

    /// <inheritdoc />
    public bool SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return false;

        // A zero return means timeout, hung target (SMTO_ABORTIFHUNG gives up at once) or failure alike.
        var sent = NativeMethods.SendMessageTimeoutW(
            hwnd,
            unchecked((int)msg),
            wParam,
            lParam,
            NativeMethods.SmtoNormal | NativeMethods.SmtoAbortIfHung,
            timeoutMs,
            out result);
        return sent != IntPtr.Zero;
    }

    /// <inheritdoc />
    public bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam)
        => NativeMethods.EnumWindowsNative(callback, lParam);

    /// <inheritdoc />
    public IntPtr SetParent(IntPtr child, IntPtr newParent)
        => NativeMethods.SetParentNative(child, newParent);

    /// <inheritdoc />
    public IntPtr GetParent(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetParentNative(hwnd);

    /// <inheritdoc />
    public string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;

        // 256 chars is the documented maximum length of a registered window class name.
        var buffer = new System.Text.StringBuilder(256);
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    /// <inheritdoc />
    public IntPtr GetWindow(IntPtr hwnd, int command)
        => hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetWindowNative(hwnd, command);

    /// <inheritdoc />
    public IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => NativeMethods.GetWindowLongPtrW(hwnd, index);

    /// <inheritdoc />
    public IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong)
        => NativeMethods.SetWindowLongPtrW(hwnd, index, newLong);

    /// <inheritdoc />
    public bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags)
        => hwnd != IntPtr.Zero && NativeMethods.SetWindowPos(hwnd, hwndInsertAfter, x, y, cx, cy, flags);

    /// <inheritdoc />
    public System.Drawing.Point GetCursorPos()
    {
        NativeMethods.GetCursorPos(out var pt);
        return new System.Drawing.Point(pt.X, pt.Y);
    }

    /// <inheritdoc />
    public bool ShowWindow(IntPtr hwnd, int command)
        => NativeMethods.ShowWindow(hwnd, command);

    /// <inheritdoc />
    public bool IsWindowVisible(IntPtr hwnd)
        => NativeMethods.IsWindowVisible(hwnd);

    /// <inheritdoc />
    public bool IsExplorerDesktopIconsSettingOff()
        => ReadCurrentUserDword(ExplorerAdvancedKeyPath, "HideIcons") == 1
           || ReadCurrentUserDword(ExplorerPoliciesKeyPath, "NoDesktop") == 1;

    /// <inheritdoc />
    public IntPtr GetForegroundWindow()
        => NativeMethods.GetForegroundWindow();

    /// <inheritdoc />
    public bool AllowSetForegroundWindow(int processId)
    {
        try
        {
            if (NativeMethods.AllowSetForegroundWindow(processId)) return true;

            // Expected and harmless in the ADR-023 R-13 flow: a process that is not itself in the
            // foreground has no foreground right to hand over. Traced (not thrown, not surfaced) —
            // the caller degrades to "the primary's window flashes in the taskbar" rather than failing.
            System.Diagnostics.Trace.WriteLine($"AllowSetForegroundWindow({processId}) failed, Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AllowSetForegroundWindow({processId}) threw: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public IntPtr GetKnownFolderPidl(Guid knownFolderId)
    {
        try
        {
            var id = knownFolderId;
            var hr = NativeMethods.SHGetKnownFolderIDList(ref id, 0, IntPtr.Zero, out var pidl);
            return hr == 0 ? pidl : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    public void FreePidl(IntPtr pidl)
    {
        if (pidl != IntPtr.Zero)
        {
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    /// <inheritdoc />
    public ImageSource? GetShellIconByPidl(IntPtr pidl, int sizePx)
    {
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        var iconHandle = IntPtr.Zero;
        try
        {
            var info = default(ShellFileInfo);
            var sizeFlag = sizePx <= 16 ? NativeMethods.ShgfiSmallIcon : NativeMethods.ShgfiLargeIcon;
            var flags = NativeMethods.ShgfiPidl | NativeMethods.ShgfiIcon | sizeFlag;

            var result = NativeMethods.SHGetFileInfoPidl(
                pidl,
                0,
                ref info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                flags);

            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            {
                return null;
            }

            iconHandle = info.IconHandle;
            var image = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIconByHandle(iconHandle);
            }
        }
    }

    /// <inheritdoc />
    public bool ShellExecutePidl(IntPtr pidl, IntPtr ownerHwnd)
    {
        if (pidl == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var info = new NativeMethods.ShellExecuteInfo
            {
                cbSize = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
                fMask = NativeMethods.SeeMaskInvokeIdList | NativeMethods.SeeMaskIdList,
                hwnd = ownerHwnd,
                lpVerb = null,
                lpFile = null,
                nShow = NativeMethods.SwShow,
                lpIDList = pidl,
            };

            return NativeMethods.ShellExecuteExW(ref info);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Must be called on an STA thread (WPF UI thread). IShellFolder and IContextMenu are
    /// apartment-threaded COM objects; TrackPopupMenuEx also requires the owning STA thread. Shares the
    /// tracking/invocation core with <see cref="ShowShellContextMenuForPath"/> (helper-window owner,
    /// foreground handling, IContextMenu2/3 message forwarding), but keeps its historical <c>CMF_NORMAL</c>
    /// menu (+ <c>CMF_EXTENDEDVERBS</c> while Shift is held) for the virtual System Icons items.</remarks>
    public void ShowShellContextMenu(IntPtr pidl, IntPtr ownerHwnd, int screenX, int screenY)
    {
        System.Diagnostics.Debug.Assert(
            System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA,
            "ShowShellContextMenu must be called on an STA thread.");

        if (pidl == IntPtr.Zero)
        {
            return;
        }

        // The caller (SystemShellService) owns and frees the pidl.
        ShowShellContextMenuCore(pidl, ownerHwnd, screenX, screenY, NativeMethods.CmfNormal, ShellVerbIntercepts.None, extension: null);
    }

    /// <inheritdoc />
    public ShellContextMenuResult ShowShellContextMenuForPath(string path, IntPtr ownerHwnd, int screenX, int screenY, bool interceptDelete)
    {
        System.Diagnostics.Debug.Assert(
            System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA,
            "ShowShellContextMenuForPath must be called on an STA thread.");

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ShellContextMenuResult(ShellContextMenuOutcome.NotShown, null);
        }

        var extension = GetExtensionForLog(path);
        var pidl = IntPtr.Zero;

        // MINOR 4: the stage that actually failed goes to the log, not always "ParseDisplayName".
        var stage = "ParseDisplayName";
        try
        {
            var hr = NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                LogShellMenuFailure(stage, extension, hr);
                return new ShellContextMenuResult(ShellContextMenuOutcome.NotShown, null);
            }

            var intercepts = interceptDelete
                ? ShellVerbIntercepts.Rename | ShellVerbIntercepts.Delete
                : ShellVerbIntercepts.Rename;

            // The core logs its own stages and never throws; anything reaching the catch below from here on
            // escaped its cleanup.
            stage = "ShowMenu";
            return ShowShellContextMenuCore(
                pidl,
                ownerHwnd,
                screenX,
                screenY,
                NativeMethods.CmfNormal | NativeMethods.CmfExplore | NativeMethods.CmfCanRename,
                intercepts,
                extension);
        }
        catch (Exception ex)
        {
            LogShellMenuFailure(stage, extension, ex.HResult);
            return new ShellContextMenuResult(ShellContextMenuOutcome.NotShown, null);
        }
        finally
        {
            // SHParseDisplayName allocates the absolute PIDL with the COM task allocator; this method owns it.
            if (pidl != IntPtr.Zero)
            {
                NativeMethods.CoTaskMemFree(pidl);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A pure handle comparison against the current helper window — no Win32 call, never throws. Expected on the UI
    /// thread (the only thread that creates/destroys the helper). Answers <see langword="false"/> for
    /// <see cref="IntPtr.Zero"/> and before the helper was ever created.
    /// </remarks>
    public bool IsOwnHelperWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && _shellMenuHost.IsValid && hwnd == _shellMenuHost.Handle;

    /// <summary>
    /// Destroys the hidden shell-menu helper window (see <see cref="EnsureShellMenuHost"/>) and unregisters
    /// its window class. Idempotent. Must run on the thread that created the window (the UI thread) for
    /// <c>DestroyWindow</c> to succeed; on any other thread the call fails harmlessly and the window dies with
    /// the process. Also releases this instance's <c>uxtheme.dll</c> reference exactly once; afterwards
    /// <see cref="TrySetPreferredAppMode"/>/<see cref="TryFlushMenuThemes"/> are no-ops answering
    /// <see langword="false"/>.
    /// </summary>
    public void Dispose()
    {
        var host = _shellMenuHost;
        _shellMenuHost = default;
        if (host.HasResources)
        {
            DestroyMessageWindow(host);
        }

        // Releases only this instance's own reference on uxtheme.dll (the module stays loaded for WPF/
        // WinForms, which hold their own); the menu-theme mode already set stays in effect. Exactly once
        // (review L-2): a second Dispose must not drop a reference this instance no longer owns.
        if (Interlocked.Exchange(ref _uxThemeReleased, 1) == 0 && _uxTheme.IsValueCreated && _uxTheme.Value != IntPtr.Zero)
        {
            NativeLibrary.Free(_uxTheme.Value);
        }
    }

    /// <inheritdoc />
    public RecycleBinNativeState QueryRecycleBin()
    {
        try
        {
            var info = new NativeMethods.ShQueryRbInfo
            {
                cbSize = Marshal.SizeOf<NativeMethods.ShQueryRbInfo>(),
            };

            var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            return hr == 0
                ? new RecycleBinNativeState(info.i64NumItems, info.i64Size)
                : new RecycleBinNativeState(0, 0);
        }
        catch
        {
            return new RecycleBinNativeState(0, 0);
        }
    }

    /// <summary>
    /// The <c>FOF_*</c> flags of <see cref="RecycleItems"/> and <see cref="SendToRecycleBin"/> (ADR-035):
    /// no FOF_SILENT on purpose — the progress dialog lets the user cancel a long operation, and
    /// <c>FOF_WANTNUKEWARNING</c> must be able to ask before anything is destroyed for good (a file too
    /// large for the Recycle Bin, a drive without one). An ordinary recycle still shows no confirmation
    /// (<c>FOF_NOCONFIRMATION</c>) and no error UI (<c>FOF_NOERRORUI</c>).
    /// </summary>
    internal const ushort RecycleFlags = NativeMethods.FofAllowUndo
        | NativeMethods.FofNoConfirmation
        | NativeMethods.FofNoErrorUi
        | NativeMethods.FofWantNukeWarning;

    /// <inheritdoc />
    public bool SendToRecycleBin(string[] paths, IntPtr ownerHwnd)
        => RunShFileDelete(paths, ownerHwnd, RecycleFlags);

    /// <inheritdoc />
    public bool RecycleItems(string[] paths, IntPtr ownerHwnd)
        => RunShFileDelete(paths, ownerHwnd, RecycleFlags);

    // ── Low-level mouse hook (WH_MOUSE_LL), see ADR-003 / ADR-012 ───────────────────────────────

    /// <inheritdoc />
    public IntPtr GetModuleHandle(string? moduleName)
        => NativeMethods.GetModuleHandleW(moduleName);

    /// <inheritdoc />
    public IntPtr SetWindowsHookEx(int hookId, LowLevelMouseCallback callback, IntPtr moduleHandle, uint threadId)
        => NativeMethods.SetWindowsHookExW(hookId, callback, moduleHandle, threadId);

    /// <inheritdoc />
    public bool UnhookWindowsHookEx(IntPtr hookHandle)
        => hookHandle != IntPtr.Zero && NativeMethods.UnhookWindowsHookEx(hookHandle);

    /// <inheritdoc />
    public IntPtr CallNextHookEx(IntPtr hookHandle, int nCode, IntPtr wParam, IntPtr lParam)
        => NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);

    /// <inheritdoc />
    public System.Drawing.Point ReadMouseHookPoint(IntPtr lParam)
    {
        // Deliberately not guarded: a garbage lParam must surface to the hook's own try/catch
        // (ADR-012) rather than be silently reported as the (0,0) screen corner.
        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        return new System.Drawing.Point(data.pt.X, data.pt.Y);
    }

    /// <inheritdoc />
    public IntPtr WindowFromPoint(int screenX, int screenY)
        => NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = screenX, Y = screenY });

    /// <inheritdoc />
    public uint GetDoubleClickTime()
        => NativeMethods.GetDoubleClickTime();

    /// <inheritdoc />
    public bool IsListViewPointEmpty(IntPtr listView, int screenX, int screenY)
    {
        if (listView == IntPtr.Zero) return false;

        var clientPt = new NativeMethods.POINT { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(listView, ref clientPt)) return false;

        NativeMethods.GetWindowThreadProcessId(listView, out var pid);
        if (pid == 0) return false;

        var proc = NativeMethods.OpenProcess(
            NativeMethods.ProcessVmOperation | NativeMethods.ProcessVmRead | NativeMethods.ProcessVmWrite,
            false,
            pid);
        if (proc == IntPtr.Zero) return false;

        var size = Marshal.SizeOf<NativeMethods.LVHITTESTINFO>();
        var remote = IntPtr.Zero;
        var local = IntPtr.Zero;

        try
        {
            remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)size, NativeMethods.MemCommit, NativeMethods.PageReadWrite);
            if (remote == IntPtr.Zero) return false;

            var info = new NativeMethods.LVHITTESTINFO { pt = clientPt, iItem = -1 };
            local = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, local, false);

            if (!NativeMethods.WriteProcessMemory(proc, remote, local, (UIntPtr)size, out _)) return false;

            // CRITICAL: this runs inside the WH_MOUSE_LL callback. A plain SendMessage would block
            // the hook thread — and therefore mouse input for the entire OS — while explorer.exe is
            // hung. SMTO_ABORTIFHUNG + a short timeout make the worst case a wrong "not empty".
            // SMTO_BLOCK stops this thread (the hook/UI thread) from reentrantly pumping other
            // threads' incoming sent-messages while it waits, which is not safe mid-hook-callback.
            var sent = NativeMethods.SendMessageTimeoutW(
                listView,
                NativeMethods.LvmHitTest,
                IntPtr.Zero,
                remote,
                NativeMethods.SmtoAbortIfHung | NativeMethods.SmtoBlock,
                NativeMethods.HitTestTimeoutMs,
                out _);
            if (sent == IntPtr.Zero) return false;

            if (!NativeMethods.ReadProcessMemory(proc, remote, local, (UIntPtr)size, out _)) return false;

            info = Marshal.PtrToStructure<NativeMethods.LVHITTESTINFO>(local);
            return info.iItem < 0;
        }
        catch (Exception ex)
        {
            // This runs inside the WH_MOUSE_LL callback (M6): never format/write a log entry on this
            // thread. A rate-limit check (one short lock) decides, and the write is handed to the pool.
            if (_hitTestFailures.TryAdmit(listView, out var suppressed))
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state => state.Log.Error(
                        "NativeApi",
                        "ListViewHitTestFailed",
                        state.Ex,
                        new KeyValuePair<string, object?>("suppressedSinceLast", state.Suppressed)),
                    (Log, Ex: ex, Suppressed: suppressed),
                    preferLocal: false);
            }

            return false;
        }
        finally
        {
            if (local != IntPtr.Zero) Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero) NativeMethods.VirtualFreeEx(proc, remote, UIntPtr.Zero, NativeMethods.MemRelease);
            NativeMethods.CloseHandle(proc);
        }
    }

    // ── Monitors & hidden broadcast window ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<NativeMonitorInfo> GetMonitorInfos()
    {
        var monitors = new List<NativeMonitorInfo>();

        bool Collect(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data)
        {
            var mi = NativeMethods.MONITORINFOEX.Create();
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                monitors.Add(new NativeMonitorInfo(
                    mi.DeviceName,
                    ToRectangle(mi.rcMonitor),
                    ToRectangle(mi.rcWork),
                    (mi.dwFlags & NativeMethods.MonitorinfofPrimary) != 0,
                    GetEffectiveDpi(hMonitor)));
            }

            return true; // continue enumeration
        }

        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // Defensive: never let a native failure bubble into the caller (often the UI thread).
            AppLog.Current.Error("NativeApi", "EnumDisplayMonitorsFailed", ex);
        }

        return monitors;
    }

    /// <inheritdoc />
    public double GetSystemDpiScale()
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return 1.0;

        try
        {
            var dpi = NativeMethods.GetDeviceCaps(hdc, NativeMethods.LogPixelsX);
            return dpi > 0 ? DesktopScaleModel.ScaleFromDpi((uint)dpi) : 1.0;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <inheritdoc />
    public System.Drawing.Size GetPrimaryScreenSizePx()
        => new(NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen));

    /// <inheritdoc />
    public NativeMessageWindow CreateMessageWindow(string className, NativeWindowProc windowProc)
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);

        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = windowProc,
            hInstance = hInstance,
            lpszClassName = className,
        };

        var atom = NativeMethods.RegisterClass(ref wc);
        if (atom == 0) return new NativeMessageWindow(IntPtr.Zero, 0, Marshal.GetLastWin32Error());

        // IMPORTANT: a message-only window (HWND_MESSAGE parent) does NOT receive broadcast messages
        // such as WM_DISPLAYCHANGE / WM_SETTINGCHANGE — Windows excludes message-only windows from
        // broadcasts by design. We therefore create an ordinary hidden top-level window (no parent,
        // WS_POPUP, never WS_VISIBLE). Top-level windows DO receive broadcasts, while WS_POPUP
        // without WS_VISIBLE keeps it off-screen and invisible to the user.
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            className,
            className,
            NativeMethods.WsPopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (hwnd != IntPtr.Zero) return new NativeMessageWindow(hwnd, atom, 0);

        // Roll the class registration back so a retry can reuse the name.
        var error = Marshal.GetLastWin32Error();
        NativeMethods.UnregisterClass((IntPtr)atom, hInstance);
        return new NativeMessageWindow(IntPtr.Zero, 0, error);
    }

    /// <inheritdoc />
    public void DestroyMessageWindow(NativeMessageWindow window)
    {
        if (window.Handle != IntPtr.Zero) NativeMethods.DestroyWindow(window.Handle);
        if (window.ClassAtom != 0) NativeMethods.UnregisterClass((IntPtr)window.ClassAtom, NativeMethods.GetModuleHandleW(null));
    }

    /// <inheritdoc />
    public IntPtr DefaultWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);

    // ── Explorer restart recovery (ADR-034) ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public int RegisterWindowMessage(string messageName)
        => string.IsNullOrEmpty(messageName) ? 0 : (int)NativeMethods.RegisterWindowMessageW(messageName);

    /// <inheritdoc />
    public bool AllowWindowMessage(IntPtr hwnd, int msg)
        => hwnd != IntPtr.Zero
           && msg != 0
           && NativeMethods.ChangeWindowMessageFilterEx(hwnd, (uint)msg, NativeMethods.MsgFltAllow, IntPtr.Zero);

    /// <inheritdoc />
    public bool IsWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && NativeMethods.IsWindowNative(hwnd);

    /// <inheritdoc />
    public IntPtr GetAncestorParent(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GaParent);

    /// <inheritdoc />
    public IntPtr GetRootWindow(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);

    /// <inheritdoc />
    public uint GetWindowProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    /// <inheritdoc />
    public bool TryReadWindowPosZOrder(IntPtr windowPos, out IntPtr insertAfter, out uint flags)
    {
        insertAfter = IntPtr.Zero;
        flags = 0;
        if (windowPos == IntPtr.Zero) return false;

        // No try/catch on purpose (ADR-audit review cycle 1, G3): the pointer is valid by the contract of
        // WM_WINDOWPOSCHANGING, and a genuinely bad pointer would raise a hardware access violation, which the
        // .NET 8 runtime never delivers to a managed catch (corrupted-state exception) — a filter for it would
        // only pretend to protect.
        insertAfter = Marshal.ReadIntPtr(windowPos, NativeMethods.WindowPosInsertAfterOffset);
        flags = unchecked((uint)Marshal.ReadInt32(windowPos, NativeMethods.WindowPosFlagsOffset));
        return true;
    }

    /// <inheritdoc />
    public void WriteWindowPosZOrder(IntPtr windowPos, IntPtr insertAfter, uint flags)
    {
        if (windowPos == IntPtr.Zero) return;

        // No try/catch — see TryReadWindowPosZOrder: the pointer is valid by the WM_WINDOWPOSCHANGING contract.
        Marshal.WriteIntPtr(windowPos, NativeMethods.WindowPosInsertAfterOffset, insertAfter);
        Marshal.WriteInt32(windowPos, NativeMethods.WindowPosFlagsOffset, unchecked((int)flags));
    }

    /// <inheritdoc />
    public bool SetTimer(IntPtr hwnd, IntPtr timerId, uint intervalMs)
        => hwnd != IntPtr.Zero
           && timerId != IntPtr.Zero
           && NativeMethods.SetTimerNative(hwnd, timerId, intervalMs, IntPtr.Zero) != IntPtr.Zero;

    /// <inheritdoc />
    public bool KillTimer(IntPtr hwnd, IntPtr timerId)
        => hwnd != IntPtr.Zero && timerId != IntPtr.Zero && NativeMethods.KillTimerNative(hwnd, timerId);

    // ── System theme and window frame (design system 1.x, phase 6.1, ADR-047 D-2) ────────────────

    /// <inheritdoc />
    public int? ReadCurrentUserDword(string keyPath, string valueName)
    {
        if (string.IsNullOrEmpty(keyPath) || valueName is null) return null;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsHighContrastOn()
    {
        try
        {
            // WinForms' SystemInformation.HighContrast calls SystemParametersInfo(SPI_GETHIGHCONTRAST) on
            // every read (unlike WPF's SystemParameters.HighContrast, which is a cache refreshed by WPF's own
            // WM_SETTINGCHANGE handling and may not have been updated yet when our listener fires). Managed
            // API, so no new P/Invoke declaration is needed.
            return System.Windows.Forms.SystemInformation.HighContrast;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public int GetSystemColor(int index)
    {
        try
        {
            return unchecked((int)NativeMethods.GetSysColor(index));
        }
        catch
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public double GetMessageFontSize()
    {
        try
        {
            var metrics = new NativeMethods.NonClientMetricsW { cbSize = (uint)Marshal.SizeOf<NativeMethods.NonClientMetricsW>() };
            if (!NativeMethods.SystemParametersInfoNonClientMetrics(NativeMethods.SpiGetNonClientMetrics, metrics.cbSize, ref metrics, 0))
                return 0;

            var height = Math.Abs(metrics.lfMessageFont.lfHeight);
            if (height == 0) return 0;

            // SPI_GETNONCLIENTMETRICS reports pixels at the system DPI; WPF converts the same way.
            var hdc = NativeMethods.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return 0;

            try
            {
                var dpi = NativeMethods.GetDeviceCaps(hdc, NativeMethods.LogPixelsY);
                return dpi > 0 ? height / DesktopScaleModel.ScaleFromDpi((uint)dpi) : 0;
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public int SetDwmWindowAttribute(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd == IntPtr.Zero) return NativeMethods.EInvalidArg;

        try
        {
            return NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return NativeMethods.ENotImpl;
        }
        catch (Exception ex)
        {
            return ex.HResult < 0 ? ex.HResult : NativeMethods.EFail;
        }
    }

    /// <inheritdoc />
    public unsafe bool TrySetPreferredAppMode(int mode)
    {
        var export = GetUxThemeExport(NativeMethods.UxThemeOrdinalSetPreferredAppMode);
        if (export == IntPtr.Zero) return false;

        try
        {
            // PreferredAppMode SetPreferredAppMode(PreferredAppMode) — one 4-byte argument, 4-byte result.
            // On 1809 the same ordinal is AllowDarkModeForApp(BOOL): an ABI-identical signature, so even a
            // caller that bypassed the build gate could not unbalance the stack.
            _ = ((delegate* unmanaged[Stdcall]<int, int>)export)(mode);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("NativeApi", "SetPreferredAppModeFailed", new KeyValuePair<string, object?>("hresult", ex.HResult));
            return false;
        }
    }

    /// <inheritdoc />
    public unsafe bool TryFlushMenuThemes()
    {
        var export = GetUxThemeExport(NativeMethods.UxThemeOrdinalFlushMenuThemes);
        if (export == IntPtr.Zero) return false;

        try
        {
            // void FlushMenuThemes(void).
            ((delegate* unmanaged[Stdcall]<void>)export)();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("NativeApi", "FlushMenuThemesFailed", new KeyValuePair<string, object?>("hresult", ex.HResult));
            return false;
        }
    }

    // ── Native panel sizing primitives (ADR-022 / Phase 2) ──────────────────────────────────────

    /// <inheritdoc />
    public bool GetWindowRect(IntPtr hwnd, out NativeRectPx rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var native))
        {
            // Defensive: a destroyed/invalid HWND must not throw into a window procedure. Read the
            // error first — the rate limiter must not be able to clobber it.
            var win32Error = Marshal.GetLastWin32Error();
            if (_getWindowRectFailures.TryAdmit(hwnd, out var suppressed))
            {
                Log.Warn(
                    "NativeApi",
                    "GetWindowRectFailed",
                    new KeyValuePair<string, object?>("win32Error", win32Error),
                    new KeyValuePair<string, object?>("suppressedSinceLast", suppressed));
            }

            return false;
        }

        rect = new NativeRectPx(native.left, native.top, native.right, native.bottom);
        return true;
    }

    /// <inheritdoc />
    public NativeRectPx ReadRect(IntPtr lParam)
    {
        // Deliberately not guarded, exactly like ReadMouseHookPoint: a garbage lParam must surface to
        // the caller's own try/catch (the sizing hook wraps every message) rather than be silently
        // reported as an empty rectangle at the screen origin.
        var native = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        return new NativeRectPx(native.left, native.top, native.right, native.bottom);
    }

    /// <inheritdoc />
    public void WriteRect(IntPtr lParam, NativeRectPx rect)
    {
        var native = new NativeMethods.RECT
        {
            left = rect.Left,
            top = rect.Top,
            right = rect.Right,
            bottom = rect.Bottom,
        };

        // fDeleteOld: false — RECT is blittable and owns no unmanaged references, so there is nothing
        // in the destination buffer to release (the buffer belongs to the sizing loop, not to us).
        Marshal.StructureToPtr(native, lParam, false);
    }

    /// <inheritdoc />
    public double GetDpiScaleForWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var dpi = NativeMethods.GetDpiForWindow(hwnd);
                if (dpi > 0) return DesktopScaleModel.ScaleFromDpi(dpi);
            }
            catch (EntryPointNotFoundException)
            {
                // Windows older than 10/1607 has no GetDpiForWindow — fall through to system DPI.
            }
            catch (DllNotFoundException)
            {
                // Same fallback path; keeps the caller (a window procedure) exception-free.
            }
        }

        return GetSystemDpiScale();
    }

    /// <inheritdoc />
    public bool CancelSizingLoop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            // The modal move/size loop runs on the window's capture: no capture on this thread means no
            // loop to cancel (or a foreign one), and nothing must be sent.
            if (NativeMethods.GetCapture() != hwnd) return false;

            // WM_CANCELMODE is the documented "abort any modal state" request; DefWindowProc answers it
            // by releasing the capture (WinUser.h, MSDN WM_CANCELMODE). Same-thread SendMessage is a direct
            // call into the window procedure, so this cannot block on another process.
            NativeMethods.SendMessageW(hwnd, NativeMethods.WmCancelMode, IntPtr.Zero, IntPtr.Zero);

            // Belt and braces: a window procedure that swallowed WM_CANCELMODE would leave the loop running.
            if (NativeMethods.GetCapture() == hwnd) NativeMethods.ReleaseCapture();

            return true;
        }
        catch (Exception ex)
        {
            // Called from a dispatcher operation that may be nested inside the sizing loop — never throw.
            Log.Warn("NativeApi", "CancelSizingLoopFailed", new KeyValuePair<string, object?>("hresult", ex.HResult));
            return false;
        }
    }

    // ── High-resolution panel item icons (IShellItemImageFactory / system image list) ───────────
    // Self-contained block: the implementations below plus their own NativeMethods region at the end of
    // the file. Deliberately shares nothing with ShowShellContextMenu/IShellFolder/IContextMenu;
    // its declarations live in IconNativeMethods.

    /// <inheritdoc />
    /// <remarks>
    /// Expected on an STA thread (see <see cref="INativeApi.GetShellItemImage"/>). The factory's RCW
    /// and the raw interface pointer are both released in <see langword="finally"/> on the calling thread; on any failure after
    /// <c>GetImage</c> produced a bitmap, that bitmap is deleted here, so the caller only ever owns a
    /// handle when the method returned 0.
    /// </remarks>
    public int GetShellItemImage(string path, int sizePx, out IntPtr bitmapHandle)
    {
        bitmapHandle = IntPtr.Zero;
        if (string.IsNullOrEmpty(path) || sizePx <= 0) return IconNativeMethods.EInvalidArg;

        var factoryPtr = IntPtr.Zero;
        object? factory = null;
        try
        {
            var iid = IconNativeMethods.IidIShellItemImageFactory;
            var hr = IconNativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factoryPtr);
            if (hr < 0) return hr;
            if (factoryPtr == IntPtr.Zero) return IconNativeMethods.EFail;

            factory = Marshal.GetObjectForIUnknown(factoryPtr);
            if (factory is not IconNativeMethods.IShellItemImageFactory imageFactory) return IconNativeMethods.ENoInterface;

            var size = new IconNativeMethods.SIZE { cx = sizePx, cy = sizePx };
            hr = imageFactory.GetImage(size, IconNativeMethods.SiigbfIconOnly | IconNativeMethods.SiigbfBiggerSizeOk, out var hbitmap);
            if (hr < 0 || hbitmap == IntPtr.Zero)
            {
                // Defensive: a misbehaving handler reporting failure yet still handing out a bitmap must
                // not leak it.
                if (hbitmap != IntPtr.Zero) IconNativeMethods.DeleteObject(hbitmap);
                return hr < 0 ? hr : IconNativeMethods.EFail;
            }

            bitmapHandle = hbitmap;
            return 0;
        }
        catch (Exception ex)
        {
            // COMException/InvalidCastException/SEHException from a broken shell extension: reported as an
            // HRESULT so the caller degrades to the next fallback; never thrown into the UI thread.
            return ex.HResult < 0 ? ex.HResult : IconNativeMethods.EFail;
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
        }
    }

    /// <inheritdoc />
    public unsafe bool TryReadBitmapPixels(IntPtr bitmapHandle, out NativeBitmapPixels pixels)
    {
        pixels = default;
        if (bitmapHandle == IntPtr.Zero) return false;

        var hdc = IntPtr.Zero;
        try
        {
            if (IconNativeMethods.GetObjectW(bitmapHandle, sizeof(IconNativeMethods.BITMAP), out var bitmap) == 0) return false;

            var width = bitmap.bmWidth;
            var height = Math.Abs(bitmap.bmHeight);
            if (width <= 0 || height <= 0 || width > IconNativeMethods.MaxIconBitmapSide || height > IconNativeMethods.MaxIconBitmapSide)
            {
                return false;
            }

            // A negative biHeight asks GetDIBits for a TOP-DOWN 32-bpp copy regardless of how the source
            // is stored (IShellItemImageFactory hands out bottom-up DIB sections), so the managed consumer
            // never has to know about DIB orientation. 32 bpp → 32 bpp BI_RGB copies the alpha byte as-is.
            var info = new IconNativeMethods.BITMAPINFO
            {
                bmiHeader = new IconNativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(IconNativeMethods.BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = IconNativeMethods.BiRgb,
                },
            };

            var buffer = new byte[width * height * 4];
            hdc = NativeMethods.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            int lines;
            fixed (byte* bits = buffer)
            {
                lines = IconNativeMethods.GetDIBits(hdc, bitmapHandle, 0, (uint)height, bits, ref info, IconNativeMethods.DibRgbColors);
            }

            if (lines != height) return false;

            pixels = new NativeBitmapPixels(width, height, buffer);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <inheritdoc />
    public bool DeleteBitmap(IntPtr bitmapHandle)
        => bitmapHandle != IntPtr.Zero && IconNativeMethods.DeleteObject(bitmapHandle);

    /// <inheritdoc />
    /// <remarks>
    /// Goes through the <c>IImageList</c> COM interface rather than the flat <c>ImageList_GetIcon</c>
    /// export: the system image list is a comctl32 v6 object, and this WPF process does not necessarily
    /// activate comctl32 v6 for its flat exports.
    /// </remarks>
    public IntPtr GetSystemImageListIcon(int imageList, int iconIndex)
    {
        if (iconIndex < 0) return IntPtr.Zero;

        var listPtr = IntPtr.Zero;
        object? list = null;
        try
        {
            var iid = IconNativeMethods.IidIImageList;
            var hr = IconNativeMethods.SHGetImageList(imageList, ref iid, out listPtr);
            if (hr < 0 || listPtr == IntPtr.Zero) return IntPtr.Zero;

            list = Marshal.GetObjectForIUnknown(listPtr);
            if (list is not IconNativeMethods.IImageList imageListObject) return IntPtr.Zero;

            hr = imageListObject.GetIcon(iconIndex, IconNativeMethods.IldTransparent, out var iconHandle);
            if (hr >= 0) return iconHandle;

            if (iconHandle != IntPtr.Zero) NativeMethods.DestroyIconByHandle(iconHandle);
            return IntPtr.Zero;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (list is not null) Marshal.ReleaseComObject(list);
            if (listPtr != IntPtr.Zero) Marshal.Release(listPtr);
        }
    }

    // ── Background icon loading: remote-root classification (ADR-046 D-2, 7a-N) ────────────────
    // Called from the icon worker's STA thread (RemotePathClassifier). Declarations in IconNativeMethods.

    /// <inheritdoc />
    /// <remarks>
    /// <c>CoCreateInstance(CLSID_ShellLink)</c> → <c>IPersistFile::Load(STGM_READ)</c> →
    /// <c>IShellLinkW::GetPath(SLGP_RAWPATH)</c>. <c>IShellLinkW::Resolve</c> is deliberately never called:
    /// that is the step that searches for a moved target, may touch the network and may show UI. Loading the
    /// <c>.lnk</c> only reads the local file. The RCW and the raw interface pointer are released in
    /// <see langword="finally"/> on the calling thread, so no finalizer ever has to reach this apartment later
    /// (R7-7). A target longer than the buffer is reported truncated — callers only need its root.
    /// </remarks>
    public unsafe bool TryGetShortcutRawTarget(string shortcutPath, [NotNullWhen(true)] out string? targetPath)
    {
        targetPath = null;
        if (string.IsNullOrEmpty(shortcutPath)) return false;

        var linkPtr = IntPtr.Zero;
        object? link = null;
        try
        {
            var clsid = IconNativeMethods.ClsidShellLink;
            var iid = IconNativeMethods.IidIShellLinkW;
            var hr = IconNativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, IconNativeMethods.ClsctxInprocServer, ref iid, out linkPtr);
            if (hr < 0 || linkPtr == IntPtr.Zero) return false;

            link = Marshal.GetObjectForIUnknown(linkPtr);
            if (link is not IconNativeMethods.IPersistFile persistFile || link is not IconNativeMethods.IShellLinkW shellLink) return false;

            if (persistFile.Load(shortcutPath, IconNativeMethods.StgmRead) < 0) return false;

            var buffer = new char[IconNativeMethods.ShortcutPathBufferChars];
            fixed (char* chars = buffer)
            {
                // S_FALSE (1) = the link has no path (PIDL-only target, e.g. a Control Panel item).
                hr = shellLink.GetPath((IntPtr)chars, buffer.Length, IntPtr.Zero, IconNativeMethods.SlgpRawPath);
            }

            if (hr != 0) return false;

            var length = Array.IndexOf(buffer, '\0');
            var raw = new string(buffer, 0, length < 0 ? buffer.Length : length);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            targetPath = Environment.ExpandEnvironmentVariables(raw);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // COMException/InvalidCastException/SEHException from a corrupt link or a broken shell
            // extension: an unreadable link is simply "not classified", never an exception on the worker.
            targetPath = null;
            return false;
        }
        finally
        {
            ReleaseComObjectQuietly(link);
            if (linkPtr != IntPtr.Zero) Marshal.Release(linkPtr);
        }
    }

    /// <inheritdoc />
    public DriveKind GetDriveKind(string rootPath)
    {
        // GetDriveTypeW(NULL) would classify the CURRENT directory's root — never what a caller means.
        if (string.IsNullOrEmpty(rootPath)) return DriveKind.Unknown;

        try
        {
            return IconNativeMethods.GetDriveTypeW(rootPath) switch
            {
                IconNativeMethods.DriveNoRootDir => DriveKind.NoRootDirectory,
                IconNativeMethods.DriveRemovable => DriveKind.Removable,
                IconNativeMethods.DriveFixed => DriveKind.Fixed,
                IconNativeMethods.DriveRemote => DriveKind.Remote,
                IconNativeMethods.DriveCdRom => DriveKind.CdRom,
                IconNativeMethods.DriveRamDisk => DriveKind.RamDisk,
                _ => DriveKind.Unknown,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DriveKind.Unknown;
        }
    }

    /// <inheritdoc />
    public bool IsPackagedProcess => HasPackageIdentity;

    /// <summary>
    /// Interprets the return code of <c>GetCurrentPackageFullName</c> called with a zero-length buffer
    /// (the identity probe, Microsoft Store channel).
    /// </summary>
    /// <remarks>
    /// <c>APPMODEL_ERROR_NO_PACKAGE</c> (15700) means the process has no package identity;
    /// <c>ERROR_INSUFFICIENT_BUFFER</c> (122) — the expected answer for a zero-length buffer — and
    /// <c>ERROR_SUCCESS</c> (0) both mean it has one. Any other code is not proof of identity, so it
    /// resolves to <see langword="false"/> (the Inno layout, the one that worked before the Store channel
    /// existed, is the safe default).
    /// </remarks>
    /// <param name="rc">The Win32 return code of the probe call.</param>
    /// <returns><see langword="true"/> when the code proves the process runs with package identity.</returns>
    internal static bool InterpretPackageProbe(int rc)
        => rc is NativeMethods.ErrorSuccess or NativeMethods.ErrorInsufficientBuffer;

    /// <summary>
    /// Gets a value indicating whether this process runs with package identity (MSIX / Microsoft Store).
    /// Probed once per process and cached; never throws. The only static consumer outside this file is
    /// <c>AppStorageRoot</c> (it is needed before DI exists) — pinned by <c>PackageProbeUsageGuardTests</c>;
    /// everything else reads it through <see cref="IsPackagedProcess"/> / <c>IAppPackageContext</c>.
    /// </summary>
    internal static bool HasPackageIdentity => PackageProbe.Value.IsPackaged;

    /// <summary>
    /// Gets the raw return code of the identity probe (<c>GetCurrentPackageFullName</c>), or
    /// <see cref="PackageProbeUnavailableCode"/> when the call could not be made at all. Kept because
    /// nothing can be logged before DI exists: <c>Program.Main</c> writes it once after the log is up.
    /// </summary>
    internal static int PackageProbeResultCode => PackageProbe.Value.ResultCode;

    private static (bool IsPackaged, int ResultCode) RunPackageProbe()
    {
        try
        {
            uint length = 0;
            var rc = NativeMethods.GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return (InterpretPackageProbe(rc), rc);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // DllNotFoundException / EntryPointNotFoundException (pre-Windows 8, stripped-down host: no
            // packages exist there) or anything unexpected: not proof of identity, so "not packaged".
            return (false, PackageProbeUnavailableCode);
        }
    }

    /// <summary>
    /// Loads the System32 copy of <c>uxtheme.dll</c> (never a same-named DLL next to the executable).
    /// </summary>
    /// <returns>The module handle, or <see cref="IntPtr.Zero"/> when it cannot be loaded. Never throws.</returns>
    private static IntPtr LoadUxTheme()
    {
        try
        {
            return NativeLibrary.TryLoad("uxtheme.dll", typeof(NativeApi).Assembly, DllImportSearchPath.System32, out var handle)
                ? handle
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static System.Drawing.Rectangle ToRectangle(NativeMethods.RECT r)
        => System.Drawing.Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);

    /// <summary>
    /// Effective DPI of a monitor (<c>shcore!GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>), or 0 when it cannot
    /// be read — the caller (<c>DesktopScaleProvider</c>) substitutes the system scale for 0. Runs inside
    /// the <c>EnumDisplayMonitors</c> callback, so it must never throw.
    /// </summary>
    /// <param name="hMonitor">Monitor handle from the enumeration.</param>
    /// <returns>The effective DPI (the X value; X and Y are always equal for this DPI type), or 0.</returns>
    private static uint GetEffectiveDpi(IntPtr hMonitor)
    {
        try
        {
            var hr = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _);
            return hr == 0 ? dpiX : 0;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-8.1 shcore without the export: unknown, the provider falls back to the system scale.
            return 0;
        }
        catch (DllNotFoundException)
        {
            // No shcore.dll at all: same fallback.
            return 0;
        }
    }

    /// <summary>
    /// Single <c>SHFileOperationW(FO_DELETE)</c> batch shared by <see cref="SendToRecycleBin"/> and
    /// <see cref="RecycleItems"/>, which differ only in their <c>FOF_*</c> flags.
    /// </summary>
    /// <param name="paths">Full paths to delete; <see langword="null"/>/empty answers <see langword="false"/>.</param>
    /// <param name="ownerHwnd">Owner window for any shell UI, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="flags">The <c>FOF_*</c> flags of the operation.</param>
    /// <returns>Whether the shell reported success with no aborted operations. Never throws.</returns>
    private static bool RunShFileDelete(string[] paths, IntPtr ownerHwnd, ushort flags)
    {
        if (paths is null || paths.Length == 0)
        {
            return false;
        }

        try
        {
            // Double null-terminated, null-terminated list of paths.
            var from = string.Join("\0", paths) + "\0\0";

            var op = new NativeMethods.ShFileOpStruct
            {
                hwnd = ownerHwnd,
                wFunc = NativeMethods.FoDelete,
                pFrom = from,
                pTo = null,
                fFlags = flags,
            };

            var result = NativeMethods.SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extension of <paramref name="path"/> for diagnostics — the only part of a path that may be logged
    /// (privacy invariant, same rule as <c>LogScrubber</c>). Never throws.
    /// </summary>
    private static string GetExtensionForLog(string path)
    {
        try
        {
            return System.IO.Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Asks the context menu for the canonical verb of the chosen command (<c>GCS_VERBW</c>) — e.g. <c>rename</c>,
    /// <c>delete</c>, <c>cut</c>. Some handlers do not implement verbs for every command (or fail the call outright)
    /// — any failure answers <see langword="null"/>. Never throws.
    /// </summary>
    private static string? GetCanonicalVerb(NativeMethods.IContextMenu menu, uint commandOffset)
    {
        const int maxChars = 64;
        var bufferBytes = (maxChars + 1) * sizeof(char);
        var buffer = Marshal.AllocCoTaskMem(bufferBytes);
        try
        {
            // Zero-filled so the result is NUL-terminated even if the handler writes nothing.
            Marshal.Copy(new byte[bufferBytes], 0, buffer, bufferBytes);
            var hr = menu.GetCommandString((IntPtr)commandOffset, NativeMethods.GcsVerbW, IntPtr.Zero, buffer, maxChars);
            return hr == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// Whether a key is currently held (<c>GetAsyncKeyState</c> high bit). The thread's own key state is not
    /// reliable here: the panels are <c>WS_EX_NOACTIVATE</c> and never receive keyboard input.
    /// </summary>
    private static bool IsKeyDown(int virtualKey) => NativeMethods.GetAsyncKeyState(virtualKey) < 0;

    /// <summary>
    /// Invokes the command at <paramref name="commandOffset"/> (offset from <c>idCmdFirst</c>) with a
    /// <c>CMINVOKECOMMANDINFOEX</c> carrying the verb both as an ANSI and a Unicode <c>MAKEINTRESOURCE</c>
    /// id, the invocation point and the modifier-key state. Returns the handler's <c>HRESULT</c>.
    /// </summary>
    private static int InvokeShellCommand(NativeMethods.IContextMenu menu, IntPtr owner, uint commandOffset, int screenX, int screenY, bool shiftDown)
    {
        var mask = NativeMethods.CmicMaskUnicode | NativeMethods.CmicMaskPtInvoke;
        if (shiftDown)
        {
            mask |= NativeMethods.CmicMaskShiftDown;
        }

        if (IsKeyDown(NativeMethods.VkControl))
        {
            mask |= NativeMethods.CmicMaskControlDown;
        }

        var invoke = new NativeMethods.CmInvokeCommandInfoEx
        {
            cbSize = Marshal.SizeOf<NativeMethods.CmInvokeCommandInfoEx>(),
            fMask = mask,
            hwnd = owner,
            lpVerb = (IntPtr)commandOffset,
            lpVerbW = (IntPtr)commandOffset,
            nShow = NativeMethods.SwShowNormal,
            ptInvoke = new NativeMethods.POINT { X = screenX, Y = screenY },
        };

        return menu.InvokeCommand(ref invoke);
    }

    /// <summary>
    /// Releases a shell COM object without letting a dead/disconnected RCW throw out of a <see langword="finally"/>.
    /// </summary>
    private static void ReleaseComObjectQuietly(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // An RCW whose server died (Explorer restart, crashed extension) cannot be released any further.
        }
    }

    /// <summary>
    /// Resolves an ordinal-only <c>uxtheme.dll</c> export (<c>GetProcAddress</c> with <c>MAKEINTRESOURCE</c>).
    /// </summary>
    /// <param name="ordinal">The export ordinal.</param>
    /// <returns>
    /// The export address, or <see cref="IntPtr.Zero"/> when the module or the export is missing or this
    /// instance is disposed (review L-2: <see cref="TrySetPreferredAppMode"/>/<see cref="TryFlushMenuThemes"/>
    /// then answer <see langword="false"/> without touching the module). Never throws.
    /// </returns>
    private IntPtr GetUxThemeExport(int ordinal)
    {
        if (Volatile.Read(ref _uxThemeReleased) != 0) return IntPtr.Zero;

        try
        {
            var module = _uxTheme.Value;
            return module == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetProcAddressByOrdinal(module, ordinal);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Shared core of <see cref="ShowShellContextMenu"/>/<see cref="ShowShellContextMenuForPath"/>: binds
    /// <paramref name="pidl"/> to its parent folder, builds the item's <c>IContextMenu</c>, tracks it from the
    /// helper window and invokes the chosen command. Never throws; releases every COM object and the menu in
    /// <see langword="finally"/>. Does not free <paramref name="pidl"/> (its caller owns it).
    /// </summary>
    /// <param name="pidl">Absolute PIDL of the item.</param>
    /// <param name="fallbackOwner">Owner used only when the helper window cannot be created.</param>
    /// <param name="screenX">Horizontal menu position, in physical pixels.</param>
    /// <param name="screenY">Vertical menu position, in physical pixels.</param>
    /// <param name="flags"><c>CMF_*</c> flags for <c>QueryContextMenu</c>; <c>CMF_EXTENDEDVERBS</c> is added while Shift is held.</param>
    /// <param name="intercepts">
    /// Shell verbs that are reported instead of invoked: <c>rename</c> as <see cref="ShellContextMenuOutcome.RenameRequested"/>,
    /// <c>delete</c> as <see cref="ShellContextMenuOutcome.DeleteRequested"/>.
    /// </param>
    /// <param name="extension">The item's extension for diagnostics, or <see langword="null"/> for a virtual item.</param>
    /// <returns>How the menu ended, plus the invoked command's canonical verb (M-B, release-polish 1.0 final review).</returns>
    private ShellContextMenuResult ShowShellContextMenuCore(
        IntPtr pidl,
        IntPtr fallbackOwner,
        int screenX,
        int screenY,
        uint flags,
        ShellVerbIntercepts intercepts,
        string? extension)
    {
        // TrackPopupMenuEx pumps messages; a second right-click can never reach here while a menu is up, but a
        // queued dispatcher operation could — never nest a second shell menu into the first one's modal loop.
        if (_shellMenuActive)
        {
            return new ShellContextMenuResult(ShellContextMenuOutcome.NotShown, null);
        }

        _shellMenuActive = true;
        var outcome = ShellContextMenuOutcome.NotShown;
        string? verb = null;
        var stage = "BindToParent";
        var hMenu = IntPtr.Zero;
        object? parentFolder = null;
        object? contextMenu = null;
        try
        {
            var host = EnsureShellMenuHost();
            var owner = host != IntPtr.Zero ? host : fallbackOwner;

            var iidShellFolder = NativeMethods.IidIShellFolder;

            // childPidl is a pointer INTO pidl's allocation — never free it separately.
            var hr = NativeMethods.SHBindToParent(pidl, ref iidShellFolder, out var parentPtr, out var childPidl);
            if (hr != 0 || parentPtr == IntPtr.Zero || childPidl == IntPtr.Zero)
            {
                if (parentPtr != IntPtr.Zero)
                {
                    Marshal.Release(parentPtr);
                }

                LogShellMenuFailure(stage, extension, hr);
                return new ShellContextMenuResult(outcome, verb);
            }

            parentFolder = Marshal.GetObjectForIUnknown(parentPtr);
            Marshal.Release(parentPtr);
            if (parentFolder is not NativeMethods.IShellFolder shellFolder)
            {
                LogShellMenuFailure(stage, extension, NativeMethods.ENoInterface);
                return new ShellContextMenuResult(outcome, verb);
            }

            stage = "GetUIObjectOf";
            var apidl = new[] { childPidl };
            var iidContextMenu = NativeMethods.IidIContextMenu;
            hr = shellFolder.GetUIObjectOf(owner, 1, apidl, ref iidContextMenu, IntPtr.Zero, out var ctxPtr);
            if (hr != 0 || ctxPtr == IntPtr.Zero)
            {
                if (ctxPtr != IntPtr.Zero)
                {
                    Marshal.Release(ctxPtr);
                }

                LogShellMenuFailure(stage, extension, hr);
                return new ShellContextMenuResult(outcome, verb);
            }

            contextMenu = Marshal.GetObjectForIUnknown(ctxPtr);
            Marshal.Release(ctxPtr);
            if (contextMenu is not NativeMethods.IContextMenu menu)
            {
                LogShellMenuFailure(stage, extension, NativeMethods.ENoInterface);
                return new ShellContextMenuResult(outcome, verb);
            }

            stage = "CreatePopupMenu";
            hMenu = NativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                LogShellMenuFailure(stage, extension, Marshal.GetHRForLastWin32Error());
                return new ShellContextMenuResult(outcome, verb);
            }

            var shiftDown = IsKeyDown(NativeMethods.VkShift);
            if (shiftDown)
            {
                flags |= NativeMethods.CmfExtendedVerbs;
            }

            // QueryContextMenu returns an HRESULT whose SUCCESS code carries the number of inserted items in
            // the low word. A failed query must not be tracked/invoked — doing so passes a stale/empty HMENU
            // into the shell verb dispatch and is a source of access violations on virtual/broken items.
            stage = "QueryContextMenu";
            hr = menu.QueryContextMenu(hMenu, 0, NativeMethods.ShellMenuCmdFirst, NativeMethods.ShellMenuCmdLast, flags);
            if (hr < 0)
            {
                LogShellMenuFailure(stage, extension, hr);
                return new ShellContextMenuResult(outcome, verb);
            }

            stage = "TrackPopupMenu";
            var command = TrackShellMenu(contextMenu, hMenu, owner, host != IntPtr.Zero, screenX, screenY);
            outcome = ShellContextMenuOutcome.Dismissed;
            if (command < NativeMethods.ShellMenuCmdFirst || command > NativeMethods.ShellMenuCmdLast)
            {
                return new ShellContextMenuResult(outcome, verb);
            }

            var commandOffset = command - NativeMethods.ShellMenuCmdFirst;
            if (intercepts != ShellVerbIntercepts.None)
            {
                stage = "GetCommandString";
                verb = GetCanonicalVerb(menu, commandOffset);
                if ((intercepts & ShellVerbIntercepts.Rename) != 0 && string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
                {
                    outcome = ShellContextMenuOutcome.RenameRequested;
                    return new ShellContextMenuResult(outcome, verb);
                }

                if ((intercepts & ShellVerbIntercepts.Delete) != 0 && string.Equals(verb, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    outcome = ShellContextMenuOutcome.DeleteRequested;
                    return new ShellContextMenuResult(outcome, verb);
                }
            }

            stage = "InvokeCommand";
            outcome = ShellContextMenuOutcome.Invoked;
            hr = InvokeShellCommand(menu, owner, commandOffset, screenX, screenY, shiftDown);

            // A user cancelling a confirmation the command itself showed is not a failure.
            if (hr < 0 && hr != NativeMethods.HResultCancelled)
            {
                LogShellMenuFailure(stage, extension, hr);
            }

            return new ShellContextMenuResult(outcome, verb);
        }
        catch (Exception ex)
        {
            // Degrade silently: a broken/virtual item or a faulty shell extension must never disrupt the UI
            // thread. HRESULT + extension only — never the path.
            LogShellMenuFailure(stage, extension, ex.HResult);
            return new ShellContextMenuResult(outcome, verb);
        }
        finally
        {
            // The re-entrancy flag is cleared no matter what the releases below do: a stuck flag would
            // silently disable every future shell menu of the session.
            _shellMenuActive = false;

            if (hMenu != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(hMenu);
            }

            ReleaseComObjectQuietly(contextMenu);
            ReleaseComObjectQuietly(parentFolder);
        }
    }

    /// <summary>
    /// Tracks <paramref name="hMenu"/> modally and returns the chosen command id (0 when dismissed). While
    /// tracking, <see cref="ShellMenuHostWndProc"/> forwards the owner-draw/popup-init messages to the
    /// menu's <c>IContextMenu3</c>/<c>IContextMenu2</c> (whichever the handler implements). When the helper
    /// window owns the menu it is made foreground first and nudged with <c>WM_NULL</c> afterwards — the
    /// documented pattern (KB 135788) without which a menu owned by a non-foreground window never closes on
    /// an outside click. The panel itself is never activated (it stays <c>WS_EX_NOACTIVATE</c> and keeps
    /// its Z-order).
    /// </summary>
    private uint TrackShellMenu(object contextMenu, IntPtr hMenu, IntPtr owner, bool ownerIsHost, int screenX, int screenY)
    {
        _activeMenu3 = contextMenu as NativeMethods.IContextMenu3;
        _activeMenu2 = _activeMenu3 is null ? contextMenu as NativeMethods.IContextMenu2 : null;
        try
        {
            if (ownerIsHost)
            {
                NativeMethods.SetForegroundWindow(owner);
            }

            // No TPM_NONOTIFY: the WM_INITMENUPOPUP notifications are exactly what fills the lazy submenus.
            var command = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton,
                screenX,
                screenY,
                owner,
                IntPtr.Zero);

            if (ownerIsHost)
            {
                NativeMethods.PostMessageW(owner, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero);
            }

            return command;
        }
        finally
        {
            _activeMenu3 = null;
            _activeMenu2 = null;
        }
    }

    /// <summary>
    /// Returns the hidden helper window that owns every tracked shell context menu, creating it on first
    /// use. A top-level, never-shown <c>WS_POPUP</c> (the same kind <see cref="CreateMessageWindow"/> makes)
    /// — not a message-only window, which can never become foreground. Kept for the lifetime of this
    /// (singleton) instance rather than per menu: destroying the foreground window right after the menu
    /// would hand activation to an arbitrary window and take the foreground away from dialogs the invoked
    /// command opens asynchronously (Properties, confirmations). Released in <see cref="Dispose"/>.
    /// </summary>
    /// <returns>The helper window, or <see cref="IntPtr.Zero"/> when it cannot be created (logged).</returns>
    private IntPtr EnsureShellMenuHost()
    {
        if (_shellMenuHost.IsValid && NativeMethods.IsWindowNative(_shellMenuHost.Handle))
        {
            return _shellMenuHost.Handle;
        }

        if (_shellMenuHost.HasResources)
        {
            // The window died (should not happen for a hidden window) — release the leftovers first.
            DestroyMessageWindow(_shellMenuHost);
            _shellMenuHost = default;
        }

        _shellMenuHostProc ??= ShellMenuHostWndProc;

        // Unique per instance: a second NativeApi in the same process (tests) must not collide on the class.
        var window = CreateMessageWindow($"DesktopOrganizer.ShellMenuHost.{Guid.NewGuid():N}", _shellMenuHostProc);
        if (!window.IsValid)
        {
            Log.Warn(
                "NativeApi",
                "ShellMenuHostCreateFailed",
                new KeyValuePair<string, object?>("win32Error", window.LastError));
            return IntPtr.Zero;
        }

        _shellMenuHost = window;
        return window.Handle;
    }

    /// <summary>
    /// Window procedure of the helper window. Forwards the menu messages shell extensions need
    /// (<c>WM_INITMENUPOPUP</c>, <c>WM_DRAWITEM</c>, <c>WM_MEASUREITEM</c>, <c>WM_MENUCHAR</c>) to the menu being
    /// tracked; everything else — and anything the handler declines — goes to <c>DefWindowProc</c>. Runs
    /// inside the menu's modal loop: never lets an exception cross back into native code.
    /// </summary>
    private IntPtr ShellMenuHostWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg is NativeMethods.WmInitMenuPopup or NativeMethods.WmDrawItem or NativeMethods.WmMeasureItem or NativeMethods.WmMenuChar)
            {
                if (_activeMenu3 is { } menu3)
                {
                    if (menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                    {
                        return result;
                    }
                }
                else if (_activeMenu2 is { } menu2 && msg != NativeMethods.WmMenuChar)
                {
                    if (menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                    {
                        // WM_INITMENUPOPUP returns 0 when processed; WM_DRAWITEM/WM_MEASUREITEM return TRUE.
                        return msg == NativeMethods.WmInitMenuPopup ? IntPtr.Zero : new IntPtr(1);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Swallowed on purpose: a faulty shell extension must not take the menu loop down with it.
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Writes the single diagnostics entry for a shell context menu that could not be shown or whose command
    /// failed. Stage + extension + <c>HRESULT</c> only — never the path (privacy invariant).
    /// </summary>
    private void LogShellMenuFailure(string stage, string? extension, int hresult)
        => Log.Warn(
            "NativeApi",
            "ShellContextMenuFailed",
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("extension", extension ?? "<virtual>"),
            new KeyValuePair<string, object?>("hresult", $"0x{hresult:X8}"));

    /// <summary>
    /// Shell verbs <see cref="ShowShellContextMenuCore"/> reports to its caller instead of invoking them.
    /// </summary>
    [Flags]
    private enum ShellVerbIntercepts
    {
        /// <summary>Every chosen command is invoked.</summary>
        None = 0,

        /// <summary>The canonical <c>rename</c> verb becomes <see cref="ShellContextMenuOutcome.RenameRequested"/>.</summary>
        Rename = 1,

        /// <summary>The canonical <c>delete</c> verb becomes <see cref="ShellContextMenuOutcome.DeleteRequested"/>.</summary>
        Delete = 2,
    }

    private static partial class NativeMethods
    {
        // GetCurrentPackageFullName return codes that prove package identity (see InterpretPackageProbe).
        public const int ErrorSuccess = 0;
        public const int ErrorInsufficientBuffer = 122;

        // Shell "Properties" dialog
        public const uint SeeMaskInvokeIdList = 0x0000000C;
        public const uint SeeMaskIdList = 0x00000004;
        public const int SwShow = 5;

        // SHGetFileInfo flags
        public const uint ShgfiIcon = 0x000000100;
        public const uint ShgfiLargeIcon = 0x000000000;
        public const uint ShgfiSmallIcon = 0x000000001;
        public const uint ShgfiPidl = 0x000000008;

        // SHFileOperation
        public const uint FoDelete = 0x0003;
        public const ushort FofSilent = 0x0004;
        public const ushort FofNoConfirmation = 0x0010;
        public const ushort FofAllowUndo = 0x0040;
        public const ushort FofNoErrorUi = 0x0400;
        public const ushort FofWantNukeWarning = 0x4000;

        // Context menu
        public const uint CmfNormal = 0x00000000;
        public const uint TpmReturnCmd = 0x0100;
        public const uint TpmRightButton = 0x0002;
        public const uint CmfExplore = 0x00000004;
        public const uint CmfCanRename = 0x00000010;
        public const uint CmfExtendedVerbs = 0x00000100;
        public const uint ShellMenuCmdFirst = 1;
        public const uint ShellMenuCmdLast = 0x7FFF;
        public const uint GcsVerbW = 0x00000004;
        public const uint CmicMaskUnicode = 0x00004000;
        public const uint CmicMaskShiftDown = 0x10000000;
        public const uint CmicMaskPtInvoke = 0x20000000;
        public const uint CmicMaskControlDown = 0x40000000;
        public const int SwShowNormal = 1;
        public const int VkShift = 0x10;
        public const int VkControl = 0x11;
        public const int WmNull = 0x0000;
        public const int WmCancelMode = 0x001F;
        public const int WmDrawItem = 0x002B;
        public const int WmMeasureItem = 0x002C;
        public const int WmInitMenuPopup = 0x0117;
        public const int WmMenuChar = 0x0120;
        public const int ENoInterface = unchecked((int)0x80004002);

        // Generic failure HRESULTs reported by SetDwmWindowAttribute instead of throwing.
        public const int EInvalidArg = unchecked((int)0x80070057);
        public const int ENotImpl = unchecked((int)0x80004001);
        public const int EFail = unchecked((int)0x80004005);

        // Undocumented, ordinal-only uxtheme exports (design system 1.x, phase 6.4b): SetPreferredAppMode
        // (Windows 10 1903+) and FlushMenuThemes. Called through function pointers, see TrySetPreferredAppMode.
        public const int UxThemeOrdinalSetPreferredAppMode = 135;
        public const int UxThemeOrdinalFlushMenuThemes = 136;

        // HRESULT_FROM_WIN32(ERROR_CANCELLED): the user cancelled a confirmation the invoked command showed.
        public const int HResultCancelled = unchecked((int)0x800704C7);

        // Low-level mouse hook + cross-process LVM_HITTEST
        public const int LvmHitTest = 0x1000 + 18;
        public const uint SmtoNormal = 0x0000;
        public const uint SmtoBlock = 0x0001;
        public const uint SmtoAbortIfHung = 0x0002;

        // Windows abandons (and, on repeat, silently uninstalls) a WH_MOUSE_LL hook that doesn't
        // return within HKCU\Control Panel\Desktop\LowLevelHooksTimeout (default 300ms) — this
        // whole IsListViewPointEmpty call happens inside that window, so the SendMessageTimeoutW
        // budget below must leave headroom for the OpenProcess/VirtualAllocEx/WriteProcessMemory/
        // ReadProcessMemory calls around it, not consume the full 300ms itself.
        public const uint HitTestTimeoutMs = 50;
        public const uint ProcessVmOperation = 0x0008;
        public const uint ProcessVmRead = 0x0010;
        public const uint ProcessVmWrite = 0x0020;
        public const uint MemCommit = 0x1000;
        public const uint MemRelease = 0x8000;
        public const uint PageReadWrite = 0x04;

        // Monitors / hidden broadcast window
        public const int MonitorinfofPrimary = 0x00000001;

        // Explorer restart recovery (ADR-034)
        public const uint MsgFltAllow = 1;
        public const uint GaParent = 1;

        // Per-monitor DPI (release-hardening F1)
        public const uint GaRoot = 2;
        public const int MdtEffectiveDpi = 0;
        public const int LogPixelsX = 88;
        public const int LogPixelsY = 90;

        // SPI_GETNONCLIENTMETRICS (system message font for the theme provider, review 6.1 M-1).
        public const uint SpiGetNonClientMetrics = 0x0029;
        public const int SmCxScreen = 0;
        public const int SmCyScreen = 1;

        // Hidden top-level window style: a popup that is never shown (no WS_VISIBLE).
        public const int WsPopup = unchecked((int)0x80000000);

        public static Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
        public static Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");

        // Shell
        [LibraryImport("Shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr ShGetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

        [LibraryImport("User32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyIconByHandle(IntPtr iconHandle);

        // Window lookup
        [LibraryImport("User32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr FindWindowW(string lpClassName, string? lpWindowName);

        [LibraryImport("User32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        // EnumWindows needs a managed delegate callback — DllImport required
        [DllImport("User32.dll", EntryPoint = "EnumWindows")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindowsNative(EnumWindowsCallback lpEnumFunc, IntPtr lParam);

        // Parenting & styles
        [LibraryImport("User32.dll", EntryPoint = "SetParent")]
        public static partial IntPtr SetParentNative(IntPtr hWndChild, IntPtr hWndNewParent);

        [LibraryImport("User32.dll", EntryPoint = "GetParent")]
        public static partial IntPtr GetParentNative(IntPtr hWnd);

        // GetClassName writes into a caller-supplied buffer — StringBuilder marshalling is not
        // supported by the LibraryImport source generator, so this one stays a classic DllImport.
        [DllImport("User32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [LibraryImport("User32.dll", EntryPoint = "GetWindow")]
        public static partial IntPtr GetWindowNative(IntPtr hWnd, int uCmd);

        [LibraryImport("User32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [LibraryImport("User32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [LibraryImport("User32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [LibraryImport("User32.dll", EntryPoint = "GetCursorPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("User32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("User32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("User32.dll", EntryPoint = "GetForegroundWindow")]
        public static partial IntPtr GetForegroundWindow();

        // ASFW_ANY == -1. Grants the foreground-set right away so another process (here: the
        // single-instance primary, known only by pipe name) can call SetForegroundWindow/Activate
        // successfully instead of merely flashing its taskbar button (ADR-023 D-8/R-13).
        [LibraryImport("User32.dll", EntryPoint = "AllowSetForegroundWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AllowSetForegroundWindow(int dwProcessId);

        // ShellExecuteEx carries a struct with string fields — DllImport (not source-gen LibraryImport)
        [DllImport("Shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellExecuteExW(ref ShellExecuteInfo lpExecInfo);

        /// <summary>
        /// Byte offset of <c>WINDOWPOS.hwndInsertAfter</c> (read/written in place inside
        /// <c>WM_WINDOWPOSCHANGING</c>; ADR-034 amendment 2026-10-09).
        /// </summary>
        public static readonly int WindowPosInsertAfterOffset = (int)Marshal.OffsetOf<WINDOWPOS>(nameof(WINDOWPOS.HwndInsertAfter));

        /// <summary>Byte offset of <c>WINDOWPOS.flags</c>.</summary>
        public static readonly int WindowPosFlagsOffset = (int)Marshal.OffsetOf<WINDOWPOS>(nameof(WINDOWPOS.Flags));

        /// <summary>
        /// Native <c>WINDOWPOS</c> layout. Only used to compute field offsets — the structure itself always
        /// lives in the sender's memory and is edited in place.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr Hwnd;
            public IntPtr HwndInsertAfter;
            public int X;
            public int Y;
            public int Cx;
            public int Cy;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X; public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ShellExecuteInfo
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpFile;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        // Known folder / PIDL
        [LibraryImport("Shell32.dll", EntryPoint = "SHGetKnownFolderIDList")]
        public static partial int SHGetKnownFolderIDList(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppidl);

        [LibraryImport("Ole32.dll", EntryPoint = "CoTaskMemFree")]
        public static partial void CoTaskMemFree(IntPtr pv);

        // SHGetFileInfo with PIDL (lpszPath is a PIDL pointer, not a string)
        [LibraryImport("Shell32.dll", EntryPoint = "SHGetFileInfoW")]
        public static partial IntPtr SHGetFileInfoPidl(IntPtr pidl, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

        [LibraryImport("Shell32.dll", EntryPoint = "SHBindToParent")]
        public static partial int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

        // Recycle bin
        [LibraryImport("Shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

        // SHFileOperation carries string fields with double-null termination — DllImport.
        [DllImport("Shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

        // Menu
        [LibraryImport("User32.dll", EntryPoint = "CreatePopupMenu")]
        public static partial IntPtr CreatePopupMenu();

        [LibraryImport("User32.dll", EntryPoint = "DestroyMenu")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyMenu(IntPtr hMenu);

        [LibraryImport("User32.dll", EntryPoint = "TrackPopupMenuEx")]
        public static partial uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        // Native shell context menu for a file-system path (SHParseDisplayName -> SHBindToParent) and the
        // helper-window foreground dance around TrackPopupMenuEx (SetForegroundWindow + WM_NULL, KB 135788).
        [LibraryImport("Shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [LibraryImport("User32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("User32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("User32.dll", EntryPoint = "GetAsyncKeyState")]
        public static partial short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        public struct ShQueryRbInfo
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ShFileOpStruct
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        /// <summary>
        /// <c>CMINVOKECOMMANDINFOEX</c>. Every string member is a raw pointer: the verb is passed as a
        /// <c>MAKEINTRESOURCE</c> command offset (in both <c>lpVerb</c> and <c>lpVerbW</c>), never as a real
        /// string, and the remaining strings are unused (zero).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CmInvokeCommandInfoEx
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public POINT ptInvoke;
        }

        /// <summary>
        /// COM interop declaration of the shell's <c>IShellFolder</c> interface.
        /// </summary>
        /// <remarks>
        /// Declaration order MUST match the native vtable; unused members exist only as vtable slots —
        /// never remove or reorder. Only <c>GetUIObjectOf</c> is called (from <c>ShowShellContextMenu</c>).
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        public interface IShellFolder
        {
            /// <summary><c>IShellFolder.ParseDisplayName</c>: parses a display name into a PIDL relative to this folder.</summary>
            /// <param name="hwnd">Owner window for any UI the parse operation may show.</param>
            /// <param name="pbc">Optional bind context, or <see cref="IntPtr.Zero"/>.</param>
            /// <param name="pszDisplayName">The display name to parse.</param>
            /// <param name="pchEaten">Receives the number of characters of <paramref name="pszDisplayName"/> that were parsed.</param>
            /// <param name="ppidl">Receives the resulting PIDL.</param>
            /// <param name="pdwAttributes">Requested/returned <c>SFGAO_*</c> attribute flags.</param>
            [PreserveSig]
            int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

            /// <summary><c>IShellFolder.EnumObjects</c>: creates an enumerator over the folder's contents.</summary>
            /// <param name="hwnd">Owner window for any UI the enumeration may show.</param>
            /// <param name="grfFlags">Combination of <c>SHCONTF_*</c> flags selecting which items to enumerate.</param>
            /// <param name="ppenumIDList">Receives the resulting <c>IEnumIDList</c> COM pointer.</param>
            [PreserveSig]
            int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

            /// <summary><c>IShellFolder.BindToObject</c>: binds to a subfolder identified by a relative PIDL.</summary>
            /// <param name="pidl">Relative PIDL of the subfolder to bind to.</param>
            /// <param name="pbc">Optional bind context, or <see cref="IntPtr.Zero"/>.</param>
            /// <param name="riid">Interface identifier requested on the resulting object.</param>
            /// <param name="ppv">Receives the resulting COM interface pointer.</param>
            [PreserveSig]
            int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

            /// <summary><c>IShellFolder.BindToStorage</c>: binds to the storage object of an item identified by a relative PIDL.</summary>
            /// <param name="pidl">Relative PIDL of the item to bind to.</param>
            /// <param name="pbc">Optional bind context, or <see cref="IntPtr.Zero"/>.</param>
            /// <param name="riid">Interface identifier requested on the resulting object.</param>
            /// <param name="ppv">Receives the resulting COM interface pointer.</param>
            [PreserveSig]
            int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

            /// <summary><c>IShellFolder.CompareIDs</c>: compares two items' relative PIDLs for sort order.</summary>
            /// <param name="lParam">Comparison flags, low word identifies the sorting rule.</param>
            /// <param name="pidl1">Relative PIDL of the first item.</param>
            /// <param name="pidl2">Relative PIDL of the second item.</param>
            [PreserveSig]
            int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

            /// <summary><c>IShellFolder.CreateViewObject</c>: creates a shell view-related object for the folder (e.g. a context menu).</summary>
            /// <param name="hwndOwner">Owner window for the resulting object's UI.</param>
            /// <param name="riid">Interface identifier requested on the resulting object.</param>
            /// <param name="ppv">Receives the resulting COM interface pointer.</param>
            [PreserveSig]
            int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

            /// <summary><c>IShellFolder.GetAttributesOf</c>: retrieves the <c>SFGAO_*</c> attributes of one or more items.</summary>
            /// <param name="cidl">Number of PIDLs in <paramref name="apidl"/>.</param>
            /// <param name="apidl">Relative PIDLs of the items to query.</param>
            /// <param name="rgfInOut">Requested attribute mask on input; receives the actual attributes on output.</param>
            [PreserveSig]
            int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

            /// <summary><c>IShellFolder.GetUIObjectOf</c>: retrieves a UI-related COM object for one or more items (e.g. <see cref="IContextMenu"/>).</summary>
            /// <param name="hwndOwner">Owner window for the resulting object's UI.</param>
            /// <param name="cidl">Number of PIDLs in <paramref name="apidl"/>.</param>
            /// <param name="apidl">Relative PIDLs of the items to query.</param>
            /// <param name="riid">Interface identifier requested on the resulting object.</param>
            /// <param name="rgfReserved">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
            /// <param name="ppv">Receives the resulting COM interface pointer.</param>
            [PreserveSig]
            int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

            /// <summary><c>IShellFolder.GetDisplayNameOf</c>: retrieves the display name of an item identified by a relative PIDL.</summary>
            /// <param name="pidl">Relative PIDL of the item.</param>
            /// <param name="uFlags">Combination of <c>SHGDN_*</c> flags controlling the name's form.</param>
            /// <param name="pName">Pointer to a <c>STRRET</c> structure that receives the name.</param>
            [PreserveSig]
            int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

            /// <summary><c>IShellFolder.SetNameOf</c>: renames an item identified by a relative PIDL.</summary>
            /// <param name="hwnd">Owner window for any UI the rename may show.</param>
            /// <param name="pidl">Relative PIDL of the item to rename.</param>
            /// <param name="pszName">The new display name.</param>
            /// <param name="uFlags">Combination of <c>SHGDN_*</c> flags controlling how <paramref name="pszName"/> is interpreted.</param>
            /// <param name="ppidlOut">Receives the item's new relative PIDL.</param>
            [PreserveSig]
            int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        /// <summary>
        /// COM interop declaration of the shell's <c>IContextMenu</c> interface — used to query and
        /// invoke a shell item's native context menu.
        /// </summary>
        /// <remarks>
        /// Declaration order MUST match the native vtable; unused members exist only as vtable slots —
        /// never remove or reorder. All three are called from <c>ShowShellContextMenuCore</c>
        /// (<c>GetCommandString</c> only to recognize the <c>rename</c> verb).
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E4-0000-0000-C000-000000000046")]
        public interface IContextMenu
        {
            /// <summary><c>IContextMenu.QueryContextMenu</c>: adds the item's context menu commands to an existing menu.</summary>
            /// <param name="hMenu">Handle of the menu to add commands to.</param>
            /// <param name="indexMenu">Zero-based position in <paramref name="hMenu"/> at which to insert the first new item.</param>
            /// <param name="idCmdFirst">Lowest command identifier value that may be used.</param>
            /// <param name="idCmdLast">Highest command identifier value that may be used.</param>
            /// <param name="uFlags">Combination of <c>CMF_*</c> flags controlling which commands are added.</param>
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            /// <summary><c>IContextMenu.InvokeCommand</c>: carries out the command selected from the context menu.</summary>
            /// <param name="pici">Structure identifying the command to invoke and its invocation context (the <c>EX</c> layout, flagged by its <c>cbSize</c>).</param>
            [PreserveSig]
            int InvokeCommand(ref CmInvokeCommandInfoEx pici);

            /// <summary><c>IContextMenu.GetCommandString</c>: retrieves a string (e.g. help text or verb) associated with a command.</summary>
            /// <param name="idCmd">Command identifier, always an offset from <c>idCmdFirst</c>.</param>
            /// <param name="uType">Combination of <c>GCS_*</c> flags selecting which string to retrieve and its encoding.</param>
            /// <param name="pReserved">Reserved; must be <see cref="IntPtr.Zero"/>.</param>
            /// <param name="pszName">Buffer that receives the requested string.</param>
            /// <param name="cchMax">Size, in characters, of the buffer pointed to by <paramref name="pszName"/>.</param>
            [PreserveSig]
            int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        }

        /// <summary>
        /// COM interop declaration of <c>IContextMenu2</c>: <see cref="IContextMenu"/> plus
        /// <c>HandleMenuMsg</c>, through which owner-drawn items and lazily-filled submenus receive
        /// <c>WM_INITMENUPOPUP</c>/<c>WM_DRAWITEM</c>/<c>WM_MEASUREITEM</c>.
        /// </summary>
        /// <remarks>
        /// A <c>[ComImport]</c> interface does not inherit the vtable slots of its base: the three
        /// <see cref="IContextMenu"/> members are re-declared first, in vtable order — never remove or reorder.
        /// Obtained by casting the <see cref="IContextMenu"/> RCW (a <c>QueryInterface</c>); only
        /// <c>HandleMenuMsg</c> is called through it.
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F4-0000-0000-C000-000000000046")]
        public interface IContextMenu2
        {
            /// <summary>Vtable slot of <see cref="IContextMenu.QueryContextMenu"/>.</summary>
            /// <param name="hMenu">Handle of the menu to add commands to.</param>
            /// <param name="indexMenu">Zero-based insertion position.</param>
            /// <param name="idCmdFirst">Lowest command identifier value that may be used.</param>
            /// <param name="idCmdLast">Highest command identifier value that may be used.</param>
            /// <param name="uFlags">Combination of <c>CMF_*</c> flags.</param>
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            /// <summary>Vtable slot of <see cref="IContextMenu.InvokeCommand"/>.</summary>
            /// <param name="pici">Structure identifying the command to invoke.</param>
            [PreserveSig]
            int InvokeCommand(ref CmInvokeCommandInfoEx pici);

            /// <summary>Vtable slot of <see cref="IContextMenu.GetCommandString"/>.</summary>
            /// <param name="idCmd">Command offset from <c>idCmdFirst</c>.</param>
            /// <param name="uType">Combination of <c>GCS_*</c> flags.</param>
            /// <param name="pReserved">Reserved; <see cref="IntPtr.Zero"/>.</param>
            /// <param name="pszName">Buffer that receives the string.</param>
            /// <param name="cchMax">Buffer size in characters.</param>
            [PreserveSig]
            int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

            /// <summary><c>IContextMenu2.HandleMenuMsg</c>: lets the handler process a menu message sent to the owner window.</summary>
            /// <param name="uMsg">The window message.</param>
            /// <param name="wParam">The message wParam.</param>
            /// <param name="lParam">The message lParam.</param>
            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        /// <summary>
        /// COM interop declaration of <c>IContextMenu3</c>: <see cref="IContextMenu2"/> plus
        /// <c>HandleMenuMsg2</c>, which also covers <c>WM_MENUCHAR</c> and returns the message result.
        /// </summary>
        /// <remarks>
        /// Same vtable rule as <see cref="IContextMenu2"/>: every base member is re-declared first, in order.
        /// Only <c>HandleMenuMsg2</c> is called through it.
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
        public interface IContextMenu3
        {
            /// <summary>Vtable slot of <see cref="IContextMenu.QueryContextMenu"/>.</summary>
            /// <param name="hMenu">Handle of the menu to add commands to.</param>
            /// <param name="indexMenu">Zero-based insertion position.</param>
            /// <param name="idCmdFirst">Lowest command identifier value that may be used.</param>
            /// <param name="idCmdLast">Highest command identifier value that may be used.</param>
            /// <param name="uFlags">Combination of <c>CMF_*</c> flags.</param>
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            /// <summary>Vtable slot of <see cref="IContextMenu.InvokeCommand"/>.</summary>
            /// <param name="pici">Structure identifying the command to invoke.</param>
            [PreserveSig]
            int InvokeCommand(ref CmInvokeCommandInfoEx pici);

            /// <summary>Vtable slot of <see cref="IContextMenu.GetCommandString"/>.</summary>
            /// <param name="idCmd">Command offset from <c>idCmdFirst</c>.</param>
            /// <param name="uType">Combination of <c>GCS_*</c> flags.</param>
            /// <param name="pReserved">Reserved; <see cref="IntPtr.Zero"/>.</param>
            /// <param name="pszName">Buffer that receives the string.</param>
            /// <param name="cchMax">Buffer size in characters.</param>
            [PreserveSig]
            int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

            /// <summary>Vtable slot of <see cref="IContextMenu2.HandleMenuMsg"/>.</summary>
            /// <param name="uMsg">The window message.</param>
            /// <param name="wParam">The message wParam.</param>
            /// <param name="lParam">The message lParam.</param>
            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

            /// <summary><c>IContextMenu3.HandleMenuMsg2</c>: like <c>HandleMenuMsg</c>, but also handles <c>WM_MENUCHAR</c> and returns the message result.</summary>
            /// <param name="uMsg">The window message.</param>
            /// <param name="wParam">The message wParam.</param>
            /// <param name="lParam">The message lParam.</param>
            /// <param name="plResult">Receives the value the owner window procedure must return for the message.</param>
            [PreserveSig]
            int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        // ── Low-level mouse hook, monitors, hidden broadcast window ─────────────────────────────
        // (constants for this block live with the other constants at the top of the class)
        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LVHITTESTINFO
        {
            public POINT pt;
            public uint flags;
            public int iItem;
            public int iSubItem;
            public int iGroup;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;

            public readonly string DeviceName => szDevice;

            public static MONITORINFOEX Create() => new() { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public NativeWindowProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [LibraryImport("Kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial IntPtr GetModuleHandleW(string? lpModuleName);

        // Package-identity probe (Microsoft Store channel). Called with a zero-length buffer: the return
        // code alone says whether the process has a package (see InterpretPackageProbe). It is a plain
        // return value, not GetLastError, so no SetLastError.
        [LibraryImport("Kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
        public static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

        // SetWindowsHookEx takes a managed delegate callback — DllImport required.
        [DllImport("User32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseCallback lpfn, IntPtr hMod, uint dwThreadId);

        [LibraryImport("User32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnhookWindowsHookEx(IntPtr hhk);

        [LibraryImport("User32.dll", EntryPoint = "CallNextHookEx")]
        public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [LibraryImport("User32.dll", EntryPoint = "WindowFromPoint")]
        public static partial IntPtr WindowFromPoint(POINT point);

        [LibraryImport("User32.dll", EntryPoint = "GetDoubleClickTime")]
        public static partial uint GetDoubleClickTime();

        [LibraryImport("User32.dll", EntryPoint = "ScreenToClient")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [LibraryImport("User32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
        public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("User32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        public static partial IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [LibraryImport("Kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        public static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [LibraryImport("Kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("Kernel32.dll", EntryPoint = "VirtualAllocEx", SetLastError = true)]
        public static partial IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint protect);

        [LibraryImport("Kernel32.dll", EntryPoint = "VirtualFreeEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualFreeEx(IntPtr proc, IntPtr addr, UIntPtr size, uint freeType);

        [LibraryImport("Kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WriteProcessMemory(IntPtr proc, IntPtr baseAddr, IntPtr buffer, UIntPtr size, out UIntPtr written);

        [LibraryImport("Kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ReadProcessMemory(IntPtr proc, IntPtr baseAddr, IntPtr buffer, UIntPtr size, out UIntPtr read);

        // EnumDisplayMonitors takes a managed delegate callback — DllImport required.
        [DllImport("User32.dll", EntryPoint = "EnumDisplayMonitors")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        // MONITORINFOEX carries a ByValTStr field — not blittable, so DllImport (not LibraryImport).
        [DllImport("User32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [LibraryImport("User32.dll", EntryPoint = "GetSystemMetrics")]
        public static partial int GetSystemMetrics(int nIndex);

        // ── Native panel sizing (ADR-022 / Phase 2) ─────────────────────────────────────────────
        [LibraryImport("User32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // Windows 10 1607+ only. Resolved lazily on first call, so an older OS surfaces as an
        // EntryPointNotFoundException at the call site (handled there with a system-DPI fallback).
        [LibraryImport("User32.dll", EntryPoint = "GetDpiForWindow")]
        public static partial uint GetDpiForWindow(IntPtr hWnd);

        // G-2 (ADR-022 phase 0-B): cancelling the modal sizing loop (WM_CANCELMODE is declared with the
        // other window messages at the top of this class).
        [LibraryImport("User32.dll", EntryPoint = "GetCapture")]
        public static partial IntPtr GetCapture();

        [LibraryImport("User32.dll", EntryPoint = "ReleaseCapture", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ReleaseCapture();

        [LibraryImport("User32.dll", EntryPoint = "SendMessageW")]
        public static partial IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Windows 8.1+ (shcore). Returns an HRESULT; resolved lazily, so a missing export surfaces as an
        // EntryPointNotFoundException/DllNotFoundException at the call site (handled there → 0).
        [LibraryImport("Shcore.dll", EntryPoint = "GetDpiForMonitor")]
        public static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [LibraryImport("User32.dll", EntryPoint = "GetDC")]
        public static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("User32.dll", EntryPoint = "ReleaseDC")]
        public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [LibraryImport("Gdi32.dll", EntryPoint = "GetDeviceCaps")]
        public static partial int GetDeviceCaps(IntPtr hdc, int nIndex);

        // WNDCLASS carries a delegate + string fields — DllImport (not source-gen LibraryImport).
        [DllImport("User32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [LibraryImport("User32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterClass(IntPtr lpClassName, IntPtr hInstance);

        [DllImport("User32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [LibraryImport("User32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("User32.dll", EntryPoint = "DefWindowProcW")]
        public static partial IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ── Explorer restart recovery (ADR-034) ─────────────────────────────────────────────────
        [LibraryImport("User32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial uint RegisterWindowMessageW(string lpString);

        [LibraryImport("User32.dll", EntryPoint = "ChangeWindowMessageFilterEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

        [LibraryImport("User32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowNative(IntPtr hWnd);

        [LibraryImport("User32.dll", EntryPoint = "GetAncestor")]
        public static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [LibraryImport("User32.dll", EntryPoint = "SetTimer", SetLastError = true)]
        public static partial IntPtr SetTimerNative(IntPtr hWnd, IntPtr nIdEvent, uint uElapse, IntPtr lpTimerFunc);

        [LibraryImport("User32.dll", EntryPoint = "KillTimer", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool KillTimerNative(IntPtr hWnd, IntPtr uIdEvent);

        // DWM window attributes (design system 1.x, phase 6.1): 4-byte values only (BOOL/enum).
        [LibraryImport("Dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        public static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        // GetProcAddress with an ordinal in the low word of lpProcName (MAKEINTRESOURCEA).
        [LibraryImport("Kernel32.dll", EntryPoint = "GetProcAddress")]
        public static partial IntPtr GetProcAddressByOrdinal(IntPtr hModule, nint ordinal);

        // System colours / fonts (design system 1.x, phase 6.1 review M-1): change fingerprint for the
        // theme provider — a high-contrast → high-contrast switch changes only these, not the flags.
        [LibraryImport("User32.dll", EntryPoint = "GetSysColor")]
        public static partial uint GetSysColor(int nIndex);

        [LibraryImport("User32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SystemParametersInfoNonClientMetrics(uint uiAction, uint uiParam, ref NonClientMetricsW pvParam, uint fWinIni);

        /// <summary>
        /// <c>LOGFONTW</c>. The face name is a fixed <see cref="ushort"/> buffer (UTF-16 code units) so the
        /// struct stays blittable for the <c>LibraryImport</c> source generator; it is never read.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct LogFontW
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            public fixed ushort lfFaceName[32];
        }

        /// <summary><c>NONCLIENTMETRICSW</c> including <c>iPaddedBorderWidth</c> (Vista+, 504 bytes).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct NonClientMetricsW
        {
            public uint cbSize;
            public int iBorderWidth;
            public int iScrollWidth;
            public int iScrollHeight;
            public int iCaptionWidth;
            public int iCaptionHeight;
            public LogFontW lfCaptionFont;
            public int iSmCaptionWidth;
            public int iSmCaptionHeight;
            public LogFontW lfSmCaptionFont;
            public int iMenuWidth;
            public int iMenuHeight;
            public LogFontW lfMenuFont;
            public LogFontW lfStatusFont;
            public LogFontW lfMessageFont;
            public int iPaddedBorderWidth;
        }

        /// <summary>
        /// <c>MONITORENUMPROC</c>. <paramref name="lprcMonitor"/> is kept as a raw pointer (the
        /// caller reads the rectangles through <c>GetMonitorInfo</c> instead), which keeps the
        /// delegate blittable.
        /// </summary>
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);
    }

    // ── High-resolution panel item icons (IShellItemImageFactory / system image list) ────────────
    // A separate nested class (not part of NativeMethods above) so this block merges independently of
    // unrelated shell work (e.g. the context-menu declarations in NativeMethods). Only GetDC/ReleaseDC/
    // DestroyIconByHandle are reused from NativeMethods.
    private static partial class IconNativeMethods
    {
        /// <summary><c>SIIGBF_BIGGERSIZEOK</c>: a bigger image than requested is acceptable.</summary>
        public const int SiigbfBiggerSizeOk = 0x00000001;

        /// <summary><c>SIIGBF_ICONONLY</c>: the item's icon, never a thumbnail (Fences/desktop behavior).</summary>
        public const int SiigbfIconOnly = 0x00000004;

        /// <summary><c>ILD_TRANSPARENT</c>: draw/extract with the image list's mask (keeps alpha).</summary>
        public const int IldTransparent = 0x00000001;

        /// <summary><c>BI_RGB</c>: uncompressed DIB.</summary>
        public const uint BiRgb = 0;

        /// <summary><c>DIB_RGB_COLORS</c>.</summary>
        public const uint DibRgbColors = 0;

        /// <summary>
        /// Upper bound for a bitmap read back into managed memory: the largest shell icon is 256 px, and
        /// <c>SIIGBF_BIGGERSIZEOK</c> may round up to at most that; anything larger is not an icon.
        /// </summary>
        public const int MaxIconBitmapSide = 1024;

        /// <summary><c>E_FAIL</c>.</summary>
        public const int EFail = unchecked((int)0x80004005);

        /// <summary><c>E_NOINTERFACE</c>.</summary>
        public const int ENoInterface = unchecked((int)0x80004002);

        /// <summary><c>E_INVALIDARG</c>.</summary>
        public const int EInvalidArg = unchecked((int)0x80070057);

        /// <summary><c>CLSCTX_INPROC_SERVER</c>.</summary>
        public const uint ClsctxInprocServer = 0x1;

        /// <summary><c>STGM_READ</c>: open the <c>.lnk</c> read-only.</summary>
        public const int StgmRead = 0x0;

        /// <summary><c>SLGP_RAWPATH</c>: the stored path as-is (environment variables unexpanded), no resolution.</summary>
        public const uint SlgpRawPath = 0x4;

        /// <summary>
        /// Buffer for <c>IShellLinkW::GetPath</c>. Larger than <c>MAX_PATH</c> so ordinary long targets fit; a
        /// longer one is truncated, which is harmless for root classification.
        /// </summary>
        public const int ShortcutPathBufferChars = 2048;

        /// <summary><c>DRIVE_NO_ROOT_DIR</c>.</summary>
        public const uint DriveNoRootDir = 1;

        /// <summary><c>DRIVE_REMOVABLE</c>.</summary>
        public const uint DriveRemovable = 2;

        /// <summary><c>DRIVE_FIXED</c>.</summary>
        public const uint DriveFixed = 3;

        /// <summary><c>DRIVE_REMOTE</c>.</summary>
        public const uint DriveRemote = 4;

        /// <summary><c>DRIVE_CDROM</c>.</summary>
        public const uint DriveCdRom = 5;

        /// <summary><c>DRIVE_RAMDISK</c>.</summary>
        public const uint DriveRamDisk = 6;

        /// <summary><c>IID_IShellItemImageFactory</c>.</summary>
        public static readonly Guid IidIShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

        /// <summary><c>IID_IImageList</c>.</summary>
        public static readonly Guid IidIImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

        /// <summary><c>CLSID_ShellLink</c>.</summary>
        public static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

        /// <summary><c>IID_IShellLinkW</c>.</summary>
        public static readonly Guid IidIShellLinkW = new("000214F9-0000-0000-C000-000000000046");

        // riid = IID_IShellItemImageFactory: asks for the factory interface directly, no intermediate
        // IShellItem RCW. Requires COM to be initialized on the calling thread (the WPF UI thread is STA).
        [LibraryImport("Shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [LibraryImport("Shell32.dll", EntryPoint = "SHGetImageList")]
        public static partial int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppvObj);

        // ADR-046 D-2: .lnk raw target / drive type (TryGetShortcutRawTarget, GetDriveKind).
        [LibraryImport("Ole32.dll", EntryPoint = "CoCreateInstance")]
        public static partial int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [LibraryImport("Kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint GetDriveTypeW(string lpRootPathName);

        [LibraryImport("Gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DeleteObject(IntPtr hObject);

        [LibraryImport("Gdi32.dll", EntryPoint = "GetObjectW")]
        public static partial int GetObjectW(IntPtr h, int c, out BITMAP pv);

        [LibraryImport("Gdi32.dll", EntryPoint = "GetDIBits")]
        public static unsafe partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte* lpvBits, ref BITMAPINFO lpbmi, uint usage);

        /// <summary>Native <c>SIZE</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        /// <summary>Native <c>BITMAP</c> (as filled by <c>GetObject</c> for an <c>HBITMAP</c>).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        /// <summary>Native <c>BITMAPINFOHEADER</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        /// <summary>
        /// Native <c>BITMAPINFO</c>. A 32-bpp <c>BI_RGB</c> request has no color table, but three
        /// <c>DWORD</c>s of slack are reserved anyway (room for <c>BI_BITFIELDS</c> masks) so a driver
        /// writing them can never overrun the structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColor0;
            public uint bmiColor1;
            public uint bmiColor2;
        }

        /// <summary>
        /// COM interop declaration of the shell's <c>IShellItemImageFactory</c> interface.
        /// </summary>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        public interface IShellItemImageFactory
        {
            /// <summary><c>IShellItemImageFactory.GetImage</c>: renders the item as an <c>HBITMAP</c>.</summary>
            /// <param name="size">Requested size in pixels.</param>
            /// <param name="flags">Combination of <c>SIIGBF_*</c> flags.</param>
            /// <param name="phbm">Receives the bitmap; the caller owns it (<c>DeleteObject</c>).</param>
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        /// <summary>
        /// COM interop declaration of comctl32's <c>IImageList</c> interface, up to <c>GetIcon</c>.
        /// </summary>
        /// <remarks>
        /// Declaration order MUST match the native vtable; the members before <c>GetIcon</c> exist only as
        /// vtable slots — never remove or reorder. Only <c>GetIcon</c> is called.
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        public interface IImageList
        {
            /// <summary><c>IImageList.Add</c> (vtable slot only).</summary>
            /// <param name="hbmImage">Image bitmap.</param>
            /// <param name="hbmMask">Mask bitmap.</param>
            /// <param name="pi">Receives the new index.</param>
            [PreserveSig]
            int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);

            /// <summary><c>IImageList.ReplaceIcon</c> (vtable slot only).</summary>
            /// <param name="i">Index to replace, or -1 to append.</param>
            /// <param name="hicon">Icon to store.</param>
            /// <param name="pi">Receives the index.</param>
            [PreserveSig]
            int ReplaceIcon(int i, IntPtr hicon, out int pi);

            /// <summary><c>IImageList.SetOverlayImage</c> (vtable slot only).</summary>
            /// <param name="iImage">Image index.</param>
            /// <param name="iOverlay">Overlay index.</param>
            [PreserveSig]
            int SetOverlayImage(int iImage, int iOverlay);

            /// <summary><c>IImageList.Replace</c> (vtable slot only).</summary>
            /// <param name="i">Index to replace.</param>
            /// <param name="hbmImage">Image bitmap.</param>
            /// <param name="hbmMask">Mask bitmap.</param>
            [PreserveSig]
            int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);

            /// <summary><c>IImageList.AddMasked</c> (vtable slot only).</summary>
            /// <param name="hbmImage">Image bitmap.</param>
            /// <param name="crMask">Mask color.</param>
            /// <param name="pi">Receives the new index.</param>
            [PreserveSig]
            int AddMasked(IntPtr hbmImage, int crMask, out int pi);

            /// <summary><c>IImageList.Draw</c> (vtable slot only).</summary>
            /// <param name="pimldp">Pointer to an <c>IMAGELISTDRAWPARAMS</c>.</param>
            [PreserveSig]
            int Draw(IntPtr pimldp);

            /// <summary><c>IImageList.Remove</c> (vtable slot only).</summary>
            /// <param name="i">Index to remove.</param>
            [PreserveSig]
            int Remove(int i);

            /// <summary><c>IImageList.GetIcon</c>: creates an icon from an image and mask.</summary>
            /// <param name="i">Image index.</param>
            /// <param name="flags">Combination of <c>ILD_*</c> flags.</param>
            /// <param name="picon">Receives the icon; the caller owns it (<c>DestroyIcon</c>).</param>
            [PreserveSig]
            int GetIcon(int i, int flags, out IntPtr picon);
        }

        /// <summary>
        /// COM interop declaration of the shell's <c>IShellLinkW</c> interface, up to <c>GetPath</c>.
        /// </summary>
        /// <remarks>
        /// <c>GetPath</c> is the first method after <c>IUnknown</c>; the remaining methods (including
        /// <c>Resolve</c>, which must never be called here) are intentionally not declared.
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        public interface IShellLinkW
        {
            /// <summary><c>IShellLinkW.GetPath</c>: copies the link target's path.</summary>
            /// <param name="pszFile">Caller-allocated UTF-16 buffer.</param>
            /// <param name="cch">Size of <paramref name="pszFile"/> in characters.</param>
            /// <param name="pfd">Optional <c>WIN32_FIND_DATAW</c>, or <see cref="IntPtr.Zero"/>.</param>
            /// <param name="fFlags">Combination of <c>SLGP_*</c> flags.</param>
            [PreserveSig]
            int GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        }

        /// <summary>
        /// COM interop declaration of <c>IPersistFile</c> (with its <c>IPersist</c> base slot), up to <c>Load</c>.
        /// </summary>
        /// <remarks>
        /// Declaration order MUST match the native vtable; <c>GetClassID</c>/<c>IsDirty</c> exist only as
        /// vtable slots — never remove or reorder.
        /// </remarks>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        public interface IPersistFile
        {
            /// <summary><c>IPersist.GetClassID</c> (vtable slot only).</summary>
            /// <param name="pClassID">Receives the CLSID.</param>
            [PreserveSig]
            int GetClassID(out Guid pClassID);

            /// <summary><c>IPersistFile.IsDirty</c> (vtable slot only).</summary>
            [PreserveSig]
            int IsDirty();

            /// <summary><c>IPersistFile.Load</c>: loads the object from a file.</summary>
            /// <param name="pszFileName">Absolute path of the file.</param>
            /// <param name="dwMode">Combination of <c>STGM_*</c> flags.</param>
            [PreserveSig]
            int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        }
    }
}
