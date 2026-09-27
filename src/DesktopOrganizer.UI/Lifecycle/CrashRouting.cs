using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Process-wide ambient hand-off point from the crash handlers <c>Program.Main</c> installs as early as
/// possible (ADR-033 part 2, release-hardening plan §2 — <see cref="AppDomain.UnhandledException"/>,
/// <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>'s non-fatal path aside, and
/// the WinForms bridge) to the one <see cref="CrashCoordinator"/> instance <c>App</c>'s constructor
/// creates — mirroring <c>AppLog.Current</c>'s own "installed once, read from anywhere, defaults to a
/// safe no-op" shape, for the same reason: those handlers must be wired before the DI container (and
/// therefore before any <see cref="CrashCoordinator"/>) exists at all.
/// </summary>
/// <remarks>
/// <see cref="Handler"/> is <see langword="null"/> until <c>App</c>'s constructor runs. A crash that
/// manages to reach <see cref="AppDomain.CurrentDomain"/>'s handler before that (extremely early —
/// before the single-instance handshake even starts) finds nothing installed yet and is silently
/// dropped by design: <c>Program.Main</c>'s own outer <see langword="try"/>/<see langword="catch"/>
/// already covers that narrow window with its own message box, and there is no
/// <see cref="IDesktopIconsService"/>/state store/crash-report directory to act on yet regardless.
/// </remarks>
public static class CrashRouting
{
    /// <summary>
    /// The current fatal-crash handler, or <see langword="null"/> before <c>App</c>'s constructor has
    /// run. Set exactly once, from <c>App</c>'s constructor.
    /// </summary>
    public static Action<Exception, CrashOrigin>? Handler { get; set; }
}
