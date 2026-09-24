using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
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
    public static void ConfigureServices(this IServiceCollection services) =>
        ConfigureServices(services, diagnosticsLogDirectory: null);

    /// <summary>
    /// Test-only overload letting a caller override the directory the underlying
    /// <c>JsonLinesDiagnosticsLog</c> writes to (forwarded to <c>DesktopOrganizer.Services</c>'
    /// <see cref="DesktopOrganizer.Services.Configuration.DiConfig"/> internal overload), so tests
    /// exercising this UI service collection end-to-end don't touch the real per-user diagnostics log
    /// directory — and its retention cleanup — every run. <see langword="null"/> (the public,
    /// production path above) keeps the existing default.
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    /// <param name="diagnosticsLogDirectory">
    /// Directory to use for the diagnostics log instead of its real per-user default, or
    /// <see langword="null"/> to keep the default.
    /// </param>
    internal static void ConfigureServices(this IServiceCollection services, string? diagnosticsLogDirectory) =>
        ConfigureServices(services, diagnosticsLogDirectory, storageRoot: null);

    /// <summary>
    /// Test-only overload additionally letting a caller override the versioned JSON application-state
    /// store's storage root (ADR-024 D-4), forwarded to <c>DesktopOrganizer.Services</c>' internal
    /// three-argument <see cref="DiConfig"/> overload — mirrors <paramref name="diagnosticsLogDirectory"/>'s
    /// existing pattern so DI-composition tests never touch the real per-user
    /// <c>%LOCALAPPDATA%\DesktopOrganizer\State</c>.
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    /// <param name="diagnosticsLogDirectory">See the two-argument overload.</param>
    /// <param name="storageRoot">
    /// Root folder to use instead of <c>%LOCALAPPDATA%\DesktopOrganizer</c>, or <see langword="null"/>
    /// to keep the default.
    /// </param>
    internal static void ConfigureServices(this IServiceCollection services, string? diagnosticsLogDirectory, string? storageRoot)
    {
        services.RegisterServices(diagnosticsLogDirectory, storageRoot);

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();        // Settings VM — no file state
        services.AddTransient<ChildWindowViewModel>();       // Per-panel VM — has file state
        services.AddSingleton<LayoutSnapshotsViewModel>();  // Snapshots section VM

        services.AddSingleton<IConfirmationService, ConfirmationService>();

        // Transient: one instance per ChildWindow (itself transient), so per-gesture throttling state
        // never needs to be keyed by panel — see GestureDiagnostics' remarks.
        services.AddTransient<IGestureDiagnostics, GestureDiagnostics>();
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IAutostartService, AutostartService>();

        // Versioned JSON application-state store, UI-layer facade (ADR-024 §2.3, C6). Singleton so the
        // same instance backs Program.Main's one-time Load() call and every later RequestSave()/
        // SaveNow() from the four consumers below. ILegacyStateImporter is registered here (not in
        // DesktopOrganizer.Services' DiConfig) because LegacySettingsImporter depends on IAppSettings,
        // a UI-layer type — DesktopOrganizer.Services.Storage.JsonApplicationStateStore only ever sees
        // it through the Services.Abstractions.Storage.ILegacyStateImporter interface, resolved lazily,
        // so this registration's position relative to RegisterServices() above never matters.
        services.AddSingleton<ILegacyStateImporter, LegacySettingsImporter>();
        services.AddSingleton<IApplicationStateService, ApplicationStateService>();

        services.AddSingleton<IWindowManager, WindowManager>();
        services.AddSingleton<IPanelSelectionCoordinator, PanelSelectionCoordinator>();

        // Global (process-wide, not per-panel) Edit Layout mode state, direct analogue of
        // IPanelSelectionCoordinator above (docs/PLANS/phase-6-edit-layout-ux.md §1 D-1).
        services.AddSingleton<ILayoutEditModeService, LayoutEditModeService>();
        services.AddSingleton<IPageManager, PageManagerService>();
        services.AddSingleton<ILayoutSnapshotService, LayoutSnapshotService>();
        services.AddSingleton<MainWindow>();   // Singleton: hidden, not re-created
        services.AddTransient<ChildWindow>();

        // System icons panel
        services.AddSingleton<ISystemIconsPanelManager>(sp =>
            new SystemIconsPanelManager(
                sp.GetRequiredService<IApplicationStateService>(),
                sp.GetRequiredService<IDesktopIntegrationService>(),
                sp.GetRequiredService<IMonitorService>(),
                sp.GetRequiredService<IVirtualDesktopService>(),
                () => sp.GetRequiredService<SystemIconsPanelWindow>()));
        services.AddTransient<SystemIconsPanelViewModel>(sp =>
            new SystemIconsPanelViewModel(
                sp.GetRequiredService<ISystemIconsPanelManager>().Model,
                sp.GetRequiredService<ISystemShellService>(),
                sp.GetRequiredService<IPanelSelectionCoordinator>()));
        services.AddTransient<SystemIconsPanelWindow>();
    }
}
