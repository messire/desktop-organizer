using System.Windows;
using System.Windows.Media;

namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Builds the single "Palette T" <see cref="ResourceDictionary"/> <see cref="AppThemeService"/> merges
/// into <see cref="Application.Resources"/> (docs/PLANS/design-system-1x.md §2.1, ADR-047 D-1/D-2/D-3).
/// Every <see cref="ThemeKeys"/> key is present in the result, for every <see cref="AppTheme"/> value —
/// <c>ThemeKeysCoverageTests</c> (T-2) checks this directly. Colour keys resolve to already-<see cref="Freezable.Freeze()"/>d
/// <see cref="SolidColorBrush"/>es (the dictionary itself is replaced wholesale on a theme switch, never
/// mutated in place, so freezing individual brushes is safe and required per WPF's own performance
/// guidance); geometry/typography keys resolve to plain <see cref="double"/>/<see cref="Thickness"/>/
/// <see cref="CornerRadius"/> values.
/// </summary>
public static class ThemeResourceBuilder
{
    /// <summary>
    /// Builds the resource dictionary for <paramref name="theme"/>.
    /// </summary>
    /// <param name="theme">
    /// <see cref="AppTheme.Light"/>/<see cref="AppTheme.Dark"/> pull from <see cref="ThemePalette"/>;
    /// <see cref="AppTheme.HighContrast"/> pulls every colour key from <see cref="SystemColors"/> instead.
    /// </param>
    /// <param name="messageFontSize">
    /// The base "M" size for <see cref="TypeRamp.FromMessageFontSize"/>, ordinarily
    /// <see cref="SystemFonts.MessageFontSize"/> — accepted as a parameter (not read internally) so tests
    /// can pin an exact ramp without touching the real system setting.
    /// </param>
    public static ResourceDictionary Build(AppTheme theme, double messageFontSize)
    {
        var dictionary = new ResourceDictionary();

        if (theme == AppTheme.HighContrast)
            AddHighContrastColors(dictionary);
        else
            AddPaletteColors(dictionary, theme == AppTheme.Dark ? ThemePalette.Dark : ThemePalette.Light);

        AddTypography(dictionary, TypeRamp.FromMessageFontSize(messageFontSize));
        AddGeometry(dictionary);

        return dictionary;
    }

    private static void AddPaletteColors(ResourceDictionary dictionary, ThemePalette palette)
    {
        foreach (var key in ThemeKeys.ColorKeys)
            dictionary[key] = FrozenBrush(palette.ByKey(key));
    }

    /// <summary>
    /// High-contrast mapping (plan §2.2's "HC (<c>SystemColors</c>)" column). <see cref="SystemColors"/>'
    /// own <c>*Brush</c> properties are already frozen system-owned singletons, so they are used directly
    /// rather than cloned — sharing them across dictionary rebuilds is exactly what WPF expects.
    /// </summary>
    private static void AddHighContrastColors(ResourceDictionary dictionary)
    {
        dictionary[ThemeKeys.SurfaceWindow] = SystemColors.WindowBrush;
        dictionary[ThemeKeys.SurfaceCard] = SystemColors.WindowBrush;
        dictionary[ThemeKeys.SurfaceCardHover] = SystemColors.ControlLightBrush;
        dictionary[ThemeKeys.SurfaceFlyout] = SystemColors.MenuBrush;

        dictionary[ThemeKeys.TextPrimary] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.TextSecondary] = SystemColors.WindowTextBrush; // HC reserves GrayText for disabled (plan footnote)
        dictionary[ThemeKeys.TextDisabled] = SystemColors.GrayTextBrush;
        dictionary[ThemeKeys.TextOnAccent] = SystemColors.HighlightTextBrush;
        dictionary[ThemeKeys.TextCaution] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.TextCritical] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.TextSuccess] = SystemColors.WindowTextBrush;

        dictionary[ThemeKeys.AccentFill] = SystemColors.HighlightBrush;
        dictionary[ThemeKeys.AccentHover] = SystemColors.HighlightBrush;
        dictionary[ThemeKeys.AccentPressed] = SystemColors.HighlightBrush;
        dictionary[ThemeKeys.AccentDisabled] = SystemColors.HighlightBrush;

        // WPF's SystemColors has no "ButtonFace"/"ButtonText" member (those are the WinForms/Win32 names) —
        // SystemColors.Control*/.ControlText* are its equivalents.
        dictionary[ThemeKeys.ControlFill] = SystemColors.ControlBrush;
        dictionary[ThemeKeys.ControlHover] = SystemColors.ControlBrush;
        dictionary[ThemeKeys.ControlPressed] = SystemColors.ControlBrush;
        dictionary[ThemeKeys.ControlDisabled] = SystemColors.ControlBrush;
        dictionary[ThemeKeys.ControlSubtleHover] = SystemColors.HighlightBrush;
        dictionary[ThemeKeys.ControlSubtlePressed] = SystemColors.HighlightBrush;

        dictionary[ThemeKeys.StrokeCard] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.StrokeDivider] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.StrokeControl] = SystemColors.ControlTextBrush;
        dictionary[ThemeKeys.StrokeInput] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.StrokeFocus] = SystemColors.WindowTextBrush;
        dictionary[ThemeKeys.StrokeFocusInner] = SystemColors.WindowBrush;

        dictionary[ThemeKeys.SelectionIndicator] = SystemColors.HighlightBrush;
        dictionary[ThemeKeys.SelectionFill] = SystemColors.HighlightBrush;
        dictionary[ThemeKeys.SelectionText] = SystemColors.HighlightTextBrush;

        dictionary[ThemeKeys.InfoBarInfoBackground] = SystemColors.WindowBrush;
        dictionary[ThemeKeys.InfoBarSuccessBackground] = SystemColors.WindowBrush;
        dictionary[ThemeKeys.InfoBarCautionBackground] = SystemColors.WindowBrush;
        dictionary[ThemeKeys.InfoBarCriticalBackground] = SystemColors.WindowBrush;
    }

    private static void AddTypography(ResourceDictionary dictionary, TypeRamp ramp)
    {
        dictionary[ThemeKeys.FontSizeCaption] = ramp.Caption;
        dictionary[ThemeKeys.FontSizeBody] = ramp.Body;
        dictionary[ThemeKeys.FontSizeBodyStrong] = ramp.BodyStrong;
        dictionary[ThemeKeys.FontSizeSubtitle] = ramp.Subtitle;
        dictionary[ThemeKeys.FontSizeTitle] = ramp.Title;
    }

    /// <summary>Theme-independent geometry (§2.4) — same values in Light/Dark/HighContrast.</summary>
    private static void AddGeometry(ResourceDictionary dictionary)
    {
        dictionary[ThemeKeys.CardPadding] = new Thickness(16, 12, 16, 12);
        dictionary[ThemeKeys.CardGap] = 4.0;
        dictionary[ThemeKeys.GroupGapTop] = 24.0;
        dictionary[ThemeKeys.GroupGapBottom] = 8.0;
        dictionary[ThemeKeys.PagePaddingSettings] = new Thickness(24, 16, 24, 16);
        dictionary[ThemeKeys.PagePaddingCustomize] = new Thickness(16, 12, 16, 12);
        dictionary[ThemeKeys.ControlMinHeight] = 32.0;
        dictionary[ThemeKeys.MenuItemMinHeight] = 30.0;
        dictionary[ThemeKeys.MenuItemPadding] = new Thickness(12, 6, 12, 6);
        dictionary[ThemeKeys.RadiusControl] = new CornerRadius(4);
        dictionary[ThemeKeys.RadiusPopup] = new CornerRadius(8);
        dictionary[ThemeKeys.RadiusCard] = new CornerRadius(6);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
