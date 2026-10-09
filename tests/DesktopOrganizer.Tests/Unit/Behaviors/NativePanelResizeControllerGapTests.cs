using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Interaction.Native;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// ADR-022 phase 0-B (docs/PLANS/native-resize-single-path.md §2, §5.1): the native-path gaps G-1, G-2 and
/// G-4 at the controller level — the REAL <see cref="NativePanelResizeController"/>, driven through its
/// <see cref="INativePanelSizingClient"/> surface with mocked <see cref="INativePanelSizingService"/>,
/// <see cref="IDesktopScaleProvider"/> and <see cref="IPanelSizingGridHost"/>.
/// </summary>
/// <remarks>
/// A separate file so the golden suites (<c>NativePanelResizeControllerTests</c>,
/// <c>ResizeGeometryCalculatorApplyEquivalenceTests</c>) stay untouched. G-5/G-6 and the service half of G-2
/// live in <c>NativePanelSizingServiceTests</c>.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativePanelResizeControllerGapTests
{
    private const double TitleBarHeight = 32d;
    private const int GridSize = 48;
    private static readonly IntPtr PanelHwnd = new(0x4242);

    // Multiples of 4 DIP (lossless px↔DIP at 1.0), same as NativePanelResizeControllerTests.
    private static readonly Rect StartBoundsDip = new(100, 200, 320, 240);
    private static readonly NativeRectPx StartPx = new(100, 200, 420, 440);

    // ══ G-1: WM_DPICHANGED mid-gesture keeps the gesture ═══════════════════════════════════════════

    /// <summary>
    /// G-1: a DPI change in the middle of a gesture does not end it — the tracker stays
    /// <see cref="PanelInteractionState.Resizing"/>, the next <c>WM_SIZING</c> is still snapped (GridSize
    /// lattice) exactly as without the DPI change, and <c>WM_EXITSIZEMOVE</c> persists exactly once.
    /// </summary>
    [Fact]
    public void DpiChanged_MidGesture_KeepsSessionActive_NextProposeBoundsStillSnapped_ExitPersistsOnce()
    {
        var harness = new Harness(gridSize: GridSize);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, StartPx);

        harness.Controller.DpiChanged(1.5, new NativeRectPx(150, 300, 630, 660));

        Assert.Equal(PanelInteractionState.Resizing, harness.Tracker.State);
        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Rect>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);

        // +40 px → raw width 360 DIP, quantised to the 48-DIP lattice (336) — i.e. NOT the system's rect.
        var proposed = new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right + 40, StartPx.Bottom);
        var returned = harness.Controller.ProposeBounds(NativeSizingEdge.Right, proposed);

        Assert.NotEqual(proposed, returned);
        Assert.Equal(0, returned.Width % GridSize);
        Assert.Equal(RunWithoutDpiChange(proposed), returned);

        harness.Controller.SizeMoveExited(returned);

        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
        Assert.Empty(harness.DeferredPersistCalls);
        Assert.Equal(PanelInteractionState.Idle, harness.Tracker.State);
        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(It.IsAny<Guid?>(), ResizeEdge.Right.ToString(), false, It.IsAny<Rect>(), nameof(ResizeEndReason.Completed), null),
            Times.Once);
    }

    /// <summary>
    /// G-1: every px↔DIP conversion of one gesture goes through the desktop-scale snapshot taken on
    /// <c>SizeMoveEntered</c> — a snapshot rebuilt mid-gesture (here: 1.0 → 2.0) does not change the
    /// geometry the remaining <c>WM_SIZING</c> messages produce.
    /// </summary>
    [Fact]
    public void DpiChanged_MidGesture_UsesGestureStartScaleSnapshot()
    {
        var harness = new Harness(gridSize: GridSize);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, StartPx);

        // The provider's live snapshot changes (e.g. ChildWindow.OnDpiChanged → Refresh) mid-gesture.
        harness.CurrentScale = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 2.0, 2.0, []);
        harness.Controller.DpiChanged(2.0, new NativeRectPx(200, 400, 840, 880));

        var proposed = new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right + 40, StartPx.Bottom);
        var returned = harness.Controller.ProposeBounds(NativeSizingEdge.Right, proposed);

        Assert.Equal(RunWithoutDpiChange(proposed), returned);
    }

    // ══ G-2: AbortResize cancels the system loop ═══════════════════════════════════════════════════

    /// <summary>
    /// G-2: <c>AbortResize</c> during an active cycle asks the sizing service to cancel the system loop
    /// for this panel's HWND, ends the gesture and DEFERS the persist (the loop is still on the stack).
    /// </summary>
    [Fact]
    public void AbortResize_DuringActiveCycle_RequestsLoopCancel_AndDefersPersist()
    {
        var harness = new Harness();
        harness.Controller.AttachTo(PanelHwnd);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right + 20, StartPx.Bottom));

        harness.Controller.AbortResize(ResizeEndReason.SourceChanged);

        harness.SizingService.Verify(s => s.CancelActiveSizing(PanelHwnd), Times.Once);
        Assert.Equal(PanelInteractionState.Idle, harness.Tracker.State);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
        Assert.Single(harness.DeferredPersistCalls);

        harness.RunDeferredPersistCalls();
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    /// <summary>
    /// G-2: teardown reasons (<see cref="ResizeEndReason.Closing"/>/<see cref="ResizeEndReason.Unloaded"/>)
    /// also cancel the loop, but persist synchronously — a deferred callback would outlive the window.
    /// </summary>
    /// <param name="reason">The teardown reason.</param>
    [Theory]
    [InlineData(ResizeEndReason.Closing)]
    [InlineData(ResizeEndReason.Unloaded)]
    public void AbortResize_Teardown_CancelsLoop_AndPersistsSynchronously(ResizeEndReason reason)
    {
        var harness = new Harness();
        harness.Controller.AttachTo(PanelHwnd);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right + 20, StartPx.Bottom));

        harness.Controller.AbortResize(reason);

        harness.SizingService.Verify(s => s.CancelActiveSizing(PanelHwnd), Times.Once);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
        Assert.Empty(harness.DeferredPersistCalls);
    }

    /// <summary>
    /// G-2: outside a gesture <c>AbortResize</c> stays a no-op — no loop to cancel, nothing persisted.
    /// </summary>
    [Fact]
    public void AbortResize_WithoutActiveGesture_DoesNotTouchTheSizingService()
    {
        var harness = new Harness();
        harness.Controller.AttachTo(PanelHwnd);

        harness.Controller.AbortResize(ResizeEndReason.SourceChanged);

        harness.SizingService.Verify(s => s.CancelActiveSizing(It.IsAny<IntPtr>()), Times.Never);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
        Assert.Empty(harness.DeferredPersistCalls);
    }

    /// <summary>
    /// Review cycle 1 (S3): <c>AbortResize</c> cancelled the system loop and <c>DefWindowProc</c> put the window
    /// back to the gesture's starting rectangle — the deferred persist is skipped entirely (no
    /// <c>SaveWindowsState</c>, no grid snap, no height-commit notification, so an AutoContent panel is not
    /// demoted to Manual), exactly like the native <c>ESC</c> of G-4.
    /// </summary>
    [Fact]
    public void AbortResize_WindowBackAtStartRect_DeferredPersistSkipped_NoAutoContentDemotion()
    {
        var harness = new Harness();
        harness.Controller.AttachTo(PanelHwnd);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Bottom, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Bottom, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right, StartPx.Bottom + 40));

        harness.Controller.AbortResize(ResizeEndReason.SourceChanged);
        Assert.Single(harness.DeferredPersistCalls);

        var restored = StartPx;
        harness.SizingService.Setup(s => s.TryGetWindowRect(PanelHwnd, out restored)).Returns(true);
        harness.RunDeferredPersistCalls();

        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
        Assert.Equal(0, harness.SnapToGridCount);
        harness.GridHost.Verify(h => h.NotifyHeightResizeCommitted(), Times.Never);
        harness.GridHost.Verify(h => h.CommitGridGeometry(It.IsAny<Rect>(), It.IsAny<GridSpan>()), Times.Never);
    }

    /// <summary>
    /// Review cycle 1 (S3) control case: the window did NOT return to the starting rectangle (the resize
    /// really changed it) — the deferred persist runs, including the height-commit notification.
    /// </summary>
    [Fact]
    public void AbortResize_WindowDiffersFromStartRect_DeferredPersistRuns_AndNotifiesHeightCommit()
    {
        var harness = new Harness();
        harness.Controller.AttachTo(PanelHwnd);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Bottom, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Bottom, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right, StartPx.Bottom + 40));

        harness.Controller.AbortResize(ResizeEndReason.SourceChanged);

        var moved = new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right, StartPx.Bottom + 40);
        harness.SizingService.Setup(s => s.TryGetWindowRect(PanelHwnd, out moved)).Returns(true);
        harness.RunDeferredPersistCalls();

        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
        harness.GridHost.Verify(h => h.NotifyHeightResizeCommitted(), Times.Once);
    }

    /// <summary>
    /// Review cycle 1 (S3): the start-rectangle check never applies to the synchronous teardown persist
    /// (<see cref="ResizeEndReason.Closing"/>) — the window is going away, the persist runs regardless.
    /// </summary>
    [Fact]
    public void AbortResize_Teardown_PersistsEvenIfWindowIsAtStartRect()
    {
        var harness = new Harness();
        harness.Controller.AttachTo(PanelHwnd);
        var restored = StartPx;
        harness.SizingService.Setup(s => s.TryGetWindowRect(PanelHwnd, out restored)).Returns(true);
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Right, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Right, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right + 20, StartPx.Bottom));

        harness.Controller.AbortResize(ResizeEndReason.Closing);

        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
    }

    // ══ G-4: ESC (final rect == start rect) is a cancel ════════════════════════════════════════════

    /// <summary>
    /// G-4: the system restored the start rectangle (ESC) — the gesture ends as
    /// <see cref="ResizeEndReason.Canceled"/>, nothing is persisted (not even deferred), no commit-time
    /// normalisation runs and an AutoContent panel is NOT demoted to Manual, although deltas were applied.
    /// </summary>
    [Fact]
    public void SizeMoveExited_FinalRectEqualsStart_EndsCanceled_NoPersist_NoAutoContentDemotion()
    {
        var harness = new Harness();
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Bottom, StartPx, dpiScale: 1.0));
        harness.Controller.ProposeBounds(NativeSizingEdge.Bottom, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right, StartPx.Bottom + 40));

        harness.Controller.SizeMoveExited(StartPx);

        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(It.IsAny<Guid?>(), ResizeEdge.Bottom.ToString(), true, It.IsAny<Rect>(), nameof(ResizeEndReason.Canceled), null),
            Times.Once);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Never);
        Assert.Empty(harness.DeferredPersistCalls);
        Assert.Equal(0, harness.SnapToGridCount);
        harness.GridHost.Verify(h => h.NotifyHeightResizeCommitted(), Times.Never);
        harness.GridHost.Verify(h => h.CommitGridGeometry(It.IsAny<Rect>(), It.IsAny<GridSpan>()), Times.Never);
        Assert.Equal(PanelInteractionState.Idle, harness.Tracker.State);
    }

    /// <summary>
    /// G-4 control case: a real change of the final rectangle still completes, persists once and — for a
    /// height-affecting edge — notifies the host (the AutoContent→Manual transition stays intact).
    /// </summary>
    [Fact]
    public void SizeMoveExited_FinalRectDiffers_CompletesAndPersists_AndNotifiesHeightCommit()
    {
        var harness = new Harness();
        Assert.True(harness.Controller.SizeMoveEntered(NativeSizingEdge.Bottom, StartPx, dpiScale: 1.0));
        var returned = harness.Controller.ProposeBounds(NativeSizingEdge.Bottom, new NativeRectPx(StartPx.Left, StartPx.Top, StartPx.Right, StartPx.Bottom + 40));

        harness.Controller.SizeMoveExited(returned);

        harness.Diagnostics.Verify(
            d => d.ResizeCompleted(It.IsAny<Guid?>(), ResizeEdge.Bottom.ToString(), false, It.IsAny<Rect>(), nameof(ResizeEndReason.Completed), null),
            Times.Once);
        harness.WindowManager.Verify(w => w.SaveWindowsState(), Times.Once);
        harness.GridHost.Verify(h => h.NotifyHeightResizeCommitted(), Times.Once);
    }

    /// <summary>
    /// Runs the same gesture (enter → zero-movement frame → <paramref name="proposed"/>) on a fresh harness
    /// at a constant 1.0 scale and without any DPI change — the oracle for the G-1 tests.
    /// </summary>
    /// <param name="proposed">The second <c>WM_SIZING</c> rectangle.</param>
    /// <returns>What the controller wrote back for <paramref name="proposed"/>.</returns>
    private static NativeRectPx RunWithoutDpiChange(NativeRectPx proposed)
    {
        var control = new Harness(gridSize: GridSize);
        Assert.True(control.Controller.SizeMoveEntered(NativeSizingEdge.Right, StartPx, dpiScale: 1.0));
        control.Controller.ProposeBounds(NativeSizingEdge.Right, StartPx);
        return control.Controller.ProposeBounds(NativeSizingEdge.Right, proposed);
    }

    /// <summary>The real controller plus its mocked collaborators; bounds live in an in-memory <see cref="Rect"/>.</summary>
    private sealed class Harness
    {
        public readonly List<Action> DeferredPersistCalls = [];
        public readonly Mock<IAppSettings> AppSettings = new();
        public readonly Mock<IWindowManager> WindowManager = new();
        public readonly Mock<IGestureDiagnostics> Diagnostics = new();
        public readonly Mock<INativePanelSizingService> SizingService = new();
        public readonly Mock<IPanelSizingGridHost> GridHost = new();
        public readonly Mock<IDesktopScaleProvider> ScaleProvider = new();
        public readonly PanelInteractionTracker Tracker = new();
        public readonly ChildWindowModel Model = new() { ViewMode = PanelViewMode.List, IconSize = 48d };
        public Rect Bounds = StartBoundsDip;
        public int SnapToGridCount;

        public Harness(int gridSize = 0)
        {
            AppSettings.SetupProperty(s => s.SnapResizeToIconGrid, false);
            AppSettings.SetupProperty(s => s.GridSize, gridSize);
            SizingService.Setup(s => s.Attach(It.IsAny<IntPtr>(), It.IsAny<INativePanelSizingClient>())).Returns(Mock.Of<IDisposable>());
            SizingService.Setup(s => s.CancelActiveSizing(It.IsAny<IntPtr>())).Returns(true);
            GridHost.Setup(h => h.ResolveGridState(It.IsAny<Rect>())).Returns(PanelGridState.Legacy);
            ScaleProvider.SetupGet(p => p.Current).Returns(() => CurrentScale);
            ScaleProvider.SetupGet(p => p.Mode).Returns(DesktopLayerMode.Embedded);

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
                snapToGrid: () => SnapToGridCount++,
                titleBarHeight: TitleBarHeight,
                Diagnostics.Object,
                Tracker,
                SizingService.Object,
                isResizeEnabled: () => true,
                desktopScale: ScaleProvider.Object,
                gridHost: GridHost.Object,
                isFreeResizeModifierDown: () => false);
        }

        public DesktopScaleModel CurrentScale { get; set; } = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 1.0, 1.0, []);

        public NativePanelResizeController Controller { get; }

        public void RunDeferredPersistCalls()
        {
            var pending = DeferredPersistCalls.ToArray();
            DeferredPersistCalls.Clear();
            foreach (var action in pending)
                action();
        }
    }
}
