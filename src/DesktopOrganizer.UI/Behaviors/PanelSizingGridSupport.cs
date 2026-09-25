using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// The tiny operations both resize controllers need against an <see cref="IPanelSizingGridHost"/>
/// — kept in ONE place rather than copied into <see cref="PanelResizeController"/> and
/// <see cref="NativePanelResizeController"/> (docs/PLANS/workspace-grid-03-04-integration.md §3.4's
/// explicit warning about duplicated resize logic: <c>SnapWidthToGrid</c>/<c>ResizeSnapEdgePadding</c>
/// already cost this project once, baseline risk 10).
/// </summary>
/// <remarks>
/// Every member tolerates a <see langword="null"/> host and degenerate geometry by falling back to the
/// <see cref="PanelGeometryMode.LegacyFreeform"/> answer — the resize pipeline must never throw out of a
/// gesture callback just because the workspace grid could not be resolved for a frame.
/// </remarks>
internal static class PanelSizingGridSupport
{
    /// <summary>
    /// Fills <paramref name="buffer"/> with the anti-overlap obstacles for a
    /// <see cref="PanelGeometryMode.GridAligned"/> resize (P04-G3) and returns it, or returns
    /// <see langword="null"/> for a legacy gesture (no obstacle snapshot at all — resize anti-overlap
    /// stays an open ADR-017 item for that mode).
    /// </summary>
    /// <param name="host">The panel's grid host, or <see langword="null"/>.</param>
    /// <param name="gridState">The gesture's already-resolved grid state.</param>
    /// <param name="buffer">The controller-owned, reused obstacle buffer.</param>
    public static IReadOnlyList<Rect>? CollectObstacles(IPanelSizingGridHost? host, PanelGridState gridState, List<Rect> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        buffer.Clear();
        if (host is null || !gridState.IsGridAligned) return null;

        host.CollectResizeObstacles(buffer);
        return buffer;
    }

    /// <summary>
    /// The ONE workspace-grid quantisation pipeline for a <see cref="PanelGeometryMode.GridAligned"/>
    /// resize, shared by the per-frame path (<see cref="ResizeGestureSession.TryUpdate"/>, both the
    /// <c>Thumb</c> and the native <c>WM_SIZING</c> controller) and the commit path
    /// (<see cref="PersistSpans"/>) — so a frame the user sees and the geometry that is finally written
    /// can never be produced by two different formulas (§3.4's explicit warning, baseline risk 10):
    /// <list type="number">
    /// <item><see cref="WorkspaceLayoutPolicy.SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>
    /// — nearest whole span, both edges of each resized axis on the grid, the edge opposite
    /// <paramref name="edge"/> fixed when it was already on-grid.</item>
    /// <item><see cref="WorkspaceLayoutPolicy.ClampResizeToWorkArea"/> (P04 fix-cycle 1, MAJOR-1) — the
    /// MOVING edge is shrunk back inside the monitor's work area rather than left to be TRANSLATED by the
    /// mouse-up <c>SafeClamp</c>, which would move the anchor edge the user never touched.</item>
    /// <item><see cref="WorkspaceLayoutPolicy.ClampResizeToSlots"/> — panel-vs-panel anti-overlap, always
    /// last, same layering as the drag pipeline.</item>
    /// </list>
    /// A no-op (returns <paramref name="raw"/>) for a non-grid-aligned state or degenerate geometry.
    /// </summary>
    /// <param name="raw">The proposed rectangle for this frame (<see cref="ResizeGeometryCalculator.Apply"/>'s output) or at commit (the panel's live bounds).</param>
    /// <param name="edge">The edge/corner the gesture is dragging.</param>
    /// <param name="gridState">The gesture's already-resolved grid state.</param>
    /// <param name="obstacles">The other panels' bounds, or <see langword="null"/>/empty to skip anti-overlap.</param>
    /// <returns>A rectangle whose size is a whole span, on the grid, inside the work area and clear of every obstacle it can clear.</returns>
    public static Rect QuantiseResize(Rect raw, ResizeEdge edge, PanelGridState gridState, IReadOnlyList<Rect>? obstacles)
    {
        if (!gridState.IsGridAligned) return raw;

        // Defensive, same shape as ResizeGeometryCalculator.Apply's own H3 guard: never feed a
        // non-finite rectangle (or a degenerate unit size, which only a hand-built PanelGridState can
        // produce) into the policy, and never write one to Window.Width/Height.
        if (!IsFinite(raw) || !double.IsFinite(gridState.UnitSize) || gridState.UnitSize <= 0d) return raw;

        var snapped = WorkspaceLayoutPolicy.SnapBounds(
            raw, gridState.Origin, edge, gridState.UnitSize, gridState.Gap, gridState.MinSpan);

        var fitted = WorkspaceLayoutPolicy.ClampResizeToWorkArea(
            snapped, edge, gridState.WorkArea, gridState.UnitSize, gridState.Gap, gridState.MinSpan);

        return obstacles is not { Count: > 0 }
            ? fitted
            : WorkspaceLayoutPolicy.ClampResizeToSlots(
                fitted, edge, obstacles, gridState.UnitSize, gridState.Gap, gridState.MinSpan);
    }

    /// <summary>
    /// Commits the panel's final geometry AND its span in one act after a gesture ended (P04-G6):
    /// re-resolves the grid state against the final bounds, quantises them through
    /// <see cref="QuantiseResize"/> when the caller names the gesture's <paramref name="committedEdge"/>,
    /// and writes bounds + span back through the panel's <see cref="PanelGeometryWriter"/>. A no-op for a
    /// legacy panel or non-finite bounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>P04 fix-cycle 1, MAJOR-2 — why the bounds are rewritten, not just measured.</b> The previous
    /// version derived the span with <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/> (Ceiling) from
    /// whatever <c>Window.Width</c>/<c>Height</c> happened to read back, and committed THOSE bounds
    /// verbatim. Both halves were wrong together: the read-back value has been through
    /// <c>NativeSizingCoordinateMapper</c>'s px round trip, so at 150% a 312 DIP panel comes back as
    /// 312.667 — <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/> then says span 41 (320 DIP) while
    /// <see cref="PanelGeometryWriter"/> writes <c>model.Size = (int)312.667 = 312</c>, i.e. the window,
    /// the model size and the span all disagree, and the next gesture ratchets the span up again. Now the
    /// span is derived by NEAREST rounding (<see cref="WorkspaceLayoutPolicy.RoundBoundsToSpans"/>, immune
    /// to sub-DIP noise) and the bounds committed are <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> of
    /// that very span — so window, model and span are the same number by construction.
    /// </para>
    /// <para>
    /// <b>The commit-time quantisation is also what makes <c>Alt</c> safe.</b> While <c>Alt</c> is held,
    /// <see cref="ResizeGestureSession.TryUpdate"/> deliberately skips <see cref="QuantiseResize"/> so the
    /// edge tracks the cursor 1:1 (ADR-028 «Модификатор свободного движения», extended to resize in P04
    /// fix-cycle 1). Running the same pipeline here, at mouse-up/<c>WM_EXITSIZEMOVE</c>, is what still
    /// guarantees the ADR's absolute rule that a <see cref="PanelGeometryMode.GridAligned"/> panel never
    /// comes to REST off the grid — exactly as <c>EndDrag</c> does for the move gesture.
    /// </para>
    /// </remarks>
    /// <param name="host">The panel's grid host, or <see langword="null"/>.</param>
    /// <param name="finalBounds">The panel's committed bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="committedEdge">
    /// The edge the just-ended RESIZE gesture was dragging, or <see langword="null"/> for a caller that is
    /// not a resize at all (<see cref="RollUpBehavior"/> pinning the expanded height) — a
    /// <see langword="null"/> edge normalises the SIZE only, anchored at the top-left, and applies neither
    /// the work-area nor the anti-overlap clamp, because neither is this caller's business.
    /// </param>
    /// <param name="obstacleBuffer">
    /// A caller-owned, reused buffer for the anti-overlap snapshot. Must NOT be the same buffer the
    /// gesture's own <see cref="CollectObstacles"/> call filled: a deferred commit (M-3) can run after a
    /// NEW gesture already snapshotted into it. <see langword="null"/> allocates one for this call.
    /// </param>
    public static void PersistSpans(
        IPanelSizingGridHost? host,
        Rect finalBounds,
        ResizeEdge? committedEdge = null,
        List<Rect>? obstacleBuffer = null)
    {
        if (host is null) return;
        if (!IsFinite(finalBounds) || finalBounds.Width <= 0d || finalBounds.Height <= 0d) return;
        if (!host.ParticipatesInGrid) return;

        var state = host.ResolveGridState(finalBounds);
        if (!state.IsGridAligned || !double.IsFinite(state.UnitSize) || state.UnitSize <= 0d) return;

        Rect committed;
        if (committedEdge is { } edge)
        {
            var buffer = obstacleBuffer ?? [];
            buffer.Clear();
            host.CollectResizeObstacles(buffer);

            committed = QuantiseResize(finalBounds, edge, state, buffer);
        }
        else
        {
            committed = finalBounds;
        }

        var span = WorkspaceLayoutPolicy.ClampToMinimum(
            WorkspaceLayoutPolicy.RoundBoundsToSpans(committed.Size, state.UnitSize, state.Gap), state.MinSpan);

        // MAJOR-2: the exact span extent, anchored at the (already anchor-correct) top-left of the
        // quantised rectangle — never the raw read-back size.
        var exact = WorkspaceLayoutPolicy.SpansToBounds(span, state.UnitSize, state.Gap);

        host.CommitGridGeometry(new Rect(committed.X, committed.Y, exact.Width, exact.Height), span);
    }

    /// <summary>
    /// F-2 gate (§3.7): <see langword="true"/> when the panel is grid-aligned and therefore must NOT be
    /// re-snapped to the ICON grid (System C) by an <c>IconSize</c>/<c>ViewMode</c>/<c>IconLayoutPolicy</c>
    /// change — "IconSize never changes a GridAligned panel's bounds" is absolute (ADR-028).
    /// </summary>
    /// <remarks>
    /// P04 fix-cycle 1 (MINOR): short-circuits on <see cref="IPanelSizingGridHost.ParticipatesInGrid"/>
    /// first, so a <see cref="PanelGeometryMode.LegacyFreeform"/> panel does not pay for an
    /// <c>IMonitorService.GetMonitors()</c> enumeration on every single <c>IconSize</c> slider tick.
    /// </remarks>
    /// <param name="host">The panel's grid host, or <see langword="null"/>.</param>
    /// <param name="bounds">The panel's current bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
    public static bool IsGridAligned(IPanelSizingGridHost? host, Rect bounds)
        => host is not null && host.ParticipatesInGrid && host.ResolveGridState(bounds).IsGridAligned;

    /// <summary>
    /// P06 fix-cycle MAJOR-4: the fourth tiny shared operation (see class remarks) — calls
    /// <see cref="IPanelSizingGridHost.NotifyHeightResizeCommitted"/> when <paramref name="edge"/> moved
    /// the panel's HEIGHT, for EITHER <see cref="PanelGeometryMode"/> (deliberately not gated on
    /// <see cref="PanelGeometryMode.GridAligned"/> the way <see cref="PersistSpans"/> is — the
    /// <c>AutoContent -&gt; Manual</c> transition applies uniformly to <see cref="PanelGeometryMode.LegacyFreeform"/>
    /// too, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"). A no-op for a <see langword="null"/> host
    /// or a width-only edge.
    /// </summary>
    /// <param name="host">The panel's grid host, or <see langword="null"/>.</param>
    /// <param name="edge">The edge/corner the just-ended gesture was resizing.</param>
    public static void NotifyHeightResizeCommitted(IPanelSizingGridHost? host, ResizeEdge edge)
    {
        if (host is not null && edge.AffectsHeight())
            host.NotifyHeightResizeCommitted();
    }

    /// <summary>
    /// ADR-011 Amendment 2026-09-24 (resize extension): the idempotent commit-time safety net that
    /// guarantees a resize gesture never comes to rest off <c>IAppSettings.GridSize</c>'s lattice, shared
    /// by both resize controllers' end-of-gesture path (mirrors why <see cref="PersistSpans"/> itself is
    /// shared — one formula, not two copies that can silently diverge). In the overwhelmingly common
    /// case this is a pure no-op: <see cref="ResizeGestureSession.TryUpdate"/> already quantises every
    /// live frame to <see cref="ResizeConstraints.SizeGridStep"/> (see that session's remarks), so the
    /// panel's bounds are already on the GridSize lattice by the time a gesture ends. It only actually
    /// moves anything in the residual case the ADR-028 Alt/GridAligned commit-time re-snap ALSO exists
    /// for: the free-resize modifier (<c>Alt</c>) held for the gesture's very last frame (or the whole
    /// gesture), which suppresses GridSize quantisation per-frame (see
    /// <see cref="ResizeGestureSession.TryUpdate"/>'s <c>freeFrame</c> branch) but must never leave the
    /// panel resting off-grid once the mouse is released.
    /// </summary>
    /// <remarks>
    /// Called AFTER <see cref="PersistSpans"/> in both controllers' end-of-gesture path — for a
    /// <see cref="PanelGeometryMode.GridAligned"/> panel <see cref="PersistSpans"/> has already put the
    /// committed bounds on a whole span (and, when <paramref name="gridSize"/> is commensurate with
    /// <see cref="WorkspaceGridMetrics.UnitSize"/>, ALSO on the GridSize lattice — see
    /// <see cref="ResizeConstraintsFactory.BuildConstraints"/>'s commensurability gate), so re-resolving
    /// <paramref name="host"/>'s grid state here and re-deriving <see cref="ResizeConstraints.SizeGridStep"/>
    /// from it is a genuine no-op in that case (<see cref="Models.GridSizeNormalizer.NormalizeExtent"/> is
    /// itself idempotent on an already-exact multiple — see that method's own remarks). For a
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> panel (or no host at all) <see cref="PersistSpans"/>
    /// was a no-op, and THIS method is the only thing that ever re-aligns <c>Width</c>/<c>Height</c> to
    /// GridSize at commit — <c>PanelDragMoveBehavior.SnapToGrid</c> (the <c>_snapToGrid()</c> call
    /// immediately following this one in both controllers) only ever re-snaps POSITION, never size.
    /// </remarks>
    /// <param name="host">The panel's grid host, or <see langword="null"/> (LegacyFreeform).</param>
    /// <param name="model">The panel's current model — resolves the same MinWidth/MinHeight floor <see cref="ResizeConstraintsFactory.BuildConstraints"/> would for a fresh gesture on it right now.</param>
    /// <param name="snapResizeToIconGrid"><c>IAppSettings.SnapResizeToIconGrid</c> — irrelevant whenever <paramref name="gridSize"/> is active (GridSize has priority), consulted only to resolve the SAME <see cref="ResizeConstraints"/> a fresh gesture would.</param>
    /// <param name="titleBarHeight"><c>ChildWindow.TitleBarHeight</c> — irrelevant to this method's own math, forwarded only because <see cref="ResizeConstraintsFactory.BuildConstraints"/> requires it.</param>
    /// <param name="gridSize"><c>IAppSettings.GridSize</c>. Values <c>&lt;= 1</c> make this method a no-op.</param>
    /// <param name="finalBounds">The panel's committed bounds (post-<see cref="PersistSpans"/>), in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="committedEdge">
    /// The edge/corner the just-ended resize gesture was dragging — ONLY the axis/axes
    /// <see cref="ResizeEdgeParser.AffectsWidth"/>/<see cref="ResizeEdgeParser.AffectsHeight"/> report for
    /// it are normalised (mirrors <see cref="ResizeGeometryCalculator.Apply"/>'s own "point 7": the
    /// untouched axis is returned byte-for-byte unchanged, never opportunistically re-aligned just
    /// because this gesture happened to touch the OTHER axis). <see langword="null"/> normalises BOTH
    /// axes, anchored at the top-left (mirrors <see cref="PersistSpans"/>'s own <c>committedEdge</c>
    /// contract for a non-resize caller).
    /// </param>
    /// <returns><paramref name="finalBounds"/> unchanged, or the GridSize-realigned bounds.</returns>
    public static Rect NormalizeCommittedSize(
        IPanelSizingGridHost? host,
        ChildWindowModel? model,
        bool snapResizeToIconGrid,
        double titleBarHeight,
        int gridSize,
        Rect finalBounds,
        ResizeEdge? committedEdge)
    {
        if (gridSize <= 1) return finalBounds;
        if (!IsFinite(finalBounds) || finalBounds.Width <= 0d || finalBounds.Height <= 0d) return finalBounds;

        var state = host is not null && host.ParticipatesInGrid
            ? host.ResolveGridState(finalBounds)
            : PanelGridState.Legacy;

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapResizeToIconGrid, titleBarHeight, state, gridSize);
        if (constraints.SizeGridStep <= 1d) return finalBounds;

        // Point 7 (ResizeGeometryCalculator.Apply's own contract, mirrored here): a specific edge only
        // normalises the axis/axes it actually moves — a Right-edge commit must never also "fix" an
        // unrelated, off-grid HEIGHT the gesture never touched. A null edge (no specific axis constraint)
        // normalises both, matching PersistSpans' own no-edge contract.
        var normalizeWidth = committedEdge is not { } widthEdge || widthEdge.AffectsWidth();
        var normalizeHeight = committedEdge is not { } heightEdge || heightEdge.AffectsHeight();

        var width = normalizeWidth
            ? GridSizeNormalizer.NormalizeExtent(finalBounds.Width, constraints.SizeGridStep, constraints.MinWidth)
            : finalBounds.Width;
        var height = normalizeHeight
            ? GridSizeNormalizer.NormalizeExtent(finalBounds.Height, constraints.SizeGridStep, constraints.MinHeight)
            : finalBounds.Height;

        if (width == finalBounds.Width && height == finalBounds.Height) return finalBounds;

        var left = committedEdge is { } leftEdge && leftEdge.AnchorsRight() ? finalBounds.Right - width : finalBounds.Left;
        var top = committedEdge is { } topEdge && topEdge.AnchorsBottom() ? finalBounds.Bottom - height : finalBounds.Top;

        return new Rect(left, top, width, height);
    }

    private static bool IsFinite(Rect rect)
        => double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);
}
