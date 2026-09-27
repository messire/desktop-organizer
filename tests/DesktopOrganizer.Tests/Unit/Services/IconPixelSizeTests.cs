using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="IconPixelSize"/> — the DIP × DPI → snapped pixel size policy behind per-panel
/// icon requests.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class IconPixelSizeTests
{
    /// <summary>
    /// Verifies that a requested size is rounded UP to the next bucket and clamped to [16, 256].
    /// </summary>
    /// <param name="requested">The requested pixel size.</param>
    /// <param name="expected">The expected bucket.</param>
    [Theory]
    [InlineData(-5, 16)]
    [InlineData(0, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 20)]
    [InlineData(24, 24)]
    [InlineData(25, 32)]
    [InlineData(33, 40)]
    [InlineData(41, 48)]
    [InlineData(48, 48)]
    [InlineData(49, 64)]
    [InlineData(65, 72)]
    [InlineData(73, 96)]
    [InlineData(97, 128)]
    [InlineData(129, 144)]
    [InlineData(145, 192)]
    [InlineData(193, 256)]
    [InlineData(256, 256)]
    [InlineData(10_000, 256)]
    public void Snap_RoundsUpToNextBucket_AndClamps(int requested, int expected)
        => Assert.Equal(expected, IconPixelSize.Snap(requested));

    /// <summary>
    /// Verifies the DIP × scale conversion for the common per-panel sizes and Windows scale factors.
    /// </summary>
    /// <param name="sizeDip">Icon size in DIP.</param>
    /// <param name="scale">DPI scale.</param>
    /// <param name="expected">Expected snapped pixel size.</param>
    [Theory]
    [InlineData(16, 1.0, 16)]
    [InlineData(16, 1.25, 20)]
    [InlineData(16, 1.5, 24)]
    [InlineData(48, 1.0, 48)]
    [InlineData(48, 1.25, 64)]
    [InlineData(48, 1.5, 72)]
    [InlineData(48, 2.0, 96)]
    [InlineData(96, 1.5, 144)]
    [InlineData(96, 2.0, 192)]
    [InlineData(96, 2.5, 256)]
    [InlineData(47.2, 1.0, 48)]
    public void FromDip_MultipliesByScale_AndSnaps(double sizeDip, double scale, int expected)
        => Assert.Equal(expected, IconPixelSize.FromDip(sizeDip, scale));

    /// <summary>
    /// Verifies the new icon-size range's top end (docs/PLANS/color-presets-and-size-step.md §6.3 R-6:
    /// the new <see cref="DesktopOrganizer.UI.Models.SizeStepPolicy.IconSizeMaximum"/> of 128, up from the
    /// pre-feature slider's 96 ceiling) at the Windows scale factors a per-panel icon actually renders at —
    /// every bucket this range can produce stays within [<see cref="IconPixelSize.Min"/>, <see cref="IconPixelSize.Max"/>],
    /// confirming <see cref="IconPixelSize"/> itself needed no change for the wider slider range (§6.3's
    /// own "not changed" note).
    /// </summary>
    /// <param name="sizeDip">Icon size in DIP, from the new range's upper third (104..128).</param>
    /// <param name="scale">DPI scale.</param>
    /// <param name="expected">Expected snapped pixel size.</param>
    [Theory]
    [InlineData(104, 1.0, 128)]
    [InlineData(116, 1.0, 128)]
    [InlineData(128, 1.0, 128)]
    [InlineData(104, 1.5, 192)]
    [InlineData(116, 1.5, 192)]
    [InlineData(128, 1.5, 192)]
    [InlineData(104, 2.0, 256)]
    [InlineData(116, 2.0, 256)]
    [InlineData(128, 2.0, 256)]
    public void FromDip_LargeIconRange_SnapsToExpectedBucketWithinMax(double sizeDip, double scale, int expected)
    {
        var actual = IconPixelSize.FromDip(sizeDip, scale);

        Assert.Equal(expected, actual);
        Assert.InRange(actual, IconPixelSize.Min, IconPixelSize.Max);
    }

    /// <summary>
    /// Verifies that corrupt inputs degrade to the default size / 1.0 scale instead of throwing or producing
    /// an out-of-range value.
    /// </summary>
    /// <param name="sizeDip">Icon size in DIP.</param>
    /// <param name="scale">DPI scale.</param>
    /// <param name="expected">Expected snapped pixel size.</param>
    [Theory]
    [InlineData(double.NaN, 1.0, IconPixelSize.Default)]
    [InlineData(-10, 1.0, IconPixelSize.Default)]
    [InlineData(0, 1.0, IconPixelSize.Default)]
    [InlineData(32, double.NaN, 32)]
    [InlineData(32, 0, 32)]
    [InlineData(32, double.PositiveInfinity, 32)]
    [InlineData(double.MaxValue, 1.0, IconPixelSize.Max)]
    public void FromDip_InvalidInput_DegradesSafely(double sizeDip, double scale, int expected)
        => Assert.Equal(expected, IconPixelSize.FromDip(sizeDip, scale));
}
