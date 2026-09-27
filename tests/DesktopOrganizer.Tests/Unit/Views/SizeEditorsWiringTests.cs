using System.Windows;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views.Settings;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Regression tests for the two settings-window sections that host a <see cref="SteppedValueEditor"/>
/// (docs/PLANS/color-presets-and-size-step.md §5.4/§7 T-14) — pins that each one is actually wired to
/// <see cref="SizeStepPolicy"/>'s icon-size range, not a stray hand-written literal (e.g. a leftover
/// <c>Maximum="96"</c> from before this feature), and that <c>NewPanelDefaultsSection</c> hosts the shared
/// <see cref="PanelColorEditor"/>. <c>PanelCustomizeWindow</c>'s own <c>IconSizeEditor</c> — the third of
/// the plan's "three XAML places" — is covered by <c>PanelCustomizeWindowClosingTests</c> instead, which
/// already builds a real, shown instance for its own <c>OnClosing</c> coverage.
/// </summary>
/// <remarks>
/// Constructed the same way as <c>SettingsSectionsInitializeTests</c> (plain <c>Activator.CreateInstance</c>
/// under <see cref="WpfApplicationHost.EnsureApplication"/>, no hosting window, no <c>DataContext</c>) —
/// sufficient here because every <see cref="SteppedValueEditor.Minimum"/>/<see cref="SteppedValueEditor.Maximum"/>
/// value under test is set via <c>{x:Static}</c> in XAML, resolved at parse time independent of any
/// <c>DataContext</c> or <c>Loaded</c> pass.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SizeEditorsWiringTests
{
    /// <summary>
    /// Verifies that <c>LayoutSnappingSection</c>'s single <see cref="SteppedValueEditor"/> (the grid-step
    /// card) is wired to <see cref="SizeStepPolicy.GridStepMinimum"/>/<see cref="SizeStepPolicy.GridStepMaximum"/>/
    /// <see cref="SizeStepPolicy.Step"/> — not a leftover <c>Maximum="96"</c>/<c>TickFrequency="4"</c> from
    /// the pre-feature <see cref="System.Windows.Controls.Slider"/> this replaced.
    /// </summary>
    [Fact]
    public void LayoutSnappingSection_SteppedValueEditor_UsesGridStepRange()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var section = new LayoutSnappingSection();
            var editor = FindLogicalDescendants<SteppedValueEditor>(section).Single();

            // Every DependencyObject touched above (including editor's own DP reads below) belongs to
            // THIS STA worker thread — asserting outside this callback would read them from the xUnit
            // thread instead, which WPF's own thread-affinity check rejects outright.
            Assert.Equal(SizeStepPolicy.GridStepMinimum, editor.Minimum);
            Assert.Equal(SizeStepPolicy.GridStepMaximum, editor.Maximum);
            Assert.Equal(SizeStepPolicy.Step, editor.Step);
        });
    }

    /// <summary>
    /// Verifies that <c>NewPanelDefaultsSection</c>'s single <see cref="SteppedValueEditor"/> (the icon-size
    /// card) is wired to <see cref="SizeStepPolicy.IconSizeMinimum"/>/<see cref="SizeStepPolicy.IconSizeMaximum"/>/
    /// <see cref="SizeStepPolicy.Step"/>, and that the section also hosts exactly one shared
    /// <see cref="PanelColorEditor"/> (the Colors card).
    /// </summary>
    [Fact]
    public void NewPanelDefaultsSection_SteppedValueEditor_UsesIconSizeRange_AndHostsPanelColorEditor()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var section = new NewPanelDefaultsSection();

            var editor = FindLogicalDescendants<SteppedValueEditor>(section).Single();
            Assert.Equal(SizeStepPolicy.IconSizeMinimum, editor.Minimum);
            Assert.Equal(SizeStepPolicy.IconSizeMaximum, editor.Maximum);
            Assert.Equal(SizeStepPolicy.Step, editor.Step);

            Assert.Single(FindLogicalDescendants<PanelColorEditor>(section));
        });
    }

    /// <summary>
    /// UX#3 (release-polish-1.0 visual review): <c>NewPanelDefaultsSection</c>'s Surface card comes BEFORE
    /// its Icon size card — matching <c>PanelCustomizeWindow</c>'s own card order — where this section used
    /// to have them reversed. Checked via document order in the logical tree: the Surface card's own
    /// <c>OpacityLabel</c>-named element must appear before the icon-size <see cref="SteppedValueEditor"/>.
    /// </summary>
    [Fact]
    public void NewPanelDefaultsSection_SurfaceCard_ComesBeforeIconSizeCard()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var section = new NewPanelDefaultsSection();

            var elements = FindLogicalDescendants<FrameworkElement>(section).ToList();
            var opacityLabelIndex = elements.FindIndex(e => e.Name == "OpacityLabel");
            var iconSizeEditorIndex = elements.FindIndex(e => e is SteppedValueEditor);

            Assert.True(opacityLabelIndex >= 0, "OpacityLabel not found.");
            Assert.True(iconSizeEditorIndex >= 0, "SteppedValueEditor not found.");
            Assert.True(opacityLabelIndex < iconSizeEditorIndex, "Surface card must come before the Icon size card.");
        });
    }

    /// <summary>
    /// Recursively enumerates every logical-tree descendant of <paramref name="root"/> assignable to
    /// <typeparamref name="T"/> — <see cref="LogicalTreeHelper.GetChildren(DependencyObject)"/> walks a
    /// <see cref="System.Windows.Controls.UserControl"/>'s content (<see cref="System.Windows.Controls.Panel"/>/<see cref="System.Windows.Controls.Border"/>/etc.)
    /// without requiring the control to ever have been shown or measured, unlike
    /// <see cref="System.Windows.Media.VisualTreeHelper"/>.
    /// </summary>
    /// <typeparam name="T">The descendant type to find.</typeparam>
    /// <param name="root">The logical-tree root to search from (exclusive of itself).</param>
    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;

            if (node is T match) yield return match;

            foreach (var nested in FindLogicalDescendants<T>(node))
                yield return nested;
        }
    }
}
