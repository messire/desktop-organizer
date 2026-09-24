using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="MotionState"/> (docs/PLANS/workspace-grid-07-visual.md "reduced motion").
/// Only checks the type's own contract — <c>App.xaml.cs</c>'s <c>LoadResourceDictionaries()</c>, the
/// sole writer (via <see cref="System.Windows.SystemParameters.ClientAreaAnimation"/>), is WPF startup
/// plumbing not exercised by unit tests (manual verify on Windows) — same split as
/// <see cref="HighContrastStateTests"/> for its sibling flag.
/// </summary>
[Trait(UnitTests, "UI")]
public class MotionStateTests
{
    [Fact]
    public void AnimationsEnabled_IsSettableAndReadable_RoundTrips()
    {
        var original = MotionState.AnimationsEnabled;
        try
        {
            MotionState.AnimationsEnabled = false;
            Assert.False(MotionState.AnimationsEnabled);

            MotionState.AnimationsEnabled = true;
            Assert.True(MotionState.AnimationsEnabled);
        }
        finally
        {
            MotionState.AnimationsEnabled = original;
        }
    }
}
