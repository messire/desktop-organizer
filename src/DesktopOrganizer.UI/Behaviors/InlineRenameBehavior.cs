using System.Windows.Controls;
using System.Windows.Threading;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Attached behavior wiring an inline-rename <see cref="TextBox"/> to a pair of commands (ADR-043,
/// docs/PLANS/page-display-names.md §5.2) — first consumer: <c>PagesSection.xaml</c>'s per-page name
/// editor. Generalizes <see cref="RenameEditBehavior"/>'s three behaviors (commit on Enter/lost focus,
/// cancel on Escape, focus + select-all when the editor becomes visible) for a caller that has its own
/// <see cref="ICommand"/> pair rather than a single, hard-coded ViewModel type to look up via
/// <c>Window.GetWindow(element)?.DataContext</c> — <see cref="RenameEditBehavior"/> itself is left
/// untouched (it is wired specifically to <c>ChildWindowViewModel</c>'s rename commands and used inside a
/// <c>WS_EX_NOACTIVATE</c> panel's own per-item <see cref="DataTemplate"/>; this
/// type has no such constraint — <c>PagesSection</c> is an ordinary, activatable settings window control).
/// Both commands are invoked with the <see cref="TextBox"/>'s own <c>DataContext</c> as the command
/// parameter — the item a <see cref="System.Windows.Controls.ItemsControl.ItemTemplate"/> instantiated this
/// editor for (e.g. a <c>PageItemViewModel</c>) — so the bound command can be a simple
/// <c>RelayCommand&lt;T&gt;</c> without needing a separate "which row" lookup of its own.
/// </summary>
public static class InlineRenameBehavior
{
    /// <summary>
    /// When set to <see langword="true"/> on a <see cref="TextBox"/>, wires it up as an inline-rename
    /// editor (see remarks on <see cref="InlineRenameBehavior"/>).
    /// </summary>
    public static readonly DependencyProperty IsRenameEditorProperty =
        DependencyProperty.RegisterAttached(
            "IsRenameEditor",
            typeof(bool),
            typeof(InlineRenameBehavior),
            new PropertyMetadata(false, OnIsRenameEditorChanged));

    /// <summary>
    /// The command executed when the edit is committed by pressing Enter. Invoked with the
    /// <see cref="TextBox"/>'s own <c>DataContext</c> as the parameter. See
    /// <see cref="CommitOnLostFocusCommandProperty"/> for the (optionally distinct) lost-focus commit
    /// command — UX-4 fix, backlog 1.x wave 1 review.
    /// </summary>
    public static readonly DependencyProperty CommitCommandProperty =
        DependencyProperty.RegisterAttached("CommitCommand", typeof(ICommand), typeof(InlineRenameBehavior));

    /// <summary>
    /// The command executed when the edit is committed by the editor losing keyboard focus (clicking
    /// elsewhere, Tab, Alt+Tab, etc.) — UX-4 fix, backlog 1.x wave 1 review. Invoked with the
    /// <see cref="TextBox"/>'s own <c>DataContext</c> as the parameter. Optional: when left unset, falls
    /// back to <see cref="CommitCommandProperty"/> (see <see cref="OnLostKeyboardFocus"/>'s remarks for
    /// why a caller might want a distinct command here — <c>PagesSection.xaml</c>, the first consumer,
    /// does).
    /// </summary>
    public static readonly DependencyProperty CommitOnLostFocusCommandProperty =
        DependencyProperty.RegisterAttached("CommitOnLostFocusCommand", typeof(ICommand), typeof(InlineRenameBehavior));

    /// <summary>
    /// The command executed when the edit is cancelled (Escape). Invoked with the <see cref="TextBox"/>'s
    /// own <c>DataContext</c> as the parameter.
    /// </summary>
    public static readonly DependencyProperty CancelCommandProperty =
        DependencyProperty.RegisterAttached("CancelCommand", typeof(ICommand), typeof(InlineRenameBehavior));

    /// <summary>Gets the value of the <see cref="IsRenameEditorProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to read the property from.</param>
    public static bool GetIsRenameEditor(DependencyObject obj) => (bool)obj.GetValue(IsRenameEditorProperty);

    /// <summary>Sets the value of the <see cref="IsRenameEditorProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to wire up (or unwire) as an inline-rename editor.</param>
    /// <param name="value"><see langword="true"/> to wire this <see cref="TextBox"/> up as a rename editor; <see langword="false"/> to remove the wiring.</param>
    public static void SetIsRenameEditor(DependencyObject obj, bool value) => obj.SetValue(IsRenameEditorProperty, value);

    /// <summary>Gets the value of the <see cref="CommitCommandProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to read the property from.</param>
    public static ICommand? GetCommitCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CommitCommandProperty);

    /// <summary>Sets the value of the <see cref="CommitCommandProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to set the command on.</param>
    /// <param name="value">The command to execute on commit.</param>
    public static void SetCommitCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CommitCommandProperty, value);

    /// <summary>Gets the value of the <see cref="CommitOnLostFocusCommandProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to read the property from.</param>
    public static ICommand? GetCommitOnLostFocusCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CommitOnLostFocusCommandProperty);

    /// <summary>Sets the value of the <see cref="CommitOnLostFocusCommandProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to set the command on.</param>
    /// <param name="value">The command to execute when the edit is committed by losing keyboard focus.</param>
    public static void SetCommitOnLostFocusCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CommitOnLostFocusCommandProperty, value);

    /// <summary>Gets the value of the <see cref="CancelCommandProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to read the property from.</param>
    public static ICommand? GetCancelCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CancelCommandProperty);

    /// <summary>Sets the value of the <see cref="CancelCommandProperty"/> attached property.</summary>
    /// <param name="obj">The <see cref="TextBox"/> to set the command on.</param>
    /// <param name="value">The command to execute on cancel.</param>
    public static void SetCancelCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CancelCommandProperty, value);

    private static void OnIsRenameEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if ((bool)e.NewValue)
        {
            textBox.IsVisibleChanged += OnIsVisibleChanged;
            textBox.PreviewKeyDown += OnPreviewKeyDown;
            textBox.LostKeyboardFocus += OnLostKeyboardFocus;
        }
        else
        {
            textBox.IsVisibleChanged -= OnIsVisibleChanged;
            textBox.PreviewKeyDown -= OnPreviewKeyDown;
            textBox.LostKeyboardFocus -= OnLostKeyboardFocus;
        }
    }

    /// <summary>
    /// Focuses and selects all text the moment the editor becomes visible (mirrors
    /// <see cref="RenameEditBehavior"/>'s own <c>OnIsVisibleChanged</c>) — deferred to
    /// <see cref="DispatcherPriority.Input"/> because the visibility flip and the layout pass that
    /// actually makes the element focusable are not synchronous.
    /// </summary>
    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } textBox) return;

        textBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }));
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        switch (e.Key)
        {
            case Key.Enter:
                GetCommitCommand(textBox)?.Execute(textBox.DataContext);
                e.Handled = true;
                break;
            case Key.Escape:
                GetCancelCommand(textBox)?.Execute(textBox.DataContext);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Commits on losing keyboard focus (clicking elsewhere, Tab, Alt+Tab, etc.) — mirrors
    /// <see cref="RenameEditBehavior"/>'s own <c>OnLostKeyboardFocus</c>, but invokes
    /// <see cref="CommitOnLostFocusCommandProperty"/> (falling back to <see cref="CommitCommandProperty"/>
    /// when unset) rather than always reusing the Enter-path command.
    /// </summary>
    /// <remarks>
    /// UX-4 fix (backlog 1.x wave 1 review) — replaces this behavior's previous choice of always leaving
    /// a failed commit's editor open, regardless of what triggered it: a failed commit triggered by focus
    /// loss left the row stuck <c>IsEditing</c> with no keyboard focus and no caret in its
    /// <see cref="TextBox"/> — a dead end a user could only escape by clicking back into the very row
    /// that just rejected their input, since the row's Rename/"Make current"/Remove buttons are also
    /// hidden while <c>IsEditing</c> (<c>PagesSection.xaml</c>'s own <c>Pages.RowButton</c> style). The
    /// bound <see cref="CommitOnLostFocusCommandProperty"/> command (<c>MainWindowViewModel</c>'s
    /// <c>CommitRenamePageOnLostFocus</c>, the first consumer) instead auto-cancels the edit on a failed
    /// lost-focus commit, closing the row back to view mode; an Enter-triggered failed commit
    /// (<see cref="OnPreviewKeyDown"/>, unchanged) still leaves the editor open instead, since the user is
    /// still right there, mid-keystroke, and can just fix the text and retry.
    /// </remarks>
    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        (GetCommitOnLostFocusCommand(textBox) ?? GetCommitCommand(textBox))?.Execute(textBox.DataContext);
    }
}
