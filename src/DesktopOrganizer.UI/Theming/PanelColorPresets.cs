namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// One selectable (accent, dark-mode text, light-mode text) triple a user can apply to a panel via
/// <c>PanelColorEditor</c> (ADR-042). Both text hex values are canonical uppercase <c>#RRGGBB</c>; which
/// one actually applies to a panel is picked by <see cref="TextFor"/> from the panel's
/// <see cref="PanelThemeMode"/> (Light or Dark), itself chosen by the user alongside the accent — the
/// preset no longer hard-codes a single text colour.
/// </summary>
/// <param name="Id">Stable, code-facing identifier (e.g. <c>"Red"</c>) — also the suffix of <see cref="NameKey"/>.</param>
/// <param name="AccentHex">The preset's accent colour, canonical uppercase <c>#RRGGBB</c>.</param>
/// <param name="DarkModeTextHex">This preset's text colour when applied in <see cref="PanelThemeMode.Dark"/>.</param>
/// <param name="LightModeTextHex">This preset's text colour when applied in <see cref="PanelThemeMode.Light"/>.</param>
public sealed record PanelColorPreset(string Id, string AccentHex, string DarkModeTextHex, string LightModeTextHex)
{
    /// <summary>The RESX key of this preset's localized display name.</summary>
    public string NameKey => "ColorPreset_" + Id;

    /// <summary>Returns <see cref="DarkModeTextHex"/> or <see cref="LightModeTextHex"/> for <paramref name="mode"/>.</summary>
    /// <param name="mode">Which mode's text colour to return.</param>
    public string TextFor(PanelThemeMode mode) => mode == PanelThemeMode.Light ? LightModeTextHex : DarkModeTextHex;
}

/// <summary>
/// The twelve selectable panel colour presets (ADR-042), in spectrum order with the two neutrals last —
/// distinct from, and never merged with, the six legacy <see cref="AccentPalette.Presets"/> accents (those
/// remain test-only, ADR-025). Pure data plus the hex-matching logic <c>PanelColorEditorViewModel</c> uses
/// to highlight the preset that exactly matches a panel's current accent. Replaces the previous ten-preset
/// palette (Azure/Violet/Coral/Forest/Graphite/Sky/Lavender/Peach/Mint/Rose) and its accent-plus-single-text
/// shape: each preset now carries both a dark-mode and a light-mode text colour (<see cref="PanelColorPreset.TextFor"/>),
/// since ADR-042 makes the theme mode an explicit user choice (<see cref="PanelThemeMode"/>) rather than a
/// property baked into the preset.
/// </summary>
public static class PanelColorPresets
{
    // Backlog 1.x item 4 (§4.2a, DarkModeTextHex): each CHROMATIC preset below now carries its OWN pale
    // tint of its accent's hue (HSL lightness ~91%, saturation ~32%) instead of the shared plain white
    // every preset used before this change — the root cause of "the preset doesn't seem to change the
    // text" in Dark mode (every preset wrote the identical #FFFFFF). Every value is measured (see
    // PanelColorPresetsContrastTests.ComposedSurface_MeetsMinimumTextContrast_ForEveryStyleAndOpacity) to
    // clear ContrastCalculator.MinimumTextContrast (4.5:1) against SurfaceComposer.ComposeSurface's output
    // for every PanelSurfaceStyle at both SurfaceComposer.MinSurfaceOpacity and MaxSurfaceOpacity, and to
    // still resolve PanelThemeMode.Dark via PanelThemeRules.FromTextColor (its contrast against
    // SurfaceComposer.DarkNeutralBase stays far higher than against LightNeutralBase — a light-lightness-
    // 91% tint is still much brighter than the dark base and only barely dimmer than the near-white
    // LightNeutralBase, so it never "jumps" mode). The two NEUTRAL presets (Graphite, Stone) keep the
    // original plain white — ADR-042 amendment explicitly allows neutrals to stay white/near-white.

    /// <summary>The "Red" preset.</summary>
    public static PanelColorPreset Red { get; } = new("Red", "#D9534F", "#EFE1E1", "#4A1F1E");

    /// <summary>The "Orange" preset.</summary>
    public static PanelColorPreset Orange { get; } = new("Orange", "#E0812F", "#EFE8E1", "#4B2A0C");

    /// <summary>The "Amber" preset.</summary>
    public static PanelColorPreset Amber { get; } = new("Amber", "#D6A324", "#EFEBE1", "#453309");

    /// <summary>The "Lime" preset.</summary>
    public static PanelColorPreset Lime { get; } = new("Lime", "#8FB43A", "#EBEFE1", "#2F3D11");

    /// <summary>The "Green" preset.</summary>
    public static PanelColorPreset Green { get; } = new("Green", "#3F9A5A", "#E1EFE5", "#173A24");

    /// <summary>The "Teal" preset.</summary>
    public static PanelColorPreset Teal { get; } = new("Teal", "#2F9C93", "#E1EFEE", "#12403C");

    /// <summary>The "Cyan" preset.</summary>
    public static PanelColorPreset Cyan { get; } = new("Cyan", "#2E98C8", "#E1EBEF", "#123B4E");

    /// <summary>The "Blue" preset — <see cref="Default"/>.</summary>
    public static PanelColorPreset Blue { get; } = new("Blue", "#3F7FD9", "#E1E7EF", "#172F4B");

    /// <summary>The "Violet" preset.</summary>
    public static PanelColorPreset Violet { get; } = new("Violet", "#7A5BE7", "#E4E1EF", "#2B2350");

    /// <summary>The "Magenta" preset.</summary>
    public static PanelColorPreset Magenta { get; } = new("Magenta", "#C8559E", "#EFE1EA", "#4A1F3B");

    /// <summary>The "Graphite" preset (neutral) — stays plain white in Dark mode (no accent hue to tint toward that would read as meaningfully distinct).</summary>
    public static PanelColorPreset Graphite { get; } = new("Graphite", "#596273", "#FFFFFF", "#22262E");

    /// <summary>The "Stone" preset (neutral) — stays plain white in Dark mode, same rationale as <see cref="Graphite"/>.</summary>
    public static PanelColorPreset Stone { get; } = new("Stone", "#7D7468", "#FFFFFF", "#2E2A26");

    /// <summary>All twelve presets, in the exact spectrum order they are displayed (neutrals last).</summary>
    public static IReadOnlyList<PanelColorPreset> All { get; } =
        new[] { Red, Orange, Amber, Lime, Green, Teal, Cyan, Blue, Violet, Magenta, Graphite, Stone };

    /// <summary>The default preset for new/unaccented panels — <see cref="Blue"/>.</summary>
    public static PanelColorPreset Default => Blue;

    /// <summary>
    /// The seed colours for the <c>ColorDialog</c>'s 16 custom-color slots: the twelve accents in
    /// <see cref="All"/> order, followed by (at most) four distinct text colours — collected by scanning
    /// <see cref="All"/> in order and, for each preset, considering <see cref="PanelColorPreset.DarkModeTextHex"/>
    /// then <see cref="PanelColorPreset.LightModeTextHex"/>, adding the first four distinct (case-insensitive)
    /// values encountered. Since backlog 1.x item 4 (§4.2a) every preset's own <see cref="PanelColorPreset.DarkModeTextHex"/>
    /// is now distinct per chromatic preset (no longer a single shared <c>#FFFFFF</c>), this yields the
    /// first two presets' own (dark, light) pair — <see cref="Red"/>'s <see cref="PanelColorPreset.DarkModeTextHex"/>/
    /// <see cref="PanelColorPreset.LightModeTextHex"/> then <see cref="Orange"/>'s own pair — always exactly
    /// 16 entries.
    /// </summary>
    public static IReadOnlyList<string> CustomDialogSeed { get; } = BuildCustomDialogSeed();

    /// <summary>
    /// Finds the preset whose <see cref="PanelColorPreset.AccentHex"/> matches <paramref name="accentHex"/>
    /// exactly, after normalizing both through <see cref="TryNormalizeOpaqueRgb"/> (case-insensitively).
    /// Returns <see langword="null"/> if <paramref name="accentHex"/> is not a valid opaque RGB hex string,
    /// or if no preset's accent matches it. Deliberately does not use <see cref="AccentPalette.Normalize"/>:
    /// that method maps unrecognized/junk input to <see cref="AccentPalette.Neutral"/>, which would make a
    /// piece of junk falsely match a preset whose accent happens to equal that fallback.
    /// </summary>
    /// <param name="accentHex">The candidate accent colour.</param>
    public static PanelColorPreset? FindByAccent(string? accentHex)
    {
        if (!TryNormalizeOpaqueRgb(accentHex, out var accent))
            return null;

        foreach (var preset in All)
        {
            if (string.Equals(preset.AccentHex, accent, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return null;
    }

    /// <summary>
    /// Normalizes an opaque hex colour string (<c>#RGB</c>, <c>#RRGGBB</c>, <c>#FRGB</c>, or
    /// <c>#FFRRGGBB</c> — alpha nibble/byte must be fully opaque, i.e. <c>F</c>/<c>FF</c>) to canonical
    /// uppercase <c>#RRGGBB</c>. Returns <see langword="false"/> for <see langword="null"/>/empty/whitespace
    /// input, malformed hex, an unrecognized digit count, or any non-opaque alpha. Never throws.
    /// </summary>
    /// <param name="hex">The candidate hex colour string.</param>
    /// <param name="normalized">The canonical uppercase <c>#RRGGBB</c> form on success; <see cref="string.Empty"/> otherwise.</param>
    internal static bool TryNormalizeOpaqueRgb(string? hex, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var trimmed = hex.Trim();
        var hasRecognizedLength = trimmed.Length is 4 or 5 or 7 or 9;
        if (!hasRecognizedLength || trimmed[0] != '#')
            return false;

        var digits = trimmed[1..];
        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        string rgb;
        switch (digits.Length)
        {
            case 3:
                rgb = string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]);
                break;
            case 4:
                var isOpaqueAlphaNibble = digits[0] is 'F' or 'f';
                if (!isOpaqueAlphaNibble)
                    return false;
                rgb = string.Concat(digits[1], digits[1], digits[2], digits[2], digits[3], digits[3]);
                break;
            case 6:
                rgb = digits;
                break;
            case 8:
                if (!string.Equals(digits[..2], "FF", StringComparison.OrdinalIgnoreCase))
                    return false;
                rgb = digits[2..];
                break;
            default:
                return false;
        }

        normalized = "#" + rgb.ToUpperInvariant();
        return true;
    }

    private static IReadOnlyList<string> BuildCustomDialogSeed()
    {
        const int maxDistinctTexts = 4;

        var seed = new List<string>(16);
        foreach (var preset in All)
            seed.Add(preset.AccentHex);

        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textCandidates = All.SelectMany(p => new[] { p.DarkModeTextHex, p.LightModeTextHex });
        foreach (var text in textCandidates)
        {
            if (seenTexts.Count >= maxDistinctTexts)
                break;

            if (seenTexts.Add(text))
                seed.Add(text);
        }

        return seed;
    }
}
