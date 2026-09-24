using System.Windows;
using System.Windows.Data;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// NIT coverage (P05 fix-cycle review): proves the exact <c>Binding</c> path
/// <c>Resources/Templates.xaml</c>'s <c>GridViewTemplate</c> <c>ItemsPanelTemplate</c> uses for
/// <see cref="AdaptiveIconGridPanel.LayoutPolicy"/> —
/// <c>{Binding DataContext.Model.IconLayoutPolicy, RelativeSource={RelativeSource AncestorType=Window}}</c>
/// — actually resolves at runtime against a real <c>ChildWindowModel.IconLayoutPolicy</c> value, now
/// that P02b has landed that property (the binding used to fail silently and fall back to the DP's own
/// <see cref="IconLayoutPolicy.LegacyStretch"/> default, by design, before P02b merged — see this
/// fix-cycle's Templates.xaml comment update). Uses <see cref="BindingOperations.SetBinding"/> directly
/// against a real <see cref="AdaptiveIconGridPanel"/> instance with the SAME path/RelativeSource, hosted
/// as a real <see cref="Window"/>'s <see cref="Window.Content"/>.
/// </summary>
/// <remarks>
/// <c>RelativeSource AncestorType=Window</c> walks the VISUAL tree, which for <see cref="Window.Content"/>
/// only actually connects once the <see cref="Window"/>'s own template applies a real
/// <see cref="System.Windows.Controls.ContentPresenter"/> — a plain <c>Measure</c>/<c>Arrange</c> call
/// pair on an unshown <see cref="Window"/> was tried first and did NOT reliably establish that
/// connection (confirmed empirically: the binding silently never resolved, indistinguishable from "no
/// path" without a genuinely showing window) — <see cref="Window.Show"/> is what actually drives a
/// <see cref="Window"/> through <see cref="System.Windows.Interop.HwndSource"/>/its real layout manager.
/// Kept off-screen (<see cref="Window.Left"/>/<see cref="Window.Top"/> far negative) and
/// non-activating so it never visibly flashes or steals focus during a <c>dotnet test</c> run.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class AdaptiveIconGridPanelLayoutPolicyBindingTests
{
    private sealed class StubModelHost(ChildWindowModel model)
    {
        public ChildWindowModel Model { get; } = model;
    }

    private static Binding BuildTemplatesXamlLayoutPolicyBinding() => new("DataContext.Model.IconLayoutPolicy")
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
    };

    private static Window BuildOffscreenWindow(object dataContext, UIElement content) => new()
    {
        DataContext = dataContext,
        Content = content,
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        ShowActivated = false,
        AllowsTransparency = true,
        Background = System.Windows.Media.Brushes.Transparent,
        Width = 500,
        Height = 500,
        Left = -32000,
        Top = -32000,
    };

    [Theory]
    [InlineData(IconLayoutPolicy.FixedGap)]
    [InlineData(IconLayoutPolicy.LegacyStretch)]
    public void LayoutPolicyBinding_ResolvesFromWindowDataContextModel_MatchingTemplatesXamlPath(IconLayoutPolicy policy)
    {
        StaTestRunner.Run(() =>
        {
            var model = new ChildWindowModel { IconLayoutPolicy = policy };
            var panel = new AdaptiveIconGridPanel();
            var window = BuildOffscreenWindow(new StubModelHost(model), panel);

            BindingOperations.SetBinding(panel, AdaptiveIconGridPanel.LayoutPolicyProperty, BuildTemplatesXamlLayoutPolicyBinding());

            try
            {
                window.Show();
                Assert.Equal(policy, panel.LayoutPolicy);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Regression guard for the pre-P02b state this binding used to be in: if the bound property is
    /// simply ABSENT (path doesn't exist on the DataContext at all), the binding must fail silently and
    /// the DP must keep its own default (<see cref="IconLayoutPolicy.LegacyStretch"/>) — never throw.
    /// This is what every panel observed before P02b merged, and it must remain a safe (if stale)
    /// fallback shape, not a hard failure, for any future binding path that similarly doesn't resolve.
    /// </summary>
    [Fact]
    public void LayoutPolicyBinding_MissingPath_FallsBackToDefault_DoesNotThrow()
    {
        StaTestRunner.Run(() =>
        {
            var panel = new AdaptiveIconGridPanel();
            var window = BuildOffscreenWindow(new object(), panel); // no .Model.IconLayoutPolicy path at all

            BindingOperations.SetBinding(panel, AdaptiveIconGridPanel.LayoutPolicyProperty, BuildTemplatesXamlLayoutPolicyBinding());

            try
            {
                window.Show();
                Assert.Equal(IconLayoutPolicy.LegacyStretch, panel.LayoutPolicy);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
