using DesktopOrganizer.Services.Updates;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="UpdateEndpoints.TryGetTrustedReleaseUrl"/> (M4, release-hardening review):
/// validates a GitHub release API response's <c>html_url</c> structurally (scheme, host, path) via
/// <see cref="Uri"/> rather than a raw string prefix.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class UpdateEndpointsTests
{
    /// <summary>A well-formed, absolute release URL under this project's own repository is trusted and returned as its own <see cref="Uri.AbsoluteUri"/>.</summary>
    [Fact]
    public void TryGetTrustedReleaseUrl_OwnRepositoryReleaseUrl_ReturnsAbsoluteUri()
    {
        const string url = "https://github.com/messire/DesktopOrganizer/releases/tag/v1.3.0";

        var result = UpdateEndpoints.TryGetTrustedReleaseUrl(url);

        Assert.Equal(url, result);
    }

    /// <summary><see langword="null"/> input is rejected.</summary>
    [Fact]
    public void TryGetTrustedReleaseUrl_Null_ReturnsNull()
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl(null));
    }

    /// <summary>A <c>javascript:</c> URL is rejected.</summary>
    [Fact]
    public void TryGetTrustedReleaseUrl_JavascriptScheme_ReturnsNull()
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl("javascript:alert(1)"));
    }

    /// <summary>A <c>file:</c> URL is rejected.</summary>
    [Fact]
    public void TryGetTrustedReleaseUrl_FileScheme_ReturnsNull()
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl(@"file:///C:/Windows/System32/cmd.exe"));
    }

    /// <summary>A plain (unencrypted) <c>http://</c> URL is rejected — only <c>https://</c> is trusted.</summary>
    [Fact]
    public void TryGetTrustedReleaseUrl_PlainHttpScheme_ReturnsNull()
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl("http://github.com/messire/DesktopOrganizer/releases/tag/v1.0.0"));
    }

    /// <summary>A malformed/quoted string that doesn't parse as an absolute URI at all is rejected.</summary>
    /// <param name="value">The malformed/quoted string under test.</param>
    [Theory]
    [InlineData("\"https://github.com/messire/DesktopOrganizer\" & calc.exe")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void TryGetTrustedReleaseUrl_MalformedOrQuotedString_ReturnsNull(string value)
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl(value));
    }

    /// <summary>An https URL on a lookalike host (not exactly <c>github.com</c>) is rejected.</summary>
    /// <param name="value">The lookalike-host URL under test.</param>
    [Theory]
    [InlineData("https://github.com.evil.example/messire/DesktopOrganizer/releases/tag/v1.0.0")]
    [InlineData("https://evil.example/github.com/messire/DesktopOrganizer/releases/tag/v1.0.0")]
    [InlineData("https://notgithub.com/messire/DesktopOrganizer/releases/tag/v1.0.0")]
    public void TryGetTrustedReleaseUrl_LookalikeHost_ReturnsNull(string value)
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl(value));
    }

    /// <summary>A github.com URL for a DIFFERENT repository/org is rejected — only this project's own repository is trusted.</summary>
    [Fact]
    public void TryGetTrustedReleaseUrl_DifferentRepository_ReturnsNull()
    {
        Assert.Null(UpdateEndpoints.TryGetTrustedReleaseUrl("https://github.com/someone-else/SomeOtherRepo/releases/tag/v1.0.0"));
    }
}
