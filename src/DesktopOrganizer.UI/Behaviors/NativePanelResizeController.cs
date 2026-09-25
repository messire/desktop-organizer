using System.Windows.Threading;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Interaction.Native;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// <see cref="INativePanelSizingClient"/> adapter that drives the panel's resize gesture off the native
/// <c>WM_SIZING</c> loop instead of a WPF <c>Thumb</c> (docs/PLANS/phase-2-native-panel-sizing.md §1.5,
/// commit C4). Reuses Phase 1's <see cref="ResizeGestureSession"/>/<see cref="ResizeGeometryCalculator"/>
/// and commit C4's shared <see cref="ResizeConstraintsFactory"/> unchanged — this class owns only the
/// native-message-shaped glue: px→DIP conversion (<see cref="NativeSizingCoordinateMapper"/>), the
/// <c>WM_SIZING</c> coordinate-space self-calibration (<see cref="NativeSizingOriginResolver"/>), and
/// translating <see cref="NativeSizingEdge"/> to/from <see cref="ResizeEdge"/>
/// (<see cref="NativeSizingEdgeMap"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>D-4 — no <see cref="ResizeGeometryCalculator.Accumulate"/> chain:</b> <c>WM_SIZING</c> already
/// carries the gesture's absolute proposed extent (the system's own running total of cursor movement
/// since <c>WM_ENTERSIZEMOVE</c>), i.e. exactly the value <see cref="ResizeGeometryCalculator.Accumulate"/>
/// would reach after N incremental <c>Thumb.DragDelta</c> calls. <see cref="ProposeBounds"/> therefore
/// does not accumulate a running delta itself; instead it computes, for every single <c>WM_SIZING</c>
/// message, the ONE synthetic <see cref="Vector"/> that makes
/// <see cref="ResizeGestureSession.TryUpdate"/>'s own <see cref="ResizeGeometryCalculator.Accumulate"/>
/// call land exactly on the system's proposed extent — see <see cref="ProposeBounds"/>'s body for the
/// derivation. <see cref="ResizeGestureSession"/>/<see cref="ResizeGeometryCalculator"/> are not touched
/// (plan §3, "жёсткое требование") — this reuses <see cref="ResizeGestureSession.TryUpdate"/>'s existing
/// public surface, so <see cref="Interaction.ResizeEndOutcome.AppliedDeltaCount"/>/<c>ShouldPersist</c>
/// keep exactly the same semantics as the Thumb path.
/// </para>
/// <para>
/// <b>D-5:</b> <see cref="HitTest"/> only ever returns a sizing edge/corner or
/// <see cref="NativeSizingEdge.None"/> — never anything that would map to
/// <c>HTCAPTION</c>. Panel move stays a WPF gesture (<see cref="PanelDragMoveBehavior"/>).
/// </para>
/// </remarks>
public sealed class NativePanelResizeController : INativePanelSizingClient, IPanelResizeController, IDisposable
{
    /// <summary>
    /// Straight-edge strip thickness, in DIP, used by <see cref="HitTest"/> — reads
    /// <see cref="PanelMetrics.ResizeEdgeThickness"/>, the single source of truth shared with
    /// <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> Grid columns/rows (commit C2's
    /// <see cref="NativeHitTestMapper"/> contract; also exercised generically by
    /// <c>NativeHitTestMapperTests</c>) — MAJOR-1 fix, docs/PLANS/workspace-grid-07-visual.md fix-cycle 1:
    /// previously an independent <c>6d</c> literal kept in sync only by comment/convention with the XAML
    /// side, which is exactly the kind of drift that let the two geometries desync when
    /// <c>Radius.Panel</c> widened without a matching widening here. Split from a single shared
    /// <c>BorderDip</c> into this and <see cref="CornerDip"/> in fix-cycle 2 (MAJOR-A) — see
    /// <see cref="PanelMetrics.ResizeEdgeThickness"/>'s own remarks for why a single shared value was
    /// itself a real regression (it widened every straight edge, not just the corners the click-through
    /// fix actually needed).
    /// </summary>
    private const double EdgeDip = PanelMetrics.ResizeEdgeThickness;

    /// <summary>
    /// Corner square width/height, in DIP, used by <see cref="HitTest"/> — reads
    /// <see cref="PanelMetrics.ResizeCornerThickness"/>, the single source of truth shared with
    /// <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> corner <c>Thumb</c>s' explicit <c>Width</c>/
    /// <c>Height</c>. See <see cref="EdgeDip"/>'s remarks for the fix-cycle 2 split.
    /// </summary>
    private const double CornerDip = PanelMetrics.ResizeCornerThickness;

    /// <summary>
    /// <c>captureOwner</c> value recorded on <see cref="IGestureDiagnostics.ResizeStarted"/> for gestures
    /// on this path — the native sizing loop holds Win32 mouse capture itself (<c>SetCapture</c>), which
    /// has no meaningful WPF <see cref="System.Windows.Input.Mouse.Captured"/> element to report (unlike
    /// the Thumb path's <c>captureOwner</c>, which names the capturing WPF element's type).
    /// </summary>
    private const string NativeCaptureOwnerLabel = "NativeSizingLoop";

    private readonly Func<Rect> _getBounds;
    private readonly Action<double> _setWidth;
    private readonly Action<Rect> _setBounds;
    private readonly Action<Action> _scheduleDeferredPersist;
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly Func<ChildWindowModel?> _getModel;
    private readonly Action _onResizeGestureStarting;
    private readonly Action _clearDragObstacles;
    private readonly Action _snapToGrid;
    private readonly double _titleBarHeight;
    private readonly IGestureDiagnostics _diagnostics;
    private readonly ResizeGestureSession _session;
    private readonly INativePanelSizingService _nativeSizingService;
    private readonly Func<bool> _isResizeEnabled;

    // P04 (docs/PLANS/workspace-grid-03-04-integration.md §3.3/§3.4) — see PanelResizeController's
    // identically-named field: the ADR-028 workspace-grid seam, supplied by the panel window itself
    // (Views.ChildWindow) or, for headless tests, through the testability-seam constructor. Null means
    // "LegacyFreeform everywhere", i.e. the pre-P04 behaviour bit-for-bit.
    private readonly IPanelSizingGridHost? _gridHost;

    // Anti-overlap obstacles for a GridAligned resize (P04-G3), snapshotted once per gesture.
    private readonly List<Rect> _resizeObstacles = [];

    // A SECOND buffer, used only by the end-of-gesture commit (P04 fix-cycle 1) — see
    // PanelResizeController's identically-named field for why it must not be _resizeObstacles. The
    // hazard is sharper here: PersistAfterGesture can run DEFERRED (M-3), i.e. after a brand-new gesture
    // has already snapshotted into _resizeObstacles.
    private readonly List<Rect> _commitObstacles = [];

    private IntPtr _hwnd;
    private IDisposable? _attachToken;

    // Per-gesture state, valid only while _session.IsActive (mirrors ResizeGestureSession's own "valid
    // only while IsActive" fields — reset at the top of every successful SizeMoveEntered).
    private double _dpiScale = 1d;
    private NativeRectPx _startWindowRectPx;
    private NativeVectorPx _originOffsetPx;
    private bool _originResolved;
    private Rect _lastAppliedBoundsDip;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativePanelResizeController"/> class.
    /// </summary>
    /// <param name="window">The owning panel window — read for diagnostics geometry only; the native
    /// sizing loop (not this class) is what actually moves/resizes it via <c>WriteRect</c>.</param>
    /// <param name="appSettings">Snap-to-grid settings driving whether/how the resize quantises.</param>
    /// <param name="windowManager">Used to persist layout state once a resize completes.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="onResizeGestureStarting">Invoked at the start of every resize gesture — see <see cref="PanelResizeController"/>'s matching parameter.</param>
    /// <param name="clearDragObstacles">Clears <see cref="PanelDragMoveBehavior"/>'s obstacle snapshot so a resize never inherits a stale one.</param>
    /// <param name="snapToGrid">Shared drop-time grid snap — see <see cref="PanelDragMoveBehavior.SnapToGrid"/>.</param>
    /// <param name="titleBarHeight">The panel's fixed title-bar height, used to strip chrome before quantising the resize gesture's height.</param>
    /// <param name="diagnostics">Log-only observer of the resize gesture lifecycle — never influences behavior.</param>
    /// <param name="tracker">The panel's shared <see cref="PanelInteractionTracker"/> (ADR-021).</param>
    /// <param name="nativeSizingService">Installs/removes the <c>WM_SIZING</c>-loop window hook (see <see cref="AttachTo"/>).</param>
    /// <param name="isResizeEnabled">
    /// Roll-up gate: when it returns <see langword="false"/>, <see cref="HitTest"/> always reports
    /// <see cref="NativeSizingEdge.None"/>, mirroring the Thumb path's
    /// <c>IResizeAffordance</c>-driven overlay disable (plan §1.6, R-9).
    /// </param>
    public NativePanelResizeController(
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
        INativePanelSizingService nativeSizingService,
        Func<bool> isResizeEnabled)
        : this(
            GetBoundsDelegate(window),
            SetWidthDelegate(window),
            SetBoundsDelegate(window),
            ScheduleDeferredPersistDelegate(window),
            appSettings,
            windowManager,
            getModel,
            onResizeGestureStarting,
            clearDragObstacles,
            snapToGrid,
            titleBarHeight,
            diagnostics,
            tracker,
            nativeSizingService,
            isResizeEnabled,
            window as IPanelSizingGridHost)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NativePanelResizeController"/> class.
    /// Testability-seam constructor (code-quality-reviewer finding, Phase 2 §1.5/§5): identical to the
    /// public <see cref="Window"/>-taking constructor above, except the three points where that
    /// constructor previously read/wrote <c>_window</c> directly (<c>EndResize</c>'s diagnostics geometry
    /// read, <c>EndResize</c>'s deferred-persist dispatch, and <c>SnapWidthToGrid</c>'s width read/write)
    /// are supplied as plain delegates instead. The public constructor above is now a thin wrapper that
    /// closes these three delegates over a real <see cref="Window"/> (<see cref="GetBoundsDelegate"/>/
    /// <see cref="SetWidthDelegate"/>/<see cref="ScheduleDeferredPersistDelegate"/>) — production behavior
    /// is unchanged bit-for-bit, and <c>Views.ChildWindow.xaml.cs</c> (C5) needs no changes. This
    /// constructor is what makes the class constructible headless: tests close the same three delegates
    /// over a local, in-memory <see cref="Rect"/> instead of a real <see cref="Window"/> (no STA thread
    /// required) — see <c>NativePanelResizeControllerTests</c>.
    /// </summary>
    /// <param name="getBounds">
    /// Reads the panel's current on-screen bounds — replaces <c>_window.Left</c>/<c>Top</c>/<c>Width</c>/
    /// <c>Height</c> reads in <c>EndResize</c> (diagnostics geometry) and <c>SnapWidthToGrid</c> (current
    /// width).
    /// </param>
    /// <param name="setWidth">
    /// Writes the panel's width — replaces the <c>_window.Width =</c> write in <c>SnapWidthToGrid</c>.
    /// </param>
    /// <param name="setBounds">
    /// ADR-011 Amendment 2026-09-24 (resize extension): writes the panel's whole
    /// <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> — replaces the four individual
    /// <c>_window.Left/Top/Width/Height =</c> writes <c>PersistAfterGesture</c>'s commit-time
    /// <c>PanelSizingGridSupport.NormalizeCommittedSize</c> step performs when it actually moves the
    /// panel (the overwhelmingly common case is a no-op — see that method's own remarks). Unlike
    /// <paramref name="setWidth"/> (icon-grid drop-time width-only snap, which never needs to move
    /// <c>Left</c>/<c>Top</c>), this normalisation can re-anchor the panel, so it needs all four values.
    /// </param>
    /// <param name="scheduleDeferredPersist">
    /// Schedules a persist callback to run AFTER the current call stack unwinds — replaces
    /// <c>_window.Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)</c> in <c>EndResize</c>'s
    /// <c>deferPersist</c> path (M-3, commit 646e04c). The production delegate posts to the real WPF
    /// dispatcher; a test delegate can run the callback synchronously, capture it for manual invocation,
    /// or record that it was scheduled without running it inline — see that parameter's use in
    /// <c>EndResize</c> for why running it inline would be unsafe mid <c>WM_SIZING</c>.
    /// </param>
    /// <param name="appSettings">Snap-to-grid settings driving whether/how the resize quantises.</param>
    /// <param name="windowManager">Used to persist layout state once a resize completes.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="onResizeGestureStarting">Invoked at the start of every resize gesture — see <see cref="PanelResizeController"/>'s matching parameter.</param>
    /// <param name="clearDragObstacles">Clears <see cref="PanelDragMoveBehavior"/>'s obstacle snapshot so a resize never inherits a stale one.</param>
    /// <param name="snapToGrid">Shared drop-time grid snap — see <see cref="PanelDragMoveBehavior.SnapToGrid"/>.</param>
    /// <param name="titleBarHeight">The panel's fixed title-bar height, used to strip chrome before quantising the resize gesture's height.</param>
    /// <param name="diagnostics">Log-only observer of the resize gesture lifecycle — never influences behavior.</param>
    /// <param name="tracker">The panel's shared <see cref="PanelInteractionTracker"/> (ADR-021).</param>
    /// <param name="nativeSizingService">Installs/removes the <c>WM_SIZING</c>-loop window hook (see <see cref="AttachTo"/>).</param>
    /// <param name="isResizeEnabled">
    /// Roll-up gate: when it returns <see langword="false"/>, <see cref="HitTest"/> always reports
    /// <see cref="NativeSizingEdge.None"/>, mirroring the Thumb path's
    /// <c>IResizeAffordance</c>-driven overlay disable (plan §1.6, R-9).
    /// </param>
    /// <param name="gridHost">
    /// P04 (docs/PLANS/workspace-grid-03-04-integration.md §3.3/§3.4): the ADR-028 workspace-grid seam.
    /// The public constructor supplies <c>window as IPanelSizingGridHost</c>; headless tests supply a
    /// fake (or <see langword="null"/>, which means "LegacyFreeform" and keeps every pre-P04 test's
    /// expectations byte-for-byte).
    /// </param>
    /// <param name="isFreeResizeModifierDown">
    /// P04 fix-cycle 1: test seam for the <c>Alt</c> free-resize modifier, forwarded verbatim to
    /// <see cref="ResizeGestureSession"/>. <see langword="null"/> (production) means "read the real
    /// keyboard" — valid on this path too, because <c>WM_SIZING</c> is pumped on the UI thread.
    /// </param>
    internal NativePanelResizeController(
        Func<Rect> getBounds,
        Action<double> setWidth,
        Action<Rect> setBounds,
        Action<Action> scheduleDeferredPersist,
        IAppSettings appSettings,
        IWindowManager windowManager,
        Func<ChildWindowModel?> getModel,
        Action onResizeGestureStarting,
        Action clearDragObstacles,
        Action snapToGrid,
        double titleBarHeight,
        IGestureDiagnostics diagnostics,
        PanelInteractionTracker tracker,
        INativePanelSizingService nativeSizingService,
        Func<bool> isResizeEnabled,
        IPanelSizingGridHost? gridHost = null,
        Func<bool>? isFreeResizeModifierDown = null)
    {
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(setWidth);
        ArgumentNullException.ThrowIfNull(setBounds);
        ArgumentNullException.ThrowIfNull(scheduleDeferredPersist);
        ArgumentNullException.ThrowIfNull(appSettings);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(getModel);
        ArgumentNullException.ThrowIfNull(onResizeGestureStarting);
        ArgumentNullException.ThrowIfNull(clearDragObstacles);
        ArgumentNullException.ThrowIfNull(snapToGrid);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(nativeSizingService);
        ArgumentNullException.ThrowIfNull(isResizeEnabled);

        _getBounds = getBounds;
        _setWidth = setWidth;
        _setBounds = setBounds;
        _scheduleDeferredPersist = scheduleDeferredPersist;
        _appSettings = appSettings;
        _windowManager = windowManager;
        _getModel = getModel;
        _onResizeGestureStarting = onResizeGestureStarting;
        _clearDragObstacles = clearDragObstacles;
        _snapToGrid = snapToGrid;
        _titleBarHeight = titleBarHeight;
        _diagnostics = diagnostics;
        _session = new ResizeGestureSession(tracker, isFreeResizeModifierDown);
        _nativeSizingService = nativeSizingService;
        _isResizeEnabled = isResizeEnabled;
        _gridHost = gridHost;
    }

    /// <summary>
    /// Installs the native sizing-loop hook on the panel's HWND (call once, from
    /// <c>ChildWindow.OnSourceInitialized</c>, after <c>DesktopIntegrationService.EmbedInDesktop</c>).
    /// Idempotent per instance: a second call detaches the previous token first.
    /// </summary>
    /// <param name="hwnd">The panel's window handle.</param>
    public void AttachTo(IntPtr hwnd)
    {
        _attachToken?.Dispose();
        _hwnd = hwnd;
        _attachToken = _nativeSizingService.Attach(hwnd, this);
    }

    /// <summary>Removes the native sizing-loop hook — equivalent to, and implemented via, <see cref="Dispose"/>.</summary>
    public void Detach() => Dispose();

    /// <inheritdoc />
    public NativeSizingEdge HitTest(NativePointPx screenPoint, NativeRectPx windowRect, double dpiScale)
    {
        if (!_isResizeEnabled())
            return NativeSizingEdge.None;

        return NativeHitTestMapper.Map(screenPoint, windowRect, dpiScale, EdgeDip, CornerDip, enabled: true);
    }

    /// <inheritdoc />
    public bool SizeMoveEntered(NativeSizingEdge edge, NativeRectPx windowRect, double dpiScale)
    {
        var model = _getModel();

        // Literal plan-table order (§1.5): the raw wire-level diagnostic fires first, then the same
        // "commit rename / clear drag obstacles" side effects the Thumb path runs — but here
        // unconditionally, before TryBegin is known to succeed. Unlike
        // PanelResizeController.OnResizeDragStarted (which defers these two calls until AFTER TryBegin
        // accepts the gesture, see its "finalization MINOR m-4" remark), the native path has no
        // pre-TryBegin rejection point equivalent to a capture-ownership mismatch: WM_ENTERSIZEMOVE only
        // ever arrives after the system has already committed to a sizing loop.
        RecordSizeMoveEntered(model?.Id, edge);
        _onResizeGestureStarting();
        _clearDragObstacles();

        var resizeEdge = NativeSizingEdgeMap.ToResizeEdge(edge);
        if (resizeEdge is null)
        {
            // Defensive only: WM_ENTERSIZEMOVE is only ever entered from an HT*-sizing HitTest result
            // (D-5), so `edge` should never actually be None here.
            //
            // M-4: a rejected cycle never reaches EndResize (no session was ever begun) AND
            // NativePanelSizingService.OnEnterSizeMove leaves `_cycleAccepted == false`, which gates OFF
            // every later WM_SIZING/WM_EXITSIZEMOVE/WM_CAPTURECHANGED callback for this cycle (see
            // PanelSizingAttachment.OnSizing/EndCycle) — so this SizeMoveEntered would otherwise never get
            // a matching SizeMoveExited in the triage log. Pair it right here.
            RecordSizeMoveExited(model?.Id, NativeSizingCoordinateMapper.ToDip(windowRect, dpiScale));
            return false;
        }

        var startBoundsDip = NativeSizingCoordinateMapper.ToDip(windowRect, dpiScale);

        // P04: ONE grid-state resolution per gesture, exactly like the Thumb path's (§1.3). NOTE
        // (MANUAL VERIFY MV-1): startBoundsDip comes from GetWindowRect, i.e. SCREEN space, while
        // IPanelSizingGridHost.ResolveGridState is specified in Window.Left/Top space — the two are
        // identical while DesktopCoordinateSpace.Offset is (0,0) (its default, and the only value
        // anything sets today, OQ-1 = A). If MV-1 ever turns calibration on, this call needs
        // DesktopCoordinateSpace.ToWindowSpace(startBoundsDip) here.
        // NOTE (P04 fix-cycle 1, MINOR): resolved BEFORE TryBegin, so a refused gesture still performs
        // the resolver's lazy span-migration side effect — deliberate and harmless since MAJOR-3 made
        // that migration idempotent; the constraint floors on the next line need the state first.
        var gridState = _gridHost?.ResolveGridState(startBoundsDip) ?? PanelGridState.Legacy;
        var constraints = ResizeConstraintsFactory.BuildConstraints(model, _appSettings.SnapResizeToIconGrid, _titleBarHeight, gridState, _appSettings.GridSize);

        if (!_session.TryBegin(resizeEdge.Value, startBoundsDip, constraints, gridState, PanelSizingGridSupport.CollectObstacles(_gridHost, gridState, _resizeObstacles)))
        {
            // M-4: same reasoning as the `resizeEdge is null` branch above — TryBegin refusing the
            // gesture (tracker already owned by e.g. a drag-move) leaves `_cycleAccepted == false` at the
            // service, so no later callback will ever pair this SizeMoveEntered.
            RecordSizeMoveExited(model?.Id, startBoundsDip);
            return false;
        }

        _dpiScale = dpiScale;
        _startWindowRectPx = windowRect;
        _originResolved = false;
        _lastAppliedBoundsDip = startBoundsDip;

        _diagnostics.ResizeStarted(
            model?.Id,
            resizeEdge.Value.ToString(),
            startBoundsDip,
            model?.ViewMode ?? PanelViewMode.List,
            model?.IconSize ?? 0d,
            _appSettings.SnapResizeToIconGrid,
            constraints.SnapCellWidth,
            constraints.SnapCellHeight,
            NativeCaptureOwnerLabel);

        return true;
    }

    /// <inheritdoc />
    public NativeRectPx ProposeBounds(NativeSizingEdge edge, NativeRectPx proposed)
    {
        var resizeEdge = NativeSizingEdgeMap.ToResizeEdge(edge);

        if (!_session.IsActive || resizeEdge is null || resizeEdge.Value != _session.ActiveEdge)
        {
            // Unexpected source change (mirrors PanelResizeController.OnResizeDragDelta's stale-drag
            // self-heal): either no gesture is active, or this WM_SIZING's own WMSZ_* wParam edge
            // disagrees with the edge WM_ENTERSIZEMOVE recorded. End whatever the session thinks is
            // active and do not touch the system's proposed rect.
            // M-3: this EndResize can run while WM_SIZING is still being pumped (mouse button still down,
            // DefWindowProc's nested modal loop still owns the message pump) — defer any persist it
            // triggers off the loop's call stack, see EndResize's deferPersist parameter.
            EndResize(ResizeEndReason.SourceChanged, deferPersist: true);
            return proposed;
        }

        Rect applied;
        double deltaX, deltaY;

        try
        {
            // R-3/C-1: self-calibrate the WM_SIZING coordinate space once per gesture, on its first
            // message — see NativeSizingOriginResolver's remarks. `_originOffsetPx` is defined as
            // `screenSpace - sizingSpace` (windowRectScreen - firstSizingRect, see Resolve's doc comment
            // and NativeSizingOriginResolverTests), i.e. the constant to ADD to a sizing-space rect to
            // bring it into screen-space. Every subsequent proposed rect of THIS gesture is shifted by
            // the same constant offset — on the way IN below, and symmetrically SUBTRACTED again on the
            // way OUT (see this method's final `return` statement): `proposed` and
            // the value this method returns both travel in `WM_SIZING`'s own coordinate space (whatever
            // that is), while everything in between (`_session`/`ResizeGeometryCalculator`,
            // `_lastAppliedBoundsDip`, `_startWindowRectPx`-derived `startBoundsDip` from
            // `SizeMoveEntered`) is screen-space DIP, because `GetWindowRect` — the ONLY thing
            // `startBoundsDip` and `_lastAppliedBoundsDip`'s seed value are ever built from — is always
            // screen-space regardless of parentage. Converting `proposed` to screen-space here is what
            // makes reusing that screen-space session state correct; converting the result back to
            // sizing-space before returning is what `NativePanelSizingService.WriteRect` requires, since
            // Win32 expects `WM_SIZING`'s response rect in the SAME coordinate space the request arrived
            // in. Before this fix, the offset was applied on the way in but never undone on the way out,
            // so the panel would jump by `_originOffsetPx` on the very first frame of any gesture where
            // the two spaces actually differ (R-3's WorkerW-reparented case) — see the commit report for
            // the full derivation.
            if (!_originResolved)
            {
                _originOffsetPx = NativeSizingOriginResolver.Resolve(_startWindowRectPx, proposed);
                _originResolved = true;
            }

            var adjustedPx = new NativeRectPx(
                proposed.Left + _originOffsetPx.X,
                proposed.Top + _originOffsetPx.Y,
                proposed.Right + _originOffsetPx.X,
                proposed.Bottom + _originOffsetPx.Y);

            var targetDip = NativeSizingCoordinateMapper.ToDip(adjustedPx, _dpiScale);

            // D-4: WM_SIZING already carries the gesture's absolute proposed extent (origin + the
            // system's own running total of cursor movement since WM_ENTERSIZEMOVE) — exactly the value
            // ResizeGeometryCalculator.Accumulate would reach after N incremental Thumb.DragDelta calls.
            // Build the ONE synthetic Vector that makes ResizeGestureSession.TryUpdate's own Accumulate
            // call land exactly on `targetDip`, instead of adding a second entry point to
            // ResizeGestureSession/ResizeGeometryCalculator (plan §3 forbids changing either): Accumulate
            // computes `next = current +/- delta` using the exact same edge.AnchorsRight()/AnchorsBottom()
            // sign convention used below, so inverting it here (`delta = sign * (target - current)`)
            // guarantees `current + sign*delta == target` regardless of any prior rounding drift — the
            // native path recomputes from `targetDip` fresh on every single WM_SIZING, it never adds this
            // delta on top of a previous delta the way N chained Thumb deltas would. See the commit-C4
            // report for the full equivalence argument (T-EQ-* pins it bit-for-bit in the next commit).
            //
            // m-3 (finalization MINOR): only the axis the edge actually affects gets a real delta — the
            // other axis's `targetDip.Width/Height` is translation-invariant chatter (rounding noise from
            // the DIP round-trip, not a real cursor movement on that axis) and would otherwise show up as
            // a spurious non-zero value in the ResizeDelta diagnostic below, unlike the Thumb path, whose
            // deltaX/deltaY are always the REAL per-frame mouse deltas Thumb itself reports.
            // ResizeGeometryCalculator.Accumulate already ignores the inactive axis internally (it only
            // adds `delta.X`/`delta.Y` when AffectsWidth()/AffectsHeight() is true), so zeroing it here
            // does not change the applied geometry — only what gets logged.
            var signX = resizeEdge.Value.AnchorsRight() ? -1d : 1d;
            deltaX = resizeEdge.Value.AffectsWidth() ? signX * (targetDip.Width - _session.Raw.Width) : 0d;

            var signY = resizeEdge.Value.AnchorsBottom() ? -1d : 1d;
            deltaY = resizeEdge.Value.AffectsHeight() ? signY * (targetDip.Height - _session.Raw.Height) : 0d;

            if (!_session.TryUpdate(resizeEdge.Value, new Vector(deltaX, deltaY), _lastAppliedBoundsDip, out applied))
            {
                EndResize(ResizeEndReason.SourceChanged, deferPersist: true);
                return proposed;
            }

            _lastAppliedBoundsDip = applied;
        }
        catch (Exception ex)
        {
            // Privacy invariant (diagnostics-log-events.md): NEVER log ex.Message. Mirrors
            // PanelResizeController.OnResizeDragDelta's catch — end the gesture as Failed (ShouldPersist
            // becomes false) and hand the system back its OWN proposed rect unchanged rather than risk
            // writing a half-computed one into WM_SIZING's lParam.
            var exceptionType = ex.GetType().Name;
            System.Diagnostics.Debug.WriteLine($"NativePanelResizeController.ProposeBounds failed: {exceptionType}");
            EndResize(ResizeEndReason.Failed, exceptionType, deferPersist: true);
            return proposed;
        }

        // Kept out of the geometry try/catch above, in its own try/catch — a failure in this log-only
        // diagnostics call must never retroactively turn an already-computed geometry update into a
        // Failed gesture (mirrors PanelResizeController.OnResizeDragDelta's "finalization MINOR m-2").
        try
        {
            _diagnostics.ResizeDelta(
                _getModel()?.Id,
                resizeEdge.Value.ToString(),
                deltaX,
                deltaY,
                _session.Raw.Width,
                _session.Raw.Height,
                applied);
        }
        catch
        {
            // Swallowed intentionally — log-only diagnostics must never affect gesture lifecycle.
        }

        // C-1: symmetric inverse of the input-side offset applied above — `applied`/the ToPx conversion
        // just below are in screen-space (StartBounds/`_lastAppliedBoundsDip` are seeded from
        // `GetWindowRect`, always screen-space), but this return value is written verbatim into
        // `WM_SIZING`'s lParam by `NativePanelSizingService`, which expects the SAME coordinate space
        // `proposed` arrived in. `_originOffsetPx == screenSpace - sizingSpace` (see the remark above), so
        // `sizingSpace == screenSpace - offset` — subtract it back out here. When the two spaces coincide
        // (top-level, offset == (0,0)) this is a no-op, matching the pre-fix behavior exactly.
        var screenPx = NativeSizingCoordinateMapper.ToPx(applied, _dpiScale);
        return new NativeRectPx(
            screenPx.Left - _originOffsetPx.X,
            screenPx.Top - _originOffsetPx.Y,
            screenPx.Right - _originOffsetPx.X,
            screenPx.Bottom - _originOffsetPx.Y);
    }

    /// <inheritdoc />
    public void SizeMoveExited(NativeRectPx finalRect) => EndResize(ResizeEndReason.Completed);

    /// <inheritdoc />
    public void SizeMoveAborted() => EndResize(ResizeEndReason.CaptureLost, deferPersist: true);

    /// <inheritdoc />
    public void DpiChanged(double newScale, NativeRectPx suggestedRect)
    {
        // D-6: NOT the source of truth for DPI/geometry changes (that is
        // IMonitorService.MonitorsChanged) — this is a best-effort observational path that may never
        // fire for a WorkerW-reparented child window under the app's current (non-PMv2) DPI awareness.
        // If a gesture happens to be active when it does fire, end it safely rather than attempt to
        // rescale an in-progress gesture on the fly. MANUAL VERIFY (plan §6.2 rows 24/25, R-7).
        if (_session.IsActive)
            EndResize(ResizeEndReason.SourceChanged, deferPersist: true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Called from <c>ChildWindow.OnClosing</c>/<c>Unloaded</c> — outside the native modal sizing loop
    /// (window teardown, not a sizing-loop callback), so unlike <see cref="SizeMoveAborted"/>/
    /// <see cref="DpiChanged"/> this does not need <c>deferPersist</c>.
    /// </remarks>
    public void AbortResize(ResizeEndReason reason) => EndResize(reason);

    /// <inheritdoc />
    public void SnapWidthToGrid()
    {
        var model = _getModel();
        if (model is null || model.ViewMode != PanelViewMode.Grid || !_appSettings.SnapResizeToIconGrid) return;

        // F-2 (§3.7, DoD D4-2) — see PanelResizeController.SnapWidthToGrid's matching remark for the
        // full rationale; the gate lives in both copies of this method because the method itself is
        // duplicated (by that method's own long-standing precedent), and the two must not diverge.
        if (PanelSizingGridSupport.IsGridAligned(_gridHost, _getBounds())) return;

        var cell = GridMetrics.CellSize(model.IconSize);

        // M-2 fix-cycle amendment (adr/ADR-029-icon-layout-policy.md Решение 4) — see
        // PanelResizeController.SnapWidthToGrid's matching remark; duplicated here rather than shared,
        // matching that method's own precedent of keeping this one-liner duplicated rather than reaching
        // for a panel reference it does not have.
        var chrome = GridMetrics.ContentHorizontalChrome(model.IconLayoutPolicy, SystemParameters.VerticalScrollBarWidth);
        var content = Math.Max(0d, _getBounds().Width - chrome);

        // Round the snapped width UP to a whole pixel (Math.Ceiling), never down — see
        // PanelResizeController.SnapWidthToGrid's matching remark (finalization MAJOR M-1); duplicated
        // here rather than shared, matching that method's own precedent of keeping this one-liner
        // duplicated rather than reaching for a panel reference it does not have.
        _setWidth(Math.Ceiling(Math.Max(
            GridMetrics.MinPanelWidth(model.IconSize, model.IconLayoutPolicy, SystemParameters.VerticalScrollBarWidth),
            AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(cell)) + chrome)));
    }

    /// <summary>Removes the native sizing-loop hook. Idempotent.</summary>
    public void Dispose()
    {
        _attachToken?.Dispose();
        _attachToken = null;
    }

    /// <summary>Builds the public constructor's <c>getBounds</c> delegate — also where <c>window</c>'s null check actually happens (evaluated eagerly, as a constructor-initializer argument, before <c>: this(...)</c> runs).</summary>
    private static Func<Rect> GetBoundsDelegate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return () => new Rect(window.Left, window.Top, window.Width, window.Height);
    }

    /// <summary>Builds the public constructor's <c>setWidth</c> delegate.</summary>
    private static Action<double> SetWidthDelegate(Window window) => width => window.Width = width;

    /// <summary>
    /// Builds the public constructor's <c>setBounds</c> delegate (ADR-011 Amendment 2026-09-24, resize
    /// extension) — writes all four of <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c>, unlike
    /// <see cref="SetWidthDelegate"/>.
    /// </summary>
    private static Action<Rect> SetBoundsDelegate(Window window) => bounds =>
    {
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
    };

    /// <summary>Builds the public constructor's <c>scheduleDeferredPersist</c> delegate — the real WPF dispatcher, <see cref="DispatcherPriority.Background"/>, matching pre-seam behavior exactly.</summary>
    private static Action<Action> ScheduleDeferredPersistDelegate(Window window) =>
        action => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    /// <summary>
    /// HORIZONTAL edge padding (<c>m</c>) for <see cref="SnapWidthToGrid"/> — see
    /// <see cref="PanelResizeController"/>'s identically-named private method for the full doc comment;
    /// duplicated on purpose (see <see cref="SnapWidthToGrid"/>'s remark).
    /// </summary>
    private static double ResizeSnapEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    /// <summary>
    /// The single unified end-of-gesture path — mirrors <c>PanelResizeController.EndResize</c>. Safe to
    /// call when no gesture is active: <see cref="ResizeGestureSession.End"/> is idempotent, so this is
    /// then a no-op (and returns <see langword="false"/>).
    /// </summary>
    /// <param name="reason">Why the gesture is ending.</param>
    /// <param name="exceptionType">
    /// The failing exception's <c>Type.Name</c> when <paramref name="reason"/> is
    /// <see cref="ResizeEndReason.Failed"/> — <see langword="null"/> for every other reason.
    /// </param>
    /// <param name="deferPersist">
    /// M-3 (finalization MAJOR): <see langword="true"/> when this call site can run WHILE the native
    /// modal sizing loop is still pumping messages — <see cref="ProposeBounds"/>'s own
    /// <see cref="ResizeEndReason.SourceChanged"/>/<see cref="ResizeEndReason.Failed"/> calls (mid
    /// <c>WM_SIZING</c>, mouse still down), <see cref="SizeMoveAborted"/> (<c>WM_CAPTURECHANGED</c> can
    /// fire mid-loop — Alt+Tab/menu while still dragging), and <see cref="DpiChanged"/>'s
    /// <see cref="ResizeEndReason.SourceChanged"/> (a DPI change mid-drag). A previous version of this
    /// comment claimed persistence "never runs mid-loop" — false for exactly these three call sites: when
    /// <c>outcome.ShouldPersist</c> is also true there, a synchronous <c>_snapToGrid()</c> would run
    /// anti-overlap/grid-snap against <see cref="PanelDragMoveBehavior"/>'s obstacle snapshot, which
    /// <see cref="SizeMoveEntered"/> already cleared for THIS gesture via <c>clearDragObstacles()</c> —
    /// i.e. against an empty set, while the OS still owns the panel's geometry. Only
    /// <see cref="SizeMoveExited"/>'s <see cref="ResizeEndReason.Completed"/> path (the system's own
    /// <c>WM_EXITSIZEMOVE</c> — R-5's designated, intentionally-synchronous persist point) and
    /// <see cref="AbortResize"/>'s <c>Closing</c>/<c>Unloaded</c> paths (window teardown, not a
    /// sizing-loop callback) pass <see langword="false"/>. When <see langword="true"/>, the persist step
    /// is pushed onto the dispatcher queue at <see cref="DispatcherPriority.Background"/> — after the
    /// nested loop has actually returned control — instead of running inline.
    /// </param>
    /// <returns><see langword="true"/> if a gesture was actually active and this call ended it.</returns>
    private bool EndResize(ResizeEndReason reason, string? exceptionType = null, bool deferPersist = false)
    {
        var outcome = _session.End(reason);
        if (!outcome.WasActive) return false;

        // Persist only when geometry was actually applied (same ShouldPersist gate as the Thumb path).
        // See the deferPersist parameter doc above for why some call sites cannot persist inline.
        if (outcome.ShouldPersist)
        {
            // P04 fix-cycle 1: the edge is captured BY VALUE here — by the time a deferred persist runs,
            // _session may already belong to a new gesture on a different edge.
            var committedEdge = outcome.Edge;

            if (deferPersist)
                _scheduleDeferredPersist(() => PersistAfterGesture(committedEdge));
            else
                PersistAfterGesture(committedEdge);
        }

        // Reads the panel's bounds AFTER the persistence above, same as PanelResizeController.EndResize —
        // the native sizing loop already wrote the OS-committed geometry back to this same Window (WPF
        // syncs Window.Width/Height/Left/Top from the underlying HWND), so this is the actually-committed
        // geometry, not a locally-tracked shadow copy. Even when persistence itself was just deferred
        // above, this geometry read is NOT: it is only diagnostics (read-only), and the OS has already
        // committed the geometry to the HWND by the time ANY of EndResize's call sites run (including the
        // mid-loop ones — WM_SIZING's lParam round-trip through NativePanelSizingService.WriteRect is what
        // commits each intermediate frame).
        var finalBoundsDip = _getBounds();

        _diagnostics.ResizeCompleted(
            _getModel()?.Id,
            outcome.Edge.ToString(),
            canceled: false, // R-8: ESC during the native loop is indistinguishable from an ordinary completion.
            finalBoundsDip,
            outcome.Reason.ToString(),
            exceptionType);

        // M-4: unconditional on every path that actually ended an active session — previously this was
        // only recorded from the WM_EXITSIZEMOVE-triggered SizeMoveExited(NativeRectPx) overload, so any
        // early end (SourceChanged/Failed/CaptureLost) left its SizeMoveEntered unpaired in the triage
        // log. Reuses the same finalBoundsDip as ResizeCompleted above rather than a second geometry read.
        RecordSizeMoveExited(_getModel()?.Id, finalBoundsDip);

        return true;
    }

    /// <summary>
    /// The actual persist step gated by <see cref="ResizeEndOutcome.ShouldPersist"/> in
    /// <see cref="EndResize"/> — split out so it can run either inline or via
    /// <see cref="Dispatcher.BeginInvoke(DispatcherPriority, Delegate)"/> (see the <c>deferPersist</c>
    /// parameter).
    /// </summary>
    /// <param name="committedEdge">
    /// The edge/corner the just-ended gesture was resizing — captured BY VALUE in <see cref="EndResize"/>
    /// (a deferred persist may run after <c>_session</c> already belongs to a new gesture). Used by
    /// <see cref="PanelSizingGridSupport.PersistSpans"/> (P04 fix-cycle 1) and by
    /// <see cref="PanelSizingGridSupport.NotifyHeightResizeCommitted"/> to decide whether this was a
    /// HEIGHT-affecting gesture (P06 fix-cycle MAJOR-4).
    /// </param>
    private void PersistAfterGesture(ResizeEdge committedEdge)
    {
        // P04-G6: spans are persisted here — inside PersistAfterGesture, NOT in EndResize — so the
        // deferred path (deferPersist: true, M-3) writes them only after the native modal sizing loop
        // has actually returned control, exactly like the SaveWindowsState below it.
        //
        // P04 fix-cycle 1, MAJOR-1: and BEFORE _snapToGrid(), not after — PersistSpans is what puts the
        // final geometry back inside the work area by SHRINKING the moving edge, which leaves nothing for
        // SnapToGrid's own SafeClamp to "fix" by TRANSLATING the panel off its anchor. See
        // PanelResizeController.EndResize's matching remark.
        PanelSizingGridSupport.PersistSpans(_gridHost, _getBounds(), committedEdge, _commitObstacles);

        // ADR-011 Amendment 2026-09-24 (resize extension): see PanelResizeController.EndResize's
        // identical step for the full rationale — this is the native path's equivalent, run at the same
        // "geometry actually committed" point (deferred or not, exactly like PersistSpans above).
        var beforeSizeGridNormalize = _getBounds();
        var sizeGridNormalized = PanelSizingGridSupport.NormalizeCommittedSize(
            _gridHost,
            _getModel(),
            _appSettings.SnapResizeToIconGrid,
            _titleBarHeight,
            _appSettings.GridSize,
            beforeSizeGridNormalize,
            committedEdge);

        if (sizeGridNormalized != beforeSizeGridNormalize)
            _setBounds(sizeGridNormalized);

        _snapToGrid();

        _windowManager.SaveWindowsState();

        // P06 fix-cycle MAJOR-4: see PanelResizeController.EndResize's identical call for the full
        // rationale — this is the native path's equivalent, at the same "geometry actually committed"
        // point (deferred or not).
        PanelSizingGridSupport.NotifyHeightResizeCommitted(_gridHost, committedEdge);
    }

    private void RecordSizeMoveEntered(Guid? panelId, NativeSizingEdge edge)
    {
        try
        {
            _diagnostics.SizeMoveEntered(panelId, _hwnd.ToInt64(), edge.ToString());
        }
        catch
        {
            // Swallowed intentionally — log-only diagnostics must never affect gesture lifecycle.
        }
    }

    private void RecordSizeMoveExited(Guid? panelId, Rect finalBoundsDip)
    {
        try
        {
            _diagnostics.SizeMoveExited(panelId, _hwnd.ToInt64(), finalBoundsDip);
        }
        catch
        {
            // Swallowed intentionally — log-only diagnostics must never affect gesture lifecycle.
        }
    }
}
