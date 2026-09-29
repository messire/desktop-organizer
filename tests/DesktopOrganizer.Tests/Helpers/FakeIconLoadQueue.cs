using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Fake <see cref="IIconLoadQueue"/> (ADR-046) for <c>BaseViewModel</c>/<c>ChildWindowViewModel</c>
/// unit tests: <see cref="CreateGroup"/> just records the call and hands back a
/// <see cref="FakeIconLoadGroup"/> — nothing is ever resolved automatically. A test drives delivery
/// explicitly via <see cref="FakeIconLoadGroup.Deliver"/> plus the captured delivery
/// <see cref="SynchronizationContext"/>'s own pump (typically a <see cref="CollectingSynchronizationContext"/>).
/// </summary>
internal sealed class FakeIconLoadQueue : IIconLoadQueue
{
    /// <summary>Every group created so far, in creation order.</summary>
    public List<FakeIconLoadGroup> Groups { get; } = [];

    /// <summary>Gets the most recently created group. Throws if none was created yet.</summary>
    public FakeIconLoadGroup Group => Groups[^1];

    /// <summary>Gets whether <see cref="Dispose"/> was called.</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public IIconLoadGroup CreateGroup(
        IconLoadPriority priority, SynchronizationContext deliveryContext, Action<IReadOnlyList<IconLoadResult>> onResults)
    {
        var group = new FakeIconLoadGroup(priority, deliveryContext, onResults);
        Groups.Add(group);
        return group;
    }

    /// <inheritdoc />
    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// Fake <see cref="IIconLoadGroup"/> backing <see cref="FakeIconLoadQueue"/> — records every
/// <see cref="Submit"/> call verbatim (no automatic resolution) and replicates the real queue's
/// generation-filtering contract (ADR-046 D-8): <see cref="Deliver"/> silently drops any result whose
/// <see cref="IconLoadResult.Generation"/> doesn't match the group's CURRENT <see cref="Generation"/>, and
/// delivers nothing at all once <see cref="Dispose"/> has been called.
/// </summary>
internal sealed class FakeIconLoadGroup(
    IconLoadPriority priority, SynchronizationContext deliveryContext, Action<IReadOnlyList<IconLoadResult>> onResults)
    : IIconLoadGroup
{
    /// <inheritdoc />
    public IconLoadPriority Priority { get; set; } = priority;

    /// <inheritdoc />
    public int Generation { get; private set; }

    /// <summary>Gets how many times <see cref="CancelPending"/> was called.</summary>
    public int CancelPendingCallCount { get; private set; }

    /// <summary>Gets whether <see cref="Dispose"/> was called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Every batch passed to <see cref="Submit"/>, in call order.</summary>
    public List<IReadOnlyList<IconLoadRequest>> SubmitCalls { get; } = [];

    /// <summary>Gets the flattened requests of every <see cref="Submit"/> call so far, in order.</summary>
    public IReadOnlyList<IconLoadRequest> AllSubmittedRequests => [.. SubmitCalls.SelectMany(batch => batch)];

    /// <inheritdoc />
    public void Submit(IReadOnlyList<IconLoadRequest> requests)
    {
        if (IsDisposed || requests.Count == 0) return;
        SubmitCalls.Add(requests);
    }

    /// <inheritdoc />
    public void CancelPending()
    {
        if (IsDisposed) return;
        CancelPendingCallCount++;
        Generation++;
    }

    /// <inheritdoc />
    public void Dispose() => IsDisposed = true;

    /// <summary>
    /// Builds a result for <paramref name="request"/> at the group's CURRENT <see cref="Generation"/> —
    /// convenience for the common case where a test wants to simulate an in-order response to a request
    /// it just observed via <see cref="SubmitCalls"/>.
    /// </summary>
    /// <param name="request">The request this result answers.</param>
    /// <param name="outcome">How the request was resolved.</param>
    /// <param name="icon">The resolved icon, if any.</param>
    /// <param name="exists">Whether the item existed at probe time, if known.</param>
    /// <param name="isDirectory">Whether the item is a directory, if known.</param>
    public IconLoadResult MakeResult(
        IconLoadRequest request, IconLoadOutcome outcome, ImageSource? icon = null, bool? exists = null, bool? isDirectory = null)
        => new(request.Path, request.PixelSize, Generation, outcome, icon, exists, isDirectory);

    /// <summary>
    /// Simulates the queue delivering <paramref name="results"/> to this group's <c>onResults</c> callback:
    /// posts (never sends — ADR-046 C-5) exactly once to the captured delivery context with whichever of
    /// the results still carry the CURRENT generation (silently dropping the rest, and posting nothing at
    /// all if every one of them is stale or this group is disposed) — the caller still has to pump the
    /// delivery context (e.g. <see cref="CollectingSynchronizationContext.Pump"/>) for the callback to
    /// actually run.
    /// </summary>
    /// <param name="results">The results to deliver.</param>
    public void Deliver(params IconLoadResult[] results)
    {
        if (IsDisposed) return;

        var current = results.Where(r => r.Generation == Generation).ToList();
        if (current.Count == 0) return;

        deliveryContext.Post(_ => onResults(current), null);
    }
}
