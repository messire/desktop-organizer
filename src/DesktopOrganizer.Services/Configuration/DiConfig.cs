using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Localization;
using DesktopOrganizer.Services.Providers;
using DesktopOrganizer.Services.SingleInstance;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Storage.Migrations;
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
    public static void RegisterServices(this IServiceCollection services) =>
        RegisterServices(services, diagnosticsLogDirectory: null);

    /// <summary>
    /// Test-only overload letting a caller override the directory <see cref="JsonLinesDiagnosticsLog"/>
    /// writes to, so tests exercising DI end-to-end (see <c>DiConfigTests</c>) don't touch the real
    /// per-user <see cref="DefaultDiagnosticsLogDirectory"/> — and its real retention cleanup — every
    /// run. <see langword="null"/> (the public, production path above) keeps the existing default.
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    /// <param name="diagnosticsLogDirectory">
    /// Directory to use for <see cref="JsonLinesDiagnosticsLog"/> instead of
    /// <see cref="DefaultDiagnosticsLogDirectory"/>, or <see langword="null"/> to keep the default.
    /// </param>
    internal static void RegisterServices(this IServiceCollection services, string? diagnosticsLogDirectory) =>
        RegisterServices(services, diagnosticsLogDirectory, storageRoot: null);

    /// <summary>
    /// Test-only overload additionally letting a caller override the storage root
    /// <see cref="StorageFileLock"/>/<see cref="JsonApplicationStateStore"/> use for the advisory lock
    /// file and the versioned JSON document (ADR-024 D-4/D-8), mirroring
    /// <paramref name="diagnosticsLogDirectory"/>'s existing pattern — so DI-composition tests (see
    /// <c>DiConfigTests</c>) never touch the real per-user <c>%LOCALAPPDATA%\DesktopOrganizer\State</c>
    /// or its <c>.storage-lock</c> file. <see langword="null"/> (both public paths above) keeps the
    /// real per-user storage root.
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    /// <param name="diagnosticsLogDirectory">See the two-argument overload.</param>
    /// <param name="storageRoot">
    /// Root folder to use instead of <c>%LOCALAPPDATA%\DesktopOrganizer</c>, or <see langword="null"/>
    /// to keep the default.
    /// </param>
    internal static void RegisterServices(this IServiceCollection services, string? diagnosticsLogDirectory, string? storageRoot)
    {
        // Localization (docs/PLANS/localization.md §2.2/§4.0). SystemCulture.OriginalUiCulture is what
        // "follow Windows" (IAppSettings.Language == "") resolves against — it must already have been
        // captured (Program.Main touches it as its very first line, before this container is even built)
        // so the snapshot predates any CurrentUICulture override. The initial language preference itself
        // is NOT applied here: DesktopOrganizer.Services has no reference to IAppSettings (a UI-layer
        // type) — App.StartApp calls ApplyLanguage(AppSettings.Language) once, before LoadResourceDictionaries.
        //
        // IDiagnosticsLog is resolved eagerly here (not the lazy Func<IDiagnosticsLog> pattern used for
        // ISingleInstanceCoordinator/IApplicationStateStore below) because this ILocalizationService
        // singleton itself is only ever resolved in a process that is already Primary: App.StartApp's
        // sp.GetRequiredService<ILocalizationService>() call (App.xaml.cs:282) is the only caller, and
        // App is only ever constructed after a RunAsPrimary decision. A Secondary process's own
        // MessageBox calls (Program.cs) go through the static LocalizationResources helper instead,
        // never through this DI registration, so there is no ADR-023 R-12 concern about a burst of
        // Secondary launches materializing the JSONL writer — the same footing as
        // InstallerShutdownRequestListener's own eager resolve further down (:203-205).
        services.AddSingleton<ILocalizationService>(sp =>
            new LocalizationService(SystemCulture.OriginalUiCulture, diagnosticsLog: sp.GetRequiredService<IDiagnosticsLog>()));

        // Utils
        services.AddSingleton<IIconHandler, IconHandler>();
        services.AddSingleton<IIconFactory, IconFactory>();
        services.AddSingleton<INativeApi, NativeApi>();
        services.AddSingleton<INotifyIconAdapter, NotifyIconAdapter>();

        // Diagnostics — off by default in Release (see DiagnosticsSwitch); the JSONL sink owns a
        // background writer thread, so it must be resolved as a singleton and disposed exactly once
        // at shutdown (IDisposable singletons are disposed automatically when the root
        // ServiceProvider is disposed — see App.OnExit).
        services.AddSingleton<IDiagnosticsLog>(_ =>
            DiagnosticsSwitch.IsEnabled(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable)
                ? new JsonLinesDiagnosticsLog(diagnosticsLogDirectory ?? DefaultDiagnosticsLogDirectory)
                : NullDiagnosticsLog.Instance);

        // Services
        services.AddSingleton<IIconProvider, IconProvider>();
        services.AddSingleton<ITrayIconManager, TrayIconManager>();
        services.AddSingleton<IDesktopIntegrationService, DesktopIntegrationService>();
        services.AddSingleton<IFileLauncher, FileLauncher>();
        services.AddSingleton<IDesktopIconsService, DesktopIconsService>();
        services.AddSingleton<IDesktopGestureService, DesktopGestureService>();
        services.AddSingleton<IFolderWatcherFactory, FolderWatcherFactory>();
        services.AddSingleton<ISystemShellService, SystemShellService>();
        services.AddSingleton<IPanelItemStorageService, PanelItemStorageService>();
        services.AddSingleton<IFileItemRenameService, FileItemRenameService>();

        // Multi-monitor: self-contained service (owns its own WM_DISPLAYCHANGE listener).
        services.AddSingleton<IMonitorService, MonitorService>();

        // Windows virtual desktops (Task View) — NOT physical monitors. Degrades silently
        // to IsSupported == false when the shell COM object is unavailable.
        services.AddSingleton<IVirtualDesktopService, VirtualDesktopService>();

        // Native panel sizing (ADR-022 / Phase 2). Stateless singleton: it holds one hook per attached
        // panel HWND and is inert until a panel calls Attach — which only happens behind the
        // NativePanelSizing feature flag (default off).
        services.AddSingleton<INativePanelSizingService, NativePanelSizingService>();

        // Versioned JSON application-state store (ADR-024, Phase 4). The migration chain is registered
        // as ISettingsMigration entries, not a StateMigrationRunner directly — JsonApplicationStateStore's
        // production constructor builds its own StateMigrationRunner from IEnumerable<ISettingsMigration>
        // (see its remarks), so the runner itself never needs its own DI registration.
        services.AddSingleton<ISettingsMigration, V1ToV2Migration>();
        services.AddSingleton<ISettingsMigration, V2ToV3Migration>();
        services.AddSingleton<ISettingsMigration, V3ToV4Migration>();
        services.AddSingleton<ISettingsMigration, V4ToV5Migration>();

        // Advisory storage-root lock (ADR-024 D-8, closes ADR-023 R-15). Singleton so the same instance
        // backs both Program.Main's TryAcquire() and IApplicationStateStore.IsWritable's live read of
        // IStorageLock.IsHeld — disposed automatically with the root ServiceProvider.
        //
        // diagnostics is a lazily-resolved Func<IDiagnosticsLog>, same ADR-023 R-12 pattern as
        // ISingleInstanceCoordinator below: a process that degrades to a read-only session (failed
        // storage lock) must not be forced to materialize the JSONL log writer just to construct this
        // store. ILegacyStateImporter is registered in DesktopOrganizer.UI's DiConfig (LegacySettingsImporter
        // depends on IAppSettings, a UI-layer type) — resolved lazily here too, so registration order
        // between the two ConfigureServices/RegisterServices calls never matters.
        if (storageRoot is null)
        {
            services.AddSingleton<IStorageLock, StorageFileLock>();
            services.AddSingleton<IApplicationStateStore>(sp => new JsonApplicationStateStore(
                sp.GetRequiredService<IStorageLock>(),
                sp.GetRequiredService<ILegacyStateImporter>(),
                sp.GetRequiredService<IEnumerable<ISettingsMigration>>(),
                () => sp.GetRequiredService<IDiagnosticsLog>()));

            // Manual recovery switch (ADR-024 §7 scenario C, --restore-state-backup). Standalone —
            // never resolved as part of the normal Program.Main startup ordering, only when that
            // command-line switch is present (see Program.cs).
            services.AddSingleton<IStateRestoreCommand>(sp => new StateRestoreCommand(
                sp.GetRequiredService<IEnumerable<ISettingsMigration>>(),
                () => sp.GetRequiredService<IDiagnosticsLog>()));
        }
        else
        {
            services.AddSingleton<IStorageLock>(_ =>
                new StorageFileLock(new StateStorePaths(storageRoot), maxAttempts: 3, retryDelay: TimeSpan.FromMilliseconds(150)));
            services.AddSingleton<IApplicationStateStore>(sp => new JsonApplicationStateStore(
                new StateStorePaths(storageRoot),
                sp.GetRequiredService<IStorageLock>(),
                sp.GetRequiredService<ILegacyStateImporter>(),
                new StateMigrationRunner(sp.GetRequiredService<IEnumerable<ISettingsMigration>>()),
                () => sp.GetRequiredService<IDiagnosticsLog>()));

            services.AddSingleton<IStateRestoreCommand>(sp => new StateRestoreCommand(
                new StateStorePaths(storageRoot),
                new StateMigrationRunner(sp.GetRequiredService<IEnumerable<ISettingsMigration>>()),
                () => sp.GetRequiredService<IDiagnosticsLog>()));
        }

        // Single-instance handshake (ADR-023). Registered as a singleton so the same coordinator
        // instance backs both Program.Main's Start()/TryBeginServing() call and App.OnExit's
        // EnterShutdown() — disposed automatically with the root ServiceProvider (see App.OnExit),
        // which is also what stops the activation server and releases both mutexes (ADR-023 D-9).
        //
        // diagnostics is a lazily-resolved Func<IDiagnosticsLog>, not IDiagnosticsLog directly: a
        // process that never becomes Primary must never force DI to materialize the JSONL log writer
        // (ADR-023 R-12) — a burst of secondary launches must not evict the Primary's own session log
        // from its file-count retention.
        services.AddSingleton<ISingleInstanceCoordinator>(sp =>
        {
            var userKey = SingleInstanceNaming.UserKeyHash(
                WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName.ToUpperInvariant());
            var sessionId = Process.GetCurrentProcess().SessionId;
            var pipeName = SingleInstanceNaming.ActivationPipeName(userKey);

            // The activation client uses INativeApi for exactly one call — the AllowSetForegroundWindow
            // handoff that lets the primary's window actually come forward (ADR-023 D-8/R-13). Resolving
            // it eagerly here is free of the R-12 concern that applies to IDiagnosticsLog: NativeApi is a
            // stateless P/Invoke wrapper that opens no files and starts no threads.
            var nativeApi = sp.GetRequiredService<INativeApi>();

            return new SingleInstanceCoordinator(
                guard: new MutexGate(SingleInstanceNaming.GuardMutexName(userKey)),
                detection: new MutexGate(SingleInstanceNaming.InstallerDetectionMutexName),
                server: new NamedPipeActivationServer(pipeName, sessionId),
                client: new NamedPipeActivationClient(pipeName, nativeApi),
                policy: SingleInstanceHandshakePolicy.Default,
                nowMs: () => Environment.TickCount64,
                sessionId: sessionId,
                diagnostics: () => sp.GetRequiredService<IDiagnosticsLog>());
        });

        // Installer-initiated graceful shutdown. The event name is a contract with
        // installer/DesktopOrganizer.iss's "#define AppShutdownEventName" (pinned by
        // InstallerMutexContractTests) — SetEvent there is what this listener wakes up for. Singleton so
        // App.OnExit's container disposal is what tears it down; unlike ISingleInstanceCoordinator above,
        // IDiagnosticsLog is resolved eagerly (not through a lazy Func) because this listener only ever
        // exists in a process that is already Primary (App.StartApp only runs there), so there is no
        // ADR-023 R-12 concern about a burst of Secondary launches materializing the JSONL writer.
        services.AddSingleton<IShutdownRequestListener>(sp => new InstallerShutdownRequestListener(
            SingleInstanceNaming.InstallerShutdownRequestEventName,
            sp.GetRequiredService<IDiagnosticsLog>()));
    }

    /// <summary>
    /// Per-user folder <see cref="JsonLinesDiagnosticsLog"/> writes its JSONL session files to.
    /// </summary>
    private static string DefaultDiagnosticsLogDirectory =>
        Path.Combine(Storage.AppStorageRoot.Default, "Logs");
}
