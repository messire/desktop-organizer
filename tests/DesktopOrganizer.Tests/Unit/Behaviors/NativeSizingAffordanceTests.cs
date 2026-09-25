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
    /// <summary>Verifies that a freshly-constructed <see cref="NativeSizingAffordance"/> defaults to enabled, mirroring the resize overlay's XAML-authored default visibility.</summary>
    [Fact]
    public void IsEnabled_DefaultsToTrue_MirroringResizeOverlayXamlAuthoredVisibility()
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

    /// <summary>Verifies that <see cref="NativeSizingAffordance"/> can be consumed entirely through the <see cref="IResizeAffordance"/> interface.</summary>
    [Fact]
    public void NativeSizingAffordance_ImplementsIResizeAffordance()
    {
        IResizeAffordance affordance = new NativeSizingAffordance();

        affordance.SetEnabled(false);

        Assert.False(((NativeSizingAffordance)affordance).IsEnabled);
    }
}
