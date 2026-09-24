namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Turns a desktop lasso gesture's rectangle into the geometry a new panel is created with (F-7,
/// ADR-028 «Lasso-создание панели: px vs DIP»; P03 §2.6). Pure — <c>App.CreateLassoPanel</c> supplies the
/// DPI scale and the monitor snapshot, this type does every conversion and quantisation.
/// </summary>
/// <remarks>
/// <b>The bug this closes (baseline M5).</b> <c>DesktopLassoEventArgs</c> is documented — and produced —
/// in PHYSICAL SCREEN PIXELS (the low-level mouse hook reports pixels), while
/// <see cref="ChildWindowModel.Position"/>/<see cref="ChildWindowModel.Size"/> and
/// <c>Window.Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> are DIP. Writing one straight into the other
/// made a lassoed panel come out 25%/50%/75% too large — and displaced — on any non-100% display, which
/// is the common case on laptops. The scale used here is
/// <c>IMonitorService.GetSystemDpiScale()</c>: deliberately the SAME coefficient
/// <c>MonitorService.GetMonitors()</c> already divides by, so panel geometry and monitor geometry cannot
/// drift apart (the ADR-028 baseline audit lists "three independent DPI sources" as an existing risk —
/// this adds none).
/// </remarks>
public static class LassoPanelGeometry
{
    /// <summary>
    /// Converts a lasso rectangle from physical screen pixels to DIP and applies the minimum panel size.
    /// </summary>
    /// <param name="x">Lasso left, in physical pixels.</param>
    /// <param name="y">Lasso top, in physical pixels.</param>
    /// <param name="width">Lasso width, in physical pixels.</param>
    /// <param name="height">Lasso height, in physical pixels.</param>
    /// <param name="dpiScale">System DPI scale (<c>1.0</c> at 96 DPI). Non-finite/non-positive values are treated as <c>1.0</c>.</param>
    /// <param name="minSize">The minimum panel width/height, in DIP — applied AFTER the conversion, so it means the same thing at every DPI.</param>
    /// <returns>The lasso rectangle in DIP, never smaller than <paramref name="minSize"/> on either axis.</returns>
    public static Rect ToDip(int x, int y, int width, int height, double dpiScale, double minSize)
    {
        var scale = double.IsFinite(dpiScale) && dpiScale > 0d ? dpiScale : 1d;
        var safeMin = double.IsFinite(minSize) && minSize > 0d ? minSize : 0d;

        return new Rect(
            x / scale,
            y / scale,
            Math.Max(safeMin, width / scale),
            Math.Max(safeMin, height / scale));
    }

    /// <summary>
    /// Writes <paramref name="dipBounds"/> onto <paramref name="model"/>, quantising the POSITION to the
    /// workspace grid of the monitor the lasso was drawn on when (and only when) the new panel is
    /// <see cref="PanelGeometryMode.GridAligned"/> — the half of TODO(P03) that
    /// <c>PanelDefaults.ReconcileGridAlignedSpans</c> deliberately left open, because it needs a resolved
    /// monitor origin and that type has no access to one.
    /// </summary>
    /// <remarks>
    /// Rounding is <see cref="Math.Round(double)"/>, never a <c>(int)</c> cast: truncation is asymmetric
    /// around zero, so on a monitor placed left of/above the primary (negative coordinates) a cast would
    /// bias every lassoed panel toward the origin by up to a pixel while the same lasso on the primary
    /// monitor biased away from it (baseline M1/M5).
    /// </remarks>
    /// <param name="model">The new panel's model (its <see cref="ChildWindowModel.GeometryMode"/> is already assigned).</param>
    /// <param name="dipBounds">The lasso rectangle in DIP, from <see cref="ToDip"/>.</param>
    /// <param name="monitors">Monitor snapshot, in screen DIP.</param>
    public static void ApplyTo(
        ChildWindowModel model,
        Rect dipBounds,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(monitors);

        var position = dipBounds.TopLeft;

        // Инвариант В1: the mode question goes through the shared resolver, which also picks the monitor
        // the lasso's CENTER landed on and hands back that monitor's work-area origin.
        var state = PanelGridStateResolver.Resolve(model, dipBounds, monitors, isRolledUp: false);
        if (state.IsGridAligned)
        {
            position = WorkspaceLayoutPolicy.SnapPosition(position, state.Origin, state.UnitSize);
            model.MonitorDeviceId = state.MonitorDeviceId;
        }

        model.Position = new System.Drawing.Point((int)Math.Round(position.X), (int)Math.Round(position.Y));
        model.Size = new System.Drawing.Size(
            (int)Math.Round(dipBounds.Width),
            (int)Math.Round(dipBounds.Height));
    }
}
