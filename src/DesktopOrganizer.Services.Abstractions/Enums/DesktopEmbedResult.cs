namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Outcome of <see cref="IDesktopIntegrationService.EmbedInDesktop"/>: whether the window ended up as a
/// child of the shell's <c>WorkerW</c> desktop layer, or stayed a top-level window because no such layer
/// could be found (ADR-034).
/// </summary>
/// <remarks>
/// Two shell layouts exist in the field (release-hardening F0 spike): the classic one (Windows 10,
/// Windows 11 up to 23H2), where the product's <c>WorkerW</c> lookup succeeds and panels are embedded
/// (<see cref="Reparented"/>/<see cref="AlreadyEmbedded"/>), and the Windows 11 24H2+ one, where the
/// lookup returns nothing and panels are deliberately left top-level (<see cref="NoDesktopLayer"/>). The
/// latter is an expected, permanent mode on those builds — not an error.
/// </remarks>
public enum DesktopEmbedResult
{
    /// <summary>The handle was <see cref="IntPtr.Zero"/> or no longer names a live window; nothing was done.</summary>
    InvalidWindow,

    /// <summary>
    /// The window stays top-level (the styles were applied): no <c>WorkerW</c> desktop layer was found,
    /// Explorer did not answer the lookup in time, or re-parenting into the found layer did not take.
    /// </summary>
    NoDesktopLayer,

    /// <summary>The window was already a child of the current <c>WorkerW</c>; <c>SetParent</c> was skipped.</summary>
    AlreadyEmbedded,

    /// <summary>The window was re-parented into the current <c>WorkerW</c> (verified by re-reading its parent).</summary>
    Reparented,
}
