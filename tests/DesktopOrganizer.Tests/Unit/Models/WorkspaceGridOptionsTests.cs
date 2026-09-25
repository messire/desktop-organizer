using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="WorkspaceGridOptions"/> — the configurable, validatable bundle of workspace
/// grid parameters (ADR-028). <see cref="WorkspaceLayoutPolicy"/> itself never requires this type (its
/// methods take plain <see cref="double"/>/<see cref="GridSpan"/> parameters); this record exists purely
/// for callers that want to persist/validate the whole parameter set at once.
/// </summary>
[Trait(UnitTests, "UI")]
public class WorkspaceGridOptionsTests
{
    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Default"/>'s field values match the
    /// corresponding <see cref="WorkspaceGridMetrics"/> constants.
    /// </summary>
    [Fact]
    public void Default_MatchesWorkspaceGridMetricsConstants()
    {
        var options = WorkspaceGridOptions.Default;

        Assert.Equal(WorkspaceGridMetrics.UnitSize, options.UnitSize);
        Assert.Equal(WorkspaceGridMetrics.DefaultGap, options.Gap);
        Assert.Equal(WorkspaceGridMetrics.MinWidthSpanExpanded, options.MinWidthSpanExpanded);
        Assert.Equal(WorkspaceGridMetrics.MinHeightSpanExpanded, options.MinHeightSpanExpanded);
        Assert.Equal(WorkspaceGridMetrics.CollapsedHeaderSpan, options.CollapsedHeaderSpan);
        Assert.Same(WorkspaceGridMetrics.DefaultPresetSpans, options.PresetSpans);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Default"/> itself passes
    /// <see cref="WorkspaceGridOptions.Validate"/> without throwing.
    /// </summary>
    [Fact]
    public void Default_PassesValidate()
        => WorkspaceGridOptions.Default.Validate(); // does not throw

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.MinSpanExpanded"/> is composed exactly from the
    /// record's own <c>MinWidthSpanExpanded</c>/<c>MinHeightSpanExpanded</c> fields.
    /// </summary>
    [Fact]
    public void MinSpanExpanded_ComposedFromFields()
    {
        var options = WorkspaceGridOptions.Default with { MinWidthSpanExpanded = 10, MinHeightSpanExpanded = 14 };

        Assert.Equal(new GridSpan(10, 14), options.MinSpanExpanded);
    }

    /// <summary>Roll-up never changes width — width always matches <see cref="WorkspaceGridOptions.MinWidthSpanExpanded"/>.</summary>
    [Fact]
    public void MinSpanCollapsed_WidthMatchesExpandedWidth()
    {
        var options = WorkspaceGridOptions.Default with { MinWidthSpanExpanded = 10, CollapsedHeaderSpan = 4 };

        Assert.Equal(new GridSpan(10, 4), options.MinSpanCollapsed);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws when <c>Gap</c> is not a
    /// whole multiple of <c>UnitSize</c>.
    /// </summary>
    [Fact]
    public void Validate_GapNotWholeMultipleOfUnitSize_Throws()
    {
        var options = WorkspaceGridOptions.Default with { Gap = 3d };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws when
    /// <c>MinWidthSpanExpanded</c> is zero or negative.
    /// </summary>
    /// <param name="minWidth">A non-positive value for <c>MinWidthSpanExpanded</c>.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMinWidthSpanExpanded_Throws(int minWidth)
    {
        var options = WorkspaceGridOptions.Default with { MinWidthSpanExpanded = minWidth };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws when
    /// <c>MinHeightSpanExpanded</c> is zero or negative.
    /// </summary>
    /// <param name="minHeight">A non-positive value for <c>MinHeightSpanExpanded</c>.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMinHeightSpanExpanded_Throws(int minHeight)
    {
        var options = WorkspaceGridOptions.Default with { MinHeightSpanExpanded = minHeight };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws when
    /// <c>CollapsedHeaderSpan</c> is zero or negative.
    /// </summary>
    /// <param name="collapsedHeaderSpan">A non-positive value for <c>CollapsedHeaderSpan</c>.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveCollapsedHeaderSpan_Throws(int collapsedHeaderSpan)
    {
        var options = WorkspaceGridOptions.Default with { CollapsedHeaderSpan = collapsedHeaderSpan };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>A rolled-up panel must never be taller than the expanded minimum.</summary>
    [Fact]
    public void Validate_CollapsedHeaderSpanExceedsMinHeightSpanExpanded_Throws()
    {
        var options = WorkspaceGridOptions.Default with { MinHeightSpanExpanded = 12, CollapsedHeaderSpan = 13 };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws when <c>PresetSpans</c> is
    /// an empty dictionary.
    /// </summary>
    [Fact]
    public void Validate_EmptyPresetSpans_Throws()
    {
        var options = WorkspaceGridOptions.Default with { PresetSpans = new Dictionary<PanelSizePreset, GridSpan>() };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws when a present preset's own
    /// span value falls below the minimum expanded span, even though every enum member is present.
    /// </summary>
    [Fact]
    public void Validate_PresetBelowMinimumExpandedSpan_Throws()
    {
        // Fix-cycle 1: keeps every PanelSizePreset entry present (only Small's VALUE violates the
        // minimum) so this test unambiguously exercises the per-entry minimum check below, not the
        // separate completeness check added by Validate_IncompletePresetSpans_MissingEnumMember_Throws.
        var options = WorkspaceGridOptions.Default with
        {
            PresetSpans = new Dictionary<PanelSizePreset, GridSpan>(WorkspaceGridMetrics.DefaultPresetSpans)
            {
                [PanelSizePreset.Small] = new GridSpan(1, 1), // below the 12x12 default minimum
            },
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> throws
    /// <see cref="ArgumentNullException"/> when <c>PresetSpans</c> is null.
    /// </summary>
    [Fact]
    public void Validate_NullPresetSpans_Throws()
    {
        var options = WorkspaceGridOptions.Default with { PresetSpans = null! };

        Assert.Throws<ArgumentNullException>(options.Validate);
    }

    /// <summary>Fix-cycle 1 MINOR: an incomplete table (missing an enum member) must fail loudly here, not later as a KeyNotFoundException.</summary>
    [Fact]
    public void Validate_IncompletePresetSpans_MissingEnumMember_Throws()
    {
        var options = WorkspaceGridOptions.Default with
        {
            PresetSpans = new Dictionary<PanelSizePreset, GridSpan>
            {
                [PanelSizePreset.Small] = new GridSpan(20, 20),
                [PanelSizePreset.Medium] = new GridSpan(32, 30),
                [PanelSizePreset.Large] = new GridSpan(44, 40),

                // ExtraLarge deliberately omitted.
            },
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Fix-cycle 1 MINOR: the "cell + gap" extent formula (<c>n*UnitSize - Gap</c>) is only guaranteed
    /// positive for the SMALLEST allowed span when <c>Gap &lt; UnitSize * smallestSpanDimension</c> — a
    /// gap that large would make a rolled-up panel's DIP height zero or negative.
    /// </summary>
    /// <remarks>
    /// <b>Expectation moved (not weakened) by P04's <c>CollapsedHeaderSpan</c> 4 -> 5 change (§3.6,
    /// OQ-4 = A).</b> The boundary these two tests straddle is <c>CollapsedHeaderSpan * UnitSize</c>: it
    /// was 32 DIP (4*8) and is now 40 DIP (5*8), so the literals 32/24 used before would both sit on the
    /// same (valid) side of it today. Both cases are now derived from the constant itself rather than
    /// hard-coded, so the next change to it cannot silently turn either test vacuous.
    /// </remarks>
    [Fact]
    public void Validate_GapNotLessThanSmallestSpanExtent_Throws()
    {
        var smallestSpanExtent = WorkspaceGridMetrics.CollapsedHeaderSpan * WorkspaceGridMetrics.UnitSize;
        var options = WorkspaceGridOptions.Default with { Gap = smallestSpanExtent };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridOptions.Validate"/> does not throw when the gap is
    /// strictly less than the smallest span's own DIP extent.
    /// </summary>
    [Fact]
    public void Validate_GapLessThanSmallestSpanExtent_DoesNotThrow()
    {
        var smallestSpanExtent = WorkspaceGridMetrics.CollapsedHeaderSpan * WorkspaceGridMetrics.UnitSize;
        var options = WorkspaceGridOptions.Default with { Gap = smallestSpanExtent - WorkspaceGridMetrics.UnitSize };

        options.Validate(); // does not throw
    }
}
