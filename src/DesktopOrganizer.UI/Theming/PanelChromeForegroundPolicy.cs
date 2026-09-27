using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Which fixed, non-accent panel-chrome token a caller wants resolved by <see cref="PanelChromeForegroundPolicy.Resolve"/>
/// (docs/PLANS/color-presets-and-size-step.md WP-B "ADDITIONAL"). <see cref="Primary"/>/<see cref="Secondary"/>/
/// <see cref="Muted"/> mirror <c>Color.Text.Primary/Secondary/Muted</c>'s three weights; the four
/// <c>ScrollBarThumb*</c> members mirror <c>Color.ScrollBar.Thumb/ThumbBorder/ThumbHover/ThumbPressed</c>'s
/// four states.
/// </summary>
public enum PanelChromeForegroundKind
{
    /// <summary>Header text/icon glyphs that read as the panel's primary chrome foreground.</summary>
    Primary,

    /// <summary>Header icon buttons (Sort/View/RollUp/Overflow) and other secondary chrome glyphs.</summary>
    Secondary,

    /// <summary>The dimmest chrome text weight (e.g. the rolled-up item-count caption).</summary>
    Muted,

    /// <summary>The overlay scrollbar thumb's resting fill.</summary>
    ScrollBarThumb,

    /// <summary>The overlay scrollbar thumb's hairline border.</summary>
    ScrollBarThumbBorder,

    /// <summary>The overlay scrollbar thumb's hover fill.</summary>
    ScrollBarThumbHover,

    /// <summary>The overlay scrollbar thumb's dragging/pressed fill.</summary>
    ScrollBarThumbPressed,

    /// <summary>
    /// <c>PanelChrome</c>'s own <c>PART_NeutralStroke</c>/<c>PART_InteractionOverlay</c> hairline (MINOR-4,
    /// docs/PLANS/color-presets-and-size-step.md review round) — previously a fixed <c>Brush.Border.Default</c>,
    /// near-invisible on a light-accent panel's now-light surface (Q-1 variant B).
    /// </summary>
    Border,
}

/// <summary>
/// Pure colour policy behind <see cref="Converters.PanelChromeForegroundBrushConverter"/> — the
/// mode-adaptive counterpart of <see cref="HoverOverlayPolicy"/> for panel chrome that is NOT the
/// user-configurable <c>Model.TextColor</c> (the panel title and item labels already read that directly —
/// <c>ChildWindow.xaml</c>/<c>Templates.xaml</c>). Before this feature every panel surface was dark, so
/// these fixed chrome tokens (header icon-button glyphs, the overlay scrollbar thumb, and
/// <c>PanelItemContainerStyle</c>'s own <c>Foreground</c> fallback) could simply be hardcoded light-on-dark.
/// A <see cref="PanelThemeMode.Light"/> panel now gets a LIGHT neutral base
/// (<see cref="SurfaceComposer.LightNeutralBase"/>), against which those same light-on-dark tokens read at
/// roughly 1.4:1 — this policy picks the SAME rule <see cref="SurfaceComposer.ResolveNeutralBase"/> already
/// uses (ADR-042: the panel's text colour's own <see cref="PanelThemeMode"/>, via
/// <see cref="PanelThemeRules.IsLightSurface"/>) to swap in a dark-on-light token set instead.
/// </summary>
public static class PanelChromeForegroundPolicy
{
    // ── Dark-surface tokens — byte-identical to Colors.xaml's existing Color.Text.Primary/Secondary/Muted
    // and Color.ScrollBar.Thumb/ThumbBorder/ThumbHover/ThumbPressed. Every accent this app shipped before
    // Q-1 variant B (AccentPalette.Neutral + its six Presets) resolves here, unchanged. ──────────────────

    /// <summary>Dark-surface "Primary" — byte-identical to <c>Color.Text.Primary</c> (<c>#DDDDDD</c>).</summary>
    public static readonly Color DarkPrimary = Color.FromRgb(0xDD, 0xDD, 0xDD);

    /// <summary>Dark-surface "Secondary" — byte-identical to <c>Color.Text.Secondary</c> (<c>#AAAAAA</c>).</summary>
    public static readonly Color DarkSecondary = Color.FromRgb(0xAA, 0xAA, 0xAA);

    /// <summary>Dark-surface "Muted" — byte-identical to <c>Color.Text.Muted</c> (<c>#8A8A8A</c>).</summary>
    public static readonly Color DarkMuted = Color.FromRgb(0x8A, 0x8A, 0x8A);

    /// <summary>Dark-surface scrollbar thumb fill — byte-identical to <c>Color.ScrollBar.Thumb</c>.</summary>
    public static readonly Color DarkScrollBarThumb = Color.FromArgb(0x66, 0x78, 0x80, 0x88);

    /// <summary>Dark-surface scrollbar thumb border — byte-identical to <c>Color.ScrollBar.ThumbBorder</c>.</summary>
    public static readonly Color DarkScrollBarThumbBorder = Color.FromArgb(0x40, 0x7F, 0x8A, 0x90);

    /// <summary>Dark-surface scrollbar thumb hover fill — byte-identical to <c>Color.ScrollBar.ThumbHover</c>.</summary>
    public static readonly Color DarkScrollBarThumbHover = Color.FromArgb(0x80, 0x91, 0x9B, 0xA0);

    /// <summary>Dark-surface scrollbar thumb pressed fill — byte-identical to <c>Color.ScrollBar.ThumbPressed</c>.</summary>
    public static readonly Color DarkScrollBarThumbPressed = Color.FromArgb(0xA0, 0xA8, 0xB0, 0xB4);

    // ── Light-surface tokens (Q-1 variant B) — only reached when the panel's accent resolves to
    // SurfaceComposer.LightNeutralBase. ─────────────────────────────────────────────────────────────────

    /// <summary>Light-surface "Primary" — byte-identical to <c>Color.Settings.TextPrimary</c> (<c>#1B1B1B</c>).</summary>
    public static readonly Color LightPrimary = Color.FromRgb(0x1B, 0x1B, 0x1B);

    /// <summary>
    /// Light-surface "Secondary"/"Muted" — byte-identical to <c>Color.Settings.TextMuted</c> (<c>#666666</c>).
    /// The Settings window's own light palette has no separate "secondary" vs. "muted" weight (only
    /// <c>TextPrimary</c>/<c>TextMuted</c>), so both dark-surface weights collapse onto this single light
    /// token rather than inventing an unreviewed third one.
    /// </summary>
    public static readonly Color LightSecondary = Color.FromRgb(0x66, 0x66, 0x66);

    /// <summary>
    /// Light-surface scrollbar thumb fill — a darkened variant of the SAME blue-grey hue family as
    /// <see cref="DarkScrollBarThumb"/> (<c>#788088</c> scaled to ~45% intensity, <c>#363A3D</c>), at a
    /// higher alpha than the dark set so it clears <see cref="ContrastCalculator.MinimumIconContrast"/>
    /// against <see cref="SurfaceComposer.LightNeutralBase"/> (pinned by
    /// <c>PanelChromeForegroundBrushConverterTests</c>).
    /// </summary>
    public static readonly Color LightScrollBarThumb = Color.FromArgb(0xB3, 0x36, 0x3A, 0x3D);

    /// <summary>Light-surface scrollbar thumb border — same hue as <see cref="LightScrollBarThumb"/>, lower alpha (subtler outline).</summary>
    public static readonly Color LightScrollBarThumbBorder = Color.FromArgb(0x73, 0x36, 0x3A, 0x3D);

    /// <summary>Light-surface scrollbar thumb hover fill — same hue, higher alpha than the resting thumb.</summary>
    public static readonly Color LightScrollBarThumbHover = Color.FromArgb(0xCC, 0x36, 0x3A, 0x3D);

    /// <summary>Light-surface scrollbar thumb pressed fill — same hue, the highest alpha of the four states.</summary>
    public static readonly Color LightScrollBarThumbPressed = Color.FromArgb(0xED, 0x36, 0x3A, 0x3D);

    /// <summary>Dark-surface chrome hairline (<c>PART_NeutralStroke</c>/<c>PART_InteractionOverlay</c>) — byte-identical to <c>Color.Border.Default</c> (<c>#33FFFFFF</c>).</summary>
    public static readonly Color DarkBorder = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Light-surface chrome hairline (MINOR-4) — a translucent BLACK at the same alpha as
    /// <see cref="DarkBorder"/>'s own translucent white, so it reads at a comparable strength against
    /// <see cref="SurfaceComposer.LightNeutralBase"/> that <see cref="DarkBorder"/> reads at against
    /// <see cref="SurfaceComposer.DarkNeutralBase"/>.
    /// </summary>
    public static readonly Color LightBorder = Color.FromArgb(0x1F, 0x00, 0x00, 0x00);

    /// <summary>
    /// Resolves the concrete <see cref="Color"/> for <paramref name="kind"/>, given the panel's own
    /// <paramref name="text"/> colour — dark-surface tokens (byte-identical to today's shipped values)
    /// when <paramref name="text"/> resolves to <see cref="PanelThemeMode.Dark"/>, light-surface tokens
    /// when it resolves to <see cref="PanelThemeMode.Light"/> — the exact same rule
    /// <see cref="SurfaceComposer.ResolveNeutralBase"/> itself applies (ADR-042: derived from
    /// <paramref name="text"/> alone via <see cref="PanelThemeRules.IsLightSurface"/>).
    /// <paramref name="accent"/> is accepted only for signature/call-site parity with
    /// <see cref="SurfaceComposer.ResolveNeutralBase"/> — it does not affect the result.
    /// </summary>
    /// <param name="accent">The panel's accent colour (<c>PanelTheme.Accent</c>'s underlying colour) — not read by this method (see remarks).</param>
    /// <param name="text">The panel's text colour (<c>PanelTheme.Text</c>'s underlying colour) — the sole input to the light/dark decision.</param>
    /// <param name="kind">Which chrome token to resolve.</param>
    public static Color Resolve(Color accent, Color text, PanelChromeForegroundKind kind)
    {
        var isLight = SurfaceComposer.ResolveNeutralBase(accent, text) == SurfaceComposer.LightNeutralBase;

        return kind switch
        {
            PanelChromeForegroundKind.Primary => isLight ? LightPrimary : DarkPrimary,
            PanelChromeForegroundKind.Secondary => isLight ? LightSecondary : DarkSecondary,
            PanelChromeForegroundKind.Muted => isLight ? LightSecondary : DarkMuted,
            PanelChromeForegroundKind.ScrollBarThumb => isLight ? LightScrollBarThumb : DarkScrollBarThumb,
            PanelChromeForegroundKind.ScrollBarThumbBorder => isLight ? LightScrollBarThumbBorder : DarkScrollBarThumbBorder,
            PanelChromeForegroundKind.ScrollBarThumbHover => isLight ? LightScrollBarThumbHover : DarkScrollBarThumbHover,
            PanelChromeForegroundKind.ScrollBarThumbPressed => isLight ? LightScrollBarThumbPressed : DarkScrollBarThumbPressed,
            PanelChromeForegroundKind.Border => isLight ? LightBorder : DarkBorder,
            _ => isLight ? LightPrimary : DarkPrimary,
        };
    }
}
