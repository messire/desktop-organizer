using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL) that recognises desktop gestures.
/// See ADR-003. The hook only observes input; it suppresses a single right-button-up
/// when a lasso drag completes, so the desktop context menu does not appear.
/// </summary>
/// <remarks>
/// All P/Invoke is delegated to <see cref="INativeApi"/> per the project invariant — this class owns
/// the hook lifetime and the gesture wiring only.
/// </remarks>
internal sealed class DesktopGestureService : IDesktopGestureService, IDesktopGestureEnvironment
{
    private const int WhMouseLl = 14;

    /// <summary>
    /// Minimum time between two hook-failure log entries (per event kind). A failure that repeats on every
    /// mouse move is reported once a minute with an <c>occurrences</c> count, not once per event.
    /// </summary>
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);

    private readonly INativeApi _nativeApi;

    // Keeps the hook procedure rooted for the lifetime of the hook: the native side holds only an
    // unmanaged thunk, so a collected delegate would crash the whole input pipeline.
    private readonly LowLevelMouseCallback _proc;
    private readonly DesktopGestureStateMachine _state;
    private readonly IAppLog? _appLog;

    // M6 (release-hardening review): the hook callback never writes a log entry itself — see
    // DeferredFailureLog. One instance per event kind so the two kinds are rate-limited independently.
    private readonly DeferredFailureLog _hookFailures;
    private readonly DeferredFailureLog _subscriberFailures;
    private IntPtr _hookHandle;

    /// <inheritdoc />
    public event EventHandler? DesktopDoubleClicked;

    /// <inheritdoc />
    public event EventHandler<DesktopLassoEventArgs>? LassoCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopGestureService"/> class.
    /// </summary>
    /// <param name="nativeApi">Native API façade (all P/Invoke lives behind it).</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public DesktopGestureService(INativeApi nativeApi, IAppLog? appLog = null)
        : this(nativeApi, appLog, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopGestureService"/> class with an explicit clock
    /// (tests drive the deferred failure logging through it).
    /// </summary>
    /// <param name="nativeApi">Native API façade (all P/Invoke lives behind it).</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    /// <param name="timeProvider">Clock and timer factory for the deferred failure logging.</param>
    internal DesktopGestureService(INativeApi nativeApi, IAppLog? appLog, TimeProvider timeProvider)
    {
        _nativeApi = nativeApi;
        _proc = HookCallback;
        _state = new DesktopGestureStateMachine(this);
        _appLog = appLog;
        _hookFailures = new DeferredFailureLog(() => Log, nameof(DesktopGestureService), "HookCallbackFailed", FailureLogInterval, timeProvider);
        _subscriberFailures = new DeferredFailureLog(() => Log, nameof(DesktopGestureService), "SubscriberFailed", FailureLogInterval, timeProvider);
    }

    /// <inheritdoc />
    int IDesktopGestureEnvironment.TickCount => Environment.TickCount;

    /// <inheritdoc />
    uint IDesktopGestureEnvironment.DoubleClickTime => _nativeApi.GetDoubleClickTime();

    /// <inheritdoc />
    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookHandle = _nativeApi.SetWindowsHookEx(WhMouseLl, _proc, _nativeApi.GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            // Do not fail the app: desktop gestures degrade to "unavailable", but the reason must be
            // visible instead of silently no-oping.
            Log.Warn(
                "DesktopGestureService",
                "HookInstallFailed",
                new KeyValuePair<string, object?>("win32Error", Marshal.GetLastWin32Error()));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            _nativeApi.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        // After the unhook, so no callback can record into a disposed reporter; each flushes its
        // pending batch synchronously here (outside any hook callback).
        _hookFailures.Dispose();
        _subscriberFailures.Dispose();
    }

    /// <inheritdoc />
    /// <remarks>Over the desktop means the background or the icon view — not an app or a panel.</remarks>
    bool IDesktopGestureEnvironment.IsDesktopPoint(int x, int y)
        => _nativeApi.GetWindowClassName(_nativeApi.WindowFromPoint(x, y))
            is "SysListView32" or "SHELLDLL_DefView" or "WorkerW" or "Progman";

    /// <inheritdoc />
    /// <remarks>Empty means over the desktop but not on an icon.</remarks>
    bool IDesktopGestureEnvironment.IsEmptyDesktopPoint(int x, int y)
    {
        var hwnd = _nativeApi.WindowFromPoint(x, y);
        var cls = _nativeApi.GetWindowClassName(hwnd);

        // Pure background layers are always "empty".
        if (cls is "WorkerW" or "Progman") return true;
        if (cls is not("SysListView32" or "SHELLDLL_DefView")) return false;

        var listView = cls == "SysListView32"
            ? hwnd
            : _nativeApi.FindWindowEx(hwnd, IntPtr.Zero, "SysListView32", null);

        return listView != IntPtr.Zero && _nativeApi.IsListViewPointEmpty(listView, x, y);
    }

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <summary>
    /// The WH_MOUSE_LL callback. This runs on every mouse event of the whole OS, so it does the
    /// absolute minimum and — critically — must never let an exception escape into the hook chain:
    /// an unhandled exception here stalls input system-wide. All real work lives in
    /// <see cref="ProcessMessage"/> / <see cref="DesktopGestureStateMachine"/> behind a try/catch.
    /// A caught failure is only <i>recorded</i> here; it is logged later, off the hook thread and
    /// rate-limited, by <see cref="DeferredFailureLog"/> — a log write inside the callback could push it
    /// past <c>LowLevelHooksTimeout</c>, after which Windows silently drops the hook.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return _nativeApi.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var swallow = false;

        try
        {
            swallow = ProcessMessage(wParam, lParam);
        }
        catch (Exception ex)
        {
            // Defensive: never swallow the event on failure — a bug here must not turn into a
            // "right click stopped working" for the entire system. Also covers a garbage lParam.
            // Record only (never throws) — the log entry is written off the hook thread.
            _hookFailures.Record(ex);
            swallow = false;
        }

        var next = _nativeApi.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        return swallow ? (IntPtr)1 : next;
    }

    /// <summary>
    /// Marshals the hook payload, advances the gesture state machine and notifies subscribers.
    /// </summary>
    /// <returns>Whether the message must be suppressed.</returns>
    private bool ProcessMessage(IntPtr wParam, IntPtr lParam)
    {
        var pt = _nativeApi.ReadMouseHookPoint(lParam);

        // The state machine has already reset its own state by the time it returns a decision,
        // so a throwing subscriber below cannot leave the gesture latched.
        var decision = _state.Process((int)wParam, pt.X, pt.Y);
        if (decision.RaisesEvent) RaiseSubscribers(decision);

        return decision.Swallow;
    }

    /// <summary>
    /// Notifies subscribers inside its own guard: a throwing subscriber must not change the
    /// suppression decision, nor propagate out of the hook.
    /// </summary>
    private void RaiseSubscribers(DesktopGestureDecision decision)
    {
        try
        {
            if (decision.RaiseDoubleClick) DesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
            if (decision.Lasso is { } lasso) LassoCompleted?.Invoke(this, lasso);
        }
        catch (Exception ex)
        {
            // Still inside the hook callback: record only, logged later off the hook thread.
            _subscriberFailures.Record(ex);
        }
    }
}
