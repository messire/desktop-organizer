using System.Windows;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns the panel's edge/corner resize gesture, extracted from <c>ChildWindow.xaml.cs</c>'s
/// <c>Resize_DragStarted/Delta/Completed</c> and <c>SnapWidthToGrid</c> (REFACTORING_PLAN.md §5.3). The
/// pixel math itself (<see cref="Models.GridMetrics"/>/<see cref="Models.AdaptiveGridLayout"/> calls) was
/// already side-effect-free before this extraction; this class wraps it with the small amount of
/// per-gesture mutable state (raw accumulators, the drag-gesture start snapshot) that the original
/// code-behind fields held.
/// </summary>
/// <remarks>
/// As of Phase 1 resize stabilization (docs/PLANS/phase-1-resize-stabilization.md §2.5, commit C2),
/// the actual snap/clamp/reposition math lives in the pure, Window-free
/// <see cref="Interaction.ResizeGeometryCalculator"/> — this class now only owns the WPF-specific glue
/// (<c>Thumb</c>/<c>Window</c> access, <see cref="IGestureDiagnostics"/> calls, persistence). See that
/// type's remarks for the relationship to the earlier draft at
/// <c>docs/Refactoring/UI/ResizeGeometryCalculator.cs</c>.
/// <para>
/// As of commit C4 (plan §2.6/§3.1), the gesture's lifecycle (active/not, idempotent end,
/// ADR-021's shared <see cref="PanelInteractionTracker"/> claim) is owned by
/// <see cref="Interaction.ResizeGestureSession"/> — this class only translates <c>Thumb</c>/<c>Window</c>
/// events into session calls and reacts to the resulting <see cref="Interaction.ResizeEndOutcome"/>.
/// Unlike pre-C4, <see cref="Interaction.ResizeConstraints"/> is now computed once per gesture (at
/// <see cref="Interaction.ResizeGestureSession.TryBegin"/>) and held fixed for its whole duration —
/// including <c>MinWidth</c>, which the pre-session code re-read from the live model on every single
/// delta. This is a deliberate simplification that comes with introducing an explicit session (no
/// characterization test exercises changing <c>IconSize</c>/<c>ViewMode</c> mid-gesture); it is NOT
/// part of the bit-for-bit contract commit C2 established.
/// </para>
/// </remarks>
public sealed class PanelResizeController : IPanelResizeController
{
    private readonly Window _window;
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly Func<ChildWindowModel?> _getModel;
    private readonly Action _onResizeGestureStarting;
    private readonly Action _clearDragObstacles;
    private readonly Action _snapToGrid;
    private readonly double _titleBarHeight;
    private readonly IGestureDiagnostics _diagnostics;
    private readonly ResizeGestureSession _session;

    // P04 (docs/PLANS/workspace-grid-03-04-integration.md §3.3/§3.4): the ADR-028 workspace-grid seam,
    // derived from the panel window itself (Views.ChildWindow implements it in its Sizing partial) so
    // this class needs no IMonitorService/PanelGeometryWriter dependency and ChildWindow's constructor
    // needs no new argument. Null for any other Window (including the plain Windows used by tests) —
    // which resolves to PanelGridState.Legacy everywhere below, i.e. exactly the pre-P04 behaviour.
    private readonly IPanelSizingGridHost? _gridHost;

    // Anti-overlap obstacles for a GridAligned resize (P04-G3), snapshotted once per gesture. Owned by
    // this controller, deliberately NOT PanelDragMoveBehavior's own _dragObstacles snapshot, which
    // OnResizeDragStarted explicitly clears for resize gestures (see _clearDragObstacles below).
    private readonly List<Rect> _resizeObstacles = [];

    // A SECOND buffer, used only by the end-of-gesture commit (P04 fix-cycle 1). Deliberately not
    // _resizeObstacles: that list's reference is handed to ResizeGestureSession.TryBegin and is still
    // owned by the (possibly next) gesture, so refilling it from a commit would mutate an active
    // gesture's snapshot. One extra empty List<Rect> per panel is cheaper than that class of bug.
    private readonly List<Rect> _commitObstacles = [];

    // The Thumb that owns the current gesture's mouse capture, tracked so EndResize can release it
    // without re-acquiring capture (plan §3.1 point 2) — set in OnResizeDragStarted, cleared in
    // EndResize. Never used to start a NEW gesture; ResizeGestureSession.IsActive is the sole source of
    // truth for "is a resize currently active".
    private Thumb? _activeThumb;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelResizeController"/> class.
    /// </summary>
    /// <param name="window">The owning panel window (its <see cref="Window.Width"/>/<see cref="Window.Height"/>/<see cref="Window.Left"/>/<see cref="Window.Top"/> are resized).</param>
    /// <param name="appSettings">Snap-to-grid settings driving whether/how the resize quantises.</param>
    /// <param name="windowManager">Used to persist layout state once a resize completes.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="onResizeGestureStarting">
    /// Invoked at the start of every resize gesture — commits any in-progress file-item rename and
    /// clears the panel's own file selection (mirrors the original <c>CommitActiveItemRenameIfAny</c> +
    /// <c>SelectedFile = null</c> pair also used by the title-bar drag gesture).
    /// </param>
    /// <param name="clearDragObstacles">Clears <see cref="PanelDragMoveBehavior"/>'s obstacle snapshot so a resize never inherits a stale one.</param>
    /// <param name="snapToGrid">Shared drop-time grid snap — see <see cref="PanelDragMoveBehavior.SnapToGrid"/>.</param>
    /// <param name="titleBarHeight">The panel's fixed title-bar height, used to strip chrome before quantising the resize gesture's height.</param>
    /// <param name="diagnostics">Log-only observer of the resize gesture lifecycle (docs/Refactoring/ §3.4) — never influences behavior.</param>
    /// <param name="tracker">
    /// The panel's shared <see cref="PanelInteractionTracker"/> (ADR-021) — handed to a new
    /// <see cref="ResizeGestureSession"/>, which claims <see cref="PanelInteractionState.Resizing"/> for
    /// the duration of each gesture.
    /// </param>
    /// <param name="isFreeResizeModifierDown">
    /// P04 fix-cycle 1: test seam for the <c>Alt</c> free-resize modifier, forwarded verbatim to
    /// <see cref="ResizeGestureSession"/>. <see langword="null"/> (production, and every pre-existing
    /// call site) means "read the real keyboard".
    /// </param>
    public PanelResizeController(
        Window window,
        IAppSettings appSettings,
        IWindowManager windowManager,
        Func<ChildWindowModel?> getModel,
        Action onResizeGestureStarting,
        Action clearDragObstacles,
        Action snapToGrid,
        double titleBarHeight,
        IGestureDiagnostics diagnostics,
        PanelInteractionTracker tracker,
        Func<bool>? isFreeResizeModifierDown = null)
    {
        _window = window;
        _appSettings = appSettings;
        _windowManager = windowManager;
        _getModel = getModel;
        _onResizeGestureStarting = onResizeGestureStarting;
        _clearDragObstacles = clearDragObstacles;
        _snapToGrid = snapToGrid;
        _titleBarHeight = titleBarHeight;
        _diagnostics = diagnostics;
        _session = new ResizeGestureSession(tracker, isFreeResizeModifierDown);
        _gridHost = window as IPanelSizingGridHost;
    }

    public void OnResizeDragStarted(object sender, DragStartedEventArgs e)
    {
        // Tag is always one of the 8 known strings set on the 8 Thumb elements in ChildWindow.xaml;
        // this branch is defensive only and never reachable in production — there is nowhere useful to
        // log a parse failure (no dedicated diagnostics event exists, and adding one is out of scope —
        // plan §3.6 "не вводить новых имён событий").
        if (sender is not Thumb thumb || !ResizeEdgeParser.TryParse(thumb.Tag as string, out var edge))
            return;

        // Stale-drag self-heal (H4): a previous gesture never reached EndResize (e.g. native capture
        // theft while this WS_EX_NOACTIVATE/WorkerW-embedded panel never receives its own
        // LostMouseCapture — see RecoverStaleDrag). End it now instead of refusing every future resize
        // on this panel forever (plan §3.1 point 2, §6 H4).
        if (_session.IsActive)
        {
            // finalization MAJOR M-3: the stale gesture's Thumb reference may be the SAME Thumb object
            // being clicked again (the user re-grabbing the very handle that got stuck) — by the time
            // DragStarted fires, that Thumb has ALREADY legitimately re-captured the mouse for THIS new
            // gesture (Thumb.OnMouseLeftButtonDown captures before raising DragStarted). EndResize below
            // calls ReleaseCaptureIfOurs(), which reads _activeThumb from the PREVIOUS gesture; if left
            // set, it would call ReleaseMouseCapture() on that same Thumb and tear the brand-new
            // gesture's capture away before the ownership check further down even runs — silently
            // swallowing the first click after a stale-drag recovery. Null it out first: a genuinely
            // stale gesture's Thumb has, by definition (H4), already lost real capture to something
            // else, so skipping the release here is a no-op for that case, and safe for the re-click case.
            _activeThumb = null;
            EndResize(ResizeEndReason.SourceChanged);
        }

        var model = _getModel();
        var startBounds = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);

        // P04: ONE resolution per gesture (§1.3's "снимок один раз на жест" contract) — feeds both the
        // constraint floors (§3.3) and the per-frame quantisation inside the session (§3.4).
        // NOTE (P04 fix-cycle 1, MINOR): this resolution runs BEFORE TryBegin, so a gesture the tracker
        // refuses still performs the resolver's lazy span-migration side effect. That is deliberate and
        // harmless: since MAJOR-3 the migration is idempotent (the derived span is clamped to the floor,
        // so the second resolve finds it valid and writes nothing), and the constraint floors below need
        // the state before the session can be started with them.
        var gridState = _gridHost?.ResolveGridState(startBounds) ?? PanelGridState.Legacy;
        var constraints = ResizeConstraintsFactory.BuildConstraints(model, _appSettings.SnapResizeToIconGrid, _titleBarHeight, gridState, _appSettings.GridSize);

        // Thumb.OnMouseLeftButtonDown already captured the mouse before raising DragStarted — capturing
        // again here would steal it FROM the thumb and break its own internal IsDragging bookkeeping
        // (plan §3.1 point 2). Verify ownership instead of re-capturing; the mismatch (if any) is
        // logged below via ResizeStarted's captureOwner argument, once the gesture is actually accepted.
        if (!ReferenceEquals(System.Windows.Input.Mouse.Captured, thumb))
        {
            thumb.CancelDrag();
            return;
        }

        if (!_session.TryBegin(edge, startBounds, constraints, gridState, PanelSizingGridSupport.CollectObstacles(_gridHost, gridState, _resizeObstacles)))
        {
            thumb.CancelDrag();
            return;
        }

        // Resize anti-overlap is explicitly out of scope (plan §3.6) — see PanelDragMoveBehavior.SnapToGrid's
        // remarks. Clear the drag-obstacle snapshot explicitly here too so a resize can never silently
        // inherit a stale, non-empty snapshot left behind by a drag gesture that ended through some path
        // other than PanelDragMoveBehavior.EndDrag (finalization MINOR m-1).
        //
        // finalization MINOR m-4: both of these run only now — AFTER capture ownership was verified and
        // TryBegin actually accepted the gesture — not unconditionally at the top of the method. A
        // rejected resize (capture mismatch, or the tracker already owns Dragging/another Resizing) must
        // not commit an in-progress rename or clear the panel's file selection as a side effect; moving
        // these two calls here (instead of first thing, as originally written) removes that side effect.
        _clearDragObstacles();
        _onResizeGestureStarting();

        // finalization MAJOR M-2: recorded only once the gesture is actually accepted (capture verified
        // AND TryBegin succeeded) — previously this ran unconditionally before either check, so a
        // rejected gesture (thumb.CancelDrag() + return, no matching End) produced a ResizeStarted with
        // no corresponding ResizeCompleted, and silently reset GestureDiagnostics' internal delta
        // counters for a gesture that never began.
        _diagnostics.ResizeStarted(
            model?.Id,
            edge.ToString(),
            startBounds,
            model?.ViewMode ?? PanelViewMode.List,
            model?.IconSize ?? 0d,
            _appSettings.SnapResizeToIconGrid,
            constraints.SnapCellWidth,
            constraints.SnapCellHeight,
            System.Windows.Input.Mouse.Captured?.GetType().Name);

        _activeThumb = thumb;
    }

    public void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string direction } thumb || !ResizeEdgeParser.TryParse(direction, out var edge))
            return;

        Rect applied;

        // H2/R3: the pre-Phase-1 delta path had no try/catch at all — an exception here left
        // Thumb.IsDragging permanently true (capture held forever, panel "stuck" to the cursor). Any
        // exception now ends the gesture instead of escaping into the WPF input pipeline.
        try
        {
            var current = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);

            if (!_session.TryUpdate(edge, new Vector(e.HorizontalChange, e.VerticalChange), current, out applied))
            {
                // Unexpected source change (plan §1.2) — e.g. a delta arrived for an edge that isn't the
                // one TryBegin recorded, or no gesture is active at all. Self-heal rather than silently
                // applying nothing: end whatever the session thinks is active and cancel this Thumb's drag.
                EndResize(ResizeEndReason.SourceChanged);
                thumb.CancelDrag();
                return;
            }

            _window.Left = applied.Left;
            _window.Top = applied.Top;
            _window.Width = applied.Width;
            _window.Height = applied.Height;
        }
        catch (Exception ex)
        {
            // Privacy invariant (diagnostics-log-events.md): NEVER log ex.Message (may contain a file
            // path) — only the exception's type name. R3: still surface this loudly during development
            // via Debug.WriteLine so a real bug here is never silently swallowed. NOTE: this must NEVER be
            // Debug.Fail — on .NET (Core) 8, unlike .NET Framework, Debug.Fail terminates the process when
            // no debugger is attached (it does not show a dismissable dialog), which defeats the entire
            // point of this try/catch (finalization CRITICAL C-1: an exception here was meant to end the
            // gesture cleanly, not crash the app). End(Failed) ensures ShouldPersist is false — the
            // geometry may be only half-applied, so it must never be written to disk (commit C6). The type
            // name also flows into ResizeCompleted's exceptionType field (commit C8) so it is visible in
            // the diagnostics log, not just Debug output.
            var exceptionType = ex.GetType().Name;
            System.Diagnostics.Debug.WriteLine($"OnResizeDragDelta failed: {exceptionType}");
            EndResize(ResizeEndReason.Failed, exceptionType);
            return;
        }

        // finalization MINOR m-2: kept out of the geometry try/catch above, in its own try/catch — a
        // failure in this log-only diagnostics call must never retroactively turn an already-applied,
        // visually-committed geometry update into a Failed gesture (which would suppress persistence and
        // effectively cancel the drag out from under the user).
        try
        {
            _diagnostics.ResizeDelta(
                _getModel()?.Id,
                direction,
                e.HorizontalChange,
                e.VerticalChange,
                _session.Raw.Width,
                _session.Raw.Height,
                applied);
        }
        catch
        {
            // Swallowed intentionally — log-only diagnostics must never affect gesture lifecycle.
        }
    }

    public void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
        => EndResize(e.Canceled ? ResizeEndReason.Canceled : ResizeEndReason.Completed);

    /// <summary>
    /// Reacts to the <c>ResizeOverlay</c>'s bubbling <c>Mouse.LostMouseCapture</c> (involuntary capture
    /// loss — Alt+Tab, system dialog, WorkerW/native capture theft, etc.) by ending whatever gesture is
    /// currently active. Safe to call unconditionally on every <c>LostMouseCapture</c>, including the
    /// synchronous one <see cref="EndResize"/> itself triggers by releasing capture on a normal
    /// completion — <see cref="ResizeGestureSession.End"/>'s idempotency makes that a no-op.
    /// </summary>
    public void OnCaptureLost(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // finalization MAJOR M-1: Thumb.OnMouseLeftButtonUp resets Thumb.IsDragging to false and calls
        // ReleaseMouseCapture() BEFORE raising its own DragCompleted event. ReleaseMouseCapture()
        // synchronously raises THIS handler (via the bubbling Mouse.LostMouseCapture on ResizeOverlay)
        // first. Ending the gesture as CaptureLost here would make every ordinary mouse-up look
        // identical to an involuntary capture loss (Alt+Tab, WorkerW/native capture theft) in the
        // diagnostics log, and turn the real DragCompleted that follows a moment later into an
        // idempotent no-op — losing the Completed/Canceled distinction the triage contract depends on
        // (diagnostics-log-events.md).
        //
        // Detect that specific, ordinary shape and defer to it: the capture that was just lost belongs
        // to OUR active gesture's own Thumb (ReferenceEquals against _activeThumb — not just "any
        // Thumb", so a stray/foreign Thumb's LostMouseCapture is never treated as ordinary), AND that
        // Thumb has ALREADY reset IsDragging to false by the time this handler runs — something only the
        // Thumb's own OnMouseLeftButtonUp does. An involuntary loss never gets that chance: the Thumb is
        // still genuinely mid-drag when capture is torn away from underneath it, so IsDragging is still
        // true here. If both hold, do nothing and let the imminent DragCompleted end the gesture as
        // Completed/Canceled instead.
        if (ReferenceEquals(e.OriginalSource, _activeThumb) && e.OriginalSource is Thumb { IsDragging: false })
            return;

        AbortResize(ResizeEndReason.CaptureLost);
    }

    /// <summary>
    /// Reacts to the panel's <c>Unloaded</c> event by ending whatever gesture is currently active — part
    /// of ADR-021's "window disposal forces state to Idle" invariant.
    /// </summary>
    public void OnUnloaded() => AbortResize(ResizeEndReason.Unloaded);

    /// <summary>
    /// Unconditionally ends whatever gesture is currently active with <paramref name="reason"/> — the
    /// public entry point for callers outside the normal Thumb event flow
    /// (<see cref="Views.ChildWindow.OnClosing"/> uses <see cref="ResizeEndReason.Closing"/>). A no-op if
    /// no gesture is active (<see cref="ResizeGestureSession.End"/> is idempotent).
    /// </summary>
    public void AbortResize(ResizeEndReason reason) => EndResize(reason);

    /// <summary>
    /// Self-heals a "stale drag" (H4): if a <c>Thumb</c> still reports <see cref="Thumb.IsDragging"/> =
    /// <see langword="true"/> from a PREVIOUS gesture that never received its own <c>LostMouseCapture</c>
    /// (native/WorkerW capture theft while this panel is <c>WS_EX_NOACTIVATE</c> — see plan §6 H4), a
    /// fresh mouse-down on it would otherwise be swallowed by the <c>Thumb</c>'s own early-exit-while-
    /// dragging guard, permanently disabling that handle. Called from
    /// <see cref="Views.ChildWindow.ResizeOverlay_PreviewMouseLeftButtonDown"/> — BEFORE the <c>Thumb</c>'s
    /// own handler runs (tunneling <c>Preview</c> pass) — so the stale state is cleared in time for the
    /// same click to start a fresh gesture.
    /// </summary>
    /// <param name="originalSource"><c>MouseButtonEventArgs.OriginalSource</c> from the tunneling event.</param>
    public void RecoverStaleDrag(object? originalSource)
    {
        if (originalSource is not Thumb { IsDragging: true } thumb) return;

        // finalization MAJOR M-4: the original "not stale" bail-out — `ReferenceEquals(Mouse.Captured,
        // thumb)` — fires in EVERY H4 case, not just the genuinely-not-stale one: H4 is precisely the
        // scenario where WPF never raised LostMouseCapture for this Thumb (WS_EX_NOACTIVATE/WorkerW
        // native capture theft it never got notified about), so WPF's own bookkeeping still believes
        // `Mouse.Captured == thumb` — that stale belief is exactly what needs self-healing, yet the old
        // condition treated it as proof the Thumb "genuinely" still holds capture and skipped recovery.
        // The real "still genuinely in progress, do not touch" signal is our OWN session's state, not
        // WPF's capture bookkeeping: only skip recovery when `_session.IsActive` AND WPF's capture still
        // points at this Thumb — anything else (session already ended for this Thumb, or WPF's capture
        // has moved on) means the Thumb's IsDragging=true is stale and must be cleared now, before the
        // Thumb's own MouseLeftButtonDown handler silently no-ops on it.
        if (_session.IsActive && ReferenceEquals(System.Windows.Input.Mouse.Captured, thumb)) return;

        // CancelDrag() itself synchronously raises this same Thumb's DragCompleted(Canceled: true),
        // which reaches OnResizeDragCompleted -> EndResize(Canceled) and already ends the stale session
        // (if one was still active). The explicit AbortResize below is a harmless, idempotent safety
        // net for the case where the session had already ended some other way while the Thumb itself
        // stayed stuck — matches plan §6 H4's stated shape exactly.
        thumb.CancelDrag();
        AbortResize(ResizeEndReason.SourceChanged);
    }

    /// <summary>
    /// Snaps the window width to the nearest whole column in Grid mode.
    /// Accounts for <see cref="GridMetrics.ContentHorizontalChrome"/> (M-2 fix-cycle amendment,
    /// adr/ADR-029-icon-layout-policy.md Решение 4 — <see cref="GridMetrics.FileListHPadding"/> plus a
    /// <see cref="IconLayoutPolicy.FixedGap"/>-only reserved scrollbar width) so left/right margins are
    /// equal AND the resulting window width reproduces the exact column count
    /// <see cref="Controls.AdaptiveIconGridPanel"/> will actually render for this panel's policy.
    /// No-op in List mode or when snap is disabled.
    /// </summary>
    public void SnapWidthToGrid()
    {
        var model = _getModel();
        if (model is null || model.ViewMode != PanelViewMode.Grid || !_appSettings.SnapResizeToIconGrid) return;

        // F-2 (docs/PLANS/workspace-grid-03-04-integration.md §3.7, DoD D4-2): a GridAligned panel's
        // WIDTH is a workspace-grid span (System B) and must never be re-derived from IconSize/the icon
        // grid (System C) — the ONE rule ADR-028 calls absolute. Gated here, inside the operation
        // itself, rather than at ChildWindow.OnModelPropertyChanged's single call site, so every present
        // and future caller of SnapWidthToGrid inherits it (and so P04 needs no edit in the
        // ChildWindow.xaml.cs region P03 is concurrently changing — §4.2). LegacyFreeform keeps the
        // existing behaviour, F-2 remaining that mode's documented limitation.
        if (PanelSizingGridSupport.IsGridAligned(_gridHost, new Rect(_window.Left, _window.Top, _window.Width, _window.Height))) return;

        var cell = GridMetrics.CellSize(model.IconSize);
        var chrome = GridMetrics.ContentHorizontalChrome(model.IconLayoutPolicy, SystemParameters.VerticalScrollBarWidth);
        var content = Math.Max(0d, _window.Width - chrome);

        // Round the snapped width UP to a whole pixel (Math.Ceiling), never down: the actual Win32
        // window width is always an integer device pixel, and a fractional IconSize (sliders aren't
        // tick-snapped) or DPI/layout rounding can otherwise leave the real window a hair narrower than
        // the analytical Extent(k) it was just snapped to — which makes AdaptiveGridLayout.RowCapacity
        // see a shortfall and drop a whole column (finalization MAJOR M-1).
        _window.Width = Math.Ceiling(Math.Max(
            GridMetrics.MinPanelWidth(model.IconSize, model.IconLayoutPolicy, SystemParameters.VerticalScrollBarWidth),
            AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(cell)) + chrome));
    }

    /// <summary>
    /// HORIZONTAL edge padding (<c>m</c>) used by <see cref="SnapWidthToGrid"/>'s standalone drop-time
    /// snap. The resize-drag width/height counterparts now live on
    /// <see cref="Interaction.ResizeGeometryCalculator"/> (Phase 1, commit C2) — see that type's
    /// <c>ResizeSnapEdgePadding</c>/<c>ResizeSnapVerticalEdgePadding</c> for the full doc comment
    /// (the "no reachable panel reference" compromise, and the bug the two separate axis ratios fix).
    /// Kept here, duplicated as a single line with no WPF/model dependency, purely because
    /// <see cref="SnapWidthToGrid"/> itself is out of Phase 1's extraction scope (plan §3.1 — listed
    /// "without changes").
    /// </summary>
    private static double ResizeSnapEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    /// <summary>
    /// The single unified end-of-gesture path (mirrors <see cref="PanelDragMoveBehavior.EndDrag"/>) —
    /// every way a resize gesture can stop (<see cref="OnResizeDragCompleted"/>'s ordinary/canceled
    /// mouse-up, the stale-drag/unexpected-source-change self-heals above, and — from commit C5 —
    /// capture loss/unload/closing/exception paths) funnels through here. Safe to call when no gesture
    /// is active: <see cref="ResizeGestureSession.End"/> is idempotent, so this is then a no-op.
    /// </summary>
    /// <param name="reason">Why the gesture is ending.</param>
    /// <param name="exceptionType">
    /// The failing exception's <see cref="Type.Name"/> when <paramref name="reason"/> is
    /// <see cref="ResizeEndReason.Failed"/> — <see langword="null"/> for every other reason. Never the
    /// exception's <c>Message</c> (privacy invariant, diagnostics-log-events.md).
    /// </param>
    private void EndResize(ResizeEndReason reason, string? exceptionType = null)
    {
        var outcome = _session.End(reason);
        if (!outcome.WasActive) return;

        ReleaseCaptureIfOurs();

        // Persist only when geometry was actually applied to the window (plan §1.3/§2.6, commit C6):
        // ResizeGestureSession.End computes ShouldPersist = at least one delta was applied AND the
        // gesture didn't end with Failed (a partially-applied geometry must never be written to disk).
        // A gesture that never moved the panel at all — e.g. CaptureLost/SourceChanged/Closing/Unloaded
        // arriving before the first DragDelta — now correctly writes nothing, instead of the pre-C6
        // behavior of unconditionally calling SnapToGrid/SaveWindowsState for every single end reason.
        if (outcome.ShouldPersist)
        {
            // P04-G6: geometry AND span are written through PanelGeometryWriter (never straight onto the
            // model) — so the model/window/span triple is always committed as one act (§1.5, baseline
            // risk 17).
            //
            // P04 fix-cycle 1, MAJOR-1: this now runs BEFORE the drop-time position snap, not after.
            // PersistSpans quantises the final bounds (size + anchor + work area + anti-overlap), which
            // makes the SafeClamp inside SnapToGrid -> ResolveGridDrop a guaranteed no-op for a
            // GridAligned panel; in the old order that clamp saw an oversized rectangle and "fixed" it by
            // TRANSLATING the panel, moving the very edge the resize had anchored. For a LegacyFreeform
            // panel PersistSpans is a no-op, so the legacy order (snap, then save) is unchanged.
            PanelSizingGridSupport.PersistSpans(
                _gridHost,
                new Rect(_window.Left, _window.Top, _window.Width, _window.Height),
                outcome.Edge,
                _commitObstacles);

            // ADR-011 Amendment 2026-09-24 (resize extension): idempotent commit-time safety net —
            // guarantees the panel never comes to rest off IAppSettings.GridSize's lattice even if Alt
            // suppressed per-frame quantisation for the gesture's last frame (or the whole gesture). A
            // no-op in the overwhelmingly common case (every live frame already quantised to GridSize —
            // see ResizeGestureSession.TryUpdate). Runs AFTER PersistSpans, same ordering rationale as
            // that call relative to _snapToGrid() below: whichever step can still move Width/Height must
            // run before the position-only drop-time snap.
            var beforeSizeGridNormalize = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
            var sizeGridNormalized = PanelSizingGridSupport.NormalizeCommittedSize(
                _gridHost,
                _getModel(),
                _appSettings.SnapResizeToIconGrid,
                _titleBarHeight,
                _appSettings.GridSize,
                beforeSizeGridNormalize,
                outcome.Edge);

            if (sizeGridNormalized != beforeSizeGridNormalize)
            {
                _window.Left = sizeGridNormalized.Left;
                _window.Top = sizeGridNormalized.Top;
                _window.Width = sizeGridNormalized.Width;
                _window.Height = sizeGridNormalized.Height;
            }

            _snapToGrid();

            _windowManager.SaveWindowsState();

            // P06 fix-cycle MAJOR-4: a completed, persisted resize that moved the panel's HEIGHT demotes
            // an AutoContent panel back to Manual (adr/ADR-029-icon-layout-policy.md "Fix-цикл P06") — a
            // width-only resize (Left/Right) never reaches this call (see the helper's own AffectsHeight
            // gate), so the existing width-resize-recomputes-height-via-debounce behavior is unaffected.
            PanelSizingGridSupport.NotifyHeightResizeCommitted(_gridHost, outcome.Edge);
        }

        // Diagnostics is recorded AFTER the persistence above (not before, as originally written) so it
        // reads _window.Width/Height/Left/Top post-snap when persistence ran — i.e. the geometry that
        // was actually committed/persisted, not the raw pre-snap drag result (finalization M-5). When
        // ShouldPersist is false the window was never touched by this method, so this simply reads
        // whatever geometry was already there. ResizeCompleted is still recorded unconditionally here
        // (WasActive was already true) — the diagnostics-log-events.md triage contract ("no CaptureLost
        // without a following ResizeCompleted") does not depend on whether anything was persisted.
        _diagnostics.ResizeCompleted(
            _getModel()?.Id,
            outcome.Edge.ToString(),
            reason == ResizeEndReason.Canceled,
            new Rect(_window.Left, _window.Top, _window.Width, _window.Height),
            outcome.Reason.ToString(),
            exceptionType);
    }

    /// <summary>
    /// Releases mouse capture from the gesture's <see cref="Thumb"/> — but ONLY if it still actually
    /// holds capture (<see cref="UIElement.ReleaseMouseCapture"/> is a documented no-op otherwise), and
    /// never by re-capturing (see <see cref="OnResizeDragStarted"/>'s remarks on why re-capturing is
    /// forbidden here).
    /// </summary>
    private void ReleaseCaptureIfOurs()
    {
        _activeThumb?.ReleaseMouseCapture();
        _activeThumb = null;
    }
}
