using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Chooses which DIRECTION a "neutral hover wash" should nudge a header icon button's underlying panel
/// surface colour — MAJOR-3, docs/PLANS/workspace-grid-07-visual.md fix-cycle 1. Before this fix, every
/// header icon button (Sort/View/RollUp/Overflow) always painted the SAME translucent WHITE wash on
/// hover (<c>Color.Overlay.Hover</c>, <c>#1AFFFFFF</c>), regardless of what it sat on top of. That is
/// correct only when the surface underneath is already light enough that lightening it further increases
/// its distance from the mid-tone icon colour (<c>Color.Text.Secondary</c>). At the time of the original
/// fix, every <see cref="Models.PanelSurfaceStyle"/>/accent combination this app shipped was DARK (a
/// mostly-opaque <c>#1E1E1E</c> neutral base with at most an 8% accent tint — see
/// <see cref="SurfaceComposer.ComposeSurface"/>), so lightening it on hover instead pushed the surface
/// TOWARD the icon's own mid-tone colour, silently REDUCING contrast below
/// <see cref="ContrastCalculator.MinimumIconContrast"/> on a light wallpaper at the app's own default
/// <c>SurfaceOpacity</c> (found by <c>AmbientAccentContrastAuditTests</c>'s audit: idle 3.59:1 PASS -&gt;
/// hover 2.72:1 FAIL). docs/PLANS/color-presets-and-size-step.md Q-1 variant B later gave LIGHT accents
/// their own LIGHT neutral base (<see cref="SurfaceComposer.ResolveNeutralBase"/>/<see cref="SurfaceComposer.LightNeutralBase"/>)
/// — this class's own luminance-driven direction choice already generalizes to that case unchanged
/// (a light surface lightening further on hover still increases, not decreases, its distance from the
/// same fixed mid-tone icon colour), which is exactly the "a future light Solid surface... could" case this
/// class's own <see cref="LightenWash"/> doc anticipated; no logic change was needed here for Q-1 variant B.
/// </summary>
/// <remarks>
/// Pure function, no XAML/Brush lookup of its own — <c>Converters/HoverOverlayBrushConverter.cs</c> is
/// the only consumer, feeding it <see cref="ContrastCalculator.RelativeLuminance"/> of the panel's OWN
/// composed surface colour (<see cref="SurfaceComposer.ComposeSurface"/>'s output, alpha ignored —
/// <see cref="ContrastCalculator.RelativeLuminance"/> only reads R/G/B). That colour's RGB channels are
/// entirely independent of <c>SurfaceOpacity</c> (<see cref="SurfaceComposer.ComposeSurface"/> only ever
/// varies the ALPHA channel by opacity, never the mixed RGB), which is what makes this a genuinely
/// available, always-computable-at-bind-time input — there is no way for this app to know the REAL
/// desktop wallpaper colour actually showing through a translucent panel at runtime, so this policy
/// deliberately reasons only about the panel's own resting colour, not a hypothetical composited-with-
/// wallpaper result (that composition is only ever done for the STATIC contrast AUDIT/tests, against
/// representative wallpaper constants, never live).
/// </remarks>
public static class HoverOverlayPolicy
{
    /// <summary>
    /// WCAG relative luminance midpoint ([0, 1]) below which a surface is treated as "dark" for
    /// hover-wash direction purposes.
    /// </summary>
    public const double DarkSurfaceLuminanceThreshold = 0.5;

    /// <summary>
    /// The original, pre-fix-cycle-1 wash — a translucent WHITE, byte-identical to <c>Color.Overlay.Hover</c>'s
    /// own XAML value. <see cref="For"/> no longer returns this (MAJOR-3 fix, review round,
    /// docs/PLANS/color-presets-and-size-step.md): lightening an already near-white surface
    /// (<see cref="SurfaceComposer.LightNeutralBase"/>, realized by Q-1 variant B's light accent presets,
    /// no longer a purely hypothetical "a future... could") is visually imperceptible — see
    /// <see cref="LightSurfaceDarkenWash"/>. Kept only as that XAML token's own mirror
    /// (<c>HoverOverlayPolicyTests.LightenWash_MatchesThePreFixCycleOneColorOverlayHoverToken</c>) and as
    /// the High Contrast passthrough fallback value XAML supplies to <c>HoverOverlayBrushConverter</c>.
    /// </summary>
    public static readonly Color LightenWash = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// A translucent BLACK wash, for surfaces below <see cref="DarkSurfaceLuminanceThreshold"/> — pushes an
    /// already-dark surface further AWAY from a mid-tone icon/text colour on hover instead of toward it,
    /// restoring (in fact improving on) idle contrast rather than eroding it.
    /// </summary>
    public static readonly Color DarkenWash = Color.FromArgb(0x1A, 0x00, 0x00, 0x00);

    /// <summary>
    /// A weaker translucent BLACK wash (MAJOR-3 fix, review round, docs/PLANS/color-presets-and-size-step.md)
    /// for surfaces AT OR ABOVE <see cref="DarkSurfaceLuminanceThreshold"/> — <see cref="LightenWash"/>
    /// (lightening an already near-white surface further) is visually imperceptible over
    /// <see cref="SurfaceComposer.LightNeutralBase"/>, so a light surface instead gets a subtle, visible
    /// DARKEN wash at a LOWER alpha than <see cref="DarkenWash"/>'s own — the light-surface chrome tokens
    /// (<see cref="PanelChromeForegroundPolicy.LightPrimary"/>/<see cref="PanelChromeForegroundPolicy.LightSecondary"/>,
    /// <c>#1B1B1B</c>/<c>#666666</c>) already sit far darker than the near-white base, so even this lighter
    /// darkening keeps that chrome's own contrast comfortably above
    /// <see cref="ContrastCalculator.MinimumIconContrast"/> — pinned by <c>AmbientAccentContrastAuditTests</c>.
    /// </summary>
    public static readonly Color LightSurfaceDarkenWash = Color.FromArgb(0x14, 0x00, 0x00, 0x00);

    /// <summary>
    /// Returns the hover-wash colour for a panel surface at <paramref name="ownSurfaceLuminance"/>
    /// (<see cref="ContrastCalculator.RelativeLuminance"/>'s own <c>[0, 1]</c> range) — a DARK surface
    /// (below <see cref="DarkSurfaceLuminanceThreshold"/>) gets <see cref="DarkenWash"/>, pushing it further
    /// AWAY from a mid-tone icon/text colour on hover (increasing, not decreasing, contrast against the
    /// panel's OWN resting tint — see this class's own remarks for why that own-tint luminance, not a
    /// hypothetical wallpaper-composited one, is what is actually available and sufficient here). A LIGHT
    /// surface (at/above the threshold) gets <see cref="LightSurfaceDarkenWash"/> instead of
    /// <see cref="LightenWash"/> (MAJOR-3 fix, review round) — lightening an already near-white surface is
    /// imperceptible, so a lighter, still-visible darken wash is used there too, verified to keep the
    /// light-surface chrome tokens' own contrast above <see cref="ContrastCalculator.MinimumIconContrast"/>.
    /// Non-finite input (<see cref="double.NaN"/>/infinity — should never occur for a real
    /// <see cref="ContrastCalculator.RelativeLuminance"/> result, but this stays defensive like every other
    /// Ambient Accent pure-math helper) is treated as "dark" — the overwhelmingly common case for every
    /// token this app ships today.
    /// </summary>
    /// <param name="ownSurfaceLuminance">The panel surface's own resting relative luminance, in <c>[0, 1]</c>.</param>
    public static Color For(double ownSurfaceLuminance)
    {
        if (double.IsNaN(ownSurfaceLuminance))
            return DarkenWash;

        return ownSurfaceLuminance < DarkSurfaceLuminanceThreshold ? DarkenWash : LightSurfaceDarkenWash;
    }

    /// <summary>
    /// Returns the hover-wash colour for <c>PanelItemContainerStyle</c>'s icon-tile hover specifically
    /// (NEW-1 fix, docs/PLANS/release-polish-1.0.md) — unlike <see cref="For"/>, a DARK surface (below
    /// <see cref="DarkSurfaceLuminanceThreshold"/>, or non-finite input, treated the same defensive way
    /// <see cref="For"/> treats it) gets <see cref="LightenWash"/>, not <see cref="DarkenWash"/>. Every
    /// dark panel this app ships (the legacy neutral accent, every dark preset, and the non-themed
    /// <c>SystemIconsPanelWindow</c>) shares this Style, and before this fix its item-tile hover was routed
    /// onto the SAME direction-aware wash the header icon buttons use — which for a dark surface is
    /// <see cref="DarkenWash"/>, a translucent BLACK at 10% alpha over an already near-black
    /// <c>#1E1E1E</c>-based surface, nearly invisible. <see cref="LightenWash"/> is byte-identical to what
    /// the item-tile hover used before that MAJOR-3 fix-cycle-1 routing change, restoring the original,
    /// clearly-visible dark-surface hover while a LIGHT surface (at/above the threshold — only reachable via
    /// a light accent preset paired with its own dark text, see <see cref="SurfaceComposer.ResolveNeutralBase"/>)
    /// still gets <see cref="LightSurfaceDarkenWash"/>, exactly like the header buttons, since lightening an
    /// already near-white surface stays imperceptible there too.
    /// </summary>
    /// <param name="ownSurfaceLuminance">The panel surface's own resting relative luminance, in <c>[0, 1]</c>.</param>
    public static Color ForItemTile(double ownSurfaceLuminance)
    {
        if (double.IsNaN(ownSurfaceLuminance))
            return LightenWash;

        return ownSurfaceLuminance < DarkSurfaceLuminanceThreshold ? LightenWash : LightSurfaceDarkenWash;
    }
}
