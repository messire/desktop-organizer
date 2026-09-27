namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Which of the four shapes an <see cref="UpdateCheckResult"/> can take.
/// </summary>
public enum UpdateCheckStatus
{
    /// <summary>The current build is the same as or newer than the latest published release.</summary>
    UpToDate,

    /// <summary>A newer release is published — <see cref="UpdateCheckResult.LatestVersion"/>/<see cref="UpdateCheckResult.ReleaseUrl"/> are set.</summary>
    UpdateAvailable,

    /// <summary>The repository has no published releases yet (GitHub returned <c>404</c>).</summary>
    NoReleases,

    /// <summary>The check could not be completed — see <see cref="UpdateCheckResult.FailureKind"/> for why.</summary>
    Failed,
}

/// <summary>
/// Why an <see cref="UpdateCheckStatus.Failed"/> check failed, coarse enough to show a helpful (but still
/// generic — no exception text, no stack trace) inline message.
/// </summary>
public enum UpdateCheckFailureKind
{
    /// <summary>Not applicable — the check did not fail.</summary>
    None,

    /// <summary>The request could not reach GitHub at all (DNS/TLS/connection failure).</summary>
    Network,

    /// <summary>The request did not complete within the 10-second timeout.</summary>
    Timeout,

    /// <summary>GitHub responded <c>403</c>/<c>429</c> (unauthenticated rate limit, ~60 requests/hour).</summary>
    RateLimited,

    /// <summary>
    /// GitHub responded with something this checker cannot make sense of: a non-success status other
    /// than <c>404</c>/<c>403</c>/<c>429</c>, a body over the 256&#160;KB cap, malformed JSON, or a JSON
    /// body missing a usable <c>tag_name</c>.
    /// </summary>
    InvalidResponse,

    /// <summary>Anything else (an unexpected exception this checker did not anticipate).</summary>
    Unexpected,
}

/// <summary>
/// Result of <see cref="IUpdateCheckService.CheckForUpdatesAsync"/>. Construct via the static factory
/// methods below rather than the primary constructor directly — they keep each <see cref="UpdateCheckStatus"/>
/// paired with exactly the fields that make sense for it.
/// </summary>
/// <param name="Status">Which of the four outcomes this is.</param>
/// <param name="LatestVersion">
/// The latest published release's version string (e.g. <c>"1.3.0"</c>, <c>v</c> prefix stripped), set only
/// for <see cref="UpdateCheckStatus.UpdateAvailable"/>.
/// </param>
/// <param name="ReleaseUrl">
/// The page to send the user to for <see cref="UpdateCheckStatus.UpdateAvailable"/> — GitHub's own
/// <c>html_url</c> for the release when it points at this project's own repository, otherwise the
/// repository's releases page as a safe fallback. Set only for <see cref="UpdateCheckStatus.UpdateAvailable"/>.
/// </param>
/// <param name="FailureKind">Why the check failed; <see cref="UpdateCheckFailureKind.None"/> unless <paramref name="Status"/> is <see cref="UpdateCheckStatus.Failed"/>.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersion,
    string? ReleaseUrl,
    UpdateCheckFailureKind FailureKind)
{
    /// <summary>The current build is already the latest (or newer than any published release).</summary>
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate, LatestVersion: null, ReleaseUrl: null, UpdateCheckFailureKind.None);

    /// <summary>A newer release than the current build is published.</summary>
    /// <param name="latestVersion">See <see cref="UpdateCheckResult.LatestVersion"/>.</param>
    /// <param name="releaseUrl">See <see cref="UpdateCheckResult.ReleaseUrl"/>.</param>
    public static UpdateCheckResult UpdateAvailable(string latestVersion, string releaseUrl) =>
        new(UpdateCheckStatus.UpdateAvailable, latestVersion, releaseUrl, UpdateCheckFailureKind.None);

    /// <summary>The repository has no published releases yet.</summary>
    public static UpdateCheckResult NoReleases() => new(UpdateCheckStatus.NoReleases, LatestVersion: null, ReleaseUrl: null, UpdateCheckFailureKind.None);

    /// <summary>The check could not be completed.</summary>
    /// <param name="kind">See <see cref="UpdateCheckResult.FailureKind"/>.</param>
    public static UpdateCheckResult Failed(UpdateCheckFailureKind kind) => new(UpdateCheckStatus.Failed, LatestVersion: null, ReleaseUrl: null, kind);
}

/// <summary>
/// Manual, on-demand "is a newer version published" check (release-hardening-1.0.md §G2) — the app's
/// first-ever network call. Deliberately narrow: there is no persisted "last checked" state and nothing
/// runs this automatically at startup (CLAUDE.md persistence freeze; and a first network feature staying
/// opt-in-per-click is the safer default for a desktop utility) — the Settings window's About section is
/// the only caller. Implementations must never throw: every failure mode — offline, rate-limited, a
/// malformed response — is reported through <see cref="UpdateCheckResult.Status"/>/<see cref="UpdateCheckResult.FailureKind"/>.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Asks GitHub for this project's latest published release and compares it against the running
    /// build's own version (semver precedence, <see cref="DesktopOrganizer.Core.SemVersion"/>).
    /// </summary>
    /// <param name="cancellationToken">Cancels the request; the request also self-cancels after a fixed 10-second timeout regardless.</param>
    /// <returns>The outcome — never throws.</returns>
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}
