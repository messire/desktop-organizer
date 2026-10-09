namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Outcome kind of the desktop-band walk <see cref="DesktopIntegrationService"/> performs for a top-level
/// fence panel (ADR-034 amendment 2026-10-09).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/>, <see cref="ProgmanMissing"/> and <see cref="WalkCapExceeded"/> all mean "no decision —
/// leave the Z-order alone"; the latter two are told apart only so the service can log each anomaly once per
/// session (ADR-audit review cycle 1, G2).
/// </remarks>
internal enum DesktopBandPlacementKind
{
    /// <summary>No decision for an unremarkable reason (the window asked about is <c>Progman</c> itself).</summary>
    Unknown,

    /// <summary>The window already is inside the desktop band — no Z-order change.</summary>
    InBand,

    /// <summary>The window has to be inserted after <see cref="DesktopBandPlacement.InsertAfter"/>.</summary>
    InsertAfter,

    /// <summary>No decision: there is no <c>Progman</c> window (Explorer down or restarting).</summary>
    ProgmanMissing,

    /// <summary>No decision: the walk up from <c>Progman</c> hit <see cref="DesktopIntegrationService.MaxPanelBandWalk"/>.</summary>
    WalkCapExceeded,
}
