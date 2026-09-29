using System.Globalization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="ISystemThemeProvider"/>
/// <remarks>
/// <para>
/// Owns its <b>own</b> hidden top-level listener window (not <see cref="MonitorService"/>'s or
/// <see cref="ExplorerRestartMonitor"/>'s — the listeners stay decoupled, same rule as ADR-034). An
/// ordinary invisible <c>WS_POPUP</c> window is used because message-only windows do not receive
/// broadcasts (see <c>NativeApi.CreateMessageWindow</c>).
/// </para>
/// <para>
/// Messages are deliberately not filtered by their <c>lParam</c> string (<c>"ImmersiveColorSet"</c>): a
/// high-contrast switch arrives as <c>SPI_SETHIGHCONTRAST</c> with a different (or no) string, and reading
/// the string would mean dereferencing a foreign pointer inside the window procedure. Instead every
/// <c>WM_SETTINGCHANGE</c>/<c>WM_SYSCOLORCHANGE</c>/<c>WM_THEMECHANGED</c> (re)starts a
/// <see cref="DebounceIntervalMs"/> <c>WM_TIMER</c> on the same window; when it fires the snapshot is
/// re-read once (two registry values, the high-contrast <c>SystemParametersInfo</c> query,
/// <see cref="FingerprintColorIndices"/>.Length <c>GetSysColor</c> calls and one
/// <c>SPI_GETNONCLIENTMETRICS</c> query) and <see cref="Changed"/> is raised only if it differs. Unrelated
/// broadcasts therefore cost one cheap read and raise nothing. The system-colour fingerprint is what makes a
/// high-contrast → high-contrast switch (<c>WM_SYSCOLORCHANGE</c>, flags unchanged) observable, and the
/// message font size does the same for a text-size change (<c>WM_SETTINGCHANGE</c> with
/// <c>SPI_SETNONCLIENTMETRICS</c>).
/// </para>
/// <para>
/// The window procedure does the minimum, is wrapped in <c>try</c>/<c>catch</c> and always falls through
/// to <c>DefWindowProc</c>, so neither a registry failure nor a faulty subscriber can break message
/// processing. Everything runs on the thread that called <see cref="Start"/> — no thread hop, no lock
/// other than the published snapshot reference.
/// </para>
/// <para>
/// MANUAL VERIFY (Win10 22H2 / Win11 24H2): Settings → Personalization → Colors, switch "app mode" and
/// "Windows mode" separately and toggle a contrast theme — one <c>SystemThemeChanged</c> log line per
/// switch, with the matching flags; switch between two contrast themes (Aquatic → Desert) — one line with a
/// new <c>systemColorsFingerprint</c>; Accessibility → Text size — one line with a new
/// <c>messageFontSize</c>.
/// </para>
/// </remarks>
internal sealed class SystemThemeProvider(INativeApi nativeApi, IAppLog? appLog = null) : ISystemThemeProvider
{
    /// <summary>Registry key (below <c>HKCU</c>) holding the two colour-mode values.</summary>
    internal const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>"Choose your app mode": 1 = light, 0 = dark.</summary>
    internal const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    /// <summary>"Choose your Windows mode": 1 = light, 0 = dark.</summary>
    internal const string SystemUsesLightThemeValueName = "SystemUsesLightTheme";

    /// <summary><c>WM_SETTINGCHANGE</c> (<c>ImmersiveColorSet</c>, <c>SPI_SETHIGHCONTRAST</c>, …).</summary>
    internal const int WmSettingChange = 0x001A;

    /// <summary><c>WM_SYSCOLORCHANGE</c> — accompanies a high-contrast switch.</summary>
    internal const int WmSysColorChange = 0x0015;

    /// <summary><c>WM_THEMECHANGED</c> — visual-style (theme) switch.</summary>
    internal const int WmThemeChanged = 0x031A;

    /// <summary><c>WM_TIMER</c>.</summary>
    internal const int WmTimer = 0x0113;

    /// <summary>
    /// Debounce interval: Windows sends a burst of broadcasts for one switch (plan §3 6.1: ~100 ms).
    /// </summary>
    internal const uint DebounceIntervalMs = 100;

    /// <summary>Id of the debounce timer on the listener window; any non-zero value is fine.</summary>
    internal static readonly IntPtr DebounceTimerId = new(0x5448);

    /// <summary>
    /// The <c>COLOR_*</c> indices folded into <see cref="SystemThemeSnapshot.SystemColorsFingerprint"/> —
    /// the Win32 counterparts of the <c>SystemColors</c> the high-contrast palette uses: <c>COLOR_WINDOW</c>
    /// (5), <c>COLOR_WINDOWTEXT</c> (8), <c>COLOR_HIGHLIGHT</c> (13), <c>COLOR_HIGHLIGHTTEXT</c> (14),
    /// <c>COLOR_BTNFACE</c> (15), <c>COLOR_BTNTEXT</c> (18), <c>COLOR_GRAYTEXT</c> (17), <c>COLOR_MENU</c> (4),
    /// <c>COLOR_3DLIGHT</c> (22).
    /// </summary>
    internal static readonly int[] FingerprintColorIndices = [5, 8, 13, 14, 15, 18, 17, 4, 22];

    private const string ClassNamePrefix = "DesktopOrganizer.ThemeWatcher.";
    private const string LogSource = "SystemThemeProvider";

    // Rooted for the lifetime of the window: the native side only holds an unmanaged thunk.
    private NativeWindowProc? _wndProc;
    private NativeMessageWindow _window;

    // Last published snapshot; null until Start() read the baseline. Written only on the listener
    // thread, read from any thread through Current — a reference swap, hence volatile is enough.
    private volatile SystemThemeSnapshot? _current;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<SystemThemeSnapshot>? Changed;

    /// <inheritdoc />
    public SystemThemeSnapshot Current
        => _window.IsValid && _current is { } cached ? cached : ReadSnapshot(fallback: _current);

    /// <inheritdoc />
    public bool IsListening => _window.IsValid;

    /// <summary>
    /// The log this instance writes to: the one explicitly supplied, falling back to the process-wide
    /// ambient <see cref="AppLog.Current"/> for tests that construct this type without a log.
    /// </summary>
    private IAppLog Log => appLog ?? AppLog.Current;

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed || _window.IsValid) return;

        try
        {
            var baseline = ReadSnapshot(fallback: null);
            _current = baseline;
            Log.Info(
                LogSource,
                "SystemThemeInitial",
                SnapshotFields(baseline));

            _wndProc = WndProc;
            _window = nativeApi.CreateMessageWindow(ClassNamePrefix + Guid.NewGuid().ToString("N"), _wndProc);
            if (!_window.IsValid)
            {
                // Degrade to "theme read once at startup" — visibly, not silently.
                Log.Warn(
                    LogSource,
                    "ListenerWindowCreateFailed",
                    new KeyValuePair<string, object?>("win32Error", _window.LastError));
                ReleaseWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "StartFailed", ex);
            ReleaseWindow();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Must run on the thread that called <see cref="Start"/>: <c>DestroyWindow</c> only succeeds on the
    /// window's owning thread. The regular shutdown path satisfies this (UI thread); a dispose from another
    /// thread (e.g. a crash handler or finalizer-driven teardown) fails to destroy the window — the failure
    /// is caught and logged (<c>ReleaseFailed</c>), never thrown — and the OS reclaims the window with the
    /// process, so this is harmless there.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseWindow();
        Changed = null;
    }

    /// <summary>
    /// Maps the raw registry values to the snapshot flags: an absent <c>AppsUseLightTheme</c> reads as
    /// light (the Windows default); an absent <c>SystemUsesLightTheme</c> falls back to the app mode
    /// (plan §2.1: tray → <c>SystemUsesLightTheme</c>, else <c>AppsUseLightTheme</c>, else light). Any
    /// non-zero value is light.
    /// </summary>
    /// <param name="appsUseLightTheme">Raw <c>AppsUseLightTheme</c>, or <see langword="null"/> when absent.</param>
    /// <param name="systemUsesLightTheme">Raw <c>SystemUsesLightTheme</c>, or <see langword="null"/> when absent.</param>
    /// <param name="highContrast">Whether high contrast is on.</param>
    /// <param name="systemColorsFingerprint">See <see cref="SystemThemeSnapshot.SystemColorsFingerprint"/>.</param>
    /// <param name="messageFontSize">See <see cref="SystemThemeSnapshot.MessageFontSize"/>.</param>
    /// <returns>The snapshot.</returns>
    internal static SystemThemeSnapshot MapSnapshot(
        int? appsUseLightTheme,
        int? systemUsesLightTheme,
        bool highContrast,
        int systemColorsFingerprint = 0,
        double messageFontSize = 0)
    {
        var appsLight = appsUseLightTheme is not { } apps || apps != 0;
        var systemLight = systemUsesLightTheme is { } system ? system != 0 : appsLight;
        return new SystemThemeSnapshot(appsLight, systemLight, highContrast, systemColorsFingerprint, messageFontSize);
    }

    /// <summary>
    /// Folds <c>COLORREF</c> values into a deterministic fingerprint (polynomial hash, base 31, seed 0 —
    /// not <see cref="HashCode"/>, which is randomised per process and would make log lines incomparable).
    /// Order-sensitive, so swapping two colours still changes it.
    /// </summary>
    /// <param name="colors">The colours, in <see cref="FingerprintColorIndices"/> order.</param>
    /// <returns>The fingerprint.</returns>
    internal static int ComputeColorsFingerprint(IEnumerable<int> colors)
    {
        var hash = 0;
        foreach (var color in colors)
            hash = unchecked((hash * 31) + color);

        return hash;
    }

    /// <summary>
    /// Re-reads the snapshot and raises <see cref="Changed"/> when it differs from the last published
    /// one. Internal so tests can drive it directly; in production it runs from the debounce
    /// <c>WM_TIMER</c> inside <see cref="WndProc"/> (or directly when the timer cannot be started).
    /// </summary>
    /// <returns><see langword="true"/> when the snapshot changed and <see cref="Changed"/> was raised.</returns>
    internal bool Refresh()
    {
        var previous = _current;
        var next = ReadSnapshot(fallback: previous);
        if (next.Equals(previous)) return false;

        _current = next;
        Log.Info(
            LogSource,
            "SystemThemeChanged",
            SnapshotFields(next));

        RaiseChanged(next);
        return true;
    }

    private static KeyValuePair<string, object?>[] SnapshotFields(SystemThemeSnapshot snapshot) =>
    [
        new("appsUseLightTheme", snapshot.AppsUseLightTheme),
        new("systemUsesLightTheme", snapshot.SystemUsesLightTheme),
        new("highContrast", snapshot.HighContrast),
        new("systemColorsFingerprint", snapshot.SystemColorsFingerprint.ToString("X8", CultureInfo.InvariantCulture)),
        new("messageFontSize", snapshot.MessageFontSize),
    ];

    private SystemThemeSnapshot ReadSnapshot(SystemThemeSnapshot? fallback)
    {
        try
        {
            var apps = nativeApi.ReadCurrentUserDword(PersonalizeKeyPath, AppsUseLightThemeValueName);
            var system = nativeApi.ReadCurrentUserDword(PersonalizeKeyPath, SystemUsesLightThemeValueName);
            var highContrast = nativeApi.IsHighContrastOn();
            var fingerprint = ComputeColorsFingerprint(FingerprintColorIndices.Select(nativeApi.GetSystemColor));
            var messageFontSize = nativeApi.GetMessageFontSize();
            return MapSnapshot(apps, system, highContrast, fingerprint, messageFontSize);
        }
        catch (Exception ex)
        {
            // INativeApi promises never to throw here; this is belt and braces for the window procedure.
            Log.Error(LogSource, "ReadFailed", ex);
            return fallback ?? SystemThemeSnapshot.Default;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // Minimal work, never throw out of a window procedure.
        try
        {
            if (msg is WmSettingChange or WmSysColorChange or WmThemeChanged)
            {
                // SetTimer with an existing id restarts it — that is the debounce. If the timer cannot
                // be started, refresh right away rather than miss the switch.
                if (!nativeApi.SetTimer(hwnd, DebounceTimerId, DebounceIntervalMs))
                    Refresh();
            }
            else if (msg == WmTimer && wParam == DebounceTimerId)
            {
                nativeApi.KillTimer(hwnd, DebounceTimerId);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "WndProcError", ex);
        }

        return nativeApi.DefaultWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RaiseChanged(SystemThemeSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;

        // One faulty subscriber must neither escape into the window procedure nor starve the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SystemThemeSnapshot>>())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception ex)
            {
                Log.Error(LogSource, "ChangedHandlerFailed", ex);
            }
        }
    }

    private void ReleaseWindow()
    {
        try
        {
            if (_window.IsValid)
                nativeApi.KillTimer(_window.Handle, DebounceTimerId);

            if (_window.HasResources)
                nativeApi.DestroyMessageWindow(_window);
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "ReleaseFailed", ex);
        }
        finally
        {
            _window = default;
            _wndProc = null;
        }
    }
}
