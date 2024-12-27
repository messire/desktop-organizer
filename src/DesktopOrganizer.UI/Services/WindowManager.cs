using System.Text.Json;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Handles window management in the application.
/// </summary>
public class WindowManager(IServiceProvider serviceProvider) : IWindowManager
{
    private readonly Dictionary<Guid, ChildWindow> _childWindows = [];

    /// <inheritdoc />
    public event EventHandler<bool>? OnMainWindowVisibilityChanged;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowAdded;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowRemoved;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowModified;

    /// <inheritdoc />
    public void StartMainWindow()
    {
        var mainWindow = CreateMainWindowIfNotExists();
        if (mainWindow is null) return;

        if (mainWindow.Visibility == Visibility.Visible)
        {
            HideWindow(mainWindow);
        }
    }

    /// <inheritdoc />
    public ChildWindow CreateChildWindow(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var window = serviceProvider.GetRequiredService<ChildWindow>();
        window.DataContext = model;

        _childWindows[model.Id] = window;

        if (model.IsVisible)
        {
            window.Show();
        }

        OnChildWindowAdded?.Invoke(this, model);
        return window;
    }

    /// <inheritdoc />
    public void RemoveChildWindow(Guid windowId)
    {
        if (!_childWindows.TryGetValue(windowId, out var window)) return;

        var model = (ChildWindowModel)window.DataContext;
        OnChildWindowRemoved?.Invoke(this, model);
        window.Close();
        _childWindows.Remove(windowId);
        SaveWindowsState();
    }

    /// <inheritdoc />
    public IEnumerable<ChildWindowModel> GetAllChildWindows()
        => _childWindows.Values.Select(childWindow => (ChildWindowModel)childWindow.DataContext);

    /// <inheritdoc />
    public void SaveWindowsState()
    {
        var states = GetAllChildWindows().ToList();
        var json = JsonSerializer.Serialize(states);
        Properties.Settings.Default.ChildWindowsState = json;
        Properties.Settings.Default.Save();
    }

    /// <inheritdoc />
    public void LoadWindowsState()
    {
        var savedState = Properties.Settings.Default.ChildWindowsState;
        if (string.IsNullOrWhiteSpace(savedState)) return;

        try
        {
            var states = JsonSerializer.Deserialize<List<ChildWindowModel>>(savedState);
            if (states == null) return;

            foreach (var state in states)
            {
                CreateChildWindow(state);
            }
        }
        catch (JsonException)
        {
            _childWindows.Clear();
        }
    }

    private MainWindow? CreateMainWindowIfNotExists()
    {
        var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (mainWindow is not null) return mainWindow;

        mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        ShowWindow(mainWindow);
        return null;
    }

    private void ShowWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        MainWindowVisibilityChanged(true);
    }

    private void HideWindow(MainWindow mainWindow)
    {
        mainWindow.Hide();
        MainWindowVisibilityChanged(false);
    }

    private void MainWindowVisibilityChanged(bool visibility)
        => OnMainWindowVisibilityChanged?.Invoke(this, visibility);
}
