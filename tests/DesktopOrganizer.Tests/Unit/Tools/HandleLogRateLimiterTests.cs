namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="HandleLogRateLimiter"/> (release-hardening review L11): one admitted log entry
/// per handle per interval, a count of what was suppressed, and bounded memory.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class HandleLogRateLimiterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly IntPtr HwndA = new(0x1111);
    private static readonly IntPtr HwndB = new(0x2222);

    private readonly ManualTimeProvider _time = new();
    private readonly HandleLogRateLimiter _sut;

    /// <summary>
    /// Initializes a new instance of the <see cref="HandleLogRateLimiterTests"/> class.
    /// </summary>
    public HandleLogRateLimiterTests()
    {
        _sut = new HandleLogRateLimiter(Interval, _time);
    }

    /// <summary>The first failure for a handle is admitted; the rest within the interval are not.</summary>
    [Fact]
    public void TryAdmit_RepeatedWithinInterval_AdmitsOnlyFirst()
    {
        var admitted = Enumerable.Range(0, 100).Count(_ => _sut.TryAdmit(HwndA, out _));

        Assert.Equal(1, admitted);
    }

    /// <summary>Once the interval has elapsed the next failure is admitted and reports what was suppressed.</summary>
    [Fact]
    public void TryAdmit_AfterInterval_AdmitsAndReportsSuppressedCount()
    {
        Assert.True(_sut.TryAdmit(HwndA, out var first));
        Assert.Equal(0, first);
        for (var i = 0; i < 5; i++) Assert.False(_sut.TryAdmit(HwndA, out _));

        _time.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.False(_sut.TryAdmit(HwndA, out _));

        _time.Advance(TimeSpan.FromTicks(1));
        Assert.True(_sut.TryAdmit(HwndA, out var suppressed));
        Assert.Equal(6, suppressed);
    }

    /// <summary>Handles are limited independently.</summary>
    [Fact]
    public void TryAdmit_DifferentHandles_AreIndependent()
    {
        Assert.True(_sut.TryAdmit(HwndA, out _));
        Assert.True(_sut.TryAdmit(HwndB, out _));
        Assert.False(_sut.TryAdmit(HwndA, out _));
        Assert.False(_sut.TryAdmit(HwndB, out _));
    }

    /// <summary>
    /// A flood of distinct handles never grows the table without bound: once full, new handles are still
    /// admitted (the table is pruned/cleared rather than refusing to log).
    /// </summary>
    [Fact]
    public void TryAdmit_ManyDistinctHandles_StaysAdmittingWhenTableIsFull()
    {
        for (var i = 1; i <= HandleLogRateLimiter.MaxTrackedHandles * 3; i++)
            Assert.True(_sut.TryAdmit(new IntPtr(i), out _));
    }
}
