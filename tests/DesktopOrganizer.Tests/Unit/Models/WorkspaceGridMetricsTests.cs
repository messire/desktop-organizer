using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="WorkspaceGridMetrics"/> — the workspace panel grid's (ADR-028, System B)
/// default constants and validation. See <see cref="WorkspaceLayoutPolicyTests"/> for the pure
/// snap/quantisation geometry that consumes these constants.
/// </summary>
[Trait(UnitTests, "UI")]
public class WorkspaceGridMetricsTests
{
    // ----- Constants -----

    /// <summary>ADR-028 starting hypothesis: <c>U = 8 DIP</c>.</summary>
    [Fact]
    public void UnitSize_Is8Dip()
        => Assert.Equal(8d, WorkspaceGridMetrics.UnitSize);

    /// <summary>ADR-028 default: <c>G = U = 8 DIP</c>.</summary>
    [Fact]
    public void DefaultGap_Is8Dip()
        => Assert.Equal(8d, WorkspaceGridMetrics.DefaultGap);

    /// <summary>The default gap must itself satisfy the invariant it validates for callers.</summary>
    [Fact]
    public void DefaultGap_IsWholeMultipleOfDefaultUnitSize()
        => WorkspaceGridMetrics.ValidateGap(WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap); // does not throw

    /// <summary>
    /// <see cref="WorkspaceGridMetrics.CollapsedHeaderSpan"/> is not a new number — it must always equal
    /// <see cref="PanelMetrics.HeaderHeight"/> expressed in <see cref="WorkspaceGridMetrics.UnitSize"/>-units
    /// (ADR-028 table).
    /// </summary>
    /// <remarks>
    /// <b>Expectation deliberately changed by P04 (§3.6, OQ-4 = A; DoD D4-4).</b>
    /// <list type="bullet">
    /// <item>OLD (P02): <c>CollapsedHeaderSpan * UnitSize == HeaderHeight</c> — i.e. <c>4 * 8 == 32</c>.
    /// That equation ignores the gap the "cell + gap" model subtracts from every extent, so span 4
    /// actually rendered as <c>4*8 - 8 = 24</c> DIP, 8 DIP shorter than the real collapsed height.</item>
    /// <item>NEW (P04): <c>SpansToBounds(CollapsedHeaderSpan).Height == HeaderHeight</c> — i.e.
    /// <c>5*8 - 8 == 32</c>. This is the equation every consumer actually evaluates.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void CollapsedHeaderSpan_ExtentMatchesPanelMetricsHeaderHeight()
    {
        var extent = WorkspaceLayoutPolicy.SpansToBounds(
            new GridSpan(1, WorkspaceGridMetrics.CollapsedHeaderSpan),
            WorkspaceGridMetrics.UnitSize,
            WorkspaceGridMetrics.DefaultGap);

        Assert.Equal(PanelMetrics.HeaderHeight, extent.Height);

        // The pre-P04 form, kept as an explicit marker of what changed and why (it is now off by
        // exactly one gap — the whole point of OQ-4 = A).
        Assert.Equal(
            PanelMetrics.HeaderHeight + WorkspaceGridMetrics.DefaultGap,
            WorkspaceGridMetrics.CollapsedHeaderSpan * WorkspaceGridMetrics.UnitSize);
    }

    /// <summary>
    /// DoD D4-4 restated at the metrics level: a rolled-up panel occupies whole workspace-grid units, so
    /// its bottom edge lands on a grid line — no "collapsed height is an exception" carve-out anywhere.
    /// </summary>
    [Fact]
    public void CollapsedHeaderSpan_IsAWholeNumberOfUnits_AndSmallerThanTheExpandedMinimum()
    {
        Assert.True(WorkspaceGridMetrics.CollapsedHeaderSpan > 0);
        Assert.True(WorkspaceGridMetrics.CollapsedHeaderSpan < WorkspaceGridMetrics.MinHeightSpanExpanded);
    }

    /// <summary>
    /// P04 §3 item 6 (closes ADR-028 Open Question 1): the workspace-grid width floor must be at least
    /// the WPF <c>Window.MinWidth</c> coercion <c>ChildWindow.xaml</c> applies — otherwise the window is
    /// silently wider than its own span says, and every later <c>BoundsToSpans</c> disagrees with the
    /// stored span. <c>GridMetrics.MinPanelSize</c> is the C# constant that XAML literal is required (by
    /// <c>ChildWindow.xaml</c>'s own header comment) to equal.
    /// </summary>
    [Fact]
    public void MinSpanExpanded_Width_IsAtLeastTheWindowMinWidthCoercion()
    {
        var minWidth = WorkspaceLayoutPolicy
            .SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap)
            .Width;

        Assert.True(minWidth >= GridMetrics.MinPanelSize, $"MinSpanExpanded width {minWidth} DIP is below the {GridMetrics.MinPanelSize} DIP window floor.");
        Assert.Equal(GridMetrics.MinPanelSize, minWidth); // they are deliberately the SAME number, not merely compatible
    }

    /// <summary>
    /// The second half of ADR-028 Open Question 1: the floor must also fit the panel header's own button
    /// row (4 x <see cref="PanelMetrics.HeaderButtonSize"/>), before the accent indicator, the content
    /// inset or any title text.
    /// </summary>
    [Fact]
    public void MinSpanExpanded_Width_FitsTheHeaderButtonRow()
    {
        var minWidth = WorkspaceLayoutPolicy
            .SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap)
            .Width;

        Assert.True(minWidth >= 4d * PanelMetrics.HeaderButtonSize);
    }

    /// <summary>Roll-up never changes width (ADR-028 table: "MinWidthSpanCollapsed = MinWidthSpanExpanded").</summary>
    [Fact]
    public void MinWidthSpanCollapsed_EqualsMinWidthSpanExpanded()
        => Assert.Equal(WorkspaceGridMetrics.MinWidthSpanExpanded, WorkspaceGridMetrics.MinWidthSpanCollapsed);

    [Fact]
    public void MinSpanExpanded_ComposedFromWidthAndHeightConstants()
    {
        var span = WorkspaceGridMetrics.MinSpanExpanded;
        Assert.Equal(WorkspaceGridMetrics.MinWidthSpanExpanded, span.WidthUnits);
        Assert.Equal(WorkspaceGridMetrics.MinHeightSpanExpanded, span.HeightUnits);
    }

    [Fact]
    public void MinSpanCollapsed_ComposedFromWidthAndHeaderConstants()
    {
        var span = WorkspaceGridMetrics.MinSpanCollapsed;
        Assert.Equal(WorkspaceGridMetrics.MinWidthSpanCollapsed, span.WidthUnits);
        Assert.Equal(WorkspaceGridMetrics.CollapsedHeaderSpan, span.HeightUnits);
    }

    // ----- SpanForPreset / DefaultPresetSpans -----
    [Theory]
    [InlineData(PanelSizePreset.Small, 20, 20)]
    [InlineData(PanelSizePreset.Medium, 32, 30)]
    [InlineData(PanelSizePreset.Large, 44, 40)]
    [InlineData(PanelSizePreset.ExtraLarge, 60, 50)]
    public void SpanForPreset_ReturnsAdr028Table(PanelSizePreset preset, int expectedWidth, int expectedHeight)
    {
        var span = WorkspaceGridMetrics.SpanForPreset(preset);

        Assert.Equal(expectedWidth, span.WidthUnits);
        Assert.Equal(expectedHeight, span.HeightUnits);
    }

    /// <summary>
    /// Every preset is meant to describe an EXPANDED panel — none may fall below
    /// <see cref="WorkspaceGridMetrics.MinSpanExpanded"/> (covers the "Max" side of the Min/Max
    /// requirement: <see cref="PanelSizePreset.ExtraLarge"/> is the largest named shortcut, still
    /// consistent with the minimum floor).
    /// </summary>
    [Theory]
    [InlineData(PanelSizePreset.Small)]
    [InlineData(PanelSizePreset.Medium)]
    [InlineData(PanelSizePreset.Large)]
    [InlineData(PanelSizePreset.ExtraLarge)]
    public void DefaultPresetSpans_AllMeetMinimumExpandedSpan(PanelSizePreset preset)
        => WorkspaceGridMetrics.ValidateSpan(WorkspaceGridMetrics.SpanForPreset(preset), WorkspaceGridMetrics.MinSpanExpanded); // does not throw

    // ----- ValidateGap -----
    [Theory]
    [InlineData(8d, 8d)]
    [InlineData(8d, 0d)]
    [InlineData(8d, 16d)]
    [InlineData(4d, 8d)]
    [InlineData(0.5d, 1.0d)]
    public void ValidateGap_ValidCombination_DoesNotThrow(double unitSize, double gap)
        => WorkspaceGridMetrics.ValidateGap(unitSize, gap);

    [Theory]
    [InlineData(8d, 3d)]
    [InlineData(8d, 12d)]
    [InlineData(3d, 8d)]
    public void ValidateGap_GapNotWholeMultipleOfUnitSize_Throws(double unitSize, double gap)
        => Assert.Throws<ArgumentException>(() => WorkspaceGridMetrics.ValidateGap(unitSize, gap));

    [Fact]
    public void ValidateGap_NegativeGap_Throws()
        => Assert.Throws<ArgumentException>(() => WorkspaceGridMetrics.ValidateGap(8d, -8d));

    [Theory]
    [InlineData(0d)]
    [InlineData(-8d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ValidateGap_NonPositiveOrNonFiniteUnitSize_Throws(double unitSize)
        => Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceGridMetrics.ValidateGap(unitSize, 8d));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValidateGap_NonFiniteGap_Throws(double gap)
        => Assert.Throws<ArgumentException>(() => WorkspaceGridMetrics.ValidateGap(8d, gap));

    // ----- ValidateSpan -----
    [Fact]
    public void ValidateSpan_MeetsMinimum_DoesNotThrow()
        => WorkspaceGridMetrics.ValidateSpan(new GridSpan(12, 12), new GridSpan(12, 12));

    [Fact]
    public void ValidateSpan_BelowMinimumWidth_Throws()
        => Assert.Throws<ArgumentException>(() => WorkspaceGridMetrics.ValidateSpan(new GridSpan(11, 20), new GridSpan(12, 12)));

    [Fact]
    public void ValidateSpan_BelowMinimumHeight_Throws()
        => Assert.Throws<ArgumentException>(() => WorkspaceGridMetrics.ValidateSpan(new GridSpan(20, 11), new GridSpan(12, 12)));

    [Theory]
    [InlineData(0, 20)]
    [InlineData(20, 0)]
    [InlineData(-1, 20)]
    [InlineData(20, -1)]
    public void ValidateSpan_NonPositiveDimension_Throws(int width, int height)
        => Assert.Throws<ArgumentException>(() => WorkspaceGridMetrics.ValidateSpan(new GridSpan(width, height), new GridSpan(1, 1)));
}
