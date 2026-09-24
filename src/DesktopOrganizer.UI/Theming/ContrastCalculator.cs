using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Pure WCAG 2.x contrast-ratio math (docs/PLANS/workspace-grid-07-visual.md "Contrast") — the
/// machine-checkable form of the plan's contrast table, which checks the Ambient Accent surface tokens'
/// text/icon contrast when composited over a light (<c>#F0F0F0</c>) and a dark (<c>#101010</c>)
/// representative wallpaper for <see cref="PanelSurfaceStyle.Solid"/>/<see cref="PanelSurfaceStyle.Ambient"/>/
/// <see cref="PanelSurfaceStyle.Glass"/>. This type has no XAML/converter consumer of its own — it is a
/// small, self-contained calculator whose output feeds that plan document's table (and is itself
/// machine-checked, in the test project, against WCAG's own published reference values) — the same
/// "pure logic + doc-table" split already used by <see cref="SurfaceComposer"/> for the colour-
/// composition side of the same pipeline.
/// </summary>
/// <remarks>
/// Formulas: WCAG 2.x relative luminance (<see href="https://www.w3.org/TR/WCAG21/#dfn-relative-luminance"/>)
/// and contrast ratio (<see href="https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio"/>); alpha compositing
/// is the standard "source-over" Porter-Duff formula for a fully OPAQUE destination — every wallpaper
/// this class is used against is opaque by definition, so <see cref="CompositeOverOpaque"/> only needs to
/// honour the FOREGROUND's own alpha channel, not the background's.
/// </remarks>
public static class ContrastCalculator
{
    /// <summary>WCAG AA minimum contrast ratio for normal-size body text/labels (docs/PLANS/workspace-grid-07-visual.md's "≥4.5:1 for labels").</summary>
    public const double MinimumTextContrast = 4.5;

    /// <summary>WCAG AA minimum contrast ratio for icons/UI component graphics (docs/PLANS/workspace-grid-07-visual.md's "≥3:1 for icon buttons").</summary>
    public const double MinimumIconContrast = 3.0;

    /// <summary>
    /// Composites <paramref name="foreground"/> (honouring its own <see cref="Color.A"/>) over an
    /// OPAQUE <paramref name="background"/> (its alpha channel is ignored — every wallpaper this is used
    /// against is fully opaque by construction), returning the fully-opaque colour a viewer actually
    /// sees. This is the "translucent panel surface over a desktop wallpaper" step of the contrast
    /// pipeline; <see cref="ContrastRatio"/> is then computed between an opaque foreground token (text/
    /// icon colour, never translucent in this app's token set) and THIS method's result.
    /// </summary>
    public static Color CompositeOverOpaque(Color foreground, Color background)
    {
        var alpha = foreground.A / 255.0;

        byte Channel(byte fg, byte bg) =>
            (byte)Math.Round((fg * alpha) + (bg * (1 - alpha)), MidpointRounding.AwayFromZero);

        return Color.FromRgb(
            Channel(foreground.R, background.R),
            Channel(foreground.G, background.G),
            Channel(foreground.B, background.B));
    }

    /// <summary>
    /// WCAG relative luminance of an OPAQUE colour, in <c>[0, 1]</c> (0 = black, 1 = white).
    /// <paramref name="color"/>'s own alpha channel is ignored — this function assumes an
    /// already-composited, opaque colour (call <see cref="CompositeOverOpaque"/> first for a translucent
    /// surface).
    /// </summary>
    public static double RelativeLuminance(Color color)
    {
        double Linearize(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));
    }

    /// <summary>
    /// WCAG contrast ratio between two OPAQUE colours, in <c>[1.0, 21.0]</c> — always ≥ 1.0 (identical
    /// colours), order-independent (<c>ContrastRatio(a, b) == ContrastRatio(b, a)</c>), maximal (21.0)
    /// between pure black and pure white.
    /// </summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var luminanceA = RelativeLuminance(a);
        var luminanceB = RelativeLuminance(b);
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Convenience for this class's exact use case: the contrast ratio between an opaque
    /// <paramref name="foreground"/> (a text/icon token) and a (possibly translucent)
    /// <paramref name="surface"/> composited over <paramref name="wallpaper"/> — i.e. "how readable is
    /// this label/icon, given the panel surface it sits on may let the desktop wallpaper show through".
    /// </summary>
    public static double ContrastAgainstComposited(Color foreground, Color surface, Color wallpaper)
        => ContrastRatio(foreground, CompositeOverOpaque(surface, wallpaper));
}
