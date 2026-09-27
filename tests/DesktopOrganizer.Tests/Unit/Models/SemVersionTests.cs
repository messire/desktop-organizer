using DesktopOrganizer.Core;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="SemVersion"/> (release-hardening-1.0.md §G2): parsing tolerance (a leading
/// <c>v</c>, pre-release/build metadata, malformed input) and precedence (semver.org §11).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.Core")]
public class SemVersionTests
{
    /// <summary>Verifies that every tolerated shape parses successfully.</summary>
    /// <param name="input">The version string under test.</param>
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("0.6.1")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3+ab12cd34")]
    [InlineData("1.2.3-beta.1+ab12cd34")]
    [InlineData("0.0.0")]
    public void TryParse_ValidInput_Succeeds(string input)
    {
        var parsed = SemVersion.TryParse(input, out var version);

        Assert.True(parsed);
        Assert.NotNull(version);
    }

    /// <summary>Verifies that every malformed shape fails cleanly (no exception, <see langword="null"/> output) rather than throwing.</summary>
    /// <param name="input">The malformed version string under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("abc")]
    [InlineData("1.2.x")]
    [InlineData("1.02.3")] // leading zero
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-01")] // numeric pre-release identifier with leading zero
    [InlineData("1.2.3-beta_1")] // underscore not allowed in a pre-release identifier
    public void TryParse_MalformedInput_Fails(string? input)
    {
        var parsed = SemVersion.TryParse(input, out var version);

        Assert.False(parsed);
        Assert.Null(version);
    }

    /// <summary>Verifies that a leading <c>v</c>/<c>V</c> (GitHub's own tag convention) is stripped, not part of <see cref="SemVersion.Major"/>.</summary>
    [Fact]
    public void TryParse_StripsLeadingVPrefix()
    {
        SemVersion.TryParse("v1.2.3", out var version);

        Assert.Equal(1, version!.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Patch);
    }

    /// <summary>Verifies that <see cref="SemVersion.Prerelease"/>/<see cref="SemVersion.BuildMetadata"/> are split correctly.</summary>
    [Fact]
    public void TryParse_SplitsPrereleaseAndBuildMetadata()
    {
        SemVersion.TryParse("1.2.3-beta.1+ab12cd34", out var version);

        Assert.Equal("beta.1", version!.Prerelease);
        Assert.Equal("ab12cd34", version.BuildMetadata);
    }

    /// <summary>Verifies <see cref="SemVersion.CompareTo"/> against every semver.org §11 precedence rule.</summary>
    /// <param name="left">The left-hand version.</param>
    /// <param name="right">The right-hand version.</param>
    /// <param name="expectedSign">The expected sign of <c>left.CompareTo(right)</c> (-1/0/1).</param>
    [Theory]
    [InlineData("1.3.0", "1.2.3", 1)] // newer
    [InlineData("1.2.3", "1.2.3", 0)] // equal
    [InlineData("1.2.3", "1.3.0", -1)] // older
    [InlineData("1.2.3", "1.2.3-beta.1", 1)] // release outranks pre-release of the same core
    [InlineData("1.2.3-beta.1", "1.2.3", -1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)] // alphanumeric identifiers compare ordinally
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha.2", -1)] // numeric identifiers compare numerically
    [InlineData("1.2.3-alpha.10", "1.2.3-alpha.2", 1)] // 10 > 2 numerically, not lexically
    [InlineData("1.2.3-alpha", "1.2.3-alpha.1", -1)] // fewer identifiers, all shared ones equal, sorts first
    [InlineData("1.2.3-1", "1.2.3-alpha", -1)] // numeric identifier always sorts before alphanumeric
    [InlineData("1.2.3+build1", "1.2.3+build2", 0)] // build metadata never affects precedence
    public void CompareTo_FollowsSemverPrecedence(string left, string right, int expectedSign)
    {
        SemVersion.TryParse(left, out var leftVersion);
        SemVersion.TryParse(right, out var rightVersion);

        var actual = leftVersion!.CompareTo(rightVersion);

        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    /// <summary>Verifies that comparing against <see langword="null"/> always ranks this instance higher, per <see cref="IComparable{T}"/> convention.</summary>
    [Fact]
    public void CompareTo_Null_ReturnsPositive()
    {
        SemVersion.TryParse("1.0.0", out var version);

        Assert.True(version!.CompareTo(null) > 0);
    }

    /// <summary>Verifies that two versions with the same core/pre-release but different build metadata are equal (build metadata never affects equality either).</summary>
    [Fact]
    public void Equals_SameCoreAndPrerelease_AreEqual()
    {
        SemVersion.TryParse("1.2.3-beta.1+shaA", out var a);
        SemVersion.TryParse("1.2.3-beta.1+shaB", out var b);

        Assert.True(a!.Equals(b));
        Assert.Equal(a.GetHashCode(), b!.GetHashCode());
    }
}
