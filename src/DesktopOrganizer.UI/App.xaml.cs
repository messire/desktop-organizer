using DesktopOrganizer.UI.Configuration;
using DesktopOrganizer.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI;

/// <summary>
/// Interaction logic for App.xaml.
/// </summary>
public partial class App : Application
{
    private readonly ServiceProvider _serviceProvider;
    private ITrayIconManager? _trayIconManager;
    private IWindowManager? _windowManager;

    private ITrayIconManager TrayIconManager
        => (_trayIconManager ??= _serviceProvider.GetService<ITrayIconManager>())!;

    private IWindowManager WindowManager
        => (_windowManager ??= _serviceProvider.GetService<IWindowManager>())!;

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
        _serviceProvider.Dispose();
        base.OnExit(e);
    }

    private void StartApp(object? sender, StartupEventArgs e)
    {
        InitTrayIcon();
        ShowMainWindow(sender, e);
    }

    private void ShowMainWindow(object? sender, EventArgs e)
        => Current.Dispatcher.Invoke(() => WindowManager.ShowMainWindow());

    private void InitTrayIcon()
    {
        TrayIconManager.Initialize();
        TrayIconManager.OnShowMainWindowRequested += ShowMainWindow;
        TrayIconManager.OnExitRequested += (_, _) => Shutdown();
    }
}
