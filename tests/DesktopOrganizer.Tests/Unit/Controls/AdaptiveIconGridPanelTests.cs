using System.Windows;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// <c>Measure</c>/<c>Arrange</c> unit tests for the REAL <see cref="AdaptiveIconGridPanel"/> — closes the
/// P00-identified gap (docs/PLANS/workspace-grid-00-baseline.md, part D): every other test of this
/// panel's geometry (<c>AdaptiveGridLayoutTests</c>, <c>AdaptiveGridLayoutIconLayoutPolicyTests</c>)
/// exercises the pure <see cref="AdaptiveGridLayout"/> math directly, never the WPF <see cref="AdaptiveIconGridPanel"/>
/// class itself end-to-end. Runs on a dedicated STA thread via <see cref="StaTestRunner"/> (this
/// project's own minimal STA-thread convention — see that class's remarks for why, and why no existing
/// convention/package covered this before).
/// <para>
/// All expected coordinates below are the same canonical <c>IconSize = 48</c> values used throughout
/// <c>AdaptiveGridLayoutTests</c>/<c>AdaptiveGridLayoutIconLayoutPolicyTests</c> (<c>Cell = 84</c>,
/// <c>RowHeight = 92</c>, <c>EdgeM = 42</c>, <c>MinGap = 4</c>) — children are plain
/// <see cref="FrameworkElement"/> instances with explicit <see cref="FrameworkElement.Width"/>/
/// <see cref="FrameworkElement.Height"/> set to exactly that cell footprint, so
/// <see cref="AdaptiveIconGridPanel.MeasureOverride"/>'s <c>child.DesiredSize</c> read reproduces the
/// same <c>cell</c>/<c>rowHeight</c> the analytic tests assume, without needing a real
/// <c>GridViewTemplate</c> item template.
/// </para>
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class AdaptiveIconGridPanelTests
{
    private const double Cell = 84d; // GridMetrics.CellSize(48)
    private const double RowHeightValue = 92d; // GridMetrics.RowHeight(48)
    private const double EdgeM = 42d; // AdaptiveGridLayout.EdgePadding(84, DefaultEdgePaddingRatio)
    private const double MinGap = AdaptiveGridLayout.DefaultMinGap;

    /// <summary>
    /// Verifies that a freshly constructed <see cref="AdaptiveIconGridPanel"/> defaults to
    /// <see cref="IconLayoutPolicy.LegacyStretch"/>.
    /// </summary>
    [Fact]
    public void LayoutPolicy_DefaultsToLegacyStretch()
    {
        StaTestRunner.Run(() =>
        {
            var panel = new AdaptiveIconGridPanel();

            Assert.Equal(IconLayoutPolicy.LegacyStretch, panel.LayoutPolicy);
        });
    }

    /// <summary>
    /// Verifies that under <see cref="IconLayoutPolicy.FixedGap"/>, three icons in a row that has extra
    /// slack keep their fixed cell pitch and leave the surplus space unabsorbed on the right edge.
    /// </summary>
    [Fact]
    public void FixedGap_ThreeIcons_FullRowWithSlack_DoesNotStretch_MatchesAnalyticFormula()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 384d; // Extent(3, 84, 4, 42) = 344, + 40 slack
            var panel = BuildPanel(3, IconLayoutPolicy.FixedGap);

            MeasureAndArrange(panel, panelWidth);

            Assert.Equal(EdgeM, ChildOrigin(panel, 0).X, 1e-6);
            Assert.Equal(EdgeM + Cell + MinGap, ChildOrigin(panel, 1).X, 1e-6);
            Assert.Equal(EdgeM + (2 * (Cell + MinGap)), ChildOrigin(panel, 2).X, 1e-6);

            // Real WPF Arrange actually sized each child to the uniform cell, not merely computed it.
            Assert.Equal(Cell, panel.Children[0].RenderSize.Width, 1e-6);
            Assert.Equal(RowHeightValue, panel.Children[0].RenderSize.Height, 1e-6);

            // Surplus space (the 40px slack) is left on the right, not absorbed into the gaps.
            var lastRightEdge = ChildOrigin(panel, 2).X + Cell;
            Assert.Equal(panelWidth - EdgeM - 40d, lastRightEdge, 1e-6);
        });
    }

    /// <summary>
    /// Verifies that under <see cref="IconLayoutPolicy.LegacyStretch"/>, three icons in a row that has
    /// extra slack stretch to fill the panel width exactly, unlike <see cref="IconLayoutPolicy.FixedGap"/>.
    /// </summary>
    [Fact]
    public void LegacyStretch_ThreeIcons_FullRowWithSlack_StretchesToFillWidth()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 384d; // same width as the FixedGap test above, for direct contrast
            var panel = BuildPanel(3, IconLayoutPolicy.LegacyStretch);

            MeasureAndArrange(panel, panelWidth);

            var x0 = ChildOrigin(panel, 0).X;
            var x2 = ChildOrigin(panel, 2).X;

            Assert.Equal(EdgeM, x0, 1e-6);

            // Full row stretches to reach the right edge exactly — unlike FixedGap above, which left the
            // 40px slack unused.
            Assert.Equal(panelWidth - EdgeM, x2 + Cell, 1e-6);

            // The stretched span between the first and last icon is wider than FixedGap's fixed-pitch
            // span would have been for the same 3 icons.
            Assert.True(x2 - x0 > 2 * (Cell + MinGap));
        });
    }

    /// <summary>
    /// Verifies that seven icons under <see cref="IconLayoutPolicy.FixedGap"/> wrap into two rows, with the
    /// second row's vertical offset equal to one row height plus the minimum gap and the same X origin as
    /// the first row's column 0.
    /// </summary>
    [Fact]
    public void SevenIcons_WrapsIntoTwoRows_FixedGap_SecondRowStartsAtCorrectOffsetAndSameXOrigin()
    {
        StaTestRunner.Run(() =>
        {
            // M-2 fix-cycle 1 amendment: FixedGap reserves SystemParameters.VerticalScrollBarWidth for
            // RowCapacity — but fix-cycle 2 CRITICAL-1 changed WHERE that reservation is resolved from:
            // this panel is constructed directly here, with no CapacityWidth bound (this test never sets
            // it), so AdaptiveIconGridPanel.ResolveAvailableForCapacity's fallback branch applies — NO
            // reservation at all in that case (see its doc comment). The extra
            // "+ SystemParameters.VerticalScrollBarWidth" below is therefore no longer load-bearing for
            // this test's own "4 columns" outcome (Extent(4, ...) alone is already enough without a
            // bound CapacityWidth) — kept anyway as harmless slack so this test's own numbers don't need
            // to move, and so it still passes unchanged if CapacityWidth is ever wired up as a default
            // (non-NaN) in the future.
            var panelWidth = AdaptiveGridLayout.Extent(4, Cell, MinGap, EdgeM) + SystemParameters.VerticalScrollBarWidth;
            var panel = BuildPanel(7, IconLayoutPolicy.FixedGap);

            MeasureAndArrange(panel, panelWidth);

            var row0FirstOrigin = ChildOrigin(panel, 0);
            var row1FirstOrigin = ChildOrigin(panel, 4); // 5th icon (index 4) starts row 1

            Assert.Equal(0d, row0FirstOrigin.Y, 1e-6); // DefaultVerticalEdgePaddingRatio == 0 at m_v
            Assert.Equal(RowHeightValue + MinGap, row1FirstOrigin.Y, 1e-6); // vertical gap is ALWAYS MinGap

            // Row 1 (partial: 3 items) starts at the SAME X origin as row 0 — column 0 is column 0
            // regardless of which row it's in or which policy is active.
            Assert.Equal(EdgeM, row1FirstOrigin.X, 1e-6);
            Assert.Equal(row0FirstOrigin.X, row1FirstOrigin.X, 1e-6);
        });
    }

    /// <summary>
    /// Verifies that measuring and arranging an <see cref="AdaptiveIconGridPanel"/> with no children
    /// neither throws nor sizes the panel.
    /// </summary>
    [Fact]
    public void EmptyPanel_MeasureAndArrange_DoNotThrow()
    {
        StaTestRunner.Run(() =>
        {
            var panel = new AdaptiveIconGridPanel { LayoutPolicy = IconLayoutPolicy.FixedGap };

            panel.Measure(new Size(500d, double.PositiveInfinity));
            Assert.Equal(new Size(0d, 0d), panel.DesiredSize);

            panel.Arrange(new Rect(0, 0, 500d, 0d));
        });
    }

    /// <summary>
    /// Verifies that a single icon lands at the same origin under both <see cref="IconLayoutPolicy.FixedGap"/>
    /// and <see cref="IconLayoutPolicy.LegacyStretch"/>, since a lone item is never treated as a full row.
    /// </summary>
    [Fact]
    public void SingleIcon_BothPolicies_SamePosition()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 500d;

            var fixedGapPanel = BuildPanel(1, IconLayoutPolicy.FixedGap);
            var legacyStretchPanel = BuildPanel(1, IconLayoutPolicy.LegacyStretch);

            MeasureAndArrange(fixedGapPanel, panelWidth);
            MeasureAndArrange(legacyStretchPanel, panelWidth);

            // A single item is never a "full row with >1 item" — RowGap short-circuits to MinGap under
            // BOTH policies, so a lone icon lands in the exact same place regardless of LayoutPolicy.
            Assert.Equal(ChildOrigin(fixedGapPanel, 0), ChildOrigin(legacyStretchPanel, 0));
            Assert.Equal(EdgeM, ChildOrigin(fixedGapPanel, 0).X, 1e-6);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // M-2 fix-cycle amendment (adr/ADR-029-icon-layout-policy.md Решение 4): FixedGap reserves
    // SystemParameters.VerticalScrollBarWidth in its RowCapacity computation, UNCONDITIONALLY — so the
    // rendered column count never changes as a real vertical scrollbar appears/disappears.
    // LegacyStretch is unaffected (ADR-018's original, accepted M-2 trade-off).
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// LegacyStretch does NOT reserve scrollbar width: at a width that is EXACTLY the 3-column
    /// threshold (<c>Extent(3, 84, 4, 42) == 344</c>), all 3 icons land in row 0 — RowCapacity is
    /// computed straight off the panel's real <c>availableSize</c>/<c>finalSize</c>.Width, with no
    /// reservation. Unchanged by the M-2 fix-cycle amendment (scoped to FixedGap only).
    /// </summary>
    [Fact]
    public void LegacyStretch_RowCapacity_DoesNotReserveVerticalScrollBarWidth()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 344d; // AdaptiveGridLayout.Extent(3, Cell, MinGap, EdgeM)
            var panel = BuildPanel(7, IconLayoutPolicy.LegacyStretch);

            MeasureAndArrange(panel, panelWidth);

            Assert.Equal(0d, ChildOrigin(panel, 2).Y, 1e-6); // 3rd item still on row 0...
            Assert.True(ChildOrigin(panel, 3).Y > 0d); // ...4th item wraps to row 1 — RowCapacity == 3.
        });
    }

    /// <summary>
    /// FixedGap DOES reserve <see cref="SystemParameters.VerticalScrollBarWidth"/> — WHEN
    /// <see cref="AdaptiveIconGridPanel.CapacityWidth"/> is bound (fix-cycle 2 CRITICAL-1: the
    /// reservation is now resolved against <see cref="AdaptiveIconGridPanel.CapacityWidth"/>, not the
    /// raw <c>availableSize</c>/<c>finalSize</c> this test measures/arranges with — see the private
    /// <c>ResolveAvailableForCapacity</c> method's doc comment). Binding
    /// <see cref="AdaptiveIconGridPanel.CapacityWidth"/> to the SAME width used for
    /// <c>Measure</c>/<c>Arrange</c> below simulates the real <c>Templates.xaml</c> wiring at the
    /// specific moment its vertical scrollbar is NOT yet visible (the <c>ScrollViewer</c>'s own
    /// <c>ActualWidth</c> and the viewport width it hands its content coincide exactly then). The
    /// resulting RowCapacity must match the pure-math prediction of
    /// <see cref="AdaptiveGridLayout.RowCapacity(double, double, double, double)"/> fed
    /// <c>panelWidth - SystemParameters.VerticalScrollBarWidth</c>.
    /// </summary>
    [Fact]
    public void FixedGap_RowCapacity_ReservesVerticalScrollBarWidth_MatchesAnalyticFormula()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 344d;
            var expectedRowCapacity = AdaptiveGridLayout.RowCapacity(
                panelWidth - SystemParameters.VerticalScrollBarWidth, Cell, MinGap, EdgeM);

            // Sanity: the reservation must actually be large enough on this machine to change the
            // result relative to LegacyStretch's 3 above, or this test would pass vacuously.
            Assert.True(expectedRowCapacity is >= 1 and < 3, $"expectedRowCapacity={expectedRowCapacity} — test assumes a real, nonzero SystemParameters.VerticalScrollBarWidth.");

            var panel = BuildPanel(7, IconLayoutPolicy.FixedGap);
            panel.CapacityWidth = panelWidth; // fix-cycle 2: the reservation now reads THIS, not availableSize/finalSize directly — see this test's own remarks.
            MeasureAndArrange(panel, panelWidth);

            for (var i = 0; i < expectedRowCapacity; i++)
                Assert.Equal(0d, ChildOrigin(panel, i).Y, 1e-6);

            Assert.True(ChildOrigin(panel, expectedRowCapacity).Y > 0d); // first item past the reserved capacity wraps to row 1
        });
    }

    /// <summary>
    /// Fix-cycle 2 CRITICAL-1 regression pin (adr/ADR-029-icon-layout-policy.md "Fix-цикл P05"): the
    /// fix-cycle 1 version of this test measured/arranged at <c>344</c> and <c>344 - scrollBarWidth</c>
    /// DIRECTLY, with NO <see cref="AdaptiveIconGridPanel.CapacityWidth"/> concept to bind — under the
    /// fix-cycle 1 implementation (subtracting the reservation straight from
    /// <c>availableSize</c>/<c>finalSize</c>.Width) that meant RowCapacity was actually computed from
    /// <c>344 - scrollBarWidth</c> in the FIRST pass and <c>(344 - scrollBarWidth) - scrollBarWidth</c>
    /// (double-subtracted) in the SECOND — the test only happened to pass because both narrower numbers
    /// still floored to the SAME 2-column bucket on the reference machine, not because RowCapacity was
    /// genuinely stable. This version uses the REAL production shape instead:
    /// <see cref="AdaptiveIconGridPanel.CapacityWidth"/> bound ONCE to a STABLE value (344 +
    /// scrollBarWidth — what the real outer <c>ScrollViewer.ActualWidth</c> would be, per
    /// <c>Templates.xaml</c>'s binding), while the RAW width handed to <c>Measure</c>/<c>Arrange</c>
    /// varies between the two passes exactly as the real <c>ScrollViewer</c>'s viewport would (full
    /// width without a visible scrollbar, width-minus-scrollBarWidth with one) — reproducing the actual
    /// double-subtraction bug's precondition instead of a coincidentally-similar substitute.
    /// </summary>
    [Fact]
    public void FixedGap_RowCapacity_StableAcrossScrollBarAppearing()
    {
        StaTestRunner.Run(() =>
        {
            var scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
            Assert.True(scrollBarWidth > 0d, "Test assumes a real, nonzero SystemParameters.VerticalScrollBarWidth.");

            // Extent(3, Cell, MinGap, EdgeM) == 344 -> the stable outer ScrollViewer width (CapacityWidth)
            // that gives exactly 3 columns once the reservation is subtracted.
            var capacityWidth = 344d + scrollBarWidth;

            var withoutScrollbarPanel = BuildPanel(7, IconLayoutPolicy.FixedGap);
            withoutScrollbarPanel.CapacityWidth = capacityWidth;
            MeasureAndArrange(withoutScrollbarPanel, capacityWidth); // no scrollbar yet -> viewport == CapacityWidth

            var withScrollbarPanel = BuildPanel(7, IconLayoutPolicy.FixedGap);
            withScrollbarPanel.CapacityWidth = capacityWidth; // SAME stable outer width...
            MeasureAndArrange(withScrollbarPanel, capacityWidth - scrollBarWidth); // ...but a narrower viewport now that a real scrollbar showed up

            for (var i = 0; i < 7; i++)
                Assert.Equal(ChildOrigin(withoutScrollbarPanel, i), ChildOrigin(withScrollbarPanel, i));

            // Not vacuous: pin the actual column count at 3 (not e.g. both degrading to the same 2-column
            // bucket, which is exactly how the fix-cycle 1 version of this test passed "by accident").
            Assert.Equal(0d, ChildOrigin(withoutScrollbarPanel, 2).Y, 1e-6);
            Assert.True(ChildOrigin(withoutScrollbarPanel, 3).Y > 0d);
        });
    }

    /// <summary>
    /// Fix-cycle 2 CRITICAL-1's documented graceful-degradation branch: a <see cref="IconLayoutPolicy.FixedGap"/>
    /// panel constructed OUTSIDE <c>GridViewTemplate</c>'s <c>ScrollViewer</c> (so
    /// <see cref="AdaptiveIconGridPanel.CapacityWidth"/> is never bound, staying at its
    /// <see cref="double.NaN"/> default) reserves NOTHING — RowCapacity is computed straight from the
    /// raw <c>availableSize</c>/<c>finalSize</c>.Width, the SAME formula
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> always uses. This is a deliberate behavior CHANGE
    /// from the fix-cycle 1 implementation (which reserved unconditionally, from the raw width, in this
    /// exact scenario — see <see cref="FixedGap_RowCapacity_ReservesVerticalScrollBarWidth_MatchesAnalyticFormula"/>'s
    /// remarks for why that was also the CRITICAL-1 double-subtraction bug's root cause once a real
    /// <c>ScrollViewer</c> WAS in play) — see the private <c>ResolveAvailableForCapacity</c> method's
    /// doc comment for the rationale (no stable width exists to reserve against outside the real
    /// template).
    /// </summary>
    [Fact]
    public void FixedGap_RowCapacity_CapacityWidthUnbound_DegradesToNoReservation()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 344d; // Extent(3, Cell, MinGap, EdgeM)
            var panel = BuildPanel(7, IconLayoutPolicy.FixedGap); // CapacityWidth left at its NaN default
            MeasureAndArrange(panel, panelWidth);

            Assert.Equal(0d, ChildOrigin(panel, 2).Y, 1e-6); // 3rd item still row 0...
            Assert.True(ChildOrigin(panel, 3).Y > 0d); // ...4th wraps -> RowCapacity == 3, no reservation applied.
        });
    }

    /// <summary>
    /// Fix-cycle 2 CRITICAL-1 safety-net: binding <see cref="AdaptiveIconGridPanel.CapacityWidth"/> must
    /// have ZERO effect under <see cref="IconLayoutPolicy.LegacyStretch"/> — its
    /// <see cref="GridMetrics.ReservedVerticalScrollBarWidth"/> is always <c>0</c>, so the private
    /// <c>ResolveAvailableForCapacity</c> method must keep reading the REAL, raw
    /// <c>availableSize</c>/<c>finalSize</c>.Width for it — never the (deliberately wildly different,
    /// here) <see cref="AdaptiveIconGridPanel.CapacityWidth"/> value — preserving ADR-018's own accepted
    /// M-2 trade-off (variant (c): RowCapacity fluctuates with the real, possibly scrollbar-narrowed
    /// viewport) for this policy, completely unchanged by this fix-cycle.
    /// </summary>
    [Fact]
    public void LegacyStretch_RowCapacity_UnaffectedByCapacityWidth_EvenWhenBound()
    {
        StaTestRunner.Run(() =>
        {
            const double panelWidth = 344d; // Extent(3, Cell, MinGap, EdgeM)
            var panel = BuildPanel(7, IconLayoutPolicy.LegacyStretch);
            panel.CapacityWidth = 1000d; // deliberately wrong/unrelated -- must be completely ignored.
            MeasureAndArrange(panel, panelWidth);

            Assert.Equal(0d, ChildOrigin(panel, 2).Y, 1e-6); // still 3 columns, from the RAW 344 width...
            Assert.True(ChildOrigin(panel, 3).Y > 0d); // ...not CapacityWidth's 1000.
        });
    }

    private static AdaptiveIconGridPanel BuildPanel(int itemCount, IconLayoutPolicy policy)
    {
        var panel = new AdaptiveIconGridPanel { LayoutPolicy = policy };
        for (var i = 0; i < itemCount; i++)
            panel.Children.Add(new FrameworkElement { Width = Cell, Height = RowHeightValue });

        return panel;
    }

    private static void MeasureAndArrange(AdaptiveIconGridPanel panel, double availableWidth)
    {
        // Mirrors GridViewTemplate's real host: HorizontalScrollBarVisibility="Disabled" (finite width)
        // + VerticalScrollBarVisibility="Auto" (infinite height) — see AdaptiveIconGridPanel.MeasureOverride's
        // own remarks on why it never reads availableSize.Height.
        panel.Measure(new Size(availableWidth, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, availableWidth, panel.DesiredSize.Height));
    }

    /// <summary>
    /// The child's actual on-screen origin relative to <paramref name="panel"/>, read back from the REAL
    /// arrange result via the visual-tree transform — not a value this test computed itself, so it
    /// genuinely exercises <see cref="AdaptiveIconGridPanel.ArrangeOverride"/>'s own arithmetic.
    /// </summary>
    private static Point ChildOrigin(AdaptiveIconGridPanel panel, int index)
        => panel.Children[index].TranslatePoint(new Point(0, 0), panel);
}
