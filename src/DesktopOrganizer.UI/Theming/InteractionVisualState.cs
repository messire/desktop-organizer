using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Resolves a panel's <see cref="PanelVisualState"/> from three independent facts (D-6) and exposes
/// the opacity/emphasis values each state renders with. This is the machine-checkable form of the
/// phase's DoD "Idle/Hover/Editing/Dragging/Resizing are visually distinguishable": every state's
/// combined (<see cref="BorderOpacityFor"/>, <see cref="ChromeOpacityFor"/>, <see cref="AccentEmphasisFor"/>)
/// signature is pairwise distinct from every other state's (MAJOR-5 fix — <see cref="PanelVisualState.Dragging"/>
/// and <see cref="PanelVisualState.Resizing"/> used to render identically on all three channels; now
/// <see cref="BorderOpacityFor"/> alone tells them apart, strictly increasing across all five states).
/// <see cref="ChromeOpacityFor"/> still ties between Dragging and Resizing — both legitimately render as
/// fully revealed action chrome — but <see cref="AccentEmphasisFor"/> no longer does either (MAJOR-B
/// fix): now that it is actually wired to <c>Panels.xaml</c>'s <c>PART_AccentIndicator</c> width (via
/// <see cref="DesktopOrganizer.UI.Converters.AccentIndicatorWidthConverter"/>), Resizing renders a
/// visibly wider accent strip than Dragging, giving that pair a second, independently-perceivable
/// differentiator beyond the border's 0.95-vs-1.0 opacity delta.
/// </summary>
public static class InteractionVisualState
{
    /// <summary>
    /// Priority: <see cref="PanelInteractionState.Dragging"/>/<see cref="PanelInteractionState.Resizing"/>
    /// &gt; <paramref name="isTitleEditing"/> &gt; <paramref name="isPointerOver"/> &gt; idle (D-6). A
    /// live drag/resize gesture always wins; <see cref="PanelInteractionState.Hover"/>/<see cref="PanelInteractionState.Editing"/>
    /// are reserved states the real tracker never enters (T-TRK-08) and are treated the same as
    /// <see cref="PanelInteractionState.Idle"/> here — only the two boolean facts decide Hover vs. Editing vs. Idle.
    /// </summary>
    /// <param name="gesture">The panel's current gesture state, from <see cref="PanelInteractionTracker.State"/>.</param>
    /// <param name="isPointerOver">Whether the pointer is currently over the panel's root element.</param>
    /// <param name="isTitleEditing">Whether the panel title is currently being edited.</param>
    public static PanelVisualState Resolve(PanelInteractionState gesture, bool isPointerOver, bool isTitleEditing)
    {
        if (gesture == PanelInteractionState.Dragging)
            return PanelVisualState.Dragging;

        if (gesture == PanelInteractionState.Resizing)
            return PanelVisualState.Resizing;

        if (isTitleEditing)
            return PanelVisualState.Editing;

        return isPointerOver ? PanelVisualState.Hover : PanelVisualState.Idle;
    }

    /// <summary>
    /// Border opacity for <paramref name="state"/>: 0.15 (Idle) .. 1.0 (Resizing), strictly increasing
    /// across all five states (MAJOR-5 fix). Resizing renders slightly more intense than Dragging
    /// (1.0 vs. 0.95) — the one channel that keeps them visually distinguishable, since geometry is
    /// actively changing under the user's cursor during a resize.
    /// </summary>
    /// <param name="state">The resolved visual state to look up the border opacity for.</param>
    public static double BorderOpacityFor(PanelVisualState state) => state switch
    {
        PanelVisualState.Idle => 0.15,
        PanelVisualState.Hover => 0.35,
        PanelVisualState.Editing => 0.60,
        PanelVisualState.Dragging => 0.95,
        PanelVisualState.Resizing => 1.0,
        _ => 0.15,
    };

    /// <summary>Header/chrome (action buttons) opacity for <paramref name="state"/>: 0.45 (Idle, "actions muted") .. 1.0 (fully revealed).</summary>
    /// <param name="state">The resolved visual state to look up the chrome opacity for.</param>
    public static double ChromeOpacityFor(PanelVisualState state) => state switch
    {
        PanelVisualState.Idle => 0.45,
        PanelVisualState.Hover => 0.70,
        PanelVisualState.Editing => 0.85,
        PanelVisualState.Dragging => 1.0,
        PanelVisualState.Resizing => 1.0,
        _ => 0.45,
    };

    /// <summary>
    /// Multiplier applied to the accent indicator's thickness for <paramref name="state"/> (consumed by
    /// <see cref="DesktopOrganizer.UI.Converters.AccentIndicatorWidthConverter"/> against
    /// <see cref="DesktopOrganizer.UI.Models.PanelMetrics.AccentIndicatorThickness"/>). Resizing renders
    /// wider than Dragging (1.7 vs. 1.5, MAJOR-B fix) — previously tied at 1.5/1.5, which mattered not at
    /// all while this value was unbound from any XAML element, but now gives Dragging/Resizing a second,
    /// independently-perceivable differentiator alongside <see cref="BorderOpacityFor"/>'s 0.95-vs-1.0.
    /// </summary>
    /// <param name="state">The resolved visual state to look up the accent emphasis multiplier for.</param>
    public static double AccentEmphasisFor(PanelVisualState state) => state switch
    {
        PanelVisualState.Idle => 1.0,
        PanelVisualState.Hover => 1.2,
        PanelVisualState.Editing => 1.35,
        PanelVisualState.Dragging => 1.5,
        PanelVisualState.Resizing => 1.7,
        _ => 1.0,
    };
}
