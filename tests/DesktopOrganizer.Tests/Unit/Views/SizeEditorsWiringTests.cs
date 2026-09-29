using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;
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
    /// Backlog 1.x item 5 (§5.2/§5.6 DoD 3, superseding UX#3's "Surface card comes before Icon size card"
    /// ordering check — that stand-alone card no longer exists): <c>NewPanelDefaultsSection</c> itself must
    /// NOT host a Surface style/Opacity card of its own any more — those controls live inside the shared
    /// <see cref="PanelColorEditor"/>'s collapsed-by-default "Advanced" card via
    /// <see cref="PanelColorEditor.AdvancedExtraContent"/>, so they are no longer duplicated markup between
    /// this section and <c>PanelCustomizeWindow.xaml</c> (which wires the identical content the same way).
    /// Backlog 1.x wave 2 review (M-G): that content is now the single shared
    /// <see cref="SurfaceOptionsEditor"/> control (rather than each host's own inline copy of its markup) —
    /// this test now also pins that <see cref="PanelColorEditor.AdvancedExtraContent"/> reachable via
    /// <see cref="LogicalTreeHelper"/> (M-G registers it as a real logical child — see
    /// <c>PanelColorEditor.xaml.cs</c>'s own remarks), not merely via the
    /// <see cref="PanelColorEditor.AdvancedExtraContent"/> property's own value.
    /// </summary>
    [Fact]
    public void NewPanelDefaultsSection_SurfaceControls_LiveInsideASharedSurfaceOptionsEditor_NotAsOwnCard()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var section = new NewPanelDefaultsSection();

            var editor = FindLogicalDescendants<PanelColorEditor>(section).Single();
            var surfaceEditor = Assert.IsType<SurfaceOptionsEditor>(editor.AdvancedExtraContent);

            // Exactly one SurfaceOptionsEditor anywhere in the section's logical tree — it lives inside
            // PanelColorEditor's AdvancedExtraContent (a real logical child as of M-G), not as a separate
            // stand-alone card of the section's own.
            Assert.Same(surfaceEditor, FindLogicalDescendants<SurfaceOptionsEditor>(section).Single());

            var surfaceRadios = FindLogicalDescendants<RadioButton>(surfaceEditor)
                .Where(r => r.GroupName == "SurfaceOptionsEditor.SurfaceStyle")
                .ToList();
            Assert.Equal(3, surfaceRadios.Count);
            Assert.Contains(FindLogicalDescendants<Slider>(surfaceEditor), s => s.Minimum.Equals(SurfaceComposer.MinSurfaceOpacity));
        });
    }

    /// <summary>
    /// Backlog 1.x item 5/wave 2 review M-G: the shared <see cref="SurfaceOptionsEditor"/>'s Surface
    /// style/Opacity controls resolve <see cref="AutomationProperties.LabeledByProperty"/> to the right
    /// label via ordinary <c>ElementName</c> bindings — no longer the "Path=Children[0]" tree-position
    /// workaround the inline markup this control replaced needed (MC3093 no longer applies: this control
    /// has its own dedicated compiled NameScope — see its own XAML remarks). Exercises the REAL production
    /// markup end-to-end (Show + expand), not just a synthetic analogue of the technique.
    /// </summary>
    [Fact]
    public void NewPanelDefaultsSection_SurfaceControls_LabeledByResolvesToTheCorrectLabel()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var section = new NewPanelDefaultsSection();
            var window = new Window { Content = section, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            // The Advanced card's body only materializes its visual children once expanded (Settings.
            // CardExpander's own ContentPresenter skips Measure while Collapsed) — see PanelColorEditorTests'
            // own remarks for the identical finding.
            FindVisualDescendants<Expander>(section).Single(e => e.Style == (Style)Application.Current.Resources["Settings.CardExpander"]).IsExpanded = true;
            window.UpdateLayout();

            var surfaceRadios = FindVisualDescendants<RadioButton>(section).Where(r => r.GroupName == "SurfaceOptionsEditor.SurfaceStyle").ToList();
            Assert.Equal(3, surfaceRadios.Count);
            foreach (var radio in surfaceRadios)
            {
                var label = Assert.IsType<TextBlock>(AutomationProperties.GetLabeledBy(radio));
                Assert.Equal("Style", label.Text);
            }

            var opacitySlider = FindVisualDescendants<Slider>(section).Single(s => s.Minimum.Equals(SurfaceComposer.MinSurfaceOpacity));
            var opacityLabel = Assert.IsType<TextBlock>(AutomationProperties.GetLabeledBy(opacitySlider));
            Assert.Equal("Opacity", opacityLabel.Text);

            window.Close();
        });
    }

    /// <summary>
    /// Recursively enumerates every VISUAL-tree descendant of <paramref name="root"/> assignable to
    /// <typeparamref name="T"/> — needed (unlike <see cref="FindLogicalDescendants{T}"/>) wherever content is
    /// only realized during layout, e.g. anything inside a collapsed <c>Settings.CardExpander</c> or a
    /// <see cref="PanelColorEditor.AdvancedExtraContent"/> <see cref="ContentPresenter"/> (see this file's
    /// own remarks above).
    /// </summary>
    /// <typeparam name="T">The descendant type to find.</typeparam>
    /// <param name="root">The visual-tree root to search from (exclusive of itself).</param>
    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T match) yield return match;

            foreach (var nested in FindVisualDescendants<T>(child))
                yield return nested;
        }
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
