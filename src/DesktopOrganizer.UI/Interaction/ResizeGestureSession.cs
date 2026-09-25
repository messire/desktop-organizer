using System.Windows;
using System.Windows.Input;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// Why a <see cref="ResizeGestureSession"/> ended. Purely descriptive — every value is a legitimate,
/// expected way for a resize gesture to stop (docs/PLANS/phase-1-resize-stabilization.md §2.6); none of
/// them is treated as an application error by the session itself.
/// </summary>
public enum ResizeEndReason
{
    /// <summary><c>Thumb.DragCompleted</c> with <c>Canceled == false</c> — the ordinary mouse-up end.</summary>
    Completed,

    /// <summary><c>Thumb.DragCompleted</c> with <c>Canceled == true</c> — the <c>Thumb</c> lost capture mid-gesture.</summary>
    Canceled,

    /// <summary>The <c>ResizeOverlay</c>'s bubbling <c>Mouse.LostMouseCapture</c> fired while this session was still active.</summary>
    CaptureLost,

    /// <summary>
    /// A new gesture started on a different edge than the one this session is tracking (stale-drag
    /// self-heal, or a <see cref="ResizeGeometryCalculator.Apply"/> input mismatch) — see H4.
    /// </summary>
    SourceChanged,

    /// <summary>The panel's <c>ChildWindow.Unloaded</c> fired while this session was still active.</summary>
    Unloaded,

    /// <summary>The panel is closing (<c>ChildWindow.OnClosing</c>) while this session was still active.</summary>
    Closing,

    /// <summary>An exception was thrown while applying a delta — see <see cref="ResizeEndOutcome.ShouldPersist"/>.</summary>
    Failed,
}

/// <summary>
/// The result of calling <see cref="ResizeGestureSession.End"/> — everything the WPF-facing caller
/// (<see cref="Behaviors.PanelResizeController"/>) needs to decide what to do next, without reaching
/// back into the session's private state.
/// </summary>
/// <param name="WasActive">
/// <see langword="false"/> means this call was a no-op (the session was already ended, or never began)
/// — the caller must do NOTHING else: no capture release, no persistence, no diagnostics. This is what
/// makes repeated/re-entrant <see cref="ResizeGestureSession.End"/> calls safe.
/// </param>
/// <param name="ShouldPersist">
/// <see langword="true"/> only when at least one delta was actually applied to the panel's geometry AND
/// <paramref name="Reason"/> is not <see cref="ResizeEndReason.Failed"/> — see
/// <see cref="ResizeGestureSession.End"/>'s remarks for the full rationale (plan §1.3).
/// </param>
/// <param name="Edge">The edge/corner this gesture was resizing.</param>
/// <param name="Reason">The reason <see cref="ResizeGestureSession.End"/> was called with.</param>
/// <param name="AppliedDeltaCount">How many <see cref="ResizeGestureSession.TryUpdate"/> calls actually applied a delta during this gesture.</param>
public readonly record struct ResizeEndOutcome(
    bool WasActive,
    bool ShouldPersist,
    ResizeEdge Edge,
    ResizeEndReason Reason,
    int AppliedDeltaCount);

/// <summary>
/// The explicit, idempotent state machine of ONE resize gesture — Window-free (knows nothing about
/// <c>Thumb</c>/<c>Window</c>), UI-thread-affine, no locking (docs/PLANS/phase-1-resize-stabilization.md
/// §2.6). Owns the ADR-021 "only one gesture owns capture at a time" invariant for resize via the shared
/// <see cref="PanelInteractionTracker"/>, and "every Begin has exactly one logical End, even if platform
/// events are duplicated/missing" via <see cref="TryBegin"/>/<see cref="End"/>'s idempotency guards.
/// </summary>
/// <remarks>
/// One instance is created per panel (alongside its <see cref="PanelInteractionTracker"/>) and reused
/// across every resize gesture that panel ever performs — <see cref="TryBegin"/> resets all per-gesture
/// state, so there is no need to construct a new session per drag.
/// </remarks>
public sealed class ResizeGestureSession
{
    private readonly PanelInteractionTracker _tracker;

    // P04 fix-cycle 1 (orchestrator decision 2026-09-22, variant B): the Alt free-resize modifier, read
    // FRESH on every frame that asks — never snapshotted at TryBegin, exactly like
    // PanelDragMoveBehavior.IsFreeMoveModifierDown, so pressing/releasing Alt takes effect on the very
    // next WM_SIZING/DragDelta. Injectable purely for tests (a headless xUnit thread has no real
    // keyboard); production always gets DefaultFreeResizeModifier.
    private readonly Func<bool> _isFreeResizeModifierDown;

    private bool _isActive;
    private bool _ending;
    private ResizeEdge _activeEdge;
    private Rect _startBounds;
    private ResizeRawExtent _raw;
    private double _anchorRight;
    private double _anchorBottom;
    private ResizeConstraints _constraints;
    private int _appliedDeltaCount;

    // P04 (docs/PLANS/workspace-grid-03-04-integration.md §3.4): the ADR-028 workspace-grid context for
    // THIS gesture — snapshotted by TryBegin exactly like _constraints, never re-read per frame. Both
    // stay at their "legacy" values (IsGridAligned == false / empty obstacle list) for a
    // LegacyFreeform panel, which is what keeps that mode bit-for-bit unchanged.
    private PanelGridState _gridState = PanelGridState.Legacy;
    private IReadOnlyList<Rect> _obstacles = Array.Empty<Rect>();

    /// <summary>
    /// Initializes a new instance of the <see cref="ResizeGestureSession"/> class.
    /// </summary>
    /// <param name="tracker">The panel's shared <see cref="PanelInteractionTracker"/> (ADR-021).</param>
    /// <param name="isFreeResizeModifierDown">
    /// P04 fix-cycle 1: reads whether the <c>Alt</c> free-resize modifier is held RIGHT NOW. Defaults to
    /// <see cref="DefaultFreeResizeModifier"/> (the real <see cref="Keyboard.Modifiers"/>), which is what
    /// production always uses; a test supplies its own so the Alt branch is reachable without a real
    /// keyboard. Called once per <see cref="TryUpdate"/> and never cached — see
    /// <see cref="_isFreeResizeModifierDown"/>.
    /// </param>
    public ResizeGestureSession(PanelInteractionTracker tracker, Func<bool>? isFreeResizeModifierDown = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _isFreeResizeModifierDown = isFreeResizeModifierDown ?? DefaultFreeResizeModifier;
    }

    /// <summary><see langword="true"/> while a resize gesture is in progress (between a successful <see cref="TryBegin"/> and its matching <see cref="End"/>).</summary>
    public bool IsActive => _isActive;

    /// <summary>The edge/corner being resized. Valid only while <see cref="IsActive"/> (or immediately after <see cref="End"/>, describing the just-ended gesture).</summary>
    public ResizeEdge ActiveEdge => _activeEdge;

    /// <summary>The panel's on-screen <see cref="Rect"/> snapshotted once at <see cref="TryBegin"/>. Valid only while <see cref="IsActive"/>.</summary>
    public Rect StartBounds => _startBounds;

    /// <summary>The gesture's raw (unquantised) accumulated extent (<see cref="ResizeGeometryCalculator.Accumulate"/>). Valid only while <see cref="IsActive"/>.</summary>
    public ResizeRawExtent Raw => _raw;

    /// <summary>The fixed right-edge anchor snapshotted at <see cref="TryBegin"/> (<c>StartBounds.Right</c>). Valid only while <see cref="IsActive"/>.</summary>
    public double AnchorRight => _anchorRight;

    /// <summary>The fixed bottom-edge anchor snapshotted at <see cref="TryBegin"/> (<c>StartBounds.Bottom</c>). Valid only while <see cref="IsActive"/>.</summary>
    public double AnchorBottom => _anchorBottom;

    /// <summary>The snap/chrome/floor configuration snapshotted at <see cref="TryBegin"/>, fixed for the gesture's whole duration. Valid only while <see cref="IsActive"/>.</summary>
    public ResizeConstraints Constraints => _constraints;

    /// <summary>
    /// The ADR-028 workspace-grid context snapshotted at <see cref="TryBegin"/> (P04, §3.4). Valid only
    /// while <see cref="IsActive"/>; <see cref="PanelGridState.Legacy"/> for a
    /// <see cref="Models.PanelGeometryMode.LegacyFreeform"/> gesture.
    /// </summary>
    public PanelGridState GridState => _gridState;

    /// <summary>
    /// The anti-overlap obstacle snapshot taken once at <see cref="TryBegin"/> (P04-G3) — the other
    /// panels' bounds a <see cref="Models.PanelGeometryMode.GridAligned"/> resize may not grow into.
    /// Always empty for <see cref="Models.PanelGeometryMode.LegacyFreeform"/>, where resize anti-overlap
    /// stays an open ADR-017 backlog item (ADR-028 "Границы объёма").
    /// </summary>
    public IReadOnlyList<Rect> Obstacles => _obstacles;

    /// <summary>
    /// Attempts to start a new resize gesture. Fails (returns <see langword="false"/>, no state
    /// changed) if a gesture is already active (double <c>DragStarted</c> without a matching
    /// <see cref="End"/> — the caller is expected to self-heal via <see cref="End"/> first, see
    /// <see cref="Behaviors.PanelResizeController"/>'s stale-drag recovery), or if the shared
    /// <see cref="PanelInteractionTracker"/> refuses to claim <see cref="PanelInteractionState.Resizing"/>
    /// (another gesture — e.g. a title-bar drag — currently owns the panel).
    /// </summary>
    /// <param name="edge">The edge/corner being dragged.</param>
    /// <param name="startBounds">The panel's on-screen <see cref="Rect"/> at the moment the gesture starts.</param>
    /// <param name="constraints">The snap/chrome/floor configuration for this gesture — fixed for its whole duration.</param>
    /// <param name="gridState">
    /// The panel's ADR-028 workspace-grid context (P04-G1) — also fixed for the gesture's whole
    /// duration, exactly like <paramref name="constraints"/>. Defaults to
    /// <see cref="PanelGridState.Legacy"/>, so a caller that does not know about the workspace grid
    /// gets the pre-P04 behaviour unchanged.
    /// </param>
    /// <param name="obstacles">
    /// The other panels' bounds this gesture may not grow into (P04-G3) — snapshotted ONCE here, never
    /// re-collected per frame, the same contract <c>PanelDragMoveBehavior</c> uses for its own drag
    /// obstacles. Only consulted when <paramref name="gridState"/> is grid-aligned;
    /// <see langword="null"/>/empty disables resize anti-overlap entirely (the
    /// <see cref="Models.PanelGeometryMode.LegacyFreeform"/> contract).
    /// </param>
    /// <returns><see langword="true"/> if the gesture actually started.</returns>
    public bool TryBegin(
        ResizeEdge edge,
        Rect startBounds,
        ResizeConstraints constraints,
        PanelGridState gridState = default,
        IReadOnlyList<Rect>? obstacles = null)
    {
        if (_isActive) return false;
        if (!_tracker.TryEnter(PanelInteractionState.Resizing)) return false;

        _activeEdge = edge;
        _startBounds = startBounds;
        _raw = new ResizeRawExtent(startBounds.Width, startBounds.Height);
        _anchorRight = startBounds.Right;
        _anchorBottom = startBounds.Bottom;
        _constraints = constraints;
        _gridState = gridState;
        _obstacles = gridState.IsGridAligned && obstacles is { Count: > 0 } ? obstacles : Array.Empty<Rect>();
        _appliedDeltaCount = 0;
        _isActive = true;

        return true;
    }

    /// <summary>
    /// Applies one <c>Thumb.DragDelta</c> to the gesture's raw extent and computes the panel's next
    /// <see cref="Rect"/> via <see cref="ResizeGeometryCalculator"/>. Fails (returns
    /// <see langword="false"/>, <paramref name="applied"/> set to <paramref name="current"/> unchanged,
    /// no state mutated) if no gesture is active, or if <paramref name="edge"/> does not match the edge
    /// <see cref="TryBegin"/> was called with (an "unexpected source change" — plan §1.2 — the caller
    /// must immediately call <see cref="End"/> with <see cref="ResizeEndReason.SourceChanged"/>).
    /// </summary>
    /// <param name="edge">The edge/corner reported by this delta's originating <c>Thumb</c>.</param>
    /// <param name="delta">The raw mouse delta for this frame (<c>HorizontalChange</c>/<c>VerticalChange</c>).</param>
    /// <param name="current">The panel's on-screen <see cref="Rect"/> right before this delta.</param>
    /// <param name="applied">The panel's next <see cref="Rect"/> to apply.</param>
    /// <returns><see langword="true"/> if the delta was actually applied.</returns>
    public bool TryUpdate(ResizeEdge edge, Vector delta, Rect current, out Rect applied)
    {
        if (!_isActive || edge != _activeEdge)
        {
            applied = current;
            return false;
        }

        _raw = ResizeGeometryCalculator.Accumulate(_raw, edge, delta);

        // P04 fix-cycle 1 (variant B), extended by the ADR-011 Amendment 2026-09-24 (resize extension):
        // Alt suppresses quantisation for THIS FRAME only — the edge tracks the cursor 1:1, like
        // LegacyFreeform with no active grid at all. This now ALSO suppresses ResizeConstraints.
        // SizeGridStep (IAppSettings.GridSize), not just the workspace-grid quantisation below: a
        // free-resize frame gets a copy of _constraints with SizeGridStep zeroed, so
        // ResizeGeometryCalculator.Apply takes its ordinary icon-grid-snap/free path for this one frame.
        // The panel is still put back on the (GridSize and/or workspace) grid at commit — by
        // PanelSizingGridSupport.PersistSpans for a GridAligned panel, and by
        // PanelSizingGridSupport.NormalizeCommittedSize for either mode — so neither grid's "never comes
        // to rest off it" guarantee is weakened; see those methods' remarks and ADR-028 «Модификатор
        // свободного движения».
        var freeFrame = IsFreeResizeFrame();
        var frameConstraints = freeFrame && _constraints.SizeGridStep > 0d
            ? _constraints with { SizeGridStep = 0d }
            : _constraints;

        applied = ResizeGeometryCalculator.Apply(current, edge, _raw, _anchorRight, _anchorBottom, frameConstraints);

        if (!freeFrame)
            applied = QuantiseToWorkspaceGrid(applied, edge);

        _appliedDeltaCount++;

        return true;
    }

    /// <summary>
    /// Ends the current gesture (if any) — idempotent and re-entrancy-safe (see the <c>_ending</c>
    /// guard below): a second/nested call while the first is still unwinding (e.g. because releasing
    /// capture synchronously re-raises a completion event into the same call stack) returns
    /// <see cref="ResizeEndOutcome.WasActive"/> = <see langword="false"/> immediately instead of
    /// recursing or double-releasing the tracker.
    /// </summary>
    /// <remarks>
    /// <see cref="ResizeEndOutcome.ShouldPersist"/> is <see langword="true"/> whenever at least one
    /// delta was actually applied, REGARDLESS of <paramref name="reason"/> — including
    /// <see cref="ResizeEndReason.Canceled"/>: WPF does not roll back geometry already written to
    /// <c>Window.Width</c>/<c>Height</c>/<c>Left</c>/<c>Top</c> when a <c>Thumb</c>'s <c>DragCompleted</c>
    /// reports <c>Canceled == true</c> — the panel is already visibly at the new geometry, so persisting
    /// anything else would desync the saved layout from what the user sees on screen (plan §1.3). The
    /// one exception is <see cref="ResizeEndReason.Failed"/>: an exception mid-delta may have left the
    /// geometry only partially applied, so it is never safe to persist.
    /// </remarks>
    /// <param name="reason">Why the gesture is ending.</param>
    /// <returns>The outcome describing what the caller should do next.</returns>
    public ResizeEndOutcome End(ResizeEndReason reason)
    {
        if (!_isActive || _ending)
            return new ResizeEndOutcome(WasActive: false, ShouldPersist: false, _activeEdge, reason, _appliedDeltaCount);

        _ending = true;
        try
        {
            return new ResizeEndOutcome(
                WasActive: true,
                ShouldPersist: reason != ResizeEndReason.Failed && _appliedDeltaCount > 0,
                _activeEdge,
                reason,
                _appliedDeltaCount);
        }
        finally
        {
            _isActive = false;
            _tracker.Exit(PanelInteractionState.Resizing);
            _ending = false;
        }
    }

    /// <summary>
    /// The production <c>Alt</c> reader. <c>WM_SIZING</c> is pumped on the UI thread (inside
    /// <c>DefWindowProc</c>'s nested modal loop) and <c>Thumb.DragDelta</c> is a WPF input event, so
    /// <see cref="Keyboard.Modifiers"/> is valid on both resize paths — the same source
    /// <c>PanelDragMoveBehavior</c> uses for the drag half of this modifier (P03).
    /// </summary>
    /// <remarks>
    /// Wrapped in a try/catch because this runs inside a gesture callback that must never throw into the
    /// WPF input pipeline (or into the native sizing loop): a thread with no usable
    /// <see cref="System.Windows.Input.InputManager"/> (a headless unit-test thread constructing a
    /// session directly) degrades to "modifier not held", i.e. the ordinary quantised behaviour.
    /// </remarks>
    private static bool DefaultFreeResizeModifier()
    {
        try
        {
            return (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        }
        catch (Exception)
        {
            // Swallowed intentionally — see the remarks above; a modifier read must never end a gesture.
            return false;
        }
    }

    /// <summary>
    /// Whether THIS frame is a free (unquantised) one. <see langword="true"/> for a
    /// <see cref="Models.PanelGeometryMode.GridAligned"/> gesture (the original P04 scope), and — as of
    /// the ADR-011 Amendment 2026-09-24 (resize extension) — ALSO for a gesture whose
    /// <see cref="ResizeConstraints.SizeGridStep"/> is active (<c>IAppSettings.GridSize &gt; 1</c>),
    /// regardless of <see cref="Models.PanelGeometryMode"/>. When neither is true, quantisation is
    /// already a no-op for every frame of this gesture, so asking the keyboard would be a pointless
    /// per-frame cost — <c>_isFreeResizeModifierDown()</c> is never called in that case.
    /// </summary>
    private bool IsFreeResizeFrame() => (_gridState.IsGridAligned || _constraints.SizeGridStep > 0d) && _isFreeResizeModifierDown();

    /// <summary>
    /// P04-G2 (docs/PLANS/workspace-grid-03-04-integration.md §3.4): the workspace-grid half of one
    /// resize frame, applied to <see cref="ResizeGeometryCalculator.Apply"/>'s output — never inside it
    /// (<see cref="ResizeGeometryCalculator"/> stays byte-for-byte untouched, ADR-021 D-2/R6 confirmed
    /// by ADR-028 Amendment MAJOR-4). The steps themselves live in
    /// <see cref="PanelSizingGridSupport.QuantiseResize"/> (P04 fix-cycle 1) so the COMMIT path
    /// (<see cref="PanelSizingGridSupport.PersistSpans"/>) runs the identical pipeline — otherwise
    /// releasing <c>Alt</c>-suppressed frames onto the grid at mouse-up would be a second, subtly
    /// different copy of this math (the "two copies of SnapWidthToGrid" failure mode, baseline risk 10).
    /// A no-op for a <see cref="Models.PanelGeometryMode.LegacyFreeform"/> gesture.
    /// </summary>
    private Rect QuantiseToWorkspaceGrid(Rect applied, ResizeEdge edge)
        => PanelSizingGridSupport.QuantiseResize(applied, edge, _gridState, _obstacles);
}
