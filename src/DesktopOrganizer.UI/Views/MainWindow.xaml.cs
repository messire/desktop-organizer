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
            new RelayPageCommand(_viewModel, next: true),
            new KeyGesture(Key.Next, ModifierKeys.Control)));

        InputBindings.Add(new KeyBinding(
            new RelayPageCommand(_viewModel, next: false),
            new KeyGesture(Key.Prior, ModifierKeys.Control)));
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of closing — the app lives in the tray
        e.Cancel = true;
        Hide();
    }

    // ------------------------------------------------------------------
    // Small private command that delegates Ctrl+PgUp/Down to the VM
    // ------------------------------------------------------------------
    private sealed class RelayPageCommand(MainWindowViewModel vm, bool next) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            var pages = vm.Pages;
            if (pages.Count == 0) return;

            var currentIndex = pages.IndexOf(vm.CurrentPage);
            int targetIndex;
            if (next)
                targetIndex = (currentIndex + 1) % pages.Count;
            else
                targetIndex = (currentIndex - 1 + pages.Count) % pages.Count;

            vm.SwitchPageCommand.Execute(pages[targetIndex]);
        }
    }
}
