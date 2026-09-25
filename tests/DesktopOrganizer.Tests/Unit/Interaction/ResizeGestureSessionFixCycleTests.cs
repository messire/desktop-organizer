using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// P04 fix-cycle 1 at the gesture level: the work-area clamp (MAJOR-1) seen through the REAL
/// <see cref="ResizeGestureSession"/>, and the <c>Alt</c> free-resize modifier (orchestrator decision
/// 2026-09-22, variant B — Alt suppresses quantisation per FRAME, the commit still lands on the grid).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGestureSessionFixCycleTests
{
    private const double TitleBarHeight = ChildWindow.TitleBarHeight;
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    /// <summary>Gets the eight <see cref="ResizeEdge"/> values under test.</summary>
    public static TheoryData<ResizeEdge> EightEdges => new()
    {
        ResizeEdge.Left, ResizeEdge.Right, ResizeEdge.Top, ResizeEdge.Bottom,
        ResizeEdge.TopLeft, ResizeEdge.TopRight, ResizeEdge.BottomLeft, ResizeEdge.BottomRight,
    };

    // ── MAJOR-1, the review's exact repro. Work area 1920x1040; panel Left=1600 W=280; drag the RIGHT
    // edge +200. Pre-fix: the live frame grew to 480 (Right = 2080), and the mouse-up snap
    // (SnapToGrid -> ResolveGridDrop -> SafeClamp, "never shrink, move instead") pulled the panel back by
    // TRANSLATING it to Left = 1440 — a 160 DIP jump of the anchor edge the user never touched. ────────

    /// <summary>Verifies MAJOR-1's exact repro: growing past the right work-area wall stops the panel at the wall, and the drop-time snap does not translate it afterward.</summary>
    [Fact]
    public void GrowingPastTheRightWall_StopsAtTheWall_AndTheDropTimeSnapDoesNotTranslateThePanel()
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var start = new Rect(1600d, 200d, 280d, 240d);

        var live = RunGesture(state, ResizeEdge.Right, start, new Vector(200d, 0d));

        Assert.Equal(1600d, live.Left);
        Assert.Equal(1920d, live.Right);
        Assert.Equal(320d, live.Width);

        // What EndResize's _snapToGrid() does for a GridAligned panel — now provably a no-op.
        var dropped = PanelDragPipeline.ResolveGridDrop(live, state, []);
        Assert.Equal(live.Left, dropped.Left);
        Assert.Equal(live.Top, dropped.Top);
    }

    // ── All 8 edges against their own wall(s): inside the work area, anchor fixed, whole span. ───────

    /// <summary>Verifies that for every edge, growing outward against its own work-area wall stops the panel at that wall, keeping the opposite edge fixed and the size on a whole span.</summary>
    /// <param name="edge">The edge being dragged against its own wall.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void EveryEdge_StopsAtItsWall_KeepingTheOppositeEdgeFixed(ResizeEdge edge)
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var start = StartAgainstTheWall(edge, state.WorkArea);

        var live = RunGesture(state, edge, start, OutwardDelta(edge, 400d));

        if (edge.AffectsWidth())
        {
            Assert.True(live.Left >= state.WorkArea.Left, $"{edge}: left {live.Left}");
            Assert.True(live.Right <= state.WorkArea.Right, $"{edge}: right {live.Right}");
            if (edge.AnchorsRight()) Assert.Equal(start.Right, live.Right);
            else Assert.Equal(start.Left, live.Left);
        }

        if (edge.AffectsHeight())
        {
            Assert.True(live.Top >= state.WorkArea.Top, $"{edge}: top {live.Top}");
            Assert.True(live.Bottom <= state.WorkArea.Bottom, $"{edge}: bottom {live.Bottom}");
            if (edge.AnchorsBottom()) Assert.Equal(start.Bottom, live.Bottom);
            else Assert.Equal(start.Top, live.Top);
        }

        var span = WorkspaceLayoutPolicy.BoundsToSpans(live.Size, U, G);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(span, U, G), live.Size);

        // And the drop-time snap leaves the position alone, on every edge.
        var dropped = PanelDragPipeline.ResolveGridDrop(live, state, []);
        Assert.Equal(live.Left, dropped.Left);
        Assert.Equal(live.Top, dropped.Top);
    }

    // ── Alt (variant B), live frames: the edge tracks the cursor 1:1 — byte-for-byte what
    // ResizeGeometryCalculator.Apply alone produces, i.e. exactly the LegacyFreeform behaviour. ───────

    /// <summary>Verifies that holding Alt suppresses grid quantisation for that live frame only — the very next frame with Alt released is quantised again.</summary>
    /// <param name="edge">The edge being dragged.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void AltHeld_SuppressesQuantisation_ForThatFrameOnly(ResizeEdge edge)
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var start = new Rect(101d, 203d, 317d, 243d); // deliberately off-grid on every edge
        var delta = OutwardDelta(edge, 37d);          // 37 is not a multiple of the 8 DIP unit

        var altDown = true;
        var session = new ResizeGestureSession(new PanelInteractionTracker(), () => altDown);
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state);
        Assert.True(session.TryBegin(edge, start, constraints, state));

        Assert.True(session.TryUpdate(edge, delta, start, out var free));

        var raw = ResizeGeometryCalculator.Accumulate(new ResizeRawExtent(start.Width, start.Height), edge, delta);
        var unquantised = ResizeGeometryCalculator.Apply(start, edge, raw, start.Right, start.Bottom, constraints);
        Assert.Equal(unquantised, free);

        // Releasing Alt takes effect on the VERY NEXT frame (read fresh, never snapshotted at TryBegin).
        altDown = false;
        Assert.True(session.TryUpdate(edge, delta, free, out var quantised));
        AssertOnGrid(quantised, state);
    }

    // ── Alt (variant B), commit: however free the frames were, the panel comes to REST on the grid —
    // ADR-028's absolute rule, enforced by PanelSizingGridSupport.PersistSpans, not by the frames. ────

    /// <summary>Verifies that even with Alt held for the whole gesture, the panel still comes to rest on the grid at commit time, since <see cref="PanelSizingGridSupport.PersistSpans"/> enforces it, not the live frames.</summary>
    /// <param name="edge">The edge being dragged.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void AltHeldForTheWholeGesture_StillCommitsOnTheGrid(ResizeEdge edge)
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var host = new FakePanelSizingGridHost { State = state };
        var start = new Rect(101d, 203d, 317d, 243d);

        var session = new ResizeGestureSession(new PanelInteractionTracker(), () => true);
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state);
        Assert.True(session.TryBegin(edge, start, constraints, state));
        Assert.True(session.TryUpdate(edge, OutwardDelta(edge, 37d), start, out var free));
        Assert.True(session.End(ResizeEndReason.Completed).ShouldPersist);

        PanelSizingGridSupport.PersistSpans(host, free, edge);

        var commit = Assert.Single(host.Commits);
        AssertOnGrid(commit.Bounds, state);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(commit.Span, U, G), commit.Bounds.Size);
    }

    // ── Alt is only ever consulted for a GridAligned gesture (a legacy one is free anyway, and paying
    // for a keyboard read on every WM_SIZING frame of every legacy panel would be pure waste). ───────

    /// <summary>Verifies that a LegacyFreeform gesture never consults the free-resize modifier delegate, since a legacy panel is free regardless.</summary>
    [Fact]
    public void ALegacyGesture_NeverAsksForTheModifier()
    {
        var asked = 0;
        var session = new ResizeGestureSession(
            new PanelInteractionTracker(),
            () =>
            {
                asked++;
                return true;
            });
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight);
        var start = new Rect(101d, 203d, 317d, 243d);

        Assert.True(session.TryBegin(ResizeEdge.Right, start, constraints));
        Assert.True(session.TryUpdate(ResizeEdge.Right, new Vector(37d, 0d), start, out _));

        Assert.Equal(0, asked);
    }

    private static Rect RunGesture(PanelGridState state, ResizeEdge edge, Rect start, Vector totalDelta, int frames = 4)
    {
        var session = new ResizeGestureSession(new PanelInteractionTracker(), () => false);
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state);
        Assert.True(session.TryBegin(edge, start, constraints, state));

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

    /// <summary>A whole-span rectangle 8 DIP from every wall <paramref name="edge"/> moves toward.</summary>
    private static Rect StartAgainstTheWall(ResizeEdge edge, Rect workArea)
    {
        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(36, 31), U, G); // 280 x 240

        var left = !edge.AffectsWidth() ? workArea.Left + 400d
            : edge.AnchorsRight() ? workArea.Left + U
            : workArea.Right - U - size.Width;

        var top = !edge.AffectsHeight() ? workArea.Top + 304d
            : edge.AnchorsBottom() ? workArea.Top + U
            : workArea.Bottom - U - size.Height;

        return new Rect(left, top, size.Width, size.Height);
    }

    /// <summary>A delta that GROWS the panel on every axis <paramref name="edge"/> moves.</summary>
    private static Vector OutwardDelta(ResizeEdge edge, double by) => new(
        edge.AffectsWidth() ? (edge.AnchorsRight() ? -by : by) : 0d,
        edge.AffectsHeight() ? (edge.AnchorsBottom() ? -by : by) : 0d);

    private static void AssertOnGrid(Rect result, PanelGridState state)
    {
        Assert.Equal(0d, Math.IEEERemainder(result.Left - state.Origin.X, U), 9);
        Assert.Equal(0d, Math.IEEERemainder(result.Top - state.Origin.Y, U), 9);

        var span = WorkspaceLayoutPolicy.BoundsToSpans(result.Size, U, G);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(span, U, G), result.Size);
    }
}
