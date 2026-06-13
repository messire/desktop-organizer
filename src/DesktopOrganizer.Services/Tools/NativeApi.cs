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
    public IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => NativeMethods.GetWindowLongPtrW(hwnd, index);

    /// <inheritdoc />
    public IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong)
        => NativeMethods.SetWindowLongPtrW(hwnd, index, newLong);

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

    private static partial class NativeMethods
    {
        // Shell "Properties" dialog
        public const uint SeeMaskInvokeIdList = 0x0000000C;
        public const int  SwShow = 5;
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

        [LibraryImport("User32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [LibraryImport("User32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [LibraryImport("User32.dll", EntryPoint = "GetCursorPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("User32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("User32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowVisible(IntPtr hWnd);

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
    }
}
