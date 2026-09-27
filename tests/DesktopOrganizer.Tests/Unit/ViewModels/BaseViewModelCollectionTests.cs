using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

// ---------------------------------------------------------------------------
// WPF-free stub exposing BaseViewModel's `protected` (not `protected internal`) mutation surface
// for direct testing. AddFiles/RemoveFileByPath/ReplaceAll are `protected internal` and already
// callable directly on a BaseViewModel reference from this assembly (see AssemblyInfo.cs
// InternalsVisibleTo), mirroring Unit/ContentFilter/ContentFilterTests.cs's FilterViewModel. RemoveFile
// is plain `protected`, so it needs this tiny subclass wrapper.
// ---------------------------------------------------------------------------

/// <summary>
/// WPF-free stub exposing <see cref="BaseViewModel"/>'s <c>protected</c> (not <c>protected internal</c>)
/// mutation surface — specifically <c>RemoveFile</c> — for direct testing.
/// </summary>
/// <param name="iconProvider">The icon provider forwarded to the base <see cref="BaseViewModel"/>.</param>
internal sealed class MutableViewModel(IIconProvider iconProvider) : BaseViewModel(iconProvider)
{
    /// <summary>
    /// Removes the given item via the base class's <c>protected</c> <c>RemoveFile</c> method.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    public void RemoveFileItem(FileItem item) => RemoveFile(item);
}

// ---------------------------------------------------------------------------
// WPF-free stub applying a caller-settable predicate on every OnSourceChanged rebuild — mirrors
// ChildWindowViewModel's ApplyFilterAndSort at the BaseViewModel level, without pulling in the whole
// WPF-dependent ChildWindowViewModel (Application.Current template resolution etc.). Used to exercise
// MoveItem's visible-index-to-full-set anchoring rule (ADR-014 Decision 5) and ReplaceFileItems' early
// exit (ADR-014 Decision 8) directly against BaseViewModel, independent of ChildWindowViewModel.
// ---------------------------------------------------------------------------

/// <summary>
/// WPF-free stub applying a caller-settable predicate on every <c>OnSourceChanged</c> rebuild — mirrors
/// <c>ChildWindowViewModel.ApplyFilterAndSort</c> at the <see cref="BaseViewModel"/> level, without pulling
/// in the whole WPF-dependent <c>ChildWindowViewModel</c>. Used to exercise <see cref="BaseViewModel.MoveItem"/>'s
/// visible-index-to-full-set anchoring rule (ADR-014 Decision 5) and <c>ReplaceFileItems</c>' early exit
/// (ADR-014 Decision 8) directly against <see cref="BaseViewModel"/>.
/// </summary>
/// <param name="iconProvider">The icon provider forwarded to the base <see cref="BaseViewModel"/>.</param>
internal sealed class FilterableViewModel(IIconProvider iconProvider) : BaseViewModel(iconProvider)
{
    /// <summary>
    /// Gets or sets the predicate applied to <see cref="BaseViewModel.AllItems"/> on every rebuild. Defaults
    /// to showing every item.
    /// </summary>
    public Func<FileItem, bool> Filter { get; set; } = _ => true;

    /// <summary>Re-applies the current <see cref="Filter"/> without any underlying state change.</summary>
    public void Reproject() => OnSourceChanged();

    /// <summary>
    /// Rebuilds <see cref="BaseViewModel.FileItems"/> by projecting <see cref="BaseViewModel.AllItems"/>
    /// through the current <see cref="Filter"/>.
    /// </summary>
    protected override void OnSourceChanged() => ReplaceFileItems(AllItems.Where(Filter));
}

/// <summary>
/// Unit tests for Phase 7 Block D (docs/PLANS/phase-7-performance-hardening.md — audit findings 5, 6):
/// O(1) duplicate detection in <see cref="BaseViewModel.AddFiles"/>/<see cref="BaseViewModel.ReplaceAll"/>
/// and single-notification batch rebuilds of <see cref="BaseViewModel.FileItems"/> via
/// <see cref="DesktopOrganizer.UI.Models.ObservableRangeCollection{T}"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class BaseViewModelCollectionTests
{
    private const string PathA = @"C:\fake\A.txt";
    private const string PathB = @"C:\fake\B.txt";
    private const string PathC = @"C:\fake\C.txt";
    private const string PathD = @"C:\fake\D.txt";

    // ── D1: O(1) dedup index stays consistent with _allItems across all mutations ─────────────────

    /// <summary>
    /// Verifies that adding the exact same path twice in a single call results in only one tracked item.
    /// </summary>
    [Fact]
    public void AddFiles_ExactDuplicatePath_AddedOnlyOnce()
    {
        var vm = BuildVm();

        vm.AddFiles([PathA, PathA]);

        Assert.Single(vm.AllItems);
    }

    /// <summary>
    /// Verifies that the dedup index treats paths differing only by casing as the same entry, matching
    /// <c>RemoveFileByPath</c>'s case-insensitive comparer.
    /// </summary>
    [Fact]
    public void AddFiles_DifferingCasing_TreatedAsSameEntry()
    {
        // Windows paths are case-insensitive; the dedup index must match RemoveFileByPath's
        // pre-existing OrdinalIgnoreCase comparer (previously AddFiles compared ordinal/case-sensitive
        // while RemoveFileByPath compared case-insensitive — an inconsistency fixed by Block D).
        var vm = BuildVm();

        vm.AddFiles([@"C:\A.txt"]);
        vm.AddFiles([@"c:\a.txt"]);

        Assert.Single(vm.AllItems);
    }

    /// <summary>
    /// Verifies that removing an item by path frees its slot in the dedup index, so re-adding the same
    /// path afterward is treated as a new file rather than being silently swallowed.
    /// </summary>
    [Fact]
    public void AddFiles_ThenRemoveByPath_ThenReAdd_IndexAllowsReAdd()
    {
        // Removing must clear the path from the dedup index too, or a re-add of the same path would
        // be silently swallowed as a "duplicate" forever.
        var vm = BuildVm();

        vm.AddFiles([PathA]);
        vm.RemoveFileByPath(PathA);
        vm.AddFiles([PathA]);

        Assert.Single(vm.AllItems);
    }

    /// <summary>
    /// Verifies that removing an item via <see cref="MutableViewModel.RemoveFileItem"/> frees its path in
    /// the dedup index, so re-adding the same path afterward is treated as a new file.
    /// </summary>
    [Fact]
    public void RemoveFile_ThenReAddSamePath_IndexAllowsReAdd()
    {
        var vm = (MutableViewModel)BuildVm();

        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        vm.RemoveFileItem(item);
        vm.AddFiles([PathA]);

        Assert.Single(vm.AllItems);
    }

    /// <summary>
    /// Verifies that <c>ReplaceAll</c> clears the dedup index, so a path dropped by one call can be
    /// re-submitted successfully by a later one.
    /// </summary>
    [Fact]
    public void ReplaceAll_ClearsIndex_AllowsPreviousPathsToBeReAdded()
    {
        var vm = BuildVm();

        vm.AddFiles([PathA, PathB]);
        vm.ReplaceAll([PathA]); // PathB dropped, PathA re-submitted via the same (cleared) index

        var item = Assert.Single(vm.AllItems);
        Assert.Equal(PathA, item.Path);
    }

    /// <summary>
    /// Verifies that a single <c>ReplaceAll</c> call whose input contains duplicate paths only adds each
    /// distinct path once.
    /// </summary>
    [Fact]
    public void ReplaceAll_WithDuplicatePathsInInput_AddsEachPathOnce()
    {
        var vm = BuildVm();

        vm.ReplaceAll([PathA, PathA, PathB]);

        Assert.Equal(2, vm.AllItems.Count);
    }

    // ── D2: batch notification — exactly one CollectionChanged per rebuild ─────────────────────────

    /// <summary>
    /// Verifies that a full <see cref="BaseViewModel.FileItems"/> rebuild raises exactly one
    /// <see cref="INotifyCollectionChanged.CollectionChanged"/> event, with <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// </summary>
    [Fact]
    public void OnSourceChanged_FullRebuild_RaisesExactlyOneCollectionChangedReset()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.AddFiles([PathC]); // triggers OnSourceChanged -> full rebuild

        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, raised.Action);
    }

    /// <summary>
    /// Verifies that <c>ReplaceAll</c> raises exactly one <see cref="INotifyCollectionChanged.CollectionChanged"/>
    /// event, with <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// </summary>
    [Fact]
    public void ReplaceAll_FullRebuild_RaisesExactlyOneCollectionChangedReset()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.ReplaceAll([PathC]);

        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, raised.Action);
    }

    /// <summary>
    /// Verifies that after a batched rebuild, <see cref="BaseViewModel.FileItems"/> actually reflects the
    /// new contents rather than staying stale.
    /// </summary>
    [Fact]
    public void FileItems_AfterRebuild_ContainsProjectedItems()
    {
        // Sanity check that the batched Reset actually carries the new contents (not left stale).
        var vm = BuildVm();
        vm.AddFiles([PathA]);
        vm.AddFiles([PathB, PathC]);

        Assert.Equal(3, vm.FileItems.Count);
        Assert.Contains(vm.FileItems, f => f.Path == PathA);
        Assert.Contains(vm.FileItems, f => f.Path == PathB);
        Assert.Contains(vm.FileItems, f => f.Path == PathC);
    }

    // ── D1 (early-exit optimization): AddFiles with nothing new added skips the rebuild ─────────────

    /// <summary>
    /// Verifies that calling <c>AddFiles</c> with paths that are all already tracked is a no-op re-sync
    /// that raises no <see cref="INotifyCollectionChanged.CollectionChanged"/> notification.
    /// </summary>
    [Fact]
    public void AddFiles_AllPathsAlreadyTracked_DoesNotRaiseCollectionChanged()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.AddFiles([PathA, PathB]); // both already present — a no-op re-sync

        Assert.Empty(events);
    }

    /// <summary>
    /// Verifies that calling <c>AddFiles</c> with an empty array raises no
    /// <see cref="INotifyCollectionChanged.CollectionChanged"/> event.
    /// </summary>
    [Fact]
    public void AddFiles_EmptyArray_DoesNotRaiseCollectionChanged()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.AddFiles([]);

        Assert.Empty(events);
    }

    /// <summary>
    /// Verifies that <c>ReplaceAll</c> with an empty set still clears <see cref="BaseViewModel.FileItems"/>,
    /// so the early-exit optimization in <c>AddFiles</c> does not suppress this rebuild.
    /// </summary>
    [Fact]
    public void ReplaceAll_ToEmpty_StillClearsFileItems()
    {
        // Regression guard: ReplaceAll must always rebuild FileItems even when the new set is empty
        // (AddFiles' own early-exit must not suppress this — Folder Portal folder emptied out).
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);
        Assert.NotEmpty(vm.FileItems);

        vm.ReplaceAll([]);

        Assert.Empty(vm.AllItems);
        Assert.Empty(vm.FileItems);
    }

    // ── TryRenameItem (ADR-013 Decision 4/6): _allItemPaths re-indexed after a physical rename ──────

    /// <summary>
    /// Verifies that after <c>TryRenameItem</c> relocates an item, its old path is freed from the dedup
    /// index, so re-adding that old path is treated as a distinct new file.
    /// </summary>
    [Fact]
    public void TryRenameItem_OldPathIsFreed_CanBeReAddedAsANewFile()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);

        vm.TryRenameItem(item, PathB);

        Assert.Equal(PathB, item.Path);

        // The old path index entry must be released — a fresh AddFiles for it is treated as a new,
        // distinct file, not silently swallowed as an already-tracked duplicate.
        vm.AddFiles([PathA]);

        Assert.Equal(2, vm.AllItems.Count);
        Assert.Contains(vm.AllItems, f => f.Path == PathA);
        Assert.Contains(vm.AllItems, f => f.Path == PathB);
    }

    /// <summary>
    /// Verifies that after <c>TryRenameItem</c> relocates an item, its new path is indexed, so
    /// re-submitting that path via <c>AddFiles</c> is deduplicated rather than creating a duplicate item.
    /// </summary>
    [Fact]
    public void TryRenameItem_NewPathIsDeduplicatedOnReAdd()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);
        var item = vm.AllItems.First(f => f.Path == PathA);

        vm.TryRenameItem(item, PathC);

        // The new path must now be indexed — re-submitting it via AddFiles must be a no-op dedup,
        // not add a second, duplicate FileItem for the same on-disk path.
        vm.AddFiles([PathC]);

        Assert.Equal(2, vm.AllItems.Count);
        Assert.Contains(vm.AllItems, f => f.Path == PathC);
        Assert.Contains(vm.AllItems, f => f.Path == PathB);
    }

    /// <summary>
    /// Verifies that calling <c>TryRenameItem</c> for an item that is not tracked in
    /// <see cref="BaseViewModel.AllItems"/> is a no-op that leaves the untracked item and the index
    /// untouched.
    /// </summary>
    [Fact]
    public void TryRenameItem_ItemNotTracked_NoOp()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA]);
        var untracked = new FileItem(PathB, null);

        vm.TryRenameItem(untracked, PathC);

        Assert.Equal(PathB, untracked.Path); // left untouched — Relocate never ran
        var item = Assert.Single(vm.AllItems);
        Assert.Equal(PathA, item.Path);

        // Neither the old nor the "new" path leaked into the index for an item that was never tracked.
        vm.AddFiles([PathB, PathC]);
        Assert.Equal(3, vm.AllItems.Count);
    }

    /// <summary>
    /// Verifies that <c>TryRenameItem</c> relocates the same <see cref="FileItem"/> instance in place,
    /// rather than discarding and recreating it, so UI state bound to that instance stays valid.
    /// </summary>
    [Fact]
    public void TryRenameItem_RelocatesTheSameInstance_InPlace()
    {
        // The whole point of TryRenameItem over discard-and-recreate: any UI state bound to the
        // specific FileItem instance (e.g. SelectedFile) keeps pointing at a still-valid object.
        var vm = BuildVm();
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);

        vm.TryRenameItem(item, PathB);

        Assert.Same(item, Assert.Single(vm.AllItems));
    }

    // ── Scale smoke test (non-gating: generous threshold, documents the O(n) fix) ───────────────────

    /// <summary>
    /// Verifies that <c>ReplaceAll</c> with five thousand paths completes well within a generous time
    /// threshold, documenting the O(n) index-based dedup fix.
    /// </summary>
    [Fact]
    public void ReplaceAll_FiveThousandPaths_CompletesWithinGenerousThreshold()
    {
        var vm = BuildVm();
        var paths = Enumerable.Range(0, 5000).Select(i => $@"C:\fake\file_{i}.txt").ToArray();

        var stopwatch = Stopwatch.StartNew();
        vm.ReplaceAll(paths);
        stopwatch.Stop();

        Assert.Equal(5000, vm.AllItems.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"ReplaceAll(5000 paths) took {stopwatch.Elapsed} — expected the O(n) index-based dedup to keep this well under 2s.");
    }

    // ── ADR-014 Decision 5: BaseViewModel.MoveItem — no active filter ──────────────────────────────

    /// <summary>
    /// Verifies that moving an item forward to a gap index inserts it right after the item that now
    /// precedes that gap.
    /// </summary>
    [Fact]
    public void MoveItem_Forward_InsertsRightAfterTargetPredecessor()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC, PathD]);
        var itemA = vm.FileItems.First(f => f.Path == PathA);

        // Gap index 2 = "after B, before C" — A ends up immediately after B.
        var moved = vm.MoveItem(itemA, 2);

        Assert.True(moved);
        Assert.Equal([PathB, PathA, PathC, PathD], vm.FileItems.Select(f => f.Path));
        Assert.Equal([PathB, PathA, PathC, PathD], vm.AllItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that moving an item backward to gap index 0 inserts it at the very front of the collection.
    /// </summary>
    [Fact]
    public void MoveItem_Backward_InsertsBeforeTargetPredecessor()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC, PathD]);
        var itemD = vm.FileItems.First(f => f.Path == PathD);

        // Gap index 0 = "before the first visible item" — D moves to the very front.
        var moved = vm.MoveItem(itemD, 0);

        Assert.True(moved);
        Assert.Equal([PathD, PathA, PathB, PathC], vm.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that moving the last item to gap index 0 relocates it to index zero, ahead of every
    /// other item.
    /// </summary>
    [Fact]
    public void MoveItem_ToStart_MovesToIndexZero()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC]);
        var itemC = vm.FileItems.First(f => f.Path == PathC);

        var moved = vm.MoveItem(itemC, 0);

        Assert.True(moved);
        Assert.Equal([PathC, PathA, PathB], vm.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that moving the first item to a gap index equal to the item count relocates it past the
    /// last item.
    /// </summary>
    [Fact]
    public void MoveItem_ToEnd_MovesPastLastItem()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC]);
        var itemA = vm.FileItems.First(f => f.Path == PathA);

        var moved = vm.MoveItem(itemA, vm.FileItems.Count); // gap == FileItems.Count == "after the last item"

        Assert.True(moved);
        Assert.Equal([PathB, PathC, PathA], vm.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that moving an item to the gap immediately before its own current position is a no-op
    /// that leaves the order unchanged.
    /// </summary>
    [Fact]
    public void MoveItem_GapImmediatelyBeforeItsOwnPosition_IsNoOp()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC]);
        var itemB = vm.FileItems.First(f => f.Path == PathB); // vFrom == 1

        var moved = vm.MoveItem(itemB, 1); // gap == vFrom

        Assert.False(moved);
        Assert.Equal([PathA, PathB, PathC], vm.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that moving an item to the gap immediately after its own current position is a no-op
    /// that leaves the order unchanged.
    /// </summary>
    [Fact]
    public void MoveItem_GapImmediatelyAfterItsOwnPosition_IsNoOp()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC]);
        var itemB = vm.FileItems.First(f => f.Path == PathB); // vFrom == 1

        var moved = vm.MoveItem(itemB, 2); // gap == vFrom + 1

        Assert.False(moved);
        Assert.Equal([PathA, PathB, PathC], vm.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that calling <c>MoveItem</c> with an item not present in <see cref="BaseViewModel.FileItems"/>
    /// returns <c>false</c> and leaves the collection order unchanged.
    /// </summary>
    [Fact]
    public void MoveItem_ItemNotInFileItems_ReturnsFalse()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB]);
        var untracked = new FileItem(PathC, null);

        var moved = vm.MoveItem(untracked, 0);

        Assert.False(moved);
        Assert.Equal([PathA, PathB], vm.FileItems.Select(f => f.Path));
    }

    /// <summary>
    /// Verifies that <c>MoveItem</c> only reorders items and does not affect the dedup index, so
    /// re-adding an already-tracked path afterward still deduplicates.
    /// </summary>
    [Fact]
    public void MoveItem_DoesNotTouchAllItemPaths_ReAddingSamePathStillDeduplicates()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC]);
        var itemA = vm.FileItems.First(f => f.Path == PathA);

        vm.MoveItem(itemA, 3); // move to end — only order changes

        vm.AddFiles([PathA]); // still tracked — must still dedup, not re-add as "new"

        Assert.Equal(3, vm.AllItems.Count);
    }

    /// <summary>
    /// Verifies that a real <c>MoveItem</c> reorder raises exactly one
    /// <see cref="INotifyCollectionChanged.CollectionChanged"/> event with <see cref="NotifyCollectionChangedAction.Move"/>,
    /// not a full <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// </summary>
    [Fact]
    public void MoveItem_RealMove_RaisesExactlyOneMoveNotification_NotReset()
    {
        var vm = BuildVm();
        vm.AddFiles([PathA, PathB, PathC]);
        var itemA = vm.FileItems.First(f => f.Path == PathA);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        var moved = vm.MoveItem(itemA, 3);

        Assert.True(moved);
        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Move, raised.Action);
    }

    // ── ADR-014 Decision 5 (R4 — key test): MoveItem with an active content filter ──────────────────

    /// <summary>
    /// Verifies that moving a visible item under an active content filter anchors it to its new visible
    /// neighbor's position in the full <see cref="BaseViewModel.AllItems"/> set, while preserving the
    /// relative order of the items hidden by the filter.
    /// </summary>
    [Fact]
    public void MoveItem_WithHiddenItems_AnchorsToVisibleNeighbor_PreservingHiddenItemsRelativeOrder()
    {
        // [A(hidden), B, C(hidden), D] — only B and D are visible. Move D to the gap before B
        // (visible index 0). Expected full-set result per ADR-014 Decision 5's anchoring rule:
        // A, D, B, C — D lands immediately before B (its new visible predecessor's position), and the
        // hidden items A and C keep their relative order (A still before C), neither dropped nor
        // collapsed to the end.
        var vm = new FilterableViewModel(BuildIconProviderMock().Object) { Filter = f => f.Path is PathB or PathD };
        vm.AddFiles([PathA, PathB, PathC, PathD]);
        Assert.Equal([PathB, PathD], vm.FileItems.Select(f => f.Path)); // sanity: only B, D visible

        var itemD = vm.FileItems.First(f => f.Path == PathD);
        var moved = vm.MoveItem(itemD, 0);

        Assert.True(moved);
        Assert.Equal([PathA, PathD, PathB, PathC], vm.AllItems.Select(f => f.Path));
        Assert.Equal(4, vm.AllItems.Count); // nothing lost
        Assert.Equal([PathD, PathB], vm.FileItems.Select(f => f.Path)); // D now visually before B
    }

    // ── ADR-014 Decision 6: BaseViewModel.SortAllItems ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>SortAllItems</c> reorders <see cref="BaseViewModel.AllItems"/> in place without
    /// raising any <see cref="BaseViewModel.FileItems"/> notification and without disturbing dedup
    /// membership.
    /// </summary>
    [Fact]
    public void SortAllItems_ReordersAllItems_WithoutRebuildingFileItemsOrChangingMembership()
    {
        var vm = BuildVm();
        vm.AddFiles([@"C:\fake\file10.txt", @"C:\fake\file2.txt", @"C:\fake\file1.txt"]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.SortAllItems(PanelItemSortComparer.ByName);

        Assert.Equal(
            [@"C:\fake\file1.txt", @"C:\fake\file2.txt", @"C:\fake\file10.txt"],
            vm.AllItems.Select(f => f.Path));

        // SortAllItems never calls OnSourceChanged — FileItems (and any of its notifications) is
        // completely untouched by this call.
        Assert.Empty(events);
        Assert.Empty(raisedProperties);

        // Membership (_allItemPaths) is unaffected by the reorder — re-adding any of the same paths
        // is still deduplicated, not treated as a "new" file.
        vm.AddFiles([@"C:\fake\file10.txt"]);
        Assert.Equal(3, vm.AllItems.Count);
    }

    // ── ADR-014 Decision 8: ReplaceFileItems early exit on an identical projection ──────────────────

    /// <summary>
    /// Verifies that re-projecting <see cref="BaseViewModel.FileItems"/> against an unchanged source and
    /// filter raises no <see cref="INotifyCollectionChanged.CollectionChanged"/> notification.
    /// </summary>
    [Fact]
    public void ReplaceFileItems_IdenticalProjection_RaisesNoCollectionChanged()
    {
        var vm = new FilterableViewModel(BuildIconProviderMock().Object);
        vm.AddFiles([PathA, PathB]); // first rebuild already happened here

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.Reproject(); // same AllItems, same Filter -> reference-identical projection to what's shown

        Assert.Empty(events);
    }

    /// <summary>
    /// Verifies that when the underlying source genuinely changes, the resulting projection raises
    /// exactly one <see cref="NotifyCollectionChangedAction.Reset"/> event.
    /// </summary>
    [Fact]
    public void ReplaceFileItems_ChangedProjection_RaisesExactlyOneReset()
    {
        var vm = new FilterableViewModel(BuildIconProviderMock().Object);
        vm.AddFiles([PathA, PathB]);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)vm.FileItems).CollectionChanged += (_, e) => events.Add(e);

        vm.AddFiles([PathC]); // AllItems really changes -> a genuinely different projection

        var raised = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, raised.Action);
    }

    /// <summary>
    /// Verifies that when a real rebuild happens and the previously selected file is still present in
    /// the new projection, <see cref="BaseViewModel.SelectedFile"/> remains the same instance.
    /// </summary>
    [Fact]
    public void ReplaceFileItems_RealRebuild_SelectedFileStillPresent_RemainsSelected()
    {
        // Regression guard (ADR-013 A6): the early-exit added by ADR-014 Decision 8 must not disturb
        // selection restoration on the path where a rebuild genuinely happens.
        var vm = new FilterableViewModel(BuildIconProviderMock().Object);
        vm.AddFiles([PathA, PathB]);
        var itemA = vm.FileItems.First(f => f.Path == PathA);
        vm.SelectedFile = itemA;

        vm.AddFiles([PathC]); // real rebuild — AllItems changed

        Assert.Same(itemA, vm.SelectedFile);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static Mock<IIconProvider> BuildIconProviderMock()
    {
        var iconMock = new Mock<IIconProvider>();
        iconMock.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((System.Windows.Media.ImageSource)null!);
        return iconMock;
    }

    private static BaseViewModel BuildVm() => new MutableViewModel(BuildIconProviderMock().Object);
}
