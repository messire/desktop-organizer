namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// "Palette T" font-size scale, derived from a single base ("M" = <see cref="System.Windows.SystemFonts.MessageFontSize"/>)
/// so the chrome's text grows with Windows' own "Accessibility → Text size" setting instead of being
/// pinned to fixed DIP literals (docs/PLANS/design-system-1x.md §2.3, ADR-047 D-4).
/// </summary>
/// <param name="Caption">M — descriptions, badges, compact segment labels, menu items.</param>
/// <param name="Body">M + 2 — card headers, control text, dialog/InfoBar body.</param>
/// <param name="BodyStrong">M + 2 (SemiBold weight is applied by the consuming style, not carried here) — group headers, InfoBar title.</param>
/// <param name="Subtitle">M + 6 — dialog title.</param>
/// <param name="Title">M + 8 — Settings/Customize page header.</param>
/// <remarks>
/// The plan's §2.3 footnote reserves a fallback (Body = M + 1) for a 420 DIP Customize layout that turns
/// out too tight at M + 2 (T-9, phase 6.2c) — that fallback has no caller yet in 6.1 and is intentionally
/// not implemented here; today's <see cref="FromMessageFontSize"/> always uses M + 2, matching this
/// phase's own T-4 contract (M = 12 → 12/14/14/18/20).
/// </remarks>
public sealed record TypeRamp(double Caption, double Body, double BodyStrong, double Subtitle, double Title)
{
    /// <summary>The offset from the base M added for <see cref="Body"/>/<see cref="BodyStrong"/>.</summary>
    internal const double BodyOffset = 2;

    /// <summary>The offset from the base M added for <see cref="Subtitle"/>.</summary>
    internal const double SubtitleOffset = 6;

    /// <summary>The offset from the base M added for <see cref="Title"/>.</summary>
    internal const double TitleOffset = 8;

    /// <summary>
    /// Computes the ramp from a base "M" size. A non-positive input (a caller passing an unset/invalid
    /// <see cref="System.Windows.SystemFonts.MessageFontSize"/>) falls back to 12 — the ordinary Windows
    /// default at 100% text size — rather than propagating a zero/negative font size into every chrome
    /// style.
    /// </summary>
    /// <param name="messageFontSize">The base "M" size, ordinarily <see cref="System.Windows.SystemFonts.MessageFontSize"/>.</param>
    public static TypeRamp FromMessageFontSize(double messageFontSize)
    {
        var m = messageFontSize > 0 ? messageFontSize : 12.0;
        return new TypeRamp(
            Caption: m,
            Body: m + BodyOffset,
            BodyStrong: m + BodyOffset,
            Subtitle: m + SubtitleOffset,
            Title: m + TitleOffset);
    }
}
