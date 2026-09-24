using System.Windows;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Interaction.Native;

/// <summary>
/// The single px&lt;-&gt;DIP conversion point for the native panel-sizing path (D-3,
/// docs/PLANS/phase-2-native-panel-sizing.md §1.4). Pure, zero mutable state — physical pixels travel
/// across the native (<see cref="INativePanelSizingClient"/>) boundary, DIP is used only on this side by
/// <c>ResizeGeometryCalculator</c>. Kept deliberately separate from <c>MonitorService.ToDipRect</c>
/// (system DPI scale) to avoid a second DPI policy silently diverging from it (D-3).
/// </summary>
public static class NativeSizingCoordinateMapper
{
    /// <summary>
    /// Converts a physical-pixel rectangle to DIP. Exact (no rounding) — <see cref="ToPx(Rect, double)"/>
    /// is the only side that rounds, since it must produce integer pixels.
    /// </summary>
    /// <param name="rect">The rectangle, in physical screen pixels.</param>
    /// <param name="scale">The DPI scale (1.0 = 96 DPI) to divide by.</param>
    public static Rect ToDip(NativeRectPx rect, double scale) =>
        new(rect.Left / scale, rect.Top / scale, rect.Width / scale, rect.Height / scale);

    /// <summary>
    /// Converts a DIP rectangle to physical pixels, rounding each edge independently to the nearest
    /// integer pixel via <see cref="Math.Round(double)"/> (default <see cref="MidpointRounding.ToEven"/>).
    /// Edges are rounded independently (not width/height derived from rounded edges) so the result stays
    /// consistent with <see cref="NativeRectPx.Width"/>/<see cref="NativeRectPx.Height"/> being computed
    /// as <c>Right - Left</c>/<c>Bottom - Top</c>.
    /// </summary>
    /// <param name="rect">The rectangle, in DIP.</param>
    /// <param name="scale">The DPI scale (1.0 = 96 DPI) to multiply by.</param>
    public static NativeRectPx ToPx(Rect rect, double scale)
    {
        var left = (int)Math.Round(rect.Left * scale);
        var top = (int)Math.Round(rect.Top * scale);
        var right = (int)Math.Round((rect.Left + rect.Width) * scale);
        var bottom = (int)Math.Round((rect.Top + rect.Height) * scale);

        return new NativeRectPx(left, top, right, bottom);
    }
}
