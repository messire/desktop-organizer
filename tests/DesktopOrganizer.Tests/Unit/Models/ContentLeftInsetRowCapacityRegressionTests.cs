using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Regression coverage for ADR-029's F-3 fix (docs/PLANS/workspace-grid-00-baseline.md finding F-3,
/// docs/fix-prompts/05-icon-layout.md): a panel snapped to <c>k</c> columns
/// (<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c>) must
/// actually RENDER <c>k</c> columns once <c>PanelChrome</c>'s fixed accent-indicator column
/// (<see cref="PanelMetrics.ContentLeftInset"/>) is subtracted from the snapped window width — before
/// this fix, <see cref="GridMetrics.FileListHPadding"/> was an independent <c>0</c> literal that let
/// every snap-time formula assume the FULL window width was available to the icon grid, while
/// <see cref="DesktopOrganizer.UI.Controls.AdaptiveIconGridPanel"/> itself (driven by real WPF <c>*</c>-column layout, not
/// this constant) only ever received <c>Window.Width - 8</c>, silently losing one column at every snap
/// point. All assertions here operate purely in DIP (no physical-pixel/DPI-scale term anywhere in the
/// formulas involved), so the guarantee holds identically at any DPI — see
/// <see cref="F3_RowCapacity_HoldsWithSubPixelShortfallWithinM1Tolerance_AcrossIconSizes"/> for the one place a
/// DPI-attributable sub-pixel shortfall (ADR-018 MAJOR-bug M-1, <see cref="AdaptiveGridLayout.LayoutTolerance"/>)
/// is exercised explicitly, confirming the F-3 fix does not erode that existing safety margin.
/// </summary>
[Trait(UnitTests, "UI")]
public class ContentLeftInsetRowCapacityRegressionTests
{
    /// <summary>
    /// The core F-3 regression: for a representative spread of <c>IconSize</c> values — the full tick
    /// range (16-96 in steps of 4) PLUS real fractional values persisted on a live user's machine before
    /// any tick-snap fix (P00 finding F-11, docs/PLANS/workspace-grid-00-baseline.md) — a panel snapped
    /// to <c>k</c> columns for every <c>k</c> from 1 to 8 must report EXACTLY <c>k</c> back out of
    /// <see cref="AdaptiveGridLayout.RowCapacity"/> once the real content width is computed. Before the
    /// F-3 fix this failed for every <c>k &gt;= 1</c> where <c>ContentLeftInset (8)</c> was enough to
    /// push the shortfall past <see cref="AdaptiveGridLayout.LayoutTolerance"/> — i.e. essentially always,
    /// for any <c>IconSize</c> where <c>cell + minGap &gt; 8</c> (see
    /// docs/PLANS/workspace-grid-00-baseline.md's sverка in the "Сверка срезов" note).
    /// </summary>
    /// <param name="iconSize">The icon size used to derive the cell size and edge padding under test.</param>
    [Theory]
    [InlineData(16d)]
    [InlineData(20d)]
    [InlineData(24d)]
    [InlineData(28d)]
    [InlineData(32d)]
    [InlineData(36d)]
    [InlineData(40d)]
    [InlineData(44d)]
    [InlineData(48d)]
    [InlineData(52d)]
    [InlineData(56d)]
    [InlineData(60d)]
    [InlineData(64d)]
    [InlineData(68d)]
    [InlineData(72d)]
    [InlineData(76d)]
    [InlineData(80d)]
    [InlineData(84d)]
    [InlineData(88d)]
    [InlineData(92d)]
    [InlineData(96d)]
    [InlineData(104d)]
    [InlineData(112d)]
    [InlineData(120d)]
    [InlineData(128d)]
    [InlineData(32.010182570303485d)] // real persisted DefaultIconSize, P00 finding F-11 — not on a tick
    [InlineData(31.512447285700045d)] // real persisted panel IconSize, P00 finding F-11 — not on a tick
    [InlineData(31.630540508481275d)] // real persisted panel IconSize, P00 finding F-11 — not on a tick
    public void F3_RowCapacity_OfSnappedContentWidth_NeverLosesAColumn(double iconSize)
    {
        var cell = GridMetrics.CellSize(iconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

        for (var k = 1; k <= 8; k++)
        {
            var snappedWindowWidth = SnappedWindowWidth(k, cell, edgePadding);
            var actualContentWidth = ActualContentWidth(snappedWindowWidth);

            var rowCapacity = AdaptiveGridLayout.RowCapacity(actualContentWidth, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);

            Assert.True(
                k == rowCapacity,
                $"IconSize={iconSize}, k={k}: expected RowCapacity {k}, got {rowCapacity} (snappedWindowWidth={snappedWindowWidth}, actualContentWidth={actualContentWidth}).");
        }
    }

    /// <summary>
    /// Symmetric characterization of the PRE-fix bug, kept as a permanent regression pin (not deleted
    /// once the fix landed): if a caller ever mistakenly reverts to treating
    /// <see cref="GridMetrics.FileListHPadding"/> as <c>0</c> for the window-to-content conversion (i.e.
    /// assumes the full window width reaches the icon grid), <see cref="AdaptiveGridLayout.RowCapacity"/>
    /// systematically reports <c>k - 1</c> instead of <c>k</c> for every column count large enough that
    /// <c>PanelMetrics.ContentLeftInset (8)</c> exceeds <see cref="AdaptiveGridLayout.LayoutTolerance"/>
    /// relative to one cell's width — i.e. for every practical <c>IconSize</c>/<c>k</c> combination. This
    /// documents WHY the fix was needed, using the same live constants (not a hardcoded pre-fix
    /// snapshot), so it keeps tracking reality if <see cref="PanelMetrics.ContentLeftInset"/> itself ever
    /// changes.
    /// </summary>
    /// <param name="iconSize">The icon size used to derive the cell size and edge padding under test.</param>
    /// <param name="k">The column count the panel is (analytically) snapped to.</param>
    [Theory]
    [InlineData(48d, 2)]
    [InlineData(48d, 3)]
    [InlineData(32d, 4)]
    public void PreF3Bug_Characterization_TreatingFullWindowWidthAsContentWidth_LosesOneColumn(double iconSize, int k)
    {
        var cell = GridMetrics.CellSize(iconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

        // Pre-fix formula (ADR-015): GridMetrics.FileListHPadding used to be an INDEPENDENT 0d literal,
        // not a reference to PanelMetrics.ContentLeftInset — so the pre-fix snapped window width for k
        // columns was simply Extent(k) + 0. Modeled here with a literal 0d (NOT GridMetrics.
        // FileListHPadding's current value) precisely so this test keeps demonstrating the historical bug
        // even after the fix, instead of accidentally re-deriving today's already-correct behavior.
        const double preFixChrome = 0d;
        var snappedWindowWidth = AdaptiveGridLayout.Extent(k, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding) + preFixChrome;

        var buggyRowCapacity = AdaptiveGridLayout.RowCapacity(snappedWindowWidth, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);

        Assert.Equal(k, buggyRowCapacity); // the pre-fix analytical count still claimed k columns fit...

        // ...but PanelChrome's accent column (PanelMetrics.ContentLeftInset, always a real 8 DIP,
        // regardless of what the snap formula believed) really does take 8 DIP away from the icon grid's
        // actual content width — for these (iconSize, k) combinations that shortfall exceeds
        // LayoutTolerance, so the panel would really only ever have rendered k - 1 columns.
        var actualContentWidth = snappedWindowWidth - PanelMetrics.ContentLeftInset;
        var actualRowCapacity = AdaptiveGridLayout.RowCapacity(actualContentWidth, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);

        Assert.Equal(k - 1, actualRowCapacity);
    }

    /// <summary>
    /// Confirms the F-3 fix does not erode ADR-018's MAJOR-bug M-1 tolerance
    /// (<see cref="AdaptiveGridLayout.LayoutTolerance"/> = 0.5 DIP): even with an ADDITIONAL sub-pixel
    /// shortfall safely INSIDE the tolerance window (0.49 of the 0.5 DIP budget — the same margin
    /// <c>AdaptiveGridLayoutTests.RowCapacity_SubPixelShortfallWithinTolerance_StillReturnsK</c> already
    /// uses, deliberately not the exact boundary itself: at exactly <c>0.5</c> the result is only
    /// analytically <c>k</c> in exact arithmetic — accumulated <see cref="double"/> rounding across a
    /// fractional <c>IconSize</c>'s <see cref="GridMetrics.CellSize"/>/edge-padding chain can legitimately
    /// land a hair on either side of that razor's edge, which would make a boundary-exact assertion here
    /// flaky rather than meaningful) layered on top of the F-3-corrected content width (the worst case
    /// M-1 was designed to absorb — fractional <c>IconSize</c>/DPI layout rounding, see ADR-018),
    /// <see cref="AdaptiveGridLayout.RowCapacity"/> still reports the full <c>k</c>. Since every quantity
    /// here is a DIP (device-independent pixel) by construction — <see cref="GridMetrics"/>/
    /// <see cref="AdaptiveGridLayout"/>/<see cref="PanelMetrics"/> never operate in physical pixels — this
    /// holds identically regardless of the monitor's actual DPI scale factor.
    /// </summary>
    /// <param name="iconSize">The icon size used to derive the cell size and edge padding under test.</param>
    [Theory]
    [InlineData(16d)]
    [InlineData(32.010182570303485d)]
    [InlineData(48d)]
    [InlineData(96d)]
    [InlineData(104d)]
    [InlineData(112d)]
    [InlineData(120d)]
    [InlineData(128d)]
    public void F3_RowCapacity_HoldsWithSubPixelShortfallWithinM1Tolerance_AcrossIconSizes(double iconSize)
    {
        const double shortfallWithinTolerance = 0.49d; // < AdaptiveGridLayout.LayoutTolerance (0.5)
        var cell = GridMetrics.CellSize(iconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

        for (var k = 2; k <= 5; k++)
        {
            var snappedWindowWidth = SnappedWindowWidth(k, cell, edgePadding);
            var actualContentWidth = ActualContentWidth(snappedWindowWidth) - shortfallWithinTolerance;

            var rowCapacity = AdaptiveGridLayout.RowCapacity(actualContentWidth, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);

            Assert.True(
                k == rowCapacity,
                $"IconSize={iconSize}, k={k}: expected RowCapacity {k} with a sub-pixel shortfall inside M-1 tolerance, got {rowCapacity}.");
        }
    }

    /// <summary>
    /// The exact window-width formula <c>NativePanelResizeController</c>'s
    /// <c>SnapWidthToGrid</c> use for a panel that snaps to exactly <paramref name="columns"/> columns —
    /// content <see cref="AdaptiveGridLayout.Extent"/> plus the chrome
    /// <see cref="GridMetrics.FileListHPadding"/> now accounts for (F-3 fix).
    /// </summary>
    private static double SnappedWindowWidth(int columns, double cell, double edgePadding)
        => AdaptiveGridLayout.Extent(columns, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding) + GridMetrics.FileListHPadding;

    /// <summary>
    /// The content width <see cref="DesktopOrganizer.UI.Controls.AdaptiveIconGridPanel"/> ACTUALLY receives as
    /// <c>availableSize.Width</c>/<c>finalSize.Width</c> once real WPF <c>*</c>-column layout
    /// (<c>Resources/Styles/Panels.xaml</c>'s <c>PART_ContentHost</c>) has taken
    /// <see cref="PanelMetrics.ContentLeftInset"/> off the window width.
    /// </summary>
    private static double ActualContentWidth(double snappedWindowWidth) => snappedWindowWidth - PanelMetrics.ContentLeftInset;
}
