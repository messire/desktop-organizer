namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Edit Layout visual emphasis (docs/PLANS/phase-6-edit-layout-ux.md §1 D-6) — the machine-checkable form
/// of the phase's DoD "edit-mode is visually distinguishable". Reuses <see cref="InteractionVisualState"/>'s
/// five-state opacity/emphasis channels verbatim (Phase 5's ADR-025 visual language, not a new colour
/// vocabulary): every function here returns EXACTLY <see cref="InteractionVisualState"/>'s own value when
/// <c>isLayoutEditing</c> is <see langword="false"/> (pinned by <c>LayoutEditVisualsTests</c>' regression
/// suite), and layers a strictly-stronger, independent emphasis on top when it is <see langword="true"/>.
/// </summary>
/// <remarks>
/// Deliberately does not touch <see cref="InteractionVisualState"/> (§0 — its five branches stay
/// byte-for-byte, and so do <c>InteractionVisualStateTests</c>): every function below reads one of its
/// existing branches as a baseline and applies a second, independent transform, never replacing it.
/// </remarks>
public static class LayoutEditVisuals
{
    /// <summary>
    /// The floor <see cref="BorderOpacityFor"/> enforces while editing — deliberately the exact same
    /// value <see cref="InteractionVisualState.BorderOpacityFor"/> already renders for
    /// <see cref="PanelVisualState.Editing"/> (0.60, an in-progress title rename): Edit Layout mode must
    /// never look less emphasized than an ordinary rename already does in Normal mode.
    /// </summary>
    public const double EditBorderFloor = 0.60;

    /// <summary>Multiplier <see cref="AccentEmphasisFor"/> applies on top of <see cref="InteractionVisualState.AccentEmphasisFor"/> while editing.</summary>
    public const double AccentEmphasis = 1.35;

    /// <summary>
    /// Border opacity for <paramref name="state"/>. Identical to
    /// <see cref="InteractionVisualState.BorderOpacityFor"/> when <paramref name="isLayoutEditing"/> is
    /// <see langword="false"/>; otherwise the greater of that value and <see cref="EditBorderFloor"/> — the
    /// border can only ever get MORE emphasized while editing, never less.
    /// </summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    public static double BorderOpacityFor(PanelVisualState state, bool isLayoutEditing)
    {
        var baseline = InteractionVisualState.BorderOpacityFor(state);
        return isLayoutEditing ? Math.Max(baseline, EditBorderFloor) : baseline;
    }

    /// <summary>
    /// Header/chrome (action buttons) opacity for <paramref name="state"/>. Identical to
    /// <see cref="InteractionVisualState.ChromeOpacityFor"/> when <paramref name="isLayoutEditing"/> is
    /// <see langword="false"/>. While editing, <see cref="PanelVisualState.Idle"/> is forced fully opaque
    /// (1.0, up from Phase 5's "actions muted" 0.45) — muting move/resize/rename controls while the user
    /// is explicitly editing layout would be counterproductive; every other state already renders
    /// &gt;= 0.70 in Phase 5 and is left untouched.
    /// </summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    public static double ChromeOpacityFor(PanelVisualState state, bool isLayoutEditing)
    {
        var baseline = InteractionVisualState.ChromeOpacityFor(state);
        return isLayoutEditing && state == PanelVisualState.Idle ? 1.0 : baseline;
    }

    /// <summary>
    /// Accent-indicator width multiplier for <paramref name="state"/> (consumed the same way
    /// <see cref="InteractionVisualState.AccentEmphasisFor"/> is, by
    /// <see cref="DesktopOrganizer.UI.Converters.AccentIndicatorWidthConverter"/>'s edit-aware sibling).
    /// Identical to <see cref="InteractionVisualState.AccentEmphasisFor"/> when
    /// <paramref name="isLayoutEditing"/> is <see langword="false"/>; otherwise scaled by
    /// <see cref="AccentEmphasis"/> (1.35x) on top.
    /// </summary>
    /// <param name="state">The resolved visual state.</param>
    /// <param name="isLayoutEditing">Whether Edit Layout mode is currently active.</param>
    public static double AccentEmphasisFor(PanelVisualState state, bool isLayoutEditing)
    {
        var baseline = InteractionVisualState.AccentEmphasisFor(state);
        return isLayoutEditing ? baseline * AccentEmphasis : baseline;
    }
}
