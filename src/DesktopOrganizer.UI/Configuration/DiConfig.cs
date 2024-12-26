using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.Services.Providers;
using DesktopOrganizer.UI.Services;
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
        services.AddSingleton<IWindowManager, WindowManager>();
        services.AddTransient<MainWindow>();
    }
}
