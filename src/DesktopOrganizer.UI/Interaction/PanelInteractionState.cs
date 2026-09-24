namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// The single source of truth for what gesture (if any) currently owns a panel's input/capture
/// (ADR-021, docs/PLANS/phase-1-resize-stabilization.md §2.1). Lives ONLY in the UI layer — this enum
/// must never be referenced from <c>DesktopOrganizer.Core</c>/<c>DesktopOrganizer.Services</c>
/// (ADR-021 Decision).
/// </summary>
/// <remarks>
/// Moved here, unchanged, from the refactoring-package draft at
/// <c>docs/Refactoring/UI/PanelInteractionState.cs</c> (kept in place as a package artifact — not
/// deleted/moved, see that plan's §8). <see cref="PanelInteractionTracker"/> is the only type that
/// mutates a panel's current value of this enum.
/// </remarks>
public enum PanelInteractionState
{
    /// <summary>No gesture owns this panel. The only state every other state can always return to.</summary>
    Idle,

    /// <summary>
    /// Reserved for Phase 6 ("Edit Layout UX"). No Phase 1 code path ever enters this state — see
    /// <c>PanelInteractionTrackerTests</c>'s T-TRK-08, which guards against it silently becoming a dead
    /// state (ADR-021 Consequences/Negative).
    /// </summary>
    Hover,

    /// <summary>
    /// Reserved for Phase 6 (inline-rename under <c>WS_EX_NOACTIVATE</c>). No Phase 1 code path ever
    /// enters this state — see the remarks on <see cref="Hover"/>.
    /// </summary>
    Editing,

    /// <summary>A title-bar drag-to-move gesture owns the panel (<see cref="Behaviors.PanelDragMoveBehavior"/>).</summary>
    Dragging,

    /// <summary>A resize-thumb gesture owns the panel (<see cref="Behaviors.PanelResizeController"/>/<see cref="ResizeGestureSession"/>).</summary>
    Resizing,
}
