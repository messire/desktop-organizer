using DesktopOrganizer.Services.Abstractions;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="DeferredFailureLog"/> (release-hardening review M6): failures recorded inside a
/// <c>WH_MOUSE_LL</c> callback are never logged synchronously, and a burst of them becomes one entry per
/// interval carrying an <c>occurrences</c> count.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DeferredFailureLogTests
{
    private const string Source = "Src";
    private const string Evt = "Evt";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Mock<IAppLog> _log = new();
    private readonly ManualTimeProvider _time = new();
    private readonly DeferredFailureLog _sut;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeferredFailureLogTests"/> class.
    /// </summary>
    public DeferredFailureLogTests()
    {
        _sut = new DeferredFailureLog(() => _log.Object, Source, Evt, Interval, _time);
    }

    private ManualTimeProvider.ManualTimer Timer => Assert.Single(_time.Timers);

    /// <summary>Recording never writes to the log on the calling (hook) thread.</summary>
    [Fact]
    public void Record_DoesNotLogSynchronously()
    {
        _sut.Record(new InvalidOperationException("boom"));

        VerifyErrors(Times.Never());
    }

    /// <summary>N failures in a burst → the timer is armed once and exactly one entry is written.</summary>
    [Fact]
    public void Record_ManyFailures_ProducesSingleEntryWithOccurrenceCount()
    {
        const int failures = 50;
        var last = new InvalidOperationException("last");

        for (var i = 0; i < failures - 1; i++) _sut.Record(new InvalidOperationException("n"));
        _sut.Record(last);

        _time.Advance(TimeSpan.Zero);

        Assert.Equal(1, Timer.ArmCount);
        Assert.Equal(TimeSpan.Zero, Timer.LastDueTime);
        _log.Verify(
            l => l.Error(Source, Evt, last, It.Is<KeyValuePair<string, object?>[]>(d => HasOccurrences(d, failures))),
            Times.Once);
        VerifyErrors(Times.Once());
    }

    /// <summary>
    /// After an entry was written, the next batch waits for the rest of the interval and is then written as
    /// one more entry.
    /// </summary>
    [Fact]
    public void Record_AfterFlush_NextBatchIsDelayedUntilIntervalElapsed()
    {
        _sut.Record(new InvalidOperationException("first"));
        _time.Advance(TimeSpan.Zero);
        VerifyErrors(Times.Once());

        _time.Advance(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 7; i++) _sut.Record(new InvalidOperationException("again"));

        Assert.Equal(2, Timer.ArmCount);
        Assert.Equal(TimeSpan.FromSeconds(40), Timer.LastDueTime);

        _time.Advance(TimeSpan.FromSeconds(39));
        VerifyErrors(Times.Once());

        _time.Advance(TimeSpan.FromSeconds(1));
        VerifyErrors(Times.Exactly(2));
        _log.Verify(
            l => l.Error(Source, Evt, It.IsAny<Exception?>(), It.Is<KeyValuePair<string, object?>[]>(d => HasOccurrences(d, 7))),
            Times.Once);
    }

    /// <summary>A flush with nothing pending writes nothing.</summary>
    [Fact]
    public void Flush_WithNothingPending_WritesNothing()
    {
        _sut.Flush();

        VerifyErrors(Times.Never());
    }

    /// <summary>Dispose writes a still-pending batch so it is not lost at shutdown, and disposes the timer.</summary>
    [Fact]
    public void Dispose_FlushesPendingBatchAndDisposesTimer()
    {
        _sut.Record(new InvalidOperationException("pending"));

        _sut.Dispose();

        VerifyErrors(Times.Once());
        Assert.True(Timer.IsDisposed);
    }

    /// <summary>Recording after dispose is a silent no-op — it can happen in a late hook callback.</summary>
    [Fact]
    public void Record_AfterDispose_DoesNotThrowOrLog()
    {
        _sut.Dispose();

        var ex = Record.Exception(() => _sut.Record(new InvalidOperationException("late")));
        _time.Advance(Interval);

        Assert.Null(ex);
        VerifyErrors(Times.Never());
    }

    /// <summary>A throwing log must not escape the flush (it runs on a pool thread).</summary>
    [Fact]
    public void Flush_WhenLogThrows_DoesNotPropagate()
    {
        _log
            .Setup(l => l.Error(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()))
            .Throws(new InvalidOperationException("disk full"));
        _sut.Record(new InvalidOperationException("boom"));

        var ex = Record.Exception(() => _time.Advance(TimeSpan.Zero));

        Assert.Null(ex);
    }

    private static bool HasOccurrences(KeyValuePair<string, object?>[] data, int expected)
        => data.Any(kv => kv.Key == "occurrences" && kv.Value is int n && n == expected);

    private void VerifyErrors(Times times)
        => _log.Verify(
            l => l.Error(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            times);
}
