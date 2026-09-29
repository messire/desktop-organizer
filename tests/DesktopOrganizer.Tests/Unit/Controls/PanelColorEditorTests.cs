using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// Unit tests for <see cref="PanelColorEditor"/> (backlog 1.x item 5, §5.2/§5.5 "simplified colour
/// editor"): the "Advanced" <see cref="Expander"/> is collapsed by default, its
/// <see cref="PanelColorEditor.AdvancedExtraContent"/> slot actually renders whatever a host supplies, and
/// expanding/collapsing that card never touches the bound <c>PanelColorEditorViewModel</c>'s own colour
/// data — it is a purely visual, in-memory <see cref="Expander.IsExpanded"/> toggle (CLAUDE.md persistence
/// freeze: no new setting backs it).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelColorEditorTests
{
    /// <summary>The Advanced card starts collapsed — the swatch grid (the "one obvious path", §5.6 DoD 1) is the only thing visible by default.</summary>
    [Fact]
    public void AdvancedExpander_IsCollapsedByDefault()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var editor = BuildEditor();
            var window = new Window { Content = editor, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            var expander = FindVisualDescendants<Expander>(editor).Single();
            Assert.False(expander.IsExpanded);

            window.Close();
        });
    }

    /// <summary>
    /// A host-supplied <see cref="PanelColorEditor.AdvancedExtraContent"/> (backlog 1.x item 5 §5.2: where
    /// <c>PanelCustomizeWindow</c>/<c>NewPanelDefaultsSection</c> inject their own surface-style/opacity
    /// controls) actually renders into the Advanced card's visual tree once expanded. <see cref="Expander"/>'s
    /// own body <see cref="ContentPresenter"/> (<c>Settings.CardExpander</c>) only materializes ITS visual
    /// child during a <c>Measure</c> pass, which WPF skips entirely for a <see cref="UIElement.Visibility"/>
    /// of <see cref="Visibility.Collapsed"/> — so this control's own nested
    /// <see cref="PanelColorEditor.AdvancedExtraContent"/> <see cref="ContentPresenter"/> is genuinely
    /// unrealized (not merely invisible) until the card is expanded, same as every other Advanced-card
    /// control.
    /// </summary>
    [Fact]
    public void AdvancedExtraContent_RendersSuppliedContent()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var marker = new Button { Name = "AdvancedExtraMarker", Content = "Extra" };
            var editor = BuildEditor();
            editor.AdvancedExtraContent = marker;

            var window = new Window { Content = editor, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            FindVisualDescendants<Expander>(editor).Single().IsExpanded = true;
            window.UpdateLayout();

            var found = FindVisualDescendants<Button>(editor).SingleOrDefault(b => b.Name == "AdvancedExtraMarker");
            Assert.NotNull(found);
            Assert.Same(marker, found);

            window.Close();
        });
    }

    /// <summary>
    /// Backlog 1.x wave 2 review (M-G): a host-supplied <see cref="PanelColorEditor.AdvancedExtraContent"/>
    /// is a genuine <see cref="LogicalTreeHelper"/> child of the <see cref="PanelColorEditor"/> as soon as
    /// it is assigned — independently of whether the control has ever been shown/measured, unlike the
    /// VISUAL-tree materialization <see cref="AdvancedExtraContent_RendersSuppliedContent"/> covers.
    /// Clearing it back to <see langword="null"/> removes it again.
    /// </summary>
    [Fact]
    public void AdvancedExtraContent_IsRegisteredAsALogicalChild()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var marker = new Button { Name = "AdvancedExtraMarker", Content = "Extra" };
            var editor = BuildEditor();

            Assert.DoesNotContain(marker, LogicalTreeHelper.GetChildren(editor).OfType<Button>());

            editor.AdvancedExtraContent = marker;
            Assert.Contains(marker, LogicalTreeHelper.GetChildren(editor).OfType<Button>());
            Assert.Same(editor, LogicalTreeHelper.GetParent(marker));

            editor.AdvancedExtraContent = null;
            Assert.DoesNotContain(marker, LogicalTreeHelper.GetChildren(editor).OfType<Button>());
            Assert.Null(LogicalTreeHelper.GetParent(marker));
        });
    }

    /// <summary>
    /// <see cref="PanelColorEditor.AdvancedExtraContent"/> defaults to <see langword="null"/> — a host that
    /// doesn't supply one (there is none today, but the slot must degrade gracefully) never throws laying
    /// out the control.
    /// </summary>
    [Fact]
    public void AdvancedExtraContent_Default_IsNullAndDoesNotThrow()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var editor = BuildEditor();
            Assert.Null(editor.AdvancedExtraContent);

            var exception = Record.Exception(() =>
            {
                var window = new Window { Content = editor, ShowActivated = false, Left = -5000, Top = -5000 };
                window.Show();
                window.UpdateLayout();
                window.Close();
            });

            Assert.Null(exception);
        });
    }

    /// <summary>
    /// Expanding, then collapsing, the Advanced card is a purely visual toggle — it never calls any of the
    /// owning ViewModel's read/write delegates (no new "in-memory" state was added to
    /// <c>PanelColorEditorViewModel</c> itself for this — see this control's own remarks).
    /// </summary>
    [Fact]
    public void TogglingAdvancedExpander_NeverChangesTheOwnersColours()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var accent = "#3F7FD9";
            var text = "#E1E7EF";
            var writes = 0;
            var vm = new PanelColorEditorViewModel(
                Mock.Of<IColorPickerService>(),
                () => accent,
                _ => writes++,
                () => text,
                _ => writes++,
                (_, _) => writes++);

            var editor = new PanelColorEditor { DataContext = vm };
            var window = new Window { Content = editor, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            var expander = FindVisualDescendants<Expander>(editor).Single();

            expander.IsExpanded = true;
            window.UpdateLayout();
            expander.IsExpanded = false;
            window.UpdateLayout();

            Assert.Equal(0, writes);
            Assert.Equal(accent, vm.AccentColor);
            Assert.Equal(text, vm.TextColor);

            window.Close();
        });
    }

    private static PanelColorEditor BuildEditor()
    {
        var accent = "#3F7FD9";
        var text = "#E1E7EF";
        var vm = new PanelColorEditorViewModel(
            Mock.Of<IColorPickerService>(),
            () => accent,
            _ => { },
            () => text,
            _ => { },
            (_, _) => { });

        return new PanelColorEditor { DataContext = vm };
    }

    /// <summary>
    /// Recursively enumerates every VISUAL-tree descendant of <paramref name="root"/> assignable to
    /// <typeparamref name="T"/> — needed here (unlike most of this project's other control tests, which use
    /// <see cref="System.Windows.LogicalTreeHelper"/>) because <see cref="PanelColorEditor.AdvancedExtraContent"/>
    /// is displayed through a <see cref="ContentPresenter"/> whose own content only materializes VISUALLY
    /// once a layout pass runs (e.g. once the Advanced <see cref="Expander"/> that hosts it is expanded —
    /// its <c>ContentPresenter</c> skips <c>Measure</c> entirely while <see cref="UIElement.Visibility"/> is
    /// <see cref="Visibility.Collapsed"/>). <see cref="PanelColorEditor.AdvancedExtraContent"/> IS registered as a genuine
    /// LOGICAL child of this control (backlog 1.x wave 2 review M-G — see that property's own remarks,
    /// <c>PanelColorEditor.xaml.cs</c>) independently of whether it has ever been shown/measured; this
    /// helper still uses the visual tree because most of the assertions below care about actual rendered
    /// state, not merely logical-tree reachability.
    /// </summary>
    /// <typeparam name="T">The descendant type to find.</typeparam>
    /// <param name="root">The visual-tree root to search from (exclusive of itself).</param>
    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) yield return match;

            foreach (var nested in FindVisualDescendants<T>(child))
                yield return nested;
        }
    }
}
