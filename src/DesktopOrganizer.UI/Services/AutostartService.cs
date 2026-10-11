using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using Microsoft.Win32;

namespace DesktopOrganizer.UI.Services;

/// <inheritdoc />
/// <remarks>
/// <para>
/// The HKCU <c>Run</c> value is the single source of truth for autostart — there is no mirror of it
/// in the app's JSON state. <c>installer/DesktopOrganizer.iss</c> reads and writes the very same value
/// from its "Start with Windows" checkbox on the Finished page, so the installer and the Settings
/// window always agree. <see cref="RunKeyPath"/> and <see cref="InstallerValueName"/> must therefore stay in
/// sync with the <c>AutostartRunKey</c>/<c>AutostartValueName</c> constants in the <c>.iss</c> — pinned by
/// <c>InstallerAutostartContractTests</c>. A Debug build uses a separate value name
/// (<see cref="DebugValueName"/>), see there.
/// </para>
/// <para>
/// A package-identity (Microsoft Store / MSIX) build does not use the <c>Run</c> key at all: autostart is
/// the manifest <c>startupTask</c> (<see cref="StartupTaskId"/>), whose enabled state only the user can
/// change in Windows Settings. <see cref="IsManagedBySystem"/> is then <see langword="true"/> and this
/// type performs no registry access whatsoever.
/// </para>
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

    /// <summary>
    /// <c>TaskId</c> of the manifest <c>windows.startupTask</c> extension a package-identity (Microsoft
    /// Store / MSIX) build registers its autostart under, instead of the HKCU <c>Run</c> value above.
    /// Must equal <c>desktop:StartupTask/@TaskId</c> in <c>installer/msix/AppxManifest.xml</c> (pinned by
    /// <c>MsixManifestContractTests</c>). Both of those files ship with the MSIX packaging package, which
    /// is developed on a parallel branch and appears in this tree when that branch is merged. No dot,
    /// deliberately: conservative compatibility with the manifest schema.
    /// </summary>
    internal const string StartupTaskId = "DesktopOrganizerStartup";

#if DEBUG
    /// <summary>Run value name in effect for this build (Debug ⇒ <see cref="DebugValueName"/>).</summary>
    internal const string ValueName = DebugValueName;
#else
    /// <summary>Run value name in effect for this build (Release ⇒ <see cref="InstallerValueName"/>).</summary>
    internal const string ValueName = InstallerValueName;
#endif

    private readonly IAppPackageContext _packageContext;
    private readonly IExternalLinkLauncher _linkLauncher;
    private readonly IAppLog? _appLog;
    private readonly string _valueName;
    private readonly string _runKeyPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutostartService"/> class.
    /// </summary>
    /// <param name="packageContext">Tells whether the process has package identity (see <see cref="IsManagedBySystem"/>).</param>
    /// <param name="linkLauncher">Opens the system "Startup apps" page for <see cref="OpenSystemStartupSettings"/>.</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public AutostartService(IAppPackageContext packageContext, IExternalLinkLauncher linkLauncher, IAppLog? appLog = null)
        : this(packageContext, linkLauncher, appLog, ValueName, RunKeyPath)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AutostartService"/> class with an explicit Run key
    /// and value name — lets tests exercise the registry path against a disposable HKCU sub-key instead of
    /// the real <c>Run</c> key (a test process killed mid-run must never leave an autostart entry behind).
    /// </summary>
    /// <param name="packageContext">Channel probe, as in the public constructor.</param>
    /// <param name="linkLauncher">System-settings opener, as in the public constructor.</param>
    /// <param name="appLog">Failure log, as in the public constructor.</param>
    /// <param name="valueName">The Run value name to read and write.</param>
    /// <param name="runKeyPath">The HKCU-relative key that holds the value (<see cref="RunKeyPath"/> in production).</param>
    internal AutostartService(IAppPackageContext packageContext, IExternalLinkLauncher linkLauncher, IAppLog? appLog, string valueName, string runKeyPath)
    {
        _packageContext = packageContext ?? throw new ArgumentNullException(nameof(packageContext));
        _linkLauncher = linkLauncher ?? throw new ArgumentNullException(nameof(linkLauncher));
        _appLog = appLog;
        _valueName = valueName ?? throw new ArgumentNullException(nameof(valueName));
        _runKeyPath = runKeyPath ?? throw new ArgumentNullException(nameof(runKeyPath));
    }

    /// <inheritdoc />
    public bool IsManagedBySystem => _packageContext.IsPackaged;

    /// <inheritdoc />
    public void OpenSystemStartupSettings() => _linkLauncher.OpenStartupAppsSettings();

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            // Package identity: the startup task state lives in Windows Settings, and the HKCU Run value is
            // not ours to read (or trust) — report "off" without touching the registry.
            if (IsManagedBySystem) return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
                return key?.GetValue(_valueName) is not null;
            }
            catch (Exception ex)
            {
                Log.Error("AutostartService", "ReadFailed", ex);
                return false;
            }
        }

        set
        {
            if (IsManagedBySystem)
            {
                Log.Warn("AutostartService", "WriteIgnoredPackaged");
                return;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(_runKeyPath);
                if (key is null) return;

                if (value)
                {
                    var path = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(path))
                        key.SetValue(_valueName, $"\"{path}\"");
                }
                else
                {
                    key.DeleteValue(_valueName, throwOnMissingValue: false);
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
