using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Unit tests for <see cref="PanelCustomizeWindow.OnClosing"/>'s <c>IsDirty</c> guard (FIX round 2
/// MINOR-3): the two colour hex <see cref="TextBox"/>es bind with <c>UpdateSourceTrigger=LostFocus</c>
/// (<c>PanelCustomizeWindow.xaml</c>), so closing the window while one still has an uncommitted edit and
/// no <c>LostFocus</c> ever fired must still push that edit into the model — but ONLY when the binding
/// actually has a pending edit, not unconditionally on every close.
/// </summary>
/// <remarks>
/// Constructs a REAL <see cref="PanelCustomizeWindow"/> and drives the actual <see cref="Window.Show"/>/
/// <see cref="Window.Close"/> lifecycle — unlike most of this project's other Window-subclass harnesses
/// (e.g. <c>ChildWindowInitializeTests</c>), which deliberately never call <see cref="Window.Show"/> — so
/// that <see cref="Window.OnClosing"/> fires through its NORMAL path rather than via reflection. This is
/// required here (not merely convenient): <c>AccentColorTextBox</c>/<c>TextColorTextBox</c>'s
/// <c>{Binding Appearance.SelectedPanelAccentColor/TextColor}</c> only activate (<c>BindingExpression</c>
/// goes from <c>Unattached</c> to <c>Active</c>) once <see cref="Window.Show"/> actually drives this
/// <see cref="Window"/> through a real layout pass — confirmed empirically, mirroring
/// <c>Unit.Controls.AdaptiveIconGridPanelLayoutPolicyBindingTests</c>' own remarks for the identical
/// reason. <see cref="Window.Left"/>/<see cref="Window.Top"/> are pushed far off-screen and
/// <see cref="Window.ShowActivated"/> is set <see langword="false"/> before <see cref="Window.Show"/>, so
/// nothing visibly flashes or steals focus during a <c>dotnet test</c> run — same precaution that class
/// takes. Unlike <c>ChildWindow</c> (see <c>ResizeOverlayGeometryTests</c>' remarks on why THAT class
/// avoids <see cref="Window.Show"/>), <see cref="PanelCustomizeWindow"/> owns no background
/// <see cref="System.Threading.Timer"/>/native <c>HWND</c> integration of its own, so showing/closing a
/// real instance here carries none of that cross-thread dispatcher-ownership risk.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelCustomizeWindowClosingTests
{
    /// <summary>
    /// Verifies that closing the window while <c>AccentColorTextBox</c> has an uncommitted (dirty) edit —
    /// typed but never blurred, so <c>LostFocus</c> never pushed it — still commits that edit to the
    /// target panel's <see cref="ChildWindowModel.AccentColor"/>.
    /// </summary>
    [Fact]
    public void OnClosing_DirtyAccentColorBinding_CommitsValueToModel()
    {
        RunOnStaThread(() =>
        {
            using var harness = PanelCustomizeWindowHarness.Build();
            var model = new ChildWindowModel { AccentColor = "#111111" };
            harness.ViewModel.Target = model;

            harness.ShowOffscreen();

            // SetCurrentValue, NOT the Text property setter: TextBox's own internal editing pipeline
            // updates Text this way specifically so an active Binding stays attached and tracks the
            // pending edit — a plain SetValue (what the `Text = ...` CLR setter does) replaces the
            // property's local value outright and never registers as a dirty, uncommitted edit.
            harness.Window.AccentColorTextBox.SetCurrentValue(TextBox.TextProperty, "#ABCDEF"); // Never lost focus.
            Assert.True(harness.Window.AccentColorTextBox.GetBindingExpression(TextBox.TextProperty)!.IsDirty);

            harness.Window.Close();

            Assert.Equal("#ABCDEF", model.AccentColor);
        });
    }

    /// <summary>
    /// Symmetric coverage for <c>TextColorTextBox</c>/<see cref="ChildWindowModel.TextColor"/>.
    /// </summary>
    [Fact]
    public void OnClosing_DirtyTextColorBinding_CommitsValueToModel()
    {
        RunOnStaThread(() =>
        {
            using var harness = PanelCustomizeWindowHarness.Build();
            var model = new ChildWindowModel { TextColor = "#EEEEEE" };
            harness.ViewModel.Target = model;

            harness.ShowOffscreen();

            harness.Window.TextColorTextBox.SetCurrentValue(TextBox.TextProperty, "#123456");
            Assert.True(harness.Window.TextColorTextBox.GetBindingExpression(TextBox.TextProperty)!.IsDirty);

            harness.Window.Close();

            Assert.Equal("#123456", model.TextColor);
        });
    }

    /// <summary>
    /// Verifies that closing the window WITHOUT ever touching either colour <see cref="TextBox"/> — the
    /// common case, both bindings stay clean — never cancels closing and leaves the model's colours
    /// completely untouched (MINOR-3's actual point: <c>UpdateSource()</c> is no longer called
    /// unconditionally on every close).
    /// </summary>
    [Fact]
    public void OnClosing_NeitherBindingDirty_LeavesModelUntouchedAndDoesNotCancel()
    {
        RunOnStaThread(() =>
        {
            using var harness = PanelCustomizeWindowHarness.Build();
            var model = new ChildWindowModel { AccentColor = "#111111", TextColor = "#EEEEEE" };
            harness.ViewModel.Target = model;

            harness.ShowOffscreen();

            Assert.False(harness.Window.AccentColorTextBox.GetBindingExpression(TextBox.TextProperty)!.IsDirty);
            Assert.False(harness.Window.TextColorTextBox.GetBindingExpression(TextBox.TextProperty)!.IsDirty);

            var cancelled = false;
            harness.Window.Closing += (_, e) => cancelled = e.Cancel;

            harness.Window.Close();

            Assert.False(cancelled);
            Assert.Equal("#111111", model.AccentColor);
            Assert.Equal("#EEEEEE", model.TextColor);
        });
    }

    private static void RunOnStaThread(Action action) => StaTestRunner.Run(action);

    /// <summary>
    /// Owns the swapped-in <see cref="ResourceDictionary"/> for one <see cref="StaTestRunner.Run(Action)"/>
    /// call and a real <see cref="PanelCustomizeWindow"/> built against it — same shape as
    /// <c>ChildWindowInitializeTests.ChildWindowHarness</c>.
    /// </summary>
    private sealed class PanelCustomizeWindowHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private PanelCustomizeWindowHarness(
            Application app,
            ResourceDictionary? originalResources,
            bool ownsApplication,
            PanelCustomizeWindow window,
            PanelCustomizeViewModel viewModel)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
            ViewModel = viewModel;
        }

        public PanelCustomizeWindow Window { get; }

        public PanelCustomizeViewModel ViewModel { get; }

        public static PanelCustomizeWindowHarness Build()
        {
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            MergeResourceDictionaries(app);

            var viewModel = new PanelCustomizeViewModel(
                Mock.Of<IWindowManager>(),
                Mock.Of<ILayoutEditModeService>(),
                Mock.Of<IFolderPickerService>());

            var window = new PanelCustomizeWindow(viewModel, Mock.Of<IMonitorService>());

            return new PanelCustomizeWindowHarness(app, originalResources, ownsApplication, window, viewModel);
        }

        /// <summary>
        /// Shows <see cref="Window"/> far off-screen and non-activating (matching
        /// <c>AdaptiveIconGridPanelLayoutPolicyBindingTests.BuildOffscreenWindow</c>'s own precaution) —
        /// required so its <c>Binding</c>s actually activate (see this class's own remarks) without ever
        /// visibly flashing or stealing focus during a <c>dotnet test</c> run.
        /// </summary>
        public void ShowOffscreen()
        {
            Window.Left = -5000;
            Window.Top = -5000;
            Window.ShowActivated = false;
            Window.Show();
        }

        public void Dispose()
        {
            if (Window.IsVisible) Window.Close();

            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }

        private static void MergeResourceDictionaries(Application app)
        {
            var merged = app.Resources.MergedDictionaries;
            foreach (var relativePath in new[]
                     {
                         "/Resources/Styles/Colors.xaml",
                         "/Resources/Styles/Spacing.xaml",
                         "/Resources/Styles/Brushes.xaml",
                         "/Resources/Styles/Typography.xaml",
                         "/Resources/Styles/Icons.xaml",
                         "/Resources/Styles/Controls.xaml",
                         "/Resources/Styles/Panels.xaml",
                         "/Resources/Templates.xaml",
                     })
            {
                merged.Add(new ResourceDictionary { Source = new Uri($"/DesktopOrganizer.UI;component{relativePath}", UriKind.Relative) });
            }
        }
    }
}
