using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Views.Settings;

/// <summary>"Pages" settings section: list pages, add, remove (not "default"), make current, and inline-rename (ADR-043).</summary>
public partial class PagesSection : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="PagesSection"/> class.</summary>
    public PagesSection() => InitializeComponent();

    /// <summary>
    /// Double-clicking a page's name begins inline rename (ADR-043, docs/PLANS/page-display-names.md
    /// §5.1) — a pure view mechanic (XAML has no double-click gesture binding), the same category as
    /// <c>PanelsSection.OpenMenuOnLeftClick_Click</c>.
    /// </summary>
    private void PageNameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: PageItemViewModel page }) return;
        if (DataContext is not MainWindowViewModel viewModel) return;

        viewModel.BeginRenamePageCommand.Execute(page);
        e.Handled = true;
    }

    /// <summary>
    /// <c>F2</c> on a focused page row begins inline rename (ADR-043 §5.1) — mirrors
    /// <c>ChildWindow</c>'s own global F2 shortcut for the panel-item rename editor.
    /// </summary>
    /// <remarks>
    /// M-1 fix (backlog 1.x wave 1 review): <c>e.OriginalSource</c> is checked first —
    /// this handler is on the row's own <c>Border</c>, but <c>KeyDown</c> bubbles up to it from the
    /// in-progress rename <see cref="TextBox"/> too (the row stays <c>KeyDown</c>-subscribed the whole
    /// time it is <see cref="PageItemViewModel.IsEditing"/>). Without this guard, pressing <c>F2</c> while
    /// already typing in that <see cref="TextBox"/> — a perfectly normal keystroke to type, not a request
    /// to restart the rename — reached <c>MainWindowViewModel.BeginRenamePageCommand</c> again, which reset
    /// <see cref="PageItemViewModel.EditingName"/> straight back to its seed value, silently discarding
    /// whatever had already been typed. <c>MainWindowViewModel.BeginRenamePage</c>'s own
    /// <see cref="PageItemViewModel.IsEditing"/> guard is a second, independent line of defence for the
    /// same scenario (e.g. a future caller of this behavior with no <see cref="TextBox"/> in the mix).
    /// </remarks>
    private void PageRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        if (e.OriginalSource is TextBox) return;
        if (sender is not FrameworkElement { DataContext: PageItemViewModel page }) return;
        if (DataContext is not MainWindowViewModel viewModel) return;

        viewModel.BeginRenamePageCommand.Execute(page);
        e.Handled = true;
    }

    /// <summary>
    /// L-7 fix (backlog 1.x wave 1 review): a click anywhere in a page row's own background (as opposed
    /// to one of its child <c>Button</c>s/the rename <see cref="TextBox"/>, which already focus themselves
    /// as normal) moves keyboard focus to the row itself — see the row <c>Border</c>'s own XAML comment
    /// (<c>PagesSection.xaml</c>) for why <c>Background="Transparent"</c> had to be added alongside this
    /// handler for it to ever fire on an empty part of the row. Needed so <c>F2</c>
    /// (<see cref="PageRow_KeyDown"/>) has a row to target after a plain click that isn't itself a
    /// double-click (which already begins rename via <see cref="PageNameText_MouseLeftButtonDown"/>).
    /// </summary>
    private void PageRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row) row.Focus();
    }
}
