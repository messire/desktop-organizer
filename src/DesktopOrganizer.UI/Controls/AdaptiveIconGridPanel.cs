using System.Windows;
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
/// space always goes into gap width instead. <c>ListReorderGeometry.ComputeInsertionGap</c> compares the
/// cursor against <c>container.ActualWidth / 2</c>; a stretched container would shift that midpoint and
/// desync the drag-reorder "insert before/after" decision.</item>
/// <item>Positioning is done purely via the coordinates passed to <see cref="UIElement.Arrange"/> — no
/// <c>RenderTransform</c>/<c>TranslateTransform</c> — so hit-testing and <c>TranslatePoint</c> calls in
/// <c>ChildWindow.xaml.cs</c> see the same coordinates this panel used to lay items out.</item>
/// <item>Cells never overlap (guaranteed by <c>MinGap &gt;= 0</c>), so
/// <c>VisualTreeHelper.HitTest</c> + <c>FindAncestorOrSelf&lt;ListBoxItem&gt;</c> always resolves to a
/// single, unambiguous container.</item>
/// </list>
/// No <see cref="System.Windows.Controls.Primitives.IScrollInfo"/> is implemented (matches the <c>WrapPanel</c> it replaces — physical,
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
    /// (<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c>) and this panel always agree on the same value.
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
    /// HORIZONTAL edge-padding ratio (<c>m = x * ratio</c>). Default is
    /// <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/> (0.5 — half a cell of left/right padding,
    /// serving a real Fences-parity visual-centering purpose for rows narrower than the viewport). See
    /// <see cref="MinGapProperty"/>'s doc comment for why this is left unset in XAML.
    /// <para>
    /// Vertical (top/bottom) edge padding is a SEPARATE property, <see cref="VerticalEdgePaddingRatio"/>
    /// — the two axes used to share this one ratio, but that made a single row of icons report a content
    /// height of <c>2 * rowHeight</c> (half a row of blank space above AND below the one visible row),
    /// which was large enough to trigger a spurious vertical scrollbar even though the row plainly fit.
    /// See <see cref="AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio"/> for the fix.
    /// </para>
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
    /// VERTICAL edge-padding ratio (<c>m_v = x_v * ratio</c>), independent of
    /// <see cref="EdgePaddingRatio"/>. Default is <see cref="AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio"/>
    /// (<c>0</c> — no top/bottom padding). See <see cref="EdgePaddingRatioProperty"/>'s doc comment for
    /// why this is a separate DP from the horizontal ratio, and <see cref="MinGapProperty"/>'s doc
    /// comment for why this is left unset in XAML (the default lives solely in
    /// <see cref="AdaptiveGridLayout"/> so <see cref="Behaviors.NativePanelResizeController"/>'s height-snap
    /// stays in agreement with this panel's actual rendered height).
    /// </summary>
    public static readonly DependencyProperty VerticalEdgePaddingRatioProperty = DependencyProperty.Register(
        nameof(VerticalEdgePaddingRatio),
        typeof(double),
        typeof(AdaptiveIconGridPanel),
        new FrameworkPropertyMetadata(
            AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsNonNegativeDouble);

    /// <summary>
    /// Which of the two ADR-029 gap-distribution policies this panel uses. Default is
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> — the value ADR-018's original stretch-gap math
    /// already produced before this property existed — so a panel with no explicit binding renders
    /// byte-for-byte as it always has. As of the M-2 fix-cycle amendment (adr/ADR-029-icon-layout-policy.md
    /// Решение 4) BOTH overrides below read it, not just <see cref="ArrangeOverride"/>: HORIZONTAL row
    /// gaps (<see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double, IconLayoutPolicy)"/>,
    /// arrange-only) AND <see cref="AdaptiveGridLayout.RowCapacity"/>'s available-width term (measure
    /// AND arrange — <see cref="GridMetrics.ReservedVerticalScrollBarWidth"/> reserves extra width for
    /// <see cref="IconLayoutPolicy.FixedGap"/>, resolved against <see cref="CapacityWidth"/> rather than
    /// the raw measure/arrange width (fix-cycle 2 CRITICAL-1 — see <see cref="CapacityWidthProperty"/>'s
    /// doc comment) so the vertical scrollbar's appearance/disappearance never changes the column count)
    /// now depend on this property's value — see
    /// <see cref="MeasureOverride"/>/<see cref="ArrangeOverride"/>'s own remarks. The VERTICAL axis's
    /// GAP (as opposed to row capacity) is unaffected by this property under either policy: vertical
    /// gaps are ALWAYS <see cref="MinGap"/> (ADR-018 D-3, untouched by ADR-029) —
    /// <see cref="ArrangeOverride"/>'s vertical <see cref="AdaptiveGridLayout.Offset"/> call never goes
    /// through <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double)"/> at all, policy-aware or not.
    /// <para>
    /// Deliberately left unset in <c>GridViewTemplate</c>'s <c>ItemsPanelTemplate</c> literal markup —
    /// same reasoning as <see cref="MinGapProperty"/>'s doc comment: the real value always arrives
    /// through an explicit <c>Binding</c> to the owning panel's model
    /// (<c>ChildWindowModel.IconLayoutPolicy</c>) in <c>Templates.xaml</c>, not a hand-authored default
    /// here.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty LayoutPolicyProperty = DependencyProperty.Register(
        nameof(LayoutPolicy),
        typeof(IconLayoutPolicy),
        typeof(AdaptiveIconGridPanel),
        new FrameworkPropertyMetadata(
            IconLayoutPolicy.LegacyStretch,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsDefinedIconLayoutPolicy);

    /// <summary>
    /// Stable, scrollbar-visibility-INDEPENDENT width fed to <see cref="AdaptiveGridLayout.RowCapacity"/>
    /// for <see cref="IconLayoutPolicy.FixedGap"/> (fix-cycle 2 CRITICAL-1, adr/ADR-029-icon-layout-policy.md
    /// "Fix-цикл P05"). Default is <see cref="double.NaN"/> ("unbound" — see
    /// <see cref="ResolveAvailableForCapacity"/> for the fallback this triggers).
    /// <para>
    /// <b>The bug this fixes.</b> The M-2 fix-cycle 1 amendment subtracted
    /// <see cref="GridMetrics.ReservedVerticalScrollBarWidth"/> directly from
    /// <c>availableSize</c>/<c>finalSize</c>.Width — but <c>GridViewTemplate</c>'s
    /// <c>ScrollViewer.VerticalScrollBarVisibility="Auto"</c> ALREADY hands this panel a narrower width
    /// once its own vertical scrollbar actually becomes visible. The reservation was therefore applied
    /// TWICE in that case (once by the real <see cref="ScrollViewer"/>, once by this panel), so the
    /// column count still flipped exactly at the scrollbar-appearance threshold — the very thing the
    /// fix-cycle 1 amendment was meant to eliminate.
    /// </para>
    /// <para>
    /// <b>The fix.</b> <c>Templates.xaml</c> binds this property to the SAME <see cref="ScrollViewer"/>'s
    /// own <c>ActualWidth</c> — a value set by the <see cref="ScrollViewer"/>'s PARENT layout, which does
    /// NOT shrink when the <see cref="ScrollViewer"/>'s own vertical scrollbar appears (unlike
    /// <c>availableSize</c>/<c>finalSize</c>, which are exactly that shrunk viewport width). The
    /// reservation is now subtracted exactly once, from this stable value, so <see cref="AdaptiveGridLayout.RowCapacity"/>
    /// stops depending on whether a scrollbar happens to be visible on this particular measure/arrange
    /// pass. See <see cref="ResolveAvailableForCapacity"/> for the exact formula, including why
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> is deliberately left reading the raw, fluctuating
    /// width (its own accepted ADR-018 M-2 trade-off, variant (c), unaffected by this fix).
    /// </para>
    /// <para>
    /// Deliberately left unset in <c>GridViewTemplate</c>'s <c>ItemsPanelTemplate</c> literal markup —
    /// same reasoning as <see cref="MinGapProperty"/>'s doc comment: the real value always arrives
    /// through an explicit <c>Binding</c> (<c>RelativeSource AncestorType=ScrollViewer</c>) in
    /// <c>Templates.xaml</c>, not a hand-authored default here. A panel constructed directly, outside
    /// that template (e.g. most of this class's own unit tests), simply keeps the <see cref="double.NaN"/>
    /// default — see <see cref="ResolveAvailableForCapacity"/>'s fallback branch.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty CapacityWidthProperty = DependencyProperty.Register(
        nameof(CapacityWidth),
        typeof(double),
        typeof(AdaptiveIconGridPanel),
        new FrameworkPropertyMetadata(
            double.NaN,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsNaNOrNonNegativeDouble);

    /// <summary>Gets or sets the minimum gap between adjacent cells (<c>pmin</c>/<c>pmin_v</c>).</summary>
    public double MinGap
    {
        get => (double)GetValue(MinGapProperty);
        set => SetValue(MinGapProperty, value);
    }

    /// <summary>Gets or sets the HORIZONTAL edge-padding ratio (<c>m = x * ratio</c>).</summary>
    public double EdgePaddingRatio
    {
        get => (double)GetValue(EdgePaddingRatioProperty);
        set => SetValue(EdgePaddingRatioProperty, value);
    }

    /// <summary>Gets or sets the VERTICAL edge-padding ratio (<c>m_v = x_v * ratio</c>).</summary>
    public double VerticalEdgePaddingRatio
    {
        get => (double)GetValue(VerticalEdgePaddingRatioProperty);
        set => SetValue(VerticalEdgePaddingRatioProperty, value);
    }

    /// <summary>Gets or sets which ADR-029 gap-distribution policy this panel's horizontal axis uses.</summary>
    public IconLayoutPolicy LayoutPolicy
    {
        get => (IconLayoutPolicy)GetValue(LayoutPolicyProperty);
        set => SetValue(LayoutPolicyProperty, value);
    }

    /// <summary>Gets or sets the stable width fed to <see cref="AdaptiveGridLayout.RowCapacity"/>'s M-2
    /// scrollbar reservation. See <see cref="CapacityWidthProperty"/>'s doc comment.</summary>
    public double CapacityWidth
    {
        get => (double)GetValue(CapacityWidthProperty);
        set => SetValue(CapacityWidthProperty, value);
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
        var m = AdaptiveGridLayout.EdgePadding(cell, EdgePaddingRatio);
        var mv = AdaptiveGridLayout.EdgePadding(rowHeight, VerticalEdgePaddingRatio);

        // M-2 fix-cycle amendment (adr/ADR-029-icon-layout-policy.md Решение 4): FixedGap reserves the
        // vertical scrollbar's width UNCONDITIONALLY — independent of whether GridViewTemplate's
        // ScrollViewer.VerticalScrollBarVisibility="Auto" actually shows one right now — so RowCapacity
        // never flips as items are added/removed near the threshold where a scrollbar would appear.
        // LegacyStretch is unaffected (ReservedVerticalScrollBarWidth returns 0 for it). Fix-cycle 2
        // CRITICAL-1: the reservation is resolved via CapacityWidth (ResolveAvailableForCapacity), NOT
        // subtracted from availableSize directly — see that method's doc comment for why the earlier,
        // direct-subtraction version double-counted the scrollbar once a real one became visible.
        var reservedScrollBar = GridMetrics.ReservedVerticalScrollBarWidth(LayoutPolicy, SystemParameters.VerticalScrollBarWidth);

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
            var availableForCapacity = ResolveAvailableForCapacity(availableSize.Width, reservedScrollBar);
            rowCapacity = AdaptiveGridLayout.RowCapacity(availableForCapacity, cell, minGap, m);
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
        var m = AdaptiveGridLayout.EdgePadding(cell, EdgePaddingRatio);
        var mv = AdaptiveGridLayout.EdgePadding(rowHeight, VerticalEdgePaddingRatio);

        // M-2 fix-cycle amendment — see MeasureOverride's matching remark; RowCapacity must be computed
        // from the SAME scrollbar-reserved width here as in MeasureOverride, or ArrangeOverride could
        // legitimately compute a different rowCapacity than the size MeasureOverride already reported
        // (this method's own doc comment already documents that finalSize.Width and the measure pass's
        // availableSize.Width can legitimately differ — a real Auto scrollbar appearing between passes —
        // this reservation exists precisely so that FixedGap's rowCapacity does NOT also differ then).
        // Fix-cycle 2 CRITICAL-1: resolved via CapacityWidth (ResolveAvailableForCapacity), not a direct
        // subtraction from finalSize.Width — see that method's doc comment.
        var reservedScrollBar = GridMetrics.ReservedVerticalScrollBarWidth(LayoutPolicy, SystemParameters.VerticalScrollBarWidth);
        var availableForCapacity = ResolveAvailableForCapacity(finalSize.Width, reservedScrollBar);
        var rowCapacity = AdaptiveGridLayout.RowCapacity(availableForCapacity, cell, minGap, m);
        var rowCount = AdaptiveGridLayout.RowCount(children.Count, rowCapacity);

        var index = 0;
        for (var row = 0; row < rowCount; row++)
        {
            var itemsInRow = AdaptiveGridLayout.ItemsInRow(children.Count, rowCapacity, row);

            // Horizontal gaps stretch to fill a full row's leftover space under LegacyStretch, or stay
            // pinned at MinGap under FixedGap (ADR-029 Решение 1/Решение 3 — LayoutPolicy is the ONLY
            // switch-point between the two); vertical gaps never stretch under EITHER policy (plan §5,
            // "D-3": "вертикальные зазоры никогда не растягиваются") — always MinGap, policy-agnostic.
            var gap = AdaptiveGridLayout.RowGap(itemsInRow, rowCapacity, finalSize.Width, cell, minGap, m, LayoutPolicy);
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

    /// <summary>
    /// Shared <see cref="ValidateValueCallback"/> for <see cref="MinGapProperty"/>,
    /// <see cref="EdgePaddingRatioProperty"/>, and <see cref="VerticalEdgePaddingRatioProperty"/>: rejects
    /// negative or <c>NaN</c> values so the "cells never overlap" invariant documented in this class's
    /// remarks (<c>MinGap &gt;= 0</c>) is actually enforced rather than merely asserted in a comment.
    /// </summary>
    private static bool IsNonNegativeDouble(object value) => (double)value >= 0d && !double.IsNaN((double)value);

    /// <summary>
    /// <see cref="ValidateValueCallback"/> for <see cref="CapacityWidthProperty"/>: unlike
    /// <see cref="IsNonNegativeDouble"/>, <see cref="double.NaN"/> is explicitly ALLOWED here — it is this
    /// property's own "unbound" sentinel (see its doc comment), not an invalid input.
    /// </summary>
    private static bool IsNaNOrNonNegativeDouble(object value)
    {
        var d = (double)value;
        return double.IsNaN(d) || d >= 0d;
    }

    /// <summary>
    /// <see cref="ValidateValueCallback"/> for <see cref="LayoutPolicyProperty"/>: rejects any boxed
    /// <see cref="int"/>/enum value outside the two defined <see cref="IconLayoutPolicy"/> members, the
    /// same defense-in-depth already applied to the panel's <see langword="double"/> DPs above.
    /// </summary>
    private static bool IsDefinedIconLayoutPolicy(object value) => value is IconLayoutPolicy policy && Enum.IsDefined(policy);

    /// <summary>
    /// Resolves the width <see cref="AdaptiveGridLayout.RowCapacity"/> is computed against (fix-cycle 2
    /// CRITICAL-1 — see <see cref="CapacityWidthProperty"/>'s doc comment for the double-subtraction bug
    /// this replaces).
    /// </summary>
    /// <param name="rawWidth">
    /// The width WPF actually handed this pass (<c>availableSize</c>/<c>finalSize</c>.Width) — the SAME
    /// value <c>GridViewTemplate</c>'s <see cref="ScrollViewer"/> already narrows once its own Auto
    /// vertical scrollbar becomes visible.
    /// </param>
    /// <param name="reservedScrollBar">
    /// <see cref="GridMetrics.ReservedVerticalScrollBarWidth"/> for <see cref="LayoutPolicy"/> — <c>0</c>
    /// for <see cref="IconLayoutPolicy.LegacyStretch"/>, the real scrollbar width for
    /// <see cref="IconLayoutPolicy.FixedGap"/>.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    /// <item><description><paramref name="reservedScrollBar"/> == <c>0</c> (<see cref="IconLayoutPolicy.LegacyStretch"/>):
    /// always <paramref name="rawWidth"/>, unchanged — this policy's own accepted ADR-018 M-2 trade-off
    /// (variant (c)) keeps fluctuating with the real, possibly scrollbar-narrowed viewport exactly as it
    /// did before this fix.</description></item>
    /// <item><description><paramref name="reservedScrollBar"/> &gt; <c>0</c> AND <see cref="CapacityWidth"/>
    /// is bound (not <see cref="double.NaN"/>): <c>max(0, CapacityWidth - reservedScrollBar)</c> — the
    /// reservation applied exactly once, against the STABLE outer width, independent of whether a
    /// scrollbar happens to be visible on this particular pass.</description></item>
    /// <item><description><paramref name="reservedScrollBar"/> &gt; <c>0</c> but <see cref="CapacityWidth"/>
    /// is unbound (e.g. this panel constructed directly in a unit test, outside <c>GridViewTemplate</c>'s
    /// <see cref="ScrollViewer"/>): no stable width exists to reserve against, so this degrades
    /// gracefully to <paramref name="rawWidth"/> with NO reservation at all — the same formula
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> always uses — rather than risk double-subtracting a
    /// second time from an already-narrowed width.</description></item>
    /// </list>
    /// </returns>
    private double ResolveAvailableForCapacity(double rawWidth, double reservedScrollBar)
        => reservedScrollBar > 0d && !double.IsNaN(CapacityWidth)
            ? Math.Max(0d, CapacityWidth - reservedScrollBar)
            : rawWidth;
}
