namespace DesktopOrganizer.UI.Theming;

/// <summary>
/// Resolves and applies the current "Palette T" (application chrome) theme
/// (docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1, ADR-047 D-1/D-2). A DI singleton — see
/// <see cref="AppThemeService"/>'s own remarks for the resolution rule and the <c>DarkThemeEnabled</c> gate.
/// </summary>
public interface IAppThemeService
{
    /// <summary>The theme currently applied to <see cref="System.Windows.Application.Resources"/>.</summary>
    AppTheme Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> changes and the new dictionary has already been merged into
    /// <see cref="System.Windows.Application.Resources"/> — always on the UI thread.
    /// </summary>
    event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>
    /// Resolves the initial theme, merges its dictionary into <see cref="System.Windows.Application.Resources"/>,
    /// and starts listening for further Windows theme/high-contrast/font-size changes. Must be called from
    /// the UI thread, once, after <c>LoadResourceDictionaries</c>. Idempotent.
    /// </summary>
    void Start();
}
