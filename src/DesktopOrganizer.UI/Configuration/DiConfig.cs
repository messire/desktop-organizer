using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.UI.Configuration;

/// <summary>
/// Extensions for DesktopOrganizer.UI DI registration.
/// </summary>
public static class DiConfig
{
    /// <summary>
    /// Register services.
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    public static void ConfigureServices(this IServiceCollection services)
    {
        services.RegisterServices();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();        // Settings VM — no file state
        services.AddTransient<ChildWindowViewModel>();       // Per-panel VM — has file state
        services.AddSingleton<LayoutSnapshotsViewModel>();  // Snapshots section VM

        services.AddSingleton<IConfirmationService, ConfirmationService>();
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IAutostartService, AutostartService>();
        services.AddSingleton<IWindowManager, WindowManager>();
        services.AddSingleton<IPageManager, PageManagerService>();
        services.AddSingleton<ILayoutSnapshotService, LayoutSnapshotService>();
        services.AddSingleton<ISortingRuleService, SortingRuleService>();
        services.AddSingleton<MainWindow>();   // Singleton: hidden, not re-created
        services.AddTransient<ChildWindow>();
    }
}
