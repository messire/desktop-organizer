using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools.IconLoading;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>
/// <see cref="StaIconLoadQueue"/> driven by REAL <see cref="StaWorkerThread"/>s (ADR-046,
/// docs/PLANS/background-icon-loading.md §8.1 "Threading", §8.4 row 2): a blocking mock
/// <see cref="IIconProvider"/> stands in for a hung shell handler or network share. Time only moves through
/// <see cref="ManualTimeProvider"/> (the watchdog never fires on its own); every wait is a signal with a
/// 10 s budget — nothing asserts on elapsed wall-clock time
/// (<c>docs/Refactoring/Testing/sta-test-conventions.md</c>).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class StaIconLoadQueueThreadingTests : IDisposable
{
    private const string RemoteRoot = @"\\srv\share";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly Mock<IIconProvider> _iconProvider = new();
    private readonly Mock<IPathProbe> _pathProbe = new();
    private readonly Mock<IRemotePathClassifier> _classifier = new();
    private readonly Mock<IRemoteRootCircuitBreaker> _breaker = new();
    private readonly Mock<IDiagnosticsLog> _diagnosticsLog = new();
    private readonly ManualTimeProvider _time = new();
    private readonly CollectingSynchronizationContext _context = new();
    private readonly List<IconLoadResult> _delivered = []; // filled on the test thread only (Pump)
    private readonly RecordingWorkerFactory _workers = new();
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int ThreadId, ApartmentState Apartment)> _getIconCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StaIconLoadQueue> _queues = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="StaIconLoadQueueThreadingTests"/> class: every path is a
    /// local, existing file with a cached type icon (no type jobs), except paths under <see cref="RemoteRoot"/>,
    /// which classify as remote; <c>GetIcon</c> blocks for any path a test registered a <see cref="Gate"/> for.
    /// </summary>
    public StaIconLoadQueueThreadingTests()
    {
        var frozen = CreateFrozenIcon();
        _pathProbe.Setup(p => p.Probe(It.IsAny<string>())).Returns((string path) => new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true));
        _classifier
            .Setup(c => c.GetOwnRemoteRoot(It.IsAny<string>()))
            .Returns((string path) => path.StartsWith(RemoteRoot, StringComparison.OrdinalIgnoreCase) ? RemoteRoot : null);
        _classifier.Setup(c => c.GetShortcutTargetRemoteRoot(It.IsAny<string>())).Returns((string?)null);
        _breaker.Setup(b => b.IsOpen(It.IsAny<string>())).Returns(false);
        _breaker.Setup(b => b.BeginCall(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        _diagnosticsLog.SetupGet(l => l.IsEnabled).Returns(true);
        _iconProvider.Setup(p => p.TryPeekTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns(frozen);
        _iconProvider
            .Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string path, int _) =>
            {
                _getIconCalls[path] = (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState());
                if (_gates.TryGetValue(path, out var gate))
                {
                    gate.Entered.Set();
                    gate.Release.Wait(Budget + Budget);
                }

                return frozen;
            });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Never leave a worker parked on a gate: release everything, then tear the queues down.
        foreach (var gate in _gates.Values) gate.Release.Set();
        foreach (var queue in _queues) queue.Dispose();

        // Released/abandoned workers leave their pump on their own; wait for that (bounded) before disposing
        // the events a returning worker may still be touching.
        foreach (var worker in _workers.Created) _ = worker.Completion.Wait(Budget);
        foreach (var gate in _gates.Values) gate.Dispose();
    }

    /// <summary><c>GetIcon</c> runs on a dedicated STA worker (not the test thread); the result comes back through the delivery context.</summary>
    [Fact]
    public void Submit_GetIconRunsOnStaWorker_ResultPostedToDeliveryContext()
    {
        var group = CreateGroup(CreateQueue());

        group.Submit([new IconLoadRequest(@"C:\local\a.txt", 32)]);

        Assert.True(_context.PumpUntil(() => HasOutcome(@"C:\local\a.txt", IconLoadOutcome.Loaded), Budget));
        var call = _getIconCalls[@"C:\local\a.txt"];
        Assert.NotEqual(Environment.CurrentManagedThreadId, call.ThreadId);
        Assert.Equal(ApartmentState.STA, call.Apartment);
        Assert.Equal(_workers.Created[0].ManagedThreadId, call.ThreadId);
    }

    /// <summary>
    /// H1 regression (lost wakeup): a background thread rapidly submits many distinct jobs while the real
    /// STA worker thread concurrently drains them — deliberately racing the exact "lane about to go idle"
    /// boundary hundreds of times. Before the fix, a Submit landing between <c>DispatchNext</c> deciding
    /// there was no more work and <c>WorkerRunning</c> actually flipping to <see langword="false"/> would see
    /// <c>WorkerRunning</c> still <see langword="true"/>, skip <c>EnsureLaneRunning</c>, and leave that job
    /// stuck forever with nothing left to ever dispatch it. <see cref="CollectingSynchronizationContext.PumpUntil"/>'s
    /// bounded budget below turns "stuck forever" into a test failure instead of a hang.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ConcurrentSubmitsWhileDraining_NeverLeaveAJobStuck()
    {
        var group = CreateGroup(CreateQueue());
        var paths = Enumerable.Range(0, 300).Select(i => $@"C:\local\rapid{i}.txt").ToArray();

        var submitter = Task.Run(() =>
        {
            foreach (var path in paths) group.Submit([new IconLoadRequest(path, 32)]);
        });

        await submitter.WaitAsync(Budget);
        Assert.True(
            _context.PumpUntil(() => Array.TrueForAll(paths, p => HasOutcome(p, IconLoadOutcome.Loaded)), Budget),
            "at least one rapidly-submitted job was never delivered — possible lost wakeup (H1).");
    }

    /// <summary>
    /// Metric (§8.4 row 2): a job hung on a network share holds only the Remote lane — a local job submitted
    /// after it is delivered while the remote one is still stuck, and <c>Submit</c> itself never waits.
    /// </summary>
    [Fact]
    public void StuckRemoteJob_DoesNotBlockLocalJobs()
    {
        var group = CreateGroup(CreateQueue());
        var stuck = AddGate(RemoteRoot + @"\stuck.txt");

        group.Submit([new IconLoadRequest(RemoteRoot + @"\stuck.txt", 32), new IconLoadRequest(@"C:\local\b.txt", 32)]);
        Assert.True(stuck.Entered.Wait(Budget));

        group.Submit([new IconLoadRequest(@"C:\local\c.txt", 32)]); // returns although the Remote lane is hung

        Assert.True(_context.PumpUntil(
            () => HasOutcome(@"C:\local\b.txt", IconLoadOutcome.Loaded) && HasOutcome(@"C:\local\c.txt", IconLoadOutcome.Loaded),
            Budget));
        Assert.DoesNotContain(_delivered, r => r.Path == RemoteRoot + @"\stuck.txt");
        Assert.NotEqual(_getIconCalls[RemoteRoot + @"\stuck.txt"].ThreadId, _getIconCalls[@"C:\local\b.txt"].ThreadId);

        stuck.Release.Set();
        Assert.True(_context.PumpUntil(() => HasOutcome(RemoteRoot + @"\stuck.txt", IconLoadOutcome.Loaded), Budget));
    }

    /// <summary>
    /// Watchdog on a real worker (D-4): a local job stuck past <c>StuckJobTimeout</c> is resolved
    /// <see cref="IconLoadOutcome.Unavailable"/>, its worker abandoned (the caller is not blocked), the next job
    /// of the same group runs on a replacement worker; when the hung call finally returns, its late result is
    /// discarded and the abandoned thread exits by itself.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task StuckLocalJob_WatchdogAbandonsWorker_NextJobRunsOnReplacement_LateResultDiscarded()
    {
        var queue = CreateQueue();
        var group = CreateGroup(queue);
        var stuck = AddGate(@"C:\local\stuck.txt");

        group.Submit([new IconLoadRequest(@"C:\local\stuck.txt", 32), new IconLoadRequest(@"C:\local\next.txt", 32)]);
        Assert.True(stuck.Entered.Wait(Budget));

        _time.Advance(IconLoadQueueOptions.Default.StuckJobTimeout);
        queue.RunWatchdogPass();

        Assert.True(_context.PumpUntil(
            () => HasOutcome(@"C:\local\stuck.txt", IconLoadOutcome.Unavailable) && HasOutcome(@"C:\local\next.txt", IconLoadOutcome.Loaded),
            Budget));
        var abandoned = _workers.Created[0];
        var replacement = _workers.Created[1];
        Assert.True(abandoned.IsAbandoned);
        Assert.Equal(replacement.ManagedThreadId, _getIconCalls[@"C:\local\next.txt"].ThreadId);

        stuck.Release.Set();
        await abandoned.Completion.WaitAsync(Budget);

        _context.Pump();
        Assert.DoesNotContain(_delivered, r => r.Path == @"C:\local\stuck.txt" && r.Outcome == IconLoadOutcome.Loaded);
        VerifyLogged("IconWorkerAbandoned", Times.Once());
    }

    /// <summary>
    /// D-4 limit counts only abandoned threads that are STILL hung: once an abandoned worker's call returns, its
    /// slot is freed — with a limit of 1, a second hang after the first one recovered does not disable the lane.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AbandonedWorkerThatReturned_FreesItsSlot_LaneStaysEnabled()
    {
        var queue = CreateQueue(IconLoadQueueOptions.Default with { MaxAbandonedWorkersPerLane = 1 });
        var group = CreateGroup(queue);
        var first = AddGate(@"C:\local\first.txt");
        var second = AddGate(@"C:\local\second.txt");

        group.Submit([new IconLoadRequest(@"C:\local\first.txt", 32)]);
        Assert.True(first.Entered.Wait(Budget));
        _time.Advance(IconLoadQueueOptions.Default.StuckJobTimeout);
        queue.RunWatchdogPass();

        first.Release.Set();
        await _workers.Created[0].Completion.WaitAsync(Budget);

        group.Submit([new IconLoadRequest(@"C:\local\second.txt", 32)]);
        Assert.True(second.Entered.Wait(Budget));
        _time.Advance(IconLoadQueueOptions.Default.StuckJobTimeout);
        queue.RunWatchdogPass();

        group.Submit([new IconLoadRequest(@"C:\local\after.txt", 32)]);

        Assert.True(_context.PumpUntil(() => HasOutcome(@"C:\local\after.txt", IconLoadOutcome.Loaded), Budget));
        VerifyLogged("IconLaneDisabled", Times.Never());
        Assert.Equal(3, _workers.Created.Count);
    }

    /// <summary>
    /// D-4 limit with threads that never come back: exceeding <c>MaxAbandonedWorkersPerLane</c> disables the
    /// lane — its remaining jobs resolve <see cref="IconLoadOutcome.Unavailable"/> instead of spawning ever more
    /// threads.
    /// </summary>
    [Fact]
    public void AbandonedWorkersStillHung_ExceedLimit_LaneDisabled()
    {
        var queue = CreateQueue(IconLoadQueueOptions.Default with { MaxAbandonedWorkersPerLane = 1 });
        var group = CreateGroup(queue);
        var first = AddGate(@"C:\local\first.txt");
        var second = AddGate(@"C:\local\second.txt");

        group.Submit([
            new IconLoadRequest(@"C:\local\first.txt", 32),
            new IconLoadRequest(@"C:\local\second.txt", 32),
            new IconLoadRequest(@"C:\local\third.txt", 32),
        ]);
        Assert.True(first.Entered.Wait(Budget));
        _time.Advance(IconLoadQueueOptions.Default.StuckJobTimeout);
        queue.RunWatchdogPass();

        Assert.True(second.Entered.Wait(Budget));
        _time.Advance(IconLoadQueueOptions.Default.StuckJobTimeout);
        queue.RunWatchdogPass();

        // A disabled lane never "drains", so its Unavailable results leave the group buffer on the D-9 flush
        // timer — which runs on the manual clock.
        _time.Advance(IconLoadQueueOptions.Default.DeliveryFlushInterval);

        Assert.True(_context.PumpUntil(() => HasOutcome(@"C:\local\third.txt", IconLoadOutcome.Unavailable), Budget));
        VerifyLogged("IconLaneDisabled", Times.Once());
        Assert.Equal(2, _workers.Created.Count);
        Assert.False(_getIconCalls.ContainsKey(@"C:\local\third.txt"));
    }

    /// <summary>
    /// C-4 end to end: an icon built by the real <see cref="IconFactory"/> ON the STA worker arrives frozen
    /// and is readable from the (different) test thread.
    /// </summary>
    [Fact]
    public void IconCreatedOnWorker_IsFrozenAndReadableFromTestThread()
    {
        var factory = new IconFactory();
        _iconProvider
            .Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(() => factory.CreateBitmap(new NativeBitmapPixels(48, 48, CreateOpaquePixels(48, 48)), 32));
        var group = CreateGroup(CreateQueue());

        group.Submit([new IconLoadRequest(@"C:\local\real.png", 32)]);

        Assert.True(_context.PumpUntil(() => HasOutcome(@"C:\local\real.png", IconLoadOutcome.Loaded), Budget));
        var icon = Assert.IsAssignableFrom<BitmapSource>(_delivered.Single(r => r.Path == @"C:\local\real.png").Icon);
        Assert.True(icon.IsFrozen);
        Assert.Equal(32, icon.PixelWidth); // cross-thread read: throws if the bitmap were still thread-affine
    }

    /// <summary>Disposing the queue shuts its real worker threads down (D-14).</summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Dispose_StopsWorkerThreads()
    {
        var queue = CreateQueue();
        var group = CreateGroup(queue);
        group.Submit([new IconLoadRequest(@"C:\local\a.txt", 32), new IconLoadRequest(RemoteRoot + @"\b.txt", 32)]);
        Assert.True(_context.PumpUntil(
            () => HasOutcome(@"C:\local\a.txt", IconLoadOutcome.Loaded) && HasOutcome(RemoteRoot + @"\b.txt", IconLoadOutcome.Loaded),
            Budget));

        queue.Dispose();

        Assert.Equal(2, _workers.Created.Count);
        foreach (var worker in _workers.Created) await worker.Completion.WaitAsync(Budget);
    }

    private static ImageSource CreateFrozenIcon()
    {
        var icon = new DrawingImage();
        icon.Freeze();
        return icon;
    }

    private static byte[] CreateOpaquePixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xC0;
            pixels[i + 3] = 0xFF;
        }

        return pixels;
    }

    private StaIconLoadQueue CreateQueue(IconLoadQueueOptions? options = null)
    {
        var queue = new StaIconLoadQueue(
            _iconProvider.Object,
            _pathProbe.Object,
            _classifier.Object,
            _breaker.Object,
            _workers,
            _diagnosticsLog.Object,
            options ?? IconLoadQueueOptions.Default,
            _time);
        _queues.Add(queue);
        return queue;
    }

    private IIconLoadGroup CreateGroup(StaIconLoadQueue queue)
        => queue.CreateGroup(IconLoadPriority.Visible, _context, results => _delivered.AddRange(results));

    private Gate AddGate(string path)
    {
        var gate = new Gate();
        _gates[path] = gate;
        return gate;
    }

    private bool HasOutcome(string path, IconLoadOutcome outcome)
        => _delivered.Exists(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.Outcome == outcome);

    private void VerifyLogged(string eventName, Times times)
        => _diagnosticsLog.Verify(
            l => l.Write(It.IsAny<string>(), eventName, It.IsAny<string?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            times);

    /// <summary>A hung native call: <see cref="Entered"/> is set once the worker is inside, <see cref="Release"/> lets it return.</summary>
    private sealed class Gate : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    /// <summary>Real <see cref="StaWorkerThread"/>s, remembered in creation order so tests can observe abandon/exit.</summary>
    private sealed class RecordingWorkerFactory : IStaWorkerFactory
    {
        private readonly object _gate = new();
        private readonly List<StaWorkerThread> _created = [];

        public IReadOnlyList<StaWorkerThread> Created
        {
            get
            {
                lock (_gate) return [.. _created];
            }
        }

        public IStaWorker Create(string name)
        {
            var worker = new StaWorkerThread(name, joinTimeout: Budget);
            lock (_gate) _created.Add(worker);
            return worker;
        }
    }
}
