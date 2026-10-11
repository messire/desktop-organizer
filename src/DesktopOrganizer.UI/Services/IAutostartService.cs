namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Controls whether the application launches automatically when Windows starts.
/// </summary>
public interface IAutostartService
{
    /// <summary>
    /// Gets or sets whether the application starts with Windows (per-user). Only meaningful when
    /// <see cref="IsManagedBySystem"/> is <see langword="false"/>: for a package-identity build the getter
    /// always returns <see langword="false"/> and the setter is a logged no-op.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Gets a value indicating whether Windows itself owns the autostart decision (a package-identity /
    /// Microsoft Store build, where the user toggles the manifest <c>startupTask</c> in Settings →
    /// Apps → Startup). In that case this application neither reads nor writes the HKCU <c>Run</c> key and
    /// the Settings window shows a pointer to the system page instead of a toggle.
    /// </summary>
    bool IsManagedBySystem { get; }

    /// <summary>
    /// Opens the Windows "Startup apps" settings page, where a <see cref="IsManagedBySystem"/> build's
    /// startup task is enabled. A convenience action: failures are logged by the launcher, never thrown.
    /// </summary>
    void OpenSystemStartupSettings();
}
