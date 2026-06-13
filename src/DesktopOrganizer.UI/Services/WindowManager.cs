using System.Text.Json;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Point = System.Drawing.Point;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Handles window management in the application.
/// </summary>
public class WindowManager : IWindowManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMonitorService _monitorService;
    private readonly Dictionary<Guid, (ChildWindow Window, ChildWindowModel Model)> _childWindows = [];

    // Lazily resolved to break the circular dependency WindowManager ↔ PageManagerService.
    private IPageManager? _pageManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowManager"/> class.
    /// </summary>
    /// <param name="serviceProvider">DI provider used to resolve windows.</param>
    /// <param name="monitorService">Monitor enumeration / change-tracking service.</param>
    public WindowManager(IServiceProvider serviceProvider, IMonitorService monitorService)
    {
        _serviceProvider = serviceProvider;
        _monitorService = monitorService;
        _monitorService.MonitorsChanged += OnMonitorsChanged;
    }

    private IPageManager PageManager => _pageManager ??= _serviceProvider.GetRequiredService<IPageManager>();

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
        var mainWindow = GetOrCreateMainWindow();

        if (mainWindow.Visibility == Visibility.Visible)
            HideWindow(mainWindow);
        else
            ShowWindow(mainWindow);
    }

    /// <inheritdoc />
    public ChildWindow CreateChildWindow(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Assign the current page if the model does not already have a non-default page.
        if (string.IsNullOrWhiteSpace(model.PageId))
            model.PageId = "default";

        ResolveMonitorPlacement(model);

        var window = _serviceProvider.GetRequiredService<ChildWindow>();
        window.Initialize(model);

        _childWindows[model.Id] = (window, model);

        // Only show the panel when its page is the active page.
        var isOnActivePage = model.PageId == PageManager.CurrentPage;
        if (model.IsVisible && isOnActivePage)
        {
            window.Show();
        }

        OnChildWindowAdded?.Invoke(this, model);
        return window;
    }

    /// <inheritdoc />
    public void ApplyPanelVisibility(ChildWindowModel model)
    {
        if (!_childWindows.TryGetValue(model.Id, out var entry)) return;

        if (model.IsVisible)
            entry.Window.Show();
        else
            entry.Window.Hide();
    }

    /// <inheritdoc />
    public void RemoveChildWindow(Guid windowId)
    {
        if (!_childWindows.TryGetValue(windowId, out var entry)) return;

        OnChildWindowRemoved?.Invoke(this, entry.Model);
        entry.Window.Close();
        _childWindows.Remove(windowId);
        SaveWindowsState();
    }

    /// <inheritdoc />
    public IEnumerable<ChildWindowModel> GetAllChildWindows()
        => _childWindows.Values.Select(entry => entry.Model);

    /// <inheritdoc />
    public void SaveWindowsState()
    {
        foreach (var (window, _) in _childWindows.Values)
        {
            window.SyncToModel();
        }

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

    // MainWindow is a singleton in DI — always returns the same instance
    private MainWindow GetOrCreateMainWindow()
        => _serviceProvider.GetRequiredService<MainWindow>();

    /// <summary>
    /// Ensures the panel is placed on a valid monitor:
    /// <list type="bullet">
    /// <item>If <see cref="ChildWindowModel.MonitorDeviceId"/> matches an existing monitor, keep the
    /// position but clamp it inside that monitor's bounds.</item>
    /// <item>If the stored monitor no longer exists, move the panel onto the monitor that currently
    /// contains its position, else the primary monitor.</item>
    /// <item>If no monitor id is set yet (new panel), assign the monitor under the panel's position.</item>
    /// </list>
    /// All coordinates are WPF DIPs, matching <see cref="MonitorInfo"/>.
    /// </summary>
    private void ResolveMonitorPlacement(ChildWindowModel model)
    {
        var pos = new System.Windows.Point(model.Position.X, model.Position.Y);

        var target = !string.IsNullOrEmpty(model.MonitorDeviceId)
            ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
            : null;

        // Stored monitor gone (or never set) → resolve by current position, else primary.
        target ??= _monitorService.FindMonitorAt(pos) ?? _monitorService.GetPrimaryMonitor();

        model.MonitorDeviceId = target.DeviceName;
        ClampIntoMonitor(model, target);
    }

    /// <summary>
    /// Keeps the panel's top-left within the monitor's work area so it never lands off-screen.
    /// </summary>
    private static void ClampIntoMonitor(ChildWindowModel model, MonitorInfo monitor)
    {
        var area = monitor.WorkArea;
        var width = Math.Max(0, model.Size.Width);
        var height = Math.Max(0, model.Size.Height);

        // If the panel already sits within the monitor, leave it untouched.
        var inside = model.Position.X >= area.Left && model.Position.Y >= area.Top
                     && model.Position.X < area.Right && model.Position.Y < area.Bottom;
        if (inside) return;

        var maxX = Math.Max(area.Left, area.Right - width);
        var maxY = Math.Max(area.Top, area.Bottom - height);
        var x = Math.Min(Math.Max(model.Position.X, area.Left), maxX);
        var y = Math.Min(Math.Max(model.Position.Y, area.Top), maxY);
        model.Position = new Point((int)x, (int)y);
    }

    /// <summary>
    /// Re-evaluates placement for every panel when the monitor configuration changes.
    /// Panels whose monitor disappeared are moved onto the primary monitor.
    /// </summary>
    private void OnMonitorsChanged()
    {
        if (_childWindows.Count == 0) return;

        var primary = _monitorService.GetPrimaryMonitor();

        foreach (var (window, model) in _childWindows.Values)
        {
            var monitor = !string.IsNullOrEmpty(model.MonitorDeviceId)
                ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
                : null;

            // Monitor still present → nothing to do.
            if (monitor is not null) continue;

            model.MonitorDeviceId = primary.DeviceName;
            ClampIntoMonitor(model, primary);

            window.Left = model.Position.X;
            window.Top = model.Position.Y;
        }

        SaveWindowsState();
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
