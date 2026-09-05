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
    /// the cursor isn't over any item container (empty space below/after the last item).
    /// </summary>
    public static ListBoxItem? HitTestReorderContainer(ListBox listBox, DragEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
        return hit is null ? null : FindAncestorOrSelf<ListBoxItem>(hit.VisualHit);
    }
}
