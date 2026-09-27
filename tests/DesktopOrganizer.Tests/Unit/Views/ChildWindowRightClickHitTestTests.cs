using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.Core;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Unit tests for <see cref="ChildWindow.IsInlineRenameEditorHit"/> and
/// <see cref="ChildWindow.ResolveRightClickedFileItem"/> — the two pure, testable decision
/// helpers <c>ChildWindow.FileListHost_PreviewMouseRightButtonUp</c> (UX parity fix,
/// fences-ux-parity-reviewer, release-polish 1.0 review) uses to tell an item right-click apart
/// from the EMPTY panel body (which now opens the fence menu) and from the inline-rename editor's
/// own edit menu.
/// </summary>
/// <remarks>
/// Runs on a dedicated STA thread (<see cref="StaTestRunner"/>) — a real, minimal
/// <see cref="ListBox"/>/<see cref="ListBoxItem"/> visual tree, same convention
/// <c>ListReorderGeometryHitTestTests</c> already establishes for this project. The ListBox bypasses
/// the OS/theme default template (a minimal <see cref="ItemsPresenter"/>-only one instead), same
/// "smallest real template it needs, skip the theme" approach used there.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowRightClickHitTestTests
{
    // ── IsInlineRenameEditorHit ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A click landing directly on the inline-rename <see cref="TextBox"/> itself (the routed event's
    /// <c>OriginalSource</c> is commonly the TextBox's own rendered surface) is recognized as the
    /// editor's own edit menu.
    /// </summary>
    [Fact]
    public void IsInlineRenameEditorHit_SourceIsTheTextBoxItself_ReturnsTrue()
    {
        StaTestRunner.Run(() =>
        {
            var textBox = new TextBox();

            Assert.True(ChildWindow.IsInlineRenameEditorHit(textBox));
        });
    }

    /// <summary>
    /// A click landing on a real DESCENDANT of the inline-rename <see cref="TextBox"/> (e.g. its
    /// internal text-rendering surface) still walks up to the <see cref="TextBox"/> ancestor. Gives the
    /// TextBox an explicit, minimal <see cref="ControlTemplate"/> — same "smallest real template it
    /// needs, skip the theme" approach as <see cref="BuildListBox"/> below — rather than relying on the
    /// OS/theme default one, which does not reliably materialize in this headless test host (no running
    /// <see cref="System.Windows.Application"/>/theme dictionaries loaded).
    /// </summary>
    [Fact]
    public void IsInlineRenameEditorHit_SourceIsADescendantOfTheTextBox_ReturnsTrue()
    {
        StaTestRunner.Run(() =>
        {
            var textBox = new TextBox
            {
                Template = new ControlTemplate(typeof(TextBox))
                {
                    VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.Grid)),
                },
            };
            textBox.ApplyTemplate();

            Assert.True(
                System.Windows.Media.VisualTreeHelper.GetChildrenCount(textBox) > 0,
                "sanity: the TextBox's own (explicit, minimal) control template must have built a real visual tree.");
            var descendant = System.Windows.Media.VisualTreeHelper.GetChild(textBox, 0);

            Assert.True(ChildWindow.IsInlineRenameEditorHit(descendant));
        });
    }

    /// <summary>
    /// A click that never crosses a <see cref="TextBox"/> ancestor at all (e.g. landing on a plain
    /// panel-body element) is not mistaken for the inline-rename editor.
    /// </summary>
    [Fact]
    public void IsInlineRenameEditorHit_NoTextBoxAncestor_ReturnsFalse()
    {
        StaTestRunner.Run(() =>
        {
            var emptyAreaElement = new System.Windows.Controls.Grid();

            Assert.False(ChildWindow.IsInlineRenameEditorHit(emptyAreaElement));
        });
    }

    // ── IsScrollBarHit (MINOR 9, release-polish 1.0 final review) ────────────────────────────

    /// <summary>
    /// A click landing directly on a <see cref="ScrollBar"/> is recognized so the caller leaves it alone
    /// instead of popping the fence's <c>HeaderContextMenu</c> over it.
    /// </summary>
    [Fact]
    public void IsScrollBarHit_SourceIsTheScrollBarItself_ReturnsTrue()
    {
        StaTestRunner.Run(() =>
        {
            var scrollBar = new ScrollBar();

            Assert.True(ChildWindow.IsScrollBarHit(scrollBar));
        });
    }

    /// <summary>
    /// A click landing on a real DESCENDANT of a <see cref="ScrollBar"/> (its internal thumb/track visuals)
    /// still walks up to the <see cref="ScrollBar"/> ancestor.
    /// </summary>
    [Fact]
    public void IsScrollBarHit_SourceIsADescendantOfTheScrollBar_ReturnsTrue()
    {
        StaTestRunner.Run(() =>
        {
            var scrollBar = new ScrollBar
            {
                Template = new ControlTemplate(typeof(ScrollBar))
                {
                    VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.Grid)),
                },
            };
            scrollBar.ApplyTemplate();

            Assert.True(
                System.Windows.Media.VisualTreeHelper.GetChildrenCount(scrollBar) > 0,
                "sanity: the ScrollBar's own (explicit, minimal) control template must have built a real visual tree.");
            var descendant = System.Windows.Media.VisualTreeHelper.GetChild(scrollBar, 0);

            Assert.True(ChildWindow.IsScrollBarHit(descendant));
        });
    }

    /// <summary>
    /// A click that never crosses a <see cref="ScrollBar"/> ancestor at all is not mistaken for one.
    /// </summary>
    [Fact]
    public void IsScrollBarHit_NoScrollBarAncestor_ReturnsFalse()
    {
        StaTestRunner.Run(() =>
        {
            var emptyAreaElement = new System.Windows.Controls.Grid();

            Assert.False(ChildWindow.IsScrollBarHit(emptyAreaElement));
        });
    }

    // ── ResolveRightClickedFileItem ──────────────────────────────────────────────────────────

    /// <summary>
    /// A click landing on a real descendant inside an item's <see cref="ListBoxItem"/> container
    /// (the DataTemplate's own content, not the container itself) resolves to that item's
    /// <see cref="FileItem"/> — the "right-click on an item" case.
    /// </summary>
    [Fact]
    public void ResolveRightClickedFileItem_DescendantOfAnItemContainer_ResolvesToThatItem()
    {
        StaTestRunner.Run(() =>
        {
            var fileItemA = new FileItem(@"C:\fake\a.txt", null);
            var fileItemB = new FileItem(@"C:\fake\b.txt", null);
            var listBox = BuildListBox(fileItemA, fileItemB);
            MeasureAndArrange(listBox);

            var containerB = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromItem(fileItemB)!;
            var descendant = FindVisualChild<TextBlock>(containerB);
            Assert.NotNull(descendant); // sanity: the item template actually built a real visual tree.

            var resolved = ChildWindow.ResolveRightClickedFileItem(descendant!);

            Assert.Same(fileItemB, resolved);
        });
    }

    /// <summary>
    /// A click landing on the EMPTY panel body — no <see cref="ListBoxItem"/> ancestor anywhere in the
    /// visual tree up from the click's source — resolves to <see langword="null"/>, so
    /// <c>ChildWindow.FileListHost_PreviewMouseRightButtonUp</c> opens the fence menu instead of
    /// the native shell item menu (UX parity fix).
    /// </summary>
    [Fact]
    public void ResolveRightClickedFileItem_EmptyPanelBody_ReturnsNull()
    {
        StaTestRunner.Run(() =>
        {
            var fileItemA = new FileItem(@"C:\fake\a.txt", null);
            var listBox = BuildListBox(fileItemA);
            MeasureAndArrange(listBox);

            // The ListBox's own root (below every item container) stands in for whitespace within
            // FileListHost that never crosses a ListBoxItem ancestor.
            var resolved = ChildWindow.ResolveRightClickedFileItem(listBox);

            Assert.Null(resolved);
        });
    }

    /// <summary>
    /// A <see cref="ListBoxItem"/> container whose <c>DataContext</c> is not (or not yet) a
    /// <see cref="FileItem"/> — defensive: never seen in production (the ListBox is only ever bound to
    /// <c>FileItem</c>s) but must not throw or mis-resolve — also resolves to <see langword="null"/>,
    /// same as a genuine empty-area miss.
    /// </summary>
    [Fact]
    public void ResolveRightClickedFileItem_ContainerWithNonFileItemDataContext_ReturnsNull()
    {
        StaTestRunner.Run(() =>
        {
            var oddContainer = new ListBoxItem { DataContext = "not a FileItem" };

            var resolved = ChildWindow.ResolveRightClickedFileItem(oddContainer);

            Assert.Null(resolved);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static ListBox BuildListBox(params FileItem[] items)
    {
        var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
        textBlockFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FileItem.DisplayName)));

        // Explicit, minimal ListBoxItem container template (a bare ContentPresenter, wired to the
        // container's own Content/ContentTemplate via TemplateBinding) — bypasses the OS/theme default
        // ListBoxItem template, which does not reliably materialize its ContentPresenter far enough to
        // actually render the ItemTemplate below in this headless test host (no running
        // System.Windows.Application/theme dictionaries loaded). Same "smallest real template it needs,
        // skip the theme" approach as the ListBox's own Template below.
        var contentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenterFactory.SetValue(
            ContentPresenter.ContentSourceProperty, "Content");
        var itemContainerStyle = new Style(typeof(ListBoxItem));
        itemContainerStyle.Setters.Add(new Setter(
            Control.TemplateProperty,
            new ControlTemplate(typeof(ListBoxItem)) { VisualTree = contentPresenterFactory }));

        var listBox = new ListBox
        {
            ItemTemplate = new DataTemplate { VisualTree = textBlockFactory },
            ItemContainerStyle = itemContainerStyle,

            // Bypasses the OS/theme default ControlTemplate — same minimal "just an ItemsPresenter"
            // template ListReorderGeometryHitTestTests.BuildFixedGapListBox uses, for the same reason
            // (no running Application/theme dictionaries loaded in this headless test host).
            Template = new ControlTemplate(typeof(ListBox))
            {
                VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)),
            },
        };

        foreach (var item in items)
            listBox.Items.Add(item);

        return listBox;
    }

    private static void MeasureAndArrange(ListBox listBox)
    {
        listBox.Measure(new Size(300, double.PositiveInfinity));
        listBox.Arrange(new Rect(0, 0, 300, listBox.DesiredSize.Height));
        listBox.UpdateLayout();
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is { } found) return found;
        }

        return null;
    }
}
