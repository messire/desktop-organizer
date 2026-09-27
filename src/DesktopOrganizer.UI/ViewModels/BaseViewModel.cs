using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Base class for all ViewModels.
/// </summary>
public abstract class BaseViewModel(IIconProvider iconProvider) : INotifyPropertyChanged
{
    /// <summary>Maximum number of placeholder-retry attempts per item (see <see cref="RetryPlaceholderIcon"/>).</summary>
    private const int MaxPlaceholderRetryAttempts = 3;

    private readonly IIconProvider _iconProvider = iconProvider;
    private readonly List<FileItem> _allItems = [];

    /// <summary>
    /// O(1) membership index mirroring <see cref="_allItems"/>, keyed by path. Windows paths are
    /// case-insensitive, so <c>C:\A.txt</c> and <c>c:\a.txt</c> are treated as the same entry —
    /// consistent with the comparer already used by <see cref="RemoveFileByPath"/>.
    /// </summary>
    private readonly HashSet<string> _allItemPaths = new(StringComparer.OrdinalIgnoreCase);

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

    private FileItem? _selectedFile;

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
    /// The full, unfiltered set of items managed by this panel.
    /// Subclasses use this as the source when computing filter projections.
    /// </summary>
    protected internal IReadOnlyList<FileItem> AllItems => _allItems;

    /// <summary>
    /// Re-indexes <see cref="_allItemPaths"/> for <paramref name="item"/> after a successful physical
    /// rename (see <c>ChildWindowViewModel.CommitRenameCommand</c>) and relocates the item in place via
    /// <see cref="FileItem.Relocate"/> — the same <see cref="FileItem"/> instance is kept (rather than
    /// replaced), so any bound selection/UI state referencing it survives. No-op (and no
    /// <see cref="OnSourceChanged"/> raised) if <paramref name="item"/> isn't currently tracked by this
    /// ViewModel.
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

        _allItemPaths.Remove(item.Path);

        // A rename can never collide with an already-tracked path (the rename service itself rejects
        // NameTaken), but guard anyway: dedup the same way AddFilesCore does, rather than risk two
        // entries in _allItemPaths pointing at the same FileItem.
        _allItemPaths.Add(newPath);

        item.Relocate(newPath, _iconProvider.GetIcon(newPath));
    }

    /// <summary>
    /// Re-validates <see cref="FileItem.Exists"/>/<see cref="FileItem.IsDirectory"/> for every tracked
    /// item against the current file-system state (<see cref="FileItem.Refresh"/>), and re-resolves the
    /// icon for any item that just transitioned from missing to present. <see cref="IIconProvider"/>
    /// deliberately never caches its placeholder result for a missing path (see
    /// <c>IconProvider.GetIcon</c>), so a single fresh call here is enough to replace a stuck
    /// placeholder with the file's real icon once the underlying file/folder actually reappears (e.g.
    /// a removable drive is remounted, or a stale path was resolved to its managed copy on the next
    /// <c>ChildWindowViewModel.LoadFromModel</c>) — P00 baseline finding H3.
    /// </summary>
    /// <remarks>
    /// A content-change re-fetch for items that stay present throughout (e.g. an <c>.exe</c>'s
    /// embedded icon changing after an in-place update) is intentionally NOT performed here:
    /// <see cref="IIconProvider.GetIcon"/> already does that freshness check (comparing
    /// <c>LastWriteTimeUtc</c>) once actually called, but calling it for every tracked item on every
    /// liveness tick would reintroduce the exact synchronous UI-thread cost this panel's icon pipeline
    /// already measured as expensive for large panels (~2.8ms/item — see
    /// docs/DIAGNOSTICS/icon-loading-findings.md). The missing → present transition this method reacts
    /// to is comparatively rare and bounded by how many items were actually broken, not by the panel's
    /// full size.
    /// <para>
    /// <b>M3 addition (P08 fix cycle 1, 2026-09-22):</b> the missing→present transition above only
    /// ever fires once per disappearance. A file that is <see cref="FileItem.Exists"/> throughout but
    /// whose <em>icon retrieval itself</em> failed transiently — e.g. <c>SHGetFileInfoW</c> called
    /// before the shell has finished initializing during autostart — used to leave the item stuck on
    /// <see cref="IIconProvider"/>'s placeholder for the rest of the session, since nothing ever called
    /// <see cref="IIconProvider.GetIcon"/> again for an item that was never "missing" to begin with.
    /// Every existing, still-placeholder item is now retried too, bounded to
    /// <see cref="MaxPlaceholderRetryAttempts"/> attempts with a growing per-tick backoff (see
    /// <see cref="RetryPlaceholderIcon"/>) — NOT unconditionally on every tick, which would reintroduce
    /// the exact synchronous UI-thread cost documented above for the common "still missing"/"still
    /// present with a real icon" cases (this only ever does extra work for the rare stuck-placeholder
    /// case, bounded to 3 tries total per item).
    /// </para>
    /// </remarks>
    internal void RefreshItemsLiveness()
    {
        foreach (var item in _allItems)
        {
            var wasMissing = !item.Exists;
            item.Refresh();

            if (wasMissing && item.Exists)
            {
                item.UpdateIcon(_iconProvider.GetIcon(item.Path));
                _placeholderRetries.Remove(item); // fresh reappearance supersedes any prior retry state
            }
            else if (item.Exists && _iconProvider.IsPlaceholder(item.Icon))
            {
                RetryPlaceholderIcon(item);
            }
            else
            {
                // Either still missing, or present with a real (non-placeholder) icon — either way,
                // any previous retry bookkeeping for this item is no longer relevant.
                _placeholderRetries.Remove(item);
            }
        }
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
        var item = _allItems.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        _allItems.Remove(item);
        _allItemPaths.Remove(item.Path);
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
        _allItems.Clear();
        _allItemPaths.Clear();
        AddFilesCore(paths.ToArray());
        OnSourceChanged();
    }

    /// <summary>
    /// Reorders <see cref="AllItems"/> in place according to <paramref name="comparer"/> — the
    /// "materialize the sort order" half of the <c>Name -&gt; Manual</c> auto-switch (ADR-014
    /// Decision 6, used by <c>ChildWindowViewModel.ReorderItem</c>). Does <b>not</b> touch
    /// <see cref="_allItemPaths"/> (membership doesn't change, only order) and does <b>not</b> call
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
    /// Does <b>not</b> touch <see cref="_allItemPaths"/> — the set of tracked paths is unchanged, only
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
    /// Removes a file item from the full collection.
    /// </summary>
    /// <param name="file">File item to be removed.</param>
    protected void RemoveFile(FileItem file)
    {
        if (!_allItems.Remove(file)) return;
        _allItemPaths.Remove(file.Path);
        OnSourceChanged();
    }

    /// <summary>
    /// Called whenever <see cref="AllItems"/> changes. The default implementation
    /// projects all items into <see cref="FileItems"/> unchanged (no filtering).
    /// Override in subclasses to apply a filter predicate.
    /// </summary>
    protected virtual void OnSourceChanged() => ReplaceFileItems(_allItems);

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
    /// Adds new, not-yet-tracked entries from <paramref name="files"/> to <see cref="_allItems"/> /
    /// <see cref="_allItemPaths"/> without raising <see cref="OnSourceChanged"/>. The O(1)
    /// <see cref="_allItemPaths"/> lookup (instead of scanning <see cref="_allItems"/> per candidate)
    /// is what keeps <see cref="ReplaceAll"/> O(n) instead of O(n²) on a full resync.
    /// </summary>
    /// <param name="files">Candidate file-system paths.</param>
    /// <returns><see langword="true"/> if at least one new item was added.</returns>
    private bool AddFilesCore(string[] files)
    {
        var added = false;

        // Keep entries even if the path is currently missing — they render greyed out
        // (a file on an unmounted drive shouldn't silently vanish from the panel).
        foreach (var file in files.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (_allItemPaths.Contains(file)) continue;

            // Mark the path as tracked only after the item is fully constructed and added — if
            // GetIcon throws, the path must not be left "claimed" in _allItemPaths with no
            // corresponding _allItems entry, or this file could never be added again.
            var icon = _iconProvider.GetIcon(file);
            _allItems.Add(new FileItem(file, icon, displayName: ResolveDisplayName(file)));
            _allItemPaths.Add(file);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Retries <see cref="IIconProvider.GetIcon"/> for <paramref name="item"/> — an item that exists on
    /// disk but is still showing the icon pipeline's own placeholder (see
    /// <see cref="RefreshItemsLiveness"/>) — bounded to <see cref="MaxPlaceholderRetryAttempts"/> total
    /// attempts, with a growing per-liveness-tick backoff between attempts (1 tick before the 2nd try,
    /// 2 before the 3rd) so a persistently-broken item doesn't pay for a full shell round-trip on every
    /// single 3-second tick forever. <see cref="IIconProvider"/> never caches a placeholder result
    /// against a valid path (only a successful retrieval is ever cached — see <c>IconProvider.StoreInCache</c>),
    /// so each attempt here genuinely re-invokes the shell rather than serving a stale cached failure.
    /// </summary>
    /// <param name="item">An existing item whose current <see cref="FileItem.Icon"/> is the placeholder.</param>
    private void RetryPlaceholderIcon(FileItem item)
    {
        var state = _placeholderRetries.GetValue(item, static _ => new PlaceholderRetryState());
        if (state.AttemptsRemaining <= 0) return;

        if (state.TicksUntilNextAttempt > 0)
        {
            state.TicksUntilNextAttempt--;
            return;
        }

        state.AttemptsRemaining--;
        state.TicksUntilNextAttempt = MaxPlaceholderRetryAttempts - state.AttemptsRemaining; // 1, then 2, ...

        var icon = _iconProvider.GetIcon(item.Path);
        if (_iconProvider.IsPlaceholder(icon)) return; // still stuck — leave the counted-down state as-is

        item.UpdateIcon(icon);
        _placeholderRetries.Remove(item); // resolved — no further bookkeeping needed
    }

    /// <summary>Per-item mutable retry counters for <see cref="RetryPlaceholderIcon"/>.</summary>
    private sealed class PlaceholderRetryState
    {
        public int AttemptsRemaining = MaxPlaceholderRetryAttempts;
        public int TicksUntilNextAttempt;
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
}
