using System.Windows.Threading;
using DesktopOrganizer.Services.Tools.IconLoading;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>
/// Tests for <see cref="StaWorkerThread"/> (ADR-046 D-1/D-4, docs/PLANS/background-icon-loading.md §5.2,
/// §8.1): a real dedicated STA thread with a <see cref="Dispatcher"/> pump. Every wait is signal-driven with
/// a 10 s budget (<c>docs/Refactoring/Testing/sta-test-conventions.md</c>, "Wall-clock тесты"); nothing
/// asserts on elapsed time.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class StaWorkerThreadTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Work runs on the worker's own background STA thread, never the caller's.</summary>
    [Fact]
    public void TryPost_RunsWorkOnDedicatedBackgroundStaThread()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#1");
        using var ran = new ManualResetEventSlim();
        ApartmentState apartment = ApartmentState.Unknown;
        var threadId = 0;
        var isBackground = false;
        string? threadName = null;

        Assert.True(worker.TryPost(() =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            threadId = Environment.CurrentManagedThreadId;
            isBackground = Thread.CurrentThread.IsBackground;
            threadName = Thread.CurrentThread.Name;
            ran.Set();
        }));

        Assert.True(ran.Wait(Budget));
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal(worker.ManagedThreadId, threadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
        Assert.True(isBackground);
        Assert.Equal("DO.Test.Worker#1", threadName);
    }

    /// <summary>Items posted right after construction (before the pump is up) are buffered and run in FIFO order.</summary>
    [Fact]
    public void TryPost_ManyItemsRightAfterConstruction_RunInFifoOrder()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#fifo");
        using var done = new ManualResetEventSlim();
        var order = new List<int>(); // touched on the worker thread only, read after the signal

        for (var i = 0; i < 50; i++)
        {
            var captured = i;
            Assert.True(worker.TryPost(() => order.Add(captured)));
        }

        Assert.True(worker.TryPost(done.Set));

        Assert.True(done.Wait(Budget));
        Assert.Equal(Enumerable.Range(0, 50), order);
    }

    /// <summary>An exception escaping a work item neither kills the worker nor skips the next item.</summary>
    [Fact]
    public void TryPost_WorkThrows_WorkerSurvivesAndRunsNextItem()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#throw");
        using var nextRan = new ManualResetEventSlim();

        Assert.True(worker.TryPost(() => throw new InvalidOperationException("job failure")));
        Assert.True(worker.TryPost(nextRan.Set));

        Assert.True(nextRan.Wait(Budget));
    }

    /// <summary>
    /// Non-reentrancy (contract of <c>IStaWorker.TryPost</c>, R7-13): an item that pumps a nested frame does
    /// not start the next queued item on its own stack. The next item's drain operation is queued at a HIGHER
    /// dispatcher priority than the operation that ends the nested frame, so the nested pump reaches it first
    /// — without the guard, "B" would be logged before "frame-exit".
    /// </summary>
    [Fact]
    public void TryPost_ItemPumpsNestedFrame_NextItemDoesNotStartOnThatStack()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#nested");
        using var done = new ManualResetEventSlim();
        var log = new List<string>(); // worker thread only
        var depth = 0;

        Assert.True(worker.TryPost(() =>
        {
            log.Add("A-start");
            Assert.True(worker.TryPost(() =>
            {
                log.Add($"B(depth={depth})");
                done.Set();
            }));

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                log.Add("frame-exit");
                frame.Continue = false;
            });

            depth = 1;
            Dispatcher.PushFrame(frame);
            depth = 0;
            log.Add("A-end");
        }));

        Assert.True(done.Wait(Budget));
        Assert.Equal(["A-start", "frame-exit", "A-end", "B(depth=0)"], log);
    }

    /// <summary>An idle worker that is abandoned refuses work and its thread exits on its own.</summary>
    [Fact]
    public async Task Abandon_IdleWorker_RejectsWorkAndThreadExits()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#abandon-idle");
        using var warm = new ManualResetEventSlim();
        Assert.True(worker.TryPost(warm.Set));
        Assert.True(warm.Wait(Budget));

        worker.Abandon();

        Assert.True(worker.IsAbandoned);
        Assert.False(worker.TryPost(() => { }));
        await worker.Completion.WaitAsync(Budget);
    }

    /// <summary>
    /// Abandon never blocks the caller, even while the worker is stuck in an item; queued items are discarded,
    /// and once the stuck item returns the thread shuts its own pump down and exits.
    /// </summary>
    [Fact]
    public async Task Abandon_WhileItemBlocked_DoesNotBlockCaller_DiscardsQueue_ThreadExitsAfterItemReturns()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#abandon-stuck");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queuedItemRan = false;

        Assert.True(worker.TryPost(() =>
        {
            entered.Set();
            release.Wait(Budget);
        }));
        Assert.True(worker.TryPost(() => queuedItemRan = true));
        Assert.True(entered.Wait(Budget));

        worker.Abandon(); // returns while the item is still blocked — reaching the next line is the proof

        Assert.False(worker.Completion.IsCompleted);
        release.Set();

        await worker.Completion.WaitAsync(Budget);
        Assert.False(queuedItemRan);
    }

    /// <summary>Dispose shuts the pump down and joins the thread; work is rejected afterwards; a second Dispose is a no-op.</summary>
    [Fact]
    public void Dispose_IdleWorker_JoinsThreadAndRejectsWork()
    {
        var worker = new StaWorkerThread("DO.Test.Worker#dispose", joinTimeout: Budget);
        using var warm = new ManualResetEventSlim();
        Assert.True(worker.TryPost(warm.Set));
        Assert.True(warm.Wait(Budget));

        worker.Dispose();

        Assert.True(worker.Completion.IsCompleted);
        Assert.False(worker.TryPost(() => { }));
        worker.Dispose();
    }

    /// <summary>Disposing a worker before its thread even reached its pump still lets the thread exit.</summary>
    [Fact]
    public async Task Dispose_ImmediatelyAfterConstruction_ThreadExits()
    {
        var worker = new StaWorkerThread("DO.Test.Worker#dispose-early", joinTimeout: Budget);

        worker.Dispose();

        await worker.Completion.WaitAsync(Budget);
    }

    /// <summary>
    /// Dispose's join is bounded: with a zero join timeout it returns while the worker is still stuck, and the
    /// thread still exits by itself once the item returns.
    /// </summary>
    [Fact]
    public async Task Dispose_WhileItemBlocked_ReturnsAfterBoundedJoin_ThreadExitsLater()
    {
        var worker = new StaWorkerThread("DO.Test.Worker#dispose-stuck", joinTimeout: TimeSpan.Zero);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Assert.True(worker.TryPost(() =>
        {
            entered.Set();
            release.Wait(Budget);
        }));
        Assert.True(entered.Wait(Budget));

        worker.Dispose();

        Assert.False(worker.Completion.IsCompleted);
        release.Set();
        await worker.Completion.WaitAsync(Budget);
    }

    /// <summary>Disposing an already abandoned worker neither throws nor hangs.</summary>
    [Fact]
    public async Task Dispose_AfterAbandon_DoesNotThrow()
    {
        var worker = new StaWorkerThread("DO.Test.Worker#abandon-dispose", joinTimeout: Budget);

        worker.Abandon();
        worker.Dispose();

        await worker.Completion.WaitAsync(Budget);
    }

    /// <summary>
    /// M2: a work item that shuts the worker's own dispatcher down ends the pump without Abandon/Dispose. From
    /// that moment on the worker refuses work (even before its thread has left the pump), the thread exits, an
    /// item queued behind the shutdown never runs, and later posts are still refused.
    /// </summary>
    [Fact]
    public async Task TryPost_AfterWorkItemShutsDispatcherDown_ReturnsFalseAndThreadExits()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#self-shutdown", joinTimeout: Budget);
        using var shutDown = new ManualResetEventSlim();
        using var queuedBehind = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool? postFromInsideAfterShutdown = null;
        var queuedItemRan = false;

        Assert.True(worker.TryPost(() =>
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            postFromInsideAfterShutdown = worker.TryPost(() => { });
            shutDown.Set();

            // Hold the pump until the test has queued another item behind this one.
            Assert.True(release.Wait(Budget));
        }));
        Assert.True(shutDown.Wait(Budget));

        var postedBehind = worker.TryPost(() => queuedItemRan = true);
        release.Set();

        await worker.Completion.WaitAsync(Budget);
        Assert.False(postFromInsideAfterShutdown);
        Assert.False(postedBehind);
        Assert.False(queuedItemRan);
        Assert.False(worker.TryPost(() => { }));
    }

    /// <summary>
    /// M2: a work item that makes <see cref="Dispatcher.Run"/> return WITHOUT a shutdown (all frames exited,
    /// like a stray <c>WM_QUIT</c>) still leaves a dead worker behind: the thread exits, an item queued before the
    /// pump ended is discarded, and <c>TryPost</c> returns <see langword="false"/> instead of handing work to a
    /// dispatcher that will never run it.
    /// </summary>
    [Fact]
    public async Task TryPost_AfterPumpExitsWithoutShutdown_ReturnsFalse()
    {
        using var worker = new StaWorkerThread("DO.Test.Worker#exit-frames", joinTimeout: Budget);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queuedItemRan = false;

        Assert.True(worker.TryPost(() =>
        {
            entered.Set();
            Assert.True(release.Wait(Budget));
            Dispatcher.ExitAllFrames();
        }));
        Assert.True(entered.Wait(Budget));
        Assert.True(worker.TryPost(() => queuedItemRan = true)); // accepted while the pump was still alive
        release.Set();

        await worker.Completion.WaitAsync(Budget);
        Assert.False(queuedItemRan);
        Assert.False(worker.TryPost(() => { }));
        Assert.False(worker.IsAbandoned);
    }
}
