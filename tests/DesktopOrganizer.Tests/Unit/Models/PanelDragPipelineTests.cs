using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelDragPipeline"/> — the pure half of the
/// <see cref="PanelGeometryMode.GridAligned"/> drag pipeline (P03, docs/PLANS/workspace-grid-03-04-integration.md
/// §2.3/§5.1). No WPF window, no STA thread, no <c>IMonitorService</c>: the grid state is constructed
/// directly, exactly as <see cref="PanelGridStateResolver.Resolve"/> would hand it to the behavior.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelDragPipelineTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    private static readonly Rect PrimaryWorkArea = new(0, 40, 1920, 1040);

    // A monitor left of and above the primary — the negative-coordinate case ADR-028 calls out.
    private static readonly Rect NegativeWorkArea = new(-1366, -160, 1366, 728);

    private static PanelGridState State(Rect? workArea = null, string? deviceId = Display1)
    {
        var area = workArea ?? PrimaryWorkArea;
        return new PanelGridState(
            IsGridAligned: true,
            Origin: area.TopLeft,
            WorkArea: area,
            UnitSize: U,
            Gap: G,
            MinSpan: WorkspaceGridMetrics.MinSpanExpanded,
            MonitorDeviceId: deviceId);
    }

    /// <summary>
    /// M-1 fix-cycle 1: a single, effectively unbounded monitor whose work area is <paramref name="workArea"/>
    /// — reproduces <see cref="ResolveGridFrame"/>'s PRE-fix behavior (a single, gesture-start-frozen work
    /// area used for every frame) for every test below that is not itself exercising the per-frame
    /// monitor resolution the fix adds. <c>Bounds</c> is deliberately huge so it contains every coordinate
    /// any test in this file ever proposes.
    /// </summary>
    private static IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> SingleMonitor(Rect? workArea = null, string deviceId = Display1) =>
        [(deviceId, new Rect(-1_000_000, -1_000_000, 2_000_000, 2_000_000), workArea ?? PrimaryWorkArea)];

    /// <summary>Asserts that <paramref name="coordinate"/> sits exactly on a grid line measured from <paramref name="origin"/>.</summary>
    private static void AssertOnGrid(double coordinate, double origin)
    {
        var offset = (coordinate - origin) / U;
        Assert.Equal(Math.Round(offset), offset, 9);
    }

    // ── ResolveGridFrame: step 1, quantisation relative to the monitor origin ─────────────────────

    [Fact]
    public void ResolveGridFrame_QuantisesToTheMonitorWorkAreaOrigin_NotGlobalZero()
    {
        var state = State();
        var current = new Rect(100, 140, 248, 248);

        // Raw asks for (103, 145): 103 is 103 from global zero (off-grid either way), but the origin's
        // Y is 40, so the Y line is 40 + k*8 -- 144, not 144-from-zero by accident.
        var result = PanelDragPipeline.ResolveGridFrame(
            current, new Point(103, 145), state, SingleMonitor(), [], isEditingLayout: false, freeMove: false);

        Assert.Equal(104d, result.X);
        Assert.Equal(144d, result.Y);
        AssertOnGrid(result.X, state.Origin.X);
        AssertOnGrid(result.Y, state.Origin.Y);
    }

    [Fact]
    public void ResolveGridFrame_NegativeOriginMonitor_SnapsRelativeToThatOrigin()
    {
        var state = State(NegativeWorkArea);
        var current = new Rect(-1000, -100, 248, 248);

        var result = PanelDragPipeline.ResolveGridFrame(
            current, new Point(-1003.5, -101.5), state, SingleMonitor(NegativeWorkArea), [], isEditingLayout: false, freeMove: false);

        AssertOnGrid(result.X, state.Origin.X);
        AssertOnGrid(result.Y, state.Origin.Y);
        Assert.True(Math.Abs(result.X - (-1003.5)) <= U / 2d);
        Assert.True(Math.Abs(result.Y - (-101.5)) <= U / 2d);
    }

    [Fact]
    public void ResolveGridFrame_IsIdempotent_ReSnappingItsOwnOutputChangesNothing()
    {
        var state = State();
        var current = new Rect(100, 140, 248, 248);

        var once = PanelDragPipeline.ResolveGridFrame(
            current, new Point(317.3, 209.9), state, SingleMonitor(), [], isEditingLayout: false, freeMove: false);
        var twice = PanelDragPipeline.ResolveGridFrame(
            current, once, state, SingleMonitor(), [], isEditingLayout: false, freeMove: false);

        Assert.Equal(once, twice);
    }

    // ── ResolveGridFrame: the Alt free-move modifier (D3-9) ───────────────────────────────────────

    [Fact]
    public void ResolveGridFrame_FreeMove_SuppressesQuantisationOnly()
    {
        var state = State();
        var current = new Rect(100, 140, 248, 248);
        var raw = new Point(317.3, 209.9);

        var free = PanelDragPipeline.ResolveGridFrame(current, raw, state, SingleMonitor(), [], isEditingLayout: false, freeMove: true);

        Assert.Equal(raw, free);
    }

    [Fact]
    public void ResolveGridFrame_FreeMove_StillClampedByTheHardWall()
    {
        var state = State();
        var current = new Rect(0, 40, 248, 248);
        var obstacle = new Rect(400, 40, 248, 248);

        var free = PanelDragPipeline.ResolveGridFrame(
            current, new Point(390, 40), state, SingleMonitor(), [obstacle], isEditingLayout: false, freeMove: true);

        // Alt loosens the grid, never the wall (ADR-028 «Модификатор свободного движения»).
        Assert.True(free.X + current.Width <= obstacle.X - G + 0.001);
    }

    [Fact]
    public void ResolveGridFrame_FreeMove_StillRunsGuidesInEditLayout()
    {
        var state = State();
        var current = new Rect(0, 40, 100, 100);
        var obstacle = new Rect(500, 600, 100, 100);

        var guided = PanelDragPipeline.ResolveGridFrame(
            current, new Point(497, 40), state, SingleMonitor(), [obstacle], isEditingLayout: true, freeMove: true);

        // Left edges align: the guide magnet is independent of the grid step Alt suppressed.
        Assert.Equal(obstacle.X, guided.X);
    }

    // ── ResolveGridFrame: step 2, ADR-026 guides, Edit Layout only ────────────────────────────────

    [Fact]
    public void ResolveGridFrame_GuidesRunOnlyInEditLayout_AndAfterQuantisation()
    {
        var state = State();
        var current = new Rect(0, 40, 100, 100);
        var obstacle = new Rect(500, 600, 100, 100);
        var raw = new Point(497, 40);

        var normal = PanelDragPipeline.ResolveGridFrame(current, raw, state, SingleMonitor(), [obstacle], isEditingLayout: false, freeMove: false);
        var editing = PanelDragPipeline.ResolveGridFrame(current, raw, state, SingleMonitor(), [obstacle], isEditingLayout: true, freeMove: false);

        Assert.Equal(496d, normal.X);       // quantised only
        Assert.Equal(obstacle.X, editing.X); // ... then pulled onto the neighbour's edge by the guide
    }

    // ── ResolveGridFrame: step 3, the hard wall is always last (D3-10) ────────────────────────────

    [Fact]
    public void ResolveGridFrame_QuantisationNeverPushesThePanelIntoANeighbour()
    {
        var state = State();
        var current = new Rect(0, 40, 248, 248);
        var obstacle = new Rect(256, 40, 248, 248);

        var result = PanelDragPipeline.ResolveGridFrame(
            current, new Point(250, 40), state, SingleMonitor(), [obstacle], isEditingLayout: false, freeMove: false);

        var moved = new Rect(result, current.Size);
        Assert.False(PanelCollision.Overlaps(moved, obstacle));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResolveGridFrame_RandomTrajectories_NeverCreateANewOverlap(bool isEditingLayout, bool freeMove)
    {
        var state = State();
        var obstacles = new[]
        {
            new Rect(400, 200, 248, 248),
            new Rect(700, 400, 248, 496),
            new Rect(1100, 100, 400, 248),
        };

        var random = new Random(20260922);
        var current = new Rect(0, 40, 248, 248);

        for (var i = 0; i < 1000; i++)
        {
            var raw = new Point(
                (random.NextDouble() * 1900d) - 50d,
                (random.NextDouble() * 1000d) + 20d);

            var next = PanelDragPipeline.ResolveGridFrame(current, raw, state, SingleMonitor(), obstacles, isEditingLayout, freeMove);
            var moved = new Rect(next, current.Size);

            foreach (var obstacle in obstacles)
            {
                Assert.False(
                    PanelCollision.Overlaps(moved, obstacle),
                    $"iteration {i}: {moved} overlaps {obstacle} (raw {raw})");
            }

            current = moved; // the next frame starts from where this one landed, like a real gesture
        }
    }

    // ── M-1 fix-cycle 1: the guide step's work area is resolved PER FRAME, not frozen at gesture start ─

    [Fact]
    public void ResolveGridFrame_GuideWorkArea_IsResolvedPerFrame_NotFrozenAtGestureStart()
    {
        // The gesture STARTED on DISPLAY1 (state.WorkArea == PrimaryWorkArea, 0..1920), but the panel and
        // its neighbour have both crossed onto DISPLAY2 (WorkArea 1920..3840) by this frame -- exactly
        // the mid-drag cross-monitor case the finding calls out. Before the fix, the guide step used
        // `state.WorkArea` (frozen at DISPLAY1) unconditionally, so a guide correction that lands well
        // outside 0..1920 -- even though it is perfectly valid on the monitor the panel is ACTUALLY over
        // -- was discarded by StaysOnScreenX. Numbers chosen so quantisation alone (step 1) does NOT
        // already land on the obstacle's edge (2203 is not a multiple of 8 from either origin), so only
        // the guide step can produce it.
        // The obstacle sits far away on the Y axis so the guide-corrected X can never actually overlap it
        // (ClampHardWall — always the LAST step — would otherwise push the result away from the obstacle
        // regardless of the guide, confounding what this test means to isolate).
        var state = State(); // DISPLAY1, Origin/WorkArea = PrimaryWorkArea (0,40,1920,1040)
        var monitors = TwoMonitors;
        var current = new Rect(1950, 40, 100, 100); // already on DISPLAY2
        var obstacle = new Rect(2203, 600, 100, 100); // neighbour, also on DISPLAY2, off-grid on purpose
        var raw = new Point(2205, 40);

        var result = PanelDragPipeline.ResolveGridFrame(
            current, raw, state, monitors, [obstacle], isEditingLayout: true, freeMove: false);

        // The guide must actually fire: DISPLAY2's OWN work area (not the frozen DISPLAY1 one) is what
        // lets StaysOnScreenX accept a correction near X=2203 (well outside DISPLAY1's 0..1920 work area,
        // but comfortably inside DISPLAY2's 1920..3840 one).
        Assert.Equal(obstacle.X, result.X);
    }

    [Fact]
    public void ResolveGridFrame_GuideWorkArea_FallsBackToDisabledGuard_WhenNoMonitorMatches()
    {
        // Mirrors the LegacyFreeform per-frame scan's own fallback (Rect.Empty "disable the guard", not a
        // stale work area) when the proposed position does not fall inside ANY snapshotted monitor.
        var state = State();
        var current = new Rect(0, 40, 100, 100);
        var obstacle = new Rect(5100, 600, 100, 100); // off the edge of every monitor in TwoMonitors
        var raw = new Point(5097, 40);

        var result = PanelDragPipeline.ResolveGridFrame(
            current, raw, state, TwoMonitors, [obstacle], isEditingLayout: true, freeMove: false);

        // A disabled guard means StaysOnScreenX always accepts -- the guide still fires (there is no
        // monitor to reject it against), landing exactly on the obstacle's edge.
        Assert.Equal(obstacle.X, result.X);
    }

    // ── ResolveGridDrop (D3-5, D3-9's "drop always quantises") ────────────────────────────────────

    [Fact]
    public void ResolveGridDrop_QuantisesEvenWhenTheGestureEndedOffGrid()
    {
        var state = State();
        var current = new Rect(317.3, 209.9, 248, 248);

        var dropped = PanelDragPipeline.ResolveGridDrop(current, state, []);

        AssertOnGrid(dropped.X, state.Origin.X);
        AssertOnGrid(dropped.Y, state.Origin.Y);
        Assert.Equal(current.Size, dropped.Size);
    }

    [Fact]
    public void ResolveGridDrop_IsIdempotent()
    {
        var state = State();
        var current = new Rect(317.3, 209.9, 248, 248);

        var once = PanelDragPipeline.ResolveGridDrop(current, state, []);
        var twice = PanelDragPipeline.ResolveGridDrop(once, state, []);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ResolveGridDrop_OutsideTheWorkArea_PulledBackOnGridAndInsideIt()
    {
        var state = State();
        var current = new Rect(1900, 1000, 248, 248); // hangs off the right/bottom edges

        var dropped = PanelDragPipeline.ResolveGridDrop(current, state, []);

        AssertOnGrid(dropped.X, state.Origin.X);
        AssertOnGrid(dropped.Y, state.Origin.Y);
        Assert.True(dropped.Right <= state.WorkArea.Right + 0.001);
        Assert.True(dropped.Bottom <= state.WorkArea.Bottom + 0.001);

        // MAJOR-2: the clamp result is ITSELF on the grid, so re-snapping cannot push it back out.
        Assert.Equal(dropped, PanelDragPipeline.ResolveGridDrop(dropped, state, []));
    }

    [Fact]
    public void ResolveGridDrop_NegativeOriginMonitor_StaysOnThatMonitorsGrid()
    {
        var state = State(NegativeWorkArea);
        var current = new Rect(-1300.7, -120.4, 248, 248);

        var dropped = PanelDragPipeline.ResolveGridDrop(current, state, []);

        AssertOnGrid(dropped.X, state.Origin.X);
        AssertOnGrid(dropped.Y, state.Origin.Y);
        Assert.True(dropped.X >= state.WorkArea.X - 0.001);
        Assert.True(dropped.Y >= state.WorkArea.Y - 0.001);
    }

    // ── m-2 fix-cycle 1: SafeClamp reasons about the EXPANDED size for a rolled-up panel ─────────────

    [Fact]
    public void ResolveGridDrop_RolledUpPanelExpandedSizeForClamp_KeepsFullFootprintInsideWorkArea()
    {
        // A shallow work area makes the bug observable with small numbers: only 400 DIP tall.
        var state = State(new Rect(0, 0, 1920, 400));
        const double collapsedHeight = 32d; // PanelMetrics.HeaderHeight -- what a rolled-up panel's LIVE rect reports.
        var current = new Rect(100, 380, 248, collapsedHeight); // near the work area's bottom edge, ROLLED UP
        var expandedSize = new Size(248, 300); // the panel's real, persisted (expanded) height

        var dropped = PanelDragPipeline.ResolveGridDrop(current, state, [], expandedSize);

        // The chosen POSITION must leave room for the EXPANDED height inside the work area -- otherwise
        // un-rolling the panel later (with no further move) would push it straight past the work area's
        // bottom edge, because the drop-time clamp only ever reasoned about the ~32 DIP collapsed height.
        Assert.True(
            dropped.Y + expandedSize.Height <= state.WorkArea.Bottom + 0.001,
            $"dropped.Y={dropped.Y} + expanded height {expandedSize.Height} exceeds work area bottom {state.WorkArea.Bottom}");

        // The rectangle's own reported size is still `current`'s (only .TopLeft is ever committed by
        // callers) -- this overload never changes what gets WRITTEN, only where.
        Assert.Equal(current.Size, dropped.Size);
    }

    [Fact]
    public void ResolveGridDrop_ThreeArgumentOverload_StillClampsByTheLiveSize_UnchangedFromBeforeTheFix()
    {
        // Backward-compatibility guard: every existing caller (and the pre-m-2 behavior) uses the
        // 3-argument overload, which must keep clamping by `current`'s OWN size verbatim.
        var state = State(new Rect(0, 0, 1920, 400));
        var current = new Rect(100, 380, 248, 32d);

        var viaThreeArgs = PanelDragPipeline.ResolveGridDrop(current, state, []);
        var viaFourArgsWithLiveSize = PanelDragPipeline.ResolveGridDrop(current, state, [], current.Size);

        Assert.Equal(viaFourArgsWithLiveSize, viaThreeArgs);
    }

    // ── ClampHardWall (D3-2 equal gaps, D3-10 the wall is never pierced) ──────────────────────────

    [Fact]
    public void ClampHardWall_StopsExactlyOneGapShortOfTheNeighbour()
    {
        var current = new Rect(0, 40, 248, 248);
        var obstacle = new Rect(600, 40, 248, 248);

        var result = PanelDragPipeline.ClampHardWall(current, 900, 40, [obstacle], G);

        Assert.Equal(G, obstacle.X - result.Right, 9);
    }

    [Fact]
    public void ClampHardWall_TwoNeighboursApproachedFromBothSides_LeaveTheSameGap()
    {
        var left = new Rect(0, 40, 248, 248);
        var right = new Rect(800, 40, 248, 248);

        var fromLeft = PanelDragPipeline.ClampHardWall(new Rect(300, 40, 248, 248), -500, 40, [left, right], G);
        var fromRight = PanelDragPipeline.ClampHardWall(new Rect(300, 40, 248, 248), 5000, 40, [left, right], G);

        Assert.Equal(fromLeft.X - left.Right, right.X - fromRight.Right, 9);
    }

    [Fact]
    public void ClampHardWall_SlidingAlongAWall_IsNotBlockedByAPerpendicularNeighbour()
    {
        // P03 carry-over regression (PanelCollision.InflateX): a neighbour 5 DIP above the panel is not
        // in the way of a horizontal move, even though 5 < gap.
        var current = new Rect(150, 100, 100, 100);
        var above = new Rect(100, 0, 100, 95);

        var result = PanelDragPipeline.ClampHardWall(current, 300, 100, [above], G);

        Assert.Equal(300d, result.X);
    }

    [Fact]
    public void ClampHardWall_PreExistingOverlap_IsStillAmnestied_R1()
    {
        var current = new Rect(60, 40, 248, 248);
        var obstacle = new Rect(0, 40, 248, 248); // already overlapping by ~188 DIP

        var away = PanelDragPipeline.ClampHardWall(current, 400, 40, [obstacle], G);

        Assert.Equal(400d, away.X); // can still be dragged apart
    }

    [Fact]
    public void ClampHardWall_NoObstacles_ReturnsTheProposedPositionUnchanged()
    {
        var current = new Rect(10, 20, 100, 100);

        var result = PanelDragPipeline.ClampHardWall(current, 500, 600, [], G);

        Assert.Equal(new Rect(500, 600, 100, 100), result);
    }

    // ── ResolveDropMonitor (F-6, D3-7) ────────────────────────────────────────────────────────────

    private static IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> TwoMonitors =>
    [
        (Display1, new Rect(0, 0, 1920, 1080), PrimaryWorkArea),
        (Display2, new Rect(1920, 0, 1920, 1080), new Rect(1920, 0, 1920, 1080)),
    ];

    [Fact]
    public void ResolveDropMonitor_CenterOnTheOtherMonitor_ReturnsItsDeviceName()
    {
        // Top-left is still on DISPLAY1 -- only the CENTER decides (ADR-028).
        var final = new Rect(1800, 200, 400, 300);

        var result = PanelDragPipeline.ResolveDropMonitor(final, TwoMonitors, Display1);

        Assert.Equal(Display2, result);
    }

    [Fact]
    public void ResolveDropMonitor_CenterOnTheSameMonitor_ReturnsNull()
    {
        var final = new Rect(100, 200, 400, 300);

        Assert.Null(PanelDragPipeline.ResolveDropMonitor(final, TwoMonitors, Display1));
    }

    [Fact]
    public void ResolveDropMonitor_DeviceNameComparisonIsCaseInsensitive()
    {
        var final = new Rect(100, 200, 400, 300);

        Assert.Null(PanelDragPipeline.ResolveDropMonitor(final, TwoMonitors, Display1.ToUpperInvariant()));
    }

    [Fact]
    public void ResolveDropMonitor_CenterOutsideEveryMonitor_KeepsTheCurrentId()
    {
        var final = new Rect(5000, 5000, 400, 300);

        Assert.Null(PanelDragPipeline.ResolveDropMonitor(final, TwoMonitors, Display1));
    }

    [Fact]
    public void ResolveDropMonitor_NoMonitorsOrDegenerateRect_ReturnsNull()
    {
        Assert.Null(PanelDragPipeline.ResolveDropMonitor(new Rect(0, 0, 10, 10), [], Display1));
        Assert.Null(PanelDragPipeline.ResolveDropMonitor(new Rect(double.NaN, 0, 10, 10), TwoMonitors, Display1));
    }

    [Fact]
    public void ResolveDropMonitor_PanelWithNoMonitorIdYet_AdoptsTheMonitorItLandedOn()
    {
        var final = new Rect(100, 200, 400, 300);

        Assert.Equal(Display1, PanelDragPipeline.ResolveDropMonitor(final, TwoMonitors, currentDeviceId: null));
    }

    // ── P04 fix-cycle 1 final verification (conditional MAJOR): the drop-time snap must reason about the
    //    size the resize controller just COMMITTED (Window.Width/Height), not the one WPF has laid out so
    //    far (ActualWidth/Height lags one layout pass) — otherwise an Alt-resize that ended at a live 480
    //    but committed 320 is clamped as if it were still 480 and the anchor edge jumps. ─────────────────

    [Fact]
    public void CommittedSize_PrefersExplicitWindowSize_OverStaleActualSize()
    {
        var size = PanelDragPipeline.CommittedSize(width: 320, height: 248, actualWidth: 480, actualHeight: 248);

        Assert.Equal(new Size(320, 248), size);
    }

    [Fact]
    public void CommittedSize_FallsBackToActualSize_WhenWindowSizeIsNaN()
    {
        var size = PanelDragPipeline.CommittedSize(width: double.NaN, height: double.NaN, actualWidth: 480, actualHeight: 248);

        Assert.Equal(new Size(480, 248), size);
    }

    [Fact]
    public void ResolveGridDrop_UsingCommittedSize_KeepsTheAnchorEdgeAtTheWorkAreaWall()
    {
        // WA 1920 wide, panel anchored at Left=1600: the resize committed W=320 (fits exactly), but the
        // window's ActualWidth still reports the pre-commit live 480. Clamping by 480 would translate the
        // panel to Left=1440 (the MAJOR-1 symptom); clamping by the committed 320 must leave Left=1600.
        var state = State(new Rect(0, 0, 1920, 1040));
        var committed = PanelDragPipeline.CommittedSize(320, 248, 480, 248);
        var current = new Rect(new Point(1600, 40), committed);

        var dropped = PanelDragPipeline.ResolveGridDrop(current, state, [], committed);

        Assert.Equal(1600, dropped.X);
    }

    // ── C-1 fix-cycle 1: RetargetToDropMonitor (the F-6 teleport-back-to-origin-monitor bug) ─────────

    [Fact]
    public void RetargetToDropMonitor_CenterCrossedToTheOtherMonitor_RetargetsOriginWorkAreaAndDeviceId()
    {
        var state = State(); // resolved for DISPLAY1 at gesture start
        var raw = new Rect(2200, 40, 248, 248); // center (2324, 164) -- on DISPLAY2

        var retargeted = PanelDragPipeline.RetargetToDropMonitor(raw, state, TwoMonitors);

        Assert.Equal(Display2, retargeted.MonitorDeviceId);
        Assert.Equal(TwoMonitors[1].WorkArea.TopLeft, retargeted.Origin);
        Assert.Equal(TwoMonitors[1].WorkArea, retargeted.WorkArea);
        // Everything that does NOT depend on which monitor is unchanged.
        Assert.Equal(state.UnitSize, retargeted.UnitSize);
        Assert.Equal(state.Gap, retargeted.Gap);
        Assert.Equal(state.MinSpan, retargeted.MinSpan);
    }

    [Fact]
    public void RetargetToDropMonitor_ComposedWithResolveGridDrop_KeepsThePanelOnTheMonitorItWasDroppedOn()
    {
        // C-1 fix-cycle 1 repro, numbers verbatim from the code-review finding: DISPLAY1 WA
        // (0,40,1920,1040), DISPLAY2 WA (1920,0,1920,1080), panel width 248, U=8. Before this fix,
        // `PanelDragMoveBehavior.SnapToGrid` called ResolveGridDrop with the STALE gesture-start `state`
        // (still DISPLAY1's origin/work area) -- SafeClamp's maxSlot for a 248-wide panel in a 1920-wide
        // work area is floor((1920-248)/8)=209, so the panel was silently clamped back to
        // X = 0 + 209*8 = 1672, a 528 DIP teleport away from where it was actually dropped (Left=2200).
        var state = State();
        var raw = new Rect(2200, 40, 248, 248); // dropped with its center on DISPLAY2

        // Pins the documented pre-fix regression number: this is exactly what the bug produced.
        var withoutTheFix = PanelDragPipeline.ResolveGridDrop(raw, state, []);
        Assert.Equal(1672d, withoutTheFix.X);

        var retargeted = PanelDragPipeline.RetargetToDropMonitor(raw, state, TwoMonitors);
        var dropped = PanelDragPipeline.ResolveGridDrop(raw, retargeted, []);

        Assert.Equal(Display2, retargeted.MonitorDeviceId);
        Assert.True(dropped.X > 1920d, $"expected the panel to stay on DISPLAY2, got X={dropped.X}");
        AssertOnGrid(dropped.X, retargeted.Origin.X);
    }

    [Fact]
    public void RetargetToDropMonitor_PanelNeverLeftItsMonitor_ReturnsTheSameStateInstance()
    {
        var state = State();
        var raw = new Rect(100, 200, 248, 248); // center well inside DISPLAY1

        var retargeted = PanelDragPipeline.RetargetToDropMonitor(raw, state, TwoMonitors);

        Assert.Equal(state, retargeted);
    }

    [Fact]
    public void RetargetToDropMonitor_NotGridAligned_IsANoOp()
    {
        var raw = new Rect(2200, 40, 248, 248);

        var retargeted = PanelDragPipeline.RetargetToDropMonitor(raw, PanelGridState.Legacy, TwoMonitors);

        Assert.Equal(PanelGridState.Legacy, retargeted);
    }

    [Fact]
    public void RetargetToDropMonitor_NoMonitors_IsANoOp()
    {
        var state = State();
        var raw = new Rect(2200, 40, 248, 248);

        var retargeted = PanelDragPipeline.RetargetToDropMonitor(raw, state, []);

        Assert.Equal(state, retargeted);
    }
}
