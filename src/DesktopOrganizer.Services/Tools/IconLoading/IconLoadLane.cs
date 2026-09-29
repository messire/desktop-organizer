using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>
/// Round-robin rotation of groups that currently have at least one pending job in a given
/// <c>(lane, priority)</c> bucket (ADR-046 D-6). A group is a member at most once; <see cref="DequeueRotate"/>
/// removes it (the caller re-adds it via <see cref="EnsureMember"/> only if it still has more work), which
/// is what gives every member a fair turn instead of always draining one group to empty first.
/// </summary>
internal sealed class IconLoadRotationRing
{
    private readonly LinkedList<StaIconLoadGroup> _order = new();
    private readonly Dictionary<StaIconLoadGroup, LinkedListNode<StaIconLoadGroup>> _members = [];

    /// <summary>Gets a value indicating whether no group currently has pending work in this bucket.</summary>
    public bool IsEmpty => _order.Count == 0;

    /// <summary>Adds <paramref name="group"/> at the back of the rotation if it is not already a member.</summary>
    /// <param name="group">The group to (re-)admit.</param>
    public void EnsureMember(StaIconLoadGroup group)
    {
        if (_members.ContainsKey(group)) return;
        _members[group] = _order.AddLast(group);
    }

    /// <summary>Removes <paramref name="group"/> from the rotation, if present. No-op otherwise.</summary>
    /// <param name="group">The group to remove.</param>
    public void Remove(StaIconLoadGroup group)
    {
        if (_members.Remove(group, out var node)) _order.Remove(node);
    }

    /// <summary>Removes and returns the group at the front of the rotation, or <see langword="null"/> when empty.</summary>
    public StaIconLoadGroup? DequeueRotate()
    {
        if (_order.First is not { } node) return null;
        _order.RemoveFirst();
        _members.Remove(node.Value);
        return node.Value;
    }
}

/// <summary>
/// Mutable per-lane state (ADR-046 D-1/D-2/D-4): its own STA worker, the currently-executing job (for the
/// watchdog), the abandoned-worker count, and — Local only — the type-icon job queue that always runs
/// ahead of everything else.
/// </summary>
/// <param name="kind">Which lane this is.</param>
/// <param name="workerNamePrefix">Thread-name prefix passed to <see cref="IStaWorkerFactory.Create"/> (a running counter is appended on every (re)creation, e.g. after a watchdog abandon).</param>
internal sealed class IconLoadLaneState(IconLoadLaneKind kind, string workerNamePrefix)
{
    private int _workerGeneration;

    /// <summary>Gets which lane this is.</summary>
    public IconLoadLaneKind Kind { get; } = kind;

    /// <summary>Gets the rotation of groups with a pending <see cref="IconLoadPriority.Visible"/> job.</summary>
    public IconLoadRotationRing VisibleRing { get; } = new();

    /// <summary>Gets the rotation of groups with a pending <see cref="IconLoadPriority.Background"/> job.</summary>
    public IconLoadRotationRing BackgroundRing { get; } = new();

    /// <summary>Gets the Local-lane-only type-icon job queue (D-5) — always served before either ring above. Unused on the Remote lane.</summary>
    public Queue<IconTypeJob> TypeJobs { get; } = new();

    /// <summary>Gets or sets this lane's current worker, or <see langword="null"/> before the first job is submitted, while the watchdog is replacing an abandoned worker, and after the lane disabled itself. A worker that is no longer this reference is stale and must not touch the lane.</summary>
    public IStaWorker? Worker { get; set; }

    /// <summary>Gets or sets a value indicating whether a <c>RunNext</c> continuation is currently posted/executing for this lane.</summary>
    public bool WorkerRunning { get; set; }

    /// <summary>Gets or sets the real job currently executing on this lane's worker, for the watchdog's stuck-job scan. <see langword="null"/> while idle or running a type job.</summary>
    public IconRealJob? CurrentJob { get; set; }

    /// <summary>Gets or sets the type-icon job currently executing on this lane's worker (M1: type jobs are subject to the watchdog too — a hung shell handler's <c>GetTypeIcon</c> call must not wedge the Local lane forever). <see langword="null"/> while idle or running a real job.</summary>
    public IconTypeJob? CurrentTypeJob { get; set; }

    /// <summary>Gets or sets when the currently-executing unit (<see cref="CurrentJob"/> OR <see cref="CurrentTypeJob"/> — never both) started, for the watchdog's single stuck-unit check (M1). <see langword="null"/> while idle.</summary>
    public DateTimeOffset? CurrentUnitStartedUtc { get; set; }

    /// <summary>Gets or sets how many of this lane's workers have been abandoned by the watchdog so far (D-4).</summary>
    public int AbandonedCount { get; set; }

    /// <summary>Gets or sets a value indicating whether this lane disabled itself after exceeding its abandoned-worker limit (D-4). Once set, the lane never resumes for the life of the queue.</summary>
    public bool Disabled { get; set; }

    /// <summary>Gets or sets when the current drain episode (a <see cref="WorkerRunning"/> run from idle to idle) started, for the <c>IconQueueDrained</c> diagnostic.</summary>
    public DateTimeOffset DrainStartedUtc { get; set; }

    /// <summary>Gets or sets how many real jobs (not type-icon jobs) the current drain episode has executed so far.</summary>
    public int JobsProcessedThisDrain { get; set; }

    /// <summary>Returns the next unique worker thread name for this lane (e.g. after a watchdog replacement).</summary>
    public string NextWorkerName() => $"{workerNamePrefix}#{++_workerGeneration}";
}
