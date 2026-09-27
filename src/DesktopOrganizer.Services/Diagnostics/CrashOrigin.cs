namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Which kind of thread a fatal, unhandled exception was caught on (ADR-033 part 2, release-hardening
/// plan §2). Drives both what <c>CrashCoordinator</c> records and whether it attempts to save
/// application state (plan Q1): <see cref="Dispatcher"/>/<see cref="WinForms"/> both run on the UI
/// thread, so a synchronous save + an orderly <c>Shutdown</c> call are safe; <see cref="Background"/>
/// means the exception reached <see cref="AppDomain.UnhandledException"/> — a genuine background
/// thread — where the CLR terminates the whole process immediately after the handler returns
/// regardless of anything this process does, so no save is attempted and no exit code can be
/// guaranteed (the risk this plan section documents as "AppDomain: процесс завершает CLR, код
/// документируется как «неуправляемый»").
/// </summary>
public enum CrashOrigin
{
    /// <summary>Caught by <see cref="System.Windows.Threading.Dispatcher.UnhandledException"/> — the WPF UI thread.</summary>
    Dispatcher,

    /// <summary>
    /// Caught by the WinForms bridge (see <c>WinFormsExceptionBridge</c>) — a
    /// <see cref="System.Windows.Forms.NotifyIcon"/> callback, which runs on the very same UI thread as
    /// <see cref="Dispatcher"/>, just outside WPF's own dispatcher exception handling.
    /// </summary>
    WinForms,

    /// <summary>
    /// Caught by <see cref="AppDomain.UnhandledException"/> — a genuine background thread. By the time
    /// this fires, the CLR has already decided to terminate the process.
    /// </summary>
    Background,
}
