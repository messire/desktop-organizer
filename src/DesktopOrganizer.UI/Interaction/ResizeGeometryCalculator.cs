using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// The raw (unquantised) accumulated extent of an in-progress resize gesture — replaces the
/// pre-extraction <c>PanelResizeController</c>'s private <c>_rawWidth</c>/<c>_rawHeight</c> fields.
/// </summary>
public readonly record struct ResizeRawExtent(double Width, double Height);

/// <summary>
/// Pure resize geometry math — zero WPF dependencies beyond <see cref="Rect"/>/<see cref="Vector"/>,
/// zero mutable state. Extracted bit-for-bit from <c>PanelResizeController.OnResizeDragStarted</c>'s
/// raw-accumulator setup and <c>OnResizeDragDelta</c>'s quantize/clamp/reposition math
/// (docs/PLANS/phase-1-resize-stabilization.md §2.5) — the single source of truth for panel resize
/// snap semantics; Phase 2's native <c>WM_SIZING</c> handler is expected to call this same type (plan
/// §0, "R6").
/// </summary>
/// <remarks>
/// An earlier, simpler seam for this same idea exists at
/// <c>docs/Refactoring/UI/ResizeGeometryCalculator.cs</c> (form-only draft — kept in place as a
/// refactoring-package artifact, not deleted/moved). That draft computes from "start bounds + total
/// delta" and knows nothing about chrome, snapping, or <see cref="Math.Ceiling(double)"/>; THIS type is
/// the production implementation and intentionally diverges from the draft's body — see
/// docs/PLANS/phase-1-resize-stabilization.md §2.5 for the full contract, most importantly point 5: the
/// free-mode height branch is DELIBERATELY asymmetric with the free-mode width branch. Do not "clean
/// up" that asymmetry.
/// </remarks>
public static class ResizeGeometryCalculator
{
    /// <summary>
    /// Accumulates one <c>Thumb.DragDelta</c> into the gesture's raw (unquantised) extent. Mirrors the
    /// pre-extraction <c>OnResizeDragDelta:136-154</c> sign convention exactly: <see cref="ResizeEdge.Right"/>/
    /// <see cref="ResizeEdge.Bottom"/> (and every corner containing them) ADD the delta;
    /// <see cref="ResizeEdge.Left"/>/<see cref="ResizeEdge.Top"/> (and every corner containing them)
    /// SUBTRACT it. An edge that doesn't affect an axis (<see cref="ResizeEdgeParser.AffectsWidth"/>/
    /// <see cref="ResizeEdgeParser.AffectsHeight"/>) leaves that axis of <paramref name="current"/> untouched.
    /// </summary>
    /// <param name="current">The gesture's raw extent accumulated so far.</param>
    /// <param name="edge">The edge/corner being dragged, selecting which axes and signs the delta applies to.</param>
    /// <param name="delta">The incremental cursor movement reported by this <c>Thumb.DragDelta</c>.</param>
    public static ResizeRawExtent Accumulate(ResizeRawExtent current, ResizeEdge edge, Vector delta)
    {
        var width = current.Width;
        var height = current.Height;

        if (edge.AffectsWidth())
            width += edge.AnchorsRight() ? -delta.X : delta.X;

        if (edge.AffectsHeight())
            height += edge.AnchorsBottom() ? -delta.Y : delta.Y;

        return new ResizeRawExtent(width, height);
    }

    /// <summary>
    /// Quantizes/clamps <paramref name="raw"/> into the panel's next <see cref="Rect"/>, repositioning
    /// the dragged edge(s) while keeping the opposite (anchor) edge(s) fixed. Mirrors the pre-extraction
    /// <c>OnResizeDragDelta:156-214</c> line for line — see
    /// docs/PLANS/phase-1-resize-stabilization.md §2.5 for the numbered contract this method must keep.
    /// </summary>
    /// <param name="current">The panel's current on-screen <see cref="Rect"/> (read for the axis the
    /// dragged edge does NOT affect — that axis is returned unchanged, point 7).</param>
    /// <param name="edge">The edge/corner being dragged.</param>
    /// <param name="raw">The gesture's raw (unquantised) accumulated extent (<see cref="Accumulate"/>).</param>
    /// <param name="anchorRight">The fixed right-edge anchor, snapshotted once at gesture start
    /// (<c>StartBounds.Right</c>) — used only when <paramref name="edge"/> anchors the right edge.</param>
    /// <param name="anchorBottom">The fixed bottom-edge anchor, snapshotted once at gesture start
    /// (<c>StartBounds.Bottom</c>) — used only when <paramref name="edge"/> anchors the bottom edge.</param>
    /// <param name="constraints">Snap/chrome/floor configuration for this gesture (fixed for its whole
    /// duration — see <see cref="ResizeConstraints"/>'s remarks).</param>
    /// <returns>
    /// The next <see cref="Rect"/> to apply to the panel window, or <paramref name="current"/>
    /// unchanged if any of <paramref name="raw"/>/<paramref name="anchorRight"/>/
    /// <paramref name="anchorBottom"/>/<paramref name="constraints"/>'s fields is not
    /// <see cref="double.IsFinite(double)"/> (H3 guard, plan §2.5 — never write
    /// <c>NaN</c>/<c>Infinity</c> into <c>Window.Width</c>/<c>Height</c>).
    /// </returns>
    public static Rect Apply(
        Rect current,
        ResizeEdge edge,
        ResizeRawExtent raw,
        double anchorRight,
        double anchorBottom,
        ResizeConstraints constraints)
    {
        // finalization MAJOR M-5: the guard originally covered only raw/anchorRight/anchorBottom, not
        // constraints' own fields (e.g. MinWidth) — reachable if a caller ever derives a constraint from
        // a NaN input (model.IconSize == NaN, say). A non-finite MinWidth alone is enough to make the
        // point-8 invariant below (`newWidth >= constraints.MinWidth`) evaluate to false (NaN comparisons
        // are always false), which would trip the Debug.Assert two lines down — and on .NET 8,
        // Debug.Assert (like Debug.Fail) terminates the process outright when no debugger is attached,
        // instead of leaving the gesture harmlessly unchanged the way this guard intends. Covering every
        // constraints field here makes that assert unreachable via a non-finite input.
        if (!double.IsFinite(raw.Width) || !double.IsFinite(raw.Height) ||
            !double.IsFinite(anchorRight) || !double.IsFinite(anchorBottom) ||
            !double.IsFinite(constraints.MinWidth) || !double.IsFinite(constraints.MinHeight) ||
            !double.IsFinite(constraints.SnapCellWidth) || !double.IsFinite(constraints.SnapCellHeight) ||
            !double.IsFinite(constraints.TitleBarHeight) || !double.IsFinite(constraints.HorizontalChrome) ||
            !double.IsFinite(constraints.VerticalChrome) || !double.IsFinite(constraints.SizeGridStep))
        {
            return current;
        }

        double newWidth;
        double newHeight;

        if (constraints.SizeGridStep > 0d)
        {
            // ADR-011 Amendment 2026-09-24 (resize extension): IAppSettings.GridSize takes priority over
            // the icon-grid snap below — reuses GridSizeNormalizer.NormalizeExtent (round-half-up,
            // unconditional, the SAME function WindowManager already applies to a panel's persisted
            // bounds at restore) directly on the RAW window Width/Height, deliberately WITHOUT stripping
            // HorizontalChrome/TitleBarHeight/VerticalChrome first — GridSize is a whole-panel concept
            // (see ResizeConstraints.SizeGridStep's remarks), unrelated to icon-grid content geometry, so
            // there is no "content" to strip chrome down to here. MinWidth/MinHeight are still whichever
            // floor ResizeConstraintsFactory.BuildConstraints resolved for this gesture (icon-grid-derived,
            // flat List floor, or the GridAligned MinSpan-derived floor) — NormalizeExtent enforces them
            // internally, so no separate Math.Max wrapper is needed here.
            newWidth = GridSizeNormalizer.NormalizeExtent(raw.Width, constraints.SizeGridStep, constraints.MinWidth);
            newHeight = GridSizeNormalizer.NormalizeExtent(raw.Height, constraints.SizeGridStep, constraints.MinHeight);
        }
        else
        {
            // Point 1 — strip chrome before quantising, for both axes.
            var contentW = Math.Max(0d, raw.Width - constraints.HorizontalChrome);
            var contentH = Math.Max(0d, raw.Height - constraints.TitleBarHeight - constraints.VerticalChrome);

            // Points 2/3 — width: snap branch re-adds HorizontalChrome after quantising (Math.Ceiling —
            // never round down, see ResizeSnapEdgePadding's remarks); free branch re-adds it too
            // (content + chrome), symmetrically.
            if (constraints.SnapCellWidth > 0d)
            {
                var snappedWidth = AdaptiveGridLayout.SnapExtent(
                    contentW,
                    constraints.SnapCellWidth,
                    AdaptiveGridLayout.DefaultMinGap,
                    ResizeSnapEdgePadding(constraints.SnapCellWidth));
                newWidth = Math.Ceiling(Math.Max(constraints.MinWidth, snappedWidth + constraints.HorizontalChrome));
            }
            else
            {
                newWidth = Math.Max(constraints.MinWidth, contentW + constraints.HorizontalChrome);
            }

            // Points 4/5 — height: snap branch re-adds chrome after quantising (symmetric with width), but
            // the FREE branch deliberately uses raw.Height DIRECTLY, NOT `contentH + TitleBarHeight +
            // VerticalChrome` — unlike the free WIDTH branch just above, which DOES re-add
            // (`contentW + constraints.HorizontalChrome`, not raw.Width). plan §2.5 point 5 forbids "fixing"
            // this width/height asymmetry. NOTE: this was never a numeric no-op, at either constants' value —
            // `contentH + TitleBarHeight + VerticalChrome` equals raw.Height only away from the
            // `Math.Max(0d, ...)` floor above; the two forms genuinely diverge once that floor clamps
            // (raw.Height smaller than TitleBarHeight + VerticalChrome), which is precisely the case this
            // deliberate asymmetry is about — VerticalChrome (GridMetrics.FileListVPadding = 4) has never
            // been 0, and this is independent of HorizontalChrome/GridMetrics.FileListHPadding (a completely
            // different axis's chrome; see ADR-029's F-3 fix for why THAT one changed from 0 to
            // PanelMetrics.ContentLeftInset = 8).
            if (constraints.SnapCellHeight > 0d)
            {
                var snappedHeight = AdaptiveGridLayout.SnapExtent(
                    contentH,
                    constraints.SnapCellHeight,
                    AdaptiveGridLayout.DefaultMinGap,
                    ResizeSnapVerticalEdgePadding(constraints.SnapCellHeight));
                newHeight = Math.Ceiling(Math.Max(
                    constraints.MinHeight,
                    constraints.TitleBarHeight + constraints.VerticalChrome + snappedHeight));
            }
            else
            {
                newHeight = Math.Max(constraints.MinHeight, raw.Height);
            }
        }

        // Point 8 — the calculator must never return a width below MinWidth; both branches above are
        // constructed so this always holds (Math.Max/Math.Ceiling of a Math.Max), asserted defensively.
        System.Diagnostics.Debug.Assert(newWidth >= constraints.MinWidth, "ResizeGeometryCalculator.Apply must never return a width below MinWidth.");

        double left = current.Left, top = current.Top, width = current.Width, height = current.Height;

        // Point 7 — width applied only on the dragged axis; the untouched axis returns `current` as-is.
        if (edge.AnchorsRight())
        {
            left = anchorRight - newWidth;
            width = newWidth;
        }
        else if (edge.AffectsWidth())
        {
            width = newWidth;
        }

        // Point 7 — same shape for height.
        if (edge.AnchorsBottom())
        {
            top = anchorBottom - newHeight;
            height = newHeight;
        }
        else if (edge.AffectsHeight())
        {
            height = newHeight;
        }

        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// HORIZONTAL edge padding (<c>m</c>) used by <see cref="Apply"/>'s width branch, moved here
    /// verbatim from the pre-extraction <c>PanelResizeController.ResizeSnapEdgePadding</c> (plan §2.5
    /// point 6 — this doc comment carries the history of two production bugs and must travel with the
    /// code). Deliberately always uses <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/> rather
    /// than reading <c>AdaptiveIconGridPanel.EdgePaddingRatio</c> off the live panel — this class has no
    /// reachable reference to the panel instance buried inside the <c>ListBox</c>'s <c>DataTemplate</c>.
    /// This is an intentional, documented compromise: if <c>MinGap</c>/<c>EdgePaddingRatio</c> are ever
    /// overridden in Templates.xaml's <c>ItemsPanelTemplate</c>, update this method (and the
    /// <see cref="AdaptiveGridLayout.DefaultMinGap"/> use above it) to match, or resize-snap will
    /// disagree with the actual on-screen column count. See <see cref="ResizeSnapVerticalEdgePadding"/>
    /// for the VERTICAL (height) counterpart — the two axes no longer share one ratio/constant (bug
    /// fix: a single row of icons was reporting double its actual height, tripping a spurious
    /// scrollbar).
    /// </summary>
    private static double ResizeSnapEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    /// <summary>
    /// VERTICAL edge padding (<c>m_v</c>) used by <see cref="Apply"/>'s height branch, moved here
    /// verbatim from the pre-extraction <c>PanelResizeController.ResizeSnapVerticalEdgePadding</c> (plan
    /// §2.5 point 6) — uses the separate, smaller
    /// <see cref="AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio"/> (<c>0</c>, deliberately NOT the
    /// same as <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/> used by
    /// <see cref="ResizeSnapEdgePadding"/> — see that method's doc comment for the bug this fixes). Same
    /// "no reachable panel reference" compromise as <see cref="ResizeSnapEdgePadding"/> applies here
    /// too: if <c>AdaptiveIconGridPanel.VerticalEdgePaddingRatio</c> is ever overridden in
    /// Templates.xaml, update this method to match, or height resize-snap will silently desync from
    /// what the panel actually renders.
    /// </summary>
    private static double ResizeSnapVerticalEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio);
}
