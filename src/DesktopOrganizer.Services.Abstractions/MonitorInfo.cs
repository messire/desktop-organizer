using System.Windows;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Describes a physical display device, in WPF device-independent (DIP) coordinates.
/// </summary>
/// <param name="DeviceName">Stable device name (e.g. <c>\\.\DISPLAY1</c>) used as a persistent monitor id.</param>
/// <param name="Bounds">Full monitor rectangle in virtual-desktop DIP coordinates.</param>
/// <param name="WorkArea">Work area (excludes taskbar/appbars) in virtual-desktop DIP coordinates.</param>
/// <param name="IsPrimary">Whether this is the primary monitor.</param>
public record MonitorInfo(string DeviceName, Rect Bounds, Rect WorkArea, bool IsPrimary);
