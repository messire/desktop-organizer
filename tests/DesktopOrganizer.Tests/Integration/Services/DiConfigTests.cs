using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// Integration tests for <see cref="DiConfig"/>.
/// </summary>
[Trait(IntegrationTests, nameof(DesktopOrganizer.Services))]
public class DiConfigTests
{
    private readonly ServiceProvider _serviceProvider;

    public DiConfigTests()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        _serviceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Ensures that all services are resolved without exceptions.
    /// </summary>
    [Fact]
    public void RegisterServices_AllDependenciesResolveCorrectly()
    {
        // Act & Assert
        var iconHandler = _serviceProvider.GetRequiredService<IIconHandler>();
        var iconFactory = _serviceProvider.GetRequiredService<IIconFactory>();
        var iconProvider = _serviceProvider.GetRequiredService<IIconProvider>();

        Assert.NotNull(iconHandler);
        Assert.NotNull(iconFactory);
        Assert.NotNull(iconProvider);
    }

    /// <summary>
    /// Ensures that singleton services are reused across resolutions.
    /// </summary>
    [Fact]
    public void RegisterServices_SingletonServicesAreReused()
    {
        // Act
        var firstIconHandler = _serviceProvider.GetRequiredService<IIconHandler>();
        var secondIconHandler = _serviceProvider.GetRequiredService<IIconHandler>();
        var firstIconProvider = _serviceProvider.GetRequiredService<IIconProvider>();

        var firstIconFactory = _serviceProvider.GetRequiredService<IIconFactory>();
        var secondIconFactory = _serviceProvider.GetRequiredService<IIconFactory>();
        var secondIconProvider = _serviceProvider.GetRequiredService<IIconProvider>();

        // Assert
        Assert.Same(firstIconHandler, secondIconHandler);
        Assert.Same(firstIconFactory, secondIconFactory);
        Assert.Same(firstIconProvider, secondIconProvider);
    }
}
