using System.Net;
using System.Net.Http;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Updates;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="GitHubReleaseUpdateChecker"/> (release-hardening-1.0.md §G2) against a fake
/// <see cref="HttpMessageHandler"/> — never the real network. Covers every branch of the "single GET,
/// never throws" contract: newer/equal/older/pre-release comparisons, <c>404</c>/<c>403</c>/timeout/
/// malformed-JSON/oversized-body/untrusted-<c>html_url</c> handling.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.Services")]
public class GitHubReleaseUpdateCheckerTests
{
    private readonly IAppLog _log = NullAppLog.Instance;

    /// <summary>Verifies that a newer published release is reported as <see cref="UpdateCheckStatus.UpdateAvailable"/> with its version and its own trusted <c>html_url</c>.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_NewerReleasePublished_ReturnsUpdateAvailable()
    {
        var (_, _, newerTag) = BuildRelativeTags();
        var releaseUrl = $"https://github.com/messire/DesktopOrganizer/releases/tag/{newerTag}";
        var handler = FakeHandler.Ok(ReleaseJson(newerTag, releaseUrl));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(newerTag.TrimStart('v'), result.LatestVersion);
        Assert.Equal(releaseUrl, result.ReleaseUrl);
    }

    /// <summary>Verifies that a published release exactly matching the running build's own version is reported as <see cref="UpdateCheckStatus.UpToDate"/>.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_SameVersionPublished_ReturnsUpToDate()
    {
        var (currentTag, _, _) = BuildRelativeTags();
        var handler = FakeHandler.Ok(ReleaseJson(currentTag, $"https://github.com/messire/DesktopOrganizer/releases/tag/{currentTag}"));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.LatestVersion);
        Assert.Null(result.ReleaseUrl);
    }

    /// <summary>Verifies that a published release OLDER than the running build is also reported as <see cref="UpdateCheckStatus.UpToDate"/> (never "downgrade available").</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_OlderReleasePublished_ReturnsUpToDate()
    {
        var (_, olderTag, _) = BuildRelativeTags();
        var handler = FakeHandler.Ok(ReleaseJson(olderTag, $"https://github.com/messire/DesktopOrganizer/releases/tag/{olderTag}"));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    /// <summary>Verifies that a pre-release tag with a newer CORE version still outranks the current release build (semver.org §11.3 does not apply across different core versions).</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_PrereleaseNewerCoreVersion_ReturnsUpdateAvailable()
    {
        var (_, _, newerTag) = BuildRelativeTags();
        var prereleaseTag = $"{newerTag}-beta.1";
        var handler = FakeHandler.Ok(ReleaseJson(prereleaseTag, $"https://github.com/messire/DesktopOrganizer/releases/tag/{prereleaseTag}"));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        // A newer CORE version, even as a pre-release, still outranks the current release build.
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(prereleaseTag.TrimStart('v'), result.LatestVersion);
    }

    /// <summary>Verifies that an <c>html_url</c> not pointing at this project's own GitHub org (<see cref="UpdateEndpoints.TryGetTrustedReleaseUrl"/>) is never opened directly — the fixed <see cref="UpdateEndpoints.ReleasesUrl"/> is used instead.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_UntrustedHtmlUrl_FallsBackToReleasesUrl()
    {
        var (_, _, newerTag) = BuildRelativeTags();
        var handler = FakeHandler.Ok(ReleaseJson(newerTag, "https://evil.example.com/not-us"));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(UpdateEndpoints.ReleasesUrl, result.ReleaseUrl);
    }

    /// <summary>Verifies that a <c>404</c> (the repository has no releases yet) is reported as <see cref="UpdateCheckStatus.NoReleases"/>, not a failure.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_NotFound_ReturnsNoReleases()
    {
        var handler = FakeHandler.Status(HttpStatusCode.NotFound);
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.NoReleases, result.Status);
    }

    /// <summary>Verifies that both GitHub rate-limit statuses (<c>403</c>/<c>429</c>) map to <see cref="UpdateCheckFailureKind.RateLimited"/>.</summary>
    /// <param name="statusCode">The rate-limit status code under test.</param>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CheckForUpdatesAsync_RateLimitedStatus_ReturnsFailedRateLimited(HttpStatusCode statusCode)
    {
        var handler = FakeHandler.Status(statusCode);
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.RateLimited, result.FailureKind);
    }

    /// <summary>Verifies that a non-success status other than <c>404</c>/<c>403</c>/<c>429</c> maps to <see cref="UpdateCheckFailureKind.Unexpected"/>.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_ServerError_ReturnsFailedUnexpected()
    {
        var handler = FakeHandler.Status(HttpStatusCode.InternalServerError);
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.Unexpected, result.FailureKind);
    }

    /// <summary>Verifies that the request self-cancelling via <see cref="TaskCanceledException"/> (simulating <see cref="HttpClient.Timeout"/> firing) maps to <see cref="UpdateCheckFailureKind.Timeout"/>, not an unhandled cancellation.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_TimedOut_ReturnsFailedTimeout()
    {
        var handler = FakeHandler.Throwing(new TaskCanceledException("simulated timeout"));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.Timeout, result.FailureKind);
    }

    /// <summary>Verifies that an <see cref="HttpRequestException"/> (no connectivity, DNS/TLS failure) maps to <see cref="UpdateCheckFailureKind.Network"/> and is logged.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_NetworkFailure_ReturnsFailedNetwork()
    {
        var handler = FakeHandler.Throwing(new HttpRequestException("simulated network failure"));
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.Network, result.FailureKind);
    }

    /// <summary>Verifies that a response body that isn't valid JSON at all maps to <see cref="UpdateCheckFailureKind.InvalidResponse"/> rather than throwing <see cref="System.Text.Json.JsonException"/>.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_MalformedJson_ReturnsFailedInvalidResponse()
    {
        var handler = FakeHandler.Ok("{ this is not valid json");
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.InvalidResponse, result.FailureKind);
    }

    /// <summary>Verifies that valid JSON missing a usable <c>tag_name</c> field maps to <see cref="UpdateCheckFailureKind.InvalidResponse"/>.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_MissingTagName_ReturnsFailedInvalidResponse()
    {
        var handler = FakeHandler.Ok("{\"name\":\"Release without a tag_name\"}");
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.InvalidResponse, result.FailureKind);
    }

    /// <summary>Verifies that a <c>tag_name</c> that isn't a valid semver (<see cref="SemVersion.TryParse"/> fails) maps to <see cref="UpdateCheckFailureKind.InvalidResponse"/>.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_UnparsableTagName_ReturnsFailedInvalidResponse()
    {
        var handler = FakeHandler.Ok("{\"tag_name\":\"not-a-version\"}");
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.InvalidResponse, result.FailureKind);
    }

    /// <summary>Verifies that a response body over the 256&#160;KB cap maps to <see cref="UpdateCheckFailureKind.InvalidResponse"/> instead of being buffered in full.</summary>
    [Fact]
    public async Task CheckForUpdatesAsync_OversizedBody_ReturnsFailedInvalidResponse()
    {
        var hugeJson = "{\"tag_name\":\"v9.9.9\",\"padding\":\"" + new string('x', 300 * 1024) + "\"}";
        var handler = FakeHandler.Ok(hugeJson);
        var sut = new GitHubReleaseUpdateChecker(handler, _log);

        var result = await sut.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureKind.InvalidResponse, result.FailureKind);
    }

    /// <summary>
    /// Builds three tags relative to the running build's own <see cref="AppVersionInfo.Display"/> — the
    /// current version's own tag, one patch OLDER, and one patch NEWER — so these tests never hardcode
    /// today's project version (which will move on) and stay correct across a future version bump.
    /// </summary>
    private static (string CurrentTag, string OlderTag, string NewerTag) BuildRelativeTags()
    {
        Assert.True(SemVersion.TryParse(AppVersionInfo.Display, out var current), $"AppVersionInfo.Display ('{AppVersionInfo.Display}') must itself be a valid semver.");

        var currentTag = $"v{current!.Major}.{current.Minor}.{current.Patch}";
        var olderTag = current.Patch > 0
            ? $"v{current.Major}.{current.Minor}.{current.Patch - 1}"
            : $"v{current.Major}.{Math.Max(current.Minor - 1, 0)}.0";
        var newerTag = $"v{current.Major}.{current.Minor}.{current.Patch + 1}";

        return (currentTag, olderTag, newerTag);
    }

    /// <summary>Builds a minimal GitHub "latest release" JSON body with just the two fields this checker reads.</summary>
    private static string ReleaseJson(string tagName, string htmlUrl) =>
        "{\"tag_name\":\"" + tagName + "\",\"html_url\":\"" + htmlUrl + "\"}";

    /// <summary>A canned-response/canned-exception <see cref="HttpMessageHandler"/> for these tests.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;

        private FakeHandler(Func<HttpResponseMessage> responder) => _responder = responder;

        public static FakeHandler Ok(string jsonBody) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonBody),
        });

        public static FakeHandler Status(HttpStatusCode statusCode) => new(() => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(string.Empty),
        });

        public static FakeHandler Throwing(Exception exception) => new(() => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder());
    }
}
