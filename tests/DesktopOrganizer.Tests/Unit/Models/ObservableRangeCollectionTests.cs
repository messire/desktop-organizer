using System.Collections.Specialized;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for Phase 7 Block D code-quality follow-up (docs/PLANS/phase-7-performance-hardening.md):
/// <see cref="ObservableRangeCollection{T}.ReplaceAll"/> must guard against reentrancy (MAJOR-3) and
/// must not break when the source enumerable is a lazy projection over the collection itself (MAJOR-4).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ObservableRangeCollectionTests
{
    [Fact]
    public void ReplaceAll_SelfReferentialProjection_KeepsAllMatchingItems()
    {
        // Regression guard: items must be materialized BEFORE Items.Clear() runs. Enumerating a lazy
        // Where() over the collection AFTER clearing it would silently observe an empty source and
        // produce an empty result instead of throwing — the worst class of bug.
        var collection = new ObservableRangeCollection<int> { 1, 2, 3, 4 };

        collection.ReplaceAll(collection.Where(x => x % 2 == 0));

        Assert.Equal([2, 4], collection);
    }

    [Fact]
    public void ReplaceAll_CalledReentrantlyFromCollectionChangedHandler_ThrowsInvalidOperationException()
    {
        // ObservableCollection's own reentrancy guard (CheckReentrancy/BlockReentrancy) must still
        // apply here — ReplaceAll mutates Items directly, bypassing Add()/Remove(), which previously
        // skipped the guard entirely and would silently corrupt state instead of failing loudly.
        //
        // Note: ObservableCollection.CheckReentrancy() only throws when CollectionChanged has MORE
        // THAN ONE subscriber (a deliberate BCL exception for single-listener patterns like
        // Selector.SelectedItem) — a second, otherwise-inert handler is required to actually trigger
        // the guard here. With only one handler, the reentrant call is allowed through and recurses
        // unboundedly (stack overflow) instead of throwing, which is not the scenario under test.
        var collection = new ObservableRangeCollection<int> { 1, 2 };
        collection.CollectionChanged += (_, _) => { };
        collection.CollectionChanged += (_, _) => collection.ReplaceAll([9, 9]);

        Assert.Throws<InvalidOperationException>(() => collection.ReplaceAll([3, 4]));
    }

    [Fact]
    public void ReplaceAll_RaisesExactlyOneResetNotification()
    {
        var collection = new ObservableRangeCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => events.Add(e);

        collection.ReplaceAll([4, 5]);

        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, raised.Action);
        Assert.Equal([4, 5], collection);
    }
}
