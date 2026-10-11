namespace DesktopOrganizer.Services.Updates;

/// <summary>
/// The fixed set of URLs the update-check feature (release-hardening-1.0.md §G2) and the About section's
/// "Links" row talk to — every one a compile-time constant, never user-configurable (this project has no
/// settings surface for "which repository/URL to check", by design).
/// </summary>
public static class UpdateEndpoints
{
    /// <summary>The GitHub <c>owner/repo</c> slug this project is published under.</summary>
    public const string Repo = "messire/DesktopOrganizer";

    /// <summary>GitHub REST API endpoint for this repository's latest published release.</summary>
    public const string ApiLatestReleaseUrl = "https://api.github.com/repos/messire/DesktopOrganizer/releases/latest";

    /// <summary>The repository's releases page — the "View release" fallback when a release's own <c>html_url</c> isn't trusted (see <see cref="TryGetTrustedReleaseUrl"/>).</summary>
    public const string ReleasesUrl = "https://github.com/messire/DesktopOrganizer/releases";

    /// <summary>Pre-filled "new issue" page of the source repository. Not used anywhere in <c>src</c>: it is no longer linked from the UI (the repository is private, see <see cref="SupportUrl"/>) and only tests reference it, to pin that <see cref="SupportUrl"/> is not this page. Kept as the future public tracker address.</summary>
    public const string IssuesUrl = "https://github.com/messire/DesktopOrganizer/issues/new";

    /// <summary>
    /// The public web page of the product — the single place every user-facing "project"/"support"/
    /// "privacy" link resolves to, so switching to the owner's real public page is a one-constant change.
    /// </summary>
    /// <remarks>Owner's public site (GitHub Pages, separate public site-only repository; the source repository stays private). Changing the address is a one-constant edit here plus the Partner Center listing fields.</remarks>
    public const string PublicSiteUrl = "https://messire.github.io/desktop-organizer-site/";

    /// <summary>Where "Report a problem" leads — currently <see cref="PublicSiteUrl"/>, see there.</summary>
    public const string SupportUrl = PublicSiteUrl;

    /// <summary>The privacy-policy page for the Store listing (<see cref="PublicSiteUrl"/> + <c>privacy</c>). Not used by any code yet (no in-app link points at it); it is the single place the address is recorded.</summary>
    public const string PrivacyPolicyUrl = PublicSiteUrl + "privacy";

    /// <summary>The About section's "Project page" link — <see cref="PublicSiteUrl"/>.</summary>
    public const string ProjectPageUrl = PublicSiteUrl;

    /// <summary>Path prefix every trusted <c>html_url</c> must have — GitHub's own API response for OUR release, never blindly trusted third-party content.</summary>
    private const string TrustedPathPrefix = "/messire/DesktopOrganizer/";

    /// <summary>
    /// M4 (release-hardening review): validates <paramref name="htmlUrl"/> as an absolute
    /// <c>https://github.com/messire/DesktopOrganizer/…</c> URL — the only <c>html_url</c> value from the
    /// release-check API response this checker will ever open directly, rather than falling back to the
    /// fixed <see cref="ReleasesUrl"/>. Validated via <see cref="Uri"/> (scheme, host, and path parsed and
    /// compared structurally) rather than a raw string prefix, so a value such as
    /// <c>https://github.com.evil.example/…</c> or <c>https://evil.example/redirect?=https://github.com/…</c>
    /// — which a naive <c>StartsWith("https://github.com/messire/")</c> could be tricked by by construction
    /// only in degenerate cases, but which is still safer to reject via structural parsing — cannot pass.
    /// </summary>
    /// <param name="htmlUrl">The <c>html_url</c> field from the GitHub releases API response, or <see langword="null"/>.</param>
    /// <returns>
    /// The trusted URL's own <see cref="Uri.AbsoluteUri"/> (the canonical form <see cref="Uri"/> parsed it
    /// into, not necessarily byte-identical to <paramref name="htmlUrl"/>) if it is trusted; otherwise
    /// <see langword="null"/>.
    /// </returns>
    public static string? TryGetTrustedReleaseUrl(string? htmlUrl)
    {
        if (htmlUrl is null) return null;
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.AbsolutePath.StartsWith(TrustedPathPrefix, StringComparison.Ordinal)) return null;

        return uri.AbsoluteUri;
    }
}
