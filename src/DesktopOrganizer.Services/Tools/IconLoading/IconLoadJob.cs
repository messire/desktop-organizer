using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools.IconLoading;

/// <summary>The two worker lanes <see cref="StaIconLoadQueue"/> schedules jobs onto (ADR-046 D-2).</summary>
internal enum IconLoadLaneKind
{
    /// <summary>Everything not yet proven to sit on a network root.</summary>
    Local,

    /// <summary>Jobs whose path (or, for a <c>.lnk</c>, target) resolved to a network root.</summary>
    Remote,
}

/// <summary>
/// Dedup key for a real icon job: path (ordinal-ignore-case — Windows path semantics, matching
/// <c>IconProvider</c>'s own cache key) plus the already-snapped pixel size.
/// </summary>
/// <param name="Path">The item path.</param>
/// <param name="Size">The snapped pixel size.</param>
internal readonly record struct IconJobKey(string Path, int Size);

/// <summary>Equality for <see cref="IconJobKey"/>: ordinal-ignore-case path, exact size.</summary>
internal sealed class IconJobKeyComparer : IEqualityComparer<IconJobKey>
{
    /// <summary>The shared instance.</summary>
    public static readonly IconJobKeyComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(IconJobKey x, IconJobKey y)
        => x.Size == y.Size && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

    /// <inheritdoc />
    public int GetHashCode(IconJobKey obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.Size);
}

/// <summary>One caller waiting on a job's result: which group, and at which of its generations.</summary>
/// <param name="Group">The group that submitted the request.</param>
/// <param name="Generation">The group's <see cref="IIconLoadGroup.Generation"/> at submit time.</param>
internal readonly record struct IconJobWaiter(StaIconLoadGroup Group, int Generation)
{
    /// <summary>
    /// Whether this waiter is still worth delivering to: its group has not been disposed, and its
    /// captured generation still matches the group's current one (D-8 — an older generation was
    /// detached by a <see cref="IIconLoadGroup.CancelPending"/> the caller made since submitting).
    /// </summary>
    public bool IsLive => !Group.IsDisposed && Group.Generation == Generation;
}

/// <summary>
/// A single, deduplicated <c>(path, size)</c> icon fetch (ADR-046 D-7). Mutable — the scheduler updates it
/// in place as it moves between lanes, starts executing, and gains/loses waiters. Every mutation happens
/// under <see cref="StaIconLoadQueue"/>'s own gate.
/// </summary>
internal sealed class IconRealJob(string path, int size)
{
    /// <summary>Gets the item path.</summary>
    public string Path { get; } = path;

    /// <summary>Gets the already-snapped pixel size.</summary>
    public int Size { get; } = size;

    /// <summary>Gets or sets the lane this job currently belongs to.</summary>
    public IconLoadLaneKind Lane { get; set; } = IconLoadLaneKind.Local;

    /// <summary>Gets or sets the network root key, once classified as remote (<see langword="null"/> while still on the Local lane).</summary>
    public string? RemoteRoot { get; set; }

    /// <summary>Gets or sets the group whose per-lane/priority queue currently owns this job's rotation slot.</summary>
    public StaIconLoadGroup? OwnerGroup { get; set; }

    /// <summary>Gets or sets this job's node in <see cref="OwnerGroup"/>'s bucket for <see cref="Lane"/>/its priority at enqueue time, for O(1) removal (back-pressure eviction).</summary>
    public LinkedListNode<IconRealJob>? BucketNode { get; set; }

    /// <summary>Gets or sets this job's node in the queue's global age-ordered list, for O(1) back-pressure eviction.</summary>
    public LinkedListNode<IconRealJob>? InsertionNode { get; set; }

    /// <summary>Gets or sets a value indicating whether this job is currently running on a worker thread (excluded from back-pressure eviction).</summary>
    public bool Executing { get; set; }

    /// <summary>Gets or sets when this job started executing (watchdog stuck-job detection), <see langword="null"/> while pending.</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>Gets or sets the type-icon cache key this job is registered under for <see cref="IconTypeJob"/> fan-out (<see langword="null"/> once its own <see cref="Waiters"/> has a terminal result, or if a type icon was already cached at submit time).</summary>
    public string? TypeKey { get; set; }

    /// <summary>Gets the callers waiting on this job's result.</summary>
    public List<IconJobWaiter> Waiters { get; } = [];

    /// <summary>Gets a value indicating whether at least one waiter is still live (<see cref="IconJobWaiter.IsLive"/>).</summary>
    public bool HasLiveWaiters => Waiters.Exists(static w => w.IsLive);
}

/// <summary>
/// A provisional type-icon fetch (ADR-046 D-5) — always runs on the Local lane, ahead of every real job,
/// and fans its result out as <see cref="Abstractions.IconLoadOutcome.Interim"/> to every
/// <see cref="IconRealJob"/> currently registered under the same <see cref="TypeKey"/>.
/// </summary>
/// <param name="TypeKey">Lower-case extension, or the closed <c>"&lt;dir&gt;"</c> token.</param>
/// <param name="IsDirectory">Whether this key represents a directory.</param>
/// <param name="RepresentativePath">One path that needed this type key, passed to <c>IIconProvider.GetTypeIcon</c> purely so it can re-derive the extension (unused when <paramref name="IsDirectory"/> is <see langword="true"/>).</param>
internal readonly record struct IconTypeJob(string TypeKey, bool IsDirectory, string RepresentativePath);
