using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Integration-level coverage for <c>Views/ChildWindow.ContentSizing.cs</c> (P06,
/// <c>docs/fix-prompts/06-fit-content.md</c>, ADR-029 Решение 7) — a REAL <see cref="ChildWindow"/>,
/// exercised through its actual context-menu Click handlers (invoked via reflection, since they are
/// private event handlers — the same shape <see cref="ChildWindowInitializeTests"/> would use if it
/// needed to reach a Click handler instead of a public model-property setter) and its real
/// <see cref="BaseViewModel.FileItems"/>/<see cref="ChildWindowModel.PropertyChanged"/>
/// subscriptions. The STA/resource-chain harness below is duplicated from
/// <see cref="ChildWindowInitializeTests"/> (itself duplicated from
/// <c>Unit.Resources.ResourceDictionaryMergeTests</c>) — see that class's remarks for the full
/// rationale; this project's established precedent is to duplicate this small harness per test class
/// rather than share it.
/// </summary>
/// <remarks>
/// <see cref="RunOnStaThread{T}"/> does not pump a <see cref="Dispatcher"/> message loop WHILE
/// <c>func</c> runs (only afterward, forever, so the owning thread never tears down WPF's thread-affine
/// resource cache mid-suite) — so <c>ChildWindow.ContentSizing.cs</c>'s debounced
/// <c>Window.SizeChanged</c> trigger (a <see cref="DispatcherTimer"/>) never actually ticks inside these
/// tests. That trigger is exercised only by inspection/manual verify (see
/// <c>docs/PLANS/workspace-grid-06-fit-content.md</c>) — every OTHER trigger below fires synchronously as
/// a direct side effect of a C# event (<see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>/
/// <see cref="System.Collections.Specialized.INotifyCollectionChanged.CollectionChanged"/>) or a direct
/// method call, neither of which needs a running message loop.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowContentSizingTests
{
    // ----- "Fit panel to content" -----

    /// <summary>
    /// Verifies that clicking "Fit panel to content" sets the window height to the exact analytic
    /// formula value for various item counts, including the empty-panel drop-zone floor.
    /// </summary>
    /// <param name="itemCount">The number of items seeded into the grid-mode panel.</param>
    [Theory]
    [InlineData(0)] // empty panel -> the one-row drop-zone floor.
    [InlineData(1)]
    [InlineData(8)] // several rows, at IconSize=48/windowWidth below.
    public void FitPanelToContentMenu_Click_SetsHeightToTheFormulaValue_ForVariousItemCounts(int itemCount)
    {
        const double windowWidth = 352d; // 3-column LegacyStretch width at IconSize=48 (F-3-correct).
        var height = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount, windowWidth, windowHeight: 500d);
            harness.Window.Initialize(model);

            Invoke(harness.Window, "FitPanelToContentMenu_Click");
            return harness.Window.Height;
        });

        // Recomputed independently, outside the STA thread, from the exact same public formula.
        var model2 = BuildGridModel(itemCount, windowWidth, windowHeight: 500d);
        Assert.Equal(ExpectedFitHeight(model2, windowWidth, itemCount), height);
    }

    /// <summary>
    /// Verifies that clicking "Fit panel to content" while the panel is in
    /// <see cref="PanelViewMode.List"/> is a no-op, since there is no List-mode fit formula.
    /// </summary>
    [Fact]
    public void FitPanelToContentMenu_Click_ListMode_IsANoOp()
    {
        const double windowWidth = 260d;
        var height = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = new ChildWindowModel
            {
                ViewMode = PanelViewMode.List,
                Position = new DrawingPoint(0, 0),
                Size = new DrawingSize((int)windowWidth, 500),
            };
            harness.Window.Initialize(model);

            Invoke(harness.Window, "FitPanelToContentMenu_Click");
            return harness.Window.Height;
        });

        Assert.Equal(500d, height); // Q3 = B: no List-mode formula — untouched.
    }

    /// <summary>
    /// ADR-026 gating: verifies that clicking "Fit panel to content" while the layout is locked is a
    /// no-op, via a defensive check inside the handler itself.
    /// </summary>
    [Fact]
    public void FitPanelToContentMenu_Click_WhenLayoutLocked_IsANoOp()
    {
        const double windowWidth = 352d;
        var height = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            harness.LayoutEditModeMock.SetupProperty(l => l.IsLayoutLocked, true);
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d);
            harness.Window.Initialize(model);

            Invoke(harness.Window, "FitPanelToContentMenu_Click");
            return harness.Window.Height;
        });

        Assert.Equal(500d, height); // ADR-026 gating — defensive check inside the handler itself.
    }

    /// <summary>
    /// Verifies that clicking "Fit panel to content" applies the fit height even while the panel is in
    /// <see cref="PanelSizingMode.Manual"/>, without switching the sizing mode itself.
    /// </summary>
    [Fact]
    public void FitPanelToContentMenu_Click_WorksEvenInManualSizingMode()
    {
        const double windowWidth = 352d;
        var height = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.Manual);
            harness.Window.Initialize(model);

            Invoke(harness.Window, "FitPanelToContentMenu_Click");
            return (harness.Window.Height, model.SizingMode);
        });

        var (newHeight, sizingModeAfter) = height;
        Assert.NotEqual(500d, newHeight); // the one-shot action DID apply, despite Manual mode.
        Assert.Equal(PanelSizingMode.Manual, sizingModeAfter); // and it never flips SizingMode itself.
    }

    /// <summary>
    /// ADR-029 Решение 7's roll-up row: while <see cref="ChildWindowModel.IsRolledUp"/>, the fit
    /// height is written onto the model's persisted <see cref="ChildWindowModel.Size"/> (for
    /// restore) but <see cref="FrameworkElement.Height"/> — the live, currently-collapsed value — is left alone.
    /// Sets <c>IsRolledUp</c> directly on the model AFTER <c>Initialize</c> (rather than at construction)
    /// so no roll-up COLLAPSE ANIMATION is ever triggered (only <c>RollUpBehavior.ToggleRollUp</c> starts
    /// one — a raw model flag flip is otherwise inert) — isolating this test to ONLY the branch inside
    /// <c>ChildWindow.ContentSizing.cs</c>'s own <c>ApplyHeight</c>, not <c>RollUpBehavior</c>'s animation
    /// timing (which needs a pumping <see cref="Dispatcher"/> this harness deliberately does not run
    /// during the test body — see this class's own remarks).
    /// </summary>
    [Fact]
    public void FitPanelToContentMenu_Click_WhileRolledUp_UpdatesModelSizeButNeverWindowHeight()
    {
        const double windowWidth = 352d;
        var (windowHeightAfter, modelSizeHeightAfter) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d);
            harness.Window.Initialize(model);

            model.IsRolledUp = true; // inert on its own (see remarks) — never collapses Window.Height.

            Invoke(harness.Window, "FitPanelToContentMenu_Click");
            return (harness.Window.Height, model.Size.Height);
        });

        var expected = ExpectedFitHeight(BuildGridModel(8, windowWidth, 500d), windowWidth, 8);

        Assert.Equal(500d, windowHeightAfter); // untouched — the whole point of this test.
        Assert.Equal((int)Math.Round(expected), modelSizeHeightAfter); // the REMEMBERED expanded height was updated instead.
        Assert.NotEqual(500, modelSizeHeightAfter); // sanity: the fit height for 8 items really does differ from the arbitrary 500 seed.
    }

    // ----- "Auto-size to content" -----

    /// <summary>
    /// Verifies that clicking "Auto-size to content" switches the panel to
    /// <see cref="PanelSizingMode.AutoContent"/> and immediately recomputes the height, rather than
    /// leaving it inert until the next unrelated trigger.
    /// </summary>
    [Fact]
    public void AutoSizeToContentMenu_Click_TurningOn_ImmediatelyFitsHeight()
    {
        const double windowWidth = 352d;
        var (height, sizingMode) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.Manual);
            harness.Window.Initialize(model);

            Invoke(harness.Window, "AutoSizeToContentMenu_Click");
            return (harness.Window.Height, model.SizingMode);
        });

        Assert.Equal(PanelSizingMode.AutoContent, sizingMode);
        Assert.NotEqual(500d, height); // immediately fit, not left inert until the next unrelated trigger.
    }

    /// <summary>
    /// Verifies that clicking "Auto-size to content" while already active switches the panel to
    /// <see cref="PanelSizingMode.Manual"/> without recomputing or changing the current height.
    /// </summary>
    [Fact]
    public void AutoSizeToContentMenu_Click_TurningOff_PreservesCurrentHeight()
    {
        const double windowWidth = 352d;
        var (heightAfterInit, heightAfterToggleOff, sizingModeAfter) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();

            // Already AutoContent when loaded — Initialize deliberately never force-recomputes on load
            // (see ChildWindow.ContentSizing.cs's AttachContentSizing remarks), so the persisted 500 DIP
            // height survives Initialize unchanged, exactly like a Manual panel would.
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);
            var heightAfterInit = harness.Window.Height;

            Invoke(harness.Window, "AutoSizeToContentMenu_Click"); // AutoContent -> Manual.
            return (heightAfterInit, harness.Window.Height, model.SizingMode);
        });

        Assert.Equal(500d, heightAfterInit); // no-jump-on-load invariant, even for a persisted AutoContent panel.
        Assert.Equal(heightAfterInit, heightAfterToggleOff); // turning OFF never recomputes/changes height.
        Assert.Equal(PanelSizingMode.Manual, sizingModeAfter);
    }

    // ----- PanelSizingMode.AutoContent passive recompute triggers -----

    /// <summary>
    /// ADR-029 Решение 7: verifies that adding an item to a <see cref="PanelSizingMode.Manual"/> panel
    /// never changes its height.
    /// </summary>
    [Fact]
    public void Manual_AddingAnItemAfterLoad_NeverChangesHeight()
    {
        const double windowWidth = 352d;
        var (before, after) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 1, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.Manual);
            harness.Window.Initialize(model);
            var before = harness.Window.Height;

            harness.ViewModel.FileItems.Add(new DesktopOrganizer.Core.FileItem(@"C:\nonexistent\extra.txt", null));

            return (before, harness.Window.Height);
        });

        Assert.Equal(500d, before);
        Assert.Equal(before, after); // ADR-029 Решение 7: "Manual никогда не меняет высоту".
    }

    /// <summary>
    /// Verifies that adding an item to a <see cref="PanelSizingMode.AutoContent"/> panel recomputes its
    /// height to match the new item count.
    /// </summary>
    [Fact]
    public void AutoContent_AddingAnItemAfterLoad_RecomputesHeight()
    {
        const double windowWidth = 352d;
        var (before, after, itemCountAfter) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 1, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);
            var before = harness.Window.Height;

            harness.ViewModel.FileItems.Add(new DesktopOrganizer.Core.FileItem(@"C:\nonexistent\extra.txt", null));

            return (before, harness.Window.Height, harness.ViewModel.FileItems.Count);
        });

        Assert.Equal(500d, before); // load itself never recomputes (see AutoSizeToContentMenu_Click_TurningOff test).
        var expectedAfter = ExpectedFitHeight(BuildGridModel(itemCountAfter, windowWidth, 500d), windowWidth, itemCountAfter);
        Assert.Equal(expectedAfter, after);
        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Verifies that changing the icon size on a <see cref="PanelSizingMode.AutoContent"/> panel
    /// recomputes the height using the width AFTER the icon-size-triggered grid re-snap, not the stale
    /// pre-snap width.
    /// </summary>
    [Fact]
    public void AutoContent_IconSizeChange_RecomputesHeight_UsingThePostSnapWidth()
    {
        var (before, afterWidth, afterHeight) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 3, windowWidth: 352d, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent, iconSize: 48d);
            harness.Window.Initialize(model);
            var before = harness.Window.Height;

            model.IconSize = 64d; // triggers OnModelPropertyChanged's SnapWidthToGrid FIRST (subscribed earlier), then this partial's own recompute.

            return (before, harness.Window.Width, harness.Window.Height);
        });

        Assert.Equal(500d, before);

        // The recomputed height must be derived from the WIDTH AFTER the IconSize-triggered re-snap
        // (subscription-order requirement — see AttachContentSizing's remarks), not the stale
        // pre-snap width — pin this by recomputing the expectation from afterWidth, not the original 352.
        var expected = ExpectedFitHeight(BuildGridModel(3, afterWidth, 500d, iconSize: 64d), afterWidth, 3);
        Assert.Equal(expected, afterHeight);
    }

    /// <summary>
    /// Verifies that switching a <see cref="PanelSizingMode.AutoContent"/> panel to
    /// <see cref="PanelViewMode.List"/> short-circuits the auto-fit recompute without throwing or
    /// changing the height.
    /// </summary>
    [Fact]
    public void AutoContent_SwitchingViewModeToList_StopsRecomputing_ButNeverThrows()
    {
        const double windowWidth = 352d;
        var (afterInit, afterSwitch) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);
            var afterInit = harness.Window.Height;

            model.ViewMode = PanelViewMode.List; // MaybeAutoFit's ViewMode gate short-circuits — no exception, no change.

            return (afterInit, harness.Window.Height);
        });

        Assert.Equal(500d, afterInit);
        Assert.Equal(afterInit, afterSwitch);
    }

    /// <summary>
    /// Verifies that fitting a <see cref="PanelGeometryMode.GridAligned"/> panel to content rounds the
    /// height up to a whole workspace-grid row span, and derives the width span from the panel's real
    /// width rather than the fallback minimum span.
    /// </summary>
    [Fact]
    public void AutoContent_GridAligned_RoundsUpToWorkspaceRowSpan()
    {
        const double windowWidth = 352d;
        var monitors = new[]
        {
            new MonitorInfo(@"\\.\DISPLAY1", new Rect(0d, 0d, 1920d, 1080d), new Rect(0d, 0d, 1920d, 1040d), IsPrimary: true),
        };

        var (heightAfter, heightUnitsExpected, widthUnitsAfter) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build(monitors);
            var model = BuildGridModel(
                itemCount: 8,
                windowWidth,
                windowHeight: 500d,
                sizingMode: PanelSizingMode.AutoContent,
                geometryMode: PanelGeometryMode.GridAligned);
            harness.Window.Initialize(model);

            Invoke(harness.Window, "FitPanelToContentMenu_Click"); // deterministic, one-shot — avoids relying on load-time recompute semantics for this geometry-rounding assertion.
            return (harness.Window.Height, model.HeightSpanUnits, model.WidthSpanUnits);
        });

        var raw = PanelContentSizer.ComputeRawContentHeight(8, 48d, windowWidth, IconLayoutPolicy.LegacyStretch, SystemParameters.VerticalScrollBarWidth);
        var expectedUnits = PanelContentSizer.RoundHeightUnitsForGridAligned(raw);
        var expectedHeight = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, expectedUnits), WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap).Height;

        Assert.Equal(expectedHeight, heightAfter);
        Assert.Equal(expectedUnits, heightUnitsExpected);

        // P06 fix-cycle MAJOR-2: widthUnits must come from BoundsToSpans(352) == 45
        // (Ceiling((352 + 8) / 8)) — never from WorkspaceGridMetrics.MinWidthSpanExpanded (16, the old,
        // buggy fallback that silently shrank a real 352 DIP-wide panel to a 120 DIP span).
        Assert.Equal(45, widthUnitsAfter);
        Assert.NotEqual(WorkspaceGridMetrics.MinWidthSpanExpanded, widthUnitsAfter);
    }

    /// <summary>
    /// P06 fix-cycle MAJOR-2 (adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"): reproduces the exact
    /// fail-safe path the old code mishandled — a <see cref="PanelGeometryMode.GridAligned"/> panel whose
    /// <see cref="PanelGridStateResolver.Resolve"/> call degrades to
    /// <see cref="PanelGridState.Legacy"/> for this frame (here: zero monitors, the harness's own
    /// default — see <see cref="ChildWindowContentSizingHarness.Build"/>'s remarks). The fit height must
    /// still apply (as a <see cref="PanelGeometryMode.LegacyFreeform"/>-rounded value) and must NEVER
    /// write a span derived from <see cref="WorkspaceGridMetrics.MinWidthSpanExpanded"/> — before the fix,
    /// <c>ApplyFitHeight</c> ignored <c>Resolve</c>'s return value entirely and fell through to exactly
    /// that wrong span.
    /// </summary>
    [Fact]
    public void AutoContent_GridAligned_NoMonitors_FallsBackToLegacyFreeformRounding_NeverWritesMinimumSpan()
    {
        const double windowWidth = 352d;
        var (heightAfter, widthSpanAfter, heightSpanAfter) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build(); // no monitors — the fail-safe path.
            var model = BuildGridModel(
                itemCount: 8,
                windowWidth,
                windowHeight: 500d,
                sizingMode: PanelSizingMode.AutoContent,
                geometryMode: PanelGeometryMode.GridAligned);
            harness.Window.Initialize(model);

            Invoke(harness.Window, "FitPanelToContentMenu_Click");
            return (harness.Window.Height, model.WidthSpanUnits, model.HeightSpanUnits);
        });

        var raw = PanelContentSizer.ComputeRawContentHeight(8, 48d, windowWidth, IconLayoutPolicy.LegacyStretch, SystemParameters.VerticalScrollBarWidth);
        var expectedHeight = PanelContentSizer.RoundForLegacyFreeform(raw);

        Assert.Equal(expectedHeight, heightAfter);
        Assert.Null(widthSpanAfter); // never migrated/written — Resolve's own "leave the model untouched on this fail-safe path" contract.
        Assert.Null(heightSpanAfter);
    }

    // ----- "Apply to all panels" -----

    /// <summary>
    /// Verifies that when the user confirms, clicking "Apply icon layout to all panels" broadcasts the
    /// current panel's <see cref="IconLayoutPolicy"/> to every other panel returned by
    /// <see cref="IWindowManager.GetAllChildWindows"/>.
    /// </summary>
    [Fact]
    public void ApplyIconLayoutPolicyToAllMenu_Click_Confirmed_BroadcastsToEveryPanel()
    {
        var otherPanelA = new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch };
        var otherPanelB = new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch };

        RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 1, windowWidth: 260d, windowHeight: 300d, policy: IconLayoutPolicy.FixedGap);
            harness.Window.Initialize(model);

            harness.WindowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([model, otherPanelA, otherPanelB]);
            harness.ConfirmationMock.Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Windows.MessageBoxImage>())).Returns(true);

            Invoke(harness.Window, "ApplyIconLayoutPolicyToAllMenu_Click");
            return 0;
        });

        Assert.Equal(IconLayoutPolicy.FixedGap, otherPanelA.IconLayoutPolicy);
        Assert.Equal(IconLayoutPolicy.FixedGap, otherPanelB.IconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that when the user declines confirmation, clicking "Apply icon layout to all panels"
    /// never broadcasts the current panel's icon layout policy to other panels.
    /// </summary>
    [Fact]
    public void ApplyIconLayoutPolicyToAllMenu_Click_Declined_NeverBroadcasts()
    {
        var otherPanel = new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch };

        RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 1, windowWidth: 260d, windowHeight: 300d, policy: IconLayoutPolicy.FixedGap);
            harness.Window.Initialize(model);

            harness.WindowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([model, otherPanel]);
            harness.ConfirmationMock.Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Windows.MessageBoxImage>())).Returns(false);

            Invoke(harness.Window, "ApplyIconLayoutPolicyToAllMenu_Click");
            return 0;
        });

        Assert.Equal(IconLayoutPolicy.LegacyStretch, otherPanel.IconLayoutPolicy); // untouched — the dialog was declined.
    }

    /// <summary>
    /// Verifies that when the user confirms, clicking "Apply sizing mode to all panels" broadcasts the
    /// current panel's <see cref="PanelSizingMode"/> to every other panel.
    /// </summary>
    [Fact]
    public void ApplySizingModeToAllMenu_Click_Confirmed_BroadcastsToEveryPanel()
    {
        var otherPanel = new ChildWindowModel { SizingMode = PanelSizingMode.Manual };

        RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 1, windowWidth: 260d, windowHeight: 300d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            harness.WindowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([model, otherPanel]);
            harness.ConfirmationMock.Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Windows.MessageBoxImage>())).Returns(true);

            Invoke(harness.Window, "ApplySizingModeToAllMenu_Click");
            return 0;
        });

        Assert.Equal(PanelSizingMode.AutoContent, otherPanel.SizingMode);
    }

    // ----- P06 fix-cycle MAJOR-1: IsLayoutLocked guards both "Apply to all panels…" broadcasts -----

    /// <summary>
    /// Verifies that clicking "Apply icon layout to all panels" while the layout is locked is a no-op
    /// that never even reaches the confirmation dialog.
    /// </summary>
    [Fact]
    public void ApplyIconLayoutPolicyToAllMenu_Click_WhenLayoutLocked_IsANoOp()
    {
        var otherPanel = new ChildWindowModel { IconLayoutPolicy = IconLayoutPolicy.LegacyStretch };

        RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            harness.LayoutEditModeMock.SetupProperty(l => l.IsLayoutLocked, true);
            var model = BuildGridModel(itemCount: 1, windowWidth: 260d, windowHeight: 300d, policy: IconLayoutPolicy.FixedGap);
            harness.Window.Initialize(model);

            harness.WindowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([model, otherPanel]);
            harness.ConfirmationMock.Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Windows.MessageBoxImage>())).Returns(true);

            Invoke(harness.Window, "ApplyIconLayoutPolicyToAllMenu_Click");
            return 0;
        });

        Assert.Equal(IconLayoutPolicy.LegacyStretch, otherPanel.IconLayoutPolicy); // untouched — never even reached the confirmation dialog.
    }

    /// <summary>
    /// Verifies that clicking "Apply sizing mode to all panels" while the layout is locked is a no-op.
    /// </summary>
    [Fact]
    public void ApplySizingModeToAllMenu_Click_WhenLayoutLocked_IsANoOp()
    {
        var otherPanel = new ChildWindowModel { SizingMode = PanelSizingMode.Manual };

        RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            harness.LayoutEditModeMock.SetupProperty(l => l.IsLayoutLocked, true);
            var model = BuildGridModel(itemCount: 1, windowWidth: 260d, windowHeight: 300d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            harness.WindowManagerMock.Setup(w => w.GetAllChildWindows()).Returns([model, otherPanel]);
            harness.ConfirmationMock.Setup(c => c.Confirm(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Windows.MessageBoxImage>())).Returns(true);

            Invoke(harness.Window, "ApplySizingModeToAllMenu_Click");
            return 0;
        });

        Assert.Equal(PanelSizingMode.Manual, otherPanel.SizingMode); // untouched.
    }

    // ----- P06 fix-cycle MAJOR-2: GridAligned fail-safe (no monitors) never writes the minimum span -----
    // (see AutoContent_GridAligned_RoundsUpToWorkspaceRowSpan and
    // AutoContent_GridAligned_NoMonitors_FallsBackToLegacyFreeformRounding_NeverWritesMinimumSpan above,
    // grouped with the rest of the "Fit panel to content"/AutoContent suite for locality.)

    // ----- P06 fix-cycle MAJOR-3: the debounce tick recharges instead of applying mid-gesture -----

    /// <summary>
    /// Verifies that an auto-fit debounce tick while a resize gesture is active recharges the timer
    /// instead of applying the fit height, and applies it once the gesture ends.
    /// </summary>
    [Fact]
    public void AutoFitDebounceTick_WhileResizeGestureActive_RechargesTimerInsteadOfApplying()
    {
        const double windowWidth = 352d;
        var (heightWhileResizing, heightAfterIdle) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            var tracker = GetInteractionTracker(harness.Window);
            tracker.TryEnter(PanelInteractionState.Resizing);
            InvokeDebounceTick(harness.Window);
            var heightWhileResizing = harness.Window.Height;

            tracker.Exit(PanelInteractionState.Resizing);
            InvokeDebounceTick(harness.Window);
            var heightAfterIdle = harness.Window.Height;

            return (heightWhileResizing, heightAfterIdle);
        });

        Assert.Equal(500d, heightWhileResizing); // recharged, not applied, while the gesture is still active.
        var expected = ExpectedFitHeight(BuildGridModel(8, windowWidth, 500d), windowWidth, 8);
        Assert.Equal(expected, heightAfterIdle); // applied once the gesture ends.
        Assert.NotEqual(500d, heightAfterIdle);
    }

    /// <summary>
    /// Verifies that an auto-fit debounce tick while a drag gesture is active recharges the timer
    /// instead of applying the fit height, and applies it once the gesture ends.
    /// </summary>
    [Fact]
    public void AutoFitDebounceTick_WhileDragGestureActive_RechargesTimerInsteadOfApplying()
    {
        const double windowWidth = 352d;
        var (heightWhileDragging, heightAfterIdle) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            var tracker = GetInteractionTracker(harness.Window);
            tracker.TryEnter(PanelInteractionState.Dragging);
            InvokeDebounceTick(harness.Window);
            var heightWhileDragging = harness.Window.Height;

            tracker.Exit(PanelInteractionState.Dragging);
            InvokeDebounceTick(harness.Window);
            var heightAfterIdle = harness.Window.Height;

            return (heightWhileDragging, heightAfterIdle);
        });

        Assert.Equal(500d, heightWhileDragging);
        var expected = ExpectedFitHeight(BuildGridModel(8, windowWidth, 500d), windowWidth, 8);
        Assert.Equal(expected, heightAfterIdle);
    }

    // ----- P06 fix-cycle MAJOR-4: a completed HEIGHT-affecting resize demotes AutoContent to Manual -----

    /// <summary>
    /// Verifies that <see cref="IPanelSizingGridHost.NotifyHeightResizeCommitted"/> demotes a
    /// <see cref="PanelSizingMode.AutoContent"/> panel to <see cref="PanelSizingMode.Manual"/> without
    /// itself changing the window height.
    /// </summary>
    [Fact]
    public void NotifyHeightResizeCommitted_WhenAutoContent_TransitionsToManual_WithoutTouchingHeight()
    {
        const double windowWidth = 352d;
        var (sizingModeAfter, heightAfter) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            // The single seam PanelResizeController/NativePanelResizeController call through
            // PanelSizingGridSupport.NotifyHeightResizeCommitted at the end of a HEIGHT-affecting gesture
            // — exercised here directly via the public IPanelSizingGridHost surface (an explicit
            // interface implementation is still callable through the interface, no reflection needed).
            ((IPanelSizingGridHost)harness.Window).NotifyHeightResizeCommitted();

            return (model.SizingMode, harness.Window.Height);
        });

        Assert.Equal(PanelSizingMode.Manual, sizingModeAfter);
        Assert.Equal(500d, heightAfter); // the transition itself never changes height (Manual never auto-fits).
    }

    /// <summary>
    /// Verifies that <see cref="IPanelSizingGridHost.NotifyHeightResizeCommitted"/> is a no-op when the
    /// panel is already <see cref="PanelSizingMode.Manual"/>.
    /// </summary>
    [Fact]
    public void NotifyHeightResizeCommitted_WhenAlreadyManual_IsANoOp()
    {
        const double windowWidth = 352d;
        var sizingModeAfter = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.Manual);
            harness.Window.Initialize(model);

            ((IPanelSizingGridHost)harness.Window).NotifyHeightResizeCommitted();

            return model.SizingMode;
        });

        Assert.Equal(PanelSizingMode.Manual, sizingModeAfter);
    }

    /// <summary>
    /// Verifies that <see cref="IPanelSizingGridHost.NotifyHeightResizeCommitted"/> records a
    /// <see cref="IGestureDiagnostics.SizingModeChanged"/> event for the AutoContent-to-Manual
    /// transition, tagged <c>"HeightResizeCommitted"</c>.
    /// </summary>
    [Fact]
    public void NotifyHeightResizeCommitted_WhenAutoContent_RecordsSizingModeChangedDiagnostics()
    {
        const double windowWidth = 352d;
        Mock<IGestureDiagnostics>? diagnosticsMock = null;

        RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            diagnosticsMock = harness.DiagnosticsMock;
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            ((IPanelSizingGridHost)harness.Window).NotifyHeightResizeCommitted();
            return 0;
        });

        diagnosticsMock!.Verify(
            d => d.SizingModeChanged(It.IsAny<Guid?>(), PanelSizingMode.AutoContent, PanelSizingMode.Manual, "HeightResizeCommitted"),
            Times.Once);
    }

    // ----- P06 fix-cycle MINOR m-1: the FIRST IsVisibleChanged(true) is skipped (no jump on first launch) -----

    /// <summary>
    /// Verifies that the FIRST <c>IsVisibleChanged(true)</c> transition after load is skipped (no jump
    /// on first launch), but a second such transition does recompute the height.
    /// </summary>
    [Fact]
    public void AutoContent_FirstIsVisibleChangedTrue_DoesNotRecompute_SecondTransitionDoes()
    {
        const double windowWidth = 352d;
        var (afterFirst, afterSecond) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 8, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            InvokeVisibleChanged(harness.Window, true);
            var afterFirst = harness.Window.Height;

            InvokeVisibleChanged(harness.Window, true);
            var afterSecond = harness.Window.Height;

            return (afterFirst, afterSecond);
        });

        Assert.Equal(500d, afterFirst); // first transition — skipped, no jump on first launch.
        var expected = ExpectedFitHeight(BuildGridModel(8, windowWidth, 500d), windowWidth, 8);
        Assert.Equal(expected, afterSecond); // every later transition (e.g. page switch) still recomputes.
    }

    // ----- P06 fix-cycle MINOR m-2: defers instead of applying while a roll-up animation is in flight -----

    /// <summary>
    /// Verifies that an auto-fit trigger while a roll-up animation is in flight defers applying the new
    /// height until the animation ends, then applies it via the same debounce timer.
    /// </summary>
    [Fact]
    public void MaybeAutoFit_WhileRollUpAnimationActive_DefersUntilAnimationEnds()
    {
        const double windowWidth = 352d;
        var (heightWhileAnimating, heightAfterAnimationEnds) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowContentSizingHarness.Build();
            var model = BuildGridModel(itemCount: 1, windowWidth, windowHeight: 500d, sizingMode: PanelSizingMode.AutoContent);
            harness.Window.Initialize(model);

            SetRollUpIsAnimating(harness.Window, true);
            harness.ViewModel.FileItems.Add(new DesktopOrganizer.Core.FileItem(@"C:\nonexistent\extra.txt", null));
            var heightWhileAnimating = harness.Window.Height;

            SetRollUpIsAnimating(harness.Window, false);
            InvokeDebounceTick(harness.Window); // the deferred recompute is retried through the same timer.
            var heightAfterAnimationEnds = harness.Window.Height;

            return (heightWhileAnimating, heightAfterAnimationEnds);
        });

        Assert.Equal(500d, heightWhileAnimating); // deferred — never raced the (simulated) in-flight animation.
        var expected = ExpectedFitHeight(BuildGridModel(2, windowWidth, 500d), windowWidth, 2);
        Assert.Equal(expected, heightAfterAnimationEnds);
    }

    // ----- helpers -----
    private static ChildWindowModel BuildGridModel(
        int itemCount,
        double windowWidth,
        double windowHeight,
        double iconSize = 48d,
        IconLayoutPolicy policy = IconLayoutPolicy.LegacyStretch,
        PanelSizingMode sizingMode = PanelSizingMode.Manual,
        PanelGeometryMode geometryMode = PanelGeometryMode.LegacyFreeform)
    {
        var model = new ChildWindowModel
        {
            ViewMode = PanelViewMode.Grid,
            IconSize = iconSize,
            IconLayoutPolicy = policy,
            SizingMode = sizingMode,
            GeometryMode = geometryMode,
            Position = new DrawingPoint(0, 0),
            Size = new DrawingSize((int)windowWidth, (int)windowHeight),
        };

        for (var i = 0; i < itemCount; i++)
            model.FilePaths.Add($@"C:\nonexistent\item-{i}.txt"); // battila paths never crash loading (project invariant) — only Exists/IsDirectory are false.

        return model;
    }

    private static double ExpectedFitHeight(ChildWindowModel model, double windowWidth, int itemCount) =>
        PanelContentSizer.FitHeight(
            itemCount,
            model.IconSize,
            windowWidth,
            model.IconLayoutPolicy,
            SystemParameters.VerticalScrollBarWidth,
            model.GeometryMode);

    private static void Invoke(ChildWindow window, string methodName)
    {
        var method = typeof(ChildWindow).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(ChildWindow), methodName);
        method.Invoke(window, [null, new RoutedEventArgs()]);
    }

    /// <summary>P06 fix-cycle MAJOR-3: invokes the private debounce-timer <c>Tick</c> handler directly — the STA harness never pumps a <see cref="Dispatcher"/> loop, so the real <see cref="DispatcherTimer"/> never ticks on its own (see this class's own remarks).</summary>
    private static void InvokeDebounceTick(ChildWindow window)
    {
        var method = typeof(ChildWindow).GetMethod("OnAutoFitDebounceTick", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(ChildWindow), "OnAutoFitDebounceTick");
        method.Invoke(window, [null, EventArgs.Empty]);
    }

    /// <summary>P06 fix-cycle MAJOR-3: reaches the shared ADR-021 <see cref="PanelInteractionTracker"/> every gesture in this window reports through, so a test can simulate "a resize/drag gesture is still active" without driving the real Thumb/native gesture machinery.</summary>
    private static PanelInteractionTracker GetInteractionTracker(ChildWindow window)
    {
        var field = typeof(ChildWindow).GetField("_interactionTracker", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(ChildWindow), "_interactionTracker");
        return (PanelInteractionTracker)field.GetValue(window)!;
    }

    /// <summary>P06 fix-cycle MINOR m-1: invokes the private <c>IsVisibleChanged</c> handler directly with a real <see cref="DependencyPropertyChangedEventArgs"/> — driving an actual <c>Show()</c>/<c>Hide()</c> cycle would need a pumped message loop this harness deliberately does not run mid-test (see this class's own remarks).</summary>
    private static void InvokeVisibleChanged(ChildWindow window, bool newValue)
    {
        var method = typeof(ChildWindow).GetMethod("OnWindowIsVisibleChangedForContentSizing", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(ChildWindow), "OnWindowIsVisibleChangedForContentSizing");
        var args = new DependencyPropertyChangedEventArgs(UIElement.IsVisibleProperty, !newValue, newValue);
        method.Invoke(window, [window, args]);
    }

    /// <summary>P06 fix-cycle MINOR m-2: forces <c>RollUpBehavior.IsAnimating</c> deterministically — the real 150ms roll-up/roll-down animation depends on <c>MotionState.AnimationsEnabled</c>, which reads a system-wide, environment-dependent setting this test must not rely on.</summary>
    private static void SetRollUpIsAnimating(ChildWindow window, bool value)
    {
        var rollUpField = typeof(ChildWindow).GetField("_rollUpBehavior", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(ChildWindow), "_rollUpBehavior");
        var rollUpBehavior = rollUpField.GetValue(window)
            ?? throw new InvalidOperationException("ChildWindow._rollUpBehavior was null.");
        var animatingField = rollUpBehavior.GetType().GetField("_isAnimating", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("RollUpBehavior", "_isAnimating");
        animatingField.SetValue(rollUpBehavior, value);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // STA/resource-chain infrastructure — duplicated from ChildWindowInitializeTests, see this class's
    // own remarks for why.
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    private sealed class ChildWindowContentSizingHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private ChildWindowContentSizingHarness(
            Application app,
            ResourceDictionary? originalResources,
            bool ownsApplication,
            ChildWindow window,
            ChildWindowViewModel viewModel,
            Mock<IWindowManager> windowManagerMock,
            Mock<IConfirmationService> confirmationMock,
            Mock<ILayoutEditModeService> layoutEditModeMock,
            Mock<IMonitorService> monitorServiceMock,
            Mock<IGestureDiagnostics> diagnosticsMock)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
            ViewModel = viewModel;
            WindowManagerMock = windowManagerMock;
            ConfirmationMock = confirmationMock;
            LayoutEditModeMock = layoutEditModeMock;
            MonitorServiceMock = monitorServiceMock;
            DiagnosticsMock = diagnosticsMock;
        }

        public ChildWindow Window { get; }

        public ChildWindowViewModel ViewModel { get; }

        public Mock<IWindowManager> WindowManagerMock { get; }

        public Mock<IConfirmationService> ConfirmationMock { get; }

        public Mock<ILayoutEditModeService> LayoutEditModeMock { get; }

        public Mock<IMonitorService> MonitorServiceMock { get; }

        public Mock<IGestureDiagnostics> DiagnosticsMock { get; }

        /// <param name="monitors">
        /// P06 fix-cycle MAJOR-2 regression coverage: <see langword="null"/> (the default) reproduces the
        /// "no monitors at all" fail-safe path (<see cref="PanelGridStateResolver.Resolve"/>'s own
        /// Rule 2) — every OTHER test in this class that does not care about
        /// <see cref="PanelGeometryMode.GridAligned"/>'s monitor-dependent branch relies on this
        /// default. Pass an explicit monitor list to exercise the actually-grid-aligned path instead.
        /// </param>
        public static ChildWindowContentSizingHarness Build(IReadOnlyList<MonitorInfo>? monitors = null)
        {
            // M-2 fix (P08 fix cycle 2): ownership decided by reference-comparing
            // WpfApplicationHost.EnsureApplication()'s return value against the pre-call ambient state,
            // not by a separate "was it null" probe — see ResourceDictionaryMergeTests's own remarks.
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            MergeResourceDictionaries(app);

            var layoutEditModeMock = new Mock<ILayoutEditModeService>();
            layoutEditModeMock.SetupProperty(l => l.IsLayoutLocked, false);
            layoutEditModeMock.Setup(l => l.CanEnterEditLayout).Returns(() => !layoutEditModeMock.Object.IsLayoutLocked);
            app.Resources["LayoutEditMode"] = layoutEditModeMock.Object;

            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((System.Windows.Media.ImageSource)null!);

            var viewModel = new ChildWindowViewModel(
                iconProvider.Object,
                Mock.Of<IFileLauncher>(),
                Mock.Of<IFolderWatcherFactory>(),
                Mock.Of<IPanelItemStorageService>(),
                Mock.Of<IFileItemRenameService>(),
                Mock.Of<IAppSettings>(),
                Mock.Of<IPanelSelectionCoordinator>(),
                Mock.Of<IGestureDiagnostics>());

            var appSettings = new Mock<IAppSettings>();
            appSettings.SetupProperty(s => s.NativePanelSizing, false);
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, true);
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, string.Empty);

            var windowManagerMock = new Mock<IWindowManager>();
            var confirmationMock = new Mock<IConfirmationService>();

            var monitorServiceMock = new Mock<IMonitorService>();
            monitorServiceMock.Setup(m => m.GetMonitors()).Returns(monitors ?? Array.Empty<MonitorInfo>());

            var diagnosticsMock = new Mock<IGestureDiagnostics>();

            var window = new ChildWindow(
                viewModel,
                windowManagerMock.Object,
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                diagnosticsMock.Object,
                Mock.Of<INativePanelSizingService>(),
                layoutEditModeMock.Object,
                monitorServiceMock.Object,
                confirmationMock.Object,
                Mock.Of<IPanelCustomizeService>());

            return new ChildWindowContentSizingHarness(app, originalResources, ownsApplication, window, viewModel, windowManagerMock, confirmationMock, layoutEditModeMock, monitorServiceMock, diagnosticsMock);
        }

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }

        private static void MergeResourceDictionaries(Application app)
        {
            var merged = app.Resources.MergedDictionaries;
            foreach (var relativePath in new[]
                     {
                         "/Resources/Styles/Colors.xaml",
                         "/Resources/Styles/Spacing.xaml",
                         "/Resources/Styles/Brushes.xaml",
                         "/Resources/Styles/Typography.xaml",
                         "/Resources/Styles/Icons.xaml",
                         "/Resources/Styles/Controls.xaml",
                         "/Resources/Styles/Panels.xaml",
                         "/Resources/Templates.xaml",
                     })
            {
                merged.Add(new ResourceDictionary { Source = new Uri($"/DesktopOrganizer.UI;component{relativePath}", UriKind.Relative) });
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="func"/> to completion on a brand-new STA thread and returns its result.
    /// </summary>
    /// <remarks>
    /// M-2 fix (P08 fix cycle 2): delegates to <see cref="StaTestRunner.Run{T}"/> instead of this
    /// class's own former hand-rolled duplicate of the STA-thread/ownership machinery, which kept an
    /// owning thread pumping (<see cref="Dispatcher.Run()"/>) forever with NO
    /// <see cref="Dispatcher.UnhandledException"/> absorber — a leaked callback throwing on that
    /// pumped-forever thread was an unguarded process kill. <see cref="StaTestRunner.Run{T}"/> already
    /// closes that gap.
    /// </remarks>
    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);
}
