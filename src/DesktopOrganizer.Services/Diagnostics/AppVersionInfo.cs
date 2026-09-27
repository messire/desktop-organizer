using System.Reflection;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// The single source of truth for "what version is this build" (plan §0.4), reading
/// <see cref="AssemblyInformationalVersionAttribute"/> off this very assembly rather than
/// <c>AssemblyName.Version</c> — <c>Directory.Build.props</c> pins <c>AssemblyVersion</c> to a fixed
/// <c>1.0.0.0</c> for every project in the solution (so <c>user.config</c>'s per-version folder never
/// moves across releases), which makes <c>AssemblyName.Version</c> permanently wrong for display
/// purposes. <c>&lt;Version&gt;</c>/informational-version metadata is set once, solution-wide, in
/// <c>Directory.Build.props</c>, so any assembly's own attribute carries the same release version and
/// (when present) the same <c>+&lt;sha&gt;</c> build-metadata suffix the .NET SDK appends automatically
/// under a deterministic/CI build.
/// </summary>
public static class AppVersionInfo
{
    /// <summary>
    /// The full informational version, including any <c>+&lt;sha&gt;</c> build-metadata suffix, or
    /// <c>"unknown"</c> if the attribute is missing/blank.
    /// </summary>
    public static string Full { get; } = ResolveFull();

    /// <summary>
    /// Just the release version (e.g. <c>"0.6.1"</c>) — <see cref="Full"/> with any <c>+&lt;sha&gt;</c>
    /// build-metadata suffix stripped. What ships in user-facing surfaces (About section, crash dialog).
    /// </summary>
    public static string Display { get; } = StripBuildMetadata(Full);

    /// <summary>
    /// Test-only pure helper: strips everything from the first <c>+</c> onward. Exposed so
    /// <c>AppVersionInfoTests</c> can pin the stripping behavior deterministically without depending on
    /// whatever <c>+&lt;sha&gt;</c> suffix (or lack of one) happens to be present in this particular build.
    /// </summary>
    /// <param name="full">The full informational version, e.g. <c>"0.6.1+ab12cd34"</c>.</param>
    /// <returns><paramref name="full"/> with any <c>+</c>-prefixed suffix removed.</returns>
    internal static string StripBuildMetadata(string full)
    {
        var plusIndex = full.IndexOf('+', StringComparison.Ordinal);
        return plusIndex < 0 ? full : new string(full.AsSpan(0, plusIndex));
    }

    private static string ResolveFull()
    {
        var infoVersion = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(infoVersion) ? "unknown" : infoVersion;
    }
}
