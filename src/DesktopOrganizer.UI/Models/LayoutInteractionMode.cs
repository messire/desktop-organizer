namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The application-global (not per-panel) input-priority mode — Fences-parity "Edit Layout"
/// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-1, ADR-026). Exactly two values: <c>Drag Item</c> /
/// <c>Rename Item</c> from the ADR-026 scaffold are <b>not</b> members of this enum — they remain the
/// existing panel-local <see cref="Interaction.PanelInteractionState.Dragging"/>/<c>Editing</c> states
/// (ADR-021), orthogonal to this mode.
/// </summary>
public enum LayoutInteractionMode
{
    /// <summary>Default. Content (launching/selecting/renaming) has priority; geometry affordances are gated off by <see cref="LayoutGesturePolicy"/>.</summary>
    Normal = 0,

    /// <summary>Panel geometry (move/resize/guides) has priority.</summary>
    EditLayout = 1,
}
