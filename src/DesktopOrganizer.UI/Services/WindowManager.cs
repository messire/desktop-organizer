using DesktopOrganizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Handles window management in the application.
/// </summary>
public class WindowManager(IServiceProvider serviceProvider) : IWindowManager
{
    /// <inheritdoc />
    public event EventHandler<bool>? OnMainWindowVisibilityChanged;

    /// <inheritdoc />
    public void ShowMainWindow()
    {
        var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

        if (mainWindow == null)
        {
            CreateWindow();
            return;
        }

        if (mainWindow.Visibility == Visibility.Visible)
        {
            HideWindow(mainWindow);
            return;
        }

        ShowWindow(mainWindow);
    }

    private void CreateWindow()
    {
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        MainWindowShow();
    }

    private void ShowWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        MainWindowShow();
    }

    private void HideWindow(MainWindow mainWindow)
    {
        mainWindow.Hide();
        MainWindowHide();
    }

    private void MainWindowShow()
        => MainWindowVisibilityChanged(true);

    private void MainWindowHide()
        => MainWindowVisibilityChanged(false);

    private void MainWindowVisibilityChanged(bool visibility)
        => OnMainWindowVisibilityChanged?.Invoke(this, visibility);
}
