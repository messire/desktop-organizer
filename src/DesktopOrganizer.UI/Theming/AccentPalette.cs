using System.Text.RegularExpressions;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// The six selectable panel accent colours plus the non-selectable <see cref="Neutral"/> fallback
/// (ADR-025 §3.1, docs/PLANS/phase-5-ambient-accent-ui.md §3.1). Pure string/constant logic only —
/// these hex values are mirrored verbatim by the <c>Color.Accent.*</c> section of
/// <c>Resources/Styles/Colors.xaml</c> (C1); keep the two in sync by construction if either changes.
/// </summary>
public static class AccentPalette
{
    /// <summary>Migration/global fallback accent — never a preset (see <see cref="IsPreset"/>).</summary>
    public const string Neutral = "#6B7280";

    /// <summary>The "Blue" selectable accent preset.</summary>
    public const string Blue = "#3B82F6";

    /// <summary>The "Violet" selectable accent preset.</summary>
    public const string Violet = "#A855F7";

    /// <summary>The "Amber" selectable accent preset.</summary>
    public const string Amber = "#F59E0B";

    /// <summary>The "Green" selectable accent preset.</summary>
    public const string Green = "#22C55E";

    /// <summary>The "Cyan" selectable accent preset.</summary>
    public const string Cyan = "#06B6D4";

    /// <summary>The "Coral" selectable accent preset.</summary>
    public const string Coral = "#F06464";

    /// <summary>
    /// Schema v1-v3's historical default panel background (<c>Properties.Settings</c>'s own shipped
    /// <c>DefaultBackgroundColor</c> default) — see <see cref="ResolveDefaultAccent"/>. Mirrors
    /// <c>V3ToV4Migration.LegacyDefaultBackgroundColor</c>, which applies the identical substitution to
    /// EXISTING panels; kept in sync with that constant by construction/comment, the same way this
    /// class's six preset constants are kept in sync with <c>Color.Accent.*</c>.
    /// </summary>
    public const string LegacyDefaultBackgroundColor = "#1E1E1E";

    /// <summary>
    /// Matches a 3-, 4-, 6-, or 8-digit hex colour, e.g. <c>#abc</c>, <c>#8abc</c> (ARGB), <c>#AABBCC</c>,
    /// or <c>#CCAABBCC</c> (AARRGGBB) — the same shape <see cref="DesktopOrganizer.UI.Converters.HexBrushConverter"/>
    /// accepts, so a v3 panel's ARGB <c>backgroundColor</c> normalizes here instead of being silently rejected.
    /// </summary>
    private static readonly Regex HexPattern = new(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    /// <summary>The six selectable accent presets, in ADR-025's canonical order. Excludes <see cref="Neutral"/>.</summary>
    public static IReadOnlyList<string> Presets { get; } = new[] { Blue, Violet, Amber, Green, Cyan, Coral };

    /// <summary>The default accent for new/unaccented panels.</summary>
    public static string Default => Neutral;

    /// <summary>
    /// Normalizes a hex colour string to canonical <c>#RRGGBB</c> uppercase form (e.g. <c>" #abc "</c>
    /// or <c>"#AABBCC"</c> both become <c>"#AABBCC"</c>). Also accepts an alpha-prefixed 4- or 8-digit
    /// input (<c>#8abc</c>/<c>#CCAABBCC</c>) and drops the alpha nibble/byte — the panel's own accent
    /// alpha is already controlled separately via <see cref="SurfaceComposer.AccentTint"/>, so accent
    /// hex strings only ever carry RGB. Never throws: <see langword="null"/>, empty, whitespace-only, or
    /// any string that isn't one of the four accepted digit lengths returns <see cref="Default"/> instead.
    /// </summary>
    /// <param name="hex">The candidate hex colour string to normalize.</param>
    public static string Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Default;

        var trimmed = hex.Trim();
        if (!HexPattern.IsMatch(trimmed))
            return Default;

        var digits = trimmed[1..];
        var rgb = digits.Length switch
        {
            3 => string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]),
            4 => string.Concat(digits[1], digits[1], digits[2], digits[2], digits[3], digits[3]), // #ARGB: drop alpha nibble, double the rest
            8 => digits[2..], // #AARRGGBB: drop the leading alpha byte, keep RRGGBB
            _ => digits, // 6 digits: already bare RRGGBB
        };

        return "#" + rgb.ToUpperInvariant();
    }

    /// <summary>
    /// Resolves the accent colour to seed a NEWLY CREATED panel with, from the raw
    /// <c>AppSettings.DefaultBackgroundColor</c>/<c>GlobalDefaults.DefaultBackgroundColor</c> value —
    /// kept as an AppSettings-level key verbatim per ADR-025 D-10 (DO-014), which now seeds a new
    /// panel's <c>AccentColor</c>. <c>Properties.Settings</c> still ships <see cref="LegacyDefaultBackgroundColor"/>
    /// (<c>#1E1E1E</c>) as ITS OWN default — a near-black background colour that reads as an almost
    /// invisible accent once rendered through the Ambient Accent system (CRITICAL-1) — so, exactly like
    /// <c>V3ToV4Migration</c> already does for EXISTING panels, that one specific legacy value is swapped
    /// for <see cref="Neutral"/> here too. <see langword="null"/>, empty, and whitespace-only input also
    /// map to <see cref="Neutral"/> (not returned verbatim) — otherwise a blank value could round-trip
    /// all the way into a panel's persisted <c>AccentColor</c> and only get caught later, downstream, by
    /// <see cref="Normalize"/>'s own fallback. Any OTHER value is returned verbatim, untouched by
    /// <see cref="Normalize"/>: once a user has actually changed "Default background color" away from the
    /// shipped default, that is a deliberate choice and must not be second-guessed or reformatted.
    /// </summary>
    /// <param name="defaultBackgroundColor">The raw <c>AppSettings.DefaultBackgroundColor</c> value to resolve from.</param>
    public static string ResolveDefaultAccent(string? defaultBackgroundColor)
    {
        if (string.IsNullOrWhiteSpace(defaultBackgroundColor))
            return Neutral;

        return string.Equals(defaultBackgroundColor.Trim(), LegacyDefaultBackgroundColor, StringComparison.OrdinalIgnoreCase)
            ? Neutral
            : defaultBackgroundColor;
    }

    /// <summary>
    /// Whether <paramref name="hex"/> (after <see cref="Normalize"/>) is one of the six selectable
    /// <see cref="Presets"/>. <see cref="Neutral"/> and any junk/unrecognized input return <see langword="false"/>.
    /// </summary>
    /// <param name="hex">The candidate hex colour string to test.</param>
    public static bool IsPreset(string? hex)
    {
        var normalized = Normalize(hex);
        foreach (var preset in Presets)
        {
            if (string.Equals(preset, normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
