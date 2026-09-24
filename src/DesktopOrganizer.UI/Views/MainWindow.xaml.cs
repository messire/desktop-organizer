using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Settings/Config window. Opened from the system tray, hidden instead of closed.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    /// <param name="viewModel"><see cref="MainWindowViewModel"/> instance.</param>
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

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
    private void SwitchPage(bool next)
    {
        var pages = _viewModel.Pages;
        if (pages.Count == 0) return;

        var currentIndex = pages.IndexOf(_viewModel.CurrentPage);
        var targetIndex = next
            ? (currentIndex + 1) % pages.Count
            : (currentIndex - 1 + pages.Count) % pages.Count;

        _viewModel.SwitchPageCommand.Execute(pages[targetIndex]);
    }
}
