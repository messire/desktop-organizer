using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="HighContrastState"/> (CRITICAL-2/MAJOR-4 fix, ADR-025 D-12). Only checks
/// the type's own contract — App.xaml.cs's <c>LoadResourceDictionaries()</c>, which is the sole writer
/// (via <see cref="System.Windows.SystemParameters.HighContrast"/>), is WPF startup plumbing not
/// exercised by unit tests (manual verify on Windows). Shares a collection with
/// <see cref="DesktopOrganizer.Tests.Unit.Resources.ResourceDictionaryMergeTests"/>, which also mutates
/// this same static flag, so the two never race.
/// </summary>
[Collection("HighContrastState")]
[Trait(UnitTests, "UI")]
public class HighContrastStateTests
{
    /// <summary>
    /// Verifies that <see cref="HighContrastState.IsActive"/> round-trips through set/get, restoring the
    /// original value afterward so this test never leaks state into other tests sharing the collection.
    /// </summary>
    [Fact]
    public void IsActive_IsSettableAndReadable_RoundTrips()
    {
        var original = HighContrastState.IsActive;
        try
        {
            HighContrastState.IsActive = true;
            Assert.True(HighContrastState.IsActive);

            HighContrastState.IsActive = false;
            Assert.False(HighContrastState.IsActive);
        }
        finally
        {
            HighContrastState.IsActive = original;
        }
    }
}
