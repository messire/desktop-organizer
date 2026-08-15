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
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
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
    }
}
