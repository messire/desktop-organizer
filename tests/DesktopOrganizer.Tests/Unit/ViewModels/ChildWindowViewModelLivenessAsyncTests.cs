using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

/// <summary>
/// Unit tests for ADR-046 7c's asynchronous existence-liveness pipeline
/// (docs/PLANS/background-icon-loading.md §7/§8.3): <see cref="BaseViewModel.RefreshItemsLivenessAsync"/>'s
/// snapshot/probe/apply sequencing, its coalescing of concurrent calls, the M-C vanish-confirmation grace
/// window (<see cref="ChildWindowViewModel.ProcessPendingVanishedManagedItems"/>), and the various
/// snapshot/apply race guards. Kept in its own file (rather than the much larger
/// <c>ChildWindowViewModelTests</c>/<c>ChildWindowViewModelShellMenuTests</c>) so it does not collide with
/// concurrent, unrelated work on those files.
/// </summary>
/// <remarks>
/// Every test that needs the REAL (non-<see cref="SynchronousItemProbeQueue"/>) async pipeline installs a
/// <see cref="CollectingSynchronizationContext"/> as <see cref="SynchronizationContext.Current"/> BEFORE
/// constructing the ViewModel (<see cref="SyncContextScope"/>) and supplies a <see cref="FakeItemProbeQueue"/>
/// — see <c>BaseViewModel.ResolveItemProbeQueue</c>'s D-13 rule. A test that also cares about icon delivery
/// timing supplies a <see cref="FakeIconLoadQueue"/> too, so a freshly added item stays
/// <see cref="FileItem.IsProbePending"/> until the test explicitly delivers an icon result.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowViewModelLivenessAsyncTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Mock<IIconProvider> _iconProviderMock = new();
    private readonly Mock<IFileLauncher> _fileLauncherMock = new();
    private readonly Mock<IFolderWatcherFactory> _watcherFactoryMock = new();
    private readonly Mock<IPanelItemStorageService> _storageMock = new();
    private readonly Mock<IFileItemRenameService> _renameServiceMock = new();
    private readonly Mock<IAppSettings> _appSettingsMock = new();
    private readonly Mock<IPanelSelectionCoordinator> _selectionCoordinatorMock = new();
    private readonly Mock<IGestureDiagnostics> _diagnosticsMock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindowViewModelLivenessAsyncTests"/> class.
    /// </summary>
    public ChildWindowViewModelLivenessAsyncTests()
    {
        WpfApplicationHost.EnsureApplication();
        _iconProviderMock.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((System.Windows.Media.ImageSource)null!);
        _appSettingsMock.SetupProperty(s => s.HasShownMoveHint, false);
    }

    // ── Snapshot → probe → apply sequencing ──────────────────────────────────────────────

    /// <summary>
    /// A liveness pass's snapshot is sent to <see cref="IItemProbeQueue.ProbeAsync"/>; nothing is applied
    /// to the tracked <see cref="FileItem"/> until that call actually completes and the test pumps the
    /// delivery <see cref="SynchronizationContext"/>.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ProbeNotYetComplete_AppliesNothingUntilItDoes()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var probeQueue = new FakeItemProbeQueue();
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\a.txt"]);
        var item = Assert.Single(sut.AllItems);

        var task = sut.RefreshItemsLivenessAsync();

        Assert.False(task.IsCompleted);
        var call = Assert.Single(probeQueue.Calls);
        Assert.Equal([@"Q:\nope\a.txt"], call.Paths);
        Assert.True(item.IsProbePending, "nothing has been applied yet — the probe hasn't completed.");
    }

    /// <summary>
    /// Once the probe completes and the delivery context is pumped, the result is applied to the
    /// snapshotted item.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ProbeCompletes_AppliesResultAfterPump()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\a.txt"]);
        var item = Assert.Single(sut.AllItems);

        var task = sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: false);
        syncContext.Pump();

        Assert.True(task.IsCompleted);
        Assert.False(item.Exists);
        Assert.False(item.IsProbePending);
    }

    // ── Coalescing (ADR-046 7c remarks on RefreshItemsLivenessAsync) ─────────────────────

    /// <summary>
    /// A second call arriving while a pass is still in flight does not start a second, concurrent probe —
    /// it returns the SAME task and only triggers a fresh pass once the first one completes.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_SecondCallWhileInFlight_DoesNotStartASecondProbeImmediately()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\a.txt"]);

        var first = sut.RefreshItemsLivenessAsync();
        var second = sut.RefreshItemsLivenessAsync();

        Assert.Same(first, second);
        Assert.Single(probeQueue.Calls);

        // Completing the first pass triggers the coalesced rerun — a SECOND probe call, automatically.
        probeQueue.Calls[0].Complete(exists: true);
        syncContext.Pump();

        Assert.Equal(2, probeQueue.Calls.Count);
    }

    // ── Races: rename / removal / ReplaceAll between snapshot and apply ──────────────────

    /// <summary>
    /// An item removed from the panel between the snapshot and the probe completing is simply skipped —
    /// no exception, no resurrection.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ItemRemovedBeforeApply_ResultSkippedWithoutError()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\a.txt"]);

        sut.RefreshItemsLivenessAsync();
        sut.RemoveFileByPath(@"Q:\nope\a.txt");

        var ex = Record.Exception(() =>
        {
            probeQueue.LastCall.Complete(exists: true);
            syncContext.Pump();
        });

        Assert.Null(ex);
        Assert.Empty(sut.AllItems);
    }

    /// <summary>
    /// An item renamed between the snapshot and the probe completing: the stale (old-path) result is
    /// skipped, never misapplied to whatever now lives at that key.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ItemRenamedBeforeApply_StaleResultSkipped()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\old.txt"]);
        var item = Assert.Single(sut.AllItems);

        sut.RefreshItemsLivenessAsync();
        sut.TryRenameItem(item, @"Q:\nope\new.txt");

        probeQueue.LastCall.Complete(exists: false); // stale result for the OLD path.
        syncContext.Pump();

        // The rename's own optimistic Exists=true (RelocateInPlace, no fs) must survive — the stale
        // old-path probe result must never have been applied to the (same) item under its new path.
        Assert.True(item.Exists);
        Assert.Equal(@"Q:\nope\new.txt", item.Path);
    }

    /// <summary>
    /// A <c>ReplaceAll</c> (e.g. a Folder Portal resync) between the snapshot and the probe completing
    /// bumps the generation — the whole stale pass is discarded, never partially applied to the new item set.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ReplaceAllBeforeApply_WholePassDiscarded()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\old.txt"]);

        sut.RefreshItemsLivenessAsync();
        sut.ReplaceAll([@"Q:\nope\new.txt"]);
        var newItem = Assert.Single(sut.AllItems);

        probeQueue.Calls[0].Complete(exists: false); // stale pass for the OLD item set.
        syncContext.Pump();

        // The new item (from ReplaceAll, never itself probed by the stale pass) keeps its own optimistic
        // pending state untouched by the discarded pass.
        Assert.Equal(@"Q:\nope\new.txt", newItem.Path);
        Assert.True(newItem.Exists);
    }

    // ── M-A (review it.2): shell operation depth at APPLY time discards the whole pass ────

    /// <summary>
    /// M-A/M-6 (review it.2): a liveness pass whose probe happens to complete WHILE a shell operation's own
    /// modal loop is mid-flight on this panel — simulated here by completing the probe (and pumping the
    /// delivery context) from INSIDE <see cref="IFileLauncher.ShowContextMenu"/>'s own mocked call, exactly
    /// the nested-message-pump scenario <c>ChildWindowViewModel._shellOperationDepth</c> guards
    /// against — is discarded WHOLESALE by <see cref="ChildWindowViewModel.CanApplyLivenessResults"/>
    /// (checked at APPLY time, after the probe already returned): the present→missing transition is NOT
    /// applied right then, but it is NOT lost either — the very next, ordinary tick (no shell operation in
    /// flight) reports it.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ShellOperationDepthPositiveAtApplyTime_PassDiscarded_ReportedOnNextTick()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        const string path = @"Q:\nope\managed.txt";
        using var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([path]);

        // Confirm present first — a still-pending item's own first probe coming back missing is never a
        // vanish transition to begin with (see the dedicated test for that rule).
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: true);
        syncContext.Pump();
        var item = Assert.Single(sut.AllItems);
        Assert.True(item.Exists);

        // A second pass starts (present -> missing) but its probe answers from INSIDE a shell operation's
        // own native call — _shellOperationDepth is still 1 at that exact moment (ShowShellContextMenu's
        // own try/finally has not decremented it yet).
        sut.RefreshItemsLivenessAsync();
        var call = probeQueue.LastCall;
        _fileLauncherMock
            .Setup(l => l.ShowContextMenu(It.IsAny<string>(), It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback(() =>
            {
                call.Complete(exists: false);
                syncContext.Pump(); // runs the pass's continuation NOW, while _shellOperationDepth > 0.
            })
            .Returns(new ShellContextMenuResult(ShellContextMenuOutcome.Dismissed, null));

        sut.ShowShellContextMenu(item, new IntPtr(0x1234), 0, 0);

        Assert.True(item.Exists, "a pass that raced a shell operation must be discarded wholesale, not applied.");

        // The next, ordinary tick (no shell operation in flight) reports the SAME transition — it was
        // deferred, not lost.
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: false);
        syncContext.Pump();

        Assert.False(item.Exists);
    }

    // ── Pending item's own first probe is not a "vanished" transition ────────────────────

    /// <summary>
    /// A brand-new, still-<see cref="FileItem.IsProbePending"/> item whose very first probe comes back
    /// missing is NOT reported as a present→missing transition (there was nothing confirmed to transition
    /// FROM) — <see cref="ChildWindowViewModel.OnItemsVanished"/>'s managed-item reconcile never sees it.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_PendingItemFirstProbeMissing_NeverReportedAsVanished()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var iconQueue = new FakeIconLoadQueue(); // never delivers — item stays IsProbePending.
        var timeProvider = new ManualTimeProvider();
        const string path = @"Q:\nope\pending.txt";
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        using var sut = BuildSut(iconLoadQueue: iconQueue, itemProbeQueue: probeQueue, timeProvider: timeProvider);
        sut.AddFiles([path]);
        Assert.True(Assert.Single(sut.AllItems).IsProbePending);

        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: false);
        syncContext.Pump();

        // Advance well past the M-C grace window and run the reconcile sweep — if the item had (wrongly)
        // been queued as vanished, this would call ReconcileExternalChange for it.
        timeProvider.Advance(TimeSpan.FromMilliseconds(3000));
        sut.ProcessPendingVanishedManagedItems();

        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
    }

    // ── N-2 (small iteration 3, ADR-046 D-3): a short-circuited probe is never a real observation ────

    /// <summary>
    /// N-2 regression (small iteration 3): a probe result with <see cref="PathProbeResult.IsKnown"/>
    /// <see langword="false"/> — the remote-root circuit breaker short-circuited it without ever touching
    /// the file system (D-3) — must never be applied to a CONFIRMED-present item, even though it always
    /// carries <c>Exists = false</c> (legacy shape, not an actual observation). Applying it would grey the
    /// item out and wrongly queue it as vanished for as long as the breaker stays open.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ProbeResultNotKnown_ConfirmedPresentItemUntouched_NeverReportedAsVanished()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var timeProvider = new ManualTimeProvider();
        const string path = @"Q:\nope\onshare.lnk";
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        using var sut = BuildSut(itemProbeQueue: probeQueue, timeProvider: timeProvider);
        sut.AddFiles([path]);

        // Confirm present first — IsKnown=false's own guard is about a CONFIRMED item, not a still-pending
        // one (that case already has its own, separate rule — see the "first probe missing" test above).
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: true);
        syncContext.Pump();
        var item = Assert.Single(sut.AllItems);
        Assert.True(item.Exists);

        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(new PathProbeResult(path, Exists: false, IsDirectory: false, IsKnown: false));
        syncContext.Pump();

        Assert.True(item.Exists, "an unknown (short-circuited) probe result must never mark a confirmed-present item missing.");

        timeProvider.Advance(TimeSpan.FromMilliseconds(3000));
        sut.ProcessPendingVanishedManagedItems();
        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
    }

    // ── M-C grace window (ADR-046 7c §7.2) ────────────────────────────────────────────────

    /// <summary>
    /// A managed item confirmed missing is NOT reconciled before <see cref="ManualTimeProvider"/> has
    /// advanced past the grace window (3 s) since it was first observed missing — even across multiple
    /// liveness passes in between.
    /// </summary>
    [Fact]
    public void ProcessPendingVanishedManagedItems_BeforeGraceWindowElapses_NeverReconciles()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var timeProvider = new ManualTimeProvider();
        const string path = @"Q:\nope\managed.txt";
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        using var sut = BuildSut(itemProbeQueue: probeQueue, timeProvider: timeProvider);
        sut.AddFiles([path]);

        // First confirm the item as genuinely present (clears IsProbePending) — a still-pending item's
        // OWN first probe coming back missing is never a vanish transition to begin with (see the
        // dedicated test for that rule); this test is about the GRACE WINDOW that follows a real one.
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: true);
        syncContext.Pump();

        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: false);
        syncContext.Pump();

        sut.ProcessPendingVanishedManagedItems();
        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);

        // Just under the grace window — still not reconciled.
        timeProvider.Advance(TimeSpan.FromMilliseconds(2999));
        sut.ProcessPendingVanishedManagedItems();
        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
    }

    /// <summary>
    /// Once the grace window has fully elapsed since the item was first observed missing, the next
    /// <see cref="ChildWindowViewModel.ProcessPendingVanishedManagedItems"/> call reconciles it.
    /// </summary>
    [Fact]
    public void ProcessPendingVanishedManagedItems_AfterGraceWindowElapses_Reconciles()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var timeProvider = new ManualTimeProvider();
        const string path = @"Q:\nope\managed.txt";
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        _storageMock.Setup(s => s.ReconcileExternalChange(path))
            .Returns(new ManagedItemReconcileResult(ManagedItemReconcileKind.Deleted, null));
        using var sut = BuildSut(itemProbeQueue: probeQueue, timeProvider: timeProvider);
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);

        // First confirm the item as genuinely present (clears IsProbePending) — see the identical comment
        // in the "before grace window" test above.
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: true);
        syncContext.Pump();

        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: false);
        syncContext.Pump();

        timeProvider.Advance(TimeSpan.FromMilliseconds(3000));
        sut.ProcessPendingVanishedManagedItems();

        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Once);
        Assert.DoesNotContain(item, sut.AllItems);
    }

    /// <summary>
    /// A managed item that reappears (a later probe confirms it again) before the grace window elapses is
    /// simply dropped from the pending set — never reconciled, even once the window would otherwise have
    /// elapsed.
    /// </summary>
    [Fact]
    public void ProcessPendingVanishedManagedItems_ReappearsBeforeGraceWindow_NeverReconciled()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var timeProvider = new ManualTimeProvider();
        const string path = @"Q:\nope\managed.txt";
        _storageMock.Setup(s => s.IsManaged(path)).Returns(true);
        using var sut = BuildSut(itemProbeQueue: probeQueue, timeProvider: timeProvider);
        sut.AddFiles([path]);
        var item = Assert.Single(sut.AllItems);

        // First confirm the item as genuinely present (clears IsProbePending) — see the identical comment
        // in the "before grace window" test above.
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: true);
        syncContext.Pump();

        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: false);
        syncContext.Pump();
        Assert.False(item.Exists);

        // A fast external save-by-recreate: the next pass sees it present again, before the grace window.
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        sut.RefreshItemsLivenessAsync();
        probeQueue.LastCall.Complete(exists: true);
        syncContext.Pump();
        Assert.True(item.Exists);

        timeProvider.Advance(TimeSpan.FromMilliseconds(3000));
        sut.ProcessPendingVanishedManagedItems();

        _storageMock.Verify(s => s.ReconcileExternalChange(path), Times.Never);
        Assert.Contains(item, sut.AllItems);
    }

    // ── LivenessPassTimeout (ADR-046 7c §7.1) ─────────────────────────────────────────────

    /// <summary>
    /// A probe call that never completes within <c>LivenessPassTimeout</c> (10 s) is discarded wholesale —
    /// the pass completes anyway (never applied), the single-flight state resets, and the NEXT call starts
    /// a fresh pass rather than staying stuck forever.
    /// </summary>
    [Fact]
    public void RefreshItemsLivenessAsync_ProbeNeverCompletesWithinTimeout_PassDiscarded_NextCallStartsFresh()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var timeProvider = new ManualTimeProvider();
        using var sut = BuildSut(itemProbeQueue: probeQueue, timeProvider: timeProvider);
        sut.AddFiles([@"Q:\nope\a.txt"]);
        var item = Assert.Single(sut.AllItems);

        var task = sut.RefreshItemsLivenessAsync();
        Assert.False(task.IsCompleted);
        var abandonedCall = probeQueue.LastCall;
        Assert.False(abandonedCall.CancellationToken.IsCancellationRequested);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        syncContext.Pump();

        Assert.True(task.IsCompleted, "the timeout must resolve the pass even though the probe itself never answered.");
        Assert.True(item.IsProbePending, "a discarded pass must never apply a partial/stale result.");

        // M-2 (review it.2): the timeout doesn't just walk away from the abandoned call — it actually
        // cancels it (a linked token, distinct from the ViewModel's own long-lived liveness CTS), so
        // BackgroundItemProbeQueue's SemaphoreSlim.WaitAsync/per-path loop can free its resources instead
        // of running the abandoned batch to completion "in vain".
        Assert.True(abandonedCall.CancellationToken.IsCancellationRequested);

        // The abandoned first call is still technically in flight on the fake queue (never completed) —
        // the NEXT RefreshItemsLivenessAsync call must still start its OWN, independent probe rather than
        // wedging forever because the single-flight state never reset.
        var secondTask = sut.RefreshItemsLivenessAsync();
        Assert.NotSame(task, secondTask);
        Assert.Equal(2, probeQueue.Calls.Count);
    }

    // ── Dispose during an in-flight pass (ADR-046 7c) ─────────────────────────────────────

    /// <summary>
    /// Disposing the panel while a liveness pass is in flight cancels the probe's token; the pass unwinds
    /// without applying anything and without throwing.
    /// </summary>
    [Fact]
    public void Dispose_DuringInFlightPass_NeverAppliesAndNeverThrows()
    {
        var syncContext = new CollectingSynchronizationContext();
        using var scope = new SyncContextScope(syncContext);
        var probeQueue = new FakeItemProbeQueue();
        var sut = BuildSut(itemProbeQueue: probeQueue);
        sut.AddFiles([@"Q:\nope\a.txt"]);
        var item = Assert.Single(sut.AllItems);

        var task = sut.RefreshItemsLivenessAsync();
        var call = probeQueue.LastCall;

        var ex = Record.Exception(sut.Dispose);
        Assert.Null(ex);

        // The fake queue's own cancellation-token registration (mirroring the real contract) now faults
        // the in-flight call's task with OperationCanceledException — the pass must swallow it, not throw.
        Assert.True(call.CancellationToken.IsCancellationRequested);
        syncContext.Pump();

        Assert.True(item.IsProbePending, "a cancelled pass must never apply a result.");
    }

    /// <summary>
    /// Builds the ViewModel under test. <paramref name="iconLoadQueue"/> defaults to a FRESH, never-delivering
    /// <see cref="FakeIconLoadQueue"/> (not <see langword="null"/>/<see cref="SynchronousIconLoadQueue"/>) —
    /// every test here uses fake, non-existent paths (<c>Q:\nope\...</c>), and the Synchronous fallback
    /// would otherwise make its OWN real <c>File.Exists</c> call during <c>AddFiles</c>/<c>TryRenameItem</c>
    /// and immediately resolve <see cref="FileItem.IsProbePending"/>/<see cref="FileItem.Exists"/> before
    /// the test ever gets to drive the liveness probe itself.
    /// </summary>
    private ChildWindowViewModel BuildSut(
        IIconLoadQueue? iconLoadQueue = null,
        IItemProbeQueue? itemProbeQueue = null,
        TimeProvider? timeProvider = null) => new(
        _iconProviderMock.Object,
        _fileLauncherMock.Object,
        _watcherFactoryMock.Object,
        _storageMock.Object,
        _renameServiceMock.Object,
        _appSettingsMock.Object,
        _selectionCoordinatorMock.Object,
        _diagnosticsMock.Object,
        iconResizeDebounceTimerProvider: null,
        windowManager: null,
        pageManager: null,
        iconLoadQueue: iconLoadQueue ?? new FakeIconLoadQueue(),
        itemProbeQueue: itemProbeQueue,
        timeProvider: timeProvider);

    /// <summary>Sets a <see cref="SynchronizationContext"/> as <see cref="SynchronizationContext.Current"/> for the scope's lifetime, restoring the previous one on <see cref="Dispose"/>.</summary>
    private sealed class SyncContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous;

        public SyncContextScope(SynchronizationContext context)
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
        }

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
