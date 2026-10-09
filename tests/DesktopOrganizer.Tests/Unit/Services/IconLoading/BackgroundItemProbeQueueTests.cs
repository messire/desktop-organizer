using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools.IconLoading;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>
/// Unit tests for <see cref="BackgroundItemProbeQueue"/> (ADR-046 D-12, 7c): result order matches input
/// order, cancellation between paths surfaces <see cref="OperationCanceledException"/> rather than
/// faulting the batch, concurrent batches are bounded, and an unexpected <see cref="IPathProbe"/> failure
/// degrades a single path to <c>Unknown</c> instead of faulting the whole call.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class BackgroundItemProbeQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Mock<IPathProbe> _pathProbeMock = new();
    private readonly Mock<IDiagnosticsLog> _diagnosticsLogMock = new();

    /// <summary>Initializes a new instance of the <see cref="BackgroundItemProbeQueueTests"/> class.</summary>
    public BackgroundItemProbeQueueTests()
    {
        _diagnosticsLogMock.SetupGet(l => l.IsEnabled).Returns(false);
    }

    /// <summary>An empty path list resolves immediately to an empty result, without ever calling the probe.</summary>
    [Fact]
    public async Task ProbeAsync_EmptyPaths_ReturnsEmptyWithoutProbing()
    {
        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object);

        var results = await sut.ProbeAsync([], CancellationToken.None);

        Assert.Empty(results);
        _pathProbeMock.Verify(p => p.Probe(It.IsAny<string>()), Times.Never);
    }

    /// <summary>Results are returned in exactly the same order as the input paths.</summary>
    [Fact]
    public async Task ProbeAsync_ResultOrder_MatchesPathOrder()
    {
        var paths = new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" };
        foreach (var path in paths)
        {
            _pathProbeMock.Setup(p => p.Probe(path)).Returns(new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true));
        }

        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object);

        var results = await sut.ProbeAsync(paths, CancellationToken.None);

        Assert.Equal(paths, results.Select(r => r.Path));
    }

    /// <summary>A batch runs entirely off the calling thread.</summary>
    [Fact]
    public async Task ProbeAsync_RunsOffTheCallingThread()
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        var probeThreadId = -1;
        _pathProbeMock.Setup(p => p.Probe(It.IsAny<string>())).Returns((string path) =>
        {
            probeThreadId = Environment.CurrentManagedThreadId;
            return new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true);
        });

        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object);

        await sut.ProbeAsync([@"C:\a.txt"], CancellationToken.None);

        Assert.NotEqual(callingThreadId, probeThreadId);
    }

    /// <summary>Cancelling between paths surfaces <see cref="OperationCanceledException"/> — the task does not fault with anything else.</summary>
    [Fact]
    public async Task ProbeAsync_CancelledBetweenPaths_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        using var firstProbed = new ManualResetEventSlim(initialState: false);

        _pathProbeMock.Setup(p => p.Probe(@"C:\a.txt")).Returns((string path) =>
        {
            cts.Cancel(); // cancel while "inside" the first path's probe, before the second path is reached.
            firstProbed.Set();
            return new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true);
        });
        _pathProbeMock.Setup(p => p.Probe(@"C:\b.txt")).Returns(new PathProbeResult(@"C:\b.txt", Exists: true, IsDirectory: false, IsKnown: true));

        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object);

        var task = sut.ProbeAsync([@"C:\a.txt", @"C:\b.txt"], cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.True(firstProbed.Wait(Timeout));
        _pathProbeMock.Verify(p => p.Probe(@"C:\b.txt"), Times.Never);
    }

    /// <summary>An exception <see cref="IPathProbe.Probe"/> is not documented to throw degrades that ONE path to Unknown rather than faulting the whole batch.</summary>
    [Fact]
    public async Task ProbeAsync_ProbeThrows_DegradesToUnknown_DoesNotFaultTheTask()
    {
        _pathProbeMock.Setup(p => p.Probe(@"C:\bad.txt")).Throws(new InvalidOperationException("boom"));
        _pathProbeMock.Setup(p => p.Probe(@"C:\good.txt")).Returns(new PathProbeResult(@"C:\good.txt", Exists: true, IsDirectory: false, IsKnown: true));

        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object);

        var results = await sut.ProbeAsync([@"C:\bad.txt", @"C:\good.txt"], CancellationToken.None);

        var bad = results[0];
        Assert.False(bad.Exists);
        Assert.False(bad.IsDirectory);
        Assert.False(bad.IsKnown);

        var good = results[1];
        Assert.True(good.Exists);
        Assert.True(good.IsKnown);
    }

    /// <summary>
    /// No more than the configured concurrency cap of batches probe at once — a third concurrent call
    /// waits for a permit rather than running immediately, proven with signals (no wall-clock — C-7).
    /// </summary>
    [Fact]
    public async Task ProbeAsync_MoreThanMaxConcurrentBatches_ThirdWaitsForAPermit()
    {
        using var gateA = new ManualResetEventSlim(initialState: false);
        using var gateB = new ManualResetEventSlim(initialState: false);
        using var enteredA = new ManualResetEventSlim(initialState: false);
        using var enteredB = new ManualResetEventSlim(initialState: false);

        _pathProbeMock.Setup(p => p.Probe(@"C:\a.txt")).Returns((string path) =>
        {
            enteredA.Set();
            gateA.Wait(Timeout);
            return new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true);
        });
        _pathProbeMock.Setup(p => p.Probe(@"C:\b.txt")).Returns((string path) =>
        {
            enteredB.Set();
            gateB.Wait(Timeout);
            return new PathProbeResult(path, Exists: true, IsDirectory: false, IsKnown: true);
        });
        _pathProbeMock.Setup(p => p.Probe(@"C:\c.txt")).Returns(new PathProbeResult(@"C:\c.txt", Exists: true, IsDirectory: false, IsKnown: true));

        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object, maxConcurrentBatches: 2);

        var taskA = sut.ProbeAsync([@"C:\a.txt"], CancellationToken.None);
        var taskB = sut.ProbeAsync([@"C:\b.txt"], CancellationToken.None);
        Assert.True(enteredA.Wait(Timeout));
        Assert.True(enteredB.Wait(Timeout));

        var taskC = sut.ProbeAsync([@"C:\c.txt"], CancellationToken.None);

        // L-7 (review it.2, C-7 "no wall-clock tests"): no scheduler-yield/sleep needed at all — with zero
        // permits left (A and B both hold one, genuinely blocked on gateA/gateB), SemaphoreSlim.WaitAsync
        // synchronously returns an INCOMPLETE task the moment it is awaited, so ProbeAsync's async state
        // machine deterministically suspends right there and taskC is provably not completed the instant
        // this line runs — no race, no timing assumption, nothing to wait out.
        Assert.False(taskC.IsCompleted);

        gateA.Set();
        await taskA;

        // Releasing A frees a permit — C can now proceed even though B is still blocked.
        await taskC.WaitAsync(Timeout);

        gateB.Set();
        await taskB;
    }

    /// <summary>
    /// G4 (ADR-audit review cycle 1): a probe that throws for every path reaches the always-on app log only
    /// <see cref="AppLogEventThrottle.DefaultLimitPerEvent"/> times (each Error carries a stack trace), followed by
    /// exactly one suppression notice — every path still degrades to <c>Unknown</c>.
    /// </summary>
    /// <returns>A task that completes when the batch has been probed.</returns>
    [Fact]
    public async Task ProbeAsync_ProbeKeepsThrowing_AppLogErrorsAreThrottled()
    {
        var paths = Enumerable.Range(0, AppLogEventThrottle.DefaultLimitPerEvent + 4).Select(i => $@"C:\f{i}.txt").ToArray();
        _pathProbeMock.Setup(p => p.Probe(It.IsAny<string>())).Throws(new IOException("boom"));
        var appLog = new Mock<IAppLog>();
        var sut = new BackgroundItemProbeQueue(_pathProbeMock.Object, _diagnosticsLogMock.Object, appLog.Object);

        var results = await sut.ProbeAsync(paths, CancellationToken.None);

        Assert.All(results, r => Assert.False(r.IsKnown));
        appLog.Verify(
            l => l.Error("BackgroundItemProbeQueue", "ItemProbeFailed", It.IsAny<IOException>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Exactly(AppLogEventThrottle.DefaultLimitPerEvent));
        appLog.Verify(
            l => l.Warn(
                "BackgroundItemProbeQueue",
                AppLogEventThrottle.SuppressedEvent,
                It.Is<KeyValuePair<string, object?>[]>(d => d.Any(kv => kv.Key == "event" && Equals(kv.Value, "ItemProbeFailed")))),
            Times.Once);
    }
}
