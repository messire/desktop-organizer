namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The pure, WPF-free half of the <see cref="PanelGeometryMode.GridAligned"/> drag pipeline (ADR-028
/// «Drag: приоритет snap-механизмов», docs/PLANS/workspace-grid-03-04-integration.md §2.3):
/// <c>SnapPosition → snap-guides → anti-overlap</c> per frame, and
/// <c>SnapPosition → grid-aware SafeClamp → anti-overlap</c> on drop.
/// <c>Behaviors.PanelDragMoveBehavior</c> is a thin adapter over these functions — it contributes the
/// live <see cref="Window"/>/mouse state, this type contributes every geometric decision, so the whole
/// pipeline is unit-testable without an STA thread or a real window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline order is ADR-028's, not negotiable here.</b> Grid alignment first (it and the guides pull
/// toward the same lines once both panels are on the shared grid), guides second (ADR-026, Edit Layout
/// only), anti-overlap ALWAYS last and never pierced (ADR-017 «твёрдая стена»).
/// </para>
/// <para>
/// <b><see cref="PanelGeometryMode.LegacyFreeform"/> never reaches this type.</b> Its pipeline
/// (<see cref="GridSnap"/> magnet → guides → gap-less <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect})"/>)
/// stays byte-for-byte where it already lives, inline in the behavior — see ADR-028 «Конвейер
/// LegacyFreeform (без изменений)».
/// </para>
/// </remarks>
public static class PanelDragPipeline
{
    /// <summary>
    /// One live drag frame for a <see cref="PanelGeometryMode.GridAligned"/> panel.
    /// </summary>
    /// <param name="current">The panel's rectangle BEFORE this frame's move (its size is preserved exactly).</param>
    /// <param name="raw">The raw, unsnapped position the cursor delta asks for, in window space.</param>
    /// <param name="state">The gesture's resolved grid state (origin/work area/unit/gap) — see <see cref="PanelGridStateResolver.Resolve"/>.</param>
    /// <param name="monitors">
    /// The gesture's once-per-gesture monitor snapshot, in SCREEN DIP (same shape as
    /// <see cref="PanelGridStateResolver.Resolve"/>'s own <c>monitors</c> parameter) — fix-cycle 1, M-1:
    /// used ONLY by the guide step (below) to resolve the work area of whichever monitor the panel's
    /// CURRENT frame position is actually over, exactly like the untouched
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> pipeline already does on every <c>MouseMove</c>
    /// (<c>Behaviors.PanelDragMoveBehavior.OnTitleBarMouseMove</c>'s own per-frame monitor scan). Before
    /// this fix, the guide step used <paramref name="state"/>'s <see cref="PanelGridState.WorkArea"/>
    /// unconditionally — which is frozen at gesture START and stays the ORIGIN monitor's work area even
    /// after the panel's cursor has crossed onto a different one mid-drag, silently making
    /// <c>SnapGuideCalculator.Snap</c>'s <c>StaysOnScreenX/Y</c> guard discard every guide against a
    /// neighbour that is legitimately on the second monitor.
    /// </param>
    /// <param name="obstacles">The once-per-gesture snapshot of every other live panel's rectangle.</param>
    /// <param name="isEditingLayout">
    /// <see cref="Services.ILayoutEditModeService.IsEditing"/>, read fresh on every frame — gates the
    /// ADR-026 alignment-guide step exactly like the <see cref="PanelGeometryMode.LegacyFreeform"/> path does.
    /// </param>
    /// <param name="freeMove">
    /// <see langword="true"/> while the free-move modifier (<c>Alt</c>) is held: the QUANTISATION step is
    /// skipped for this frame only (the panel follows the cursor 1:1), while guides and anti-overlap keep
    /// running — ADR-028 «Модификатор свободного движения». Never affects
    /// <see cref="ResolveGridDrop"/>: on release the panel is always quantised again.
    /// </param>
    /// <returns>The position to actually write onto the window this frame.</returns>
    public static Point ResolveGridFrame(
        Rect current,
        Point raw,
        PanelGridState state,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors,
        IReadOnlyList<Rect> obstacles,
        bool isEditingLayout,
        bool freeMove)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(obstacles);

        // Step 1 — workspace grid quantisation, relative to the panel's own monitor's work-area origin
        // (never the global (0,0)). Suppressed for this frame only while Alt is held.
        var proposed = freeMove
            ? raw
            : WorkspaceLayoutPolicy.SnapPosition(raw, state.Origin, state.UnitSize);

        // Step 2 — ADR-026 inter-panel alignment guides, Edit Layout only. M-1 fix-cycle 1: the work area
        // is resolved PER FRAME from `monitors`, for whichever one the panel's proposed position actually
        // falls on — not the gesture-start `state.WorkArea` (see this method's own remarks above).
        if (isEditingLayout)
        {
            var workArea = ResolveWorkAreaAtPosition(proposed, monitors);
            var guided = SnapGuideCalculator.Snap(
                new Rect(proposed, current.Size),
                obstacles,
                workArea,
                SnapGuideCalculator.DefaultThreshold);
            proposed = guided.Position;
        }

        // Step 3 — the hard wall (always last).
        return ClampHardWall(current, proposed.X, proposed.Y, obstacles, state.Gap).TopLeft;
    }

    /// <summary>
    /// M-1 fix-cycle 1: resolves the work area of whichever monitor's <c>Bounds</c> contains
    /// <paramref name="positionWindowSpace"/> (converted to screen space via
    /// <see cref="DesktopCoordinateSpace"/>) — the same "scan the once-per-gesture monitor snapshot"
    /// technique the <see cref="PanelGeometryMode.LegacyFreeform"/> guide block already uses, just moved
    /// into the pure pipeline so <see cref="ResolveGridFrame"/> can do it too. Falls back to
    /// <see cref="Rect.Empty"/> (SnapGuideCalculator.Snap's own documented "disable the guard" contract)
    /// when no entry matches — the same fallback the Legacy scan uses, not a silently stale work area.
    /// </summary>
    private static Rect ResolveWorkAreaAtPosition(
        Point positionWindowSpace,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors)
    {
        var screenPoint = DesktopCoordinateSpace.ToScreenSpace(positionWindowSpace);
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Bounds.Contains(screenPoint))
                return DesktopCoordinateSpace.ToWindowSpace(monitors[i].WorkArea);
        }

        return Rect.Empty;
    }

    /// <summary>
    /// Drop-time (mouse-up) geometry for a <see cref="PanelGeometryMode.GridAligned"/> panel: the position
    /// is quantised UNCONDITIONALLY (ADR-028: a grid-aligned panel never comes to rest off the grid, even
    /// if the gesture ended while the free-move modifier was held), pulled back onto the panel's own work
    /// area with the GRID-AWARE <see cref="WorkspaceLayoutPolicy.SafeClamp(Rect,Rect,double,double)"/>
    /// overload (so the clamp result is itself on the grid and a following snap can never push it back
    /// out — ADR-028 Amendment MAJOR-2), and finally re-checked against the anti-overlap wall.
    /// </summary>
    /// <param name="current">The panel's rectangle as the gesture left it (size preserved exactly).</param>
    /// <param name="state">The resolved grid state for this panel.</param>
    /// <param name="obstacles">
    /// The drag gesture's obstacle snapshot. Empty for a drop that follows a RESIZE rather than a drag
    /// (the resize path shares this entry point) — in which case the clamp is a no-op, exactly as the
    /// legacy drop-snap already behaves there.
    /// </param>
    /// <returns>The rectangle to commit (same size as <paramref name="current"/>).</returns>
    public static Rect ResolveGridDrop(Rect current, PanelGridState state, IReadOnlyList<Rect> obstacles)
        => ResolveGridDrop(current, state, obstacles, current.Size);

    /// <summary>
    /// m-2 fix-cycle 1 overload: same as <see cref="ResolveGridDrop(Rect,PanelGridState,IReadOnlyList{Rect})"/>,
    /// but <see cref="WorkspaceLayoutPolicy.SafeClamp"/> reasons about <paramref name="expandedSizeForClamp"/>
    /// instead of <paramref name="current"/>'s own (possibly ROLLED-UP/collapsed) size. A panel dropped
    /// while rolled up was previously clamped as if it were only its ~32 DIP collapsed header tall,
    /// letting the position sit close enough to the work area's bottom/right edge that expanding it back
    /// out (with no further move) would push its real, EXPANDED footprint past that edge. The returned
    /// rectangle's <c>Width</c>/<c>Height</c> are still <paramref name="current"/>'s own — this parameter
    /// only makes the POSITION honest about the panel's real size; only <see cref="Rect.TopLeft"/> is ever
    /// committed by callers (see <c>Behaviors.PanelDragMoveBehavior.SnapToGrid</c>).
    /// </summary>
    /// <param name="current">The panel's rectangle as the gesture left it (size preserved exactly in the result).</param>
    /// <param name="state">The resolved grid state for this panel.</param>
    /// <param name="obstacles">Same as the three-argument overload.</param>
    /// <param name="expandedSizeForClamp">
    /// The size <see cref="WorkspaceLayoutPolicy.SafeClamp"/> should reason about — the panel's EXPANDED
    /// size (e.g. from <see cref="WorkspaceGridPlacement.ExpandedBounds"/>) when it might currently be
    /// rolled up, or simply <paramref name="current"/>'s own size otherwise.
    /// </param>
    /// <returns>The rectangle to commit (same size as <paramref name="current"/>).</returns>
    public static Rect ResolveGridDrop(
        Rect current, PanelGridState state, IReadOnlyList<Rect> obstacles, Size expandedSizeForClamp)
    {
        ArgumentNullException.ThrowIfNull(obstacles);

        var snapped = WorkspaceLayoutPolicy.SnapPosition(current.TopLeft, state.Origin, state.UnitSize);
        var safe = WorkspaceLayoutPolicy.SafeClamp(
            new Rect(snapped, expandedSizeForClamp), state.WorkArea, state.UnitSize, state.Gap);

        return ClampHardWall(current, safe.X, safe.Y, obstacles, state.Gap);
    }

    /// <summary>
    /// The ADR-017 «твёрдая стена», gap-aware: delegates to
    /// <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect},double)"/>
    /// (which keeps <paramref name="gap"/> DIP of clearance from every obstacle it stops against) and then
    /// adds a per-axis rollback for the one case that overload cannot cover on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the extra rollback.</b> Defence in depth for ADR-017's single hardest invariant ("the wall
    /// is never pierced"), which the gap-aware clamp has already broken once: fix-cycle 1 implemented
    /// clearance by inflating every obstacle and delegating, which made the R1 "already overlapping =
    /// ghost" amnesty fire for a panel that was merely CLOSER than <paramref name="gap"/> to a neighbour
    /// — and an amnestied obstacle blocks nothing, so the panel could be dragged straight through it
    /// (fixed in fix-cycle 2; a second, perpendicular-axis variant of the same class of bug was found in
    /// its final verification and fixed in P03 — see <c>PanelCollision.InflateX</c>). This rollback
    /// re-checks the clamp's own answer against the REAL obstacles through
    /// <see cref="PanelCollision.CreatesNewOverlap"/> and refuses any axis step that would create a NEW
    /// overlap, so the invariant holds independently of how the clearance arithmetic evolves. It is
    /// expected to be a no-op against the current implementation: it costs one extra O(obstacles) pass
    /// per axis and buys an invariant that cannot silently regress.
    /// </para>
    /// <para>
    /// R1 is preserved exactly: <see cref="PanelCollision.CreatesNewOverlap"/> only reports overlaps that
    /// are NEW relative to <paramref name="current"/>, so a panel restored on top of a neighbour (a
    /// pre-feature layout) can still be dragged apart — and even deeper in — as before.
    /// </para>
    /// <para>
    /// Axis order mirrors <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect})"/>'s
    /// own: X is resolved first, then Y is evaluated against the already-resolved X.
    /// </para>
    /// </remarks>
    /// <param name="current">The panel's rectangle before the move.</param>
    /// <param name="proposedLeft">The proposed new left, after every magnet step.</param>
    /// <param name="proposedTop">The proposed new top, after every magnet step.</param>
    /// <param name="obstacles">The other live panels' rectangles.</param>
    /// <param name="gap">The clearance to keep, in DIP (<see cref="WorkspaceGridMetrics.DefaultGap"/>).</param>
    /// <returns>The clamped rectangle; never overlaps an obstacle it did not already overlap.</returns>
    public static Rect ClampHardWall(
        Rect current,
        double proposedLeft,
        double proposedTop,
        IReadOnlyList<Rect> obstacles,
        double gap)
    {
        ArgumentNullException.ThrowIfNull(obstacles);

        var clamped = PanelCollision.ClampAgainstObstacles(current, proposedLeft, proposedTop, obstacles, gap);
        if (obstacles.Count == 0) return clamped;

        var width = current.Width;
        var height = current.Height;

        var left = clamped.X;
        if (PanelCollision.CreatesNewOverlap(current, new Rect(left, current.Y, width, height), obstacles))
            left = current.X;

        var afterX = new Rect(left, current.Y, width, height);
        var top = clamped.Y;
        if (PanelCollision.CreatesNewOverlap(afterX, new Rect(left, top, width, height), obstacles))
            top = current.Y;

        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// F-6 (ADR-028 «<c>MonitorDeviceId</c> при межмониторном drag»; OQ-2 = A — applies to BOTH geometry
    /// modes, because it fixes which monitor the panel is RESTORED onto and never changes the geometry of
    /// the gesture itself): resolves the device name of the monitor the panel came to rest on, by the
    /// CENTER of its final rectangle.
    /// </summary>
    /// <param name="finalBoundsWindowSpace">The panel's committed rectangle, in window space.</param>
    /// <param name="monitors">The gesture's monitor snapshot, in screen DIP.</param>
    /// <param name="currentDeviceId">The panel's current <see cref="ChildWindowModel.MonitorDeviceId"/>.</param>
    /// <returns>
    /// The new device name when the panel's center landed on a DIFFERENT monitor than
    /// <paramref name="currentDeviceId"/>; <see langword="null"/> when nothing should change — including
    /// when the center lies outside every monitor (a panel dropped on a disconnected area keeps its last
    /// known monitor rather than being reassigned to an arbitrary one — top-left/"nearest" heuristics are
    /// exactly what ADR-028 rejects here).
    /// </returns>
    public static string? ResolveDropMonitor(
        Rect finalBoundsWindowSpace,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors,
        string? currentDeviceId)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) return null;
        if (!double.IsFinite(finalBoundsWindowSpace.X) || !double.IsFinite(finalBoundsWindowSpace.Y)
            || !double.IsFinite(finalBoundsWindowSpace.Width) || !double.IsFinite(finalBoundsWindowSpace.Height))
        {
            return null;
        }

        var screen = DesktopCoordinateSpace.ToScreenSpace(finalBoundsWindowSpace);
        var center = new Point(screen.X + (screen.Width / 2d), screen.Y + (screen.Height / 2d));

        for (var i = 0; i < monitors.Count; i++)
        {
            if (!monitors[i].Bounds.Contains(center)) continue;

            var name = monitors[i].DeviceName;
            return string.Equals(name, currentDeviceId, StringComparison.OrdinalIgnoreCase) ? null : name;
        }

        return null;
    }

    /// <summary>
    /// The size a drop-time snap must reason about: the window's EXPLICIT <c>Width</c>/<c>Height</c>
    /// (what the resize controller just committed through the geometry writer) when they are set, and
    /// only otherwise the laid-out <c>ActualWidth</c>/<c>ActualHeight</c>. <c>Actual*</c> lags the commit
    /// by one layout pass, so reading it right after a commit clamps against the PRE-commit live size — an
    /// Alt-resize that ended at a live 480 DIP but committed 320 would then be translated off its anchor
    /// (P04 fix-cycle 1 final verification, conditional MAJOR — the very MAJOR-1 symptom, re-entering
    /// through the WPF path).
    /// </summary>
    /// <param name="width">The window's <c>Width</c> (may be <see cref="double.NaN"/>).</param>
    /// <param name="height">The window's <c>Height</c> (may be <see cref="double.NaN"/>).</param>
    /// <param name="actualWidth">The window's <c>ActualWidth</c>.</param>
    /// <param name="actualHeight">The window's <c>ActualHeight</c>.</param>
    /// <returns>The committed size in DIP.</returns>
    public static Size CommittedSize(double width, double height, double actualWidth, double actualHeight)
        => new(
            double.IsFinite(width) && width > 0d ? width : actualWidth,
            double.IsFinite(height) && height > 0d ? height : actualHeight);

    /// <summary>
    /// C-1 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2.3, ADR-028 «Реализация P03 —
    /// fix-цикл 1»): retargets <paramref name="state"/> to whichever monitor <paramref name="current"/>'s
    /// (the panel's RAW, pre-<see cref="ResolveGridDrop(Rect,PanelGridState,IReadOnlyList{Rect})"/>) rectangle
    /// actually landed on, composing <see cref="ResolveDropMonitor"/> with a lookup into
    /// <paramref name="monitors"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this closes.</b> <see cref="PanelGridState"/> is resolved ONCE, at gesture START,
    /// against whichever monitor <c>ChildWindowModel.MonitorDeviceId</c> named at that moment — so
    /// <paramref name="state"/>'s <see cref="PanelGridState.Origin"/>/<see cref="PanelGridState.WorkArea"/>
    /// still belong to the ORIGIN monitor even after the panel has been dragged onto a different one.
    /// <c>Behaviors.PanelDragMoveBehavior.EndDrag</c> used to call
    /// <see cref="ResolveGridDrop(Rect,PanelGridState,IReadOnlyList{Rect})"/> with that stale <paramref name="state"/>
    /// FIRST — whose <see cref="WorkspaceLayoutPolicy.SafeClamp(Rect,Rect,double,double)"/> step then
    /// clamped the panel's position straight back onto the origin monitor's work area (a visible
    /// teleport) — and only AFTERWARDS asked <c>UpdateMonitorDeviceIdOnDrop</c> which monitor the
    /// (by-then already-clamped-back) result belonged to, which of course always answered "the same one",
    /// silently regressing F-6 for every cross-monitor <see cref="PanelGeometryMode.GridAligned"/> drag.
    /// Calling this method FIRST — against the RAW, not-yet-clamped rectangle — and feeding its result
    /// into <see cref="ResolveGridDrop(Rect,PanelGridState,IReadOnlyList{Rect})"/> makes the clamp target
    /// the monitor the panel is ACTUALLY over.
    /// </para>
    /// <para>
    /// A no-op (returns <paramref name="state"/> unchanged) whenever <paramref name="state"/> is not
    /// grid-aligned, <paramref name="monitors"/> is empty, or <see cref="ResolveDropMonitor"/> itself
    /// reports no change (the overwhelmingly common case: the panel never left its starting monitor).
    /// </para>
    /// </remarks>
    /// <param name="current">The panel's RAW rectangle, exactly as the live drag left it (before any drop-time quantisation/clamp).</param>
    /// <param name="state">The gesture-start grid state to retarget.</param>
    /// <param name="monitors">The gesture's monitor snapshot, in screen DIP — the same snapshot <paramref name="state"/> was originally resolved from.</param>
    /// <returns>
    /// <paramref name="state"/>, with <see cref="PanelGridState.Origin"/>/<see cref="PanelGridState.WorkArea"/>/
    /// <see cref="PanelGridState.MonitorDeviceId"/> replaced by the monitor <paramref name="current"/>'s
    /// center actually falls on, when that differs from <paramref name="state"/>'s own; otherwise
    /// <paramref name="state"/> itself, unchanged.
    /// </returns>
    public static PanelGridState RetargetToDropMonitor(
        Rect current,
        PanelGridState state,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (!state.IsGridAligned) return state;

        var newDeviceId = ResolveDropMonitor(current, monitors, state.MonitorDeviceId);
        if (newDeviceId is null) return state;

        for (var i = 0; i < monitors.Count; i++)
        {
            if (!string.Equals(monitors[i].DeviceName, newDeviceId, StringComparison.OrdinalIgnoreCase)) continue;

            var origin = DesktopCoordinateSpace.ToWindowSpace(monitors[i].WorkArea.TopLeft);
            var workArea = DesktopCoordinateSpace.ToWindowSpace(monitors[i].WorkArea);
            return state with { Origin = origin, WorkArea = workArea, MonitorDeviceId = newDeviceId };
        }

        return state;
    }
}
