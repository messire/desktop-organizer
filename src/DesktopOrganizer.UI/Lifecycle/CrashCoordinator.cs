using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// The application's one fatal-crash sequence (ADR-033 part 2, release-hardening plan §2): re-show the
/// real desktop icons, write a crash report plus an always-on app-log entry, optionally save
/// application state, show a short localized dialog, then exit. Every dependency is a plain
/// delegate/interface captured by the caller ahead of time — never a fresh DI resolve during the crash
/// itself — so this can be exercised without a live WPF <see cref="System.Windows.Application"/>, a real
/// file system, or a real dialog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent:</b> guarded by a single <see cref="Interlocked"/> flag — <see cref="HandleFatal"/> runs
/// its sequence at most once across the lifetime of this instance; every later call (a second exception
/// arriving while the first is still being handled, or after it finished) is a silent no-op.
/// </para>
/// <para>
/// <b>Each step is independent:</b> a failure in one step (e.g. the <c>desktopIcons</c> dependency throwing)
/// must never skip the rest — there is nowhere left to safely report a failure of the crash handler
/// itself, so every step's exception is deliberately swallowed by <see cref="TryRun"/>.
/// </para>
/// <para>
/// <b>State is saved only for a UI-thread origin</b> (plan Q1 default): <see cref="CrashOrigin.Dispatcher"/>
/// and <see cref="CrashOrigin.WinForms"/> both run on the UI thread, where a synchronous save is safe;
/// <see cref="CrashOrigin.Background"/> means <see cref="AppDomain.UnhandledException"/> already caught
/// this on a genuine background thread, with the CLR about to terminate the process regardless —
/// attempting a save there would race the process's own teardown for no benefit.
/// </para>
/// </remarks>
public sealed class CrashCoordinator
{
    private readonly IDesktopIconsService _desktopIcons;
    private readonly Func<Exception, CrashOrigin, string> _writeCrashReport;
    private readonly IAppLog _appLog;
    private readonly Action _flushAppLog;
    private readonly Action _saveStateForUiThreadCrash;
    private readonly Func<string, bool> _showCrashDialog;
    private readonly Action<string> _openLogsFolder;
    private readonly Action<int> _exit;
    private int _handled;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashCoordinator"/> class.
    /// </summary>
    /// <param name="desktopIcons">Re-shows the real desktop icons — step 1. Resolved/captured ahead of time by the caller.</param>
    /// <param name="writeCrashReport">Writes the crash report and returns its full path (or <see cref="string.Empty"/> on failure) — step 2.</param>
    /// <param name="appLog">Records the <c>Error</c> app-log event — step 2.</param>
    /// <param name="flushAppLog">Flushes the app log so the event just written cannot be lost if the process exits abruptly — step 2.</param>
    /// <param name="saveStateForUiThreadCrash">Runs the application's existing idempotent shutdown/save sequence — step 3, <see cref="CrashOrigin.Background"/> only skips this.</param>
    /// <param name="showCrashDialog">Shows the crash dialog; receives the crash report path and returns whether the user asked to open the logs folder — step 4.</param>
    /// <param name="openLogsFolder">Opens the logs folder, called only when <paramref name="showCrashDialog"/> returned <see langword="true"/> — step 4.</param>
    /// <param name="exit">Requests process exit with the given code — step 5. Expected to no-op when called from a thread that cannot safely drive an orderly shutdown (see <see cref="CrashOrigin.Background"/>'s remarks).</param>
    public CrashCoordinator(
        IDesktopIconsService desktopIcons,
        Func<Exception, CrashOrigin, string> writeCrashReport,
        IAppLog appLog,
        Action flushAppLog,
        Action saveStateForUiThreadCrash,
        Func<string, bool> showCrashDialog,
        Action<string> openLogsFolder,
        Action<int> exit)
    {
        _desktopIcons = desktopIcons ?? throw new ArgumentNullException(nameof(desktopIcons));
        _writeCrashReport = writeCrashReport ?? throw new ArgumentNullException(nameof(writeCrashReport));
        _appLog = appLog ?? throw new ArgumentNullException(nameof(appLog));
        _flushAppLog = flushAppLog ?? throw new ArgumentNullException(nameof(flushAppLog));
        _saveStateForUiThreadCrash = saveStateForUiThreadCrash ?? throw new ArgumentNullException(nameof(saveStateForUiThreadCrash));
        _showCrashDialog = showCrashDialog ?? throw new ArgumentNullException(nameof(showCrashDialog));
        _openLogsFolder = openLogsFolder ?? throw new ArgumentNullException(nameof(openLogsFolder));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    /// <summary>
    /// Runs the fatal-crash sequence for <paramref name="exception"/>, caught on <paramref name="origin"/>.
    /// See the type remarks for idempotency, per-step isolation, and the Q1 save-state rule.
    /// </summary>
    /// <param name="exception">The fatal, unhandled exception.</param>
    /// <param name="origin">Which kind of thread it was caught on.</param>
    public void HandleFatal(Exception exception, CrashOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Interlocked.Exchange(ref _handled, 1) != 0) return;

        // Step 1: the user must get their desktop back regardless of what happens below.
        TryRun(() => _desktopIcons.SetIconsVisible(true));

        // Step 2: crash report + always-on app log, flushed before anything that might exit the process.
        var reportPath = string.Empty;
        TryRun(() => reportPath = _writeCrashReport(exception, origin));
        TryRun(() => _appLog.Error(
            "CrashCoordinator",
            "UnhandledException",
            exception,
            new KeyValuePair<string, object?>("origin", origin.ToString())));
        TryRun(_flushAppLog);

        // Step 3 (Q1 default): only a UI-thread origin may attempt to save state.
        if (origin != CrashOrigin.Background)
            TryRun(_saveStateForUiThreadCrash);

        // Step 4: a short localized dialog, offering to open the logs folder.
        var openLogsFolderRequested = false;
        TryRun(() => openLogsFolderRequested = _showCrashDialog(reportPath));

        if (openLogsFolderRequested)
            TryRun(() => _openLogsFolder(reportPath));

        // Step 5: exit. See the exit parameter's remarks for why this is safe to call unconditionally.
        TryRun(() => _exit(ExitCodes.Crashed));
    }

    /// <summary>
    /// Runs <paramref name="step"/>, swallowing any exception it throws — see type remarks on why a
    /// failure in one crash-handling step must never cancel the rest.
    /// </summary>
    /// <param name="step">The step to run.</param>
    private static void TryRun(Action step)
    {
        try
        {
            step();
        }
        catch
        {
            // Deliberately swallowed — see type remarks.
        }
    }
}
