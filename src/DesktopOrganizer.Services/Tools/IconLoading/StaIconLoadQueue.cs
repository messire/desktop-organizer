using System.IO;
using System.Windows.Media;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Background icon-load queue (ADR-046, docs/PLANS/background-icon-loading.md §5.1). Two STA worker lanes
/// (Local/Remote, D-2) drain deduplicated <see cref="IconRealJob"/>s; a Local-only type-icon job queue
/// (D-5) always runs first and fans an <see cref="IconLoadOutcome.Interim"/> result out to every job of the
/// same extension. A watchdog (D-4) abandons a job stuck longer than
/// <see cref="IconLoadQueueOptions.StuckJobTimeout"/>, quarantines its path (Local) or reports it to the
/// <see cref="IRemoteRootCircuitBreaker"/> (Remote), and replaces the worker — up to
/// <see cref="IconLoadQueueOptions.MaxAbandonedWorkersPerLane"/> times before disabling the lane for the
/// rest of the session.
/// </summary>
/// <remarks>
/// One lock (<c>_gate</c>) protects every job/lane/rotation structure; it is taken only for bookkeeping —
/// never while a job is executing (<c>IIconProvider.GetIcon</c>/native calls run with no lock held) or
/// while delivering a result (<see cref="SynchronizationContext.Post"/> always happens outside the lock,
/// from <see cref="StaIconLoadGroup"/>'s own, separate buffer lock).
/// </remarks>
internal sealed class StaIconLoadQueue : IIconLoadQueue
{
    private const string DirectoryTypeKey = "<dir>";
    private const string AppLogSource = "StaIconLoadQueue";

    private readonly IIconProvider _iconProvider;
    private readonly IPathProbe _pathProbe;
    private readonly IRemotePathClassifier _classifier;
    private readonly IRemoteRootCircuitBreaker _breaker;
    private readonly IStaWorkerFactory _workerFactory;
    private readonly IDiagnosticsLog _diagnosticsLog;
    private readonly IAppLog? _appLog;

    /// <summary>
    /// Caps every Error event of this queue in the always-on app log at
    /// <see cref="AppLogEventThrottle.DefaultLimitPerEvent"/> entries per session (ADR-audit review cycle 1, G4):
    /// a sink or watchdog failure can repeat per delivery/pass, and each entry carries a stack trace.
    /// </summary>
    private readonly AppLogEventThrottle _appLogErrorThrottle = new ();
    private readonly IconLoadQueueOptions _options;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private readonly IconLoadLaneState _localLane = new(IconLoadLaneKind.Local, "DO.IconWorker.Local");
    private readonly IconLoadLaneState _remoteLane = new(IconLoadLaneKind.Remote, "DO.IconWorker.Remote");
    private readonly Dictionary<IconJobKey, IconRealJob> _realJobs = new(IconJobKeyComparer.Instance);
    private readonly LinkedList<IconRealJob> _insertionOrder = new();
    private readonly Dictionary<string, HashSet<IconRealJob>> _realJobsByTypeKey = [];
    private readonly HashSet<string> _pendingTypeJobKeys = [];
    private readonly Dictionary<string, DateTimeOffset> _pathQuarantineUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<StaIconLoadGroup> _liveGroups = [];
    private readonly ITimer _watchdogTimer;

    // M4: how many lanes currently have a unit (real job or type job) executing — the watchdog timer is
    // armed only while this is greater than zero (see DispatchNext/RunUnit/HandleStuckLane), so an idle
    // queue costs nothing at all, not even a periodic no-op scan. Guarded by _gate.
    private int _activeUnitCount;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaIconLoadQueue"/> class with production options
    /// (<see cref="IconLoadQueueOptions.Default"/>) and the real clock.
    /// </summary>
    /// <param name="iconProvider">Resolves each job's icon (and, for the Local lane's type jobs, the interim type icon).</param>
    /// <param name="pathProbe">One cheap existence/directory-ness probe per job (D-11).</param>
    /// <param name="classifier">Classifies a Local-lane job's path as local or remote (D-2), without any network I/O.</param>
    /// <param name="breaker">Shared circuit breaker for the Remote lane (D-3).</param>
    /// <param name="workerFactory">Creates each lane's STA worker, and its replacement after a watchdog abandon.</param>
    /// <param name="diagnosticsLog">Structured diagnostics sink for the <c>icon</c> category's queue events.</param>
    /// <param name="appLog">
    /// Always-on application log (ADR-033): receives the operational Warn/Error events (lane disabled, root
    /// quarantined, non-frozen icon, sink exception, watchdog abandon) in addition to <paramref name="diagnosticsLog"/>,
    /// which is a no-op without <c>--diag</c>. Optional — <see langword="null"/> keeps diagnostics-only behavior.
    /// </param>
    public StaIconLoadQueue(
        IIconProvider iconProvider,
        IPathProbe pathProbe,
        IRemotePathClassifier classifier,
        IRemoteRootCircuitBreaker breaker,
        IStaWorkerFactory workerFactory,
        IDiagnosticsLog diagnosticsLog,
        IAppLog? appLog = null)
        : this(iconProvider, pathProbe, classifier, breaker, workerFactory, diagnosticsLog, IconLoadQueueOptions.Default, TimeProvider.System, appLog)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StaIconLoadQueue"/> class with explicit options and
    /// clock. Internal — lets tests shrink the numbers in <see cref="IconLoadQueueOptions"/> and drive time
    /// deterministically (<c>docs/Refactoring/Testing/sta-test-conventions.md</c>'s "Wall-clock тесты" rule).
    /// </summary>
    /// <param name="iconProvider">Resolves each job's icon and interim type icon.</param>
    /// <param name="pathProbe">One cheap existence/directory-ness probe per job (D-11).</param>
    /// <param name="classifier">Classifies a Local-lane job's path as local or remote (D-2).</param>
    /// <param name="breaker">Shared circuit breaker for the Remote lane (D-3).</param>
    /// <param name="workerFactory">Creates each lane's STA worker (typically a test fake here).</param>
    /// <param name="diagnosticsLog">Structured diagnostics sink for the <c>icon</c> category.</param>
    /// <param name="options">Tunable constants.</param>
    /// <param name="timeProvider">Clock used for every timing decision (job start times, quarantines, delivery flush timers).</param>
    /// <param name="appLog">Always-on application log for the operational Warn/Error events; <see langword="null"/> disables it.</param>
    internal StaIconLoadQueue(
        IIconProvider iconProvider,
        IPathProbe pathProbe,
        IRemotePathClassifier classifier,
        IRemoteRootCircuitBreaker breaker,
        IStaWorkerFactory workerFactory,
        IDiagnosticsLog diagnosticsLog,
        IconLoadQueueOptions options,
        TimeProvider timeProvider,
        IAppLog? appLog = null)
    {
        _iconProvider = iconProvider;
        _pathProbe = pathProbe;
        _classifier = classifier;
        _breaker = breaker;
        _workerFactory = workerFactory;
        _diagnosticsLog = diagnosticsLog;
        _appLog = appLog;
        _options = options;
        _timeProvider = timeProvider;

        // M3/M4: the timer itself starts disarmed (Infinite/Infinite) — DispatchNext arms it
        // (WatchdogPeriod/WatchdogPeriod) the moment the first lane starts executing a unit (real job OR
        // type job, M1) and disarms it again the moment the last one finishes (RunUnit/HandleStuckLane's
        // DispatchNext's own failure path), tracked by _activeUnitCount. An idle queue between panels'
        // requests therefore costs nothing at all, not even a periodic no-op scan. RunWatchdogPassSafe
        // wraps the scan in its own try/catch (C-5/M3): an exception escaping a ThreadPool timer callback
        // would otherwise crash the whole process.
        _watchdogTimer = _timeProvider.CreateTimer(_ => RunWatchdogPassSafe(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets the number of distinct <c>(path, size)</c> jobs currently tracked (pending or executing). Test seam.</summary>
    internal int PendingJobCount
    {
        get
        {
            lock (_gate) return _realJobs.Count;
        }
    }

    /// <summary>Gets a value indicating whether either lane has ever created its worker yet (test seam — <c>DiConfigTests</c>' "resolving the queue starts no thread" check, D-14/M9).</summary>
    internal bool HasCreatedAnyWorker
    {
        get
        {
            lock (_gate) return _localLane.Worker is not null || _remoteLane.Worker is not null;
        }
    }

    /// <summary>Gets how many groups are currently tracked as live (test seam — H3's "a disposed group is removed, not leaked" check).</summary>
    internal int LiveGroupCount
    {
        get
        {
            lock (_gate) return _liveGroups.Count;
        }
    }

    private IEnumerable<IconLoadLaneState> Lanes
    {
        get
        {
            yield return _localLane;
            yield return _remoteLane;
        }
    }

    /// <inheritdoc />
    public IIconLoadGroup CreateGroup(
        IconLoadPriority priority, SynchronizationContext deliveryContext, Action<IReadOnlyList<IconLoadResult>> onResults)
    {
        ArgumentNullException.ThrowIfNull(deliveryContext);
        ArgumentNullException.ThrowIfNull(onResults);

        var group = new StaIconLoadGroup(this, priority, deliveryContext, onResults);
        lock (_gate) _liveGroups.Add(group);
        return group;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<StaIconLoadGroup> groups;
        IStaWorker? localWorker;
        IStaWorker? remoteWorker;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            groups = [.. _liveGroups];
            localWorker = _localLane.Worker;
            remoteWorker = _remoteLane.Worker;
        }

        _watchdogTimer.Dispose();

        // D-14: cancelling every live group bumps its generation, so any result already buffered/in-flight
        // for the old generation is silently dropped by StaIconLoadGroup's own delivery-time check — this
        // is what makes "no onResults after Dispose" true without needing Submit/BufferResult themselves to
        // consult the QUEUE's disposed flag on every call. L2: SuppressPendingDelivery additionally
        // disarms each group's own flush timer and drops its buffer — without it, a result already
        // buffered (below DeliveryBatchSize, flush timer not yet due) would still eventually fire that
        // timer and call SynchronizationContext.Post even after the queue itself is gone; the generation
        // bump above only makes that late post harmless at DELIVERY time, not prevent it from happening at
        // all (D-14's "not even one Post after Dispose").
        foreach (var group in groups)
        {
            group.CancelPending();
            group.SuppressPendingDelivery();
        }

        // L3: request BOTH workers to stop before waiting on either — Abandon() never blocks, so this
        // issues both shutdown requests back-to-back instead of serializing "request + wait up to ~1s" for
        // the Local lane before even telling the Remote lane's worker to stop. By the time the Dispose
        // calls below start their own bounded Join, both threads already had the same head start winding
        // their message pump down, so the two joins overlap instead of stacking to roughly 2x the
        // per-worker budget.
        localWorker?.Abandon();
        remoteWorker?.Abandon();

        // IStaWorker.Dispose is documented to InvokeShutdown + bounded Join and never throw; abandoned
        // workers from a disabled lane are intentionally not tracked/waited on here (D-4: "брошенные потоки
        // не ждём").
        localWorker?.Dispose();
        remoteWorker?.Dispose();
    }

    /// <summary>
    /// Runs one watchdog scan: any lane whose current unit — a real job OR (M1) a Local-lane type-icon job
    /// — has been executing at least <see cref="IconLoadQueueOptions.StuckJobTimeout"/> is handled (D-4).
    /// Called by the internal timer (via <see cref="RunWatchdogPassSafe"/>); also a test seam so tests can
    /// drive it deterministically via a fake <see cref="TimeProvider"/>.
    /// </summary>
    internal void RunWatchdogPass()
    {
        List<IconLoadLaneState>? stuckLanes = null;

        lock (_gate)
        {
            if (_disposed) return;

            var now = _timeProvider.GetUtcNow();
            foreach (var lane in Lanes)
            {
                if (lane.Disabled || lane.CurrentUnitStartedUtc is not { } startedAt)
                {
                    continue;
                }

                if (now - startedAt >= _options.StuckJobTimeout)
                {
                    (stuckLanes ??= []).Add(lane);
                }
            }
        }

        if (stuckLanes is null) return;
        foreach (var lane in stuckLanes) HandleStuckLane(lane);
    }

    /// <summary>Submits <paramref name="requests"/> on behalf of <paramref name="group"/> (called by <see cref="StaIconLoadGroup.Submit"/>).</summary>
    /// <param name="group">The submitting group.</param>
    /// <param name="requests">The requests to submit.</param>
    internal void SubmitInternal(StaIconLoadGroup group, IReadOnlyList<IconLoadRequest> requests)
    {
        if (requests.Count == 0) return;

        // M-3 (review it.2): a disposed group's own waiters can never be live again (IconJobWaiter.IsLive
        // already checks !Group.IsDisposed), so any job created for it here would just sit dead until a
        // dequeue lazily drops it (D-8) — skip the bookkeeping entirely instead of paying for it on every
        // stray Submit that races a group's own Dispose.
        if (group.IsDisposed) return;

        var startLocalLane = false;
        List<IconRealJob>? disabledLaneOrphans = null;
        List<(StaIconLoadGroup Group, IconLoadResult Result)>? evictedBuffers = null;

        lock (_gate)
        {
            if (_disposed) return;

            var generation = group.Generation;
            foreach (var request in requests)
            {
                var size = IconPixelSize.Snap(request.PixelSize);
                var key = new IconJobKey(request.Path, size);

                if (!_realJobs.TryGetValue(key, out var job))
                {
                    if (_realJobs.Count >= _options.MaxPendingJobs)
                    {
                        var evicted = EvictOldestBackgroundJobLocked();
                        if (evicted is not null) (evictedBuffers ??= []).AddRange(evicted);
                    }

                    job = new IconRealJob(request.Path, size);
                    _realJobs[key] = job;
                    job.InsertionNode = _insertionOrder.AddLast(job);

                    // Every new job starts on the Local lane (only execution there can classify it as
                    // remote) — if that lane already disabled itself (D-4), it can never be dispatched, so
                    // resolve it Unavailable right away instead of leaking it forever.
                    if (_localLane.Disabled)
                    {
                        (disabledLaneOrphans ??= []).Add(job);
                    }
                    else
                    {
                        EnqueueRealJobLocked(_localLane, job, group, group.Priority);
                        RegisterTypeInterestLocked(job);
                        startLocalLane = true;
                    }
                }
                else
                {
                    // M7: a Visible-priority waiter joining a job someone else already queued at Background
                    // priority must not wait behind that owner group's other Background work — promote the
                    // still-pending job itself into its owner group's Visible bucket so the very next
                    // dequeue on that lane considers it. A no-op for a job that is already Executing (moved
                    // out of every bucket) or already Visible.
                    PromoteJobToVisibleLocked(job, group.Priority);
                }

                job.Waiters.Add(new IconJobWaiter(group, generation));
            }
        }

        // M5: BufferResult may synchronously flush and call SynchronizationContext.Post — never while _gate
        // is held (a caller's re-entrant onResults could otherwise call back into Submit on the very same
        // thread while this method's own lock is still open further up the stack).
        if (evictedBuffers is not null)
        {
            foreach (var (evictedGroup, result) in evictedBuffers)
            {
                evictedGroup.BufferResult(result, _options.DeliveryBatchSize, _options.DeliveryFlushInterval, _timeProvider);
            }
        }

        if (disabledLaneOrphans is not null)
        {
            foreach (var job in disabledLaneOrphans)
            {
                TerminateJob(job, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
            }
        }

        if (startLocalLane) EnsureLaneRunning(_localLane);
    }

    /// <summary>Relocates every pending job <paramref name="group"/> owns from <paramref name="oldPriority"/> to <paramref name="newPriority"/>, in both lanes (D-6).</summary>
    /// <param name="group">The group whose priority changed.</param>
    /// <param name="oldPriority">The group's previous priority.</param>
    /// <param name="newPriority">The group's new priority.</param>
    internal void RelocateGroupPriority(StaIconLoadGroup group, IconLoadPriority oldPriority, IconLoadPriority newPriority)
    {
        lock (_gate)
        {
            foreach (var lane in Lanes)
            {
                var oldBucket = group.GetBucket(lane.Kind, oldPriority);
                if (oldBucket.Count == 0) continue;

                var newBucket = group.GetBucket(lane.Kind, newPriority);
                var wasNewEmpty = newBucket.Count == 0;

                var moving = new List<IconRealJob>(oldBucket.Count);
                for (var node = oldBucket.First; node is not null; node = node.Next) moving.Add(node.Value);
                oldBucket.Clear();

                foreach (var job in moving) job.BucketNode = newBucket.AddLast(job);

                GetRing(lane, oldPriority).Remove(group);
                if (wasNewEmpty) GetRing(lane, newPriority).EnsureMember(group);
            }
        }
    }

    /// <summary>Reports an exception <see cref="StaIconLoadGroup"/>'s <c>onResults</c> callback threw (D-9).</summary>
    /// <param name="ex">The exception the sink threw.</param>
    internal void LogSinkThrew(Exception ex)
        => LogError("IconQueueSinkThrew", ex, new KeyValuePair<string, object?>("errorType", ex.GetType().Name));

    /// <summary>
    /// Removes <paramref name="group"/> from <see cref="_liveGroups"/> once it disposes (H3) — without this,
    /// a disposed group stays reachable (and referenced) for the rest of the queue's life, and every
    /// <see cref="FlushAllGroupBuffers"/>/<see cref="Dispose"/> sweep keeps pointlessly visiting it.
    /// </summary>
    /// <param name="group">The group that just disposed.</param>
    internal void RemoveGroup(StaIconLoadGroup group)
    {
        lock (_gate) _liveGroups.Remove(group);
    }

    private static IconLoadRotationRing GetRing(IconLoadLaneState lane, IconLoadPriority priority)
        => priority == IconLoadPriority.Visible ? lane.VisibleRing : lane.BackgroundRing;

    private static string LaneName(IconLoadLaneKind kind) => kind == IconLoadLaneKind.Local ? "local" : "remote";

    private static bool IsShortcut(string path) => string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);

    private static bool GuessIsDirectory(string path) => string.IsNullOrEmpty(Path.GetExtension(path));

    private static string SafeLowerExtension(string path) => Path.GetExtension(path).ToLowerInvariant();

    private static string ClassifyRootKind(string root) => root.StartsWith(@"\\", StringComparison.Ordinal) ? "unc" : "mappedDrive";

    private static void EnqueueRealJobLocked(IconLoadLaneState lane, IconRealJob job, StaIconLoadGroup ownerGroup, IconLoadPriority priority)
    {
        job.Lane = lane.Kind;
        job.OwnerGroup = ownerGroup;

        var bucket = ownerGroup.GetBucket(lane.Kind, priority);
        job.BucketNode = bucket.AddLast(job);

        if (bucket.Count == 1) GetRing(lane, priority).EnsureMember(ownerGroup);
    }

    private static void DrainRingLocked(IconLoadLaneState lane, IconLoadRotationRing ring, IconLoadPriority priority, List<IconRealJob> collected)
    {
        while (ring.DequeueRotate() is { } group)
        {
            var bucket = group.GetBucket(lane.Kind, priority);
            while (bucket.First is { } node)
            {
                bucket.RemoveFirst();
                node.Value.BucketNode = null;
                collected.Add(node.Value);
            }
        }
    }

    private static bool HasLiveVisibleWaiter(IconRealJob job)
        => job.Waiters.Exists(static w => w.IsLive && w.Group.Priority == IconLoadPriority.Visible);

    /// <summary>
    /// M7: promotes <paramref name="job"/> from its owner group's Background bucket into that same owner
    /// group's Visible bucket, when <paramref name="incomingPriority"/> is <see cref="IconLoadPriority.Visible"/>
    /// and the job is still pending (not yet dequeued/executing) — so a Visible-priority waiter that joins a
    /// job someone else already queued at Background priority does not wait behind that owner's other
    /// Background work. A no-op when the job is already executing (nothing pending left to move), was never
    /// enqueued at all (e.g. a disabled-lane orphan, <see cref="IconRealJob.OwnerGroup"/> still
    /// <see langword="null"/>), or its owner is already at Visible priority.
    /// </summary>
    /// <param name="job">The (possibly shared) job a new waiter just joined.</param>
    /// <param name="incomingPriority">The joining group's current priority.</param>
    private void PromoteJobToVisibleLocked(IconRealJob job, IconLoadPriority incomingPriority)
    {
        if (incomingPriority != IconLoadPriority.Visible) return;
        if (job.BucketNode is not { } node) return;
        if (job.OwnerGroup is not { } owner || owner.Priority != IconLoadPriority.Background) return;

        var lane = job.Lane == IconLoadLaneKind.Local ? _localLane : _remoteLane;
        var oldBucket = owner.GetBucket(lane.Kind, IconLoadPriority.Background);

        oldBucket.Remove(node);
        job.BucketNode = null;
        if (oldBucket.Count == 0) GetRing(lane, IconLoadPriority.Background).Remove(owner);

        EnqueueRealJobLocked(lane, job, owner, IconLoadPriority.Visible);
    }

    /// <summary>
    /// The internal timer's actual callback (M3/C-5): an exception escaping a ThreadPool timer callback
    /// would crash the whole process, so <see cref="RunWatchdogPass"/> itself is never wired to the timer
    /// directly.
    /// </summary>
    private void RunWatchdogPassSafe()
    {
        try
        {
            RunWatchdogPass();
        }
        catch (Exception ex)
        {
            LogWatchdogPassFailedSafe(ex);
        }
    }

    /// <summary>Best-effort diagnostic for <see cref="RunWatchdogPassSafe"/> — logging itself must never be what crashes a background timer thread.</summary>
    /// <param name="ex">The exception <see cref="RunWatchdogPass"/> threw.</param>
    private void LogWatchdogPassFailedSafe(Exception ex)
    {
        try
        {
            LogError("IconQueueWatchdogPassFailed", ex, new KeyValuePair<string, object?>("errorType", ex.GetType().Name));
        }
        catch (Exception)
        {
            // Best effort: see the remarks above.
        }
    }

    private void RegisterTypeInterestLocked(IconRealJob job)
    {
        var isDirectoryGuess = GuessIsDirectory(job.Path);
        if (_iconProvider.TryPeekTypeIcon(job.Path, isDirectoryGuess) is not null) return;

        var typeKey = isDirectoryGuess ? DirectoryTypeKey : SafeLowerExtension(job.Path);
        job.TypeKey = typeKey;

        if (!_realJobsByTypeKey.TryGetValue(typeKey, out var interested))
        {
            interested = [];
            _realJobsByTypeKey[typeKey] = interested;
        }

        interested.Add(job);

        if (_pendingTypeJobKeys.Add(typeKey))
        {
            _localLane.TypeJobs.Enqueue(new IconTypeJob(typeKey, isDirectoryGuess, job.Path));
        }
    }

    /// <summary>
    /// Evicts one Background-owned, non-executing job to make room under <see cref="IconLoadQueueOptions.MaxPendingJobs"/>
    /// (D-10), returning the (group, Dropped-result) pairs to buffer — the caller delivers them AFTER
    /// releasing <see cref="_gate"/> (M5: <see cref="StaIconLoadGroup.BufferResult"/> may synchronously flush
    /// and <c>Post</c> to the delivery <see cref="SynchronizationContext"/>). M7: a job with a live
    /// Visible-priority waiter is never evicted (regardless of which group originally queued it — a Visible
    /// request that later joined a pending Background job, <see cref="PromoteJobToVisibleLocked"/>, must not
    /// then get sacrificed to back-pressure), and a job nobody is even waiting for any more (every waiter
    /// already detached) is always preferred over one a live Background waiter still wants.
    /// </summary>
    private List<(StaIconLoadGroup Group, IconLoadResult Result)>? EvictOldestBackgroundJobLocked()
    {
        var evicted = FindEvictionCandidateLocked(requireNoLiveWaiters: true)
                      ?? FindEvictionCandidateLocked(requireNoLiveWaiters: false);
        if (evicted is null) return null;

        List<(StaIconLoadGroup, IconLoadResult)>? toBuffer = null;
        foreach (var waiter in evicted.Waiters)
        {
            if (!waiter.IsLive) continue;
            (toBuffer ??= []).Add((
                waiter.Group,
                new IconLoadResult(evicted.Path, evicted.Size, waiter.Generation, IconLoadOutcome.Dropped, null, null, null)));
        }

        RemoveJobEverywhereLocked(evicted);
        LogDiagnostic("IconQueueBackpressureDropped", new KeyValuePair<string, object?>("count", 1));
        return toBuffer;
    }

    /// <summary>Oldest non-executing, Background-owned, no-live-Visible-waiter job — see <see cref="EvictOldestBackgroundJobLocked"/>.</summary>
    /// <param name="requireNoLiveWaiters">When <see langword="true"/>, only considers a job every waiter has already detached from.</param>
    private IconRealJob? FindEvictionCandidateLocked(bool requireNoLiveWaiters)
    {
        for (var node = _insertionOrder.First; node is not null; node = node.Next)
        {
            var candidate = node.Value;
            if (candidate.Executing) continue;
            if (candidate.OwnerGroup is not { } owner || owner.Priority != IconLoadPriority.Background) continue;
            if (HasLiveVisibleWaiter(candidate)) continue;
            if (requireNoLiveWaiters && candidate.HasLiveWaiters) continue;

            return candidate;
        }

        // No evictable job found — allow the new job to exceed the cap by one rather than silently
        // refusing to track a Visible-owned request the user is looking at.
        return null;
    }

    private void RemoveJobEverywhereLocked(IconRealJob job)
    {
        _realJobs.Remove(new IconJobKey(job.Path, job.Size));

        if (job.InsertionNode is { } insertionNode)
        {
            _insertionOrder.Remove(insertionNode);
            job.InsertionNode = null;
        }

        // Harmless if the job was already dequeued (BucketNode already null) or already detached — a
        // LinkedListNode<T> knows its own owning list, so this works regardless of which lane/priority
        // bucket the job currently sits in. If this leaves a group's bucket empty without going through the
        // normal dequeue path, the rotation ring may keep a stale membership entry for it; the next dequeue
        // attempt on that ring lazily drops it (empty bucket -> "continue", same as a no-live-waiters job).
        if (job.BucketNode is { } bucketNode)
        {
            bucketNode.List?.Remove(bucketNode);
            job.BucketNode = null;
        }

        if (job.TypeKey is { } typeKey && _realJobsByTypeKey.TryGetValue(typeKey, out var interested))
        {
            interested.Remove(job);
            if (interested.Count == 0) _realJobsByTypeKey.Remove(typeKey);
        }
    }

    private void EnsureLaneRunning(IconLoadLaneState lane)
    {
        IStaWorker worker;

        lock (_gate)
        {
            if (_disposed || lane.Disabled || lane.WorkerRunning) return;

            lane.Worker ??= _workerFactory.Create(lane.NextWorkerName());
            lane.WorkerRunning = true;
            lane.DrainStartedUtc = _timeProvider.GetUtcNow();
            lane.JobsProcessedThisDrain = 0;
            worker = lane.Worker;
        }

        LogDiagnostic("IconQueueStarted", new KeyValuePair<string, object?>("lane", LaneName(lane.Kind)));
        PostDispatch(lane, worker);
    }

    /// <summary>
    /// Posts the "dequeue-and-mark-current" step (<see cref="DispatchNext"/>) to the worker — deliberately a
    /// SEPARATE posted step from the actual icon-fetch/native work (<see cref="RunUnit"/>), so a fake
    /// <c>IStaWorker</c> in a test can run the dequeue step alone (establishing
    /// <see cref="IconLoadLaneState.CurrentJob"/>/<see cref="IconRealJob.StartedAtUtc"/>, the watchdog's
    /// target) and then simulate "stuck in the native call" simply by never running the second step at all.
    /// Both steps together still only ever dequeue when the worker actually has a turn to give — an idle
    /// lane's first job is not decided until then, so a job submitted a moment later at a higher priority
    /// still gets first pick, exactly like every later dequeue decision (ADR-046 D-6).
    /// </summary>
    private void PostDispatch(IconLoadLaneState lane, IStaWorker worker)
    {
        if (worker.TryPost(() => DispatchNext(lane, worker))) return;

        // The worker was abandoned (watchdog) or is shutting down (Dispose) between EnsureLaneRunning/RunUnit
        // resolving it and this post — or (the reason HandlePostFailed itself exists) its pump silently died
        // on its own without ever being told to stop. Either way, see HandlePostFailed's remarks.
        HandlePostFailed(lane, worker);
    }

    /// <summary>Dequeues the next unit of work for <paramref name="lane"/>, marks it as current, and posts the actual work (<see cref="RunUnit"/>). See <see cref="PostDispatch"/>'s remarks.</summary>
    private void DispatchNext(IconLoadLaneState lane, IStaWorker worker)
    {
        object? unit;
        var armWatchdog = false;

        lock (_gate)
        {
            // A worker the watchdog already replaced (see HandleStuckLane) must not drive the lane any more.
            if (!ReferenceEquals(lane.Worker, worker)) return;

            if (_disposed || lane.Disabled)
            {
                lane.WorkerRunning = false;
                return;
            }

            unit = DequeueNextLocked(lane);
            if (unit is null)
            {
                lane.WorkerRunning = false;
                return;
            }

            // M1: the watchdog now covers a Local-lane type-icon job's GetTypeIcon call too, not just real
            // jobs — CurrentUnitStartedUtc is the single timestamp RunWatchdogPass checks for either kind.
            var startedAt = _timeProvider.GetUtcNow();
            lane.CurrentUnitStartedUtc = startedAt;

            if (unit is IconRealJob realJob)
            {
                realJob.Executing = true;
                realJob.StartedAtUtc = startedAt;
                lane.CurrentJob = realJob;
            }
            else if (unit is IconTypeJob typeJob)
            {
                lane.CurrentTypeJob = typeJob;
            }

            // M4: the watchdog timer is armed only while at least one lane actually has a unit executing.
            armWatchdog = ++_activeUnitCount == 1;
        }

        if (armWatchdog) _watchdogTimer.Change(_options.WatchdogPeriod, _options.WatchdogPeriod);

        if (worker.TryPost(() => RunUnit(lane, worker, unit))) return;

        // The worker was abandoned/shut down between the dequeue above and this post — already dequeued, so
        // it must not be silently orphaned: report it Unavailable like any other watchdog-abandoned job (a
        // type job instead simply loses its registration — the real jobs waiting on it fall through to
        // their own Loaded/Unavailable/Failed result later, same as ExecuteTypeJob's own exception path).
        var disarmWatchdog = false;
        lock (_gate)
        {
            if (unit is IconRealJob job0 && job0.Lane == lane.Kind) job0.Executing = false;
            lane.CurrentJob = null;
            lane.CurrentTypeJob = null;
            lane.CurrentUnitStartedUtc = null;

            if (unit is IconTypeJob typeJob0)
            {
                _pendingTypeJobKeys.Remove(typeJob0.TypeKey);
                _realJobsByTypeKey.Remove(typeJob0.TypeKey);
            }

            disarmWatchdog = --_activeUnitCount == 0;
        }

        if (disarmWatchdog) _watchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (unit is IconRealJob abandonedJob)
        {
            TerminateJob(abandonedJob, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
        }

        HandlePostFailed(lane, worker);
    }

    /// <summary>
    /// Common reaction to a failed <see cref="IStaWorker.TryPost"/> from <see cref="PostDispatch"/> or
    /// <see cref="DispatchNext"/>'s own post of <see cref="RunUnit"/>. Two distinct cases (see
    /// <see cref="IStaWorker.TryPost"/>'s own remarks): (1) the worker was deliberately
    /// <see cref="IStaWorker.Abandon"/>ed (watchdog) or is being disposed — whoever did that already owns
    /// recovering/tearing down the lane, so this just clears <see cref="IconLoadLaneState.WorkerRunning"/>;
    /// (2) its message pump died on its own (<see cref="IStaWorker.IsAbandoned"/> still
    /// <see langword="false"/> — a work item shut the dispatcher down, posted <c>WM_QUIT</c>, or the pump
    /// itself threw) — in that case, simply marking the lane idle would leave it stuck forever (nothing else
    /// would ever call <see cref="EnsureLaneRunning"/> again for a lane that still has a — now-dead —
    /// <c>Worker</c> reference), so the dead worker is disposed (its own contract: the caller must dispose a
    /// worker whose <c>TryPost</c> returned <see langword="false"/> without <c>IsAbandoned</c>) and replaced,
    /// and — if the lane still has pending work — a fresh drain is kicked off immediately.
    /// </summary>
    private void HandlePostFailed(IconLoadLaneState lane, IStaWorker worker)
    {
        var recreate = false;

        lock (_gate)
        {
            if (!ReferenceEquals(lane.Worker, worker)) return; // Already superseded by a watchdog pass or Dispose.

            if (worker.IsAbandoned)
            {
                lane.WorkerRunning = false;
                return;
            }

            lane.Worker = null;
            lane.WorkerRunning = false;
            recreate = !_disposed && !lane.Disabled;
        }

        // IStaWorker.TryPost's own contract: a false without IsAbandoned means the pump is dead for good and
        // the caller owns disposing it — safe to call unconditionally even if Dispose already partly ran on
        // its own (IStaWorker.Dispose is documented to never throw and tolerate repeat/self calls).
        worker.Dispose();

        if (recreate) EnsureLaneRunning(lane);
    }

    /// <summary>Runs exactly one already-dequeued unit of work on the worker's own thread, then dispatches the next one if more work remains.</summary>
    private void RunUnit(IconLoadLaneState lane, IStaWorker worker, object? unit)
    {
        var staleWorker = false;
        try
        {
            switch (unit)
            {
                case IconTypeJob typeJob:
                    ExecuteTypeJob(typeJob);
                    break;
                case IconRealJob realJob:
                    ExecuteRealJob(lane, realJob);
                    break;
            }
        }
        catch (Exception)
        {
            // C-5/§5.1.8: any exception from a job degrades that job to Failed; the worker itself must
            // stay alive for the next one. TerminateJob is safe to call again even if the job already
            // reached a terminal state on some paths above, because RemoveJobEverywhereLocked/Waiters are
            // idempotent no-ops for a job no longer in _realJobs.
            if (unit is IconRealJob failedJob) TerminateJob(failedJob, IconLoadOutcome.Failed, icon: null, exists: null, isDirectory: null);
        }
        finally
        {
            var disarmWatchdog = false;
            lock (_gate)
            {
                if (!ReferenceEquals(lane.Worker, worker))
                {
                    // D-4: the watchdog abandoned this worker while the call was stuck, and it has only now
                    // come back. The job was already resolved Unavailable (TerminateJob ignores the late
                    // result — or, for a type job, its registration was already dropped), and the lane's
                    // state belongs to the replacement worker — touch nothing but the abandoned-worker
                    // count: this thread is about to exit, so it no longer counts against
                    // MaxAbandonedWorkersPerLane. A lane that already disabled itself stays disabled.
                    // HandleStuckLane already decremented _activeUnitCount for this unit when it abandoned
                    // it, so this stale-worker branch must not decrement it a second time.
                    staleWorker = true;
                    if (lane.AbandonedCount > 0) lane.AbandonedCount--;
                }
                else
                {
                    // The Lane guard matters for a job ExecuteRealJob just moved to the OTHER lane
                    // (MoveJobToRemoteLane already reset Executing/StartedAtUtc and re-enqueued it there as
                    // a pending job, M6) — this lane's cleanup must not clobber whatever the Remote lane
                    // does with it next (it may already be Executing again by the time this Local-lane
                    // cleanup runs, if the Remote lane's worker was faster).
                    if (unit is IconRealJob doneJob && doneJob.Lane == lane.Kind) doneJob.Executing = false;
                    if (unit is IconRealJob) lane.JobsProcessedThisDrain++;
                    lane.CurrentJob = null;
                    lane.CurrentTypeJob = null;
                    lane.CurrentUnitStartedUtc = null;
                    disarmWatchdog = --_activeUnitCount == 0;
                }
            }

            if (disarmWatchdog) _watchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (staleWorker) return;

        bool hasMore;
        lock (_gate)
        {
            if (_disposed || lane.Disabled)
            {
                lane.WorkerRunning = false;
                return;
            }

            hasMore = lane.TypeJobs.Count > 0 || !lane.VisibleRing.IsEmpty || !lane.BackgroundRing.IsEmpty;

            // H1: resetting WorkerRunning must happen in the SAME critical section that decided there is no
            // more work — otherwise a Submit landing between "hasMore == false" being computed and
            // WorkerRunning actually flipping to false would see WorkerRunning still true, skip
            // EnsureLaneRunning, and leave its new job stuck forever with nothing left to ever dispatch it
            // (a lost wakeup). Guarded the same way the old, separate MarkLaneIdleIfCurrentWorker call was:
            // only touch WorkerRunning if this is still the lane's current worker.
            if (!hasMore && ReferenceEquals(lane.Worker, worker)) lane.WorkerRunning = false;
        }

        if (hasMore)
        {
            PostDispatch(lane, worker);
            return;
        }

        LogDiagnostic(
            "IconQueueDrained",
            new KeyValuePair<string, object?>("lane", LaneName(lane.Kind)),
            new KeyValuePair<string, object?>("jobs", lane.JobsProcessedThisDrain),
            new KeyValuePair<string, object?>("elapsedMs", (long)(_timeProvider.GetUtcNow() - lane.DrainStartedUtc).TotalMilliseconds));

        // D-9's third flush trigger: the lane emptied out, so nothing else is coming for a while — flush
        // every group's buffer now instead of waiting out its own flush-interval timer.
        FlushAllGroupBuffers();
    }

    private object? DequeueNextLocked(IconLoadLaneState lane)
    {
        if (lane.Kind == IconLoadLaneKind.Local && lane.TypeJobs.Count > 0)
        {
            return lane.TypeJobs.Dequeue();
        }

        return DequeueRealJobLocked(lane, lane.VisibleRing, IconLoadPriority.Visible)
               ?? DequeueRealJobLocked(lane, lane.BackgroundRing, IconLoadPriority.Background);
    }

    private IconRealJob? DequeueRealJobLocked(IconLoadLaneState lane, IconLoadRotationRing ring, IconLoadPriority priority)
    {
        while (ring.DequeueRotate() is { } group)
        {
            var bucket = group.GetBucket(lane.Kind, priority);
            if (bucket.First is not { } node) continue; // stale rotation membership; drop it.

            bucket.RemoveFirst();
            var job = node.Value;
            job.BucketNode = null;

            if (bucket.Count > 0) ring.EnsureMember(group);

            if (!job.HasLiveWaiters)
            {
                // D-8: a job every waiter has detached from (CancelPending/Dispose) is dropped without
                // ever calling GetIcon for it.
                RemoveJobEverywhereLocked(job);
                continue;
            }

            return job;
        }

        return null;
    }

    private void ExecuteTypeJob(IconTypeJob typeJob)
    {
        ImageSource? icon;
        try
        {
            icon = _iconProvider.GetTypeIcon(typeJob.RepresentativePath, typeJob.IsDirectory);

            // M-5 (review it.2, C-4): a type icon crosses the same worker-thread → UI-thread boundary as a
            // Loaded icon (D-9's Post) and must be Frozen for the same reason — this runtime check was
            // previously only done in ExecuteIconFetch, leaving the type-icon Interim fast path unchecked.
            // A type job has no single "job" to report Failed against (many real jobs share it), so an
            // unfrozen icon here is treated exactly like GetTypeIcon throwing below: no Interim is sent,
            // and every waiting real job simply falls through to its own Loaded/Unavailable/Failed result.
            if (icon is not null && !icon.IsFrozen)
            {
                LogError("IconResultNotFrozen", null, new KeyValuePair<string, object?>("stage", "typeIcon"));
                icon = null;
            }
        }
        catch (Exception)
        {
            // GetTypeIcon's own contract says it never throws (falls back to the placeholder), but a type
            // job has no single "job" to report Failed against — the real jobs waiting on it simply don't
            // get an Interim and fall through to their own Loaded/Unavailable/Failed result later.
            icon = null;
        }

        List<(StaIconLoadGroup Group, IconLoadResult Result)>? toBuffer = null;

        lock (_gate)
        {
            _pendingTypeJobKeys.Remove(typeJob.TypeKey);

            if (icon is not null && _realJobsByTypeKey.TryGetValue(typeJob.TypeKey, out var interested))
            {
                foreach (var job in interested)
                {
                    foreach (var waiter in job.Waiters)
                    {
                        if (!waiter.IsLive) continue;
                        (toBuffer ??= []).Add((
                            waiter.Group,
                            new IconLoadResult(job.Path, job.Size, waiter.Generation, IconLoadOutcome.Interim, icon, null, null)));
                    }
                }

                _realJobsByTypeKey.Remove(typeJob.TypeKey);
            }
        }

        // M5: BufferResult may synchronously flush and Post to the delivery SynchronizationContext — never
        // while _gate is held.
        if (toBuffer is null) return;
        foreach (var (group, result) in toBuffer)
        {
            group.BufferResult(result, _options.DeliveryBatchSize, _options.DeliveryFlushInterval, _timeProvider);
        }
    }

    private void ExecuteRealJob(IconLoadLaneState lane, IconRealJob job)
    {
        if (lane.Kind == IconLoadLaneKind.Local)
        {
            if (IsPathQuarantined(job.Path))
            {
                TerminateJob(job, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
                return;
            }

            var remoteRoot = _classifier.GetOwnRemoteRoot(job.Path)
                              ?? (IsShortcut(job.Path) ? _classifier.GetShortcutTargetRemoteRoot(job.Path) : null);

            if (remoteRoot is not null)
            {
                MoveJobToRemoteLane(job, remoteRoot);
                return;
            }

            ExecuteIconFetch(job, lane, withinBreakerCall: false);
        }
        else
        {
            var root = job.RemoteRoot!;
            if (_breaker.IsOpen(root))
            {
                // UX-CRITICAL (review it.2): unlike a probed miss, this path never touched the file system —
                // the breaker short-circuited before any I/O — so Exists/IsDirectory must stay unknown
                // (null), exactly like every other Unavailable branch (watchdog abandon, quarantine, lane
                // disabled). Reporting exists:false here made BaseViewModel.OnIconResults call
                // ApplyProbe(false, false) for the ITEM'S OWN local path (e.g. a local .lnk whose target is
                // an unreachable remote share) and grey it out as "missing" for seconds, even though the
                // .lnk file itself is still right there.
                TerminateJob(job, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
                return;
            }

            using (_breaker.BeginCall(root))
            {
                // M-4 (review it.2): this call is already registered with the breaker as in-flight for
                // `root` — possibly as the half-open trial itself. ExecuteIconFetch must probe via
                // IPathProbe.ProbeWithinCall (never Probe) here, or the probe's own breaker.IsOpen check
                // would see this very call's Trial slot occupied and short-circuit itself to Exists=false,
                // even though the share just came back.
                ExecuteIconFetch(job, lane, withinBreakerCall: true);
            }
        }
    }

    private void ExecuteIconFetch(IconRealJob job, IconLoadLaneState lane, bool withinBreakerCall)
    {
        var probe = withinBreakerCall ? _pathProbe.ProbeWithinCall(job.Path) : _pathProbe.Probe(job.Path);
        var icon = _iconProvider.GetIcon(job.Path, job.Size);

        if (!icon.IsFrozen)
        {
            LogError("IconResultNotFrozen", null, new KeyValuePair<string, object?>("stage", LaneName(lane.Kind)));
            TerminateJob(job, IconLoadOutcome.Failed, icon: null, exists: null, isDirectory: null);
            return;
        }

        TerminateJob(job, IconLoadOutcome.Loaded, icon, probe.Exists, probe.IsDirectory);
    }

    private void MoveJobToRemoteLane(IconRealJob job, string remoteRoot)
    {
        job.RemoteRoot = remoteRoot;

        var orphaned = false;
        lock (_gate)
        {
            // !IsRegisteredLocked: the classification itself was stuck (IPersistFile::Load of the .lnk) and the
            // watchdog already resolved this job — do not resurrect it on the Remote lane.
            if (_disposed || !IsRegisteredLocked(job)) return;

            // M6: the job is leaving the Local lane's "currently executing" slot and going back to being a
            // plain pending job (in the Remote lane's queue instead) — reset both flags so a back-pressure
            // eviction scan (EvictOldestBackgroundJobLocked) does not skip it as "still executing" while it
            // is, in fact, sitting idle waiting for the Remote lane's worker to pick it up.
            job.Executing = false;
            job.StartedAtUtc = null;

            // H2: a Remote lane that already disabled itself (D-4) can never dispatch this job — resolve it
            // Unavailable right away instead of leaking it forever in a bucket nothing will ever drain.
            if (_remoteLane.Disabled)
            {
                orphaned = true;
            }
            else
            {
                EnqueueRealJobLocked(_remoteLane, job, job.OwnerGroup!, job.OwnerGroup!.Priority);
            }
        }

        if (orphaned)
        {
            TerminateJob(job, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
            return;
        }

        EnsureLaneRunning(_remoteLane);
    }

    private bool IsRegisteredLocked(IconRealJob job)
        => _realJobs.TryGetValue(new IconJobKey(job.Path, job.Size), out var registered) && ReferenceEquals(registered, job);

    private bool IsPathQuarantined(string path)
    {
        lock (_gate)
        {
            return _pathQuarantineUntil.TryGetValue(path, out var until) && _timeProvider.GetUtcNow() < until;
        }
    }

    private void TerminateJob(IconRealJob job, IconLoadOutcome outcome, ImageSource? icon, bool? exists, bool? isDirectory)
    {
        List<(StaIconLoadGroup Group, IconLoadResult Result)>? toBuffer = null;

        lock (_gate)
        {
            // Exactly one terminal result per job: a job already resolved (watchdog abandon, lane disable) is no
            // longer registered, so a late result from its abandoned worker is discarded here (D-4).
            if (!IsRegisteredLocked(job)) return;

            foreach (var waiter in job.Waiters)
            {
                if (!waiter.IsLive) continue;
                (toBuffer ??= []).Add((
                    waiter.Group,
                    new IconLoadResult(job.Path, job.Size, waiter.Generation, outcome, icon, exists, isDirectory)));
            }

            RemoveJobEverywhereLocked(job);
        }

        // M5: BufferResult may synchronously flush and Post to the delivery SynchronizationContext — never
        // while _gate is held.
        if (toBuffer is null) return;
        foreach (var (group, result) in toBuffer)
        {
            group.BufferResult(result, _options.DeliveryBatchSize, _options.DeliveryFlushInterval, _timeProvider);
        }
    }

    private void HandleStuckLane(IconLoadLaneState lane)
    {
        IconRealJob? job;
        IconTypeJob? typeJob;
        IStaWorker? oldWorker;
        var disarmWatchdog = false;

        lock (_gate)
        {
            job = lane.CurrentJob;
            typeJob = lane.CurrentTypeJob;
            if (job is null && typeJob is null) return; // already handled by a previous pass.

            // L1: a real job may already have been resolved elsewhere (e.g. TerminateJob raced this pass,
            // though the lane still thought it was "current") — do not double-abandon a worker for a job
            // that no longer needs it.
            if (job is not null && !IsRegisteredLocked(job)) return;

            oldWorker = lane.Worker;
            lane.CurrentJob = null;
            lane.CurrentTypeJob = null;
            lane.CurrentUnitStartedUtc = null;

            // Detached atomically with taking its job, so the abandoned worker's own RunUnit — if its stuck
            // call returns at any point from here on — recognises itself as stale and leaves the lane alone.
            lane.Worker = null;
            lane.AbandonedCount++;
            disarmWatchdog = --_activeUnitCount == 0;
        }

        if (disarmWatchdog) _watchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        oldWorker?.Abandon();

        var extensionOrTypeKey = job is not null ? IconDiagnostics.SafeExtension(job.Path) : typeJob!.Value.TypeKey;

        // The diagnostics trace keeps its two separate events (schema: docs/Refactoring/Testing/
        // diagnostics-log-events.md); the always-on app log gets ONE Warn per timeout carrying the fields of
        // both (ADR-audit review cycle 1, G4) — the timeout and the abandon are the same incident, and a pair
        // of entries per stuck icon only doubles the noise.
        var laneField = new KeyValuePair<string, object?>("lane", LaneName(lane.Kind));
        var extensionField = new KeyValuePair<string, object?>("extension", extensionOrTypeKey);
        var abandonedField = new KeyValuePair<string, object?>("abandoned", lane.AbandonedCount);
        LogDiagnostic("IconJobTimedOut", laneField, extensionField);
        LogDiagnostic("IconWorkerAbandoned", laneField, abandonedField);
        _appLog?.Warn(AppLogSource, "IconWorkerAbandoned", laneField, extensionField, abandonedField);

        if (job is not null)
        {
            if (lane.Kind == IconLoadLaneKind.Local)
            {
                lock (_gate) _pathQuarantineUntil[job.Path] = _timeProvider.GetUtcNow() + _options.PathQuarantine;
            }
            else
            {
                _breaker.ReportStuck(job.RemoteRoot!);
                LogWarn("RemoteRootQuarantined", new KeyValuePair<string, object?>("rootKind", ClassifyRootKind(job.RemoteRoot!)));
            }

            TerminateJob(job, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
        }
        else
        {
            // M1: a hung Local-lane GetTypeIcon call — drop its registration so no dead job hangs onto it
            // forever; every real job waiting on this type key simply falls through to its own
            // Loaded/Unavailable/Failed result later, same as ExecuteTypeJob's own exception path.
            lock (_gate)
            {
                _pendingTypeJobKeys.Remove(typeJob!.Value.TypeKey);
                _realJobsByTypeKey.Remove(typeJob.Value.TypeKey);
            }
        }

        bool disabled;
        lock (_gate)
        {
            disabled = lane.AbandonedCount > _options.MaxAbandonedWorkersPerLane;
            if (disabled)
            {
                lane.Disabled = true;
            }
            else if (!_disposed)
            {
                lane.Worker = _workerFactory.Create(lane.NextWorkerName());
            }

            lane.WorkerRunning = false;
        }

        if (disabled)
        {
            LogWarn("IconLaneDisabled", new KeyValuePair<string, object?>("lane", LaneName(lane.Kind)));
            DrainLaneAsUnavailable(lane);
        }
        else
        {
            EnsureLaneRunning(lane);
        }
    }

    private void DrainLaneAsUnavailable(IconLoadLaneState lane)
    {
        var toDrop = new List<IconRealJob>();

        lock (_gate)
        {
            DrainRingLocked(lane, lane.VisibleRing, IconLoadPriority.Visible, toDrop);
            DrainRingLocked(lane, lane.BackgroundRing, IconLoadPriority.Background, toDrop);
            lane.TypeJobs.Clear();
        }

        foreach (var job in toDrop)
        {
            TerminateJob(job, IconLoadOutcome.Unavailable, icon: null, exists: null, isDirectory: null);
        }
    }

    private void FlushAllGroupBuffers()
    {
        List<StaIconLoadGroup> groups;
        lock (_gate) groups = [.. _liveGroups];

        foreach (var group in groups) group.FlushIfAny();
    }

    private void LogDiagnostic(string name, params KeyValuePair<string, object?>[] payload)
    {
        if (_diagnosticsLog.IsEnabled) _diagnosticsLog.Write(IconDiagnostics.Category, name, null, payload);
    }

    /// <summary>
    /// An operational Warn-level event (lane disabled, root quarantined, worker abandoned): written to the
    /// diagnostics trace as before, and duplicated into the always-on <see cref="IAppLog"/> (ADR-033) so it
    /// survives without <c>--diag</c>. Payloads carry only closed-set values (lane, root kind, extension).
    /// </summary>
    private void LogWarn(string name, params KeyValuePair<string, object?>[] payload)
    {
        LogDiagnostic(name, payload);
        _appLog?.Warn(AppLogSource, name, payload);
    }

    /// <summary>
    /// An operational Error-level event (Freeze-invariant violation, sink/watchdog exception); see <see cref="LogWarn"/>.
    /// The diagnostics trace gets every occurrence; the app log only the first few per event name
    /// (<see cref="_appLogErrorThrottle"/>).
    /// </summary>
    private void LogError(string name, Exception? ex, params KeyValuePair<string, object?>[] payload)
    {
        LogDiagnostic(name, payload);
        _appLogErrorThrottle.Error(_appLog, AppLogSource, name, ex, payload);
    }
}

/// <summary>
/// One panel's slice of a <see cref="StaIconLoadQueue"/> (ADR-046). Every public member is a UI-thread API
/// — <see cref="Priority"/>, <see cref="Submit"/> and <see cref="CancelPending"/> are never called
/// concurrently with each other — but <see cref="Generation"/> and <see cref="IsDisposed"/> are also read
/// from the queue's worker threads (to decide whether a completed job still has anyone to deliver to), so
/// both are backed by lock-free, thread-safe fields.
/// </summary>
internal sealed class StaIconLoadGroup : IIconLoadGroup
{
    private readonly StaIconLoadQueue _queue;
    private readonly SynchronizationContext _deliveryContext;
    private readonly Action<IReadOnlyList<IconLoadResult>> _onResults;
    private readonly LinkedList<IconRealJob>[,] _buckets;
    private readonly object _bufferLock = new();

    private List<IconLoadResult> _buffer = [];
    private ITimer? _flushTimer;
    private int _generation;
    private IconLoadPriority _priority;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaIconLoadGroup"/> class. Only
    /// <see cref="StaIconLoadQueue.CreateGroup"/> constructs one.
    /// </summary>
    /// <param name="queue">The owning queue.</param>
    /// <param name="priority">Initial priority.</param>
    /// <param name="deliveryContext">Where results are posted.</param>
    /// <param name="onResults">The caller's result callback.</param>
    internal StaIconLoadGroup(
        StaIconLoadQueue queue, IconLoadPriority priority, SynchronizationContext deliveryContext, Action<IReadOnlyList<IconLoadResult>> onResults)
    {
        _queue = queue;
        _priority = priority;
        _deliveryContext = deliveryContext;
        _onResults = onResults;

        _buckets = new LinkedList<IconRealJob>[2, 2];
        for (var lane = 0; lane < 2; lane++)
        {
            for (var pr = 0; pr < 2; pr++)
            {
                _buckets[lane, pr] = new LinkedList<IconRealJob>();
            }
        }
    }

    /// <inheritdoc />
    public IconLoadPriority Priority
    {
        get => _priority;
        set
        {
            if (_priority == value) return;
            var old = _priority;
            _priority = value;
            _queue.RelocateGroupPriority(this, old, value);
        }
    }

    /// <inheritdoc />
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Gets a value indicating whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc />
    public void Submit(IReadOnlyList<IconLoadRequest> requests) => _queue.SubmitInternal(this, requests);

    /// <inheritdoc />
    public void CancelPending() => Interlocked.Increment(ref _generation);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;

        // Bumps Generation too, so any job's Waiters entry for this group is already "not live" the next
        // time a lane dequeues or terminates it — see IconJobWaiter.IsLive.
        CancelPending();

        lock (_bufferLock)
        {
            _buffer.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        // H3: without this, a disposed-but-never-cleaned-up group would sit in _liveGroups for the rest of
        // the queue's life — a real leak (it is never garbage-collected while the queue holds a reference),
        // and every FlushAllGroupBuffers/Dispose sweep would keep calling into it (harmlessly, since it is
        // already disposed, but pointlessly).
        _queue.RemoveGroup(this);
    }

    /// <summary>
    /// Disarms this group's flush timer and drops its buffer without marking the group itself disposed
    /// (<see cref="IsDisposed"/> stays as the caller left it) — called by <see cref="StaIconLoadQueue.Dispose"/>
    /// (L2/D-14) so that no buffered-but-not-yet-flushed result can ever trigger a late
    /// <see cref="SynchronizationContext.Post"/> once the queue itself is gone. <see cref="Dispose"/>'s own
    /// <see cref="CancelPending"/> already makes such a post harmless AT DELIVERY time (a stale generation is
    /// dropped); this instead prevents the post from happening at all.
    /// </summary>
    internal void SuppressPendingDelivery()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    /// <summary>Gets this group's job queue for <paramref name="lane"/>/<paramref name="priority"/> (ADR-046 D-6).</summary>
    /// <param name="lane">Which lane.</param>
    /// <param name="priority">Which priority.</param>
    internal LinkedList<IconRealJob> GetBucket(IconLoadLaneKind lane, IconLoadPriority priority) => _buckets[(int)lane, (int)priority];

    /// <summary>
    /// Buffers <paramref name="result"/> for delivery (D-9): flushes immediately once the buffer reaches
    /// <paramref name="batchSize"/>, otherwise arms (or re-arms, from the first result in an empty buffer)
    /// a flush timer for <paramref name="flushInterval"/>.
    /// </summary>
    /// <param name="result">The result to buffer.</param>
    /// <param name="batchSize">Buffer size that triggers an immediate flush.</param>
    /// <param name="flushInterval">Maximum time a partial batch waits before flushing.</param>
    /// <param name="timeProvider">Clock the flush timer runs against.</param>
    internal void BufferResult(IconLoadResult result, int batchSize, TimeSpan flushInterval, TimeProvider timeProvider)
    {
        List<IconLoadResult>? toFlush = null;

        lock (_bufferLock)
        {
            if (_disposed) return;

            _buffer.Add(result);
            if (_buffer.Count == 1)
            {
                // L4: one timer per group, reused via Change — every group only ever runs against the same
                // TimeProvider (the queue's own), so there is nothing to gain from disposing and recreating
                // an ITimer for every batch, and it is one less allocation on the hot path.
                if (_flushTimer is null)
                {
                    _flushTimer = timeProvider.CreateTimer(_ => FlushDue(), null, flushInterval, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _flushTimer.Change(flushInterval, Timeout.InfiniteTimeSpan);
                }
            }

            if (_buffer.Count >= batchSize) toFlush = TakeBufferLocked();
        }

        if (toFlush is { Count: > 0 }) Deliver(toFlush);
    }

    /// <summary>Flushes whatever is currently buffered, if anything (D-9's "lane emptied" trigger).</summary>
    internal void FlushIfAny()
    {
        List<IconLoadResult>? toFlush;
        lock (_bufferLock) toFlush = TakeBufferLocked();
        if (toFlush is { Count: > 0 }) Deliver(toFlush);
    }

    private static void DeliverCallback(object? state)
    {
        var (group, batch) = ((StaIconLoadGroup, List<IconLoadResult>))state!;
        if (group._disposed) return;

        // D-8: a result whose baked-in generation no longer matches the group's CURRENT one raced a
        // CancelPending made after it was buffered — silently dropped rather than handed to onResults.
        var currentGeneration = group.Generation;
        List<IconLoadResult>? live = null;
        foreach (var result in batch)
        {
            if (result.Generation != currentGeneration) continue;
            (live ??= new List<IconLoadResult>(batch.Count)).Add(result);
        }

        if (live is not { Count: > 0 }) return;

        try
        {
            group._onResults(live);
        }
        catch (Exception ex)
        {
            group._queue.LogSinkThrew(ex);
        }
    }

    private void FlushDue()
    {
        List<IconLoadResult>? toFlush;
        lock (_bufferLock) toFlush = TakeBufferLocked();
        if (toFlush is { Count: > 0 }) Deliver(toFlush);
    }

    private List<IconLoadResult>? TakeBufferLocked()
    {
        // L4: disarm (not dispose) — the same ITimer instance is reused for the group's next batch.
        _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (_buffer.Count == 0) return null;

        var taken = _buffer;
        _buffer = [];
        return taken;
    }

    private void Deliver(List<IconLoadResult> results)
    {
        if (_disposed) return;

        try
        {
            _deliveryContext.Post(DeliverCallback, (this, results));
        }
        catch (Exception)
        {
            // C-5: posting itself failing must never escape onto a worker thread. The results are simply
            // lost, same outcome as if the group had already been disposed.
        }
    }
}
