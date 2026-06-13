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
    MonitorInfo? FindMonitorAt(Point point);

    /// <summary>
    /// Finds a monitor by its <see cref="MonitorInfo.DeviceName"/>, or <see langword="null"/>
    /// if no monitor with that name currently exists.
    /// </summary>
    MonitorInfo? FindByDeviceName(string deviceName);
}
