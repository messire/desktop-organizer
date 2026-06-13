using System.Text;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL) that recognises desktop gestures.
/// See ADR-003. The hook only observes input; it suppresses a single right-button-up
/// when a lasso drag completes, so the desktop context menu does not appear.
/// </summary>
internal sealed class DesktopGestureService : IDesktopGestureService
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;

    private const int DragThreshold = 8;
    private const int DoubleClickSlop = 4;

    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hookHandle;

    private int _lastLeftDownTime;
    private NativeMethods.POINT _lastLeftDownPos;

    private bool _rightDown;
    private bool _rightDragging;
    private NativeMethods.POINT _rightStart;

    /// <inheritdoc />
    public event EventHandler? DesktopDoubleClicked;

    /// <inheritdoc />
    public event EventHandler<DesktopLassoEventArgs>? LassoCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopGestureService"/> class.
    /// </summary>
    public DesktopGestureService() => _proc = HookCallback;

    /// <inheritdoc />
    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _hookHandle = NativeMethods.SetWindowsHookExW(WhMouseLl, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        var swallow = false;

        switch ((int)wParam)
        {
            case WmLButtonDown:
                HandleLeftDown(data.pt);
                break;

            case WmRButtonDown:
                if (IsDesktopPoint(data.pt))
                {
                    _rightDown = true;
                    _rightDragging = false;
                    _rightStart = data.pt;
                }

                break;

            case WmMouseMove:
                if (_rightDown && !_rightDragging && Chebyshev(_rightStart, data.pt) > DragThreshold)
                    _rightDragging = true;

                break;

            case WmRButtonUp:
                if (_rightDown && _rightDragging)
                {
                    RaiseLasso(_rightStart, data.pt);
                    swallow = true; // hide the desktop context menu for a completed drag
                }

                _rightDown = false;
                _rightDragging = false;
                break;
        }

        var next = NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        return swallow ? (IntPtr)1 : next;
    }

    private void HandleLeftDown(NativeMethods.POINT pt)
    {
        var now = Environment.TickCount;
        var isDouble = now - _lastLeftDownTime <= NativeMethods.GetDoubleClickTime()
                       && Chebyshev(_lastLeftDownPos, pt) <= DoubleClickSlop;

        _lastLeftDownTime = now;
        _lastLeftDownPos = pt;

        if (!isDouble || !IsEmptyDesktopPoint(pt)) return;

        _lastLeftDownTime = 0; // consume so a triple click isn't a second double
        DesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseLasso(NativeMethods.POINT start, NativeMethods.POINT end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);
        LassoCompleted?.Invoke(this, new DesktopLassoEventArgs(x, y, w, h));
    }

    private static int Chebyshev(NativeMethods.POINT a, NativeMethods.POINT b)
        => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Whether the point is over the desktop (background or icon view), not over an app/panel.</summary>
    private static bool IsDesktopPoint(NativeMethods.POINT pt)
        => GetClass(NativeMethods.WindowFromPoint(pt)) is "SysListView32" or "SHELLDLL_DefView" or "WorkerW" or "Progman";

    /// <summary>Whether the point is over an empty part of the desktop (not on an icon).</summary>
    private static bool IsEmptyDesktopPoint(NativeMethods.POINT pt)
    {
        var hwnd = NativeMethods.WindowFromPoint(pt);
        var cls = GetClass(hwnd);

        // Pure background layers are always "empty".
        if (cls is "WorkerW" or "Progman") return true;
        if (cls is not ("SysListView32" or "SHELLDLL_DefView")) return false;

        var listView = cls == "SysListView32"
            ? hwnd
            : NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SysListView32", null);

        return listView != IntPtr.Zero && NativeMethods.ListViewPointIsEmpty(listView, pt);
    }

    private static string GetClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(256);
        var len = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static class NativeMethods
    {
        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int LvmHitTest = 0x1000 + 18;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint MemCommit = 0x1000;
        private const uint MemRelease = 0x8000;
        private const uint PageReadWrite = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

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
        private struct LVHITTESTINFO
        {
            public POINT pt;
            public uint flags;
            public int iItem;
            public int iSubItem;
            public int iGroup;
        }

        [DllImport("User32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("User32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("User32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("User32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("User32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("User32.dll")]
        public static extern uint GetDoubleClickTime();

        [DllImport("User32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("Kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("Kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("Kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint protect);

        [DllImport("Kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, UIntPtr size, uint freeType);

        [DllImport("Kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteProcessMemory(IntPtr proc, IntPtr baseAddr, IntPtr buffer, UIntPtr size, out UIntPtr written);

        [DllImport("Kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(IntPtr proc, IntPtr baseAddr, IntPtr buffer, UIntPtr size, out UIntPtr read);

        /// <summary>
        /// Cross-process LVM_HITTEST: returns true when <paramref name="screenPt"/> hits no list item.
        /// Defensive — any failure returns false (treated as "not empty"), per ADR-003 fallback.
        /// </summary>
        public static bool ListViewPointIsEmpty(IntPtr listView, POINT screenPt)
        {
            var clientPt = screenPt;
            if (!ScreenToClient(listView, ref clientPt)) return false;

            GetWindowThreadProcessId(listView, out var pid);
            if (pid == 0) return false;

            var proc = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, pid);
            if (proc == IntPtr.Zero) return false;

            var size = Marshal.SizeOf<LVHITTESTINFO>();
            var remote = IntPtr.Zero;
            var local = IntPtr.Zero;

            try
            {
                remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)size, MemCommit, PageReadWrite);
                if (remote == IntPtr.Zero) return false;

                var info = new LVHITTESTINFO { pt = clientPt, iItem = -1 };
                local = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(info, local, false);

                if (!WriteProcessMemory(proc, remote, local, (UIntPtr)size, out _)) return false;
                SendMessageW(listView, LvmHitTest, IntPtr.Zero, remote);
                if (!ReadProcessMemory(proc, remote, local, (UIntPtr)size, out _)) return false;

                info = Marshal.PtrToStructure<LVHITTESTINFO>(local);
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
                if (remote != IntPtr.Zero) VirtualFreeEx(proc, remote, UIntPtr.Zero, MemRelease);
                CloseHandle(proc);
            }
        }
    }
}
