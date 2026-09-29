using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="ITrayMenuWindowStyler"/>
/// <remarks>
/// <para>
/// Two documented DWM attributes, Windows 11 (build ≥ <see cref="MinimumSupportedBuild"/>) only:
/// <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> = <c>DWMWCP_ROUNDSMALL</c> (the radius Windows 11 itself uses for
/// menus) and <c>DWMWA_BORDER_COLOR</c> = the palette border, so the DWM-drawn edge of the rounded popup
/// matches the 1 px border <c>TrayMenuRenderer</c> paints. For <see langword="null"/> (high contrast) both are
/// reset to the system defaults (<c>DWMWCP_DEFAULT</c>, <c>DWMWA_COLOR_DEFAULT</c>). Windows 10 has neither
/// attribute: no native call at all, the menu keeps square corners.
/// </para>
/// <para>
/// Purely cosmetic and failure-tolerant: a failing <c>HRESULT</c> is logged (rate-limited per window) and
/// the popup simply keeps the square system corners. <see cref="RoundedCornersEnabled"/> is the code-level
/// switch should manual verification find an artefact (e.g. a square drop shadow under the rounded corners,
/// plan §10 M-6.13/M-6.14).
/// </para>
/// </remarks>
internal sealed class TrayMenuWindowStyler : ITrayMenuWindowStyler
{
    /// <summary>Code-level switch for the rounded tray-menu popups (phase 6.4, optional per plan §2.9). Not a user setting.</summary>
    internal const bool RoundedCornersEnabled = true;

    /// <summary>Windows 11 21H2 — the first build with <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>/<c>DWMWA_BORDER_COLOR</c>.</summary>
    internal const int MinimumSupportedBuild = 22000;

    /// <summary><c>DWMWA_WINDOW_CORNER_PREFERENCE</c>.</summary>
    internal const int DwmwaWindowCornerPreference = 33;

    /// <summary><c>DWMWA_BORDER_COLOR</c>.</summary>
    internal const int DwmwaBorderColor = 34;

    /// <summary><c>DWMWCP_DEFAULT</c> — let the system decide (square for these popups).</summary>
    internal const int DwmwcpDefault = 0;

    /// <summary><c>DWMWCP_ROUNDSMALL</c> — the small Windows 11 menu radius.</summary>
    internal const int DwmwcpRoundSmall = 3;

    /// <summary><c>DWMWA_COLOR_DEFAULT</c> — the system border colour.</summary>
    internal const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);

    private const string LogSource = "TrayMenuWindowStyler";

    private readonly INativeApi _nativeApi;
    private readonly IAppLog? _appLog;
    private readonly HandleLogRateLimiter _failures;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayMenuWindowStyler"/> class for the running Windows build.
    /// </summary>
    /// <param name="nativeApi">Native façade (<c>DwmSetWindowAttribute</c>).</param>
    /// <param name="appLog">Application log.</param>
    public TrayMenuWindowStyler(INativeApi nativeApi, IAppLog appLog)
        : this(nativeApi, appLog, CurrentWindowsBuild(), RoundedCornersEnabled, timeProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayMenuWindowStyler"/> class for an explicit Windows build,
    /// feature switch and clock (tests).
    /// </summary>
    /// <param name="nativeApi">Native façade (<c>DwmSetWindowAttribute</c>).</param>
    /// <param name="appLog">Log to write to; the ambient <see cref="AppLog.Current"/> when <see langword="null"/>.</param>
    /// <param name="windowsBuild">The Windows build number to gate on (0 = unknown/unsupported).</param>
    /// <param name="enabled">The feature switch (<see cref="RoundedCornersEnabled"/> in production).</param>
    /// <param name="timeProvider">Clock for the failure-log rate limit; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    internal TrayMenuWindowStyler(INativeApi nativeApi, IAppLog? appLog, int windowsBuild, bool enabled, TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);

        _nativeApi = nativeApi;
        _appLog = appLog;
        IsSupported = enabled && windowsBuild >= MinimumSupportedBuild;
        _failures = new HandleLogRateLimiter(TimeSpan.FromMinutes(1), timeProvider);
    }

    /// <summary>Gets a value indicating whether the DWM attributes are applied at all on this build.</summary>
    internal bool IsSupported { get; }

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public void Apply(IntPtr hwnd, TrayMenuPalette? palette)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return;

        try
        {
            var corner = palette is null ? DwmwcpDefault : DwmwcpRoundSmall;
            var border = palette is null ? DwmwaColorDefault : ToColorRef(palette.Border);

            var hrCorner = _nativeApi.SetDwmWindowAttribute(hwnd, DwmwaWindowCornerPreference, corner);
            var hrBorder = _nativeApi.SetDwmWindowAttribute(hwnd, DwmwaBorderColor, border);
            if (hrCorner >= 0 && hrBorder >= 0) return;

            if (_failures.TryAdmit(hwnd, out var suppressed))
            {
                Log.Warn(
                    LogSource,
                    "TrayMenuWindowStyleFailed",
                    new KeyValuePair<string, object?>("cornerHresult", FormatHresult(hrCorner)),
                    new KeyValuePair<string, object?>("borderHresult", FormatHresult(hrBorder)),
                    new KeyValuePair<string, object?>("suppressedSinceLast", suppressed));
            }
        }
        catch (Exception ex)
        {
            // INativeApi promises never to throw; belt and braces for a HandleCreated/Opening caller.
            if (_failures.TryAdmit(hwnd, out var suppressed))
            {
                Log.Error(LogSource, "TrayMenuWindowStyleFailed", ex, new KeyValuePair<string, object?>("suppressedSinceLast", suppressed));
            }
        }
    }

    /// <summary>
    /// Converts an ARGB <c>0xAARRGGBB</c> palette colour into a DWM <c>COLORREF</c> (<c>0x00BBGGRR</c>).
    /// </summary>
    /// <param name="argb">The palette colour.</param>
    /// <returns>The <c>COLORREF</c> value (alpha dropped).</returns>
    internal static int ToColorRef(uint argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return (int)((b << 16) | (g << 8) | r);
    }

    private static string FormatHresult(int hresult)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{hresult:X8}");

    private static int CurrentWindowsBuild()
    {
        var version = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() && version.Major >= 10 ? version.Build : 0;
    }
}
