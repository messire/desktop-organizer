using System.Windows.Media;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Builds the tray menu's <see cref="TrayMenuPalette"/> (ARGB primitives for the WinForms renderer in
/// DesktopOrganizer.Services) from the single C# source of truth, <see cref="ThemePalette"/> (design system
/// 1.x, docs/PLANS/design-system-1x.md §2.1/§2.9 variant A, ADR-047 D-10), and decides which palette the
/// tray uses for a given Windows theme state. The token mapping is the WPF menus' own
/// (<c>Theme.ContextMenu</c>/<c>Theme.MenuItem</c> in <c>Controls.xaml</c>), so the tray menu and the WPF
/// menus render from the same colours.
/// </summary>
/// <remarks>
/// Lives next to <see cref="TrayThemeAdapter"/> (its only production caller) rather than in
/// <c>UI/Theming</c>: phase 6.4 ran in parallel with 6.2b/6.2c, which own that folder.
/// </remarks>
internal static class TrayMenuPaletteFactory
{
    /// <summary>
    /// Highlight corner radius in DIPs — the same value <see cref="ThemeResourceBuilder"/> publishes as
    /// <c>Theme.Radius.Control</c>, the corner radius of a WPF <c>Theme.MenuItem</c> highlight.
    /// </summary>
    internal const double HighlightCornerRadius = 4;

    /// <summary>Gets the light tray palette (built from <see cref="ThemePalette.Light"/>).</summary>
    public static TrayMenuPalette Light { get; } = Create(ThemePalette.Light);

    /// <summary>Gets the dark tray palette (built from <see cref="ThemePalette.Dark"/>).</summary>
    public static TrayMenuPalette Dark { get; } = Create(ThemePalette.Dark);

    /// <summary>
    /// Maps one <see cref="ThemePalette"/> onto the tray palette: surface <c>Surface.Flyout</c>, border
    /// <c>Stroke.Card</c>, text <c>Text.Primary</c>/<c>Text.Disabled</c>, highlight <c>Control.Subtle.Hover</c>
    /// composited over the surface (the renderer fills it opaquely), separator <c>Stroke.Divider</c>, glyphs
    /// <c>Text.Primary</c> (the WPF checkmark inherits the item foreground).
    /// </summary>
    /// <param name="palette">The chrome palette.</param>
    /// <returns>The tray palette.</returns>
    internal static TrayMenuPalette Create(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        return new TrayMenuPalette(
            Background: ToArgb(palette.SurfaceFlyout),
            Border: ToArgb(palette.StrokeCard),
            Text: ToArgb(palette.TextPrimary),
            TextDisabled: ToArgb(palette.TextDisabled),
            HoverFill: ToArgb(ContrastCalculator.CompositeOverOpaque(palette.ControlSubtleHover, palette.SurfaceFlyout)),
            Separator: ToArgb(palette.StrokeDivider),
            Glyph: ToArgb(palette.TextPrimary),
            CornerRadius: HighlightCornerRadius);
    }

    /// <summary>
    /// Pure resolution rule (§2.1): high contrast (either the resolved application theme or the snapshot's
    /// own flag) → <see langword="null"/> (the system renderer); otherwise the tray follows the Windows
    /// MODE (<see cref="SystemThemeSnapshot.SystemUsesLightTheme"/> — the taskbar/native tray menus follow
    /// it, not the app mode), and only once the application's dark-theme gate is open — the same
    /// <see cref="AppThemeService.DefaultDarkThemeEnabled"/> switch that ships the rest of the dark chrome.
    /// </summary>
    /// <param name="appTheme">The resolved application theme (<see cref="IAppThemeService.Current"/>).</param>
    /// <param name="snapshot">The current Windows colour-mode snapshot.</param>
    /// <param name="darkThemeEnabled">The dark-theme gate.</param>
    /// <returns>The palette to apply, or <see langword="null"/> for the high-contrast system look.</returns>
    internal static TrayMenuPalette? Resolve(AppTheme appTheme, SystemThemeSnapshot snapshot, bool darkThemeEnabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (appTheme == AppTheme.HighContrast || snapshot.HighContrast) return null;
        return darkThemeEnabled && !snapshot.SystemUsesLightTheme ? Dark : Light;
    }

    /// <summary>Packs a WPF colour into <c>0xAARRGGBB</c>.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The ARGB value.</returns>
    internal static uint ToArgb(Color color)
        => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
