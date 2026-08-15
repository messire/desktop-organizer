using System.Windows.Controls;
using System.Windows.Threading;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Attached behavior that wires an inline-rename <see cref="TextBox"/> declared in
/// <c>Resources/Templates.xaml</c> to <see cref="ChildWindowViewModel"/>'s rename commands.
/// </summary>
/// <remarks>
/// <c>Templates.xaml</c> is a plain <see cref="ResourceDictionary"/> with no <c>x:Class</c>, so — unlike
/// <c>ChildWindow.xaml.cs</c>'s <c>TitleEditBox_KeyDown</c>/<c>TitleEditBox_LostKeyboardFocus</c>, which
/// back the panel-title inline editor — it cannot host XAML event-handler attributes (<c>KeyDown="..."</c>);
/// there is no partial class for the XAML compiler to wire them into. It also cannot use a single
/// <c>x:Name</c> + code-behind field the way <c>ChildWindow.xaml.cs</c> does for <c>TitleEditBox</c>,
/// because the file-item <see cref="TextBox"/> lives inside a <see cref="DataTemplate"/> and is
/// instantiated once per panel item, not once per window. This attached property reproduces the same
/// three behaviors (commit on Enter/lost focus, cancel on Escape, focus + select-all when the editor
/// becomes visible) entirely in code, attached declaratively from XAML via one property.
/// </remarks>
public static class RenameEditBehavior
{
    /// <summary>
    /// When set to <see langword="true"/> on a <see cref="TextBox"/>, wires it up as an inline-rename
    /// editor (see remarks on <see cref="RenameEditBehavior"/>).
    /// </summary>
    public static readonly DependencyProperty IsRenameEditorProperty =
        DependencyProperty.RegisterAttached(
            "IsRenameEditor",
            typeof(bool),
            typeof(RenameEditBehavior),
            new PropertyMetadata(false, OnIsRenameEditorChanged));

    /// <summary>Gets the value of the <see cref="IsRenameEditorProperty"/> attached property.</summary>
    public static bool GetIsRenameEditor(DependencyObject obj) => (bool)obj.GetValue(IsRenameEditorProperty);

    /// <summary>Sets the value of the <see cref="IsRenameEditorProperty"/> attached property.</summary>
    public static void SetIsRenameEditor(DependencyObject obj, bool value) => obj.SetValue(IsRenameEditorProperty, value);

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
    /// Focuses and selects all text the moment the editor becomes visible — i.e. the moment
    /// <c>ChildWindowViewModel.EditingItem</c> starts pointing at this item (see the
    /// <c>FileNameEditBoxStyle</c> DataTrigger in Templates.xaml). Deferred to
    /// <see cref="DispatcherPriority.Input"/> because the visibility flip and the layout pass that
    /// actually makes the element focusable are not synchronous — neither is realizing the item's
    /// container for a not-yet-rendered <see cref="TextBox"/>.
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
                GetViewModel(textBox)?.CommitRenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                GetViewModel(textBox)?.CancelRenameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Commits on losing keyboard focus (clicking elsewhere, Tab, etc.) — mirrors
    /// <c>TitleEditBox_LostKeyboardFocus</c>. Safe to fire redundantly after an Enter/Escape already
    /// cleared <c>EditingItem</c>: <c>CommitRenameCommand</c> is a no-op once there's nothing being
    /// edited (losing focus is itself a side effect of the editor becoming invisible again).
    /// </summary>
    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        GetViewModel(textBox)?.CommitRenameCommand.Execute(null);
    }

    private static ChildWindowViewModel? GetViewModel(FrameworkElement element)
        => Window.GetWindow(element)?.DataContext as ChildWindowViewModel;
}
