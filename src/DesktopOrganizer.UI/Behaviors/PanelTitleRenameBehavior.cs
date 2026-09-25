using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns the panel title's inline-rename gesture, extracted from <c>ChildWindow.xaml.cs</c>'s
/// <c>BeginRename</c>/<c>CommitRename</c>/<c>CancelRename</c>/<c>TitleEditBox_*</c>
/// (REFACTORING_PLAN.md §5.3). Modeled on <see cref="RenameEditBehavior"/> (the equivalent behavior for
/// a per-item file-name editor) but kept as an instance class, rather than an attached property, because
/// the panel title editor is a single, per-window element (<c>TitleText</c>/<c>TitleEditBox</c>) rather
/// than one instantiated per <c>DataTemplate</c> item.
/// </summary>
public sealed class PanelTitleRenameBehavior
{
    private readonly IWindowManager _windowManager;
    private readonly TextBlock _titleText;
    private readonly TextBox _titleEditBox;
    private readonly Func<ChildWindowModel?> _getModel;
    private readonly Action _syncToModel;

    private bool _isEditingTitle;
    private string _titleBeforeEdit = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelTitleRenameBehavior"/> class.
    /// </summary>
    /// <param name="windowManager">Used to persist layout state after a committed rename.</param>
    /// <param name="titleText">The read-only title <see cref="TextBlock"/>, hidden while editing.</param>
    /// <param name="titleEditBox">The inline-edit <see cref="TextBox"/>, shown while editing.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="syncToModel">Callback invoked after a committed rename to sync the ViewModel's title/file list back to the model.</param>
    public PanelTitleRenameBehavior(
        IWindowManager windowManager,
        TextBlock titleText,
        TextBox titleEditBox,
        Func<ChildWindowModel?> getModel,
        Action syncToModel)
    {
        _windowManager = windowManager;
        _titleText = titleText;
        _titleEditBox = titleEditBox;
        _getModel = getModel;
        _syncToModel = syncToModel;
    }

    /// <summary>
    /// Whether the panel title is currently being edited — used by <c>ChildWindow.ChildWindow_PreviewKeyDown</c>
    /// (the global F2 shortcut) to avoid fighting the title editor for focus, and by the title-bar/header
    /// click handlers to commit an in-progress file-item rename without also disturbing the title editor.
    /// </summary>
    public bool IsEditingTitle => _isEditingTitle;

    /// <summary>
    /// Switches the title into inline-edit mode.
    /// </summary>
    public void BeginRename()
    {
        var model = _getModel();
        if (model is null) return;

        _isEditingTitle = true;
        _titleBeforeEdit = model.Title;

        _titleText.Visibility = Visibility.Collapsed;
        _titleEditBox.Visibility = Visibility.Visible;
        _titleEditBox.Focus();
        _titleEditBox.SelectAll();
    }

    /// <summary>
    /// Commits the current title edit.
    /// </summary>
    public void CommitRename()
    {
        if (!_isEditingTitle) return;
        _isEditingTitle = false;

        _titleEditBox.Visibility = Visibility.Collapsed;
        _titleText.Visibility = Visibility.Visible;

        _syncToModel();
        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// Cancels the current title edit, restoring the title as it was before editing began.
    /// </summary>
    public void CancelRename()
    {
        if (!_isEditingTitle) return;
        _isEditingTitle = false;

        var model = _getModel();
        if (model is not null) model.Title = _titleBeforeEdit;

        _titleEditBox.Visibility = Visibility.Collapsed;
        _titleText.Visibility = Visibility.Visible;
    }

    /// <summary>Handles <c>Enter</c> (commit) and <c>Escape</c> (cancel) within the title edit box.</summary>
    /// <param name="sender">Unused; present to match the WPF key-event handler signature.</param>
    /// <param name="e">The key event; <see cref="KeyEventArgs.Key"/> selects commit vs. cancel.</param>
    public void OnTitleEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitRename();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelRename();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Commits the rename when the edit box loses keyboard focus (click-away, Tab, etc.).</summary>
    /// <param name="sender">The event source; unused.</param>
    /// <param name="e">The keyboard-focus event data; unused, present only to match the WPF handler signature.</param>
    public void OnTitleEditBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CommitRename();
}
