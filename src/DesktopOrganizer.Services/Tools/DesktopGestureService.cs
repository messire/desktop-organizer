using System.Text;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL) that recognises desktop gestures.
/// See ADR-003. The hook only observes input; it suppresses a single right-button-up
/// when a lasso drag completes, so the desktop context menu does not appear.
/// </summary>
internal sealed class DesktopGestureService : IDesktopGestureService, IDesktopGestureEnvironment
{
    private const int WhMouseLl = 14;

    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly DesktopGestureStateMachine _state;
    private IntPtr _hookHandle;

    /// <inheritdoc />
    public event EventHandler? DesktopDoubleClicked;

    /// <inheritdoc />
    public event EventHandler<DesktopLassoEventArgs>? LassoCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopGestureService"/> class.
    /// </summary>
    public DesktopGestureService()
    {
        _proc = HookCallback;
        _state = new DesktopGestureStateMachine(this);
    }

    /// <inheritdoc />
    int IDesktopGestureEnvironment.TickCount => Environment.TickCount;

    /// <inheritdoc />
    uint IDesktopGestureEnvironment.DoubleClickTime => NativeMethods.GetDoubleClickTime();

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

    /// <summary>
    /// The WH_MOUSE_LL callback. This runs on every mouse event of the whole OS, so it does the
    /// absolute minimum and — critically — must never let an exception escape into the hook chain:
    /// an unhandled exception here stalls input system-wide. All real work lives in
    /// <see cref="ProcessMessage"/> / <see cref="DesktopGestureStateMachine"/> behind a try/catch.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var swallow = false;

        try
        {
            swallow = ProcessMessage(wParam, lParam);
        }
        catch (Exception ex)
        {
            // Defensive: never swallow the event on failure — a bug here must not turn into a
            // "right click stopped working" for the entire system. Also covers a garbage lParam.
            Console.Error.WriteLine($"Desktop gesture hook failed: {ex.Message}");
            swallow = false;
        }

        var next = NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        return swallow ? (IntPtr)1 : next;
    }

    /// <summary>
    /// Marshals the hook payload, advances the gesture state machine and notifies subscribers.
    /// </summary>
    /// <returns>Whether the message must be suppressed.</returns>
    private bool ProcessMessage(IntPtr wParam, IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

        // The state machine has already reset its own state by the time it returns a decision,
        // so a throwing subscriber below cannot leave the gesture latched.
        var decision = _state.Process((int)wParam, data.pt.X, data.pt.Y);
        if (decision.RaisesEvent) RaiseSubscribers(decision);

        return decision.Swallow;
    }

    /// <summary>
    /// Notifies subscribers inside its own guard: a throwing subscriber must not change the
    /// suppression decision, nor propagate out of the hook.
    /// </summary>
    private void RaiseSubscribers(DesktopGestureDecision decision)
    {
        try
        {
            if (decision.RaiseDoubleClick) DesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
            if (decision.Lasso is { } lasso) LassoCompleted?.Invoke(this, lasso);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Desktop gesture subscriber failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    bool IDesktopGestureEnvironment.IsDesktopPoint(int x, int y)
        => IsDesktopPoint(new NativeMethods.POINT { X = x, Y = y });

    /// <inheritdoc />
    bool IDesktopGestureEnvironment.IsEmptyDesktopPoint(int x, int y)
        => IsEmptyDesktopPoint(new NativeMethods.POINT { X = x, Y = y });

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
