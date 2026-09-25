namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Listens for an external (installer) request to shut the application down gracefully, so an
/// upgrade/uninstall can close the running Primary through its normal exit path (state save, desktop
/// icon restore, single-instance teardown) instead of the installer killing the process or leaving a
/// locked <c>.exe</c> behind.
/// </summary>
/// <remarks>
/// Backed by a named, session-scoped <see cref="System.Threading.EventWaitHandle"/> — see
/// <c>DesktopOrganizer.Services.SingleInstance.SingleInstanceNaming.InstallerShutdownRequestEventName</c>
/// for the naming contract shared with <c>installer/DesktopOrganizer.iss</c>. Only the Primary process
/// ever starts this listener (a Secondary process never reaches <c>App.StartApp</c> at all — ADR-023
/// D-2), so there is exactly one listener per user session.
/// <para>
/// <b>Thread affinity:</b> <see cref="Start"/> and <see cref="System.IDisposable.Dispose"/> are only ever
/// called from the UI thread in production (<c>App.xaml.cs</c>'s <c>StartApp</c>/<c>OnExit</c>), though
/// neither has any actual UI-thread requirement of its own. <see cref="ShutdownRequested"/> is the one
/// member with a hard thread guarantee in the OPPOSITE direction: it is always raised on a thread-pool
/// wait-callback thread, never on the UI thread — see its own remarks.
/// </para>
/// </remarks>
public interface IShutdownRequestListener : IDisposable
{
    /// <summary>
    /// Raised on a thread-pool wait-callback thread when the installer signals the shutdown-request
    /// event. Subscribers must marshal onto the UI thread themselves before touching any WPF object —
    /// this event is never raised on the UI thread.
    /// </summary>
    event EventHandler? ShutdownRequested;

    /// <summary>
    /// Creates (or opens) the named event and begins waiting for it on a thread-pool registered wait.
    /// Idempotent — a second call (including one after <see cref="System.IDisposable.Dispose"/>) is a
    /// no-op. Never throws: any failure (e.g. the named object already exists but is owned by a
    /// principal this process cannot open with the required rights) is logged and simply leaves the
    /// listener inactive, so a diagnostics/permissions problem here can never take down application
    /// startup.
    /// </summary>
    void Start();
}
