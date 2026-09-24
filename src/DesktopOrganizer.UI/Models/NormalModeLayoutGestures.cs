namespace DesktopOrganizer.UI.Models;

/// <summary>
/// User preference for which panel-move/resize gestures remain available while
/// <see cref="LayoutInteractionMode"/> is <see cref="LayoutInteractionMode.Normal"/>
/// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2). Persisted as
/// <see cref="Services.IAppSettings.NormalModeLayoutGestures"/> (its enum member name, e.g.
/// <c>"MoveOnly"</c>) — see <see cref="NormalModeLayoutGesturesParser"/>.
/// </summary>
public enum NormalModeLayoutGestures
{
    /// <summary>Both move (title-bar drag) and resize are allowed in Normal mode — pre-Phase-6 behavior.</summary>
    MoveAndResize = 0,

    /// <summary>
    /// Default. Move is allowed (gated only by the drag-start threshold); resize is gated off — the
    /// gesture-conflict source ADR-026 targets (docs/PLANS/phase-6-edit-layout-ux.md §1 D-2).
    /// </summary>
    MoveOnly = 1,

    /// <summary>Neither move nor resize is allowed in Normal mode; both require <see cref="LayoutInteractionMode.EditLayout"/>.</summary>
    None = 2,
}

/// <summary>
/// Parsing helper for <see cref="NormalModeLayoutGestures"/> — same shape as
/// <see cref="PanelSurfaceStyleParser"/> (ADR-025 D-10 precedent): case-insensitive, never throws,
/// unrecognised/empty input falls back to the product default <see cref="NormalModeLayoutGestures.MoveOnly"/>.
/// </summary>
public static class NormalModeLayoutGesturesParser
{
    /// <summary>Parses <paramref name="value"/> case-insensitively; unrecognised/empty input falls back to <see cref="NormalModeLayoutGestures.MoveOnly"/> — never throws.</summary>
    public static NormalModeLayoutGestures ParseOrMoveOnly(string? value) =>
        Enum.TryParse<NormalModeLayoutGestures>(value, ignoreCase: true, out var parsed) ? parsed : NormalModeLayoutGestures.MoveOnly;
}
