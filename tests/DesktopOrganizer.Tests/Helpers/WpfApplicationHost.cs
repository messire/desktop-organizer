using System.Windows;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Creates the ONE process-wide <see cref="Application"/> this test assembly is allowed to have, with
/// the same resource-dictionary merge chain <c>App.xaml.cs</c>'s <c>LoadResourceDictionaries()</c>
/// builds in production (normal, non-High-Contrast).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Application"/> is a singleton that can never be un-constructed: whichever test happens to
/// build it first decides, for the rest of the <c>dotnet test</c> process, both what
/// <see cref="Application.Resources"/> contains and which thread's <see cref="System.Windows.Threading.Dispatcher"/>
/// every later <c>Application.Current.Dispatcher</c> marshal targets. A bare <c>new Application()</c>
/// therefore has action at a distance: it leaves EMPTY resources behind and breaks every other fixture
/// that expects the real merge chain (e.g. <c>ChildWindowViewModel.GetTemplate</c>'s
/// <c>FindResource("ListViewTemplate")</c>). This helper exists so that "if I turn out to be first,
/// leave the state everyone else already assumes" is one call instead of a copy-pasted list.
/// </para>
/// <para>
/// See docs/Refactoring/Testing/sta-test-conventions.md for the full set of rules (one owner, the owner
/// never shuts its dispatcher down, timers stopped on teardown).
/// </para>
/// </remarks>
public static class WpfApplicationHost
{
    /// <summary>
    /// The merge chain, in the same order <c>App.xaml.cs</c>'s <c>LoadResourceDictionaries()</c> uses.
    /// </summary>
    private static readonly string[] ResourcePaths =
    [
        "/Resources/Styles/Colors.xaml",
        "/Resources/Styles/Spacing.xaml",
        "/Resources/Styles/Brushes.xaml",
        "/Resources/Styles/Typography.xaml",
        "/Resources/Styles/Icons.xaml",
        "/Resources/Styles/Controls.xaml",
        "/Resources/Styles/Settings.xaml",

        // Design-system-1x phase 6.2a: mirrors App.xaml.cs's own LoadResourceDictionaries() order — see
        // that method's own remarks.
        "/Resources/Styles/Theme.Controls.xaml",
        "/Resources/Styles/Panels.xaml",
        "/Resources/Templates.xaml",
    ];

    /// <summary>
    /// Returns the existing <see cref="Application.Current"/>, or constructs it — with the real resource
    /// chain merged in — on the CALLING thread if there isn't one yet.
    /// </summary>
    /// <returns>The process-wide <see cref="Application"/>; never <see langword="null"/>.</returns>
    /// <remarks>
    /// Call this from an STA thread that will keep pumping afterwards (<see cref="StaTestRunner"/> does
    /// this automatically for the thread that ends up owning the Application) — NOT from a thread that is
    /// about to exit or shut its dispatcher down.
    /// </remarks>
    public static Application EnsureApplication()
    {
        if (Application.Current is { } existing) return existing;

        var app = new Application();
        foreach (var relativePath in ResourcePaths)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/DesktopOrganizer.UI;component{relativePath}", UriKind.Relative),
            });
        }

        // Phase 6.1 (design-system-1x.md §3): the "Palette T" theme dictionary App.xaml.cs's
        // AppThemeService.Start() applies in production — see TestThemeResources.AppendTheme's own remarks.
        TestThemeResources.AppendTheme(app, DesktopOrganizer.UI.Theming.AppTheme.Light);

        return app;
    }
}
