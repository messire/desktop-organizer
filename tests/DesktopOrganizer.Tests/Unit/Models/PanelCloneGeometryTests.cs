using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelCloneGeometry.Offset"/> (ADR-045, «Panel clone to page», backlog 1.x
/// §8b): a same-page clone must never sit exactly on top of its source, whatever the panel's geometry
/// mode or where it sits on the monitor.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCloneGeometryTests
{
    private static readonly Rect LargeWorkArea = new(0, 0, 1920, 1080);

    /// <summary>
    /// L-5 fix (code review, panel clone to page): a <see cref="PanelGeometryMode.GridAligned"/> clone
    /// steps by 4 whole workspace grid units (32 DIP at the default <c>U=8</c>) on both axes — one unit
    /// (8 DIP) left the clone's title bar almost entirely overlapping the source's.
    /// </summary>
    [Fact]
    public void Offset_GridAligned_StepsByFourWorkspaceUnits()
    {
        var clone = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            Position = new Point(100, 100),
            Size = new Size(50, 50),
        };
        var monitor = BuildMonitor(LargeWorkArea);
        var step = (int)WorkspaceGridMetrics.UnitSize * 4;

        PanelCloneGeometry.Offset(clone, gridSize: 32, monitor);

        Assert.Equal(new Point(100 + step, 100 + step), clone.Position);
    }

    /// <summary>A <see cref="PanelGeometryMode.LegacyFreeform"/> clone with an active legacy grid steps by <c>gridSize</c> DIP.</summary>
    [Fact]
    public void Offset_LegacyFreeform_GridSizeGreaterThanOne_StepsByGridSize()
    {
        var clone = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new Point(100, 100),
            Size = new Size(50, 50),
        };
        var monitor = BuildMonitor(LargeWorkArea);

        PanelCloneGeometry.Offset(clone, gridSize: 32, monitor);

        Assert.Equal(new Point(132, 132), clone.Position);
    }

    /// <summary>
    /// With no active legacy grid (<c>gridSize &lt;= 1</c>), a LegacyFreeform clone falls back to the fixed
    /// 24 DIP step — but L-5's 32 DIP floor (code review, panel clone to page) still applies on top of it,
    /// so the actual step is 32, not 24.
    /// </summary>
    /// <param name="gridSize">The non-positive/unity <c>IAppSettings.GridSize</c> value under test.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Offset_LegacyFreeform_GridSizeLessOrEqualOne_UsesMinStepOf32(int gridSize)
    {
        var clone = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new Point(100, 100),
            Size = new Size(50, 50),
        };
        var monitor = BuildMonitor(LargeWorkArea);

        PanelCloneGeometry.Offset(clone, gridSize, monitor);

        Assert.Equal(new Point(132, 132), clone.Position);
    }

    /// <summary>
    /// L-5 fix (code review, panel clone to page): an active legacy grid smaller than the 32 DIP floor
    /// (e.g. 8) is floored to 32 instead of stepping by the tiny <c>gridSize</c> itself.
    /// </summary>
    [Fact]
    public void Offset_LegacyFreeform_GridSizeSmallerThanFloor_UsesMinStepOf32()
    {
        var clone = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new Point(100, 100),
            Size = new Size(50, 50),
        };
        var monitor = BuildMonitor(LargeWorkArea);

        PanelCloneGeometry.Offset(clone, gridSize: 8, monitor);

        Assert.Equal(new Point(132, 132), clone.Position);
    }

    /// <summary>
    /// A source sitting flush against the work area's bottom-right corner would clamp the offset right
    /// back onto the source's own position — <see cref="PanelCloneGeometry.Offset"/> detects that and
    /// applies the offset in the OPPOSITE direction instead.
    /// </summary>
    [Fact]
    public void Offset_ClampWouldCollapseBackToOriginal_ReversesDirection()
    {
        var workArea = new Rect(0, 0, 200, 200);
        var monitor = BuildMonitor(workArea);
        var original = new Point(150, 150);
        var clone = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            Position = original,
            Size = new Size(50, 50), // top-left (150,150) + size (50,50) exactly fills the work area.
        };

        PanelCloneGeometry.Offset(clone, gridSize: 32, monitor);

        // Stepping by +4 units (32 DIP) would clamp right back to (150,150) — the reversed -32 DIP step
        // (118,118) is still fully inside the work area, so no further clamping applies.
        Assert.Equal(new Point(118, 118), clone.Position);
        Assert.NotEqual(original, clone.Position);
    }

    /// <summary>The clone's position must never equal the source's own position after <see cref="PanelCloneGeometry.Offset"/> — checked across every scenario above plus a mid-screen sanity case.</summary>
    [Fact]
    public void Offset_MidScreenPosition_NeverEqualsSourcePosition()
    {
        var clone = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new Point(500, 500),
            Size = new Size(100, 100),
        };
        var original = clone.Position;
        var monitor = BuildMonitor(LargeWorkArea);

        PanelCloneGeometry.Offset(clone, gridSize: 1, monitor);

        Assert.NotEqual(original, clone.Position);
    }

    private static MonitorInfo BuildMonitor(Rect workArea) =>
        new("\\\\.\\DISPLAY1", workArea, workArea, IsPrimary: true);
}
