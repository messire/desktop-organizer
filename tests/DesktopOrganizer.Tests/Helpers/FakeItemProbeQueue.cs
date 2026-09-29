using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Fake <see cref="IItemProbeQueue"/> (ADR-046 7c) for <c>BaseViewModel</c>/<c>ChildWindowViewModel</c>
/// liveness unit tests: <see cref="ProbeAsync"/> never resolves automatically — it records the call and
/// hands back a <see cref="ProbeCall"/> a test completes (or cancels) explicitly, so a liveness pass can
/// be driven deterministically across the exact moment its off-thread probe is "in flight".
/// </summary>
internal sealed class FakeItemProbeQueue : IItemProbeQueue
{
    /// <summary>Every call made so far, in call order.</summary>
    public List<ProbeCall> Calls { get; } = [];

    /// <summary>Gets the most recently made call. Throws if none was made yet.</summary>
    public ProbeCall LastCall => Calls[^1];

    /// <inheritdoc />
    public Task<IReadOnlyList<PathProbeResult>> ProbeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var call = new ProbeCall(paths, cancellationToken);
        Calls.Add(call);
        return call.Completion.Task;
    }
}

/// <summary>
/// One <see cref="FakeItemProbeQueue.ProbeAsync"/> invocation — everything a test needs to complete or
/// cancel it deterministically, and to inspect exactly which paths (and in what order) were probed.
/// </summary>
internal sealed class ProbeCall
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProbeCall"/> class.
    /// </summary>
    /// <param name="paths">The paths this call was made with.</param>
    /// <param name="cancellationToken">The token this call was made with.</param>
    public ProbeCall(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Paths = paths;
        CancellationToken = cancellationToken;

        // Deliberately NOT TaskCreationOptions.RunContinuationsAsynchronously: a test's Complete() call
        // must run the awaiting liveness pass's continuation (up to and including its next
        // SynchronizationContext.Post) SYNCHRONOUSLY, inline, on the calling (test) thread — otherwise
        // Complete() immediately followed by CollectingSynchronizationContext.Pump() would race a
        // thread-pool-scheduled continuation that hasn't posted anything yet. This mirrors
        // FakeIconLoadGroup.Deliver's own synchronous Post call, just one layer further down (through the
        // runtime's own await machinery instead of a direct Post).
        Completion = new TaskCompletionSource<IReadOnlyList<PathProbeResult>>();

        // Mirrors the real IItemProbeQueue contract: cancelling the token faults the returned task with
        // OperationCanceledException, exactly like BackgroundItemProbeQueue's own ThrowIfCancellationRequested.
        cancellationToken.Register(() => Completion.TrySetCanceled(cancellationToken));
    }

    /// <summary>Gets the paths this call was made with, in order.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Gets the cancellation token this call was made with.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the completion source backing this call's returned task.</summary>
    public TaskCompletionSource<IReadOnlyList<PathProbeResult>> Completion { get; }

    /// <summary>Completes this call with the same <c>(exists, isDirectory)</c> pair for every path, in order.</summary>
    /// <param name="exists">Whether every path exists.</param>
    /// <param name="isDirectory">Whether every path is a directory.</param>
    public void Complete(bool exists, bool isDirectory = false)
        => Completion.TrySetResult([.. Paths.Select(p => new PathProbeResult(p, exists, isDirectory, IsKnown: true))]);

    /// <summary>Completes this call with explicit, per-path results (same order as <see cref="Paths"/>).</summary>
    /// <param name="results">The results to complete with.</param>
    public void Complete(params PathProbeResult[] results) => Completion.TrySetResult(results);
}
