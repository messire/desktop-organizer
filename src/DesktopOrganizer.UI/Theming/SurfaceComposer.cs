using System.Windows.Media;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Pure colour-composition logic for a panel's surface fill, backing <c>PanelSurfaceBrushConverter</c>
/// (ADR-025 §3.2, docs/PLANS/phase-5-ambient-accent-ui.md §3.2). This is the machine-checkable form of
/// the DoD "accent is local, not a full-panel fill": <see cref="PanelSurfaceStyle.Solid"/> ignores its
/// accent parameter entirely, and only <see cref="PanelSurfaceStyle.Ambient"/>/<see cref="PanelSurfaceStyle.Glass"/>
/// mix in a small, capped amount of it.
/// </summary>
public static class SurfaceComposer
{
    /// <summary>Floor for panel surface opacity — below this, text/icon contrast is not guaranteed readable.</summary>
    public const double MinSurfaceOpacity = 0.35;

    /// <summary>Ceiling for panel surface opacity — fully opaque.</summary>
    public const double MaxSurfaceOpacity = 1.0;

    /// <summary>Fraction of accent mixed into the neutral base for <see cref="PanelSurfaceStyle.Ambient"/> (ADR-025 "~10% accent").</summary>
    public const double AmbientAccentRatio = 0.08;

    /// <summary>Multiplier applied to the Ambient alpha to get the (lower) Glass alpha.</summary>
    public const double GlassOpacityFactor = 0.60;

    /// <summary>
    /// Clamps <paramref name="value"/> to <c>[MinSurfaceOpacity, MaxSurfaceOpacity]</c>. Non-finite
    /// input (<see cref="double.NaN"/>, <see cref="double.PositiveInfinity"/>,
    /// <see cref="double.NegativeInfinity"/>) is untrusted by construction and floors to
    /// <see cref="MinSurfaceOpacity"/> rather than propagating.
    /// </summary>
    /// <param name="value">The candidate opacity value to clamp.</param>
    public static double ClampOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return MinSurfaceOpacity;

        return Math.Clamp(value, MinSurfaceOpacity, MaxSurfaceOpacity);
    }

    /// <summary>Converts a (clamped) opacity to a rounded byte alpha channel value.</summary>
    /// <param name="opacity">
    /// Clamped to <c>[<see cref="MinSurfaceOpacity"/> (0.35), 1]</c>; non-finite values become 0.35.
    /// </param>
    public static byte AlphaFromOpacity(double opacity)
    {
        var clamped = ClampOpacity(opacity);
        return (byte)Math.Round(clamped * 255, MidpointRounding.AwayFromZero);
    }

    /// <summary>Returns <paramref name="color"/> with its alpha channel replaced by <paramref name="alpha"/>.</summary>
    /// <param name="color">The source colour, whose R/G/B channels are kept unchanged.</param>
    /// <param name="alpha">The new alpha channel value.</param>
    public static Color WithAlpha(Color color, byte alpha)
    {
        var result = color;
        result.A = alpha;
        return result;
    }

    /// <summary>Linearly interpolates every channel (including alpha) of <paramref name="a"/> toward <paramref name="b"/> by <paramref name="ratio"/> (clamped to <c>[0, 1]</c>).</summary>
    /// <param name="a">The starting colour, at <paramref name="ratio"/> == 0.</param>
    /// <param name="b">The target colour, at <paramref name="ratio"/> == 1.</param>
    /// <param name="ratio">The interpolation fraction, clamped to <c>[0, 1]</c>.</param>
    public static Color Mix(Color a, Color b, double ratio)
    {
        var r = Math.Clamp(ratio, 0.0, 1.0);

        byte MixChannel(byte x, byte y) => (byte)Math.Round(x + ((y - x) * r), MidpointRounding.AwayFromZero);

        return Color.FromArgb(
            MixChannel(a.A, b.A),
            MixChannel(a.R, b.R),
            MixChannel(a.G, b.G),
            MixChannel(a.B, b.B));
    }

    /// <summary>
    /// Composes the final panel surface fill colour. <see cref="PanelSurfaceStyle.Solid"/> never
    /// mixes in <paramref name="accent"/> (DoD "accent is not a full-panel fill"). <paramref name="highContrast"/>
    /// forces the result to <c>neutralBase</c> with alpha 255 (D-12) — opaque, no accent mixed in —
    /// regardless of <paramref name="style"/>.
    /// </summary>
    /// <param name="neutralBase">The panel's neutral (non-accent) base colour.</param>
    /// <param name="accent">The panel's accent colour, mixed in per <paramref name="style"/>.</param>
    /// <param name="style">Which of the ADR-025/ADR-030 surface treatments to compose.</param>
    /// <param name="surfaceOpacity">
    /// The panel's configured surface opacity. Clamped to <c>[<see cref="MinSurfaceOpacity"/> (0.35), 1]</c>;
    /// non-finite values become 0.35.
    /// </param>
    /// <param name="highContrast">When <see langword="true"/>, forces an opaque <paramref name="neutralBase"/> result with no accent mixed in, regardless of <paramref name="style"/>.</param>
    public static Color ComposeSurface(
        Color neutralBase, Color accent, PanelSurfaceStyle style, double surfaceOpacity, bool highContrast = false)
    {
        if (highContrast)
            return WithAlpha(neutralBase, 255);

        var clampedOpacity = ClampOpacity(surfaceOpacity);

        switch (style)
        {
            case PanelSurfaceStyle.Solid:
                return WithAlpha(neutralBase, AlphaFromOpacity(clampedOpacity));

            case PanelSurfaceStyle.Glass:
            {
                var ambientAlpha = AlphaFromOpacity(clampedOpacity);
                var floor = AlphaFromOpacity(MinSurfaceOpacity);
                var scaled = (byte)Math.Round(ambientAlpha * GlassOpacityFactor, MidpointRounding.AwayFromZero);
                var glassAlpha = Math.Max(floor, scaled);
                var mixed = Mix(neutralBase, accent, AmbientAccentRatio);
                return WithAlpha(mixed, glassAlpha);
            }

            case PanelSurfaceStyle.Ambient:
            default:
            {
                var mixed = Mix(neutralBase, accent, AmbientAccentRatio);
                return WithAlpha(mixed, AlphaFromOpacity(clampedOpacity));
            }
        }
    }

    /// <summary>Accent at a low, explicit alpha (0..1, clamped; non-finite input treated as 0) — used for selection/hover tints, not the surface fill.</summary>
    /// <param name="accent">The panel's accent colour.</param>
    /// <param name="alpha">The desired alpha, in <c>[0, 1]</c> (clamped; non-finite treated as 0).</param>
    public static Color AccentTint(Color accent, double alpha)
    {
        var a = double.IsNaN(alpha) ? 0.0 : Math.Clamp(alpha, 0.0, 1.0);
        var byteAlpha = (byte)Math.Round(a * 255, MidpointRounding.AwayFromZero);
        return WithAlpha(accent, byteAlpha);
    }
}
