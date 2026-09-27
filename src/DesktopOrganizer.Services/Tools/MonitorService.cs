using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Enumerates display monitors via <c>EnumDisplayMonitors</c> and exposes them in the single
/// desktop-DIP space (release-hardening F, ADR-037). All px↔DIP conversion is delegated to
/// <see cref="IDesktopScaleProvider"/>'s current <see cref="DesktopScaleModel"/> snapshot — this
/// class itself never divides a physical-pixel rectangle by a DPI scale.
/// </summary>
/// <remarks>
/// All P/Invoke is delegated to <see cref="INativeApi"/> per the project invariant.
/// MANUAL VERIFY (Win10/Win11): per-monitor-DPI mixed-scaling setups — see
/// docs/PLANS/release-hardening-1.0-F-dpi.md §10.
/// MANUAL VERIFY (Win10/Win11): disconnect/disable a monitor — WM_DISPLAYCHANGE must fire and
/// panels on the removed monitor must relocate to the primary monitor.
/// </remarks>
internal sealed class MonitorService(INativeApi nativeApi, IDesktopScaleProvider desktopScaleProvider, IAppLog? appLog = null) : IMonitorService
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;

    // Keep the WndProc rooted for the lifetime of the message window so the GC cannot collect it.
    private NativeWindowProc? _wndProc;
    private NativeMessageWindow _messageWindow;
    private bool _scaleProviderSubscribed;

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => appLog ?? AppLog.Current;

    /// <inheritdoc />
    public event Action? MonitorsChanged;

    /// <inheritdoc />
    public void Start()
    {
        // Wired once, independent of the broadcast-window outcome below: IDesktopScaleProvider.Changed
        // also fires from callers that never go through this WndProc (WindowManager.RecoverDesktopLayer,
        // a window's own OnDpiChanged), and every one of those must still surface as MonitorsChanged.
        if (!_scaleProviderSubscribed)
        {
            desktopScaleProvider.Changed += OnScaleProviderChanged;
            _scaleProviderSubscribed = true;
        }

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
                Log.Warn(
                    "MonitorService",
                    "BroadcastWindowCreateFailed",
                    new KeyValuePair<string, object?>("win32Error", _messageWindow.LastError));
                _wndProc = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("MonitorService", "StartFailed", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_scaleProviderSubscribed)
        {
            desktopScaleProvider.Changed -= OnScaleProviderChanged;
            _scaleProviderSubscribed = false;
        }

        if (_messageWindow.HasResources)
        {
            nativeApi.DestroyMessageWindow(_messageWindow);
            _messageWindow = default;
        }

        _wndProc = null;
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            var native = nativeApi.GetMonitorInfos();
            var current = desktopScaleProvider.Current;

            // H1: a device EnumDisplayMonitors just reported but the scale snapshot doesn't know about
            // yet (e.g. a monitor attached between snapshots) makes GetWorkAreaDip return Rect.Empty
            // (X/Y = +Infinity). Give the snapshot one chance to catch up before falling back to
            // dropping the device — never hand a caller a Rect.Empty MonitorInfo (WindowManager.
            // ClampIntoMonitor would otherwise round +Infinity into a persisted int.MinValue Position).
            if (native.Any(m => current.GetWorkAreaDip(m.DeviceName).IsEmpty))
            {
                desktopScaleProvider.Refresh();
                current = desktopScaleProvider.Current;
            }

            var result = new List<MonitorInfo>(native.Count);
            foreach (var m in native)
            {
                var workArea = current.GetWorkAreaDip(m.DeviceName);
                if (workArea.IsEmpty)
                {
                    Log.Warn(
                        "MonitorService",
                        "MonitorMissingFromScaleSnapshot",
                        new KeyValuePair<string, object?>("deviceName", m.DeviceName));
                    continue;
                }

                result.Add(new MonitorInfo(m.DeviceName, current.GetBoundsDip(m.DeviceName), workArea, m.IsPrimary));
            }

            return result;
        }
        catch (Exception ex)
        {
            // Defensive: never let a native failure bubble into the caller (often the UI thread).
            Log.Error("MonitorService", "GetMonitorsFailed", ex);
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

    /// <inheritdoc />
    public Rect? GetWindowRectDip(IntPtr hwnd)
    {
        if (!nativeApi.GetWindowRect(hwnd, out var rect)) return null;

        var px = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return desktopScaleProvider.Current.PxToDip(px);
    }

    private IntPtr WndProcImpl(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // Minimal work in the WndProc; never throw out of it.
        try
        {
            if (msg is WmDisplayChange or WmSettingChange or WmDpiChanged)
            {
                // Rebuild the scale snapshot BEFORE anything observes MonitorsChanged: Refresh() raises
                // IDesktopScaleProvider.Changed synchronously when the snapshot actually differs, and
                // OnScaleProviderChanged forwards that as MonitorsChanged — exactly once per real change,
                // never invoked a second time from here (no double-fire).
                desktopScaleProvider.Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Error("MonitorService", "WndProcError", ex);
        }

        return nativeApi.DefaultWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnScaleProviderChanged(object? sender, DesktopScaleChangedEventArgs e)
    {
        try
        {
            MonitorsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber's failure must not escape into the WndProc (or whichever caller triggered
            // the provider's Refresh) that led here.
            Log.Error("MonitorService", "MonitorsChangedHandlerFailed", ex);
        }
    }

    private MonitorInfo FallbackMonitor()
    {
        // Last-resort single virtual screen; keeps callers from crashing if enumeration yields nothing.
        var scale = desktopScaleProvider.Current.ReferenceScale;
        var safeScale = scale > 0 ? scale : 1.0;
        var size = nativeApi.GetPrimaryScreenSizePx();
        var w = size.Width / safeScale;
        var h = size.Height / safeScale;
        var rect = new Rect(0, 0, w > 0 ? w : 1920, h > 0 ? h : 1080);
        return new MonitorInfo(@"\\.\DISPLAY1", rect, rect, true);
    }
}
