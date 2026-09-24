namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The resolved geometric state of one panel for a SINGLE gesture/operation frame (drag start,
/// resize begin, "Align to workspace grid") — ADR-028's System B, snapshotted the same way
/// <c>PanelDragMoveBehavior</c> already snapshots obstacles/monitors once per gesture rather than
/// re-deriving them on every mouse move (docs/PLANS/workspace-grid-03-04-integration.md §1.3).
/// </summary>
/// <param name="IsGridAligned">
/// Whether the panel should be treated as <see cref="PanelGeometryMode.GridAligned"/> for this frame —
/// the ONLY field callers should branch on; every other field is meaningless (and left at its default)
/// when this is <see langword="false"/> (see <see cref="Legacy"/>).
/// </param>
/// <param name="Origin">
/// The panel's monitor's work-area top-left, in the SAME coordinate space as <c>Window.Left</c>/<c>Top</c>
/// (already passed through <see cref="DesktopCoordinateSpace.ToWindowSpace(System.Windows.Point)"/>).
/// </param>
/// <param name="WorkArea">The panel's monitor's full work-area rectangle, in the same window-space as <paramref name="Origin"/>.</param>
/// <param name="UnitSize"><see cref="WorkspaceGridMetrics.UnitSize"/> at the moment of resolution.</param>
/// <param name="Gap"><see cref="WorkspaceGridMetrics.DefaultGap"/> at the moment of resolution.</param>
/// <param name="MinSpan">
/// <see cref="WorkspaceGridMetrics.MinSpanCollapsed"/> or <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>,
/// chosen from the <c>isRolledUp</c> argument passed to <see cref="PanelGridStateResolver.Resolve"/>.
/// </param>
/// <param name="MonitorDeviceId">
/// The resolved monitor's stable device name (see <see cref="PanelGridStateResolver.Resolve"/> for the
/// resolution order) — a read-only OUTPUT of resolution, not written back onto the model here; F-6
/// (updating <c>ChildWindowModel.MonitorDeviceId</c> after a cross-monitor drag) is implemented by the
/// P03 drag pipeline, not by this resolver.
/// </param>
public readonly record struct PanelGridState(
    bool IsGridAligned,
    Point Origin,
    Rect WorkArea,
    double UnitSize,
    double Gap,
    GridSpan MinSpan,
    string? MonitorDeviceId)
{
    /// <summary>
    /// The non-grid-aligned state: <see cref="IsGridAligned"/> is <see langword="false"/> and every
    /// other field is its type's default (an empty <see cref="Rect"/>, a zeroed <see cref="Point"/>, a
    /// zeroed <see cref="GridSpan"/>, <see langword="null"/> device id) — callers must never read these
    /// defaults as meaningful values; they exist only because a <see langword="readonly record struct"/>
    /// cannot omit them.
    /// </summary>
    public static readonly PanelGridState Legacy = new(false, default, Rect.Empty, 0d, 0d, default, null);
}

/// <summary>
/// The single point where <see cref="ChildWindowModel.GeometryMode"/> is read to decide whether a panel
/// participates in the workspace grid for the current gesture/operation (Инвариант В1,
/// docs/PLANS/workspace-grid-03-04-integration.md §0.1/§1.3). No behavior/controller anywhere in
/// P03/P04 is meant to compare <c>model.GeometryMode == PanelGeometryMode.GridAligned</c> directly —
/// they call <see cref="Resolve"/> instead, so a <see cref="PanelGeometryMode.GridAligned"/> panel with
/// missing/invalid spans (a corrupted or not-yet-migrated document) degrades to
/// <see cref="PanelGridState.Legacy"/> in exactly one place rather than N slightly different checks.
/// </summary>
public static class PanelGridStateResolver
{
    /// <summary>
    /// Cheap pre-check (P03): whether <paramref name="model"/> could participate in the workspace grid at
    /// all, WITHOUT the caller having to gather a monitor snapshot first. Lives here, next to
    /// <see cref="Resolve"/>, precisely because <see cref="ChildWindowModel.GeometryMode"/> may only be
    /// read in this one type (Инвариант В1).
    /// </summary>
    /// <remarks>
    /// May ONLY be used as a fast path to <see cref="PanelGridState.Legacy"/> — never as a substitute for
    /// <see cref="Resolve"/> when the answer is <see langword="true"/>: a panel can be labelled
    /// <see cref="PanelGeometryMode.GridAligned"/> and still resolve to
    /// <see cref="PanelGridState.Legacy"/> (no monitors, degenerate size), and only <see cref="Resolve"/>
    /// knows that. Exists so a caller reached from a NON-grid path (e.g. the resize-completed handler,
    /// which shares <c>PanelDragMoveBehavior.SnapToGrid</c>) does not pay for an
    /// <c>IMonitorService.GetMonitors()</c> enumeration on every gesture of every
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> panel.
    /// </remarks>
    /// <param name="model">The panel's model, or <see langword="null"/>.</param>
    /// <returns><see langword="false"/> when the panel definitely cannot be grid-aligned this frame.</returns>
    public static bool ParticipatesInGrid(ChildWindowModel? model)
        => model is not null && model.GeometryMode == PanelGeometryMode.GridAligned;

    /// <summary>
    /// Resolves <paramref name="model"/>'s grid state for one gesture/operation frame.
    /// </summary>
    /// <param name="model">
    /// The panel's model, or <see langword="null"/> (e.g. called before <c>ChildWindow.Initialize</c>)
    /// — either way, <see cref="PanelGeometryMode.LegacyFreeform"/>/<see langword="null"/> both resolve
    /// to <see cref="PanelGridState.Legacy"/>.
    /// </param>
    /// <param name="currentBoundsWindowSpace">
    /// The panel's live bounds (e.g. <c>Window.Left/Top/ActualWidth/ActualHeight</c>), in
    /// <c>Window.Left</c>/<c>Top</c>'s own coordinate space — NOT necessarily <c>model.Position</c>/
    /// <c>model.Size</c>, which may be stale mid-gesture.
    /// </param>
    /// <param name="monitors">
    /// A snapshot of every monitor's <c>(DeviceName, Bounds, WorkArea)</c> in SCREEN DIP — the same
    /// shape (and the same once-per-gesture snapshot) <c>PanelDragMoveBehavior</c>/resize sessions
    /// already take from <see cref="DesktopOrganizer.Services.Abstractions.IMonitorService.GetMonitors"/>.
    /// An empty list always resolves to <see cref="PanelGridState.Legacy"/> (fail-safe: without an
    /// origin, <see cref="PanelGeometryMode.GridAligned"/> is not well-defined for this frame). When no
    /// monitor matches by device id or by bounds, the FIRST entry is used as the primary-monitor
    /// fallback — callers populate this list from <c>IMonitorService.GetMonitors()</c>, whose own
    /// <c>GetPrimaryMonitor()</c> falls back the same way when nothing is flagged primary.
    /// </param>
    /// <param name="isRolledUp">Selects <see cref="WorkspaceGridMetrics.MinSpanCollapsed"/> vs <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>.</param>
    public static PanelGridState Resolve(
        ChildWindowModel? model,
        Rect currentBoundsWindowSpace,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors,
        bool isRolledUp)
    {
        // Rule 1 (Инвариант В1): the ONLY place ChildWindowModel.GeometryMode is read.
        if (model is null || model.GeometryMode != PanelGeometryMode.GridAligned)
            return PanelGridState.Legacy;

        // Rule 2 (fail-safe): no monitors at all -> GridAligned is not well-defined for this frame.
        if (monitors.Count == 0)
            return PanelGridState.Legacy;

        var monitor = ResolveMonitor(model.MonitorDeviceId, currentBoundsWindowSpace, monitors);

        // Rule 3: origin/work area translated into Window.Left/Top's own space.
        var origin = DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea.TopLeft);
        var workArea = DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea);

        // Rule 4: constants + rolled-up-aware minimum span.
        const double unitSize = WorkspaceGridMetrics.UnitSize;
        const double gap = WorkspaceGridMetrics.DefaultGap;
        var minSpan = isRolledUp ? WorkspaceGridMetrics.MinSpanCollapsed : WorkspaceGridMetrics.MinSpanExpanded;

        // Rule 5: spans — persisted-and-valid as-is, otherwise lazily derived from the live size (and
        // written back onto the model so the migration only ever happens once), otherwise (degenerate
        // size) Legacy for this frame only — the model itself is left untouched in that branch so the
        // next frame (e.g. after the first OnRenderSizeChanged) gets another chance to migrate it.
        //
        // P04 fix-cycle 1, MAJOR-3: the derived span is clamped to `minSpan` BEFORE it is written back.
        // Without the clamp, "the migration only ever happens once" was false for any panel smaller than
        // the floor on an axis WPF does not coerce: Window.MinWidth (120 DIP) self-heals the width, but
        // ChildWindow.xaml's MinHeight is the COLLAPSED 32 DIP, well below MinHeightSpanExpanded (12
        // units = 88 DIP) — so a 56 DIP-tall panel (a pre-span document, or a panel restored under a
        // smaller work area) derived span 8, wrote it back, and HasValidPersistedSpan rejected that very
        // value on the next resolve, re-deriving 8 again on every preset/F-2/resize/roll-up resolve,
        // forever. The panel also never gained a span the persistence layer would accept, so roll-down
        // and restore replayed the too-small height. Clamping makes the write idempotent: the second
        // resolve finds a valid span and does nothing.
        if (!HasValidPersistedSpan(model, minSpan))
        {
            if (!IsFiniteAndPositive(currentBoundsWindowSpace.Width) || !IsFiniteAndPositive(currentBoundsWindowSpace.Height))
                return PanelGridState.Legacy;

            var derived = WorkspaceLayoutPolicy.ClampToMinimum(
                WorkspaceLayoutPolicy.BoundsToSpans(currentBoundsWindowSpace.Size, unitSize, gap), minSpan);

            model.WidthSpanUnits = derived.WidthUnits;
            model.HeightSpanUnits = derived.HeightUnits;
        }

        return new PanelGridState(
            IsGridAligned: true,
            Origin: origin,
            WorkArea: workArea,
            UnitSize: unitSize,
            Gap: gap,
            MinSpan: minSpan,
            MonitorDeviceId: monitor.DeviceName);
    }

    private static bool HasValidPersistedSpan(ChildWindowModel model, GridSpan minSpan) =>
        model.WidthSpanUnits is { } w && w >= minSpan.WidthUnits &&
        model.HeightSpanUnits is { } h && h >= minSpan.HeightUnits;

    private static bool IsFiniteAndPositive(double value) => double.IsFinite(value) && value > 0d;

    /// <summary>
    /// Monitor resolution order (ADR-028 «<c>MonitorDeviceId</c> при межмониторном drag»): (1) exact
    /// (case-insensitive) <c>DeviceName</c> match against <c>model.MonitorDeviceId</c>; (2) the monitor
    /// whose <c>Bounds</c> contains the CENTER (not top-left — a top-left-outside-every-monitor panel is
    /// exactly the negative-coordinate failure mode this resolver exists to avoid) of
    /// <paramref name="currentBoundsWindowSpace"/>, translated into screen space; (3) the first snapshot
    /// entry (primary-monitor fallback — see the <c>monitors</c> parameter's own remarks).
    /// </summary>
    private static (string DeviceName, Rect Bounds, Rect WorkArea) ResolveMonitor(
        string? monitorDeviceId,
        Rect currentBoundsWindowSpace,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors)
    {
        if (!string.IsNullOrEmpty(monitorDeviceId))
        {
            foreach (var candidate in monitors)
            {
                if (string.Equals(candidate.DeviceName, monitorDeviceId, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        var screenBounds = DesktopCoordinateSpace.ToScreenSpace(currentBoundsWindowSpace);
        var center = new Point(
            screenBounds.X + (screenBounds.Width / 2d),
            screenBounds.Y + (screenBounds.Height / 2d));

        foreach (var candidate in monitors)
        {
            if (candidate.Bounds.Contains(center))
                return candidate;
        }

        return monitors[0];
    }
}
