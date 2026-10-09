using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools.IconLoading;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>
/// Unit tests for <see cref="StaIconLoadQueue"/> (ADR-046, docs/PLANS/background-icon-loading.md §8.1) —
/// scheduling, dedup, cancellation, back-pressure, delivery batching and the watchdog/circuit-breaker
/// interaction, all driven deterministically through <see cref="ManualStaWorker"/>,
/// <see cref="CollectingSynchronizationContext"/> and <see cref="ManualTimeProvider"/> (no real threads, no
/// wall-clock waits — <c>docs/Refactoring/Testing/sta-test-conventions.md</c>).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class StaIconLoadQueueTests
{
    private readonly Mock<IIconProvider> _iconProvider = new();
    private readonly Mock<IPathProbe> _pathProbe = new();
    private readonly Mock<IRemotePathClassifier> _classifier = new();
    private readonly Mock<IRemoteRootCircuitBreaker> _breaker = new();
    private readonly Mock<IDiagnosticsLog> _diagnosticsLog = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<ManualStaWorker> _localWorkers = [];
    private readonly List<ManualStaWorker> _remoteWorkers = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="StaIconLoadQueueTests"/> class with defaults that make
    /// every path resolve as a fast, local, existing file with no cached type icon: every job goes through
    /// GetIcon and no test needs to special-case the type-job fan-out unless it explicitly opts in.
    /// </summary>
    public StaIconLoadQueueTests()
    {
        _pathProbe.Setup(p => p.Probe(It.IsAny<string>())).Returns((string path) => new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true));
        _pathProbe.Setup(p => p.ProbeWithinCall(It.IsAny<string>())).Returns((string path) => new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true));
        _classifier.Setup(c => c.GetOwnRemoteRoot(It.IsAny<string>())).Returns((string?)null);
        _classifier.Setup(c => c.GetShortcutTargetRemoteRoot(It.IsAny<string>())).Returns((string?)null);
        _breaker.Setup(b => b.IsOpen(It.IsAny<string>())).Returns(false);
        _breaker.Setup(b => b.BeginCall(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        _diagnosticsLog.SetupGet(l => l.IsEnabled).Returns(true);

        // Type-icon peek "hits" by default (returns SOME cached icon) so RegisterTypeInterestLocked never
        // enqueues a type job — most tests care about real-job scheduling only. IconLoadInterimTests below
        // overrides this to null to exercise the type-job fan-out itself.
        _iconProvider.Setup(p => p.TryPeekTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns(CreateFrozenIcon());
        _iconProvider.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns(CreateFrozenIcon());
    }

    // ── Ordering / dedup ─────────────────────────────────────────────────────────────────────────────

    /// <summary>FIFO within a single group: two requests submitted together resolve in submission order.</summary>
    [Fact]
    public void Submit_TwoRequestsSameGroup_ResolveInFifoOrder()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32), new IconLoadRequest("b.txt", 32)]);

        _localWorkers.Single().RunAll();
        context.Pump();

        Assert.Equal(["a.txt", "b.txt"], delivered.Select(r => r.Path));
    }

    /// <summary>A Visible-priority job is dequeued before a Background one queued earlier (D-6).</summary>
    [Fact]
    public void Dequeue_VisibleGroup_RunsBeforeEarlierBackgroundGroup()
    {
        var queue = CreateQueue();
        var (backgroundGroup, backgroundContext, backgroundDelivered) = CreateGroup(queue, IconLoadPriority.Background);
        var (visibleGroup, visibleContext, visibleDelivered) = CreateGroup(queue, IconLoadPriority.Visible);

        backgroundGroup.Submit([new IconLoadRequest("bg.txt", 32)]);
        visibleGroup.Submit([new IconLoadRequest("fg.txt", 32)]);

        var worker = _localWorkers.Single();
        worker.RunNext(); // dispatch (dequeue) — Visible beats the earlier-queued Background job
        worker.RunNext(); // execute the dequeued job (fg.txt)

        // bg.txt is still pending, so the lane itself never goes idle (D-9's lane-idle flush does not fire
        // yet) — advance past the flush interval so fg.txt's own buffered result flushes independently.
        _time.Advance(IconLoadQueueOptions.Default.DeliveryFlushInterval);
        backgroundContext.Pump();
        visibleContext.Pump();

        Assert.Equal(["fg.txt"], visibleDelivered.Select(r => r.Path));
        Assert.Empty(backgroundDelivered); // bg.txt is still pending — not even dequeued yet
    }

    /// <summary>Round-robin at the same priority: a small group is not starved behind a large one (D-6).</summary>
    [Fact]
    public void Dequeue_TwoVisibleGroups_RoundRobinsInsteadOfDrainingOneFirst()
    {
        var queue = CreateQueue();
        var (bigGroup, bigContext, bigDelivered) = CreateGroup(queue);
        var (smallGroup, smallContext, smallDelivered) = CreateGroup(queue);

        bigGroup.Submit([new IconLoadRequest("big1.txt", 32), new IconLoadRequest("big2.txt", 32)]);
        smallGroup.Submit([new IconLoadRequest("small1.txt", 32)]);

        _localWorkers.Single().RunAll(); // drains dispatch+execute for all three jobs
        bigContext.Pump();
        smallContext.Pump();

        Assert.Equal(["big1.txt", "big2.txt"], bigDelivered.Select(r => r.Path));
        Assert.Equal(["small1.txt"], smallDelivered.Select(r => r.Path));
    }

    /// <summary>Changing a group's priority moves its still-pending jobs into the other ring (D-6).</summary>
    [Fact]
    public void Priority_ChangedAfterSubmit_MovesPendingJobsToNewRing()
    {
        var queue = CreateQueue();
        var (visibleGroup, visibleContext, visibleDelivered) = CreateGroup(queue, IconLoadPriority.Visible);
        var (backgroundGroup, backgroundContext, backgroundDelivered) = CreateGroup(queue, IconLoadPriority.Background);

        // Two Background jobs queued first, then a Visible one — Visible always wins while both are still
        // at their original priorities.
        backgroundGroup.Submit([new IconLoadRequest("bg1.txt", 32), new IconLoadRequest("bg2.txt", 32)]);
        visibleGroup.Submit([new IconLoadRequest("fg.txt", 32)]);

        backgroundGroup.Priority = IconLoadPriority.Visible;

        var worker = _localWorkers.Single();
        worker.RunAll();
        visibleContext.Pump();
        backgroundContext.Pump();

        // Both groups are now Visible; fg.txt's group was already in the Visible ring, backgroundGroup's
        // jobs moved in after — round robin still applies once relocated.
        Assert.Equal(["fg.txt"], visibleDelivered.Select(r => r.Path));
        Assert.Equal(["bg1.txt", "bg2.txt"], backgroundDelivered.Select(r => r.Path));
    }

    /// <summary>Two submits of the same (path, size) dedup to a single <c>GetIcon</c> call but each waiter gets its own result.</summary>
    [Fact]
    public void Submit_SamePathAndSizeTwice_DedupsToOneGetIconCallButTwoResults()
    {
        var queue = CreateQueue();
        var (groupA, contextA, deliveredA) = CreateGroup(queue);
        var (groupB, contextB, deliveredB) = CreateGroup(queue);

        groupA.Submit([new IconLoadRequest("shared.txt", 32)]);
        groupB.Submit([new IconLoadRequest("shared.txt", 32)]);

        _localWorkers.Single().RunAll();
        contextA.Pump();
        contextB.Pump();

        _iconProvider.Verify(p => p.GetIcon("shared.txt", 32), Times.Once);
        Assert.Single(deliveredA);
        Assert.Single(deliveredB);
    }

    /// <summary>The same path at a different (snapped) size is a separate job: two <c>GetIcon</c> calls.</summary>
    [Fact]
    public void Submit_SamePathDifferentSize_IsTwoSeparateJobs()
    {
        var queue = CreateQueue();
        var (group, context, _) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("shared.txt", 16), new IconLoadRequest("shared.txt", 96)]);

        _localWorkers.Single().RunAll();
        context.Pump();

        _iconProvider.Verify(p => p.GetIcon("shared.txt", 16), Times.Once);
        _iconProvider.Verify(p => p.GetIcon("shared.txt", 96), Times.Once);
    }

    /// <summary>Path comparison is ordinal-ignore-case, matching Windows path semantics (and <c>IconProvider</c>'s own cache key).</summary>
    [Fact]
    public void Submit_SamePathDifferentCase_Dedups()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest(@"C:\Item.txt", 32), new IconLoadRequest(@"c:\item.txt", 32)]);

        _localWorkers.Single().RunAll();
        context.Pump();

        _iconProvider.Verify(p => p.GetIcon(It.IsAny<string>(), 32), Times.Once);
        Assert.Equal(2, delivered.Count);
    }

    // ── Cancellation / disposal (D-8) ────────────────────────────────────────────────────────────────

    /// <summary>Cancelling before the job ever runs means <c>GetIcon</c> is never called and no result is delivered.</summary>
    [Fact]
    public void CancelPending_BeforeJobRuns_SkipsGetIconAndDeliversNothing()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        group.CancelPending();

        _localWorkers.Single().RunAll();
        context.Pump();

        _iconProvider.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.Empty(delivered);
    }

    /// <summary>Cancelling while the job is already executing still lets <c>GetIcon</c> finish, but the result is not delivered.</summary>
    [Fact]
    public void CancelPending_DuringExecution_JobFinishesButResultIsDropped()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);

        var worker = _localWorkers.Single();
        worker.RunNext(); // dispatch (dequeue): the job is now "in flight" from the queue's perspective

        // Cancelling now races the in-flight fetch, per IIconLoadGroup's own contract: GetIcon still runs
        // (the second RunNext below), but the result is dropped at delivery.
        group.CancelPending();
        worker.RunAll();
        context.Pump();

        _iconProvider.Verify(p => p.GetIcon("a.txt", 32), Times.Once);
        Assert.Empty(delivered);
    }

    /// <summary>After the group's <see cref="IDisposable.Dispose"/> is called, no further result ever reaches <c>onResults</c>.</summary>
    [Fact]
    public void Dispose_Group_NeverDeliversAfterward()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        group.Dispose();

        _localWorkers.Single().RunAll();
        context.Pump();

        Assert.Empty(delivered);
    }

    /// <summary>
    /// M-3 (review it.2): a <c>Submit</c> call that races a group's own <c>Dispose</c> — the group is
    /// already disposed by the time <c>SubmitInternal</c> runs — creates no job at all, rather than
    /// registering one that can only ever be a dead entry (its sole waiter is never live —
    /// <c>IconJobWaiter.IsLive</c> already checks <c>!Group.IsDisposed</c>) until a later dequeue lazily
    /// drops it.
    /// </summary>
    [Fact]
    public void Submit_AfterGroupDisposed_CreatesNoJob()
    {
        var queue = CreateQueue();
        var (group, _, _) = CreateGroup(queue);

        group.Dispose();
        group.Submit([new IconLoadRequest("a.txt", 32)]);

        Assert.Equal(0, queue.PendingJobCount);
        Assert.Empty(_localWorkers);
    }

    /// <summary>A result for an already-superseded generation, still sitting in the delivery context when pumped, is dropped.</summary>
    [Fact]
    public void Pump_StaleGenerationResultInContext_IsDropped()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        _localWorkers.Single().RunAll();

        // The result is now Posted (sitting in the context) but not yet pumped — CancelPending races it.
        group.CancelPending();
        context.Pump();

        Assert.Empty(delivered);
    }

    // ── Back-pressure (D-10) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Exceeding <see cref="IconLoadQueueOptions.MaxPendingJobs"/> drops the oldest Background job as <c>Dropped</c>.</summary>
    [Fact]
    public void Submit_BeyondMaxPendingJobs_DropsOldestBackgroundJob()
    {
        var options = IconLoadQueueOptions.Default with { MaxPendingJobs = 3 };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue, IconLoadPriority.Background);

        group.Submit([
            new IconLoadRequest("a.txt", 32),
            new IconLoadRequest("b.txt", 32),
            new IconLoadRequest("c.txt", 32),
        ]);

        // A fourth distinct job pushes the tracked count past the cap — "a.txt" (oldest) is evicted
        // immediately, synchronously, before ever being dispatched to a worker.
        group.Submit([new IconLoadRequest("d.txt", 32)]);

        // The Dropped result is buffered but not yet flushed; draining the lane (b.txt/c.txt/d.txt still
        // run normally) empties it and triggers D-9's lane-idle flush.
        _localWorkers.Single().RunAll();
        context.Pump();

        var dropped = Assert.Single(delivered, r => r.Outcome == IconLoadOutcome.Dropped);
        Assert.Equal("a.txt", dropped.Path);
    }

    // ── Delivery batching (D-9) ──────────────────────────────────────────────────────────────────────

    /// <summary>A batch that reaches <see cref="IconLoadQueueOptions.DeliveryBatchSize"/> flushes immediately, without waiting for the flush interval.</summary>
    [Fact]
    public void BufferResult_ReachesBatchSize_FlushesImmediately()
    {
        var options = IconLoadQueueOptions.Default with { DeliveryBatchSize = 4, DeliveryFlushInterval = TimeSpan.FromDays(1) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit(Enumerable.Range(0, 4).Select(i => new IconLoadRequest($"item{i}.txt", 32)).ToArray());

        _localWorkers.Single().RunAll();

        // Not yet pumped: the flush already reached the context via Post once the 4th result buffered —
        // Pump() just runs the already-queued callback, it does not itself gate the flush.
        context.Pump();

        Assert.Equal(4, delivered.Count);
    }

    /// <summary>A partial (below-batch-size) buffer still flushes once its lane goes idle (D-9's third trigger) — see <see cref="BufferResult_FlushTimer_FlushesWithoutWaitingForLaneToEmpty"/> for the flush-interval trigger in isolation.</summary>
    [Fact]
    public void BufferResult_PartialBatch_FlushesWhenLaneGoesIdle()
    {
        var options = IconLoadQueueOptions.Default with { DeliveryBatchSize = 64, DeliveryFlushInterval = TimeSpan.FromMilliseconds(16) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        _localWorkers.Single().RunAll();
        context.Pump();

        Assert.Single(delivered);
    }

    /// <summary>Isolates the flush-INTERVAL trigger from the lane-idle trigger: buffers via direct access while the lane still has pending work.</summary>
    [Fact]
    public void BufferResult_FlushTimer_FlushesWithoutWaitingForLaneToEmpty()
    {
        var options = IconLoadQueueOptions.Default with { DeliveryBatchSize = 64, DeliveryFlushInterval = TimeSpan.FromMilliseconds(16) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);

        var buffered = new IconLoadResult("direct.txt", 32, group.Generation, IconLoadOutcome.Loaded, CreateFrozenIcon(), true, false);
        group.BufferResult(buffered, options.DeliveryBatchSize, options.DeliveryFlushInterval, _time);

        Assert.Equal(0, context.PostedCount);
        _time.Advance(options.DeliveryFlushInterval);
        Assert.Equal(1, context.PostedCount);

        context.Pump();
        Assert.Contains(delivered, r => r.Path == "direct.txt");
    }

    // ── Failure handling (C-4/C-5) ───────────────────────────────────────────────────────────────────

    /// <summary>An exception from <c>GetIcon</c> degrades that job to <see cref="IconLoadOutcome.Failed"/>; the next job still runs.</summary>
    [Fact]
    public void ExecuteJob_GetIconThrows_DeliversFailedAndKeepsGoing()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _iconProvider.Setup(p => p.GetIcon("bad.txt", 32)).Throws<InvalidOperationException>();

        group.Submit([new IconLoadRequest("bad.txt", 32), new IconLoadRequest("good.txt", 32)]);
        _localWorkers.Single().RunAll();
        context.Pump();

        Assert.Equal(IconLoadOutcome.Failed, delivered.Single(r => r.Path == "bad.txt").Outcome);
        Assert.Equal(IconLoadOutcome.Loaded, delivered.Single(r => r.Path == "good.txt").Outcome);
    }

    /// <summary>An unfrozen <c>ImageSource</c> is treated as a failure (C-4) and never delivered as the icon.</summary>
    [Fact]
    public void ExecuteJob_UnfrozenIcon_DeliversFailedWithNoIcon()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _iconProvider.Setup(p => p.GetIcon("unfrozen.txt", 32)).Returns(new DrawingImage()); // not frozen

        group.Submit([new IconLoadRequest("unfrozen.txt", 32)]);
        _localWorkers.Single().RunAll();
        context.Pump();

        var result = Assert.Single(delivered);
        Assert.Equal(IconLoadOutcome.Failed, result.Outcome);
        Assert.Null(result.Icon);
    }

    /// <summary>An exception thrown by the caller's own <c>onResults</c> is swallowed and logged — it must not escape the delivery pump.</summary>
    [Fact]
    public void Deliver_OnResultsThrows_IsSwallowedAndLogged()
    {
        var queue = CreateQueue();
        var context = new CollectingSynchronizationContext();
        var group = (StaIconLoadGroup)queue.CreateGroup(IconLoadPriority.Visible, context, _ => throw new InvalidOperationException("boom"));

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        _localWorkers.Single().RunAll();

        var exception = Record.Exception(() => context.Pump());

        Assert.Null(exception);
        _diagnosticsLog.Verify(
            l => l.Write("icon", "IconQueueSinkThrew", null, It.Is<KeyValuePair<string, object?>[]>(p => p.Any(kv => kv.Key == "errorType"))),
            Times.Once);
    }

    // ── Interim type icon (D-5) ──────────────────────────────────────────────────────────────────────

    /// <summary>A type-icon job runs first and fans an <see cref="IconLoadOutcome.Interim"/> result out to every waiting job of the same extension, ahead of the real <see cref="IconLoadOutcome.Loaded"/> result.</summary>
    [Fact]
    public void Submit_NoCachedTypeIcon_DeliversInterimBeforeLoaded()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _iconProvider.Setup(p => p.TryPeekTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns((ImageSource?)null);
        _iconProvider.Setup(p => p.GetTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns(CreateFrozenIcon());

        group.Submit([new IconLoadRequest("a.txt", 32), new IconLoadRequest("b.txt", 32)]);

        var worker = _localWorkers.Single();
        worker.RunAll();
        context.Pump();

        Assert.Equal(4, delivered.Count); // Interim fan-out to both waiting jobs + each job's own Loaded
        Assert.Equal(2, delivered.Count(r => r.Outcome == IconLoadOutcome.Interim));
        Assert.Equal(2, delivered.Count(r => r.Outcome == IconLoadOutcome.Loaded));
        Assert.All(delivered.Where(r => r.Outcome == IconLoadOutcome.Interim), r => Assert.True(r.Icon!.IsFrozen));
    }

    /// <summary>
    /// M-5 (review it.2, C-4): an unfrozen type icon is treated exactly like <c>GetTypeIcon</c> throwing —
    /// no <see cref="IconLoadOutcome.Interim"/> is sent, and the waiting job simply falls through to its own
    /// <see cref="IconLoadOutcome.Loaded"/> result. Previously only <c>ExecuteIconFetch</c>'s Loaded path
    /// checked <c>IsFrozen</c>; the type-icon Interim fast path did not.
    /// </summary>
    [Fact]
    public void Submit_UnfrozenTypeIcon_SkipsInterimAndLogsNotFrozen()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _iconProvider.Setup(p => p.TryPeekTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns((ImageSource?)null);
        _iconProvider.Setup(p => p.GetTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns(new DrawingImage()); // not frozen

        group.Submit([new IconLoadRequest("a.txt", 32)]);

        _localWorkers.Single().RunAll();
        context.Pump();

        Assert.Equal(IconLoadOutcome.Loaded, Assert.Single(delivered).Outcome);
        Assert.DoesNotContain(delivered, r => r.Outcome == IconLoadOutcome.Interim);
        _diagnosticsLog.Verify(
            l => l.Write(
                "icon",
                "IconResultNotFrozen",
                null,
                It.Is<KeyValuePair<string, object?>[]>(p => p.Any(kv => kv.Key == "stage" && Equals(kv.Value, "typeIcon")))),
            Times.Once);
    }

    // ── Always-on IAppLog duplication (ADR-033) ──────────────────────────────────────────────────────

    /// <summary>A disabled lane reaches the always-on app log as a Warn even when the diagnostics log is off (no <c>--diag</c>).</summary>
    [Fact]
    public void RunWatchdogPass_LaneDisabled_WritesWarnToAppLog_EvenWithDiagnosticsOff()
    {
        _diagnosticsLog.SetupGet(l => l.IsEnabled).Returns(false);
        var appLog = new Mock<IAppLog>();
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), MaxAbandonedWorkersPerLane = 0 };
        var queue = CreateQueue(options, appLog.Object);
        var (group, _, _) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("stuck.txt", 32)]);
        _localWorkers.Single().RunNext();
        _localWorkers.Single().SimulateStuck();
        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();

        appLog.Verify(l => l.Warn(It.IsAny<string>(), "IconLaneDisabled", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        appLog.Verify(l => l.Warn(It.IsAny<string>(), "IconWorkerAbandoned", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
        _diagnosticsLog.Verify(l => l.Write(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);
    }

    /// <summary>
    /// G4 (ADR-audit review cycle 1): one timeout is ONE app-log Warn carrying the fields of both trace events
    /// (lane, extension, abandoned count) — <c>IconJobTimedOut</c> no longer reaches the app log as a second entry —
    /// while the diagnostics trace keeps both events with their documented payloads.
    /// </summary>
    [Fact]
    public void RunWatchdogPass_StuckJob_WritesSingleMergedWarnToAppLog_AndBothTraceEvents()
    {
        var appLog = new Mock<IAppLog>();
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5) };
        var queue = CreateQueue(options, appLog.Object);
        var (group, _, _) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("stuck.txt", 32)]);
        _localWorkers.First().RunNext();
        _localWorkers.First().SimulateStuck();
        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();

        appLog.Verify(
            l => l.Warn(
                It.IsAny<string>(),
                "IconWorkerAbandoned",
                It.Is<KeyValuePair<string, object?>[]>(p =>
                    p.Any(kv => kv.Key == "lane" && Equals(kv.Value, "local")) &&
                    p.Any(kv => kv.Key == "extension") &&
                    p.Any(kv => kv.Key == "abandoned" && Equals(kv.Value, 1)))),
            Times.Once);
        appLog.Verify(l => l.Warn(It.IsAny<string>(), "IconJobTimedOut", It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);
        _diagnosticsLog.Verify(
            l => l.Write("icon", "IconJobTimedOut", null, It.Is<KeyValuePair<string, object?>[]>(p => p.Any(kv => kv.Key == "extension"))),
            Times.Once);
        _diagnosticsLog.Verify(
            l => l.Write("icon", "IconWorkerAbandoned", null, It.Is<KeyValuePair<string, object?>[]>(p => p.Any(kv => kv.Key == "abandoned") && p.All(kv => kv.Key != "extension"))),
            Times.Once);
    }

    /// <summary>
    /// G4 (ADR-audit review cycle 1): a sink that keeps throwing reaches the app log only
    /// <see cref="AppLogEventThrottle.DefaultLimitPerEvent"/> times, followed by one suppression notice; the
    /// diagnostics trace still records every occurrence.
    /// </summary>
    [Fact]
    public void Deliver_OnResultsKeepsThrowing_AppLogErrorsAreThrottled()
    {
        const int deliveries = AppLogEventThrottle.DefaultLimitPerEvent + 3;
        var appLog = new Mock<IAppLog>();
        var queue = CreateQueue(appLog: appLog.Object);
        var context = new CollectingSynchronizationContext();
        var group = (StaIconLoadGroup)queue.CreateGroup(IconLoadPriority.Visible, context, _ => throw new InvalidOperationException("boom"));

        for (var i = 0; i < deliveries; i++)
        {
            group.Submit([new IconLoadRequest($"f{i}.txt", 32)]);
            _localWorkers.Single().RunAll();
            context.Pump();
        }

        appLog.Verify(
            l => l.Error(It.IsAny<string>(), "IconQueueSinkThrew", It.IsAny<InvalidOperationException>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Exactly(AppLogEventThrottle.DefaultLimitPerEvent));
        appLog.Verify(
            l => l.Warn(
                It.IsAny<string>(),
                AppLogEventThrottle.SuppressedEvent,
                It.Is<KeyValuePair<string, object?>[]>(p => p.Any(kv => kv.Key == "event" && Equals(kv.Value, "IconQueueSinkThrew")))),
            Times.Once);
        _diagnosticsLog.Verify(
            l => l.Write("icon", "IconQueueSinkThrew", null, It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.AtLeast(deliveries));
    }

    /// <summary>A quarantined remote root reaches the app log as a Warn, carrying only the closed-set root kind (never the path).</summary>
    [Fact]
    public void RunWatchdogPass_StuckRemoteJob_WritesRootQuarantineWarnToAppLog()
    {
        var appLog = new Mock<IAppLog>();
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5) };
        var queue = CreateQueue(options, appLog.Object);
        var (group, _, _) = CreateGroup(queue);
        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\file.txt")).Returns(@"\\srv\share");

        group.Submit([new IconLoadRequest(@"\\srv\share\file.txt", 32)]);
        _localWorkers.Single().RunAll();
        var remoteWorker = _remoteWorkers.Single();
        remoteWorker.RunNext();
        remoteWorker.SimulateStuck();
        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();

        appLog.Verify(
            l => l.Warn(
                It.IsAny<string>(),
                "RemoteRootQuarantined",
                It.Is<KeyValuePair<string, object?>[]>(p => p.All(kv => !(Convert.ToString(kv.Value) ?? string.Empty).Contains("srv", StringComparison.OrdinalIgnoreCase)))),
            Times.Once);
    }

    /// <summary>An exception thrown by the caller's sink reaches the app log as an Error carrying the exception.</summary>
    [Fact]
    public void Deliver_OnResultsThrows_WritesErrorToAppLog()
    {
        var appLog = new Mock<IAppLog>();
        var queue = CreateQueue(appLog: appLog.Object);
        var context = new CollectingSynchronizationContext();
        var group = (StaIconLoadGroup)queue.CreateGroup(IconLoadPriority.Visible, context, _ => throw new InvalidOperationException("boom"));

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        _localWorkers.Single().RunAll();
        context.Pump();

        appLog.Verify(
            l => l.Error(It.IsAny<string>(), "IconQueueSinkThrew", It.IsAny<InvalidOperationException>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>A Freeze-invariant violation reaches the app log as an Error.</summary>
    [Fact]
    public void Submit_UnfrozenIcon_WritesErrorToAppLog()
    {
        var appLog = new Mock<IAppLog>();
        var queue = CreateQueue(appLog: appLog.Object);
        var (group, _, _) = CreateGroup(queue);
        _iconProvider.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns(new DrawingImage()); // not frozen

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        _localWorkers.Single().RunAll();

        appLog.Verify(l => l.Error(It.IsAny<string>(), "IconResultNotFrozen", null, It.IsAny<KeyValuePair<string, object?>[]>()), Times.Once);
    }

    // ── Remote lane / circuit breaker (D-2/D-3) ──────────────────────────────────────────────────────

    /// <summary>A path whose root classifies as remote runs on the Remote lane's own worker, never the Local one.</summary>
    [Fact]
    public void Submit_RemoteRoot_RunsOnRemoteLane()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\file.txt")).Returns(@"\\srv\share");

        group.Submit([new IconLoadRequest(@"\\srv\share\file.txt", 32)]);

        // The Local lane classifies and re-routes; nothing to run there for THIS job, but the dispatch loop
        // for the local lane itself still needs one pump (it dequeues, classifies, and hands off).
        _localWorkers.Single().RunAll();
        Assert.Single(_remoteWorkers);

        _remoteWorkers.Single().RunAll();
        context.Pump();

        Assert.Equal(IconLoadOutcome.Loaded, Assert.Single(delivered).Outcome);
    }

    /// <summary>
    /// M-4 (review it.2): a Remote-lane fetch probes via <see cref="IPathProbe.ProbeWithinCall"/> — never
    /// <see cref="IPathProbe.Probe"/> — since it already holds its own <c>BeginCall</c> scope for the job's
    /// root; calling <c>Probe</c> again from inside that scope would see its own in-flight call via
    /// <c>IsOpen</c> and could short-circuit a probe the breaker isn't actually blocking (the half-open
    /// trial self-short-circuit bug).
    /// </summary>
    [Fact]
    public void ExecuteJob_RemoteRoot_ProbesWithinCallNotPlainProbe()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\file.txt")).Returns(@"\\srv\share");

        group.Submit([new IconLoadRequest(@"\\srv\share\file.txt", 32)]);
        _localWorkers.Single().RunAll();
        _remoteWorkers.Single().RunAll();
        context.Pump();

        Assert.Equal(IconLoadOutcome.Loaded, Assert.Single(delivered).Outcome);
        _pathProbe.Verify(p => p.ProbeWithinCall(@"\\srv\share\file.txt"), Times.Once);
        _pathProbe.Verify(p => p.Probe(It.IsAny<string>()), Times.Never);
    }

    /// <summary>An open circuit breaker fails a Remote-lane job immediately, without ever calling <c>GetIcon</c>.</summary>
    [Fact]
    public void ExecuteJob_RemoteRootBreakerOpen_DeliversUnavailableWithoutGetIcon()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);

        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\file.txt")).Returns(@"\\srv\share");
        _breaker.Setup(b => b.IsOpen(@"\\srv\share")).Returns(true);

        group.Submit([new IconLoadRequest(@"\\srv\share\file.txt", 32)]);
        _localWorkers.Single().RunAll();
        _remoteWorkers.Single().RunAll();
        context.Pump();

        var result = Assert.Single(delivered);
        Assert.Equal(IconLoadOutcome.Unavailable, result.Outcome);

        // UX-CRITICAL (review it.2): a breaker-open short-circuit never touched the file system, so
        // Exists/IsDirectory must stay unknown (null) — same as every other Unavailable branch. Reporting
        // exists:false here made BaseViewModel.OnIconResults grey out the item's OWN local path (e.g. a
        // local .lnk whose target is an unreachable share) as "missing" for seconds.
        Assert.Null(result.Exists);
        Assert.Null(result.IsDirectory);
        _iconProvider.Verify(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    // ── Watchdog (D-4) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A Local-lane job stuck past <c>StuckJobTimeout</c> is abandoned: waiters get <c>Unavailable</c>, the worker is abandoned and replaced, and the path is quarantined.</summary>
    [Fact]
    public void RunWatchdogPass_StuckLocalJob_AbandonsWorkerAndQuarantinesPath()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), PathQuarantine = TimeSpan.FromMinutes(5) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("stuck.txt", 32)]);
        var stuckWorker = _localWorkers.Single();
        stuckWorker.RunNext(); // dispatch (dequeue): CurrentJob/StartedAtUtc are now set; the actual GetIcon call is what's about to hang
        stuckWorker.SimulateStuck();

        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();
        _time.Advance(options.DeliveryFlushInterval); // flush the buffered Unavailable result (the lane itself never goes idle here — see BufferResult's own tests)
        context.Pump();

        Assert.True(stuckWorker.IsAbandoned);
        Assert.Equal(IconLoadOutcome.Unavailable, Assert.Single(delivered).Outcome);
        Assert.Equal(2, _localWorkers.Count); // watchdog created a replacement

        // A repeat submit for the quarantined path resolves Unavailable immediately, without ever calling GetIcon.
        var (group2, context2, delivered2) = CreateGroup(queue);
        group2.Submit([new IconLoadRequest("stuck.txt", 32)]);
        _localWorkers[1].RunAll();
        context2.Pump();

        Assert.Equal(IconLoadOutcome.Unavailable, Assert.Single(delivered2).Outcome);
        _iconProvider.Verify(p => p.GetIcon("stuck.txt", 32), Times.Never);

        // Quarantine expires after PathQuarantine.
        _time.Advance(options.PathQuarantine);
        var (group3, context3, delivered3) = CreateGroup(queue);
        group3.Submit([new IconLoadRequest("stuck.txt", 32)]);
        _localWorkers[1].RunAll();
        context3.Pump();

        Assert.Equal(IconLoadOutcome.Loaded, Assert.Single(delivered3).Outcome);
    }

    /// <summary>Exceeding <see cref="IconLoadQueueOptions.MaxAbandonedWorkersPerLane"/> disables the lane: every pending job resolves <c>Unavailable</c> and no new worker is created.</summary>
    [Fact]
    public void RunWatchdogPass_ExceedsMaxAbandonedWorkers_DisablesLane()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), MaxAbandonedWorkersPerLane = 2 };
        var queue = CreateQueue(options);

        // Two sequential stuck jobs (worker #0 then its replacement #1) — each abandonment stays within
        // MaxAbandonedWorkersPerLane = 2, so the lane replaces its worker and keeps going both times.
        for (var i = 0; i < 2; i++)
        {
            var (group, _, _) = CreateGroup(queue);
            group.Submit([new IconLoadRequest($"stuck{i}.txt", 32)]);
            _localWorkers[i].RunNext(); // dispatch (dequeue), on the CURRENT worker for this lane
            _localWorkers[i].SimulateStuck();
            _time.Advance(options.StuckJobTimeout);
            queue.RunWatchdogPass();
        }

        // 1 initial worker + 2 replacements (one per abandonment above).
        Assert.Equal(3, _localWorkers.Count);

        // A third abandonment exceeds the limit and disables the lane.
        var (lastGroup, lastContext, lastDelivered) = CreateGroup(queue);
        lastGroup.Submit([new IconLoadRequest("stuck2.txt", 32)]);
        _localWorkers[2].RunNext(); // dispatch (dequeue) — the second replacement worker from the loop above
        _localWorkers[2].SimulateStuck();
        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();
        _time.Advance(options.DeliveryFlushInterval); // flush the buffered Unavailable result
        lastContext.Pump();

        Assert.Equal(IconLoadOutcome.Unavailable, Assert.Single(lastDelivered).Outcome);

        // Lane disabled: no fourth replacement worker, and a brand-new submit never even gets a worker.
        Assert.Equal(3, _localWorkers.Count);
        var (afterDisableGroup, afterDisableContext, afterDisableDelivered) = CreateGroup(queue);
        afterDisableGroup.Submit([new IconLoadRequest("after-disable.txt", 32)]);
        _time.Advance(options.DeliveryFlushInterval); // flush the buffered Unavailable result
        afterDisableContext.Pump();

        Assert.Equal(IconLoadOutcome.Unavailable, Assert.Single(afterDisableDelivered).Outcome);
        Assert.Equal(3, _localWorkers.Count);
    }

    /// <summary>A stuck Remote-lane job reports the root to the circuit breaker instead of quarantining a path.</summary>
    [Fact]
    public void RunWatchdogPass_StuckRemoteJob_ReportsRootToBreaker()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\file.txt")).Returns(@"\\srv\share");

        group.Submit([new IconLoadRequest(@"\\srv\share\file.txt", 32)]);
        _localWorkers.Single().RunAll(); // classify + hand off to Remote lane

        var remoteWorker = _remoteWorkers.Single();
        remoteWorker.RunNext(); // dispatch (dequeue) on the Remote lane
        remoteWorker.SimulateStuck();
        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();
        _time.Advance(options.DeliveryFlushInterval); // flush the buffered Unavailable result
        context.Pump();

        Assert.True(remoteWorker.IsAbandoned);
        Assert.Equal(IconLoadOutcome.Unavailable, Assert.Single(delivered).Outcome);
        _breaker.Verify(b => b.ReportStuck(@"\\srv\share"), Times.Once);
    }

    /// <summary>M1: a Local-lane type-icon job stuck past <c>StuckJobTimeout</c> is abandoned exactly like a real job — its worker is replaced and its registration dropped — instead of wedging the lane forever behind a hung <c>GetTypeIcon</c> call.</summary>
    [Fact]
    public void RunWatchdogPass_StuckTypeJob_AbandonsWorkerAndDropsRegistration()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        _iconProvider.Setup(p => p.TryPeekTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns((ImageSource?)null);

        group.Submit([new IconLoadRequest("a.txt", 32)]);

        var stuckWorker = _localWorkers.Single();
        stuckWorker.RunNext(); // dispatch: dequeues the TYPE job (D-5: it always runs first), marks it current
        stuckWorker.SimulateStuck(); // the GetTypeIcon call itself is what's hanging

        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass();

        Assert.True(stuckWorker.IsAbandoned);
        Assert.Equal(2, _localWorkers.Count); // watchdog created a replacement, same as a stuck real job

        // The real job waiting on the (now dropped) type key falls through to its own Loaded result — it
        // never gets an Interim — once the replacement worker drains it.
        _localWorkers[1].RunAll();
        _time.Advance(options.DeliveryFlushInterval);
        context.Pump();

        Assert.Equal(IconLoadOutcome.Loaded, Assert.Single(delivered).Outcome);
    }

    /// <summary>M3/M4/L8: the watchdog timer is armed automatically once a unit starts executing and — being genuinely periodic now (L8) — fires on its own without any test ever calling <c>RunWatchdogPass</c> directly.</summary>
    [Fact]
    public void WatchdogTimer_ArmedWhileJobExecutes_FiresAutomaticallyAndAbandonsStuckJob()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), WatchdogPeriod = TimeSpan.FromMilliseconds(500) };
        var queue = CreateQueue(options);
        var (group, _, _) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("stuck.txt", 32)]);
        var stuckWorker = _localWorkers.Single();
        stuckWorker.RunNext(); // dispatch — arms the watchdog timer (M4)
        stuckWorker.SimulateStuck();

        for (var i = 0; i < 11 && !stuckWorker.IsAbandoned; i++) _time.Advance(options.WatchdogPeriod);

        Assert.True(stuckWorker.IsAbandoned);
        Assert.Equal(2, _localWorkers.Count);
    }

    /// <summary>M3/C-5: an exception the watchdog scan itself throws (here, from diagnostics logging) must never escape the ThreadPool timer callback.</summary>
    [Fact]
    public void WatchdogTimer_DiagnosticsThrows_DoesNotPropagateOutOfTimerCallback()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), WatchdogPeriod = TimeSpan.FromMilliseconds(500) };
        var queue = CreateQueue(options);
        var (group, _, _) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("stuck.txt", 32)]);
        var stuckWorker = _localWorkers.Single();
        stuckWorker.RunNext();
        stuckWorker.SimulateStuck();

        _diagnosticsLog.SetupGet(l => l.IsEnabled).Throws<InvalidOperationException>();

        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 11; i++) _time.Advance(options.WatchdogPeriod);
        });

        Assert.Null(exception);
    }

    /// <summary>M4: once a job finishes normally, the watchdog timer disarms — advancing time well past <c>StuckJobTimeout</c> afterward must not abandon anything (there is nothing left running to abandon).</summary>
    [Fact]
    public void WatchdogTimer_DisarmsAfterJobCompletes_NoPhantomAbandonLater()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), WatchdogPeriod = TimeSpan.FromMilliseconds(500) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        group.Submit([new IconLoadRequest("a.txt", 32)]);
        _localWorkers.Single().RunAll(); // dispatch + execute + drain to idle — arms then disarms the watchdog
        context.Pump();

        Assert.Single(delivered);

        for (var i = 0; i < 11; i++) _time.Advance(options.WatchdogPeriod);

        Assert.False(_localWorkers.Single().IsAbandoned);
        Assert.Single(_localWorkers);
    }

    /// <summary>H2: a Remote lane the watchdog already disabled must not silently swallow a NEW job that classifies as remote — it resolves <see cref="IconLoadOutcome.Unavailable"/> instead of leaking the job forever in a bucket nothing will ever drain.</summary>
    [Fact]
    public void MoveJobToRemoteLane_RemoteLaneAlreadyDisabled_ResolvesUnavailable()
    {
        var options = IconLoadQueueOptions.Default with { StuckJobTimeout = TimeSpan.FromSeconds(5), MaxAbandonedWorkersPerLane = 0 };
        var queue = CreateQueue(options);

        // Disable the Remote lane: with MaxAbandonedWorkersPerLane = 0, one stuck Remote job is enough.
        var (setupGroup, _, _) = CreateGroup(queue);
        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\first.txt")).Returns(@"\\srv\share");
        setupGroup.Submit([new IconLoadRequest(@"\\srv\share\first.txt", 32)]);
        _localWorkers.Single().RunAll(); // classify + hand off to Remote lane
        var remoteWorker = _remoteWorkers.Single();
        remoteWorker.RunNext();
        remoteWorker.SimulateStuck();
        _time.Advance(options.StuckJobTimeout);
        queue.RunWatchdogPass(); // abandons the only allowed worker -> disables the Remote lane

        // A brand new job that also classifies as remote must resolve Unavailable, not hang forever.
        var (group, context, delivered) = CreateGroup(queue);
        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\second.txt")).Returns(@"\\srv\share");
        group.Submit([new IconLoadRequest(@"\\srv\share\second.txt", 32)]);
        _localWorkers[0].RunAll(); // classify — MoveJobToRemoteLane sees the Remote lane disabled

        _time.Advance(options.DeliveryFlushInterval);
        context.Pump();

        Assert.Equal(IconLoadOutcome.Unavailable, Assert.Single(delivered).Outcome);
        Assert.Single(_remoteWorkers); // no phantom replacement for the disabled lane
    }

    /// <summary>M6: a job <see cref="StaIconLoadQueue"/> just moved to the Remote lane is reset back to "pending, not executing" — a back-pressure eviction scan must be able to evict it like any other pending job instead of skipping it forever as "still executing".</summary>
    [Fact]
    public void MoveJobToRemoteLane_ResetsExecutingAndStartedAt_JobRemainsEvictable()
    {
        var options = IconLoadQueueOptions.Default with { MaxPendingJobs = 1 };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue, IconLoadPriority.Background);

        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\remote.txt")).Returns(@"\\srv\share");

        group.Submit([new IconLoadRequest(@"\\srv\share\remote.txt", 32)]);

        var localWorker = _localWorkers.Single();
        localWorker.RunNext(); // dispatch: marks Executing/CurrentJob on the Local lane
        localWorker.RunNext(); // execute: ExecuteRealJob classifies remote and calls MoveJobToRemoteLane

        Assert.Single(_remoteWorkers); // moved and the Remote lane started, but nothing has run there yet

        // A second job pushes past MaxPendingJobs = 1 — remote.txt (now pending in the Remote lane's
        // bucket, not currently executing) must be evictable; before M6 it stayed Executing == true from
        // its earlier Local-lane dispatch and eviction silently skipped it, exceeding the cap instead.
        group.Submit([new IconLoadRequest("other.txt", 32)]);

        _time.Advance(options.DeliveryFlushInterval);
        context.Pump();

        var dropped = Assert.Single(delivered, r => r.Outcome == IconLoadOutcome.Dropped);
        Assert.Equal(@"\\srv\share\remote.txt", dropped.Path);
    }

    /// <summary>M7: a Visible-priority waiter joining a job someone else already queued at Background priority promotes that job into its owner group's Visible bucket, so it is dequeued ahead of the owner's OTHER, still-Background work queued earlier.</summary>
    [Fact]
    public void Submit_VisibleWaiterJoinsPendingBackgroundJob_PromotesItAheadOfOlderBackgroundWork()
    {
        var queue = CreateQueue();
        var (groupA, contextA, deliveredA) = CreateGroup(queue, IconLoadPriority.Background);
        var (groupB, contextB, deliveredB) = CreateGroup(queue, IconLoadPriority.Visible);

        groupA.Submit([new IconLoadRequest("bg-other.txt", 32), new IconLoadRequest("shared.txt", 32)]);
        groupB.Submit([new IconLoadRequest("shared.txt", 32)]); // joins as a Visible waiter -> promotion

        var worker = _localWorkers.Single();
        worker.RunNext(); // dispatch — must pick shared.txt (now Visible) over the older bg-other.txt
        worker.RunNext(); // execute it

        _time.Advance(IconLoadQueueOptions.Default.DeliveryFlushInterval);
        contextA.Pump();
        contextB.Pump();

        Assert.Contains(deliveredA, r => r.Path == "shared.txt");
        Assert.Contains(deliveredB, r => r.Path == "shared.txt");
        Assert.DoesNotContain(deliveredA, r => r.Path == "bg-other.txt"); // not yet run — proves the jump
    }

    /// <summary>M7: a job with a live Visible-priority waiter is never evicted under back-pressure, regardless of which group originally queued it or how old it is.</summary>
    [Fact]
    public void EvictOldestBackgroundJob_JobHasLiveVisibleWaiter_IsNeverEvicted()
    {
        var options = IconLoadQueueOptions.Default with { MaxPendingJobs = 2 };
        var queue = CreateQueue(options);
        var (groupA, contextA, deliveredA) = CreateGroup(queue, IconLoadPriority.Background);
        var (groupB, contextB, deliveredB) = CreateGroup(queue, IconLoadPriority.Visible);

        // shared.txt is the OLDEST job — without M7's "skip a live Visible waiter" check, the plain
        // oldest-first scan below would pick it for eviction.
        groupA.Submit([new IconLoadRequest("shared.txt", 32), new IconLoadRequest("a.txt", 32)]);
        groupB.Submit([new IconLoadRequest("shared.txt", 32)]); // promotes it AND gives it a live Visible waiter

        // A third distinct job pushes past MaxPendingJobs = 2 — "a.txt" (no live Visible waiter) must be
        // evicted instead of "shared.txt".
        groupA.Submit([new IconLoadRequest("c.txt", 32)]);

        _localWorkers.Single().RunAll();
        _time.Advance(options.DeliveryFlushInterval);
        contextA.Pump();
        contextB.Pump();

        Assert.Contains(deliveredA, r => r.Path == "a.txt" && r.Outcome == IconLoadOutcome.Dropped);
        Assert.DoesNotContain(deliveredA.Concat(deliveredB), r => r.Path == "shared.txt" && r.Outcome == IconLoadOutcome.Dropped);
    }

    /// <summary>M7: a job nobody is even waiting for any more (every waiter already detached) is preferred for eviction over an older job a live Background waiter still wants.</summary>
    [Fact]
    public void EvictOldestBackgroundJob_PrefersJobWithNoLiveWaiters_OverOlderLiveJob()
    {
        var options = IconLoadQueueOptions.Default with { MaxPendingJobs = 2 };
        var queue = CreateQueue(options);
        var (aliveGroup, aliveContext, aliveDelivered) = CreateGroup(queue, IconLoadPriority.Background);

        aliveGroup.Submit([new IconLoadRequest("alive.txt", 32)]); // oldest, and stays live throughout

        var deadGroup = (StaIconLoadGroup)queue.CreateGroup(IconLoadPriority.Background, new CollectingSynchronizationContext(), _ => { });
        deadGroup.Submit([new IconLoadRequest("dead.txt", 32)]); // newer than alive.txt
        deadGroup.Dispose(); // its only waiter is no longer live

        // A third job pushes count (2) to the cap (2) — dead.txt (no live waiters) must be preferred over
        // alive.txt even though dead.txt is the NEWER of the two.
        aliveGroup.Submit([new IconLoadRequest("third.txt", 32)]);

        _localWorkers.Single().RunAll();
        _time.Advance(options.DeliveryFlushInterval);
        aliveContext.Pump();

        Assert.DoesNotContain(aliveDelivered, r => r.Path == "alive.txt" && r.Outcome == IconLoadOutcome.Dropped);
        Assert.Contains(aliveDelivered, r => r.Path == "alive.txt" && r.Outcome == IconLoadOutcome.Loaded);
        Assert.Contains(aliveDelivered, r => r.Path == "third.txt" && r.Outcome == IconLoadOutcome.Loaded);
    }

    /// <summary>M5: <c>TerminateJob</c> must collect its (group, result) pairs and call <c>BufferResult</c>/deliver AFTER releasing the queue's internal lock — an inline delivery context whose <c>Post</c> runs synchronously, re-entering <c>Submit</c> for the SAME path from inside <c>onResults</c>, must not corrupt the very <c>Waiters</c> list <c>TerminateJob</c> is enumerating (an <see cref="InvalidOperationException"/> with the old, still-locked call site).</summary>
    [Fact]
    public void TerminateJob_InlineContextImmediateFlush_ReentrantSubmitSamePath_DoesNotThrow()
    {
        var options = IconLoadQueueOptions.Default with { DeliveryBatchSize = 1 };
        var queue = CreateQueue(options);
        var context = new InlineSynchronizationContext();
        StaIconLoadGroup? group = null;
        var delivered = new List<IconLoadResult>();
        var reentered = false;

        group = (StaIconLoadGroup)queue.CreateGroup(IconLoadPriority.Visible, context, results =>
        {
            delivered.AddRange(results);
            if (!reentered)
            {
                reentered = true;
                group!.Submit([new IconLoadRequest("a.txt", 32)]); // reentrant Submit of the SAME path
            }
        });

        group.Submit([new IconLoadRequest("a.txt", 32)]);

        var exception = Record.Exception(() => _localWorkers.Single().RunAll());

        Assert.Null(exception);
        Assert.Contains(delivered, r => r.Path == "a.txt");
    }

    /// <summary>
    /// Reaction to <see cref="IStaWorker.TryPost"/>'s "dead for good" contract (StaWorkerThread's own
    /// dead-pump detection, ADR-046): a worker whose pump died on its own — <see cref="IStaWorker.IsAbandoned"/>
    /// never set — must be replaced (and disposed) immediately instead of the lane going idle forever with no
    /// dispatch loop ever running again for it.
    /// </summary>
    [Fact]
    public void PostDispatch_WorkerPumpDiedWithoutAbandon_RecreatesAndDisposesOldWorker()
    {
        var deadWorker = new DeadPumpStaWorker();
        var replacementCreated = false;

        var workerFactory = new Mock<IStaWorkerFactory>();
        workerFactory.SetupSequence(f => f.Create(It.IsAny<string>()))
            .Returns(deadWorker)
            .Returns(() =>
            {
                replacementCreated = true;
                var replacement = new ManualStaWorker();
                _localWorkers.Add(replacement);
                return replacement;
            });

        var queue = new StaIconLoadQueue(
            _iconProvider.Object,
            _pathProbe.Object,
            _classifier.Object,
            _breaker.Object,
            workerFactory.Object,
            _diagnosticsLog.Object,
            IconLoadQueueOptions.Default,
            _time);

        var (group, context, delivered) = CreateGroup(queue);
        group.Submit([new IconLoadRequest("a.txt", 32)]);

        Assert.True(replacementCreated);
        Assert.True(deadWorker.Disposed);
        Assert.Single(_localWorkers); // only the replacement is tracked as a ManualStaWorker

        // The replacement worker actually received the job — PostDispatch retried via EnsureLaneRunning.
        _localWorkers.Single().RunAll();
        _time.Advance(IconLoadQueueOptions.Default.DeliveryFlushInterval);
        context.Pump();

        Assert.Equal(IconLoadOutcome.Loaded, Assert.Single(delivered).Outcome);
    }

    /// <summary>H3: a disposed group is removed from the queue's live-group bookkeeping (not merely marked disposed) — it is not leaked and no further sweep (flush/Dispose) ever visits it again.</summary>
    [Fact]
    public void Dispose_Group_RemovesItFromQueuesLiveGroups()
    {
        var queue = CreateQueue();
        var (groupA, _, _) = CreateGroup(queue);
        var (groupB, _, _) = CreateGroup(queue);

        Assert.Equal(2, queue.LiveGroupCount);

        groupA.Dispose();

        Assert.Equal(1, queue.LiveGroupCount);
    }

    /// <summary>L2/D-14: once the queue is disposed, a result already buffered (below batch size, flush timer not yet due) must never trigger a late <c>Post</c> — <c>SuppressPendingDelivery</c> disarms each group's flush timer and drops its buffer synchronously at Dispose time.</summary>
    [Fact]
    public void Dispose_Queue_SuppressesAlreadyBufferedResult_NoLatePost()
    {
        var options = IconLoadQueueOptions.Default with { DeliveryBatchSize = 64, DeliveryFlushInterval = TimeSpan.FromMilliseconds(16) };
        var queue = CreateQueue(options);
        var (group, context, delivered) = CreateGroup(queue);

        var buffered = new IconLoadResult("buffered.txt", 32, group.Generation, IconLoadOutcome.Loaded, CreateFrozenIcon(), true, false);
        group.BufferResult(buffered, options.DeliveryBatchSize, options.DeliveryFlushInterval, _time);

        Assert.Equal(0, context.PostedCount);

        queue.Dispose();

        _time.Advance(options.DeliveryFlushInterval);

        Assert.Equal(0, context.PostedCount);
        Assert.Empty(delivered);
    }

    /// <summary>L3: Dispose requests BOTH workers to stop (<c>Abandon</c>) before waiting on either (<c>Dispose</c>) — the two shutdown requests are issued back-to-back instead of fully serializing "abandon + bounded join" for one lane before even signalling the other.</summary>
    [Fact]
    public void Dispose_Queue_AbandonsBothWorkersBeforeDisposingEither()
    {
        var callOrder = new List<string>();
        var localWorker = new RecordingOrderStaWorker("local", callOrder);
        var remoteWorker = new RecordingOrderStaWorker("remote", callOrder);

        var workerFactory = new Mock<IStaWorkerFactory>();
        workerFactory.SetupSequence(f => f.Create(It.IsAny<string>()))
            .Returns(localWorker)
            .Returns(remoteWorker);

        var queue = new StaIconLoadQueue(
            _iconProvider.Object,
            _pathProbe.Object,
            _classifier.Object,
            _breaker.Object,
            workerFactory.Object,
            _diagnosticsLog.Object,
            IconLoadQueueOptions.Default,
            _time);

        var group = queue.CreateGroup(IconLoadPriority.Visible, new CollectingSynchronizationContext(), _ => { });
        _classifier.Setup(c => c.GetOwnRemoteRoot(@"\\srv\share\r.txt")).Returns(@"\\srv\share");
        group.Submit([new IconLoadRequest(@"\\srv\share\r.txt", 32)]);

        localWorker.RunAll(); // dispatch + classify + MoveJobToRemoteLane -> creates remoteWorker too

        queue.Dispose();

        Assert.Equal(["local:Abandon", "remote:Abandon", "local:Dispose", "remote:Dispose"], callOrder);
    }

    // ── Test seams / lifecycle ───────────────────────────────────────────────────────────────────────

    /// <summary><see cref="StaIconLoadQueue.PendingJobCount"/> reflects distinct jobs tracked, dedup included.</summary>
    [Fact]
    public void PendingJobCount_ReflectsDedupedDistinctJobs()
    {
        var queue = CreateQueue();
        var (groupA, _, _) = CreateGroup(queue);
        var (groupB, _, _) = CreateGroup(queue);

        groupA.Submit([new IconLoadRequest("a.txt", 32)]);
        groupB.Submit([new IconLoadRequest("a.txt", 32), new IconLoadRequest("b.txt", 32)]);

        Assert.Equal(2, queue.PendingJobCount);
    }

    /// <summary>Disposing the queue disposes both lanes' workers, and a repeat <see cref="IDisposable.Dispose"/> is a no-op.</summary>
    [Fact]
    public void Dispose_Queue_DisposesWorkersAndIsIdempotent()
    {
        var queue = CreateQueue();
        var (group, _, _) = CreateGroup(queue);
        group.Submit([new IconLoadRequest("a.txt", 32)]);

        var worker = _localWorkers.Single();

        queue.Dispose();
        var exception = Record.Exception(queue.Dispose);

        Assert.Null(exception);
        Assert.True(worker.IsDisposed);
    }

    /// <summary>After the queue is disposed, no group ever receives a result — even one already in flight.</summary>
    [Fact]
    public void Dispose_Queue_NoResultDeliveredAfterward()
    {
        var queue = CreateQueue();
        var (group, context, delivered) = CreateGroup(queue);
        group.Submit([new IconLoadRequest("a.txt", 32)]);

        var worker = _localWorkers.Single();
        queue.Dispose();
        worker.RunAll();
        context.Pump();

        Assert.Empty(delivered);
    }

    private static ImageSource CreateFrozenIcon()
    {
        var icon = new DrawingImage();
        icon.Freeze();
        return icon;
    }

    private static (StaIconLoadGroup Group, CollectingSynchronizationContext Context, List<IconLoadResult> Delivered) CreateGroup(
        StaIconLoadQueue queue, IconLoadPriority priority = IconLoadPriority.Visible)
    {
        var context = new CollectingSynchronizationContext();
        var delivered = new List<IconLoadResult>();
        var group = (StaIconLoadGroup)queue.CreateGroup(priority, context, results => delivered.AddRange(results));
        return (group, context, delivered);
    }

    private StaIconLoadQueue CreateQueue(IconLoadQueueOptions? options = null, IAppLog? appLog = null)
    {
        var workerFactory = new Mock<IStaWorkerFactory>();
        workerFactory
            .Setup(f => f.Create(It.IsAny<string>()))
            .Returns((string name) =>
            {
                var worker = new ManualStaWorker();
                (name.Contains("Local", StringComparison.Ordinal) ? _localWorkers : _remoteWorkers).Add(worker);
                return worker;
            });

        return new StaIconLoadQueue(
            _iconProvider.Object,
            _pathProbe.Object,
            _classifier.Object,
            _breaker.Object,
            workerFactory.Object,
            _diagnosticsLog.Object,
            options ?? IconLoadQueueOptions.Default,
            _time,
            appLog);
    }

    /// <summary>
    /// Fake <see cref="IStaWorker"/> for <see cref="PostDispatch_WorkerPumpDiedWithoutAbandon_RecreatesAndDisposesOldWorker"/>:
    /// its message pump is already dead — every <see cref="TryPost"/> returns <see langword="false"/> — but
    /// <see cref="IsAbandoned"/> stays <see langword="false"/> too, exactly like <c>StaWorkerThread</c>'s own
    /// dead-pump detection (a work item shut the dispatcher down, posted <c>WM_QUIT</c>, or the pump threw)
    /// reports it, per <see cref="IStaWorker.TryPost"/>'s contract.
    /// </summary>
    private sealed class DeadPumpStaWorker : IStaWorker
    {
        public int ManagedThreadId { get; } = Environment.CurrentManagedThreadId;

        public bool IsAbandoned => false;

        public bool Disposed { get; private set; }

        public bool TryPost(Action work) => false;

        public void Abandon()
        {
            // Never abandoned — the pump just died on its own, which is exactly the case this fake exists
            // to simulate.
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Fake <see cref="IStaWorker"/> for <see cref="Dispose_Queue_AbandonsBothWorkersBeforeDisposingEither"/>
    /// (L3): like <see cref="ManualStaWorker"/>, but also appends a tagged entry to a shared call-order log
    /// on every <see cref="Abandon"/>/<see cref="Dispose"/> call, so the test can assert the exact
    /// interleaving across BOTH lanes' workers rather than just each one's own state.
    /// </summary>
    private sealed class RecordingOrderStaWorker(string name, List<string> log) : IStaWorker
    {
        private readonly Queue<Action> _pending = new();

        public int ManagedThreadId { get; } = Environment.CurrentManagedThreadId;

        public bool IsAbandoned { get; private set; }

        public bool TryPost(Action work)
        {
            if (IsAbandoned) return false;
            _pending.Enqueue(work);
            return true;
        }

        public void Abandon()
        {
            IsAbandoned = true;
            log.Add($"{name}:Abandon");
        }

        public void Dispose() => log.Add($"{name}:Dispose");

        /// <summary>Runs every currently queued work item, including ones queued by an earlier item in this same call.</summary>
        public void RunAll()
        {
            while (_pending.Count > 0) _pending.Dequeue()();
        }
    }
}
