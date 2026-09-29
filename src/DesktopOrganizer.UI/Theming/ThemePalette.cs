using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// The two fixed-value "Palette T" (application chrome) colour sets — <see cref="Light"/> and
/// <see cref="Dark"/> — as plain C# data (docs/PLANS/design-system-1x.md §2.1/§2.2, ADR-047 D-1/D-2/D-3).
/// This is the single source of truth: <see cref="ThemeResourceBuilder"/> turns it into WPF resources for
/// XAML consumers, <c>TrayMenuPaletteFactory</c> (phase 6.4, not yet implemented) turns it into
/// <c>TrayMenuPalette</c> ARGB primitives for the WinForms tray renderer, and
/// <c>ThemePaletteContrastTests</c> (T-1) checks it directly with <see cref="ContrastCalculator"/> — no
/// STA/XAML parse needed for that check. The precedent is <see cref="AccentPalette"/> (Palette P's own
/// C#-first colour source). High contrast has no <see cref="ThemePalette"/> instance of its own: it is
/// built straight from <see cref="System.Windows.SystemColors"/> by <see cref="ThemeResourceBuilder"/>.
/// </summary>
/// <remarks>
/// Values are the Win11-styled hex table from the plan's §2.2 — every property name matches one
/// <c>Theme.*</c> <see cref="ThemeKeys"/> colour constant one-to-one (<c>SurfaceWindow</c> ↔
/// <c>Theme.Surface.Window</c>, etc.), which is exactly what <see cref="ThemeResourceBuilder"/> and the
/// coverage test (T-2) rely on.
/// </remarks>
public sealed record ThemePalette(
    Color SurfaceWindow,
    Color SurfaceCard,
    Color SurfaceCardHover,
    Color SurfaceFlyout,
    Color TextPrimary,
    Color TextSecondary,
    Color TextDisabled,
    Color TextOnAccent,
    Color TextCaution,
    Color TextCritical,
    Color TextSuccess,
    Color AccentFill,
    Color AccentHover,
    Color AccentPressed,
    Color AccentDisabled,
    Color ControlFill,
    Color ControlHover,
    Color ControlPressed,
    Color ControlDisabled,
    Color ControlSubtleHover,
    Color ControlSubtlePressed,
    Color StrokeCard,
    Color StrokeDivider,
    Color StrokeControl,
    Color StrokeInput,
    Color StrokeFocus,
    Color StrokeFocusInner,
    Color SelectionIndicator,
    Color SelectionFill,
    Color SelectionText,
    Color InfoBarInfoBackground,
    Color InfoBarSuccessBackground,
    Color InfoBarCautionBackground,
    Color InfoBarCriticalBackground)
{
    /// <summary>The light "Palette T" colour set.</summary>
    public static ThemePalette Light { get; } = new(
        SurfaceWindow: Argb(0xFF, 0xF3, 0xF3, 0xF3),
        SurfaceCard: Argb(0xFF, 0xFB, 0xFB, 0xFB),
        SurfaceCardHover: Argb(0xFF, 0xF6, 0xF6, 0xF6),
        SurfaceFlyout: Argb(0xFF, 0xF9, 0xF9, 0xF9),
        TextPrimary: Argb(0xFF, 0x1B, 0x1B, 0x1B),
        TextSecondary: Argb(0xFF, 0x5F, 0x5F, 0x5F),
        TextDisabled: Argb(0xFF, 0x8A, 0x8A, 0x8A),
        TextOnAccent: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        TextCaution: Argb(0xFF, 0x9D, 0x5D, 0x00),
        TextCritical: Argb(0xFF, 0xC4, 0x2B, 0x1C),
        TextSuccess: Argb(0xFF, 0x0F, 0x7B, 0x0F),
        AccentFill: Argb(0xFF, 0x00, 0x5F, 0xB8),
        AccentHover: Argb(0xFF, 0x19, 0x6E, 0xBF),

        // T-1 fix: the plan's own §2.2 literal (#3183CA) scores TextOnAccent (white) contrast 4.02:1 —
        // below the 4.5:1 floor the same row requires "on Fill/Hover/Pressed". Darkened slightly per §2.2's
        // own allowance ("исполнитель может сдвинуть оттенок, не опуская контраст ниже порога"); still
        // visually the brightest of the three accent states, just not quite as bright.
        AccentPressed: Argb(0xFF, 0x2C, 0x75, 0xB5),
        AccentDisabled: Argb(0xFF, 0xC5, 0xC5, 0xC5),
        ControlFill: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        ControlHover: Argb(0xFF, 0xF9, 0xF9, 0xF9),
        ControlPressed: Argb(0xFF, 0xF5, 0xF5, 0xF5),
        ControlDisabled: Argb(0xFF, 0xF5, 0xF5, 0xF5),
        ControlSubtleHover: Argb(0x0A, 0x00, 0x00, 0x00),
        ControlSubtlePressed: Argb(0x06, 0x00, 0x00, 0x00),
        StrokeCard: Argb(0xFF, 0xE5, 0xE5, 0xE5),
        StrokeDivider: Argb(0xFF, 0xE5, 0xE5, 0xE5),
        StrokeControl: Argb(0xFF, 0xE5, 0xE5, 0xE5),
        StrokeInput: Argb(0xFF, 0x8A, 0x8A, 0x8A),
        StrokeFocus: Argb(0xFF, 0x1B, 0x1B, 0x1B),
        StrokeFocusInner: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        SelectionIndicator: Argb(0xFF, 0x00, 0x5F, 0xB8), // = AccentFill
        SelectionFill: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        SelectionText: Argb(0xFF, 0x1B, 0x1B, 0x1B), // = TextPrimary
        InfoBarInfoBackground: Argb(0xFF, 0xF4, 0xF4, 0xF4),
        InfoBarSuccessBackground: Argb(0xFF, 0xDF, 0xF6, 0xDD),
        InfoBarCautionBackground: Argb(0xFF, 0xFF, 0xF4, 0xCE),
        InfoBarCriticalBackground: Argb(0xFF, 0xFD, 0xE7, 0xE9));

    /// <summary>The dark "Palette T" colour set.</summary>
    public static ThemePalette Dark { get; } = new(
        SurfaceWindow: Argb(0xFF, 0x20, 0x20, 0x20),
        SurfaceCard: Argb(0xFF, 0x2B, 0x2B, 0x2B),
        SurfaceCardHover: Argb(0xFF, 0x32, 0x32, 0x32),
        SurfaceFlyout: Argb(0xFF, 0x2C, 0x2C, 0x2C),
        TextPrimary: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        TextSecondary: Argb(0xFF, 0xC5, 0xC5, 0xC5),
        TextDisabled: Argb(0xFF, 0x7A, 0x7A, 0x7A),
        TextOnAccent: Argb(0xFF, 0x00, 0x00, 0x00),
        TextCaution: Argb(0xFF, 0xFC, 0xE1, 0x00),
        TextCritical: Argb(0xFF, 0xFF, 0x99, 0xA4),
        TextSuccess: Argb(0xFF, 0x6C, 0xCB, 0x5F),
        AccentFill: Argb(0xFF, 0x60, 0xCD, 0xFF),
        AccentHover: Argb(0xFF, 0x5A, 0xB8, 0xE6),
        AccentPressed: Argb(0xFF, 0x52, 0xA6, 0xCE),
        AccentDisabled: Argb(0xFF, 0x43, 0x43, 0x43),
        ControlFill: Argb(0xFF, 0x2D, 0x2D, 0x2D),
        ControlHover: Argb(0xFF, 0x32, 0x32, 0x32),
        ControlPressed: Argb(0xFF, 0x27, 0x27, 0x27),
        ControlDisabled: Argb(0xFF, 0x2A, 0x2A, 0x2A),
        ControlSubtleHover: Argb(0x0F, 0xFF, 0xFF, 0xFF),
        ControlSubtlePressed: Argb(0x0A, 0xFF, 0xFF, 0xFF),
        StrokeCard: Argb(0xFF, 0x3A, 0x3A, 0x3A),
        StrokeDivider: Argb(0xFF, 0x40, 0x40, 0x40),
        StrokeControl: Argb(0xFF, 0x3F, 0x3F, 0x3F),
        StrokeInput: Argb(0xFF, 0x9A, 0x9A, 0x9A),
        StrokeFocus: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        StrokeFocusInner: Argb(0xFF, 0x00, 0x00, 0x00),
        SelectionIndicator: Argb(0xFF, 0x60, 0xCD, 0xFF), // = AccentFill
        SelectionFill: Argb(0xFF, 0x3A, 0x3A, 0x3A),
        SelectionText: Argb(0xFF, 0xFF, 0xFF, 0xFF), // = TextPrimary
        InfoBarInfoBackground: Argb(0xFF, 0x2B, 0x2B, 0x2B),
        InfoBarSuccessBackground: Argb(0xFF, 0x39, 0x3D, 0x1B),
        InfoBarCautionBackground: Argb(0xFF, 0x43, 0x35, 0x19),
        InfoBarCriticalBackground: Argb(0xFF, 0x44, 0x27, 0x26));

    /// <summary>
    /// Looks up a colour by its <see cref="ThemeKeys"/> colour-key name (e.g. <c>"Theme.Surface.Window"</c>) —
    /// used by <see cref="ThemeResourceBuilder"/> and by tests that want to iterate <see cref="ThemeKeys.ColorKeys"/>
    /// without a parallel switch statement of their own.
    /// </summary>
    /// <param name="key">One of <see cref="ThemeKeys.ColorKeys"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is not a known colour key.</exception>
    public Color ByKey(string key) => key switch
    {
        ThemeKeys.SurfaceWindow => SurfaceWindow,
        ThemeKeys.SurfaceCard => SurfaceCard,
        ThemeKeys.SurfaceCardHover => SurfaceCardHover,
        ThemeKeys.SurfaceFlyout => SurfaceFlyout,
        ThemeKeys.TextPrimary => TextPrimary,
        ThemeKeys.TextSecondary => TextSecondary,
        ThemeKeys.TextDisabled => TextDisabled,
        ThemeKeys.TextOnAccent => TextOnAccent,
        ThemeKeys.TextCaution => TextCaution,
        ThemeKeys.TextCritical => TextCritical,
        ThemeKeys.TextSuccess => TextSuccess,
        ThemeKeys.AccentFill => AccentFill,
        ThemeKeys.AccentHover => AccentHover,
        ThemeKeys.AccentPressed => AccentPressed,
        ThemeKeys.AccentDisabled => AccentDisabled,
        ThemeKeys.ControlFill => ControlFill,
        ThemeKeys.ControlHover => ControlHover,
        ThemeKeys.ControlPressed => ControlPressed,
        ThemeKeys.ControlDisabled => ControlDisabled,
        ThemeKeys.ControlSubtleHover => ControlSubtleHover,
        ThemeKeys.ControlSubtlePressed => ControlSubtlePressed,
        ThemeKeys.StrokeCard => StrokeCard,
        ThemeKeys.StrokeDivider => StrokeDivider,
        ThemeKeys.StrokeControl => StrokeControl,
        ThemeKeys.StrokeInput => StrokeInput,
        ThemeKeys.StrokeFocus => StrokeFocus,
        ThemeKeys.StrokeFocusInner => StrokeFocusInner,
        ThemeKeys.SelectionIndicator => SelectionIndicator,
        ThemeKeys.SelectionFill => SelectionFill,
        ThemeKeys.SelectionText => SelectionText,
        ThemeKeys.InfoBarInfoBackground => InfoBarInfoBackground,
        ThemeKeys.InfoBarSuccessBackground => InfoBarSuccessBackground,
        ThemeKeys.InfoBarCautionBackground => InfoBarCautionBackground,
        ThemeKeys.InfoBarCriticalBackground => InfoBarCriticalBackground,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown Theme.* colour key."),
    };

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
