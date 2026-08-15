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

    [Fact]
    public void NotifySelected_NullOwner_Throws()
    {
        var sut = new PanelSelectionCoordinator();

        Assert.Throws<ArgumentNullException>(() => sut.NotifySelected(null!));
    }

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
