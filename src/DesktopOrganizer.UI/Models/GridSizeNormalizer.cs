using System.Windows;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static helper that re-aligns an already-persisted <see cref="PanelGeometryMode.LegacyFreeform"/>
/// panel's <c>Position</c>/<c>Size</c> onto the CURRENT <c>IAppSettings.GridSize</c> lattice (ADR-011
/// Amendment 2026-09-24). No WPF/P/Invoke dependency beyond the plain <see cref="Rect"/>/<see cref="Size"/>
/// structs — fully unit-testable, same pattern as <see cref="GridSnap"/>/<see cref="WorkspaceLayoutPolicy"/>.
/// </summary>
/// <remarks>
/// <b>The bug this closes.</b> A panel dragged/dropped while <c>GridSize</c> was, say, <c>32</c> lands on
/// multiples of 32 from the global origin <c>(0,0)</c> (<see cref="GridSnap"/>). If the user later changes
/// <c>GridSize</c> to <c>48</c>, that panel's persisted <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c>
/// are no longer multiples of 48 — dragging it afterwards (which snaps in 48-DIP steps from the same
/// origin, see <see cref="GridSnap.SnapAxis"/>) keeps the panel permanently off the new lattice, visibly
/// "living in a different grid" than every panel created after the change. This type is the one-shot
/// correction applied at restore/settings-change time (<c>WindowManager</c>) so a panel's bounds are
/// always on the CURRENT grid rather than whichever grid happened to be active when it was last moved.
/// </remarks>
/// <remarks>
/// <b>Same origin as <see cref="GridSnap"/>, deliberately.</b> Both quantise from the global <c>(0,0)</c>
/// virtual-desktop origin — never a per-monitor origin — so a panel this type just normalized and a
/// subsequent live drag (<see cref="GridSnap.SnapAxis"/>) agree on exactly the same set of valid
/// coordinates: dragging a normalized panel by whole <c>GridSize</c> steps keeps it on the same lattice.
/// </remarks>
public static class GridSizeNormalizer
{
    /// <summary>
    /// Re-aligns <paramref name="bounds"/> onto the <paramref name="gridSize"/> lattice: position snaps to
    /// the nearest multiple of <paramref name="gridSize"/> from the origin <c>(0,0)</c>
    /// (<see cref="NormalizeCoordinate"/>), and width/height snap to the nearest multiple no smaller than
    /// <paramref name="minSize"/> (<see cref="NormalizeExtent"/>). When <paramref name="workArea"/> is
    /// supplied, the result is additionally kept inside it: size is capped to the work area's extent (never
    /// larger, floored to a grid multiple) and position is pulled back in (toward the work area's own
    /// origin, in whole <paramref name="gridSize"/> steps) so the normalized panel never pokes off-screen
    /// purely as a side effect of rounding up to the next grid line.
    /// </summary>
    /// <param name="bounds">The panel's current (possibly off-grid) bounds, in DIP.</param>
    /// <param name="gridSize">
    /// The grid cell size in DIP — same semantics as <c>IAppSettings.GridSize</c>. Values <c>&lt;= 1</c>
    /// disable normalization entirely (returns <paramref name="bounds"/> unchanged), matching
    /// <see cref="GridSnap.SnapAxis"/>'s "GridSize=1 means no grid" convention.
    /// </param>
    /// <param name="minSize">
    /// The panel's minimum width/height (e.g. <c>GridMetrics.MinPanelWidth</c>/<c>MinPanelSize</c>). The
    /// normalized size is never smaller than this, even if that means landing on a grid multiple ABOVE the
    /// nearest one to <paramref name="bounds"/>'s raw size.
    /// </param>
    /// <param name="workArea">
    /// The panel's monitor's work area, in the same DIP space as <paramref name="bounds"/>, or
    /// <see langword="null"/> to skip the work-area clamp entirely.
    /// </param>
    /// <returns>The normalized bounds. Idempotent: <c>Normalize(Normalize(b, g, m, w), g, m, w)</c> equals
    /// <c>Normalize(b, g, m, w)</c> for the same <paramref name="gridSize"/>/<paramref name="minSize"/>/
    /// <paramref name="workArea"/>.</returns>
    public static Rect Normalize(Rect bounds, double gridSize, Size minSize, Rect? workArea = null)
    {
        if (gridSize <= 1) return bounds;

        var width = NormalizeExtent(bounds.Width, gridSize, minSize.Width);
        var height = NormalizeExtent(bounds.Height, gridSize, minSize.Height);

        if (workArea is { } sizeArea && IsUsableWorkArea(sizeArea))
        {
            width = ClampExtentToWorkArea(width, gridSize, sizeArea.Width);
            height = ClampExtentToWorkArea(height, gridSize, sizeArea.Height);
        }

        var x = NormalizeCoordinate(bounds.X, gridSize);
        var y = NormalizeCoordinate(bounds.Y, gridSize);

        if (workArea is { } positionArea && IsUsableWorkArea(positionArea))
        {
            x = ClampPositionToWorkArea(x, width, gridSize, positionArea.Left, positionArea.Width);
            y = ClampPositionToWorkArea(y, height, gridSize, positionArea.Top, positionArea.Height);
        }

        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Snaps one coordinate to the nearest multiple of <paramref name="gridSize"/> from the origin
    /// <c>0</c> — unconditional (no magnet threshold), unlike <see cref="GridSnap.SnapAxis"/>. Round-half-up
    /// (<c>Floor(v/g + 0.5) * g</c>), NOT <see cref="Math.Round(double)"/>'s banker's rounding: an exact
    /// half-cell value (e.g. <c>72</c> at <c>gridSize=48</c>, which sits precisely between <c>48</c> and
    /// <c>96</c>) always resolves to the HIGHER multiple, a fixed, documented tie-break rather than one that
    /// silently alternates with the neighbouring integer parity of the quotient.
    /// </summary>
    /// <param name="raw">The raw (unsnapped) coordinate, in DIP. May be negative.</param>
    /// <param name="gridSize">
    /// The grid cell size in DIP. Values <c>&lt;= 1</c> disable normalization (returns <paramref name="raw"/>
    /// unchanged).
    /// </param>
    /// <returns>The nearest multiple of <paramref name="gridSize"/> to <paramref name="raw"/>.</returns>
    public static double NormalizeCoordinate(double raw, double gridSize)
    {
        if (gridSize <= 1 || !double.IsFinite(raw)) return raw;

        return Math.Floor((raw / gridSize) + 0.5) * gridSize;
    }

    /// <summary>
    /// Snaps a width/height to the nearest multiple of <paramref name="gridSize"/> that is not smaller than
    /// <paramref name="minExtent"/>. If <see cref="NormalizeCoordinate"/>'s nearest multiple already meets
    /// the floor, it is used as-is; otherwise the result is the SMALLEST multiple of
    /// <paramref name="gridSize"/> that is <c>&gt;= minExtent</c> (never the next multiple after the
    /// rejected candidate — a value far below the floor jumps straight to the floor's own ceiling multiple,
    /// not through every intermediate multiple).
    /// </summary>
    /// <param name="raw">The raw (unsnapped) width/height, in DIP. Negative/non-finite values clamp to <c>0</c> first.</param>
    /// <param name="gridSize">The grid cell size in DIP. Values <c>&lt;= 1</c> disable normalization.</param>
    /// <param name="minExtent">The minimum width/height the result must not go below.</param>
    /// <returns>The nearest multiple of <paramref name="gridSize"/> to <paramref name="raw"/>, floored at the smallest multiple <c>&gt;= minExtent</c>.</returns>
    public static double NormalizeExtent(double raw, double gridSize, double minExtent)
    {
        if (gridSize <= 1) return raw;

        var safeRaw = double.IsFinite(raw) ? Math.Max(0d, raw) : 0d;
        var safeMin = double.IsFinite(minExtent) ? Math.Max(0d, minExtent) : 0d;

        var nearest = Math.Floor((safeRaw / gridSize) + 0.5) * gridSize;
        if (nearest >= safeMin) return nearest;

        return Math.Ceiling(safeMin / gridSize) * gridSize;
    }

    /// <summary>
    /// Whether <paramref name="area"/> is a well-formed, non-degenerate work area — the same "skip the
    /// clamp rather than divide by a bogus extent" guard <see cref="WorkspaceLayoutPolicy.SafeClampFreeform"/>
    /// applies to its own <c>workAreaDip</c> parameter.
    /// </summary>
    private static bool IsUsableWorkArea(Rect area) =>
        !area.IsEmpty
        && double.IsFinite(area.X) && double.IsFinite(area.Y)
        && double.IsFinite(area.Width) && double.IsFinite(area.Height)
        && area.Width > 0d && area.Height > 0d;

    /// <summary>
    /// Caps a normalized extent to <paramref name="areaExtent"/>, rounded DOWN to a grid multiple so the
    /// result stays snapped. Never shrinks below a single grid cell — a work area smaller than one
    /// <paramref name="gridSize"/> cell leaves <paramref name="extent"/> untouched rather than collapsing
    /// it to zero (same "never produce a degenerate size" precedent as
    /// <see cref="WorkspaceLayoutPolicy.SafeClampFreeform"/>'s "larger than the work area → pinned, not
    /// shrunk" rule, though here the shrink itself is the documented trade-off — see the type remarks on
    /// prioritizing the work area over <c>minSize</c> in this rare corner case).
    /// </summary>
    private static double ClampExtentToWorkArea(double extent, double gridSize, double areaExtent)
    {
        if (extent <= areaExtent) return extent;

        var reduced = Math.Floor(areaExtent / gridSize) * gridSize;
        return reduced > 0d ? reduced : extent;
    }

    /// <summary>
    /// Pulls a normalized coordinate back inside <c>[areaStart, areaStart + areaExtent]</c> given the
    /// panel's (already work-area-capped) <paramref name="extent"/> on this axis, moving only in whole
    /// <paramref name="gridSize"/> steps so the result stays on the grid.
    /// </summary>
    private static double ClampPositionToWorkArea(double position, double extent, double gridSize, double areaStart, double areaExtent)
    {
        var maxPosition = areaStart + areaExtent - extent;

        if (position > maxPosition)
            position = Math.Floor(maxPosition / gridSize) * gridSize;

        if (position < areaStart)
            position = Math.Ceiling(areaStart / gridSize) * gridSize;

        return position;
    }
}
