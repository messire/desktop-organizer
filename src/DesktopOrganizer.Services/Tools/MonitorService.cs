using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Enumerates display monitors via <c>EnumDisplayMonitors</c> and exposes them in WPF
/// device-independent (DIP) coordinates. Native rectangles are physical pixels; they are
/// scaled down by the system DPI factor so values can feed <c>Window.Left</c>/<c>Top</c>.
/// </summary>
/// <remarks>
/// MANUAL VERIFY (Win10/Win11): per-monitor-DPI mixed-scaling setups. WPF (and this service)
/// use the system (primary) DPI scale; monitors with a different scale may need refinement.
/// MANUAL VERIFY (Win10/Win11): disconnect/disable a monitor — WM_DISPLAYCHANGE must fire and
/// panels on the removed monitor must relocate to the primary monitor.
/// </remarks>
internal sealed class MonitorService : IMonitorService
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;

    // Keep the WndProc rooted for the lifetime of the message window so the GC cannot collect it.
    private NativeMethods.WndProc? _wndProc;
    private IntPtr _messageWindow;
    private ushort _classAtom;

    /// <inheritdoc />
    public event Action? MonitorsChanged;

    /// <inheritdoc />
    public void Start()
    {
        if (_messageWindow != IntPtr.Zero) return;

        try
        {
            _wndProc = WndProcImpl;
            var className = "DesktopOrganizer.MonitorWatcher." + Guid.NewGuid().ToString("N");

            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = className,
            };

            _classAtom = NativeMethods.RegisterClass(ref wc);
            if (_classAtom == 0) return;

            // IMPORTANT: a message-only window (HWND_MESSAGE parent) does NOT receive broadcast
            // messages such as WM_DISPLAYCHANGE / WM_SETTINGCHANGE — Windows excludes message-only
            // windows from broadcasts by design. We therefore create an ordinary hidden top-level
            // window (no parent, WS_POPUP, never WS_VISIBLE). Top-level windows DO receive broadcasts,
            // while WS_POPUP without WS_VISIBLE keeps it off-screen and invisible to the user.
            _messageWindow = NativeMethods.CreateWindowEx(
                0, className, className, NativeMethods.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MonitorService.Start failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_messageWindow != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_messageWindow);
            _messageWindow = IntPtr.Zero;
        }

        if (_classAtom != 0)
        {
            NativeMethods.UnregisterClass((IntPtr)_classAtom, NativeMethods.GetModuleHandle(null));
            _classAtom = 0;
        }

        _wndProc = null;
    }

    private IntPtr WndProcImpl(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // Minimal work in the WndProc; never throw out of it.
        try
        {
            if (msg is WmDisplayChange or WmSettingChange)
                MonitorsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MonitorService WndProc error: {ex.Message}");
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var scale = GetSystemScale();
        var result = new List<MonitorInfo>();
        var collected = result;

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            var info = TryGetMonitorInfo(hMonitor, scale);
            if (info is not null) collected.Add(info);
            return true; // continue enumeration
        }

        try
        {
            // Use a local callback that closes over the scale/list for this call only.
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // Defensive: never let a native failure bubble into the caller (often the UI thread).
            Console.Error.WriteLine($"EnumDisplayMonitors failed: {ex.Message}");
        }

        return result;
    }

    /// <inheritdoc />
    public MonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors.FirstOrDefault()
               ?? FallbackMonitor();
    }

    /// <inheritdoc />
    public MonitorInfo? FindMonitorAt(System.Windows.Point point)
        => GetMonitors().FirstOrDefault(m => m.Bounds.Contains(point));

    /// <inheritdoc />
    public MonitorInfo? FindByDeviceName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;
        return GetMonitors().FirstOrDefault(m =>
            string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static MonitorInfo? TryGetMonitorInfo(IntPtr hMonitor, double scale)
    {
        var mi = NativeMethods.MONITORINFOEX.Create();
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return null;

        var bounds = ToDipRect(mi.rcMonitor, scale);
        var work = ToDipRect(mi.rcWork, scale);
        var isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
        return new MonitorInfo(mi.DeviceName, bounds, work, isPrimary);
    }

    private static Rect ToDipRect(NativeMethods.RECT r, double scale)
    {
        var x = r.left / scale;
        var y = r.top / scale;
        var w = (r.right - r.left) / scale;
        var h = (r.bottom - r.top) / scale;
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// System DPI scale (e.g. 1.0 at 96 DPI, 1.5 at 144 DPI). Defaults to 1.0 on failure.
    /// </summary>
    private static double GetSystemScale()
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return 1.0;

        try
        {
            var dpi = NativeMethods.GetDeviceCaps(hdc, NativeMethods.LOGPIXELSX);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static MonitorInfo FallbackMonitor()
    {
        // Last-resort single virtual screen; keeps callers from crashing if enumeration yields nothing.
        var scale = GetSystemScale();
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) / scale;
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) / scale;
        var rect = new Rect(0, 0, w > 0 ? w : 1920, h > 0 ? h : 1080);
        return new MonitorInfo(@"\\.\DISPLAY1", rect, rect, true);
    }

    private static class NativeMethods
    {
        public const int MONITORINFOF_PRIMARY = 0x00000001;
        public const int LOGPIXELSX = 88;
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        // Hidden top-level window style: a popup that is never shown (no WS_VISIBLE).
        public const int WS_POPUP = unchecked((int)0x80000000);

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        public delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("User32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("User32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("User32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("User32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("User32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("Gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterClass(IntPtr lpClassName, IntPtr hInstance);

        [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("User32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
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
    }
}
