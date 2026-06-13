using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.Services.Configuration;

/// <summary>
/// Extensions for DesktopOrganizer.Services DI registration.
/// </summary>
public static class DiConfig
{
    /// <summary>
    /// Register service dependencies.
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    public static void RegisterServices(this IServiceCollection services)
    {
        // Utils
        services.AddSingleton<IIconHandler, IconHandler>();
        services.AddSingleton<IIconFactory, IconFactory>();
        services.AddSingleton<INativeApi, NativeApi>();
        services.AddSingleton<INotifyIconAdapter, NotifyIconAdapter>();

        // Services
        services.AddSingleton<IIconProvider, IconProvider>();
        services.AddSingleton<ITrayIconManager, TrayIconManager>();
        services.AddSingleton<IDesktopIntegrationService, DesktopIntegrationService>();
        services.AddSingleton<IFileLauncher, FileLauncher>();
        services.AddSingleton<IDesktopIconsService, DesktopIconsService>();
        services.AddSingleton<IDesktopGestureService, DesktopGestureService>();
        services.AddSingleton<IFolderWatcherFactory, FolderWatcherFactory>();

        // Multi-monitor: self-contained service (owns its own WM_DISPLAYCHANGE listener).
        services.AddSingleton<IMonitorService, MonitorService>();
    }
}
