using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="NativeSizingAffordance"/> — the native <c>WM_SIZING</c>-path
/// implementation of <see cref="IResizeAffordance"/> (docs/PLANS/phase-2-native-panel-sizing.md §1.6,
/// R-9). Pure in-memory bool flag, no WPF types involved when no grips element is supplied — fully testable headless.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingAffordanceTests
{
    /// <summary>Verifies that a freshly-constructed <see cref="NativeSizingAffordance"/> defaults to enabled.</summary>
    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        var affordance = new NativeSizingAffordance();

        Assert.True(affordance.IsEnabled);
    }

    /// <summary>Verifies that calling <see cref="NativeSizingAffordance.SetEnabled(bool)"/> with <see langword="false"/> makes <see cref="NativeSizingAffordance.IsEnabled"/> false.</summary>
    [Fact]
    public void SetEnabled_False_MakesIsEnabledFalse()
    {
        var affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);

        Assert.False(affordance.IsEnabled);
    }

    /// <summary>Verifies that calling <see cref="NativeSizingAffordance.SetEnabled(bool)"/> with <see langword="true"/> after a prior <see langword="false"/> call makes <see cref="NativeSizingAffordance.IsEnabled"/> true again.</summary>
    [Fact]
    public void SetEnabled_TrueAfterFalse_MakesIsEnabledTrueAgain()
    {
        var affordance = new NativeSizingAffordance();
        affordance.SetEnabled(false);

        affordance.SetEnabled(true);

        Assert.True(affordance.IsEnabled);
    }

    /// <summary>Verifies that calling <see cref="NativeSizingAffordance.SetEnabled(bool)"/> twice with the same value is idempotent.</summary>
    [Fact]
    public void SetEnabled_SameValueTwice_IsIdempotent()
    {
        var affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);
        affordance.SetEnabled(false);

        Assert.False(affordance.IsEnabled);
    }

    /// <summary>Verifies that a supplied grip-visuals element is made visible and non-hit-testable on construction (the affordance starts enabled).</summary>
    [Fact]
    public void Constructor_WithGripVisuals_ShowsThemAndForcesHitTestInvisible()
    {
        var (visibility, hitTestVisible) = StaTestRunner.Run(() =>
        {
            var grips = new System.Windows.Controls.Grid { Visibility = System.Windows.Visibility.Collapsed };

            _ = new NativeSizingAffordance(grips);

            return (grips.Visibility, grips.IsHitTestVisible);
        });

        Assert.Equal(System.Windows.Visibility.Visible, visibility);
        Assert.False(hitTestVisible);
    }

    /// <summary>Verifies that <see cref="NativeSizingAffordance.SetEnabled(bool)"/> mirrors onto the grip visuals' visibility and never makes them hit-testable.</summary>
    [Fact]
    public void SetEnabled_MirrorsOntoGripVisuals_AndNeverMakesThemHitTestVisible()
    {
        var states = StaTestRunner.Run(() =>
        {
            var grips = new System.Windows.Controls.Grid();
            var affordance = new NativeSizingAffordance(grips);

            affordance.SetEnabled(false);
            var disabled = (grips.Visibility, grips.IsHitTestVisible);
            affordance.SetEnabled(true);
            var enabled = (grips.Visibility, grips.IsHitTestVisible);

            return (disabled, enabled);
        });

        Assert.Equal(System.Windows.Visibility.Collapsed, states.disabled.Visibility);
        Assert.False(states.disabled.IsHitTestVisible);
        Assert.Equal(System.Windows.Visibility.Visible, states.enabled.Visibility);
        Assert.False(states.enabled.IsHitTestVisible);
    }

    /// <summary>Verifies that <see cref="NativeSizingAffordance"/> can be consumed entirely through the <see cref="IResizeAffordance"/> interface.</summary>
    [Fact]
    public void NativeSizingAffordance_ImplementsIResizeAffordance()
    {
        IResizeAffordance affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);

        Assert.False(((NativeSizingAffordance)affordance).IsEnabled);
    }
}
