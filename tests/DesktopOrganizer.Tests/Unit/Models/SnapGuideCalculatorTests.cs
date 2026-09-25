using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="SnapGuideCalculator"/> — the inter-panel alignment-guide magnet used by
/// Edit Layout (docs/PLANS/phase-6-edit-layout-ux.md §1 D-7, §7.1 item 5). Only the pure math is covered
/// here: the live drag pipeline wiring in <c>PanelDragMoveBehavior</c> is manual verify (Win10/Win11) —
/// see the plan's V-10.
/// </summary>
[Trait(UnitTests, "UI")]
public class SnapGuideCalculatorTests
{
    // A generous work area that never triggers the off-screen guard unless a test says otherwise.
    private static readonly Rect BigWorkArea = new(0, 0, 10_000, 10_000);

    /// <summary>
    /// Supplies (current, proposed, obstacles) triples covering a spread of guide/clamp composition
    /// scenarios — single and multiple obstacles, diagonal adjacency, and a pre-existing (ghost)
    /// overlap — for the R-8 composition theory below.
    /// </summary>
    /// <returns>The composition cases, each a (current, proposed, obstacles) triple.</returns>
    public static TheoryData<Rect, Rect, Rect[]> CompositionCases()
    {
        var data = new TheoryData<Rect, Rect, Rect[]>();

        // Case A: moving toward a single obstacle, guide would pull it flush — clamp must still hold the wall.
        data.Add(new Rect(0, 0, 100, 100), new Rect(146, 4, 100, 100), [new Rect(150, 0, 100, 100)]);

        // Case B: guide pulls toward CenterY of an obstacle directly below, while sliding X freely.
        data.Add(new Rect(0, 0, 100, 100), new Rect(400, 146, 100, 100), [new Rect(0, 150, 100, 100)]);

        // Case C: two obstacles, one on each axis, both within guide range and both blocking.
        data.Add(
            new Rect(0, 0, 100, 100),
            new Rect(148, 148, 100, 100),
            [new Rect(150, 0, 100, 100), new Rect(0, 150, 100, 100)]);

        // Case D: obstacle is diagonally adjacent — guide aligns centre while the panel is also moving
        // straight toward it; clamp must still prevent any new overlap.
        data.Add(new Rect(0, 0, 100, 100), new Rect(96, 96, 100, 100), [new Rect(100, 100, 100, 100)]);

        // Case E: panel already overlapping an obstacle (R1 ghost) moves further in — guide should not
        // change the ghost-amnesty outcome of the clamp step.
        data.Add(new Rect(60, 0, 100, 100), new Rect(66, 4, 100, 100), [new Rect(0, 0, 100, 100)]);

        // Case F: no obstacle within guide range at all — degenerates to plain clamp behaviour.
        data.Add(new Rect(0, 0, 100, 100), new Rect(300, 300, 100, 100), [new Rect(150, 0, 100, 100)]);

        return data;
    }

    // ----- No obstacles / degenerate input -----

    /// <summary>Test 1: empty obstacle list → no-op, no guides.</summary>
    [Fact]
    public void Snap_NoObstacles_ReturnsUnchangedPositionAndNoGuides()
    {
        var moving = new Rect(100, 100, 80, 80);

        var result = SnapGuideCalculator.Snap(moving, [], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(100d, result.Position.X);
        Assert.Equal(100d, result.Position.Y);
        Assert.Empty(result.Guides);
    }

    /// <summary>Test 2: no obstacle within the magnet threshold → no-op, no guides.</summary>
    [Fact]
    public void Snap_ObstaclesFarAway_ReturnsUnchangedPositionAndNoGuides()
    {
        var moving = new Rect(100, 100, 80, 80);
        var obstacle = new Rect(2000, 2000, 80, 80);

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(100d, result.Position.X);
        Assert.Equal(100d, result.Position.Y);
        Assert.Empty(result.Guides);
    }

    // ----- Six guide kinds, one at a time -----

    /// <summary>Test 3: LeftEdge — moving.Left snaps onto obstacle.Left.</summary>
    [Fact]
    public void Snap_WithinThreshold_SnapsToObstacleLeftEdge()
    {
        // Y is deliberately far apart (5000 vs 500) so only the LeftEdge candidate can ever qualify —
        // isolates this test to a single axis/kind.
        var moving = new Rect(205, 5000, 80, 80); // moving.Left(205) is 5px from obstacle.Left(200)
        var obstacle = new Rect(200, 500, 80, 80);

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.X); // snapped onto obstacle.Left
        Assert.Equal(5000d, result.Position.Y); // Y untouched — no vertical obstacle relationship here
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.LeftEdge, guide.Kind);
        Assert.Equal(200d, guide.Coordinate);
        Assert.Equal(obstacle, guide.Against);
    }

    /// <summary>Test 4: RightEdge — moving.Right snaps onto obstacle.Right.</summary>
    [Fact]
    public void Snap_WithinThreshold_SnapsToObstacleRightEdge()
    {
        // obstacle is wide (Left=200, Right=380) so moving.Left(305) is far from obstacle.Left(200) and
        // moving's centre is far from the obstacle's centre — only RightEdge can qualify.
        var obstacle = new Rect(200, 500, 180, 80); // Left = 200, Right = 380, CenterX = 290
        var moving = new Rect(305, 700, 80, 80); // moving.Right = 385, 5px from obstacle.Right(380)

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(300d, result.Position.X); // newLeft = obstacle.Right(380) - width(80) = 300
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.RightEdge, guide.Kind);
        Assert.Equal(380d, guide.Coordinate);
    }

    /// <summary>Test 5: TopEdge — moving.Top snaps onto obstacle.Top.</summary>
    [Fact]
    public void Snap_WithinThreshold_SnapsToObstacleTopEdge()
    {
        var obstacle = new Rect(900, 200, 80, 80);
        var moving = new Rect(1100, 206, 80, 80); // moving.Top(206) is 6px from obstacle.Top(200)

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.Y);
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.TopEdge, guide.Kind);
        Assert.Equal(200d, guide.Coordinate);
    }

    /// <summary>Test 6: BottomEdge — moving.Bottom snaps onto obstacle.Bottom.</summary>
    [Fact]
    public void Snap_WithinThreshold_SnapsToObstacleBottomEdge()
    {
        var obstacle = new Rect(900, 300, 80, 80); // Top = 300, Bottom = 380

        // moving is short (Height=20) so its Top is far from obstacle.Top and its centre is far from the
        // obstacle's centre — only BottomEdge can qualify.
        var moving = new Rect(1100, 357, 80, 20); // moving.Bottom = 377, 3px from obstacle.Bottom(380)

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(360d, result.Position.Y); // newTop = obstacle.Bottom(380) - height(20) = 360
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.BottomEdge, guide.Kind);
        Assert.Equal(380d, guide.Coordinate);
    }

    /// <summary>Test 7: CenterX — moving's horizontal centre snaps onto obstacle's horizontal centre.</summary>
    [Fact]
    public void Snap_WithinThreshold_SnapsToObstacleCenterX()
    {
        var obstacle = new Rect(400, 500, 100, 100); // Left = 400, Right = 500, CenterX = 450

        // moving is narrow (Width=44) and offset so its Left(430)/Right(474) are both far from the
        // obstacle's Left/Right, but its centre(452) is close to the obstacle's centre(450) —
        // only CenterX can qualify.
        var moving = new Rect(430, 700, 44, 44); // moving centreX = 452, 2px from 450

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(428d, result.Position.X); // newLeft = obstacle.CenterX(450) - moving.Width/2(22) = 428
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.CenterX, guide.Kind);
        Assert.Equal(450d, guide.Coordinate);
    }

    /// <summary>Test 8: CenterY — moving's vertical centre snaps onto obstacle's vertical centre.</summary>
    [Fact]
    public void Snap_WithinThreshold_SnapsToObstacleCenterY()
    {
        var obstacle = new Rect(500, 400, 100, 100); // Top = 400, Bottom = 500, CenterY = 450

        // moving is short (Height=44) and offset so its Top(430)/Bottom(474) are both far from the
        // obstacle's Top/Bottom, but its centre(452) is close to the obstacle's centre(450) —
        // only CenterY can qualify.
        var moving = new Rect(700, 430, 100, 44); // moving centreY = 452, 2px from 450

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(428d, result.Position.Y); // newTop = obstacle.CenterY(450) - moving.Height/2(22) = 428
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.CenterY, guide.Kind);
        Assert.Equal(450d, guide.Coordinate);
    }

    // ----- Determinism: obstacle index wins over delta -----

    /// <summary>
    /// Test 9: two obstacles both have a qualifying LeftEdge candidate — the FIRST obstacle in the list
    /// wins, even though the second obstacle's candidate is numerically closer (smaller delta). This is
    /// the index-first determinism rule (docs/PLANS/phase-6-edit-layout-ux.md §7.1 item 5) that avoids
    /// cross-obstacle delta-comparison flicker.
    /// </summary>
    [Fact]
    public void Snap_TwoObstaclesBothQualify_FirstObstacleInListWinsRegardlessOfDelta()
    {
        // Y is deliberately far apart from both obstacles (10000 vs 500/700) so neither obstacle can also
        // qualify a Y guide — isolates this test to the X axis only.
        var moving = new Rect(206, 10_000, 80, 80); // Left = 206
        var firstObstacle = new Rect(200, 500, 80, 80); // delta 6 — listed first
        var secondObstacle = new Rect(205, 700, 80, 80); // delta 1 — closer, but listed second

        var result = SnapGuideCalculator.Snap(moving, [firstObstacle, secondObstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.X); // snapped to firstObstacle, not the numerically closer second
        var guide = Assert.Single(result.Guides);
        Assert.Equal(firstObstacle, guide.Against);
    }

    /// <summary>
    /// Test 10: the first obstacle in the list does NOT qualify on this axis (too far) — the second
    /// obstacle, which does qualify, wins. Confirms the rule is "first QUALIFYING obstacle", not
    /// unconditionally "obstacle 0".
    /// </summary>
    [Fact]
    public void Snap_FirstObstacleDoesNotQualify_SecondObstacleWins()
    {
        // Y is deliberately far apart from both obstacles (10000 vs 500/700) so neither obstacle can also
        // qualify a Y guide — isolates this test to the X axis only.
        var moving = new Rect(206, 10_000, 80, 80);
        var farObstacle = new Rect(2000, 500, 80, 80); // out of range entirely
        var nearObstacle = new Rect(200, 700, 80, 80); // delta 6, within threshold

        var result = SnapGuideCalculator.Snap(moving, [farObstacle, nearObstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.X);
        var guide = Assert.Single(result.Guides);
        Assert.Equal(nearObstacle, guide.Against);
    }

    /// <summary>
    /// Test 11: same obstacle qualifies on two different kinds (LeftEdge and CenterX) — the smaller
    /// |delta| wins between them.
    /// </summary>
    [Fact]
    public void Snap_SameObstacleMultipleKindsQualify_SmallestDeltaWins()
    {
        // obstacle: Left=200, Width=100 -> Right=300, CenterX=250.
        var obstacle = new Rect(200, 500, 100, 100);

        // moving: Left=205 -> LeftEdge delta=5 (qualifies); Right=205+88=293 -> RightEdge delta=7
        // (qualifies too); centreX=205+44=249 -> CenterX delta=1 (qualifies AND is the strict minimum of
        // all three — CenterX must win, not merely because of declaration order).
        var moving = new Rect(205, 700, 88, 88);

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.CenterX, guide.Kind);
    }

    // ----- Epsilon-stability at the threshold boundary -----

    /// <summary>Test 12: delta exactly equal to the threshold still qualifies (inclusive boundary).</summary>
    [Fact]
    public void Snap_DeltaExactlyAtThreshold_Qualifies()
    {
        var obstacle = new Rect(200, 500, 80, 80);
        var moving = new Rect(200 + SnapGuideCalculator.DefaultThreshold, 700, 80, 80);

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.X);
        Assert.Single(result.Guides);
    }

    /// <summary>
    /// Test 13: two calls whose input differs only by a floating-point-noise-sized amount (1e-9) around
    /// the exact threshold boundary must produce the SAME qualify/don't-qualify outcome — no flicker.
    /// The <c>Eps</c> tolerance band (0.5 DIU, mirroring <c>PanelCollision.Eps</c>) absorbs noise this
    /// small on both sides of the boundary.
    /// </summary>
    [Fact]
    public void Snap_NearThresholdBoundary_ConsecutiveCallsAgreeDespiteFloatingPointNoise()
    {
        var obstacle = new Rect(200, 500, 80, 80);
        var threshold = SnapGuideCalculator.DefaultThreshold;

        var justBelow = new Rect(200 + threshold - 1e-9, 700, 80, 80);
        var justAbove = new Rect(200 + threshold + 1e-9, 700, 80, 80);

        var resultBelow = SnapGuideCalculator.Snap(justBelow, [obstacle], BigWorkArea, threshold);
        var resultAbove = SnapGuideCalculator.Snap(justAbove, [obstacle], BigWorkArea, threshold);

        Assert.Single(resultBelow.Guides);
        Assert.Single(resultAbove.Guides);
        Assert.Equal(resultBelow.Guides[0].Kind, resultAbove.Guides[0].Kind);
        Assert.Equal(resultBelow.Position.X, resultAbove.Position.X);
    }

    /// <summary>Test 14: comfortably past threshold + Eps → does not qualify.</summary>
    [Fact]
    public void Snap_PastThresholdPlusEpsilon_DoesNotQualify()
    {
        var obstacle = new Rect(200, 500, 80, 80);
        var moving = new Rect(200 + SnapGuideCalculator.DefaultThreshold + 1.0, 700, 80, 80); // threshold + 1.0 > threshold + 0.5(Eps)

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Empty(result.Guides);
        Assert.Equal(moving.X, result.Position.X);
    }

    // ----- Independent axes -----

    /// <summary>Test 15: X qualifies, Y does not — only the X guide is produced, Y stays raw.</summary>
    [Fact]
    public void Snap_OnlyXQualifies_YRemainsUnchangedAndUnguided()
    {
        var obstacle = new Rect(200, 2000, 80, 80); // far away vertically
        var moving = new Rect(204, 500, 80, 80); // close on X (delta 4), Y unrelated

        var result = SnapGuideCalculator.Snap(moving, [obstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.X);
        Assert.Equal(500d, result.Position.Y);
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.LeftEdge, guide.Kind);
    }

    /// <summary>Test 16: both axes qualify (against different obstacles) → at most 2 guides, one per axis.</summary>
    [Fact]
    public void Snap_BothAxesQualify_ReturnsAtMostTwoGuidesOnePerAxis()
    {
        var xObstacle = new Rect(200, 2000, 80, 80);
        var yObstacle = new Rect(2000, 300, 80, 80);
        var moving = new Rect(204, 297, 80, 80); // X close to xObstacle.Left(200); Y close to yObstacle.Top(300)

        var result = SnapGuideCalculator.Snap(moving, [xObstacle, yObstacle], BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(2, result.Guides.Count);
        Assert.Contains(result.Guides, g => g.Kind == SnapGuideKind.LeftEdge);
        Assert.Contains(result.Guides, g => g.Kind == SnapGuideKind.TopEdge);
    }

    // ----- Off-screen soft guard -----

    /// <summary>
    /// Test 17: a qualifying guide correction that would push the panel's whole rectangle outside the
    /// supplied work area is discarded — that axis falls back to the pre-guide coordinate.
    /// </summary>
    [Fact]
    public void Snap_CorrectionWouldLeaveWorkArea_GuideDiscardedOnThatAxis()
    {
        var workArea = new Rect(0, 0, 1000, 1000);

        // Y is deliberately far apart (5000) so only the X guide could ever qualify — isolates the
        // off-screen guard's effect to the axis under test.
        var obstacle = new Rect(-100, 5000, 80, 80); // LeftEdge target = -100 -> new panel rect fully off-screen (X in [-100,-20])
        var moving = new Rect(-96, 500, 80, 80); // delta 4, within threshold, but the corrected rect would be fully off-screen

        var result = SnapGuideCalculator.Snap(moving, [obstacle], workArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Empty(result.Guides);
        Assert.Equal(moving.X, result.Position.X);
    }

    /// <summary>Test 18: a degenerate (zero-size) work area disables the off-screen guard entirely.</summary>
    [Fact]
    public void Snap_DegenerateWorkArea_GuardDisabled()
    {
        // Y is deliberately far apart so only the X guide can qualify — isolates this test to the guard.
        var obstacle = new Rect(200, 500, 80, 80);
        var moving = new Rect(204, 5000, 80, 80);

        var result = SnapGuideCalculator.Snap(moving, [obstacle], default, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(200d, result.Position.X);
        Assert.Single(result.Guides);
    }

    // ----- Cross-monitor workArea regression pin (MAJOR-A fix, fix-cycle round 2, b02ced6) -----
    //
    // PanelDragMoveBehavior.OnTitleBarMouseMove resolves and passes a `workArea` to Snap every frame —
    // it must be the work area of the monitor the PANEL CURRENTLY occupies, not the monitor the drag
    // gesture started on. An intermediate fix (d16aab6) snapshotted a single work area once per gesture
    // and reused it for the whole drag; once the cursor crossed onto a second monitor mid-drag, every
    // guide against an obstacle on THAT monitor was silently discarded by Snap's own off-screen guard
    // (StaysOnScreenX/Y) for the rest of the gesture, because the corrected position legitimately falls
    // outside the stale (wrong-monitor) work area. Snap itself was never buggy — it has always faithfully
    // honoured whatever `workArea` its caller passes; the regression was entirely in what
    // PanelDragMoveBehavior chose to pass. These two tests pin exactly that contract at the pure-math
    // level closest to what actually broke: the SAME obstacle/moving geometry produces the guide when
    // the CORRECT (current-monitor) workArea is supplied, and silently drops it when a stale
    // (wrong-monitor) workArea is supplied instead — proving Snap's behavior is correct AND that a caller
    // regressing to a single fixed workArea (the exact shape of the d16aab6 bug) reintroduces the bug.
    // A full behavioral test of PanelDragMoveBehavior itself is not attempted here: it is a WPF
    // Window-based class driven by real MouseButtonEventArgs/MouseEventArgs (which require a live
    // MouseDevice/PresentationSource to construct), and the project has no existing headless-WPF-gesture
    // test harness to reuse for it (unlike ResourceDictionaryMergeTests' STA resource-merge harness,
    // which solves a different problem). Pinning the math one layer down is the documented, deliberate
    // trade-off for this task.

    /// <summary>
    /// Test 20: an obstacle sits in a second monitor's region (e.g. Left starting at 1920, a monitor
    /// to the right of a 1920-wide primary) and the moving panel is already dragged into that same
    /// region. When the CORRECT (current-monitor) work area is supplied, the qualifying LeftEdge guide
    /// applies exactly as it would for any single-monitor scenario.
    /// </summary>
    [Fact]
    public void Snap_ObstacleOnSecondMonitor_AppliesGuide_WhenCorrectCurrentMonitorWorkAreaIsSupplied()
    {
        var secondMonitorWorkArea = new Rect(1920, 0, 1920, 1040);
        var obstacle = new Rect(2000, 500, 80, 80); // lives entirely within the second monitor's region

        // Y is deliberately far apart so only the X/LeftEdge candidate can qualify.
        var moving = new Rect(2005, 10_000, 80, 80); // moving.Left(2005) is 5px from obstacle.Left(2000)

        var result = SnapGuideCalculator.Snap(moving, [obstacle], secondMonitorWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Equal(2000d, result.Position.X); // snapped onto obstacle.Left
        var guide = Assert.Single(result.Guides);
        Assert.Equal(SnapGuideKind.LeftEdge, guide.Kind);
    }

    /// <summary>
    /// Test 21 (the actual regression shape): identical obstacle/moving geometry to Test 20 above — the
    /// same panel, at the same position, against the same obstacle — but the STALE work area from the
    /// FIRST (drag-start) monitor is supplied instead of the current one. The corrected X (2000) falls
    /// entirely outside that stale work area's [0, 1920) range, so Snap's off-screen guard rejects the
    /// guide and the axis silently falls back to the raw (unsnapped) position — exactly the invisible
    /// failure mode d16aab6 introduced (and b02ced6 fixed by resolving the CURRENT monitor's work area
    /// every frame instead of a single gesture-start snapshot).
    /// </summary>
    [Fact]
    public void Snap_ObstacleOnSecondMonitor_GuideSilentlyDropped_WhenStaleFirstMonitorWorkAreaIsSupplied()
    {
        var staleFirstMonitorWorkArea = new Rect(0, 0, 1920, 1040);
        var obstacle = new Rect(2000, 500, 80, 80);
        var moving = new Rect(2005, 10_000, 80, 80);

        var result = SnapGuideCalculator.Snap(moving, [obstacle], staleFirstMonitorWorkArea, SnapGuideCalculator.DefaultThreshold);

        Assert.Empty(result.Guides);
        Assert.Equal(moving.X, result.Position.X); // raw, unsnapped — the guide was silently discarded
    }

    // ----- R-8 composition: guide-snap must never create a NEW overlap once ClampAgainstObstacles runs -----

    /// <summary>
    /// Test 19 (R-8, critical): the full drag pipeline — <c>GridSnap</c> (simulated as already applied to
    /// <paramref name="proposed"/>'s position by the caller in production; here <c>proposed</c> already
    /// stands in for the post-grid-snap proposal) → <see cref="SnapGuideCalculator.Snap"/> →
    /// <c>PanelCollision.ClampAgainstObstacles</c> — never leaves the panel newly overlapping any
    /// obstacle, across a spread of obstacle layouts and starting positions. Mirrors
    /// <c>PanelCollisionTests</c>' own <c>CreatesNewOverlap</c>-style checks, extended to also exercise the
    /// guide step in between. The "hard wall" (<c>PanelCollision.ClampAgainstObstacles</c>) always
    /// runs last, exactly as <c>PanelDragMoveBehavior.OnTitleBarMouseMove</c> wires it.
    /// </summary>
    /// <param name="current">The panel's rectangle before this frame's move.</param>
    /// <param name="proposed">The post-grid-snap proposed rectangle, before the guide step runs.</param>
    /// <param name="obstacles">The stationary obstacles the guide and clamp steps must respect.</param>
    [Theory]
    [MemberData(nameof(CompositionCases))]
    public void Pipeline_GuideSnapThenClamp_NeverCreatesNewOverlap(Rect current, Rect proposed, Rect[] obstacles)
    {
        var guideResult = SnapGuideCalculator.Snap(proposed, obstacles, BigWorkArea, SnapGuideCalculator.DefaultThreshold);

        var clamped = PanelCollision.ClampAgainstObstacles(current, guideResult.Position.X, guideResult.Position.Y, obstacles);
        var afterRect = new Rect(clamped.X, clamped.Y, current.Width, current.Height);

        foreach (var obstacle in obstacles)
        {
            var wasAlreadyOverlapping = PanelCollision.Overlaps(current, obstacle);
            var isNowOverlapping = PanelCollision.Overlaps(afterRect, obstacle);

            var message = $"Guide-snap + clamp introduced a NEW overlap against obstacle {obstacle} " +
                $"(current={current}, proposed={proposed}, guided={guideResult.Position}, clamped={afterRect}).";
            Assert.False(isNowOverlapping && !wasAlreadyOverlapping, message);
        }
    }
}
