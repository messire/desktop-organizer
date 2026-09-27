using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Process exit codes this application returns itself from inside a running <c>App</c> (ADR-033 part 2)
/// — as opposed to the pre-<c>App</c>-construction codes 0–4 documented on <c>Program.Main</c>, which
/// this does not duplicate.
/// </summary>
public static class ExitCodes
{
    /// <summary>
    /// A fatal unhandled exception was caught by <see cref="CrashCoordinator"/> on the UI thread
    /// (<see cref="CrashOrigin.Dispatcher"/>/<see cref="CrashOrigin.WinForms"/>) and the crash sequence
    /// ran to completion (or degraded gracefully) before <c>Application.Shutdown</c>. A crash on a
    /// genuine background thread (<see cref="CrashOrigin.Background"/>) does not use this — the CLR
    /// terminates the process itself, with its own exit code, before this application ever gets a
    /// chance to call <c>Shutdown</c>.
    /// </summary>
    public const int Crashed = 5;
}
