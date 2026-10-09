using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns manual title-bar drag-to-move for a <see cref="Views.ChildWindow"/>, extracted from
/// <c>ChildWindow.xaml.cs</c>'s <c>TitleBar_MouseLeftButtonDown/Move/Up</c>/<c>LostMouseCapture</c>/
/// <c>EndDrag</c> (REFACTORING_PLAN.md §5.3). Replaces <see cref="Window.DragMove()"/>, which blocks the
/// calling thread and hands movement off to the native OS move loop until mouse-up — incompatible with
/// per-mouse-move magnetic grid snapping (see docs/PLANS/grid-snap-during-drag.md). Also owns the
/// drop-time grid-snap (<see cref="SnapToGrid"/>), which is shared with <see cref="NativePanelResizeController"/>'s
/// end-of-gesture handler (see its remarks).
/// </summary>
public sealed class PanelDragMoveBehavior
{
    private readonly Window _window;
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly Func<Guid?> _getPanelId;
    private readonly Func<ChildWindowModel?> _getModel;
    private readonly Action _onDragGestureStarting;
    private readonly Action _onDoubleClick;
    private readonly IGestureDiagnostics _diagnostics;
    private readonly PanelInteractionTracker _tracker;
    private readonly Func<bool> _canMove;
    private readonly ILayoutEditModeService _layoutEditMode;
    private readonly IMonitorService _monitorService;
    private readonly PanelGeometryWriter _geometryWriter;
    private readonly IDesktopScaleProvider _desktopScale;

    // Snapshot of every OTHER live panel's screen rectangle, taken once per drag gesture (see
    // docs/PLANS/panel-anti-overlap.md §3.3) — safe to take only once because the mouse is captured
    // for the whole gesture, so no other panel can move/resize/appear/disappear meanwhile. A reusable
    // list (not reallocated per-gesture) so TitleBar_MouseMove never allocates on the per-frame hot
    // path; repopulated in OnTitleBarMouseLeftButtonDown, cleared in EndDrag.
    private readonly List<Rect> _dragObstacles = [];

    // Snapshot of EVERY monitor's bounds/work-area (MAJOR-2 fix, phase-6 fix cycle; MAJOR-A fix,
    // fix-cycle round 2), taken once per drag gesture at the same point _dragObstacles is populated
    // below — same rationale as that snapshot: mouse capture is held for the whole gesture, so the
    // monitor layout cannot change meanwhile. Before the original MAJOR-2 fix, OnTitleBarMouseMove
    // called IMonitorService.FindMonitorAt/GetPrimaryMonitor (each a P/Invoke monitor enumeration +
    // LINQ allocation) on every single MouseMove while Edit Layout's alignment-guide step was active
    // (~100+ Hz) — a per-frame cost in the most latency-sensitive path in the project.
    //
    // MAJOR-A: that first fix snapshotted a single work area (the monitor the panel started the drag
    // on) and reused it for the whole gesture. But SnapGuideCalculator.Snap's `workArea` guard
    // (StaysOnScreenX/Y) discards any guide whose corrected position falls outside it — so once the
    // panel's cursor crossed onto a SECOND monitor mid-drag, every guide against an obstacle on that
    // second monitor was silently rejected for the rest of the gesture (obstacles are legitimately
    // cross-monitor; CollectOtherPanelBounds does not filter by monitor). Snapshotting every monitor
    // once per gesture (still a single IMonitorService.GetMonitors() enumeration — no P/Invoke per
    // frame) and resolving which one the panel currently occupies in managed code on every MouseMove
    // (a cheap list scan) restores the pre-d16aab6 cross-monitor behaviour while keeping the whole
    // perf win. Cleared in EndDrag (and ClearDragObstacles) alongside _dragObstacles.
    // P03-D1/D2 (docs/PLANS/workspace-grid-03-04-integration.md §2.3): DeviceName was previously
    // dropped when filling this snapshot; it is carried now because EndDrag needs it to update
    // ChildWindowModel.MonitorDeviceId when a panel is dropped on a different monitor (F-6 / ADR-028
    // «MonitorDeviceId при межмониторном drag»), and because PanelGridStateResolver.Resolve takes the
    // same (DeviceName, Bounds, WorkArea) shape.
    private readonly List<(string DeviceName, Rect Bounds, Rect WorkArea)> _dragMonitors = [];

    // R-3 (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2/D-5): two sub-states of "a press is currently
    // captured", not one. _isArmed spans from a successful CaptureMouse() at mouse-down until either
    // EndDrag (click, threshold never exceeded) or the cursor crosses the system drag threshold, at
    // which point _isDragging additionally becomes true and the gesture actually commits (tracker
    // claim, obstacle snapshot, DragStarted, and — on release — SnapToGrid/SaveWindowsState). A bare
    // click (armed, never dragging) never touches any of that.
    private bool _isArmed;
    private bool _isDragging;
    private Point _dragStartScreenCursor;

    // release-hardening F (ADR-037): the panel's own top-left at gesture start, in PHYSICAL screen
    // pixels (Window.PointToScreen(0,0)) — replaces the pre-F Window.Left/Top (DIP) snapshot.
    // DragPositionCalculator anchors the whole gesture's raw position here; see its remarks for why the
    // delta between _dragStartScreenCursor and the live cursor position needs no DPI conversion of its
    // own (both are physical pixels) while the RESULT does (a monitor-crossing drag can change scale).
    private Point _dragStartTopLeftPx;

    // Updated every frame after the pipeline below resolves the panel's new position — the desktop-DIP
    // equivalent of what used to be a bare Window.Left/Top re-read. Kept as a field (rather than always
    // re-reading Window.Left/Top through the adapter) because, once PanelWindowCoordinates writes a
    // position back onto the window, Window.Left/Top is in WINDOW space again, which only equals
    // desktop-DIP on a uniform snapshot.
    private Point _currentDesktopPosition;

    // Filled in OnTitleBarMouseLeftButtonDown immediately after a successful CaptureMouse(); cleared in
    // EndDrag. Lets AbortDrag() (D-4) end an in-flight gesture from outside the normal mouse event flow
    // (a layout-mode change) without needing its own reference to whatever element originated the press.
    private UIElement? _captureElement;

    // P03-D3: the panel's resolved workspace-grid state for THIS gesture — origin (its monitor's work
    // area top-left), work area, unit/gap, minimum span. Resolved exactly once per gesture, at the same
    // point _dragObstacles/_dragMonitors are snapshotted (never per mouse-move), and reset to
    // PanelGridState.Legacy in EndDrag alongside them. PanelGridState.Legacy (the default) means "this
    // gesture uses the untouched LegacyFreeform pipeline" — see Инвариант В1 in
    // PanelGridStateResolver's own remarks: this field is the ONLY thing the drag code branches on, and
    // it is produced by the one resolver that is allowed to read ChildWindowModel.GeometryMode.
    private PanelGridState _gridState = PanelGridState.Legacy;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelDragMoveBehavior"/> class.
    /// </summary>
    /// <param name="window">The owning panel window (its <see cref="Window.Left"/>/<see cref="Window.Top"/> are moved).</param>
    /// <param name="appSettings">Grid size/snap-distance settings driving the live drag-time magnet.</param>
    /// <param name="windowManager">Used to snapshot obstacle panels and persist layout state.</param>
    /// <param name="getPanelId">Accessor for the current panel's <see cref="ChildWindowModel.Id"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="getModel">
    /// Accessor for the panel's model (may return <see langword="null"/> before <c>Initialize</c>) —
    /// P03-D3: the single input <see cref="PanelGridStateResolver.Resolve"/> needs to decide whether this
    /// gesture runs the <see cref="PanelGeometryMode.GridAligned"/> pipeline or the untouched
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> one. Passed in addition to (not instead of)
    /// <paramref name="getPanelId"/>, which stays the obstacle-snapshot/diagnostics identity accessor.
    /// </param>
    /// <param name="onDragGestureStarting">
    /// Invoked at the very start of every title-bar press, before the roll-up/drag branch is decided —
    /// commits any in-progress file-item rename and clears the panel's own file selection (mirrors the
    /// original <c>CommitActiveItemRenameIfAny</c> + <c>SelectedFile = null</c> pair).
    /// </param>
    /// <param name="onDoubleClick">Invoked instead of starting a drag when the title bar receives a double click (toggles roll-up).</param>
    /// <param name="diagnostics">Log-only observer of the drag gesture lifecycle (docs/Refactoring/ §3.4) — never influences behavior.</param>
    /// <param name="tracker">
    /// The panel's shared <see cref="PanelInteractionTracker"/> (ADR-021) — claims
    /// <see cref="PanelInteractionState.Dragging"/> for the duration of the gesture, enforcing "only one
    /// gesture owns capture at a time" against a concurrent resize (docs/PLANS/phase-1-resize-stabilization.md §3.2).
    /// </param>
    /// <param name="canMove">
    /// Live policy gate (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2/D-5) — re-evaluated on every
    /// title-bar press, AFTER <paramref name="onDragGestureStarting"/> and the double-click/roll-up
    /// branch (D-3: double click still collapses/expands the panel even when move is disallowed), but
    /// BEFORE any mouse capture is taken. Typically backed by
    /// <c>LayoutGesturePolicy.Resolve(mode, isLayoutLocked, normalPolicy).CanMove</c>, read fresh on
    /// every call rather than snapshotted.
    /// </param>
    /// <param name="layoutEditMode">
    /// docs/PLANS/phase-6-edit-layout-ux.md §1 D-7 (P6-5) — gates the <see cref="Models.SnapGuideCalculator"/>
    /// step inserted into <see cref="OnTitleBarMouseMove"/>'s pipeline: the guide step runs ONLY while
    /// <see cref="ILayoutEditModeService.IsEditing"/> is <see langword="true"/>, read fresh on every
    /// mouse-move (same "never snapshotted" convention as <paramref name="canMove"/>) — so the Normal-mode
    /// drag pipeline stays byte-for-byte the pre-P6-5 pipeline (§0 zero-regression requirement).
    /// </param>
    /// <param name="monitorService">
    /// Supplies the once-per-gesture monitor snapshot (<see cref="_dragMonitors"/>) used to resolve the
    /// guide step's soft off-screen guard (<see cref="Models.SnapGuideCalculator.Snap"/>'s <c>workArea</c>
    /// parameter) for whichever monitor the panel currently occupies — see that field's own doc comment
    /// (MAJOR-A fix, fix-cycle round 2).
    /// </param>
    /// <param name="geometryWriter">
    /// The panel's single geometry writer (docs/PLANS/workspace-grid-03-04-integration.md §1.5) — used by
    /// the <see cref="PanelGeometryMode.GridAligned"/> DROP path (and the F-6 monitor-id update) so the
    /// window and its model are written in one guarded act. The per-frame live-drag write deliberately
    /// stays a per-frame call into <see cref="Interaction.PanelWindowCoordinates"/> (plan P03-D8): it is
    /// transient, runs at mouse-move frequency, and is committed to the model by the drop-time write below
    /// anyway.
    /// </param>
    /// <param name="desktopScale">
    /// The per-monitor DPI snapshot provider (release-hardening F, ADR-037) — every px↔desktop-DIP
    /// conversion this behavior performs (<see cref="Interaction.DragPositionCalculator"/>,
    /// <see cref="Interaction.PanelWindowCoordinates"/>) reads its current snapshot.
    /// </param>
    public PanelDragMoveBehavior(
        Window window,
        IAppSettings appSettings,
        IWindowManager windowManager,
        Func<Guid?> getPanelId,
        Func<ChildWindowModel?> getModel,
        Action onDragGestureStarting,
        Action onDoubleClick,
        IGestureDiagnostics diagnostics,
        PanelInteractionTracker tracker,
        Func<bool> canMove,
        ILayoutEditModeService layoutEditMode,
        IMonitorService monitorService,
        PanelGeometryWriter geometryWriter,
        IDesktopScaleProvider desktopScale)
    {
        _window = window;
        _appSettings = appSettings;
        _windowManager = windowManager;
        _getPanelId = getPanelId;
        _getModel = getModel;
        _onDragGestureStarting = onDragGestureStarting;
        _onDoubleClick = onDoubleClick;
        _diagnostics = diagnostics;
        _tracker = tracker;
        _canMove = canMove;
        _layoutEditMode = layoutEditMode;
        _monitorService = monitorService;
        _geometryWriter = geometryWriter;
        _desktopScale = desktopScale;
    }

    /// <summary>
    /// Clears the drag-obstacle and monitor snapshots without ending a drag. Used by
    /// <see cref="NativePanelResizeController"/> at the start of a resize gesture so a resize gesture can never
    /// inherit a stale snapshot left behind by a drag that ended through some path other than
    /// <see cref="EndDrag"/> (finalization MINOR m-1; MINOR-7, fix-cycle round 2 — <see cref="_dragMonitors"/>
    /// was missing from this cleanup, unlike <see cref="EndDrag"/>'s own).
    /// </summary>
    public void ClearDragObstacles()
    {
        _dragObstacles.Clear();
        _dragMonitors.Clear();

        // P03-D3: the per-gesture grid state is snapshotted alongside the two lists above and must be
        // dropped with them — otherwise a resize gesture starting right after an abandoned drag could
        // inherit that drag's origin/work area (SnapToGrid re-resolves it on demand, see its remarks).
        _gridState = PanelGridState.Legacy;
    }

    /// <summary>
    /// Handles the initial press on the panel's title bar: commits any active inline rename, toggles
    /// roll-up on a double click, or — for a single click, when moving is currently allowed — captures
    /// the mouse and arms a drag gesture (without yet claiming <see cref="PanelInteractionState.Dragging"/>;
    /// see <see cref="OnTitleBarMouseMove"/>).
    /// </summary>
    /// <param name="sender">The title-bar element that receives mouse capture for the gesture.</param>
    /// <param name="e">The mouse-button event; used to detect the click count and button state.</param>
    public void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // An active file-item inline rename never sees this click as a LostKeyboardFocus (the panel
        // carries WS_EX_NOACTIVATE) — commit it explicitly first, and clear this panel's own selection
        // (clicking the title bar is not clicking a file item) — see the constructor doc comment.
        _onDragGestureStarting();

        // Double click on the title bar toggles roll-up; single click drags the panel.
        // Clicks that land on child controls (rollup/rename/menu buttons) never reach here:
        // ButtonBase marks MouseLeftButtonDown as handled before it bubbles to this Border.
        if (e.ClickCount == 2)
        {
            _onDoubleClick();
            return;
        }

        // D-5 (docs/PLANS/phase-6-edit-layout-ux.md §1): gated AFTER _onDragGestureStarting() and the
        // double-click branch above (D-3 — double click still rolls the panel up/down even when move is
        // disallowed), but BEFORE any mouse capture is taken — a disallowed move never starts any
        // gesture at all, armed or otherwise.
        if (!_canMove()) return;

        // Start the drag optimistically, but only if mouse capture actually succeeds — otherwise
        // MouseMove/MouseLeftButtonUp may never arrive (e.g. capture denied while another element
        // already holds it) and _isArmed would get stuck true with no way to clear it.
        if (sender is not UIElement element) return;
        if (!element.CaptureMouse()) return;

        // R-3 (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2): capture is taken immediately — so
        // MouseMove/Up keep routing here regardless of the eventual click-vs-drag outcome — but the
        // gesture is only ARMED at this point. Claiming PanelInteractionState.Dragging, snapshotting
        // obstacles, and recording DragStarted are all deferred to OnTitleBarMouseMove, once the cursor
        // has actually crossed the system drag threshold (see its remarks). A plain click that releases
        // before that point never reaches any of that (EndDrag's _isDragging check).
        _captureElement = element;
        _isArmed = true;
        _dragStartScreenCursor = _window.PointToScreen(Mouse.GetPosition(_window));

        // release-hardening F (ADR-037, §5 #17), MV-1 fix (H2): the physical-pixel anchor
        // DragPositionCalculator resolves every frame's raw position from. This used to be
        // Window.PointToScreen(0,0) — TRUE screen pixels, anchored at the physical-screen origin
        // (top-left of the primary monitor). But the position DragPositionCalculator resolves every
        // frame is written back through PanelWindowCoordinates.WriteDesktopPosition, i.e. in
        // DESKTOP-DIP space, whose origin (in Embedded mode, DesktopCoordinateSpace) is the WorkerW
        // child's own origin — which is offset from the physical-screen origin by the virtual-screen
        // origin whenever a monitor sits left of/above the primary. Anchoring the start point in a
        // DIFFERENT space than the one every subsequent frame writes into made the panel jump by that
        // offset on the very first move. Deriving the anchor from the same desktop-DIP read the rest
        // of the gesture uses, then converting it to px through the very same scale, keeps this
        // bit-identical to 0.6.1 on every uniform/Embedded snapshot (DipToPx/PxToDip round-trip
        // through one scale factor) while fixing the offset on a mixed-DPI TopLevel desktop.
        var scale = _desktopScale.Current;
        _dragStartTopLeftPx = scale.DipToPx(PanelWindowCoordinates.ReadDesktopBounds(_window, scale).TopLeft);
        _currentDesktopPosition = PanelWindowCoordinates.ReadDesktopBounds(_window, scale).TopLeft;
    }

    /// <summary>
    /// Live drag: on every mouse move while armed, re-derive the raw (unsnapped) window position from
    /// the cursor delta since the press started. Below the system drag threshold this is a no-op (R-3);
    /// once the threshold is crossed, the gesture commits (claims <see cref="PanelInteractionState.Dragging"/>,
    /// snapshots obstacles, records <see cref="IGestureDiagnostics.DragStarted"/>) and every move
    /// thereafter magnetically snaps the position to the icon grid via <see cref="GridSnap"/>, then
    /// clamps it against the drag-obstacle snapshot so the panel can never overlap another live panel
    /// (docs/PLANS/panel-anti-overlap.md §3.5).
    /// </summary>
    /// <param name="sender">Unused; present to match the WPF mouse-event handler signature.</param>
    /// <param name="e">
    /// The mouse-move event; only its <see cref="MouseEventArgs.LeftButton"/> is read — the cursor
    /// position driving the live drag delta comes from <see cref="Mouse.GetPosition(IInputElement)"/>,
    /// not from this event.
    /// </param>
    public void OnTitleBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isArmed || e.LeftButton != MouseButtonState.Pressed) return;

        // PointToScreen returns physical device pixels, but SystemParameters.MinimumHorizontal/
        // VerticalDragDistance below are device-independent units (96 DPI baseline). At any DPI scale
        // other than 100% (e.g. 125%/150%, common on laptops) comparing a raw device-pixel delta against
        // a DIU value would trip the threshold too early/late. Convert the delta to DIU using this
        // window's DPI scale, for the threshold check only — release-hardening F (ADR-037): reads through
        // the adapter's GetWindowScale (the one allowed VisualTreeHelper.GetDpi use outside
        // Interaction/PanelWindowCoordinates.cs itself), not a raw VisualTreeHelper.GetDpi call. The
        // actual raw target position below is resolved separately, px-anchored (DragPositionCalculator).
        var windowScale = PanelWindowCoordinates.GetWindowScale(_window, _desktopScale.Current);
        var currentScreenCursor = _window.PointToScreen(Mouse.GetPosition(_window));
        var deltaX = (currentScreenCursor.X - _dragStartScreenCursor.X) / windowScale;
        var deltaY = (currentScreenCursor.Y - _dragStartScreenCursor.Y) / windowScale;

        if (!_isDragging)
        {
            // R-3 (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2): a bare click on the title bar must
            // never itself count as a drag — mirrors the same system-drag-threshold check
            // ListReorderGestureController.OnPreviewMouseMove uses (ADR-014). Below the threshold this
            // is a complete no-op: no Window.Left/Top write, no tracker claim, no obstacle snapshot.
            if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
                return;

            // ADR-021 "only one gesture owns capture at a time": claim Dragging only now that a real
            // drag is starting. Mouse capture has been held exclusively by this element since mouse-down
            // (OnTitleBarMouseLeftButtonDown), so nothing else in this process could have claimed
            // Resizing/Editing in the meantime — TryEnter refusing here is not expected in practice, but
            // the same defensive fallback the original mouse-down handler had still applies.
            if (!_tracker.TryEnter(PanelInteractionState.Dragging))
            {
                _captureElement?.ReleaseMouseCapture();
                _isArmed = false;
                _captureElement = null;
                return;
            }

            _isDragging = true;

            // Snapshot every other live panel's rectangle exactly once for this gesture (plan §3.3):
            // correct because the mouse is captured for the whole gesture, so nothing else on screen can
            // move/resize/appear/disappear in the meantime. The panel id can be null in tests that never
            // call Initialize — guard the same way the rest of this class does, and clear rather than
            // leave a stale snapshot from a previous gesture in that case.
            if (_getPanelId() is { } panelId)
                _windowManager.CollectOtherPanelBounds(panelId, _dragObstacles);
            else
                _dragObstacles.Clear();

            // MAJOR-2 / MAJOR-A fix: snapshot EVERY monitor's bounds/work-area once per gesture, right
            // alongside the obstacle snapshot above, rather than re-deriving a single work area (via a
            // fresh monitor enumeration) on every MouseMove below — see the field's own doc comment.
            _dragMonitors.Clear();
            foreach (var monitor in _monitorService.GetMonitors())
                _dragMonitors.Add((monitor.DeviceName, monitor.Bounds, monitor.WorkArea));

            // P03-D3: resolve this panel's workspace-grid state ONCE for the whole gesture, from the
            // snapshot taken just above — never per mouse-move (same contract as the two snapshots).
            // PanelGridStateResolver is the single place ChildWindowModel.GeometryMode is read
            // (Инвариант В1); a LegacyFreeform panel (or a GridAligned one whose spans cannot be
            // resolved this frame) yields PanelGridState.Legacy and the pipeline below stays
            // byte-for-byte the pre-P03 one.
            _gridState = ResolveGridState();

            // Diagnostics geometry deliberately reads Width/Height, not ActualWidth/ActualHeight: Width/
            // Height is what ChildWindow.OnRenderSizeChanged actually persists into
            // ChildWindowModel.Size (see that method's remarks), so it matches both the value that gets
            // saved to disk and the resize controller's own diagnostics convention — unified here rather
            // than left to differ per gesture type (finalization M-5 cleanup). This does not touch the
            // `current` Rect below, which intentionally still reads ActualWidth/ActualHeight for the
            // real anti-overlap clamp — only the log-only diagnostics reads change. Position is the
            // last-resolved desktop-DIP position (release-hardening F, ADR-037) — see
            // _currentDesktopPosition's own remarks.
            _diagnostics.DragStarted(
                _getPanelId(),
                _dragStartScreenCursor,
                new Rect(_currentDesktopPosition, new Size(_window.Width, _window.Height)));
        }

        // release-hardening F (ADR-037, §5 #17): px-anchored — the raw target position is resolved from
        // the physical-pixel cursor delta through the current desktop-scale snapshot (per-monitor,
        // anchored on a mixed-DPI TopLevel desktop), not from a DIP delta added to a DIP start position.
        // Bit-identical to the pre-F `_dragStartLeft/Top + deltaX/Y` on any uniform snapshot.
        var rawDesktop = DragPositionCalculator.ResolveDesktopTopLeft(
            _dragStartTopLeftPx, _dragStartScreenCursor, currentScreenCursor, _desktopScale.Current);
        var rawLeft = rawDesktop.X;
        var rawTop = rawDesktop.Y;

        // P03-D4/D5/D6/D7: a GridAligned panel runs the whole ADR-028 pipeline (workspace-grid
        // quantisation -> ADR-026 guides -> gap-aware hard wall) in one pure call; the Alt modifier is
        // read FRESH on every frame (never snapshotted — same convention as _canMove and
        // ILayoutEditModeService.IsEditing) and suppresses the quantisation step for that frame only.
        // Everything below this branch is the untouched LegacyFreeform pipeline (ADR-028 «Конвейер
        // LegacyFreeform (без изменений)»).
        if (_gridState.IsGridAligned)
        {
            var gridCurrent = new Rect(_currentDesktopPosition, new Size(_window.ActualWidth, _window.ActualHeight));
            var gridPosition = PanelDragPipeline.ResolveGridFrame(
                gridCurrent,
                new Point(rawLeft, rawTop),
                _gridState,
                _dragMonitors,
                _dragObstacles,
                isEditingLayout: _layoutEditMode.IsEditing,
                freeMove: IsFreeMoveModifierDown);

            _currentDesktopPosition = gridPosition;
            PanelWindowCoordinates.WriteDesktopPosition(_window, gridPosition, _desktopScale.Current);

            _diagnostics.DragDelta(_getPanelId(), new Rect(_currentDesktopPosition, new Size(_window.Width, _window.Height)));
            return;
        }

        var (snappedLeft, snappedTop) = GridSnap.Snap(rawLeft, rawTop, _appSettings.GridSize, _appSettings.GridSnapDistance);

        // docs/PLANS/phase-6-edit-layout-ux.md §1 D-7 (P6-5): the inter-panel alignment-guide magnet runs
        // strictly between GridSnap (above) and PanelCollision.ClampAgainstObstacles (below), and ONLY
        // while in Edit Layout. Outside Edit Layout this whole block is skipped — the Normal-mode pipeline
        // is byte-for-byte identical to the pre-P6-5 pipeline (§0 zero-regression requirement), not merely
        // a no-op computation. IsEditing is read fresh here (not snapshotted), matching _canMove's own
        // "read fresh on every call" convention.
        if (_layoutEditMode.IsEditing)
        {
            var proposed = new Rect(snappedLeft, snappedTop, _window.ActualWidth, _window.ActualHeight);

            // MAJOR-2 / MAJOR-A fix: resolve the CURRENT monitor's work area (the panel's live top-left
            // may have crossed onto a different monitor than the one the gesture started on) from the
            // gesture-start monitor snapshot (_dragMonitors, taken once where _dragObstacles is
            // populated) instead of re-enumerating monitors on every MouseMove. A plain list scan — no
            // P/Invoke — so this stays on the cheap side of the hot path. If the panel's origin does not
            // fall inside any snapshotted monitor bounds (e.g. transiently off every screen), workArea
            // stays Rect.Empty, which SnapGuideCalculator.Snap's contract treats as "disable the guard"
            // rather than "reject every guide".
            var origin = _currentDesktopPosition;
            var workArea = Rect.Empty;
            for (var i = 0; i < _dragMonitors.Count; i++)
            {
                if (_dragMonitors[i].Bounds.Contains(origin))
                {
                    workArea = _dragMonitors[i].WorkArea;
                    break;
                }
            }

            var guideResult = SnapGuideCalculator.Snap(proposed, _dragObstacles, workArea, SnapGuideCalculator.DefaultThreshold);
            snappedLeft = guideResult.Position.X;
            snappedTop = guideResult.Position.Y;
        }

        // ClampAgainstObstacles ALWAYS runs last, on whatever snappedLeft/snappedTop the steps above
        // produced (grid-only in Normal mode, grid+guide in Edit Layout) — R-8's "hard wall": no upstream
        // magnet, including the guide step above, is ever allowed to push the panel into a neighbour.
        var current = new Rect(_currentDesktopPosition, new Size(_window.ActualWidth, _window.ActualHeight));
        var clamped = PanelCollision.ClampAgainstObstacles(current, snappedLeft, snappedTop, _dragObstacles);

        _currentDesktopPosition = clamped.TopLeft;
        PanelWindowCoordinates.WriteDesktopPosition(_window, clamped.TopLeft, _desktopScale.Current);

        // Width/Height, not ActualWidth/ActualHeight — see the DragStarted comment above for why;
        // `clamped`/`current` above are the real (unmodified) clamp math and still use
        // ActualWidth/ActualHeight.
        _diagnostics.DragDelta(_getPanelId(), new Rect(_currentDesktopPosition, new Size(_window.Width, _window.Height)));
    }

    /// <summary>Ends the drag gesture (if any) on release of the title-bar mouse button.</summary>
    /// <param name="sender">The element releasing mouse capture; forwarded to <see cref="EndDrag"/>.</param>
    /// <param name="e">Unused; present to match the WPF mouse-event handler signature.</param>
    public void OnTitleBarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndDrag(sender as UIElement);

    /// <summary>
    /// Handles involuntary loss of mouse capture (Alt+Tab, system dialog, window deactivation, etc.) so
    /// a drag never gets "stuck" with the armed/dragging flags left <see langword="true"/>. Safe to call
    /// redundantly: <see cref="EndDrag"/> is a no-op once the gesture has already ended.
    /// </summary>
    /// <param name="sender">The element that lost mouse capture; forwarded to <see cref="EndDrag"/>.</param>
    /// <param name="e">Unused; present to match the WPF mouse-event handler signature.</param>
    public void OnTitleBarLostMouseCapture(object sender, MouseEventArgs e)
        => EndDrag(sender as UIElement);

    /// <summary>
    /// Externally aborts an in-flight drag (docs/PLANS/phase-6-edit-layout-ux.md §1 D-4) — invoked by
    /// <see cref="Views.ChildWindow"/>'s <see cref="Services.ILayoutEditModeService.ModeChanged"/>
    /// handler so exiting Edit Layout (or the layout being locked) deterministically ends any drag still
    /// in progress. Idempotent: a no-op if no gesture is currently armed/dragging. Goes through exactly
    /// the same path as an ordinary <see cref="OnTitleBarMouseLeftButtonUp"/>/<see cref="OnTitleBarLostMouseCapture"/>
    /// — release capture, (if the drag threshold was actually crossed) <see cref="SnapToGrid"/>, clear
    /// obstacles, <see cref="IWindowManager.SaveWindowsState"/>, <see cref="PanelInteractionTracker.Exit"/>
    /// in <c>finally</c> (ADR-021).
    /// </summary>
    public void AbortDrag() => EndDrag(_captureElement);

    /// <summary>
    /// Rounds Left and Top to the nearest grid cell after a drag or resize operation, using the same
    /// <see cref="IAppSettings.GridSnapDistance"/> threshold as the live drag-time magnet (rather than an
    /// unconditional quantise with <c>threshold = grid</c>). Keeping the same threshold for both the live
    /// magnet and the drop-time snap avoids a visible "jump" on mouse-up: with an unconditional drop
    /// snap, a panel released just outside the live-drag magnet radius (but still off-grid) would jerk
    /// onto the nearest line instead of staying where the user visually left it. GridSize&lt;=1 means no
    /// effective snapping (every pixel is a valid position).
    /// </summary>
    /// <remarks>
    /// Anti-overlap on mouse-up (docs/PLANS/panel-anti-overlap.md §3.5): this is NOT a full
    /// <see cref="PanelCollision.ClampAgainstObstacles(Rect, double, double, System.Collections.Generic.IReadOnlyList{Rect})"/>
    /// pass — the live drag already clamped the panel
    /// against <see cref="_dragObstacles"/> on every frame, so it cannot already be inside a neighbour
    /// when this runs. The only way the grid snap below could create a NEW overlap is by nudging the
    /// already-clamped position by up to <see cref="IAppSettings.GridSnapDistance"/> pixels back toward a
    /// grid line. Each axis is therefore snapped independently and rolled back (kept at its pre-snap
    /// value) if — and only if — <see cref="PanelCollision.CreatesNewOverlap"/> says that specific axis's
    /// snap would introduce a NEW overlap; an overlap that already existed before this drag (R1 — a
    /// pre-feature saved layout) never blocks the snap, because <c>CreatesNewOverlap</c> only reports
    /// overlaps that are new relative to <c>before</c>.
    /// <para>
    /// Also called by <see cref="NativePanelResizeController"/>'s end-of-gesture handler, but no drag gesture
    /// (and therefore no <see cref="OnTitleBarMouseLeftButtonDown"/>) runs before a resize, so
    /// <see cref="_dragObstacles"/> is empty there and <c>CreatesNewOverlap</c> always returns
    /// <see langword="false"/> for both axes — the snap behaves exactly as it did before this feature for
    /// a resize. This is intentional (plan §3.6 — resize anti-overlap is explicitly out of scope), not an
    /// oversight: do not "fix" it by populating <see cref="_dragObstacles"/> from resize handlers.
    /// </para>
    /// </remarks>
    public void SnapToGrid()
    {
        // P03-D9: a GridAligned panel comes to rest on the workspace grid, unconditionally — Alt only
        // ever loosened the LIVE frames (ADR-028 «Модификатор свободного движения»), and
        // IAppSettings.GridSize (the LegacyFreeform magnet, ADR-011) does not gate this path at all.
        //
        // _gridState is only filled while a DRAG gesture is in flight; this method is also called from
        // the resize controllers' completed-handlers (see the remarks above), where no drag ran and the
        // field is still PanelGridState.Legacy — so it re-resolves on demand there rather than silently
        // falling back to the legacy magnet (plan §4.3 item 3).
        var scale = _desktopScale.Current;
        var state = _gridState.IsGridAligned ? _gridState : ResolveGridState();
        if (state.IsGridAligned)
        {
            // Committed (explicit) size, not ActualWidth/Height: the resize controllers call this right
            // after committing Width/Height, one layout pass before Actual* catches up — see
            // PanelDragPipeline.CommittedSize's remarks. release-hardening F (ADR-037): the position is
            // read fresh, through the adapter — this method also runs from a resize-completed handler,
            // where no drag gesture ran this frame and _currentDesktopPosition may be stale/unset.
            var current = new Rect(
                PanelWindowCoordinates.ReadDesktopBounds(_window, scale).TopLeft,
                PanelDragPipeline.CommittedSize(_window.Width, _window.Height, _window.ActualWidth, _window.ActualHeight));

            // C-1 fix-cycle 1: retarget `state` to whichever monitor the panel's RAW (pre-clamp) rect
            // actually landed on BEFORE quantising/clamping — see RetargetToDropMonitor's own remarks for
            // the teleport-back-to-the-origin-monitor bug this closes. A no-op in the overwhelmingly
            // common case (the panel never left its starting monitor).
            state = PanelDragPipeline.RetargetToDropMonitor(current, state, _dragMonitors);

            // m-2 fix-cycle 1: SafeClamp must reason about the panel's EXPANDED footprint, not its live
            // (possibly ROLLED-UP/collapsed) one — see ResolveGridDrop's 4-argument overload remarks.
            var expanded = WorkspaceGridPlacement.ExpandedBounds(current.TopLeft, current.Size, _getModel());
            var dropped = PanelDragPipeline.ResolveGridDrop(current, state, _dragObstacles, expanded.Size);

            // Committed through the single geometry writer (plan §1.5) so Window.Left/Top and
            // ChildWindowModel.Position are written as one guarded act. Position only: a move never
            // changes the panel's size, and its span is already whatever the resolver validated.
            _geometryWriter.ApplyPosition(dropped.TopLeft, span: null, monitorDeviceId: null);
            return;
        }

        var grid = _appSettings.GridSize;
        if (grid <= 1) return;

        var threshold = _appSettings.GridSnapDistance;
        var width = _window.ActualWidth;
        var height = _window.ActualHeight;

        // release-hardening F (ADR-037): read/write through the adapter — see the GridAligned branch's
        // matching remark for why this cannot rely on _currentDesktopPosition here.
        var position = PanelWindowCoordinates.ReadDesktopBounds(_window, scale).TopLeft;
        var left = position.X;
        var top = position.Y;

        var snappedLeft = GridSnap.SnapAxis(left, grid, threshold);
        var beforeX = new Rect(left, top, width, height);
        var afterX = new Rect(snappedLeft, top, width, height);
        if (!PanelCollision.CreatesNewOverlap(beforeX, afterX, _dragObstacles))
            left = snappedLeft;

        var snappedTop = GridSnap.SnapAxis(top, grid, threshold);
        var beforeY = new Rect(left, top, width, height);
        var afterY = new Rect(left, snappedTop, width, height);
        if (!PanelCollision.CreatesNewOverlap(beforeY, afterY, _dragObstacles))
            top = snappedTop;

        PanelWindowCoordinates.WriteDesktopPosition(_window, new Point(left, top), scale);
    }

    private void EndDrag(UIElement? element)
    {
        if (!_isArmed) return;

        _isArmed = false;
        var wasDragging = _isDragging;
        _isDragging = false;

        // Cleared here (not just on the successful path below) so a second AbortDrag()/lost-capture
        // call after this gesture has already ended can never resolve a stale element reference — see
        // this field's own doc comment (D-4).
        _captureElement = null;

        if (!wasDragging)
        {
            // R-3: the press never crossed the drag threshold — a plain click. Release capture, but
            // never touch the tracker (Dragging was never claimed), SnapToGrid, or SaveWindowsState —
            // exactly the documented defect this threshold exists to fix (a click-only title-bar press
            // must not snap-to-grid/persist with a zero net offset).
            element?.ReleaseMouseCapture();
            return;
        }

        // ADR-021 "every Begin has exactly one logical End" / "window disposal forces state to Idle":
        // Exit runs in `finally` so a throw from SnapToGrid/SaveWindowsState below still releases
        // Dragging back to Idle instead of leaving the tracker stuck and blocking every future resize
        // on this panel (plan §3.2, R2).
        try
        {
            element?.ReleaseMouseCapture();
            SnapToGrid();

            // F-6 (P03-D10, ADR-028 «MonitorDeviceId при межмониторном drag», OQ-2 = A): record which
            // monitor the panel actually came to rest on, in BOTH geometry modes — the field is not part
            // of any drag-frame geometry, it only decides which monitor WindowManager restores the panel
            // onto later, which is exactly the bug this closes (drag to DISPLAY2 -> any
            // WM_SETTINGCHANGE/restart pulled the panel back to DISPLAY1). Must run BEFORE
            // SaveWindowsState() below.
            UpdateMonitorDeviceIdOnDrop();

            // Drop the drag-gesture obstacle snapshot (docs/PLANS/panel-anti-overlap.md §3.3, R5): never
            // leave a stale snapshot lying around between gestures (e.g. after Alt+Tab/system-dialog
            // involuntary capture loss via OnTitleBarLostMouseCapture). _dragMonitors (MAJOR-2/MAJOR-A
            // fix) is cleared alongside it for the same reason, even though it is always re-snapshotted
            // before its next read.
            _dragObstacles.Clear();
            _dragMonitors.Clear();
            _gridState = PanelGridState.Legacy;

            _windowManager.SaveWindowsState();
        }
        finally
        {
            _tracker.Exit(PanelInteractionState.Dragging);
        }

        // Diagnostics is recorded AFTER SnapToGrid()/SaveWindowsState() (not before, as originally
        // written) so it reads the post-snap, actually-committed/persisted geometry rather than the
        // raw pre-snap drag result (finalization M-5).
        // Purely a reordering of the log-only diagnostics call; the real control flow above (capture
        // release, SnapToGrid, obstacle clear, SaveWindowsState, and their internal order) is
        // unchanged. Width/Height (not ActualWidth/ActualHeight) for the same reason as DragStarted/
        // DragDelta above. If SnapToGrid/SaveWindowsState throws, diagnostics simply never gets
        // recorded for this gesture — acceptable, diagnostics is best-effort by design.
        var finalPosition = PanelWindowCoordinates.ReadDesktopBounds(_window, _desktopScale.Current).TopLeft;
        _diagnostics.DragCompleted(_getPanelId(), new Rect(finalPosition, new Size(_window.Width, _window.Height)));
    }

    /// <summary>
    /// The free-move modifier (<c>Alt</c>), read fresh on every frame that asks — never snapshotted, the
    /// same convention as <c>canMove</c>/<see cref="ILayoutEditModeService.IsEditing"/>, so pressing or
    /// releasing it mid-gesture takes effect on the very next mouse-move (ADR-028 «Модификатор
    /// свободного движения»: it loosens one frame, it never changes <see cref="ChildWindowModel.GeometryMode"/>).
    /// </summary>
    private static bool IsFreeMoveModifierDown => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

    /// <summary>
    /// Resolves the panel's <see cref="PanelGridState"/> from a fresh monitor snapshot, reusing
    /// <see cref="_dragMonitors"/> when a gesture already snapshotted it this frame. The ONLY place this
    /// class obtains grid state — <see cref="ChildWindowModel.GeometryMode"/> itself is never read here
    /// (Инвариант В1); <see cref="PanelGridStateResolver.Resolve"/> owns that decision.
    /// </summary>
    private PanelGridState ResolveGridState()
    {
        // Fast path: a LegacyFreeform panel must not pay for a monitor enumeration. This is the ONE
        // allowed shortcut (it can only skip TOWARDS PanelGridState.Legacy) and it still asks the
        // resolver, not ChildWindowModel.GeometryMode, so Инвариант В1 holds.
        if (!PanelGridStateResolver.ParticipatesInGrid(_getModel())) return PanelGridState.Legacy;

        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors;
        if (_dragMonitors.Count > 0)
        {
            monitors = _dragMonitors;
        }
        else
        {
            // No gesture snapshot available (SnapToGrid called from a resize-completed handler) — one
            // enumeration, off the per-frame path.
            var snapshot = new List<(string DeviceName, Rect Bounds, Rect WorkArea)>();
            foreach (var monitor in _monitorService.GetMonitors())
                snapshot.Add((monitor.DeviceName, monitor.Bounds, monitor.WorkArea));
            monitors = snapshot;
        }

        var model = _getModel();

        // The EXPANDED bounds, not the live ones: PanelGridStateResolver may derive (and persist) spans
        // from the size it is given, and a rolled-up panel's live height is its collapsed header — see
        // WorkspaceGridPlacement.ExpandedBounds' remarks. The live rectangle is still what the collision
        // math below uses; only the resolver's view of "how big is this panel" is corrected here.
        var bounds = WorkspaceGridPlacement.ExpandedBounds(
            PanelWindowCoordinates.ReadDesktopBounds(_window, _desktopScale.Current).TopLeft,
            new Size(_window.ActualWidth, _window.ActualHeight),
            model);

        return PanelGridStateResolver.Resolve(model, bounds, monitors, isRolledUp: model?.IsRolledUp ?? false);
    }

    /// <summary>
    /// F-6: updates <see cref="ChildWindowModel.MonitorDeviceId"/> when the drop landed the panel's
    /// CENTER on a different monitor than the one it was last associated with. Goes through the single
    /// geometry writer so the model write stays in one place; the position it re-applies is the panel's
    /// own current one, so this is geometrically a no-op.
    /// </summary>
    private void UpdateMonitorDeviceIdOnDrop()
    {
        if (_getModel() is not { } model) return;
        if (_dragMonitors.Count == 0) return;

        var final = new Rect(
            PanelWindowCoordinates.ReadDesktopBounds(_window, _desktopScale.Current).TopLeft,
            new Size(_window.ActualWidth, _window.ActualHeight));
        var deviceId = PanelDragPipeline.ResolveDropMonitor(final, _dragMonitors, model.MonitorDeviceId);
        if (deviceId is null) return;

        _geometryWriter.ApplyPosition(final.TopLeft, span: null, monitorDeviceId: deviceId);
    }
}
