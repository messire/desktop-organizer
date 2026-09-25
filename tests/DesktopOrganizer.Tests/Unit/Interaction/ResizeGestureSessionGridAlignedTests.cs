using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// P04-G1/G2/G3 (docs/PLANS/workspace-grid-03-04-integration.md §3.4): the workspace-grid quantisation
/// step that <see cref="ResizeGestureSession.TryUpdate"/> applies to
/// <see cref="ResizeGeometryCalculator.Apply"/>'s output — the SINGLE point shared by the <c>Thumb</c>
/// and native <c>WM_SIZING</c> paths, which is why every geometric property below is asserted here
/// rather than twice, once per controller.
/// </summary>
/// <remarks>
/// <see cref="ResizeGeometryCalculator"/> itself is not touched by P04 (ADR-021 D-2/R6, ADR-028
/// Amendment MAJOR-4) — the "legacy is unchanged" half of that claim is pinned by
/// <c>ResizeGestureSessionTests</c>/<c>ResizeGeometryCharacterizationTests</c> (untouched) plus
/// <see cref="LegacyGesture_IsBitForBitIdenticalToApplyAlone"/> below.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGestureSessionGridAlignedTests
{
    private const double TitleBarHeight = ChildWindow.TitleBarHeight;
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // Fully on the workspace grid for origin (0,0): 96/200 are multiples of 8, and 320/240 are exact
    // span extents (41*8-8 = 320, 31*8-8 = 240), so both edges of both axes start on a grid line.
    private static readonly Rect OnGridStart = new(96d, 200d, 320d, 240d);

    // Deliberately off-grid on every edge — the "first GridAligned resize right after LegacyFreeform"
    // case ADR-028 Amendment MAJOR-3 describes (the anchor is aligned as part of the same call).
    private static readonly Rect OffGridStart = new(101d, 203d, 317d, 243d);

    /// <summary>Gets the eight <see cref="ResizeEdge"/> values, each paired with a raw drag delta.</summary>
    public static TheoryData<ResizeEdge, double, double> EightEdges => new()
    {
        { ResizeEdge.Left, -40d, 0d },
        { ResizeEdge.Right, 40d, 0d },
        { ResizeEdge.Top, 0d, -40d },
        { ResizeEdge.Bottom, 0d, 40d },
        { ResizeEdge.TopLeft, -40d, -40d },
        { ResizeEdge.TopRight, 40d, -40d },
        { ResizeEdge.BottomLeft, -40d, 40d },
        { ResizeEdge.BottomRight, 40d, 40d },
    };

    // ── DoD D4-3, all 8 edges, already-on-grid: the edge OPPOSITE the dragged one never moves. ──────

    /// <summary>Verifies that starting already on the grid, every edge keeps the opposite edge fixed and lands on whole spans.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="dx">The raw horizontal delta.</param>
    /// <param name="dy">The raw vertical delta.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void OnGrid_EveryEdge_KeepsTheOppositeEdgeFixed_AndLandsOnWholeSpans(ResizeEdge edge, double dx, double dy)
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var result = RunGesture(state, edge, OnGridStart, new Vector(dx, dy));

        AssertOppositeEdgesFixed(OnGridStart, result, edge);
        AssertOnGrid(result, state);
    }

    // ── DoD D4-3, all 8 edges, anchor edge starts OFF grid: it is ALIGNED (once), not copied verbatim
    // — ADR-028 Amendment MAJOR-3's second behaviour change. ─────────────────────────────────────────

    /// <summary>Verifies that starting off the grid, every edge aligns its anchor once, on the first quantised frame, then holds it fixed for the rest of the gesture.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="dx">The raw horizontal delta.</param>
    /// <param name="dy">The raw vertical delta.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void OffGrid_EveryEdge_AlignsTheAnchorOnce_ThenHoldsIt(ResizeEdge edge, double dx, double dy)
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var afterOneFrame = RunGesture(state, edge, OffGridStart, new Vector(dx, dy), frames: 1);
        var afterFourFrames = RunGesture(state, edge, OffGridStart, new Vector(dx, dy), frames: 4);

        AssertOnGrid(afterOneFrame, state);
        AssertOnGrid(afterFourFrames, state);

        // The anchor settles on the very first quantised frame and then stays put for the rest of the
        // gesture (it is re-derived from the same StartBounds-anchored Apply output every frame).
        if (edge.AffectsWidth())
            Assert.Equal(edge.AnchorsRight() ? afterOneFrame.Right : afterOneFrame.Left, edge.AnchorsRight() ? afterFourFrames.Right : afterFourFrames.Left);

        if (edge.AffectsHeight())
            Assert.Equal(edge.AnchorsBottom() ? afterOneFrame.Bottom : afterOneFrame.Top, edge.AnchorsBottom() ? afterFourFrames.Bottom : afterFourFrames.Top);
    }

    // ── A non-zero (and negative) origin — a secondary monitor left of/above the primary — quantises
    // relative to THAT origin, not to absolute zero (the GridSnap bug ADR-028 replaces). ─────────────

    /// <summary>Verifies that with a non-zero, negative monitor origin, every edge quantises relative to that origin rather than absolute zero.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="dx">The raw horizontal delta.</param>
    /// <param name="dy">The raw vertical delta.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void NegativeOrigin_EveryEdge_QuantisesRelativeToTheMonitorOrigin(ResizeEdge edge, double dx, double dy)
    {
        var origin = new Point(-1366d, -37d); // deliberately NOT a multiple of the unit size
        var state = FakePanelSizingGridHost.GridAligned(origin, workArea: new Rect(-1366d, -37d, 1366d, 731d));
        var start = new Rect(-1000d, 100d, 317d, 243d);

        var result = RunGesture(state, edge, start, new Vector(dx, dy));

        AssertOnGrid(result, state);
    }

    // ── The minimum span is a hard floor: dragging far past it stops exactly at SpansToBounds(MinSpan)
    // on the dragged axis, with the anchor still fixed. ──────────────────────────────────────────────

    /// <summary>Verifies that shrinking far past the minimum span stops exactly at <c>SpansToBounds(MinSpan)</c> on the dragged axis, with the anchor still fixed.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="dx">The raw horizontal delta.</param>
    /// <param name="dy">The raw vertical delta.</param>
    [Theory]
    [InlineData(ResizeEdge.Right, -400d, 0d)]
    [InlineData(ResizeEdge.Left, 400d, 0d)]
    [InlineData(ResizeEdge.Bottom, 0d, -400d)]
    [InlineData(ResizeEdge.Top, 0d, 400d)]
    public void ShrinkingPastTheMinimum_StopsAtTheMinimumSpan(ResizeEdge edge, double dx, double dy)
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var minSize = WorkspaceLayoutPolicy.SpansToBounds(state.MinSpan, U, G);

        var result = RunGesture(state, edge, OnGridStart, new Vector(dx, dy));

        if (edge.AffectsWidth()) Assert.Equal(minSize.Width, result.Width);
        if (edge.AffectsHeight()) Assert.Equal(minSize.Height, result.Height);
        AssertOppositeEdgesFixed(OnGridStart, result, edge);
    }

    // ── P04-G3: resize anti-overlap for GridAligned (closes ADR-017's open backlog item for this mode).
    // The panel grows toward a neighbour and stops exactly Gap DIP short of it, on a whole slot. ──────

    /// <summary>Verifies that growing toward a neighbour stops exactly one gap short of it, on a whole grid slot — resize anti-overlap for a grid-aligned panel.</summary>
    [Fact]
    public void GrowingIntoANeighbour_StopsOneGapShortOfIt_OnAWholeSlot()
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var neighbour = new Rect(480d, 200d, 200d, 240d); // 64 DIP to the right of OnGridStart's right edge

        var result = RunGesture(state, ResizeEdge.Right, OnGridStart, new Vector(200d, 0d), obstacles: [neighbour]);

        Assert.Equal(OnGridStart.Left, result.Left);           // anchor untouched
        Assert.Equal(neighbour.Left - G, result.Right);        // exactly one gap of clearance
        Assert.Equal(376d, result.Width);                      // 48*8-8 — a whole span, not a partial slot
        AssertOnGrid(result, state);
    }

    // ── Without an obstacle snapshot the same gesture grows freely — proves the test above is not
    // measuring the min-span floor or the work area by accident. ─────────────────────────────────────

    /// <summary>Verifies that without an obstacle snapshot, the same gesture grows freely past the neighbour's position, proving the gap test above measures anti-overlap and not some other floor.</summary>
    [Fact]
    public void WithoutObstacles_TheSameGestureGrowsPastTheNeighboursPosition()
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var result = RunGesture(state, ResizeEdge.Right, OnGridStart, new Vector(200d, 0d));

        Assert.True(result.Right > 480d);
    }

    // ── DoD D4-5 (the half P04 owns): every gesture result survives the span round trip the persistence
    // layer performs — bounds -> span -> bounds is the identity, so a restart replays the same size. ──

    /// <summary>Verifies that every gesture result survives the bounds → span → bounds round trip the persistence layer performs, and stays above the minimum span floor.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="dx">The raw horizontal delta.</param>
    /// <param name="dy">The raw vertical delta.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void EveryGestureResult_SurvivesTheSpanRoundTrip(ResizeEdge edge, double dx, double dy)
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var result = RunGesture(state, edge, OffGridStart, new Vector(dx, dy));

        var span = WorkspaceLayoutPolicy.BoundsToSpans(result.Size, U, G);
        Assert.Equal(result.Size, WorkspaceLayoutPolicy.SpansToBounds(span, U, G));

        // ...and the restored size is still above the floor, so a reload cannot re-trigger migration.
        Assert.True(span.WidthUnits >= state.MinSpan.WidthUnits);
        Assert.True(span.HeightUnits >= state.MinSpan.HeightUnits);
    }

    // ── LegacyFreeform: TryUpdate must return ResizeGeometryCalculator.Apply's output untouched — the
    // machine-checked half of "P04 does not change the legacy mode" (plan §5.4). ─────────────────────

    /// <summary>Verifies that for a LegacyFreeform gesture, <see cref="ResizeGestureSession.TryUpdate"/> returns <see cref="ResizeGeometryCalculator.Apply"/>'s output untouched, bit-for-bit.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="dx">The raw horizontal delta.</param>
    /// <param name="dy">The raw vertical delta.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void LegacyGesture_IsBitForBitIdenticalToApplyAlone(ResizeEdge edge, double dx, double dy)
    {
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight);
        var session = new ResizeGestureSession(new PanelInteractionTracker());
        Assert.True(session.TryBegin(edge, OffGridStart, constraints));

        var delta = new Vector(dx, dy);
        Assert.True(session.TryUpdate(edge, delta, OffGridStart, out var actual));

        var raw = ResizeGeometryCalculator.Accumulate(new ResizeRawExtent(OffGridStart.Width, OffGridStart.Height), edge, delta);
        var expected = ResizeGeometryCalculator.Apply(OffGridStart, edge, raw, OffGridStart.Right, OffGridStart.Bottom, constraints);

        Assert.Equal(expected, actual);
    }

    // ── An obstacle list passed for a LEGACY gesture is ignored outright (resize anti-overlap stays out
    // of scope for that mode — ADR-028 "Границы объёма"). ────────────────────────────────────────────

    /// <summary>Verifies that an obstacle list passed for a LegacyFreeform gesture is ignored outright, since resize anti-overlap stays out of scope for that mode.</summary>
    [Fact]
    public void LegacyGesture_IgnoresObstaclesEntirely()
    {
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight);
        var session = new ResizeGestureSession(new PanelInteractionTracker());

        Assert.True(session.TryBegin(ResizeEdge.Right, OnGridStart, constraints, PanelGridState.Legacy, [new Rect(480d, 200d, 200d, 240d)]));

        Assert.Empty(session.Obstacles);
        Assert.True(session.TryUpdate(ResizeEdge.Right, new Vector(200d, 0d), OnGridStart, out var applied));
        Assert.True(applied.Right > 480d);
    }

    // ── DoD D4-1: 30 consecutive begin/update/end cycles on every edge leave no stuck state — the
    // session is reusable, the tracker returns to Idle, and the geometry stays deterministic. ────────

    /// <summary>Verifies that 30 consecutive begin/update/end cycles, across every edge and every end reason, leave no stuck state, return the tracker to idle each time, and stay geometrically deterministic.</summary>
    [Fact]
    public void ThirtyConsecutiveGestures_LeaveNoStuckState_AndStayDeterministic()
    {
        var tracker = new PanelInteractionTracker();
        var session = new ResizeGestureSession(tracker);
        var state = FakePanelSizingGridHost.GridAligned();
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state);
        var edges = new[]
        {
            ResizeEdge.Left, ResizeEdge.Right, ResizeEdge.Top, ResizeEdge.Bottom,
            ResizeEdge.TopLeft, ResizeEdge.TopRight, ResizeEdge.BottomLeft, ResizeEdge.BottomRight,
        };

        Rect? firstRoundResult = null;

        for (var cycle = 0; cycle < 30; cycle++)
        {
            var edge = edges[cycle % edges.Length];

            Assert.True(session.TryBegin(edge, OnGridStart, constraints, state, [new Rect(800d, 200d, 100d, 100d)]));
            Assert.Equal(PanelInteractionState.Resizing, tracker.State);

            Assert.True(session.TryUpdate(edge, new Vector(16d, 16d), OnGridStart, out var applied));

            // Interleave the two involuntary end reasons the stress matrix cares about (capture loss /
            // source change) with ordinary completions — all three must leave the session reusable.
            var reason = (cycle % 3) switch
            {
                0 => ResizeEndReason.Completed,
                1 => ResizeEndReason.CaptureLost,
                _ => ResizeEndReason.SourceChanged,
            };

            var outcome = session.End(reason);
            Assert.True(outcome.WasActive);
            Assert.False(session.IsActive);
            Assert.Equal(PanelInteractionState.Idle, tracker.State);

            // A second End is a harmless no-op (idempotency), exactly as during a real stuck-gesture heal.
            Assert.False(session.End(reason).WasActive);

            if (edge == ResizeEdge.BottomRight)
            {
                firstRoundResult ??= applied;
                Assert.Equal(firstRoundResult.Value, applied); // identical inputs -> identical geometry, 30 cycles later
            }
        }
    }

    private static Rect RunGesture(
        PanelGridState state,
        ResizeEdge edge,
        Rect start,
        Vector totalDelta,
        IReadOnlyList<Rect>? obstacles = null,
        int frames = 4)
    {
        var session = new ResizeGestureSession(new PanelInteractionTracker());
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state);
        Assert.True(session.TryBegin(edge, start, constraints, state, obstacles));

        var current = start;
        var frameDelta = new Vector(totalDelta.X / frames, totalDelta.Y / frames);
        for (var i = 0; i < frames; i++)
        {
            Assert.True(session.TryUpdate(edge, frameDelta, current, out var applied));
            current = applied;
        }

        session.End(ResizeEndReason.Completed);
        return current;
    }

    private static void AssertOppositeEdgesFixed(Rect start, Rect result, ResizeEdge edge)
    {
        if (edge.AffectsWidth())
        {
            if (edge.AnchorsRight()) Assert.Equal(start.Right, result.Right);
            else Assert.Equal(start.Left, result.Left);
        }
        else
        {
            Assert.Equal(start.Left, result.Left);
            Assert.Equal(start.Width, result.Width);
        }

        if (edge.AffectsHeight())
        {
            if (edge.AnchorsBottom()) Assert.Equal(start.Bottom, result.Bottom);
            else Assert.Equal(start.Top, result.Top);
        }
        else
        {
            Assert.Equal(start.Top, result.Top);
            Assert.Equal(start.Height, result.Height);
        }
    }

    /// <summary>
    /// Both edges of both axes sit exactly on the workspace grid relative to the state's origin, and the
    /// size is exactly <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> of a whole span.
    /// </summary>
    private static void AssertOnGrid(Rect result, PanelGridState state)
    {
        Assert.Equal(0d, Math.IEEERemainder(result.Left - state.Origin.X, U), 9);
        Assert.Equal(0d, Math.IEEERemainder(result.Top - state.Origin.Y, U), 9);

        var span = WorkspaceLayoutPolicy.BoundsToSpans(result.Size, U, G);
        var exact = WorkspaceLayoutPolicy.SpansToBounds(span, U, G);
        Assert.Equal(exact.Width, result.Width, 9);
        Assert.Equal(exact.Height, result.Height, 9);
        Assert.True(span.WidthUnits >= state.MinSpan.WidthUnits);
        Assert.True(span.HeightUnits >= state.MinSpan.HeightUnits);
    }
}
