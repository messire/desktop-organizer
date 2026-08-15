using System.Windows.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Geometry-layer replacement for <c>WrapPanel</c> in <c>GridViewTemplate</c>
/// (docs/PLANS/adaptive-icon-grid.md, "D-5"): lays out its <see cref="Panel.InternalChildren"/> in a
/// uniform-cell grid, distributing any leftover row space evenly into the gaps between cells instead of
/// stretching the cells themselves. All the actual layout math lives in <see cref="AdaptiveGridLayout"/>
/// (pure, WPF-free, unit-testable); this class only measures children, reads their
/// <see cref="UIElement.DesiredSize"/> as the uniform cell size, and calls
/// <see cref="UIElement.Arrange"/> with the resulting rectangles.
/// </summary>
/// <remarks>
/// Hard invariants (plan §7, "D-5") that keep the existing hit-testing/drag-reorder code in
/// <c>ChildWindow.xaml.cs</c> working unmodified:
/// <list type="bullet">
/// <item>Only reads/writes via <see cref="Panel.InternalChildren"/> — never adds, removes, or
/// reorders children. Layout order is always <c>InternalChildren</c> order, so existing sorting
/// (<c>PanelItemSortComparer</c>, drag-reorder) is untouched.</item>
/// <item>Every child is arranged at the uniform cell size (the max of all children's
/// <see cref="UIElement.DesiredSize"/>), never stretched to fill the row's leftover space — the leftover
/// space always goes into gap width instead. <c>ChildWindow.ComputeInsertionGap</c> compares the
/// cursor against <c>container.ActualWidth / 2</c>; a stretched container would shift that midpoint and
/// desync the drag-reorder "insert before/after" decision.</item>
/// <item>Positioning is done purely via the coordinates passed to <see cref="UIElement.Arrange"/> — no
/// <c>RenderTransform</c>/<c>TranslateTransform</c> — so hit-testing and <c>TranslatePoint</c> calls in
/// <c>ChildWindow.xaml.cs</c> see the same coordinates this panel used to lay items out.</item>
/// <item>Cells never overlap (guaranteed by <c>MinGap &gt;= 0</c>), so
/// <c>VisualTreeHelper.HitTest</c> + <c>FindAncestorOrSelf&lt;ListBoxItem&gt;</c> always resolves to a
/// single, unambiguous container.</item>
/// </list>
/// No <see cref="IScrollInfo"/> is implemented (matches the <c>WrapPanel</c> it replaces — physical,
/// pixel-based scrolling via the <c>ListBox</c>'s own <see cref="ScrollViewer"/>, see
/// <c>GridViewTemplate</c> in Templates.xaml) and no virtualization is introduced (not a regression —
/// <c>WrapPanel</c> didn't virtualize either).
/// </remarks>
public sealed class AdaptiveIconGridPanel : Panel
{
    /// <summary>
    /// Minimum gap between adjacent cells (<c>pmin</c>/<c>pmin_v</c> — the same value is used for both
    /// axes; see <see cref="AdaptiveGridLayout"/>'s remarks for why a single DP is enough). Default is
    /// <see cref="AdaptiveGridLayout.DefaultMinGap"/>. Deliberately left unset in
    /// <c>GridViewTemplate</c>'s <c>ItemsPanelTemplate</c> — the default lives solely in
    /// <see cref="AdaptiveGridLayout"/> so <c>ChildWindow.xaml.cs</c>'s resize-snap code
    /// (<c>Resize_DragDelta</c>/<c>SnapWidthToGrid</c>) and this panel always agree on the same value.
    /// If this is ever overridden in XAML, update the resize-snap code's constant too (plan §4, "R5").
    /// </summary>
    public static readonly DependencyProperty MinGapProperty = DependencyProperty.Register(
        nameof(MinGap),
        typeof(double),
        typeof(AdaptiveIconGridPanel),
        new FrameworkPropertyMetadata(
            AdaptiveGridLayout.DefaultMinGap,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsNonNegativeDouble);

    /// <summary>
    /// Edge-padding ratio (<c>m = x * ratio</c>, <c>m_v = x_v * ratio</c>). Default is
    /// <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/>. See <see cref="MinGapProperty"/>'s doc
    /// comment for why this is left unset in XAML.
    /// </summary>
    public static readonly DependencyProperty EdgePaddingRatioProperty = DependencyProperty.Register(
        nameof(EdgePaddingRatio),
        typeof(double),
        typeof(AdaptiveIconGridPanel),
        new FrameworkPropertyMetadata(
            AdaptiveGridLayout.DefaultEdgePaddingRatio,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsNonNegativeDouble);

    /// <summary>
    /// Shared <see cref="ValidateValueCallback"/> for <see cref="MinGapProperty"/> and
    /// <see cref="EdgePaddingRatioProperty"/>: rejects negative or <c>NaN</c> values so the "cells never
    /// overlap" invariant documented in this class's remarks (<c>MinGap &gt;= 0</c>) is actually enforced
    /// rather than merely asserted in a comment.
    /// </summary>
    private static bool IsNonNegativeDouble(object value) => (double)value >= 0d && !double.IsNaN((double)value);

    /// <summary>Gets or sets the minimum gap between adjacent cells (<c>pmin</c>/<c>pmin_v</c>).</summary>
    public double MinGap
    {
        get => (double)GetValue(MinGapProperty);
        set => SetValue(MinGapProperty, value);
    }

    /// <summary>Gets or sets the edge-padding ratio (<c>m = x * ratio</c>).</summary>
    public double EdgePaddingRatio
    {
        get => (double)GetValue(EdgePaddingRatioProperty);
        set => SetValue(EdgePaddingRatioProperty, value);
    }

    /// <summary>
    /// Measures every child at its natural (unconstrained) size to determine the uniform cell size,
    /// then computes the panel's desired size. Deliberately never reads
    /// <paramref name="availableSize"/>.Height — the panel lives inside a vertically-scrolling
    /// <see cref="ScrollViewer"/> (<c>ScrollViewer.VerticalScrollBarVisibility="Auto"</c> in
    /// GridViewTemplate), which measures with an infinite height; the panel's own
    /// <see cref="Size.Height"/> here becomes that <see cref="ScrollViewer"/>'s
    /// <c>ExtentHeight</c>, so the vertical scrollbar appears exactly when the content doesn't fit
    /// (plan §6, "D-4").
    /// </summary>
    /// <param name="availableSize">
    /// The available size. <see cref="Size.Width"/> must be finite for <c>k_max</c> (the column count)
    /// to be well-defined — guaranteed by <c>GridViewTemplate</c>'s
    /// <c>ScrollViewer.HorizontalScrollBarVisibility="Disabled"</c> (plan §6, "D-4"/"R7"). The fallback
    /// branch below handles the case where a future caller measures with infinite width anyway (e.g. a
    /// different host container) by laying everything out in a single row instead of dividing by
    /// infinity.
    /// </param>
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;

        // Empty panel: no children means no cell size to derive from, so desired size is truly zero
        // (unlike ArrangeOverride below, which must still fill/return the parent-allotted finalSize).
        if (children.Count == 0) return new Size(0d, 0d);

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double cell = 0d;
        double rowHeight = 0d;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(infinite);
            if (child.DesiredSize.Width > cell) cell = child.DesiredSize.Width;
            if (child.DesiredSize.Height > rowHeight) rowHeight = child.DesiredSize.Height;
        }

        var minGap = MinGap;
        var ratio = EdgePaddingRatio;
        var m = AdaptiveGridLayout.EdgePadding(cell, ratio);
        var mv = AdaptiveGridLayout.EdgePadding(rowHeight, ratio);

        double width;
        int rowCapacity;
        if (double.IsPositiveInfinity(availableSize.Width))
        {
            // Fallback (plan §6 "R7"): no finite width to derive k_max from — lay out a single row.
            rowCapacity = children.Count;
            width = AdaptiveGridLayout.Extent(Math.Min(children.Count, rowCapacity), cell, minGap, m);
        }
        else
        {
            rowCapacity = AdaptiveGridLayout.RowCapacity(availableSize.Width, cell, minGap, m);
            width = availableSize.Width;
        }

        var rowCount = AdaptiveGridLayout.RowCount(children.Count, rowCapacity);
        var height = AdaptiveGridLayout.Extent(rowCount, rowHeight, minGap, mv);

        return new Size(width, height);
    }

    /// <summary>
    /// Arranges every child into its row/column slot. Recomputes <c>k_max</c> from
    /// <paramref name="finalSize"/>.Width rather than reusing a value cached from
    /// <see cref="MeasureOverride"/>, because arrange width can legitimately differ from the measure
    /// pass's available width (e.g. a vertical scrollbar appearing/disappearing changes the viewport
    /// width between the two passes).
    /// </summary>
    /// <param name="finalSize">The final size allotted to the panel by its parent.</param>
    /// <returns>
    /// <paramref name="finalSize"/> with <see cref="Size.Height"/> raised to at least the content's
    /// full vertical extent (<c>H</c>) — never less, so a viewport taller than the content isn't
    /// reported as shorter than it actually rendered.
    /// </returns>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;

        // Empty panel: nothing to arrange, but (unlike MeasureOverride above) must still return the
        // size the parent already allotted us, not zero — arrange results aren't a negotiation.
        if (children.Count == 0) return finalSize;

        double cell = 0d;
        double rowHeight = 0d;
        for (var i = 0; i < children.Count; i++)
        {
            var desired = children[i].DesiredSize;
            if (desired.Width > cell) cell = desired.Width;
            if (desired.Height > rowHeight) rowHeight = desired.Height;
        }

        var minGap = MinGap;
        var ratio = EdgePaddingRatio;
        var m = AdaptiveGridLayout.EdgePadding(cell, ratio);
        var mv = AdaptiveGridLayout.EdgePadding(rowHeight, ratio);

        var rowCapacity = AdaptiveGridLayout.RowCapacity(finalSize.Width, cell, minGap, m);
        var rowCount = AdaptiveGridLayout.RowCount(children.Count, rowCapacity);

        var index = 0;
        for (var row = 0; row < rowCount; row++)
        {
            var itemsInRow = AdaptiveGridLayout.ItemsInRow(children.Count, rowCapacity, row);

            // Horizontal gaps stretch to fill a full row's leftover space; vertical gaps never stretch
            // (plan §5, "D-3": "вертикальные зазоры никогда не растягиваются") — always MinGap.
            var gap = AdaptiveGridLayout.RowGap(itemsInRow, rowCapacity, finalSize.Width, cell, minGap, m);
            var y = AdaptiveGridLayout.Offset(row, rowHeight, minGap, mv);

            for (var column = 0; column < itemsInRow; column++)
            {
                var x = AdaptiveGridLayout.Offset(column, cell, gap, m);
                children[index].Arrange(new Rect(x, y, cell, rowHeight));
                index++;
            }
        }

        var height = AdaptiveGridLayout.Extent(rowCount, rowHeight, minGap, mv);
        return new Size(finalSize.Width, Math.Max(finalSize.Height, height));
    }
}
