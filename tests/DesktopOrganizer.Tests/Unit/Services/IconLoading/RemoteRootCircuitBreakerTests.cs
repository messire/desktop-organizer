using DesktopOrganizer.Services.Tools.IconLoading;

namespace DesktopOrganizer.Tests.Unit.Services.IconLoading;

/// <summary>Unit tests for <see cref="RemoteRootCircuitBreaker"/> (ADR-046 D-3).</summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class RemoteRootCircuitBreakerTests
{
    private const string Root = @"\\srv\share";
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Quarantine = TimeSpan.FromSeconds(60);

    private readonly ManualTimeProvider _time = new();
    private readonly RemoteRootCircuitBreaker _breaker;

    /// <summary>Initializes a new instance of the <see cref="RemoteRootCircuitBreakerTests"/> class.</summary>
    public RemoteRootCircuitBreakerTests() => _breaker = new RemoteRootCircuitBreaker(SlowThreshold, Quarantine, _time);

    /// <summary>A root nobody has ever called through is closed.</summary>
    [Fact]
    public void IsOpen_UnknownRoot_ReturnsFalse() => Assert.False(_breaker.IsOpen(Root));

    /// <summary>A call that completes in under the slow threshold never opens the root.</summary>
    [Fact]
    public void BeginCall_FastCompletion_KeepsRootClosed()
    {
        using (_breaker.BeginCall(Root))
        {
            _time.Advance(SlowThreshold - TimeSpan.FromMilliseconds(1));
        }

        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>A call that takes at least the slow threshold to complete opens the root for the quarantine period.</summary>
    [Fact]
    public void BeginCall_SlowCompletion_OpensRootForQuarantine()
    {
        using (_breaker.BeginCall(Root))
        {
            _time.Advance(SlowThreshold);
        }

        Assert.True(_breaker.IsOpen(Root));
    }

    /// <summary>While a call is still in flight and has already run at least the slow threshold, the root is open for OTHER concurrent callers too.</summary>
    [Fact]
    public void IsOpen_InFlightCallOlderThanSlowThreshold_OpensRootForConcurrentCallers()
    {
        using var scope = _breaker.BeginCall(Root);
        _time.Advance(SlowThreshold);

        Assert.True(_breaker.IsOpen(Root));

        // A second root, with its own fresh in-flight call, is unaffected.
        using var otherScope = _breaker.BeginCall(@"\\other\share");
        Assert.False(_breaker.IsOpen(@"\\other\share"));
    }

    /// <summary>A watchdog-reported stuck call opens the root immediately, for the full quarantine period, regardless of the slow threshold.</summary>
    [Fact]
    public void ReportStuck_OpensRootForQuarantine()
    {
        _breaker.ReportStuck(Root);

        Assert.True(_breaker.IsOpen(Root));
    }

    /// <summary>After the quarantine period elapses, the root is half-open: nobody is in flight, so the next caller may try.</summary>
    [Fact]
    public void IsOpen_AfterQuarantineElapses_ReturnsFalse()
    {
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>A fast call after quarantine expires keeps the root closed (does not re-open it).</summary>
    [Fact]
    public void BeginCall_FastCallAfterQuarantineExpires_RootStaysClosed()
    {
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        using (_breaker.BeginCall(Root))
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>
    /// M8: the call the watchdog abandoned never returns, yet the root is only open for the quarantine — the
    /// stuck call is dropped from in-flight tracking instead of keeping the root open forever.
    /// </summary>
    [Fact]
    public void ReportStuck_CallNeverReturns_RootOpenOnlyForQuarantine()
    {
        _ = _breaker.BeginCall(Root); // never disposed: the native call never returns
        _time.Advance(TimeSpan.FromSeconds(5));
        _breaker.ReportStuck(Root);

        _time.Advance(Quarantine - TimeSpan.FromMilliseconds(1));
        Assert.True(_breaker.IsOpen(Root));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>
    /// D-3 half-open: after the quarantine exactly one trial call is let through — while it is in flight every
    /// other caller still sees the root as open; the trial completing fast closes the root for good.
    /// </summary>
    [Fact]
    public void HalfOpen_SingleTrialInFlight_OthersSeeOpen_FastTrialClosesRoot()
    {
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);
        Assert.False(_breaker.IsOpen(Root));

        using (_breaker.BeginCall(Root))
        {
            Assert.True(_breaker.IsOpen(Root));
            _time.Advance(SlowThreshold - TimeSpan.FromMilliseconds(1));
        }

        Assert.False(_breaker.IsOpen(Root));

        // Closed again: concurrent fast calls no longer block each other.
        using var first = _breaker.BeginCall(Root);
        using var second = _breaker.BeginCall(Root);
        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>D-3 half-open: a slow trial re-opens the root for another full quarantine.</summary>
    [Fact]
    public void HalfOpen_SlowTrial_ReopensForFullQuarantine()
    {
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        using (_breaker.BeginCall(Root))
        {
            _time.Advance(SlowThreshold);
        }

        _time.Advance(Quarantine - TimeSpan.FromMilliseconds(1));
        Assert.True(_breaker.IsOpen(Root));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>
    /// D-3 half-open: a trial the watchdog abandons re-opens the root; after that quarantine the root is
    /// half-open again, and the next call is a fresh trial.
    /// </summary>
    [Fact]
    public void HalfOpen_StuckTrial_ReopensThenAllowsNewTrial()
    {
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        _ = _breaker.BeginCall(Root); // the trial; never returns
        _time.Advance(TimeSpan.FromSeconds(5));
        _breaker.ReportStuck(Root);
        Assert.True(_breaker.IsOpen(Root));

        _time.Advance(Quarantine);
        Assert.False(_breaker.IsOpen(Root));

        using (_breaker.BeginCall(Root))
        {
            Assert.True(_breaker.IsOpen(Root));
        }

        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>
    /// The abandoned call's late return (the stuck native call finally came back) is ignored: it neither
    /// re-opens the root nor counts as the half-open trial's outcome.
    /// </summary>
    [Fact]
    public void ReportStuck_LateDisposeOfAbandonedCall_IsIgnored()
    {
        var stuck = _breaker.BeginCall(Root);
        _time.Advance(TimeSpan.FromSeconds(5));
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        using (_breaker.BeginCall(Root))
        {
            stuck.Dispose(); // ran 65 s — would re-open the root if it still counted
            Assert.True(_breaker.IsOpen(Root)); // the trial is still in flight
        }

        Assert.False(_breaker.IsOpen(Root));
    }

    /// <summary>ReportStuck drops only the longest-running call; a younger concurrent call keeps being tracked.</summary>
    [Fact]
    public void ReportStuck_DropsOnlyOldestInFlightCall()
    {
        _ = _breaker.BeginCall(Root); // the stuck one; never returns
        _time.Advance(TimeSpan.FromSeconds(5));
        var younger = _breaker.BeginCall(Root);
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        // The younger call has now been in flight for the whole quarantine: still tracked, so still "slow in flight".
        Assert.True(_breaker.IsOpen(Root));

        younger.Dispose();
        Assert.True(_breaker.IsOpen(Root)); // it completed slowly: a fresh quarantine
    }

    /// <summary>Disposing a call scope twice reports completion once.</summary>
    [Fact]
    public void CallScope_DisposedTwice_ReportsOnce()
    {
        _breaker.ReportStuck(Root);
        _time.Advance(Quarantine);

        var trial = _breaker.BeginCall(Root);
        trial.Dispose(); // fast trial: closed
        _time.Advance(SlowThreshold);
        trial.Dispose(); // would re-open the root if counted again

        Assert.False(_breaker.IsOpen(Root));
    }
}
