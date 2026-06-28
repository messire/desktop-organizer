using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="GridSnap"/> — the magnetic grid-snapping math used both by the live
/// drag-time magnet (per mouse-move, threshold-based) and by the drop-time quantiser. Only the pure
/// math is covered here: the mouse drag cycle itself (capture/move/release) is manual verify
/// (Win10/Win11) — see docs/PLANS/grid-snap-during-drag.md §8.
/// </summary>
[Trait(UnitTests, "UI")]
public class GridSnapTests
{
    // ----- SnapAxis: within / on / outside threshold -----

    /// <summary>
    /// 103 is 3px from the nearest line (100) with grid=20; within threshold=5 → snaps to 100.
    /// </summary>
    [Fact]
    public void SnapAxis_WithinThreshold_SnapsToNearestLine()
    {
        Assert.Equal(100d, GridSnap.SnapAxis(103, 20, 5));
    }

    /// <summary>
    /// 105 is exactly 5px from the nearest line (100); threshold is inclusive (<c>&lt;=</c>) → snaps.
    /// </summary>
    [Fact]
    public void SnapAxis_ExactlyAtThreshold_Snaps()
    {
        Assert.Equal(100d, GridSnap.SnapAxis(105, 20, 5));
    }

    /// <summary>
    /// 107 is 7px from the nearest line (100), past threshold=5 → returns raw unchanged.
    /// </summary>
    [Fact]
    public void SnapAxis_PastThreshold_ReturnsRawUnchanged()
    {
        Assert.Equal(107d, GridSnap.SnapAxis(107, 20, 5));
    }

    // ----- grid <= 1 disables snapping -----

    [Fact]
    public void SnapAxis_GridOne_ReturnsRawUnchanged()
    {
        Assert.Equal(103d, GridSnap.SnapAxis(103, 1, 12));
    }

    [Fact]
    public void SnapAxis_GridZero_ReturnsRawUnchanged()
    {
        Assert.Equal(103d, GridSnap.SnapAxis(103, 0, 12));
    }

    [Fact]
    public void SnapAxis_GridNegative_ReturnsRawUnchanged()
    {
        Assert.Equal(103d, GridSnap.SnapAxis(103, -4, 12));
    }

    // ----- threshold = 0 -----

    /// <summary>
    /// threshold=0 → snaps only on an exact hit on the grid line.
    /// </summary>
    [Fact]
    public void SnapAxis_ZeroThreshold_ExactHit_Snaps()
    {
        Assert.Equal(100d, GridSnap.SnapAxis(100, 20, 0));
    }

    /// <summary>
    /// threshold=0 → any offset from the line, however small, does not snap.
    /// </summary>
    [Fact]
    public void SnapAxis_ZeroThreshold_OffLine_DoesNotSnap()
    {
        Assert.Equal(101d, GridSnap.SnapAxis(101, 20, 0));
    }

    /// <summary>
    /// Negative threshold disables snapping entirely (defensive; mirrors grid &lt;= 1 behaviour).
    /// </summary>
    [Fact]
    public void SnapAxis_NegativeThreshold_ReturnsRawUnchanged()
    {
        Assert.Equal(100d, GridSnap.SnapAxis(100, 20, -1));
    }

    // ----- negative coordinates (multi-monitor left/above the primary) -----

    /// <summary>
    /// Negative raw coordinates snap correctly to the nearest (also negative) grid line.
    /// -103 is 3px from -100 (nearest multiple of 20) — within threshold=5 → snaps to -100.
    /// </summary>
    [Fact]
    public void SnapAxis_NegativeCoordinate_WithinThreshold_Snaps()
    {
        Assert.Equal(-100d, GridSnap.SnapAxis(-103, 20, 5));
    }

    /// <summary>
    /// -107 is 7px from -100, past threshold=5 → returns raw unchanged.
    /// </summary>
    [Fact]
    public void SnapAxis_NegativeCoordinate_PastThreshold_ReturnsRawUnchanged()
    {
        Assert.Equal(-107d, GridSnap.SnapAxis(-107, 20, 5));
    }

    // ----- midpoint rounding (documents Math.Round's banker's rounding, as in GridMetricsTests) -----

    /// <summary>
    /// 110 is exactly halfway between 100 and 120 (grid=20). Math.Round uses round-half-to-even,
    /// so 110/20 = 5.5 rounds to 6 → nearest = 120. Within threshold=10 → snaps to 120.
    /// </summary>
    [Fact]
    public void SnapAxis_Midpoint_RoundsToEven()
    {
        Assert.Equal(120d, GridSnap.SnapAxis(110, 20, 10));
    }

    /// <summary>
    /// 90 is exactly halfway between 80 and 100 (grid=20). 90/20 = 4.5 rounds to 4 (round-half-to-even)
    /// → nearest = 80. Within threshold=10 → snaps to 80.
    /// </summary>
    [Fact]
    public void SnapAxis_Midpoint_RoundsToEven_OtherSide()
    {
        Assert.Equal(80d, GridSnap.SnapAxis(90, 20, 10));
    }

    // ----- Snap: independent axes -----

    /// <summary>
    /// X is within threshold (snaps), Y is outside threshold (stays raw) — axes are independent.
    /// </summary>
    [Fact]
    public void Snap_IndependentAxes_OnlyXSnaps()
    {
        var (left, top) = GridSnap.Snap(103, 107, 20, 5);

        Assert.Equal(100d, left);
        Assert.Equal(107d, top);
    }

    /// <summary>
    /// Both axes within threshold — both snap.
    /// </summary>
    [Fact]
    public void Snap_BothWithinThreshold_BothSnap()
    {
        var (left, top) = GridSnap.Snap(103, 98, 20, 5);

        Assert.Equal(100d, left);
        Assert.Equal(100d, top);
    }

    /// <summary>
    /// Both axes outside threshold — both stay raw (free movement, matches current px-to-px behaviour).
    /// </summary>
    [Fact]
    public void Snap_BothPastThreshold_BothRemainRaw()
    {
        var (left, top) = GridSnap.Snap(150, 250, 20, 5);

        Assert.Equal(150d, left);
        Assert.Equal(250d, top);
    }

    /// <summary>
    /// grid &lt;= 1 disables snapping for the whole point, both axes returned raw.
    /// </summary>
    [Fact]
    public void Snap_GridOne_BothAxesRaw()
    {
        var (left, top) = GridSnap.Snap(103, 107, 1, 12);

        Assert.Equal(103d, left);
        Assert.Equal(107d, top);
    }
}
