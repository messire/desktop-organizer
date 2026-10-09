using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Interaction.Native;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// P04 at the controller level (docs/PLANS/workspace-grid-03-04-integration.md §3.4/§3.7, DoD
/// D4-1/D4-2/D4-3/D4-5/D4-6): the REAL <see cref="NativePanelResizeController"/>, driven through its real
/// <c>INativePanelSizingClient</c> surface with a <see cref="PanelGeometryMode.GridAligned"/> grid host —
/// including the non-zero <c>WM_SIZING</c> origin offset and the px↔DIP round trip, the two pieces of
/// glue that <c>ResizeGeometryCalculatorApplyEquivalenceTests</c> explicitly does NOT cover.
/// </summary>
/// <remarks>
/// A separate file from <c>NativePanelResizeControllerTests</c> on purpose: that suite is the
/// <see cref="PanelGeometryMode.LegacyFreeform"/> golden baseline and P04 must be able to claim it is
/// untouched (plan §5.4).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativePanelResizeControllerGridAlignedTests
{
    private const double TitleBarHeight = ChildWindow.TitleBarHeight;
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    private const int OriginOffsetXPx = 17;
    private const int OriginOffsetYPx = -11;

    // Multiples of 8 DIP on every edge: both on the workspace grid AND lossless through the px↔DIP
    // round trip at scale 1.0/1.25/1.5 (8*1.25 = 10, 8*1.5 = 12 — both integers).
    private static readonly Rect StartBoundsDip = new(96d, 200d, 320d, 240d);

    /// <summary>Gets the eight <see cref="ResizeEdge"/> values, each paired with a total drag delta and repeated across every scale in <c>{1.0, 1.25, 1.5}</c>.</summary>
    public static TheoryData<ResizeEdge, double, double, double> EightEdgesAcrossDpiScales
    {
        get
        {
            var data = new TheoryData<ResizeEdge, double, double, double>();
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                data.Add(ResizeEdge.Left, -40d, 0d, scale);
                data.Add(ResizeEdge.Right, 40d, 0d, scale);
                data.Add(ResizeEdge.Top, 0d, -40d, scale);
                data.Add(ResizeEdge.Bottom, 0d, 40d, scale);
                data.Add(ResizeEdge.TopLeft, -40d, -40d, scale);
                data.Add(ResizeEdge.TopRight, 40d, -40d, scale);
                data.Add(ResizeEdge.BottomLeft, -40d, 40d, scale);
                data.Add(ResizeEdge.BottomRight, 40d, 40d, scale);
            }

            return data;
        }
    }

    // ── D4-6 for GridAligned: the real native controller's output equals the pure pipeline
    // (Apply -> SnapBounds), bit-for-bit, at every DPI scale and on all 8 edges. ─────────────────────

    /// <summary>Verifies that the real <see cref="NativePanelResizeController"/>, driven through a full native gesture on <paramref name="edge"/>, produces bit-for-bit the same result as the pure <c>Apply</c> → <c>SnapBounds</c> pipeline, and that the result lands on the workspace grid.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="totalDx">The total horizontal drag delta, in DIPs.</param>
    /// <param name="totalDy">The total vertical drag delta, in DIPs.</param>
    /// <param name="scale">The DPI scale factor applied to the gesture.</param>
    [Theory]
    [MemberData(nameof(EightEdgesAcrossDpiScales))]
    public void RealController_GridAligned_MatchesThePurePipeline_BitForBit(ResizeEdge edge, double totalDx, double totalDy, double scale)
    {
        var harness = CreateHarness(gridAligned: true, desktopScale: scale);
        var state = harness.GridHost.State;
        var constraints = ResizeConstraintsFactory.BuildConstraints(harness.Model, snapEnabled: true, TitleBarHeight, state);

        var actualScreenPx = harness.RunGesture(edge, totalDx, totalDy, scale);

        var expectedDip = ExpectedPipeline(StartBoundsDip, edge, totalDx, totalDy, constraints, state);
        Assert.Equal(NativeSizingCoordinateMapper.ToPx(expectedDip, scale), actualScreenPx);

        // And the result really is on the grid — the property the whole mode exists for.
        var resultDip = NativeSizingCoordinateMapper.ToDip(actualScreenPx, scale);
        Assert.Equal(0d, Math.IEEERemainder(resultDip.Left - state.Origin.X, U), 6);
        Assert.Equal(0d, Math.IEEERemainder(resultDip.Top - state.Origin.Y, U), 6);
    }

    // ── DoD D4-3 through the real controller: the opposite edge does not move. ───────────────────────

    /// <summary>Verifies that the real <see cref="NativePanelResizeController"/> keeps the edge opposite <paramref name="edge"/> fixed for the whole gesture.</summary>
    /// <param name="edge">The edge being dragged.</param>
    /// <param name="totalDx">The total horizontal drag delta, in DIPs.</param>
    /// <param name="totalDy">The total vertical drag delta, in DIPs.</param>
    /// <param name="scale">The DPI scale factor applied to the gesture.</param>
    [Theory]
    [MemberData(nameof(EightEdgesAcrossDpiScales))]
    public void RealController_GridAligned_KeepsTheOppositeEdgeFixed(ResizeEdge edge, double totalDx, double totalDy, double scale)
    {
        var harness = CreateHarness(gridAligned: true, desktopScale: scale);

        var resultDip = NativeSizingCoordinateMapper.ToDip(harness.RunGesture(edge, totalDx, totalDy, scale), scale);

        if (edge.AffectsWidth())
        {
            if (edge.AnchorsRight()) Assert.Equal(StartBoundsDip.Right, resultDip.Right, 6);
            else Assert.Equal(StartBoundsDip.Left, resultDip.Left, 6);
        }

        if (edge.AffectsHeight())
        {
            if (edge.AnchorsBottom()) Assert.Equal(StartBoundsDip.Bottom, resultDip.Bottom, 6);
            else Assert.Equal(StartBoundsDip.Top, resultDip.Top, 6);
        }
    }

    // ── P04-G6/DoD D4-5: a completed gesture persists the span through the host (i.e. through
    // PanelGeometryWriter), derived from the panel's FINAL bounds. ──────────────────────────────────

    /// <summary>Verifies that a completed gesture persists exactly one span, derived from the panel's final bounds, and round-trips back to the same bounds.</summary>
    [Fact]
    public void CompletedGesture_PersistsTheSpanDerivedFromTheFinalBounds()
    {
        var harness = CreateHarness(gridAligned: true);

        harness.RunGesture(ResizeEdge.Right, totalDx: 40d, totalDy: 0d, scale: 1.0);

        var commit = Assert.Single(harness.GridHost.Commits);
        Assert.Equal(harness.Bounds, commit.Bounds);
        Assert.Equal(WorkspaceLayoutPolicy.BoundsToSpans(harness.Bounds.Size, U, G), commit.Span);

        // Round trip: the persisted span reproduces exactly the bounds it was derived from.
        Assert.Equal(harness.Bounds.Size, WorkspaceLayoutPolicy.SpansToBounds(commit.Span, U, G));
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    // ── LegacyFreeform: no span is ever written (the model has none, and writing one would make
    // PanelStateMapper treat the panel as grid-aligned on the next load). ────────────────────────────

    /// <summary>Verifies that a completed gesture on a <see cref="PanelGeometryMode.LegacyFreeform"/> panel never writes a span.</summary>
    [Fact]
    public void CompletedGesture_LegacyPanel_PersistsNoSpan()
    {
        var harness = CreateHarness(gridAligned: false);

        harness.RunGesture(ResizeEdge.Right, totalDx: 40d, totalDy: 0d, scale: 1.0);

        Assert.Empty(harness.GridHost.Commits);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    // ── M-3 is preserved: a mid-loop end still DEFERS the whole persist step, spans included. ────────

    /// <summary>Verifies that aborting a gesture mid <c>WM_SIZING</c> loop defers the span write until the deferred persist callback runs, rather than writing it inline.</summary>
    [Fact]
    public void AbortedGesture_DefersTheSpanWriteUntilTheDeferredCallbackRuns()
    {
        var harness = CreateHarness(gridAligned: true);
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 40, startPx.Bottom));

        harness.Controller.SizeMoveAborted();

        Assert.Empty(harness.GridHost.Commits); // not written inline, mid WM_SIZING loop
        harness.RunDeferredPersistCalls();
        Assert.Single(harness.GridHost.Commits);
    }

    // ── P06 fix-cycle MAJOR-4 (adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): a completed gesture
    // that moved the panel's HEIGHT notifies the host exactly once; a width-only (Left/Right) gesture
    // never does. ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Verifies that a completed gesture on a height-affecting <paramref name="edge"/> notifies the host's height-resize-committed callback exactly once.</summary>
    /// <param name="edge">The height-affecting edge being dragged.</param>
    [Theory]
    [InlineData(ResizeEdge.Top)]
    [InlineData(ResizeEdge.Bottom)]
    [InlineData(ResizeEdge.TopLeft)]
    [InlineData(ResizeEdge.TopRight)]
    [InlineData(ResizeEdge.BottomLeft)]
    [InlineData(ResizeEdge.BottomRight)]
    public void CompletedGesture_HeightAffectingEdge_NotifiesHostExactlyOnce(ResizeEdge edge)
    {
        var harness = CreateHarness(gridAligned: true);
        var dx = edge.AffectsWidth() ? 40d : 0d;
        var dy = edge.AffectsHeight() ? 40d : 0d;

        harness.RunGesture(edge, dx, dy, scale: 1.0);

        Assert.Equal(1, harness.GridHost.NotifyHeightResizeCommittedCallCount);
    }

    /// <summary>Verifies that a completed gesture on a width-only <paramref name="edge"/> never notifies the host's height-resize-committed callback.</summary>
    /// <param name="edge">The width-only edge being dragged.</param>
    [Theory]
    [InlineData(ResizeEdge.Left)]
    [InlineData(ResizeEdge.Right)]
    public void CompletedGesture_WidthOnlyEdge_NeverNotifiesHost(ResizeEdge edge)
    {
        var harness = CreateHarness(gridAligned: true);

        harness.RunGesture(edge, totalDx: 40d, totalDy: 0d, scale: 1.0);

        Assert.Equal(0, harness.GridHost.NotifyHeightResizeCommittedCallCount);
    }

    /// <summary>Verifies that aborting a height-affecting gesture mid <c>WM_SIZING</c> loop defers the height-resize-committed notification until the deferred persist callback runs.</summary>
    [Fact]
    public void AbortedGesture_DefersTheHeightNotificationUntilTheDeferredCallbackRuns()
    {
        var harness = CreateHarness(gridAligned: true);
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Bottom, startPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Bottom, new NativeRectPx(startPx.Left, startPx.Top, startPx.Right, startPx.Bottom + 40));

        harness.Controller.SizeMoveAborted();

        Assert.Equal(0, harness.GridHost.NotifyHeightResizeCommittedCallCount); // not notified inline, mid WM_SIZING loop
        harness.RunDeferredPersistCalls();
        Assert.Equal(1, harness.GridHost.NotifyHeightResizeCommittedCallCount);
    }

    // ── DoD D4-2 / F-2 (§3.7): IconSize/ViewMode/IconLayoutPolicy changes must not re-snap a
    // GridAligned panel's width. ─────────────────────────────────────────────────────────────────────

    /// <summary>Verifies that <see cref="NativePanelResizeController.SnapWidthToGrid"/> is a no-op for a <see cref="PanelGeometryMode.GridAligned"/> panel.</summary>
    [Fact]
    public void SnapWidthToGrid_IsANoOp_ForAGridAlignedPanel()
    {
        var harness = CreateHarness(gridAligned: true, viewMode: PanelViewMode.Grid, snapEnabled: true);
        var before = harness.Bounds;

        harness.Controller.SnapWidthToGrid();

        Assert.Equal(before, harness.Bounds);
    }

    /// <summary>Verifies that <see cref="NativePanelResizeController.SnapWidthToGrid"/> still snaps the width for a <see cref="PanelGeometryMode.LegacyFreeform"/> panel.</summary>
    [Fact]
    public void SnapWidthToGrid_StillSnaps_ForALegacyFreeformPanel()
    {
        var harness = CreateHarness(gridAligned: false, viewMode: PanelViewMode.Grid, snapEnabled: true);
        var before = harness.Bounds;

        harness.Controller.SnapWidthToGrid();

        // Not vacuous: the legacy behaviour genuinely moves the width for this IconSize/start width.
        Assert.NotEqual(before.Width, harness.Bounds.Width);
    }

    /// <summary>
    /// Verifies that <see cref="NativePanelResizeController.SnapWidthToGrid"/> follows the same priority rule as
    /// <see cref="ResizeConstraintsFactory"/> (ADR-011 amendment 2026-09-24): an active <c>GridSize</c> (&gt; 1)
    /// beats <c>SnapResizeToIconGrid</c>, so a <see cref="PanelGeometryMode.LegacyFreeform"/> panel's width is
    /// left alone instead of being pulled off the GridSize lattice onto the icon grid.
    /// </summary>
    [Fact]
    public void SnapWidthToGrid_IsANoOp_ForALegacyFreeformPanel_WhenGridSizeIsActive()
    {
        var harness = CreateHarness(gridAligned: false, viewMode: PanelViewMode.Grid, snapEnabled: true, gridSize: 16);
        var before = harness.Bounds;

        harness.Controller.SnapWidthToGrid();

        Assert.Equal(before, harness.Bounds);
    }

    // ── DoD D4-1 through the REAL controller: 30 full native cycles, no stuck state, deterministic
    // geometry, one persist per cycle. ───────────────────────────────────────────────────────────────

    /// <summary>Verifies that 30 consecutive full native gesture cycles, across all 8 edges, leave the tracker idle and each persist exactly one commit, with no stuck state.</summary>
    [Fact]
    public void ThirtyNativeGestureCycles_LeaveNoStuckState()
    {
        var harness = CreateHarness(gridAligned: true);
        var edges = new[]
        {
            ResizeEdge.Left, ResizeEdge.Right, ResizeEdge.Top, ResizeEdge.Bottom,
            ResizeEdge.TopLeft, ResizeEdge.TopRight, ResizeEdge.BottomLeft, ResizeEdge.BottomRight,
        };

        for (var cycle = 0; cycle < 30; cycle++)
        {
            var edge = edges[cycle % edges.Length];
            harness.Bounds = StartBoundsDip; // each cycle starts from the same geometry
            harness.RunGesture(edge, edge.AffectsWidth() ? (edge.AnchorsRight() ? -16d : 16d) : 0d, edge.AffectsHeight() ? (edge.AnchorsBottom() ? -16d : 16d) : 0d, scale: 1.0);

            Assert.Equal(PanelInteractionState.Idle, harness.Tracker.State);
            Assert.Empty(harness.DeferredPersistCalls); // Completed persists synchronously (R-5)
        }

        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Exactly(30));
        Assert.Equal(30, harness.GridHost.Commits.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The pure pipeline P04-G2 specifies: <c>Apply</c> then <c>SnapBounds</c> (no obstacles here).</summary>
    private static Rect ExpectedPipeline(Rect start, ResizeEdge edge, double totalDx, double totalDy, ResizeConstraints constraints, PanelGridState state)
    {
        var raw = ResizeGeometryCalculator.Accumulate(new ResizeRawExtent(start.Width, start.Height), edge, new Vector(totalDx, totalDy));
        var applied = ResizeGeometryCalculator.Apply(start, edge, raw, start.Right, start.Bottom, constraints);

        return WorkspaceLayoutPolicy.SnapBounds(applied, state.Origin, edge, state.UnitSize, state.Gap, state.MinSpan);
    }

    private static ControllerHarness CreateHarness(
        bool gridAligned,
        PanelViewMode viewMode = PanelViewMode.Grid,
        bool snapEnabled = true,
        int gridSize = 0,
        Func<bool>? isFreeResizeModifierDown = null,
        double desktopScale = 1d)
    {
        var model = new ChildWindowModel
        {
            ViewMode = viewMode,
            IconSize = 48d,
            GeometryMode = gridAligned ? PanelGeometryMode.GridAligned : PanelGeometryMode.LegacyFreeform,
        };

        var host = new FakePanelSizingGridHost
        {
            State = gridAligned ? FakePanelSizingGridHost.GridAligned() : PanelGridState.Legacy,
        };

        return new ControllerHarness(StartBoundsDip, model, snapEnabled, host, gridSize, isFreeResizeModifierDown, desktopScale);
    }

    /// <summary>
    /// The same headless harness shape <c>NativePanelResizeControllerTests</c> uses (in-memory bounds
    /// instead of a <see cref="Window"/>), plus a <see cref="FakePanelSizingGridHost"/> and a
    /// <c>ProposeBounds</c> wrapper that writes the OS-committed geometry back into
    /// <see cref="Bounds"/> — what <c>NativePanelSizingService.WriteRect</c> + WPF's own HWND sync do in
    /// production, and what makes the span-persistence assertions above meaningful.
    /// </summary>
    private sealed class ControllerHarness
    {
        public readonly List<Action> DeferredPersistCalls = [];
        public readonly Mock<IAppSettings> AppSettings = new();
        public readonly Mock<IWindowManager> WindowManager = new();
        public readonly PanelInteractionTracker Tracker = new();
        public readonly ChildWindowModel Model;
        public readonly FakePanelSizingGridHost GridHost;
        public Rect Bounds;

        public ControllerHarness(
            Rect startBounds,
            ChildWindowModel model,
            bool snapEnabled,
            FakePanelSizingGridHost gridHost,
            int gridSize = 0,
            Func<bool>? isFreeResizeModifierDown = null,
            double desktopScale = 1d)
        {
            Bounds = startBounds;
            Model = model;
            GridHost = gridHost;
            AppSettings.SetupProperty(s => s.SnapResizeToIconGrid, snapEnabled);
            AppSettings.SetupProperty(s => s.GridSize, gridSize);

            Controller = new NativePanelResizeController(
                getBounds: () => Bounds,
                setWidth: w => Bounds = new Rect(Bounds.Left, Bounds.Top, w, Bounds.Height),
                setBounds: b => Bounds = b,
                scheduleDeferredPersist: DeferredPersistCalls.Add,
                AppSettings.Object,
                WindowManager.Object,
                getModel: () => Model,
                onResizeGestureStarting: () => { },
                clearDragObstacles: () => { },
                snapToGrid: () => { },
                titleBarHeight: TitleBarHeight,
                Mock.Of<IGestureDiagnostics>(),
                Tracker,
                Mock.Of<INativePanelSizingService>(),
                isResizeEnabled: () => true,
                desktopScale: FakeDesktopScale.Uniform(desktopScale).Object,
                gridHost: gridHost,
                isFreeResizeModifierDown: isFreeResizeModifierDown);
        }

        public NativePanelResizeController Controller { get; }

        /// <summary>Drives one full native gesture (ENTERSIZEMOVE → 2 × WM_SIZING → EXITSIZEMOVE) with a
        /// non-zero sizing-space origin offset, and returns the SCREEN-space result.</summary>
        public NativeRectPx RunGesture(ResizeEdge edge, double totalDx, double totalDy, double scale)
        {
            var nativeEdge = NativeSizingEdgeMap.FromResizeEdge(edge);
            var startScreenPx = NativeSizingCoordinateMapper.ToPx(Bounds, scale);
            var start = Bounds;

            Assert.True(Controller.SizeMoveEntered(nativeEdge, startScreenPx, scale));

            // First WM_SIZING: zero net movement, establishes the sizing-space offset (C-1).
            Commit(Controller.ProposeBounds(nativeEdge, ToSizingSpace(startScreenPx)), scale);

            var rawWidth = edge.AffectsWidth() ? start.Width + (edge.AnchorsRight() ? -totalDx : totalDx) : start.Width;
            var rawHeight = edge.AffectsHeight() ? start.Height + (edge.AnchorsBottom() ? -totalDy : totalDy) : start.Height;
            var targetDip = new Rect(
                edge.AnchorsRight() ? start.Right - rawWidth : start.Left,
                edge.AnchorsBottom() ? start.Bottom - rawHeight : start.Top,
                rawWidth,
                rawHeight);

            var returned = Controller.ProposeBounds(nativeEdge, ToSizingSpace(NativeSizingCoordinateMapper.ToPx(targetDip, scale)));
            var screenPx = ToScreenSpace(returned);
            Commit(returned, scale);

            Controller.SizeMoveExited(screenPx);

            return screenPx;
        }

        public void RunDeferredPersistCalls()
        {
            var pending = DeferredPersistCalls.ToArray();
            DeferredPersistCalls.Clear();
            foreach (var action in pending)
                action();
        }

        private static NativeRectPx ToSizingSpace(NativeRectPx screenPx) => new(
            screenPx.Left - OriginOffsetXPx, screenPx.Top - OriginOffsetYPx,
            screenPx.Right - OriginOffsetXPx, screenPx.Bottom - OriginOffsetYPx);

        private static NativeRectPx ToScreenSpace(NativeRectPx sizingSpacePx) => new(
            sizingSpacePx.Left + OriginOffsetXPx, sizingSpacePx.Top + OriginOffsetYPx,
            sizingSpacePx.Right + OriginOffsetXPx, sizingSpacePx.Bottom + OriginOffsetYPx);

        /// <summary>Mirrors WriteRect: the OS commits the rect the controller returned.</summary>
        private void Commit(NativeRectPx sizingSpacePx, double scale)
            => Bounds = NativeSizingCoordinateMapper.ToDip(ToScreenSpace(sizingSpacePx), scale);
    }
}
