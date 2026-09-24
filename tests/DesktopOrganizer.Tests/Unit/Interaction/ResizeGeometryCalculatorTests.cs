using System.Windows;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Phase 1 (commit C9, docs/PLANS/phase-1-resize-stabilization.md §5.1) direct unit tests for the
/// production <see cref="ResizeGeometryCalculator"/> — as opposed to
/// <see cref="ResizeGeometryCharacterizationTests"/> (C1), which pins the PRE-extraction formula via a
/// standalone, Window-free copy. These tests call the actual extracted type (<see cref="ResizeGeometryCalculator.Accumulate"/>/
/// <see cref="ResizeGeometryCalculator.Apply"/>) directly, so a regression introduced anywhere between
/// C2 and C8 (not just in the original math) would be caught here too.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGeometryCalculatorTests
{
    // Same fixed "current window" bounds as the C1 characterization suite: rawRight = 400, rawBottom = 450.
    private static readonly Rect StartBounds = new(100, 200, 300, 250);

    private static ResizeConstraints FreeConstraints(double minWidth = GridMetrics.MinPanelSize) => new(
        MinWidth: minWidth,
        MinHeight: GridMetrics.MinPanelSize,
        SnapCellWidth: 0d,
        SnapCellHeight: 0d,
        TitleBarHeight: ChildWindow.TitleBarHeight,
        HorizontalChrome: GridMetrics.FileListHPadding,
        VerticalChrome: GridMetrics.FileListVPadding);

    private static Rect ApplyOneDelta(ResizeEdge edge, Vector delta, ResizeConstraints constraints, Rect? start = null)
    {
        var bounds = start ?? StartBounds;
        var raw = new ResizeRawExtent(bounds.Width, bounds.Height);
        raw = ResizeGeometryCalculator.Accumulate(raw, edge, delta);

        return ResizeGeometryCalculator.Apply(bounds, edge, raw, bounds.Right, bounds.Bottom, constraints);
    }

    // ── T-GEO-01..08: one case per ResizeEdge, free mode (List / snap off) ──────────────────────────
    [Theory]
    [InlineData(ResizeEdge.Right, 40d, 0d, 100d, 200d, 340d, 250d)]
    [InlineData(ResizeEdge.Left, 40d, 0d, 140d, 200d, 260d, 250d)]
    [InlineData(ResizeEdge.Top, 0d, 25d, 100d, 225d, 300d, 225d)]
    [InlineData(ResizeEdge.Bottom, 0d, 25d, 100d, 200d, 300d, 275d)]
    [InlineData(ResizeEdge.TopLeft, 40d, 25d, 140d, 225d, 260d, 225d)]
    [InlineData(ResizeEdge.TopRight, 40d, 25d, 100d, 225d, 340d, 225d)]
    [InlineData(ResizeEdge.BottomLeft, 40d, 25d, 140d, 200d, 260d, 275d)]
    [InlineData(ResizeEdge.BottomRight, 40d, 25d, 100d, 200d, 340d, 275d)]
    public void Apply_FreeMode_AllEightEdges_MovesOnlyExpectedAxisAndKeepsAnchor(
        ResizeEdge edge, double dx, double dy,
        double expectedLeft, double expectedTop, double expectedWidth, double expectedHeight)
    {
        var result = ApplyOneDelta(edge, new Vector(dx, dy), FreeConstraints());

        Assert.Equal(new Rect(expectedLeft, expectedTop, expectedWidth, expectedHeight), result);
    }

    // ── T-GEO-09..12: corners — both axes and both anchors simultaneously (already exercised above as
    // part of the 8-edge theory; these add an explicit second-delta accumulation to prove Accumulate's
    // running total, not just a single call, drives Apply correctly for corners specifically). ──────
    [Theory]
    [InlineData(ResizeEdge.TopLeft)]
    [InlineData(ResizeEdge.TopRight)]
    [InlineData(ResizeEdge.BottomLeft)]
    [InlineData(ResizeEdge.BottomRight)]
    public void Apply_Corners_TwoAccumulatedDeltas_BothAxesMoveAndBothAnchorsHold(ResizeEdge edge)
    {
        var raw = new ResizeRawExtent(StartBounds.Width, StartBounds.Height);
        raw = ResizeGeometryCalculator.Accumulate(raw, edge, new Vector(10d, 5d));
        raw = ResizeGeometryCalculator.Accumulate(raw, edge, new Vector(10d, 5d)); // total (20, 10)

        var result = ResizeGeometryCalculator.Apply(StartBounds, edge, raw, StartBounds.Right, StartBounds.Bottom, FreeConstraints());

        // Sign convention pinned separately/literally by Accumulate_SignConventionAndUntouchedAxis
        // (T-GEO-24) — reused structurally here via the same ResizeEdgeParser helpers production
        // itself branches on, to check Apply's REPOSITIONING (not just Accumulate's running total).
        var expectedWidth = edge.AnchorsRight() ? StartBounds.Width - 20d : StartBounds.Width + 20d;
        var expectedHeight = edge.AnchorsBottom() ? StartBounds.Height - 10d : StartBounds.Height + 10d;
        Assert.Equal(expectedWidth, result.Width, precision: 6);
        Assert.Equal(expectedHeight, result.Height, precision: 6);

        // Right anchor holds unless this corner anchors the right edge (Left-containing corners).
        if (edge.AnchorsRight())
            Assert.Equal(StartBounds.Right, result.Left + result.Width, precision: 6);
        else
            Assert.Equal(StartBounds.Left, result.Left, precision: 6);

        if (edge.AnchorsBottom())
            Assert.Equal(StartBounds.Bottom, result.Top + result.Height, precision: 6);
        else
            Assert.Equal(StartBounds.Top, result.Top, precision: 6);
    }

    // ── T-GEO-13..16: min size — drag far past the minimum; opposite (anchor) edge must not move. ───
    [Theory]
    [InlineData(ResizeEdge.Left, 250d, 0d, 280d, 200d, 120d, 250d)]
    [InlineData(ResizeEdge.Right, -250d, 0d, 100d, 200d, 120d, 250d)]
    [InlineData(ResizeEdge.Top, 0d, 200d, 100d, 330d, 300d, 120d)]
    [InlineData(ResizeEdge.Bottom, 0d, -200d, 100d, 200d, 300d, 120d)]
    public void Apply_FreeMode_MinSizeClamp_AnchorEdgeUnaffected(
        ResizeEdge edge, double dx, double dy,
        double expectedLeft, double expectedTop, double expectedWidth, double expectedHeight)
    {
        var result = ApplyOneDelta(edge, new Vector(dx, dy), FreeConstraints());

        Assert.Equal(new Rect(expectedLeft, expectedTop, expectedWidth, expectedHeight), result);
    }

    // ── T-GEO-17: Grid + snap on — width quantised to a whole column via Math.Ceiling. ───────────────
    [Fact]
    public void Apply_GridSnapOn_Width_CeilsToSnappedColumnExtent()
    {
        const double iconSize = 48d;
        var cellWidth = GridMetrics.CellSize(iconSize);
        var minWidth = GridMetrics.MinPanelWidth(iconSize);
        var constraints = FreeConstraints(minWidth) with { SnapCellWidth = cellWidth };

        var result = ApplyOneDelta(ResizeEdge.Right, new Vector(10d, 0d), constraints);

        var contentW = Math.Max(0d, 310d - GridMetrics.FileListHPadding);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cellWidth, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var expectedWidth = Math.Ceiling(Math.Max(minWidth, AdaptiveGridLayout.SnapExtent(contentW, cellWidth, AdaptiveGridLayout.DefaultMinGap, edgePadding) + GridMetrics.FileListHPadding));

        Assert.Equal(expectedWidth, result.Width, precision: 6);
        Assert.Equal(Math.Ceiling(expectedWidth), expectedWidth, precision: 6);
    }

    // ── T-GEO-18: Grid + snap on — height quantised to a whole row via Math.Ceiling. ─────────────────
    [Fact]
    public void Apply_GridSnapOn_Height_CeilsToSnappedRowExtent()
    {
        const double iconSize = 48d;
        var cellHeight = GridMetrics.RowHeight(iconSize);
        var constraints = FreeConstraints() with { SnapCellHeight = cellHeight };

        var result = ApplyOneDelta(ResizeEdge.Bottom, new Vector(0d, -50d), constraints);

        var contentH = Math.Max(0d, 200d - ChildWindow.TitleBarHeight - GridMetrics.FileListVPadding);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cellHeight, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio);
        var expectedHeight = Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, ChildWindow.TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, cellHeight, AdaptiveGridLayout.DefaultMinGap, edgePadding)));

        Assert.Equal(expectedHeight, result.Height, precision: 6);
        Assert.Equal(Math.Ceiling(expectedHeight), expectedHeight, precision: 6);
    }

    // ── T-GEO-19: dropping a fractional IconSize into snap-on still yields an integer pixel result
    // (Math.Ceiling really applied, not just "already a whole number by coincidence"). ───────────────
    [Fact]
    public void Apply_GridSnapOn_FractionalIconSize_StillProducesWholePixelWidth()
    {
        const double iconSize = 47.3d; // deliberately fractional — sliders aren't tick-snapped
        var cellWidth = GridMetrics.CellSize(iconSize);
        var minWidth = GridMetrics.MinPanelWidth(iconSize);
        var constraints = FreeConstraints(minWidth) with { SnapCellWidth = cellWidth };

        var result = ApplyOneDelta(ResizeEdge.Right, new Vector(3.7d, 0d), constraints);

        Assert.Equal(Math.Ceiling(result.Width), result.Width, precision: 9);
    }

    // ── T-GEO-20: Grid mode but snap disabled -> pixel-accurate, no quantization at all. ─────────────
    [Fact]
    public void Apply_GridModeSnapOff_IsPixelAccurate()
    {
        const double iconSize = 48d;
        var minWidth = GridMetrics.MinPanelWidth(iconSize);
        var constraints = FreeConstraints(minWidth); // SnapCellWidth/Height stay 0 even though minWidth is Grid-derived

        var result = ApplyOneDelta(ResizeEdge.Right, new Vector(7.25d, 0d), constraints);

        Assert.Equal(307.25d, result.Width, precision: 6);
    }

    // ── T-GEO-21: List-mode constraints (SnapCellWidth/Height == 0) stay free on BOTH axes even when
    // fed a Grid-shaped delta — SnapResizeToIconGrid is enforced by the CALLER (BuildConstraints), not
    // by Apply itself; Apply is free purely because the constraints say so. ─────────────────────────
    [Fact]
    public void Apply_ListModeConstraints_BothAxesFreeRegardlessOfDeltaShape()
    {
        var constraints = FreeConstraints(); // List-mode shape: SnapCellWidth == SnapCellHeight == 0

        var result = ApplyOneDelta(ResizeEdge.BottomRight, new Vector(13.5d, 27.5d), constraints);

        Assert.Equal(313.5d, result.Width, precision: 6);
        Assert.Equal(277.5d, result.Height, precision: 6);
    }

    // ── T-GEO-22: MinWidth selection differs by view mode (List vs Grid) — Apply just uses whatever
    // MinWidth it is given; this pins that a small IconSize genuinely produces two DIFFERENT floors. ─
    [Fact]
    public void MinWidth_GridVsList_AreGenuinelyDifferentFloors()
    {
        const double iconSize = 48d; // large enough that the unclamped icon-grid floor exceeds MinPanelSize

        var gridMinWidth = GridMetrics.MinPanelWidth(iconSize);
        var listMinWidth = GridMetrics.MinPanelSize;

        Assert.NotEqual(listMinWidth, gridMinWidth);
    }

    // ── T-GEO-23: horizontal (0.5) vs vertical (0) edge-padding ratios genuinely diverge — regression
    // pin for "one row of icons reported double height" bug. ─────────────────────────────────────────
    [Fact]
    public void Apply_GridSnapOn_HeightUsesVerticalRatio_NotHorizontalRatio()
    {
        const double iconSize = 48d;
        var cellHeight = GridMetrics.RowHeight(iconSize);
        var constraints = FreeConstraints() with { SnapCellHeight = cellHeight };

        var result = ApplyOneDelta(ResizeEdge.Bottom, new Vector(0d, -50d), constraints);

        var contentH = Math.Max(0d, 200d - ChildWindow.TitleBarHeight - GridMetrics.FileListVPadding);
        var wrongEdgePadding = AdaptiveGridLayout.EdgePadding(cellHeight, AdaptiveGridLayout.DefaultEdgePaddingRatio); // 0.5 — width-only ratio
        var expectedWithWrongRatio = Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, ChildWindow.TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, cellHeight, AdaptiveGridLayout.DefaultMinGap, wrongEdgePadding)));

        Assert.NotEqual(expectedWithWrongRatio, result.Height);
    }

    // ── T-GEO-24: Accumulate — sign convention: Right/Bottom ADD, Left/Top SUBTRACT; the untouched
    // axis is left exactly as passed in. ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(ResizeEdge.Right, 10d, 0d, 310d, 250d)]
    [InlineData(ResizeEdge.Left, 10d, 0d, 290d, 250d)]
    [InlineData(ResizeEdge.Bottom, 0d, 10d, 300d, 260d)]
    [InlineData(ResizeEdge.Top, 0d, 10d, 300d, 240d)]
    [InlineData(ResizeEdge.Left, 0d, 999d, 300d, 250d)] // Left doesn't affect height at all
    [InlineData(ResizeEdge.Top, 999d, 0d, 300d, 250d)] // Top doesn't affect width at all
    public void Accumulate_SignConventionAndUntouchedAxis(ResizeEdge edge, double dx, double dy, double expectedWidth, double expectedHeight)
    {
        var start = new ResizeRawExtent(300d, 250d);

        var result = ResizeGeometryCalculator.Accumulate(start, edge, new Vector(dx, dy));

        Assert.Equal(expectedWidth, result.Width, precision: 6);
        Assert.Equal(expectedHeight, result.Height, precision: 6);
    }

    // ── T-GEO-25: non-finite input (NaN/Infinity) -> Apply returns `current` unchanged (H3 guard). ──
    [Theory]
    [InlineData(double.NaN, 250d)]
    [InlineData(double.PositiveInfinity, 250d)]
    [InlineData(300d, double.NaN)]
    [InlineData(300d, double.NegativeInfinity)]
    public void Apply_NonFiniteRawExtent_ReturnsCurrentUnchanged(double rawWidth, double rawHeight)
    {
        var raw = new ResizeRawExtent(rawWidth, rawHeight);

        var result = ResizeGeometryCalculator.Apply(StartBounds, ResizeEdge.BottomRight, raw, StartBounds.Right, StartBounds.Bottom, FreeConstraints());

        Assert.Equal(StartBounds, result);
    }

    [Theory]
    [InlineData(double.NaN, 450d)]
    [InlineData(400d, double.PositiveInfinity)]
    public void Apply_NonFiniteAnchor_ReturnsCurrentUnchanged(double anchorRight, double anchorBottom)
    {
        var raw = new ResizeRawExtent(StartBounds.Width, StartBounds.Height);

        var result = ResizeGeometryCalculator.Apply(StartBounds, ResizeEdge.TopLeft, raw, anchorRight, anchorBottom, FreeConstraints());

        Assert.Equal(StartBounds, result);
    }

    // ── Point 7 — the axis the dragged edge does NOT affect is returned from `current`, not
    // recomputed even structurally-equal: prove Top/Bottom leave X/Width from `current` untouched even
    // when `current` deliberately disagrees with StartBounds (simulating a mid-gesture current rect). ─
    [Fact]
    public void Apply_VerticalOnlyEdge_LeavesCurrentWidthAndLeftUntouched()
    {
        var current = new Rect(999d, 200d, 555d, 250d); // deliberately different X/Width than StartBounds
        var raw = ResizeGeometryCalculator.Accumulate(new ResizeRawExtent(current.Width, current.Height), ResizeEdge.Bottom, new Vector(0d, 10d));

        var result = ResizeGeometryCalculator.Apply(current, ResizeEdge.Bottom, raw, current.Right, current.Bottom, FreeConstraints());

        Assert.Equal(999d, result.Left, precision: 6);
        Assert.Equal(555d, result.Width, precision: 6);
    }
}
