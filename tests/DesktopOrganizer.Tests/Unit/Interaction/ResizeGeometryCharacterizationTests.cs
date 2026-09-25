using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Phase 1 (commit C1, docs/PLANS/phase-1-resize-stabilization.md §4/§5.1) characterization tests.
/// These pin the CURRENT resize geometry math — <see cref="PanelResizeController.OnResizeDragStarted"/>'s
/// raw-accumulator/snap-cell setup and <see cref="PanelResizeController.OnResizeDragDelta"/>'s
/// quantize/clamp/reposition math (PanelResizeController.cs:83-215), plus
/// <see cref="PanelResizeController.SnapWidthToGrid"/> — BEFORE any refactoring.
/// <para>
/// The test project cannot construct a real WPF <see cref="System.Windows.Window"/>/<see cref="System.Windows.Controls.Primitives.Thumb"/>
/// (no STA thread — see <c>SystemIconsPanelManagerTests.cs:58</c>), so <see cref="PanelResizeController"/>
/// itself cannot be exercised directly. Instead, <see cref="ResizeMathHarness"/> below is a line-for-line,
/// Window-free copy of the production formula, operating on a plain <see cref="Rect"/>. Every branch is
/// annotated with the exact production line range it mirrors.
/// </para>
/// <para>
/// <b>This file is the safety net for commit C2</b> (`refactor(ui): extract pure ResizeGeometryCalculator +
/// ResizeEdge`), which lifts this exact math into a standalone, testable calculator. Every test here MUST
/// stay green through C2 without a single edit — if one needs to change afterwards, that is a REGRESSION
/// signal, not a "test went stale" situation (plan §4, row C1/C2; plan §2.5's line "Golden values below are
/// derived ... never by re-invoking this file's own harness to check itself" is honored throughout: expected
/// values are computed either structurally — e.g. "the edge not being dragged doesn't move" — or via the
/// already-unit-tested pure building blocks <see cref="GridMetrics"/>/<see cref="AdaptiveGridLayout"/> that
/// production itself calls, never via magic literals nor by asserting the harness against itself).
/// </para>
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGeometryCharacterizationTests
{
    // Arbitrary but fixed "current window" bounds shared by the free-mode (List/snap-off) cases below.
    // rawRight = 100 + 300 = 400, rawBottom = 200 + 250 = 450 (PanelResizeController.cs:95-96) — these are
    // the fixed opposite-edge anchors snapshotted once at DragStarted, referenced throughout.
    private static readonly Rect StartBounds = new(100, 200, 300, 250);

    // ── T-GEO-01..08: one case per ResizeEdge, free mode (List / snap off) ──────────────────────────
    // Single-axis edges (Left/Top/Right/Bottom) additionally prove: (a) only the intended axis moves,
    // (b) the OPPOSITE edge (the anchor snapshotted at DragStarted) stays exactly where it was, and
    // (c) the sign convention of the raw accumulator (T-GEO-24): Right/Bottom ADD the delta,
    // Left/Top SUBTRACT it (PanelResizeController.cs:136-154).
    // Corner edges (T-GEO-09..12) exercise both axes and both anchors simultaneously.

    /// <summary>Verifies that applying one delta to each of the eight edges through <see cref="ResizeMathHarness"/> matches the pinned pre-C2 production formula.</summary>
    /// <param name="direction">The edge direction string (e.g. <c>"Right"</c>, <c>"TopLeft"</c>).</param>
    /// <param name="horizontalChange">The raw horizontal delta.</param>
    /// <param name="verticalChange">The raw vertical delta.</param>
    /// <param name="expectedLeft">The expected resulting Left.</param>
    /// <param name="expectedTop">The expected resulting Top.</param>
    /// <param name="expectedWidth">The expected resulting width.</param>
    /// <param name="expectedHeight">The expected resulting height.</param>
    [Theory]
    [InlineData("Right", 40d, 0d, 100d, 200d, 340d, 250d)] // right edge grows; left (anchor) unchanged
    [InlineData("Left", 40d, 0d, 140d, 200d, 260d, 250d)] // left edge shrinks inward; right edge (100+300=400) preserved via left=rawRight-newWidth
    [InlineData("Top", 0d, 25d, 100d, 225d, 300d, 225d)] // top edge shrinks; bottom edge (200+250=450) preserved
    [InlineData("Bottom", 0d, 25d, 100d, 200d, 300d, 275d)] // bottom edge grows; top (anchor) unchanged
    [InlineData("TopLeft", 40d, 25d, 140d, 225d, 260d, 225d)] // both anchors (right=400, bottom=450) preserved
    [InlineData("TopRight", 40d, 25d, 100d, 225d, 340d, 225d)] // left anchor (100) + bottom anchor (450) preserved
    [InlineData("BottomLeft", 40d, 25d, 140d, 200d, 260d, 275d)] // right anchor (400) + top anchor (200) preserved
    [InlineData("BottomRight", 40d, 25d, 100d, 200d, 340d, 275d)] // left anchor (100) + top anchor (200) preserved
    public void ApplyDelta_FreeMode_AllEightEdges_MatchesCurrentFormula(
        string direction,
        double horizontalChange,
        double verticalChange,
        double expectedLeft,
        double expectedTop,
        double expectedWidth,
        double expectedHeight)
    {
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: 0d, snapCellHeight: 0d, minWidth: GridMetrics.MinPanelSize);

        var result = harness.ApplyDelta(direction, StartBounds, horizontalChange, verticalChange);

        Assert.Equal(new Rect(expectedLeft, expectedTop, expectedWidth, expectedHeight), result);
    }

    // ── T-GEO-13..16: min-size clamp — shrink far past the minimum on each edge ─────────────────────
    // The opposite (anchor) edge must stay exactly where it was even though the dragged edge's own
    // travel is clamped by GridMetrics.MinPanelSize (List mode's flat floor) — PanelResizeController.cs:173-183.

    /// <summary>Verifies that shrinking far past the minimum on each edge clamps the result to <c>GridMetrics.MinPanelSize</c> while the anchor edge stays unaffected.</summary>
    /// <param name="direction">The edge direction string (e.g. <c>"Left"</c>, <c>"Bottom"</c>).</param>
    /// <param name="horizontalChange">The raw horizontal delta.</param>
    /// <param name="verticalChange">The raw vertical delta.</param>
    /// <param name="expectedLeft">The expected resulting Left.</param>
    /// <param name="expectedTop">The expected resulting Top.</param>
    /// <param name="expectedWidth">The expected resulting width.</param>
    /// <param name="expectedHeight">The expected resulting height.</param>
    [Theory]
    [InlineData("Left", 250d, 0d, 280d, 200d, 120d, 250d)] // rawWidth = 300-250=50 < 120 -> clamped to 120; right edge (400) preserved
    [InlineData("Right", -250d, 0d, 100d, 200d, 120d, 250d)] // rawWidth = 300+(-250)=50 < 120 -> clamped; left (anchor) unchanged
    [InlineData("Top", 0d, 200d, 100d, 330d, 300d, 120d)] // rawHeight = 250-200=50 < 120 -> clamped; bottom edge (450) preserved
    [InlineData("Bottom", 0d, -200d, 100d, 200d, 300d, 120d)] // rawHeight = 250+(-200)=50 < 120 -> clamped; top (anchor) unchanged
    public void ApplyDelta_FreeMode_MinSizeClamp_AnchorEdgeUnaffected(
        string direction,
        double horizontalChange,
        double verticalChange,
        double expectedLeft,
        double expectedTop,
        double expectedWidth,
        double expectedHeight)
    {
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: 0d, snapCellHeight: 0d, minWidth: GridMetrics.MinPanelSize);

        var result = harness.ApplyDelta(direction, StartBounds, horizontalChange, verticalChange);

        Assert.Equal(new Rect(expectedLeft, expectedTop, expectedWidth, expectedHeight), result);
    }

    // ── T-GEO-17/18: Grid + snap on — width snaps to whole columns, height to whole rows, both via
    // Math.Ceiling (PanelResizeController.cs:177-183). Golden values are computed by calling the exact
    // same already-tested AdaptiveGridLayout.SnapExtent/EdgePadding building blocks production calls —
    // never a literal that could hide a formula drift.

    /// <summary>Verifies that with Grid+snap on, the harness's width quantises to a whole column via <see cref="Math.Ceiling(double)"/>.</summary>
    [Fact]
    public void ApplyDelta_GridSnapOn_Width_CeilsToSnappedColumnExtent()
    {
        const double iconSize = 48d;
        var cellWidth = GridMetrics.CellSize(iconSize);
        var minWidth = GridMetrics.MinPanelWidth(iconSize);
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: cellWidth, snapCellHeight: 0d, minWidth: minWidth);

        // Right edge, +10px -> rawWidth = 310, contentW = 310 - FileListHPadding (ADR-029 F-3 fix:
        // FileListHPadding is PanelMetrics.ContentLeftInset = 8, no longer 0 — contentW is computed via
        // the live constant below, not a hardcoded literal, so this comment only documents the shape).
        var result = harness.ApplyDelta("Right", StartBounds, horizontalChange: 10d, verticalChange: 0d);

        var contentW = Math.Max(0d, 310d - GridMetrics.FileListHPadding);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cellWidth, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var expectedWidth = Math.Ceiling(Math.Max(minWidth, AdaptiveGridLayout.SnapExtent(contentW, cellWidth, AdaptiveGridLayout.DefaultMinGap, edgePadding) + GridMetrics.FileListHPadding));

        Assert.Equal(expectedWidth, result.Width, precision: 6);
        Assert.Equal(Math.Ceiling(expectedWidth), expectedWidth, precision: 6); // sanity: Ceiling really was applied (whole pixel)
    }

    /// <summary>Verifies that with Grid+snap on, the harness's height quantises to a whole row via <see cref="Math.Ceiling(double)"/>.</summary>
    [Fact]
    public void ApplyDelta_GridSnapOn_Height_CeilsToSnappedRowExtent()
    {
        const double iconSize = 48d;
        var cellHeight = GridMetrics.RowHeight(iconSize);
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: 0d, snapCellHeight: cellHeight, minWidth: GridMetrics.MinPanelSize);

        // Bottom edge, -50px -> rawHeight = 200, contentH = max(0, 200 - TitleBarHeight(32) - FileListVPadding(4)) = 164.
        var result = harness.ApplyDelta("Bottom", StartBounds, horizontalChange: 0d, verticalChange: -50d);

        var contentH = Math.Max(0d, 200d - ChildWindow.TitleBarHeight - GridMetrics.FileListVPadding);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cellHeight, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio);
        var expectedHeight = Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, ChildWindow.TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, cellHeight, AdaptiveGridLayout.DefaultMinGap, edgePadding)));

        Assert.Equal(expectedHeight, result.Height, precision: 6);
        Assert.Equal(Math.Ceiling(expectedHeight), expectedHeight, precision: 6);
    }

    // ── T-GEO-20: Grid mode but snap disabled (snapCellWidth/Height == 0) -> pixel-accurate, no
    // Math.Ceiling/SnapExtent quantization at all, even though minWidth still uses the Grid-mode floor.

    /// <summary>Verifies that Grid mode with snap disabled leaves the harness's width pixel-accurate, with no quantisation applied.</summary>
    [Fact]
    public void ApplyDelta_GridModeSnapOff_IsPixelAccurate()
    {
        const double iconSize = 48d;
        var minWidth = GridMetrics.MinPanelWidth(iconSize);
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: 0d, snapCellHeight: 0d, minWidth: minWidth);

        var result = harness.ApplyDelta("Right", StartBounds, horizontalChange: 7.25d, verticalChange: 0d);

        Assert.Equal(307.25d, result.Width, precision: 6); // 300 + 7.25, no rounding
    }

    // ── T-GEO-21: List mode is free on BOTH axes even when SnapResizeToIconGrid == true — the
    // OnResizeDragStarted branch decision (PanelResizeController.cs:100-117), not the Delta formula.

    /// <summary>Verifies that <see cref="ResizeMathHarness.SelectSnapCells"/> stays free on both axes for List mode, or when icon-grid snap is disabled.</summary>
    /// <param name="snapResizeToIconGrid">Whether icon-grid snap is enabled.</param>
    /// <param name="viewMode">The panel's view mode.</param>
    /// <param name="expectedSnapWidth">The expected snap cell width.</param>
    /// <param name="expectedSnapHeight">The expected snap cell height.</param>
    [Theory]
    [InlineData(true, PanelViewMode.List, 0d, 0d)]
    [InlineData(false, PanelViewMode.List, 0d, 0d)]
    [InlineData(false, PanelViewMode.Grid, 0d, 0d)]
    public void SelectSnapCells_ListModeOrSnapDisabled_BothAxesFree(bool snapResizeToIconGrid, PanelViewMode viewMode, double expectedSnapWidth, double expectedSnapHeight)
    {
        var (snapCellWidth, snapCellHeight) = ResizeMathHarness.SelectSnapCells(snapResizeToIconGrid, viewMode, iconSize: 48d);

        Assert.Equal(expectedSnapWidth, snapCellWidth);
        Assert.Equal(expectedSnapHeight, snapCellHeight);
    }

    /// <summary>Verifies that <see cref="ResizeMathHarness.SelectSnapCells"/> in Grid mode with snap enabled uses the icon cell size and row height.</summary>
    [Fact]
    public void SelectSnapCells_GridModeWithSnapEnabled_UsesCellSizeAndRowHeight()
    {
        const double iconSize = 48d;

        var (snapCellWidth, snapCellHeight) = ResizeMathHarness.SelectSnapCells(snapResizeToIconGrid: true, PanelViewMode.Grid, iconSize);

        Assert.Equal(GridMetrics.CellSize(iconSize), snapCellWidth);
        Assert.Equal(GridMetrics.RowHeight(iconSize), snapCellHeight);
    }

    // ── T-GEO-22: min WIDTH selection — Grid uses the icon-grid-derived floor, List uses the flat
    // floor (PanelResizeController.cs:173-175). Height's floor is always GridMetrics.MinPanelSize in
    // BOTH modes (no selection at all) — already exercised implicitly by every test above using the
    // constant directly for height.

    /// <summary>Verifies that <see cref="ResizeMathHarness.SelectMinWidth"/> uses the icon-grid-derived floor for Grid mode and the flat floor for List mode.</summary>
    [Fact]
    public void SelectMinWidth_GridVsList_UsesDifferentFloors()
    {
        const double iconSize = 16d; // small IconSize is where Grid's derived floor and List's flat floor genuinely differ

        var gridMinWidth = ResizeMathHarness.SelectMinWidth(PanelViewMode.Grid, iconSize);
        var listMinWidth = ResizeMathHarness.SelectMinWidth(PanelViewMode.List, iconSize);

        Assert.Equal(GridMetrics.MinPanelWidth(iconSize), gridMinWidth);
        Assert.Equal(GridMetrics.MinPanelSize, listMinWidth);
    }

    // ── T-GEO-23: horizontal edge-padding ratio (0.5) vs vertical (0) genuinely diverge — regression
    // pin for the "single row of icons reported double height" bug (AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio's
    // doc comment). The second assertion proves the test is not vacuous: swapping in the WRONG
    // (horizontal) ratio for the height branch would have produced a different number.

    /// <summary>Verifies that the harness's height snapping uses the vertical edge-padding ratio while width snapping uses the horizontal one, and that the two genuinely diverge — regression guard for a "row reported double height" bug.</summary>
    [Fact]
    public void ApplyDelta_GridSnapOn_HeightUsesVerticalRatio_WidthUsesHorizontalRatio_AndTheyDiffer()
    {
        const double iconSize = 48d;
        var cellHeight = GridMetrics.RowHeight(iconSize);
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: 0d, snapCellHeight: cellHeight, minWidth: GridMetrics.MinPanelSize);

        var result = harness.ApplyDelta("Bottom", StartBounds, horizontalChange: 0d, verticalChange: -50d);

        var contentH = Math.Max(0d, 200d - ChildWindow.TitleBarHeight - GridMetrics.FileListVPadding);
        var correctEdgePadding = AdaptiveGridLayout.EdgePadding(cellHeight, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio); // ratio 0
        var wrongEdgePadding = AdaptiveGridLayout.EdgePadding(cellHeight, AdaptiveGridLayout.DefaultEdgePaddingRatio); // ratio 0.5 — belongs to WIDTH only

        var expectedWithCorrectRatio = Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, ChildWindow.TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, cellHeight, AdaptiveGridLayout.DefaultMinGap, correctEdgePadding)));
        var expectedWithWrongRatio = Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, ChildWindow.TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, cellHeight, AdaptiveGridLayout.DefaultMinGap, wrongEdgePadding)));

        Assert.Equal(expectedWithCorrectRatio, result.Height, precision: 6);
        Assert.NotEqual(expectedWithWrongRatio, result.Height); // proves the two ratios are NOT interchangeable here
    }

    // ── Free-height branch's deliberate asymmetry with the free-width branch (plan §2.5 point 5):
    // free width re-derives content then adds chrome back (contentW + HorizontalChrome); free height
    // instead uses _rawHeight DIRECTLY, never recomputed from contentH. This is a documented, intentional
    // divergence (numerically a no-op today only because it is always dominated by the MinPanelSize
    // clamp with real production constants) — the point of this test is to pin the FORMULA SHAPE the
    // harness (and therefore the future ResizeGeometryCalculator, commit C2) must keep: do NOT "clean up"
    // this branch to read `Math.Max(GridMetrics.MinPanelSize, contentH + TitleBarHeight + FileListVPadding)`
    // even though it looks equivalent — plan §2.5 explicitly forbids that "tidying".

    /// <summary>Verifies that the free-height branch uses the raw accumulated height directly, rather than re-deriving it from content — the deliberate asymmetry with the free-width branch.</summary>
    [Fact]
    public void ApplyDelta_FreeMode_Height_UsesRawHeightDirectly_NotRecomputedFromContent()
    {
        var harness = new ResizeMathHarness(StartBounds, snapCellWidth: 0d, snapCellHeight: 0d, minWidth: GridMetrics.MinPanelSize);

        // Bottom edge, +500px -> rawHeight = 750, comfortably above GridMetrics.MinPanelSize so the
        // Max(...) clamp cannot mask which operand won.
        var result = harness.ApplyDelta("Bottom", StartBounds, horizontalChange: 0d, verticalChange: 500d);

        Assert.Equal(750d, result.Height, precision: 6); // == raw.Height, exactly, per PanelResizeController.cs:183
    }

    // ── SnapWidthToGrid — separate, standalone drop-time snap entry point (PanelResizeController.cs:243-258).

    /// <summary>Verifies that <see cref="ResizeMathHarness.SnapWidthToGrid"/> quantises the width to a snapped column extent via <see cref="Math.Ceiling(double)"/>.</summary>
    [Fact]
    public void SnapWidthToGrid_GridMode_CeilsToSnappedColumnExtent()
    {
        const double iconSize = 48d;
        var cell = GridMetrics.CellSize(iconSize);
        var minWidth = GridMetrics.MinPanelWidth(iconSize);

        var result = ResizeMathHarness.SnapWidthToGrid(currentWidth: 260d, iconSize);

        var content = Math.Max(0d, 260d - GridMetrics.FileListHPadding);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var expected = Math.Ceiling(Math.Max(minWidth, AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding) + GridMetrics.FileListHPadding));

        Assert.Equal(expected, result, precision: 6);
    }

    /// <summary>Verifies that <see cref="ResizeMathHarness.SnapWidthToGrid"/> clamps a width below the minimum up to <c>MinPanelWidth</c>.</summary>
    [Fact]
    public void SnapWidthToGrid_BelowMinimum_ClampsToMinPanelWidth()
    {
        const double iconSize = 48d; // MinPanelWidth(48) is well above a tiny 10px width

        var result = ResizeMathHarness.SnapWidthToGrid(currentWidth: 10d, iconSize);

        Assert.Equal(GridMetrics.MinPanelWidth(iconSize), result, precision: 6);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Window-free, line-for-line copy of <see cref="PanelResizeController.OnResizeDragStarted"/>'s
    /// raw-accumulator/snap-cell setup and <see cref="PanelResizeController.OnResizeDragDelta"/>'s
    /// quantize/clamp/reposition math (PanelResizeController.cs:83-258), operating on a <see cref="Rect"/>
    /// instead of a <see cref="System.Windows.Window"/>. Every method/branch below is annotated with the
    /// exact production line range it mirrors — do NOT reorder, "simplify", or otherwise diverge from that
    /// source without re-reading it first; doing so would silently defeat this file's entire purpose
    /// (pinning CURRENT behavior ahead of commit C2's bit-for-bit extraction into
    /// <c>Interaction/ResizeGeometryCalculator.cs</c>).
    /// </summary>
    private sealed class ResizeMathHarness
    {
        private readonly double _rawRight;
        private readonly double _rawBottom;
        private readonly double _snapCellWidth;
        private readonly double _snapCellHeight;
        private readonly double _minWidth;

        private double _rawWidth;
        private double _rawHeight;

        public ResizeMathHarness(Rect start, double snapCellWidth, double snapCellHeight, double minWidth)
        {
            // PanelResizeController.OnResizeDragStarted:93-96.
            _rawWidth = start.Width;
            _rawHeight = start.Height;
            _rawRight = start.Left + start.Width;
            _rawBottom = start.Top + start.Height;
            _snapCellWidth = snapCellWidth;
            _snapCellHeight = snapCellHeight;
            _minWidth = minWidth;
        }

        /// <summary>Mirrors PanelResizeController.OnResizeDragStarted:100-117.</summary>
        public static (double SnapCellWidth, double SnapCellHeight) SelectSnapCells(bool snapResizeToIconGrid, PanelViewMode viewMode, double iconSize)
            => snapResizeToIconGrid && viewMode == PanelViewMode.Grid
                ? (GridMetrics.CellSize(iconSize), GridMetrics.RowHeight(iconSize))
                : (0d, 0d);

        /// <summary>Mirrors PanelResizeController.OnResizeDragDelta:173-175.</summary>
        public static double SelectMinWidth(PanelViewMode viewMode, double iconSize)
            => viewMode == PanelViewMode.Grid ? GridMetrics.MinPanelWidth(iconSize) : GridMetrics.MinPanelSize;

        /// <summary>Mirrors PanelResizeController.SnapWidthToGrid:243-258 (Grid mode assumed by the caller).</summary>
        public static double SnapWidthToGrid(double currentWidth, double iconSize)
        {
            var cell = GridMetrics.CellSize(iconSize);
            var content = Math.Max(0d, currentWidth - GridMetrics.FileListHPadding);

            return Math.Ceiling(Math.Max(
                GridMetrics.MinPanelWidth(iconSize),
                AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio)) + GridMetrics.FileListHPadding));
        }

        /// <summary>Mirrors PanelResizeController.OnResizeDragDelta:132-214 for a single delta applied to <paramref name="current"/>.</summary>
        public Rect ApplyDelta(string direction, Rect current, double horizontalChange, double verticalChange)
        {
            // :136-154 — raw accumulator sign convention (T-GEO-24): Right/Bottom ADD, Left/Top SUBTRACT.
            if (direction.Contains("Right")) _rawWidth += horizontalChange;
            if (direction.Contains("Bottom")) _rawHeight += verticalChange;
            if (direction.Contains("Left")) _rawWidth -= horizontalChange;
            if (direction.Contains("Top")) _rawHeight -= verticalChange;

            // :162-163 — strip chrome before quantising.
            var contentW = Math.Max(0d, _rawWidth - GridMetrics.FileListHPadding);
            var contentH = Math.Max(0d, _rawHeight - ChildWindow.TitleBarHeight - GridMetrics.FileListVPadding);

            // :177-179 — width: snap branch re-adds HorizontalChrome after quantising; free branch also
            // re-adds it (content + chrome), symmetrically.
            var newWidth = _snapCellWidth > 0d
                ? Math.Ceiling(Math.Max(_minWidth, AdaptiveGridLayout.SnapExtent(contentW, _snapCellWidth, AdaptiveGridLayout.DefaultMinGap, AdaptiveGridLayout.EdgePadding(_snapCellWidth, AdaptiveGridLayout.DefaultEdgePaddingRatio)) + GridMetrics.FileListHPadding))
                : Math.Max(_minWidth, contentW + GridMetrics.FileListHPadding);

            // :181-183 — height: snap branch re-adds chrome after quantising (symmetric with width), but
            // the FREE branch deliberately uses _rawHeight DIRECTLY, NOT `contentH + TitleBarHeight +
            // FileListVPadding` — see the class doc comment and
            // ApplyDelta_FreeMode_Height_UsesRawHeightDirectly_NotRecomputedFromContent. Do not "fix" this
            // asymmetry; plan §2.5 point 5 forbids it.
            var newHeight = _snapCellHeight > 0d
                ? Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, ChildWindow.TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, _snapCellHeight, AdaptiveGridLayout.DefaultMinGap, AdaptiveGridLayout.EdgePadding(_snapCellHeight, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio))))
                : Math.Max(GridMetrics.MinPanelSize, _rawHeight);

            double left = current.Left, top = current.Top, width = current.Width, height = current.Height;

            // :186-194 — width applied only on the dragged axis; the untouched axis returns `current` as-is.
            if (direction.Contains("Left"))
            {
                left = _rawRight - newWidth;
                width = newWidth;
            }
            else if (direction.Contains("Right"))
            {
                width = newWidth;
            }

            // :197-205 — same shape for height.
            if (direction.Contains("Top"))
            {
                top = _rawBottom - newHeight;
                height = newHeight;
            }
            else if (direction.Contains("Bottom"))
            {
                height = newHeight;
            }

            return new Rect(left, top, width, height);
        }
    }
}
