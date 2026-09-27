namespace DesktopOrganizer.UI.Models;

/// <summary>
/// What shell-restart recovery (ADR-034) found for one panel window — see
/// <c>ChildWindow.RecoverAfterShellRestart</c>.
/// </summary>
public enum PanelShellRecoveryOutcome
{
    /// <summary>The panel never got a native window (e.g. it lives on another page and was never shown); nothing to recover.</summary>
    NotCreated,

    /// <summary>The panel's native window was destroyed together with the dead shell; it must be rebuilt from its model.</summary>
    Destroyed,

    /// <summary>The window survived and is (again) a child of the current <c>WorkerW</c> desktop layer.</summary>
    Embedded,

    /// <summary>
    /// The window survived but no <c>WorkerW</c> desktop layer exists (yet): styles and Z-order were
    /// re-asserted and it stays top-level — transient while Explorer is still starting, permanent on
    /// Windows 11 24H2+.
    /// </summary>
    TopLevel,
}
