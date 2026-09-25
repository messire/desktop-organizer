using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelContentSizer"/> (ADR-029 Решение 7, P06 —
/// <c>docs/fix-prompts/06-fit-content.md</c>). Every expected value below is computed by calling the
/// SAME lower-level formulas (<see cref="GridMetrics"/>/<see cref="AdaptiveGridLayout"/>/
/// <see cref="WorkspaceLayoutPolicy"/>) the type under test itself calls — never a hand-typed magic
/// number — the same "formula, not magic number" convention already used by
/// <c>AdaptiveGridLayoutIconLayoutPolicyTests</c>/<c>ContentLeftInsetRowCapacityRegressionTests</c>.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelContentSizerTests
{
    private const double IconSize = 48d;
    private const double NoScrollBar = 0d;
    private const double ScrollBarWidth = 17d; // a plausible SystemParameters.VerticalScrollBarWidth value.

    // ----- ResolveColumnCapacity -----

    /// <summary>
    /// Verifies that a window sized for exactly three columns resolves a column capacity of 3, for
    /// both icon layout policies.
    /// </summary>
    /// <param name="policy">The icon layout policy under test.</param>
    [Theory]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    [InlineData(IconLayoutPolicy.FixedGap)]
    public void ResolveColumnCapacity_ExactThreeColumnWidth_Returns3(IconLayoutPolicy policy)
    {
        var windowWidth = ThreeColumnWindowWidth(policy, NoScrollBar);

        var capacity = PanelContentSizer.ResolveColumnCapacity(windowWidth, IconSize, policy, NoScrollBar);

        Assert.Equal(3, capacity);
    }

    /// <summary>
    /// Reproduces ADR-029's F-3/M-2 chrome accounting: for the SAME window width, a <see cref="IconLayoutPolicy.FixedGap"/>
    /// panel with a non-zero reserved scrollbar width loses exactly one column relative to
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> (which never reserves it) — proving this type reuses
    /// <see cref="GridMetrics.ContentHorizontalChrome"/> rather than the bare
    /// <see cref="PanelMetrics.ContentLeftInset"/> ADR-029's original Решение 7 draft specified.
    /// </summary>
    [Fact]
    public void ResolveColumnCapacity_FixedGap_ReservesScrollBarWidth_UnlikeLegacyStretch()
    {
        var windowWidth = ThreeColumnWindowWidth(IconLayoutPolicy.FixedGap, NoScrollBar);

        var legacyCapacity = PanelContentSizer.ResolveColumnCapacity(windowWidth, IconSize, IconLayoutPolicy.LegacyStretch, ScrollBarWidth);
        var fixedGapCapacity = PanelContentSizer.ResolveColumnCapacity(windowWidth, IconSize, IconLayoutPolicy.FixedGap, ScrollBarWidth);

        Assert.Equal(3, legacyCapacity);
        Assert.Equal(2, fixedGapCapacity);
    }

    /// <summary>
    /// Verifies that a degenerate (zero) window width still resolves to a column capacity of at
    /// least one, never zero.
    /// </summary>
    [Fact]
    public void ResolveColumnCapacity_NeverBelowOne_ForDegenerateWidth()
    {
        var capacity = PanelContentSizer.ResolveColumnCapacity(0d, IconSize, IconLayoutPolicy.FixedGap, NoScrollBar);

        Assert.Equal(1, capacity);
    }

    // ----- ResolveDisplayRowCount -----

    /// <summary>
    /// Verifies that zero items still resolve to one displayed row (the drop-zone floor), contrasting
    /// it explicitly with <see cref="AdaptiveGridLayout.RowCount"/>, which returns zero for zero items.
    /// </summary>
    [Fact]
    public void ResolveDisplayRowCount_ZeroItems_ReturnsOne_TheDropZoneFloor()
    {
        // Not a reuse of AdaptiveGridLayout.RowCount(0, k) == 0 in a different sense — see the method's
        // own doc comment. Pin both contracts side by side so a future edit can't quietly conflate them.
        Assert.Equal(0, AdaptiveGridLayout.RowCount(0, 3));
        Assert.Equal(1, PanelContentSizer.ResolveDisplayRowCount(0, 3));
    }

    /// <summary>
    /// Verifies that a single item always resolves to one displayed row, regardless of column capacity.
    /// </summary>
    [Fact]
    public void ResolveDisplayRowCount_OneItem_ReturnsOne_RegardlessOfCapacity()
    {
        Assert.Equal(1, PanelContentSizer.ResolveDisplayRowCount(1, 3));
    }

    /// <summary>
    /// Verifies that an item count exactly filling one row (equal to the column capacity) resolves
    /// to one displayed row.
    /// </summary>
    [Fact]
    public void ResolveDisplayRowCount_ExactlyOneFullRow_ReturnsOne()
    {
        Assert.Equal(1, PanelContentSizer.ResolveDisplayRowCount(3, 3));
    }

    /// <summary>
    /// Verifies that one item beyond the column capacity spills into a second displayed row.
    /// </summary>
    [Fact]
    public void ResolveDisplayRowCount_OneMoreThanCapacity_ReturnsTwo()
    {
        Assert.Equal(2, PanelContentSizer.ResolveDisplayRowCount(4, 3));
    }

    // ----- ComputeRawContentHeight -----

    /// <summary>
    /// A single icon must reserve a FULL row (header/label included) — not some smaller "just the icon"
    /// footprint — so the name label is never clipped (DoD: "1 иконка не даёт высокий прямоугольник" AND
    /// "подписи не отрезаются").
    /// </summary>
    /// <param name="policy">The icon layout policy under test.</param>
    [Theory]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    [InlineData(IconLayoutPolicy.FixedGap)]
    public void ComputeRawContentHeight_OneItem_EqualsHeaderPlusPaddingPlusOneFullRow(IconLayoutPolicy policy)
    {
        var windowWidth = ThreeColumnWindowWidth(policy, NoScrollBar);
        var expected = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding + GridMetrics.RowHeight(IconSize);

        var raw = PanelContentSizer.ComputeRawContentHeight(1, IconSize, windowWidth, policy, NoScrollBar);

        Assert.Equal(expected, raw);
    }

    /// <summary>
    /// An EMPTY panel reserves the SAME single-row height as a one-item panel (the drop-zone floor is
    /// exactly one row, never zero and never more).
    /// </summary>
    /// <param name="policy">The icon layout policy under test.</param>
    [Theory]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    [InlineData(IconLayoutPolicy.FixedGap)]
    public void ComputeRawContentHeight_ZeroItems_EqualsOneItem_TheDropZoneFloor(IconLayoutPolicy policy)
    {
        var windowWidth = ThreeColumnWindowWidth(policy, NoScrollBar);

        var zero = PanelContentSizer.ComputeRawContentHeight(0, IconSize, windowWidth, policy, NoScrollBar);
        var one = PanelContentSizer.ComputeRawContentHeight(1, IconSize, windowWidth, policy, NoScrollBar);

        Assert.Equal(one, zero);
    }

    /// <summary>
    /// Two full rows (itemCount == 2 * columnCapacity) must reserve exactly twice the single-row content
    /// extent plus one vertical gap — <see cref="AdaptiveGridLayout.Extent"/>'s own formula, not a
    /// doubled single-row height (there is no top/bottom edge padding on the vertical axis —
    /// <see cref="AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio"/> == 0 — so this distinction is
    /// observable).
    /// </summary>
    [Fact]
    public void ComputeRawContentHeight_TwoFullRows_MatchesExtentFormula_NotDoubleTheOneRowHeight()
    {
        var windowWidth = ThreeColumnWindowWidth(IconLayoutPolicy.FixedGap, NoScrollBar);
        var rowHeight = GridMetrics.RowHeight(IconSize);
        var expectedContent = AdaptiveGridLayout.Extent(2, rowHeight, AdaptiveGridLayout.DefaultMinGap, 0d);
        var expected = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding + expectedContent;

        var raw = PanelContentSizer.ComputeRawContentHeight(6, IconSize, windowWidth, IconLayoutPolicy.FixedGap, NoScrollBar);

        Assert.Equal(expected, raw);
        Assert.NotEqual(PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding + (2 * rowHeight), raw);
    }

    /// <summary>
    /// Same item count/IconSize, same NOMINAL width, but the two policies disagree on
    /// <c>columnCapacity</c> once a scrollbar reservation is in play (see
    /// <see cref="ResolveColumnCapacity_FixedGap_ReservesScrollBarWidth_UnlikeLegacyStretch"/>) — so they
    /// may legitimately compute a DIFFERENT row count/height for the exact same inputs. This is expected,
    /// not a bug: DoD's "обе политики" coverage is about both being independently well-behaved, not about
    /// them agreeing numerically.
    /// </summary>
    [Fact]
    public void ComputeRawContentHeight_BothPolicies_AreIndependentlyWellDefined_ForTheSameInputs()
    {
        var windowWidth = ThreeColumnWindowWidth(IconLayoutPolicy.FixedGap, NoScrollBar);
        const int itemCount = 4; // one more than the FixedGap-with-scrollbar capacity of 2 -> 2 rows there.

        var legacyRaw = PanelContentSizer.ComputeRawContentHeight(itemCount, IconSize, windowWidth, IconLayoutPolicy.LegacyStretch, ScrollBarWidth);
        var fixedGapRaw = PanelContentSizer.ComputeRawContentHeight(itemCount, IconSize, windowWidth, IconLayoutPolicy.FixedGap, ScrollBarWidth);

        var rowHeight = GridMetrics.RowHeight(IconSize);

        // LegacyStretch: capacity 3 (unaffected by the scrollbar reservation) -> ceil(4/3) = 2 rows.
        // FixedGap: capacity 2 (reserves the scrollbar) -> ceil(4/2) = 2 rows. Same row count here by
        // coincidence of this particular itemCount — assert both independently via the same formula
        // rather than asserting they're equal (which would be a coincidence of this fixture, not a
        // contract) or different (same reason).
        var expectedLegacy = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding
            + AdaptiveGridLayout.Extent(AdaptiveGridLayout.RowCount(itemCount, 3), rowHeight, AdaptiveGridLayout.DefaultMinGap, 0d);
        var expectedFixedGap = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding
            + AdaptiveGridLayout.Extent(AdaptiveGridLayout.RowCount(itemCount, 2), rowHeight, AdaptiveGridLayout.DefaultMinGap, 0d);

        Assert.Equal(expectedLegacy, legacyRaw);
        Assert.Equal(expectedFixedGap, fixedGapRaw);
    }

    /// <summary>
    /// DPI-independence: this type takes only DIP <see langword="double"/>s and never reads any global/
    /// static DPI or <c>SystemParameters</c> state itself (the caller passes
    /// <c>verticalScrollBarWidth</c> in explicitly — see the class remarks) — so calling it
    /// twice with identical DIP inputs always yields an identical result, independent of anything else
    /// running on the machine (including whatever the real display's DPI scale happens to be).
    /// </summary>
    [Fact]
    public void ComputeRawContentHeight_IsPureAndDeterministic_AcrossRepeatedCalls()
    {
        var windowWidth = ThreeColumnWindowWidth(IconLayoutPolicy.FixedGap, ScrollBarWidth);

        var first = PanelContentSizer.ComputeRawContentHeight(7, IconSize, windowWidth, IconLayoutPolicy.FixedGap, ScrollBarWidth);
        var second = PanelContentSizer.ComputeRawContentHeight(7, IconSize, windowWidth, IconLayoutPolicy.FixedGap, ScrollBarWidth);

        Assert.Equal(first, second);
    }

    // ----- RoundForLegacyFreeform -----

    /// <summary>
    /// Verifies that a raw height is rounded up to the nearest whole DIP, and that an already-whole
    /// value is returned unchanged.
    /// </summary>
    [Fact]
    public void RoundForLegacyFreeform_RoundsUpToWholeDip()
    {
        Assert.Equal(101d, PanelContentSizer.RoundForLegacyFreeform(100.2d));
        Assert.Equal(100d, PanelContentSizer.RoundForLegacyFreeform(100.0d));
    }

    /// <summary>
    /// Verifies that a negative raw height clamps to zero instead of throwing or returning a
    /// negative result.
    /// </summary>
    [Fact]
    public void RoundForLegacyFreeform_NegativeInput_ClampsToZero_NeverThrows()
    {
        Assert.Equal(0d, PanelContentSizer.RoundForLegacyFreeform(-5d));
    }

    // ----- RoundHeightUnitsForGridAligned -----

    /// <summary>
    /// Verifies that rounding a mid-sized raw height to grid-aligned height units reproduces the same
    /// value <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/> would compute directly.
    /// </summary>
    [Fact]
    public void RoundHeightUnitsForGridAligned_MatchesBoundsToSpans_ForAMidSizedHeight()
    {
        const double unitSize = WorkspaceGridMetrics.UnitSize;
        const double gap = WorkspaceGridMetrics.DefaultGap;
        var raw = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding + GridMetrics.RowHeight(IconSize); // one-row fit height, well above the min span floor.
        var expected = WorkspaceLayoutPolicy.BoundsToSpans(new System.Windows.Size(0d, raw), unitSize, gap).HeightUnits;

        var actual = PanelContentSizer.RoundHeightUnitsForGridAligned(raw, unitSize, gap);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that a raw height far below the minimum still rounds up to
    /// <see cref="WorkspaceGridMetrics.MinHeightSpanExpanded"/>, never below it.
    /// </summary>
    [Fact]
    public void RoundHeightUnitsForGridAligned_NeverBelowMinHeightSpanExpanded()
    {
        var actual = PanelContentSizer.RoundHeightUnitsForGridAligned(1d);

        Assert.Equal(WorkspaceGridMetrics.MinHeightSpanExpanded, actual);
    }

    /// <summary>
    /// Verifies that rounding to height units, converting back to DIP bounds via
    /// <see cref="WorkspaceLayoutPolicy.SpansToBounds"/>, and rounding again produces the same
    /// height units — the rounding is idempotent across a bounds/spans round trip.
    /// </summary>
    [Fact]
    public void RoundHeightUnitsForGridAligned_IsIdempotent_WhenRoundTrippedThroughSpansToBounds()
    {
        const double unitSize = WorkspaceGridMetrics.UnitSize;
        const double gap = WorkspaceGridMetrics.DefaultGap;
        var raw = PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding
            + AdaptiveGridLayout.Extent(3, GridMetrics.RowHeight(IconSize), AdaptiveGridLayout.DefaultMinGap, 0d);

        var heightUnits = PanelContentSizer.RoundHeightUnitsForGridAligned(raw, unitSize, gap);
        var dip = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, heightUnits), unitSize, gap).Height;
        var heightUnitsAgain = PanelContentSizer.RoundHeightUnitsForGridAligned(dip, unitSize, gap);

        Assert.Equal(heightUnits, heightUnitsAgain);
    }

    // ----- FitHeight (full pipeline, both geometries, both policies) -----

    /// <summary>
    /// Verifies that in <see cref="PanelGeometryMode.LegacyFreeform"/> mode, the fit height equals the
    /// ceiling of the raw content height, for both icon layout policies.
    /// </summary>
    /// <param name="policy">The icon layout policy under test.</param>
    [Theory]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    [InlineData(IconLayoutPolicy.FixedGap)]
    public void FitHeight_LegacyFreeform_EqualsCeilingOfRawHeight(IconLayoutPolicy policy)
    {
        var windowWidth = ThreeColumnWindowWidth(policy, NoScrollBar);
        var raw = PanelContentSizer.ComputeRawContentHeight(5, IconSize, windowWidth, policy, NoScrollBar);

        var fitHeight = PanelContentSizer.FitHeight(5, IconSize, windowWidth, policy, NoScrollBar, PanelGeometryMode.LegacyFreeform);

        Assert.Equal(Math.Ceiling(raw), fitHeight);
    }

    /// <summary>
    /// Verifies that in <see cref="PanelGeometryMode.GridAligned"/> mode, the fit height equals
    /// <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> applied to the rounded height units, for
    /// both icon layout policies.
    /// </summary>
    /// <param name="policy">The icon layout policy under test.</param>
    [Theory]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    [InlineData(IconLayoutPolicy.FixedGap)]
    public void FitHeight_GridAligned_EqualsSpansToBoundsOfRoundedHeightUnits(IconLayoutPolicy policy)
    {
        const double unitSize = WorkspaceGridMetrics.UnitSize;
        const double gap = WorkspaceGridMetrics.DefaultGap;
        var windowWidth = ThreeColumnWindowWidth(policy, NoScrollBar);
        var raw = PanelContentSizer.ComputeRawContentHeight(5, IconSize, windowWidth, policy, NoScrollBar);
        var expectedUnits = PanelContentSizer.RoundHeightUnitsForGridAligned(raw, unitSize, gap);
        var expected = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, expectedUnits), unitSize, gap).Height;

        var fitHeight = PanelContentSizer.FitHeight(5, IconSize, windowWidth, policy, NoScrollBar, PanelGeometryMode.GridAligned);

        Assert.Equal(expected, fitHeight);
    }

    /// <summary>
    /// GridAligned rounding is monotonically &gt;= the LegacyFreeform (plain ceiling) rounding for the
    /// SAME raw height — rounding up to a workspace row span can only ever add height, never remove it
    /// (both DoD "0/1/2 ряда" and the "не отрезаются" requirement would be violated by a geometry-mode
    /// rounding rule that ever rounded DOWN).
    /// </summary>
    /// <param name="itemCount">The item count used to compute both the LegacyFreeform and GridAligned fit heights.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void FitHeight_GridAlignedRounding_IsNeverShorterThanLegacyFreeformRounding(int itemCount)
    {
        var windowWidth = ThreeColumnWindowWidth(IconLayoutPolicy.FixedGap, NoScrollBar);

        var legacyFreeform = PanelContentSizer.FitHeight(itemCount, IconSize, windowWidth, IconLayoutPolicy.FixedGap, NoScrollBar, PanelGeometryMode.LegacyFreeform);
        var gridAligned = PanelContentSizer.FitHeight(itemCount, IconSize, windowWidth, IconLayoutPolicy.FixedGap, NoScrollBar, PanelGeometryMode.GridAligned);

        Assert.True(gridAligned >= legacyFreeform, $"GridAligned height {gridAligned} was shorter than LegacyFreeform height {legacyFreeform} for itemCount={itemCount}.");
    }

    /// <summary>
    /// Однонаправленность (ADR-029 Решение 7): <see cref="PanelContentSizer.FitHeight"/> never reads or
    /// derives a width from anything it computes — the SAME <c>windowWidth</c> argument passed in is the
    /// only width input anywhere in the pipeline. There is no width OUTPUT of this type at all, by
    /// construction (no method returns anything width-shaped) — this test pins that by confirming varying
    /// only <c>itemCount</c>/<c>iconSize</c> (never <c>windowWidth</c>) is enough to reproduce the DoD's
    /// "0/1/2 rows" matrix without ever needing to re-derive or widen the window.
    /// </summary>
    [Fact]
    public void FitHeight_ZeroOneAndTwoRows_AllDeriveFromTheSameFixedWindowWidth()
    {
        var windowWidth = ThreeColumnWindowWidth(IconLayoutPolicy.FixedGap, NoScrollBar);

        var zeroItems = PanelContentSizer.FitHeight(0, IconSize, windowWidth, IconLayoutPolicy.FixedGap, NoScrollBar, PanelGeometryMode.LegacyFreeform);
        var oneItem = PanelContentSizer.FitHeight(1, IconSize, windowWidth, IconLayoutPolicy.FixedGap, NoScrollBar, PanelGeometryMode.LegacyFreeform);
        var fourItems = PanelContentSizer.FitHeight(4, IconSize, windowWidth, IconLayoutPolicy.FixedGap, NoScrollBar, PanelGeometryMode.LegacyFreeform); // one more than 3-column capacity -> 2 rows.

        Assert.Equal(zeroItems, oneItem); // both the drop-zone floor.
        Assert.True(fourItems > oneItem, "A second row must add height relative to a single row.");
    }

    // ----- helpers -----
    // A window wide enough for exactly 3 FixedGap columns at IconSize=48 (cell=84, edgePadding=42,
    // minGap=4): Extent(3, 84, 4, 42) = 84 + 3*84 + 2*4 = 344; ContentHorizontalChrome(FixedGap, 0) = 8
    // (ContentLeftInset only, no scrollbar reserved when NoScrollBar is passed) -> windowWidth = 352.
    private static double ThreeColumnWindowWidth(IconLayoutPolicy policy, double verticalScrollBarWidth)
    {
        var cell = GridMetrics.CellSize(IconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var contentWidth = AdaptiveGridLayout.Extent(3, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);
        return contentWidth + GridMetrics.ContentHorizontalChrome(policy, verticalScrollBarWidth);
    }
}
