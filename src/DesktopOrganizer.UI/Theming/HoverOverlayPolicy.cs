using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Chooses which DIRECTION a "neutral hover wash" should nudge a header icon button's underlying panel
/// surface colour — MAJOR-3, docs/PLANS/workspace-grid-07-visual.md fix-cycle 1. Before this fix, every
/// header icon button (Sort/View/RollUp/Overflow) always painted the SAME translucent WHITE wash on
/// hover (<c>Color.Overlay.Hover</c>, <c>#1AFFFFFF</c>), regardless of what it sat on top of. That is
/// correct only when the surface underneath is already light enough that lightening it further increases
/// its distance from the mid-tone icon colour (<c>Color.Text.Secondary</c>); every
/// <see cref="Models.PanelSurfaceStyle"/>/accent combination this app actually ships is DARK (a mostly-opaque
/// <c>#1E1E1E</c> neutral base with at most an 8% accent tint — see <see cref="SurfaceComposer.ComposeSurface"/>),
/// so lightening it on hover instead pushed the surface TOWARD the icon's own mid-tone colour, silently
/// REDUCING contrast below <see cref="ContrastCalculator.MinimumIconContrast"/> on a light wallpaper at
/// the app's own default <c>SurfaceOpacity</c> (found by <c>AmbientAccentContrastAuditTests</c>'s audit:
/// idle 3.59:1 PASS -&gt; hover 2.72:1 FAIL).
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
    /// The original, pre-fix-cycle-1 wash — a translucent WHITE, <c>Color.Overlay.Hover</c>'s own value —
    /// kept for surfaces AT OR ABOVE <see cref="DarkSurfaceLuminanceThreshold"/> (none of this app's
    /// current tokens reach that today, but a future light `Solid` surface or bright accent could).
    /// </summary>
    public static readonly Color LightenWash = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// A translucent BLACK wash, same alpha/intensity as <see cref="LightenWash"/>, for surfaces below
    /// <see cref="DarkSurfaceLuminanceThreshold"/> — pushes an already-dark surface further AWAY from a
    /// mid-tone icon/text colour on hover instead of toward it, restoring (in fact improving on) idle
    /// contrast rather than eroding it.
    /// </summary>
    public static readonly Color DarkenWash = Color.FromArgb(0x1A, 0x00, 0x00, 0x00);

    /// <summary>
    /// Returns the hover-wash colour that increases (never decreases) contrast against a mid-tone
    /// icon/text colour, given the panel surface's own <paramref name="ownSurfaceLuminance"/>
    /// (<see cref="ContrastCalculator.RelativeLuminance"/>'s own <c>[0, 1]</c> range). "Increases contrast"
    /// here is scoped exactly to that input: the luminance of the panel's OWN resting tint
    /// (<see cref="SurfaceComposer.ComposeSurface"/>'s RGB mix of accent into the neutral base) — NOT of
    /// that tint composited over whatever the real desktop wallpaper is (this type has no way to know
    /// that at runtime; see this class's own remarks for why the own-tint luminance is what is actually
    /// available and sufficient here). Non-finite input (<see cref="double.NaN"/>/infinity — should never
    /// occur for a real <see cref="ContrastCalculator.RelativeLuminance"/> result, but this stays
    /// defensive like every other Ambient Accent pure-math helper) is treated as "dark" — the
    /// overwhelmingly common case for every token this app ships today.
    /// </summary>
    /// <param name="ownSurfaceLuminance">The panel surface's own resting relative luminance, in <c>[0, 1]</c>.</param>
    public static Color For(double ownSurfaceLuminance)
    {
        if (double.IsNaN(ownSurfaceLuminance))
            return DarkenWash;

        return ownSurfaceLuminance < DarkSurfaceLuminanceThreshold ? DarkenWash : LightenWash;
    }
}
