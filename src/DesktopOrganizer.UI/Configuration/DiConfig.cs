using System.Windows.Threading;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Configuration;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.Theming;
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
    /// storage root's <c>State</c> folder (<c>AppStorageRoot.Default</c>:
    /// Inno channel — <c>%LOCALAPPDATA%\DesktopOrganizer</c>,
    /// Store channel — <c>%USERPROFILE%\DesktopOrganizer</c>).
    /// </summary>
    /// <param name="services"><see cref="IServiceCollection"/> instance.</param>
    /// <param name="diagnosticsLogDirectory">See the two-argument overload.</param>
    /// <param name="storageRoot">
    /// Root folder to use instead of the real storage root (<c>AppStorageRoot.Default</c>:
    /// Inno channel — <c>%LOCALAPPDATA%\DesktopOrganizer</c>,
    /// Store channel — <c>%USERPROFILE%\DesktopOrganizer</c>),
    /// or <see langword="null"/> to keep the default.
    /// </param>
    internal static void ConfigureServices(this IServiceCollection services, string? diagnosticsLogDirectory, string? storageRoot)
    {
        services.RegisterServices(diagnosticsLogDirectory, storageRoot);

        // ViewModels

        // Settings VM — no file state. Takes an explicit CultureInfo (fix cycle 1 MINOR-2), not
        // auto-resolved by DI reflection — a factory lambda resolving every other dependency is used
        // instead of the parameterless AddSingleton<T>() this registration used before, mirroring the
        // sp.GetRequiredService(...) pattern already used for ISystemIconsPanelManager below.
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<IWindowManager>(),
            sp.GetRequiredService<IAppSettings>(),
            sp.GetRequiredService<IAutostartService>(),
            sp.GetRequiredService<IPageManager>(),
            sp.GetRequiredService<LayoutSnapshotsViewModel>(),
            sp.GetRequiredService<ISystemIconsPanelManager>(),
            sp.GetRequiredService<IConfirmationService>(),
            sp.GetRequiredService<IPanelItemStorageService>(),
            sp.GetRequiredService<ILayoutEditModeService>(),
            sp.GetRequiredService<IPanelCustomizeService>(),
            sp.GetRequiredService<ILocalizationService>(),
            SystemCulture.OriginalUiCulture,
            sp.GetRequiredService<AboutViewModel>(),
            sp.GetRequiredService<IColorPickerService>(),
            sp.GetRequiredService<IPanelCloneFlow>(),
            sp.GetRequiredService<IStatusNotifier>(),
            sp.GetRequiredService<IDesktopIconsService>(),
            appLog: null,
            storageOrphans: sp.GetRequiredService<ManagedStorageOrphansViewModel>()));
        services.AddTransient<ChildWindowViewModel>();       // Per-panel VM — has file state
        services.AddSingleton<LayoutSnapshotsViewModel>();  // Snapshots section VM
        services.AddSingleton<AboutViewModel>();            // About section VM (release-hardening-1.0.md §G2)
        services.AddSingleton<ManagedStorageOrphansViewModel>(); // Backup "Files without a panel" card (ADR-009 amendment 2026-10-10)
        services.AddSingleton<ICloudDropHintGate, CloudDropHintGate>(); // once-per-session cloud drop hint latch (same amendment)

        // Design system 1.x §3 phase 6.3 (docs/PLANS/design-system-1x.md §2.8, ADR-047 D-9): the
        // AppDialogWindow seam ConfirmationService/InputDialogService depend on instead of showing a real
        // Window themselves, plus the non-blocking status surface MainWindowViewModel.Status forwards to
        // MainWindow.xaml's InfoBar host. DispatcherDebounceTimer is constructed lazily here (not resolved
        // through DI itself — it has no registration of its own) so Application.Current already exists by
        // the time this factory runs, the same convention ApplicationStateService/ChildWindowViewModel's
        // own lazily-constructed debounce timers already follow.
        services.AddSingleton<IDialogPresenter, DialogPresenter>();

        // L-4 fix (review round): Application.Current is null in any host that never constructs a WPF
        // Application (e.g. a plain unit-test ServiceProvider built straight from RegisterServices) —
        // Dispatcher.CurrentDispatcher (the calling thread's own dispatcher, creating one if needed) avoids
        // a NullReferenceException the moment this factory runs outside a real App, at the cost of no
        // longer necessarily being the UI-thread dispatcher in that fallback case — acceptable here since
        // production always resolves this after OnStartup, when Application.Current already exists.
        services.AddSingleton<IStatusNotifier>(_ => new SettingsStatusNotifier(new DispatcherDebounceTimer(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)));

        // Factory lambdas (not the reflection-based AddSingleton<TService, TImplementation>() every other
        // registration in this file uses): both constructors are internal, since they take the internal
        // IDialogPresenter (ConfirmationService's own remarks) — the DI container's own reflection only
        // ever looks at PUBLIC constructors, so it could never find either one on its own.
        services.AddSingleton<IConfirmationService>(sp =>
            new ConfirmationService(sp.GetRequiredService<IDialogPresenter>(), sp.GetRequiredService<ILocalizationService>()));
        services.AddSingleton<IInputDialogService>(sp =>
            new InputDialogService(sp.GetRequiredService<IDialogPresenter>(), sp.GetRequiredService<ILocalizationService>()));
        services.AddSingleton<IExternalLinkLauncher, ExternalLinkLauncher>();

        // Design system 1.x §3 phase 6.1 (docs/PLANS/design-system-1x.md, ADR-047 D-2): the "Palette T"
        // (application chrome) theme singleton — Start()-ed from App.StartApp, right after
        // LoadResourceDictionaries(). ISystemThemeProvider/IWindowFrameTheming are DesktopOrganizer.Services
        // registrations (Services' own DiConfig).
        services.AddSingleton<IAppThemeService, AppThemeService>();

        // H-1/LOW-3 fixes (code review, panel clone to page, backlog 1.x wave 3 review): the shared
        // "Copy to page" flow (busy cursor + failure notification + stale-page re-sync) both
        // MainWindowViewModel and ChildWindowViewModel call instead of IWindowManager.ClonePanelToPage
        // directly — see IPanelCloneFlow's own remarks.
        services.AddSingleton<IBusyIndicator, MouseWaitCursorBusyIndicator>();
        services.AddSingleton<IPanelCloneFlow, PanelCloneFlow>();

        // "Customize panel…" dialog (docs/prompts/settings-window-redesign.md D-3, WP-B): the VM/window
        // are transient (a fresh one is built the first time the dialog opens, or after it was closed),
        // but IPanelCustomizeService itself is a singleton that keeps at most one open window alive and
        // re-targets it on a later Show() call — see PanelCustomizeService's own remarks.
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddTransient<PanelCustomizeViewModel>();
        services.AddTransient<PanelCustomizeWindow>();
        services.AddSingleton<IPanelCustomizeService>(sp =>
            new PanelCustomizeService(() => sp.GetRequiredService<PanelCustomizeWindow>(), sp.GetRequiredService<IWindowManager>()));

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

        // ADR-036 (release-hardening-1.0.md §E): process-wide, UI-thread-only coordination for a
        // panel-item drag-out gesture — direct analogue of IPanelSelectionCoordinator above.
        services.AddSingleton<IPanelItemDragOutSession, PanelItemDragOutSession>();

        // Global (process-wide, not per-panel) Edit Layout mode state, direct analogue of
        // IPanelSelectionCoordinator above (docs/PLANS/phase-6-edit-layout-ux.md §1 D-1).
        services.AddSingleton<ILayoutEditModeService, LayoutEditModeService>();
        services.AddSingleton<IPageManager, PageManagerService>();
        services.AddSingleton<ILayoutSnapshotService, LayoutSnapshotService>();

        // ADR-audit WP-B (B1): one-shot "this session is read-only" InfoBar notice, shown from App.StartApp.
        services.AddSingleton<ReadOnlySessionNotice>();

        // Store-publication B2: one-off "the app lives in the tray" balloon on the first run, shown from App.StartApp.
        services.AddSingleton<FirstRunTrayNotice>();

        // backlog-1x-master-plan.md §1/§2: round-robin page switching for the tray icon's double-click
        // and the tray context menu's "Pages" submenu (ADR-044). Singleton, IDisposable — constructed
        // once, Start()-ed from App.InitTrayIcon, and disposed implicitly with every other IDisposable
        // singleton when _serviceProvider.Dispose() runs.
        services.AddSingleton<TrayPagesAdapter>();

        // Design system 1.x phase 6.4/6.4b (ADR-047 D-10/D-11): the tray menu palette (Start()-ed from
        // App.InitTrayIcon, right after TrayPagesAdapter) and the dark/light mode of the classic Win32 menus
        // (Start()-ed from App.StartApp, right after IAppThemeService.Start()). Both IDisposable singletons,
        // disposed with the provider.
        services.AddSingleton<TrayThemeAdapter>();
        services.AddSingleton<NativeMenuThemeAdapter>();
        services.AddSingleton<MainWindow>();   // Singleton: hidden, not re-created
        services.AddTransient<ChildWindow>();

        // System icons panel
        services.AddSingleton<ISystemIconsPanelManager>(sp =>
            new SystemIconsPanelManager(
                sp.GetRequiredService<IApplicationStateService>(),
                sp.GetRequiredService<IDesktopIntegrationService>(),
                sp.GetRequiredService<IMonitorService>(),
                sp.GetRequiredService<IVirtualDesktopService>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IDesktopScaleProvider>(),
                () => sp.GetRequiredService<SystemIconsPanelWindow>()));
        services.AddTransient<SystemIconsPanelViewModel>(sp =>
            new SystemIconsPanelViewModel(
                sp.GetRequiredService<ISystemIconsPanelManager>().Model,
                sp.GetRequiredService<ISystemShellService>(),
                sp.GetRequiredService<IPanelSelectionCoordinator>(),
                sp.GetRequiredService<IWindowManager>()));
        services.AddTransient<SystemIconsPanelWindow>();
    }
}
