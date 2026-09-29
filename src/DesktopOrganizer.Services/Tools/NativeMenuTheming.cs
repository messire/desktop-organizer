using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="INativeMenuTheming"/>
/// <remarks>
/// <para>
/// Phase 6.4b decision (docs/PLANS/design-system-1x.md §2.10, ADR-047 D-11): the undocumented
/// <c>uxtheme.dll</c> ordinals 135 (<c>SetPreferredAppMode</c>) and 136 (<c>FlushMenuThemes</c>) are used,
/// but boxed in on every side:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="FeatureEnabled"/> — a code constant (not a setting, G-1): flipping it to
/// <see langword="false"/> removes every native call without touching any caller;</description></item>
/// <item><description>build gate ≥ <see cref="MinimumSupportedBuild"/> (Windows 10 1903): on 1809 ordinal
/// 135 is the older <c>AllowDarkModeForApp(BOOL)</c> (ABI-identical, but a different contract), on older
/// builds it does not exist;</description></item>
/// <item><description>the export is resolved by ordinal with <c>GetProcAddress</c> from System32's
/// <c>uxtheme.dll</c> and only ever called when found (<see cref="INativeApi.TrySetPreferredAppMode"/>);</description></item>
/// <item><description>light is the Windows default for every process, so a light request before dark was
/// ever requested makes NO native call — users who never run the dark theme never reach the undocumented
/// API at all;</description></item>
/// <item><description><c>ForceDark</c> (2) rather than <c>AllowDark</c> (1): the caller has already
/// resolved the application theme (high contrast, the dark-theme gate, the Windows app mode), and the menus
/// must follow that resolution, not re-read the Windows setting on their own.</description></item>
/// </list>
/// <para>
/// Affects only classic Win32 menus of this process (<c>HMENU</c> popups — the shell context menu of an
/// icon and the windows' system menu); WPF and WinForms menus are drawn by the application and are
/// unaffected. Items that a shell extension owner-draws itself may still paint light — the same limitation
/// the classic Explorer menu has. MANUAL VERIFY Windows 10 22H2 / Windows 11 24H2 (plan §10 M-6.16).
/// </para>
/// </remarks>
internal sealed class NativeMenuTheming : INativeMenuTheming
{
    /// <summary>The code-level feature gate for dark native menus (phase 6.4b). Not a user setting.</summary>
    internal const bool FeatureEnabled = true;

    /// <summary>Windows 10 1903 — the first build where uxtheme ordinal 135 is <c>SetPreferredAppMode</c>.</summary>
    internal const int MinimumSupportedBuild = 18362;

    /// <summary><c>PreferredAppMode.Default</c> — the Windows default (light classic menus).</summary>
    internal const int PreferredAppModeDefault = 0;

    /// <summary><c>PreferredAppMode.ForceDark</c> — dark classic menus regardless of the Windows app mode.</summary>
    internal const int PreferredAppModeForceDark = 2;

    private const string LogSource = "NativeMenuTheming";

    private readonly INativeApi _nativeApi;
    private readonly IAppLog? _appLog;
    private readonly int _windowsBuild;

    // The mode last successfully applied; null = never called (the process is in the Windows default).
    private bool? _appliedDark;
    private bool _failureLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeMenuTheming"/> class for the running Windows build.
    /// </summary>
    /// <param name="nativeApi">Native façade (uxtheme ordinals).</param>
    /// <param name="appLog">Application log.</param>
    public NativeMenuTheming(INativeApi nativeApi, IAppLog appLog)
        : this(nativeApi, appLog, CurrentWindowsBuild(), FeatureEnabled)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeMenuTheming"/> class for an explicit Windows build
    /// and feature gate (tests).
    /// </summary>
    /// <param name="nativeApi">Native façade (uxtheme ordinals).</param>
    /// <param name="appLog">Log to write to; the ambient <see cref="AppLog.Current"/> when <see langword="null"/>.</param>
    /// <param name="windowsBuild">The Windows build number to gate on (0 = unknown/unsupported).</param>
    /// <param name="featureEnabled">The feature gate value (<see cref="FeatureEnabled"/> in production).</param>
    internal NativeMenuTheming(INativeApi nativeApi, IAppLog? appLog, int windowsBuild, bool featureEnabled)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);

        _nativeApi = nativeApi;
        _appLog = appLog;
        _windowsBuild = windowsBuild;
        IsSupported = IsSupportedOn(windowsBuild, featureEnabled);
    }

    /// <inheritdoc />
    public bool IsSupported { get; }

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public bool TryApply(bool dark)
    {
        if (!IsSupported) return false;

        // Idempotent, and "light before dark was ever requested" is already the process default.
        if (_appliedDark == dark || (_appliedDark is null && !dark)) return true;

        var mode = dark ? PreferredAppModeForceDark : PreferredAppModeDefault;
        if (!_nativeApi.TrySetPreferredAppMode(mode))
        {
            LogFailureOnce("SetPreferredAppModeUnavailable");
            return false;
        }

        // Menus cache their theme data; without the flush a menu that was already opened once keeps the old look.
        if (!_nativeApi.TryFlushMenuThemes())
        {
            LogFailureOnce("FlushMenuThemesUnavailable");
        }

        _appliedDark = dark;
        Log.Info(LogSource, "NativeMenuModeApplied", new KeyValuePair<string, object?>("dark", dark));
        return true;
    }

    /// <summary>Pure gating rule — exposed for the version-gating tests.</summary>
    /// <param name="windowsBuild">Windows build number (0 = unknown).</param>
    /// <param name="featureEnabled">The feature gate value.</param>
    /// <returns><see langword="true"/> when the undocumented menu-theme exports may be called.</returns>
    internal static bool IsSupportedOn(int windowsBuild, bool featureEnabled)
        => featureEnabled && windowsBuild >= MinimumSupportedBuild;

    private static int CurrentWindowsBuild()
    {
        var version = Environment.OSVersion.Version;
        return OperatingSystem.IsWindows() && version.Major >= 10 ? version.Build : 0;
    }

    private void LogFailureOnce(string eventName)
    {
        if (_failureLogged) return;
        _failureLogged = true;

        Log.Warn(LogSource, eventName, new KeyValuePair<string, object?>("windowsBuild", _windowsBuild));
    }
}
