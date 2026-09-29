using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Keeps the tray context menu's style in step with the Windows theme (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.9 variant A, ADR-047 D-10) — the UI-side half of
/// <see cref="ITrayIconManager.SetMenuPalette"/>, by the same pattern as <see cref="TrayPagesAdapter"/>:
/// <see cref="ITrayIconManager"/> never reads the theme itself; this adapter resolves the palette
/// (<see cref="TrayMenuPaletteFactory.Resolve"/>) and pushes it down.
/// </summary>
/// <remarks>
/// Listens to both <see cref="ISystemThemeProvider.Changed"/> (the tray follows the Windows MODE,
/// <c>SystemUsesLightTheme</c>, which can change without the application theme changing) and
/// <see cref="IAppThemeService.ThemeChanged"/> (high contrast as WPF sees it). Both handlers only queue a
/// refresh (<see cref="DispatcherPriority.Background"/>): <see cref="ISystemThemeProvider.Changed"/> is
/// raised from inside a window procedure and must stay cheap, and Background runs after
/// <see cref="AppThemeService"/>'s own Normal-priority recompute, so <see cref="IAppThemeService.Current"/>
/// is already up to date when the palette is resolved. A DI singleton, started from <c>App.InitTrayIcon</c>
/// right after <see cref="ITrayIconManager.Initialize"/>, disposed with the service provider.
/// </remarks>
public sealed class TrayThemeAdapter : IDisposable
{
    private readonly ITrayIconManager _trayIconManager;
    private readonly IAppThemeService _appThemeService;
    private readonly ISystemThemeProvider _systemThemeProvider;
    private readonly Action<Action> _defer;
    private readonly bool _darkThemeEnabled;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayThemeAdapter"/> class.
    /// </summary>
    /// <param name="trayIconManager">The tray whose menu palette is kept in step.</param>
    /// <param name="appThemeService">Supplies the resolved application theme (high contrast) and <see cref="IAppThemeService.ThemeChanged"/>.</param>
    /// <param name="systemThemeProvider">Supplies the Windows mode (<c>SystemUsesLightTheme</c>) and <see cref="ISystemThemeProvider.Changed"/>.</param>
    public TrayThemeAdapter(ITrayIconManager trayIconManager, IAppThemeService appThemeService, ISystemThemeProvider systemThemeProvider)
        : this(trayIconManager, appThemeService, systemThemeProvider, DeferToUiThread, AppThemeService.DefaultDarkThemeEnabled)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayThemeAdapter"/> class — test-only overload with the
    /// UI-thread deferral and the dark-theme gate as explicit seams (same shape as
    /// <see cref="TrayPagesAdapter"/>/<see cref="AppThemeService"/>).
    /// </summary>
    /// <param name="trayIconManager">The tray whose menu palette is kept in step.</param>
    /// <param name="appThemeService">Supplies the resolved application theme.</param>
    /// <param name="systemThemeProvider">Supplies the Windows mode snapshot.</param>
    /// <param name="defer">Runs a refresh — immediately (tests) or queued onto the UI thread (production).</param>
    /// <param name="darkThemeEnabled">The dark-theme gate this instance resolves against.</param>
    internal TrayThemeAdapter(
        ITrayIconManager trayIconManager,
        IAppThemeService appThemeService,
        ISystemThemeProvider systemThemeProvider,
        Action<Action> defer,
        bool darkThemeEnabled)
    {
        ArgumentNullException.ThrowIfNull(trayIconManager);
        ArgumentNullException.ThrowIfNull(appThemeService);
        ArgumentNullException.ThrowIfNull(systemThemeProvider);
        ArgumentNullException.ThrowIfNull(defer);

        _trayIconManager = trayIconManager;
        _appThemeService = appThemeService;
        _systemThemeProvider = systemThemeProvider;
        _defer = defer;
        _darkThemeEnabled = darkThemeEnabled;
    }

    /// <summary>
    /// Subscribes to the theme events and applies the current palette synchronously (the caller is on the
    /// UI thread, right after <see cref="ITrayIconManager.Initialize"/>). Idempotent; a no-op after
    /// <see cref="Dispose"/>.
    /// </summary>
    public void Start()
    {
        if (_disposed || _started) return;

        _started = true;
        _appThemeService.ThemeChanged += OnAppThemeChanged;
        _systemThemeProvider.Changed += OnSystemThemeChanged;
        PushPalette();
    }

    /// <summary>
    /// Unsubscribes from every event <see cref="Start"/> wired. A refresh already queued still runs but
    /// re-checks the disposed flag first.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        if (_started)
        {
            _appThemeService.ThemeChanged -= OnAppThemeChanged;
            _systemThemeProvider.Changed -= OnSystemThemeChanged;
        }
    }

    /// <summary>Production UI-thread deferral — see this class's own remarks for the priority.</summary>
    private static void DeferToUiThread(Action action)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    private void OnAppThemeChanged(object? sender, AppTheme theme) => _defer(PushPalette);

    private void OnSystemThemeChanged(object? sender, SystemThemeSnapshot snapshot) => _defer(PushPalette);

    private void PushPalette()
    {
        if (_disposed) return;

        var palette = TrayMenuPaletteFactory.Resolve(_appThemeService.Current, _systemThemeProvider.Current, _darkThemeEnabled);
        _trayIconManager.SetMenuPalette(palette);
    }
}
