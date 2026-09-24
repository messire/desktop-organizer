using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for the <see cref="IconLayoutPolicy"/>-aware
/// <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double, IconLayoutPolicy)"/>
/// overload (ADR-029 Решение 1/Решение 3, docs/fix-prompts/05-icon-layout.md). Uses the same canonical
/// <c>IconSize = 48</c> parameters as <see cref="AdaptiveGridLayoutTests"/> (<c>x = 84</c>, <c>x_v = 92</c>,
/// <c>pmin = AdaptiveGridLayout.DefaultMinGap</c>, <c>m = 42</c>).
/// </summary>
[Trait(UnitTests, "UI")]
public class AdaptiveGridLayoutIconLayoutPolicyTests
{
    private const double Cell = 84d;
    private const double MinGap = AdaptiveGridLayout.DefaultMinGap;
    private static readonly double EdgeM = AdaptiveGridLayout.EdgePadding(Cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    // ───────────────────────────── Policy overload: LegacyStretch delegates unchanged ─────────────────

    [Theory]
    [InlineData(3, 3, 20d)] // full row, slack -> stretches under the 6-arg overload too
    [InlineData(3, 3, 0d)] // full row, exact fit -> gap == MinGap either way
    [InlineData(2, 4, 1000d)] // partial row -> gap == MinGap either way
    [InlineData(1, 1, 1000d)] // single item -> gap == MinGap either way
    public void RowGap_LegacyStretch_MatchesSixArgumentOverloadExactly(int itemsInRow, int rowCapacity, double extraSlack)
    {
        var available = AdaptiveGridLayout.Extent(rowCapacity, Cell, MinGap, EdgeM) + extraSlack;

        var legacyResult = AdaptiveGridLayout.RowGap(itemsInRow, rowCapacity, available, Cell, MinGap, EdgeM);
        var policyResult = AdaptiveGridLayout.RowGap(itemsInRow, rowCapacity, available, Cell, MinGap, EdgeM, IconLayoutPolicy.LegacyStretch);

        Assert.Equal(legacyResult, policyResult);
    }

    // ───────────────────────────── Policy overload: FixedGap never stretches ──────────────────────────

    [Theory]
    [InlineData(3, 3, 0d)] // full row, exact fit
    [InlineData(3, 3, 20d)] // full row, WOULD stretch under LegacyStretch
    [InlineData(3, 3, 200d)] // full row, large slack -> LegacyStretch would stretch a lot; FixedGap must not
    [InlineData(2, 4, 1000d)] // partial row
    [InlineData(1, 1, 1000d)] // single item
    public void RowGap_FixedGap_AlwaysReturnsMinGap(int itemsInRow, int rowCapacity, double extraSlack)
    {
        var available = AdaptiveGridLayout.Extent(rowCapacity, Cell, MinGap, EdgeM) + extraSlack;

        var gap = AdaptiveGridLayout.RowGap(itemsInRow, rowCapacity, available, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);

        Assert.Equal(MinGap, gap);
    }

    /// <summary>
    /// Direct proof that FixedGap genuinely differs from LegacyStretch when it matters (a full row with
    /// slack) — without this, the two policies could coincidentally agree on every case above by
    /// accident of the specific slack values chosen.
    /// </summary>
    [Fact]
    public void RowGap_FullRowWithSlack_FixedGapDiffersFromLegacyStretch()
    {
        const int n = 3;
        var available = AdaptiveGridLayout.Extent(n, Cell, MinGap, EdgeM) + 40d;

        var fixedGap = AdaptiveGridLayout.RowGap(n, n, available, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        var legacyStretch = AdaptiveGridLayout.RowGap(n, n, available, Cell, MinGap, EdgeM, IconLayoutPolicy.LegacyStretch);

        Assert.Equal(MinGap, fixedGap);
        Assert.True(legacyStretch > MinGap, "LegacyStretch must actually stretch when a full row has slack.");
        Assert.NotEqual(fixedGap, legacyStretch);
    }

    // ───────────────────────────── Coordinate tables: 1/3/7 icons × several widths × both policies ─────
    //
    // DoD (docs/fix-prompts/05-icon-layout.md): "независимо от panel width позиции 1-й и 2-й иконок
    // одной grid preset совпадают относительно content origin" (FixedGap) and "шаг между соседними
    // иконками одинаков... для разных ширин" (FixedGap). Each case below computes RowCapacity/RowGap/
    // Offset exactly the way AdaptiveIconGridPanel.ArrangeOverride does, at three different panel
    // widths for the SAME icon count/IconSize, and asserts on the resulting exact coordinates.

    private static double Step => Cell + MinGap; // FixedGap's constant column pitch at IconSize=48.

    public static IEnumerable<object[]> OneThreeSevenIconsAtSeveralWidths()
    {
        // (itemCount, panelWidth) — widths deliberately span very different RowCapacity values for the
        // SAME itemCount, including widths narrower than the item count (forcing wrap) and much wider
        // than needed (forcing every item into one row).
        yield return new object[] { 1, AdaptiveGridLayout.Extent(1, Cell, MinGap, EdgeM) }; // exact fit, 1 column
        yield return new object[] { 1, AdaptiveGridLayout.Extent(5, Cell, MinGap, EdgeM) }; // much wider, still 1 item
        yield return new object[] { 3, AdaptiveGridLayout.Extent(3, Cell, MinGap, EdgeM) }; // exact fit, 3 columns
        yield return new object[] { 3, AdaptiveGridLayout.Extent(3, Cell, MinGap, EdgeM) + 40d }; // 3 columns, slack
        yield return new object[] { 3, AdaptiveGridLayout.Extent(2, Cell, MinGap, EdgeM) }; // only 2 columns fit -> wraps to 2 rows
        yield return new object[] { 7, AdaptiveGridLayout.Extent(7, Cell, MinGap, EdgeM) }; // exact fit, 7 columns, 1 row
        yield return new object[] { 7, AdaptiveGridLayout.Extent(4, Cell, MinGap, EdgeM) }; // 4 columns -> wraps to 2 rows (4+3)
        yield return new object[] { 7, AdaptiveGridLayout.Extent(4, Cell, MinGap, EdgeM) + 30d }; // same wrap, with slack
    }

    /// <summary>
    /// FixedGap: the 1st icon is always at <c>x = EdgeM</c> and (when there is a 2nd icon in the same
    /// row) the 2nd icon is always at <c>x = EdgeM + Cell + MinGap</c> — independent of panel width,
    /// independent of whether the row that contains them is full or partial. This is the exact DoD
    /// requirement: "позиции 1-й и 2-й иконок одной grid preset совпадают относительно content origin в
    /// панелях разной ширины".
    /// </summary>
    [Theory]
    [MemberData(nameof(OneThreeSevenIconsAtSeveralWidths))]
    public void FixedGap_FirstAndSecondIconPositions_AreIndependentOfPanelWidth(int itemCount, double panelWidth)
    {
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(itemCount, rowCapacity, 0);
        var gap = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);

        var x0 = AdaptiveGridLayout.Offset(0, Cell, gap, EdgeM);
        Assert.Equal(EdgeM, x0);

        if (itemsInRow0 >= 2)
        {
            var x1 = AdaptiveGridLayout.Offset(1, Cell, gap, EdgeM);
            Assert.Equal(EdgeM + Step, x1);
        }
    }

    /// <summary>
    /// FixedGap: the pitch between ANY two adjacent icons within a row (<c>x1 - x0</c>) is the SAME
    /// constant (<c>Cell + MinGap</c>) across every width in the table, including widths that put the
    /// pair in a full row versus a partial row. Complements the position test above by checking the
    /// STEP explicitly, not just the absolute first/second coordinates (DoD: "шаг между соседними
    /// иконками одинаков при FixedGap для разных ширин").
    /// </summary>
    [Theory]
    [MemberData(nameof(OneThreeSevenIconsAtSeveralWidths))]
    public void FixedGap_StepBetweenAdjacentIcons_IsConstantAcrossWidths(int itemCount, double panelWidth)
    {
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(itemCount, rowCapacity, 0);
        if (itemsInRow0 < 2) return; // nothing adjacent to measure a step against in this case

        var gap = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        var x0 = AdaptiveGridLayout.Offset(0, Cell, gap, EdgeM);
        var x1 = AdaptiveGridLayout.Offset(1, Cell, gap, EdgeM);

        Assert.Equal(Step, x1 - x0, 1e-9);
    }

    /// <summary>
    /// FixedGap: a FULL row (itemsInRow == rowCapacity &gt; 1) is never stretched — the last item's right
    /// edge stays at <c>EdgeM + itemsInRow * Cell + (itemsInRow - 1) * MinGap</c>, strictly less than
    /// <c>panelWidth - EdgeM</c> whenever the panel has slack beyond the exact fit, i.e. surplus space is
    /// visibly left on the right rather than being absorbed into the gaps (ADR-029 Решение 1, surplus
    /// space alternative A).
    /// </summary>
    [Theory]
    [InlineData(3, 40d)]
    [InlineData(7, 30d)]
    public void FixedGap_FullRowWithSlack_LeavesSurplusOnTheRight(int itemCount, double slack)
    {
        var exactFitWidth = AdaptiveGridLayout.Extent(itemCount, Cell, MinGap, EdgeM);
        var panelWidth = exactFitWidth + slack;

        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        Assert.Equal(itemCount, rowCapacity); // sanity: slack alone must not add a whole extra column here

        var gap = AdaptiveGridLayout.RowGap(itemCount, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        Assert.Equal(MinGap, gap); // not stretched

        var lastRightEdge = AdaptiveGridLayout.Offset(itemCount - 1, Cell, gap, EdgeM) + Cell;
        var legacyStretchedRightEdge = panelWidth - EdgeM; // where LegacyStretch's full row would land instead

        Assert.True(lastRightEdge < legacyStretchedRightEdge, "FixedGap must leave surplus space to the right of the last column.");
        Assert.Equal(slack, legacyStretchedRightEdge - lastRightEdge, 1e-9); // the surplus is exactly the panel's slack
    }

    /// <summary>
    /// Both policies agree bit-for-bit on a PARTIAL (non-full) row, regardless of how much slack is
    /// available — the policy switch (ADR-029 Решение 1) only changes behavior for a full row with more
    /// than one item; a partial row was always <c>MinGap</c> under ADR-018, and stays that way under
    /// both <see cref="IconLayoutPolicy"/> values.
    /// </summary>
    [Theory]
    [MemberData(nameof(OneThreeSevenIconsAtSeveralWidths))]
    public void PartialRow_BothPoliciesAgree(int itemCount, double panelWidth)
    {
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(itemCount, rowCapacity, 0);
        if (itemsInRow0 >= rowCapacity && itemsInRow0 > 1) return; // this row is FULL — not the case under test here

        var fixedGap = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        var legacyStretch = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.LegacyStretch);

        Assert.Equal(MinGap, fixedGap);
        Assert.Equal(MinGap, legacyStretch);
        Assert.Equal(fixedGap, legacyStretch);
    }

    /// <summary>
    /// Explicit full-row-vs-partial-row parity check for a multi-row layout (7 icons wrapped into a full
    /// row of 4 plus a partial row of 3, at an exact-fit width so LegacyStretch's row 0 gap is
    /// observably different from FixedGap's): row 0 (full) differs between policies, row 1 (partial)
    /// does not — "full/partial rows согласованы" (docs/fix-prompts/05-icon-layout.md).
    /// </summary>
    [Fact]
    public void SevenIcons_TwoRows_FullRowDiffersBetweenPolicies_PartialRowDoesNot()
    {
        var panelWidth = AdaptiveGridLayout.Extent(4, Cell, MinGap, EdgeM) + 30d; // 4-column fit, with slack
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        Assert.Equal(4, rowCapacity);

        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(7, rowCapacity, 0);
        var itemsInRow1 = AdaptiveGridLayout.ItemsInRow(7, rowCapacity, 1);
        Assert.Equal(4, itemsInRow0); // full
        Assert.Equal(3, itemsInRow1); // partial

        var fixedGapRow0 = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        var legacyStretchRow0 = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.LegacyStretch);
        Assert.Equal(MinGap, fixedGapRow0);
        Assert.True(legacyStretchRow0 > MinGap);
        Assert.NotEqual(fixedGapRow0, legacyStretchRow0);

        var fixedGapRow1 = AdaptiveGridLayout.RowGap(itemsInRow1, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        var legacyStretchRow1 = AdaptiveGridLayout.RowGap(itemsInRow1, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.LegacyStretch);
        Assert.Equal(MinGap, fixedGapRow1);
        Assert.Equal(MinGap, legacyStretchRow1);
        Assert.Equal(fixedGapRow1, legacyStretchRow1);
    }

    // ───────────────────────────── Snapshot-style coordinate tables (explicit numbers) ─────────────────
    //
    // The tests above are formula-based (robust to future constant changes); these few pin CONCRETE
    // numbers at IconSize=48 for 1/3/7 icons as an explicit, human-checkable snapshot table, matching
    // the prompt's "snapshot-таблицы координат как ожидания" requirement literally.

    [Fact]
    public void FixedGap_SnapshotTable_OneIcon_ExactFitWidth()
    {
        // 1 icon, panel width = exact fit for 1 column (168 at IconSize=48).
        const double panelWidth = 168d; // Extent(1, 84, 4, 42) = 2*42 + 84 = 168
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(1, rowCapacity, 0);
        var gap = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);

        Assert.Equal(1, rowCapacity);
        Assert.Equal(42d, AdaptiveGridLayout.Offset(0, Cell, gap, EdgeM));
    }

    [Fact]
    public void FixedGap_SnapshotTable_ThreeIcons_ExactFitWidth()
    {
        // 3 icons, panel width = exact fit for 3 columns (344 at IconSize=48): one full row, no stretch.
        const double panelWidth = 344d; // Extent(3, 84, 4, 42) = 84 + 3*84 + 2*4 = 344
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(3, rowCapacity, 0);
        var gap = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);

        Assert.Equal(3, rowCapacity);
        Assert.Equal(4d, gap);
        Assert.Equal(42d, AdaptiveGridLayout.Offset(0, Cell, gap, EdgeM));
        Assert.Equal(130d, AdaptiveGridLayout.Offset(1, Cell, gap, EdgeM)); // 42 + 84 + 4
        Assert.Equal(218d, AdaptiveGridLayout.Offset(2, Cell, gap, EdgeM)); // 42 + 2*(84+4)
    }

    [Fact]
    public void FixedGap_SnapshotTable_SevenIcons_WrapsIntoTwoRowsOfFour()
    {
        // 7 icons, panel width = exact fit for 4 columns (432 at IconSize=48): row 0 full (4, FixedGap ->
        // no stretch), row 1 partial (3, always MinGap under either policy).
        const double panelWidth = 432d; // Extent(4, 84, 4, 42) = 84 + 4*84 + 3*4 = 432
        var rowCapacity = AdaptiveGridLayout.RowCapacity(panelWidth, Cell, MinGap, EdgeM);
        Assert.Equal(4, rowCapacity);
        Assert.Equal(2, AdaptiveGridLayout.RowCount(7, rowCapacity));

        var itemsInRow0 = AdaptiveGridLayout.ItemsInRow(7, rowCapacity, 0);
        var itemsInRow1 = AdaptiveGridLayout.ItemsInRow(7, rowCapacity, 1);
        Assert.Equal(4, itemsInRow0);
        Assert.Equal(3, itemsInRow1);

        var gapRow0 = AdaptiveGridLayout.RowGap(itemsInRow0, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);
        var gapRow1 = AdaptiveGridLayout.RowGap(itemsInRow1, rowCapacity, panelWidth, Cell, MinGap, EdgeM, IconLayoutPolicy.FixedGap);

        Assert.Equal(4d, gapRow0); // full row, FixedGap -> not stretched
        Assert.Equal(4d, gapRow1); // partial row -> always MinGap

        Assert.Equal(42d, AdaptiveGridLayout.Offset(0, Cell, gapRow0, EdgeM));
        Assert.Equal(130d, AdaptiveGridLayout.Offset(1, Cell, gapRow0, EdgeM));
        Assert.Equal(218d, AdaptiveGridLayout.Offset(2, Cell, gapRow0, EdgeM));
        Assert.Equal(306d, AdaptiveGridLayout.Offset(3, Cell, gapRow0, EdgeM));

        // Row 0's last column's right edge is exactly at panelWidth - EdgeM (a genuinely exact-fit
        // width was chosen for this snapshot, so FixedGap and LegacyStretch coincide here) — the
        // FixedGap_FullRowWithSlack_LeavesSurplusOnTheRight test above covers the WITH-slack case.
        Assert.Equal(panelWidth - EdgeM, AdaptiveGridLayout.Offset(3, Cell, gapRow0, EdgeM) + Cell);
    }
}
