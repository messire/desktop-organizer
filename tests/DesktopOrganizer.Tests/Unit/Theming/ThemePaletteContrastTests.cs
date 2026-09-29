using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// T-1 (docs/PLANS/design-system-1x.md §2.2/§2.12/§3 phase 6.1, ADR-047 D-2/D-6): checks the
/// <see cref="ThemePalette.Light"/>/<see cref="ThemePalette.Dark"/> pairs the plan's §2.2 table lists,
/// using <see cref="ContrastCalculator"/> — pure C#, no STA/XAML needed, since <see cref="ThemePalette"/>
/// is the source of truth (ADR-047 D-1). Text pairs must clear the 4.5:1 WCAG AA floor; UI-component/
/// state-indicator/disabled pairs must clear 3:1 (docs' §2.12 table).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ThemePaletteContrastTests
{
    /// <summary>Both fixed-value palettes, labelled for readable test-failure output.</summary>
    public static TheoryData<string, ThemePalette> Palettes => new()
    {
        { "Light", ThemePalette.Light },
        { "Dark", ThemePalette.Dark },
    };

    /// <summary>Primary text on every surface it can appear on (window, card, card-hover, flyout).</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextPrimary_MeetsTextContrast_OnEverySurface(string label, ThemePalette palette)
    {
        AssertText(label, "TextPrimary/SurfaceWindow", palette.TextPrimary, palette.SurfaceWindow);
        AssertText(label, "TextPrimary/SurfaceCard", palette.TextPrimary, palette.SurfaceCard);
        AssertText(label, "TextPrimary/SurfaceCardHover", palette.TextPrimary, palette.SurfaceCardHover);
        AssertText(label, "TextPrimary/SurfaceFlyout", palette.TextPrimary, palette.SurfaceFlyout);
    }

    /// <summary>Secondary (description) text on window/card — plan §2.2: "≥ 4.5 (L: 6.17 on Card, 5.76 on Window; D: 8.2 on Card)".</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextSecondary_MeetsTextContrast_OnWindowAndCard(string label, ThemePalette palette)
    {
        AssertText(label, "TextSecondary/SurfaceWindow", palette.TextSecondary, palette.SurfaceWindow);
        AssertText(label, "TextSecondary/SurfaceCard", palette.TextSecondary, palette.SurfaceCard);
    }

    /// <summary>Disabled text — plan §2.2: "≥ 3.0 (L 3.34, D 3.30 on Card)", tighter than WCAG's own disabled exemption.</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextDisabled_MeetsIconContrast_OnCard(string label, ThemePalette palette)
        => AssertIcon(label, "TextDisabled/SurfaceCard", palette.TextDisabled, palette.SurfaceCard);

    /// <summary>Text-on-accent — plan §2.2: "≥ 4.5 on Accent.* (L 6.31, D 11.7)".</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextOnAccent_MeetsTextContrast_OnAccentFillHoverPressed(string label, ThemePalette palette)
    {
        AssertText(label, "TextOnAccent/AccentFill", palette.TextOnAccent, palette.AccentFill);
        AssertText(label, "TextOnAccent/AccentHover", palette.TextOnAccent, palette.AccentHover);
        AssertText(label, "TextOnAccent/AccentPressed", palette.TextOnAccent, palette.AccentPressed);
    }

    /// <summary>Caution/Critical/Success severity text on card — plan §2.2's per-token ≥ 4.5 figures.</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void SeverityText_MeetsTextContrast_OnCard(string label, ThemePalette palette)
    {
        AssertText(label, "TextCaution/SurfaceCard", palette.TextCaution, palette.SurfaceCard);
        AssertText(label, "TextCritical/SurfaceCard", palette.TextCritical, palette.SurfaceCard);
        AssertText(label, "TextSuccess/SurfaceCard", palette.TextSuccess, palette.SurfaceCard);
    }

    /// <summary>Accent fill vs card — plan §2.2: "Fill vs Card ≥ 3 (L 6.1, D 7.9)" (a UI-component boundary, not text).</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void AccentFill_MeetsIconContrast_AgainstCard(string label, ThemePalette palette)
        => AssertIcon(label, "AccentFill/SurfaceCard", palette.AccentFill, palette.SurfaceCard);

    /// <summary>Secondary-control text (Text.Primary) on its own fill states.</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextPrimary_MeetsTextContrast_OnControlFillStates(string label, ThemePalette palette)
    {
        AssertText(label, "TextPrimary/ControlFill", palette.TextPrimary, palette.ControlFill);
        AssertText(label, "TextPrimary/ControlHover", palette.TextPrimary, palette.ControlHover);
        AssertText(label, "TextPrimary/ControlPressed", palette.TextPrimary, palette.ControlPressed);
    }

    /// <summary>Stroke.Input (TextBox/ComboBox border, toggle-off track) — plan §2.2: "≥ 3.0 vs Control.Fill и Card (L 3.45, D 4.9)".</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void StrokeInput_MeetsIconContrast_AgainstControlFillAndCard(string label, ThemePalette palette)
    {
        AssertIcon(label, "StrokeInput/ControlFill", palette.StrokeInput, palette.ControlFill);
        AssertIcon(label, "StrokeInput/SurfaceCard", palette.StrokeInput, palette.SurfaceCard);
    }

    /// <summary>Focus ring (outer) — plan §2.2: "≥ 3.0 vs всех Surface".</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void StrokeFocus_MeetsIconContrast_AgainstEverySurface(string label, ThemePalette palette)
    {
        AssertIcon(label, "StrokeFocus/SurfaceWindow", palette.StrokeFocus, palette.SurfaceWindow);
        AssertIcon(label, "StrokeFocus/SurfaceCard", palette.StrokeFocus, palette.SurfaceCard);
        AssertIcon(label, "StrokeFocus/SurfaceFlyout", palette.StrokeFocus, palette.SurfaceFlyout);
    }

    /// <summary>Selection indicator — plan §2.2: "≥ 3.0 vs Card/Window — закрывает 1.14" (the pre-6.1 sidebar gap).</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void SelectionIndicator_MeetsIconContrast_AgainstCardAndWindow(string label, ThemePalette palette)
    {
        AssertIcon(label, "SelectionIndicator/SurfaceCard", palette.SelectionIndicator, palette.SurfaceCard);
        AssertIcon(label, "SelectionIndicator/SurfaceWindow", palette.SelectionIndicator, palette.SurfaceWindow);
    }

    /// <summary>Selection text on selection fill — plan §2.2: "≥ 4.5" (closes the pre-6.1 HC selected-segment gap, ADR-042).</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void SelectionText_MeetsTextContrast_OnSelectionFill(string label, ThemePalette palette)
        => AssertText(label, "SelectionText/SelectionFill", palette.SelectionText, palette.SelectionFill);

    /// <summary>InfoBar backgrounds carry <see cref="ThemePalette.TextPrimary"/> body text — plan §2.2: "Text.Primary и Text.* severity ≥ 4.5".</summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextPrimary_MeetsTextContrast_OnEveryInfoBarBackground(string label, ThemePalette palette)
    {
        AssertText(label, "TextPrimary/InfoBarInfo", palette.TextPrimary, palette.InfoBarInfoBackground);
        AssertText(label, "TextPrimary/InfoBarSuccess", palette.TextPrimary, palette.InfoBarSuccessBackground);
        AssertText(label, "TextPrimary/InfoBarCaution", palette.TextPrimary, palette.InfoBarCautionBackground);
        AssertText(label, "TextPrimary/InfoBarCritical", palette.TextPrimary, palette.InfoBarCriticalBackground);
    }

    /// <summary>
    /// 6.1 review round (ThemePaletteContrastTests gap): the SEVERITY-coloured text an
    /// <c>InfoBar</c>/<c>Text.Caution</c>/<c>Text.Critical</c>/<c>Text.Success</c> consumer might ALSO show
    /// on its own matching InfoBar background (e.g. a caution message's title in Theme.Text.Caution sitting
    /// on Theme.InfoBar.Caution.Background) — <see cref="TextPrimary_MeetsTextContrast_OnEveryInfoBarBackground"/>
    /// only ever checked the body-text pairing (Text.Primary), not this one.
    /// </summary>
    /// <param name="label">Human-readable palette label (test-output only).</param>
    /// <param name="palette">The palette (<see cref="ThemePalette.Light"/> or <see cref="ThemePalette.Dark"/>) under test.</param>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void SeverityText_MeetsTextContrast_OnItsOwnInfoBarBackground(string label, ThemePalette palette)
    {
        AssertText(label, "TextCaution/InfoBarCaution", palette.TextCaution, palette.InfoBarCautionBackground);
        AssertText(label, "TextCritical/InfoBarCritical", palette.TextCritical, palette.InfoBarCriticalBackground);
        AssertText(label, "TextSuccess/InfoBarSuccess", palette.TextSuccess, palette.InfoBarSuccessBackground);
    }

    private static void AssertText(string paletteLabel, string pairLabel, System.Windows.Media.Color foreground, System.Windows.Media.Color background)
    {
        var contrast = ContrastCalculator.ContrastRatio(foreground, background);
        Assert.True(
            contrast >= ContrastCalculator.MinimumTextContrast,
            $"{paletteLabel} {pairLabel}: expected >= {ContrastCalculator.MinimumTextContrast}:1 text contrast; got {contrast:F2}.");
    }

    private static void AssertIcon(string paletteLabel, string pairLabel, System.Windows.Media.Color foreground, System.Windows.Media.Color background)
    {
        var contrast = ContrastCalculator.ContrastRatio(foreground, background);
        Assert.True(
            contrast >= ContrastCalculator.MinimumIconContrast,
            $"{paletteLabel} {pairLabel}: expected >= {ContrastCalculator.MinimumIconContrast}:1 non-text contrast; got {contrast:F2}.");
    }
}
