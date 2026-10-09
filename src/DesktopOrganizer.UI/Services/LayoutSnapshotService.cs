using System.Text.Json;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Implements named layout snapshots — capture, restore, delete and persistence.
/// Snapshots are persisted as part of the shared versioned JSON application-state document
/// (<see cref="IApplicationStateService"/>, ADR-024 C6). Maximum of 10 snapshots; the oldest is
/// automatically removed when the limit is exceeded.
/// </summary>
internal sealed class LayoutSnapshotService : ILayoutSnapshotService
{
    private const int MaxSnapshots = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IWindowManager _windowManager;
    private readonly IApplicationStateService _applicationStateService;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IStatusNotifier _statusNotifier;
    private readonly ILocalizationService _localization;
    private readonly IApplicationStateStore _stateStore;
    private readonly List<LayoutSnapshot> _snapshots = [];

    // Live panel Id -> the snapshot (captured) Id it was recreated from (review cycle 3, M1). Restore hands out
    // fresh Ids while the snapshot JSON keeps the old ones, so without this a second Restore of the same snapshot
    // could not tell which live panel is the twin of which snapshot panel. In-memory only - after a restart the
    // plain Id comparison applies (nothing is persisted).
    private readonly Dictionary<Guid, Guid> _originalIdByLiveId = [];

    // Guards Load() so only the first call actually seeds _snapshots — see Load()'s remarks.
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutSnapshotService"/> class.
    /// </summary>
    /// <param name="windowManager">Creates/removes/syncs the live panels a snapshot captures or restores.</param>
    /// <param name="applicationStateService">Persists captured/deleted snapshots.</param>
    /// <param name="itemStorage">
    /// Provides read-only manifest lookups for <see cref="WindowManager.ResolveStalePanelPaths"/>
    /// (MAJOR-1 fix, P08 fix cycle 2, 2026-09-22) — see <see cref="Restore"/>'s remarks.
    /// </param>
    /// <param name="statusNotifier">
    /// Non-blocking surface for the fail-visibly report when <see cref="Restore"/> cannot return a panel's
    /// orphaned managed items (ADR-035; ADR-audit WP-B, B2).
    /// </param>
    /// <param name="localization">Resolves the localized text of that report.</param>
    /// <param name="stateStore">
    /// Single source of truth for "is this session read-only" (<see cref="IApplicationStateStore.IsWritable"/>) —
    /// in a read-only session <see cref="Restore"/> never moves a file (ADR-audit WP-B review F2).
    /// </param>
    public LayoutSnapshotService(
        IWindowManager windowManager,
        IApplicationStateService applicationStateService,
        IPanelItemStorageService itemStorage,
        IStatusNotifier statusNotifier,
        ILocalizationService localization,
        IApplicationStateStore stateStore)
    {
        _windowManager = windowManager;
        _applicationStateService = applicationStateService;
        _itemStorage = itemStorage;
        _statusNotifier = statusNotifier;
        _localization = localization;
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    public IReadOnlyList<LayoutSnapshot> Snapshots => _snapshots;

    /// <inheritdoc />
    public LayoutSnapshot Capture(string name)
    {
        // Ensure every live window's model reflects its current on-screen/file state before reading it
        // below. WindowManager's regular save (SaveWindowsState) is debounced up to 500ms (Phase 7 /
        // Block C), so without this a snapshot captured shortly after e.g. removing a file from a panel
        // would serialize the panel's stale, pre-removal FilePaths — see phase-7-performance-hardening.md
        // Block C review finding M3. This only syncs in-memory models; it does not write to disk.
        _windowManager.SyncAllWindowsToModels();

        // Deep-clone all panels via JSON round-trip so changes after capture don't affect the snapshot.
        var panelJsonItems = _windowManager.GetAllChildWindows()
            .Select(m => JsonSerializer.Serialize(m, SerializerOptions))
            .ToList();

        var snapshot = new LayoutSnapshot
        {
            Name = name,
            CreatedAt = DateTime.Now,
            PanelJsonItems = panelJsonItems,
        };

        // Enforce max-10 limit — evict oldest first.
        while (_snapshots.Count >= MaxSnapshots)
        {
            var oldest = _snapshots.OrderBy(s => s.CreatedAt).First();
            _snapshots.Remove(oldest);
        }

        _snapshots.Add(snapshot);
        Save();

        return snapshot;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MAJOR-1 fix (P08 fix cycle 2, 2026-09-22): every panel model is deserialized up front, into an
    /// in-memory list, BEFORE any of them is (re)constructed via <see cref="IWindowManager.CreateChildWindow"/>
    /// — mirroring <see cref="IWindowManager.LoadWindowsState"/>'s own pre-construction resolution pass
    /// (M1, fix cycle 1). The previous single-loop implementation deserialized-then-immediately-created
    /// one panel at a time, so each panel's stale-original-path self-heal only ever ran through
    /// <c>ChildWindowViewModel.LoadFromModel</c>'s own per-panel <c>TryFindManagedPath</c> — which has no
    /// visibility into sibling panels not yet constructed. Two snapshot panels whose stale originals both
    /// unambiguously resolved to the SAME single managed copy would therefore both have been handed that
    /// same copy — the exact cross-panel-ownership bug <see cref="WindowManager.ResolveStalePanelPaths"/>
    /// already fixed for the regular startup load path (M1) — because that centralized, cross-panel-aware
    /// pass was never invoked here at all. Calling it here, across every about-to-be-recreated panel at
    /// once before any of them exists, closes the same gap for snapshot restore.
    /// <para>
    /// ADR-audit WP-B (B2): the snapshot is deserialized and stale-path-resolved BEFORE any current panel is
    /// touched, so the set of managed paths the snapshot references is known up front. A current panel
    /// created after the snapshot was taken is not part of it and disappears; its managed items are
    /// therefore returned to their original location first (<see cref="IPanelItemStorageService.Restore"/>,
    /// the ADR-010 "close" semantics) instead of being left orphaned in hidden storage. Items the snapshot
    /// DOES reference are never touched (the recreated panels point at the very same managed paths — see
    /// <c>releaseManagedItems: false</c> below). If an orphaned item cannot be returned, nothing is lost
    /// and nothing is hidden (ADR-035, fail visibly): that panel is kept, stripped of every other item
    /// (so nothing is referenced by two panels), and the user is told via <see cref="IStatusNotifier"/>.
    /// </para>
    /// <para>
    /// Review cycle 1 (WP-B): (F1) a kept panel that IS one of the snapshot's panels (same original Id —
    /// the snapshot models get fresh Ids below, so the original one is remembered) is not kept alongside its
    /// recreated twin: the un-returnable paths are merged into the recreated model and the old window is
    /// removed, so the panel is not duplicated. (F2) In a read-only session
    /// (<see cref="IApplicationStateStore.IsWritable"/> <see langword="false"/>) nothing is returned — the
    /// removal of panels is not persisted there, so no orphan can appear on disk, and no file may be moved.
    /// (F3) Folder Portal panels own no managed items and are skipped, as in
    /// <c>WindowManager.CollectManagedPaths</c>. (F6) The user is told how many files were returned.
    /// </para>
    /// <para>
    /// Review cycle 3: (M1) the service remembers, in memory only, which snapshot Id each recreated panel came
    /// from, so a second Restore of the same snapshot still recognises the live panel as the twin. (M2) Each
    /// panel is created in its own try/catch; the managed items of one that fails count as not returned, and the
    /// state flush and the report run in a <c>finally</c>. (M5) The return pass is <c>ReturnOrphanedItems</c>.
    /// </para>
    /// </remarks>
    public void Restore(string snapshotId)
    {
        var snapshot = _snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null) return;

        // Deserialize every panel model up front — see this method's remarks for why this must happen
        // before ANY panel is created (MAJOR-1) or removed (B2).
        var models = new List<ChildWindowModel>();
        var modelsByOriginalId = new Dictionary<Guid, ChildWindowModel>();
        var originalIdByNewId = new Dictionary<Guid, Guid>();
        foreach (var json in snapshot.PanelJsonItems)
        {
            ChildWindowModel? model = null;
            try
            {
                model = JsonSerializer.Deserialize<ChildWindowModel>(json, SerializerOptions);
            }
            catch (JsonException)
            {
                // Skip panels whose JSON is corrupt rather than aborting the whole restore.
            }

            if (model is null) continue;

            // Assign a fresh Id so we never collide with panels that may still be
            // in the process of being cleaned up by the window system.
            var originalId = model.Id;
            modelsByOriginalId.TryAdd(originalId, model);
            model.Id = Guid.NewGuid();
            originalIdByNewId[model.Id] = originalId;
            models.Add(model);
        }

        // Cross-panel-aware stale-path resolution across every panel about to be recreated at once —
        // see this method's remarks (MAJOR-1). Runs before the orphan check below so a stale original
        // that resolves to a managed copy counts as "referenced by the snapshot".
        WindowManager.ResolveStalePanelPaths(models, _itemStorage);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            foreach (var path in model.FilePaths)
                referenced.Add(path);
        }

        // Make the live models' FilePaths current before inspecting them (same reason as Capture).
        _windowManager.SyncAllWindowsToModels();

        var currentPanels = _windowManager.GetAllChildWindows().ToList();
        ForgetDeadPanels(currentPanels);

        // Return the managed items of panels the snapshot does not reference (B2). Skipped entirely in a
        // read-only session (F2): panel removal is not persisted there, so nothing can be orphaned on disk,
        // and a physical move would take files off the desktop with no persisted reference to them.
        var orphans = _stateStore.IsWritable
            ? ReturnOrphanedItems(currentPanels, referenced, modelsByOriginalId)
            : new OrphanReturnResult([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        var keptPanelIds = orphans.KeptPanelIds;
        var unreturned = orphans.Unreturned;

        try
        {
            // Remove all current panels that are not kept. This is NOT a permanent deletion — the panels are
            // immediately recreated below from the snapshot's JSON, which references the very same
            // app-managed paths, so releaseManagedItems must stay false or the recreated panels would point to
            // files that were just deleted. deferSave: true avoids one synchronous full-state write per
            // removed panel (Phase 7 / Block C, M4) — SaveWindowsStateNow() below flushes it once.
            foreach (var panel in currentPanels)
            {
                if (keptPanelIds.Contains(panel.Id)) continue;
                _windowManager.RemoveChildWindow(panel.Id, releaseManagedItems: false, deferSave: true);
                _originalIdByLiveId.Remove(panel.Id);
            }

            if (keptPanelIds.Count > 0)
                StripKeptPanels(currentPanels, keptPanelIds, unreturned);

            // Per-model isolation (review cycle 3, M2; mirrors WindowManager.CreateEachIsolated): one panel that
            // fails to construct must not abort the others nor skip the save/report below.
            foreach (var model in models)
            {
                try
                {
                    _windowManager.CreateChildWindow(model);
                    _originalIdByLiveId[model.Id] = originalIdByNewId[model.Id];
                }
                catch (Exception ex)
                {
                    AppLog.Current.Error(
                        "LayoutSnapshotService",
                        "RestorePanelSkipped",
                        ex,
                        new KeyValuePair<string, object?>("panelId", model.Id));

                    // The managed items only this model referenced now have no panel: count them as not
                    // returned so the user is told (ADR-035, fail visibly).
                    CountAsUnreturned(model, unreturned);
                }
            }
        }
        finally
        {
            try
            {
                // Structural (whole panel set replaced by the snapshot) — write synchronously so the restored
                // layout survives even if the app is closed moments later.
                _windowManager.SaveWindowsStateNow();
            }
            finally
            {
                ReportOutcome(orphans.ReturnedCount, unreturned.Count);
            }
        }
    }

    /// <inheritdoc />
    public void Delete(string snapshotId)
    {
        var snapshot = _snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null) return;

        _snapshots.Remove(snapshot);
        Save();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Persists synchronously via <see cref="IApplicationStateService.SaveNow"/> — snapshot
    /// create/delete is structural (ADR-024 D-12). <see cref="IApplicationStateService.SaveNow"/>
    /// itself pulls the <em>current</em> <see cref="Snapshots"/> value at write time (uniform pull
    /// model, ADR-024 D-11), so this call only needs to make sure <see cref="_snapshots"/> already
    /// reflects the desired state before asking for the write.
    /// </remarks>
    public void Save() => _applicationStateService.SaveNow();

    /// <inheritdoc />
    /// <remarks>
    /// Reads <see cref="IApplicationStateService.Snapshots"/> — the already-loaded, already-migrated
    /// snapshot list from the single <c>ApplicationStateService.Load()</c> call in <c>Program.Main</c>
    /// (ADR-024 D-11) — rather than parsing JSON off settings itself, but only on the <em>first</em>
    /// call: <see cref="IApplicationStateService.Snapshots"/> is a value frozen at that single startup
    /// <c>Load()</c> call and is never updated afterwards as snapshots are captured/deleted during the
    /// session (those live only in <see cref="_snapshots"/>). Idempotent — safe to call more than once
    /// (e.g. every time the settings window is opened, via <c>LayoutSnapshotsViewModel</c>'s
    /// constructor): every call after the first is a no-op, so a snapshot captured or deleted earlier
    /// in the session is never wiped out and replaced by the stale startup snapshot list (ADR-024
    /// finalization MAJOR M-2 — the previous Clear()+re-add-every-call behavior did exactly that).
    /// </remarks>
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;

        _snapshots.Clear();
        _snapshots.AddRange(_applicationStateService.Snapshots);
    }

    /// <summary>
    /// Returns the managed items of current panels that the snapshot does not reference to their original
    /// location (B2). A panel with an item that cannot be returned is either merged into its recreated twin (F1)
    /// or kept (fail visibly, ADR-035).
    /// </summary>
    /// <param name="currentPanels">The live panels (models already synced).</param>
    /// <param name="referenced">Every path the snapshot's panels reference.</param>
    /// <param name="modelsByOriginalId">The snapshot's models, keyed by the Id they were captured with.</param>
    /// <returns>The kept panels, the paths that could not be returned and the number of files put back.</returns>
    private OrphanReturnResult ReturnOrphanedItems(
        IReadOnlyList<ChildWindowModel> currentPanels,
        HashSet<string> referenced,
        Dictionary<Guid, ChildWindowModel> modelsByOriginalId)
    {
        var keptPanelIds = new HashSet<Guid>();
        var unreturned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var returnedCount = 0;
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in currentPanels)
        {
            // F3: a Folder Portal mirrors a live folder and owns no app-managed items.
            if (!string.IsNullOrWhiteSpace(panel.SourceFolderPath)) continue;

            var panelUnreturned = new List<string>();
            foreach (var path in panel.FilePaths)
            {
                if (referenced.Contains(path) || !_itemStorage.IsManaged(path) || !attempted.Add(path)) continue;

                var outcome = _itemStorage.Restore(path);
                if (outcome is RestoreOutcome.NotManaged) continue;
                if (outcome is RestoreOutcome.Restored)
                {
                    returnedCount++;
                    continue;
                }

                unreturned.Add(path);
                panelUnreturned.Add(path);
            }

            if (panelUnreturned.Count == 0) continue;

            // F1: this panel is also in the snapshot (its twin is recreated later) - move the stuck paths
            // into the twin and let the old window go, instead of keeping a duplicate panel.
            if (TryFindTwin(panel.Id, modelsByOriginalId, out var twin) && string.IsNullOrWhiteSpace(twin.SourceFolderPath))
            {
                foreach (var path in panelUnreturned)
                {
                    if (!twin.FilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        twin.FilePaths.Add(path);
                }
            }
            else
            {
                keptPanelIds.Add(panel.Id);
            }
        }

        return new OrphanReturnResult(keptPanelIds, unreturned, returnedCount);
    }

    /// <summary>
    /// Finds the snapshot model a live panel is the recreation of (review cycle 3, M1): by the Id the live panel
    /// itself was captured with, or — when it is itself the product of an earlier <see cref="Restore"/> and thus
    /// carries a fresh Id — by the snapshot Id it was recreated from.
    /// </summary>
    private bool TryFindTwin(Guid livePanelId, Dictionary<Guid, ChildWindowModel> modelsByOriginalId, out ChildWindowModel twin)
    {
        if (modelsByOriginalId.TryGetValue(livePanelId, out var direct))
        {
            twin = direct;
            return true;
        }

        if (_originalIdByLiveId.TryGetValue(livePanelId, out var originalId)
            && modelsByOriginalId.TryGetValue(originalId, out var mapped))
        {
            twin = mapped;
            return true;
        }

        twin = null!;
        return false;
    }

    /// <summary>Drops twin-tracking entries of panels that no longer exist, so the map cannot grow unbounded.</summary>
    private void ForgetDeadPanels(IReadOnlyList<ChildWindowModel> currentPanels)
    {
        if (_originalIdByLiveId.Count == 0) return;

        var live = new HashSet<Guid>(currentPanels.Select(p => p.Id));
        foreach (var id in _originalIdByLiveId.Keys.Where(id => !live.Contains(id)).ToList())
            _originalIdByLiveId.Remove(id);
    }

    /// <summary>
    /// Adds the managed items of a snapshot panel that could not be recreated to <paramref name="unreturned"/> —
    /// they are still hidden in managed storage with no panel referencing them.
    /// </summary>
    private void CountAsUnreturned(ChildWindowModel failed, HashSet<string> unreturned)
    {
        if (!string.IsNullOrWhiteSpace(failed.SourceFolderPath)) return;

        foreach (var path in failed.FilePaths)
        {
            try
            {
                if (_itemStorage.IsManaged(path))
                    unreturned.Add(path);
            }
            catch (Exception)
            {
                // Best effort: the report is the only thing that depends on this.
            }
        }
    }

    private sealed record OrphanReturnResult(HashSet<Guid> KeptPanelIds, HashSet<string> Unreturned, int ReturnedCount);

    /// <summary>
    /// B2 fail-visibly path: takes every item OTHER than the still-managed, un-returnable ones out of the
    /// panels that were kept (they are either already back on disk or will be referenced by the recreated
    /// snapshot panels — never by two panels).
    /// </summary>
    private void StripKeptPanels(
        IReadOnlyList<ChildWindowModel> currentPanels,
        HashSet<Guid> keptPanelIds,
        HashSet<string> unreturned)
    {
        var toStrip = currentPanels
            .Where(panel => keptPanelIds.Contains(panel.Id))
            .SelectMany(panel => panel.FilePaths)
            .Where(path => !unreturned.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toStrip.Count > 0)
            _windowManager.RemoveItemsFromPanels(toStrip);
    }

    /// <summary>
    /// Tells the user how the item return went (F6): a Critical status with the count of files that could not
    /// be returned (ADR-035, fail visibly), otherwise a self-hiding Success status with the count of files put
    /// back — nothing at all when no file was involved.
    /// </summary>
    private void ReportOutcome(int returnedCount, int unreturnedCount)
    {
        if (unreturnedCount > 0)
        {
            _statusNotifier.Show(
                StatusSeverity.Critical,
                _localization.Format("Snapshot_Restore_ItemsNotReturned_Format", unreturnedCount),
                _localization.GetString("Snapshot_Restore_ItemsNotReturned_Title"));
            return;
        }

        if (returnedCount > 0)
        {
            _statusNotifier.Show(
                StatusSeverity.Success,
                _localization.Format("Snapshot_Restore_ItemsReturned_Format", returnedCount),
                _localization.GetString("Snapshot_Restore_Title"));
        }
    }
}
