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

        // Services
        services.AddTransient<IIconProvider, IconProvider>();
    }
}
