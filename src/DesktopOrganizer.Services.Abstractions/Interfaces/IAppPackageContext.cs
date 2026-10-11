namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Tells the rest of the application which distribution channel it is running in: with package identity
/// (MSIX / Microsoft Store) or without it (the Inno Setup installer, or a developer run).
/// </summary>
/// <remarks>
/// <para>
/// The answer is a process-lifetime constant (probed once, see <c>INativeApi.IsPackagedProcess</c>), so
/// consumers may read it freely and cache it. It exists so the UI layer and tests never see the internal
/// <c>INativeApi</c>, and so channel-dependent behaviour (storage root, autostart, update check) can be
/// switched in tests without touching the process.
/// </para>
/// <para>
/// Code that runs before DI exists cannot use this service; the one such consumer is
/// <c>AppStorageRoot</c>, which reads the same cached probe statically (a documented exception to "P/Invoke
/// only behind <c>INativeApi</c>").
/// </para>
/// </remarks>
public interface IAppPackageContext
{
    /// <summary>
    /// Gets a value indicating whether the process runs with package identity (Microsoft Store / MSIX
    /// channel). <see langword="false"/> for the Inno channel, developer runs and the test host, and also
    /// when the probe could not be completed.
    /// </summary>
    bool IsPackaged { get; }
}
