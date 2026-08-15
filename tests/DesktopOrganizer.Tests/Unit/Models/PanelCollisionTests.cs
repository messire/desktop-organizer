using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelCollision"/> — the AABB collision-response math behind the
/// "panels don't overlap while dragging" feature (see docs/PLANS/panel-anti-overlap.md §7). Only the
/// pure math is covered here: the mouse drag cycle itself (capture/move/release against real
/// <c>ChildWindow</c> instances) is manual verify (Win10/Win11) — see the plan's §8 checklist.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelCollisionTests
{
    // ----- Overlaps -----

    /// <summary>Test 1: genuinely overlapping rectangles → true.</summary>
    [Fact]
    public void Overlaps_OverlappingRectangles_ReturnsTrue()
    {
        var a = new Rect(0, 0, 100, 100);
        var b = new Rect(50, 50, 100, 100);

        Assert.True(PanelCollision.Overlaps(a, b));
    }

    /// <summary>Test 2: edge-touching rectangles (a.Right == b.X) must NOT count as overlapping —
    /// otherwise a panel resting flush against a neighbour would immediately block itself.</summary>
    [Fact]
    public void Overlaps_EdgeTouching_ReturnsFalse()
    {
        var a = new Rect(0, 0, 100, 100);
        var b = new Rect(100, 0, 100, 100);

        Assert.False(PanelCollision.Overlaps(a, b));
    }

    /// <summary>Test 3: overlap smaller than Eps (0.5 DIP) → treated as not overlapping.</summary>
    [Fact]
    public void Overlaps_SmallerThanEpsilon_ReturnsFalse()
    {
        var a = new Rect(0, 0, 100, 100);
        var b = new Rect(99.8, 0, 100, 100); // 0.2px overlap on X

        Assert.False(PanelCollision.Overlaps(a, b));
    }

    /// <summary>Test 4: rectangles align on one axis only (X ranges overlap, Y ranges do not) → false.</summary>
    [Fact]
    public void Overlaps_OnlyOneAxisAligned_ReturnsFalse()
    {
        var a = new Rect(0, 0, 100, 100);
        var b = new Rect(50, 200, 100, 100); // X overlaps, Y does not

        Assert.False(PanelCollision.Overlaps(a, b));
    }

    // ----- ClampAgainstObstacles: blocking -----

    /// <summary>Test 5: moving right into a neighbour stops flush against its left edge; Top passes through unchanged.</summary>
    [Fact]
    public void ClampAgainstObstacles_MovingRightIntoObstacle_StopsAtLeftEdge()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 120, 40, [obstacle]);

        Assert.Equal(50d, result.X);  // obstacle.X (150) - width (100)
        Assert.Equal(40d, result.Y);  // proposedTop applied unchanged
    }

    /// <summary>Test 6: moving left into a neighbour stops flush against its right edge.</summary>
    [Fact]
    public void ClampAgainstObstacles_MovingLeftIntoObstacle_StopsAtRightEdge()
    {
        var current = new Rect(200, 0, 100, 100);
        var obstacle = new Rect(0, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 30, 0, [obstacle]);

        Assert.Equal(100d, result.X); // obstacle.Right
    }

    /// <summary>Test 7a: moving down into a neighbour stops flush against its top edge.</summary>
    [Fact]
    public void ClampAgainstObstacles_MovingDownIntoObstacle_StopsAtTopEdge()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(0, 150, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 0, 120, [obstacle]);

        Assert.Equal(50d, result.Y); // obstacle.Y (150) - height (100)
    }

    /// <summary>Test 7b: moving up into a neighbour stops flush against its bottom edge (symmetric case).</summary>
    [Fact]
    public void ClampAgainstObstacles_MovingUpIntoObstacle_StopsAtBottomEdge()
    {
        var current = new Rect(0, 200, 100, 100);
        var obstacle = new Rect(0, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 0, 30, [obstacle]);

        Assert.Equal(100d, result.Y); // obstacle.Bottom
    }

    /// <summary>
    /// Test 8: independence of axes — X is blocked by an obstacle to the right, while Y (moving down)
    /// is completely free, since the obstacle only occupies the same row, not the same column below.
    /// This is the "sliding along the wall" behaviour required by the feature.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_BlockedOnXFreeOnY_SlidesAlongWall()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100); // directly to the right, same row

        var result = PanelCollision.ClampAgainstObstacles(current, 120, 300, [obstacle]);

        Assert.Equal(50d, result.X);  // clamped against the obstacle
        Assert.Equal(300d, result.Y); // Y unaffected — free vertical movement
    }

    /// <summary>Test 9: cursor "runs away" 500px past the obstacle in one frame — result rests exactly
    /// at the wall, not inside the obstacle and not thrown back past the start.</summary>
    [Fact]
    public void ClampAgainstObstacles_CursorRunsFarPastObstacle_RestsExactlyAtWall()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 650, 0, [obstacle]);

        Assert.Equal(50d, result.X);
    }

    /// <summary>Test 10: from a position resting at the wall, moving back away applies 1:1, with no
    /// residual offset/rubber-banding.</summary>
    [Fact]
    public void ClampAgainstObstacles_CursorReturnsFromWall_AppliesOneToOne()
    {
        var atWall = new Rect(50, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(atWall, 10, 0, [obstacle]);

        Assert.Equal(10d, result.X);
    }

    /// <summary>Test 11: anti-tunneling — a single-frame jump whose delta exceeds the obstacle's own
    /// width would skip clean over it without a swept test; the swept rectangle must still catch it.</summary>
    [Fact]
    public void ClampAgainstObstacles_FastJumpOverNarrowObstacle_StopsAtNearEdge()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 20, 100); // narrow: |dx|(500) far exceeds obstacle.Width(20) + current.Width(100)

        var result = PanelCollision.ClampAgainstObstacles(current, 500, 0, [obstacle]);

        Assert.Equal(50d, result.X); // obstacle.X (150) - width (100), not tunneled through to 500
    }

    /// <summary>Test 12: two obstacles on the path — the nearer one (in the direction of travel) wins.</summary>
    [Fact]
    public void ClampAgainstObstacles_TwoObstaclesOnPath_NearestWins()
    {
        var current = new Rect(0, 0, 100, 100);
        var nearObstacle = new Rect(150, 0, 100, 100);
        var farObstacle = new Rect(400, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 600, 0, [nearObstacle, farObstacle]);

        Assert.Equal(50d, result.X); // stops at nearObstacle, never reaches farObstacle
    }

    /// <summary>Test 13: an obstacle not in the movement's swept path is ignored entirely.</summary>
    [Fact]
    public void ClampAgainstObstacles_ObstacleOffPath_ProposalAppliedUnchanged()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(0, 500, 100, 100); // far below, current only moves horizontally

        var result = PanelCollision.ClampAgainstObstacles(current, 300, 0, [obstacle]);

        Assert.Equal(300d, result.X);
        Assert.Equal(0d, result.Y);
    }

    /// <summary>Test 14: empty obstacle list → proposal applied unchanged.</summary>
    [Fact]
    public void ClampAgainstObstacles_NoObstacles_ProposalAppliedUnchanged()
    {
        var current = new Rect(0, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 300, 400, []);

        Assert.Equal(300d, result.X);
        Assert.Equal(400d, result.Y);
    }

    /// <summary>Test 15: zero delta on an axis leaves that axis untouched, even when an obstacle
    /// already overlaps on that axis (nothing to resolve when there's no proposed movement there).</summary>
    [Fact]
    public void ClampAgainstObstacles_ZeroDeltaOnAxis_AxisUntouched()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(50, 0, 100, 100); // already overlaps in X

        var result = PanelCollision.ClampAgainstObstacles(current, 0, 200, [obstacle]);

        Assert.Equal(0d, result.X);   // dx == 0 → untouched
        Assert.Equal(200d, result.Y); // dy resolved normally (no obstacle on the Y sweep)
    }

    // ----- ClampAgainstObstacles: R1 amnesty rule -----

    /// <summary>Test 16: current already overlaps the obstacle; moving further OUTWARD (away from it)
    /// is never blocked by the already-overlapped obstacle.</summary>
    [Fact]
    public void ClampAgainstObstacles_AlreadyOverlapping_MovingOutward_NotBlocked()
    {
        var current = new Rect(60, 0, 100, 100);
        var obstacle = new Rect(0, 0, 100, 100); // overlaps current from 60 to 100

        var result = PanelCollision.ClampAgainstObstacles(current, 300, 0, [obstacle]);

        Assert.Equal(300d, result.X); // free to move away
    }

    /// <summary>Test 17: current already overlaps the obstacle; moving further INWARD (deeper into it)
    /// is also not blocked — the obstacle is a "ghost" for as long as it's already overlapped.</summary>
    [Fact]
    public void ClampAgainstObstacles_AlreadyOverlapping_MovingDeeperInward_NotBlocked()
    {
        var current = new Rect(60, 0, 100, 100);
        var obstacle = new Rect(0, 0, 100, 100); // overlaps current from 60 to 100

        var result = PanelCollision.ClampAgainstObstacles(current, 20, 0, [obstacle]);

        Assert.Equal(20d, result.X); // free to move deeper in too
    }

    /// <summary>Test 18: the panel is entirely nested inside the obstacle — free to exit in either direction.</summary>
    [Fact]
    public void ClampAgainstObstacles_FullyNestedInsideObstacle_FreeToExitEitherDirection()
    {
        var obstacle = new Rect(0, 0, 300, 300);
        var current = new Rect(100, 100, 50, 50); // entirely inside obstacle

        var exitRight = PanelCollision.ClampAgainstObstacles(current, 400, 100, [obstacle]);
        Assert.Equal(400d, exitRight.X);

        var exitLeft = PanelCollision.ClampAgainstObstacles(current, -400, 100, [obstacle]);
        Assert.Equal(-400d, exitLeft.X);
    }

    /// <summary>
    /// Test 19: two-step scenario — step 1 exits an already-overlapped obstacle to a flush position;
    /// step 2, from that flush position, attempting to re-enter IS blocked (the obstacle is only a
    /// ghost while still actually overlapped; once clear, it's solid again).
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_TwoStep_ExitThenReEntryIsBlocked()
    {
        var obstacle = new Rect(0, 0, 100, 100);
        var current = new Rect(60, 0, 100, 100); // overlapping obstacle by 40px

        // Step 1: exit fully to the right, resting flush.
        var afterExit = PanelCollision.ClampAgainstObstacles(current, 100, 0, [obstacle]);
        Assert.Equal(100d, afterExit.X); // obstacle.Right

        var restingAtWall = new Rect(afterExit.X, afterExit.Y, current.Width, current.Height);

        // Step 2: attempt to re-enter — now blocked, since current no longer overlaps the obstacle.
        var afterReEntry = PanelCollision.ClampAgainstObstacles(restingAtWall, 20, 0, [obstacle]);
        Assert.Equal(100d, afterReEntry.X); // held at the wall, cannot re-enter
    }

    /// <summary>
    /// Test 19b (finalization MAJOR M-1): a pre-existing overlap of only 1 DIP — above the collision
    /// tolerance <c>Eps</c> (0.5) but below the R1 "ghost" amnesty tolerance <c>GhostEps</c> (2.0) — must
    /// NOT be amnestied. Without separate epsilons, accumulated double-rounding noise around a flush
    /// edge could occasionally register as exactly this kind of sub-pixel overlap and silently disable
    /// the wall for the pair until they separated completely; the fix requires this borderline overlap
    /// to still block further movement like a genuine collision.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_OnePixelPreExistingOverlap_StillBlocksFurtherInwardMovement()
    {
        var current = new Rect(99, 0, 100, 100);  // overlaps obstacle by exactly 1px (99..100)
        var obstacle = new Rect(0, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 50, 0, [obstacle]);

        Assert.Equal(99d, result.X); // held in place — NOT treated as a ghost, unlike Test 17's 40px case
    }

    /// <summary>Test 20: degenerate current rectangle (Width == 0) → proposal applied unchanged (R4 guard).</summary>
    [Fact]
    public void ClampAgainstObstacles_DegenerateCurrentWidth_ProposalAppliedUnchanged()
    {
        var current = new Rect(0, 0, 0, 100);
        var obstacle = new Rect(50, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 300, 400, [obstacle]);

        Assert.Equal(300d, result.X);
        Assert.Equal(400d, result.Y);
    }

    /// <summary>
    /// Test 20b (finalization MINOR m-2): a NaN <paramref name="current"/> coordinate must hit the early
    /// exit and never propagate NaN into the returned rectangle (which would otherwise get assigned
    /// straight to <c>Window.Left</c>/<c>Top</c>).
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_NaNCurrentPosition_ProposalAppliedUnchanged()
    {
        var current = new Rect(double.NaN, 0, 100, 100);
        var obstacle = new Rect(50, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 300, 400, [obstacle]);

        Assert.Equal(300d, result.X);
        Assert.Equal(400d, result.Y);
    }

    // ----- CreatesNewOverlap -----

    /// <summary>Test 21: a snap that introduces a new overlap where there wasn't one before → true.</summary>
    [Fact]
    public void CreatesNewOverlap_SnapIntroducesNewOverlap_ReturnsTrue()
    {
        var before = new Rect(40, 0, 100, 100);
        var after = new Rect(60, 0, 100, 100); // snap moved 20px toward the obstacle, now overlapping
        var obstacle = new Rect(150, 0, 100, 100);

        Assert.True(PanelCollision.CreatesNewOverlap(before, after, [obstacle]));
    }

    /// <summary>Test 22: a snap that keeps the panel clear of every obstacle → false.</summary>
    [Fact]
    public void CreatesNewOverlap_SnapStaysClear_ReturnsFalse()
    {
        var before = new Rect(0, 0, 100, 100);
        var after = new Rect(20, 0, 100, 100); // still nowhere near the obstacle
        var obstacle = new Rect(500, 0, 100, 100);

        Assert.False(PanelCollision.CreatesNewOverlap(before, after, [obstacle]));
    }

    /// <summary>Test 23: overlap existed both before and after the snap (R1) → not a NEW overlap, false.</summary>
    [Fact]
    public void CreatesNewOverlap_OverlapExistedBeforeAndAfter_ReturnsFalse()
    {
        var before = new Rect(60, 0, 100, 100);
        var after = new Rect(65, 0, 100, 100); // still overlapping, just shifted slightly
        var obstacle = new Rect(0, 0, 100, 100);

        Assert.False(PanelCollision.CreatesNewOverlap(before, after, [obstacle]));
    }

    /// <summary>Test 24: empty obstacle list → never a new overlap, false.</summary>
    [Fact]
    public void CreatesNewOverlap_NoObstacles_ReturnsFalse()
    {
        var before = new Rect(0, 0, 100, 100);
        var after = new Rect(1000, 1000, 100, 100);

        Assert.False(PanelCollision.CreatesNewOverlap(before, after, []));
    }
}
