using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Base class for all ViewModels.
/// </summary>
public abstract class BaseViewModel : INotifyPropertyChanged
{
    /// <summary>Maximum number of placeholder-retry attempts per item (see <see cref="RetryPlaceholderIcon"/>).</summary>
    private const int MaxPlaceholderRetryAttempts = 3;

    /// <summary>
    /// Upper bound on a single <see cref="RefreshItemsLivenessAsync"/> pass (ADR-046 7c §7.1): a probe
    /// batch stuck behind a still-quarantining network root must not keep the panel's existence-liveness
    /// pipeline waiting forever. A pass that exceeds this is discarded WHOLESALE (never applied
    /// partially) — and (M-2, review it.2) the <see cref="IItemProbeQueue.ProbeAsync"/> call itself is
    /// actually cancelled via <see cref="ProbeWithTimeoutAsync"/>'s linked token, not merely abandoned to
    /// keep running on the thread pool "in vain"; the services layer's own remote-root circuit breaker is
    /// still what actually quarantines the slow root — this timeout protects THIS panel's UI-thread
    /// pipeline from waiting on it, and cancellation on top of that frees the probe queue's own resources
    /// (a <c>BackgroundItemProbeQueue</c> batch-throttle slot, a thread-pool thread) sooner.
    /// </summary>
    private static readonly TimeSpan LivenessPassTimeout = TimeSpan.FromSeconds(10);

    private readonly IIconProvider _iconProvider;
    private readonly List<FileItem> _allItems = [];

    /// <summary>
    /// O(1) membership index mirroring <see cref="_allItems"/>, keyed by path (Windows paths are
    /// case-insensitive — <c>C:\A.txt</c> and <c>c:\a.txt</c> are the same entry). Also the lookup
    /// <see cref="OnIconResults"/> uses to match a delivered <see cref="IconLoadResult"/> back to its
    /// <see cref="FileItem"/> (ADR-046).
    /// </summary>
    private readonly Dictionary<string, FileItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableRangeCollection<FileItem> _fileItems = [];

    /// <summary>
    /// Bounded retry bookkeeping for <see cref="RefreshItemsLiveness"/>'s placeholder-retry (M3, P08
    /// fix cycle 1, 2026-09-22) — see that method's remarks. Keyed by <see cref="FileItem"/> reference
    /// via <see cref="ConditionalWeakTable{TKey,TValue}"/> rather than a plain dictionary explicitly
    /// cleaned up on <see cref="RemoveFile"/>/<see cref="RemoveFileByPath"/>/<see cref="ReplaceAll"/>:
    /// an item dropped from <see cref="_allItems"/> naturally becomes unreachable from here too once
    /// nothing else references it, with no extra bookkeeping needed at every removal call site.
    /// </summary>
    private readonly ConditionalWeakTable<FileItem, PlaceholderRetryState> _placeholderRetries = new();

    /// <summary>
    /// Per-item background icon-load bookkeeping (ADR-046) — whether a request is currently in flight and
    /// whether a real (non-placeholder) icon has already been applied. Same
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> lifetime rationale as <see cref="_placeholderRetries"/>.
    /// </summary>
    private readonly ConditionalWeakTable<FileItem, ItemIconState> _iconStates = new();

    /// <summary>
    /// This panel's slice of the background icon-load queue (ADR-046), created once for this
    /// ViewModel's lifetime (constructor — see its own remarks). See <see cref="ResolveIconLoadQueue"/>
    /// for the D-13 fallback rule and <see cref="DisposeIconLoading"/> for teardown.
    /// </summary>
    private readonly IIconLoadGroup _iconLoadGroup;

    /// <summary>
    /// Off-UI-thread existence/directory-ness probe queue for <see cref="RefreshItemsLivenessAsync"/>
    /// (ADR-046 7c) — see <see cref="ResolveItemProbeQueue"/> for the D-13 fallback rule.
    /// </summary>
    private readonly IItemProbeQueue _itemProbeQueue;

    /// <summary>
    /// Cancelled by <see cref="DisposeIconLoading"/> so an in-flight <see cref="RefreshItemsLivenessAsync"/>
    /// pass's <see cref="IItemProbeQueue.ProbeAsync"/> call unwinds instead of ever trying to apply a
    /// result to an about-to-be-unreachable ViewModel. Deliberately never <c>Dispose</c>d (only
    /// cancelled): a liveness tick that races <see cref="DisposeIconLoading"/> and reads
    /// <see cref="CancellationTokenSource.Token"/> a moment after must not see
    /// <see cref="ObjectDisposedException"/> — an already-cancelled token is all it needs.
    /// </summary>
    private readonly CancellationTokenSource _livenessCts = new();

    /// <summary>
    /// The currently in-flight <see cref="RefreshItemsLivenessAsync"/> pass, or <see langword="null"/>
    /// when none is running — see that method's own remarks for the coalescing this backs.
    /// </summary>
    private Task? _livenessPassTask;

    /// <summary>
    /// Set by a <see cref="RefreshItemsLivenessAsync"/> call that arrives while a pass is already running
    /// — see that method's own remarks.
    /// </summary>
    private bool _livenessRerunRequested;

    private FileItem? _selectedFile;

    /// <summary>
    /// Physical pixel size (already snapped — <see cref="IconPixelSize.Snap"/>) every icon of this panel is
    /// requested at. Starts at <see cref="IconPixelSize.Default"/>; a subclass that knows its display size and
    /// DPI updates it through <see cref="SetRequestedIconPixelSize"/>.
    /// </summary>
    private int _requestedIconPixelSize = IconPixelSize.Default;

    /// <summary>
    /// Number of icon-load requests submitted but not yet resolved to a terminal outcome (<see cref="IconLoadOutcome.Loaded"/>/
    /// <see cref="IconLoadOutcome.Unavailable"/>/<see cref="IconLoadOutcome.Failed"/>/<see cref="IconLoadOutcome.Dropped"/>).
    /// Backs <see cref="HasPendingIconLoads"/>.
    /// </summary>
    private int _pendingIconLoadCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseViewModel"/> class.
    /// </summary>
    /// <param name="iconProvider">Resolves item icons and this panel's cache/type-icon peeks/fallback.</param>
    /// <param name="iconLoadQueue">
    /// Background icon-load queue (ADR-046). <see langword="null"/> (production DI always supplies the real
    /// singleton) — together with a calling thread that has no <see cref="SynchronizationContext"/> — falls
    /// back to <see cref="SynchronousIconLoadQueue"/> (D-13): an older test's <c>new ChildWindowViewModel(...)</c>
    /// built with fewer constructor arguments, or any headless caller, keeps seeing the exact same
    /// synchronous, inline icon resolution as before this class grew a queue.
    /// </param>
    /// <param name="itemProbeQueue">
    /// Off-UI-thread existence/directory-ness probe queue for <see cref="RefreshItemsLivenessAsync"/>
    /// (ADR-046 7c). <see langword="null"/> — under the same rule as <paramref name="iconLoadQueue"/> —
    /// falls back to <see cref="SynchronousItemProbeQueue"/> (D-13): the exact synchronous
    /// <c>File.Exists</c>/<c>Directory.Exists</c> pair <c>ChildWindowViewModel.RefreshExistence</c> used
    /// to make directly, so a test/headless caller that never supplies one sees unchanged, synchronous
    /// timing.
    /// </param>
    protected BaseViewModel(IIconProvider iconProvider, IIconLoadQueue? iconLoadQueue = null, IItemProbeQueue? itemProbeQueue = null)
    {
        _iconProvider = iconProvider;

        // L-4 (review it.2): a real queue was supplied by the caller/DI, but this constructing thread has
        // no live SynchronizationContext to deliver results/resume an await through — D-13's fallback rule
        // (see ResolveItemProbeQueue/ResolveIconLoadQueue) silently degrades to the synchronous legacy
        // path in that case. That is the correct, safe behavior for a genuinely headless/test caller, but
        // in PRODUCTION it usually means a ViewModel got constructed before the UI dispatcher's own
        // SynchronizationContext was installed — worth a diagnostic, not a silent, permanent regression to
        // synchronous fs/shell calls on whatever thread this happens to be. Computed here (constructor
        // body, not a virtual call — a subclass's own fields/dependencies, e.g.
        // ChildWindowViewModel's `_diagnostics`, are not yet assigned while THIS base constructor runs) so
        // a subclass can inspect these two flags AFTER its own `base(...)` call returns and log them
        // through whatever diagnostics dependency it has.
        UsedSynchronousItemProbeQueueFallbackDespiteSupplied = itemProbeQueue is not null && SynchronizationContext.Current is null;
        UsedSynchronousIconLoadQueueFallbackDespiteSupplied = iconLoadQueue is not null && SynchronizationContext.Current is null;

        _itemProbeQueue = ResolveItemProbeQueue(itemProbeQueue);

        // Must happen in the constructor BODY (not a field initializer): CreateGroup's onResults callback
        // is a method group conversion of an instance method (OnIconResults), which a field initializer
        // is not allowed to reference (CS0236).
        _iconLoadGroup = ResolveIconLoadQueue(iconProvider, iconLoadQueue).CreateGroup(
            IconLoadPriority.Visible, SynchronizationContext.Current ?? new SynchronizationContext(), OnIconResults);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Collection of file items to be displayed in the UI.
    /// This is a filtered projection of <see cref="AllItems"/>; do not mutate directly.
    /// </summary>
    public ObservableCollection<FileItem> FileItems => _fileItems;

    /// <summary>
    /// The file item currently selected by the user.
    /// </summary>
    public FileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (_selectedFile == value) return;
            _selectedFile = value;
            OnPropertyChanged(nameof(SelectedFile));
            OnSelectedFileChanged();
        }
    }

    /// <summary>
    /// Incremented every time <see cref="ReplaceAll"/> rebuilds the full item set. Test seam; also the
    /// guard a future asynchronous liveness pass (ADR-046 7c) uses to detect that the item set was
    /// rebuilt out from under an in-flight snapshot.
    /// </summary>
    internal int ItemsGeneration { get; private set; }

    /// <summary>
    /// Test seam (ADR-046): <see langword="true"/> while at least one submitted icon-load request has not
    /// yet resolved to a terminal outcome.
    /// </summary>
    internal bool HasPendingIconLoads => _pendingIconLoadCount > 0;

    /// <summary>
    /// L-4 (review it.2): <see langword="true"/> when a real <see cref="IItemProbeQueue"/> was supplied to
    /// the constructor but this instance nonetheless fell back to <see cref="SynchronousItemProbeQueue"/>
    /// because the constructing thread had no live <see cref="SynchronizationContext"/> (ADR-046 D-13). A
    /// subclass with its own diagnostics dependency can log this — see this class's constructor remarks
    /// for why it isn't logged directly from here.
    /// </summary>
    internal bool UsedSynchronousItemProbeQueueFallbackDespiteSupplied { get; }

    /// <summary>
    /// L-4 (review it.2): the <see cref="IIconLoadQueue"/> counterpart of
    /// <see cref="UsedSynchronousItemProbeQueueFallbackDespiteSupplied"/>.
    /// </summary>
    internal bool UsedSynchronousIconLoadQueueFallbackDespiteSupplied { get; }

    /// <summary>
    /// The full, unfiltered set of items managed by this panel.
    /// Subclasses use this as the source when computing filter projections.
    /// </summary>
    protected internal IReadOnlyList<FileItem> AllItems => _allItems;

    /// <summary>
    /// Gets the snapped physical pixel size every item icon of this panel is requested at (see
    /// <see cref="SetRequestedIconPixelSize"/>). Used for every icon request this class makes — add,
    /// rename, reappearance and placeholder retry alike — so one panel never mixes icon resolutions.
    /// </summary>
    protected internal int RequestedIconPixelSize => _requestedIconPixelSize;

    /// <summary>
    /// Gets or sets this panel's <see cref="IIconLoadGroup"/> scheduling priority (ADR-046 D-6):
    /// <see cref="IconLoadPriority.Visible"/> while the panel's window is shown (the current page),
    /// <see cref="IconLoadPriority.Background"/> otherwise. Forwards directly to the group — setting it
    /// re-prioritizes every job of this panel still pending in the queue.
    /// </summary>
    protected internal IconLoadPriority IconLoadPriority
    {
        get => _iconLoadGroup.Priority;
        set => _iconLoadGroup.Priority = value;
    }

    /// <summary>
    /// Re-indexes <see cref="_itemsByPath"/> for <paramref name="item"/> after a successful physical
    /// rename (see <c>ChildWindowViewModel.CommitRenameCommand</c>) and relocates the item in place via
    /// <see cref="FileItem.RelocateInPlace"/> — the same <see cref="FileItem"/> instance is kept (rather
    /// than replaced), so any bound selection/UI state referencing it survives. No-op (and no
    /// <see cref="OnSourceChanged"/> raised) if <paramref name="item"/> isn't currently tracked by this
    /// ViewModel. A fresh icon fetch for <paramref name="newPath"/> is submitted to the background queue
    /// (ADR-046) — never resolved synchronously on the calling (UI) thread.
    /// </summary>
    /// <remarks>
    /// Does not call <see cref="OnSourceChanged"/> itself — the caller (<c>ChildWindowViewModel</c>)
    /// still needs to re-run its content filter afterwards, since a rename can change the extension and
    /// therefore <see cref="FileItem.IsShortcut"/>; the caller does that explicitly so it can also fold
    /// it into whatever single collection-change notification it's about to raise.
    /// </remarks>
    /// <param name="item">The item being renamed. Must be an instance currently in <see cref="AllItems"/>.</param>
    /// <param name="newPath">The item's new full path, as returned by the rename service.</param>
    internal void TryRenameItem(FileItem item, string newPath)
    {
        if (!_allItems.Contains(item)) return;

        _itemsByPath.Remove(item.Path);

        // A rename can never collide with an already-tracked path (the rename service itself rejects
        // NameTaken), but guard anyway: dedup the same way AddFilesCore does, rather than risk two
        // entries in _itemsByPath pointing at the same FileItem.
        _itemsByPath[newPath] = item;

        // A cache hit for the new path is free (no fs/shell — TryPeekCachedIcon); otherwise keep showing
        // the item's current icon (usually still visually correct for a mere rename) rather than
        // regressing to a placeholder, while the queued Submit below fetches the definitive one.
        var icon = _iconProvider.TryPeekCachedIcon(newPath, _requestedIconPixelSize, out var cached) ? cached : item.Icon;
        item.RelocateInPlace(newPath, icon);

        SubmitIconRequests([item]);
    }

    /// <summary>
    /// Fire-and-forget counterpart to <see cref="RefreshItemsLivenessAsync"/> — the name every existing
    /// caller/test already uses. See that method's own remarks for the full pipeline and its coalescing
    /// behavior.
    /// </summary>
    /// <remarks>
    /// L-1 (review it.2): the returned <see cref="Task"/> "never faults except via an unexpected exception
    /// from <see cref="OnItemsVanished"/>/<see cref="OnProbeResultsApplied"/> themselves" (see
    /// <see cref="RefreshItemsLivenessAsync"/>'s own remarks) — a genuine bug in one of those overrides
    /// would otherwise become an UNOBSERVED task exception here (this call site, unlike
    /// <c>ChildWindowViewModel.RefreshExistence</c>'s own dispatched tick, never awaits the task itself),
    /// silently losing the diagnostic. <see cref="OnLivenessPassFaulted"/> is invoked instead — on whatever
    /// thread the fault is first observed on — so a subclass can log it.
    /// </remarks>
    internal void RefreshItemsLiveness()
    {
        var task = RefreshItemsLivenessAsync();
        if (task.IsCompleted)
        {
            ObserveLivenessPassFault(task);
            return;
        }

        task.ContinueWith(
            ObserveLivenessPassFault,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Re-validates <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> for every tracked
    /// item against the current file-system state — off the UI thread, via <see cref="IItemProbeQueue"/>
    /// (ADR-046 7c) — and re-resolves the icon for any item that just transitioned from missing to
    /// present. <see cref="IIconProvider"/> deliberately never caches its placeholder result for a
    /// missing path (see <c>IconProvider.GetIcon</c>), so a single fresh request here is enough to
    /// replace a stuck placeholder with the file's real icon once the underlying file/folder actually
    /// reappears (e.g. a removable drive is remounted, or a stale path was resolved to its managed copy
    /// on the next <c>ChildWindowViewModel.LoadFromModel</c>) — P00 baseline finding H3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A content-change re-fetch for items that stay present throughout (e.g. an <c>.exe</c>'s embedded
    /// icon changing after an in-place update) is intentionally NOT performed here:
    /// <see cref="IIconProvider.GetIcon(string, int)"/> already does that freshness check (comparing
    /// <c>LastWriteTimeUtc</c>) once actually resolved on the queue's worker, but re-submitting for every
    /// tracked item on every liveness tick would reintroduce the exact cost this panel's icon pipeline
    /// already measured as expensive for large panels (~2.8ms/item — see
    /// docs/DIAGNOSTICS/icon-loading-findings.md). The missing → present transition this method reacts
    /// to is comparatively rare and bounded by how many items were actually broken, not by the panel's
    /// full size.
    /// </para>
    /// <para>
    /// <b>M3 (P08 fix cycle 1, 2026-09-22):</b> the missing→present transition above only ever fires
    /// once per disappearance. A file that is <see cref="FileItem.Exists"/> throughout but whose
    /// <em>icon retrieval itself</em> failed transiently — e.g. <c>SHGetFileInfoW</c> called before the
    /// shell has finished initializing during autostart — used to leave the item stuck on
    /// <see cref="IIconProvider"/>'s placeholder for the rest of the session. Every existing, still-
    /// placeholder item is retried too, bounded to <see cref="MaxPlaceholderRetryAttempts"/> attempts
    /// with a growing per-tick backoff (see <see cref="RetryPlaceholderIcon"/>).
    /// </para>
    /// <para>
    /// <b>Coalescing (ADR-046 7c):</b> a call that arrives while a pass is already in flight does not
    /// start a second, concurrent pass — it sets <see cref="_livenessRerunRequested"/> and returns the
    /// SAME <see cref="Task"/> the in-flight pass's caller is awaiting; once that pass (and every rerun
    /// it triggers) finally completes, the returned task completes too. This is what lets
    /// <c>ChildWindowViewModel.ReconcileAfterShellCommand</c>/<c>DeleteManagedItem</c> call this
    /// unconditionally at their tail without ever racing a still-in-flight tick from
    /// <c>RefreshExistence</c> — the rerun always re-snapshots <see cref="AllItems"/> from scratch, so the
    /// caller is guaranteed a pass that started no earlier than its own call.
    /// </para>
    /// <para>
    /// <b>Snapshot / apply guards:</b> the pass takes an up-front snapshot of every tracked item's path
    /// and <see cref="FileItem.Exists"/>, probes it off-thread, and — only if
    /// <see cref="BaseViewModel.ItemsGeneration"/> hasn't moved on (no <c>ReplaceAll</c> raced the pass)
    /// and <see cref="CanApplyLivenessResults"/> still says yes — applies each result to whichever
    /// <see cref="FileItem"/> is STILL tracked under that exact path (<c>ReferenceEquals</c>),
    /// silently skipping a path that was renamed/removed out from under the snapshot. A pass that
    /// exceeds <see cref="LivenessPassTimeout"/>, or whose <see cref="IItemProbeQueue.ProbeAsync"/> call
    /// observes cancellation (<see cref="DisposeIconLoading"/>), is discarded WHOLESALE — never applied
    /// partially, which would otherwise report a present→missing (or missing→present) transition more
    /// than once for the same disappearance.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A <see cref="Task"/> that completes once the freshest liveness pass this call could observe has
    /// been applied (or discarded) — never faults except via an unexpected exception from
    /// <see cref="OnItemsVanished"/>/<see cref="OnProbeResultsApplied"/> themselves.
    /// </returns>
    internal Task RefreshItemsLivenessAsync()
    {
        if (_livenessPassTask is { } inFlight)
        {
            _livenessRerunRequested = true;
            return inFlight;
        }

        var task = RunLivenessPassesAsync();

        // D-13: when the pass (and every rerun it triggered) ran FULLY SYNCHRONOUSLY to completion — the
        // whole point of the D-13 fallback (SynchronousItemProbeQueue always returns an already-completed
        // task, and ProbeWithTimeoutAsync's own fast path never suspends on one) — RunLivenessPassesAsync's
        // own `finally` has ALREADY reset _livenessPassTask back to null by the time this line runs. Do
        // NOT resurrect a stale, already-completed task reference here: only genuinely still-running work
        // needs to be recorded for the coalescing check above, or the NEXT call would wrongly believe a
        // pass is in flight (setting _livenessRerunRequested on a task nothing is ever going to rerun) and
        // silently do nothing instead of starting a fresh pass.
        if (!task.IsCompleted) _livenessPassTask = task;

        return task;
    }

    /// <summary>
    /// UX-MINOR (review it.2, M-1 follow-up): resets every still-placeholder item's exhausted
    /// <see cref="RetryPlaceholderIcon"/> budget and re-submits it, giving a panel that just became visible
    /// again (see <c>ChildWindowViewModel.SetIconLoadVisibility</c>) one more chance at whatever items were
    /// retried out (<see cref="MaxPlaceholderRetryAttempts"/> exhausted) while it was in the background —
    /// rather than leaving them stuck on the placeholder for the rest of the session just because nobody
    /// was looking at them during the retry window. A no-op for an item whose retry budget was never
    /// actually exhausted (the normal per-tick backoff already covers that case) or which isn't worth
    /// retrying to begin with — the exact same "worth it" condition <see cref="RunSingleLivenessPassAsync"/>
    /// itself uses (N-3, small iteration 3): a still-placeholder icon, OR a real one whose most recently
    /// resolved request nonetheless ended <see cref="HasUnhealthyLastOutcome">unhealthily</see>.
    /// </summary>
    internal void ResetExhaustedPlaceholderRetries()
    {
        List<FileItem>? toResubmit = null;

        foreach (var item in _allItems)
        {
            if (!item.Exists) continue;
            if (!_iconProvider.IsPlaceholder(item.Icon) && !HasUnhealthyLastOutcome(item)) continue;
            if (!_placeholderRetries.TryGetValue(item, out var retryState) || retryState.AttemptsRemaining > 0) continue;

            retryState.AttemptsRemaining = MaxPlaceholderRetryAttempts;
            retryState.TicksUntilNextAttempt = 0;
            (toResubmit ??= []).Add(item);
        }

        if (toResubmit is not null) SubmitIconRequests(toResubmit);
    }

    /// <summary>
    /// Adds an array of file paths to the full collection after validating them, ensuring no duplicates.
    /// Calls <see cref="OnSourceChanged"/> after mutation so subclasses can re-apply their filter —
    /// skipped entirely when nothing was actually added (e.g. a watcher event for a path already
    /// tracked), which avoids an unnecessary full <see cref="FileItems"/> rebuild.
    /// </summary>
    /// <param name="files">Array of file paths to be added.</param>
    protected internal void AddFiles(string[] files)
    {
        if (AddFilesCore(files)) OnSourceChanged();
    }

    /// <summary>
    /// Removes the <see cref="FileItem"/> whose <see cref="FileItem.Path"/> matches
    /// <paramref name="path"/>. No-op when no matching item is found.
    /// </summary>
    /// <param name="path">The file-system path of the item to remove.</param>
    protected internal void RemoveFileByPath(string path)
    {
        if (!_itemsByPath.TryGetValue(path, out var item)) return;
        ReleasePendingIconLoadIfInFlight(item);
        _allItems.Remove(item);
        _itemsByPath.Remove(path);
        OnSourceChanged();
    }

    /// <summary>
    /// Replaces the entire item set with items built from <paramref name="paths"/>.
    /// Used for a full resync after a watcher buffer overflow. Unlike <see cref="AddFiles"/>, this
    /// always triggers <see cref="OnSourceChanged"/> — even when the new set is empty or identical —
    /// because the underlying set was unconditionally cleared first.
    /// </summary>
    /// <param name="paths">The new set of file-system paths.</param>
    protected internal void ReplaceAll(IEnumerable<string> paths)
    {
        // ADR-046: any icon fetch still in flight for the outgoing item set must never land on a new
        // item that happens to reuse the same path — CancelPending bumps the group's generation, so the
        // queue itself never delivers those stale results to this group.
        _iconLoadGroup.CancelPending();
        ItemsGeneration++;

        // L-3 (review it.2): every outgoing item's own in-flight request (if any) is about to become
        // permanently unresolvable — CancelPending's generation bump above means the queue will never
        // deliver a terminal result for it to this group, so MarkTerminal (the only other place that
        // decrements this counter) will never run for it either. Without this reset, HasPendingIconLoads
        // would report stale in-flight requests from an item set that no longer even exists.
        _pendingIconLoadCount = 0;

        _allItems.Clear();
        _itemsByPath.Clear();

        // ADR-046 §6 item 10 (M-7.1 manual-verify timing): measures exactly the UI-thread cost the metric
        // cares about — building every FileItem (AddFilesCore, no fs/shell — D-5) plus the immediately
        // following content-filter/sort pass (OnSourceChanged) — for a large portal/model reload. Cheap
        // enough (a Stopwatch start/stop) to run unconditionally; OnItemsBuilt's default implementation
        // is a no-op, so the cost of actually LOGGING it is entirely gated by IDiagnosticsLog.IsEnabled
        // in whichever override does the logging.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        AddFilesCore(paths.ToArray());
        OnSourceChanged();
        stopwatch.Stop();

        OnItemsBuilt(_allItems.Count, (long)stopwatch.Elapsed.TotalMicroseconds);
    }

    /// <summary>
    /// Reorders <see cref="AllItems"/> in place according to <paramref name="comparer"/> — the
    /// "materialize the sort order" half of the <c>Name -&gt; Manual</c> auto-switch (ADR-014
    /// Decision 6, used by <c>ChildWindowViewModel.ReorderItem</c>). Does <b>not</b> touch
    /// <see cref="_itemsByPath"/> (membership doesn't change, only order) and does <b>not</b> call
    /// <see cref="OnSourceChanged"/> — the caller decides when (and whether) to re-project
    /// <see cref="FileItems"/>, since this is typically called immediately before flipping
    /// <c>Model.SortMode</c>, which triggers its own rebuild.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="List{T}.Sort(IComparer{T})"/>, which is not a stable sort in general — but
    /// every comparer this method is actually called with (<c>PanelItemSortComparer.ByName</c>) is a
    /// <i>total</i> order: it tie-breaks all the way down to <c>FileItem.Path</c>, so no two distinct
    /// items ever compare equal, and "stability" is moot — there's only one valid resulting order.
    /// </remarks>
    /// <param name="comparer">A total order over <see cref="FileItem"/>.</param>
    protected internal void SortAllItems(IComparer<FileItem> comparer) => _allItems.Sort(comparer);

    /// <summary>
    /// Moves <paramref name="item"/> to the insertion gap <paramref name="visibleInsertIndex"/> within
    /// the currently visible <see cref="FileItems"/> — the core of drag-to-reorder (ADR-014
    /// Decision 5). Operates on the <i>full</i> set (<see cref="AllItems"/>), not just the visible one,
    /// so a panel with an active content filter keeps its hidden items' relative order intact instead
    /// of silently collapsing them to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="visibleInsertIndex"/> is a gap, not an item index</b> — its valid range is
    /// <c>[0, FileItems.Count]</c> inclusive. A gap of <c>0</c> means "before the first visible item",
    /// a gap of <c>FileItems.Count</c> means "after the last visible item", and a gap of <c>i</c>
    /// (for <c>0 &lt; i &lt; FileItems.Count</c>) sits between the visible items currently at indices
    /// <c>i - 1</c> and <c>i</c>. A gap immediately touching <paramref name="item"/>'s own current
    /// position (i.e. dropping it back where it already is) is treated as a no-op — see step 2 below.
    /// </para>
    /// <para>
    /// <b>Anchoring rule for hidden items:</b> when a content filter hides part of <see cref="AllItems"/>,
    /// <see cref="FileItems"/> only shows a subset, so a naive index translation
    /// (<c>AllItems.Move(fullFrom, fullTo)</c> using the same numeric index that's valid for
    /// <see cref="FileItems"/>) would move the wrong element. Instead, the target position in
    /// <see cref="AllItems"/> is found by anchoring to <paramref name="item"/>'s new <i>visible</i>
    /// neighbor: the item is inserted immediately after the visible item that will end up just before
    /// it (or, if it's becoming the first visible item, immediately before the visible item that was at
    /// index <c>0</c>). This guarantees hidden items keep their relative order — none are dropped or
    /// pushed to the end — only <paramref name="item"/>'s position relative to its visible siblings
    /// changes. The one accepted asymmetry: any hidden items sitting between the two visible neighbors
    /// end up positioned <i>after</i> the moved item rather than before it (a "insert after the
    /// preceding visible neighbor" rule was chosen over "insert before the following visible neighbor"
    /// because the latter has no anchor to fall back on when dropping at the very end of the visible
    /// list, where no "following visible neighbor" exists).
    /// </para>
    /// <para>
    /// Does <b>not</b> touch <see cref="_itemsByPath"/> — the set of tracked paths is unchanged, only
    /// their order. Raises exactly one <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Move(int, int)"/>
    /// notification on <see cref="FileItems"/> (via <c>ObservableCollection&lt;T&gt;.Move</c>), never a
    /// full <c>Reset</c> — a <c>Reset</c> makes a bound <see cref="System.Windows.Controls.ListBox"/>
    /// forget its scroll position and selection (ADR-012), which would make the panel visibly "jump"
    /// at the exact moment the user releases the mouse after dragging.
    /// </para>
    /// </remarks>
    /// <param name="item">The item being dragged. Must currently be visible (in <see cref="FileItems"/>).</param>
    /// <param name="visibleInsertIndex">The insertion gap in <see cref="FileItems"/>, in <c>[0, FileItems.Count]</c>.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="item"/> was actually moved; <see langword="false"/> for
    /// a no-op (item not visible, or the gap is immediately before/after the item's own current position).
    /// </returns>
    protected internal bool MoveItem(FileItem item, int visibleInsertIndex)
    {
        var vFrom = _fileItems.IndexOf(item);
        if (vFrom < 0) return false;

        // A gap immediately before or after the item's own current position is a drop back onto
        // itself — nothing moves.
        if (visibleInsertIndex == vFrom || visibleInsertIndex == vFrom + 1) return false;

        var vTo = visibleInsertIndex > vFrom ? visibleInsertIndex - 1 : visibleInsertIndex;

        _allItems.Remove(item);

        // rest = the visible list, without `item` — i.e. what FileItems looks like immediately after
        // the removal above, before the re-insertion below. Anchor the full-set insertion point to
        // rest's neighbor at the target visible position (see the anchoring rule in the remarks).
        var rest = _fileItems.Where(f => !ReferenceEquals(f, item)).ToList();

        int allIndex;
        if (rest.Count == 0)
        {
            allIndex = 0;
        }
        else if (vTo == 0)
        {
            allIndex = _allItems.IndexOf(rest[0]); // insert BEFORE the first visible item
        }
        else
        {
            allIndex = _allItems.IndexOf(rest[vTo - 1]) + 1; // insert right AFTER the preceding visible item
        }

        _allItems.Insert(allIndex, item);
        _fileItems.Move(vFrom, vTo);

        return true;
    }

    /// <summary>
    /// Re-submits every tracked item's icon request at the current <see cref="RequestedIconPixelSize"/>
    /// to the background queue (ADR-046). Split out of <see cref="SetRequestedIconPixelSize"/> (M3,
    /// release-polish 1.0 review) so a subclass can defer/debounce this — the actually expensive part
    /// of a size change — separately from updating <see cref="RequestedIconPixelSize"/> itself, which must
    /// stay synchronous (see that method's own remarks). Declared <see langword="protected internal"/> —
    /// ahead of <see cref="SetRequestedIconPixelSize"/> below — to keep this class's member ordering
    /// (public, internal, protected internal, protected, private) SA1202-clean.
    /// </summary>
    protected internal void RefreshAllItemIcons()
    {
        // ADR-046: cancel whatever the previous size's requests were — the size-mismatch guard in
        // OnIconResults already tolerates an old-size Loaded arriving late, but there is no reason to let
        // the queue keep resolving requests for a size nobody wants anymore. Existing icons are left
        // completely untouched here (no flash to a placeholder while the new size resolves).
        _iconLoadGroup.CancelPending();

        foreach (var item in _allItems) _placeholderRetries.Remove(item);
        SubmitIconRequests(_allItems);
    }

    /// <summary>
    /// Changes the pixel size this panel's icons are requested at and, when it actually changed (after
    /// snapping — <see cref="IconPixelSize.Snap"/>), re-submits the icon request of every tracked item at
    /// the new size. The per-panel icon size slider is continuous, but the snapped size only changes at
    /// bucket boundaries, so dragging it costs one re-submit per crossed bucket, not one per tick;
    /// revisiting a size is a cache hit in <see cref="IIconProvider"/>.
    /// </summary>
    /// <remarks>
    /// Any pending placeholder-retry bookkeeping is dropped for re-submitted items: the fresh request
    /// below is itself a new attempt, and <see cref="RefreshItemsLiveness"/> starts over for an item that
    /// is still on the placeholder afterwards.
    /// </remarks>
    /// <param name="pixelSize">Desired physical pixel size (DIP size × DPI scale); snapped here.</param>
    /// <param name="refreshExisting">
    /// <see langword="false"/> when the caller is about to rebuild the whole item set anyway (e.g. right
    /// before <see cref="ReplaceAll"/>), so the existing items are not fetched twice, OR when the caller
    /// wants the size to take effect immediately (so a new item added right after this call is fetched
    /// at the right size — see <see cref="RequestedIconPixelSize"/>) while deferring the actual refetch of
    /// already-tracked items to its own debounced call to <see cref="RefreshAllItemIcons"/> (M3,
    /// release-polish 1.0 review — see <c>ChildWindowViewModel.UpdateRequestedIconPixelSize</c>).
    /// </param>
    /// <returns><see langword="true"/> when the snapped size changed.</returns>
    protected bool SetRequestedIconPixelSize(int pixelSize, bool refreshExisting = true)
    {
        var snapped = IconPixelSize.Snap(pixelSize);
        if (snapped == _requestedIconPixelSize) return false;

        _requestedIconPixelSize = snapped;
        if (refreshExisting) RefreshAllItemIcons();

        return true;
    }

    /// <summary>
    /// Removes a file item from the full collection.
    /// </summary>
    /// <param name="file">File item to be removed.</param>
    protected void RemoveFile(FileItem file)
    {
        if (!_allItems.Remove(file)) return;
        ReleasePendingIconLoadIfInFlight(file);
        _itemsByPath.Remove(file.Path);
        OnSourceChanged();
    }

    /// <summary>
    /// Called whenever <see cref="AllItems"/> changes. The default implementation
    /// projects all items into <see cref="FileItems"/> unchanged (no filtering).
    /// Override in subclasses to apply a filter predicate.
    /// </summary>
    protected virtual void OnSourceChanged() => ReplaceFileItems(_allItems);

    /// <summary>
    /// Hook invoked once per <see cref="ReplaceAll"/> call (ADR-046 §6 item 10, M-7.1) with the elapsed
    /// UI-thread time of rebuilding the whole item set — <c>AddFilesCore</c> plus the immediately
    /// following <see cref="OnSourceChanged"/>. The default implementation is a no-op;
    /// <c>ChildWindowViewModel</c> overrides this to record <c>IGestureDiagnostics.PanelItemsBuilt</c>.
    /// </summary>
    /// <param name="count">The number of items <see cref="AllItems"/> holds after the rebuild.</param>
    /// <param name="elapsedMicroseconds">Elapsed wall-clock time of the rebuild, in microseconds.</param>
    protected virtual void OnItemsBuilt(int count, long elapsedMicroseconds)
    {
    }

    /// <summary>
    /// Releases this panel's background icon-load group (ADR-046) — cancels anything still pending and
    /// guarantees no further delivery — and cancels (never disposes — see <see cref="_livenessCts"/>'s
    /// own remarks) any in-flight <see cref="RefreshItemsLivenessAsync"/> pass's probe call (ADR-046 7c).
    /// Subclasses call this from their own <c>Dispose</c>.
    /// </summary>
    protected void DisposeIconLoading()
    {
        _iconLoadGroup.Dispose();
        _livenessCts.Cancel();
    }

    /// <summary>
    /// Gets a value controlling whether a just-completed <see cref="RefreshItemsLivenessAsync"/> pass may
    /// actually apply its results (ADR-046 7c) — checked AFTER the off-thread probe returns, immediately
    /// before touching any <see cref="FileItem"/>. The default implementation always allows it;
    /// <c>ChildWindowViewModel</c> overrides this to also refuse while disposed or while a shell
    /// operation's own modal loop is mid-flight on this panel (the same guard its synchronous
    /// predecessor checked before ever starting a pass — now checked at apply time instead, since the
    /// pass itself may have started before either condition became true).
    /// </summary>
    protected virtual bool CanApplyLivenessResults => true;

    /// <summary>
    /// Gets the clock <see cref="RefreshItemsLivenessAsync"/> times <see cref="LivenessPassTimeout"/>
    /// against (C-7: no wall-clock tests). The default implementation is the real clock;
    /// <c>ChildWindowViewModel</c> overrides this with an injectable <see cref="TimeProvider"/> so tests
    /// can drive the timeout deterministically.
    /// </summary>
    protected virtual TimeProvider LivenessTimeProvider => TimeProvider.System;

    /// <summary>
    /// Hook invoked at most once per delivered result batch (ADR-046), when that batch actually flipped
    /// at least one item's <see cref="FileItem.IsDirectory"/> via <see cref="FileItem.ApplyProbe"/>. The
    /// default implementation re-runs the (unfiltered) projection via <see cref="OnSourceChanged"/>;
    /// <c>ChildWindowViewModel</c> overrides this to debounce its own content-filter re-run instead of
    /// re-filtering once per batch.
    /// </summary>
    /// <param name="directoryFlagsChanged">
    /// <see langword="true"/> when at least one item in the batch changed <see cref="FileItem.IsDirectory"/>.
    /// Always <see langword="true"/> when this hook is actually invoked (ADR-046 §6) — kept as a parameter
    /// so an override can log/assert on it without re-deriving it.
    /// </param>
    protected virtual void OnProbeResultsApplied(bool directoryFlagsChanged)
    {
        if (directoryFlagsChanged) OnSourceChanged();
    }

    /// <summary>
    /// Replaces <see cref="FileItems"/> with <paramref name="items"/> in a single batch, raising exactly
    /// one <see cref="System.Collections.Specialized.NotifyCollectionChangedEventArgs"/> (<c>Reset</c>),
    /// regardless of how many elements are added or removed. Subclasses that override
    /// <see cref="OnSourceChanged"/> to apply a filter (e.g. content-filter projections) should call this
    /// instead of mutating <see cref="FileItems"/> by hand with <c>Clear()</c> + <c>Add()</c>.
    /// </summary>
    /// <remarks>
    /// A <c>Reset</c> notification makes a bound <see cref="System.Windows.Controls.ListBox"/> forget
    /// its <c>SelectedItem</c> (known WPF behavior, see ADR-012). Rebuilding <see cref="FileItems"/> —
    /// e.g. on a content-filter switch or a Folder Portal resync tick — must not silently clear the
    /// user's selection, so the currently selected item's <see cref="FileItem.Path"/> is remembered
    /// before the swap and re-applied afterwards (case-insensitive, matching Windows path semantics),
    /// falling back to <see langword="null"/> if the item is no longer present in the new set.
    /// </remarks>
    /// <param name="items">The new, already-filtered contents of <see cref="FileItems"/>.</param>
    protected void ReplaceFileItems(IEnumerable<FileItem> items)
    {
        // Materialize once: `items` is typically a lazy LINQ projection (e.g. `AllItems.Where(...)`),
        // and it's both enumerated for the comparison below and handed to `_fileItems.ReplaceAll`.
        var materialized = items as IList<FileItem> ?? items.ToList();

        // Early exit (ADR-014 Decision 8): if the new projection is reference-for-reference identical
        // to what `_fileItems` already holds, skip the rebuild entirely — no `ReplaceAll` (so no
        // `Reset`), nothing to restore the selection from (it was never disturbed). This is what keeps
        // the `Name -> Manual` drag-triggered switch (see `ChildWindowViewModel.ReorderItem`) from
        // visibly "jumping": `SortAllItems` materializes `_allItems` into the same order the view was
        // already showing, so the very next `ApplyFilterAndSort` produces a projection identical to the
        // current one and lands here.
        if (SequenceEqualByReference(_fileItems, materialized)) return;

        var selectedPath = _selectedFile?.Path;

        _fileItems.ReplaceAll(materialized);

        if (selectedPath is not null)
        {
            SelectedFile = _fileItems.FirstOrDefault(
                f => string.Equals(f.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Hook invoked by <see cref="RefreshItemsLiveness"/> — once per call, after the whole refresh — with every item
    /// that was present on the previous check and is missing now (the present → missing transition only; an item
    /// that stays missing is never reported again, so a stable panel costs nothing extra). The default
    /// implementation is a no-op; <c>ChildWindowViewModel</c> reconciles vanished app-managed items (e.g. Cut in
    /// the shell menu followed by Paste elsewhere, which happens after the menu already closed). An override may
    /// remove or relocate the reported items.
    /// </summary>
    /// <param name="items">The items that just went missing, in panel order.</param>
    protected virtual void OnItemsVanished(IReadOnlyList<FileItem> items)
    {
    }

    /// <summary>
    /// Hook invoked (L-1, review it.2) when a <see cref="RefreshItemsLiveness"/>-started pass's
    /// <see cref="Task"/> faults — an unexpected exception from <see cref="OnItemsVanished"/> or
    /// <see cref="OnProbeResultsApplied"/> themselves; every other step of the pipeline already degrades
    /// to a no-op/discarded pass instead of faulting. Called on whatever thread the fault is first
    /// observed on (possibly a thread-pool continuation, never assumed to be the UI thread). The default
    /// implementation is a no-op; <c>ChildWindowViewModel</c> routes this to the same structured
    /// diagnostics log its own <c>RefreshExistence</c> dispatch already uses.
    /// </summary>
    /// <param name="exception">The exception the faulted pass's task completed with.</param>
    protected virtual void OnLivenessPassFaulted(Exception exception)
    {
    }

    /// <summary>
    /// Hook for subclasses to supply a cosmetic <see cref="FileItem.DisplayName"/> override for a
    /// newly constructed item, e.g. stripping a legacy managed-storage GUID suffix (see
    /// <c>ChildWindowViewModel</c>, ADR-013 Decision 3). The default implementation returns
    /// <see langword="null"/> — no override, <see cref="FileItem.DisplayName"/> falls back to
    /// <see cref="FileItem.Name"/>.
    /// </summary>
    /// <param name="path">The candidate item's file-system path.</param>
    protected virtual string? ResolveDisplayName(string path) => null;

    /// <summary>
    /// Hook for subclasses to report whether a newly added <paramref name="item"/> would currently be
    /// visible (ADR-046 D-6): <see cref="AddFilesCore"/> submits icon requests for the visible items
    /// first, so a Folder Portal loading a thousand hidden-by-filter files never starves the handful the
    /// user can actually see right now. The default implementation always returns <see langword="true"/>
    /// (a base panel with no content filter shows everything).
    /// </summary>
    /// <param name="item">The candidate item, already added to <see cref="AllItems"/>.</param>
    protected virtual bool IsItemCurrentlyVisible(FileItem item) => true;

    /// <summary>
    /// L-5 (review it.2): builds the visibility check <see cref="AddFilesCore"/> applies to a whole batch
    /// of newly added items — called ONCE per <see cref="AddFilesCore"/> call, not once per item, so an
    /// override that would otherwise re-derive an equivalent predicate per item (e.g.
    /// <c>ChildWindowViewModel</c> re-running its content-filter switch for every one of a thousand
    /// portal-loaded files) builds it a single time instead. The default implementation simply forwards to
    /// <see cref="IsItemCurrentlyVisible"/> per item (a method group — no allocation of its own), preserving
    /// the exact current behavior for a base panel/any override that only customises the single-item hook.
    /// </summary>
    protected virtual Func<FileItem, bool> CreateVisibilityPredicate() => IsItemCurrentlyVisible;

    /// <summary>
    /// Notifies about property changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that was changed.</param>
    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Hook invoked immediately after <see cref="SelectedFile"/> actually changes to a new value
    /// (including changing to <see langword="null"/>) — i.e. after the equality guard in the
    /// <see cref="SelectedFile"/> setter, so it never fires for a no-op assignment. The default
    /// implementation is a no-op. <see cref="ChildWindowViewModel"/> overrides this to integrate with
    /// <see cref="Services.IPanelSelectionCoordinator"/> (ui-polishing-1 §2.4 Block A2): claiming the
    /// global cross-panel selection whenever this panel's own selection becomes non-null.
    /// </summary>
    protected virtual void OnSelectedFileChanged()
    {
    }

    /// <summary>
    /// Reference-equality (not <see cref="FileItem"/> value-equality) sequence comparison, avoiding an
    /// allocating LINQ <c>SequenceEqual</c> with a custom comparer for this hot, frequently-called path.
    /// </summary>
    private static bool SequenceEqualByReference(IList<FileItem> left, IList<FileItem> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i])) return false;
        }

        return true;
    }

    /// <summary>
    /// ADR-046 D-13: resolves the real queue when one was actually supplied AND the constructing
    /// (UI) thread has a live <see cref="SynchronizationContext"/> to deliver results through; otherwise
    /// falls back to <see cref="SynchronousIconLoadQueue"/>, which never posts anywhere and instead
    /// resolves + delivers every request inline, synchronously, from inside <c>Submit</c> — exactly the
    /// pre-ADR-046 behavior a plain-thread unit test (no dispatcher, hence no <see cref="SynchronizationContext"/>)
    /// or an older test's fewer-argument constructor call already relies on.
    /// </summary>
    private static IIconLoadQueue ResolveIconLoadQueue(IIconProvider provider, IIconLoadQueue? suppliedQueue)
        => suppliedQueue is not null && SynchronizationContext.Current is not null
            ? suppliedQueue
            : new SynchronousIconLoadQueue(provider);

    /// <summary>
    /// ADR-046 7c/D-13: the same resolution rule as <see cref="ResolveIconLoadQueue"/>, for the liveness
    /// probe queue — falls back to <see cref="SynchronousItemProbeQueue"/> when none was supplied or the
    /// constructing thread has no live <see cref="SynchronizationContext"/> to resume an <c>await</c> on.
    /// </summary>
    private static IItemProbeQueue ResolveItemProbeQueue(IItemProbeQueue? suppliedQueue)
        => suppliedQueue is not null && SynchronizationContext.Current is not null
            ? suppliedQueue
            : new SynchronousItemProbeQueue();

    /// <summary>
    /// Runs <see cref="RunSingleLivenessPassAsync"/> in a loop, re-running once more whenever
    /// <see cref="_livenessRerunRequested"/> was set while the previous pass was in flight — see
    /// <see cref="RefreshItemsLivenessAsync"/>'s own remarks.
    /// </summary>
    private async Task RunLivenessPassesAsync()
    {
        try
        {
            do
            {
                _livenessRerunRequested = false;
                await RunSingleLivenessPassAsync();
            }
            while (_livenessRerunRequested);
        }
        finally
        {
            _livenessPassTask = null;
        }
    }

    /// <summary>
    /// L-1 (review it.2): observes <paramref name="task"/>'s exception (if any) — reading
    /// <see cref="Task.Exception"/> marks it observed, which is what actually prevents an
    /// <see cref="AppDomain.UnhandledException"/>-adjacent "unobserved task exception" for
    /// <see cref="RefreshItemsLiveness"/>'s fire-and-forget call, regardless of whether
    /// <see cref="OnLivenessPassFaulted"/> itself does anything — and forwards the (unwrapped) exception to
    /// it. A no-op for a task that completed successfully or was cancelled (disposal mid-flight — already
    /// an expected, silent outcome, not a fault to report).
    /// </summary>
    /// <param name="task">The completed <see cref="RefreshItemsLiveness"/> pass task.</param>
    private void ObserveLivenessPassFault(Task task)
    {
        if (task.Exception is not { } aggregate) return;
        OnLivenessPassFaulted(aggregate.GetBaseException());
    }

    /// <summary>One liveness pass: snapshot → probe → apply. See <see cref="RefreshItemsLivenessAsync"/>'s remarks.</summary>
    private async Task RunSingleLivenessPassAsync()
    {
        if (_allItems.Count == 0) return;

        var generationAtSnapshot = ItemsGeneration;
        var paths = new string[_allItems.Count];
        var snapshotItems = new FileItem[_allItems.Count];
        var wasExisting = new bool[_allItems.Count];
        var wasProbePending = new bool[_allItems.Count];
        for (var i = 0; i < _allItems.Count; i++)
        {
            var item = _allItems[i];
            paths[i] = item.Path;
            snapshotItems[i] = item;
            wasExisting[i] = item.Exists;
            wasProbePending[i] = item.IsProbePending;
        }

        IReadOnlyList<PathProbeResult>? results;
        try
        {
            results = await ProbeWithTimeoutAsync(paths, _livenessCts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Disposed mid-flight (or the token was already cancelled) — nothing to apply.
        }

        if (results is null) return; // LivenessPassTimeout: the whole pass is discarded (see remarks).
        if (generationAtSnapshot != ItemsGeneration) return; // ReplaceAll rebuilt the item set meanwhile.
        if (!CanApplyLivenessResults) return;

        List<FileItem>? vanished = null;
        var directoryFlagsChanged = false;

        for (var i = 0; i < snapshotItems.Length; i++)
        {
            var item = snapshotItems[i];

            // Rename/delete race guard: only apply this result if `item` is STILL the exact instance
            // tracked under the path it was snapshotted at — a rename removes the old key entirely (see
            // TryRenameItem), so a stale path simply misses here rather than clobbering whatever (if
            // anything) now lives at that key.
            if (!_itemsByPath.TryGetValue(paths[i], out var current) || !ReferenceEquals(current, item)) continue;

            var result = results[i];
            var missingBefore = !wasExisting[i];

            // N-2 (small iteration 3, ADR-046 D-3): a short-circuited probe (the remote-root circuit
            // breaker was already open or half-open) never touched the file system at all — IsKnown is
            // false and Exists is always reported as false purely to satisfy PathProbeResult's shape, NOT
            // as an actual observation (see PathProbe.Probe's own short-circuit branch). Applying it here
            // would flip a confirmed-present item on a quarantined remote root to "missing" — and report a
            // bogus vanish — for as long as the breaker stays open, even though nothing was ever actually
            // checked. Leave the item exactly as it was; the very next pass, once the breaker's half-open
            // trial actually succeeds (or the item is genuinely re-probed), applies a REAL result.
            if (!result.IsKnown) continue;

            if (item.ApplyProbe(result.Exists, result.IsDirectory)) directoryFlagsChanged = true;

            // A pending item's optimistic Exists == true (CreatePending, D-5) is a GUESS, not a confirmed
            // observation — its first-ever probe coming back false is "this item doesn't exist", never a
            // present→missing TRANSITION (there was nothing to transition FROM). Reporting it as vanished
            // here would wrongly hand a brand-new, never-actually-seen-alive item to
            // ChildWindowViewModel.OnItemsVanished's managed-item reconcile pipeline.
            if (!missingBefore && !wasProbePending[i] && !result.Exists)
            {
                (vanished ??= []).Add(item);
            }

            if (missingBefore && result.Exists)
            {
                _placeholderRetries.Remove(item); // fresh reappearance supersedes any prior retry state
                SubmitIconRequests([item]);
            }
            else if (result.Exists && (_iconProvider.IsPlaceholder(item.Icon) || HasUnhealthyLastOutcome(item)))
            {
                // M-1 (review it.2): retries not just a still-placeholder icon, but also a REAL icon whose
                // most recent request nonetheless resolved Unavailable/Failed/Dropped (e.g. the icon shown
                // is a stale one from before a network root went down, or a transient worker Failed) — see
                // ItemIconState.LastOutcome's own remarks.
                RetryPlaceholderIcon(item);
            }
            else
            {
                // Either still missing, or present with a real, healthily-resolved icon — either way, any
                // previous retry bookkeeping for this item is no longer relevant.
                _placeholderRetries.Remove(item);
            }
        }

        // After the loop: a subclass reacting to the transition may remove/relocate items (mutating _allItems).
        if (vanished is not null) OnItemsVanished(vanished);
        if (directoryFlagsChanged) OnProbeResultsApplied(true);
    }

    /// <summary>
    /// Races <paramref name="cancellationToken"/>'s <see cref="IItemProbeQueue.ProbeAsync"/> call against
    /// <see cref="LivenessPassTimeout"/> (via <see cref="LivenessTimeProvider"/>, never wall-clock — C-7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// M-2 (review it.2): a timeout actually CANCELS the underlying call through a LINKED token (not
    /// <paramref name="cancellationToken"/> itself), instead of merely walking away from it and letting it
    /// keep running "in vain" to completion. <c>BackgroundItemProbeQueue.ProbeAsync</c> observes the same
    /// linked token in both its <c>SemaphoreSlim.WaitAsync</c> and its per-path <c>ProbeBatch</c> loop —
    /// checked BETWEEN paths, not inside a single <c>File.GetAttributes</c> call itself (a synchronous,
    /// uninterruptible syscall with no cancellation support of its own — N-4, small iteration 3). A batch
    /// still waiting on the throttle semaphore is freed immediately by the cancellation; one already
    /// running frees its thread-pool thread and throttle slot as soon as whichever single path is
    /// CURRENTLY probing returns (not mid-syscall) and the loop's next check observes the cancellation,
    /// rather than occupying both for the REST of the batch.
    /// </para>
    /// <para>
    /// D-13: <paramref name="paths"/>'s probe call is checked for <see cref="Task.IsCompleted"/> BEFORE
    /// ever creating the linked token source/timer — <see cref="SynchronousItemProbeQueue"/> always
    /// returns an already-completed task, and awaiting one is guaranteed synchronous (no continuation is
    /// ever scheduled), which is what keeps this whole pass — and therefore
    /// <see cref="RefreshItemsLivenessAsync"/> — fully synchronous end-to-end for the D-13 fallback,
    /// exactly matching the pre-ADR-046 behavior every existing test relies on. Only a REAL, still-running
    /// <see cref="IItemProbeQueue"/> call ever actually races the timer below.
    /// </para>
    /// </remarks>
    /// <param name="paths">The paths to probe.</param>
    /// <param name="cancellationToken">Cancels the probe — disposal, or (via the linked token below) the pass's own timeout.</param>
    /// <returns>The probe results, or <see langword="null"/> when <see cref="LivenessPassTimeout"/> elapsed first.</returns>
    private async Task<IReadOnlyList<PathProbeResult>?> ProbeWithTimeoutAsync(string[] paths, CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var probeTask = _itemProbeQueue.ProbeAsync(paths, linkedCts.Token);
            if (probeTask.IsCompleted) return await probeTask;

            // No TaskCreationOptions.RunContinuationsAsynchronously here: the timer callback below only
            // ever does the lightweight TrySetResult/Task.WhenAny glue, never blocking work, so running it
            // inline (on whichever thread fires it — a real ThreadPool timer thread in production, or
            // synchronously from ManualTimeProvider.Advance in a test) is safe and keeps a test's
            // Advance()-then-Pump() free of a thread-pool-scheduled race. Task.WhenAny (not a bare `await
            // probeTask`) is what guarantees THIS method returns the moment the timeout elapses regardless
            // of whether the underlying IItemProbeQueue implementation ever actually observes the
            // cancellation below — the linked token is cancelled as a best-effort courtesy to free its
            // resources sooner (M-2), not relied upon to unblock this wait.
            var timedOut = new TaskCompletionSource<bool>();
            using var timeoutTimer = LivenessTimeProvider.CreateTimer(
                _ => timedOut.TrySetResult(true), null, LivenessPassTimeout, Timeout.InfiniteTimeSpan);

            var completed = await Task.WhenAny(probeTask, timedOut.Task);
            if (!ReferenceEquals(completed, probeTask))
            {
                // M-2 (review it.2): actually cancel the abandoned call — BackgroundItemProbeQueue.ProbeAsync
                // observes this same linked token in its SemaphoreSlim.WaitAsync and its per-path
                // ProbeBatch loop — instead of merely walking away and letting it keep running "in vain" to
                // completion, holding a batch-throttle slot and a thread-pool thread for however much
                // longer it takes.
                linkedCts.Cancel();
                return null;
            }

            return await probeTask;
        }
        finally
        {
            // Safe to dispose here even in the timeout branch above, where the abandoned probeTask may
            // still be running: every consumer that will ever check linkedCts.Token already registered
            // with it synchronously, inside the ProbeAsync call above (SemaphoreSlim.WaitAsync's own
            // registration, ProbeBatch's ThrowIfCancellationRequested reads) — Dispose only releases this
            // source's OWN registration on the parent `cancellationToken` and any internal timer, it does
            // not invalidate CancellationToken copies already handed out. Not disposing here would leak
            // that parent registration on this ViewModel's own long-lived `_livenessCts` for the rest of
            // the session — one more per liveness tick.
            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// Adds new, not-yet-tracked entries from <paramref name="files"/> to <see cref="_allItems"/> /
    /// <see cref="_itemsByPath"/> without raising <see cref="OnSourceChanged"/>. The O(1)
    /// <see cref="_itemsByPath"/> lookup (instead of scanning <see cref="_allItems"/> per candidate)
    /// is what keeps <see cref="ReplaceAll"/> O(n) instead of O(n²) on a full resync. Every new item
    /// appears immediately (ADR-046 D-5): no file-system/shell access happens here — the item's initial
    /// icon is a cache/type-icon peek or the provider's placeholder, and its <see cref="FileItem.Exists"/>/
    /// <see cref="FileItem.IsDirectory"/> are optimistic guesses (<see cref="FileItem.CreatePending"/>)
    /// until a queued result applies the real probe (<see cref="OnIconResults"/>).
    /// </summary>
    /// <param name="files">Candidate file-system paths.</param>
    /// <returns><see langword="true"/> if at least one new item was added.</returns>
    private bool AddFilesCore(string[] files)
    {
        List<FileItem>? added = null;

        // Keep entries even if the path is currently missing — they render greyed out
        // (a file on an unmounted drive shouldn't silently vanish from the panel).
        foreach (var file in files.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (_itemsByPath.ContainsKey(file)) continue;

            var item = FileItem.CreatePending(file, ResolvePendingIcon(file), displayName: ResolveDisplayName(file));
            _allItems.Add(item);
            _itemsByPath.Add(file, item);
            (added ??= []).Add(item);
        }

        if (added is null) return false;

        // ADR-046 D-6: whichever of the new items would currently be visible get their real icon first —
        // OrderBy is a stable sort, so within each of the two groups the original (portal enumeration)
        // order is preserved. L-5 (review it.2): the predicate is built ONCE for the whole batch
        // (CreateVisibilityPredicate), not re-derived once per item — a content-filter override
        // (ChildWindowViewModel) would otherwise re-run its filter-mode switch a thousand times over for a
        // thousand-file portal load.
        var isVisible = CreateVisibilityPredicate();
        SubmitIconRequests(added.OrderBy(item => isVisible(item) ? 0 : 1).ToList());

        return true;
    }

    /// <summary>
    /// Resolves a brand-new item's INITIAL icon without ever touching the file system or the shell
    /// (ADR-046 D-5): an existing cache hit for the exact <c>(path, size)</c>, then a cached type icon by
    /// extension, then the provider's own placeholder. The definitive icon follows once the queued
    /// request this item is about to be submitted with resolves.
    /// </summary>
    /// <param name="path">The candidate item's file-system path.</param>
    private ImageSource ResolvePendingIcon(string path)
    {
        if (_iconProvider.TryPeekCachedIcon(path, _requestedIconPixelSize, out var cached)) return cached;

        var isDirectoryHeuristic = string.IsNullOrEmpty(System.IO.Path.GetExtension(path));
        return _iconProvider.TryPeekTypeIcon(path, isDirectoryHeuristic) ?? _iconProvider.PlaceholderIcon;
    }

    /// <summary>
    /// Submits an icon-load request for every item in <paramref name="items"/> at the current
    /// <see cref="RequestedIconPixelSize"/> to this panel's <see cref="_iconLoadGroup"/> (ADR-046) — never
    /// touches the file system or the shell itself. No-op for an empty list.
    /// </summary>
    /// <param name="items">The items to (re-)fetch icons for.</param>
    private void SubmitIconRequests(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0) return;

        var requests = new IconLoadRequest[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            requests[i] = new IconLoadRequest(item.Path, _requestedIconPixelSize);

            var state = _iconStates.GetValue(item, static _ => new ItemIconState());
            if (!state.RequestInFlight) _pendingIconLoadCount++;
            state.RequestInFlight = true;
        }

        _iconLoadGroup.Submit(requests);
    }

    /// <summary>
    /// Delivery callback for this panel's <see cref="_iconLoadGroup"/> (ADR-046) — invoked on the
    /// constructing thread's <see cref="SynchronizationContext"/> (or inline, for the
    /// <see cref="SynchronousIconLoadQueue"/> fallback) with a batch of results. Matches each result back
    /// to its <see cref="FileItem"/> by path (silently dropped if no longer tracked — removed, renamed
    /// away from, or superseded by a <see cref="ReplaceAll"/> the queue's own generation check already
    /// filtered for), applies it, and raises <see cref="OnProbeResultsApplied"/> at most once for the
    /// whole batch.
    /// </summary>
    /// <param name="results">The delivered results.</param>
    private void OnIconResults(IReadOnlyList<IconLoadResult> results)
    {
        var directoryFlagsChanged = false;

        foreach (var result in results)
        {
            if (!_itemsByPath.TryGetValue(result.Path, out var item)) continue;

            switch (result.Outcome)
            {
                case IconLoadOutcome.Interim:
                    // Never overwrites an already-resolved (non-placeholder) icon: a late Interim for a
                    // request whose real Loaded result already landed is simply stale. M-5 insurance
                    // (review it.2, C-4): the queue itself already guarantees every delivered Icon is
                    // Frozen before it ever crosses the worker -> UI boundary, but this checks it again
                    // defensively rather than trusting the contract blindly — an unfrozen icon is simply
                    // never applied (the item keeps showing its current placeholder/type icon; a correctly
                    // frozen result, if any, follows separately).
                    if (result.Icon is { IsFrozen: true } interimIcon && _iconProvider.IsPlaceholder(item.Icon))
                        item.UpdateIcon(interimIcon);
                    break;

                case IconLoadOutcome.Loaded:
                    ApplyLoadedResult(item, result);
                    MarkTerminal(item, result.Outcome);
                    break;

                case IconLoadOutcome.Unavailable:
                case IconLoadOutcome.Failed:
                case IconLoadOutcome.Dropped:
                default:
                    MarkTerminal(item, result.Outcome);
                    break;
            }

            if (result.Exists is { } exists && result.IsDirectory is { } isDirectory)
            {
                // H-1 (review it.2, M-A): the icon pipeline's own probe (D-11, one metadata call per icon
                // job) must never be the one that reports a present -> missing transition — that is
                // exclusively RunSingleLivenessPassAsync's job (fixed 3 s cadence, reports each
                // disappearance exactly once via OnItemsVanished). Applying a stale/racy `exists: false`
                // here for an item this ViewModel already confirmed present would silently eat that one,
                // required transition — the very next liveness tick would see the item as "already
                // missing", not freshly vanished, permanently losing the reconcile for it. Still safe (and
                // necessary) to apply in every other case: a still-pending item's first-ever probe (never
                // observed alive, so there is no transition to protect), a confirmed non-existent item
                // staying non-existent, or any `exists: true` observation (including a genuine
                // missing->present reappearance).
                if ((item.IsProbePending || exists || !item.Exists) && item.ApplyProbe(exists, isDirectory))
                    directoryFlagsChanged = true;
            }
        }

        if (directoryFlagsChanged) OnProbeResultsApplied(true);
    }

    /// <summary>
    /// Applies a <see cref="IconLoadOutcome.Loaded"/> result's icon to <paramref name="item"/>: always
    /// when it matches <see cref="RequestedIconPixelSize"/>, or when the item doesn't have a real icon
    /// yet (an old-size icon still beats a placeholder or a type icon — ADR-046 §6). A once-real icon is
    /// never downgraded by a later, differently-sized result: the matching-size result for the CURRENT
    /// request always follows separately. Also clears any <see cref="RetryPlaceholderIcon"/> bookkeeping
    /// once the applied icon is a genuinely resolved one (not the provider's own placeholder).
    /// </summary>
    private void ApplyLoadedResult(FileItem item, IconLoadResult result)
    {
        // M-5 insurance (review it.2, C-4): the queue already guarantees every delivered Loaded icon is
        // Frozen (StaIconLoadQueue.ExecuteIconFetch's own runtime check degrades a non-Frozen result to
        // Failed before ever delivering it), but this is checked again here rather than trusted blindly —
        // an unfrozen icon is simply never applied; the item keeps whatever it was already showing.
        if (result.Icon is not { IsFrozen: true } icon) return;

        var state = _iconStates.GetValue(item, static _ => new ItemIconState());
        if (result.PixelSize != _requestedIconPixelSize && state.HasRealIcon) return;

        item.UpdateIcon(icon);

        // L-3 (review it.2): a Loaded result whose icon IS the provider's own placeholder (GetIcon fell
        // back to it internally without throwing) is not a "real" icon — marking HasRealIcon here anyway
        // would let it wrongly block a LATER, differently-sized Loaded result that actually resolved a
        // genuine icon (the "old-size icon beats a placeholder" rule above is meant for an actual old
        // icon, not a same-status placeholder pretending to be one).
        if (!_iconProvider.IsPlaceholder(icon))
        {
            state.HasRealIcon = true;
            _placeholderRetries.Remove(item);
        }
    }

    /// <summary>
    /// Marks <paramref name="item"/>'s in-flight request as resolved (ADR-046): clears
    /// <see cref="ItemIconState.RequestInFlight"/> and decrements <see cref="_pendingIconLoadCount"/>,
    /// exactly once per request — a stray extra terminal result for an item with no request currently
    /// marked in-flight (shouldn't happen, but costs nothing to guard) is a no-op. Always records
    /// <paramref name="outcome"/> as <see cref="ItemIconState.LastOutcome"/> (M-1, review it.2) regardless
    /// of that guard — <see cref="RunSingleLivenessPassAsync"/>'s retry condition reads it even for an item
    /// whose request was somehow already marked resolved.
    /// </summary>
    /// <param name="item">The item whose in-flight request just resolved.</param>
    /// <param name="outcome">The result's terminal outcome, recorded for the next liveness tick's retry decision.</param>
    private void MarkTerminal(FileItem item, IconLoadOutcome outcome)
    {
        var state = _iconStates.GetValue(item, static _ => new ItemIconState());
        state.LastOutcome = outcome;
        if (!state.RequestInFlight) return;

        state.RequestInFlight = false;
        _pendingIconLoadCount--;
    }

    /// <summary>
    /// L-3 (review it.2): counterpart to <see cref="MarkTerminal"/> for an item leaving this ViewModel
    /// (<see cref="RemoveFile"/>/<see cref="RemoveFileByPath"/>) with its request still in flight — that
    /// request's eventual result, if it ever arrives, is silently dropped by <see cref="OnIconResults"/>'s
    /// own <c>_itemsByPath</c> lookup (the item is no longer tracked under its path), so
    /// <see cref="MarkTerminal"/> would never run for it and <see cref="_pendingIconLoadCount"/> would leak
    /// one count forever. A no-op for an item with no request currently in flight.
    /// </summary>
    /// <param name="item">The item about to be removed.</param>
    private void ReleasePendingIconLoadIfInFlight(FileItem item)
    {
        if (!_iconStates.TryGetValue(item, out var state) || !state.RequestInFlight) return;

        state.RequestInFlight = false;
        _pendingIconLoadCount--;
    }

    /// <summary>
    /// Retries the icon request for <paramref name="item"/> — an item that exists on disk but is still
    /// showing the icon pipeline's own placeholder (see <see cref="RefreshItemsLiveness"/>) — bounded to
    /// <see cref="MaxPlaceholderRetryAttempts"/> total attempts, with a growing per-liveness-tick backoff
    /// between attempts (1 tick before the 2nd try, 2 before the 3rd) so a persistently-broken item
    /// doesn't pay for a full shell round-trip on every single 3-second tick forever. A retry is skipped
    /// (without spending an attempt) while a request for this item is already in flight (ADR-046 §2.3) —
    /// there is nothing useful to submit again until that one resolves.
    /// </summary>
    /// <remarks>
    /// N-3 (small iteration 3): the bounded budget above assumes a transient failure — it is spent in full
    /// within roughly <see cref="MaxPlaceholderRetryAttempts"/> liveness ticks (~15 s at the growing
    /// backoff). A <see cref="IconLoadOutcome.Unavailable"/> outcome caused by an OPEN remote-root circuit
    /// breaker (D-3) is not transient on that timescale — the breaker's own quarantine
    /// (<c>RemoteRootCircuitBreaker</c>'s 60 s) outlives the whole budget, so spending it here would
    /// exhaust the item's retries long before the root ever gets a chance to recover, leaving it stuck on
    /// the placeholder/stale icon for the rest of the session (until something like
    /// <see cref="ResetExhaustedPlaceholderRetries"/> gives it a fresh budget). While
    /// <see cref="ItemIconState.LastOutcome"/> is <see cref="IconLoadOutcome.Unavailable"/>, this instead
    /// retries on EVERY tick, unbounded, without ever touching <see cref="PlaceholderRetryState"/> — cheap,
    /// since a still-open breaker short-circuits the resubmitted request without touching the file system
    /// or the shell. The moment the outcome becomes anything else again (a real
    /// <see cref="IconLoadOutcome.Loaded"/> icon, or a transient <see cref="IconLoadOutcome.Failed"/>/
    /// <see cref="IconLoadOutcome.Dropped"/>), the normal bounded/backed-off budget resumes exactly where
    /// it was left.
    /// </remarks>
    /// <param name="item">An existing item whose current <see cref="FileItem.Icon"/> is the placeholder.</param>
    private void RetryPlaceholderIcon(FileItem item)
    {
        var retryState = _placeholderRetries.GetValue(item, static _ => new PlaceholderRetryState());
        var iconState = _iconStates.GetValue(item, static _ => new ItemIconState());
        var isQuarantineWait = iconState.LastOutcome == IconLoadOutcome.Unavailable;

        if (!isQuarantineWait)
        {
            if (retryState.AttemptsRemaining <= 0) return;

            if (retryState.TicksUntilNextAttempt > 0)
            {
                retryState.TicksUntilNextAttempt--;
                return;
            }
        }

        if (iconState.RequestInFlight) return; // already being retried by something else — don't spend an attempt

        if (!isQuarantineWait)
        {
            retryState.AttemptsRemaining--;
            retryState.TicksUntilNextAttempt = MaxPlaceholderRetryAttempts - retryState.AttemptsRemaining; // 1, then 2, ...
        }

        SubmitIconRequests([item]);
    }

    /// <summary>
    /// M-1 (review it.2): <see langword="true"/> when <paramref name="item"/>'s most recently resolved icon
    /// request ended in <see cref="IconLoadOutcome.Unavailable"/>, <see cref="IconLoadOutcome.Failed"/> or
    /// <see cref="IconLoadOutcome.Dropped"/> — i.e. its currently-displayed icon (even a real,
    /// non-placeholder one) is potentially stale/wrong and worth another <see cref="RetryPlaceholderIcon"/>
    /// attempt, not just a still-placeholder one. <see langword="false"/> for an item with no bookkeeping
    /// yet (never had a request resolve).
    /// </summary>
    private bool HasUnhealthyLastOutcome(FileItem item)
        => _iconStates.TryGetValue(item, out var state)
           && state.LastOutcome is IconLoadOutcome.Unavailable or IconLoadOutcome.Failed or IconLoadOutcome.Dropped;

    /// <summary>Per-item mutable retry counters for <see cref="RetryPlaceholderIcon"/>.</summary>
    private sealed class PlaceholderRetryState
    {
        public int AttemptsRemaining = MaxPlaceholderRetryAttempts;
        public int TicksUntilNextAttempt;
    }

    /// <summary>Per-item background icon-load bookkeeping (ADR-046) — see <see cref="_iconStates"/>.</summary>
    private sealed class ItemIconState
    {
        /// <summary>Whether a submitted request for this item has not yet resolved to a terminal outcome.</summary>
        public bool RequestInFlight;

        /// <summary>Whether a real (non-placeholder) <see cref="IconLoadOutcome.Loaded"/> icon has ever been applied.</summary>
        public bool HasRealIcon;

        /// <summary>
        /// The outcome of this item's most recently resolved icon request, or <see langword="null"/> before
        /// its first one ever resolves (M-1, review it.2). Read by <see cref="RunSingleLivenessPassAsync"/>'s
        /// retry condition: a real (non-placeholder) icon that was nonetheless last resolved
        /// <see cref="IconLoadOutcome.Unavailable"/>/<see cref="IconLoadOutcome.Failed"/>/<see cref="IconLoadOutcome.Dropped"/>
        /// (e.g. an old icon still showing from before a network root went down) is retried on the same
        /// backoff schedule as a still-placeholder item, instead of being silently left on a stale icon
        /// forever just because <see cref="IIconProvider.IsPlaceholder"/> says it "looks fine".
        /// </summary>
        public IconLoadOutcome? LastOutcome;
    }
}
