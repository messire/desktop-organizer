using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Enumerates display monitors via <c>EnumDisplayMonitors</c> and exposes them in WPF
/// device-independent (DIP) coordinates. Native rectangles are physical pixels; they are
/// scaled down by the system DPI factor so values can feed <c>Window.Left</c>/<c>Top</c>.
/// </summary>
/// <remarks>
/// All P/Invoke is delegated to <see cref="INativeApi"/> per the project invariant.
/// MANUAL VERIFY (Win10/Win11): per-monitor-DPI mixed-scaling setups. WPF (and this service)
/// use the system (primary) DPI scale; monitors with a different scale may need refinement.
/// MANUAL VERIFY (Win10/Win11): disconnect/disable a monitor — WM_DISPLAYCHANGE must fire and
/// panels on the removed monitor must relocate to the primary monitor.
/// </remarks>
internal sealed class MonitorService(INativeApi nativeApi) : IMonitorService
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;

    // Keep the WndProc rooted for the lifetime of the message window so the GC cannot collect it.
    private NativeWindowProc? _wndProc;
    private NativeMessageWindow _messageWindow;

    /// <inheritdoc />
    public event Action? MonitorsChanged;

    /// <inheritdoc />
    public void Start()
    {
        if (_messageWindow.IsValid) return;

        try
        {
            _wndProc = WndProcImpl;
            var className = "DesktopOrganizer.MonitorWatcher." + Guid.NewGuid().ToString("N");

            _messageWindow = nativeApi.CreateMessageWindow(className, _wndProc);
            if (!_messageWindow.IsValid)
            {
                // Do not fail the app: multi-monitor updates degrade to "no live notifications",
                // but the reason must be visible instead of silently no-oping.
                Console.Error.WriteLine(
                    $"MonitorService could not create its broadcast window (Win32 error {_messageWindow.LastError}).");
                _wndProc = null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MonitorService.Start failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_messageWindow.HasResources)
        {
            nativeApi.DestroyMessageWindow(_messageWindow);
            _messageWindow = default;
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

        return nativeApi.DefaultWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            var scale = nativeApi.GetSystemDpiScale();
            return nativeApi.GetMonitorInfos()
                .Select(m => new MonitorInfo(m.DeviceName, ToDipRect(m.Bounds, scale), ToDipRect(m.WorkArea, scale), m.IsPrimary))
                .ToList();
        }
        catch (Exception ex)
        {
            // Defensive: never let a native failure bubble into the caller (often the UI thread).
            Console.Error.WriteLine($"MonitorService.GetMonitors failed: {ex.Message}");
            return [];
        }
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

    private static Rect ToDipRect(System.Drawing.Rectangle r, double scale)
    {
        var safeScale = scale > 0 ? scale : 1.0;
        return new Rect(r.Left / safeScale, r.Top / safeScale, r.Width / safeScale, r.Height / safeScale);
    }

    private MonitorInfo FallbackMonitor()
    {
        // Last-resort single virtual screen; keeps callers from crashing if enumeration yields nothing.
        var scale = nativeApi.GetSystemDpiScale();
        var safeScale = scale > 0 ? scale : 1.0;
        var size = nativeApi.GetPrimaryScreenSizePx();
        var w = size.Width / safeScale;
        var h = size.Height / safeScale;
        var rect = new Rect(0, 0, w > 0 ? w : 1920, h > 0 ? h : 1080);
        return new MonitorInfo(@"\\.\DISPLAY1", rect, rect, true);
    }
}
