using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Keeps the classic Win32 menus of this process (the shell context menu of a desktop icon, ADR-040, and
/// the windows' system menus) on the same light/dark side as the application theme (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.10 / phase 6.4b, ADR-047 D-11): dark exactly when
/// <see cref="IAppThemeService.Current"/> is <see cref="AppTheme.Dark"/> — so the native menu of an icon
/// matches the WPF header menu of the same panel, respects the dark-theme gate, and falls back to the
/// Windows default in light and high-contrast themes.
/// </summary>
/// <remarks>
/// <see cref="IAppThemeService.ThemeChanged"/> is raised on the UI thread and <see cref="INativeMenuTheming.TryApply"/>
/// is two cheap, idempotent native calls at most, so no deferral is needed. A DI singleton, started from
/// <c>App.StartApp</c> right after <see cref="IAppThemeService.Start"/>, disposed with the service provider.
/// </remarks>
public sealed class NativeMenuThemeAdapter : IDisposable
{
    private readonly IAppThemeService _appThemeService;
    private readonly INativeMenuTheming _nativeMenuTheming;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeMenuThemeAdapter"/> class.
    /// </summary>
    /// <param name="appThemeService">Supplies the resolved application theme and its changes.</param>
    /// <param name="nativeMenuTheming">Switches the native menu mode (build-gated, no-op where unsupported).</param>
    public NativeMenuThemeAdapter(IAppThemeService appThemeService, INativeMenuTheming nativeMenuTheming)
    {
        ArgumentNullException.ThrowIfNull(appThemeService);
        ArgumentNullException.ThrowIfNull(nativeMenuTheming);

        _appThemeService = appThemeService;
        _nativeMenuTheming = nativeMenuTheming;
    }

    /// <summary>
    /// Subscribes to <see cref="IAppThemeService.ThemeChanged"/> and applies the current theme. Must be called
    /// on the UI thread after <see cref="IAppThemeService.Start"/>. Idempotent; a no-op after <see cref="Dispose"/>.
    /// </summary>
    public void Start()
    {
        if (_disposed || _started) return;

        _started = true;
        _appThemeService.ThemeChanged += OnThemeChanged;
        Apply(_appThemeService.Current);
    }

    /// <summary>Unsubscribes. The native menu mode already applied stays in effect for the rest of the process.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        if (_started)
        {
            _appThemeService.ThemeChanged -= OnThemeChanged;
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme) => Apply(theme);

    private void Apply(AppTheme theme)
    {
        if (_disposed) return;

        _nativeMenuTheming.TryApply(theme == AppTheme.Dark);
    }
}
