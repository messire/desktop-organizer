using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="GridMetrics"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class GridMetricsTests
{
    // ----- BoxSize -----

    /// <summary>
    /// BoxSize(48) == 72 (StackPanel width for 48-px icons, i.e. icon × 1.5).
    /// </summary>
    [Fact]
    public void BoxSize_48_Returns72()
    {
        Assert.Equal(72d, GridMetrics.BoxSize(48));
    }

    /// <summary>
    /// BoxSize(32) == 48.
    /// </summary>
    [Fact]
    public void BoxSize_32_Returns48()
    {
        Assert.Equal(48d, GridMetrics.BoxSize(32));
    }

    // ----- CellSize -----

    /// <summary>
    /// CellSize(48) == 84 (BoxSize 72 + ItemMargin 6 × 2).
    /// This is the full column footprint used for wrap-mode width quantisation.
    /// </summary>
    [Fact]
    public void CellSize_48_Returns84()
    {
        Assert.Equal(84d, GridMetrics.CellSize(48));
    }

    /// <summary>
    /// CellSize(32) == 60 (BoxSize 48 + 12).
    /// </summary>
    [Fact]
    public void CellSize_32_Returns60()
    {
        Assert.Equal(60d, GridMetrics.CellSize(32));
    }

    /// <summary>
    /// CellSize(0) must not throw.
    /// </summary>
    [Fact]
    public void CellSize_Zero_DoesNotThrow()
    {
        var result = GridMetrics.CellSize(0);
        Assert.Equal(GridMetrics.ItemMargin * 2d, result);
    }

    // ----- RowHeight -----

    /// <summary>
    /// RowHeight(48) == 92 (48 icon + 32 label + 6 + 6 margins).
    /// </summary>
    [Fact]
    public void RowHeight_48_Returns92()
    {
        Assert.Equal(92d, GridMetrics.RowHeight(48));
    }

    /// <summary>
    /// RowHeight(32) == 76 (32 icon + 32 label + 6 + 6 margins).
    /// </summary>
    [Fact]
    public void RowHeight_32_Returns76()
    {
        Assert.Equal(76d, GridMetrics.RowHeight(32));
    }

    /// <summary>
    /// RowHeight must always equal iconSize + LabelHeight + two full ItemMargin "gutters" — the same
    /// kind of byte-for-byte invariant already enforced for CellSize below, so the adaptive-grid math
    /// (AdaptiveGridLayout, fed by RowHeight as x_v) never silently desyncs from the actual rendered
    /// GridViewTemplate row footprint.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    public void RowHeight_EqualsIconPlusLabelPlusTwoItemMargins(double iconSize)
    {
        var expected = iconSize + GridMetrics.LabelHeight + (GridMetrics.ItemMargin * 2d);

        Assert.Equal(expected, GridMetrics.RowHeight(iconSize));
    }

    // ----- CellSize == BoxSize + 2*ItemMargin invariant (ui-polishing-1 §3.8.8) -----

    /// <summary>
    /// CellSize must always equal BoxSize plus two full ItemMargin "gutters" (one per side) — this is
    /// the formula B-F1 (ui-polishing-1 §3.3) explicitly preserves byte-for-byte across the
    /// SelectionBorder/margin split (5px template margin + 1px selection border = ItemMargin).
    /// Regressing this desyncs GridMetrics.CellSize from the actual per-item footprint rendered by
    /// GridViewTemplate, which breaks resize-quantisation math (Resize_DragDelta/SnapWidthToGrid).
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    public void CellSize_EqualsBoxSizePlusTwoItemMargins(double iconSize)
    {
        var expected = GridMetrics.BoxSize(iconSize) + (GridMetrics.ItemMargin * 2d);

        Assert.Equal(expected, GridMetrics.CellSize(iconSize));
    }

    // ----- FileListHPadding (ui-polishing-1 §3.5) -----

    /// <summary>
    /// FileListHPadding is 0 (FileListHost carries no extra left/right margin): the item's own
    /// per-side chrome (ItemMargin) already reproduces half the inter-column seam at the list edges,
    /// so no additional host-level padding is needed to keep the edge visually symmetric with the
    /// seam between two columns (ui-polishing-1 §3.5/§3.7).
    /// </summary>
    [Fact]
    public void FileListHPadding_IsZero()
    {
        Assert.Equal(0d, GridMetrics.FileListHPadding);
    }

    // ----- FileListVPadding (adaptive-icon-grid) -----

    /// <summary>
    /// FileListVPadding is 4 (FileListHost.Margin="0,0,0,4" — the bottom margin, previously ignored by
    /// the height-quantisation math in ChildWindow.xaml.cs, causing a systematic 4px off-by-four).
    /// </summary>
    [Fact]
    public void FileListVPadding_Is4()
    {
        Assert.Equal(4d, GridMetrics.FileListVPadding);
    }

    // ----- MinPanelSize / MinPanelWidth (finalization MAJOR M-1) -----
    //
    // Regression coverage for the bug found by code-quality-reviewer: MinPanelWidth's icon-grid-derived
    // formula (2 * CellSize(iconSize)) is smaller than the hard Window.MinWidth="120" WPF coercion in
    // ChildWindow.xaml for every IconSize the slider's low end allows (16/20/24/28 => 72/84/96/108px).
    // Before the fix, ChildWindow.Resize_DragDelta computed Left from that too-small value while WPF
    // silently coerced Width up to 120, desyncing the panel's right edge from where the user actually
    // released the LEFT resize handle. This Theory sweeps the full IconSize slider range (16-96) and
    // must fail on the pre-fix formula (Math.Max missing) and pass once MinPanelWidth is clamped to
    // MinPanelSize.

    /// <summary>
    /// MinPanelSize must stay exactly 120 — the single source of truth also duplicated (by necessity,
    /// see the XAML comment) as the literal <c>MinWidth="120"</c> in ChildWindow.xaml, and referenced by
    /// ChildWindow.xaml.cs's own <c>MinPanelSize</c> and App.xaml.cs's lasso-drawn panel minimum. A
    /// change here needs a synchronized manual update to the ChildWindow.xaml literal.
    /// </summary>
    [Fact]
    public void MinPanelSize_Is120()
    {
        Assert.Equal(120d, GridMetrics.MinPanelSize);
    }

    /// <summary>
    /// MinPanelWidth must never return a value smaller than MinPanelSize, for every IconSize the slider
    /// allows (Minimum="16" through the settings-defined upper end, swept here in steps of 4) — the hard
    /// floor WPF's own Window.MinWidth="120" coercion imposes on Window.Width regardless of what this
    /// method computes. This is the exact regression that broke ChildWindow.Resize_DragDelta's Left/Width
    /// math for the LEFT resize handle (finalization MAJOR M-1): at IconSize 16/20/24/28 the unclamped
    /// icon-grid formula (2 * CellSize(iconSize) = 72/84/96/108) undershoots 120.
    /// </summary>
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
    public void MinPanelWidth_NeverBelowMinPanelSize(double iconSize)
    {
        Assert.True(GridMetrics.MinPanelWidth(iconSize) >= GridMetrics.MinPanelSize);
    }

    /// <summary>
    /// Above the IconSize threshold where the icon-grid formula (2 * CellSize(iconSize)) already exceeds
    /// MinPanelSize on its own, MinPanelWidth must return that unclamped icon-grid value verbatim — the
    /// Math.Max clamp added for the M-1 fix must never silently widen a panel's minimum beyond what the
    /// grid geometry actually requires once it is safely above the hard floor.
    /// </summary>
    [Theory]
    [InlineData(64d)]
    [InlineData(96d)]
    public void MinPanelWidth_AboveFloor_ReturnsUnclampedIconGridValue(double iconSize)
    {
        var cell = GridMetrics.CellSize(iconSize);
        Assert.Equal(2d * cell, GridMetrics.MinPanelWidth(iconSize), 1e-9);
    }
}
