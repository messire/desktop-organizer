namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The result of a successful <see cref="WorkspaceGridPlacement.TryAlign"/>: everything the caller must
/// write, and nothing it must compute itself.
/// </summary>
/// <param name="Bounds">The aligned bounds, in <c>Window.Left</c>/<c>Top</c> space.</param>
/// <param name="Span">The whole-unit span <paramref name="Bounds"/>'s size corresponds to.</param>
/// <param name="MonitorDeviceId">The device name of the monitor whose work area supplied the origin.</param>
public readonly record struct AlignedPanelGeometry(Rect Bounds, GridSpan Span, string? MonitorDeviceId);

/// <summary>
/// Pure placement operations on the workspace grid that are NOT part of a drag gesture (P03,
/// docs/PLANS/workspace-grid-03-04-integration.md §2.5/§2.6): the explicit "Align to workspace grid"
/// command (both its per-panel and its all-panels form) and the first-free-slot search used when a new
/// <see cref="PanelGeometryMode.GridAligned"/> panel is created.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PanelDragPipeline"/> because the two answer different questions: the
/// drag pipeline never changes a panel's SIZE or its <see cref="PanelGeometryMode"/>, while these
/// operations do both. Both are pure (no WPF window, no services), so the whole "Align" semantics is
/// unit-testable; the view/window manager layer only writes the result.
/// </remarks>
public static class WorkspaceGridPlacement
{
    /// <summary>
    /// The bounds the workspace grid must reason about for a panel whose live size may be misleading:
    /// the EXPANDED height is substituted while the panel is rolled up, and the persisted size is
    /// substituted on an axis that has not been measured yet.
    /// </summary>
    /// <remarks>
    /// <b>Why this rule has to be shared.</b> Both the "Align" command and
    /// <see cref="PanelGridStateResolver.Resolve"/>'s lazy span derivation turn a panel's CURRENT SIZE
    /// into a whole-unit span. A rolled-up panel's live <c>ActualHeight</c> is its collapsed header
    /// height, so deriving a span from it would write a ~4-unit height span onto the model and the panel
    /// would come back from the next restore permanently collapsed — the size, not just the display,
    /// would have been destroyed by a gesture that never resized anything. The same applies to a panel on
    /// an inactive page, which has never been measured at all (<c>ActualWidth/Height == 0</c>).
    /// <see cref="ChildWindowModel.Size"/> is the correct fallback in both cases: it deliberately keeps
    /// the expanded dimensions (<c>ChildWindow.OnRenderSizeChanged</c> skips its write while rolled up).
    /// </remarks>
    /// <param name="topLeft">The panel's live <c>Window.Left</c>/<c>Top</c>.</param>
    /// <param name="liveSize">The panel's live <c>ActualWidth</c>/<c>ActualHeight</c>.</param>
    /// <param name="model">The panel's model, or <see langword="null"/> before <c>Initialize</c>.</param>
    /// <returns>The bounds to feed to the grid, in window space.</returns>
    public static Rect ExpandedBounds(Point topLeft, Size liveSize, ChildWindowModel? model)
    {
        var modelWidth = model?.Size.Width ?? 0;
        var modelHeight = model?.Size.Height ?? 0;

        var width = liveSize.Width > 0d ? liveSize.Width : modelWidth;
        var height = model?.IsRolledUp == true || !(liveSize.Height > 0d) ? modelHeight : liveSize.Height;

        return new Rect(topLeft, new Size(Math.Max(0d, width), Math.Max(0d, height)));
    }

    /// <summary>
    /// The "Align to workspace grid" command, ON direction (ADR-028 «Явная команда»): switches
    /// <paramref name="model"/> to <see cref="PanelGeometryMode.GridAligned"/> and computes its aligned
    /// geometry — (1) position quantised to the panel's monitor's work-area origin, (2) size rounded UP to
    /// the smallest whole span that still covers the current content (<see cref="Math.Ceiling(double)"/>,
    /// never <see cref="Math.Round(double)"/> — a one-shot alignment must not clip anything), clamped to
    /// at least the minimum span, (3) the result pulled inside the work area, and (4) finally passed
    /// through the same anti-overlap wall every drag frame uses, so the panel may legitimately stop short
    /// of its ideal slot rather than teleport into a neighbour.
    /// </summary>
    /// <remarks>
    /// Mutates <paramref name="model"/>'s <see cref="ChildWindowModel.GeometryMode"/> (and, via
    /// <see cref="PanelGridStateResolver.Resolve"/>, possibly its spans) — this is the one command whose
    /// job is exactly that. On failure the previous mode is restored, so a panel is never left labelled
    /// <see cref="PanelGeometryMode.GridAligned"/> without usable geometry (the failure modes are "no
    /// monitors at all" and "degenerate current size", both of which <see cref="PanelGridStateResolver"/>
    /// already treats as fail-safe conditions).
    /// </remarks>
    /// <param name="model">The panel to align.</param>
    /// <param name="currentExpandedBoundsWindowSpace">
    /// The panel's current bounds, in window space, using its EXPANDED height even when it is rolled up
    /// (a collapsed header is not the size the user is aligning).
    /// </param>
    /// <param name="monitors">Monitor snapshot, in screen DIP: <c>(DeviceName, Bounds, WorkArea)</c>.</param>
    /// <param name="obstacles">The other live panels' rectangles — the anti-overlap wall for step (4).</param>
    /// <param name="geometry">The geometry to write when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the panel was aligned; <see langword="false"/> when nothing changed.</returns>
    public static bool TryAlign(
        ChildWindowModel model,
        Rect currentExpandedBoundsWindowSpace,
        IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> monitors,
        IReadOnlyList<Rect> obstacles,
        out AlignedPanelGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(obstacles);

        geometry = default;

        // A panel whose live size is not known yet (never measured, e.g. it lives on an inactive page)
        // must not be "aligned" — quantising a zero size would shrink it to the minimum span behind the
        // user's back. Fail safe and leave it alone; the command is idempotent and can be re-run later.
        if (!(currentExpandedBoundsWindowSpace.Width > 0d) || !(currentExpandedBoundsWindowSpace.Height > 0d))
            return false;

        var previousMode = model.GeometryMode;

        // The command IS the mode switch, so the mode is set first and the shared resolver then answers
        // the usual questions (which monitor, which origin, which minimum span) exactly as it would for
        // an already-aligned panel — no second, parallel "forced" resolution path (Инвариант В1).
        model.GeometryMode = PanelGeometryMode.GridAligned;

        var state = PanelGridStateResolver.Resolve(model, currentExpandedBoundsWindowSpace, monitors, isRolledUp: false);
        if (!state.IsGridAligned)
        {
            model.GeometryMode = previousMode;
            return false;
        }

        var snapped = WorkspaceLayoutPolicy.SnapBounds(
            currentExpandedBoundsWindowSpace, state.Origin, state.UnitSize, state.Gap, state.MinSpan);
        var inWorkArea = WorkspaceLayoutPolicy.SafeClamp(snapped, state.WorkArea, state.UnitSize, state.Gap);

        // The collision pass must see the panel at its NEW size (the alignment may have grown it) but at
        // its OLD position, so "how far can it travel from where it is now" is answered honestly.
        var resized = new Rect(currentExpandedBoundsWindowSpace.TopLeft, inWorkArea.Size);
        var final = PanelDragPipeline.ClampHardWall(resized, inWorkArea.X, inWorkArea.Y, obstacles, state.Gap);

        var span = WorkspaceLayoutPolicy.BoundsToSpans(inWorkArea.Size, state.UnitSize, state.Gap);
        geometry = new AlignedPanelGeometry(final, span, state.MonitorDeviceId);
        return true;
    }

    /// <summary>
    /// The "Align to workspace grid" command, OFF direction (OQ-3 = B): drops the panel back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> and clears its spans. The panel's current
    /// <see cref="ChildWindowModel.Position"/>/<see cref="ChildWindowModel.Size"/> are deliberately left
    /// exactly as they are — unlike alignment, un-alignment has no single "correct" target geometry
    /// (ADR-028: «обратной команды … не имеет очевидного безопасного целевого положения»), so the panel
    /// simply stops being governed by the grid where it stands.
    /// </summary>
    /// <param name="model">The panel to release from the workspace grid.</param>
    public static void ClearAlignment(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.GeometryMode = PanelGeometryMode.LegacyFreeform;
        model.WidthSpanUnits = null;
        model.HeightSpanUnits = null;
    }

    /// <summary>
    /// First free slot for a NEW <see cref="PanelGeometryMode.GridAligned"/> panel (P03-L3, baseline M8:
    /// every "Add panel" used to create the panel at <c>(0,0)</c>, stacking new panels exactly on top of
    /// each other). Scans the work area's slots in row-major order and returns the first one where a
    /// <paramref name="size"/> panel fits entirely inside the work area and keeps at least
    /// <paramref name="gap"/> DIP of clearance from every existing panel.
    /// </summary>
    /// <remarks>
    /// Row-major (left to right, then down) is the reading order of every desktop organiser this project
    /// is modelled on, and — more importantly — it is DETERMINISTIC: the same panel set always produces
    /// the same answer, so creating N panels in a row fills the workspace predictably instead of depending
    /// on dictionary iteration order. Clearance is tested by inflating the CANDIDATE by
    /// <paramref name="gap"/> (rather than every obstacle), which is equivalent and allocation-free; two
    /// panels exactly <paramref name="gap"/> apart touch without overlapping, which
    /// <see cref="PanelCollision.Overlaps(Rect,Rect)"/>'s tolerance correctly reports as free.
    /// </remarks>
    /// <param name="workArea">The target monitor's work area, in window space (its top-left is the grid origin).</param>
    /// <param name="size">The new panel's size, in DIP.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>.</param>
    /// <param name="gap">The panel gap <c>G</c>.</param>
    /// <param name="obstacles">Existing panels' rectangles.</param>
    /// <returns>The free slot's top-left, or <see langword="null"/> when the work area has no room left.</returns>
    public static Point? FindFreeSlot(
        Rect workArea,
        Size size,
        double unitSize,
        double gap,
        IReadOnlyList<Rect> obstacles)
    {
        ArgumentNullException.ThrowIfNull(obstacles);

        if (!double.IsFinite(unitSize) || unitSize <= 0d) return null;
        if (workArea.IsEmpty || workArea.Width <= 0d || workArea.Height <= 0d) return null;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0d || size.Height <= 0d) return null;

        var safeGap = double.IsFinite(gap) && gap > 0d ? gap : 0d;
        var origin = workArea.TopLeft;

        var maxCol = (int)Math.Floor((workArea.Width - size.Width) / unitSize);
        var maxRow = (int)Math.Floor((workArea.Height - size.Height) / unitSize);
        if (maxCol < 0 || maxRow < 0) return null;

        for (var row = 0; row <= maxRow; row++)
        {
            for (var col = 0; col <= maxCol; col++)
            {
                var position = WorkspaceLayoutPolicy.SlotToPosition((col, row), origin, unitSize);
                var candidate = new Rect(
                    position.X - safeGap,
                    position.Y - safeGap,
                    size.Width + (2d * safeGap),
                    size.Height + (2d * safeGap));

                var free = true;
                for (var i = 0; i < obstacles.Count; i++)
                {
                    if (!PanelCollision.Overlaps(candidate, obstacles[i])) continue;
                    free = false;
                    break;
                }

                if (free) return position;
            }
        }

        return null;
    }
}
