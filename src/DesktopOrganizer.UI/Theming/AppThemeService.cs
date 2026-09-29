using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Theming;

/// <inheritdoc cref="IAppThemeService"/>
/// <remarks>
/// <para>
/// Resolution rule (docs/PLANS/design-system-1x.md §2.1, ADR-047 D-2): a Windows high-contrast theme
/// always wins (<see cref="AppTheme.HighContrast"/>); otherwise the answer follows
/// <see cref="SystemThemeSnapshot.AppsUseLightTheme"/> (the settings window/Customize/dialogs/menus follow
/// the Windows APP mode, not the system/taskbar mode the tray uses instead — see
/// <see cref="SystemThemeSnapshot"/>'s own remarks). <see cref="DefaultDarkThemeEnabled"/> is the gate that
/// opened this in the phase 6.5 closing commit, once every stock control consumer had its own styled
/// Palette T look (6.2a–6.4); while it is <see langword="false"/> (an internal test seam only — see the
/// test constructor below), the answer is forced to <see cref="AppTheme.Light"/> regardless of Windows'
/// own app mode. This is a code constant, not a user setting (CN-1 = A, G-1): nothing in
/// <see cref="DesktopOrganizer.UI.Services.IAppSettings"/> selects it.
/// </para>
/// <para>
/// On every resolved change this service builds a brand-new <see cref="ResourceDictionary"/>
/// (<see cref="ThemeResourceBuilder.Build"/>) and swaps it into
/// <see cref="System.Windows.Application.Resources"/>'s <see cref="ResourceDictionary.MergedDictionaries"/> —
/// added before the old one is removed, so there is never a frame with the slot entirely empty — which is
/// what makes every <c>{DynamicResource Theme.*}</c>/<c>{DynamicResource Font.Size.*}</c> consumer repaint
/// itself: WPF's own <c>DynamicResource</c> re-resolution runs off exactly this kind of
/// <see cref="ResourceDictionary.MergedDictionaries"/> collection-changed notification.
/// <see cref="Recompute"/> is idempotent — it is a no-op unless the resolved <see cref="AppTheme"/>, the
/// live <see cref="System.Windows.SystemFonts.MessageFontSize"/>, or <see cref="SystemThemeSnapshot.SystemColorsFingerprint"/>
/// actually changed since the last apply (the fingerprint check is what catches a switch between two
/// high-contrast schemes that both resolve to <see cref="AppTheme.HighContrast"/> — M-2, 6.1 review round).
/// </para>
/// <para>
/// <see cref="ISystemThemeProvider.Changed"/>/<see cref="SystemParameters.StaticPropertyChanged"/> handlers
/// both go through <c>defer</c> (production: <see cref="Dispatcher"/>.<c>BeginInvoke</c> on the UI
/// thread) before touching <see cref="Application.Resources"/> — <see cref="ISystemThemeProvider.Changed"/>
/// already documents itself as UI-thread-raised in this app (its listener window's message loop IS the UI
/// thread), but the defer is kept anyway: it is what lets tests drive this deterministically without a real
/// dispatcher pump, and it costs nothing extra on the already-correct production path.
/// </para>
/// </remarks>
public sealed class AppThemeService : IAppThemeService, IDisposable
{
    /// <summary>
    /// The production gate value (§2.1 D-2): open as of the phase 6.5 closing commit. See this class's own
    /// remarks for why this can only be overridden through the internal test constructor, never at runtime.
    /// </summary>
    internal const bool DefaultDarkThemeEnabled = true;

    private const string LogSource = "AppThemeService";

    private readonly ISystemThemeProvider _systemThemeProvider;
    private readonly Func<Action, bool> _defer;
    private readonly Func<bool> _highContrastProvider;
    private readonly Func<double> _messageFontSizeProvider;
    private readonly bool _darkThemeEnabled;
    private readonly IAppLog? _appLog;

    private bool _started;
    private bool _disposed;
    private bool _recomputePending;
    private ResourceDictionary? _appliedDictionary;
    private double _appliedMessageFontSize = double.NaN;
    private int _appliedSystemColorsFingerprint;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppThemeService"/> class for production use — the gate
    /// is <see cref="DefaultDarkThemeEnabled"/>, high contrast/font size are read live from
    /// <see cref="SystemParameters"/>/<see cref="SystemFonts"/>, and Windows-theme change handlers are
    /// marshalled onto the UI thread via <see cref="Application.Current"/>'s own <see cref="Dispatcher"/>.
    /// </summary>
    /// <param name="systemThemeProvider">Supplies <see cref="ISystemThemeProvider.Current"/>/<see cref="ISystemThemeProvider.Changed"/>.</param>
    /// <param name="appLog">Application log; falls back to the ambient <see cref="AppLog.Current"/> when <see langword="null"/>.</param>
    public AppThemeService(ISystemThemeProvider systemThemeProvider, IAppLog? appLog = null)
        : this(
            systemThemeProvider,
            DeferToUiThread,
            () => SystemParameters.HighContrast,
            () => SystemFonts.MessageFontSize,
            DefaultDarkThemeEnabled,
            appLog)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppThemeService"/> class — test-only overload that
    /// takes every ambient input as an explicit seam (the UI-thread deferral, the high-contrast/font-size
    /// providers, and, only here, the dark-theme gate) so <c>AppThemeServiceTests</c> can drive a
    /// deterministic, synchronous instance with the gate open without touching this class's real
    /// production constant.
    /// </summary>
    /// <param name="systemThemeProvider">Supplies <see cref="ISystemThemeProvider.Current"/>/<see cref="ISystemThemeProvider.Changed"/>.</param>
    /// <param name="defer">
    /// Runs a recompute — immediately (tests) or marshalled onto the UI thread (production). Returns
    /// <see langword="false"/> when the callback could not be queued at all (e.g. no live
    /// <see cref="Application.Current"/>/dispatcher already shutting down) — see <see cref="DeferToUiThread"/>
    /// and L-3 (6.5 review round).
    /// </param>
    /// <param name="highContrastProvider">Supplies the live <see cref="SystemParameters.HighContrast"/> value.</param>
    /// <param name="messageFontSizeProvider">Supplies the live <see cref="SystemFonts.MessageFontSize"/> value.</param>
    /// <param name="darkThemeEnabled">The gate value this instance resolves against.</param>
    /// <param name="appLog">Application log; falls back to the ambient <see cref="AppLog.Current"/> when <see langword="null"/>.</param>
    internal AppThemeService(
        ISystemThemeProvider systemThemeProvider,
        Func<Action, bool> defer,
        Func<bool> highContrastProvider,
        Func<double> messageFontSizeProvider,
        bool darkThemeEnabled,
        IAppLog? appLog = null)
    {
        ArgumentNullException.ThrowIfNull(systemThemeProvider);
        ArgumentNullException.ThrowIfNull(defer);
        ArgumentNullException.ThrowIfNull(highContrastProvider);
        ArgumentNullException.ThrowIfNull(messageFontSizeProvider);

        _systemThemeProvider = systemThemeProvider;
        _defer = defer;
        _highContrastProvider = highContrastProvider;
        _messageFontSizeProvider = messageFontSizeProvider;
        _darkThemeEnabled = darkThemeEnabled;
        _appLog = appLog;
    }

    /// <inheritdoc />
    public AppTheme Current { get; private set; } = AppTheme.Light;

    /// <inheritdoc />
    public event EventHandler<AppTheme>? ThemeChanged;

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        _systemThemeProvider.Start();
        _systemThemeProvider.Changed += OnSystemThemeChanged;
        SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;

        // Baseline apply — synchronous, on the calling (UI) thread, no event raised: there is no previous
        // subscriber to notify about the very first theme this process ever applies.
        Recompute(raiseEvent: false);
    }

    /// <summary>
    /// Unsubscribes from every event <see cref="Start"/> wired. Does not remove the already-applied
    /// dictionary from <see cref="Application.Resources"/> — the application is shutting down (the only
    /// real caller), and leaving the last-applied chrome resources in place is harmless.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
        {
            _systemThemeProvider.Changed -= OnSystemThemeChanged;
            SystemParameters.StaticPropertyChanged -= OnStaticPropertyChanged;
        }

        ThemeChanged = null;
    }

    /// <summary>
    /// Pure resolution rule (no I/O) — exposed so <c>ThemeResolutionTests</c>-style pure-function coverage
    /// doesn't need a full <see cref="AppThemeService"/> instance.
    /// </summary>
    /// <param name="snapshot">The current Windows colour-mode snapshot.</param>
    /// <param name="highContrast">The live <see cref="SystemParameters.HighContrast"/> value.</param>
    /// <param name="darkThemeEnabled">The gate value (<see cref="DefaultDarkThemeEnabled"/> in production).</param>
    internal static AppTheme Resolve(SystemThemeSnapshot snapshot, bool highContrast, bool darkThemeEnabled)
    {
        if (highContrast || snapshot.HighContrast) return AppTheme.HighContrast;
        if (!darkThemeEnabled) return AppTheme.Light;
        return snapshot.AppsUseLightTheme ? AppTheme.Light : AppTheme.Dark;
    }

    /// <summary>
    /// Production UI-thread deferral — see this class's own remarks. Returns <see langword="false"/> (and
    /// never queues <paramref name="action"/>) when there is no live <see cref="Application.Current"/> or
    /// its <see cref="Dispatcher"/> is already shutting down/has shut down — L-3 (6.5 review round): callers
    /// that track a "recompute pending" flag must be told this so they don't leave it stuck set forever with
    /// nothing left to clear it.
    /// </summary>
    private static bool DeferToUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return false;

        dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        return true;
    }

    private void OnSystemThemeChanged(object? sender, SystemThemeSnapshot snapshot) => _defer(() => Recompute(raiseEvent: true));

    /// <summary>
    /// M-2/M-1-UI fix (6.1 review round): reacts to ANY <see cref="SystemParameters.StaticPropertyChanged"/>
    /// notification, not just <see cref="SystemParameters.HighContrast"/> — WPF does not guarantee which
    /// <see cref="PropertyChangedEventArgs.PropertyName"/> a given <c>WM_SETTINGCHANGE</c>/
    /// <c>WM_SYSCOLORCHANGE</c> maps to (a text-size or system-colour change can surface under a property
    /// name other than the one that actually matters here), so narrowing by name risks silently missing a
    /// real font-size/HC-palette change. <see cref="_recomputePending"/> coalesces the burst of
    /// near-simultaneous notifications Windows sends for a single user action into exactly one deferred
    /// <see cref="Recompute"/> call, mirroring <c>DesktopOrganizer.Services.Tools.SystemThemeProvider</c>'s
    /// own debounce on the native side. L-3 (6.5 review round): if <see cref="_defer"/> reports it could not
    /// queue the callback at all, the flag is cleared right away instead of staying stuck.
    /// </summary>
    private void OnStaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_recomputePending) return;
        _recomputePending = true;

        var queued = _defer(() =>
        {
            _recomputePending = false;
            Recompute(raiseEvent: true);
        });

        // L-3 (6.5 review round): nothing will ever run the callback above (and clear the flag) when the
        // defer itself failed to queue it — e.g. `Application.Current` is already gone or its `Dispatcher`
        // is shutting down. Without this, `_recomputePending` would stay stuck `true` forever and silently
        // swallow every later `StaticPropertyChanged` notification for the remainder of the process.
        if (!queued)
            _recomputePending = false;
    }

    /// <summary>
    /// Re-resolves the theme and, only if the resolved <see cref="AppTheme"/>, the live message font size,
    /// or the active <see cref="SystemThemeSnapshot.SystemColorsFingerprint"/> actually changed, rebuilds
    /// and re-applies the dictionary. A disposed instance ignores a recompute that was already queued before
    /// <see cref="Dispose"/> ran — same guard shape as <see cref="Lifecycle.TrayPagesAdapter"/>'s own
    /// deferred handlers.
    /// </summary>
    /// <remarks>
    /// M-2 fix (6.1 review round): the fingerprint check closes a real gap — switching between two
    /// <see cref="AppTheme.HighContrast"/> schemes (e.g. Aquatic → Desert) resolves to the same
    /// <see cref="AppTheme"/> value both times, so the old two-dimension check (<c>theme</c> +
    /// <c>messageFontSize</c>) treated it as a no-op and left the STALE <see cref="SystemColors"/>-built
    /// dictionary applied even though the actual system colours had changed underneath it.
    /// <see cref="SystemThemeSnapshot.SystemColorsFingerprint"/> is what <see cref="ISystemThemeProvider"/>
    /// already computes for its own <see cref="ISystemThemeProvider.Changed"/> dedup — reusing it here (via
    /// <see cref="ISystemThemeProvider.Current"/>) needs no new native plumbing.
    /// </remarks>
    private void Recompute(bool raiseEvent)
    {
        if (_disposed) return;

        var snapshot = _systemThemeProvider.Current;
        var resolved = Resolve(snapshot, _highContrastProvider(), _darkThemeEnabled);
        var messageFontSize = _messageFontSizeProvider();

        var themeUnchanged = resolved == Current && _appliedDictionary is not null;
        var fontSizeUnchanged = messageFontSize.Equals(_appliedMessageFontSize);
        var fingerprintUnchanged = snapshot.SystemColorsFingerprint == _appliedSystemColorsFingerprint;
        if (themeUnchanged && fontSizeUnchanged && fingerprintUnchanged) return;

        Current = resolved;
        _appliedMessageFontSize = messageFontSize;
        _appliedSystemColorsFingerprint = snapshot.SystemColorsFingerprint;

        if (!ApplyDictionary(resolved, messageFontSize)) return;

        Log.Info(LogSource, "ThemeApplied", new KeyValuePair<string, object?>("theme", resolved));

        if (raiseEvent)
            ThemeChanged?.Invoke(this, resolved);
    }

    /// <summary>
    /// Builds the dictionary for <paramref name="theme"/> and swaps it into
    /// <see cref="Application.Current"/>'s <see cref="ResourceDictionary.MergedDictionaries"/> — new one
    /// added first, old one removed after, so <c>DynamicResource</c> consumers never see an empty slot.
    /// </summary>
    /// <returns><see langword="false"/> when there is no live <see cref="Application"/> to apply to (never true in production).</returns>
    private bool ApplyDictionary(AppTheme theme, double messageFontSize)
    {
        var app = Application.Current;
        if (app is null)
        {
            Log.Warn(LogSource, "NoApplication");
            return false;
        }

        var dictionary = ThemeResourceBuilder.Build(theme, messageFontSize);
        var merged = app.Resources.MergedDictionaries;
        var previous = _appliedDictionary;

        merged.Add(dictionary);
        _appliedDictionary = dictionary;

        if (previous is not null)
            merged.Remove(previous);

        return true;
    }
}
