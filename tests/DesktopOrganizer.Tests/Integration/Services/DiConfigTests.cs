using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools.IconLoading;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.ViewModels;
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
    /// Verifies that <see cref="ILocalizationService"/> (docs/PLANS/localization.md §2.2/§4.0)
    /// resolves without throwing and is registered as a singleton — the same instance backs both
    /// <c>App.StartApp</c>'s <c>ApplyLanguage</c> call and every other consumer (tray, ViewModels).
    /// </summary>
    [Fact]
    public void RegisterServices_ILocalizationService_ResolvesAsSingleton()
    {
        var first = _serviceProvider.GetRequiredService<ILocalizationService>();
        var second = _serviceProvider.GetRequiredService<ILocalizationService>();

        Assert.NotNull(first);
        Assert.Same(first, second);
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

    // ── IAppLog registration (ADR-033 part 1) ─────────────────────────────────
    //
    // IAppLog always writes regardless of DiagnosticsLevel — unlike IDiagnosticsLog above — but must
    // still never eagerly materialize the underlying JsonLinesDiagnosticsLog sink merely by being
    // resolved (ADR-023 R-12): only an actual Info/Warn/Error call may do that. Each test below points
    // the internal diagnosticsLogDirectory overload at its own throwaway temp directory, same as the
    // IDiagnosticsLog tests above.

    /// <summary>
    /// Build-configuration-agnostic: <see cref="IAppLog"/> resolves without throwing and as a singleton.
    /// </summary>
    [Fact]
    public void RegisterServices_IAppLog_ResolvesAsSingleton()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            services.RegisterServices(tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IAppLog>();
            var second = serviceProvider.GetRequiredService<IAppLog>();

            Assert.NotNull(first);
            Assert.Same(first, second);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// R-12: merely resolving <see cref="IAppLog"/> — without ever calling <see cref="IAppLog.Info"/>/
    /// <see cref="IAppLog.Warn"/>/<see cref="IAppLog.Error"/> — must never create the log directory, let
    /// alone a JSONL file in it. Only an actual write may materialize the underlying sink.
    /// </summary>
    [Fact]
    public void RegisterServices_IAppLog_ResolvingAloneDoesNotCreateLogDirectory()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            services.RegisterServices(tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            var appLog = serviceProvider.GetRequiredService<IAppLog>();

            Assert.NotNull(appLog);
            Assert.False(Directory.Exists(tempDir), "Resolving IAppLog must not materialize the JSONL sink.");
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// <see cref="IAppLog"/> writes to the shared sink even when <see cref="DiagnosticsSwitch"/> resolves
    /// to <see cref="DiagnosticsLevel.Standard"/> (the Release-without-flags default) — this is exactly
    /// what distinguishes it from <see cref="IDiagnosticsLog"/>. Skipped in a Debug build of this test
    /// project (see the class remarks) — Debug always resolves Verbose, so it cannot exercise the
    /// Standard-level path; the Release-only counterpart below covers that instead. Either way,
    /// materializing the sink via an actual Info() call must produce a real JSONL session file, sharing
    /// the very same directory as IDiagnosticsLog would use.
    /// </summary>
    [Fact]
    public void RegisterServices_IAppLog_InfoCallMaterializesSinkAndWritesFile()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            services.RegisterServices(tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            var appLog = serviceProvider.GetRequiredService<IAppLog>();
            appLog.Info("Test", "Probe");

            Assert.True(
                WaitForFileInDirectory(tempDir, TimeSpan.FromSeconds(5)),
                "IAppLog.Info() must eventually materialize the JSONL sink and write a session file.");
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// In <see cref="DiagnosticsLevel.Standard"/>, <see cref="IDiagnosticsLog"/> still resolves to the
    /// zero-cost <see cref="NullDiagnosticsLog"/> even after <see cref="IAppLog"/> has materialized the
    /// shared sink (proving the two facades are independently gated, sharing only the underlying sink
    /// instance when both happen to be active).
    /// </summary>
#if DEBUG
    [Fact(Skip = "Debug builds always resolve DiagnosticsLevel.Verbose (see DiagnosticsSwitchTests remarks) — this path only exists in Release.")]
#else
    [Fact]
#endif
    public void RegisterServices_StandardLevel_IDiagnosticsLogStaysNull_EvenAfterIAppLogWrites()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        try
        {
            var services = new ServiceCollection();
            services.RegisterServices(tempDir);
            using var serviceProvider = services.BuildServiceProvider();

            serviceProvider.GetRequiredService<IAppLog>().Info("Test", "Probe");
            var diagnosticsLog = serviceProvider.GetRequiredService<IDiagnosticsLog>();

            Assert.Same(NullDiagnosticsLog.Instance, diagnosticsLog);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

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

    // ── Background icon loading (ADR-046, M9) ─────────────────────────────────
    //
    // IIconLoadQueue/IItemProbeQueue registration end-to-end: singleton, no worker thread started merely
    // by resolving the queue (D-14), and IItemProbeQueue resolves to the real BackgroundItemProbeQueue
    // (7c) — thread-pool based, never faulting except on cancellation (L6's original stub-fault concern
    // no longer applies once the real implementation landed).

    /// <summary>
    /// <see cref="IIconLoadQueue"/> resolves as the real <see cref="StaIconLoadQueue"/> singleton — the
    /// same instance backs every resolution, and merely resolving it starts no worker thread (D-14): one
    /// only ever spins up lazily on the first <see cref="IIconLoadGroup.Submit"/>.
    /// </summary>
    [Fact]
    public void RegisterServices_IIconLoadQueue_ResolvesAsSingletonAndStartsNoWorkerThreads()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        using var serviceProvider = services.BuildServiceProvider();

        var first = serviceProvider.GetRequiredService<IIconLoadQueue>();
        var second = serviceProvider.GetRequiredService<IIconLoadQueue>();

        Assert.Same(first, second);
        var queue = Assert.IsType<StaIconLoadQueue>(first);
        Assert.False(queue.HasCreatedAnyWorker);
    }

    /// <summary>
    /// <see cref="IItemProbeQueue"/> resolves as the real <see cref="BackgroundItemProbeQueue"/> singleton
    /// (7c) — the same instance backs every resolution.
    /// </summary>
    [Fact]
    public void RegisterServices_IItemProbeQueue_ResolvesAsSingleton()
    {
        var first = _serviceProvider.GetRequiredService<IItemProbeQueue>();
        var second = _serviceProvider.GetRequiredService<IItemProbeQueue>();

        Assert.Same(first, second);
        Assert.IsType<BackgroundItemProbeQueue>(first);
    }

    /// <summary>
    /// The real <see cref="BackgroundItemProbeQueue"/> (7c) actually probes: a path that exists on disk
    /// resolves <c>Exists = true</c>, proving the DI graph wires <see cref="IPathProbe"/> through
    /// correctly end-to-end (not just that SOME <see cref="IItemProbeQueue"/> was registered).
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RegisterServices_IItemProbeQueue_ProbeAsync_ProbesRealPaths()
    {
        var probeQueue = _serviceProvider.GetRequiredService<IItemProbeQueue>();

        var results = await probeQueue.ProbeAsync([Environment.SystemDirectory], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Exists);
        Assert.True(result.IsDirectory);
    }

    /// <summary>
    /// Disposing the root <see cref="ServiceProvider"/> after resolving <see cref="IIconLoadQueue"/> must
    /// never throw, even though nothing ever called <see cref="IIconLoadGroup.Submit"/> (no worker thread
    /// was ever created for <see cref="IDisposable.Dispose"/> to tear down).
    /// </summary>
    [Fact]
    public void RegisterServices_IIconLoadQueue_ProviderDisposal_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.RegisterServices();
        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<IIconLoadQueue>();

        var exception = Record.Exception(serviceProvider.Dispose);

        Assert.Null(exception);
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

    // ── Settings-window redesign (docs/PLANS/settings-window-redesign.md §5.2 item 6): every new
    // WP-A/WP-B/WP-C ViewModel/service must resolve end-to-end through the real UI service collection,
    // not just under the hand-picked Moq graphs the unit tests above use. ──────────────────────────────

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel"/> (the settings window's sidebar-navigation VM),
    /// <see cref="IPanelCustomizeService"/> (the real implementation, not the pre-WP-B stub),
    /// <see cref="IInputDialogService"/>, and <see cref="LayoutSnapshotsViewModel"/> all resolve without
    /// throwing through the full UI service collection.
    /// </summary>
    [Fact]
    public void UiConfigureServices_SettingsWindowRedesignRegistrations_ResolveCorrectly()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var mainWindowViewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
            var panelCustomizeService = serviceProvider.GetRequiredService<IPanelCustomizeService>();
            var inputDialogService = serviceProvider.GetRequiredService<IInputDialogService>();
            var layoutSnapshots = serviceProvider.GetRequiredService<LayoutSnapshotsViewModel>();

            Assert.NotNull(mainWindowViewModel);
            Assert.IsType<PanelCustomizeService>(panelCustomizeService);
            Assert.NotNull(inputDialogService);
            Assert.NotNull(layoutSnapshots);

            // MainWindowViewModel is a singleton that composes LayoutSnapshotsViewModel via
            // constructor injection (Configuration/DiConfig.cs) — the same singleton this test also
            // resolved directly, not a second independent instance.
            Assert.Same(layoutSnapshots, mainWindowViewModel.LayoutSnapshots);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// Verifies that <see cref="MainWindowViewModel"/> and <see cref="LayoutSnapshotsViewModel"/> both
    /// resolve as singletons (one settings window for the whole app).
    /// </summary>
    [Fact]
    public void UiConfigureServices_MainWindowViewModelAndLayoutSnapshots_ResolveAsSingletons()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var firstVm = serviceProvider.GetRequiredService<MainWindowViewModel>();
            var secondVm = serviceProvider.GetRequiredService<MainWindowViewModel>();
            var firstSnapshots = serviceProvider.GetRequiredService<LayoutSnapshotsViewModel>();
            var secondSnapshots = serviceProvider.GetRequiredService<LayoutSnapshotsViewModel>();

            Assert.Same(firstVm, secondVm);
            Assert.Same(firstSnapshots, secondSnapshots);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    // ── Localization (docs/PLANS/localization.md §4.8, WP-6 item 5) ──────────
    //
    // ILocalizationService itself already resolves as a singleton per
    // RegisterServices_ILocalizationService_ResolvesAsSingleton above (registered once in
    // DesktopOrganizer.Services' DiConfig, which UiDiConfig.ConfigureServices builds on top of). The
    // test below resolves every UI-layer consumer that takes ILocalizationService through its own
    // constructor — the tray, the page/system-icons managers, and the settings window's VM — from that
    // SAME provider, confirming none of them accidentally shadow the registration with a second one.

    /// <summary>
    /// Verifies that <see cref="ITrayIconManager"/>, <see cref="IPageManager"/>,
    /// <see cref="ISystemIconsPanelManager"/>, and <see cref="MainWindowViewModel"/> — every UI-layer
    /// consumer of <see cref="ILocalizationService"/> — resolve without throwing through the full UI
    /// service collection, and that <see cref="ILocalizationService"/> itself is still the one singleton
    /// instance backing all of them ("один экземпляр для трея и UI").
    /// </summary>
    [Fact]
    public void UiConfigureServices_LocalizationConsumers_ResolveCorrectly()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var localization = serviceProvider.GetRequiredService<ILocalizationService>();
            var trayIconManager = serviceProvider.GetRequiredService<ITrayIconManager>();
            var pageManager = serviceProvider.GetRequiredService<IPageManager>();
            var systemIconsPanelManager = serviceProvider.GetRequiredService<ISystemIconsPanelManager>();
            var mainWindowViewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();

            Assert.NotNull(trayIconManager);
            Assert.NotNull(pageManager);
            Assert.NotNull(systemIconsPanelManager);
            Assert.NotNull(mainWindowViewModel);
            Assert.Same(localization, serviceProvider.GetRequiredService<ILocalizationService>());
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    // ── Per-panel ViewModel composition (review M-4, backlog 1.x wave 4) ─────
    //
    // ChildWindowViewModel is registered as a plain AddTransient<ChildWindowViewModel>(): DI picks its
    // longest constructor and fills every OPTIONAL dependency it can resolve, silently leaving the rest at
    // their null defaults. A registration that goes missing (or a parameter type that drifts) therefore
    // never fails resolution — it just degrades the panel (no "Copy to page", synchronous icon/liveness
    // pipeline). These tests pin the composed graph instead of mere resolvability.

    /// <summary>
    /// Review M-4: resolved on an STA thread with a live <see cref="DispatcherSynchronizationContext"/>
    /// (as <c>ChildWindow</c> does in production), <see cref="ChildWindowViewModel"/> receives the singleton
    /// <see cref="IPanelCloneFlow"/>, <see cref="IWindowManager"/> and <see cref="IPageManager"/>, and uses
    /// the real <see cref="IIconLoadQueue"/>/<see cref="IItemProbeQueue"/> — not the synchronous D-13
    /// fallbacks (ADR-046).
    /// </summary>
    [Fact]
    public void UiConfigureServices_ChildWindowViewModel_OnUiThread_GetsCloneFlowManagersAndRealQueues()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            StaTestRunner.Run(() =>
            {
                // ChildWindowViewModel's constructor looks its item templates up in Application.Current.
                WpfApplicationHost.EnsureApplication();
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                try
                {
                    var services = new ServiceCollection();
                    UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
                    using var serviceProvider = services.BuildServiceProvider();

                    using var viewModel = serviceProvider.GetRequiredService<ChildWindowViewModel>();

                    Assert.Same(serviceProvider.GetRequiredService<IPanelCloneFlow>(), PrivateField<ChildWindowViewModel>(viewModel, "_panelCloneFlow"));
                    Assert.Same(serviceProvider.GetRequiredService<IWindowManager>(), PrivateField<ChildWindowViewModel>(viewModel, "_windowManager"));
                    Assert.Same(serviceProvider.GetRequiredService<IPageManager>(), PrivateField<ChildWindowViewModel>(viewModel, "_pageManager"));

                    Assert.False(viewModel.UsedSynchronousIconLoadQueueFallbackDespiteSupplied);
                    Assert.False(viewModel.UsedSynchronousItemProbeQueueFallbackDespiteSupplied);

                    // The flags above are also false when NO queue was supplied — pin the real instances too.
                    Assert.Same(serviceProvider.GetRequiredService<IItemProbeQueue>(), PrivateField<BaseViewModel>(viewModel, "_itemProbeQueue"));
                    Assert.IsType<StaIconLoadGroup>(PrivateField<BaseViewModel>(viewModel, "_iconLoadGroup"));
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            });
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// Review M-4 / design system 1.x phase 6.4: the tray-palette and native-menu-theme adapters resolve
    /// through the full UI service collection as singletons (resolving alone does not Start them).
    /// </summary>
    [Fact]
    public void UiConfigureServices_TrayAndNativeMenuThemeAdapters_ResolveAsSingletons()
    {
        var tempDir = NewTempDiagnosticsDirectory();
        var storageRoot = NewTempStorageRoot();
        try
        {
            var services = new ServiceCollection();
            UiDiConfig.ConfigureServices(services, tempDir, storageRoot);
            using var serviceProvider = services.BuildServiceProvider();

            var trayThemeAdapter = serviceProvider.GetRequiredService<TrayThemeAdapter>();
            var nativeMenuThemeAdapter = serviceProvider.GetRequiredService<NativeMenuThemeAdapter>();

            Assert.Same(trayThemeAdapter, serviceProvider.GetRequiredService<TrayThemeAdapter>());
            Assert.Same(nativeMenuThemeAdapter, serviceProvider.GetRequiredService<NativeMenuThemeAdapter>());
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(storageRoot);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static object? PrivateField<TDeclaring>(object instance, string name) =>
        (typeof(TDeclaring).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(TDeclaring).Name, name)).GetValue(instance);

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

    /// <summary>
    /// Polls until <paramref name="directory"/> exists and contains at least one file, or
    /// <paramref name="timeout"/> elapses. <c>JsonLinesDiagnosticsLog</c>'s background writer thread
    /// creates the directory/file asynchronously (see its own remarks), so a bare
    /// <see cref="Directory.Exists(string)"/> check right after a single <c>Write</c> call would be racy.
    /// </summary>
    private static bool WaitForFileInDirectory(string directory, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any())
                return true;

            Thread.Sleep(50);
        }

        return Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any();
    }
}
