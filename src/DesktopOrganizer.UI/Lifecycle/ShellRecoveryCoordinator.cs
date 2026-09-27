using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Lifecycle;

/// <summary>
/// Puts the desktop layer back together after Explorer was restarted (ADR-034): turns the raw
/// <see cref="IExplorerRestartMonitor.ExplorerRestarted"/> signal into one coalesced, bounded
/// recovery — panels (<see cref="IWindowManager.RecoverDesktopLayer"/>), the session "icons hidden"
/// intent, the virtual desktop COM cache, the sidebar and the tray checkmark.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timing</b> is owned by <see cref="DesktopLayerRecoveryPolicy"/>: 1.5 s after the last signal of a
/// burst the first attempt runs; while a panel is still top-level (no <c>WorkerW</c> yet) or failed,
/// further attempts follow every 500 ms, at most <see cref="DesktopLayerRecoveryPolicy.MaxAttempts"/> in
/// total. A new signal supersedes everything still pending.
/// </para>
/// <para>
/// <b>Two shell layouts.</b> On Windows 10 / Windows 11 up to 23H2 the <c>WorkerW</c> normally appears
/// within the retry window and recovery completes as soon as every panel is embedded again. On
/// Windows 11 24H2+ no <c>WorkerW</c> is ever found — panels are top-level by design there — so the
/// retry budget runs out; that is reported as an informational "top-level mode" outcome, never as an
/// error, and the panels have already had their styles and Z-order re-asserted by every attempt.
/// </para>
/// <para>
/// Per attempt only the idempotent, cheap steps run (panels, icon intent). The steps that should
/// happen once per recovery — resetting the virtual desktop COM cache, re-asserting the sidebar,
/// syncing the tray checkmark — run when the policy says the recovery is finished (completed or
/// exhausted). Every step is isolated; nothing here throws, and nothing here writes application state.
/// </para>
/// <para>
/// The <c>WH_MOUSE_LL</c> gesture hook needs no refresh: low-level hooks belong to this process's
/// thread, not to Explorer, and <c>DesktopGestureService</c> resolves the shell's windows afresh on
/// every event instead of caching them. The WinForms tray icon re-adds itself on
/// <c>TaskbarCreated</c> on its own.
/// </para>
/// </remarks>
public sealed class ShellRecoveryCoordinator : IDisposable
{
    private readonly IExplorerRestartMonitor _monitor;
    private readonly IWindowManager _windowManager;
    private readonly IVirtualDesktopService _virtualDesktop;
    private readonly ISystemIconsPanelManager _systemIconsPanel;
    private readonly IDesktopIconsService _desktopIcons;
    private readonly ITrayIconManager _trayIconManager;
    private readonly IAppLog _log;
    private readonly IDesktopScaleProvider _desktopScale;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly DesktopLayerRecoveryPolicy _policy;

    private IDisposable? _pending;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellRecoveryCoordinator"/> class.
    /// </summary>
    /// <param name="monitor">Source of the raw restart signal.</param>
    /// <param name="windowManager">Recovers the panel windows.</param>
    /// <param name="virtualDesktop">Its cached COM object is reset once per recovery.</param>
    /// <param name="systemIconsPanel">The sidebar, re-asserted once per recovery.</param>
    /// <param name="desktopIcons">Re-applies the session "icons hidden" intent.</param>
    /// <param name="trayIconManager">Its "Show desktop icons" checkmark is re-synced once per recovery.</param>
    /// <param name="log">Application log.</param>
    /// <param name="desktopScale">
    /// The per-monitor DPI snapshot provider (release-hardening F, ADR-037) — M10 (release-hardening 1.0
    /// review): its <see cref="IDesktopScaleProvider.BeginBatch"/> scope wraps every
    /// <see cref="IWindowManager.RecoverDesktopLayer"/> attempt (<see cref="RunAttempt"/>) so the
    /// per-panel <c>ReportPanelWindow</c> calls that loop makes never rebuild/raise
    /// <see cref="IDesktopScaleProvider.Changed"/> mid-loop — see <see cref="RunAttempt"/>'s own remarks.
    /// </param>
    /// <param name="schedule">
    /// One-shot scheduler: runs the action once after the delay, on the UI thread; disposing the
    /// returned handle cancels it. <see langword="null"/> uses a <see cref="DispatcherTimer"/> on the
    /// calling thread's dispatcher. Injectable for tests.
    /// </param>
    /// <param name="policy">Coalescing/retry policy; <see langword="null"/> uses the defaults.</param>
    public ShellRecoveryCoordinator(
        IExplorerRestartMonitor monitor,
        IWindowManager windowManager,
        IVirtualDesktopService virtualDesktop,
        ISystemIconsPanelManager systemIconsPanel,
        IDesktopIconsService desktopIcons,
        ITrayIconManager trayIconManager,
        IAppLog log,
        IDesktopScaleProvider desktopScale,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        DesktopLayerRecoveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(virtualDesktop);
        ArgumentNullException.ThrowIfNull(systemIconsPanel);
        ArgumentNullException.ThrowIfNull(desktopIcons);
        ArgumentNullException.ThrowIfNull(trayIconManager);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(desktopScale);

        _monitor = monitor;
        _windowManager = windowManager;
        _virtualDesktop = virtualDesktop;
        _systemIconsPanel = systemIconsPanel;
        _desktopIcons = desktopIcons;
        _trayIconManager = trayIconManager;
        _log = log;
        _desktopScale = desktopScale;
        _schedule = schedule ?? ScheduleOnDispatcher;
        _policy = policy ?? new DesktopLayerRecoveryPolicy();
    }

    /// <summary>
    /// Subscribes to the monitor and starts it. Call once, on the UI thread, after the panels and the
    /// sidebar have been loaded. Idempotent.
    /// </summary>
    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        _monitor.ExplorerRestarted += OnExplorerRestarted;
        _monitor.Start();
    }

    /// <summary>
    /// Stops reacting to restarts and cancels any pending attempt. Does not dispose the monitor (a DI
    /// singleton, disposed with the container). Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitor.ExplorerRestarted -= OnExplorerRestarted;
        _policy.Cancel();
        CancelPending();
    }

    /// <summary>
    /// Handles one restart signal: supersedes whatever is pending and (re)arms the coalescing window.
    /// Internal so tests can drive it without a monitor event.
    /// </summary>
    internal void OnExplorerRestarted()
    {
        if (_disposed) return;

        CancelPending();
        var delay = _policy.OnRestartSignal();
        var generation = _policy.Generation;
        _pending = _schedule(delay, () => RunAttempt(generation));
    }

    private static IDisposable ScheduleOnDispatcher(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    private void OnExplorerRestarted(object? sender, EventArgs e)
    {
        // Runs inside the monitor's window procedure: only arms a timer, the work happens later.
        try
        {
            OnExplorerRestarted();
        }
        catch (Exception ex)
        {
            _log.Error("ShellRecovery", "SignalHandlingFailed", ex);
        }
    }

    /// <summary>
    /// Runs one recovery attempt. M10 (release-hardening 1.0 review):
    /// <see cref="IWindowManager.RecoverDesktopLayer"/> re-embeds every panel in a loop, each panel
    /// individually calling <see cref="IDesktopScaleProvider.ReportPanelWindow"/> — without the
    /// <see cref="IDesktopScaleProvider.BeginBatch"/> scope below, EVERY one of those calls could rebuild
    /// the snapshot and raise <see cref="IDesktopScaleProvider.Changed"/> immediately, mid-loop, letting a
    /// subscriber (a live panel window still further along in the SAME loop, not yet re-embedded/rebuilt)
    /// write geometry against a destroyed-but-not-yet-rebuilt window. Wrapping the whole attempt in one
    /// batch defers all of that to a single rebuild (and at most one <see cref="IDesktopScaleProvider.Changed"/>)
    /// once every panel in this attempt has already been re-embedded or recreated.
    /// </summary>
    private void RunAttempt(int generation)
    {
        // Stale (superseded/cancelled) callbacks leave _pending alone — it may already hold the handle
        // of a newer generation's timer.
        if (_disposed || !_policy.TryBeginAttempt(generation)) return;
        _pending = null;

        var result = default(DesktopLayerRecoveryResult);
        var panelsFailed = false;
        try
        {
            using (_desktopScale.BeginBatch())
                result = _windowManager.RecoverDesktopLayer();
        }
        catch (Exception ex)
        {
            // RecoverDesktopLayer isolates each panel itself; this only guards the contract.
            panelsFailed = true;
            _log.Error("ShellRecovery", "PanelRecoveryFailed", ex);
        }

        var iconsApplied = TryRun(_desktopIcons.ReapplyDesiredVisibility, "IconsReapplyFailed");

        var needsRetry = panelsFailed || result.NeedsRetry || !iconsApplied;
        var step = _policy.OnAttemptCompleted(generation, needsRetry);

        switch (step)
        {
            case DesktopLayerRecoveryStep.Retry:
                _pending = _schedule(DesktopLayerRecoveryPolicy.RetryInterval, () => RunAttempt(generation));
                break;

            case DesktopLayerRecoveryStep.Completed:
            case DesktopLayerRecoveryStep.Exhausted:
                FinishRecovery(step, result, panelsFailed, iconsApplied);
                break;

            default:
                // Superseded: a newer signal already scheduled its own recovery.
                break;
        }
    }

    private void FinishRecovery(DesktopLayerRecoveryStep step, DesktopLayerRecoveryResult result, bool panelsFailed, bool iconsApplied)
    {
        // Reset first: the sidebar's placement step below must talk to the NEW Explorer instance.
        TryRun(() => _virtualDesktop.Reset(), "VirtualDesktopResetFailed");
        TryRun(_systemIconsPanel.ReassertAfterShellRestart, "SidebarReassertFailed");
        TryRun(() => _trayIconManager.SetDesktopIconsVisible(_desktopIcons.AreIconsVisible), "TraySyncFailed");

        var data = new[]
        {
            new KeyValuePair<string, object?>("attempts", _policy.AttemptsMade),
            new KeyValuePair<string, object?>("embedded", result.Embedded),
            new KeyValuePair<string, object?>("topLevel", result.TopLevel),
            new KeyValuePair<string, object?>("recreated", result.Recreated),
            new KeyValuePair<string, object?>("skipped", result.Skipped),
            new KeyValuePair<string, object?>("failed", result.Failed),
            new KeyValuePair<string, object?>("iconsApplied", iconsApplied),
        };

        if (step == DesktopLayerRecoveryStep.Completed)
        {
            _log.Info("ShellRecovery", "ShellRecoveryCompleted", data);
        }
        else if (panelsFailed || result.Failed > 0 || !iconsApplied)
        {
            // A genuine problem outlasted the whole retry window.
            _log.Warn("ShellRecovery", "ShellRecoveryIncomplete", data);
        }
        else
        {
            // Only "no WorkerW" left: the Windows 11 24H2+ layout (or a shell without the Progman
            // trick). Panels are top-level with styles and Z-order re-asserted — expected, not an error.
            _log.Info("ShellRecovery", "ShellRecoveryTopLevelMode", data);
        }
    }

    private bool TryRun(Func<bool> step, string failureEvent)
    {
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            _log.Error("ShellRecovery", failureEvent, ex);
            return false;
        }
    }

    private void TryRun(Action step, string failureEvent)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            _log.Error("ShellRecovery", failureEvent, ex);
        }
    }

    private void CancelPending()
    {
        var pending = _pending;
        _pending = null;

        try
        {
            pending?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error("ShellRecovery", "CancelPendingFailed", ex);
        }
    }

    /// <summary>Cancels a one-shot <see cref="DispatcherTimer"/> scheduled by <see cref="ScheduleOnDispatcher"/>.</summary>
    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
