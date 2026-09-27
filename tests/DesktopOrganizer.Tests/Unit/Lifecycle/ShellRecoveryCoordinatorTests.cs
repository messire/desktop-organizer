using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="ShellRecoveryCoordinator"/> (ADR-034): recovery orchestration with every
/// collaborator mocked and time driven by a manual fake scheduler — no dispatcher, no real windows.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ShellRecoveryCoordinatorTests
{
    private static readonly DesktopLayerRecoveryResult Embedded = new(Embedded: 2, TopLevel: 0, Recreated: 2, Skipped: 1, Failed: 0);
    private static readonly DesktopLayerRecoveryResult TopLevel = new(Embedded: 0, TopLevel: 2, Recreated: 0, Skipped: 0, Failed: 0);

    private readonly Mock<IExplorerRestartMonitor> _monitor = new();
    private readonly Mock<IWindowManager> _windowManager = new();
    private readonly Mock<IVirtualDesktopService> _virtualDesktop = new();
    private readonly Mock<ISystemIconsPanelManager> _sidebar = new();
    private readonly Mock<IDesktopIconsService> _icons = new();
    private readonly Mock<ITrayIconManager> _tray = new();
    private readonly Mock<IAppLog> _log = new();

    // M10 (release-hardening 1.0 review): RunAttempt wraps IWindowManager.RecoverDesktopLayer() in
    // IDesktopScaleProvider.BeginBatch() — see BatchWrapsRecoverDesktopLayer_ExactlyOnePerAttempt below.
    private readonly Mock<IDesktopScaleProvider> _desktopScale = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly List<string> _order = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellRecoveryCoordinatorTests"/> class: by default
    /// every panel is embedded again and the icon intent applies — the classic-shell happy path.
    /// </summary>
    public ShellRecoveryCoordinatorTests()
    {
        _windowManager.Setup(w => w.RecoverDesktopLayer()).Returns(Embedded).Callback(() => _order.Add("Panels"));
        _icons.Setup(i => i.ReapplyDesiredVisibility()).Returns(true).Callback(() => _order.Add("Icons"));
        _icons.SetupGet(i => i.AreIconsVisible).Returns(false);
        _virtualDesktop.Setup(v => v.Reset()).Callback(() => _order.Add("VdReset"));
        _sidebar.Setup(s => s.ReassertAfterShellRestart()).Callback(() => _order.Add("Sidebar"));
        _tray.Setup(t => t.SetDesktopIconsVisible(It.IsAny<bool>())).Callback(() => _order.Add("Tray"));
        _desktopScale
            .Setup(d => d.BeginBatch())
            .Callback(() => _order.Add("BatchBegin"))
            .Returns(() => new RecordingDisposable(_order));
    }

    /// <summary>Start subscribes to and starts the monitor; the monitor's event arms the coalescing window.</summary>
    [Fact]
    public void Start_SubscribesAndStartsMonitor()
    {
        var sut = CreateSut();

        sut.Start();
        _monitor.Raise(m => m.ExplorerRestarted += null, EventArgs.Empty);

        _monitor.Verify(m => m.Start(), Times.Once);
        Assert.Single(_scheduler.Pending);
        Assert.Equal(DesktopLayerRecoveryPolicy.CoalesceWindow, _scheduler.Pending[0].Delay);
        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Never); // nothing runs inside the WndProc
    }

    /// <summary>Five signals within the coalescing window cost exactly one recovery.</summary>
    [Fact]
    public void BurstOfSignals_RunsOneRecovery()
    {
        var sut = CreateSut();

        for (var i = 0; i < 5; i++)
            sut.OnExplorerRestarted();

        Assert.Equal(4, _scheduler.CancelledCount); // every earlier timer was cancelled
        _scheduler.RunAll();

        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Once);
        _sidebar.Verify(s => s.ReassertAfterShellRestart(), Times.Once);
    }

    /// <summary>
    /// Classic shell (WorkerW found at once): panels + icons, then the once-per-recovery steps in order —
    /// virtual desktop reset BEFORE the sidebar, tray checkmark synced last — and an Info log.
    /// </summary>
    [Fact]
    public void EmbeddedOnFirstAttempt_CompletesInOrder()
    {
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunAll();

        Assert.Equal(new List<string> { "BatchBegin", "Panels", "BatchEnd", "Icons", "VdReset", "Sidebar", "Tray" }, _order);
        _tray.Verify(t => t.SetDesktopIconsVisible(false), Times.Once);
        _log.Verify(l => l.Info("ShellRecovery", "ShellRecoveryCompleted", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        VerifyNoWarningsOrErrors();
    }

    /// <summary>
    /// M10 (release-hardening 1.0 review): every attempt wraps its
    /// <see cref="IWindowManager.RecoverDesktopLayer"/> call in exactly one
    /// <see cref="IDesktopScaleProvider.BeginBatch"/> scope — begun immediately before the call and
    /// disposed immediately after, so every per-panel <c>ReportPanelWindow</c> call that loop makes is
    /// batched into a single deferred rebuild instead of rebuilding (and potentially raising
    /// <see cref="IDesktopScaleProvider.Changed"/>) once per panel, mid-loop.
    /// </summary>
    [Fact]
    public void BatchWrapsRecoverDesktopLayer_ExactlyOnePerAttempt()
    {
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunAll();

        _desktopScale.Verify(d => d.BeginBatch(), Times.Once);
        var beginIndex = _order.IndexOf("BatchBegin");
        var panelsIndex = _order.IndexOf("Panels");
        var endIndex = _order.IndexOf("BatchEnd");
        Assert.True(beginIndex >= 0 && beginIndex < panelsIndex && panelsIndex < endIndex);
    }

    /// <summary>
    /// Windows 11 24H2+ top-level mode: no WorkerW ever appears → bounded retries every 500 ms, then an
    /// Info "top-level mode" outcome — never a warning or error, never an endless loop.
    /// </summary>
    [Fact]
    public void NoWorkerWEver_RetriesBounded_ThenTopLevelModeInfo()
    {
        _windowManager.Setup(w => w.RecoverDesktopLayer()).Returns(TopLevel);
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        var delays = _scheduler.RunAll();

        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Exactly(DesktopLayerRecoveryPolicy.DefaultMaxAttempts));
        Assert.Equal(DesktopLayerRecoveryPolicy.CoalesceWindow, delays[0]);
        Assert.All(delays.Skip(1), d => Assert.Equal(DesktopLayerRecoveryPolicy.RetryInterval, d));
        Assert.Empty(_scheduler.Pending);

        _sidebar.Verify(s => s.ReassertAfterShellRestart(), Times.Once);
        _virtualDesktop.Verify(v => v.Reset(), Times.Once);
        _log.Verify(l => l.Info("ShellRecovery", "ShellRecoveryTopLevelMode", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        VerifyNoWarningsOrErrors();
    }

    /// <summary>A WorkerW that shows up on the third attempt completes the recovery there.</summary>
    [Fact]
    public void WorkerWAppearsLate_CompletesWhenFound()
    {
        _windowManager.SetupSequence(w => w.RecoverDesktopLayer())
            .Returns(TopLevel)
            .Returns(TopLevel)
            .Returns(Embedded);
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunAll();

        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Exactly(3));
        _log.Verify(l => l.Info("ShellRecovery", "ShellRecoveryCompleted", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A new restart during the retry window supersedes the old recovery; only the new one finishes.</summary>
    [Fact]
    public void SignalDuringRetries_SupersedesOldGeneration()
    {
        _windowManager.SetupSequence(w => w.RecoverDesktopLayer())
            .Returns(TopLevel) // gen 1, attempt 1 → retry scheduled
            .Returns(Embedded); // gen 2, attempt 1
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunNext();      // gen 1 attempt 1 → schedules retry
        var staleRetry = _scheduler.Pending[0];

        sut.OnExplorerRestarted(); // gen 2
        Assert.True(staleRetry.Cancelled);
        staleRetry.Action();       // even if the stale callback fired anyway, it must do nothing
        _scheduler.RunAll();

        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Exactly(2));
        _sidebar.Verify(s => s.ReassertAfterShellRestart(), Times.Once);
    }

    /// <summary>
    /// An icon intent that cannot be applied yet (new SHELLDLL_DefView not there) triggers a retry even
    /// when every panel is fine.
    /// </summary>
    [Fact]
    public void IconsNotYetApplied_Retries()
    {
        _icons.SetupSequence(i => i.ReapplyDesiredVisibility()).Returns(false).Returns(true);
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunAll();

        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Exactly(2));
        _icons.Verify(i => i.ReapplyDesiredVisibility(), Times.Exactly(2));
        _log.Verify(l => l.Info("ShellRecovery", "ShellRecoveryCompleted", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>
    /// Every collaborator throwing: nothing escapes, the retry budget still bounds the work, every step
    /// still runs, and the persisting problem is reported as a warning.
    /// </summary>
    [Fact]
    public void CollaboratorsThrow_NeverEscape_AndReportIncomplete()
    {
        _windowManager.Setup(w => w.RecoverDesktopLayer()).Throws(new InvalidOperationException("panels"));
        _icons.Setup(i => i.ReapplyDesiredVisibility()).Throws(new InvalidOperationException("icons"));
        _virtualDesktop.Setup(v => v.Reset()).Throws(new InvalidOperationException("vd"));
        _sidebar.Setup(s => s.ReassertAfterShellRestart()).Throws(new InvalidOperationException("sidebar"));
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        var ex = Record.Exception(() => _scheduler.RunAll());

        Assert.Null(ex);
        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Exactly(DesktopLayerRecoveryPolicy.DefaultMaxAttempts));
        _tray.Verify(t => t.SetDesktopIconsVisible(It.IsAny<bool>()), Times.Once);
        _log.Verify(l => l.Warn("ShellRecovery", "ShellRecoveryIncomplete", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>A panel that failed to recover (counted, not thrown) is retried and reported if it persists.</summary>
    [Fact]
    public void PanelFailures_RetriedThenWarned()
    {
        _windowManager.Setup(w => w.RecoverDesktopLayer()).Returns(new DesktopLayerRecoveryResult(1, 0, 0, 0, Failed: 1));
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunAll();

        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Exactly(DesktopLayerRecoveryPolicy.DefaultMaxAttempts));
        _log.Verify(l => l.Warn("ShellRecovery", "ShellRecoveryIncomplete", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    /// <summary>Dispose cancels the pending attempt, unsubscribes, and later signals/callbacks do nothing.</summary>
    [Fact]
    public void Dispose_CancelsPending_AndIgnoresLaterSignals()
    {
        var sut = CreateSut();
        sut.Start();
        sut.OnExplorerRestarted();
        var pending = _scheduler.Pending[0];

        sut.Dispose();
        pending.Action();
        _monitor.Raise(m => m.ExplorerRestarted += null, EventArgs.Empty);
        sut.OnExplorerRestarted();

        Assert.True(pending.Cancelled);
        _windowManager.Verify(w => w.RecoverDesktopLayer(), Times.Never);
        Assert.Empty(_scheduler.Pending);
    }

    /// <summary>The summary written to the log carries counts only (privacy invariant: no titles, no paths).</summary>
    [Fact]
    public void CompletionLog_CarriesCountsOnly()
    {
        KeyValuePair<string, object?>[]? data = null;
        _log.Setup(l => l.Info("ShellRecovery", "ShellRecoveryCompleted", It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback<string, string, KeyValuePair<string, object?>[]>((_, _, d) => data = d);
        var sut = CreateSut();

        sut.OnExplorerRestarted();
        _scheduler.RunAll();

        Assert.NotNull(data);
        Assert.All(data!, kv => Assert.True(kv.Value is int or bool, kv.Key));
        Assert.Contains(data!, kv => kv.Key == "recreated" && (int)kv.Value! == 2);
    }

    private ShellRecoveryCoordinator CreateSut() => new(
        _monitor.Object,
        _windowManager.Object,
        _virtualDesktop.Object,
        _sidebar.Object,
        _icons.Object,
        _tray.Object,
        _log.Object,
        _desktopScale.Object,
        _scheduler.Schedule);

    private void VerifyNoWarningsOrErrors()
    {
        _log.Verify(l => l.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);
        _log.Verify(
            l => l.Error(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Never);
    }

    /// <summary>
    /// Fake <see cref="IDesktopScaleProvider.BeginBatch"/> scope handle (M10): records "BatchEnd" into the
    /// shared <paramref name="order"/> list when disposed, so tests can assert the batch scope's Dispose
    /// happens exactly once, in the right place relative to the other recorded steps.
    /// </summary>
    /// <param name="order">The shared step-order list every other collaborator's callback also appends to.</param>
    private sealed class RecordingDisposable(List<string> order) : IDisposable
    {
        public void Dispose() => order.Add("BatchEnd");
    }

    /// <summary>
    /// Manual one-shot scheduler: records every scheduled action with its delay; tests run them
    /// explicitly, in scheduling order.
    /// </summary>
    private sealed class FakeScheduler
    {
        public List<Entry> Pending { get; } = [];

        public int CancelledCount { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var entry = new Entry(delay, action, this);
            Pending.Add(entry);
            return entry;
        }

        /// <summary>Runs the oldest pending action.</summary>
        public TimeSpan RunNext()
        {
            var next = Pending[0];
            Pending.RemoveAt(0);
            next.Action();
            return next.Delay;
        }

        /// <summary>Runs pending actions (including ones they schedule) until none are left; bounded to catch runaway loops.</summary>
        public List<TimeSpan> RunAll()
        {
            var delays = new List<TimeSpan>();
            for (var guard = 0; Pending.Count > 0; guard++)
            {
                Assert.True(guard < 100, "Recovery kept rescheduling itself — retries are not bounded.");
                delays.Add(RunNext());
            }

            return delays;
        }

        public sealed class Entry(TimeSpan delay, Action action, FakeScheduler owner) : IDisposable
        {
            public TimeSpan Delay { get; } = delay;

            public Action Action { get; } = action;

            public bool Cancelled { get; private set; }

            public void Dispose()
            {
                if (Cancelled) return;
                Cancelled = true;
                owner.CancelledCount++;
                owner.Pending.Remove(this);
            }
        }
    }
}
