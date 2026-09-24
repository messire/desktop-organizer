namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// The visually-distinguishable interaction state a panel's chrome renders (docs/PLANS/phase-5-ambient-accent-ui.md
/// §1 D-6, §3.3). Distinct from <see cref="Interaction.PanelInteractionState"/> (Phase 1, ADR-021):
/// that enum is the single source of truth for which gesture <i>owns</i> a panel's input/capture and
/// is never mutated outside <c>PanelInteractionTracker</c>. This enum is a pure, derived visual
/// projection computed by <see cref="InteractionVisualState.Resolve"/> from that gesture plus two
/// independent XAML-level facts (pointer-over, title-editing) — it adds no new states to the tracker
/// and does not make <c>PanelInteractionState.Hover</c>/<c>Editing</c> "live" (T-TRK-08 still guards that).
/// </summary>
public enum PanelVisualState
{
    Idle,
    Hover,
    Editing,
    Dragging,
    Resizing,
}
