using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;

using static DesktopOrganizer.UI.Views.VisualTreeWalker;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Pure/near-pure geometry helpers for the panel drag-to-reorder insertion-gap math, extracted from
/// <c>ChildWindow.xaml.cs</c> (REFACTORING_PLAN.md §5.3, ADR-014 Block C4). Kept separate from
/// <see cref="ListReorderGestureController{TItem}"/> because <see cref="ComputeInsertionGap"/> operates
/// on <see cref="PanelViewMode"/>, a file-panel-specific concept, whereas the gesture controller is fully
/// generic over the reorderable item type.
/// </summary>
public static class ListReorderGeometry
{
    /// <summary>
    /// Pure, testable core of the drag-to-reorder insertion-gap math (ADR-014 Block C4 — "wrap in a
    /// form that accepts primitives, not live UI objects, so it's unit-testable without a real
    /// <see cref="ListBox"/>/hit-test"). Compares the cursor position within the hit-tested container
    /// against its midpoint, along the axis that matches the current layout: horizontal (X) in
    /// <see cref="PanelViewMode.Grid"/>, vertical (Y) in <see cref="PanelViewMode.List"/> — the two
    /// view modes lay items out on different axes, so "before" the cursor means a different axis in
    /// each.
    /// </summary>
    /// <param name="containerIndex">
    /// The visible index of the <see cref="ListBoxItem"/> container the cursor is over, or
    /// <see langword="null"/> when the cursor is over empty space past the last item — always
    /// resolves to the end-of-list gap (<paramref name="itemCount"/>) regardless of the other
    /// arguments in that case.
    /// </param>
    /// <param name="cursorPosition">The cursor's position relative to the hit-tested container's own origin.</param>
    /// <param name="containerSize">The hit-tested container's size.</param>
    /// <param name="viewMode">The panel's current view mode, selecting the comparison axis.</param>
    /// <param name="itemCount">The total number of currently visible items — the end-of-list fallback gap.</param>
    /// <returns>The insertion gap index, in <c>[0, itemCount]</c>.</returns>
    public static int ComputeInsertionGap(
        int? containerIndex,
        Point cursorPosition,
        Size containerSize,
        PanelViewMode viewMode,
        int itemCount)
    {
        if (containerIndex is not { } index) return itemCount;

        var beforeMidpoint = viewMode == PanelViewMode.Grid
            ? cursorPosition.X < containerSize.Width / 2.0
            : cursorPosition.Y < containerSize.Height / 2.0;

        return beforeMidpoint ? index : index + 1;
    }

    /// <summary>
    /// Hit-tests <paramref name="listBox"/> at the drag position from <paramref name="e"/> and walks
    /// up to the nearest <see cref="ListBoxItem"/> ancestor (inclusive), or <see langword="null"/> when
    /// the cursor isn't over any item container AND isn't within any realized container's row either
    /// (empty space below/after the very last row). See the <see cref="Point"/> overload below (this is
    /// a thin wrapper over it) for the full contract, including the P05 fix-cycle same-row fallback.
    /// </summary>
    /// <param name="listBox">The panel's file-list <see cref="ListBox"/> (either view mode's <c>ItemsPanel</c>).</param>
    /// <param name="e">The drag event whose position (relative to <paramref name="listBox"/>) is hit-tested.</param>
    public static ListBoxItem? HitTestReorderContainer(ListBox listBox, DragEventArgs e)
        => HitTestReorderContainer(listBox, e.GetPosition(listBox));

    /// <summary>
    /// Point-based core of <see cref="HitTestReorderContainer(ListBox, DragEventArgs)"/> — split out so
    /// it is unit-testable without constructing a <see cref="DragEventArgs"/> (its constructor pipeline
    /// is not practically reproducible outside a real OS drag-drop operation).
    /// <para>
    /// <b>P05 fix-cycle finding (MINOR):</b> a direct <see cref="VisualTreeHelper.HitTest(Visual, Point)"/>
    /// at <paramref name="position"/> can miss even though the cursor is clearly still hovering over a
    /// row, not past the end of the list — specifically, <see cref="Controls.AdaptiveIconGridPanel"/>'s
    /// <see cref="Models.IconLayoutPolicy.FixedGap"/> policy deliberately leaves surplus, unpainted space
    /// to the right of ANY row (not just the last one, adr/ADR-029-icon-layout-policy.md Решение 1) —
    /// hovering there used to fall through to <see cref="ComputeInsertionGap"/>'s <c>containerIndex ==
    /// null -&gt; itemCount</c> branch, silently resolving "drop at the end of this row" to "drop at the
    /// end of the WHOLE LIST" whenever the row hovered over wasn't the last one. When the direct hit-test
    /// misses, this now falls back to the NEAREST realized container (by horizontal distance) among
    /// those whose own vertical extent contains <paramref name="position"/>.Y — i.e. the nearest item in
    /// the SAME row — so the caller's subsequent <c>e.GetPosition(container)</c>/
    /// <see cref="ComputeInsertionGap"/> pipeline sees a cursor position relative to THAT container and
    /// correctly resolves to "insert right after/before it", not "insert at the end of the list". A
    /// cursor genuinely below every row (no container's vertical extent contains it) still returns
    /// <see langword="null"/> — unchanged, correct "past the end of the list" behavior.
    /// </para>
    /// <para>
    /// <b>Fix-cycle 2 correction (MINOR-1):</b> the original fix-cycle 1 fallback always picked the
    /// RIGHT-MOST container in the row, regardless of which side of it the cursor actually was on. That
    /// correctly resolved the surplus-space-to-the-right case above, but broke the SYMMETRIC case: a
    /// cursor in the row's LEFT gutter (the <c>m</c> edge-padding margin before the first column, adr/
    /// ADR-029-icon-layout-policy.md's <c>m = x * ratio</c>) also misses the direct hit-test, and the
    /// old fallback resolved it to the row's LAST item (via <see cref="ComputeInsertionGap"/>'s
    /// midpoint-of-the-picked-container comparison, using a hugely negative relative X that still
    /// evaluates "before" that wrong container's midpoint — a plausible-looking but wrong insertion
    /// point, one row too far right) instead of the row's FIRST item. Picking the nearest container by
    /// horizontal distance (this method) resolves both gutters correctly: the left gutter's nearest
    /// container is the row's first item, the right surplus zone's nearest container is the row's last
    /// item — the same "always rightmost" answer as before, but now as a special case of "nearest",
    /// not a hard-coded direction.
    /// </para>
    /// </summary>
    /// <param name="listBox">The panel's file-list <see cref="ListBox"/> (either view mode's <c>ItemsPanel</c>).</param>
    /// <param name="position">The drag position, relative to <paramref name="listBox"/>'s own origin.</param>
    public static ListBoxItem? HitTestReorderContainer(ListBox listBox, Point position)
    {
        var hit = VisualTreeHelper.HitTest(listBox, position);
        if (hit is not null && FindAncestorOrSelf<ListBoxItem>(hit.VisualHit) is { } direct)
            return direct;

        return FindNearestContainerInSameRow(listBox, position);
    }

    /// <summary>
    /// Scans every realized item container (this panel never virtualizes — ADR-029 Решение 5 — so every
    /// container is realized whenever this is reachable) for those whose own vertical extent, in
    /// <paramref name="listBox"/>'s coordinate space, contains <paramref name="position"/>.Y, and returns
    /// the one horizontally NEAREST <paramref name="position"/>.X (fix-cycle 2 MINOR-1 — see this class's
    /// remarks on <see cref="HitTestReorderContainer(ListBox, Point)"/> for why "nearest", not
    /// "right-most", is the correct rule: it resolves BOTH the row's left gutter, before the first
    /// column, AND its right surplus space, after the last column, to the correct edge of the row). A
    /// container whose horizontal span already contains <paramref name="position"/>.X (distance <c>0</c>
    /// — the narrow <c>pmin</c> gap between two same-row columns, too thin for the direct hit-test above
    /// to have reliably hit either neighbor) is an exact match and short-circuits the scan. Returns
    /// <see langword="null"/> when no container's row contains <paramref name="position"/>.Y at all
    /// (cursor is below every row — genuinely past the end of the list).
    /// </summary>
    private static ListBoxItem? FindNearestContainerInSameRow(ListBox listBox, Point position)
    {
        ListBoxItem? nearest = null;
        var nearestDistance = double.PositiveInfinity;

        for (var i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;
            if (container.RenderSize.Height <= 0d) continue;

            var origin = container.TranslatePoint(new Point(0, 0), listBox);
            if (position.Y < origin.Y || position.Y >= origin.Y + container.RenderSize.Height) continue;

            double distance;
            if (position.X < origin.X)
                distance = origin.X - position.X;
            else if (position.X > origin.X + container.RenderSize.Width)
                distance = position.X - (origin.X + container.RenderSize.Width);
            else
                distance = 0d;

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = container;
            }
        }

        return nearest;
    }
}
