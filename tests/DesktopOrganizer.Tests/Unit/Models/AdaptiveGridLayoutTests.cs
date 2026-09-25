using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Baseline unit tests for <see cref="AdaptiveGridLayout"/> (docs/PLANS/adaptive-icon-grid.md §10).
/// Uses the plan's canonical parameters: <c>x = 84</c>, <c>x_v = 92</c> (IconSize = 48),
/// <c>pmin = AdaptiveGridLayout.DefaultMinGap</c> (<c>4</c> as of polishing/ui-1's horizontal-resize
/// spec — see the constant's doc comment; most tests below take <see cref="MinGap"/> as a parameter so
/// they track whatever the constant currently is rather than assuming a specific value), <c>m = 42</c>/
/// <c>m_v = 46</c> (<c>EdgePaddingRatio = AdaptiveGridLayout.DefaultEdgePaddingRatio = 0.5</c>). Covers
/// the full docs/PLANS/adaptive-icon-grid.md §10 matrix (pmin = 0 and pmin != 0 series, snap edge cases,
/// degenerate inputs) plus the finalization-review regression coverage for the <c>LayoutTolerance</c>
/// sub-pixel allowance in <see cref="AdaptiveGridLayout.RowCapacity"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class AdaptiveGridLayoutTests
{
    private const double Cell = 84d;
    private const double RowCell = 92d;
    private const double MinGap = AdaptiveGridLayout.DefaultMinGap;

    /// <summary>Non-zero pmin series (plan §10 intro: "плюс серия с pmin = 12") — proves the formulas
    /// are not hard-coded around <see cref="MinGap"/> == 0.</summary>
    private const double CustomGap = 12d;
    private static readonly double EdgeM = AdaptiveGridLayout.EdgePadding(Cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);
    private static readonly double EdgeMv = AdaptiveGridLayout.EdgePadding(RowCell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    // ----- EdgePadding -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.EdgePadding(double, double)"/> returns half of the
    /// cell size when the ratio is 0.5.
    /// </summary>
    [Fact]
    public void EdgePadding_Cell84_Ratio0_5_Returns42()
    {
        Assert.Equal(42d, AdaptiveGridLayout.EdgePadding(84, 0.5));
    }

    // ----- RowCapacity -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> returns
    /// exactly <c>k</c> when the available width equals the exact-fit extent for <c>k</c> columns.
    /// </summary>
    [Fact]
    public void RowCapacity_ExactlyExtentOfK_ReturnsK()
    {
        for (var k = 1; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM);
            Assert.Equal(k, AdaptiveGridLayout.RowCapacity(l, Cell, MinGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> drops
    /// to <c>k - 1</c> when the available width is one pixel below the exact-fit extent for <c>k</c>.
    /// </summary>
    [Fact]
    public void RowCapacity_OnePixelBelowExtentOfK_ReturnsKMinusOne()
    {
        for (var k = 2; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM) - 1d;
            Assert.Equal(k - 1, AdaptiveGridLayout.RowCapacity(l, Cell, MinGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> still
    /// returns <c>k</c> when the available width is up to one pixel short of fitting an extra column.
    /// </summary>
    [Fact]
    public void RowCapacity_ExtentOfKPlusCellMinusOne_ReturnsK()
    {
        for (var k = 1; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM) + Cell - 1d;
            Assert.Equal(k, AdaptiveGridLayout.RowCapacity(l, Cell, MinGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> clamps
    /// to a minimum of one column even when the available width is less than two edge paddings plus one
    /// cell.
    /// </summary>
    [Fact]
    public void RowCapacity_LessThanTwoEdgesPlusOneCell_ClampsToOne()
    {
        var l = (2d * EdgeM) + Cell - 1d;
        Assert.Equal(1, AdaptiveGridLayout.RowCapacity(l, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> clamps
    /// to one column, without throwing, for degenerate zero/negative available width or cell size inputs.
    /// </summary>
    /// <param name="available">The available width passed to <c>RowCapacity</c>.</param>
    /// <param name="cell">The cell size passed to <c>RowCapacity</c>.</param>
    [Theory]
    [InlineData(0d, 84d)]
    [InlineData(-100d, 84d)]
    [InlineData(500d, 0d)]
    [InlineData(500d, -10d)]
    public void RowCapacity_DegenerateInputs_ReturnsOneWithoutThrowing(double available, double cell)
    {
        var result = AdaptiveGridLayout.RowCapacity(available, cell, MinGap, EdgeM);
        Assert.Equal(1, result);
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> clamps
    /// to one column, without throwing, when the available width is <see cref="double.NaN"/>.
    /// </summary>
    [Fact]
    public void RowCapacity_NaNAvailable_ReturnsOneWithoutThrowing()
    {
        Assert.Equal(1, AdaptiveGridLayout.RowCapacity(double.NaN, Cell, MinGap, EdgeM));
    }

    // ----- RowCapacity sub-pixel tolerance (finalization MAJOR M-1) -----
    //
    // Real Win32 window widths are integer device pixels, but the cell size (IconSize-driven,
    // fractional if the slider isn't tick-snapped) and DPI/layout rounding both make the *actual*
    // available width fall a sub-pixel amount short of the analytical Extent(k) computed for the same
    // k. Without AdaptiveGridLayout.LayoutTolerance that shortfall silently drops a whole column. These
    // tests pin the tolerance window: anything up to (but not including) 1px short of Extent(k) must
    // still report k; a full 1px short (or more) must still correctly drop to k-1.

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> still
    /// returns <c>k</c> when the available width falls short of <c>Extent(k)</c> by less than one pixel
    /// (within <c>LayoutTolerance</c>).
    /// </summary>
    /// <param name="shortfall">The sub-pixel amount subtracted from the exact-fit extent.</param>
    [Theory]
    [InlineData(0.1d)]
    [InlineData(0.4d)]
    [InlineData(0.49d)]
    public void RowCapacity_SubPixelShortfallWithinTolerance_StillReturnsK(double shortfall)
    {
        for (var k = 2; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM) - shortfall;
            Assert.Equal(k, AdaptiveGridLayout.RowCapacity(l, Cell, MinGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> drops
    /// to <c>k - 1</c> once the shortfall from <c>Extent(k)</c> reaches or exceeds one pixel (outside
    /// <c>LayoutTolerance</c>).
    /// </summary>
    /// <param name="shortfall">The amount subtracted from the exact-fit extent.</param>
    [Theory]
    [InlineData(0.51d)]
    [InlineData(0.9d)]
    [InlineData(1d)]
    public void RowCapacity_ShortfallAtOrBeyondTolerance_ReturnsKMinusOne(double shortfall)
    {
        for (var k = 2; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM) - shortfall;
            Assert.Equal(k - 1, AdaptiveGridLayout.RowCapacity(l, Cell, MinGap, EdgeM));
        }
    }

    // ----- RowGap / full & partial rows -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double)"/>
    /// stretches the gap of a full row to absorb all leftover space beyond the exact-fit extent.
    /// </summary>
    [Fact]
    public void RowGap_FullRow_StretchesToFillLeftoverSpace()
    {
        const int n = 3;
        var l = AdaptiveGridLayout.Extent(n, Cell, MinGap, EdgeM) + 20d; // extra slack beyond exact fit
        var gap = AdaptiveGridLayout.RowGap(n, n, l, Cell, MinGap, EdgeM);

        var expected = (l - (2d * EdgeM) - (Cell * n)) / (n - 1);
        Assert.Equal(expected, gap, 1e-9);
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double)"/>
    /// returns <c>MinGap</c> for a full row whose available width is an exact fit (no leftover slack).
    /// </summary>
    [Fact]
    public void RowGap_FullRow_ExactFit_ReturnsMinGap()
    {
        const int n = 3;
        var l = AdaptiveGridLayout.Extent(n, Cell, MinGap, EdgeM);
        var gap = AdaptiveGridLayout.RowGap(n, n, l, Cell, MinGap, EdgeM);

        Assert.Equal(MinGap, gap);
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double)"/>
    /// never returns a gap below the configured minimum gap for a full row, even with a custom
    /// non-default <c>MinGap</c>.
    /// </summary>
    [Fact]
    public void RowGap_FullRow_NeverBelowMinGap()
    {
        const int n = 3;
        const double customMinGap = 12d;
        var l = AdaptiveGridLayout.Extent(n, Cell, customMinGap, EdgeM);
        var gap = AdaptiveGridLayout.RowGap(n, n, l, Cell, customMinGap, EdgeM);

        Assert.True(gap >= customMinGap);
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double)"/>
    /// always returns <c>MinGap</c> for a partial (non-full) row, regardless of available slack.
    /// </summary>
    [Fact]
    public void RowGap_PartialRow_ReturnsMinGap()
    {
        var gap = AdaptiveGridLayout.RowGap(2, 4, 1000d, Cell, MinGap, EdgeM);
        Assert.Equal(MinGap, gap);
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double)"/>
    /// returns <c>MinGap</c> for a row with a single item at full row capacity.
    /// </summary>
    [Fact]
    public void RowGap_SingleItem_ReturnsMinGap()
    {
        var gap = AdaptiveGridLayout.RowGap(1, 1, 1000d, Cell, MinGap, EdgeM);
        Assert.Equal(MinGap, gap);
    }

    // ----- Offset / Extent -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Offset(int, double, double, double)"/> places the
    /// first item's origin exactly at the edge padding.
    /// </summary>
    [Fact]
    public void Offset_FirstItem_EqualsEdgePadding()
    {
        Assert.Equal(EdgeM, AdaptiveGridLayout.Offset(0, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies that successive <see cref="AdaptiveGridLayout.Offset(int, double, double, double)"/>
    /// results are strictly increasing and leave no overlap between adjacent cells.
    /// </summary>
    [Fact]
    public void Offset_IsMonotonicAndNonOverlapping()
    {
        const double gap = 10d;
        for (var j = 0; j < 5; j++)
        {
            var left = AdaptiveGridLayout.Offset(j, Cell, gap, EdgeM);
            var nextLeft = AdaptiveGridLayout.Offset(j + 1, Cell, gap, EdgeM);
            Assert.True(left + Cell <= nextLeft + 1e-9);
        }
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/> returns zero
    /// for an item count of zero.
    /// </summary>
    [Fact]
    public void Extent_ZeroCount_ReturnsZero()
    {
        Assert.Equal(0d, AdaptiveGridLayout.Extent(0, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/> matches the
    /// closed-form extent formula for several item counts.
    /// </summary>
    /// <param name="count">The item count passed to <c>Extent</c>.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Extent_MatchesFormula(int count)
    {
        var expected = (2d * EdgeM) + (count * Cell) + ((count - 1) * MinGap);
        Assert.Equal(expected, AdaptiveGridLayout.Extent(count, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies the algebraic property that, with a zero gap and the default 0.5 edge-padding ratio,
    /// <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/> equals <c>Cell * (k + 1)</c>.
    /// </summary>
    /// <param name="k">The item count passed to <c>Extent</c>.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Extent_WithDefaultRatioAndZeroGap_IsCellMultiple(int k)
    {
        // Algebraic property of the formula for the specific case of a zero gap: Extent(k) == x*(k+1)
        // when pmin=0, ratio=0.5. Deliberately passes a literal 0d rather than the MinGap field — this
        // is independent of whatever AdaptiveGridLayout.DefaultMinGap currently is (it moved to 4 as of
        // polishing/ui-1; see the constant's doc comment for why 2+ column/row snap points are no
        // longer guaranteed to be CellSize multiples at the new default).
        var extent = AdaptiveGridLayout.Extent(k, Cell, 0d, EdgeM);
        Assert.Equal(Cell * (k + 1), extent);
    }

    // ----- MinPanelWidth (polishing/ui-1): Extent(1) == 2 * CellSize(iconSize), any gap -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/> for a single
    /// item equals twice the cell size, independent of the gap value (the gap term's multiplier is zero
    /// when count == 1), which <c>GridMetrics.MinPanelWidth</c> relies on.
    /// </summary>
    /// <param name="iconSize">The icon size used to derive the cell size under test.</param>
    [Theory]
    [InlineData(16d)]
    [InlineData(48d)]
    [InlineData(96d)]
    public void Extent_SingleCell_EqualsTwiceCellSize_IndependentOfGap(double iconSize)
    {
        // GridMetrics.MinPanelWidth relies on this: Extent(1, cell, gap, cell*0.5) == 2*cell for ANY
        // gap, because Extent's gap term is multiplied by (count - 1) == 0 when count == 1. Verified
        // explicitly across a spread of gap values — including the live AdaptiveGridLayout.DefaultMinGap
        // constant itself, rather than a hard-coded copy of its current value (finalization m-5/m-6) —
        // so a future change to DefaultMinGap can't silently break the MinPanelWidth formula's
        // independence from the gap without this test noticing.
        var cell = GridMetrics.CellSize(iconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

        foreach (var gap in new[] { 0d, AdaptiveGridLayout.DefaultMinGap, 4d, CustomGap })
        {
            var extent = AdaptiveGridLayout.Extent(1, cell, gap, edgePadding);
            Assert.Equal(2d * cell, extent, 1e-9);
        }
    }

    // ----- Vertical axis (Offset/Extent are axis-agnostic) -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Offset(int, double, double, double)"/> places the
    /// first row's origin exactly at the vertical edge padding.
    /// </summary>
    [Fact]
    public void Offset_Vertical_FirstRow_EqualsVerticalEdgePadding()
    {
        Assert.Equal(EdgeMv, AdaptiveGridLayout.Offset(0, RowCell, MinGap, EdgeMv));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/> matches the
    /// closed-form extent formula on the vertical (row) axis.
    /// </summary>
    /// <param name="rows">The row count passed to <c>Extent</c>.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Extent_Vertical_MatchesFormula(int rows)
    {
        var expected = (2d * EdgeMv) + (rows * RowCell) + ((rows - 1) * MinGap);
        Assert.Equal(expected, AdaptiveGridLayout.Extent(rows, RowCell, MinGap, EdgeMv));
    }

    // NOTE (finalization MINOR m-5/m-7): a former "Extent_Vertical_DoesNotStretchWithViewport" test
    // lived here. It called AdaptiveGridLayout.Extent(rows, RowCell, MinGap, EdgeMv) twice with
    // identical arguments and asserted the two results were equal — a pure-function-with-no-viewport-
    // parameter tautology that can never fail regardless of the implementation. The actual contract
    // ("vertical extent never stretches with available viewport space") is structural: Extent's
    // signature has no viewport parameter at all, so there is nothing left to regress-test here beyond
    // what Extent_Vertical_MatchesFormula above already covers. Removed rather than "fixed" — there is
    // no runtime behavior of this class left to assert.

    // ----- RowCount / ItemsInRow (wrap) -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCount(int, int)"/> returns zero for an item count
    /// of zero.
    /// </summary>
    [Fact]
    public void RowCount_ZeroItems_ReturnsZero()
    {
        Assert.Equal(0, AdaptiveGridLayout.RowCount(0, 4));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCount(int, int)"/> returns zero for a negative
    /// item count.
    /// </summary>
    [Fact]
    public void RowCount_NegativeItemCount_ReturnsZero()
    {
        Assert.Equal(0, AdaptiveGridLayout.RowCount(-3, 4));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.ItemsInRow(int, int, int)"/> returns zero for a row
    /// index outside the range of rows actually produced by the layout.
    /// </summary>
    /// <param name="rowIndex">The out-of-range row index passed to <c>ItemsInRow</c>.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(100)]
    public void ItemsInRow_OutOfRangeRowIndex_ReturnsZero(int rowIndex)
    {
        // capacity=4, itemCount=5 => exactly 2 rows (indices 0 and 1); anything else is out of range.
        Assert.Equal(0, AdaptiveGridLayout.ItemsInRow(5, 4, rowIndex));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCount(int, int)"/> returns one when the item count
    /// exactly equals the row capacity.
    /// </summary>
    [Fact]
    public void RowCount_ExactlyOneCapacity_ReturnsOne()
    {
        Assert.Equal(1, AdaptiveGridLayout.RowCount(4, 4));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCount(int, int)"/> wraps into two rows, with
    /// <see cref="AdaptiveGridLayout.ItemsInRow(int, int, int)"/> splitting a full first row and a
    /// single-item second row, when the item count is one more than capacity.
    /// </summary>
    [Fact]
    public void RowCount_OneMoreThanCapacity_ReturnsTwoWithCorrectSplit()
    {
        const int capacity = 4;
        Assert.Equal(2, AdaptiveGridLayout.RowCount(capacity + 1, capacity));
        Assert.Equal(capacity, AdaptiveGridLayout.ItemsInRow(capacity + 1, capacity, 0));
        Assert.Equal(1, AdaptiveGridLayout.ItemsInRow(capacity + 1, capacity, 1));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.RowCount(int, int)"/> and
    /// <see cref="AdaptiveGridLayout.ItemsInRow(int, int, int)"/> agree that both rows are full when the
    /// item count is exactly twice the row capacity.
    /// </summary>
    [Fact]
    public void RowCount_TwoFullRows_BothRowsFull()
    {
        const int capacity = 4;
        Assert.Equal(2, AdaptiveGridLayout.RowCount(capacity * 2, capacity));
        Assert.Equal(capacity, AdaptiveGridLayout.ItemsInRow(capacity * 2, capacity, 0));
        Assert.Equal(capacity, AdaptiveGridLayout.ItemsInRow(capacity * 2, capacity, 1));
    }

    // ----- SnapExtent / CountForExtent -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> is
    /// idempotent when the input is already an exact-fit extent for <c>k</c> columns.
    /// </summary>
    /// <param name="k">The column count whose exact-fit extent is snapped.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SnapExtent_OnExactExtent_IsIdempotent(int k)
    {
        var extent = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM);
        Assert.Equal(extent, AdaptiveGridLayout.SnapExtent(extent, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> rounds
    /// down to the current column count's extent below the midpoint step and up to the next column
    /// count's extent above it.
    /// </summary>
    [Fact]
    public void SnapExtent_RoundsToNearestColumnCount()
    {
        const int k = 3;
        var baseExtent = AdaptiveGridLayout.Extent(k, Cell, MinGap, EdgeM);
        var step = Cell + MinGap;

        var below = baseExtent + (step * 0.4);
        var above = baseExtent + (step * 0.6);

        Assert.Equal(baseExtent, AdaptiveGridLayout.SnapExtent(below, Cell, MinGap, EdgeM));
        Assert.Equal(AdaptiveGridLayout.Extent(k + 1, Cell, MinGap, EdgeM), AdaptiveGridLayout.SnapExtent(above, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> snaps
    /// zero or negative extents up to the extent of a single cell.
    /// </summary>
    [Fact]
    public void SnapExtent_ZeroOrNegative_SnapsToOneCell()
    {
        var extentOfOne = AdaptiveGridLayout.Extent(1, Cell, MinGap, EdgeM);

        Assert.Equal(extentOfOne, AdaptiveGridLayout.SnapExtent(0, Cell, MinGap, EdgeM));
        Assert.Equal(extentOfOne, AdaptiveGridLayout.SnapExtent(-50, Cell, MinGap, EdgeM));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.CountForExtent(double, double, double, double)"/>
    /// clamps to one when <c>cell + gap</c> is non-positive, rather than dividing by a zero or negative
    /// denominator.
    /// </summary>
    /// <param name="cellPlusGap">The degenerate cell-plus-gap value passed to <c>CountForExtent</c>.</param>
    [Theory]
    [InlineData(0d)]
    [InlineData(-10d)]
    public void CountForExtent_CellPlusGapNonPositive_ReturnsOne(double cellPlusGap)
    {
        // cell + gap <= 0 means the denominator is degenerate; must clamp to 1 rather than divide by
        // zero/a negative number or return something <= 0.
        Assert.Equal(1, AdaptiveGridLayout.CountForExtent(500d, cellPlusGap, 0d, EdgeM));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.CountForExtent(double, double, double, double)"/>
    /// yields the same column count before and after routing an extent through
    /// <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/>.
    /// </summary>
    [Fact]
    public void CountForExtent_RoundTripsThroughSnapExtent()
    {
        double[] samples = [10d, 84d, 130d, 336d, 1000d];
        foreach (var e in samples)
        {
            var direct = AdaptiveGridLayout.CountForExtent(e, Cell, MinGap, EdgeM);
            var afterSnap = AdaptiveGridLayout.CountForExtent(AdaptiveGridLayout.SnapExtent(e, Cell, MinGap, EdgeM), Cell, MinGap, EdgeM);
            Assert.Equal(direct, afterSnap);
        }
    }

    // ----- RowCapacity with non-zero pmin (plan §10 item 1/2, "серия с pmin = 12") -----

    /// <summary>
    /// Verifies that with a non-zero <c>MinGap</c> (<see cref="CustomGap"/>),
    /// <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> still returns
    /// exactly <c>k</c> when the available width equals the exact-fit extent for <c>k</c> columns.
    /// </summary>
    [Fact]
    public void RowCapacity_NonZeroMinGap_ExactlyExtentOfK_ReturnsK()
    {
        for (var k = 1; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, CustomGap, EdgeM);
            Assert.Equal(k, AdaptiveGridLayout.RowCapacity(l, Cell, CustomGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that with a non-zero <c>MinGap</c> (<see cref="CustomGap"/>),
    /// <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> drops to <c>k - 1</c>
    /// when the available width is one pixel below the exact-fit extent for <c>k</c>.
    /// </summary>
    [Fact]
    public void RowCapacity_NonZeroMinGap_OnePixelBelowExtentOfK_ReturnsKMinusOne()
    {
        for (var k = 2; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, CustomGap, EdgeM) - 1d;
            Assert.Equal(k - 1, AdaptiveGridLayout.RowCapacity(l, Cell, CustomGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that with a non-zero <c>MinGap</c> (<see cref="CustomGap"/>),
    /// <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> still returns
    /// <c>k</c> when the available width is up to one pixel short of fitting an extra column.
    /// </summary>
    [Fact]
    public void RowCapacity_NonZeroMinGap_ExtentOfKPlusCellMinusOne_ReturnsK()
    {
        for (var k = 1; k <= 4; k++)
        {
            var l = AdaptiveGridLayout.Extent(k, Cell, CustomGap, EdgeM) + Cell - 1d;
            Assert.Equal(k, AdaptiveGridLayout.RowCapacity(l, Cell, CustomGap, EdgeM));
        }
    }

    /// <summary>
    /// Verifies that with a non-zero <c>MinGap</c> (<see cref="CustomGap"/>),
    /// <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> still clamps to a
    /// minimum of one column when the available width is less than two edge paddings plus one cell.
    /// </summary>
    [Fact]
    public void RowCapacity_NonZeroMinGap_LessThanTwoEdgesPlusOneCell_ClampsToOne()
    {
        var l = (2d * EdgeM) + Cell - 1d;
        Assert.Equal(1, AdaptiveGridLayout.RowCapacity(l, Cell, CustomGap, EdgeM));
    }

    // ----- Full row: right edge of last element (plan §10 item 4) -----

    /// <summary>
    /// Verifies that a stretched full row's last item's right edge lands exactly at
    /// <c>available - EdgePadding</c>, for both zero and non-zero base gaps.
    /// </summary>
    /// <param name="gap">The base gap fed into <c>Extent</c>/<c>RowGap</c> before stretching.</param>
    [Theory]
    [InlineData(0d)]
    [InlineData(12d)]
    public void RowGap_FullRow_LastElementRightEdge_MatchesAvailableMinusEdgePadding(double gap)
    {
        const int n = 3;
        var l = AdaptiveGridLayout.Extent(n, Cell, gap, EdgeM) + 20d;
        var p = AdaptiveGridLayout.RowGap(n, n, l, Cell, gap, EdgeM);
        var lastRightEdge = AdaptiveGridLayout.Offset(n - 1, Cell, p, EdgeM) + Cell;

        Assert.Equal(l - EdgeM, lastRightEdge, 1e-9);
    }

    // ----- Full row: p >= pmin invariant across slack values (plan §10 item 6) -----

    /// <summary>
    /// Verifies the invariant that a full row's stretched gap never falls below the configured base
    /// gap, across a matrix of base gaps and extra slack amounts.
    /// </summary>
    /// <param name="gap">The base gap fed into <c>Extent</c>/<c>RowGap</c>.</param>
    /// <param name="extraSlack">Extra width added beyond the row's exact-fit extent.</param>
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0d, 37d)]
    [InlineData(12d, 0d)]
    [InlineData(12d, 53d)]
    public void RowGap_FullRow_NeverBelowMinGap_AcrossSlackValues(double gap, double extraSlack)
    {
        const int n = 4;
        var l = AdaptiveGridLayout.Extent(n, Cell, gap, EdgeM) + extraSlack;
        var p = AdaptiveGridLayout.RowGap(n, n, l, Cell, gap, EdgeM);

        Assert.True(p >= gap);
    }

    // ----- Partial row: gap == pmin, first offset == m, positive tail (plan §10 item 7) -----

    /// <summary>
    /// Verifies that a partial row keeps <c>gap == MinGap</c>, starts its first item at the edge
    /// padding, and leaves a positive tail of unused space, for both zero and non-zero base gaps.
    /// </summary>
    /// <param name="gap">The base gap fed into <c>Extent</c>/<c>RowGap</c>.</param>
    [Theory]
    [InlineData(0d)]
    [InlineData(12d)]
    public void RowGap_PartialRow_TailIsPositiveAndOffsetsMatchSpec(double gap)
    {
        const int rowCapacity = 4;
        const int n = 2; // n < k_max
        var l = AdaptiveGridLayout.Extent(rowCapacity, Cell, gap, EdgeM) + 40d; // slack, k_max stays 4

        var p = AdaptiveGridLayout.RowGap(n, rowCapacity, l, Cell, gap, EdgeM);
        var firstOffset = AdaptiveGridLayout.Offset(0, Cell, p, EdgeM);
        var lastRightEdge = AdaptiveGridLayout.Offset(n - 1, Cell, p, EdgeM) + Cell;

        Assert.Equal(gap, p);
        Assert.Equal(EdgeM, firstOffset);
        Assert.True(l - EdgeM - lastRightEdge > 0d);
    }

    // ----- n == 1 in a row that is NOT at capacity (plan §10 item 8, partial-capacity case) -----

    /// <summary>
    /// Verifies that a lone item in a row whose capacity is greater than one still gets
    /// <c>MinGap</c> and an offset equal to the edge padding.
    /// </summary>
    [Fact]
    public void RowGap_SingleItem_InRowOfLargerCapacity_StillReturnsMinGapAndOffsetEqualsEdgePadding()
    {
        var p = AdaptiveGridLayout.RowGap(1, 4, 1000d, Cell, MinGap, EdgeM);
        var offset = AdaptiveGridLayout.Offset(0, Cell, p, EdgeM);

        Assert.Equal(MinGap, p);
        Assert.Equal(EdgeM, offset);
    }

    // ----- Vertical Offset general formula (plan §10 item 11) -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Offset(int, double, double, double)"/> on the
    /// vertical axis matches the general offset formula at the default <c>MinGap</c>.
    /// </summary>
    /// <param name="i">The row index passed to <c>Offset</c>.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Offset_Vertical_MatchesGeneralFormula(int i)
    {
        var expected = EdgeMv + (i * (RowCell + MinGap));
        Assert.Equal(expected, AdaptiveGridLayout.Offset(i, RowCell, MinGap, EdgeMv));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.Offset(int, double, double, double)"/> on the
    /// vertical axis matches the general offset formula with a non-zero gap.
    /// </summary>
    /// <param name="i">The row index passed to <c>Offset</c>.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Offset_Vertical_NonZeroGap_MatchesGeneralFormula(int i)
    {
        const double gap = 12d;
        var expected = EdgeMv + (i * (RowCell + gap));
        Assert.Equal(expected, AdaptiveGridLayout.Offset(i, RowCell, gap, EdgeMv));
    }

    // ----- Scroll threshold contract (plan §10 item 14) -----
    //
    // NOTE (finalization MINOR m-5/m-7): three tests previously lived here
    // ("ViewportExactlyMatchesExtent_NoScrollNeeded", "ViewportOnePixelSmaller_ScrollNeeded",
    // "ViewportMuchLarger_ExtentUnchangedNoStretch"), each computing h = Extent(...) and then asserting
    // an inequality between h and a viewport value derived from h itself (h <= h, h > h-1, h*3 > h).
    // Those are arithmetic tautologies over plain doubles — they exercise no AdaptiveGridLayout code
    // path involving an actual viewport (Extent has no viewport parameter) and could never turn red for
    // any implementation. The real "does a scrollbar appear" decision is made by the enclosing
    // ScrollViewer comparing its own ExtentHeight/ViewportHeight at runtime (WPF layout), which is not
    // something this pure-function test class can exercise — that threshold is manual-verify
    // (Win10/Win11), not a unit test. Removed rather than "fixed".

    // ----- SnapExtent/CountForExtent, vertical axis (plan §10 items 16-19) -----

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> is
    /// idempotent on the vertical axis when the input is already an exact-fit extent for <c>r</c> rows.
    /// </summary>
    /// <param name="r">The row count whose exact-fit extent is snapped.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SnapExtent_Vertical_OnExactExtent_IsIdempotent(int r)
    {
        var extent = AdaptiveGridLayout.Extent(r, RowCell, MinGap, EdgeMv);
        Assert.Equal(extent, AdaptiveGridLayout.SnapExtent(extent, RowCell, MinGap, EdgeMv));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> rounds
    /// down to the current row count's extent below the midpoint step and up to the next row count's
    /// extent above it, on the vertical axis.
    /// </summary>
    [Fact]
    public void SnapExtent_Vertical_RoundsToNearestRowCount()
    {
        const int r = 3;
        var baseExtent = AdaptiveGridLayout.Extent(r, RowCell, MinGap, EdgeMv);
        var step = RowCell + MinGap;

        var below = baseExtent + (step * 0.4);
        var above = baseExtent + (step * 0.6);

        Assert.Equal(baseExtent, AdaptiveGridLayout.SnapExtent(below, RowCell, MinGap, EdgeMv));
        Assert.Equal(AdaptiveGridLayout.Extent(r + 1, RowCell, MinGap, EdgeMv), AdaptiveGridLayout.SnapExtent(above, RowCell, MinGap, EdgeMv));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> snaps
    /// zero or negative extents up to the extent of a single row.
    /// </summary>
    [Fact]
    public void SnapExtent_Vertical_ZeroOrNegative_SnapsToOneRow()
    {
        var extentOfOne = AdaptiveGridLayout.Extent(1, RowCell, MinGap, EdgeMv);

        Assert.Equal(extentOfOne, AdaptiveGridLayout.SnapExtent(0, RowCell, MinGap, EdgeMv));
        Assert.Equal(extentOfOne, AdaptiveGridLayout.SnapExtent(-50, RowCell, MinGap, EdgeMv));
    }

    /// <summary>
    /// Verifies that <see cref="AdaptiveGridLayout.CountForExtent(double, double, double, double)"/>
    /// yields the same row count before and after routing an extent through
    /// <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/>, on the vertical axis.
    /// </summary>
    [Fact]
    public void CountForExtent_Vertical_RoundTripsThroughSnapExtent()
    {
        double[] samples = [10d, 92d, 150d, 368d, 1000d];
        foreach (var e in samples)
        {
            var direct = AdaptiveGridLayout.CountForExtent(e, RowCell, MinGap, EdgeMv);
            var afterSnap = AdaptiveGridLayout.CountForExtent(AdaptiveGridLayout.SnapExtent(e, RowCell, MinGap, EdgeMv), RowCell, MinGap, EdgeMv);
            Assert.Equal(direct, afterSnap);
        }
    }

    // ----- SnapExtent with non-zero pmin (plan §10 intro: "серия с pmin = 12") -----

    /// <summary>
    /// Verifies that with a non-zero <c>MinGap</c> (<see cref="CustomGap"/>),
    /// <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> still rounds down
    /// below the midpoint step and up above it.
    /// </summary>
    [Fact]
    public void SnapExtent_NonZeroMinGap_RoundsToNearestColumnCount()
    {
        const int k = 3;
        var baseExtent = AdaptiveGridLayout.Extent(k, Cell, CustomGap, EdgeM);
        var step = Cell + CustomGap;

        var below = baseExtent + (step * 0.4);
        var above = baseExtent + (step * 0.6);

        Assert.Equal(baseExtent, AdaptiveGridLayout.SnapExtent(below, Cell, CustomGap, EdgeM));
        Assert.Equal(AdaptiveGridLayout.Extent(k + 1, Cell, CustomGap, EdgeM), AdaptiveGridLayout.SnapExtent(above, Cell, CustomGap, EdgeM));
    }

    /// <summary>
    /// Verifies that with a non-zero <c>MinGap</c> (<see cref="CustomGap"/>),
    /// <see cref="AdaptiveGridLayout.SnapExtent(double, double, double, double)"/> is still idempotent
    /// when the input is already an exact-fit extent for <c>k</c> columns.
    /// </summary>
    /// <param name="k">The column count whose exact-fit extent is snapped.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SnapExtent_NonZeroMinGap_OnExactExtent_IsIdempotent(int k)
    {
        var extent = AdaptiveGridLayout.Extent(k, Cell, CustomGap, EdgeM);
        Assert.Equal(extent, AdaptiveGridLayout.SnapExtent(extent, Cell, CustomGap, EdgeM));
    }
}
