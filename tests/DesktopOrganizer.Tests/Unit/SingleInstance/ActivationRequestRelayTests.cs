using System.Collections.Concurrent;

using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.SingleInstance;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.SingleInstance;

/// <summary>
/// Unit tests for <c>ActivationRequestRelay</c> (ADR-023 §2.2). <c>internal</c> — reachable here via
/// <c>InternalsVisibleTo</c> (Directory.Build.props).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class ActivationRequestRelayTests
{
    /// <summary>
    /// Verifies that <c>ActivationRequestRelay.Publish</c> buffers requests published before a sink has
    /// been attached (T-REL-01).
    /// </summary>
    [Fact]
    public void Publish_BeforeAttachSink_Buffers()
    {
        var relay = new ActivationRequestRelay();

        relay.Publish(new ActivationRequest(1, ActivationCommand.ActivateSettings, 1));
        relay.Publish(new ActivationRequest(1, ActivationCommand.ActivateSettings, 2));

        Assert.Equal(2, relay.BufferedCount);
    }

    /// <summary>
    /// Verifies that <c>ActivationRequestRelay.AttachSink</c> drains the buffered requests in the
    /// exact order they were published (T-REL-02).
    /// </summary>
    [Fact]
    public void AttachSink_DrainsBuffer_InPublishOrder()
    {
        var relay = new ActivationRequestRelay();
        var first = new ActivationRequest(1, ActivationCommand.ActivateSettings, 1);
        var second = new ActivationRequest(1, ActivationCommand.ActivateSettings, 2);
        var third = new ActivationRequest(1, ActivationCommand.ActivateSettings, 3);

        relay.Publish(first);
        relay.Publish(second);
        relay.Publish(third);

        var received = new List<ActivationRequest>();
        relay.AttachSink(received.Add);

        Assert.Equal([first, second, third], received);
        Assert.Equal(0, relay.BufferedCount);
    }

    /// <summary>
    /// Verifies that every request published concurrently from many threads is buffered exactly once,
    /// with no duplicates and no loss (T-REL-03).
    /// </summary>
    [Fact]
    public async Task Publish_FromManyThreadsConcurrently_AllArePublishedExactlyOnce()
    {
        var relay = new ActivationRequestRelay();
        const int threadCount = 16;
        const int perThread = 200;

        var tasks = new Task[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var sessionBase = t * perThread;
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    relay.Publish(new ActivationRequest(1, ActivationCommand.ActivateSettings, sessionBase + i));
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(threadCount * perThread, relay.BufferedCount);

        var received = new ConcurrentBag<ActivationRequest>();
        relay.AttachSink(r => received.Add(r));

        Assert.Equal(threadCount * perThread, received.Count);
        Assert.Equal(received.Select(r => r.SessionId).Distinct().Count(), received.Count);
    }

    /// <summary>
    /// Verifies that once a sink is attached, <c>ActivationRequestRelay.Publish</c> delivers directly
    /// to it rather than buffering (T-REL-04).
    /// </summary>
    [Fact]
    public void Publish_AfterAttachSink_DeliveredDirectly_NotBuffered()
    {
        var relay = new ActivationRequestRelay();
        var received = new List<ActivationRequest>();
        relay.AttachSink(received.Add);

        var request = new ActivationRequest(1, ActivationCommand.ActivateSettings, 99);
        relay.Publish(request);

        Assert.Equal([request], received);
        Assert.Equal(0, relay.BufferedCount);
    }

    /// <summary>
    /// Verifies that <c>ActivationRequestRelay.AttachSink</c> throws <see cref="ArgumentNullException"/>
    /// for a <see langword="null"/> sink.
    /// </summary>
    [Fact]
    public void AttachSink_NullSink_Throws()
    {
        var relay = new ActivationRequestRelay();

        Assert.Throws<ArgumentNullException>(() => relay.AttachSink(null!));
    }

    /// <summary>
    /// Verifies that a freshly constructed <c>ActivationRequestRelay</c> starts with
    /// <c>BufferedCount</c> equal to zero.
    /// </summary>
    [Fact]
    public void BufferedCount_InitiallyZero()
    {
        var relay = new ActivationRequestRelay();

        Assert.Equal(0, relay.BufferedCount);
    }

    // ── MAJOR M-3 (P08 fix cycle 2): Publish/AttachSink must never wedge on a slow/blocking sink ────

    /// <summary>
    /// Both <see cref="ActivationRequestRelay.Publish"/> and <see cref="ActivationRequestRelay.AttachSink"/>
    /// invoke the sink WHILE HOLDING the relay's internal gate (see their own remarks/implementation).
    /// That is only safe because production's sink is fire-and-forget: <c>App</c>'s activation wiring now
    /// marshals through <see cref="UiDispatch.TryBeginInvoke"/> (P08 fix cycle 2), which queues and
    /// returns immediately, never blocking on the UI thread. Before that fix, a sink ending in a BLOCKING
    /// <c>Dispatcher.Invoke</c> could hold this gate for as long as the UI thread took to service it —
    /// and if the UI thread's own next step needed this same gate (e.g. a hypothetical future
    /// re-subscription reaching <see cref="ActivationRequestRelay.AttachSink"/> while a
    /// <see cref="ActivationRequestRelay.Publish"/> call from the single accept thread was mid-flight),
    /// the two would deadlock. This test pins the FIX's guarantee at the relay's own boundary: many
    /// concurrent <see cref="ActivationRequestRelay.Publish"/> calls racing a concurrent
    /// <see cref="ActivationRequestRelay.AttachSink"/>, through a sink shaped like the current production one (fast,
    /// non-blocking), complete within a tight bound — not the unbounded hang the pre-fix blocking sink
    /// could have produced.
    /// </summary>
    [Fact]
    public async Task Publish_RacingConcurrentlyWithAttachSink_ThroughANonBlockingSink_NeverHangs()
    {
        var relay = new ActivationRequestRelay();
        using var attachSinkMayProceed = new ManualResetEventSlim(initialState: false);
        var received = new ConcurrentBag<ActivationRequest>();

        var publisher = Task.Run(() =>
        {
            attachSinkMayProceed.Wait(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 200; i++)
                relay.Publish(new ActivationRequest(1, ActivationCommand.ActivateSettings, i));
        });

        var attacher = Task.Run(() =>
        {
            attachSinkMayProceed.Set();

            // Shaped like production's fixed (fire-and-forget) sink: it never blocks the caller, so
            // holding the gate for the duration of this call is always cheap.
            relay.AttachSink(r => received.Add(r));
        });

        var bothCompleted = Task.WhenAll(publisher, attacher);
        var completed = await Task.WhenAny(bothCompleted, Task.Delay(TimeSpan.FromSeconds(5))) == bothCompleted;

        Assert.True(completed, "Publish/AttachSink must not hang against a fast, non-blocking sink.");
        await bothCompleted;
        Assert.Equal(200, received.Select(r => r.SessionId).Distinct().Count());
    }
}
