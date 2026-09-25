using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Buffers <see cref="ActivationRequest"/>s that arrive before anyone is ready to handle them, then
/// switches to direct delivery. Exists because the pipe server is started before <c>new App(...)</c>
/// (ADR-023 D-2): between <c>TryBeginServing()</c> and the dispatcher starting,
/// <c>Application.Current</c> is <see langword="null"/>, yet a double-click on the shortcut can
/// already have sent an activation. This is the one place that in-between state lives, and it is
/// pure — testable with no GUI (ADR-023 §2.2).
/// </summary>
/// <remarks>
/// Internal, unlike the rest of this folder: it is an implementation seam for
/// <c>SingleInstanceCoordinator</c>, not a public contract — callers outside
/// <c>DesktopOrganizer.Services</c> only ever see <c>ISingleInstanceCoordinator.ActivationRequested</c>.
/// A single lock serializes <see cref="Publish"/> against <see cref="AttachSink"/> so a request can
/// never race the buffer drain and be delivered out of order.
/// </remarks>
internal sealed class ActivationRequestRelay
{
    private readonly object _gate = new();
    private readonly Queue<ActivationRequest> _buffer = new();
    private Action<ActivationRequest>? _sink;

    /// <summary>Number of requests buffered and not yet delivered. For diagnostics/tests only.</summary>
    public int BufferedCount
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>
    /// Publishes a request. Thread-safe — may be called from the pipe server's background accept
    /// thread. Buffers the request if no sink is attached yet; otherwise delivers it directly,
    /// synchronously, on the calling thread.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the request had to be buffered because the UI was not ready to
    /// receive it yet, <see langword="false"/> if it was delivered straight to the attached sink.
    /// Reported as <c>ActivationRequestReceived.bufferedAtArrival</c> (phase-3 plan §4) — the one
    /// signal that distinguishes "the user double-clicked the shortcut while we were still starting"
    /// from a steady-state activation. Returned rather than exposed as a separate "has a sink"
    /// property so the answer is decided under the same lock as the delivery itself and can never
    /// disagree with what actually happened.
    /// </returns>
    /// <param name="request">The activation request to publish.</param>
    public bool Publish(ActivationRequest request)
    {
        lock (_gate)
        {
            if (_sink is { } sink)
            {
                sink(request);
                return false;
            }

            _buffer.Enqueue(request);
            return true;
        }
    }

    /// <summary>
    /// Attaches the sink that will receive all future <see cref="Publish"/> calls, and immediately
    /// drains anything buffered so far into it, in the order it was published. Once this returns,
    /// every subsequent <see cref="Publish"/> is delivered straight through without buffering.
    /// </summary>
    /// <param name="sink">The delegate that receives every future (and currently buffered) request.</param>
    public void AttachSink(Action<ActivationRequest> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            _sink = sink;

            while (_buffer.Count > 0)
            {
                sink(_buffer.Dequeue());
            }
        }
    }
}
