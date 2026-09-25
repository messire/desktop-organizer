using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="ListReorderGeometry.HitTestReorderContainer(ListBox, Point)"/> — the P05
/// fix-cycle's MINOR finding (ListReorderGeometry ~line 46): a direct point hit-test used to fall
/// straight through to "no container" (<see langword="null"/>) whenever the cursor was over
/// <see cref="IconLayoutPolicy.FixedGap"/>'s surplus, unpainted space to the RIGHT of a row that is not
/// the panel's last row — <see cref="ListReorderGeometry.ComputeInsertionGap"/> then resolved that
/// <see langword="null"/> to "insert at the end of the WHOLE LIST"
/// (<c>ChildWindowReorderGeometryTests.ComputeInsertionGap_NullContainerIndex_ReturnsItemCount</c>),
/// silently discarding the row the user was actually hovering over. Runs on a dedicated STA thread
/// (<see cref="StaTestRunner"/>) — a real <see cref="ListBox"/>/<see cref="AdaptiveIconGridPanel"/>
/// visual tree, same convention <c>AdaptiveIconGridPanelTests</c> already establishes for this project.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ListReorderGeometryHitTestTests
{
    private const double Cell = 84d; // GridMetrics.CellSize(48)
    private const double RowHeightValue = 92d; // GridMetrics.RowHeight(48)

    /// <summary>
    /// Core P05 fix: a cursor in row 0's FixedGap surplus space (to the right of the row's last item,
    /// but still within row 0's own vertical band) resolves to the RIGHT-MOST container in that row —
    /// row 0's last item — not <see langword="null"/>. Panel width (500) is deliberately wider than
    /// <c>Extent(4, Cell, MinGap, EdgeM) == 432</c> (row capacity 4 at this width) specifically so a
    /// real, sizeable surplus gap exists to probe.
    /// </summary>
    [Fact]
    public void HitTestReorderContainer_FixedGapSurplusZone_RightOfNonLastRow_ResolvesToRowsLastItem()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d; // RowCapacity(500, 84, 4, 42) == 4 -> row 0 = items 0-3, row 1 = items 4-6
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var lastInRow0 = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(3)!;
            var origin = lastInRow0.TranslatePoint(new Point(0, 0), listBox);
            var probe = new Point(origin.X + lastInRow0.ActualWidth + 20, origin.Y + (RowHeightValue / 2)); // right of item 3, still within row 0's vertical band

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.NotNull(container);
            Assert.Equal(3, listBox.ItemContainerGenerator.IndexFromContainer(container));

            // End-to-end: ComputeInsertionGap must now resolve to "end of row 0" (index 4), NOT
            // "end of the whole list" (itemCount == 7) — the actual bug this fix closes.
            var positionInContainer = new Point(probe.X - origin.X, probe.Y - origin.Y);
            var gap = ListReorderGeometry.ComputeInsertionGap(3, positionInContainer, new Size(lastInRow0.ActualWidth, lastInRow0.ActualHeight), PanelViewMode.Grid, itemCount: 7);

            Assert.Equal(4, gap);
        });
    }

    /// <summary>
    /// A cursor genuinely below every row (no container's vertical extent contains it) must still
    /// resolve to <see langword="null"/> — the correct, UNCHANGED "past the end of the list" behavior
    /// (<c>ComputeInsertionGap</c>'s <c>containerIndex == null -&gt; itemCount</c> branch). Proves the
    /// P05 fix's fallback is scoped to "same row, different column", not "any miss at all".
    /// </summary>
    [Fact]
    public void HitTestReorderContainer_BelowEveryRow_StillReturnsNull()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d;
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var probe = new Point(200, listBox.DesiredSize.Height + 500); // far below every row

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.Null(container);
        });
    }

    /// <summary>
    /// A direct hit (cursor actually over a real container) must still resolve via the ordinary
    /// <see cref="System.Windows.Media.VisualTreeHelper.HitTest(Visual, Point)"/> path, unaffected by
    /// the new fallback — regression guard that the fallback only engages on an actual miss.
    /// </summary>
    [Fact]
    public void HitTestReorderContainer_DirectHitOnItem_StillResolvesToThatItem()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d;
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var item1 = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(1)!;
            var origin = item1.TranslatePoint(new Point(0, 0), listBox);
            var probe = new Point(origin.X + (item1.ActualWidth / 2), origin.Y + (item1.ActualHeight / 2));

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.NotNull(container);
            Assert.Equal(1, listBox.ItemContainerGenerator.IndexFromContainer(container));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Fix-cycle 2 MINOR-1: the fix-cycle 1 fallback always picked the RIGHT-MOST container in the
    // cursor's row, regardless of which side of it the cursor actually was on — correct for the
    // right-surplus-space case above, but wrong for the row's LEFT gutter (the "m" edge-padding margin
    // before the first column), which resolved to the row's LAST item instead of its FIRST. The fix
    // (ListReorderGeometry.FindNearestContainerInSameRow) picks by horizontal distance instead.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Row 0's LEFT gutter (the "m" edge-padding margin before column 0, adr/ADR-029-icon-layout-policy.md
    /// <c>m = x * ratio</c> — <c>42</c> DIP at <c>IconSize = 48</c>) must resolve to row 0's FIRST item,
    /// not its last — the MINOR-1 regression this fix-cycle closes. Also proves the resulting insertion
    /// gap is "before item 0" (index <c>0</c>), not "end of row"/"end of list".
    /// </summary>
    [Fact]
    public void HitTestReorderContainer_LeftGutterOfNonFirstRow_ResolvesToRowsFirstItem_NotLastItem()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d; // RowCapacity(500, 84, 4, 42) == 4 -> row 0 = items 0-3, row 1 = items 4-6
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var firstInRow0 = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0)!;
            var origin = firstInRow0.TranslatePoint(new Point(0, 0), listBox);

            // Deep inside the left gutter (m == 42 DIP wide) — well clear of item 0's own left edge, so
            // the direct VisualTreeHelper.HitTest above genuinely misses every container.
            var probe = new Point(origin.X - 20, origin.Y + (RowHeightValue / 2));
            Assert.True(probe.X >= 0d, "Probe must stay inside the ListBox's own bounds to be a meaningful hit-test input.");

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.NotNull(container);
            Assert.Equal(0, listBox.ItemContainerGenerator.IndexFromContainer(container));

            // End-to-end: the cursor is well to the LEFT of item 0's own midpoint -> "insert before it"
            // (index 0), not "insert after it" (index 1) and certainly not "end of list" (7).
            var positionInContainer = new Point(probe.X - origin.X, probe.Y - origin.Y);
            var gap = ListReorderGeometry.ComputeInsertionGap(0, positionInContainer, new Size(firstInRow0.ActualWidth, firstInRow0.ActualHeight), PanelViewMode.Grid, itemCount: 7);

            Assert.Equal(0, gap);
        });
    }

    /// <summary>
    /// Symmetric non-first-row case: row 1's left gutter must resolve to row 1's first item (index
    /// <c>4</c>), not row 1's last item (index <c>6</c>) — the exact wrong answer the fix-cycle 1
    /// "always right-most" fallback would have produced here (row 1 IS in the cursor's vertical band,
    /// so the old fallback would have picked SOME container in it, just the wrong end).
    /// </summary>
    [Fact]
    public void HitTestReorderContainer_LeftGutterOfSecondRow_ResolvesToThatRowsFirstItem()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d;
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var firstInRow1 = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(4)!;
            var origin = firstInRow1.TranslatePoint(new Point(0, 0), listBox);
            var probe = new Point(origin.X - 20, origin.Y + (RowHeightValue / 2));
            Assert.True(probe.X >= 0d, "Probe must stay inside the ListBox's own bounds to be a meaningful hit-test input.");

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.NotNull(container);
            Assert.Equal(4, listBox.ItemContainerGenerator.IndexFromContainer(container));
        });
    }

    /// <summary>
    /// A cursor in the narrow <c>pmin</c> gap BETWEEN two same-row items (4 DIP — too thin for the direct
    /// hit-test to reliably land on either neighbor) must resolve to whichever of the two is horizontally
    /// nearer — here, exactly at the midpoint between items 0 and 1, so either is an acceptable "nearest"
    /// answer, but the resolved container must be one of THOSE TWO, never a different row's item and
    /// never <see langword="null"/>. Proves the fallback's row-membership + nearest-distance logic
    /// composes correctly for the "between two columns", not just "before the first"/"after the last"
    /// edge cases the other tests in this class cover.
    /// </summary>
    [Fact]
    public void HitTestReorderContainer_BetweenTwoColumnsInSameRow_ResolvesToOneOfThem()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d;
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var item0 = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0)!;
            var item1 = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(1)!;
            var origin0 = item0.TranslatePoint(new Point(0, 0), listBox);
            var origin1 = item1.TranslatePoint(new Point(0, 0), listBox);

            // Midpoint of the pmin gap between item 0's right edge and item 1's left edge.
            var gapMidX = ((origin0.X + item0.ActualWidth) + origin1.X) / 2d;
            var probe = new Point(gapMidX, origin0.Y + (RowHeightValue / 2));

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.NotNull(container);
            var index = listBox.ItemContainerGenerator.IndexFromContainer(container);
            Assert.True(index is 0 or 1, $"Expected the nearest same-row neighbor (0 or 1), got {index}.");
        });
    }

    /// <summary>
    /// Below every row (already covered by <see cref="HitTestReorderContainer_BelowEveryRow_StillReturnsNull"/>
    /// as a single case) plus, as a <c>Theory</c>, a few X positions across that same "genuinely
    /// past the end" vertical band — none of the horizontal-distance fallback logic should ever
    /// accidentally resolve a Y position below every row to a container, regardless of X.
    /// </summary>
    /// <param name="probeX">The horizontal coordinate of the probe point, in DIPs relative to the <see cref="ListBox"/>.</param>
    [Theory]
    [InlineData(-50d)]
    [InlineData(0d)]
    [InlineData(200d)]
    [InlineData(499d)]
    [InlineData(1000d)]
    public void HitTestReorderContainer_BelowEveryRow_StillReturnsNull_AcrossXPositions(double probeX)
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d;
            var listBox = BuildFixedGapListBox(7);
            MeasureAndArrange(listBox, panelWidth);

            var probe = new Point(probeX, listBox.DesiredSize.Height + 500);

            var container = ListReorderGeometry.HitTestReorderContainer(listBox, probe);

            Assert.Null(container);
        });
    }

    /// <summary>
    /// Builds a real <see cref="ListBox"/> hosting a real <see cref="AdaptiveIconGridPanel"/> (via
    /// <see cref="ItemsPanelTemplate"/>, mirroring <c>GridViewTemplate</c>'s own
    /// <c>ListBox.ItemsPanel</c>), with <paramref name="itemCount"/> plain items whose generated
    /// <see cref="ListBoxItem"/> containers are sized to the canonical <c>IconSize = 48</c> cell
    /// footprint via <see cref="ItemsControl.ItemContainerStyle"/> — the same technique
    /// <c>AdaptiveIconGridPanelTests.BuildPanel</c> uses directly on <see cref="FrameworkElement"/>
    /// children, one level up (through a real <see cref="ListBoxItem"/> container this time, since
    /// <see cref="ListReorderGeometry.HitTestReorderContainer(ListBox, Point)"/> needs real containers
    /// to walk up to, not bare children).
    /// </summary>
    private static ListBox BuildFixedGapListBox(int itemCount)
    {
        var factory = new FrameworkElementFactory(typeof(AdaptiveIconGridPanel));
        factory.SetValue(AdaptiveIconGridPanel.LayoutPolicyProperty, IconLayoutPolicy.FixedGap);

        var itemContainerStyle = new Style(typeof(ListBoxItem));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, Cell));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, RowHeightValue));

        var listBox = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(factory),
            ItemContainerStyle = itemContainerStyle,

            // Bypasses the OS/theme default ControlTemplate (which — without a running Application/theme
            // dictionaries loaded — does not reliably apply an ItemsPresenter far enough for
            // ItemContainerGenerator to actually realize containers in this headless test host) with the
            // minimal template every ItemsControl needs: just an ItemsPresenter, hosting our
            // AdaptiveIconGridPanel via ItemsPanel above. Same "give it the smallest real template it
            // needs, skip the theme" approach as ResourceDictionaryMergeTests.MaterializePanelChromeTemplate,
            // one level up (Control template vs. that one applying an app-resource Style).
            Template = new ControlTemplate(typeof(ListBox))
            {
                VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)),
            },
        };

        for (var i = 0; i < itemCount; i++)
            listBox.Items.Add(i);

        return listBox;
    }

    private static void MeasureAndArrange(ListBox listBox, double width)
    {
        listBox.Measure(new Size(width, double.PositiveInfinity));
        listBox.Arrange(new Rect(0, 0, width, listBox.DesiredSize.Height));
        listBox.UpdateLayout();
    }
}
