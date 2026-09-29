using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// T-4 (docs/PLANS/design-system-1x.md §2.3/§2.12/§3 phase 6.1, ADR-047 D-4): pins the exact ramp the plan
/// contracts (M = 12 → 12/14/14/18/20) and the "no Palette T size ever drops below M" invariant for a
/// larger base (Windows "Text size" turned up).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TypeRampTests
{
    /// <summary>The plan's own worked example: M = 12 (100% "Text size", the Windows default).</summary>
    [Fact]
    public void FromMessageFontSize_AtDefaultM_MatchesThePlansWorkedExample()
    {
        var ramp = TypeRamp.FromMessageFontSize(12);

        Assert.Equal(12, ramp.Caption);
        Assert.Equal(14, ramp.Body);
        Assert.Equal(14, ramp.BodyStrong);
        Assert.Equal(18, ramp.Subtitle);
        Assert.Equal(20, ramp.Title);
    }

    /// <summary>A larger base (accessibility "Text size" turned up) never pushes any size below M.</summary>
    [Fact]
    public void FromMessageFontSize_AtLargerM_NoSizeDropsBelowM()
    {
        const double m = 15;
        var ramp = TypeRamp.FromMessageFontSize(m);

        Assert.True(ramp.Caption >= m);
        Assert.True(ramp.Body >= m);
        Assert.True(ramp.BodyStrong >= m);
        Assert.True(ramp.Subtitle >= m);
        Assert.True(ramp.Title >= m);
    }

    /// <summary>The ramp strictly grows: Caption &lt; Body == BodyStrong &lt; Subtitle &lt; Title.</summary>
    /// <param name="m">The base "M" size to compute the ramp from.</param>
    [Theory]
    [InlineData(12.0)]
    [InlineData(15.0)]
    [InlineData(9.0)]
    public void FromMessageFontSize_RampIsMonotonicallyNonDecreasing(double m)
    {
        var ramp = TypeRamp.FromMessageFontSize(m);

        Assert.True(ramp.Caption < ramp.Body);
        Assert.Equal(ramp.Body, ramp.BodyStrong);
        Assert.True(ramp.BodyStrong < ramp.Subtitle);
        Assert.True(ramp.Subtitle < ramp.Title);
    }

    /// <summary>A non-positive/unset base falls back to 12 rather than propagating a zero/negative size.</summary>
    /// <param name="messageFontSize">The (invalid) base size to pass in.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void FromMessageFontSize_NonPositiveInput_FallsBackToTwelve(double messageFontSize)
    {
        var ramp = TypeRamp.FromMessageFontSize(messageFontSize);

        Assert.Equal(12, ramp.Caption);
    }
}
