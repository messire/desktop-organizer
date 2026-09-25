namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static helpers for magnetic grid snapping with a threshold. No WPF or P/Invoke
/// dependencies — fully unit-testable. Used both for live drag snapping (per mouse-move,
/// threshold-based "magnet" feel) and for drop-time quantisation.
/// </summary>
public static class GridSnap
{
    /// <summary>
    /// Magnetically snaps a single coordinate to the nearest grid line if it is within
    /// <paramref name="threshold"/> DIP of it; otherwise returns <paramref name="raw"/> unchanged.
    /// </summary>
    /// <param name="raw">The raw (unsnapped) coordinate, in DIP.</param>
    /// <param name="grid">The grid cell size, in DIP. Values &lt;= 1 disable snapping entirely
    /// (every DIP is a valid position), matching the existing <c>GridSize</c> semantics.</param>
    /// <param name="threshold">The magnetic pull distance, in DIP. Negative values disable snapping.</param>
    /// <returns>The snapped coordinate, or <paramref name="raw"/> if outside the threshold or snapping is disabled.</returns>
    public static double SnapAxis(double raw, int grid, double threshold)
    {
        if (grid <= 1 || threshold < 0) return raw;

        var nearest = Math.Round(raw / grid) * grid;
        var distance = Math.Abs(raw - nearest);
        return distance <= threshold ? nearest : raw;
    }

    /// <summary>
    /// Snaps a point's Left/Top coordinates independently along each axis.
    /// </summary>
    /// <param name="left">The raw (unsnapped) left coordinate, in DIP.</param>
    /// <param name="top">The raw (unsnapped) top coordinate, in DIP.</param>
    /// <param name="grid">The grid cell size, in DIP, shared by both axes.</param>
    /// <param name="threshold">The magnetic pull distance, in DIP, shared by both axes.</param>
    public static (double Left, double Top) Snap(double left, double top, int grid, double threshold)
        => (SnapAxis(left, grid, threshold), SnapAxis(top, grid, threshold));
}
