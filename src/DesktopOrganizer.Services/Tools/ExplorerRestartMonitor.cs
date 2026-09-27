using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="IExplorerRestartMonitor"/>
/// <remarks>
/// <para>
/// Owns its <b>own</b> hidden top-level window (not <see cref="MonitorService"/>'s — ADR-034 keeps the
/// two listeners decoupled so the per-monitor-DPI work of package F can change one without the other).
/// The window is an ordinary invisible <c>WS_POPUP</c> top-level window, because message-only windows
/// do not receive broadcasts such as <c>TaskbarCreated</c> (see <c>NativeApi.CreateMessageWindow</c>).
/// </para>
/// <para>
/// Both signals are delivered through that one window procedure, on the thread that called
/// <see cref="Start"/>: <c>TaskbarCreated</c> as a broadcast, and the <c>Progman</c> poll as a
/// <c>WM_TIMER</c> started on the same window — so there is no second timer object and no thread hop.
/// </para>
/// <para>
/// The window procedure does the minimum: compare the message id, read one handle, raise the event.
/// Everything is wrapped in <c>try</c>/<c>catch</c> and always falls through to
/// <c>DefWindowProc</c>, so a faulty subscriber can never break message processing.
/// </para>
/// </remarks>
internal sealed class ExplorerRestartMonitor(INativeApi nativeApi, IAppLog? appLog = null) : IExplorerRestartMonitor
{
    /// <summary>The shell's broadcast name (documented: "Taskbar Creation Notification").</summary>
    internal const string TaskbarCreatedMessageName = "TaskbarCreated";

    /// <summary>Interval of the <c>Progman</c>-handle insurance poll (ADR-034: 2 s).</summary>
    internal const uint ProgmanPollIntervalMs = 2000;

    /// <summary><c>WM_TIMER</c>.</summary>
    internal const int WmTimer = 0x0113;

    /// <summary>Id of the poll timer on the listener window; any non-zero value is fine.</summary>
    internal static readonly IntPtr ProgmanPollTimerId = new(0x4450);

    private const string ClassNamePrefix = "DesktopOrganizer.ExplorerWatcher.";

    // Rooted for the lifetime of the window: the native side only holds an unmanaged thunk.
    private NativeWindowProc? _wndProc;
    private NativeMessageWindow _window;
    private int _taskbarCreatedMessage;
    private IntPtr _lastProgman;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler? ExplorerRestarted;

    /// <inheritdoc />
    public bool IsListening => _window.IsValid;

    /// <summary>
    /// The log this instance writes to: the one explicitly supplied, falling back to the process-wide
    /// ambient <see cref="AppLog.Current"/> for tests that construct this type without a log.
    /// </summary>
    private IAppLog Log => appLog ?? AppLog.Current;

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed || _window.IsValid) return;

        try
        {
            _taskbarCreatedMessage = nativeApi.RegisterWindowMessage(TaskbarCreatedMessageName);
            if (_taskbarCreatedMessage == 0)
            {
                // Not fatal: the Progman poll below still detects a restart, only up to ~2 s later.
                Log.Warn("ExplorerRestartMonitor", "RegisterTaskbarCreatedFailed");
            }

            _wndProc = WndProc;
            _window = nativeApi.CreateMessageWindow(ClassNamePrefix + Guid.NewGuid().ToString("N"), _wndProc);
            if (!_window.IsValid)
            {
                // Degrade to the pre-ADR-034 behaviour (no automatic recovery) — visibly, not silently.
                Log.Warn(
                    "ExplorerRestartMonitor",
                    "ListenerWindowCreateFailed",
                    new KeyValuePair<string, object?>("win32Error", _window.LastError));
                ReleaseWindow();
                return;
            }

            // UIPI: an elevated process does not receive TaskbarCreated from a non-elevated Explorer
            // unless the message is explicitly let through. Best effort — the poll covers a failure.
            if (_taskbarCreatedMessage != 0)
                nativeApi.AllowWindowMessage(_window.Handle, _taskbarCreatedMessage);

            // Baseline for the insurance poll. Zero (Explorer not running at startup) is fine: the
            // first non-zero Progman seen afterwards then counts as "Explorer came (back) up".
            _lastProgman = nativeApi.FindWindow("Progman", null);

            if (!nativeApi.SetTimer(_window.Handle, ProgmanPollTimerId, ProgmanPollIntervalMs))
                Log.Warn("ExplorerRestartMonitor", "ProgmanPollTimerStartFailed");
        }
        catch (Exception ex)
        {
            Log.Error("ExplorerRestartMonitor", "StartFailed", ex);
            ReleaseWindow();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseWindow();
        ExplorerRestarted = null;
    }

    /// <summary>
    /// One tick of the <c>Progman</c>-handle insurance poll. Internal so tests can drive it directly;
    /// in production it runs from <c>WM_TIMER</c> inside <see cref="WndProc"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the handle changed and <see cref="ExplorerRestarted"/> was raised.</returns>
    internal bool PollProgmanOnce()
    {
        var current = nativeApi.FindWindow("Progman", null);

        // Explorer is down right now (0), or nothing changed: keep the last known handle, so the
        // new desktop window is recognised as a change the moment it appears.
        if (current == IntPtr.Zero || current == _lastProgman) return false;

        _lastProgman = current;
        Raise("progman");
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // Minimal work, never throw out of a window procedure.
        try
        {
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // Re-baseline the poll first, so the same restart is not reported a second time by
                // the next poll tick (the subscriber coalesces anyway; this just avoids the noise).
                var progman = nativeApi.FindWindow("Progman", null);
                if (progman != IntPtr.Zero) _lastProgman = progman;

                Raise("taskbarCreated");
            }
            else if (msg == WmTimer && wParam == ProgmanPollTimerId)
            {
                PollProgmanOnce();
            }
        }
        catch (Exception ex)
        {
            Log.Error("ExplorerRestartMonitor", "WndProcError", ex);
        }

        return nativeApi.DefaultWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Raise(string trigger)
    {
        Log.Info(
            "ExplorerRestartMonitor",
            "ExplorerRestartDetected",
            new KeyValuePair<string, object?>("trigger", trigger));

        ExplorerRestarted?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseWindow()
    {
        try
        {
            if (_window.IsValid)
                nativeApi.KillTimer(_window.Handle, ProgmanPollTimerId);

            if (_window.HasResources)
                nativeApi.DestroyMessageWindow(_window);
        }
        catch (Exception ex)
        {
            Log.Error("ExplorerRestartMonitor", "ReleaseFailed", ex);
        }
        finally
        {
            _window = default;
            _wndProc = null;
        }
    }
}
