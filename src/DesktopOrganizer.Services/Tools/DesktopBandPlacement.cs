namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Result of the desktop-band walk <see cref="DesktopIntegrationService"/> performs for a top-level fence
/// panel (ADR-034 amendment 2026-10-09): where the panel belongs relative to the desktop band.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="InsertAfter">
/// The <c>hWndInsertAfter</c> for <see cref="DesktopBandPlacementKind.InsertAfter"/>; <see cref="IntPtr.Zero"/>
/// (<c>HWND_TOP</c>) otherwise or when the whole non-topmost run above <c>Progman</c> is the band.
/// </param>
internal readonly record struct DesktopBandPlacement(DesktopBandPlacementKind Kind, IntPtr InsertAfter)
{
    /// <summary>Gets the "no decision" placement.</summary>
    public static DesktopBandPlacement Unknown => new (DesktopBandPlacementKind.Unknown, IntPtr.Zero);

    /// <summary>Gets the "no decision: no <c>Progman</c>" placement.</summary>
    public static DesktopBandPlacement ProgmanMissing => new (DesktopBandPlacementKind.ProgmanMissing, IntPtr.Zero);

    /// <summary>Gets the "no decision: walk cap exceeded" placement.</summary>
    public static DesktopBandPlacement WalkCapExceeded => new (DesktopBandPlacementKind.WalkCapExceeded, IntPtr.Zero);

    /// <summary>Gets the "already inside the band" placement.</summary>
    public static DesktopBandPlacement InBand => new (DesktopBandPlacementKind.InBand, IntPtr.Zero);

    /// <summary>Creates an "insert after <paramref name="insertAfter"/>" placement.</summary>
    /// <param name="insertAfter">The window (or <c>HWND_TOP</c>) the panel goes directly below.</param>
    /// <returns>The placement.</returns>
    public static DesktopBandPlacement At(IntPtr insertAfter) => new (DesktopBandPlacementKind.InsertAfter, insertAfter);
}
