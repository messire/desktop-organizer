using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="IWindowFrameTheming"/>
/// <remarks>
/// <para>
/// Version gating (decided once, at construction, from the real OS build — .NET 5+ reports it without
/// manifest lying):
/// </para>
/// <list type="bullet">
/// <item><description>build ≥ <see cref="FirstBuildWithAttribute20"/> (Windows 10 20H1 and every Windows 11) —
/// the documented <c>DWMWA_USE_IMMERSIVE_DARK_MODE = 20</c>;</description></item>
/// <item><description><see cref="MinimumSupportedBuild"/> (1809) ≤ build &lt; 18985 (1809/1903/1909) — the
/// pre-release id 19, which those builds honour. On 1809 itself the caption may additionally need the
/// undocumented <c>AllowDarkModeForWindow</c>; that is deliberately not called (no undocumented API for a
/// cosmetic frame), so on 1809 the call can succeed without a visible change;</description></item>
/// <item><description>older builds — unsupported: no native call at all, <see langword="false"/>, logged once.</description></item>
/// </list>
/// <para>
/// One <c>DwmSetWindowAttribute</c> per call, nothing else — no frame repaint nudge. Windows 11 repaints the
/// caption immediately; on Windows 10 a visible window may keep the old caption colour until its next
/// activation change (MANUAL VERIFY Win10 22H2 / Win11 24H2, plan §10 M-6.1). Failures are logged with the
/// <c>HRESULT</c>, rate-limited to one entry per window per minute; the handle value itself carries no
/// user data.
/// </para>
/// </remarks>
internal sealed class WindowFrameTheming : IWindowFrameTheming
{
    /// <summary><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (documented; Windows 10 20H1+ and Windows 11).</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>The pre-20H1 id of the same attribute (Windows 10 1809–1909).</summary>
    internal const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Windows 10 1809 — the first build with an immersive dark-mode frame.</summary>
    internal const int MinimumSupportedBuild = 17763;

    /// <summary>The Windows 10 build (20H1 development branch) from which the attribute id is 20.</summary>
    internal const int FirstBuildWithAttribute20 = 18985;

    private const string LogSource = "WindowFrameTheming";

    private readonly INativeApi _nativeApi;
    private readonly IAppLog? _appLog;
    private readonly int _windowsBuild;
    private readonly int? _attribute;
    private readonly HandleLogRateLimiter _failures;
    private int _unsupportedLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowFrameTheming"/> class for the running Windows build.
    /// </summary>
    /// <param name="nativeApi">Native façade (<c>DwmSetWindowAttribute</c>).</param>
    /// <param name="appLog">Application log.</param>
    public WindowFrameTheming(INativeApi nativeApi, IAppLog appLog)
        : this(nativeApi, appLog, CurrentWindowsBuild(), timeProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowFrameTheming"/> class for an explicit Windows
    /// build and clock (tests).
    /// </summary>
    /// <param name="nativeApi">Native façade (<c>DwmSetWindowAttribute</c>).</param>
    /// <param name="appLog">Log to write to; the ambient <see cref="AppLog.Current"/> when <see langword="null"/>.</param>
    /// <param name="windowsBuild">The Windows build number to gate on (0 = unknown/unsupported).</param>
    /// <param name="timeProvider">Clock for the failure-log rate limit; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    internal WindowFrameTheming(INativeApi nativeApi, IAppLog? appLog, int windowsBuild, TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);

        _nativeApi = nativeApi;
        _appLog = appLog;
        _windowsBuild = windowsBuild;
        _attribute = GetImmersiveDarkModeAttribute(windowsBuild);
        _failures = new HandleLogRateLimiter(TimeSpan.FromMinutes(1), timeProvider);
    }

    /// <inheritdoc />
    public bool IsSupported => _attribute.HasValue;

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public bool TryApplyImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (_attribute is not { } attribute)
        {
            LogUnsupportedOnce();
            return false;
        }

        int hresult;
        try
        {
            hresult = _nativeApi.SetDwmWindowAttribute(hwnd, attribute, dark ? 1 : 0);
        }
        catch (Exception ex)
        {
            // INativeApi promises never to throw here; belt and braces for a SourceInitialized/ThemeChanged caller.
            if (_failures.TryAdmit(hwnd, out var suppressed))
            {
                Log.Error(
                    LogSource,
                    "ImmersiveDarkModeFailed",
                    ex,
                    new KeyValuePair<string, object?>("attribute", attribute),
                    new KeyValuePair<string, object?>("suppressedSinceLast", suppressed));
            }

            return false;
        }

        if (hresult >= 0) return true;

        if (_failures.TryAdmit(hwnd, out var suppressedSinceLast))
        {
            Log.Warn(
                LogSource,
                "ImmersiveDarkModeFailed",
                new KeyValuePair<string, object?>("attribute", attribute),
                new KeyValuePair<string, object?>("dark", dark),
                new KeyValuePair<string, object?>("hresult", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{hresult:X8}")),
                new KeyValuePair<string, object?>("suppressedSinceLast", suppressedSinceLast));
        }

        return false;
    }

    /// <summary>
    /// The <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> id valid on <paramref name="windowsBuild"/>, or
    /// <see langword="null"/> when the build has no immersive dark-mode frame.
    /// </summary>
    /// <param name="windowsBuild">Windows build number.</param>
    /// <returns>20, 19 or <see langword="null"/>.</returns>
    internal static int? GetImmersiveDarkModeAttribute(int windowsBuild)
        => windowsBuild >= FirstBuildWithAttribute20 ? DwmwaUseImmersiveDarkMode
            : windowsBuild >= MinimumSupportedBuild ? DwmwaUseImmersiveDarkModeBefore20H1
            : null;

    private static int CurrentWindowsBuild()
    {
        var version = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() && version.Major >= 10 ? version.Build : 0;
    }

    private void LogUnsupportedOnce()
    {
        if (Interlocked.Exchange(ref _unsupportedLogged, 1) != 0) return;

        Log.Info(
            LogSource,
            "ImmersiveDarkModeUnsupported",
            new KeyValuePair<string, object?>("windowsBuild", _windowsBuild));
    }
}
