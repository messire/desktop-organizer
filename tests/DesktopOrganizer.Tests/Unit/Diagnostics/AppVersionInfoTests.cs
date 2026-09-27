using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="AppVersionInfo"/> (plan §0.4): the single source of truth for the running
/// build's version, sourced from <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>
/// rather than the deliberately-pinned <c>AssemblyVersion</c> (<c>Directory.Build.props</c>).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class AppVersionInfoTests
{
    /// <summary><see cref="AppVersionInfo.Full"/> is never null/blank — either a real informational
    /// version or the documented <c>"unknown"</c> fallback.</summary>
    [Fact]
    public void Full_IsNeverNullOrWhiteSpace()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersionInfo.Full));
    }

    /// <summary><see cref="AppVersionInfo.Display"/> never contains a <c>+</c> build-metadata separator,
    /// regardless of whether the running build's <see cref="AppVersionInfo.Full"/> has one.</summary>
    [Fact]
    public void Display_NeverContainsBuildMetadataSeparator()
    {
        Assert.DoesNotContain('+', AppVersionInfo.Display);
    }

    /// <summary><see cref="AppVersionInfo.Display"/> is a prefix of <see cref="AppVersionInfo.Full"/> —
    /// stripping never changes the leading release-version characters.</summary>
    [Fact]
    public void Display_IsAPrefixOfFull()
    {
        Assert.StartsWith(AppVersionInfo.Display, AppVersionInfo.Full, StringComparison.Ordinal);
    }

    // ── StripBuildMetadata (pure helper, deterministic without depending on this build's real attribute) ──

    /// <summary>A version with a <c>+&lt;sha&gt;</c> suffix has everything from the <c>+</c> onward removed.</summary>
    [Fact]
    public void StripBuildMetadata_VersionWithSha_RemovesSuffix()
    {
        var result = AppVersionInfo.StripBuildMetadata("0.6.1+ab12cd34ef567890ab12cd34ef567890ab12cd34");

        Assert.Equal("0.6.1", result);
    }

    /// <summary>A version with no <c>+</c> at all is returned unchanged.</summary>
    [Fact]
    public void StripBuildMetadata_VersionWithoutSha_ReturnsUnchanged()
    {
        var result = AppVersionInfo.StripBuildMetadata("0.6.1");

        Assert.Equal("0.6.1", result);
    }

    /// <summary>The documented <c>"unknown"</c> fallback (no <c>+</c>) round-trips unchanged.</summary>
    [Fact]
    public void StripBuildMetadata_UnknownFallback_ReturnsUnchanged()
    {
        var result = AppVersionInfo.StripBuildMetadata("unknown");

        Assert.Equal("unknown", result);
    }
}
