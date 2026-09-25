using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.State;
using Microsoft.Extensions.DependencyInjection;
using UiDiConfig = DesktopOrganizer.UI.Configuration.DiConfig;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// Integration tests for <see cref="DiConfig"/>.
/// </summary>
[Trait(IntegrationTests, nameof(DesktopOrganizer.Services))]
public class DiConfigTests
{
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiConfigTests"/> class, building a service provider
    /// from <see cref="DiConfig.RegisterServices(IServiceCollection)"/> shared by the tests below that do not need their own
    /// isolated diagnostics/storage directories.
    /// </summary>
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

    // ── IDiagnosticsLog registration ────────────────────────────────────────
    //
    // DiagnosticsSwitch.IsEnabled always returns true in a Debug build of DesktopOrganizer.Services
    // (see DiagnosticsSwitchTests' remarks) — this test project shares that Configuration with the
    // Services project it references, so which concrete IDiagnosticsLog the DI container resolves
    // by default genuinely differs between `dotnet test` (Debug) and `dotnet test -c Release`. Each
    // test below builds and disposes its own ServiceProvider, and — via the internal
    // diagnosticsLogDirectory overload — points any resolved real JsonLinesDiagnosticsLog at a
    // throwaway temp directory rather than the real per-user log directory, so running this test
    // repeatedly never touches (or retention-cleans) an actual user's diagnostics logs.

    /// <summary>
    /// Build-configuration-agnostic: whichever concrete <see cref="IDiagnosticsLog"/> the switch
    /// selects, it must be registered as a singleton — resolving twice must never construct two
    /// independent instances (and, for <see cref="JsonLinesDiagnosticsLog"/>, two independent
    /// background writer threads).
    /// </summary>
    [Fact]
    public void RegisterServices_IDiagnosticsLog_ResolvesAsSingleton()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            services.RegisterServices(tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IDiagnosticsLog>();
            var second = serviceProvider.GetRequiredService<IDiagnosticsLog>();

            Assert.Same(first, second);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

#if DEBUG
    /// <summary>
    /// Debug-build counterpart of <c>RegisterServices_NoExplicitDiagFlags_ResolvesToNullDiagnosticsLog</c>
    /// (Release-only): proves end-to-end through DI (not just DiagnosticsSwitch in isolation) that a
    /// Debug build always resolves the real sink, matching <c>DiagnosticsSwitch</c>'s "always on in
    /// Debug" contract.
    /// </summary>
    [Fact]
    public void RegisterServices_DebugBuild_ResolvesToJsonLinesDiagnosticsLog()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            services.RegisterServices(tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            var log = serviceProvider.GetRequiredService<IDiagnosticsLog>();

            Assert.IsType<JsonLinesDiagnosticsLog>(log);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }
#else
    /// <summary>
    /// Release-build counterpart of <c>RegisterServices_DebugBuild_ResolvesToJsonLinesDiagnosticsLog</c>:
    /// without <c>--diag</c> on the command line or <c>DESKTOPORGANIZER_DIAG=1</c> in the environment,
    /// a Release build must resolve the zero-cost no-op sink.
    /// </summary>
    [Fact]
    public void RegisterServices_NoExplicitDiagFlags_ResolvesToNullDiagnosticsLog()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        using var serviceProvider = services.BuildServiceProvider();

        var log = serviceProvider.GetRequiredService<IDiagnosticsLog>();

        Assert.Same(NullDiagnosticsLog.Instance, log);
    }
#endif

    // ── ISingleInstanceCoordinator disposal (C-2) ─────────────────────────────
    //
    // Regression coverage for a real bug caught end-to-end: Microsoft.Extensions.DependencyInjection's
    // ServiceProvider marks itself disposed BEFORE running its own singleton disposal chain, so any
    // GetService call from inside a singleton's Dispose() — e.g. SingleInstanceCoordinator resolving
    // IDiagnosticsLog to write SingleInstanceShutdownSequence — unconditionally threw
    // ObjectDisposedException, on every ordinary primary exit. Uses the real ServiceProvider (not a
    // fake/mock) specifically to reproduce the real disposal-ordering bug, not just the coordinator's
    // own unit-level contract.

    /// <summary>
    /// C-2 regression: resolving <see cref="ISingleInstanceCoordinator"/> and then disposing the
    /// <see cref="ServiceProvider"/> that owns it must never throw, even though the coordinator was
    /// never explicitly told to shut down first (<see cref="ISingleInstanceCoordinator.CompleteShutdown"/>
    /// is the host's job — see <c>App.OnExit</c> — and is not called here on purpose: this test pins
    /// that <see cref="ISingleInstanceCoordinator"/>'s inherited <c>Dispose</c> itself must be safe
    /// regardless).
    /// </summary>
    [Fact]
    public void RegisterServices_SingleInstanceCoordinator_ProviderDisposal_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        var serviceProvider = services.BuildServiceProvider();

        // Forces DI to materialize the coordinator (and, transitively, the real guard/detection mutexes
        // and pipe server/client) — side-effect-free per DiConfig's own remarks: none of this starts the
        // pipe server or acquires the guard mutex.
        var coordinator = serviceProvider.GetRequiredService<ISingleInstanceCoordinator>();
        Assert.NotNull(coordinator);

        var exception = Record.Exception(serviceProvider.Dispose);

        Assert.Null(exception);
    }

    /// <summary>
    /// <see cref="IShutdownRequestListener"/> (installer-initiated graceful shutdown) must resolve as a
    /// singleton, same as <see cref="ISingleInstanceCoordinator"/> right above — <c>App.xaml.cs</c>
    /// resolves it once in <c>StartApp</c> and relies on the same instance being disposed automatically
    /// with the root <see cref="ServiceProvider"/> in <c>OnExit</c>. Merely resolving it must never start
    /// listening on its own (that only happens on an explicit <see cref="IShutdownRequestListener.Start"/>
    /// call, which this test deliberately does not make) and must never throw.
    /// </summary>
    [Fact]
    public void RegisterServices_IShutdownRequestListener_ResolvesAsSingleton()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        using var serviceProvider = services.BuildServiceProvider();

        var first = serviceProvider.GetRequiredService<IShutdownRequestListener>();
        var second = serviceProvider.GetRequiredService<IShutdownRequestListener>();

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    // ── DesktopOrganizer.UI's DiConfig ──────────────────────────────────────
    //
    // UiDiConfig.ConfigureServices() builds the full UI service collection (ViewModels, windows,
    // etc.) on top of RegisterServices() above. Uses the internal diagnosticsLogDirectory overload for
    // the same reason as the Services-layer tests above: any resolved real JsonLinesDiagnosticsLog
    // must never touch the real per-user log directory during a test run.

    /// <summary>
    /// <see cref="IGestureDiagnostics"/> is registered transient (see <c>UiDiConfig</c>'s remarks — one
    /// instance per panel) and depends on <see cref="IDiagnosticsLog"/>, which is itself resolved from
    /// <c>RegisterServices</c>. Proves the whole chain resolves end-to-end through the real UI service
    /// collection without throwing, regardless of which concrete <see cref="IDiagnosticsLog"/> the
    /// build configuration selects.
    /// </summary>
    [Fact]
    public void UiConfigureServices_IGestureDiagnostics_ResolvesAsTransient()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IGestureDiagnostics>();
            var second = serviceProvider.GetRequiredService<IGestureDiagnostics>();

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second); // transient: a fresh instance per resolution
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    // ── Versioned JSON application-state store registrations (ADR-024, C6) ───
    //
    // All four tests below use both the diagnosticsLogDirectory AND storageRoot internal overloads,
    // so resolving IApplicationStateStore/IStorageLock/IApplicationStateService here never touches the
    // real per-user %LOCALAPPDATA%\DesktopOrganizer\State or its .storage-lock file.

    /// <summary>
    /// Verifies that <see cref="IApplicationStateStore"/>, <see cref="IStorageLock"/>,
    /// <see cref="IApplicationStateService"/>, and <see cref="ILegacyStateImporter"/> all resolve
    /// without throwing when the UI service collection is configured against a temp storage root.
    /// </summary>
    [Fact]
    public void UiConfigureServices_ApplicationStateRegistrations_ResolveCorrectly()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var store = serviceProvider.GetRequiredService<IApplicationStateStore>();
            var storageLock = serviceProvider.GetRequiredService<IStorageLock>();
            var applicationStateService = serviceProvider.GetRequiredService<IApplicationStateService>();
            var legacyImporter = serviceProvider.GetRequiredService<ILegacyStateImporter>();

            Assert.NotNull(store);
            Assert.NotNull(storageLock);
            Assert.NotNull(applicationStateService);
            Assert.NotNull(legacyImporter);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// Verifies that <see cref="IApplicationStateStore"/> resolves as a singleton: two resolutions from
    /// the same provider must return the same instance.
    /// </summary>
    [Fact]
    public void UiConfigureServices_IApplicationStateStore_ResolvesAsSingleton()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IApplicationStateStore>();
            var second = serviceProvider.GetRequiredService<IApplicationStateStore>();

            Assert.Same(first, second);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// Verifies that <see cref="IApplicationStateService"/> resolves as a singleton: two resolutions
    /// from the same provider must return the same instance.
    /// </summary>
    [Fact]
    public void UiConfigureServices_IApplicationStateService_ResolvesAsSingleton()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IApplicationStateService>();
            var second = serviceProvider.GetRequiredService<IApplicationStateService>();

            Assert.Same(first, second);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// Proves the <c>storageRoot</c> override actually takes effect (rather than silently falling
    /// back to the real per-user location): acquiring the advisory lock through the overridden
    /// registration must create <c>.storage-lock</c> under the temp <c>storageRoot</c>
    /// this test owns and cleans up, never under the real <c>%LOCALAPPDATA%\DesktopOrganizer</c>.
    /// </summary>
    [Fact]
    public void UiConfigureServices_StorageRootOverride_IsHonoredByStorageLock()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var storageLock = serviceProvider.GetRequiredService<IStorageLock>();
            var acquired = storageLock.TryAcquire();

            Assert.True(acquired);
            Assert.True(File.Exists(Path.Combine(storageRoot, ".storage-lock")));
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string NewTempDiagnosticsDirectory() =>
        Path.Combine(Path.GetTempPath(), $"DOrgDiConfigDiag_{Guid.NewGuid():N}");

    private static string NewTempStorageRoot() =>
        Path.Combine(Path.GetTempPath(), $"DOrgDiConfigStorage_{Guid.NewGuid():N}");

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle shouldn't mask the real assertion failure.
        }
    }
}
