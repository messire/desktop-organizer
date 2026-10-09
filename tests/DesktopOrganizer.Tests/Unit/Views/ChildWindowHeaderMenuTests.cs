using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// T-7 (docs/PLANS/design-system-1x.md §3 phase 6.2c): real, production-instance regression coverage for
/// <see cref="ChildWindow"/>'s own <c>HeaderContextMenu</c> — the remaining T-7 items
/// <c>Unit.Theming.ChromeStylesStaTests</c>' own remarks explicitly deferred to "once 6.2c wires
/// ChildWindow.xaml onto these styles": the REAL <c>HeaderContextMenu</c> instance (not a synthetic
/// analogue), in all three <see cref="AppTheme"/> values, plus the §2.6 p.3 structural DoD (≤ 16 top-level
/// items, no disabled-pseudo-header left) and the "<c>TitleEditBox</c> never picks up <c>Theme.TextBox</c>"
/// guarantee.
/// </summary>
/// <remarks>
/// Harness duplicated from <c>ChildWindowInitializeTests.ChildWindowHarness</c> (same convention that
/// class's own remarks document — small STA/chrome-accounting harnesses are duplicated per fixture rather
/// than shared), parameterized by <see cref="AppTheme"/> via <see cref="TestThemeResources.MergeReduced(Application, AppTheme)"/>.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowHeaderMenuTests
{
    private static readonly AppTheme[] AllThemes = [AppTheme.Light, AppTheme.Dark, AppTheme.HighContrast];

    /// <summary><c>[MemberData]</c> source — one <see cref="AppTheme"/> case per entry of <see cref="AllThemes"/>.</summary>
    public static IEnumerable<object[]> ThemeCases() => AllThemes.Select(t => new object[] { t });

    /// <summary>
    /// design-system-1x.md §2.6 p.3: the real, compiled <c>HeaderContextMenu</c> has at most 16 top-level
    /// items (Rename/Customize/Move/Copy/RollUp/Size/AlignToGrid/IconLayout/PanelSizing/EditLayout/
    /// LockLayout/Show/Arrange — 13 <see cref="MenuItem"/>s plus 3 <see cref="Separator"/>s counted here as
    /// non-<see cref="MenuItem"/> siblings, not toward the 16) — down from the pre-6.2c ≈ 20 (the
    /// <c>Panel_Menu_ContentHeader</c>/<c>Panel_Menu_ArrangeIconsHeader</c> disabled labels plus their 4/2
    /// flat radio children all counted as separate top-level items before the §2.6 p.3 submenu conversion).
    /// </summary>
    [Fact]
    public void HeaderContextMenu_TopLevelMenuItemCount_IsAtMost16()
    {
        StaTestRunner.Run(() =>
        {
            using var harness = ChildWindowHarness.Build(AppTheme.Light);

            var topLevelMenuItems = harness.Window.HeaderContextMenu.Items.OfType<MenuItem>().ToList();

            Assert.True(topLevelMenuItems.Count <= 16, $"Expected <= 16 top-level MenuItems; got {topLevelMenuItems.Count}.");
        });
    }

    /// <summary>
    /// design-system-1x.md §2.6 p.3 (closes S-17): <c>ContentFilterMenu</c>/<c>SortModeMenu</c> — the two
    /// submenus that replaced the old disabled-pseudo-header + flat radio-group run — are themselves always
    /// enabled (never <see cref="UIElement.IsEnabled"/> <see langword="false"/>, unlike the legitimately
    /// layout-gated "Size"/"Icon layout"/"Panel sizing" submenus) and each has at least one child item, so
    /// there is no longer a disabled, childless pseudo-label anywhere in this menu.
    /// </summary>
    [Fact]
    public void HeaderContextMenu_ContentAndArrangeSubmenus_AreEnabledWithChildren()
    {
        StaTestRunner.Run(() =>
        {
            using var harness = ChildWindowHarness.Build(AppTheme.Light);

            Assert.True(harness.Window.ContentFilterMenu.IsEnabled);
            Assert.NotEmpty(harness.Window.ContentFilterMenu.Items);

            Assert.True(harness.Window.SortModeMenu.IsEnabled);
            Assert.NotEmpty(harness.Window.SortModeMenu.Items);
        });
    }

    /// <summary>
    /// design-system-1x.md §2.6 p.3: once actually opened, <c>ContentFilterMenu</c>/<c>SortModeMenu</c>
    /// resolve to <see cref="MenuItemRole.SubmenuHeader"/> — a real submenu, not a top-level leaf — exactly
    /// like the pre-existing "Icon layout"/"Panel sizing" submenus already did.
    /// </summary>
    [Fact]
    public void HeaderContextMenu_ContentAndArrangeSubmenus_ResolveToSubmenuHeaderRole()
    {
        StaTestRunner.Run(() =>
        {
            using var harness = ChildWindowHarness.Build(AppTheme.Light);

            harness.Window.HeaderContextMenu.IsOpen = true;
            harness.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.Equal(MenuItemRole.SubmenuHeader, harness.Window.ContentFilterMenu.Role);
            Assert.Equal(MenuItemRole.SubmenuHeader, harness.Window.SortModeMenu.Role);

            harness.Window.HeaderContextMenu.IsOpen = false;
        });
    }

    /// <summary>
    /// design-system-1x.md §2.6/ADR-047 D-7: the real <c>HeaderContextMenu</c> is styled with
    /// <c>Theme.ContextMenu</c> — resolving in every one of <see cref="AllThemes"/> (Light/Dark/HighContrast)
    /// without throwing, confirming the menu follows the theme of Windows rather than being pinned to the
    /// pre-6.2c always-dark <c>Control.ContextMenu</c>.
    /// </summary>
    /// <param name="theme">The Palette T theme to materialize the resource chain against.</param>
    [Theory]
    [MemberData(nameof(ThemeCases))]
    public void HeaderContextMenu_UsesThemeContextMenuStyle_InEveryTheme(AppTheme theme)
    {
        StaTestRunner.Run(() =>
        {
            using var harness = ChildWindowHarness.Build(theme);

            Assert.Same(harness.App.Resources["Theme.ContextMenu"], harness.Window.HeaderContextMenu.Style);
        });
    }

    /// <summary>
    /// design-system-1x.md §2.5 (T-7): <c>TitleEditBox</c> is a Palette P (panel chrome) control — with NO
    /// <c>Style</c> attribute of its own in <c>ChildWindow.xaml</c>, it resolves to <see langword="null"/>
    /// (WPF's stock <see cref="TextBox"/> default) rather than any Palette T <c>Theme.TextBox</c> style —
    /// which could only happen if <c>Theme.Implicit.xaml</c>'s IMPLICIT (un-keyed) <see cref="TextBox"/>
    /// redirection were ever merged into this <see cref="ChildWindow"/>'s own
    /// <see cref="FrameworkElement.Resources"/> or, worse, app-wide. Neither is true today — that dictionary
    /// is scoped to chrome-window <c>Window.Resources</c> only (<c>MainWindow</c>/<c>PanelCustomizeWindow</c>/
    /// the future <c>AppDialogWindow</c>) — confirmed here structurally (never merged inside
    /// <c>ChildWindow.xaml</c>'s own markup) and at runtime (no local/implicit <see cref="Style"/> applies).
    /// </summary>
    [Fact]
    public void TitleEditBox_NeverResolvesThemeTextBoxStyle()
    {
        var repoRoot = RepoRootLocator.Find();
        var childWindowXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DesktopOrganizer.UI", "Views", "ChildWindow.xaml"));
        Assert.DoesNotContain("Theme.Implicit.xaml", childWindowXaml);
        Assert.DoesNotContain("Theme.TextBox", childWindowXaml);

        StaTestRunner.Run(() =>
        {
            using var harness = ChildWindowHarness.Build(AppTheme.Light);

            // No Theme.Controls.xaml/Theme.Implicit.xaml anywhere in this reduced, production-mirroring
            // chain (TestThemeResources.MergeReduced) — the same chain ChildWindow's own real constructor
            // path resolves against — so an un-styled TextBox has nothing but the WPF stock default to fall
            // back to.
            Assert.Null(harness.Window.TitleEditBox.Style);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // STA/resource-chain infrastructure — duplicated from ChildWindowInitializeTests.ChildWindowHarness
    // (see that class's own remarks for why), parameterized by AppTheme.
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    private sealed class ChildWindowHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private ChildWindowHarness(Application app, ResourceDictionary? originalResources, bool ownsApplication, ChildWindow window)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
        }

        public ChildWindow Window { get; }

        public Application App => _app;

        public static ChildWindowHarness Build(AppTheme theme)
        {
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            TestThemeResources.MergeReduced(app, theme);

            // ChildWindow.xaml's HeaderContextMenu binds three MenuItems directly to
            // {StaticResource LayoutEditMode} — see ChildWindowInitializeTests.ChildWindowHarness's own
            // identical remarks.
            app.Resources["LayoutEditMode"] = Mock.Of<ILayoutEditModeService>();

            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((ImageSource)null!);

            var viewModel = new ChildWindowViewModel(
                iconProvider.Object,
                Mock.Of<IFileLauncher>(),
                Mock.Of<IFolderWatcherFactory>(),
                Mock.Of<IPanelItemStorageService>(),
                Mock.Of<IFileItemRenameService>(),
                Mock.Of<IAppSettings>(),
                Mock.Of<IPanelSelectionCoordinator>(),
                Mock.Of<IGestureDiagnostics>());

            var appSettings = new Mock<IAppSettings>();
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, true);
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, string.Empty);

            var window = new ChildWindow(
                viewModel,
                Mock.Of<IWindowManager>(),
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                Mock.Of<IGestureDiagnostics>(),
                Mock.Of<INativePanelSizingService>(),
                (ILayoutEditModeService)app.Resources["LayoutEditMode"],
                Mock.Of<IMonitorService>(),
                Mock.Of<IConfirmationService>(),
                Mock.Of<IPanelCustomizeService>(),
                Mock.Of<IPanelItemDragOutSession>(),
                FakeDesktopScale.Uniform().Object);

            return new ChildWindowHarness(app, originalResources, ownsApplication, window);
        }

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }
    }
}
