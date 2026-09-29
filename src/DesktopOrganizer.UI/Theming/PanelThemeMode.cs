using System.Windows.Media;
using DesktopOrganizer.UI.Converters;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Explicit panel theme mode (ADR-042, UX research brief: Windows 11/Fluent/Material convention of an
/// explicit Light/Dark mode plus an accent, rather than deriving surface darkness implicitly per token).
/// Under the FROZEN persistence schema (ADR-024, schemaVersion 5 — see CLAUDE.md "Настройки и persistence")
/// there is no stored mode field; <see cref="PanelThemeRules.FromTextColor(string?)"/> DERIVES this from
/// the panel's own already-stored <c>Model.TextColor</c> instead of adding one.
/// </summary>
public enum PanelThemeMode
{
    /// <summary>A dark panel surface with light text — every panel this app shipped before ADR-042.</summary>
    Dark,

    /// <summary>A light panel surface with dark text.</summary>
    Light,
}

/// <summary>
/// Derives a panel's <see cref="PanelThemeMode"/> from its stored <c>Model.TextColor</c> — the single
/// FROZEN field (ADR-024) this app persists that a mode can be derived from, so ADR-042 does not need a
/// new stored field. Rule (orchestrator decision, release-polish-1.0 review round M1):
/// <see cref="PanelThemeMode.Light"/> iff the text colour's WCAG contrast ratio against
/// <see cref="SurfaceComposer.LightNeutralBase"/> is STRICTLY GREATER than its contrast ratio against
/// <see cref="SurfaceComposer.DarkNeutralBase"/> (the text simply reads better on a light panel surface
/// than on a dark one); <see cref="PanelThemeMode.Dark"/> otherwise, including an exact tie. This replaces
/// the previous fixed <c>0.5</c> WCAG-relative-luminance threshold (which classified some genuinely
/// mid-grey text, e.g. <c>#808080</c>/<c>#A0A0A0</c>, as Light even though such a colour reads BETTER on
/// this app's actual dark base than on its actual light base) with a rule measured against the two real
/// bases <see cref="SurfaceComposer.ResolveNeutralBase"/> itself chooses between. Every legacy panel's
/// light text (<c>#DDDDDD</c>/<c>#FFFFFF</c>) stays byte-identical <see cref="PanelThemeMode.Dark"/>, and
/// every one of the twelve <see cref="PanelColorPreset.LightModeTextHex"/> values — each a near-black
/// colour picked to read on a light surface — stays byte-identical <see cref="PanelThemeMode.Light"/>.
/// </summary>
public static class PanelThemeRules
{
    /// <summary>Default text colour for <see cref="PanelThemeMode.Dark"/> — byte-identical to this app's long-standing default (<c>Color.Text.Primary</c>/<c>PanelSurfaceBrushConverter.DefaultTextColor</c>'s own hex form, just fully opaque white here rather than <c>#DDDDDD</c>) — used for a CUSTOM (non-preset) accent's Dark-mode text; each preset's own <see cref="PanelColorPreset.DarkModeTextHex"/> is its own tint since backlog 1.x item 4 (§4.2a), not necessarily this value.</summary>
    public const string DefaultDarkModeText = "#FFFFFF";

    /// <summary>Default text colour for <see cref="PanelThemeMode.Light"/>.</summary>
    public const string DefaultLightModeText = "#1B1B1B";

    /// <summary>
    /// Parses <paramref name="textHex"/> and derives its <see cref="PanelThemeMode"/> via
    /// <see cref="FromTextColor(System.Windows.Media.Color)"/>. Parses <b>only</b> through
    /// <see cref="HexBrushConverter.TryParseColor"/> — the exact same parser every panel's actual rendered
    /// surface/text brush goes through (<c>PanelSurfaceBrushConverter</c>/<c>HexBrushConverter</c> itself),
    /// so this method's Light/Dark decision can never disagree with what the panel visually looks like.
    /// <see langword="null"/>, empty, whitespace-only, a named colour (e.g. <c>"Black"</c>), a
    /// <c>"ContextColor …"</c> ICC-profile reference, or any other string <see cref="HexBrushConverter.TryParseColor"/>
    /// rejects resolves to <see cref="PanelThemeMode.Dark"/> — never throws — the same fail-safe direction
    /// every other Ambient Accent default already uses: a missing/garbage stored text colour must not
    /// silently flip a panel's whole surface.
    /// </summary>
    /// <param name="textHex">The candidate text colour hex string.</param>
    public static PanelThemeMode FromTextColor(string? textHex)
    {
        if (textHex is null || !HexBrushConverter.TryParseColor(textHex, out var color))
            return PanelThemeMode.Dark;

        return FromTextColor(color);
    }

    /// <summary>
    /// Derives <paramref name="text"/>'s <see cref="PanelThemeMode"/>: <see cref="PanelThemeMode.Light"/>
    /// when <paramref name="text"/>'s WCAG contrast ratio (<see cref="ContrastCalculator.ContrastRatio"/>)
    /// against <see cref="SurfaceComposer.LightNeutralBase"/> is strictly greater than its contrast ratio
    /// against <see cref="SurfaceComposer.DarkNeutralBase"/>; <see cref="PanelThemeMode.Dark"/> otherwise
    /// (an exact tie resolves Dark).
    /// </summary>
    /// <param name="text">The text colour to derive a mode from.</param>
    public static PanelThemeMode FromTextColor(Color text)
    {
        var contrastOnLight = ContrastCalculator.ContrastRatio(text, SurfaceComposer.LightNeutralBase);
        var contrastOnDark = ContrastCalculator.ContrastRatio(text, SurfaceComposer.DarkNeutralBase);
        return contrastOnLight > contrastOnDark ? PanelThemeMode.Light : PanelThemeMode.Dark;
    }

    /// <summary>Convenience for <see cref="SurfaceComposer.ResolveNeutralBase"/>: whether <paramref name="text"/> resolves to <see cref="PanelThemeMode.Light"/>.</summary>
    /// <param name="text">The text colour to test.</param>
    public static bool IsLightSurface(Color text) => FromTextColor(text) == PanelThemeMode.Light;

    /// <summary>Returns <see cref="DefaultLightModeText"/> or <see cref="DefaultDarkModeText"/> for <paramref name="mode"/>.</summary>
    /// <param name="mode">The mode to return the default text colour for.</param>
    public static string DefaultTextFor(PanelThemeMode mode) =>
        mode == PanelThemeMode.Light ? DefaultLightModeText : DefaultDarkModeText;
}
