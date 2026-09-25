using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction.Native;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// Phase 2 (commit C2, docs/PLANS/phase-2-native-panel-sizing.md §1.4/§5, T-MAP-01..06) tests for
/// <see cref="NativeSizingCoordinateMapper"/> — the single px&lt;-&gt;DIP conversion point (D-3).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingCoordinateMapperTests
{
    /// <summary>Verifies that converting a pixel rect to DIP and back to pixels round-trips within one pixel, at a given <paramref name="scale"/>.</summary>
    /// <param name="scale">The DPI scale factor applied to the round trip.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ToDip_ThenToPx_RoundTrips_WithinOnePixel(double scale)
    {
        var original = new NativeRectPx(-137, 481, 863, 900);

        var dip = NativeSizingCoordinateMapper.ToDip(original, scale);
        var roundTripped = NativeSizingCoordinateMapper.ToPx(dip, scale);

        Assert.InRange(roundTripped.Left, original.Left - 1, original.Left + 1);
        Assert.InRange(roundTripped.Top, original.Top - 1, original.Top + 1);
        Assert.InRange(roundTripped.Right, original.Right - 1, original.Right + 1);
        Assert.InRange(roundTripped.Bottom, original.Bottom - 1, original.Bottom + 1);
    }

    /// <summary>Verifies that <see cref="NativeSizingCoordinateMapper.ToDip"/> at scale 1.0 is the identity conversion.</summary>
    [Fact]
    public void ToDip_Scale1_IsIdentity()
    {
        var rect = new NativeRectPx(100, 200, 400, 450);

        var dip = NativeSizingCoordinateMapper.ToDip(rect, 1.0);

        Assert.Equal(new Rect(100, 200, 300, 250), dip);
    }

    /// <summary>Verifies that <see cref="NativeSizingCoordinateMapper.ToDip"/> converts negative screen coordinates, as reported for a monitor left of the primary, correctly.</summary>
    [Fact]
    public void ToDip_NegativeScreenCoordinates_MonitorLeftOfPrimary_ConvertsCorrectly()
    {
        // A monitor positioned to the left of the primary monitor reports negative screen X.
        var rect = new NativeRectPx(-1920, 0, -1520, 300);

        var dip = NativeSizingCoordinateMapper.ToDip(rect, 1.0);

        Assert.Equal(new Rect(-1920, 0, 400, 300), dip);
    }

    /// <summary>Verifies that <see cref="NativeSizingCoordinateMapper.ToPx"/> converts negative DIP coordinates correctly.</summary>
    [Fact]
    public void ToPx_NegativeDipCoordinates_ConvertsCorrectly()
    {
        var rect = new Rect(-1920, 0, 400, 300);

        var px = NativeSizingCoordinateMapper.ToPx(rect, 1.0);

        Assert.Equal(new NativeRectPx(-1920, 0, -1520, 300), px);
    }

    /// <summary>Verifies that <see cref="NativeSizingCoordinateMapper.ToDip"/> at scale 1.25 produces fractional DIP values.</summary>
    [Fact]
    public void ToDip_Scale125_ProducesFractionalDipValues()
    {
        var rect = new NativeRectPx(0, 0, 100, 100);

        var dip = NativeSizingCoordinateMapper.ToDip(rect, 1.25);

        Assert.Equal(80.0, dip.Width, precision: 6);
        Assert.Equal(80.0, dip.Height, precision: 6);
    }

    /// <summary>Verifies that a half-pixel value rounds to nearest using <see cref="Math.Round(double)"/>'s default <see cref="MidpointRounding.ToEven"/> behavior.</summary>
    [Fact]
    public void ToPx_HalfPixelValue_RoundsToNearestUsingMathRoundDefault()
    {
        // 10 DIP * 1.25 = 12.5px exactly; Math.Round's default MidpointRounding (ToEven) rounds to 12.
        var rect = new Rect(0, 0, 10, 10);

        var px = NativeSizingCoordinateMapper.ToPx(rect, 1.25);

        Assert.Equal(12, px.Right);
        Assert.Equal(12, px.Bottom);
    }

    /// <summary>Verifies that an inverted <see cref="NativeRectPx"/> (right &lt; left, bottom &lt; top) reports negative width and height.</summary>
    [Fact]
    public void NativeRectPx_InvertedRect_WidthAndHeightAreNegative()
    {
        var rect = new NativeRectPx(Left: 500, Top: 400, Right: 100, Bottom: 200);

        Assert.Equal(-400, rect.Width);
        Assert.Equal(-200, rect.Height);
    }
}
