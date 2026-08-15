using DesktopOrganizer.UI.Configuration;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI;

/// <summary>
/// Interaction logic for App.xaml.
/// </summary>
public partial class App : Application
{
    // Cast from GridMetrics.MinPanelSize (the single source of truth for this value — finalization
    // MAJOR M-1/m-2 — also backs ChildWindow.xaml's hard MinWidth="120" and ChildWindow.xaml.cs's own
    // MinPanelSize) rather than an independent literal: System.Drawing.Size's width/height are int,
    // while GridMetrics.MinPanelSize is a double shared with the (fractional-IconSize-aware) WPF-side
    // panel geometry math, so a narrowing cast is required here at the int/double boundary.
    private const int MinPanelSize = (int)GridMetrics.MinPanelSize;

    // Held open for the whole process lifetime purely so the installer (see AppMutex in
    // installer\DesktopOrganizer.iss) can detect a running instance and prompt to close it before
    // overwriting locked files — not used for single-instance enforcement.
    private readonly Mutex _installerDetectionMutex = new(initiallyOwned: false, "DesktopOrganizerSingleInstanceMutex");

    private readonly ServiceProvider _serviceProvider;
    private ITrayIconManager? _trayIconManager;
    private IWindowManager? _windowManager;
    private IDesktopIconsService? _desktopIcons;
    private IDesktopGestureService? _desktopGestures;
    private IMonitorService? _monitorService;
    private IAppSettings? _appSettings;
    private ISystemIconsPanelManager? _systemIconsPanel;

    private ITrayIconManager TrayIconManager
        => (_trayIconManager ??= _serviceProvider.GetService<ITrayIconManager>())!;

    private IWindowManager WindowManager
        => (_windowManager ??= _serviceProvider.GetService<IWindowManager>())!;

    private IDesktopIconsService DesktopIcons
        => (_desktopIcons ??= _serviceProvider.GetService<IDesktopIconsService>())!;

    private IDesktopGestureService DesktopGestures
        => (_desktopGestures ??= _serviceProvider.GetService<IDesktopGestureService>())!;

    private IMonitorService MonitorService
        => (_monitorService ??= _serviceProvider.GetService<IMonitorService>())!;

    private IAppSettings AppSettings
        => (_appSettings ??= _serviceProvider.GetService<IAppSettings>())!;

    private ISystemIconsPanelManager SystemIconsPanel
        => (_systemIconsPanel ??= _serviceProvider.GetService<ISystemIconsPanelManager>())!;

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class.
    /// </summary>
    public App()
    {
        ServiceCollection services = new ServiceCollection();
        services.ConfigureServices();
        _serviceProvider = services.BuildServiceProvider();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        RestoreDesktopIcons();
        SystemIconsPanel.Save();

        // Structural (app exit): must write synchronously, before disposing the DI container, or the
        // last action the user took right before closing (which SaveWindowsState may have only just
        // scheduled, debounced) would be silently lost.
        WindowManager.SaveWindowsStateNow();
        _serviceProvider.Dispose();
        _installerDetectionMutex.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Re-shows the real desktop icons on shutdown. The app can hide them (desktop double-click
    /// gesture, settings checkbox), so without this the user is left with an empty desktop
    /// after closing the app. Failures are swallowed — shutdown must not be blocked.
    /// </summary>
    private void RestoreDesktopIcons()
    {
        try
        {
            if (!DesktopIcons.AreIconsVisible)
                DesktopIcons.SetIconsVisible(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to restore desktop icons on exit: {ex.Message}");
        }
    }

    private void StartApp(object? sender, StartupEventArgs e)
    {
        InitTrayIcon();
        MonitorService.Start(); // begin listening for display changes before panels load
        LoadPanels();
        InitDesktopGestures();

        SystemIconsPanel.Load();
        SystemIconsPanel.Apply();
    }

    private void InitDesktopGestures()
    {
        // Heavy work is deferred off the hook callback via the dispatcher.
        DesktopGestures.DesktopDoubleClicked += (_, _) => Current.Dispatcher.BeginInvoke(DesktopIcons.Toggle);
        DesktopGestures.LassoCompleted += (_, args) => Current.Dispatcher.BeginInvoke(() => CreateLassoPanel(args));
        DesktopGestures.Start();
    }

    private void LoadPanels()
    {
        WindowManager.LoadWindowsState();

        // Sync the settings VM after panels are loaded
        var settingsVm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        settingsVm.SyncPanels();
    }

    private void ShowSettingsWindow(object? sender, EventArgs e)
        => Current.Dispatcher.Invoke(() => WindowManager.ShowMainWindow());

    private void ToggleSettingsWindow(object? sender, EventArgs e)
        => Current.Dispatcher.Invoke(() => WindowManager.StartMainWindow());

    private void InitTrayIcon()
    {
        TrayIconManager.Initialize();
        TrayIconManager.OnShowMainWindowRequested += ShowSettingsWindow;
        TrayIconManager.OnToggleMainWindowVisibilityRequested += ToggleSettingsWindow;
        TrayIconManager.OnExitRequested += (_, _) => Shutdown();
    }

    /// <summary>
    /// Creates a panel covering the lasso rectangle drawn on the desktop.
    /// </summary>
    private void CreateLassoPanel(DesktopLassoEventArgs args)
    {
        var model = NewPanelWithDefaults();
        model.Title = "New Panel";
        model.Position = new System.Drawing.Point(args.X, args.Y);
        model.Size = new System.Drawing.Size(Math.Max(MinPanelSize, args.Width), Math.Max(MinPanelSize, args.Height));

        WindowManager.CreateChildWindow(model);

        // Structural (new panel created via lasso gesture) — write synchronously.
        WindowManager.SaveWindowsStateNow();
    }

    private ChildWindowModel NewPanelWithDefaults() => new()
    {
        BackgroundColor = AppSettings.DefaultBackgroundColor,
        TextColor = AppSettings.DefaultTextColor,
        BackgroundOpacity = AppSettings.DefaultOpacity,
        IconSize = AppSettings.DefaultIconSize,
    };
}
