using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PanelSelectionCoordinator"/> — the process-wide singleton that lets every
/// desktop panel's local selection stay mutually exclusive despite none of the panels ever receiving
/// normal window focus (ui-polishing-1 §2.4 Block A2).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSelectionCoordinatorTests
{
    /// <summary>
    /// Verifies that <see cref="PanelSelectionCoordinator.NotifySelected"/> raises
    /// <see cref="PanelSelectionCoordinator.SelectionClaimed"/> with the given owner.
    /// </summary>
    [Fact]
    public void NotifySelected_RaisesSelectionClaimed_WithTheGivenOwner()
    {
        var sut = new PanelSelectionCoordinator();
        var owner = new object();
        object? raisedOwner = null;
        sut.SelectionClaimed += (_, o) => raisedOwner = o;

        sut.NotifySelected(owner);

        Assert.Same(owner, raisedOwner);
    }

    /// <summary>
    /// Verifies that every subscriber to <see cref="PanelSelectionCoordinator.SelectionClaimed"/>
    /// receives a single <see cref="PanelSelectionCoordinator.NotifySelected"/> call.
    /// </summary>
    [Fact]
    public void NotifySelected_MultipleSubscribers_AllReceiveTheEvent()
    {
        var sut = new PanelSelectionCoordinator();
        var owner = new object();
        var received1 = false;
        var received2 = false;
        var received3 = false;
        sut.SelectionClaimed += (_, _) => received1 = true;
        sut.SelectionClaimed += (_, _) => received2 = true;
        sut.SelectionClaimed += (_, _) => received3 = true;

        sut.NotifySelected(owner);

        Assert.True(received1);
        Assert.True(received2);
        Assert.True(received3);
    }

    /// <summary>
    /// Verifies the coordinator-side reentrancy guard (risk R3): re-claiming a selection the same owner
    /// already holds is a no-op and never re-raises <see cref="PanelSelectionCoordinator.SelectionClaimed"/>.
    /// </summary>
    [Fact]
    public void NotifySelected_SameOwnerTwiceInARow_RaisesSelectionClaimedOnlyOnce()
    {
        // The coordinator-side half of the reentrancy guard (risk R3): re-claiming a selection the
        // same owner already holds is a no-op, so a subscriber that (incorrectly) called
        // NotifySelected again for the owner that's already current cannot re-trigger the event.
        var sut = new PanelSelectionCoordinator();
        var owner = new object();
        var raisedCount = 0;
        sut.SelectionClaimed += (_, _) => raisedCount++;

        sut.NotifySelected(owner);
        sut.NotifySelected(owner);
        sut.NotifySelected(owner);

        Assert.Equal(1, raisedCount);
    }

    /// <summary>
    /// Verifies that claiming distinct owners in sequence raises
    /// <see cref="PanelSelectionCoordinator.SelectionClaimed"/> once per claim, even when an earlier
    /// owner is re-claimed after another owner held the selection in between.
    /// </summary>
    [Fact]
    public void NotifySelected_DifferentOwners_RaisesSelectionClaimedEachTime()
    {
        var sut = new PanelSelectionCoordinator();
        var ownerA = new object();
        var ownerB = new object();
        var raisedCount = 0;
        sut.SelectionClaimed += (_, _) => raisedCount++;

        sut.NotifySelected(ownerA);
        sut.NotifySelected(ownerB);
        sut.NotifySelected(ownerA);

        Assert.Equal(3, raisedCount);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSelectionCoordinator.NotifySelected"/> throws
    /// <see cref="ArgumentNullException"/> for a <see langword="null"/> owner.
    /// </summary>
    [Fact]
    public void NotifySelected_NullOwner_Throws()
    {
        var sut = new PanelSelectionCoordinator();

        Assert.Throws<ArgumentNullException>(() => sut.NotifySelected(null!));
    }

    /// <summary>
    /// Verifies that <see cref="PanelSelectionCoordinator.ClearAll"/> raises
    /// <see cref="PanelSelectionCoordinator.SelectionClaimed"/> with a fresh sentinel owner that matches
    /// no real panel, so every subscriber deselects.
    /// </summary>
    [Fact]
    public void ClearAll_RaisesSelectionClaimed_WithAnOwnerThatMatchesNoSubscriber()
    {
        var sut = new PanelSelectionCoordinator();
        var self = new object();
        object? raisedOwner = null;
        sut.SelectionClaimed += (_, o) => raisedOwner = o;

        sut.ClearAll();

        Assert.NotNull(raisedOwner);
        Assert.NotSame(self, raisedOwner);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSelectionCoordinator.ClearAll"/> resets the current-owner state, so
    /// re-claiming the owner held before the clear raises
    /// <see cref="PanelSelectionCoordinator.SelectionClaimed"/> again instead of being swallowed by the
    /// idempotent-owner guard.
    /// </summary>
    [Fact]
    public void ClearAll_ThenNotifySelected_SameOwnerAsBeforeClearAll_RaisesSelectionClaimedAgain()
    {
        // ClearAll must reset the "current owner" state so a subsequent NotifySelected for the same
        // owner as before ClearAll is not swallowed by the idempotent-owner guard.
        var sut = new PanelSelectionCoordinator();
        var owner = new object();
        sut.NotifySelected(owner);

        sut.ClearAll();

        var raisedCount = 0;
        sut.SelectionClaimed += (_, _) => raisedCount++;
        sut.NotifySelected(owner);

        Assert.Equal(1, raisedCount);
    }

    /// <summary>
    /// Verifies that unsubscribing a handler from <see cref="PanelSelectionCoordinator.SelectionClaimed"/>
    /// stops it from receiving any further events.
    /// </summary>
    [Fact]
    public void Unsubscribe_StopsReceivingFurtherEvents()
    {
        var sut = new PanelSelectionCoordinator();
        var raisedCount = 0;
        void Handler(object? _, object o) => raisedCount++;

        sut.SelectionClaimed += Handler;
        sut.NotifySelected(new object());
        Assert.Equal(1, raisedCount);

        sut.SelectionClaimed -= Handler;
        sut.NotifySelected(new object());

        // Still 1 — the unsubscribed handler must not have run again.
        Assert.Equal(1, raisedCount);
    }

    /// <summary>
    /// Regression guard for risk R3: a subscriber that re-calls
    /// <see cref="PanelSelectionCoordinator.NotifySelected"/> for the very owner it was just told about
    /// (from within its own handler) must not send the coordinator into unbounded recursion — the
    /// idempotent-owner guard stops it regardless of subscriber behavior.
    /// </summary>
    [Fact]
    public void NotifySelected_SubscriberReClaimsSameOwnerFromWithinHandler_DoesNotRecurseInfinitely()
    {
        // Regression guard for risk R3: a (hypothetically buggy) subscriber that reacts to
        // SelectionClaimed by immediately re-calling NotifySelected for the very owner it was just
        // told about must not send the coordinator into unbounded recursion — the idempotent-owner
        // guard stops it at the coordinator level regardless of what any subscriber does.
        var sut = new PanelSelectionCoordinator();
        var owner = new object();
        var invocationCount = 0;

        sut.SelectionClaimed += (_, claimedOwner) =>
        {
            invocationCount++;
            if (invocationCount > 10) return; // safety net so a real bug fails fast instead of hanging
            sut.NotifySelected(claimedOwner);
        };

        var exception = Record.Exception(() => sut.NotifySelected(owner));

        Assert.Null(exception);
        Assert.Equal(1, invocationCount);
    }

    /// <summary>
    /// Verifies that a subscriber claiming a different owner from within its own handler (two panels
    /// "fighting" over the selection) still terminates, delivering both claims in order.
    /// </summary>
    [Fact]
    public void NotifySelected_SubscriberClaimsADifferentOwnerFromWithinHandler_TerminatesAndDeliversBoth()
    {
        // A subscriber reacting to one claim by immediately making a different claim (e.g. two panels
        // "fighting" over the selection in a pathological test) must still terminate and both claims
        // must have been observed, in order.
        var sut = new PanelSelectionCoordinator();
        var ownerA = new object();
        var ownerB = new object();
        var seen = new List<object>();
        var reentered = false;

        sut.SelectionClaimed += (_, claimedOwner) =>
        {
            seen.Add(claimedOwner);
            if (!reentered && ReferenceEquals(claimedOwner, ownerA))
            {
                reentered = true;
                sut.NotifySelected(ownerB);
            }
        };

        var exception = Record.Exception(() => sut.NotifySelected(ownerA));

        Assert.Null(exception);
        Assert.Equal([ownerA, ownerB], seen);
    }
}
