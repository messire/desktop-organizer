using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Once-per-session app-log diagnostics for the fence panels' desktop-band Z-order keeper
/// (<c>ChildWindow.ZOrder.cs</c>; ADR-034 amendment 2026-10-09, ADR-audit review cycle 1 G2). Every swallowed
/// failure is written once per event name for the whole process — never once per panel, per show or per
/// window message.
/// </summary>
/// <remarks>
/// The <c>WM_WINDOWPOSCHANGING</c> hook must not write a log entry itself (it runs inside a window procedure, for
/// every position change of every panel): <see cref="DeferWindowProcFailure"/> only flips a flag and, the first
/// time, posts the write to the dispatcher at <see cref="DispatcherPriority.Background"/> — after the message has
/// been handled.
/// </remarks>
internal sealed class DesktopZOrderDiagnostics
{
    /// <summary>The <c>source</c> of every entry.</summary>
    internal const string Source = "ChildWindow.ZOrder";

    /// <summary>Event written (deferred) for an exception swallowed inside the <c>WM_WINDOWPOSCHANGING</c> hook.</summary>
    internal const string WindowProcFailedEvent = "ZOrderFilterFailed";

    private readonly Func<IAppLog> _log;
    private readonly AppLogEventThrottle _onceLog = new (limitPerEvent: 1, announceSuppression: false);
    private int _windowProcFailureScheduled;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopZOrderDiagnostics"/> class.
    /// </summary>
    /// <param name="log">Resolves the log at write time (so the ambient log installed by <c>Program.Main</c> is honoured).</param>
    internal DesktopZOrderDiagnostics(Func<IAppLog> log)
    {
        _log = log;
    }

    /// <summary>Gets the process-wide instance every <see cref="ChildWindow"/> shares (the session budget).</summary>
    internal static DesktopZOrderDiagnostics Shared { get; } = new (() => AppLog.Current);

    /// <summary>
    /// Writes a swallowed failure once per session per <paramref name="evt"/>. For UI-thread code outside any
    /// window procedure. Never throws.
    /// </summary>
    /// <param name="evt">The event name — the once-per-session key.</param>
    /// <param name="ex">The swallowed exception; only its type name is written.</param>
    internal void WarnOnce(string evt, Exception? ex)
    {
        try
        {
            _onceLog.Warn(_log(), Source, evt, new KeyValuePair<string, object?>("errorType", ex?.GetType().Name));
        }
        catch (Exception)
        {
            // Resolving the log failed — diagnostics must never surface on the UI thread.
        }
    }

    /// <summary>
    /// Reports an exception swallowed inside the <c>WM_WINDOWPOSCHANGING</c> hook. The first call of the session
    /// posts one <see cref="WarnOnce"/> to <paramref name="dispatcher"/>; every later call is a single interlocked
    /// exchange. Never throws.
    /// </summary>
    /// <param name="dispatcher">The panel's dispatcher (the thread the hook runs on).</param>
    /// <param name="ex">The swallowed exception.</param>
    internal void DeferWindowProcFailure(Dispatcher dispatcher, Exception ex)
    {
        if (Interlocked.Exchange(ref _windowProcFailureScheduled, 1) != 0) return;

        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => WarnOnce(WindowProcFailedEvent, ex)));
        }
        catch (Exception)
        {
            // Dispatcher shutting down: nothing safe left to report to; this session's entry is lost.
        }
    }
}
