using System.Windows;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Unit tests for <see cref="ListReorderGeometry.ComputeInsertionGap"/> (ADR-014 Block C4) — the pure,
/// UI-free core of the drag-to-reorder insertion-gap math. No real <see cref="System.Windows.Controls.ListBox"/>
/// or hit-test is involved; this operates purely on primitives (container index, cursor position,
/// container size, view mode, item count), so it's directly unit-testable. Relocated from
/// <c>ChildWindow.xaml.cs</c> code-behind into <c>Behaviors/ListReorderGeometry.cs</c>
/// (REFACTORING_PLAN.md §5.3).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowReorderGeometryTests
{
    private static readonly Size ContainerSize = new(40, 20);

    [Fact]
    public void ComputeInsertionGap_NullContainerIndex_ReturnsItemCount()
    {
        // Cursor over empty space past the last item — always resolves to the end-of-list gap,
        // regardless of the other arguments.
        var gap = ListReorderGeometry.ComputeInsertionGap(null, new Point(999, 999), ContainerSize, PanelViewMode.Grid, itemCount: 5);

        Assert.Equal(5, gap);
    }

    [Fact]
    public void ComputeInsertionGap_NullContainerIndex_ListMode_StillReturnsItemCount()
    {
        var gap = ListReorderGeometry.ComputeInsertionGap(null, new Point(0, 0), ContainerSize, PanelViewMode.List, itemCount: 3);

        Assert.Equal(3, gap);
    }

    // ── Grid (PanelViewMode.Grid) — horizontal (X) axis ──────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ComputeInsertionGap_GridMode_CursorBeforeHorizontalMidpoint_ReturnsContainerIndex(int index)
    {
        var beforeMidpoint = new Point((ContainerSize.Width / 2.0) - 1, 5); // left half

        var gap = ListReorderGeometry.ComputeInsertionGap(index, beforeMidpoint, ContainerSize, PanelViewMode.Grid, itemCount: 10);

        Assert.Equal(index, gap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ComputeInsertionGap_GridMode_CursorAfterHorizontalMidpoint_ReturnsContainerIndexPlusOne(int index)
    {
        var afterMidpoint = new Point((ContainerSize.Width / 2.0) + 1, 5); // right half

        var gap = ListReorderGeometry.ComputeInsertionGap(index, afterMidpoint, ContainerSize, PanelViewMode.Grid, itemCount: 10);

        Assert.Equal(index + 1, gap);
    }

    [Fact]
    public void ComputeInsertionGap_GridMode_IgnoresVerticalPosition()
    {
        // Grid mode's comparison axis is horizontal (X) only: a cursor in the far vertical bottom but
        // horizontal left half must still resolve to "before" the container.
        var point = new Point((ContainerSize.Width / 2.0) - 1, ContainerSize.Height * 10);

        var gap = ListReorderGeometry.ComputeInsertionGap(2, point, ContainerSize, PanelViewMode.Grid, itemCount: 10);

        Assert.Equal(2, gap);
    }

    // ── List (PanelViewMode.List) — vertical (Y) axis ────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ComputeInsertionGap_ListMode_CursorBeforeVerticalMidpoint_ReturnsContainerIndex(int index)
    {
        var beforeMidpoint = new Point(5, (ContainerSize.Height / 2.0) - 1); // top half

        var gap = ListReorderGeometry.ComputeInsertionGap(index, beforeMidpoint, ContainerSize, PanelViewMode.List, itemCount: 10);

        Assert.Equal(index, gap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ComputeInsertionGap_ListMode_CursorAfterVerticalMidpoint_ReturnsContainerIndexPlusOne(int index)
    {
        var afterMidpoint = new Point(5, (ContainerSize.Height / 2.0) + 1); // bottom half

        var gap = ListReorderGeometry.ComputeInsertionGap(index, afterMidpoint, ContainerSize, PanelViewMode.List, itemCount: 10);

        Assert.Equal(index + 1, gap);
    }

    [Fact]
    public void ComputeInsertionGap_ListMode_IgnoresHorizontalPosition()
    {
        // List mode's comparison axis is vertical (Y) only: a cursor far to the right but in the
        // vertical top half must still resolve to "before" the container.
        var point = new Point(ContainerSize.Width * 10, (ContainerSize.Height / 2.0) - 1);

        var gap = ListReorderGeometry.ComputeInsertionGap(2, point, ContainerSize, PanelViewMode.List, itemCount: 10);

        Assert.Equal(2, gap);
    }
}
