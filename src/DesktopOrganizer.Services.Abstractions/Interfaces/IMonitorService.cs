using System.Windows;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Enumerates the system's display monitors and notifies on configuration changes.
/// All rectangles are expressed in WPF device-independent (DIP) coordinates so panel
/// positioning can use <see cref="Window.Left"/>/<see cref="Window.Top"/> directly.
/// </summary>
public interface IMonitorService : IDisposable
{
    /// <summary>
    /// Raised when the monitor configuration changes (resolution, attach/detach, primary change).
    /// Raised on the thread that owns the message loop (the UI thread when started from it).
    /// </summary>
    event Action? MonitorsChanged;

    /// <summary>
    /// Starts listening for display-configuration changes via a hidden message-only window.
    /// Must be called from a thread with a running message loop (the WPF UI thread).
    /// </summary>
    void Start();

    /// <summary>
    /// Returns the current list of monitors. The result is a fresh snapshot on each call.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Returns the primary monitor, falling back to the first enumerated monitor.
    /// </summary>
    MonitorInfo GetPrimaryMonitor();

    /// <summary>
    /// Finds the monitor that contains <paramref name="point"/> (DIP coordinates), or
    /// <see langword="null"/> if the point lies outside every monitor.
    /// </summary>
    /// <param name="point">The point to test, in DIP coordinates.</param>
    MonitorInfo? FindMonitorAt(Point point);

    /// <summary>
    /// Finds a monitor by its <see cref="MonitorInfo.DeviceName"/>, or <see langword="null"/>
    /// if no monitor with that name currently exists.
    /// </summary>
    /// <param name="deviceName">The monitor's device name, as reported by <see cref="MonitorInfo.DeviceName"/>.</param>
    MonitorInfo? FindByDeviceName(string deviceName);

    /// <summary>
    /// The screen-DIP bounding rectangle of the window identified by <paramref name="hwnd"/>
    /// (<c>GetWindowRect</c>, scaled the same way <see cref="GetMonitors"/> scales its own rectangles —
    /// release-hardening F, ADR-037 — so the result is directly comparable to
    /// <see cref="MonitorInfo.Bounds"/>/<see cref="MonitorInfo.WorkArea"/>). Used ONLY by
    /// <c>Models.DesktopCoordinateSpace.CalibrateOnce</c> (ADR-028 «Открытые вопросы», Вопрос 2) — not
    /// called anywhere in Stage S/P03/P04 runtime code (see that type's remarks); introduced now so a
    /// later, separate commit can wire calibration without adding a new interface member.
    /// </summary>
    /// <param name="hwnd">The window handle to measure.</param>
    /// <returns>The window's screen-DIP rectangle, or <see langword="null"/> if <paramref name="hwnd"/> is invalid or the native call failed.</returns>
    Rect? GetWindowRectDip(IntPtr hwnd);
}
