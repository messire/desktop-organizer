using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Services;
using Point = System.Drawing.Point;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Places a panel clone that lands on the SAME page as its source (ADR-045, «Panel clone to page») so it
/// never sits exactly on top of it: offsets by one grid step, then clamps into the monitor's work area
/// through the very same <see cref="WindowManager.ClampIntoMonitor"/> every other panel placement already
/// goes through — no second copy of that clamp math. A clone created on a DIFFERENT page never calls this;
/// it keeps the source's exact position (ADR-045 §8.3).
/// </summary>
internal static class PanelCloneGeometry
{
    /// <summary>
    /// Fallback offset step, in DIP, for a <see cref="PanelGeometryMode.LegacyFreeform"/> clone when
    /// <c>gridSize &lt;= 1</c> (no active legacy grid) — an arbitrary but visible nudge, same order of
    /// magnitude as <see cref="WorkspaceGridMetrics.DefaultGap"/> at typical <c>GridSize</c> values.
    /// </summary>
    private const int DefaultLegacyStep = 24;

    /// <summary>
    /// L-5 fix (code review, panel clone to page): floor for the LegacyFreeform offset step, in DIP — a
    /// small active <c>gridSize</c> (e.g. 8 or 16) used to produce a step so small the clone's title bar
    /// still sat almost entirely on top of the source's. Roughly one panel title bar's height, so the
    /// clone is always visibly a separate panel rather than looking like a rendering glitch on the source.
    /// </summary>
    private const int MinLegacyStepDip = 32;

    /// <summary>
    /// L-5 fix (code review, panel clone to page): the <see cref="PanelGeometryMode.GridAligned"/> offset
    /// step, in whole workspace-grid units (<see cref="WorkspaceGridMetrics.UnitSize"/> each) — 4 units is
    /// 32 DIP at the default <c>U=8</c>, i.e. the same <see cref="MinLegacyStepDip"/> floor as the
    /// LegacyFreeform branch, expressed on this mode's own lattice. The previous single-unit (8 DIP) step
    /// left the clone's title bar almost entirely overlapping the source's.
    /// </summary>
    private const int GridAlignedStepUnits = 4;

    /// <summary>
    /// Offsets <paramref name="clone"/>'s <see cref="ChildWindowModel.Position"/> — already set to the
    /// source's own position by <see cref="DesktopOrganizer.UI.State.PanelCloneBuilder.Build"/> — by one
    /// step: <see cref="GridAlignedStepUnits"/> workspace-grid units for
    /// <see cref="PanelGeometryMode.GridAligned"/> (keeps the clone on the same workspace-grid lattice as
    /// the source), or <paramref name="gridSize"/> DIP, floored at <see cref="MinLegacyStepDip"/>, for
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> (<see cref="DefaultLegacyStep"/> when
    /// <c>gridSize &lt;= 1</c> — same "step by one lattice cell so the existing grid-size normalization
    /// leaves it alone" reasoning, still floored the same way). If the offset position — resolved through
    /// the same placement <paramref name="clone"/> will actually get (see
    /// <see cref="ResolvesToSamePosition"/>) — collapses right back onto the source's own position (e.g.
    /// the source already sits in the work area's bottom-right corner, so there is nowhere further to clamp
    /// TO), the offset is applied in the OPPOSITE direction instead and clamped again.
    /// </summary>
    /// <param name="clone">The clone, positioned at its source's own <see cref="ChildWindowModel.Position"/> — mutated in place.</param>
    /// <param name="gridSize">The current <c>IAppSettings.GridSize</c> (consulted only for a <see cref="PanelGeometryMode.LegacyFreeform"/> clone).</param>
    /// <param name="monitor">The clone's monitor — supplies the work-area clamp.</param>
    public static void Offset(ChildWindowModel clone, int gridSize, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(monitor);

        var original = clone.Position;
        var step = clone.GeometryMode == PanelGeometryMode.GridAligned
            ? (int)WorkspaceGridMetrics.UnitSize * GridAlignedStepUnits
            : Math.Max(gridSize > 1 ? gridSize : DefaultLegacyStep, MinLegacyStepDip);

        clone.Position = new Point(original.X + step, original.Y + step);
        WindowManager.ClampIntoMonitor(clone, monitor);

        if (ResolvesToSamePosition(clone, monitor, original))
        {
            clone.Position = new Point(original.X - step, original.Y - step);
            WindowManager.ClampIntoMonitor(clone, monitor);
        }
    }

    /// <summary>
    /// L-5 fix (code review, panel clone to page): whether the clone's CURRENT (just nudged and clamped)
    /// <see cref="ChildWindowModel.Position"/> would actually settle on the exact same spot as
    /// <paramref name="originalPosition"/>. For <see cref="PanelGeometryMode.LegacyFreeform"/> this is a
    /// plain raw-position comparison; for <see cref="PanelGeometryMode.GridAligned"/> that alone is not
    /// enough — <see cref="WindowManager.ResolveGridAlignedBounds"/> snaps whatever raw position ends up
    /// here onto the workspace-grid lattice (<c>WorkspaceLayoutPolicy.SnapPosition</c>), so two DIFFERENT
    /// raw positions can still resolve to the exact same slot. Both candidates are resolved through that
    /// same snap and compared there instead, leaving <paramref name="clone"/>'s own
    /// <see cref="ChildWindowModel.Position"/> exactly as the caller left it (the temporary swap used to
    /// probe <paramref name="originalPosition"/>'s own resolved slot is always restored before returning).
    /// </summary>
    /// <param name="clone">The clone, at its nudged-and-clamped candidate position.</param>
    /// <param name="monitor">The clone's monitor.</param>
    /// <param name="originalPosition">The source's own (pre-offset) position.</param>
    private static bool ResolvesToSamePosition(ChildWindowModel clone, MonitorInfo monitor, Point originalPosition)
    {
        if (clone.GeometryMode != PanelGeometryMode.GridAligned)
            return clone.Position == originalPosition;

        var candidatePosition = clone.Position;
        var candidateBounds = WindowManager.ResolveGridAlignedBounds(clone, monitor);

        clone.Position = originalPosition;
        var originalBounds = WindowManager.ResolveGridAlignedBounds(clone, monitor);
        clone.Position = candidatePosition;

        return candidateBounds is { } candidate && originalBounds is { } resolvedOriginal
               && candidate.TopLeft == resolvedOriginal.TopLeft;
    }
}
