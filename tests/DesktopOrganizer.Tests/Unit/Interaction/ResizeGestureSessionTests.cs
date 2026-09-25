using System.Windows;
using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Unit tests for <see cref="ResizeGestureSession"/> — the explicit, idempotent lifecycle state
/// machine of one resize gesture (docs/PLANS/phase-1-resize-stabilization.md §2.6/§5.3, commit C9).
/// This is where the DoD points "<c>LostMouseCapture</c> ALWAYS leads to Idle" and "100 consecutive
/// resizes without hanging" are formalized as unit tests. No WPF/STA dependency — the session is pure
/// C# state over a <see cref="PanelInteractionTracker"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGestureSessionTests
{
    private static readonly Rect StartBounds = new(100, 200, 300, 250);

    private static readonly ResizeConstraints FreeConstraints = new(
        MinWidth: 120d,
        MinHeight: 120d,
        SnapCellWidth: 0d,
        SnapCellHeight: 0d,
        TitleBarHeight: 32d,
        HorizontalChrome: 0d,
        VerticalChrome: 4d);

    private static readonly ResizeEndReason[] AllEndReasons =
    [
        ResizeEndReason.Completed,
        ResizeEndReason.Canceled,
        ResizeEndReason.CaptureLost,
        ResizeEndReason.SourceChanged,
        ResizeEndReason.Unloaded,
        ResizeEndReason.Closing,
        ResizeEndReason.Failed,
    ];

    // T-SES-01: TryBegin from Idle -> true, tracker claims Resizing.

    /// <summary>Verifies that <see cref="ResizeGestureSession.TryBegin"/> from Idle succeeds and claims <see cref="PanelInteractionState.Resizing"/> on the tracker.</summary>
    [Fact]
    public void TryBegin_FromIdle_SucceedsAndClaimsResizingOnTracker()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);

        var result = sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints);

        Assert.True(result);
        Assert.True(sut.IsActive);
        Assert.Equal(PanelInteractionState.Resizing, tracker.State);
        Assert.Equal(ResizeEdge.Right, sut.ActiveEdge);
        Assert.Equal(StartBounds, sut.StartBounds);
        Assert.Equal(StartBounds.Right, sut.AnchorRight);
        Assert.Equal(StartBounds.Bottom, sut.AnchorBottom);
    }

    // T-SES-02: double TryBegin -> second false, first gesture untouched.

    /// <summary>Verifies that calling <see cref="ResizeGestureSession.TryBegin"/> twice fails the second time and leaves the first gesture untouched.</summary>
    [Fact]
    public void TryBegin_CalledTwice_SecondFailsAndFirstGestureUntouched()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));

        var second = sut.TryBegin(ResizeEdge.Bottom, new Rect(0, 0, 999, 999), FreeConstraints);

        Assert.False(second);
        Assert.True(sut.IsActive);
        Assert.Equal(ResizeEdge.Right, sut.ActiveEdge);
        Assert.Equal(StartBounds, sut.StartBounds);
    }

    // T-SES-03: TryBegin while the tracker already owns Dragging -> false.

    /// <summary>Verifies that <see cref="ResizeGestureSession.TryBegin"/> fails while the tracker already owns <see cref="PanelInteractionState.Dragging"/>.</summary>
    [Fact]
    public void TryBegin_WhileTrackerIsDragging_Fails()
    {
        var tracker = new PanelInteractionTracker();
        Assert.True(tracker.TryEnter(PanelInteractionState.Dragging));
        var sut = new ResizeGestureSession(tracker);

        var result = sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints);

        Assert.False(result);
        Assert.False(sut.IsActive);
        Assert.Equal(PanelInteractionState.Dragging, tracker.State);
    }

    // T-SES-04: TryUpdate before Begin -> false, `applied` echoes `current` unchanged.

    /// <summary>Verifies that calling <see cref="ResizeGestureSession.TryUpdate"/> before <see cref="ResizeGestureSession.TryBegin"/> fails and echoes <c>current</c> unchanged.</summary>
    [Fact]
    public void TryUpdate_BeforeBegin_FailsAndEchoesCurrent()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        var current = new Rect(1, 2, 3, 4);

        var result = sut.TryUpdate(ResizeEdge.Right, new Vector(10, 0), current, out var applied);

        Assert.False(result);
        Assert.Equal(current, applied);
    }

    // T-SES-05: TryUpdate with a different edge than TryBegin -> false (unexpected source change).

    /// <summary>Verifies that calling <see cref="ResizeGestureSession.TryUpdate"/> with a different edge than the one passed to <see cref="ResizeGestureSession.TryBegin"/> fails, without ending the gesture.</summary>
    [Fact]
    public void TryUpdate_WithUnexpectedEdge_Fails()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));

        var result = sut.TryUpdate(ResizeEdge.Bottom, new Vector(0, 10), StartBounds, out var applied);

        Assert.False(result);
        Assert.Equal(StartBounds, applied);
        Assert.True(sut.IsActive); // TryUpdate itself does not end the gesture — the caller must call End
    }

    // T-SES-06: End(Completed) after N deltas -> ShouldPersist = true, tracker back to Idle.

    /// <summary>Verifies that ending with <see cref="ResizeEndReason.Completed"/> after applying deltas sets <c>ShouldPersist</c> and returns the tracker to Idle.</summary>
    [Fact]
    public void End_Completed_AfterDeltas_PersistsAndReturnsTrackerToIdle()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));
        Assert.True(sut.TryUpdate(ResizeEdge.Right, new Vector(10, 0), StartBounds, out _));

        var outcome = sut.End(ResizeEndReason.Completed);

        Assert.True(outcome.WasActive);
        Assert.True(outcome.ShouldPersist);
        Assert.Equal(1, outcome.AppliedDeltaCount);
        Assert.Equal(ResizeEdge.Right, outcome.Edge);
        Assert.Equal(ResizeEndReason.Completed, outcome.Reason);
        Assert.False(sut.IsActive);
        Assert.Equal(PanelInteractionState.Idle, tracker.State);
    }

    // T-SES-07: End(Canceled) after N deltas -> ShouldPersist = true (WPF does not roll back geometry
    // already applied to the window).

    /// <summary>Verifies that ending with <see cref="ResizeEndReason.Canceled"/> after applying deltas still sets <c>ShouldPersist</c>, since WPF does not roll back geometry already applied to the window.</summary>
    [Fact]
    public void End_Canceled_AfterDeltas_StillPersists()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));
        Assert.True(sut.TryUpdate(ResizeEdge.Right, new Vector(10, 0), StartBounds, out _));

        var outcome = sut.End(ResizeEndReason.Canceled);

        Assert.True(outcome.ShouldPersist);
    }

    // T-SES-08: End(CaptureLost) with ZERO deltas -> ShouldPersist = false.

    /// <summary>Verifies that ending with <see cref="ResizeEndReason.CaptureLost"/> after zero deltas leaves <c>ShouldPersist</c> false.</summary>
    [Fact]
    public void End_CaptureLost_WithoutAnyDelta_DoesNotPersist()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));

        var outcome = sut.End(ResizeEndReason.CaptureLost);

        Assert.False(outcome.ShouldPersist);
        Assert.Equal(0, outcome.AppliedDeltaCount);
    }

    // T-SES-09: repeated End -> second call WasActive = false (idempotency).

    /// <summary>Verifies that calling <see cref="ResizeGestureSession.End"/> twice makes the second call a no-op (<c>WasActive</c> false), formalizing idempotency.</summary>
    [Fact]
    public void End_CalledTwice_SecondCallIsNoOp()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));

        var first = sut.End(ResizeEndReason.Completed);
        var second = sut.End(ResizeEndReason.Completed);

        Assert.True(first.WasActive);
        Assert.False(second.WasActive);
        Assert.False(second.ShouldPersist);
    }

    // T-SES-10: End without a preceding Begin -> WasActive = false, no exception.

    /// <summary>Verifies that calling <see cref="ResizeGestureSession.End"/> without a preceding <see cref="ResizeGestureSession.TryBegin"/> is a no-op and does not throw.</summary>
    [Fact]
    public void End_WithoutBegin_IsNoOpAndDoesNotThrow()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);

        var outcome = sut.End(ResizeEndReason.Unloaded);

        Assert.False(outcome.WasActive);
        Assert.False(outcome.ShouldPersist);
        Assert.Equal(PanelInteractionState.Idle, tracker.State);
    }

    // T-SES-11: repeated/sequential End calls (idempotency, NOT true re-entrancy — see T-SES-19 below
    // for that) — the second call happens strictly AFTER the first has already returned. This is really
    // a second copy of T-SES-09's idempotency guarantee (code-quality-reviewer minor m-1); kept as-is to
    // avoid removing existing coverage, but T-SES-19 is the test that actually exercises the `_ending`
    // reentrancy guard from inside the first call's own unwind.

    /// <summary>Verifies that a sequential (not true re-entrant) second call to <see cref="ResizeGestureSession.End"/> does not double-release the tracker and leaves it Idle.</summary>
    [Fact]
    public void End_Reentrant_DoesNotDoubleReleaseTrackerAndLeavesIdle()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));

        var reentrantOutcomes = new List<ResizeEndOutcome>();
        var stateAtReentrantCall = default(PanelInteractionState?);

        // Simulate a caller who, upon observing WasActive from the first End, immediately calls End
        // again before the first call's own cleanup (ReleaseCaptureIfOurs/persistence in the real
        // controller) has finished — the scenario the `_ending` guard exists for.
        var outcome = sut.End(ResizeEndReason.Completed);
        stateAtReentrantCall = tracker.State;
        reentrantOutcomes.Add(sut.End(ResizeEndReason.Completed));

        Assert.True(outcome.WasActive);
        Assert.All(reentrantOutcomes, o => Assert.False(o.WasActive));
        Assert.Equal(PanelInteractionState.Idle, stateAtReentrantCall);
        Assert.Equal(PanelInteractionState.Idle, tracker.State);
    }

    // T-SES-12: End(Failed) -> ShouldPersist = false even after deltas were applied (partially-applied
    // geometry must never be persisted).

    /// <summary>Verifies that ending with <see cref="ResizeEndReason.Failed"/> never sets <c>ShouldPersist</c>, even after deltas were applied.</summary>
    [Fact]
    public void End_Failed_NeverPersists_EvenAfterDeltas()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));
        Assert.True(sut.TryUpdate(ResizeEdge.Right, new Vector(10, 0), StartBounds, out _));
        Assert.True(sut.TryUpdate(ResizeEdge.Right, new Vector(10, 0), StartBounds, out _));

        var outcome = sut.End(ResizeEndReason.Failed);

        Assert.False(outcome.ShouldPersist);
        Assert.Equal(2, outcome.AppliedDeltaCount);
    }

    // T-SES-13..17: End(<reason>) for EVERY ResizeEndReason -> tracker is ALWAYS Idle afterwards — the
    // direct formalization of the DoD "LostMouseCapture ALWAYS leads to Idle".

    /// <summary>Verifies that ending for every possible <see cref="ResizeEndReason"/> always returns the tracker to <see cref="PanelInteractionState.Idle"/>.</summary>
    /// <param name="reason">The end reason under test.</param>
    [Theory]
    [InlineData(ResizeEndReason.Completed)]
    [InlineData(ResizeEndReason.Canceled)]
    [InlineData(ResizeEndReason.CaptureLost)]
    [InlineData(ResizeEndReason.SourceChanged)]
    [InlineData(ResizeEndReason.Unloaded)]
    [InlineData(ResizeEndReason.Closing)]
    [InlineData(ResizeEndReason.Failed)]
    public void End_ForEveryReason_TrackerAlwaysReturnsToIdle(ResizeEndReason reason)
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));

        sut.End(reason);

        Assert.Equal(PanelInteractionState.Idle, tracker.State);
        Assert.False(sut.IsActive);
    }

    // T-SES-18 (DoD "100 consecutive resizes without hanging"): 100 iterations of Begin -> 1..5
    // TryUpdate -> End(random reason from the full set); invariants after EVERY iteration:
    // tracker.State == Idle, session.IsActive == false, the NEXT TryBegin returns true. Also sweeps
    // every ResizeEdge. Deterministic seed so the test is reproducible.

    /// <summary>Verifies the DoD "100 consecutive resizes without hanging": 100 randomized begin/update/end cycles across every edge always end Idle and always allow the next <c>TryBegin</c>.</summary>
    [Fact]
    public void HundredConsecutiveGestures_AlwaysEndIdleAndAlwaysAllowTheNextBegin()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        var random = new Random(Seed: 20260918);
        var edges = Enum.GetValues<ResizeEdge>();

        for (var i = 0; i < 100; i++)
        {
            var edge = edges[i % edges.Length]; // guarantees every edge is exercised at least once across 100 iterations (8 edges)
            var started = sut.TryBegin(edge, StartBounds, FreeConstraints);
            Assert.True(started, $"iteration {i}: TryBegin must always succeed after the previous gesture ended");

            var deltaCount = random.Next(1, 6); // 1..5 inclusive
            for (var d = 0; d < deltaCount; d++)
            {
                var delta = new Vector(random.Next(-20, 21), random.Next(-20, 21));
                var updated = sut.TryUpdate(edge, delta, StartBounds, out _);
                Assert.True(updated, $"iteration {i}, delta {d}: TryUpdate must succeed for the edge TryBegin was called with");
            }

            var reason = AllEndReasons[random.Next(AllEndReasons.Length)];
            var outcome = sut.End(reason);

            Assert.True(outcome.WasActive, $"iteration {i}: End must observe the gesture as active");
            Assert.False(sut.IsActive, $"iteration {i}: session must not be active after End");
            Assert.Equal(PanelInteractionState.Idle, tracker.State);
        }
    }

    // T-SES-19 (M-3-class regression, targeted same-edge back-to-back coverage): T-SES-18's 100-iteration
    // fuzz sweeps `edges[i % edges.Length]` (8 edges), so no two CONSECUTIVE iterations ever land on the
    // SAME edge — it never actually exercises "End on edge A, then immediately TryBegin on that same
    // edge A again", which is the exact shape of the M-3 stale-drag-recovery scenario (the same Thumb
    // being re-clicked). At the ResizeGestureSession level this is expected to be trivially true — the
    // session keeps no "this edge was just used" state; only PanelResizeController's _activeThumb needed
    // the M-3 fix (nulling it before EndResize in the self-heal branch) precisely because the session
    // itself never blocks same-edge reuse. This test makes that guarantee explicit instead of leaving it
    // as an untested implication of T-SES-01/T-SES-18.

    /// <summary>Verifies that ending a gesture on a given edge and immediately beginning again on that SAME edge always succeeds with fresh state, matching the M-3 stale-drag-recovery scenario.</summary>
    [Fact]
    public void SameEdge_EndThenImmediateTryBegin_AlwaysSucceedsWithFreshState()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        const ResizeEdge edge = ResizeEdge.BottomRight;

        for (var i = 0; i < 50; i++)
        {
            var started = sut.TryBegin(edge, StartBounds, FreeConstraints);
            Assert.True(started, $"iteration {i}: TryBegin on edge {edge} must succeed immediately after the previous cycle's End on the SAME edge");
            Assert.Equal(edge, sut.ActiveEdge);

            Assert.True(sut.TryUpdate(edge, new Vector(5, 5), StartBounds, out _));

            var outcome = sut.End(ResizeEndReason.SourceChanged);

            Assert.True(outcome.WasActive, $"iteration {i}: End must observe the gesture as active");
            Assert.Equal(edge, outcome.Edge);
            Assert.True(outcome.AppliedDeltaCount == 1, $"iteration {i}: AppliedDeltaCount must not leak from the previous same-edge cycle");
            Assert.False(sut.IsActive);
            Assert.Equal(PanelInteractionState.Idle, tracker.State);
        }
    }

    // T-SES-20 (TRUE re-entrancy, code-quality-reviewer minor m-1): unlike T-SES-09/T-SES-11 (which call
    // End() a second time only AFTER the first call has already returned to its caller — plain
    // idempotency), this test calls End() a second time FROM INSIDE the first call's own unwind: End's
    // `finally` block sets `_isActive = false` and then calls `_tracker.Exit(...)`, which synchronously
    // raises `PanelInteractionTracker.StateChanged` BEFORE the first End() call has actually returned to
    // PanelResizeController. Subscribing to StateChanged and calling `session.End(...)` again from inside
    // that handler reproduces the real shape of `Thumb.CancelDrag()` synchronously re-raising
    // `DragCompleted` into the same call stack as an in-progress `EndResize`. The `_ending` guard (and,
    // by then, `_isActive` already being false) must make the nested call a no-op: no recursion, no
    // double release of the tracker, no corruption of the outer call's own result.

    /// <summary>Verifies that a TRUE re-entrant call to <see cref="ResizeGestureSession.End"/>, made from inside the tracker's own <c>StateChanged</c> handler while the outer call is still unwinding, is safely rejected as a no-op without corrupting the outer call's result.</summary>
    [Fact]
    public void End_TrueReentrant_CalledFromInsideTrackerStateChangedHandler_IsSafeAndConsistent()
    {
        var tracker = new PanelInteractionTracker();
        var sut = new ResizeGestureSession(tracker);
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));
        Assert.True(sut.TryUpdate(ResizeEdge.Right, new Vector(10, 0), StartBounds, out _));

        var handlerInvocations = 0;
        ResizeEndOutcome? reentrantOutcome = null;
        PanelInteractionState? reentrantTrackerStateBeforeCall = null;

        // Subscribed BEFORE End() is called, so the handler fires synchronously from inside the outer
        // End()'s own `finally` block (via `_tracker.Exit`) — genuinely nested inside the outer call,
        // not sequenced after it.
        tracker.StateChanged += (_, _) =>
        {
            handlerInvocations++;
            reentrantTrackerStateBeforeCall = tracker.State;
            reentrantOutcome = sut.End(ResizeEndReason.Completed);
        };

        var outerOutcome = sut.End(ResizeEndReason.Completed);

        // The outer call's own result must reflect the gesture as it stood before the nested call ran —
        // the nested call must not have been able to mutate/reuse state the outer call was still
        // finishing up with.
        Assert.True(outerOutcome.WasActive);
        Assert.True(outerOutcome.ShouldPersist);
        Assert.Equal(1, outerOutcome.AppliedDeltaCount);
        Assert.Equal(ResizeEdge.Right, outerOutcome.Edge);

        // The nested call observed the tracker already Idle (the outer End had just set it) and must
        // have been rejected as a no-op — proving it did not recurse into a second real End cycle.
        Assert.Equal(1, handlerInvocations);
        Assert.Equal(PanelInteractionState.Idle, reentrantTrackerStateBeforeCall);
        Assert.NotNull(reentrantOutcome);
        Assert.False(reentrantOutcome!.Value.WasActive);
        Assert.False(reentrantOutcome.Value.ShouldPersist);

        // Final state is consistent, single source of truth: no lingering active session, tracker Idle.
        Assert.False(sut.IsActive);
        Assert.Equal(PanelInteractionState.Idle, tracker.State);

        // And the session must still accept a brand-new gesture afterwards — the reentrant call must not
        // have left any residual state blocking future use.
        Assert.True(sut.TryBegin(ResizeEdge.Right, StartBounds, FreeConstraints));
    }
}
