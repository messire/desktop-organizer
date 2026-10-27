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
/// <c>InstallerAutostartContractTests</c>.
/// </para>
/// <para>
/// Windows keeps a second, user-facing switch for every <c>Run</c> value: Task Manager → Startup apps and
/// Settings → Apps → Startup store a per-value "disabled" flag under <see cref="StartupApprovedKeyPath"/>
/// (same value name; first byte of the binary data has bit 0 set when disabled). A <c>Run</c> value whose
/// <c>StartupApproved</c> entry is disabled is never launched, so <see cref="IsEnabled"/> reads both and
/// enabling writes both — otherwise re-enabling from the app after the user disabled it in Task Manager would
/// create a <c>Run</c> value that Windows silently ignores. The <c>.iss</c> applies the same rule
/// (<c>AutostartApprovedKey</c>).
/// </para>
/// <para>
/// A Debug (developer) build never touches the registry: the toggle is a session-only flag
/// (<see cref="PersistsToRegistry"/>). Registering <c>bin\Debug\...exe</c> for autostart on a machine that
/// also has the installed build registered makes the two race at logon; the one that wins the single-instance
/// handshake becomes the primary and the other exits, so the developer build ends up being the one that "starts
/// with Windows". A pre-1.0.1 Debug build kept its own value (<see cref="LegacyDebugValueName"/>); the public
/// constructor removes such a leftover once.
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
    /// HKCU-relative path of the key where Task Manager / Settings → Startup keep the per-value enabled/disabled
    /// flag for <see cref="RunKeyPath"/> entries (shared contract with the installer).
    /// </summary>
    internal const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Name of the Run value the installed (Release) application uses — the contract shared with the
    /// installer's "Start with Windows" checkbox.
    /// </summary>
    internal const string InstallerValueName = "DesktopOrganizer";

    /// <summary>
    /// Run value name Debug builds before 1.0.1 registered for themselves. No longer written (see the
    /// class remarks); a leftover entry is deleted by the public constructor so that a <c>bin\Debug</c> exe
    /// stops being launched at logon on a developer machine.
    /// </summary>
    internal const string LegacyDebugValueName = "DesktopOrganizer.Dev";

    /// <summary>
    /// <c>TaskId</c> of the manifest <c>windows.startupTask</c> extension a package-identity (Microsoft
    /// Store / MSIX) build registers its autostart under, instead of the HKCU <c>Run</c> value above.
    /// Must equal <c>desktop:StartupTask/@TaskId</c> in <c>installer/msix/AppxManifest.xml</c> (pinned by
    /// <c>MsixManifestContractTests</c>). No dot, deliberately: conservative compatibility with the
    /// manifest schema.
    /// </summary>
    internal const string StartupTaskId = "DesktopOrganizerStartup";

#if DEBUG
    /// <summary>Whether this build writes autostart to the registry (Debug ⇒ session-only flag, see the class remarks).</summary>
    internal const bool PersistsToRegistry = false;
#else
    /// <summary>Whether this build writes autostart to the registry (Release ⇒ the HKCU Run value).</summary>
    internal const bool PersistsToRegistry = true;
#endif

    /// <summary>
    /// <c>StartupApproved</c> data Windows itself writes for an enabled entry: <c>02 00 00 00</c> followed by an
    /// all-zero FILETIME (the "disabled at" timestamp, which only a disabled entry carries).
    /// </summary>
    private static readonly byte[] StartupApprovedEnabledData = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly IAppPackageContext _packageContext;
    private readonly IExternalLinkLauncher _linkLauncher;
    private readonly IAppLog? _appLog;
    private readonly bool _persistsToRegistry;
    private readonly string _valueName;
    private readonly string _runKeyPath;
    private readonly string _startupApprovedKeyPath;
    private bool _sessionOnlyEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutostartService"/> class.
    /// </summary>
    /// <param name="packageContext">Tells whether the process has package identity (see <see cref="IsManagedBySystem"/>).</param>
    /// <param name="linkLauncher">Opens the system "Startup apps" page for <see cref="OpenSystemStartupSettings"/>.</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public AutostartService(IAppPackageContext packageContext, IExternalLinkLauncher linkLauncher, IAppLog? appLog = null)
        : this(packageContext, linkLauncher, appLog, PersistsToRegistry, InstallerValueName, RunKeyPath, StartupApprovedKeyPath)
    {
        if (!IsManagedBySystem) RemoveLegacyDebugEntry();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AutostartService"/> class with an explicit Run key
    /// and value name — lets tests exercise the registry path against disposable HKCU sub-keys instead of
    /// the real <c>Run</c> key (a test process killed mid-run must never leave an autostart entry behind).
    /// </summary>
    /// <param name="packageContext">Channel probe, as in the public constructor.</param>
    /// <param name="linkLauncher">System-settings opener, as in the public constructor.</param>
    /// <param name="appLog">Failure log, as in the public constructor.</param>
    /// <param name="persistsToRegistry">
    /// <see langword="true"/> to read and write the registry; <see langword="false"/> for the Debug build's
    /// session-only flag (<see cref="PersistsToRegistry"/>).
    /// </param>
    /// <param name="valueName">The Run value name to read and write.</param>
    /// <param name="runKeyPath">The HKCU-relative key that holds the value (<see cref="RunKeyPath"/> in production).</param>
    /// <param name="startupApprovedKeyPath">The HKCU-relative key that holds the Task Manager enabled/disabled flag (<see cref="StartupApprovedKeyPath"/> in production).</param>
    internal AutostartService(
        IAppPackageContext packageContext,
        IExternalLinkLauncher linkLauncher,
        IAppLog? appLog,
        bool persistsToRegistry,
        string valueName,
        string runKeyPath,
        string startupApprovedKeyPath)
    {
        _packageContext = packageContext ?? throw new ArgumentNullException(nameof(packageContext));
        _linkLauncher = linkLauncher ?? throw new ArgumentNullException(nameof(linkLauncher));
        _appLog = appLog;
        _persistsToRegistry = persistsToRegistry;
        _valueName = valueName ?? throw new ArgumentNullException(nameof(valueName));
        _runKeyPath = runKeyPath ?? throw new ArgumentNullException(nameof(runKeyPath));
        _startupApprovedKeyPath = startupApprovedKeyPath ?? throw new ArgumentNullException(nameof(startupApprovedKeyPath));
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
            if (!_persistsToRegistry) return _sessionOnlyEnabled;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
                if (key?.GetValue(_valueName) is null) return false;

                using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath);
                return !IsDisabledByStartupApproved(approved?.GetValue(_valueName));
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

            if (!_persistsToRegistry)
            {
                _sessionOnlyEnabled = value;
                Log.Info("AutostartService", "WriteSessionOnlyDebugBuild", new KeyValuePair<string, object?>("enabled", value));
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
                    if (string.IsNullOrEmpty(path)) return;

                    key.SetValue(_valueName, $"\"{path}\"");
                    ApproveStartup();
                }
                else
                {
                    key.DeleteValue(_valueName, throwOnMissingValue: false);
                    ForgetStartupApproval();
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

    /// <summary>
    /// Interprets a <c>StartupApproved\Run</c> value: absent ⇒ enabled (Windows writes the entry only once the
    /// user has touched the switch); otherwise bit 0 of the first byte is the "disabled" flag
    /// (<c>02</c>/<c>06</c> enabled, <c>03</c>/<c>07</c> disabled).
    /// </summary>
    /// <param name="data">The raw value data, or <see langword="null"/> when there is no entry.</param>
    /// <returns><see langword="true"/> when Windows will skip this Run value at logon.</returns>
    private static bool IsDisabledByStartupApproved(object? data) =>
        data is byte[] { Length: > 0 } bytes && (bytes[0] & 0x01) != 0;

    /// <summary>
    /// Marks the Run value enabled in <c>StartupApproved\Run</c> when an entry exists there (a disabled one would
    /// make Windows skip the value just written). No entry ⇒ nothing to do: absence already means enabled.
    /// </summary>
    private void ApproveStartup()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, writable: true);
        if (approved?.GetValue(_valueName) is null) return;

        approved.SetValue(_valueName, StartupApprovedEnabledData, RegistryValueKind.Binary);
    }

    /// <summary>
    /// Removes the Run value's <c>StartupApproved\Run</c> entry together with the value itself, so that a later
    /// re-enable starts from the clean "no entry ⇒ enabled" state and Task Manager does not keep listing a
    /// startup app that no longer exists.
    /// </summary>
    private void ForgetStartupApproval()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, writable: true);
        approved?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// One-time cleanup of the <see cref="LegacyDebugValueName"/> entry (and its <c>StartupApproved</c> flag)
    /// that Debug builds before 1.0.1 wrote. Best effort: a failure is logged, never thrown.
    /// </summary>
    private void RemoveLegacyDebugEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            if (key?.GetValue(LegacyDebugValueName) is null) return;

            key.DeleteValue(LegacyDebugValueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, writable: true);
            approved?.DeleteValue(LegacyDebugValueName, throwOnMissingValue: false);
            Log.Info("AutostartService", "LegacyDebugEntryRemoved");
        }
        catch (Exception ex)
        {
            Log.Error("AutostartService", "LegacyDebugEntryRemoveFailed", ex);
        }
    }
}
