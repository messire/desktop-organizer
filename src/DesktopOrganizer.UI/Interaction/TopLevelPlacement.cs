namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// Positions a TOP-LEVEL window — the system icons sidebar, the "Customize panel…" dialog, and (by the
/// same reasoning, though no runtime code calls this for them today) the settings window/dialogs — from
/// a rectangle given in physical screen pixels (release-hardening F §5 #21/#22/#23, ADR-037).
/// </summary>
/// <remarks>
/// Unlike a fence panel (<see cref="PanelWindowCoordinates"/>), a top-level window is never part of the
/// desktop layer's single desktop-DIP space — WPF already scales it by whichever monitor it currently
/// renders on, in BOTH desktop layer modes (plan §2 "Window.Left/Top top-level окна": <c>px / s_M</c>
/// always). This type exists only because the raw PHYSICAL-PIXEL monitor rectangles
/// (<see cref="MonitorScale.BoundsPx"/>/<see cref="MonitorScale.WorkAreaPx"/>) — the only per-monitor
/// source that is correct under PerMonitorV2 without waiting on <c>IMonitorService</c>'s own F2 rewiring
/// — need dividing by <see cref="DesktopScaleModel.GetTopLevelScale"/> before they can feed
/// <see cref="Window.Left"/>/<see cref="Window.Top"/>/<see cref="FrameworkElement.Width"/>/
/// <see cref="FrameworkElement.Height"/>.
/// </remarks>
internal static class TopLevelPlacement
{
    /// <summary>
    /// Converts a physical-pixel rectangle on the monitor named <paramref name="deviceName"/> into DIP,
    /// by that monitor's own effective scale (<see cref="DesktopScaleModel.GetTopLevelScale"/>).
    /// </summary>
    /// <param name="px">The rectangle, in physical screen pixels.</param>
    /// <param name="deviceName">
    /// The monitor's device name, or <see langword="null"/>/unknown to fall back to
    /// <see cref="DesktopScaleModel.ReferenceScale"/> (the pre-F, single-scale behavior).
    /// </param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    /// <returns>The rectangle, in DIP, ready for <c>Window.Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c>.</returns>
    public static Rect ToDip(Rect px, string? deviceName, DesktopScaleModel scale)
    {
        var s = deviceName is { Length: > 0 } ? scale.GetTopLevelScale(deviceName) : scale.ReferenceScale;
        return new Rect(px.X / s, px.Y / s, px.Width / s, px.Height / s);
    }
}
