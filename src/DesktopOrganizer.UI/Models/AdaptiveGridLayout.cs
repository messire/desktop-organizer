namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static helpers for laying out a uniform-cell grid of <c>N</c> items inside a variable-width
/// container, distributing any leftover space into the gaps between cells rather than stretching the
/// cells themselves (docs/PLANS/adaptive-icon-grid.md — Fences-parity "adaptive equal-distribution"
/// icon grid). No WPF <see cref="System.Windows.DependencyObject"/>/<see cref="System.Windows.Media.Visual"/>
/// dependencies whatsoever — only <see cref="double"/>/<see cref="int"/> — so it is fully unit-testable
/// without STA/WPF test infrastructure. Fourth computational helper alongside <see cref="GridMetrics"/>
/// (ADR-005), <see cref="GridSnap"/> (ADR-011), and <see cref="PanelCollision"/> (ADR-017); unrelated to
/// any of them — the only relationship is that <see cref="Controls.AdaptiveIconGridPanel"/> (the WPF
/// consumer of this class) replaces <c>WrapPanel</c> as the geometry layer under the same
/// <c>ListBoxItem</c> containers <see cref="GridMetrics"/> already describes.
/// </summary>
/// <remarks>
/// Symbol mapping (plan §2, "D-0"): <c>x</c>/<c>x_v</c> are the measured cell width/height (a
/// <c>ListBoxItem</c>'s <see cref="System.Windows.UIElement.DesiredSize"/>; the analytical twin is
/// <see cref="GridMetrics.CellSize"/>/<see cref="GridMetrics.RowHeight"/>), <c>pmin</c>/<c>pmin_v</c> is
/// the minimum gap between cells (<see cref="Controls.AdaptiveIconGridPanel.MinGap"/>), and
/// <c>m</c>/<c>m_v</c> is the edge padding (<c>cell * ratio</c>) — <c>m</c> from
/// <see cref="Controls.AdaptiveIconGridPanel.EdgePaddingRatio"/> (horizontal,
/// <see cref="DefaultEdgePaddingRatio"/> = 0.5) and <c>m_v</c> from the separate
/// <see cref="Controls.AdaptiveIconGridPanel.VerticalEdgePaddingRatio"/> (vertical,
/// <see cref="DefaultVerticalEdgePaddingRatio"/> = 0 — see that constant's doc comment for why the two
/// axes no longer share one ratio). The same two formulas
/// (<see cref="Extent"/>/<see cref="Offset"/>) are axis-agnostic: callers pass the horizontal cell/gap/
/// padding to get <c>W(k)</c> and the vertical ones to get <c>H(R)</c> — there is exactly one source of
/// truth for both axes.
/// </remarks>
public static class AdaptiveGridLayout
{
    /// <summary>
    /// Default minimum gap between adjacent cells (<c>pmin</c>), <c>p = 4</c> (polishing/ui-1
    /// horizontal-resize spec). Each cell already carries <see cref="GridMetrics.ItemMargin"/> (6px) of
    /// its own chrome on every side (ADR-015), so this is 4px of gap ON TOP of that chrome, not the
    /// total seam: the visible seam between two icons in a non-stretched (incomplete) row grows from
    /// the old ~12px (chrome only, when this was <c>0</c>) to ~16px (chrome + <c>pmin</c>) on BOTH axes
    /// — <see cref="Controls.AdaptiveIconGridPanel.MinGap"/> feeds this same value into horizontal
    /// (within-row) AND vertical (between-row) layout (see its doc comment: "the same value is used for
    /// both axes"). This is a deliberate visual change, not a regression.
    /// <para>
    /// Consequence for <see cref="SnapExtent"/>/resize-snap (<c>NativePanelResizeController</c>,
    /// <c>SnapWidthToGrid</c>): see the note on <see cref="DefaultEdgePaddingRatio"/> — snap points for
    /// 2+ columns/rows are no longer the same values they were when this constant was <c>0</c>.
    /// </para>
    /// </summary>
    public const double DefaultMinGap = 4d;

    /// <summary>
    /// Default edge-padding ratio for the HORIZONTAL axis (<c>m = x * ratio</c>). <c>0.5</c> is the
    /// spec's <c>m = x/2</c> rule.
    /// <para>
    /// Historical note: while <see cref="DefaultMinGap"/> was <c>0</c>, this ratio kept every valid
    /// <see cref="Extent"/> a multiple of the cell size (<c>W(k) = x*(k+1)</c>) for ALL column/row
    /// counts, so panel widths/heights saved before the adaptive-icon-grid feature remained valid snap
    /// points. That is no longer true now that <see cref="DefaultMinGap"/> = 4: the gap term in
    /// <see cref="Extent"/> is multiplied by <c>(count - 1)</c>, so it drops out only for a single cell
    /// (<c>count = 1</c>) — <c>Extent(1, cell, gap, cell*0.5) == 2*cell</c> for ANY <c>gap</c>, which is
    /// exactly what <see cref="GridMetrics.MinPanelWidth(double)"/> relies on. For <c>count &gt;= 2</c> the snapped
    /// extent now includes <c>(count-1)*4</c> pixels of gap that a <c>gap = 0</c> extent didn't have, so
    /// a panel with 2+ columns/rows will land on a NEW width/height the first time it re-snaps after
    /// this change — this is an expected, intentional side effect of increasing <c>pmin</c>, not a
    /// compatibility bug.
    /// </para>
    /// <para>
    /// VERTICAL axis note: prior to the "убрать нижний и верхний отступы" fix (bug report: a
    /// single-row Grid-mode panel showed a spurious vertical scrollbar because
    /// <see cref="Extent"/>(1, rowHeight, gap, rowHeight*0.5) == 2*rowHeight — a full extra row's worth
    /// of blank space above/below one visible row), this same ratio also drove the vertical axis
    /// (<c>m_v = x_v * ratio</c>). It no longer does: the vertical axis now uses the separate, smaller
    /// <see cref="DefaultVerticalEdgePaddingRatio"/> (<see cref="Controls.AdaptiveIconGridPanel"/>'s
    /// <c>VerticalEdgePaddingRatio</c> DP). This constant is HORIZONTAL-only from that point on.
    /// </para>
    /// </summary>
    public const double DefaultEdgePaddingRatio = 0.5d;

    /// <summary>
    /// Default edge-padding ratio for the VERTICAL axis (<c>m_v = x_v * ratio</c>). <c>0</c> — no
    /// top/bottom padding at all.
    /// <para>
    /// Introduced to fix a reported bug: with the vertical axis sharing <see cref="DefaultEdgePaddingRatio"/>
    /// (<c>0.5</c>), a single row of icons (<c>count = 1</c>, so the gap term in <see cref="Extent"/>
    /// drops out) reported a content height of <c>2 * rowHeight</c> — half a row of blank padding above
    /// AND below the one visible row — which was large enough to trip the panel's
    /// <c>ScrollViewer.VerticalScrollBarVisibility="Auto"</c> into showing a scrollbar even though the
    /// single row plainly fit in the viewport. Horizontal edge padding is intentionally left untouched
    /// (unlike the vertical case, it serves a real Fences-parity visual-centering purpose for a row that
    /// isn't full-width) — see <see cref="DefaultEdgePaddingRatio"/>'s "VERTICAL axis note".
    /// </para>
    /// </summary>
    public const double DefaultVerticalEdgePaddingRatio = 0d;

    /// <summary>
    /// Tolerance (in pixels) subtracted from the shortfall before flooring in <see cref="RowCapacity"/>.
    /// The formula is exact only when the actual available width is precisely &gt;= the analytical
    /// <see cref="Extent"/> for <c>k</c> cells; in practice the real value is almost never exact:
    /// <c>IconSize</c> (and therefore the cell size) can be fractional (no tick-snapping guarantee), and
    /// WPF layout rounding / non-100% DPI scaling round the actual Win32 window width to a whole device
    /// pixel, which can shave up to ~1 device pixel off the mathematically expected value. Without a
    /// tolerance that sub-pixel shortfall makes <see cref="RowCapacity"/> silently drop an entire column
    /// even though the row would fit. <c>0.5</c> is intentionally less than a full pixel so it never masks
    /// a genuine one-column shortfall (see the boundary test at <c>Extent(k) - 1</c>).
    /// </summary>
    public const double LayoutTolerance = 0.5d;

    /// <summary>
    /// Computes the edge padding (<c>m</c> or <c>m_v</c>) for a cell of the given size.
    /// </summary>
    /// <param name="cell">The cell size along the axis (<c>x</c> or <c>x_v</c>).</param>
    /// <param name="ratio">The edge-padding ratio (see <see cref="DefaultEdgePaddingRatio"/>).</param>
    /// <returns><c>cell * ratio</c>.</returns>
    public static double EdgePadding(double cell, double ratio) => cell * ratio;

    /// <summary>
    /// Computes <c>k_max</c>, the maximum number of cells that fit across <paramref name="available"/>
    /// pixels without shrinking below <paramref name="cell"/> or the minimum gap.
    /// </summary>
    /// <param name="available">The available length along the axis (e.g. viewport width).</param>
    /// <param name="cell">The cell size along the axis. Values &lt;= 0 clamp the result to <c>1</c>
    /// (degenerate/unmeasured children must never divide by zero).</param>
    /// <param name="minGap">The minimum gap between adjacent cells (<c>pmin</c>).</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <returns>
    /// <c>max(1, floor((available - 2*edgePadding + minGap) / (cell + minGap) + tolerance))</c> — always
    /// at least <c>1</c>, so a container narrower than a single cell still lays out one (clipped) column
    /// instead of throwing or producing a zero-column, infinite-row layout. The <see cref="LayoutTolerance"/>
    /// nudge absorbs sub-pixel shortfall from fractional cell sizes and DPI/layout rounding without
    /// masking a genuine one-column shortfall — see <see cref="LayoutTolerance"/> for the rationale, or
    /// <c>NaN</c>/<c>infinity</c> inputs, which flow through <see cref="Math.Floor(double)"/> and
    /// <see cref="Math.Max(int, int)"/> to the safe <c>1</c> floor.
    /// </returns>
    public static int RowCapacity(double available, double cell, double minGap, double edgePadding)
    {
        var denominator = cell + minGap;
        if (cell <= 0d || denominator <= 0d) return 1;

        var raw = (available - (2d * edgePadding) + minGap) / denominator;
        if (double.IsNaN(raw)) return 1;

        var floor = (int)Math.Floor(raw + (LayoutTolerance / denominator));
        return Math.Max(1, floor);
    }

    /// <summary>
    /// Computes the number of rows needed to lay out <paramref name="itemCount"/> items with
    /// <paramref name="rowCapacity"/> items per full row (greedy wrap: every row is full except
    /// possibly the last).
    /// </summary>
    /// <param name="itemCount">The total number of items. <c>0</c> yields <c>0</c> rows — an empty
    /// panel contributes no edge padding and needs no scrollbar (plan §8.1).</param>
    /// <param name="rowCapacity">The maximum items per row (<see cref="RowCapacity"/>'s <c>k_max</c>).
    /// Values &lt;= 0 are treated as <c>1</c> defensively.</param>
    /// <returns><c>ceil(itemCount / rowCapacity)</c>, or <c>0</c> when <paramref name="itemCount"/> is <c>0</c>.</returns>
    public static int RowCount(int itemCount, int rowCapacity)
    {
        if (itemCount <= 0) return 0;

        var capacity = rowCapacity <= 0 ? 1 : rowCapacity;
        return (int)Math.Ceiling(itemCount / (double)capacity);
    }

    /// <summary>
    /// Computes how many items land in a given row under the greedy wrap rule (all rows full except
    /// the last, which holds the remainder).
    /// </summary>
    /// <param name="itemCount">The total number of items.</param>
    /// <param name="rowCapacity">The maximum items per row. Values &lt;= 0 are treated as <c>1</c>.</param>
    /// <param name="rowIndex">The zero-based row index. Out-of-range indices (negative, or beyond the
    /// last row) return <c>0</c>.</param>
    /// <returns>The number of items in row <paramref name="rowIndex"/>.</returns>
    public static int ItemsInRow(int itemCount, int rowCapacity, int rowIndex)
    {
        var capacity = rowCapacity <= 0 ? 1 : rowCapacity;
        var rows = RowCount(itemCount, capacity);
        if (rowIndex < 0 || rowIndex >= rows) return 0;

        return rowIndex == rows - 1 ? itemCount - (capacity * (rows - 1)) : capacity;
    }

    /// <summary>
    /// Computes the actual gap between adjacent cells within one row: <paramref name="minGap"/> for any
    /// row that is not completely full (<paramref name="itemsInRow"/> &lt; <paramref name="rowCapacity"/>)
    /// or has a single item (nothing to space out), and the stretched gap that consumes the row's
    /// leftover space for a completely full row with more than one item.
    /// </summary>
    /// <param name="itemsInRow">The number of items actually placed in this row (<see cref="ItemsInRow"/>).</param>
    /// <param name="rowCapacity">The row's maximum capacity (<see cref="RowCapacity"/>'s <c>k_max</c>).</param>
    /// <param name="available">The available length along the axis.</param>
    /// <param name="cell">The cell size along the axis.</param>
    /// <param name="minGap">The minimum gap between adjacent cells (<c>pmin</c>).</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <returns>
    /// <paramref name="minGap"/> when <paramref name="itemsInRow"/> &lt; <paramref name="rowCapacity"/>
    /// or <paramref name="itemsInRow"/> &lt;= 1; otherwise
    /// <c>max(minGap, (available - 2*edgePadding - cell*itemsInRow) / (itemsInRow - 1))</c> — the
    /// leftover row space stretched evenly into the gaps, never below <paramref name="minGap"/>.
    /// </returns>
    public static double RowGap(int itemsInRow, int rowCapacity, double available, double cell, double minGap, double edgePadding)
    {
        if (itemsInRow <= 1 || itemsInRow < rowCapacity) return minGap;

        var leftover = available - (2d * edgePadding) - (cell * itemsInRow);
        var stretched = leftover / (itemsInRow - 1);
        return Math.Max(minGap, stretched);
    }

    /// <summary>
    /// Policy-aware overload (ADR-029 Решение 1/Решение 3 — the "single new switch-point" the ADR's
    /// "Инварианты/API" table calls for): <see cref="IconLayoutPolicy.FixedGap"/> returns
    /// <paramref name="minGap"/> UNCONDITIONALLY, even for a full row with more than one item — i.e. it
    /// is exactly today's "incomplete row" branch of the six-argument overload above, applied to every
    /// row. <see cref="IconLayoutPolicy.LegacyStretch"/> delegates to that same six-argument overload
    /// with NO CHANGE whatsoever, so every existing caller/test that predates <see cref="IconLayoutPolicy"/>
    /// (and every caller that never passes one) keeps observing bit-for-bit ADR-018 stretch-gap
    /// behavior. This is the ONLY point in <see cref="AdaptiveGridLayout"/> where
    /// <see cref="IconLayoutPolicy"/> is allowed to influence the pure math — <see cref="RowCapacity"/>,
    /// <see cref="RowCount"/>, <see cref="ItemsInRow"/>, <see cref="Offset"/>, <see cref="Extent"/>,
    /// <see cref="CountForExtent"/>, and <see cref="SnapExtent"/> are all policy-agnostic by design
    /// (ADR-029 Решение 3: no second, competing "gap" concept is introduced — the policy only chooses
    /// which of the two ALREADY-EXISTING <see cref="RowGap(int, int, double, double, double, double)"/>
    /// branches applies to a full row).
    /// </summary>
    /// <param name="itemsInRow">The number of items actually placed in this row (<see cref="ItemsInRow"/>).</param>
    /// <param name="rowCapacity">The row's maximum capacity (<see cref="RowCapacity"/>'s <c>k_max</c>).</param>
    /// <param name="available">The available length along the axis.</param>
    /// <param name="cell">The cell size along the axis.</param>
    /// <param name="minGap">The minimum gap between adjacent cells (<c>pmin</c>).</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <param name="policy">Which of the two ADR-029 gap-distribution policies to apply.</param>
    /// <returns>
    /// <paramref name="minGap"/> unconditionally when <paramref name="policy"/> is
    /// <see cref="IconLayoutPolicy.FixedGap"/>; otherwise exactly what the six-argument
    /// <see cref="RowGap(int, int, double, double, double, double)"/> overload returns.
    /// </returns>
    public static double RowGap(int itemsInRow, int rowCapacity, double available, double cell, double minGap, double edgePadding, IconLayoutPolicy policy)
        => policy == IconLayoutPolicy.FixedGap
            ? minGap
            : RowGap(itemsInRow, rowCapacity, available, cell, minGap, edgePadding);

    /// <summary>
    /// Computes the offset of the <paramref name="indexInLine"/>-th cell along an axis. Axis-agnostic:
    /// pass horizontal values for column offsets, vertical values for row offsets.
    /// </summary>
    /// <param name="indexInLine">The zero-based position within the row (horizontal) or the zero-based
    /// row index (vertical).</param>
    /// <param name="cell">The cell size along the axis.</param>
    /// <param name="gap">The gap to use between cells (<see cref="RowGap(int, int, double, double, double, double)"/> for horizontal,
    /// the minimum gap — never stretched — for vertical; see plan §5).</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <returns><c>edgePadding + indexInLine * (cell + gap)</c>.</returns>
    public static double Offset(int indexInLine, double cell, double gap, double edgePadding)
        => edgePadding + (indexInLine * (cell + gap));

    /// <summary>
    /// Computes the total extent (length) of a line of <paramref name="count"/> cells along an axis.
    /// This is both <c>W(k)</c> (horizontal, count = column count) and <c>H(R)</c> (vertical, count =
    /// row count) — the same formula serves both, per plan §5 ("одна формула для W(k) и H(R)").
    /// </summary>
    /// <param name="count">The number of cells. <c>&lt;= 0</c> yields <c>0</c> (an empty panel occupies
    /// no space and contributes no edge padding — plan §8.1).</param>
    /// <param name="cell">The cell size along the axis.</param>
    /// <param name="gap">The gap between cells along the axis.</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <returns><c>2*edgePadding + count*cell + (count-1)*gap</c>, or <c>0</c> when <paramref name="count"/> &lt;= 0.</returns>
    public static double Extent(int count, double cell, double gap, double edgePadding)
    {
        if (count <= 0) return 0d;

        return (2d * edgePadding) + (count * cell) + ((count - 1) * gap);
    }

    /// <summary>
    /// Inverse of <see cref="Extent"/>: the integer cell count whose <see cref="Extent"/> is nearest
    /// <paramref name="extent"/>.
    /// </summary>
    /// <param name="extent">The raw (unsnapped) length along the axis.</param>
    /// <param name="cell">The cell size along the axis.</param>
    /// <param name="gap">The gap between cells along the axis.</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <returns>The nearest integer count &gt;= 1 (never <c>0</c> or negative — there is always at
    /// least one cell's worth of extent to snap to). Exact half-way values round <b>away from zero</b>
    /// (<see cref="MidpointRounding.AwayFromZero"/>) — deliberately, and NOT the banker's-rounding
    /// (round-to-even, <see cref="Math.Round(double)"/> default) used by <see cref="GridSnap.SnapAxis"/>.
    /// The two helpers snap different things (this one an
    /// abstract cell count, <c>GridSnap</c> a physical panel dimension) and there is no requirement that
    /// they agree on midpoint behavior; away-from-zero is simply the more predictable choice here. Do NOT
    /// "fix" this divergence to match <c>GridSnap</c> without re-reading this note first.
    /// </returns>
    public static int CountForExtent(double extent, double cell, double gap, double edgePadding)
    {
        var denominator = cell + gap;
        if (denominator <= 0d) return 1;

        var raw = (extent + gap - (2d * edgePadding)) / denominator;
        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Max(1, rounded);
    }

    /// <summary>
    /// Snaps a raw length along an axis to the nearest <see cref="Extent"/> of a whole number of cells
    /// — used to quantise panel width/height to whole columns/rows (plan §4/§5). Equivalent to
    /// <c>Extent(CountForExtent(extent, cell, gap, edgePadding), cell, gap, edgePadding)</c>.
    /// </summary>
    /// <param name="extent">The raw (unsnapped) length along the axis.</param>
    /// <param name="cell">The cell size along the axis.</param>
    /// <param name="gap">The gap between cells along the axis.</param>
    /// <param name="edgePadding">The edge padding (<c>m</c>).</param>
    /// <returns>The snapped length — always the <see cref="Extent"/> of at least one cell.</returns>
    public static double SnapExtent(double extent, double cell, double gap, double edgePadding)
        => Extent(CountForExtent(extent, cell, gap, edgePadding), cell, gap, edgePadding);
}
