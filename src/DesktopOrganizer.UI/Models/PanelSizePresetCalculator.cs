using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure geometry for the "Size → Small/Medium/Large/Extra large" command (ADR-028 "Presets",
/// docs/PLANS/workspace-grid-03-04-integration.md §3.5) — the whole command expressed as functions of
/// (current bounds, preset, context), so the <c>ChildWindow</c> handler that calls it is nothing but
/// gating plus one <c>PanelGeometryWriter.Apply</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A preset is a resize, not a teleport.</b> Both overloads anchor the panel's TOP-LEFT corner and
/// grow/shrink toward the bottom-right — i.e. exactly the shape of a
/// <see cref="ResizeEdge.BottomRight"/> drag, which is also why the grid-aligned variant reuses
/// <see cref="WorkspaceLayoutPolicy.ClampResizeToSlots"/> with that edge for anti-overlap instead of a
/// position-clamp: a preset must never push a panel out from under the user's cursor.
/// </para>
/// <para>
/// <b>Presets are a shortcut over spans, not the only allowed sizes</b> (ADR-028, CRITIC_REVIEW п.3):
/// free resize remains available afterwards, and nothing records "this panel is Medium".
/// </para>
/// </remarks>
public static class PanelSizePresetCalculator
{
    /// <summary>
    /// The <see cref="PanelGeometryMode.GridAligned"/> variant: the preset's span (never below
    /// <see cref="PanelGridState.MinSpan"/>) applied from the panel's quantised top-left, kept inside the
    /// monitor work area and clamped out of its neighbours.
    /// </summary>
    /// <param name="current">The panel's current bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="preset">The requested preset.</param>
    /// <param name="state">The panel's resolved grid state (origin/work area/unit/gap/min span).</param>
    /// <param name="obstacles">The other panels' bounds; empty disables the anti-overlap step.</param>
    /// <returns>The panel's new bounds — on the grid, a whole span, inside the work area.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> has no entry in <see cref="WorkspaceGridMetrics.DefaultPresetSpans"/>.</exception>
    public static Rect ComputeGridAligned(Rect current, PanelSizePreset preset, PanelGridState state, IReadOnlyList<Rect> obstacles)
    {
        ArgumentNullException.ThrowIfNull(obstacles);

        var presetSpan = WorkspaceGridMetrics.SpanForPreset(preset);
        var span = new GridSpan(
            Math.Max(presetSpan.WidthUnits, state.MinSpan.WidthUnits),
            Math.Max(presetSpan.HeightUnits, state.MinSpan.HeightUnits));

        var size = WorkspaceLayoutPolicy.SpansToBounds(span, state.UnitSize, state.Gap);
        var position = WorkspaceLayoutPolicy.SnapPosition(current.TopLeft, state.Origin, state.UnitSize);

        // SafeClamp's grid-aware overload: keeps the whole panel inside the work area when it fits, pins
        // it to slot 0 when it does not, and never changes the size it was handed (ADR-028 "Доступность
        // всех окон в multi-monitor") — so the preset's extent survives a small monitor.
        var clamped = WorkspaceLayoutPolicy.SafeClamp(new Rect(position, size), state.WorkArea, state.UnitSize, state.Gap);

        if (obstacles.Count == 0) return clamped;

        // Anti-overlap as TWO single-edge passes (Right, then Bottom) rather than one BottomRight pass:
        // ClampResizeToSlots shrinks EVERY axis its edge moves on each iteration, so a corner edge would
        // also shave height off a preset that only ever collided horizontally. Two passes shrink each
        // axis exactly as far as that axis actually needs, and both keep the top-left anchor fixed, so
        // the composition is still "a resize from the bottom-right corner".
        var afterWidth = WorkspaceLayoutPolicy.ClampResizeToSlots(clamped, ResizeEdge.Right, obstacles, state.UnitSize, state.Gap, state.MinSpan);

        return WorkspaceLayoutPolicy.ClampResizeToSlots(afterWidth, ResizeEdge.Bottom, obstacles, state.UnitSize, state.Gap, state.MinSpan);
    }

    /// <summary>
    /// The <see cref="PanelGeometryMode.LegacyFreeform"/> variant: the preset's DIP extent applied at the
    /// panel's current (unquantised) top-left, clamped into <paramref name="workAreaWindowSpace"/>.
    /// </summary>
    /// <remarks>
    /// <b>Decision (P04, deviating from the plan's P04-P1).</b> The command is offered in BOTH geometry
    /// modes — a named size is useful regardless of how the panel is positioned — but for a legacy panel
    /// it changes ONLY the size: no grid quantisation, no anti-overlap (resize anti-overlap stays an open
    /// ADR-017 item for that mode, ADR-028 "Границы объёма"), no span written, and above all NO switch to
    /// <see cref="PanelGeometryMode.GridAligned"/>. The plan's original sketch had the preset also flip
    /// the mode; that was rejected because OQ-3 = B made the checkable "Align to workspace grid" menu
    /// item the SINGLE explicit opt-in/opt-out for the mode — a size preset silently converting a panel
    /// would be a second, hidden one, and would be impossible to undo from the same menu.
    /// </remarks>
    /// <param name="current">The panel's current bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="preset">The requested preset.</param>
    /// <param name="workAreaWindowSpace">The panel's monitor work area in the same space, or <see cref="Rect.Empty"/> to skip clamping.</param>
    /// <returns>The panel's new bounds — the preset's extent at (approximately) its current position.</returns>
    public static Rect ComputeFreeform(Rect current, PanelSizePreset preset, Rect workAreaWindowSpace)
    {
        var size = WorkspaceLayoutPolicy.PreferredSize(preset, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);

        return WorkspaceLayoutPolicy.SafeClampFreeform(new Rect(current.TopLeft, size), workAreaWindowSpace);
    }
}
