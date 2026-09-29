using System.Windows;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Shared reduced resource-dictionary merge chain for the ChildWindow-family STA harnesses that
/// deliberately build a SMALLER <see cref="Application.Resources"/> chain than
/// <see cref="WpfApplicationHost"/>'s full one — no <c>Settings.xaml</c>, since none of these fixtures'
/// windows (<c>ChildWindow</c>, its resize/sizing-grid/content-sizing/chrome-geometry harnesses) ever
/// resolve a <c>Settings.*</c> key (see docs/PLANS/design-system-1x.md §1/R-7 "тестовые фикстуры с
/// урезанным merge-списком"). Before this helper existed, this EXACT eight-entry list was hand-duplicated,
/// byte-for-byte, across five separate harness classes'
/// own private <c>MergeResourceDictionaries(Application)</c> methods
/// (<c>Unit.Views.ChildWindowContentSizingTests</c>, <c>ChildWindowInitializeTests</c>,
/// <c>ChildWindowSizingGridTests</c>, <c>PanelChromeLayeredGeometryTests</c>,
/// <c>ResizeOverlayGeometryTests</c>) — a sixth near-twin,
/// <c>Unit.Views.PanelCustomizeWindowClosingTests</c>, is a DIFFERENT (nine-entry, Settings.xaml-inclusive)
/// list and is deliberately NOT folded into this same helper (its window's controls genuinely need
/// <c>Settings.*</c> keys post-ADR-042; see that fixture's own remarks).
/// </summary>
/// <remarks>
/// Phase 6.1 (design-system-1x.md §3): now also appends the "Palette T" theme dictionary
/// (<see cref="AppendTheme"/>, <see cref="AppTheme.Light"/>) after the reduced chain, so any of these
/// harnesses' windows/controls that resolve a <c>{DynamicResource Theme.*}</c>/<c>{DynamicResource
/// Font.Size.*}</c> key (<c>Settings.xaml</c>'s app-wide styles do, even though these five harnesses don't
/// merge <c>Settings.xaml</c> itself) get a real value instead of an unresolved <c>DynamicResource</c>.
/// <see cref="MergeReduced(Application)"/>'s own signature is unchanged — existing call sites automatically
/// pick this up.
/// </remarks>
public static class TestThemeResources
{
    /// <summary>
    /// The reduced (no <c>Settings.xaml</c>) merge chain, in the same relative order the five harnesses'
    /// own former hand-duplicated lists already used.
    /// </summary>
    private static readonly string[] ReducedResourcePaths =
    [
        "/Resources/Styles/Colors.xaml",
        "/Resources/Styles/Spacing.xaml",
        "/Resources/Styles/Brushes.xaml",
        "/Resources/Styles/Typography.xaml",
        "/Resources/Styles/Icons.xaml",
        "/Resources/Styles/Controls.xaml",
        "/Resources/Styles/Panels.xaml",
        "/Resources/Templates.xaml",
    ];

    /// <summary>
    /// Merges the reduced (no <c>Settings.xaml</c>) resource-dictionary chain into <paramref name="app"/>'s
    /// <see cref="Application.Resources"/>, then appends the Light theme dictionary
    /// (<see cref="AppendTheme"/>) — the caller is responsible for having already swapped in a fresh, empty
    /// <see cref="ResourceDictionary"/> first (same convention every one of the five harnesses' own
    /// <c>Build</c> methods already follows).
    /// </summary>
    /// <param name="app">The process-wide <see cref="Application"/> whose <see cref="Application.Resources"/> receives the merge.</param>
    public static void MergeReduced(Application app) => MergeReduced(app, AppTheme.Light);

    /// <summary>
    /// Same as <see cref="MergeReduced(Application)"/>, but appends <paramref name="theme"/>'s own built
    /// dictionary instead of always Light — added for phase 6.2c's ChildWindow-family tests
    /// (design-system-1x.md §3 T-7) that need <c>HeaderContextMenu</c>'s real, compiled
    /// <c>Theme.ContextMenu</c>/<c>Theme.MenuItem</c> styles materialized against Dark/HighContrast too, not
    /// only the production default.
    /// </summary>
    /// <param name="app">The process-wide <see cref="Application"/> whose <see cref="Application.Resources"/> receives the merge.</param>
    /// <param name="theme">The theme to append.</param>
    public static void MergeReduced(Application app, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);

        var merged = app.Resources.MergedDictionaries;
        foreach (var relativePath in ReducedResourcePaths)
        {
            merged.Add(new ResourceDictionary
            {
                Source = new Uri($"/DesktopOrganizer.UI;component{relativePath}", UriKind.Relative),
            });
        }

        AppendTheme(app, theme);
    }

    /// <summary>
    /// Appends the <see cref="ThemeResourceBuilder"/>-built dictionary for <paramref name="theme"/> to
    /// <paramref name="app"/>'s <see cref="Application.Resources"/> — the same dictionary
    /// <see cref="AppThemeService"/> would apply in production, built here directly (not through a
    /// running <see cref="AppThemeService"/> instance) since most fixtures only need a stable,
    /// one-shot set of <c>Theme.*</c>/<c>Font.Size.*</c> resources, not live theme switching. Used both by
    /// <see cref="MergeReduced(Application)"/> and directly by the "full" merge lists (<see cref="WpfApplicationHost"/>,
    /// <c>PanelCustomizeWindowClosingTests</c>) that already include <c>Settings.xaml</c>.
    /// </summary>
    /// <param name="app">The process-wide <see cref="Application"/> whose <see cref="Application.Resources"/> receives the theme dictionary.</param>
    /// <param name="theme">The theme to build — ordinarily <see cref="AppTheme.Light"/>, the production gate's default (§2.1 D-2).</param>
    public static void AppendTheme(Application app, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Resources.MergedDictionaries.Add(ThemeResourceBuilder.Build(theme, System.Windows.SystemFonts.MessageFontSize));
    }
}
