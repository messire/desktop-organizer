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
/// T-CTRL-01..10 (docs/PLANS/phase-2-native-panel-sizing.md §5) for <see cref="NativePanelResizeController"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Headless reachability (UPDATED, code-quality-reviewer finding, Phase 2 refactoring §1.5/§5).</b>
/// <see cref="NativePanelResizeController"/>'s PUBLIC constructor still requires a real
/// <see cref="System.Windows.Window"/> — that has not changed, and <c>Views.ChildWindow.xaml.cs</c> (C5)
/// still calls it unmodified. What changed is that the three points where the class used to read/write
/// that <see cref="System.Windows.Window"/> directly (<c>EndResize</c>'s diagnostics-geometry read and
/// deferred-persist dispatch; <c>SnapWidthToGrid</c>'s width read/write) are now supplied via three plain
/// delegates, closed over the real <see cref="System.Windows.Window"/> by the public constructor but
/// closed over a local, in-memory <see cref="System.Windows.Rect"/> by an <c>internal</c> testability-seam
/// constructor instead (mirrors exactly how Phase 1 lifted <see cref="ResizeGeometryCalculator"/>/
/// <see cref="ResizeGestureSession"/> out from under <c>PanelResizeController</c>'s own <c>Window</c>
/// dependency). This test class constructs the REAL <see cref="NativePanelResizeController"/> via that
/// internal constructor (see <see cref="CreateHarness"/>) and drives it through its real
/// <see cref="INativePanelSizingClient"/> surface (<c>SizeMoveEntered</c>/<c>ProposeBounds</c>/
/// <c>SizeMoveExited</c>/<c>SizeMoveAborted</c>) — no STA thread, no fake <c>Window</c> subclass needed.
/// </para>
/// <para>
/// <c>NativeHitTestMapper</c>/<c>NativeSizingCoordinateMapper</c>/<c>NativeSizingOriginResolver</c>/
/// <c>NativeSizingEdgeMap</c> (commit C2) already have their own full suites under
/// <c>Unit/Interaction/Native/</c> and are not duplicated here; this class exercises them only as part of
/// driving the real controller end-to-end.
/// </para>
/// <para>
/// <b>Still MANUAL VERIFY:</b> whether <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority, System.Delegate)"/>
/// itself actually defers execution until after the native modal sizing loop's call stack unwinds is a
/// real-WPF-dispatcher property this test project cannot exercise headless (no <see cref="System.Windows.Threading.Dispatcher"/>
/// pump without an STA thread/real <see cref="System.Windows.Window"/>). What IS covered here is the
/// class's OWN contract at that seam — that <c>EndResize</c>'s <c>deferPersist</c> paths call the supplied
/// <c>scheduleDeferredPersist</c> delegate (never persisting synchronously/inline) exactly once, with the
/// correct callback — via a test delegate that records the call instead of running it immediately (see
/// <see cref="ControllerHarness.DeferredPersistCalls"/>).
/// </para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativePanelResizeControllerTests
{
    private const double TitleBarHeight = 32d;

    // ── Grid mode, snap enabled: BOTH axes snap; MinWidth uses the icon-grid-derived floor ──────────
    [Theory]
    [InlineData(48d)]
    [InlineData(16d)] // small IconSize — where Grid's derived floor and List's flat floor genuinely differ
    [InlineData(47.3d)] // fractional — sliders aren't tick-snapped
    public void BuildConstraints_GridModeSnapEnabled_SnapsBothAxes_AndUsesIconGridMinWidth(double iconSize)
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = iconSize };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight);

        Assert.Equal(GridMetrics.CellSize(iconSize), constraints.SnapCellWidth);
        Assert.Equal(GridMetrics.RowHeight(iconSize), constraints.SnapCellHeight);
        Assert.Equal(GridMetrics.MinPanelWidth(iconSize), constraints.MinWidth);
        Assert.Equal(GridMetrics.MinPanelSize, constraints.MinHeight);
        Assert.Equal(TitleBarHeight, constraints.TitleBarHeight);
        Assert.Equal(GridMetrics.FileListHPadding, constraints.HorizontalChrome);
        Assert.Equal(GridMetrics.FileListVPadding, constraints.VerticalChrome);
    }

    // ── List mode is free on BOTH axes even when snap is enabled — SnapResizeToIconGrid only applies
    // to Grid mode (Phase 1's OnResizeDragStarted branch decision, now shared by both controllers). ───
    [Fact]
    public void BuildConstraints_ListModeSnapEnabled_BothAxesStayFree_AndMinWidthIsTheFlatFloor()
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.List, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight);

        Assert.Equal(0d, constraints.SnapCellWidth);
        Assert.Equal(0d, constraints.SnapCellHeight);
        Assert.Equal(GridMetrics.MinPanelSize, constraints.MinWidth);
    }

    // ── Grid mode with snap DISABLED: both axes stay pixel-accurate, but MinWidth is still the
    // icon-grid-derived floor — MinWidth selection depends only on ViewMode, not on snapEnabled. ──────
    [Fact]
    public void BuildConstraints_GridModeSnapDisabled_BothAxesFree_ButMinWidthStillIconGridDerived()
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: false, TitleBarHeight);

        Assert.Equal(0d, constraints.SnapCellWidth);
        Assert.Equal(0d, constraints.SnapCellHeight);
        Assert.Equal(GridMetrics.MinPanelWidth(48d), constraints.MinWidth);
        Assert.NotEqual(GridMetrics.MinPanelSize, constraints.MinWidth); // sanity: the two floors genuinely differ at 48
    }

    // ── model == null (before ChildWindow.Initialize) is treated exactly like List mode. ─────────────
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildConstraints_NullModel_TreatedAsListMode_RegardlessOfSnapEnabled(bool snapEnabled)
    {
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled, TitleBarHeight);

        Assert.Equal(0d, constraints.SnapCellWidth);
        Assert.Equal(0d, constraints.SnapCellHeight);
        Assert.Equal(GridMetrics.MinPanelSize, constraints.MinWidth);
        Assert.Equal(GridMetrics.MinPanelSize, constraints.MinHeight);
    }

    // ── HorizontalChrome/VerticalChrome/MinHeight are constants, independent of mode/snap/IconSize —
    // pin that BuildConstraints never accidentally starts branching on them. ─────────────────────────
    [Theory]
    [InlineData(true, PanelViewMode.Grid)]
    [InlineData(false, PanelViewMode.Grid)]
    [InlineData(true, PanelViewMode.List)]
    [InlineData(false, PanelViewMode.List)]
    public void BuildConstraints_ChromeAndMinHeight_AreAlwaysTheSameConstants(bool snapEnabled, PanelViewMode viewMode)
    {
        var model = new ChildWindowModel { ViewMode = viewMode, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled, TitleBarHeight);

        Assert.Equal(GridMetrics.MinPanelSize, constraints.MinHeight);
        Assert.Equal(GridMetrics.FileListHPadding, constraints.HorizontalChrome);
        Assert.Equal(GridMetrics.FileListVPadding, constraints.VerticalChrome);
    }

    // ── TitleBarHeight is passed straight through, unmodified, for every mode/snap combination —
    // shared by both controllers, so a hard-coded literal here would silently diverge from
    // ChildWindow.TitleBarHeight the moment it changes. ──────────────────────────────────────────────
    [Fact]
    public void BuildConstraints_TitleBarHeight_IsPassedThroughUnchanged()
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, ChildWindow.TitleBarHeight);

        Assert.Equal(ChildWindow.TitleBarHeight, constraints.TitleBarHeight);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Real-controller harness (Task 1's testability seam) — every test below constructs the REAL
    // NativePanelResizeController via its internal delegate-based constructor (getBounds/setWidth/
    // scheduleDeferredPersist closed over a local, in-memory Rect instead of a Window) and drives it
    // through its real INativePanelSizingClient surface.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private const double IconSize = 48d;

    // Deliberately multiples of 4: at DPI scale 1.0/1.25/1.5, 4 * scale is always an integer, so every
    // px<->DIP round trip below (NativeSizingCoordinateMapper.ToPx/ToDip) is exactly lossless as long as
    // every derived quantity (totals, target rects) also stays a multiple of 4 — see BuildTargetRectDip.
    private static readonly Rect StartBoundsDip = new(100, 200, 320, 240);

    /// <summary>Wraps a REAL <see cref="NativePanelResizeController"/> plus the fakes/mocks/spies its
    /// testability-seam constructor needs, and the in-memory <see cref="Rect"/> its getBounds/setWidth
    /// delegates close over (standing in for a real <see cref="Window"/>).</summary>
    private sealed class ControllerHarness
    {
        public Rect Bounds;
        public readonly List<Action> DeferredPersistCalls = [];
        public readonly Mock<IAppSettings> AppSettings = new();
        public readonly Mock<IWindowManager> WindowManager = new();
        public readonly Mock<IGestureDiagnostics> Diagnostics = new();
        public readonly PanelInteractionTracker Tracker = new();
        public readonly ChildWindowModel Model;
        public int OnResizeGestureStartingCount;
        public int ClearDragObstaclesCount;
        public int SnapToGridCount;

        public ControllerHarness(Rect startBounds, ChildWindowModel model, bool snapEnabled, int gridSize = 0, Func<bool>? isFreeResizeModifierDown = null)
        {
            Bounds = startBounds;
            Model = model;
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
                onResizeGestureStarting: () => OnResizeGestureStartingCount++,
                clearDragObstacles: () => ClearDragObstaclesCount++,
                snapToGrid: () => SnapToGridCount++,
                titleBarHeight: TitleBarHeight,
                Diagnostics.Object,
                Tracker,
                Mock.Of<INativePanelSizingService>(),
                isResizeEnabled: () => true,
                gridHost: null,
                isFreeResizeModifierDown: isFreeResizeModifierDown);
        }

        public NativePanelResizeController Controller { get; }

        /// <summary>Runs every deferred-persist callback recorded so far, then clears the list — stands
        /// in for the real WPF dispatcher's queue actually draining (see the class remarks' "still MANUAL
        /// VERIFY" paragraph for what this does NOT prove).</summary>
        public void RunDeferredPersistCalls()
        {
            var pending = DeferredPersistCalls.ToArray();
            DeferredPersistCalls.Clear();
            foreach (var action in pending)
                action();
        }
    }

    private static ControllerHarness CreateHarness(
        bool gridSnap = false, double iconSize = IconSize, PanelViewMode viewMode = PanelViewMode.List,
        IconLayoutPolicy iconLayoutPolicy = IconLayoutPolicy.LegacyStretch, int gridSize = 0,
        Func<bool>? isFreeResizeModifierDown = null)
    {
        var model = new ChildWindowModel { ViewMode = viewMode, IconSize = iconSize, IconLayoutPolicy = iconLayoutPolicy };
        return new ControllerHarness(StartBoundsDip, model, gridSnap, gridSize, isFreeResizeModifierDown);
    }

    // ── T-CTRL-01: Entered → Sizing×N → Exited ⇒ exactly one SaveWindowsState. ──────────────────────
    [Fact]
    public void FullLifecycle_EnteredSizingExited_PersistsExactlyOnce()
    {
        var harness = CreateHarness();
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);

        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0));

        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 20, startPx.Bottom));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 40, startPx.Bottom));
        harness.Controller.SizeMoveExited(new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 40, startPx.Bottom));

        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
        Assert.Equal(1, harness.SnapToGridCount);
        Assert.Empty(harness.DeferredPersistCalls); // Completed persists synchronously, not deferred (R-5).
    }

    // ── T-CTRL-02: CaptureChanged ⇒ ResizeCompleted(endReason=CaptureLost); persist is DEFERRED (M-3),
    // never run inline. ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SizeMoveAborted_AfterAppliedDelta_RecordsCaptureLost_AndDefersPersistInsteadOfRunningInline()
    {
        var harness = CreateHarness();
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 20, startPx.Bottom));

        harness.Controller.SizeMoveAborted();

        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(
                harness.Model.Id, ResizeEdge.Right.ToString(), false, It.IsAny<Rect>(), nameof(ResizeEndReason.CaptureLost), null),
            Times.Once);

        // M-3: NOT persisted synchronously — the callback is scheduled, not yet run.
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
        Assert.Single(harness.DeferredPersistCalls);

        harness.RunDeferredPersistCalls();
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    // ── T-CTRL-03: TryBegin rejected (tracker already owns Dragging) ⇒ zero persist effects, but the
    // raw wire-level Entered/Exited diagnostics still pair up exactly once each (M-4). ─────────────────
    [Fact]
    public void SizeMoveEntered_TrackerAlreadyDragging_RejectsGesture_AndProducesNoPersistEffects()
    {
        var harness = CreateHarness();
        Assert.True(harness.Tracker.TryEnter(PanelInteractionState.Dragging));
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);

        var accepted = harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0);

        Assert.False(accepted);
        harness.Diagnostics.Verify(
            d => d.ResizeStarted(
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<Rect>(), It.IsAny<PanelViewMode>(), It.IsAny<double>(),
                It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string?>()),
            Times.Never);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
        Assert.Empty(harness.DeferredPersistCalls);

        // M-4: still paired even though the cycle was rejected.
        harness.Diagnostics.Verify(d => d.SizeMoveEntered(It.IsAny<Guid?>(), It.IsAny<long>(), It.IsAny<string>()), Times.Once);
        harness.Diagnostics.Verify(d => d.SizeMoveExited(It.IsAny<Guid?>(), It.IsAny<long>(), It.IsAny<Rect>()), Times.Once);

        // A WM_SIZING for a cycle that never actually began must be a complete no-op too.
        var proposed = new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 20, startPx.Bottom);
        Assert.Equal(proposed, harness.Controller.ProposeBounds(NativeSizingEdge.Right, proposed));
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
    }

    // ── Defensive branch: WM_ENTERSIZEMOVE with no HitTest-mapped edge (D-5 says this should never
    // actually happen in production) still self-heals into a properly-paired rejection. ────────────────
    [Fact]
    public void SizeMoveEntered_NoneEdge_RejectsDefensively_WithPairedDiagnostics()
    {
        var harness = CreateHarness();
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);

        var accepted = harness.Controller.SizeMoveEntered(NativeSizingEdge.None, startPx, dpiScale: 1.0);

        Assert.False(accepted);
        Assert.Equal(PanelInteractionState.Idle, harness.Tracker.State);
        harness.Diagnostics.Verify(
            d => d.SizeMoveEntered(It.IsAny<Guid?>(), It.IsAny<long>(), NativeSizingEdge.None.ToString()), Times.Once);
        harness.Diagnostics.Verify(d => d.SizeMoveExited(It.IsAny<Guid?>(), It.IsAny<long>(), It.IsAny<Rect>()), Times.Once);
    }

    // ── T-CTRL-05: WMSZ_* disagrees with the edge WM_ENTERSIZEMOVE recorded ⇒ SourceChanged, and the
    // system's own proposed rect is returned COMPLETELY untouched. ─────────────────────────────────────
    [Fact]
    public void ProposeBounds_EdgeDisagreesWithEnteredEdge_EndsAsSourceChanged_AndReturnsProposedUntouched()
    {
        var harness = CreateHarness();
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0));

        var proposed = new NativeRectPx(startPx.Left - 20, startPx.Top, startPx.Right, startPx.Bottom);
        var returned = harness.Controller.ProposeBounds(NativeSizingEdge.Left, proposed);

        Assert.Equal(proposed, returned);
        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(
                harness.Model.Id, ResizeEdge.Right.ToString(), false, It.IsAny<Rect>(), nameof(ResizeEndReason.SourceChanged), null),
            Times.Once);

        // No delta was ever applied (AppliedDeltaCount stayed 0) -> ShouldPersist false -> nothing scheduled.
        Assert.Empty(harness.DeferredPersistCalls);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
    }

    // ── T-CTRL-06: SizeMoveEntered/SizeMoveExited (raw wire diagnostics) fire exactly once each across
    // a full accepted gesture. ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void FullLifecycle_RecordsSizeMoveEnteredAndExited_ExactlyOnceEach()
    {
        var harness = CreateHarness();
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);
        harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0);
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 20, startPx.Bottom));
        harness.Controller.SizeMoveExited(startPx);

        harness.Diagnostics.Verify(d => d.SizeMoveEntered(It.IsAny<Guid?>(), It.IsAny<long>(), It.IsAny<string>()), Times.Once);
        harness.Diagnostics.Verify(d => d.SizeMoveExited(It.IsAny<Guid?>(), It.IsAny<long>(), It.IsAny<Rect>()), Times.Once);
    }

    // ── T-CTRL-04 ("exception in ProposeBounds ⇒ Failed"): documented as unreachable via any current
    // dependency seam rather than faked — see this method's own remarks. The one legitimately-testable
    // half of this scenario (a failure in ProposeBounds' SEPARATE, later, log-only ResizeDelta
    // diagnostics call, m-2) is covered below instead. ──────────────────────────────────────────────
    /// <remarks>
    /// <para>
    /// <b>MANUAL VERIFY / structurally unreachable, not a gap in test-writing effort.</b>
    /// <c>ProposeBounds</c>' geometry <c>try</c>/<c>catch</c> (the one mapping an exception to
    /// <see cref="ResizeEndReason.Failed"/>) calls ONLY: <see cref="NativeSizingOriginResolver.Resolve"/>,
    /// <see cref="NativeSizingCoordinateMapper.ToDip(NativeRectPx, double)"/>, the pure
    /// <see cref="ResizeEdgeParser"/> extension methods, and <see cref="ResizeGestureSession.TryUpdate"/>
    /// (which calls <see cref="ResizeGeometryCalculator.Accumulate"/>/<see cref="ResizeGeometryCalculator.Apply"/>
    /// internally). Every one of these is a static/pure function over value types with NO injectable
    /// dependency — none of this class's three seam delegates
    /// (<c>getBounds</c>/<c>setWidth</c>/<c>scheduleDeferredPersist</c>), <c>_appSettings</c>,
    /// <c>_getModel</c>, or any other constructor-injected dependency is read anywhere inside that
    /// <c>try</c> block (confirmed by inspection of every line between the method's <c>try</c> and its
    /// <c>catch</c>). <see cref="ResizeGeometryCalculator.Apply"/> is additionally defensively guarded
    /// against every non-finite input (H3), and this project's default (unchecked) arithmetic never
    /// throws on integer overflow — so there is no reachable input, however extreme, that makes this
    /// specific <c>try</c> block throw. Concretely: reaching it would require either (a) adding a
    /// test-only throw-hook to production code — the "adding test-only methods to production classes"
    /// anti-pattern the test-driven-development skill explicitly warns against — or (b) deliberately
    /// tripping <see cref="ResizeGeometryCalculator.Apply"/>'s <c>Debug.Assert</c>, which
    /// <c>PanelResizeController.OnResizeDragDelta</c>'s own remarks document as process-terminating on
    /// .NET 8 with no debugger attached (unsafe to do inside a test run). Neither is an honest test.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProposeBounds_ResizeDeltaDiagnosticsThrows_DoesNotFailTheGesture()
    {
        var harness = CreateHarness();
        var startPx = NativeSizingCoordinateMapper.ToPx(StartBoundsDip, 1.0);
        harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, startPx, dpiScale: 1.0);
        harness.Diagnostics
            .Setup(d => d.ResizeDelta(
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Rect>()))
            .Throws(new InvalidOperationException("boom"));

        var proposed = new NativeRectPx(startPx.Left, startPx.Top, startPx.Right + 20, startPx.Bottom);
        var returned = harness.Controller.ProposeBounds(NativeSizingEdge.Right, proposed);

        // The geometry update itself was NOT rolled back / discarded by the diagnostics failure (m-2):
        // a real clamp/snap-free width change was still computed and returned, proving the gesture
        // stayed alive through the throw instead of silently reverting to the start rect.
        Assert.NotEqual(startPx, returned);

        harness.Controller.SizeMoveExited(returned);
        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(
                It.IsAny<Guid?>(), It.IsAny<string>(), false, It.IsAny<Rect>(), nameof(ResizeEndReason.Completed), null),
            Times.Once);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Honest golden equivalence (replaces the tautological T-EQ-* suite — see
    // ResizeGeometryCalculatorApplyEquivalenceTests' remarks for why the old suite never actually
    // exercised this controller). Every test below drives the REAL
    // NativePanelResizeController.ProposeBounds, through a NON-ZERO WM_SIZING origin offset (the exact
    // R-3/C-1 coordinate-space scenario fixed by commit 646e04c — see NativeSizingOriginResolverTests
    // for the same offset-simulation technique), and compares the result against the legacy
    // Accumulate-chain + Apply path. A regression of C-1 (offset applied on the way in but never undone
    // on the way out) would fail every test below.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    private const int OriginOffsetXPx = 17;
    private const int OriginOffsetYPx = -11;

    /// <summary>Builds the UNCLAMPED candidate rect (DIP) a real <c>WM_SIZING</c> would report for a raw
    /// mouse drag of <paramref name="totalDx"/>/<paramref name="totalDy"/> from <paramref name="start"/>
    /// on <paramref name="edge"/> — the anchor/sign convention mirrors
    /// <see cref="ResizeGeometryCalculator.Accumulate"/>'s own body (deliberately NOT calling it — see
    /// <c>ResizeGeometryCalculatorApplyEquivalenceTests</c>' remarks on why that would prove nothing).</summary>
    private static Rect BuildTargetRectDip(Rect start, ResizeEdge edge, double totalDx, double totalDy)
    {
        var rawWidth = edge.AffectsWidth() ? start.Width + (edge.AnchorsRight() ? -totalDx : totalDx) : start.Width;
        var rawHeight = edge.AffectsHeight() ? start.Height + (edge.AnchorsBottom() ? -totalDy : totalDy) : start.Height;

        double left = start.Left, top = start.Top;
        if (edge.AnchorsRight()) left = start.Right - rawWidth;
        if (edge.AnchorsBottom()) top = start.Bottom - rawHeight;

        return new Rect(left, top, rawWidth, rawHeight);
    }

    private static NativeRectPx ShiftIntoSizingSpace(NativeRectPx screenPx) => new(
        screenPx.Left - OriginOffsetXPx, screenPx.Top - OriginOffsetYPx,
        screenPx.Right - OriginOffsetXPx, screenPx.Bottom - OriginOffsetYPx);

    private static NativeRectPx ShiftIntoScreenSpace(NativeRectPx sizingSpacePx) => new(
        sizingSpacePx.Left + OriginOffsetXPx, sizingSpacePx.Top + OriginOffsetYPx,
        sizingSpacePx.Right + OriginOffsetXPx, sizingSpacePx.Bottom + OriginOffsetYPx);

    /// <summary>Drives ONE full gesture through the REAL controller with a non-zero origin offset
    /// (simulating R-3's WorkerW-reparented <c>WM_SIZING</c> coordinate space, self-calibrated from the
    /// gesture's FIRST <c>WM_SIZING</c> message — see <see cref="NativeSizingOriginResolver"/>) and
    /// returns the SCREEN-space <see cref="NativeRectPx"/> the controller actually computed for the
    /// total (<paramref name="totalDx"/>, <paramref name="totalDy"/>) movement.</summary>
    private static NativeRectPx RunGestureWithOriginOffset(
        NativePanelResizeController controller, Rect startDip, ResizeEdge edge, double totalDx, double totalDy, double scale)
    {
        var nativeEdge = NativeSizingEdgeMap.FromResizeEdge(edge);
        var windowRectScreenPx = NativeSizingCoordinateMapper.ToPx(startDip, scale);

        Assert.True(controller.SizeMoveEntered(nativeEdge, windowRectScreenPx, scale));

        // First WM_SIZING of the gesture: reported in a coordinate space offset from GetWindowRect by
        // (OriginOffsetXPx, OriginOffsetYPx) — establishes _originOffsetPx (C-1's exact mechanism), at
        // (effectively) zero net movement so far, exactly like the very first WM_SIZING right after
        // WM_ENTERSIZEMOVE in production.
        controller.ProposeBounds(nativeEdge, ShiftIntoSizingSpace(windowRectScreenPx));

        // Second WM_SIZING: the full total movement (D-4 — one absolute message carries it all),
        // reported in the SAME offset coordinate space the first message established.
        var targetDip = BuildTargetRectDip(startDip, edge, totalDx, totalDy);
        var targetScreenPx = NativeSizingCoordinateMapper.ToPx(targetDip, scale);
        var returnedSizingSpacePx = controller.ProposeBounds(nativeEdge, ShiftIntoSizingSpace(targetScreenPx));

        controller.SizeMoveExited(ShiftIntoScreenSpace(returnedSizingSpacePx));

        return ShiftIntoScreenSpace(returnedSizingSpacePx);
    }

    private static Rect LegacyApply(Rect start, ResizeEdge edge, double totalDx, double totalDy, ResizeConstraints constraints)
    {
        var raw = new ResizeRawExtent(start.Width, start.Height);

        // 4-step accumulate chain — the exact shape PanelResizeController.OnResizeDragDelta feeds
        // ResizeGeometryCalculator.Accumulate, one Thumb.DragDelta at a time.
        double[] dx = [totalDx * 0.25, totalDx * 0.25, totalDx * 0.25, totalDx * 0.25];
        double[] dy = [totalDy * 0.25, totalDy * 0.25, totalDy * 0.25, totalDy * 0.25];
        for (var i = 0; i < dx.Length; i++)
            raw = ResizeGeometryCalculator.Accumulate(raw, edge, new Vector(dx[i], dy[i]));

        return ResizeGeometryCalculator.Apply(start, edge, raw, start.Right, start.Bottom, constraints);
    }

    // ── Grid+snap, scale 1.0, all 8 edges — the same magnitudes as the old (tautological) T-EQ table,
    // but now driven through the real controller with a non-zero origin offset. ────────────────────────
    [Theory]
    [InlineData(ResizeEdge.Left, 84d, 0d)]
    [InlineData(ResizeEdge.Top, 0d, 92d)]
    [InlineData(ResizeEdge.Right, 84d, 0d)]
    [InlineData(ResizeEdge.Bottom, 0d, 92d)]
    [InlineData(ResizeEdge.TopLeft, 84d, 92d)]
    [InlineData(ResizeEdge.TopRight, 84d, 92d)]
    [InlineData(ResizeEdge.BottomLeft, 84d, 92d)]
    [InlineData(ResizeEdge.BottomRight, 84d, 92d)]
    public void RealController_GridSnapMode_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset(
        ResizeEdge edge, double totalDx, double totalDy)
    {
        var harness = CreateHarness(gridSnap: true, iconSize: IconSize, viewMode: PanelViewMode.Grid);
        var constraints = ResizeConstraintsFactory.BuildConstraints(harness.Model, snapEnabled: true, TitleBarHeight);

        var actualScreenPx = RunGestureWithOriginOffset(harness.Controller, StartBoundsDip, edge, totalDx, totalDy, scale: 1.0);

        var expectedDip = LegacyApply(StartBoundsDip, edge, totalDx, totalDy, constraints);
        var expectedScreenPx = NativeSizingCoordinateMapper.ToPx(expectedDip, 1.0);

        Assert.Equal(expectedScreenPx, actualScreenPx);
        Assert.Equal(expectedDip, NativeSizingCoordinateMapper.ToDip(actualScreenPx, 1.0));
    }

    // ── MINOR-2 (fix-cycle 2): the same 8-edge Grid+snap coverage as the test above, but with
    // IconLayoutPolicy.FixedGap instead of the default LegacyStretch — this "Honest golden equivalence"
    // region never exercised a FixedGap panel before this fix-cycle, even though
    // ResizeConstraintsFactory.BuildConstraints (which the test above already drives) has read
    // model.IconLayoutPolicy since P05. Proves the REAL native controller and the REAL legacy
    // Accumulate-chain path still agree bit-for-bit — including the non-zero WM_SIZING origin offset —
    // once HorizontalChrome/MinWidth both reserve M-2's extra scrollbar width for FixedGap, closing the
    // "two SnapWidthToGrid copies could silently disagree for FixedGap" gap the fix-cycle 2 review named. ──
    [Theory]
    [InlineData(ResizeEdge.Left, 84d, 0d)]
    [InlineData(ResizeEdge.Top, 0d, 92d)]
    [InlineData(ResizeEdge.Right, 84d, 0d)]
    [InlineData(ResizeEdge.Bottom, 0d, 92d)]
    [InlineData(ResizeEdge.TopLeft, 84d, 92d)]
    [InlineData(ResizeEdge.TopRight, 84d, 92d)]
    [InlineData(ResizeEdge.BottomLeft, 84d, 92d)]
    [InlineData(ResizeEdge.BottomRight, 84d, 92d)]
    public void RealController_GridSnapMode_FixedGapPolicy_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset(
        ResizeEdge edge, double totalDx, double totalDy)
    {
        var harness = CreateHarness(gridSnap: true, iconSize: IconSize, viewMode: PanelViewMode.Grid, iconLayoutPolicy: IconLayoutPolicy.FixedGap);
        var constraints = ResizeConstraintsFactory.BuildConstraints(harness.Model, snapEnabled: true, TitleBarHeight);

        // Sanity: this really is exercising the FixedGap-specific chrome/floor, not accidentally
        // identical to the LegacyStretch shape the test above already covers.
        var legacyConstraints = ResizeConstraintsFactory.BuildConstraints(
            new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = IconSize, IconLayoutPolicy = IconLayoutPolicy.LegacyStretch },
            snapEnabled: true,
            TitleBarHeight);
        Assert.NotEqual(legacyConstraints.HorizontalChrome, constraints.HorizontalChrome);

        var actualScreenPx = RunGestureWithOriginOffset(harness.Controller, StartBoundsDip, edge, totalDx, totalDy, scale: 1.0);

        var expectedDip = LegacyApply(StartBoundsDip, edge, totalDx, totalDy, constraints);
        var expectedScreenPx = NativeSizingCoordinateMapper.ToPx(expectedDip, 1.0);

        Assert.Equal(expectedScreenPx, actualScreenPx);
        Assert.Equal(expectedDip, NativeSizingCoordinateMapper.ToDip(actualScreenPx, 1.0));
    }

    // ── List+free, all 8 edges, at DPI scale 1.0/1.25/1.5 — proves the px→DIP→px round trip at non-1.0
    // scale introduces no drift relative to the legacy path (StartBoundsDip/deltas are multiples of 4,
    // so this round trip is exactly lossless at every scale used here — see StartBoundsDip's remark). ──
    [Theory]
    [InlineData(ResizeEdge.Left, 40d, 0d, 1.0)]
    [InlineData(ResizeEdge.Top, 0d, 24d, 1.0)]
    [InlineData(ResizeEdge.Right, 40d, 0d, 1.0)]
    [InlineData(ResizeEdge.Bottom, 0d, 24d, 1.0)]
    [InlineData(ResizeEdge.TopLeft, 40d, 24d, 1.0)]
    [InlineData(ResizeEdge.TopRight, 40d, 24d, 1.0)]
    [InlineData(ResizeEdge.BottomLeft, 40d, 24d, 1.0)]
    [InlineData(ResizeEdge.BottomRight, 40d, 24d, 1.0)]
    [InlineData(ResizeEdge.Left, 40d, 0d, 1.25)]
    [InlineData(ResizeEdge.Top, 0d, 24d, 1.25)]
    [InlineData(ResizeEdge.Right, 40d, 0d, 1.25)]
    [InlineData(ResizeEdge.Bottom, 0d, 24d, 1.25)]
    [InlineData(ResizeEdge.TopLeft, 40d, 24d, 1.25)]
    [InlineData(ResizeEdge.TopRight, 40d, 24d, 1.25)]
    [InlineData(ResizeEdge.BottomLeft, 40d, 24d, 1.25)]
    [InlineData(ResizeEdge.BottomRight, 40d, 24d, 1.25)]
    [InlineData(ResizeEdge.Left, 40d, 0d, 1.5)]
    [InlineData(ResizeEdge.Top, 0d, 24d, 1.5)]
    [InlineData(ResizeEdge.Right, 40d, 0d, 1.5)]
    [InlineData(ResizeEdge.Bottom, 0d, 24d, 1.5)]
    [InlineData(ResizeEdge.TopLeft, 40d, 24d, 1.5)]
    [InlineData(ResizeEdge.TopRight, 40d, 24d, 1.5)]
    [InlineData(ResizeEdge.BottomLeft, 40d, 24d, 1.5)]
    [InlineData(ResizeEdge.BottomRight, 40d, 24d, 1.5)]
    public void RealController_ListFreeMode_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset_AcrossDpiScales(
        ResizeEdge edge, double totalDx, double totalDy, double scale)
    {
        var harness = CreateHarness(gridSnap: false, viewMode: PanelViewMode.List);
        var constraints = ResizeConstraintsFactory.BuildConstraints(harness.Model, snapEnabled: false, TitleBarHeight);

        var actualScreenPx = RunGestureWithOriginOffset(harness.Controller, StartBoundsDip, edge, totalDx, totalDy, scale);

        var expectedDip = LegacyApply(StartBoundsDip, edge, totalDx, totalDy, constraints);
        var expectedScreenPx = NativeSizingCoordinateMapper.ToPx(expectedDip, scale);

        // Px-level equality: the actual wire contract WriteRect/GetWindowRect round-trip through.
        Assert.Equal(expectedScreenPx, actualScreenPx);

        // DIP-level bit-for-bit too — proves the px round trip introduced no drift at all, not just
        // that both sides happened to round to the same px.
        Assert.Equal(expectedDip, NativeSizingCoordinateMapper.ToDip(actualScreenPx, scale));
    }

    // ── ADR-011 Amendment 2026-09-24 (resize extension): GridSize=48, List+free, all 8 edges — the
    // native WM_SIZING path and the legacy Accumulate-chain + Apply path must still agree bit-for-bit
    // once IAppSettings.GridSize (not just the icon-grid snap) is driving per-frame quantisation. Reuses
    // the exact harness/LegacyApply helpers every other "Honest golden equivalence" case above uses —
    // the only difference is CreateHarness's new gridSize argument and passing it through to
    // BuildConstraints, exactly the same two call sites production code itself changed. ─────────────────
    [Theory]
    [InlineData(ResizeEdge.Left, 40d, 0d)]
    [InlineData(ResizeEdge.Top, 0d, 24d)]
    [InlineData(ResizeEdge.Right, 40d, 0d)]
    [InlineData(ResizeEdge.Bottom, 0d, 24d)]
    [InlineData(ResizeEdge.TopLeft, 40d, 24d)]
    [InlineData(ResizeEdge.TopRight, 40d, 24d)]
    [InlineData(ResizeEdge.BottomLeft, 40d, 24d)]
    [InlineData(ResizeEdge.BottomRight, 40d, 24d)]
    public void RealController_GridSizeMode_MatchesLegacyPath_BitForBit_WithNonZeroOriginOffset(
        ResizeEdge edge, double totalDx, double totalDy)
    {
        var harness = CreateHarness(gridSnap: false, viewMode: PanelViewMode.List, gridSize: 48);
        var constraints = ResizeConstraintsFactory.BuildConstraints(harness.Model, snapEnabled: false, TitleBarHeight, gridSize: 48);
        Assert.Equal(48d, constraints.SizeGridStep); // sanity: this really is exercising the new branch

        var actualScreenPx = RunGestureWithOriginOffset(harness.Controller, StartBoundsDip, edge, totalDx, totalDy, scale: 1.0);

        var expectedDip = LegacyApply(StartBoundsDip, edge, totalDx, totalDy, constraints);
        var expectedScreenPx = NativeSizingCoordinateMapper.ToPx(expectedDip, 1.0);

        Assert.Equal(expectedScreenPx, actualScreenPx);
        Assert.Equal(expectedDip, NativeSizingCoordinateMapper.ToDip(actualScreenPx, 1.0));

        // Not vacuous: prove GridSize quantisation was actually exercised, on whichever axis this edge affects.
        if (edge.AffectsWidth()) Assert.Equal(0d, expectedDip.Width % 48d);
        if (edge.AffectsHeight()) Assert.Equal(0d, expectedDip.Height % 48d);
    }

    // ── R-4 scenario through the real controller: the min-size clamp is hit, with a non-zero origin
    // offset in play. ────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(ResizeEdge.Left, 250d, 0d)]
    [InlineData(ResizeEdge.Right, -250d, 0d)]
    [InlineData(ResizeEdge.Top, 0d, 200d)]
    [InlineData(ResizeEdge.Bottom, 0d, -200d)]
    public void RealController_ListFreeMode_MatchesLegacyPath_WhenMinSizeClampIsHit_WithNonZeroOriginOffset(
        ResizeEdge edge, double totalDx, double totalDy)
    {
        var harness = CreateHarness(gridSnap: false, viewMode: PanelViewMode.List);
        var constraints = ResizeConstraintsFactory.BuildConstraints(harness.Model, snapEnabled: false, TitleBarHeight);

        var actualScreenPx = RunGestureWithOriginOffset(harness.Controller, StartBoundsDip, edge, totalDx, totalDy, scale: 1.0);

        var expectedDip = LegacyApply(StartBoundsDip, edge, totalDx, totalDy, constraints);
        var expectedScreenPx = NativeSizingCoordinateMapper.ToPx(expectedDip, 1.0);

        Assert.Equal(expectedScreenPx, actualScreenPx);

        // Not vacuous: prove the clamp was actually reached on the dragged axis.
        if (edge.AffectsWidth())
            Assert.Equal(GridMetrics.MinPanelSize, expectedDip.Width);
        else
            Assert.Equal(GridMetrics.MinPanelSize, expectedDip.Height);
    }
}
