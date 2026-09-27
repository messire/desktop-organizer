namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Per-attempt summary of <c>IWindowManager.RecoverDesktopLayer</c> (ADR-034). Counts only — never
/// panel titles or paths, so it can be logged as-is.
/// </summary>
/// <param name="Embedded">Panels whose window is (again) a child of the current <c>WorkerW</c>.</param>
/// <param name="TopLevel">Panels whose window stayed top-level because no <c>WorkerW</c> was found.</param>
/// <param name="Recreated">Panels whose destroyed window was rebuilt from the model during this attempt.</param>
/// <param name="Skipped">Panels that never had a native window (other page, never shown).</param>
/// <param name="Failed">Panels whose recovery threw (logged, isolated per panel).</param>
public readonly record struct DesktopLayerRecoveryResult(int Embedded, int TopLevel, int Recreated, int Skipped, int Failed)
{
    /// <summary>
    /// Gets a value indicating whether another attempt could still improve the outcome: a panel was
    /// left top-level (the <c>WorkerW</c> may simply not exist yet) or a panel failed to recover.
    /// </summary>
    public bool NeedsRetry => TopLevel > 0 || Failed > 0;
}
