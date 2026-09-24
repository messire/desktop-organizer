using System.Text.Json;
using DesktopOrganizer.Core;
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
    private readonly List<LayoutSnapshot> _snapshots = [];

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
    public LayoutSnapshotService(
        IWindowManager windowManager,
        IApplicationStateService applicationStateService,
        IPanelItemStorageService itemStorage)
    {
        _windowManager = windowManager;
        _applicationStateService = applicationStateService;
        _itemStorage = itemStorage;
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
    /// </remarks>
    public void Restore(string snapshotId)
    {
        var snapshot = _snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null) return;

        // Remove all current panels. This is NOT a permanent deletion — the panels are immediately
        // recreated below from the snapshot's JSON, which references the very same app-managed
        // paths, so releaseManagedItems must stay false or the recreated panels would point to
        // files that were just deleted. deferSave: true avoids one synchronous full-state write per
        // removed panel (Phase 7 / Block C, M4) — SaveWindowsStateNow() below flushes it once.
        var currentIds = _windowManager.GetAllChildWindows().Select(m => m.Id).ToList();
        foreach (var id in currentIds)
            _windowManager.RemoveChildWindow(id, releaseManagedItems: false, deferSave: true);

        // Deserialize every panel model up front — see this method's remarks for why this must happen
        // before ANY panel is created.
        var models = new List<ChildWindowModel>();
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
            model.Id = Guid.NewGuid();
            models.Add(model);
        }

        // Cross-panel-aware stale-path resolution across every panel about to be recreated at once —
        // see this method's remarks (MAJOR-1).
        WindowManager.ResolveStalePanelPaths(models, _itemStorage);

        foreach (var model in models)
        {
            // Skip panels with invalid paths — show nothing rather than crashing.
            _windowManager.CreateChildWindow(model);
        }

        // Structural (whole panel set replaced by the snapshot) — write synchronously so the restored
        // layout survives even if the app is closed moments later.
        _windowManager.SaveWindowsStateNow();
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
}
