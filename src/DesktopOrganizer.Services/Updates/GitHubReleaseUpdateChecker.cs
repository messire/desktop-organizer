using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Updates;

/// <summary>
/// The production <see cref="IUpdateCheckService"/>: a single GET against GitHub's "latest release" REST
/// endpoint (<see cref="UpdateEndpoints.ApiLatestReleaseUrl"/>), compared against the running build's own
/// version via <see cref="SemVersion"/> precedence. Never downloads or runs anything — the only output is
/// an <see cref="UpdateCheckResult"/> for the caller (<c>AboutViewModel</c>) to show.
/// </summary>
/// <remarks>
/// Owns exactly one <see cref="HttpClient"/> for its own lifetime (this type is registered as a DI
/// singleton — see <c>Services/Configuration/DiConfig.cs</c> — and disposed with the root
/// <see cref="IServiceProvider"/>), with a fixed 10-second timeout: this is a "click a button, wait a
/// moment" action, never a background poller (there is no persisted "last checked" state and nothing
/// calls this automatically — release-hardening-1.0.md §G2). Every failure mode — no network, a
/// rate-limited/erroring/oversized/malformed response — is caught here and turned into
/// <see cref="UpdateCheckResult.Failed"/>; nothing propagates as an exception to the caller.
/// </remarks>
public sealed class GitHubReleaseUpdateChecker : IUpdateCheckService, IDisposable
{
    /// <summary>
    /// Hard cap on the response body this checker will read (a "latest release" JSON payload is a few
    /// KB; anything past this is treated as <see cref="UpdateCheckFailureKind.InvalidResponse"/> rather
    /// than buffered in full).
    /// </summary>
    private const int MaxResponseBytes = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IAppLog _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubReleaseUpdateChecker"/> class with a real,
    /// network-facing <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="log">Where a transport-level failure (e.g. an <see cref="HttpRequestException"/>) is recorded.</param>
    public GitHubReleaseUpdateChecker(IAppLog log)
        : this(new HttpClient(), log)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubReleaseUpdateChecker"/> class over a
    /// caller-supplied <see cref="HttpMessageHandler"/> instead of the real network stack.
    /// </summary>
    /// <remarks>Test-only seam: lets a test route requests through a fake handler returning canned responses.</remarks>
    /// <param name="handler">The message handler to route requests through.</param>
    /// <param name="log">See the public constructor.</param>
    internal GitHubReleaseUpdateChecker(HttpMessageHandler handler, IAppLog log)
        : this(new HttpClient(handler), log)
    {
    }

    private GitHubReleaseUpdateChecker(HttpClient httpClient, IAppLog log)
    {
        _httpClient = httpClient;
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DesktopOrganizer", AppVersionInfo.Display));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(UpdateEndpoints.ApiLatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return UpdateCheckResult.NoReleases();

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return UpdateCheckResult.Failed(UpdateCheckFailureKind.RateLimited);

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed(UpdateCheckFailureKind.Unexpected);

            return await ParseSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as an OperationCanceledException tied to an internal token —
            // distinguished here from the CALLER's own cancellationToken firing, which is left to
            // propagate as a normal cancellation rather than being reported as a "Failed" result.
            return UpdateCheckResult.Failed(UpdateCheckFailureKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            _log.Error("GitHubReleaseUpdateChecker", "CheckFailed", ex);
            return UpdateCheckResult.Failed(UpdateCheckFailureKind.Network);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Reads <paramref name="stream"/> fully, refusing to buffer more than <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="stream">The response body stream.</param>
    /// <param name="maxBytes">The cap — reached even one byte over returns <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The full body, or <see langword="null"/> if it exceeded <paramref name="maxBytes"/>.</returns>
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) return null;
        }

        return buffer.ToArray();
    }

    private async Task<UpdateCheckResult> ParseSuccessResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            return UpdateCheckResult.Failed(UpdateCheckFailureKind.InvalidResponse);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var body = await ReadLimitedAsync(stream, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (body is null) return UpdateCheckResult.Failed(UpdateCheckFailureKind.InvalidResponse);

            return ParseBody(body);
        }
    }

    private UpdateCheckResult ParseBody(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(UpdateCheckFailureKind.InvalidResponse);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("tag_name", out var tagNameElement) ||
                tagNameElement.ValueKind != JsonValueKind.String)
            {
                return UpdateCheckResult.Failed(UpdateCheckFailureKind.InvalidResponse);
            }

            var tagName = tagNameElement.GetString();
            if (!SemVersion.TryParse(tagName, out var latest) || latest is null)
                return UpdateCheckResult.Failed(UpdateCheckFailureKind.InvalidResponse);

            if (!SemVersion.TryParse(AppVersionInfo.Display, out var current) || current is null)
            {
                // The running build's own informational version is unparsable (e.g. a dev/local build)
                // — this checker cannot compare against "unknown", so it reports Unexpected rather than
                // guessing.
                return UpdateCheckResult.Failed(UpdateCheckFailureKind.Unexpected);
            }

            if (current.CompareTo(latest) >= 0)
                return UpdateCheckResult.UpToDate();

            var htmlUrl = document.RootElement.TryGetProperty("html_url", out var htmlUrlElement) &&
                htmlUrlElement.ValueKind == JsonValueKind.String
                ? htmlUrlElement.GetString()
                : null;
            var releaseUrl = UpdateEndpoints.TryGetTrustedReleaseUrl(htmlUrl) ?? UpdateEndpoints.ReleasesUrl;

            // latest.ToString() (not the raw tagName) — every other version display in this app
            // (AppVersionInfo.Display, the About section's own "Version 0.6.1") omits the "v" prefix
            // GitHub's own tag convention adds; this keeps the shown version consistent.
            return UpdateCheckResult.UpdateAvailable(latest.ToString(), releaseUrl);
        }
    }
}
