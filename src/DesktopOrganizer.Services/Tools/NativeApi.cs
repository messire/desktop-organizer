using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal partial class NativeApi: INativeApi
{
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
    public IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.SendMessageW(hwnd, msg, wParam, lParam);

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
    public bool ShowFileProperties(string path)
    {
        var info = new NativeMethods.ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<NativeMethods.ShellExecuteInfo>(),
            lpVerb = "properties",
            lpFile = path,
            nShow = NativeMethods.SwShow,
            fMask = NativeMethods.SeeMaskInvokeIdList,
        };

        return NativeMethods.ShellExecuteExW(ref info);
    }

    /// <inheritdoc />
    public bool ShowWindow(IntPtr hwnd, int command)
        => NativeMethods.ShowWindow(hwnd, command);

    /// <inheritdoc />
    public bool IsWindowVisible(IntPtr hwnd)
        => NativeMethods.IsWindowVisible(hwnd);

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
    /// apartment-threaded COM objects; TrackPopupMenuEx also requires the owning STA thread.</remarks>
    public void ShowShellContextMenu(IntPtr pidl, IntPtr ownerHwnd, int screenX, int screenY)
    {
        System.Diagnostics.Debug.Assert(
            System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA,
            "ShowShellContextMenu must be called on an STA thread.");

        if (pidl == IntPtr.Zero)
        {
            return;
        }

        var hMenu = IntPtr.Zero;
        object? parentFolder = null;
        object? contextMenu = null;
        try
        {
            var iidShellFolder = NativeMethods.IidIShellFolder;
            // childPidl is a pointer INTO the parent's PIDL allocation — do NOT free it with CoTaskMemFree.
            // The caller (SystemShellService) owns and frees the original pidl separately.
            var hr = NativeMethods.SHBindToParent(pidl, ref iidShellFolder, out var parentPtr, out var childPidl);
            if (hr != 0 || parentPtr == IntPtr.Zero || childPidl == IntPtr.Zero)
            {
                return;
            }

            parentFolder = Marshal.GetObjectForIUnknown(parentPtr);
            Marshal.Release(parentPtr);
            if (parentFolder is not NativeMethods.IShellFolder shellFolder)
            {
                return;
            }

            var apidl = new[] { childPidl };
            var iidContextMenu = NativeMethods.IidIContextMenu;
            hr = shellFolder.GetUIObjectOf(ownerHwnd, 1, apidl, ref iidContextMenu, IntPtr.Zero, out var ctxPtr);
            if (hr != 0 || ctxPtr == IntPtr.Zero)
            {
                return;
            }

            contextMenu = Marshal.GetObjectForIUnknown(ctxPtr);
            Marshal.Release(ctxPtr);
            if (contextMenu is not NativeMethods.IContextMenu menu)
            {
                return;
            }

            hMenu = NativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                return;
            }

            const uint cmdFirst = 1;
            const uint cmdLast = 0x7FFF;

            // QueryContextMenu returns an HRESULT whose SUCCESS code carries the number of
            // inserted items in the low word. A failed query (or an empty menu) must not be
            // tracked/invoked — doing so passes a stale/empty HMENU into the shell verb
            // dispatch and is a source of access violations on virtual/broken items.
            var queryHr = menu.QueryContextMenu(hMenu, 0, cmdFirst, cmdLast, NativeMethods.CmfNormal);
            if (queryHr < 0)
            {
                return;
            }

            var cmd = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton,
                screenX,
                screenY,
                ownerHwnd,
                IntPtr.Zero);

            if (cmd == 0)
            {
                return;
            }

            var invoke = new NativeMethods.CmInvokeCommandInfo
            {
                cbSize = Marshal.SizeOf<NativeMethods.CmInvokeCommandInfo>(),
                hwnd = ownerHwnd,
                lpVerb = (IntPtr)(cmd - cmdFirst),
                nShow = NativeMethods.SwShow,
            };
            menu.InvokeCommand(ref invoke);
        }
        catch
        {
            // Degrade silently: a broken/virtual item must never disrupt the UI thread.
        }
        finally
        {
            if (hMenu != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(hMenu);
            }

            if (contextMenu is not null)
            {
                Marshal.ReleaseComObject(contextMenu);
            }

            if (parentFolder is not null)
            {
                Marshal.ReleaseComObject(parentFolder);
            }
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

    /// <inheritdoc />
    public bool SendToRecycleBin(string[] paths, IntPtr ownerHwnd)
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
                fFlags = NativeMethods.FofAllowUndo | NativeMethods.FofNoConfirmation | NativeMethods.FofNoErrorUi | NativeMethods.FofSilent,
            };

            var result = NativeMethods.SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

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
            Console.Error.WriteLine($"ListView hit-test failed: {ex.Message}");
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
                    (mi.dwFlags & NativeMethods.MonitorinfofPrimary) != 0));
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
            Console.Error.WriteLine($"EnumDisplayMonitors failed: {ex.Message}");
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
            return dpi > 0 ? dpi / 96.0 : 1.0;
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
            0, className, className, NativeMethods.WsPopup, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

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

    // ── Native panel sizing primitives (ADR-022 / Phase 2) ──────────────────────────────────────

    /// <inheritdoc />
    public bool GetWindowRect(IntPtr hwnd, out NativeRectPx rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var native))
        {
            // Defensive: a destroyed/invalid HWND must not throw into a window procedure.
            Console.Error.WriteLine($"GetWindowRect failed: {Marshal.GetLastWin32Error()}");
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
                if (dpi > 0) return dpi / 96.0;
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

    private static System.Drawing.Rectangle ToRectangle(NativeMethods.RECT r)
        => System.Drawing.Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);

    private static partial class NativeMethods
    {
        // Shell "Properties" dialog
        public const uint SeeMaskInvokeIdList = 0x0000000C;
        public const uint SeeMaskIdList = 0x00000004;
        public const int  SwShow = 5;

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

        // Context menu
        public const uint CmfNormal = 0x00000000;
        public const uint TpmReturnCmd = 0x0100;
        public const uint TpmRightButton = 0x0002;

        // Low-level mouse hook + cross-process LVM_HITTEST
        public const int LvmHitTest = 0x1000 + 18;
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
        public const int LogPixelsX = 88;
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

        [LibraryImport("User32.dll", EntryPoint = "SendMessageW")]
        public static partial IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ShellExecuteInfo
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
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
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CmInvokeCommandInfo
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
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        public interface IShellFolder
        {
            [PreserveSig]
            int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

            [PreserveSig]
            int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

            [PreserveSig]
            int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

            [PreserveSig]
            int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

            [PreserveSig]
            int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

            [PreserveSig]
            int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

            [PreserveSig]
            int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

            [PreserveSig]
            int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

            [PreserveSig]
            int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

            [PreserveSig]
            int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E4-0000-0000-C000-000000000046")]
        public interface IContextMenu
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            [PreserveSig]
            int InvokeCommand(ref CmInvokeCommandInfo pici);

            [PreserveSig]
            int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
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
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [LibraryImport("User32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("User32.dll", EntryPoint = "DefWindowProcW")]
        public static partial IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// <c>MONITORENUMPROC</c>. <paramref name="lprcMonitor"/> is kept as a raw pointer (the
        /// caller reads the rectangles through <c>GetMonitorInfo</c> instead), which keeps the
        /// delegate blittable.
        /// </summary>
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);
    }
}
