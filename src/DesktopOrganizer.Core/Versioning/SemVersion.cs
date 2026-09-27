using System.Globalization;

namespace DesktopOrganizer.Core;

/// <summary>
/// A parsed <see href="https://semver.org/">Semantic Versioning 2.0.0</see> version — release-hardening
/// package G2's update-check feature needs to compare "the version this build reports"
/// (<c>AppVersionInfo.Display</c>) against "the version GitHub's latest-release API reports"
/// (a <c>tag_name</c> like <c>v1.2.3</c>) without either side ever throwing on a malformed or
/// unexpected tag.
/// </summary>
/// <remarks>
/// Comparison follows semver.org §11 precedence exactly: major/minor/patch compare numerically; a
/// version WITHOUT a pre-release always outranks the same core version WITH one; two pre-release
/// strings compare identifier-by-identifier (dot-separated), where a purely-numeric identifier always
/// sorts before an alphanumeric one, two numeric identifiers compare numerically, and two alphanumeric
/// identifiers compare with ordinal (ASCII) string comparison; if every shared identifier is equal, the
/// pre-release with MORE identifiers wins. Build metadata (a trailing <c>+...</c>) is parsed but never
/// affects precedence, per spec. <see cref="TryParse"/> also accepts an optional leading <c>v</c>/<c>V</c>
/// (GitHub's own release-tag convention), which is not part of the semver grammar itself.
/// </remarks>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, string? prerelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    /// <summary>Gets the major version component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version component.</summary>
    public int Patch { get; }

    /// <summary>
    /// Gets the pre-release identifier (the part after a <c>-</c>, before any <c>+</c>), e.g.
    /// <c>"beta.1"</c> for <c>1.2.3-beta.1</c>, or <see langword="null"/> if this is a release version.
    /// </summary>
    public string? Prerelease { get; }

    /// <summary>
    /// Gets the build metadata (the part after a <c>+</c>), e.g. <c>"ab12cd34"</c> for
    /// <c>1.2.3+ab12cd34</c>, or <see langword="null"/> if none was present. Never affects
    /// <see cref="CompareTo"/>/equality (semver.org §10).
    /// </summary>
    public string? BuildMetadata { get; }

    /// <summary>
    /// Attempts to parse <paramref name="input"/> as a semantic version, tolerating a leading
    /// <c>v</c>/<c>V</c> (e.g. GitHub's <c>tag_name</c> convention). Never throws — a malformed or
    /// <see langword="null"/>/blank input simply returns <see langword="false"/>.
    /// </summary>
    /// <param name="input">The version string to parse, e.g. <c>"v1.2.3"</c> or <c>"1.2.3-beta.1+sha"</c>.</param>
    /// <param name="version">The parsed version on success; <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> if <paramref name="input"/> was a valid (optionally <c>v</c>-prefixed) semver string.</returns>
    public static bool TryParse(string? input, out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
            text = text[1..];

        string? buildMetadata = null;
        var plusIndex = text.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            buildMetadata = text[(plusIndex + 1)..];
            text = text[..plusIndex];
            if (buildMetadata.Length == 0) return false;
        }

        string? prerelease = null;
        var dashIndex = text.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            prerelease = text[(dashIndex + 1)..];
            text = text[..dashIndex];
            if (prerelease.Length == 0 || !IsValidPrerelease(prerelease)) return false;
        }

        var core = text.Split('.');
        if (core.Length != 3) return false;

        if (!TryParseCoreComponent(core[0], out var major) ||
            !TryParseCoreComponent(core[1], out var minor) ||
            !TryParseCoreComponent(core[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, prerelease, buildMetadata);
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;

        var coreCompare = Major.CompareTo(other.Major);
        if (coreCompare != 0) return coreCompare;

        coreCompare = Minor.CompareTo(other.Minor);
        if (coreCompare != 0) return coreCompare;

        coreCompare = Patch.CompareTo(other.Patch);
        if (coreCompare != 0) return coreCompare;

        // Build metadata never affects precedence (semver.org §10).
        var hasPrerelease = Prerelease is not null;
        var otherHasPrerelease = other.Prerelease is not null;
        if (hasPrerelease == otherHasPrerelease)
            return hasPrerelease ? ComparePrerelease(Prerelease!, other.Prerelease!) : 0;

        // A release outranks a pre-release of the same core version (semver.org §11.3).
        return hasPrerelease ? -1 : 1;
    }

    /// <inheritdoc/>
    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SemVersion);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    /// <inheritdoc/>
    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        var withPrerelease = Prerelease is null ? core : $"{core}-{Prerelease}";
        return BuildMetadata is null ? withPrerelease : $"{withPrerelease}+{BuildMetadata}";
    }

    private static bool TryParseCoreComponent(string component, out int value)
    {
        value = 0;
        if (component.Length == 0) return false;
        if (component.Length > 1 && component[0] == '0') return false; // no leading zeros (semver.org §2)
        foreach (var c in component)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        return int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidPrerelease(string prerelease)
    {
        foreach (var identifier in prerelease.Split('.'))
        {
            if (identifier.Length == 0) return false;

            var isNumeric = true;
            foreach (var c in identifier)
            {
                if (char.IsAsciiLetterOrDigit(c) || c == '-') continue;
                return false;
            }

            foreach (var c in identifier)
            {
                if (char.IsAsciiDigit(c)) continue;
                isNumeric = false;
                break;
            }

            // A purely-numeric identifier may not have a leading zero (semver.org §9).
            if (isNumeric && identifier.Length > 1 && identifier[0] == '0') return false;
        }

        return true;
    }

    private static int ComparePrerelease(string a, string b)
    {
        var aIds = a.Split('.');
        var bIds = b.Split('.');
        var sharedLength = Math.Min(aIds.Length, bIds.Length);

        for (var i = 0; i < sharedLength; i++)
        {
            var compare = CompareIdentifier(aIds[i], bIds[i]);
            if (compare != 0) return compare;
        }

        // More identifiers (with every shared one equal) outranks fewer (semver.org §11.4.4).
        return aIds.Length.CompareTo(bIds.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aIsNumeric = IsNumericIdentifier(a);
        var bIsNumeric = IsNumericIdentifier(b);

        if (aIsNumeric && bIsNumeric)
        {
            // Neither identifier has a leading zero (validated on parse), so comparing by length first,
            // then ordinally, is equivalent to comparing the two as arbitrary-precision integers.
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        // A numeric identifier always has lower precedence than an alphanumeric one (semver.org §11.4.3).
        if (aIsNumeric != bIsNumeric) return aIsNumeric ? -1 : 1;

        return string.CompareOrdinal(a, b);
    }

    private static bool IsNumericIdentifier(string identifier)
    {
        foreach (var c in identifier)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        return true;
    }
}
