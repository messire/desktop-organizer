using System.Windows;
using System.Windows.Interop;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Keeps one chrome <see cref="Window"/>'s DWM title bar/border in step with
/// <see cref="IAppThemeService.Current"/> (docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1, ADR-047 D-2):
/// applies <see cref="IWindowFrameTheming.TryApplyImmersiveDarkMode"/> on <see cref="Window.SourceInitialized"/>
/// (the earliest point a real <c>HWND</c> exists) and again on every later
/// <see cref="IAppThemeService.ThemeChanged"/>. Intended for the chrome window family only (the settings
/// window, Customize, dialogs) — never <c>ChildWindow</c>, which is borderless, lives in the desktop
/// layer, and has no DWM caption of its own to theme.
/// </summary>
/// <remarks>
/// Self-disposing: subscribes to <see cref="Window.Closed"/> and detaches itself there, so a
/// short-lived, transient chrome window (e.g. the pre-6.3 <c>InputDialog</c>, shown/closed once per
/// prompt) never keeps itself alive for the rest of the process merely because
/// <see cref="IAppThemeService"/> — a DI singleton — still holds a <see cref="IAppThemeService.ThemeChanged"/>
/// subscription pointing back at it. Harmless no-op for a window that is Hidden rather than Closed
/// (<c>MainWindow</c>, <c>PanelCustomizeWindow</c> while reused by <c>PanelCustomizeService</c>) — those
/// simply keep receiving theme updates for their whole, intentionally long, lifetime.
/// </remarks>
public sealed class ThemedWindowBehavior : IDisposable
{
    private readonly Window _window;
    private readonly IAppThemeService _appThemeService;
    private readonly IWindowFrameTheming _windowFrameTheming;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemedWindowBehavior"/> class and wires every event
    /// listed in this class's own remarks. Construct once, from the window's own constructor — there is no
    /// separate "start" step.
    /// </summary>
    /// <param name="window">The chrome window to theme.</param>
    /// <param name="appThemeService">Supplies <see cref="IAppThemeService.Current"/>/<see cref="IAppThemeService.ThemeChanged"/>.</param>
    /// <param name="windowFrameTheming">Applies the actual DWM attribute.</param>
    public ThemedWindowBehavior(Window window, IAppThemeService appThemeService, IWindowFrameTheming windowFrameTheming)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(appThemeService);
        ArgumentNullException.ThrowIfNull(windowFrameTheming);

        _window = window;
        _appThemeService = appThemeService;
        _windowFrameTheming = windowFrameTheming;

        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnClosed;
        _appThemeService.ThemeChanged += OnThemeChanged;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _appThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Apply(_appThemeService.Current);

    private void OnThemeChanged(object? sender, AppTheme theme) => Apply(theme);

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    /// <summary>
    /// Never dark for <see cref="AppTheme.HighContrast"/> — HC already replaces every
    /// <c>{DynamicResource Theme.*}</c> with a <see cref="System.Windows.SystemColors"/> brush; forcing
    /// the DWM frame dark on top of that would fight the very system palette HC is supposed to guarantee.
    /// </summary>
    private void Apply(AppTheme theme)
    {
        if (_disposed) return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return; // Not sourced yet — SourceInitialized will call this again.

        _windowFrameTheming.TryApplyImmersiveDarkMode(hwnd, dark: theme == AppTheme.Dark);
    }
}
