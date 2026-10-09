using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// M-2 fix (review round, design-system-1x.md §2.8): <see cref="DialogPresenter"/>'s owner-eligibility
/// rule — a <see cref="ChildWindow"/> (<c>WS_EX_NOACTIVATE</c>, ADR-001/013) or
/// <see cref="SystemIconsPanelWindow"/> (chromeless, never-activated background surface, ADR-019) must
/// never be assigned as an <see cref="DesktopOrganizer.UI.Views.Dialogs.AppDialogWindow"/>'s <see cref="Window.Owner"/>, even
/// if some future code path ever made either briefly report <see cref="Window.IsActive"/> = <see langword="true"/>.
/// </summary>
/// <remarks>
/// Only the extracted <see cref="DialogPresenter.IsEligibleOwner"/> predicate is unit-testable here — there
/// is no seam over the real, process-wide <see cref="Application.Windows"/> collection
/// <see cref="DialogPresenter"/>'s own owner-resolution loop enumerates (it reflects genuine OS-level
/// top-level windows, not something a fake list can stand in for), so the full "prefer the active chrome
/// window, else the visible MainWindow, else none" ORDERING stays manual-verify territory, same as this
/// class's other R-5/R-6 mitigations.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class DialogPresenterTests
{
    /// <summary>An ordinary chrome <see cref="Window"/> — neither a <see cref="ChildWindow"/> nor a <see cref="SystemIconsPanelWindow"/> — is eligible.</summary>
    [Fact]
    public void IsEligibleOwner_PlainWindow_ReturnsTrue()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { ShowActivated = false, Left = -5000, Top = -5000 };
            try
            {
                Assert.True(DialogPresenter.IsEligibleOwner(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A real <see cref="ChildWindow"/> is never eligible.</summary>
    [Fact]
    public void IsEligibleOwner_ChildWindow_ReturnsFalse()
    {
        StaTestRunner.Run(() =>
        {
            using var harness = ChildWindowHarness.Build();

            Assert.False(DialogPresenter.IsEligibleOwner(harness.Window));
        });
    }

    /// <summary>A real <see cref="SystemIconsPanelWindow"/> is never eligible.</summary>
    [Fact]
    public void IsEligibleOwner_SystemIconsPanelWindow_ReturnsFalse()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var viewModel = new SystemIconsPanelViewModel(
                new SystemIconsPanelModel(),
                Mock.Of<ISystemShellService>(),
                Mock.Of<IPanelSelectionCoordinator>(),
                Mock.Of<IWindowManager>());

            var window = new SystemIconsPanelWindow(viewModel) { ShowActivated = false, Left = -5000, Top = -5000 };
            try
            {
                Assert.False(DialogPresenter.IsEligibleOwner(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Minimal single-window <see cref="ChildWindow"/> harness — just enough to construct a real instance
    /// for a type check, not to exercise any of its own behavior (contrast
    /// <c>Unit.Views.ChildWindowInitializeTests</c>'s own near-identical harness, which this one is
    /// deliberately a smaller copy of — see <c>Helpers.TestThemeResources</c>'s own remarks for why this
    /// project's convention is to duplicate this small harness per test class rather than share it).
    /// </summary>
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

        public static ChildWindowHarness Build()
        {
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            TestThemeResources.MergeReduced(app);

            // ChildWindow.xaml's HeaderContextMenu binds three MenuItems directly to
            // {StaticResource LayoutEditMode} — without this key InitializeComponent() throws
            // XamlParseException immediately (see ChildWindowInitializeTests' own identical remark).
            app.Resources["LayoutEditMode"] = Mock.Of<ILayoutEditModeService>();

            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((System.Windows.Media.ImageSource)null!);

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
            Window.Close();

            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }
    }
}
