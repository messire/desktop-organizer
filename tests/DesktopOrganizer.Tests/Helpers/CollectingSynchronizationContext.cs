using System.Diagnostics.CodeAnalysis;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// Fake UI-thread <see cref="SynchronizationContext"/> for <c>StaIconLoadQueueTests</c> (ADR-046):
/// <see cref="Post"/> just queues the callback — nothing runs until the test calls <see cref="Pump"/> — so
/// a test can assert what happened (or didn't) on the "worker" side before any delivery reaches the "UI"
/// side. <see cref="Send"/> throws: production code must only ever <see cref="Post"/> to this context
/// (ADR-046 C-5) — a stray <c>Send</c> is a bug this fake deliberately surfaces as a test failure rather
/// than silently running inline.
/// </summary>
/// <remarks>
/// Thread-safe: <c>StaIconLoadQueueThreadingTests</c> has real STA workers <see cref="Post"/> here while the
/// test thread waits and pumps (<see cref="PumpUntil"/>). Callbacks always run on the thread calling
/// <see cref="Pump"/>, never under the internal lock.
/// </remarks>
internal sealed class CollectingSynchronizationContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();
    private int _postVersion;

    /// <summary>Gets how many callbacks are currently queued, unpumped.</summary>
    public int PostedCount
    {
        get
        {
            lock (_gate) return _posted.Count;
        }
    }

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
        {
            _posted.Enqueue((d, state));
            _postVersion++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
        => throw new InvalidOperationException($"{nameof(CollectingSynchronizationContext)}.{nameof(Send)} was called — production code must only ever Post (ADR-046 C-5).");

    /// <summary>
    /// Runs every callback posted so far, in order — including ones a callback itself posts while pumping
    /// (e.g. a delivery-buffer flush timer firing synchronously inside a <see cref="ManualTimeProvider.Advance"/>
    /// call made from within a callback).
    /// </summary>
    /// <returns>How many callbacks ran.</returns>
    public int Pump()
    {
        var count = 0;
        while (TryDequeue(out var callback, out var state))
        {
            callback(state);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Pumps (on the calling thread) until <paramref name="condition"/> holds, waiting for further
    /// <see cref="Post"/>s from other threads in between — a signal-driven wait, no sleeps.
    /// </summary>
    /// <param name="condition">Checked after every pump; typically inspects what the pumped callbacks collected.</param>
    /// <param name="timeout">Overall budget.</param>
    /// <returns><see langword="true"/> once <paramref name="condition"/> held; <see langword="false"/> when the budget ran out first.</returns>
    public bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            int version;
            lock (_gate) version = _postVersion;

            Pump();
            if (condition()) return true;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !WaitForPost(version, remaining)) return condition();
        }
    }

    /// <summary>Blocks until something is posted after <paramref name="seenVersion"/>, or the budget runs out.</summary>
    /// <param name="seenVersion">The post counter observed before the caller last pumped.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns><see langword="true"/> when a new post arrived.</returns>
    private bool WaitForPost(int seenVersion, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_postVersion == seenVersion)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining)) return _postVersion != seenVersion;
            }

            return true;
        }
    }

    private bool TryDequeue([NotNullWhen(true)] out SendOrPostCallback? callback, out object? state)
    {
        lock (_gate)
        {
            if (_posted.Count == 0)
            {
                callback = null;
                state = null;
                return false;
            }

            (callback, state) = _posted.Dequeue();
            return true;
        }
    }
}
