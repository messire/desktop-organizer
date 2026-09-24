using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// P04 §3.3 (docs/PLANS/workspace-grid-03-04-integration.md): the <see cref="PanelGeometryMode.GridAligned"/>
/// branch of <see cref="ResizeConstraintsFactory.BuildConstraints"/> — the single point where ADR-028's
/// System B enters the resize pipeline's configuration (Amendment MAJOR-4 п.1/п.3).
/// </summary>
/// <remarks>
/// The <see cref="PanelGeometryMode.LegacyFreeform"/> half of this factory's contract is already pinned by
/// <c>NativePanelResizeControllerTests</c>'s <c>BuildConstraints_*</c> characterization tests, which are
/// deliberately NOT edited by P04 (plan §5.4: "байт-в-байт" is a machine-checked property, not a claim) —
/// this file only adds the new branch plus the explicit "the default argument changes nothing" proof.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeConstraintsFactoryGridAlignedTests
{
    private const double TitleBarHeight = ChildWindow.TitleBarHeight;

    // ── P04-C2: BOTH axes take Apply's FREE branch — AdaptiveGridLayout.SnapExtent (System C) must
    // never quantise a System-B panel extent, regardless of ViewMode/SnapResizeToIconGrid. ───────────
    [Theory]
    [InlineData(PanelViewMode.Grid, true)]
    [InlineData(PanelViewMode.Grid, false)]
    [InlineData(PanelViewMode.List, true)]
    [InlineData(PanelViewMode.List, false)]
    public void GridAligned_BothAxesAreFree_RegardlessOfViewModeOrSnapSetting(PanelViewMode viewMode, bool snapEnabled)
    {
        var model = new ChildWindowModel { ViewMode = viewMode, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled, TitleBarHeight, FakePanelSizingGridHost.GridAligned());

        Assert.Equal(0d, constraints.SnapCellWidth);
        Assert.Equal(0d, constraints.SnapCellHeight);
    }

    // ── P04-C3: the floors are the MINIMUM SPAN in DIP, not GridMetrics' icon-derived floors. ────────
    [Fact]
    public void GridAligned_MinWidthAndMinHeight_ComeFromTheMinimumSpan()
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d };
        var state = FakePanelSizingGridHost.GridAligned();
        var expected = WorkspaceLayoutPolicy.SpansToBounds(state.MinSpan, state.UnitSize, state.Gap);

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, state);

        Assert.Equal(expected.Width, constraints.MinWidth);
        Assert.Equal(expected.Height, constraints.MinHeight);
    }

    // ── DoD D4-2, the rule ADR-028 calls absolute: NOTHING about a GridAligned panel's bounds may be a
    // function of IconSize — including the floor the resize gesture clamps against. ──────────────────
    [Fact]
    public void GridAligned_MinWidth_IsIdenticalAcrossEveryIconSize()
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var widths = new[] { 16d, 32d, 48d, 96d, 47.3d }
            .Select(iconSize => ResizeConstraintsFactory.BuildConstraints(
                new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = iconSize },
                snapEnabled: true,
                TitleBarHeight,
                state).MinWidth)
            .Distinct()
            .ToList();

        Assert.Single(widths);

        // Not vacuous: the legacy floor genuinely DOES move with IconSize, which is the bug this closes.
        Assert.NotEqual(GridMetrics.MinPanelWidth(16d), GridMetrics.MinPanelWidth(96d));
    }

    // ── A rolled-up panel resolves to MinSpanCollapsed (PanelGridStateResolver rule 4) — the floor must
    // follow it, otherwise a collapsed panel could not stay collapsed through a width resize. ────────
    [Fact]
    public void GridAligned_RolledUp_UsesTheCollapsedMinimumSpan()
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d, IsRolledUp = true };
        var state = FakePanelSizingGridHost.GridAligned(minSpan: WorkspaceGridMetrics.MinSpanCollapsed);
        var expected = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanCollapsed, state.UnitSize, state.Gap);

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, state);

        Assert.Equal(expected.Height, constraints.MinHeight);
        Assert.True(constraints.MinHeight < WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, state.UnitSize, state.Gap).Height);
    }

    // ── P04-C4: the chrome fields stay exactly what P05's policy-aware code computes — P04 adds a
    // branch NEXT TO them, it does not touch them (plan §0.2). ───────────────────────────────────────
    [Theory]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    [InlineData(IconLayoutPolicy.FixedGap)]
    public void GridAligned_ChromeIsUnchangedFromTheLegacyComputation(IconLayoutPolicy policy)
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d, IconLayoutPolicy = policy };

        var legacy = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight);
        var grid = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, FakePanelSizingGridHost.GridAligned());

        Assert.Equal(legacy.HorizontalChrome, grid.HorizontalChrome);
        Assert.Equal(legacy.VerticalChrome, grid.VerticalChrome);
        Assert.Equal(legacy.TitleBarHeight, grid.TitleBarHeight);
    }

    // ── The new parameter's default is PanelGridState.Legacy: every pre-P04 call site keeps producing
    // byte-identical constraints (this is what lets the existing characterization suite stay untouched). ──
    [Theory]
    [InlineData(PanelViewMode.Grid, true)]
    [InlineData(PanelViewMode.Grid, false)]
    [InlineData(PanelViewMode.List, true)]
    [InlineData(PanelViewMode.List, false)]
    public void OmittedGridState_ProducesExactlyTheSameConstraintsAsAnExplicitLegacyState(PanelViewMode viewMode, bool snapEnabled)
    {
        var model = new ChildWindowModel { ViewMode = viewMode, IconSize = 48d };

        var implicitDefault = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled, TitleBarHeight);
        var explicitLegacy = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled, TitleBarHeight, PanelGridState.Legacy);

        Assert.Equal(implicitDefault, explicitLegacy);
    }

    // ── Defence in depth: a hand-built "grid-aligned" state with a degenerate unit size can only come
    // from outside PanelGridStateResolver — fall back to the legacy shape rather than emit a 0/NaN floor. ──
    [Theory]
    [InlineData(0d)]
    [InlineData(-8d)]
    [InlineData(double.NaN)]
    public void GridAlignedWithDegenerateUnitSize_FallsBackToTheLegacyShape(double unitSize)
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d };
        var broken = FakePanelSizingGridHost.GridAligned() with { UnitSize = unitSize };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, broken);

        Assert.Equal(ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight), constraints);
    }
}
