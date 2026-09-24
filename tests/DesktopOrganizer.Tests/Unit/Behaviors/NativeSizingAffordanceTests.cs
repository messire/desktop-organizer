using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="NativeSizingAffordance"/> — the native <c>WM_SIZING</c>-path
/// implementation of <see cref="IResizeAffordance"/> (docs/PLANS/phase-2-native-panel-sizing.md §1.6,
/// R-9). Pure in-memory bool flag, no WPF types involved — fully testable headless, unlike
/// <see cref="ThumbOverlayAffordance"/> (see <c>ThumbOverlayAffordanceTests</c>'s remark).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingAffordanceTests
{
    [Fact]
    public void IsEnabled_DefaultsToTrue_MirroringResizeOverlayXamlAuthoredVisibility()
    {
        var affordance = new NativeSizingAffordance();

        Assert.True(affordance.IsEnabled);
    }

    [Fact]
    public void SetEnabled_False_MakesIsEnabledFalse()
    {
        var affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);

        Assert.False(affordance.IsEnabled);
    }

    [Fact]
    public void SetEnabled_TrueAfterFalse_MakesIsEnabledTrueAgain()
    {
        var affordance = new NativeSizingAffordance();
        affordance.SetEnabled(false);

        affordance.SetEnabled(true);

        Assert.True(affordance.IsEnabled);
    }

    [Fact]
    public void SetEnabled_SameValueTwice_IsIdempotent()
    {
        var affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);
        affordance.SetEnabled(false);

        Assert.False(affordance.IsEnabled);
    }

    [Fact]
    public void NativeSizingAffordance_ImplementsIResizeAffordance()
    {
        IResizeAffordance affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);

        Assert.False(((NativeSizingAffordance)affordance).IsEnabled);
    }
}
