namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Forward-compatible enum parsing for persisted fields (ADR-024 Amendment "Additive revisions",
/// docs/PLANS/schema-additive-revision-v7.md §3). Persisted enum fields are plain strings in the DTOs; an unknown
/// member name (written by a newer build) must never throw — it degrades to the documented default and the raw
/// string is remembered by the caller so it can be written back unchanged.
/// </summary>
internal static class TolerantEnumParser
{
    /// <summary>
    /// Parses <paramref name="raw"/> as a member name of <typeparamref name="TEnum"/>, case-insensitively.
    /// Rejects (as unrecognised) comma-separated combinations and numeric strings (<c>"2"</c>, <c>"-1"</c>) —
    /// this keeps the meaning of <c>JsonStringEnumConverter(allowIntegerValues: false)</c> now that the DTOs no
    /// longer use the real enum types. A <see langword="null"/> or empty value is "absent": the fallback, reported as
    /// recognised (there is nothing to preserve).
    /// </summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="raw">The persisted string.</param>
    /// <param name="fallback">The documented default returned for absent or unrecognised input.</param>
    /// <param name="recognized"><see langword="false"/> when a non-empty <paramref name="raw"/> was not a defined member name.</param>
    public static TEnum Parse<TEnum>(string? raw, TEnum fallback, out bool recognized)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(raw))
        {
            recognized = true;
            return fallback;
        }

        var trimmed = raw.AsSpan().Trim();
        if (!trimmed.IsEmpty
            && !char.IsAsciiDigit(trimmed[0])
            && trimmed[0] is not '-' and not '+'
            && !raw.Contains(',')
            && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            recognized = true;
            return parsed;
        }

        recognized = false;
        return fallback;
    }

    /// <summary>
    /// Tells whether an already-parsed value (produced by one of the pre-existing, differently shaped parsers such
    /// as <c>PanelGeometryModeParser</c>) came from a recognised input: "the parser did not fall back" — or it fell
    /// back because the input literally named the fallback member, or the input was absent.
    /// </summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="raw">The persisted string that was parsed.</param>
    /// <param name="parsed">The parser's result.</param>
    /// <param name="fallback">The parser's documented fallback member.</param>
    public static bool IsRecognized<TEnum>(string? raw, TEnum parsed, TEnum fallback)
        where TEnum : struct, Enum
        => string.IsNullOrEmpty(raw)
           || !EqualityComparer<TEnum>.Default.Equals(parsed, fallback)
           || string.Equals(raw.Trim(), fallback.ToString(), StringComparison.OrdinalIgnoreCase);
}
