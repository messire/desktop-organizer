using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// P04-R1/R2 (docs/PLANS/workspace-grid-03-04-integration.md §3.6, DoD D4-4): roll-up for a
/// <see cref="PanelGeometryMode.GridAligned"/> panel — the collapsed height is the workspace-grid
/// <see cref="WorkspaceGridMetrics.CollapsedHeaderSpan"/> extent (identical to
/// <see cref="PanelMetrics.HeaderHeight"/>, now by construction rather than by coincidence), and
/// roll-down restores the panel's persisted SPAN height rather than a raw remembered DIP value.
/// </summary>
/// <remarks>
/// Runs on an STA thread and with <see cref="MotionState.AnimationsEnabled"/> off — the deterministic
/// branch of <see cref="RollUpBehavior.ApplyRollUp"/> (the animated branch's own behaviour is already
/// covered by <c>RollUpBehaviorMotionTests</c>, which P04 does not touch).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class RollUpBehaviorGridAlignedTests
{
    private const double TitleBarHeight = PanelMetrics.HeaderHeight;
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // 39 units -> 39*8-8 = 304 DIP (PanelDefaults' own quantisation of the 300 DIP default height).
    private const int ExpandedHeightSpanUnits = 39;
    private const double ExpandedSpanHeightDip = (ExpandedHeightSpanUnits * U) - G;

    // ── DoD D4-4: the collapsed height is the header height, expressed on the grid — never a whole cell. ──

    /// <summary>Verifies that collapsing a <see cref="PanelGeometryMode.GridAligned"/> panel collapses it to the header height, expressed on the workspace grid rather than a whole cell.</summary>
    [Fact]
    public void ApplyRollUp_GridAligned_CollapsesToTheHeaderHeight_OnTheGrid()
    {
        var height = RunSta(harness =>
        {
            harness.Behavior.ApplyRollUp(collapsed: true);
            return harness.Window.Height;
        });

        Assert.Equal(PanelMetrics.HeaderHeight, height);
        Assert.Equal(
            WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, WorkspaceGridMetrics.CollapsedHeaderSpan), U, G).Height,
            height);
    }

    // ── P04-R2: roll-down restores the SPAN height, not the raw remembered DIP height. ───────────────

    /// <summary>Verifies that rolling a <see cref="PanelGeometryMode.GridAligned"/> panel back down restores the persisted span height, not the raw remembered DIP height.</summary>
    [Fact]
    public void ApplyRollUp_GridAligned_RollDownRestoresTheSpanHeight_NotTheRememberedDipHeight()
    {
        var height = RunSta(harness =>
        {
            harness.Behavior.ExpandedHeight = 333d; // deliberately NOT the span height, and not on the grid
            harness.Behavior.ApplyRollUp(collapsed: true);
            harness.Behavior.ApplyRollUp(collapsed: false);
            return harness.Window.Height;
        });

        Assert.Equal(ExpandedSpanHeightDip, height);
        Assert.NotEqual(333d, height);
    }

    // ── The poisoning guard (§3.6 remarks): the grid state is resolved against the panel's EXPANDED
    // bounds, so a lazy span migration can never capture the collapsed height. ───────────────────────

    /// <summary>Verifies that roll-down resolves the grid state against the panel's expanded bounds, never against the collapsed height, so a lazy span migration cannot be poisoned by it.</summary>
    [Fact]
    public void ApplyRollUp_GridAligned_ResolvesTheGridStateAgainstTheExpandedHeight_NotTheCollapsedOne()
    {
        var resolvedHeight = RunSta(harness =>
        {
            harness.Behavior.ExpandedHeight = ExpandedSpanHeightDip;
            harness.Behavior.ApplyRollUp(collapsed: true);
            harness.Window.Height = PanelMetrics.HeaderHeight; // the collapsed window, as it really is now

            harness.Behavior.ApplyRollUp(collapsed: false);
            return harness.GridHost.LastResolvedBounds.Height;
        });

        Assert.Equal(ExpandedSpanHeightDip, resolvedHeight);
        Assert.NotEqual(PanelMetrics.HeaderHeight, resolvedHeight);
    }

    // ── P04-R1: collapsing pins the expanded size as a span first (through the host, i.e. through
    // PanelGeometryWriter), so a panel whose span was missing still rolls back down correctly. ───────

    /// <summary>Verifies that collapsing pins the expanded size as a span before collapsing, through the host, so a panel whose span was missing still rolls back down correctly.</summary>
    [Fact]
    public void ToggleRollUp_GridAligned_PinsTheExpandedSpanBeforeCollapsing_ThenRestoresIt()
    {
        var (spanAfterCollapse, heightAfterCollapse, heightAfterExpand) = RunSta(harness =>
        {
            harness.Model.WidthSpanUnits = null;
            harness.Model.HeightSpanUnits = null;
            harness.Window.Height = ExpandedSpanHeightDip;
            harness.Behavior.ExpandedHeight = ExpandedSpanHeightDip;

            harness.Behavior.ToggleRollUp();
            var collapsed = harness.Window.Height;
            var span = harness.Model.HeightSpanUnits;

            harness.Behavior.ToggleRollUp();

            return (span, collapsed, harness.Window.Height);
        });

        Assert.Equal(ExpandedHeightSpanUnits, spanAfterCollapse);
        Assert.Equal(PanelMetrics.HeaderHeight, heightAfterCollapse);
        Assert.Equal(ExpandedSpanHeightDip, heightAfterExpand);
    }

    // ── LegacyFreeform (no grid host) keeps the pre-P04 pair exactly: title-bar height / remembered
    // ExpandedHeight, and no span is ever written. ───────────────────────────────────────────────────

    /// <summary>Verifies that a <see cref="PanelGeometryMode.LegacyFreeform"/> panel keeps the pre-P04 title-bar-height/remembered-<c>ExpandedHeight</c> pair and never writes a span.</summary>
    [Fact]
    public void ApplyRollUp_LegacyPanel_KeepsTheRememberedDipHeights_AndWritesNoSpan()
    {
        var (collapsed, expanded, commits) = RunSta(
            harness =>
            {
                harness.Behavior.ExpandedHeight = 333d;
                harness.Behavior.ApplyRollUp(collapsed: true);
                var collapsedHeight = harness.Window.Height;
                harness.Behavior.ApplyRollUp(collapsed: false);

                return (collapsedHeight, harness.Window.Height, harness.GridHost.Commits.Count);
            },
            gridAligned: false);

        Assert.Equal(TitleBarHeight, collapsed);
        Assert.Equal(333d, expanded);
        Assert.Equal(0, commits);
    }

    private static T RunSta<T>(Func<Harness, T> body, bool gridAligned = true)
    {
        T result = default!;
        StaTestRunner.Run(() =>
        {
            var originalMotion = MotionState.AnimationsEnabled;
            try
            {
                MotionState.AnimationsEnabled = false;
                result = body(new Harness(gridAligned));
            }
            finally
            {
                MotionState.AnimationsEnabled = originalMotion;
            }
        });

        return result;
    }

    private sealed class Harness
    {
        public Harness(bool gridAligned)
        {
            Model = new ChildWindowModel
            {
                GeometryMode = gridAligned ? PanelGeometryMode.GridAligned : PanelGeometryMode.LegacyFreeform,
                WidthSpanUnits = gridAligned ? 33 : null,
                HeightSpanUnits = gridAligned ? ExpandedHeightSpanUnits : null,
            };

            GridHost = new FakePanelSizingGridHost
            {
                State = gridAligned ? FakePanelSizingGridHost.GridAligned() : PanelGridState.Legacy,
                Model = Model,
            };

            Window = new Window { Left = 96d, Top = 200d, Width = 264d, Height = ExpandedSpanHeightDip };

            Behavior = new RollUpBehavior(
                Window,
                Mock.Of<IWindowManager>(),
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new Border(),
                Mock.Of<IResizeAffordance>(),
                TitleBarHeight,
                () => Model,
                GridHost);
        }

        public ChildWindowModel Model { get; }

        public FakePanelSizingGridHost GridHost { get; }

        public Window Window { get; }

        public RollUpBehavior Behavior { get; }
    }
}
