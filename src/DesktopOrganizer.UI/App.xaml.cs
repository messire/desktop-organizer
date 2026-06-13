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
    private const int MinPanelSize = 120;

    private readonly ServiceProvider _serviceProvider;
    private ITrayIconManager? _trayIconManager;
    private IWindowManager? _windowManager;
    private IDesktopIconsService? _desktopIcons;
    private IDesktopGestureService? _desktopGestures;
    private IMonitorService? _monitorService;
    private IAppSettings? _appSettings;

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
        WindowManager.SaveWindowsState();
        _serviceProvider.Dispose();
        base.OnExit(e);
    }

    private void StartApp(object? sender, StartupEventArgs e)
    {
        InitTrayIcon();
        MonitorService.Start(); // begin listening for display changes before panels load
        LoadPanels();
        InitDesktopGestures();
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
        => Current.Dispatcher.Invoke(() => WindowManager.StartMainWindow());

    private void InitTrayIcon()
    {
        TrayIconManager.Initialize();
        TrayIconManager.OnShowMainWindowRequested += ShowSettingsWindow;
        TrayIconManager.OnExitRequested += (_, _) => Shutdown();
        TrayIconManager.OnToggleDesktopIconsRequested += (_, _) => Current.Dispatcher.Invoke(DesktopIcons.Toggle);
        TrayIconManager.OnCaptureDesktopRequested += (_, _) => Current.Dispatcher.Invoke(CaptureDesktop);
    }

    /// <summary>
    /// Creates a panel covering the lasso rectangle drawn on the desktop.
    /// </summary>
    private void CreateLassoPanel(DesktopLassoEventArgs args)
    {
        var model = NewPanelWithDefaults();
        model.Title = "Новая панель";
        model.Position = new System.Drawing.Point(args.X, args.Y);
        model.Size = new System.Drawing.Size(Math.Max(MinPanelSize, args.Width), Math.Max(MinPanelSize, args.Height));

        WindowManager.CreateChildWindow(model);
        WindowManager.SaveWindowsState();
    }

    /// <summary>
    /// Creates a folder-portal panel mirroring the Desktop folder and hides the real icons.
    /// </summary>
    private void CaptureDesktop()
    {
        var model = NewPanelWithDefaults();
        model.Title = "Рабочий стол";
        model.SourceFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        model.ViewMode = PanelViewMode.Wrap;
        model.Position = new System.Drawing.Point(100, 100);
        model.Size = new System.Drawing.Size(420, 520);

        WindowManager.CreateChildWindow(model);
        WindowManager.SaveWindowsState();

        DesktopIcons.SetIconsVisible(false);
    }

    private ChildWindowModel NewPanelWithDefaults() => new()
    {
        BackgroundColor = AppSettings.DefaultBackgroundColor,
        TextColor = AppSettings.DefaultTextColor,
        BackgroundOpacity = AppSettings.DefaultOpacity,
        IconSize = AppSettings.DefaultIconSize,
    };
}
