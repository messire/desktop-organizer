namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// Owns one panel's current <see cref="PanelInteractionState"/> and enforces ADR-021's core invariant:
/// only one gesture owns a panel's capture at a time. One instance per <see cref="Views.ChildWindow"/>
/// (docs/PLANS/phase-1-resize-stabilization.md §2.2), shared between that panel's
/// <see cref="Behaviors.PanelDragMoveBehavior"/> and <see cref="Behaviors.PanelResizeController"/>
/// (via <see cref="ResizeGestureSession"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Transition matrix</b> (ADR-021, plan §2.2 — rows are the CURRENT state, columns are the state
/// <see cref="TryEnter"/> is asked to move to):
/// </para>
/// <list type="bullet">
/// <item><description><see cref="PanelInteractionState.Idle"/>/<see cref="PanelInteractionState.Hover"/> → anything else: allowed.</description></item>
/// <item><description><see cref="PanelInteractionState.Editing"/>/<see cref="PanelInteractionState.Dragging"/>/<see cref="PanelInteractionState.Resizing"/> → anything (including each other): refused; only a return to <see cref="PanelInteractionState.Idle"/> is possible, via <see cref="Exit"/>/<see cref="ForceIdle"/>, never <see cref="TryEnter"/>.</description></item>
/// </list>
/// <para>
/// <b>UI-thread-affine, no locking:</b> every caller is part of the WPF input pipeline (mouse/capture
/// events), which always runs on the UI thread. <see cref="ForceIdle"/> must never be called from a
/// finalizer or a background thread.
/// </para>
/// </remarks>
public sealed class PanelInteractionTracker
{
    /// <summary>The panel's current interaction state. Starts — and always eventually returns to — <see cref="PanelInteractionState.Idle"/>.</summary>
    public PanelInteractionState State { get; private set; } = PanelInteractionState.Idle;

    /// <summary>
    /// Raised exactly once per actual state transition (never for a no-op — e.g. a redundant
    /// <see cref="ForceIdle"/> call while already <see cref="PanelInteractionState.Idle"/>). Reserved
    /// for future use binding visual gesture-affordance states to this one source of truth; no Phase 1
    /// subscriber exists yet.
    /// </summary>
    public event EventHandler<PanelInteractionState>? StateChanged;

    /// <summary>
    /// Attempts to claim <paramref name="state"/> for the panel. Allowed only when <see cref="State"/>
    /// is currently <see cref="PanelInteractionState.Idle"/> or <see cref="PanelInteractionState.Hover"/>
    /// (the transition matrix's only two rows with any outgoing edge besides "back to Idle") — refused
    /// while another gesture (<see cref="PanelInteractionState.Editing"/>/<see cref="PanelInteractionState.Dragging"/>/
    /// <see cref="PanelInteractionState.Resizing"/>) already owns the panel, enforcing ADR-021's "only
    /// one gesture owns capture at a time" invariant. <paramref name="state"/> itself can never be
    /// <see cref="PanelInteractionState.Idle"/> — returning to Idle is always done via <see cref="Exit"/>
    /// or <see cref="ForceIdle"/>, never by "entering" it.
    /// </summary>
    /// <param name="state">The state to claim. Must not be <see cref="PanelInteractionState.Idle"/>.</param>
    /// <returns><see langword="true"/> if the claim succeeded (the state actually changed).</returns>
    public bool TryEnter(PanelInteractionState state)
    {
        if (state == PanelInteractionState.Idle) return false;
        if (State != PanelInteractionState.Idle && State != PanelInteractionState.Hover) return false;

        SetState(state);
        return true;
    }

    /// <summary>
    /// Releases <paramref name="state"/> back to <see cref="PanelInteractionState.Idle"/> — but ONLY if
    /// <see cref="State"/> is currently exactly <paramref name="state"/>; otherwise a no-op. Both the
    /// "exiting a state some other gesture already exited/overwrote" case (idempotency, called from a
    /// <c>finally</c> block) and the "exiting a state we never actually owned" case are safe no-ops by
    /// design — callers never need to guard this themselves.
    /// </summary>
    /// <param name="state">The state the caller believes it currently owns.</param>
    public void Exit(PanelInteractionState state)
    {
        if (State != state) return;
        SetState(PanelInteractionState.Idle);
    }

    /// <summary>
    /// Unconditionally forces <see cref="State"/> back to <see cref="PanelInteractionState.Idle"/> from
    /// ANY current state — the panel-disposal/<c>Unloaded</c>/<c>Closing</c> escape hatch (ADR-021
    /// Invariant "window disposal forces state to Idle"). A no-op (no event) if already Idle.
    /// </summary>
    public void ForceIdle() => SetState(PanelInteractionState.Idle);

    private void SetState(PanelInteractionState newState)
    {
        if (State == newState) return;

        State = newState;
        StateChanged?.Invoke(this, newState);
    }
}
