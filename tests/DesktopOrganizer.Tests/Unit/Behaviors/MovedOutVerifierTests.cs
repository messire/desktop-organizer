using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="MovedOutVerifier"/> — the 0/500/2000&#160;ms existence-recheck backing an
/// external panel-item drag-out (ADR-036, release-hardening-1.0.md §E). A fake <see cref="IDebounceTimer"/>
/// drives every delayed check deterministically instead of racing a real one.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class MovedOutVerifierTests
{
    private const string Path = @"C:\PanelItems\abc\file.txt";

    /// <summary>
    /// The very first (0&#160;ms) check runs synchronously — a path already gone by the time
    /// <see cref="MovedOutVerifier.Schedule"/> is called never needs a timer at all.
    /// </summary>
    [Fact]
    public void Schedule_PathAlreadyGone_InvokesOnConfirmedMoved_WithoutStartingATimer()
    {
        var timer = new FakeDebounceTimer();
        var sut = new MovedOutVerifier(() => timer, _ => false);
        var confirmed = false;

        sut.Schedule(Path, () => confirmed = true);

        Assert.True(confirmed);
        Assert.Equal(0, timer.StartCount);
    }

    /// <summary>
    /// A path that survives every scheduled check (0/500/2000&#160;ms) stays in the panel —
    /// <c>onConfirmedMoved</c> is never invoked, and no further check is scheduled after the last one.
    /// </summary>
    [Fact]
    public void Schedule_PathStillExistsThroughEveryCheck_NeverInvokesOnConfirmedMoved()
    {
        var timer = new FakeDebounceTimer();
        var sut = new MovedOutVerifier(() => timer, _ => true);
        var confirmed = false;

        sut.Schedule(Path, () => confirmed = true);
        Assert.Equal(1, timer.StartCount); // T+0 ran synchronously (still exists) and armed the +500ms check.

        timer.FireLast(); // +500ms check (still exists) arms the final +1500ms (T+2000ms) check.
        Assert.Equal(2, timer.StartCount);

        timer.FireLast(); // final check (still exists) — no further check is armed.
        Assert.False(confirmed);
        Assert.Equal(2, timer.StartCount);
    }

    /// <summary>
    /// A path found gone on the second check (the +500&#160;ms recheck) invokes
    /// <c>onConfirmedMoved</c> right then — it does not wait for the third.
    /// </summary>
    [Fact]
    public void Schedule_PathGoneOnSecondCheck_InvokesOnConfirmedMoved()
    {
        var callCount = 0;
        var timer = new FakeDebounceTimer();
        var sut = new MovedOutVerifier(() => timer, _ =>
        {
            callCount++;
            return callCount < 2; // exists on the 1st check, gone by the 2nd.
        });
        var confirmed = false;

        sut.Schedule(Path, () => confirmed = true);
        Assert.False(confirmed); // 1st (T+0) check found it still there.

        timer.FireLast(); // 2nd (+500ms) check — now gone.

        Assert.True(confirmed);
        Assert.Equal(2, callCount);
    }

    /// <summary>
    /// A path found gone only on the third (final, +2000&#160;ms total) check still invokes
    /// <c>onConfirmedMoved</c> — every scheduled check ran and every one found it gone only at the end.
    /// </summary>
    [Fact]
    public void Schedule_PathGoneOnFinalCheck_InvokesOnConfirmedMoved()
    {
        var callCount = 0;
        var timer = new FakeDebounceTimer();
        var sut = new MovedOutVerifier(() => timer, _ =>
        {
            callCount++;
            return callCount < 3; // exists on the 1st and 2nd checks, gone by the 3rd.
        });
        var confirmed = false;

        sut.Schedule(Path, () => confirmed = true);
        timer.FireLast();
        Assert.False(confirmed);

        timer.FireLast();

        Assert.True(confirmed);
        Assert.Equal(3, callCount);
    }

    // ── M1 (release-hardening 1.0 review): overlapping Schedule() calls get independent timers ────────

    /// <summary>
    /// Regression for M1: two items dragged out in close succession must never share one
    /// <see cref="IDebounceTimer"/> — a shared timer's <see cref="IDebounceTimer.Start"/> call for the
    /// SECOND item's delayed check would (per <see cref="IDebounceTimer.Start"/>'s own "last Start wins"
    /// contract, exercised by <see cref="FakeDebounceTimer"/> below — deliberately NOT changed to a
    /// multi-callback fake, so it still faithfully mirrors the real <c>DispatcherDebounceTimer</c>) silently
    /// cancel the FIRST item's still-pending check. This test verifies each <see cref="MovedOutVerifier.Schedule"/>
    /// call is handed its own, independent <see cref="IDebounceTimer"/> instance (via a factory that hands
    /// out a fresh instance every call — the fix — rather than the single shared instance the OLD fake/
    /// production wiring used), so firing the FIRST item's own timer confirms it moved, regardless of
    /// whether the SECOND item's own check is still pending.
    /// </summary>
    [Fact]
    public void Schedule_TwoOverlappingSchedules_EachGetsItsOwnIndependentTimer()
    {
        var timers = new List<FakeDebounceTimer>();
        IDebounceTimer? Factory()
        {
            var t = new FakeDebounceTimer();
            timers.Add(t);
            return t;
        }

        var stillPresent = new HashSet<string> { "a.txt", "b.txt" };
        var aConfirmed = false;
        var bConfirmed = false;

        var sut = new MovedOutVerifier(Factory, p => stillPresent.Contains(p));

        // Both items exist at their own T+0 check, so both arm their own +500ms timer.
        sut.Schedule("a.txt", () => aConfirmed = true);
        sut.Schedule("b.txt", () => bConfirmed = true);

        Assert.Equal(2, timers.Count); // NOT 1 — each Schedule() call got its own timer instance.
        Assert.NotSame(timers[0], timers[1]);

        // "a.txt" actually left between scheduling and its own +500ms check.
        stillPresent.Remove("a.txt");

        // Firing ONLY the first item's timer must confirm it moved, without disturbing the second item's
        // own still-pending timer (the exact scenario a shared timer would have broken: this Start call,
        // on a shared instance, would have silently replaced/cancelled whichever callback armed second).
        timers[0].FireLast();

        Assert.True(aConfirmed);
        Assert.False(bConfirmed);

        // The second item's own timer is still independently live and unaffected.
        timers[1].FireLast(); // +500ms: still present -> arms its own final +1500ms check.
        Assert.False(bConfirmed);
        stillPresent.Remove("b.txt");
        timers[1].FireLast(); // final check: now gone.
        Assert.True(bConfirmed);
    }

    /// <summary>
    /// No <see cref="IDebounceTimer"/> available (e.g. no UI dispatcher) degrades to running every
    /// scheduled check immediately instead of silently dropping the verification.
    /// </summary>
    [Fact]
    public void Schedule_NoTimerAvailable_DegradesToImmediateChecksUntilConfirmed()
    {
        var callCount = 0;
        var sut = new MovedOutVerifier(() => null, _ =>
        {
            callCount++;
            return callCount < 3; // gone only on the 3rd (final) check.
        });
        var confirmed = false;

        sut.Schedule(Path, () => confirmed = true);

        Assert.True(confirmed);
        Assert.Equal(3, callCount);
    }

    /// <summary>
    /// <see cref="MovedOutVerifier.CancelAll"/> (M1) stops every currently in-flight
    /// <see cref="MovedOutVerifier.Schedule"/> chain's timer — used by <c>ChildWindow.OnClosing</c> so no
    /// delayed callback can fire against a panel that is about to be disposed. Verifies neither of two
    /// concurrently in-flight chains' <c>onConfirmedMoved</c> ever fires once cancelled, even though the
    /// watched path really did disappear.
    /// </summary>
    [Fact]
    public void CancelAll_StopsEveryInFlightChain_OnConfirmedMovedNeverFiresAfterward()
    {
        var timers = new List<FakeDebounceTimer>();
        IDebounceTimer? Factory()
        {
            var t = new FakeDebounceTimer();
            timers.Add(t);
            return t;
        }

        var aConfirmed = false;
        var bConfirmed = false;
        var sut = new MovedOutVerifier(Factory, _ => true); // both still exist at T+0 -> both arm a timer.

        sut.Schedule("a.txt", () => aConfirmed = true);
        sut.Schedule("b.txt", () => bConfirmed = true);
        Assert.Equal(2, timers.Count);

        sut.CancelAll();

        // Both timers had Stop() called — their armed callback is gone, so firing them is a no-op.
        timers[0].FireLast();
        timers[1].FireLast();

        Assert.False(aConfirmed);
        Assert.False(bConfirmed);
    }

    /// <summary>
    /// Minimal <see cref="IDebounceTimer"/> test double: mirrors the same fake used by
    /// <c>SaveSchedulerTests</c> — the real <see cref="DispatcherDebounceTimer"/>'s "last
    /// <see cref="IDebounceTimer.Start"/> wins" semantics, without any actual timing.
    /// </summary>
    private sealed class FakeDebounceTimer : IDebounceTimer
    {
        private Action? _callback;

        public int StartCount { get; private set; }

        public void Start(TimeSpan window, Action callback)
        {
            StartCount++;
            _callback = callback;
        }

        public void Stop() => _callback = null;

        /// <summary>Simulates the debounce window elapsing: invokes and clears the armed callback, if any.</summary>
        public void FireLast()
        {
            var callback = _callback;
            _callback = null;
            callback?.Invoke();
        }
    }
}
