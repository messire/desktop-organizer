using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Unit tests for <see cref="PanelCustomizeWindow.ClampHeightToWorkArea"/> (UX HIGH H-1, review round):
/// the pure clamp math behind <c>PanelCustomizeWindow.ApplyCentered</c>, which keeps this
/// <c>ResizeMode="NoResize"</c> dialog's fixed <c>Height="640"</c> from overflowing a small monitor work
/// area (e.g. 1366x768 @125%, ~582 DIP tall) — the existing <c>ScrollViewer</c> absorbs whatever no longer
/// fits once <c>MaxHeight</c> is clamped.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCustomizeWindowHeightClampTests
{
    private const double DesignedHeight = 640d;

    /// <summary>A work area taller than the designed height returns the designed height unchanged.</summary>
    [Fact]
    public void WorkAreaTallerThanDesignedHeight_ReturnsDesignedHeightUnchanged()
    {
        var result = PanelCustomizeWindow.ClampHeightToWorkArea(DesignedHeight, 900d);

        Assert.Equal(DesignedHeight, result);
    }

    /// <summary>
    /// A work area shorter than the designed height (H-1's own motivating example: 1366x768 @125%, work
    /// area height ~582 DIP) clamps down to that work-area height.
    /// </summary>
    [Fact]
    public void WorkAreaShorterThanDesignedHeight_ClampsToWorkAreaHeight()
    {
        var result = PanelCustomizeWindow.ClampHeightToWorkArea(DesignedHeight, 582d);

        Assert.Equal(582d, result);
    }

    /// <summary>The result never exceeds the designed height, even for an enormous work area.</summary>
    [Fact]
    public void NeverExceedsDesignedHeight()
    {
        var result = PanelCustomizeWindow.ClampHeightToWorkArea(DesignedHeight, 4000d);

        Assert.Equal(DesignedHeight, result);
    }

    /// <summary>A degenerate (zero, negative, or non-finite) work-area height is ignored — returns the designed height unchanged instead of collapsing the window to nothing.</summary>
    /// <param name="workAreaHeight">The degenerate work-area height under test.</param>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void DegenerateWorkAreaHeight_ReturnsDesignedHeightUnchanged_DoesNotThrow(double workAreaHeight)
    {
        var exception = Record.Exception(() => PanelCustomizeWindow.ClampHeightToWorkArea(DesignedHeight, workAreaHeight));

        Assert.Null(exception);
        Assert.Equal(DesignedHeight, PanelCustomizeWindow.ClampHeightToWorkArea(DesignedHeight, workAreaHeight));
    }

    /// <summary>
    /// A very small (but positive/finite) work area still returns the defensive floor (200 DIP) rather
    /// than collapsing all the way down to the work area's own tiny height.
    /// </summary>
    [Fact]
    public void VerySmallWorkArea_ReturnsTheDefensiveFloor_NotTheTinyWorkAreaHeight()
    {
        var result = PanelCustomizeWindow.ClampHeightToWorkArea(DesignedHeight, 50d);

        Assert.Equal(200d, result);
    }
}
