using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="RollUpBehavior"/>'s reduced-motion branch
/// (docs/PLANS/workspace-grid-07-visual.md "reduced motion", <see cref="MotionState"/>).
/// </summary>
/// <remarks>
/// Runs on a dedicated STA thread (<see cref="StaTestRunner"/>) — <see cref="RollUpBehavior.ApplyRollUp"/>
/// constructs/starts a real <see cref="System.Windows.Media.Animation.DoubleAnimation"/> on the
/// "animations enabled" path, which — like every other WPF layout/animation-touching fixture in this
/// project (e.g. <see cref="Unit.Controls.AdaptiveIconGridPanelTests"/>) — needs the STA apartment WPF's
/// animation/timing engine is documented to require.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class RollUpBehaviorMotionTests
{
    [Fact]
    public void ApplyRollUp_ReducedMotion_SetsTargetHeightImmediately_NoAnimation()
    {
        StaTestRunner.Run(() =>
        {
            var originalMotion = MotionState.AnimationsEnabled;
            try
            {
                MotionState.AnimationsEnabled = false;

                var window = new Window();
                var behavior = BuildSut(window, out _, out _);
                behavior.ExpandedHeight = 300d;

                behavior.ApplyRollUp(collapsed: true);

                // Reduced motion: the target height (the collapsed title-bar height, 32) is applied
                // synchronously — no DoubleAnimation was started, so there is no "From" value to observe
                // transiently the way the animated path (below) has.
                Assert.Equal(32d, window.Height);
            }
            finally
            {
                MotionState.AnimationsEnabled = originalMotion;
            }
        });
    }

    [Fact]
    public void ApplyRollUp_ReducedMotion_RollingBackDown_SetsExpandedHeightImmediately()
    {
        StaTestRunner.Run(() =>
        {
            var originalMotion = MotionState.AnimationsEnabled;
            try
            {
                MotionState.AnimationsEnabled = false;

                var window = new Window();
                var behavior = BuildSut(window, out _, out _);
                behavior.ExpandedHeight = 300d;

                behavior.ApplyRollUp(collapsed: true);
                behavior.ApplyRollUp(collapsed: false);

                Assert.Equal(300d, window.Height);
            }
            finally
            {
                MotionState.AnimationsEnabled = originalMotion;
            }
        });
    }

    [Fact]
    public void ApplyRollUp_AnimationsEnabled_StartsFromExpandedHeightSynchronously_DoesNotJumpToTarget()
    {
        StaTestRunner.Run(() =>
        {
            var originalMotion = MotionState.AnimationsEnabled;
            try
            {
                MotionState.AnimationsEnabled = true;

                var window = new Window();
                var behavior = BuildSut(window, out _, out _);
                behavior.ExpandedHeight = 300d;

                behavior.ApplyRollUp(collapsed: true);

                // Animated path: the method sets Height to the START value (fromHeight = 300, the
                // expanded height) and then hands the property to a 150ms DoubleAnimation — it must NOT
                // have already reached the collapsed target (32) synchronously, the way the reduced-
                // motion branch above does. This is the observable behavioral difference between the two
                // branches without needing to wait for (or pump a Dispatcher through) the animation's
                // actual 150ms duration.
                Assert.Equal(300d, window.Height);
                Assert.NotEqual(32d, window.Height);
            }
            finally
            {
                MotionState.AnimationsEnabled = originalMotion;
            }
        });
    }

    [Fact]
    public void ApplyRollUp_ReducedMotion_DisablesResizeAffordanceAndCollapsesContentRow_SameAsAnimatedPath()
    {
        StaTestRunner.Run(() =>
        {
            var originalMotion = MotionState.AnimationsEnabled;
            try
            {
                MotionState.AnimationsEnabled = false;

                var window = new Window();
                var behavior = BuildSut(window, out var contentRow, out var affordanceMock);
                behavior.ExpandedHeight = 300d;

                behavior.ApplyRollUp(collapsed: true);

                // The reduced-motion branch returns EARLY (see RollUpBehavior.ApplyRollUp), but only
                // after the contentRow/fileListHost/resize-affordance side effects above it in the
                // method — this pins that early return did not accidentally skip those.
                Assert.Equal(new GridLength(0), contentRow.Height);
                affordanceMock.Verify(a => a.SetEnabled(false), Times.Once);
            }
            finally
            {
                MotionState.AnimationsEnabled = originalMotion;
            }
        });
    }

    private static RollUpBehavior BuildSut(Window window, out RowDefinition contentRow, out Mock<IResizeAffordance> affordanceMock)
    {
        contentRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        var fileListHost = new Border();
        affordanceMock = new Mock<IResizeAffordance>();
        var windowManagerMock = new Mock<IWindowManager>();

        return new RollUpBehavior(
            window,
            windowManagerMock.Object,
            contentRow,
            fileListHost,
            affordanceMock.Object,
            titleBarHeight: 32d,
            getModel: () => null);
    }
}
