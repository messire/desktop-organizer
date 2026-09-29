namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Scheduling priority of an <see cref="IIconLoadGroup"/> (one panel's slice of the background icon-load
/// queue — see ADR-046). Within a priority level, jobs from every group at that level are served
/// round-robin, so a portal with a thousand files never starves a small panel at the same level.
/// </summary>
public enum IconLoadPriority
{
    /// <summary>The panel's window is currently visible (the current page) — always dequeued before any <see cref="Background"/> job.</summary>
    Visible = 0,

    /// <summary>The panel is on a hidden page, minimized, or otherwise not currently shown.</summary>
    Background = 1,
}
