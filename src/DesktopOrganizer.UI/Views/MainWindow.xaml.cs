using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Settings/Config window. Opened from the system tray, hidden instead of closed.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    // Retained only so it isn't collected before this window is (it self-unsubscribes on Window.Closed,
    // which this window never actually raises — see ThemedWindowBehavior's own remarks) — kept as a field
    // purely for readability/symmetry with that lifetime story, not because anything else reads it.
    private readonly ThemedWindowBehavior _themedWindowBehavior;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    /// <param name="viewModel"><see cref="MainWindowViewModel"/> instance.</param>
    /// <param name="appThemeService">Design system 1.x §3 phase 6.1: follows the resolved chrome theme (<see cref="ThemedWindowBehavior"/>).</param>
    /// <param name="windowFrameTheming">Applies the DWM immersive dark-mode frame attribute for <paramref name="appThemeService"/>'s theme.</param>
    public MainWindow(MainWindowViewModel viewModel, IAppThemeService appThemeService, IWindowFrameTheming windowFrameTheming)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _themedWindowBehavior = new ThemedWindowBehavior(this, appThemeService, windowFrameTheming);

        // Ctrl+PgDown / Ctrl+PgUp — navigate between pages
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => SwitchPage(next: true)),
            new KeyGesture(Key.Next, ModifierKeys.Control)));

        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => SwitchPage(next: false)),
            new KeyGesture(Key.Prior, ModifierKeys.Control)));

        // Ctrl+E — toggle the app-global layout edit mode (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9
        // #3). This window is an ordinary activatable window (unlike ChildWindow's WS_EX_NOACTIVATE
        // panels), so keyboard input here is not the unverified risk class Escape is on a panel (R-6).
        InputBindings.Add(new KeyBinding(
            _viewModel.ToggleEditLayoutCommand,
            new KeyGesture(Key.E, ModifierKeys.Control)));

        // MINOR-6 (FIX round 2): refresh transient, externally-mutable settings state (currently just
        // HasOneTimeHintsToReset) every time this window becomes visible or gains focus again — this
        // ViewModel is a DI singleton and the window is Hidden/re-Shown rather than recreated (see
        // OnClosing below), so without this the General section could keep showing stale state from
        // whenever it last happened to raise a notification, across an entire hide/show cycle. Both
        // IsVisibleChanged (Show()/ShowMainWindow()) and Activated (regaining focus while already
        // visible) are covered — either can be the moment the user next looks at that section.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _viewModel.RefreshTransientState();
            }
            else
            {
                // L-9 fix (backlog 1.x wave 1 review): this window is Hidden, not closed (see OnClosing
                // below) — right-clicking a panel, Alt+Tab, or any other way the window loses visibility
                // while a page's inline-rename editor is still open must not leave it open across the
                // hide/show cycle (see MainWindowViewModel.CloseAnyOpenRename's own remarks — renamed from
                // CancelAnyOpenRename, LOW-4 fix, code review, panel clone to page, backlog 1.x wave 3
                // review).
                _viewModel.CloseAnyOpenRename();
            }
        };
        Activated += (_, _) => _viewModel.RefreshTransientState();
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of closing — the app lives in the tray
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Delegates Ctrl+PgUp/Down to the VM: moves to the next/previous page, wrapping around.
    /// </summary>
    /// <remarks>
    /// ADR-043: <see cref="MainWindowViewModel.Pages"/> holds <see cref="PageItemViewModel"/> rows (not
    /// bare page identifiers) — the current page is looked up by <see cref="PageItemViewModel.Id"/>, and
    /// <see cref="MainWindowViewModel.SwitchPageCommand"/> is still given the target page's <c>Id</c>
    /// (a <see cref="string"/>) — that command's own parameter type is unchanged by this.
    /// </remarks>
    private void SwitchPage(bool next)
    {
        var pages = _viewModel.Pages;
        if (pages.Count == 0) return;

        var currentIndex = -1;
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].Id == _viewModel.CurrentPage)
            {
                currentIndex = i;
                break;
            }
        }

        var targetIndex = next
            ? (currentIndex + 1) % pages.Count
            : ((currentIndex - 1) + pages.Count) % pages.Count;

        _viewModel.SwitchPageCommand.Execute(pages[targetIndex].Id);
    }
}
