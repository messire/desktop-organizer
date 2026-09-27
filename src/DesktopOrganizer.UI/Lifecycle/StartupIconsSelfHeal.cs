using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Startup self-heal for real desktop icon visibility (ADR-033 part 2, release-hardening plan §2). Icon
/// visibility lives entirely in the shell's own <c>SHELLDLL_DefView</c> state — this application never
/// persists it (it is deliberately session-only) — so if the previous process was killed while icons
/// were hidden (a crash that never reached its own recovery step, <c>taskkill /f</c>, a power loss),
/// they simply stay hidden for whatever process next queries them, with nothing in this application's
/// own state to say why.
/// </summary>
/// <remarks>
/// <para>
/// Every ordinary, non-crashing exit already re-shows icons before exiting (<c>App</c>'s own
/// <c>RestoreDesktopIcons</c> shutdown-sequence step) — so observing them still hidden at the very
/// START of a new session is itself the signal that the previous one never got to run that step.
/// </para>
/// <para>
/// Acts only on positive evidence (release-hardening review L5): <c>SHELLDLL_DefView</c> was actually
/// found and is hidden (<see cref="DesktopIconsVisibility.Hidden"/>). When it does not exist yet
/// (autostart at logon, before Explorer has built the desktop — a fresh one starts visible anyway)
/// nothing is touched and no intent is recorded; when Explorer's own "Show desktop icons" is off, that is
/// the user's choice and is left alone.
/// </para>
/// </remarks>
public static class StartupIconsSelfHeal
{
    /// <summary>
    /// Shows the desktop icons if <c>SHELLDLL_DefView</c> exists and is hidden. Idempotent — safe to call
    /// unconditionally at startup, on every launch, even after a perfectly ordinary previous exit.
    /// </summary>
    /// <param name="desktopIcons">The desktop icons service to inspect/heal.</param>
    public static void Apply(IDesktopIconsService desktopIcons)
    {
        ArgumentNullException.ThrowIfNull(desktopIcons);

        if (desktopIcons.QueryIconsVisibility() == DesktopIconsVisibility.Hidden)
            desktopIcons.SetIconsVisible(true);
    }
}
