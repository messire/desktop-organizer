using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.ViewModels;

using static DesktopOrganizer.UI.Views.VisualTreeWalker;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns the panel's two auto-hiding, non-modal hint popups — the one-time Move-semantics hint
/// (<see cref="ShowMoveHint"/>) and the inline-rename error hint (<see cref="ShowRenameError"/>) —
/// extracted from <c>ChildWindow.xaml.cs</c> (REFACTORING_PLAN.md §5.3). Neither popup is a
/// <see cref="System.Windows.MessageBox"/>: the owning panel carries <c>WS_EX_NOACTIVATE</c>, so a modal
/// dialog would never receive activation/input either (ADR-013 risk R1).
/// </summary>
public sealed class HintPopupController
{
    private readonly Dispatcher _dispatcher;
    private readonly Popup _moveHintPopup;
    private readonly Popup _renameErrorPopup;
    private readonly TextBlock _renameErrorText;
    private readonly DependencyObject _renameEditorSearchRoot;
    private readonly Func<FileItem?> _getEditingItem;

    // One-shot timer that auto-dismisses the Move-semantics hint popup; recreated per show.
    private DispatcherTimer? _moveHintTimer;

    // One-shot timer that auto-dismisses the rename-error hint popup; recreated per show.
    private DispatcherTimer? _renameErrorTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintPopupController"/> class.
    /// </summary>
    /// <param name="dispatcher">The owning window's <see cref="Dispatcher"/>, used to defer re-focusing the rename editor.</param>
    /// <param name="moveHintPopup">The Move-semantics hint popup.</param>
    /// <param name="renameErrorPopup">The rename-error hint popup.</param>
    /// <param name="renameErrorText">The <see cref="TextBlock"/> inside <paramref name="renameErrorPopup"/> whose text is set per <see cref="RenameOutcome"/>.</param>
    /// <param name="renameEditorSearchRoot">The visual-tree root to search for the active inline-rename <see cref="TextBox"/> (see <see cref="RefocusRenameEditor"/>).</param>
    /// <param name="getEditingItem">Accessor for <see cref="ChildWindowViewModel.EditingItem"/>.</param>
    public HintPopupController(
        Dispatcher dispatcher,
        Popup moveHintPopup,
        Popup renameErrorPopup,
        TextBlock renameErrorText,
        DependencyObject renameEditorSearchRoot,
        Func<FileItem?> getEditingItem)
    {
        _dispatcher = dispatcher;
        _moveHintPopup = moveHintPopup;
        _renameErrorPopup = renameErrorPopup;
        _renameErrorText = renameErrorText;
        _renameEditorSearchRoot = renameEditorSearchRoot;
        _getEditingItem = getEditingItem;
    }

    /// <summary>
    /// Shows the non-modal, auto-dismissing Move-semantics hint popup anchored to the title bar.
    /// Rendering only — the one-time decision (whether to show at all) already happened in the
    /// ViewModel before <see cref="ChildWindowViewModel.MoveHintRequested"/> was raised.
    /// </summary>
    public void ShowMoveHint()
    {
        _moveHintTimer?.Stop();
        _moveHintPopup.IsOpen = true;
        _moveHintTimer = StartAutoDismissTimer(_moveHintPopup, TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Shows the non-modal, auto-dismissing rename-error hint popup, anchored to the file list, then
    /// re-focuses the inline-rename editor so the user can immediately correct the name. The combined
    /// entry point for <see cref="ChildWindowViewModel.RenameError"/> becoming non-null.
    /// </summary>
    public void ShowRenameError(RenameOutcome outcome)
    {
        _renameErrorText.Text = outcome switch
        {
            RenameOutcome.InvalidName => "Invalid file name.",
            RenameOutcome.NameTaken => "A file or folder with that name already exists.",
            RenameOutcome.NotFound => "File not found — it may have been deleted or moved.",
            _ => "Failed to rename the file.",
        };

        _renameErrorTimer?.Stop();
        _renameErrorPopup.IsOpen = true;
        _renameErrorTimer = StartAutoDismissTimer(_renameErrorPopup, TimeSpan.FromSeconds(4));

        RefocusRenameEditor();
    }

    /// <summary>Stops both auto-dismiss timers. Must be called from the owning window's <c>OnClosing</c>.</summary>
    public void StopTimers()
    {
        _moveHintTimer?.Stop();
        _renameErrorTimer?.Stop();
    }

    /// <summary>
    /// Re-focuses the inline-rename <see cref="TextBox"/> after a failed
    /// <see cref="ChildWindowViewModel.CommitRenameCommand"/>. <see cref="ChildWindowViewModel.CommitRename"/>
    /// deliberately leaves <see cref="ChildWindowViewModel.EditingItem"/> set on failure "so the user can
    /// correct the name" — but the commit itself was triggered by <see cref="RenameEditBehavior"/>'s
    /// <c>LostKeyboardFocus</c> handler, so by the time this runs the editor is visible again but no
    /// longer holds keyboard focus. Without re-focusing it here, a second click into empty space would
    /// never raise <c>LostKeyboardFocus</c> again (focus already left the TextBox) and the editor would
    /// appear permanently stuck. Mirrors the same focus+select-all path <see cref="RenameEditBehavior"/>
    /// uses when the editor first becomes visible.
    /// </summary>
    private void RefocusRenameEditor()
    {
        if (_getEditingItem() is null) return;

        _dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                // Re-check: by the time this runs (deferred), the edit may already have been
                // committed/cancelled (e.g. Escape pressed before the dispatcher callback ran).
                if (_getEditingItem() is null) return;
                if (FindVisibleRenameEditor(_renameEditorSearchRoot) is not { } textBox) return;

                textBox.Focus();
                textBox.SelectAll();
            }));
    }

    /// <summary>
    /// Walks the visual tree from <paramref name="root"/> looking for the currently visible inline-rename
    /// <see cref="TextBox"/> (see <see cref="RenameEditBehavior"/>). There is at most one at a time — only
    /// one <c>FileItem</c> can be <see cref="ChildWindowViewModel.EditingItem"/> — but every other panel
    /// item's (invisible) rename <see cref="TextBox"/> also carries the attached property, so both checks
    /// are required.
    /// </summary>
    private static TextBox? FindVisibleRenameEditor(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBox { IsVisible: true } textBox && RenameEditBehavior.GetIsRenameEditor(textBox))
                return textBox;

            if (FindVisibleRenameEditor(child) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Starts a one-shot timer that closes <paramref name="popup"/> after <paramref name="duration"/>
    /// and then stops itself. Shared by <see cref="ShowMoveHint"/> and <see cref="ShowRenameError"/> for
    /// their identical auto-dismissing hint-popup behavior.
    /// </summary>
    private static DispatcherTimer StartAutoDismissTimer(Popup popup, TimeSpan duration)
    {
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            popup.IsOpen = false;
            timer.Stop();
        };
        timer.Start();
        return timer;
    }
}
