using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.ViewModels;
using Moq;

namespace DesktopOrganizer.Tests.Unit.ViewModels;

// ---------------------------------------------------------------------------
// WPF-free stub exposing BaseViewModel's `protected` background icon-load surface (ADR-046) for direct
// testing: DisposeIconLoading is `protected` (not `protected internal`), and OnProbeResultsApplied/
// IsItemCurrentlyVisible need an override to observe/control. Everything else (AddFiles, ReplaceAll,
// TryRenameItem, RefreshAllItemIcons, RefreshItemsLiveness, IconLoadPriority, ItemsGeneration,
// HasPendingIconLoads) is `protected internal`/`internal` and already callable directly on a
// BaseViewModel reference from this assembly (InternalsVisibleTo) — see BaseViewModelCollectionTests.
// ---------------------------------------------------------------------------

/// <summary>
/// WPF-free stub exposing <see cref="BaseViewModel"/>'s ADR-046 background icon-load surface that isn't
/// already <c>protected internal</c>/<c>internal</c>.
/// </summary>
/// <param name="iconProvider">The icon provider forwarded to the base <see cref="BaseViewModel"/>.</param>
/// <param name="iconLoadQueue">The icon-load queue forwarded to the base <see cref="BaseViewModel"/>.</param>
internal sealed class BackgroundIconTestViewModel(IIconProvider iconProvider, IIconLoadQueue? iconLoadQueue = null)
    : BaseViewModel(iconProvider, iconLoadQueue)
{
    /// <summary>Gets how many times <see cref="OnProbeResultsApplied"/> was invoked.</summary>
    public int ProbeResultsAppliedCount { get; private set; }

    /// <summary>Gets the <c>directoryFlagsChanged</c> argument of the most recent <see cref="OnProbeResultsApplied"/> call.</summary>
    public bool? LastDirectoryFlagsChanged { get; private set; }

    /// <summary>
    /// Gets or sets which items <see cref="IsItemCurrentlyVisible"/> reports as visible. Defaults to
    /// <see langword="null"/> (every item visible — the base class's own default).
    /// </summary>
    public Func<FileItem, bool>? VisiblePredicate { get; set; }

    /// <summary>Exposes the base class's <c>protected</c> <see cref="BaseViewModel.DisposeIconLoading"/> for direct testing.</summary>
    public void DisposeIconLoadingPublic() => DisposeIconLoading();

    /// <summary>Exposes the base class's <c>protected</c> <see cref="BaseViewModel.RefreshAllItemIcons"/>-adjacent liveness for L-1 tests.</summary>
    public void RefreshItemsLivenessPublic() => RefreshItemsLiveness();

    /// <summary>L-1 (review it.2) test seam: when set, <see cref="OnProbeResultsApplied"/> throws this instead of applying anything.</summary>
    public Exception? ThrowFromOnProbeResultsApplied { get; set; }

    /// <summary>Gets the exception most recently observed by <see cref="OnLivenessPassFaulted"/>, or <see langword="null"/> if none yet.</summary>
    public Exception? LastLivenessPassFault { get; private set; }

    /// <inheritdoc />
    protected override void OnProbeResultsApplied(bool directoryFlagsChanged)
    {
        ProbeResultsAppliedCount++;
        LastDirectoryFlagsChanged = directoryFlagsChanged;
        if (ThrowFromOnProbeResultsApplied is { } toThrow) throw toThrow;
        base.OnProbeResultsApplied(directoryFlagsChanged);
    }

    /// <inheritdoc />
    protected override void OnLivenessPassFaulted(Exception exception) => LastLivenessPassFault = exception;

    /// <inheritdoc />
    protected override bool IsItemCurrentlyVisible(FileItem item) => VisiblePredicate?.Invoke(item) ?? true;
}

/// <summary>
/// Unit tests for ADR-046's background icon-load integration into <see cref="BaseViewModel"/> (plan step
/// 7b, docs/PLANS/background-icon-loading.md §6/§8.2): items appear immediately without file-system/shell
/// access, icons/existence are applied only once the queue delivers a result, stale/superseded results
/// are silently ignored, and the bounded placeholder-retry policy is queue-Submit-based.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class BaseViewModelBackgroundIconTests
{
    private const string PathA = @"Q:\nope\A.txt";
    private const string PathB = @"Q:\nope\B.txt";

    private readonly BitmapImage _placeholderIcon = new();

    // ── Key metric test: 1000 items, one Submit, no fs/shell on the calling thread ───────────────────

    /// <summary>
    /// Verifies the headline ADR-046 metric: adding a thousand items on paths that don't (and can't)
    /// exist neither throws nor touches the file system/shell (a strict <see cref="IIconProvider"/> mock
    /// throws if <c>GetIcon</c>/<c>GetTypeIcon</c> is ever called), submits every path in a single
    /// <see cref="IIconLoadGroup.Submit"/> batch, and every item appears immediately, optimistically
    /// <see cref="FileItem.Exists"/> and <see cref="FileItem.IsProbePending"/>.
    /// </summary>
    [Fact]
    public void AddFiles_OneThousandItems_SingleSubmit_NoFileSystemOrShellAccess_ItemsAppearImmediately()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        var paths = Enumerable.Range(0, 1000).Select(i => $@"Q:\nope\file_{i}.txt").ToArray();

        vm.AddFiles(paths);

        Assert.Equal(1000, vm.AllItems.Count);
        Assert.All(vm.AllItems, item =>
        {
            Assert.True(item.Exists);
            Assert.True(item.IsProbePending);
        });

        var group = Assert.Single(queue.Groups);
        var submitCall = Assert.Single(group.SubmitCalls);
        Assert.Equal(1000, submitCall.Count);
    }

    /// <summary>
    /// Verifies that within a single <c>AddFiles</c> call, the items the caller reports as
    /// currently visible (<see cref="BackgroundIconTestViewModel.VisiblePredicate"/>) are submitted
    /// before the ones it reports as hidden (ADR-046 D-6), preserving each group's own relative order.
    /// </summary>
    [Fact]
    public void AddFiles_MixOfVisibleAndHiddenItems_SubmitsVisibleItemsFirst()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue)
        {
            VisiblePredicate = item => item.Path.Contains("visible", StringComparison.Ordinal),
        };

        var paths = new[]
        {
            @"Q:\nope\hidden1.txt", @"Q:\nope\visible1.txt", @"Q:\nope\hidden2.txt", @"Q:\nope\visible2.txt",
        };

        vm.AddFiles(paths);

        var submitted = Assert.Single(queue.Group.SubmitCalls);
        Assert.Equal(
            new[] { @"Q:\nope\visible1.txt", @"Q:\nope\visible2.txt", @"Q:\nope\hidden1.txt", @"Q:\nope\hidden2.txt" },
            submitted.Select(r => r.Path));
    }

    // ── Delivery: Interim/Loaded application rules (ADR-046 §6) ──────────────────────────────────────

    /// <summary>
    /// Verifies that a delivered <see cref="IconLoadOutcome.Loaded"/> result replaces the item's icon via
    /// <see cref="FileItem.UpdateIcon"/>.
    /// </summary>
    [Fact]
    public void Deliver_Loaded_UpdatesItemIcon()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        var realIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, realIcon, exists: true, isDirectory: false));
        PumpAll(queue);

        Assert.Same(realIcon, item.Icon);
        Assert.True(item.Exists);
        Assert.False(item.IsProbePending);
    }

    /// <summary>
    /// M-5 insurance (review it.2, C-4): a delivered <see cref="IconLoadOutcome.Loaded"/> result whose
    /// <see cref="IconLoadResult.Icon"/> is NOT actually frozen is never applied — the item keeps whatever
    /// it was already showing (the placeholder here) rather than trusting the queue's own Frozen guarantee
    /// blindly. <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> are unaffected — they are
    /// applied independently of the icon.
    /// </summary>
    [Fact]
    public void Deliver_LoadedWithUnfrozenIcon_NeverApplied()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var iconBefore = item.Icon;
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, CreateUnfrozenIcon(), exists: true, isDirectory: false));
        PumpAll(queue);

        Assert.Same(iconBefore, item.Icon);
        Assert.True(item.Exists); // Exists/IsDirectory still applied — only the icon itself is rejected
    }

    /// <summary>
    /// M-5 insurance (review it.2, C-4): a delivered <see cref="IconLoadOutcome.Interim"/> result whose
    /// icon is NOT frozen is never applied either.
    /// </summary>
    [Fact]
    public void Deliver_InterimWithUnfrozenIcon_NeverApplied()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var iconBefore = item.Icon;
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Interim, CreateUnfrozenIcon()));
        PumpAll(queue);

        Assert.Same(iconBefore, item.Icon);
    }

    /// <summary>
    /// Verifies that an <see cref="IconLoadOutcome.Interim"/> result never overwrites an already-resolved
    /// (non-placeholder) icon — a late Interim for a request whose real <see cref="IconLoadOutcome.Loaded"/>
    /// result already landed is simply stale.
    /// </summary>
    [Fact]
    public void Deliver_InterimAfterLoaded_DoesNotOverwriteRealIcon()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        var realIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, realIcon));
        PumpAll(queue);

        var interimIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Interim, interimIcon));
        PumpAll(queue);

        Assert.Same(realIcon, item.Icon);
    }

    /// <summary>
    /// Verifies that a <see cref="IconLoadOutcome.Loaded"/> result at a stale (non-requested) pixel size
    /// is applied while the item still has no real icon (better than the placeholder), but a SECOND
    /// stale-size <see cref="IconLoadOutcome.Loaded"/> result does not overwrite it — only a matching-size
    /// result for the current request is allowed to replace it after that.
    /// </summary>
    [Fact]
    public void Deliver_StaleSizeLoaded_AppliesOnlyOnceUntilMatchingSizeArrives()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var request = Assert.Single(queue.Group.AllSubmittedRequests);
        var staleSize = request.PixelSize + 16; // deliberately NOT the requested size

        var firstStaleIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(new IconLoadResult(request.Path, staleSize, queue.Group.Generation, IconLoadOutcome.Loaded, firstStaleIcon, null, null));
        PumpAll(queue);
        Assert.Same(firstStaleIcon, item.Icon); // old-size icon beats the placeholder

        var secondStaleIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(new IconLoadResult(request.Path, staleSize, queue.Group.Generation, IconLoadOutcome.Loaded, secondStaleIcon, null, null));
        PumpAll(queue);
        Assert.Same(firstStaleIcon, item.Icon); // NOT downgraded/replaced by another stale-size result

        var matchingIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, matchingIcon));
        PumpAll(queue);
        Assert.Same(matchingIcon, item.Icon); // the current request's own result always applies
    }

    // ── H-1 (review it.2, M-A): present -> missing is reported ONLY via liveness ───────────────────

    /// <summary>
    /// H-1 regression (small iteration 3): a still-<see cref="FileItem.IsProbePending"/> item's FIRST
    /// delivered icon result confirming it present clears <see cref="FileItem.IsProbePending"/> and applies
    /// <see cref="FileItem.Exists"/> — this is the normal, expected path (not yet a "transition" to guard).
    /// </summary>
    [Fact]
    public void Deliver_LoadedExistsTrueForPendingItem_ConfirmsPresentAndClearsProbePending()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        Assert.True(item.IsProbePending);
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, CreateFrozenRealIcon(), exists: true, isDirectory: false));
        PumpAll(queue);

        Assert.True(item.Exists);
        Assert.False(item.IsProbePending);
    }

    /// <summary>
    /// H-1 regression (small iteration 3): a still-<see cref="FileItem.IsProbePending"/> item's FIRST
    /// delivered icon result reporting it missing (never previously confirmed alive) applies
    /// <see cref="FileItem.Exists"/> = <see langword="false"/> — this is a first-ever observation, not a
    /// present -&gt; missing transition the icon pipeline must stay silent about.
    /// </summary>
    [Fact]
    public void Deliver_LoadedExistsFalseForStillPendingItem_AppliesExistsFalse()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        Assert.True(item.IsProbePending);
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, exists: false, isDirectory: false));
        PumpAll(queue);

        Assert.False(item.Exists);
        Assert.False(item.IsProbePending);
    }

    /// <summary>
    /// H-1 regression (small iteration 3): once an item has been CONFIRMED present (its
    /// <see cref="FileItem.IsProbePending"/> guess already resolved), a LATER delivered icon result
    /// reporting <c>exists: false</c> — e.g. <see cref="BaseViewModel.RefreshAllItemIcons"/>'s own re-fetch
    /// racing a momentarily-stale probe — must never flip it back to missing: that present -&gt; missing
    /// transition is exclusively <c>RunSingleLivenessPassAsync</c>'s job (fixed-cadence, reports each
    /// disappearance exactly once via <c>OnItemsVanished</c>). Applying it here would silently eat that one
    /// required transition.
    /// </summary>
    [Fact]
    public void RefreshAllItemIcons_ThenDeliverLoadedExistsFalse_ConfirmedPresentItemDoesNotBecomeMissing()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var firstRequest = Assert.Single(queue.Group.AllSubmittedRequests);
        queue.Group.Deliver(queue.Group.MakeResult(firstRequest, IconLoadOutcome.Loaded, CreateFrozenRealIcon(), exists: true, isDirectory: false));
        PumpAll(queue);
        Assert.True(item.Exists);
        Assert.False(item.IsProbePending);

        vm.RefreshAllItemIcons();
        var secondRequest = queue.Group.AllSubmittedRequests[^1];
        queue.Group.Deliver(queue.Group.MakeResult(secondRequest, IconLoadOutcome.Loaded, CreateFrozenRealIcon(), exists: false, isDirectory: false));
        PumpAll(queue);

        Assert.True(item.Exists, "the icon pipeline's own probe must never report a present -> missing transition.");
    }

    // ── L-3 (review it.2): HasPendingIconLoads never leaks an in-flight count ──────────────────────

    /// <summary>
    /// L-3 regression (small iteration 3): removing an item whose icon request is still in flight releases
    /// that in-flight count immediately — <see cref="BaseViewModel.HasPendingIconLoads"/> must not keep
    /// reporting <see langword="true"/> for a request that can never resolve (nothing is tracked under
    /// that path anymore to receive it).
    /// </summary>
    [Fact]
    public void RemoveFileByPath_WithInFlightRequest_HasPendingIconLoadsBecomesFalse()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        Assert.True(vm.HasPendingIconLoads);

        vm.RemoveFileByPath(PathA);

        Assert.False(vm.HasPendingIconLoads);
    }

    /// <summary>
    /// L-3 regression (small iteration 3): <see cref="BaseViewModel.ReplaceAll"/> resets the in-flight
    /// count for the OUTGOING item set — a request from before the call can never resolve to this group's
    /// new generation, so <see cref="BaseViewModel.HasPendingIconLoads"/> must not keep reporting
    /// <see langword="true"/> for it once the new (here, empty) set has nothing left in flight either.
    /// </summary>
    [Fact]
    public void ReplaceAll_WithInFlightRequestAndEmptyNewSet_HasPendingIconLoadsBecomesFalse()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        Assert.True(vm.HasPendingIconLoads);

        vm.ReplaceAll([]);

        Assert.False(vm.HasPendingIconLoads);
    }

    // ── Races: removed/replaced/renamed items never receive a chasing result ────────────────────────

    /// <summary>
    /// Verifies that delivering a result for an item removed before the delivery arrives is a silent
    /// no-op — no exception, nothing to apply it to.
    /// </summary>
    [Fact]
    public void Deliver_AfterItemRemoved_IsSilentlyIgnored()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        vm.RemoveFileByPath(PathA);

        var exception = Record.Exception(() =>
        {
            queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, CreateFrozenRealIcon(), exists: true, isDirectory: false));
            PumpAll(queue);
        });

        Assert.Null(exception);
        Assert.Empty(vm.AllItems);
    }

    /// <summary>
    /// Verifies that <see cref="BaseViewModel.ReplaceAll"/> cancels the group's pending generation, and a
    /// stale result for the OLD generation is never applied to the NEW item that reused the same path.
    /// </summary>
    [Fact]
    public void ReplaceAll_CancelsPending_StaleGenerationResultNeverAppliedToNewItem()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var staleRequest = Assert.Single(queue.Group.AllSubmittedRequests);
        var staleGeneration = queue.Group.Generation;

        vm.ReplaceAll([PathA]); // same path, but a brand-new FileItem instance under a new generation

        Assert.True(queue.Group.CancelPendingCallCount >= 1);
        Assert.NotEqual(staleGeneration, queue.Group.Generation);

        var newItem = Assert.Single(vm.AllItems);
        var staleIcon = CreateFrozenRealIcon();

        // Constructed at the OLD generation on purpose — FakeIconLoadGroup replicates the real queue's
        // own generation filter (ADR-046 D-8) and drops it before it ever reaches OnIconResults.
        queue.Group.Deliver(new IconLoadResult(staleRequest.Path, staleRequest.PixelSize, staleGeneration, IconLoadOutcome.Loaded, staleIcon, true, false));
        PumpAll(queue);

        Assert.NotSame(staleIcon, newItem.Icon);
    }

    /// <summary>
    /// Verifies that after <see cref="BaseViewModel.TryRenameItem"/> relocates an item, a result for the
    /// OLD path is ignored (nothing is tracked under it anymore) while a result for the NEW path is
    /// applied to the same, still-live <see cref="FileItem"/> instance.
    /// </summary>
    [Fact]
    public void TryRenameItem_ThenDeliverOldPath_Ignored_ThenDeliverNewPath_Applied()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]);
        var item = Assert.Single(vm.AllItems);
        var oldRequest = Assert.Single(queue.Group.AllSubmittedRequests);

        vm.TryRenameItem(item, PathB);
        Assert.Equal(PathB, item.Path);

        var newRequest = queue.Group.AllSubmittedRequests.Last(r => r.Path == PathB);

        var oldIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(queue.Group.MakeResult(oldRequest, IconLoadOutcome.Loaded, oldIcon, exists: true, isDirectory: false));
        PumpAll(queue);
        Assert.NotSame(oldIcon, item.Icon); // old path no longer tracked — silently ignored

        var newIcon = CreateFrozenRealIcon();
        queue.Group.Deliver(queue.Group.MakeResult(newRequest, IconLoadOutcome.Loaded, newIcon, exists: true, isDirectory: false));
        PumpAll(queue);
        Assert.Same(newIcon, item.Icon);
    }

    // ── IsDirectory batch changes → OnProbeResultsApplied at most once per batch ────────────────────

    /// <summary>
    /// Verifies that a batch flipping <see cref="FileItem.IsDirectory"/> for 50 items invokes
    /// <see cref="BaseViewModel.OnProbeResultsApplied"/> exactly once, not once per item.
    /// </summary>
    [Fact]
    public void Deliver_BatchFlipsDirectoryFlagForFiftyItems_OnProbeResultsAppliedInvokedExactlyOnce()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        // No extension -> CreatePending's heuristic guesses IsDirectory = true for every one of them.
        var paths = Enumerable.Range(0, 50).Select(i => $@"Q:\nope\entry_{i}").ToArray();
        vm.AddFiles(paths);
        Assert.All(vm.AllItems, item => Assert.True(item.IsDirectory));

        var requests = Assert.Single(queue.Group.SubmitCalls);
        var results = requests.Select(r => queue.Group.MakeResult(r, IconLoadOutcome.Loaded, exists: true, isDirectory: false)).ToArray();

        queue.Group.Deliver(results);
        PumpAll(queue);

        Assert.All(vm.AllItems, item => Assert.False(item.IsDirectory));
        Assert.Equal(1, vm.ProbeResultsAppliedCount);
        Assert.True(vm.LastDirectoryFlagsChanged);
    }

    /// <summary>
    /// Verifies that a batch which does NOT flip any item's <see cref="FileItem.IsDirectory"/> never
    /// invokes <see cref="BaseViewModel.OnProbeResultsApplied"/> at all.
    /// </summary>
    [Fact]
    public void Deliver_BatchWithNoDirectoryFlagChange_OnProbeResultsAppliedNeverInvoked()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA]); // has an extension -> CreatePending guesses IsDirectory = false
        var request = Assert.Single(queue.Group.AllSubmittedRequests);

        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, exists: true, isDirectory: false));
        PumpAll(queue);

        Assert.Equal(0, vm.ProbeResultsAppliedCount);
    }

    // ── RefreshAllItemIcons: CancelPending + resubmit, icons untouched until delivery ──────────────

    /// <summary>
    /// Verifies that <see cref="BaseViewModel.RefreshAllItemIcons"/> cancels the group's pending
    /// generation and resubmits every tracked item, while leaving every item's current icon untouched
    /// until a fresh result is actually delivered (no flash back to the placeholder).
    /// </summary>
    [Fact]
    public void RefreshAllItemIcons_CancelsPendingAndResubmitsAll_IconsUnchangedUntilDelivery()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        vm.AddFiles([PathA, PathB]);
        var iconsBefore = vm.AllItems.Select(i => i.Icon).ToList();
        var cancelCountBefore = queue.Group.CancelPendingCallCount;
        var submitCallsBefore = queue.Group.SubmitCalls.Count;

        vm.RefreshAllItemIcons();

        Assert.Equal(cancelCountBefore + 1, queue.Group.CancelPendingCallCount);
        Assert.Equal(submitCallsBefore + 1, queue.Group.SubmitCalls.Count);
        Assert.Equal(2, queue.Group.SubmitCalls[^1].Count);
        Assert.Equal(iconsBefore, vm.AllItems.Select(i => i.Icon)); // untouched — no flash to placeholder
    }

    // ── HasPendingIconLoads / IconLoadPriority / DisposeIconLoading (test seams) ────────────────────

    /// <summary>
    /// Verifies that <see cref="BaseViewModel.HasPendingIconLoads"/> is <see langword="true"/> right after
    /// a submit and flips to <see langword="false"/> once every request resolves to a terminal outcome.
    /// </summary>
    [Fact]
    public void HasPendingIconLoads_TrueWhileInFlight_FalseAfterTerminalDelivery()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        Assert.False(vm.HasPendingIconLoads);

        vm.AddFiles([PathA]);
        Assert.True(vm.HasPendingIconLoads);

        var request = Assert.Single(queue.Group.AllSubmittedRequests);
        queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Unavailable));
        PumpAll(queue);

        Assert.False(vm.HasPendingIconLoads);
    }

    /// <summary>
    /// L-4 (review it.2, ADR-046 D-13): a real <see cref="IIconLoadQueue"/> was supplied, but the
    /// constructing thread has no live <see cref="SynchronizationContext"/> — the D-13 fallback rule
    /// silently degrades to <c>SynchronousIconLoadQueue</c>, and
    /// <see cref="BaseViewModel.UsedSynchronousIconLoadQueueFallbackDespiteSupplied"/> flags it so a
    /// subclass with its own diagnostics dependency can log it (see
    /// <c>ChildWindowViewModel</c>'s constructor).
    /// </summary>
    [Fact]
    public void Construct_QueueSuppliedButNoSynchronizationContext_FlagsFallbackUsed()
    {
        using var scope = new SyncContextScope(null); // no live SynchronizationContext at all
        var queue = new FakeIconLoadQueue();

        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        Assert.True(vm.UsedSynchronousIconLoadQueueFallbackDespiteSupplied);
    }

    /// <summary>
    /// The counterpart of <see cref="Construct_QueueSuppliedButNoSynchronizationContext_FlagsFallbackUsed"/>
    /// for the ordinary (real context present) case: no false positive.
    /// </summary>
    [Fact]
    public void Construct_QueueSuppliedWithSynchronizationContext_FlagNotSet()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();

        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        Assert.False(vm.UsedSynchronousIconLoadQueueFallbackDespiteSupplied);
    }

    /// <summary>
    /// Verifies that <see cref="BaseViewModel.IconLoadPriority"/> forwards directly to the underlying
    /// <see cref="IIconLoadGroup.Priority"/>.
    /// </summary>
    [Fact]
    public void IconLoadPriority_ForwardsToGroupPriority()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        Assert.Equal(IconLoadPriority.Visible, vm.IconLoadPriority); // base default

        vm.IconLoadPriority = IconLoadPriority.Background;

        Assert.Equal(IconLoadPriority.Background, queue.Group.Priority);
        Assert.Equal(IconLoadPriority.Background, vm.IconLoadPriority);
    }

    /// <summary>
    /// Verifies that <see cref="BackgroundIconTestViewModel.DisposeIconLoadingPublic"/> (wrapping
    /// <see cref="BaseViewModel.DisposeIconLoading"/>) disposes this panel's <see cref="IIconLoadGroup"/>.
    /// </summary>
    [Fact]
    public void DisposeIconLoading_DisposesTheGroup()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        vm.DisposeIconLoadingPublic();

        Assert.True(queue.Group.IsDisposed);
    }

    // ── Placeholder retry (ADR-046 §2.3): bounded to 3 attempts, growing backoff, in-flight skip ────

    /// <summary>
    /// Verifies that an item whose icon request is still in flight is never retried — a placeholder-retry
    /// attempt would be redundant while a result for the same item is already on its way.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_ItemWithInFlightRequest_DoesNotRetry()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        var path = NewTempFilePath();
        File.WriteAllText(path, string.Empty);

        try
        {
            vm.AddFiles([path]); // one Submit; the request is never resolved in this test
            Assert.Single(queue.Group.SubmitCalls);

            vm.RefreshItemsLiveness(); // item.Exists (real file) + icon is the placeholder, but in-flight

            Assert.Single(queue.Group.SubmitCalls); // no additional retry submit
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies the full bounded-retry policy end to end: exactly 3 retry attempts total, spaced by a
    /// growing per-tick backoff (1 tick before the 2nd attempt, 2 before the 3rd), and no further attempt
    /// once the budget is spent — <see cref="ManualTimeProvider"/>-free (ADR-046 C-7): the "ticks" here
    /// are ordinary, synchronous <see cref="BaseViewModel.RefreshItemsLiveness"/> calls, not wall-clock
    /// time. Uses <see cref="IconLoadOutcome.Failed"/> (a transient, non-quarantine failure) rather than
    /// <see cref="IconLoadOutcome.Unavailable"/> — see
    /// <see cref="RefreshItemsLiveness_UnavailableOutcome_NeverExhaustsBudget_RetriesEveryTickUntilRootRecovers"/>
    /// for why <c>Unavailable</c> alone (N-3, small iteration 3) no longer spends this budget at all.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_PlaceholderRetry_BoundedToThreeAttemptsWithGrowingBackoff()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        var path = NewTempFilePath();
        File.WriteAllText(path, string.Empty);

        try
        {
            vm.AddFiles([path]);
            ResolveLatestAs(queue, IconLoadOutcome.Failed); // initial request resolved — not in flight anymore

            // Tick 1: attempt #1 (submits immediately — no backoff before the first attempt).
            vm.RefreshItemsLiveness();
            Assert.Equal(2, queue.Group.SubmitCalls.Count);
            ResolveLatestAs(queue, IconLoadOutcome.Failed);

            // Tick 2: backoff (1 tick before the 2nd attempt) — no submit yet.
            vm.RefreshItemsLiveness();
            Assert.Equal(2, queue.Group.SubmitCalls.Count);

            // Tick 3: attempt #2.
            vm.RefreshItemsLiveness();
            Assert.Equal(3, queue.Group.SubmitCalls.Count);
            ResolveLatestAs(queue, IconLoadOutcome.Failed);

            // Ticks 4-5: backoff (2 ticks before the 3rd attempt) — no submit yet.
            vm.RefreshItemsLiveness();
            Assert.Equal(3, queue.Group.SubmitCalls.Count);
            vm.RefreshItemsLiveness();
            Assert.Equal(3, queue.Group.SubmitCalls.Count);

            // Tick 6: attempt #3 — the last one (MaxPlaceholderRetryAttempts == 3).
            vm.RefreshItemsLiveness();
            Assert.Equal(4, queue.Group.SubmitCalls.Count);
            ResolveLatestAs(queue, IconLoadOutcome.Failed);

            // Tick 7: budget spent — no further attempt, ever.
            vm.RefreshItemsLiveness();
            Assert.Equal(4, queue.Group.SubmitCalls.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// N-3 (small iteration 3): a <see cref="IconLoadOutcome.Unavailable"/> outcome — the remote-root
    /// circuit breaker's own quarantine (60 s), far longer than the ~15 s the bounded M3 retry budget would
    /// otherwise take to exhaust — never spends the retry budget at all: the item
    /// keeps retrying on EVERY liveness tick, unbounded, for as long as the outcome stays
    /// <c>Unavailable</c>. The moment the root recovers (a <see cref="IconLoadOutcome.Loaded"/> result
    /// arrives), the icon is applied normally.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_UnavailableOutcome_NeverExhaustsBudget_RetriesEveryTickUntilRootRecovers()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        var path = NewTempFilePath();
        File.WriteAllText(path, string.Empty);

        try
        {
            vm.AddFiles([path]);
            var item = Assert.Single(vm.AllItems);
            ResolveLatestAs(queue, IconLoadOutcome.Unavailable);

            // Far more ticks than MaxPlaceholderRetryAttempts (3) — every single one still retries.
            for (var tick = 0; tick < 10; tick++)
            {
                var submitCountBeforeTick = queue.Group.SubmitCalls.Count;
                vm.RefreshItemsLiveness();
                Assert.Equal(
                    submitCountBeforeTick + 1,
                    queue.Group.SubmitCalls.Count);
                ResolveLatestAs(queue, IconLoadOutcome.Unavailable);
            }

            // The root recovers: the next resolved request is Loaded, and the icon is applied normally.
            var request = queue.Group.AllSubmittedRequests[^1];
            var icon = CreateFrozenRealIcon();
            queue.Group.Deliver(queue.Group.MakeResult(request, IconLoadOutcome.Loaded, icon, exists: true, isDirectory: false));
            PumpAll(queue);

            Assert.Same(icon, item.Icon);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// N-1 (small iteration 3): once an item's placeholder-retry budget is fully exhausted (via a
    /// transient, non-quarantine outcome — see <see cref="ResolveLatestAs"/>), a subsequent
    /// <see cref="BaseViewModel.ResetExhaustedPlaceholderRetries"/> call (what
    /// <c>ChildWindowViewModel.SetIconLoadVisibility(true)</c> does for a panel that just became visible
    /// again) gives it exactly one more attempt — a single new <see cref="IIconLoadGroup.Submit"/> call,
    /// not a burst.
    /// </summary>
    [Fact]
    public void ResetExhaustedPlaceholderRetries_ExhaustedBudget_ResubmitsExactlyOnce()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        var path = NewTempFilePath();
        File.WriteAllText(path, string.Empty);

        try
        {
            vm.AddFiles([path]);
            ResolveLatestAs(queue, IconLoadOutcome.Failed);

            vm.RefreshItemsLiveness(); // attempt #1
            ResolveLatestAs(queue, IconLoadOutcome.Failed);
            vm.RefreshItemsLiveness(); // backoff
            vm.RefreshItemsLiveness(); // attempt #2
            ResolveLatestAs(queue, IconLoadOutcome.Failed);
            vm.RefreshItemsLiveness(); // backoff
            vm.RefreshItemsLiveness(); // backoff
            vm.RefreshItemsLiveness(); // attempt #3 (last)
            ResolveLatestAs(queue, IconLoadOutcome.Failed);

            var submitCountBeforeExhaustionCheck = queue.Group.SubmitCalls.Count;
            vm.RefreshItemsLiveness(); // budget spent — no further attempt
            Assert.Equal(submitCountBeforeExhaustionCheck, queue.Group.SubmitCalls.Count);

            vm.ResetExhaustedPlaceholderRetries();

            Assert.Equal(submitCountBeforeExhaustionCheck + 1, queue.Group.SubmitCalls.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// M-1 (review it.2): an item whose CURRENT icon is a real (non-placeholder) one is still retried on
    /// the next liveness tick when its most recently resolved request's outcome was
    /// <see cref="IconLoadOutcome.Unavailable"/> — e.g. an icon fetched successfully before a network root
    /// went down, then a later re-fetch (triggered by, say, a resize) came back Unavailable while the item
    /// stayed <see cref="FileItem.Exists"/>. Retrying only when the icon itself "looks like" a placeholder
    /// (<see cref="IIconProvider.IsPlaceholder"/>) would leave this item stuck on a potentially stale icon
    /// forever.
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_RealIconButLastOutcomeUnavailable_StillRetries()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);
        var path = NewTempFilePath();
        File.WriteAllText(path, string.Empty);

        try
        {
            vm.AddFiles([path]);
            var item = Assert.Single(vm.AllItems);

            // First request resolves Loaded with a real icon — item.Icon is no longer the placeholder.
            var firstRequest = Assert.Single(queue.Group.AllSubmittedRequests);
            var realIcon = CreateFrozenRealIcon();
            queue.Group.Deliver(queue.Group.MakeResult(firstRequest, IconLoadOutcome.Loaded, realIcon, exists: true, isDirectory: false));
            PumpAll(queue);
            Assert.Same(realIcon, item.Icon);

            // A later re-fetch (e.g. RefreshAllItemIcons on a resize) resolves Unavailable — the icon
            // itself is untouched (still the real one from above), but LastOutcome is now unhealthy.
            vm.RefreshAllItemIcons();
            var secondRequest = queue.Group.AllSubmittedRequests[^1];
            queue.Group.Deliver(queue.Group.MakeResult(secondRequest, IconLoadOutcome.Unavailable));
            PumpAll(queue);
            Assert.Same(realIcon, item.Icon); // still the real icon — Unavailable never touches it

            var submitCountBeforeTick = queue.Group.SubmitCalls.Count;
            vm.RefreshItemsLiveness(); // item.Exists (real file); icon is real, not a placeholder

            Assert.Equal(submitCountBeforeTick + 1, queue.Group.SubmitCalls.Count); // retried anyway
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── L-1: an OnItemsVanished/OnProbeResultsApplied exception is observed, never lost ──────────────

    /// <summary>
    /// L-1 (review it.2): a liveness pass that ends up faulting — here, because
    /// <see cref="BackgroundIconTestViewModel.OnProbeResultsApplied"/> itself throws — invokes
    /// <see cref="BaseViewModel.OnLivenessPassFaulted"/> with that exception rather than becoming a
    /// silently-unobserved task exception (this call goes through the fire-and-forget
    /// <see cref="BaseViewModel.RefreshItemsLiveness"/>, not the awaited
    /// <see cref="BaseViewModel.RefreshItemsLivenessAsync"/> a caller could otherwise observe the fault on
    /// directly).
    /// </summary>
    [Fact]
    public void RefreshItemsLiveness_OnProbeResultsAppliedThrows_ObservedByOnLivenessPassFaulted()
    {
        using var scope = new SyncContextScope(new CollectingSynchronizationContext());
        var queue = new FakeIconLoadQueue();
        var vm = new BackgroundIconTestViewModel(BuildStrictIconProviderMock().Object, queue);

        // A directory that happens to have a non-empty "extension" in its name so CreatePending's
        // heuristic guesses IsDirectory = false initially; the real probe below (SynchronousItemProbeQueue
        // — no IItemProbeQueue supplied) then flips it to true, forcing OnProbeResultsApplied to run.
        var dirPath = Path.Combine(Path.GetTempPath(), $"DOrgBgIconVmTest_{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(dirPath);

        var thrown = new InvalidOperationException("boom");
        vm.ThrowFromOnProbeResultsApplied = thrown;

        try
        {
            vm.AddFiles([dirPath]);
            Assert.False(Assert.Single(vm.AllItems).IsDirectory);

            vm.RefreshItemsLivenessPublic(); // SynchronousItemProbeQueue: the whole pass runs synchronously

            Assert.Same(thrown, vm.LastLivenessPassFault);
        }
        finally
        {
            Directory.Delete(dirPath);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the most recently submitted request (via <see cref="FakeIconLoadGroup.AllSubmittedRequests"/>)
    /// with <paramref name="outcome"/> and pumps the delivery context, so the corresponding item's per-item
    /// "request in flight" bookkeeping clears before the next tick.
    /// </summary>
    private static void ResolveLatestAs(FakeIconLoadQueue queue, IconLoadOutcome outcome)
    {
        var request = queue.Group.AllSubmittedRequests[^1];
        queue.Group.Deliver(queue.Group.MakeResult(request, outcome));
        PumpAll(queue);
    }

    /// <summary>Pumps every <see cref="CollectingSynchronizationContext"/> this test's queue delivers through.</summary>
    private static void PumpAll(FakeIconLoadQueue queue)
    {
        if (SynchronizationContext.Current is CollectingSynchronizationContext context) context.Pump();
    }

    private static string NewTempFilePath() => Path.Combine(Path.GetTempPath(), $"DOrgBgIconVmTest_{Guid.NewGuid():N}.txt");

    /// <summary>
    /// Builds a Frozen <see cref="ImageSource"/> standing in for a "real" (non-placeholder) delivered icon
    /// (M-5, review it.2, C-4): <c>BaseViewModel.OnIconResults</c>'s own insurance check now requires
    /// <see cref="System.Windows.Freezable.IsFrozen"/> before ever applying a delivered
    /// <see cref="IconLoadOutcome.Loaded"/>/<see cref="IconLoadOutcome.Interim"/> icon — exactly like the
    /// real <see cref="IIconProvider"/>/<c>StaIconLoadQueue</c> guarantee it is by the time it crosses the
    /// worker -> UI boundary.
    /// </summary>
    private static ImageSource CreateFrozenRealIcon()
    {
        var icon = new DrawingImage();
        icon.Freeze();
        return icon;
    }

    /// <summary>
    /// Builds a real <see cref="IIconLoadQueue.CreateGroup"/> result whose <see cref="IconLoadResult.Icon"/>
    /// is deliberately NOT frozen (M-5, review it.2) — used only by the tests proving
    /// <c>BaseViewModel.OnIconResults</c>'s insurance check itself, never as a stand-in for a legitimately
    /// delivered icon elsewhere in this file.
    /// </summary>
    private static ImageSource CreateUnfrozenIcon() => new DrawingImage();

    /// <summary>
    /// Builds a strict <see cref="IIconProvider"/> mock (ADR-046 §8.2's "key metric" test): only the
    /// no-fs/no-shell peek members (<see cref="IIconProvider.PlaceholderIcon"/>,
    /// <see cref="IIconProvider.TryPeekCachedIcon"/>, <see cref="IIconProvider.TryPeekTypeIcon"/>,
    /// <see cref="IIconProvider.IsPlaceholder"/>) are configured — <c>GetIcon</c>/<c>GetTypeIcon</c> are
    /// deliberately left unset, so any call to either throws <see cref="MockException"/> and fails the
    /// test outright, proving the UI-thread add/rename/resize/retry paths never reach the shell.
    /// </summary>
    private Mock<IIconProvider> BuildStrictIconProviderMock()
    {
        var mock = new Mock<IIconProvider>(MockBehavior.Strict);

        mock.Setup(p => p.PlaceholderIcon).Returns(_placeholderIcon);

        ImageSource? noCacheHit = null;
        mock.Setup(p => p.TryPeekCachedIcon(It.IsAny<string>(), It.IsAny<int>(), out noCacheHit)).Returns(false);
        mock.Setup(p => p.TryPeekTypeIcon(It.IsAny<string>(), It.IsAny<bool>())).Returns((ImageSource?)null);
        mock.Setup(p => p.IsPlaceholder(It.IsAny<ImageSource?>())).Returns<ImageSource?>(icon => ReferenceEquals(icon, _placeholderIcon));

        return mock;
    }

    /// <summary>Sets a <see cref="SynchronizationContext"/> as <see cref="SynchronizationContext.Current"/> for the scope's lifetime, restoring the previous one on <see cref="Dispose"/>.</summary>
    private sealed class SyncContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous;

        /// <param name="context">
        /// The context to install for the scope's lifetime — <see langword="null"/> (L-4, review it.2)
        /// simulates a constructing thread with no live <see cref="SynchronizationContext"/> at all.
        /// </param>
        public SyncContextScope(SynchronizationContext? context)
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
        }

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
