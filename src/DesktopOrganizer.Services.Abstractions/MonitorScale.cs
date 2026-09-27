namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// One display monitor as seen by <see cref="DesktopScaleModel"/>: its physical-pixel rectangles plus its
/// effective DPI scale.
/// </summary>
/// <param name="DeviceName">Stable device name (e.g. <c>\.\DISPLAY1</c>), the same id <see cref="MonitorInfo.DeviceName"/> carries.</param>
/// <param name="BoundsPx">Full monitor rectangle in physical screen pixels.</param>
/// <param name="WorkAreaPx">Work area (excludes taskbar/appbars) in physical screen pixels.</param>
/// <param name="IsPrimary">Whether this is the primary monitor.</param>
/// <param name="Scale">
/// Effective DPI of the monitor divided by <see cref="DesktopScaleModel.BaseDpi"/> (<c>1.0</c> at 100 %)
/// — <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>, or the system scale when that is unavailable.
/// </param>
public sealed record MonitorScale(
    string DeviceName,
    System.Drawing.Rectangle BoundsPx,
    System.Drawing.Rectangle WorkAreaPx,
    bool IsPrimary,
    double Scale);
