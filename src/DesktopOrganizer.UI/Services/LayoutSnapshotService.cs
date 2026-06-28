using System.Text.Json;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Implements named layout snapshots — capture, restore, delete and JSON persistence.
/// Snapshots are stored in <see cref="IAppSettings.LayoutSnapshots"/> as a JSON array.
/// Maximum of 10 snapshots; the oldest is automatically removed when the limit is exceeded.
/// </summary>
internal sealed class LayoutSnapshotService : ILayoutSnapshotService
{
    private const int MaxSnapshots = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IWindowManager _windowManager;
    private readonly IAppSettings _appSettings;
    private readonly List<LayoutSnapshot> _snapshots = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutSnapshotService"/> class.
    /// </summary>
    public LayoutSnapshotService(IWindowManager windowManager, IAppSettings appSettings)
    {
        _windowManager = windowManager;
        _appSettings = appSettings;
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

        // Re-create panels from the snapshot.
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
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_snapshots, SerializerOptions);
            _appSettings.LayoutSnapshots = json;
            _appSettings.Save();
        }
        catch (JsonException)
        {
            // If serialization fails (e.g. some model contains non-serialisable data), keep last good state.
        }
    }

    /// <inheritdoc />
    public void Load()
    {
        _snapshots.Clear();

        var json = _appSettings.LayoutSnapshots;
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<LayoutSnapshot>>(json, SerializerOptions);
            if (loaded is not null)
                _snapshots.AddRange(loaded);
        }
        catch (JsonException)
        {
            // Corrupt persistence — start with an empty list rather than crashing.
        }
    }
}
