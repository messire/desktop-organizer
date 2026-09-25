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
    /// System-DPI scale (<c>1.0</c> at 96 DPI) — the SAME coefficient <see cref="GetMonitors"/> already
    /// uses internally to convert its physical-pixel source into the DIP rectangles it returns. Exposed
    /// here so the UI layer (which cannot reference <c>INativeApi</c> — see
    /// <c>App.xaml.cs</c>'s "<c>INativeApi</c> is internal to <c>DesktopOrganizer.Services</c>" note) can
    /// perform the ONE conversion it legitimately needs outside this service: turning a desktop lasso
    /// gesture's physical-pixel rectangle (<c>DesktopLassoEventArgs</c>, always "screen pixels" by
    /// contract) into DIP, without adding a fourth, independent DPI source next to the three the ADR-028
    /// baseline audit already flags as risky (docs/PLANS/workspace-grid-00-baseline.md, "Источники DPI").
    /// </summary>
    double GetSystemDpiScale();

    /// <summary>
    /// The screen-DIP bounding rectangle of the window identified by <paramref name="hwnd"/>
    /// (<c>GetWindowRect</c>, scaled by <see cref="GetSystemDpiScale"/> — the same conversion
    /// <see cref="GetMonitors"/> applies to its own rectangles, so the result is directly comparable to
    /// <see cref="MonitorInfo.Bounds"/>/<see cref="MonitorInfo.WorkArea"/>). Used ONLY by
    /// <c>Models.DesktopCoordinateSpace.CalibrateOnce</c> (ADR-028 «Открытые вопросы», Вопрос 2) — not
    /// called anywhere in Stage S/P03/P04 runtime code (see that type's remarks); introduced now so a
    /// later, separate commit can wire calibration without adding a new interface member.
    /// </summary>
    /// <param name="hwnd">The window handle to measure.</param>
    /// <returns>The window's screen-DIP rectangle, or <see langword="null"/> if <paramref name="hwnd"/> is invalid or the native call failed.</returns>
    Rect? GetWindowRectDip(IntPtr hwnd);
}
