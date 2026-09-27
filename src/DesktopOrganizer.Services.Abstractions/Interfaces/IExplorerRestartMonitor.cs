namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Detects that the Windows shell (<c>explorer.exe</c>) was restarted — crashed and came back, was
/// killed with <c>taskkill /f</c>, or was restarted from Task Manager — so the application can put its
/// desktop layer back together (ADR-034).
/// </summary>
/// <remarks>
/// <para>
/// Primary signal: the registered <c>TaskbarCreated</c> window message, which the shell broadcasts to
/// every top-level window once its new taskbar exists. Insurance signal: a slow poll of the
/// <c>Progman</c> window handle, which changes whenever the shell's desktop window is recreated — this
/// covers a <c>TaskbarCreated</c> broadcast lost to UIPI (the application running elevated) or to a
/// shell that brings the desktop up without a taskbar.
/// </para>
/// <para>
/// The two signals can both fire for one restart, and Explorer can also restart several times in a
/// row; <see cref="ExplorerRestarted"/> is therefore a raw, un-coalesced signal — debouncing it is the
/// subscriber's job.
/// </para>
/// </remarks>
public interface IExplorerRestartMonitor : IDisposable
{
    /// <summary>
    /// Raised on the thread that called <see cref="Start"/> (the WPF UI thread) whenever a shell
    /// restart is detected. Handlers must be cheap — they run inside a window procedure.
    /// </summary>
    event EventHandler? ExplorerRestarted;

    /// <summary>
    /// Gets a value indicating whether the hidden listener window exists, i.e. whether <see cref="Start"/>
    /// succeeded. When <see langword="false"/> the application simply never recovers from a shell
    /// restart automatically — the pre-ADR-034 behaviour — and the reason has been logged.
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// Creates the hidden listener window and starts the <c>Progman</c> poll. Must be called from a
    /// thread with a running message loop (the WPF UI thread). Idempotent; never throws.
    /// </summary>
    void Start();
}
