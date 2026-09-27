using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using Microsoft.Win32;

namespace DesktopOrganizer.UI.Services;

/// <inheritdoc />
/// <remarks>
/// The HKCU <c>Run</c> value is the single source of truth for autostart — there is no mirror of it
/// in the app's JSON state. <c>installer/DesktopOrganizer.iss</c> reads and writes the very same value
/// from its "Start with Windows" checkbox on the Finished page, so the installer and the Settings
/// window always agree. <see cref="RunKeyPath"/> and <see cref="InstallerValueName"/> must therefore stay in
/// sync with the <c>AutostartRunKey</c>/<c>AutostartValueName</c> constants in the <c>.iss</c> — pinned by
/// <c>InstallerAutostartContractTests</c>. A Debug build uses a separate value name
/// (<see cref="DebugValueName"/>), see there.
/// </remarks>
internal class AutostartService : IAutostartService
{
    /// <summary>HKCU-relative path of the per-user Run key (shared contract with the installer).</summary>
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Name of the Run value the installed (Release) application uses — the contract shared with the
    /// installer's "Start with Windows" checkbox.
    /// </summary>
    internal const string InstallerValueName = "DesktopOrganizer";

    /// <summary>
    /// Name of the Run value a Debug (developer) build uses. Distinct from
    /// <see cref="InstallerValueName"/> for the same reason Debug builds get their own storage root
    /// (<c>AppStorageRoot</c>): toggling autostart while developing must never re-point the working
    /// application's autostart entry at a <c>bin\Debug</c> exe.
    /// </summary>
    internal const string DebugValueName = "DesktopOrganizer.Dev";

#if DEBUG
    /// <summary>Run value name in effect for this build (Debug ⇒ <see cref="DebugValueName"/>).</summary>
    internal const string ValueName = DebugValueName;
#else
    /// <summary>Run value name in effect for this build (Release ⇒ <see cref="InstallerValueName"/>).</summary>
    internal const string ValueName = InstallerValueName;
#endif

    private readonly IAppLog? _appLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutostartService"/> class.
    /// </summary>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public AutostartService(IAppLog? appLog = null) => _appLog = appLog;

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                Log.Error("AutostartService", "ReadFailed", ex);
                return false;
            }
        }

        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null) return;

                if (value)
                {
                    var path = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(path))
                        key.SetValue(ValueName, $"\"{path}\"");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("AutostartService", "WriteFailed", ex);
            }
        }
    }

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;
}
