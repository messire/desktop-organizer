namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// The resolved "Palette T" (application chrome — settings window, Customize, dialogs, WPF menus,
/// tooltips, tray) theme, as decided by <see cref="IAppThemeService"/> (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1, ADR-047 D-1/D-2). Distinct from
/// <see cref="PanelThemeMode"/> (Palette P, per-panel, unaffected by this type).
/// </summary>
public enum AppTheme
{
    /// <summary>The light chrome palette (Windows "Choose your app mode" = light, or the default gate value).</summary>
    Light,

    /// <summary>The dark chrome palette (Windows "Choose your app mode" = dark, gate open).</summary>
    Dark,

    /// <summary>
    /// A Windows high-contrast theme is active: every token resolves to the matching
    /// <see cref="System.Windows.SystemColors"/> brush instead of a fixed hex value.
    /// </summary>
    HighContrast,
}
