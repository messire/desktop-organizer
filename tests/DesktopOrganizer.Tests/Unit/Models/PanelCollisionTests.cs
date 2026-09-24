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

    // ----- ClampAgainstObstacles(gap): fix-cycle 1, MAJOR-5(b) — additive gap-aware overload -----

    /// <summary>Test 25: gap=0 reproduces the zero-gap overload's exact flush-touching result.</summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_ZeroGap_MatchesZeroGapOverload()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100);

        var withoutGap = PanelCollision.ClampAgainstObstacles(current, 60d, 0d, [obstacle]);
        var withZeroGap = PanelCollision.ClampAgainstObstacles(current, 60d, 0d, [obstacle], 0d);

        Assert.Equal(withoutGap, withZeroGap);
    }

    /// <summary>Test 26: moving right into a neighbour with gap=8 stops 8 DIP short of its left edge, not flush.</summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_MovingIntoNeighbour_StopsGapDipShort()
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 60d, 0d, [obstacle], 8d);

        Assert.Equal(150d - 100d - 8d, result.X); // obstacle.X - width - gap
    }

    /// <summary>
    /// Test 27: a panel already resting exactly <c>gap</c> DIP clear of a neighbour (e.g. two adjacent
    /// <see cref="PanelGeometryMode.GridAligned"/> slots) is blocked from moving ANY further towards it —
    /// it is already sitting exactly on the gap-aware "wall", so even a small further approach is refused,
    /// the same "hard wall, zero tolerance past the boundary" contract as the zero-gap overload has for
    /// flush-touching obstacles.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_AlreadyExactlyGapApart_BlocksAnyFurtherApproach()
    {
        var current = new Rect(0, 0, 100, 100); // Right = 100
        var obstacle = new Rect(108, 0, 100, 100); // exactly 8 DIP clear of current.Right

        var result = PanelCollision.ClampAgainstObstacles(current, 5d, 0d, [obstacle], 8d);

        Assert.Equal(0d, result.X); // cannot move any closer -- already exactly gap DIP away
    }

    /// <summary>Test 28: moving away from a neighbour on the gap-aware overload is unaffected (same as zero-gap overload) — the gap only constrains approach, not departure.</summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_MovingAway_Unrestricted()
    {
        var current = new Rect(100d, 0, 100, 100);
        var obstacle = new Rect(0, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 500d, 0d, [obstacle], 8d);

        Assert.Equal(500d, result.X);
    }

    /// <summary>Test 29: non-finite/negative gap degrades to the zero-gap overload's behavior, no exception.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-8d)]
    public void ClampAgainstObstacles_WithGap_NonPositiveOrNonFiniteGap_MatchesZeroGapOverload(double gap)
    {
        var current = new Rect(0, 0, 100, 100);
        var obstacle = new Rect(150, 0, 100, 100);

        var withoutGap = PanelCollision.ClampAgainstObstacles(current, 60d, 0d, [obstacle]);
        var withBadGap = PanelCollision.ClampAgainstObstacles(current, 60d, 0d, [obstacle], gap);

        Assert.Equal(withoutGap, withBadGap);
    }

    // ----- ClampAgainstObstacles(gap): fix-cycle 2, MAJOR-A — R1 amnesty must key off the REAL obstacle -----

    /// <summary>
    /// Test 30 (MAJOR-A repro, exact numbers from the review): a panel already sitting 5 DIP from a real
    /// obstacle — closer than the required 8 DIP gap, but NOT physically overlapping it — must not tunnel
    /// through on approach. Fix-cycle 1's inflate-then-delegate implementation treated this as an R1 "ghost"
    /// (because the INFLATED obstacle already overlapped <c>current</c> by 3 DIP, past <c>GhostEps</c>) and
    /// let the panel jump straight to X=200 — 50 DIP inside the real obstacle. The fix holds the panel at
    /// its pre-existing (already too-close) position: not pulled back to a full 8 DIP of clearance, but not
    /// allowed to approach any further either (ADR-017 R1's "pre-existing state is not auto-corrected",
    /// extended to the gap concept).
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_AlreadyCloserThanGap_ApproachingFurther_HeldAtCurrentPosition()
    {
        var current = new Rect(45d, 0, 100, 100); // Right = 145 -- only 5 DIP clear of the obstacle (gap=8)
        var obstacle = new Rect(150d, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 200d, 0d, [obstacle], 8d);

        Assert.Equal(45d, result.X); // held exactly where it was -- neither pulled back nor allowed closer
        Assert.True(new Rect(result.X, result.Y, 100, 100).Right <= obstacle.X, "Panel must not end up inside the real obstacle.");
    }

    /// <summary>
    /// Test 31: from that SAME already-too-close position, retreating (increasing the real distance) must
    /// remain completely free — the gap concept only ever restricts approach, never departure, even from a
    /// position that is itself already in violation of the gap.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_AlreadyCloserThanGap_MovingAway_Unrestricted()
    {
        var current = new Rect(45d, 0, 100, 100);
        var obstacle = new Rect(150d, 0, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, -400d, 0d, [obstacle], 8d);

        Assert.Equal(-400d, result.X);
    }

    /// <summary>
    /// Test 32: vertical mirror of Test 30 — same "already closer than gap, held in place on further
    /// approach" rule on the Y axis.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_AlreadyCloserThanGap_ApproachingFurther_Vertical_HeldAtCurrentPosition()
    {
        var current = new Rect(0, 45d, 100, 100); // Bottom = 145 -- only 5 DIP clear of the obstacle (gap=8)
        var obstacle = new Rect(0, 150d, 100, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 0d, 200d, [obstacle], 8d);

        Assert.Equal(45d, result.Y);
    }

    /// <summary>
    /// Test 33: a real, genuine pre-existing overlap (well past <c>GhostEps</c>) must still be amnestied
    /// exactly as before, gap or no gap — MAJOR-A's fix must not turn true R1 overlaps into new blocks.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_RealPreExistingOverlap_StillAmnestied()
    {
        var current = new Rect(60d, 0, 100, 100); // overlaps the obstacle by 40 DIP -- a genuine intersection
        var obstacle = new Rect(0, 0, 100, 100);

        var deeper = PanelCollision.ClampAgainstObstacles(current, 20d, 0d, [obstacle], 8d);
        Assert.Equal(20d, deeper.X); // free to move deeper in, same as the zero-gap overload's R1 rule

        var outward = PanelCollision.ClampAgainstObstacles(current, 300d, 0d, [obstacle], 8d);
        Assert.Equal(300d, outward.X); // free to exit
    }

    /// <summary>
    /// Test 34: symmetry check — the same "already closer than gap, held in place" rule applies moving
    /// LEFT into an obstacle to the panel's left (mirrors Test 30's rightward approach).
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_AlreadyCloserThanGap_ApproachingFromTheRight_HeldAtCurrentPosition()
    {
        var current = new Rect(145d, 0, 100, 100); // Left = 145 -- only 5 DIP clear of the obstacle's Right (140)
        var obstacle = new Rect(0d, 0, 140, 100);

        var result = PanelCollision.ClampAgainstObstacles(current, 0d, 0d, [obstacle], 8d);

        Assert.Equal(145d, result.X);
    }

    /// <summary>
    /// Test 35 (P03 carry-over from fix-cycle 2's final verification, MAJOR): "sliding along the wall"
    /// (the invariant the zero-gap overload's own remarks state: X and Y are resolved independently, so a
    /// panel blocked on one axis keeps moving freely along the other) must survive the gap-aware overload.
    /// An obstacle only counts as a WALL for horizontal movement when it really overlaps the panel
    /// VERTICALLY — inflating it by <c>gap</c> on the perpendicular axis as well turned a neighbour that
    /// merely sits 5 DIP above the panel (never in its way) into a full-width barrier that blocked
    /// horizontal travel in BOTH directions, while the very same move is unobstructed at <c>gap = 0</c>.
    /// The gap must shift only the wall's POSITION along the axis of travel, never widen which obstacles
    /// are walls at all.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_ObstacleOnlyWithinGapOnThePerpendicularAxis_DoesNotBlockSliding()
    {
        // Obstacle's bottom edge (95) is 5 DIP above the panel's top (100) -- closer than the 8 DIP gap,
        // but they do not overlap vertically at all, so it cannot obstruct a purely horizontal move.
        var current = new Rect(150d, 100d, 100d, 100d);
        var obstacle = new Rect(100d, 0d, 100d, 95d);

        var free = PanelCollision.ClampAgainstObstacles(current, 300d, 100d, [obstacle], 8d);
        Assert.Equal(300d, free.X);

        // Same at gap = 0 -- the two overloads must agree here (the obstacle is simply not in the way).
        var zeroGap = PanelCollision.ClampAgainstObstacles(current, 300d, 100d, [obstacle]);
        Assert.Equal(zeroGap.X, free.X);

        // ... and symmetrically in the other horizontal direction.
        var leftward = PanelCollision.ClampAgainstObstacles(current, 0d, 100d, [obstacle], 8d);
        Assert.Equal(0d, leftward.X);
    }

    /// <summary>
    /// Test 36: the vertical mirror of Test 35 — an obstacle 5 DIP to the panel's left (no horizontal
    /// overlap) must not block vertical travel when a gap is in play.
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_ObstacleOnlyWithinGapHorizontally_DoesNotBlockVerticalSliding()
    {
        var current = new Rect(100d, 150d, 100d, 100d);
        var obstacle = new Rect(0d, 100d, 95d, 100d);

        var free = PanelCollision.ClampAgainstObstacles(current, 100d, 300d, [obstacle], 8d);
        Assert.Equal(300d, free.Y);

        var zeroGap = PanelCollision.ClampAgainstObstacles(current, 100d, 300d, [obstacle]);
        Assert.Equal(zeroGap.Y, free.Y);

        var upward = PanelCollision.ClampAgainstObstacles(current, 100d, 0d, [obstacle], 8d);
        Assert.Equal(0d, upward.Y);
    }

    /// <summary>
    /// Test 37: the fix for Tests 35/36 must not weaken the wall itself — an obstacle that DOES overlap
    /// on the perpendicular axis still stops the panel exactly <c>gap</c> DIP short, and a panel that is
    /// blocked horizontally still slides vertically in the same frame (the "sliding along the wall"
    /// invariant in its positive form).
    /// </summary>
    [Fact]
    public void ClampAgainstObstacles_WithGap_BlockedOnOneAxis_StillSlidesAlongTheOther()
    {
        var current = new Rect(0d, 0d, 100d, 100d);
        var obstacle = new Rect(200d, 0d, 100d, 100d); // same rows -- a real wall for horizontal travel

        var result = PanelCollision.ClampAgainstObstacles(current, 500d, 300d, [obstacle], 8d);

        Assert.Equal(92d, result.X);  // 200 - 8 - 100: stopped exactly one gap short
        Assert.Equal(300d, result.Y); // ... while sliding freely downward past it
    }
}
