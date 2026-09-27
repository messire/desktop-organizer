namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Where fence panels live in the window hierarchy, which decides how their geometry is scaled under
/// PerMonitorV2 (release-hardening F, ADR-037).
/// </summary>
/// <remarks>
/// Determined at run time by <see cref="IDesktopScaleProvider"/> — by fact (the root window of a
/// reported panel) whenever a live panel exists, otherwise by predicting from the shell's
/// <c>WorkerW</c> lookup (<see cref="IDesktopIntegrationService.FindDesktopLayerHost"/>). Process memory
/// only: never persisted.
/// </remarks>
public enum DesktopLayerMode
{
    /// <summary>Not determined yet. Geometry is handled exactly like <see cref="TopLevel"/>.</summary>
    Unknown = 0,

    /// <summary>
    /// Panels are children of the shell's top-level <c>WorkerW</c> (Windows 10, Windows 11 up to 23H2):
    /// every panel shares the single DPI of that root window, so the whole desktop layer has one scale.
    /// </summary>
    Embedded = 1,

    /// <summary>
    /// Panels are top-level windows (Windows 11 24H2+, where no <c>WorkerW</c> is found): under
    /// PerMonitorV2 each panel is scaled by the effective DPI of the monitor it is on.
    /// </summary>
    TopLevel = 2,
}
