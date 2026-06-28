using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Point = System.Drawing.Point;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Handles window management in the application.
/// </summary>
public class WindowManager : IWindowManager
{
    // Debounce window for SaveWindowsState (Phase 7 / Block C): a burst of frequent actions (drag,
    // resize, filter/view switches, ...) inside this window collapses into a single write instead of
    // one disk write per action. See docs/PLANS/phase-7-performance-hardening.md.
    private static readonly TimeSpan SaveDebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _serviceProvider;
    private readonly IMonitorService _monitorService;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly Dictionary<Guid, (ChildWindow Window, ChildWindowModel Model)> _childWindows = [];
    private readonly SaveScheduler _saveScheduler;

    // Lazily resolved to break the circular dependency WindowManager ↔ PageManagerService.
    private IPageManager? _pageManager;

    // Lazily created against Application.Current's dispatcher the first time it's actually needed
    // (constructor-time it may not exist yet in tests/headless hosts) and re-resolved to null on every
    // SaveScheduler call if Application.Current has since gone away (e.g. late shutdown) — see
    // ResolveDebounceTimer.
    private IDebounceTimer? _debounceTimer;

    // The last JSON actually written to Properties.Settings.Default.ChildWindowsState — lets
    // PersistWindowsState skip the disk write entirely when nothing changed since the last save.
    private string? _lastSavedJson;

    // Set by LoadWindowsState when a load was incomplete (corrupt JSON, or one or more panels failed
    // to construct) so the very next PersistWindowsState call backs up the original raw payload before
    // it gets overwritten with the (now smaller) recovered panel set. See docs/PLANS/
    // phase-7-performance-hardening.md, Block C4.
    private string? _pendingCorruptStateBackup;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowManager"/> class.
    /// </summary>
    /// <param name="serviceProvider">DI provider used to resolve windows.</param>
    /// <param name="monitorService">Monitor enumeration / change-tracking service.</param>
    /// <param name="itemStorage">App-managed storage used to release orphaned managed copies on permanent panel deletion.</param>
    public WindowManager(IServiceProvider serviceProvider, IMonitorService monitorService, IPanelItemStorageService itemStorage)
    {
        _serviceProvider = serviceProvider;
        _monitorService = monitorService;
        _itemStorage = itemStorage;
        _monitorService.MonitorsChanged += OnMonitorsChanged;
        _saveScheduler = new SaveScheduler(PersistWindowsState, ResolveDebounceTimer, SaveDebounceWindow);
    }

    private IPageManager PageManager => _pageManager ??= _serviceProvider.GetRequiredService<IPageManager>();

    /// <inheritdoc />
    public event EventHandler<bool>? OnMainWindowVisibilityChanged;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowAdded;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowRemoved;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowModified;

    /// <inheritdoc />
    public void StartMainWindow()
    {
        var mainWindow = GetOrCreateMainWindow();

        if (mainWindow.Visibility == Visibility.Visible)
            HideWindow(mainWindow);
        else
            ShowWindow(mainWindow);
    }

    /// <inheritdoc />
    public ChildWindow CreateChildWindow(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Assign the current page if the model does not already have a non-default page.
        if (string.IsNullOrWhiteSpace(model.PageId))
            model.PageId = "default";

        ResolveMonitorPlacement(model);

        var window = _serviceProvider.GetRequiredService<ChildWindow>();
        window.Initialize(model);

        _childWindows[model.Id] = (window, model);

        // Only show the panel when its page is the active page.
        var isOnActivePage = model.PageId == PageManager.CurrentPage;
        if (model.IsVisible && isOnActivePage)
        {
            window.Show();
        }

        OnChildWindowAdded?.Invoke(this, model);
        return window;
    }

    /// <inheritdoc />
    public void ApplyPanelVisibility(ChildWindowModel model)
    {
        if (!_childWindows.TryGetValue(model.Id, out var entry)) return;

        if (model.IsVisible)
            entry.Window.Show();
        else
            entry.Window.Hide();
    }

    /// <inheritdoc />
    public void RemoveChildWindow(Guid windowId) => RemoveChildWindow(windowId, releaseManagedItems: false);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="releaseManagedItems"/> must only be <see langword="true"/> when the panel is
    /// being permanently deleted, i.e. its app-managed item copies should never be reachable again.
    /// <c>ChildWindow.Dispose</c>/<c>OnClosing</c> fire on both permanent deletion AND ordinary close
    /// (panels must survive an app restart), so releasing managed item copies there would wipe data
    /// on every shutdown — this method is the deliberate "delete forever" decision point instead.
    /// There are three real call sites, each of which must pass the right value:
    /// <list type="bullet">
    /// <item><c>MainWindowViewModel.RemovePanel</c> — the user explicitly deletes a single panel via
    /// the UI. Permanent → <c>releaseManagedItems: true</c>.</item>
    /// <item><c>PageManagerService.RemovePage</c> — the user explicitly deletes an entire page, which
    /// permanently destroys every panel on it. Permanent → <c>releaseManagedItems: true</c>.</item>
    /// <item><c>LayoutSnapshotService.Restore</c> — removes every current panel only so it can
    /// immediately recreate the panel set from a snapshot's JSON, which references the very same
    /// managed paths. NOT a permanent deletion → <c>releaseManagedItems: false</c> (the default),
    /// otherwise restoring a snapshot would delete the managed files the recreated panels point to.
    /// </item>
    /// </list>
    /// This overload always persists synchronously (<c>deferSave: false</c>) — see
    /// <see cref="RemoveChildWindow(Guid, bool, bool)"/> for the bulk-removal-loop variant.
    /// </remarks>
    public void RemoveChildWindow(Guid windowId, bool releaseManagedItems)
        => RemoveChildWindow(windowId, releaseManagedItems, deferSave: false);

    /// <inheritdoc />
    /// <remarks>
    /// See <see cref="RemoveChildWindow(Guid, bool)"/> for <paramref name="releaseManagedItems"/>.
    /// <paramref name="deferSave"/> exists so bulk-removal call sites (<c>LayoutSnapshotService.Restore</c>,
    /// <c>PageManagerService.RemovePage</c>) don't pay for one synchronous full-state write per removed
    /// panel (Phase 7 / Block C, M4): pass <see langword="true"/> inside the loop — this only debounces
    /// a request via <see cref="SaveWindowsState"/> — then call <see cref="SaveWindowsStateNow"/> once
    /// after the loop to flush it synchronously. Every other caller must keep the default/explicit
    /// <see langword="false"/> so its single removal is never left to a debounce window that might not
    /// elapse before the process exits.
    /// </remarks>
    public void RemoveChildWindow(Guid windowId, bool releaseManagedItems, bool deferSave)
    {
        if (!_childWindows.TryGetValue(windowId, out var entry)) return;

        OnChildWindowRemoved?.Invoke(this, entry.Model);

        // Sync the model with any pending UI-side changes before inspecting FilePaths, then release
        // any app-managed copies so they don't linger orphaned in PanelItems storage — but only when
        // this is a genuine permanent deletion (see remarks above).
        entry.Window.SyncToModel();
        ReleaseManagedItemsIfRequested(entry.Model, _itemStorage, releaseManagedItems);

        entry.Window.Close();
        _childWindows.Remove(windowId);

        // Structural change (panel permanently gone or being recreated by a snapshot restore) — write
        // synchronously so it can never be lost to a debounce window that never gets to elapse, unless
        // the caller is driving a bulk removal loop and will flush once at the end itself.
        if (deferSave)
            SaveWindowsState();
        else
            SaveWindowsStateNow();
    }

    /// <summary>
    /// Releases every app-managed item copy referenced by <paramref name="model"/>'s
    /// <see cref="ChildWindowModel.FilePaths"/>. Folder Portal panels have no persisted
    /// <see cref="ChildWindowModel.FilePaths"/> pointing into managed storage, so
    /// <see cref="IPanelItemStorageService.IsManaged"/> naturally makes this a no-op for them.
    /// </summary>
    /// <remarks>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can
    /// unit test this pure model+storage logic directly, without needing a real WPF <c>ChildWindow</c>
    /// (which <see cref="RemoveChildWindow"/> otherwise requires via DI).
    /// </remarks>
    internal static void ReleaseManagedItems(ChildWindowModel model, IPanelItemStorageService storage)
    {
        foreach (var path in model.FilePaths)
        {
            if (storage.IsManaged(path))
            {
                storage.Release(path);
            }
        }
    }

    /// <summary>
    /// Gates <see cref="ReleaseManagedItems"/> behind the <c>releaseManagedItems</c> decision made by
    /// <see cref="RemoveChildWindow(Guid, bool)"/>'s callers. Extracted as its own internal member —
    /// rather than an inline <see langword="if"/> in <see cref="RemoveChildWindow(Guid, bool)"/> —
    /// solely so <c>DesktopOrganizer.Tests</c> can directly assert that the flag controls the release
    /// call, without needing a real WPF <c>ChildWindow</c>.
    /// </summary>
    internal static void ReleaseManagedItemsIfRequested(ChildWindowModel model, IPanelItemStorageService storage, bool releaseManagedItems)
    {
        if (releaseManagedItems)
        {
            ReleaseManagedItems(model, storage);
        }
    }

    /// <inheritdoc />
    public IEnumerable<ChildWindowModel> GetAllChildWindows()
        => _childWindows.Values.Select(entry => entry.Model);

    /// <inheritdoc />
    public void SaveWindowsState() => _saveScheduler.Request();

    /// <inheritdoc />
    public void SaveWindowsStateNow() => _saveScheduler.Flush();

    /// <inheritdoc />
    public void SyncAllWindowsToModels() => SyncEachIsolated(_childWindows.Values, e => e.Window.SyncToModel());

    /// <inheritdoc />
    public void LoadWindowsState()
    {
        var savedState = Properties.Settings.Default.ChildWindowsState;
        if (string.IsNullOrWhiteSpace(savedState)) return;

        List<ChildWindowModel>? states;
        try
        {
            states = JsonSerializer.Deserialize<List<ChildWindowModel>>(savedState);
        }
        catch (Exception ex)
        {
            // Not just JsonException: a corrupt/foreign payload can also fail with e.g.
            // FormatException from a bad enum/Guid value inside the JSON. Either way, don't clear
            // _childWindows here — at this point in the call chain it's always empty already (this
            // only runs once, from App.LoadPanels before any window is created), so clearing it would
            // do nothing useful; it would only mislead a future reader into thinking this recovers
            // already-created windows.
            Console.Error.WriteLine($"Failed to deserialize saved window state, starting with no panels: {ex.Message}");
            _pendingCorruptStateBackup = savedState;
            return;
        }

        if (states is null) return;

        var skipped = CreateEachIsolated(states, state => CreateChildWindow(state));
        if (skipped > 0)
        {
            Console.Error.WriteLine($"Skipped {skipped} corrupt saved panel(s) out of {states.Count} while loading window state.");
            _pendingCorruptStateBackup = savedState;
        }
    }

    /// <summary>
    /// Creates every panel in <paramref name="states"/> via <paramref name="factory"/>, isolating each
    /// call in its own try/catch so a single panel that fails to construct (e.g.
    /// <c>ResolveMonitorPlacement</c>/DI throwing) doesn't abort the rest of the load.
    /// </summary>
    /// <remarks>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can
    /// unit test this per-item isolation directly with a fake <paramref name="factory"/>, without
    /// needing a real WPF <c>ChildWindow</c> for every state.
    /// </remarks>
    /// <param name="states">The saved panel states to (re)create.</param>
    /// <param name="factory">Creates a single panel; may throw.</param>
    /// <returns>The number of states that failed to construct and were skipped.</returns>
    internal static int CreateEachIsolated(IEnumerable<ChildWindowModel> states, Action<ChildWindowModel> factory)
    {
        var skipped = 0;
        foreach (var state in states)
        {
            try
            {
                factory(state);
            }
            catch (Exception ex)
            {
                skipped++;
                Console.Error.WriteLine($"Skipped a corrupt saved panel ('{state.Title}', id {state.Id}): {ex.Message}");
            }
        }

        return skipped;
    }

    /// <summary>
    /// Runs <paramref name="sync"/> against every entry in <paramref name="windows"/>, isolating each
    /// call in its own try/catch so a single panel that fails to sync (e.g. a torn-down WPF window)
    /// doesn't prevent the rest from being synced — and, critically, doesn't abort a caller that goes
    /// on to serialize/write the whole set afterwards (Phase 7 / Block C, M1 fix).
    /// </summary>
    /// <remarks>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can
    /// unit test this per-item isolation directly with a fake <paramref name="sync"/> delegate, without
    /// needing a real WPF <c>ChildWindow</c> for every entry — mirrors <see cref="CreateEachIsolated"/>.
    /// </remarks>
    /// <param name="windows">The live window/model pairs to sync.</param>
    /// <param name="sync">Syncs a single entry (typically <c>Window.SyncToModel()</c>); may throw.</param>
    internal static void SyncEachIsolated(
        IEnumerable<(ChildWindow Window, ChildWindowModel Model)> windows,
        Action<(ChildWindow Window, ChildWindowModel Model)> sync)
    {
        foreach (var entry in windows)
        {
            try
            {
                sync(entry);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to sync panel '{entry.Model.Title}' ({entry.Model.Id}): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The actual persistence work deferred/synchronized by <see cref="_saveScheduler"/>: syncs every
    /// live window into its model, serializes, and writes to <see cref="Properties.Settings"/>. Never
    /// throws — a write failure (disk full, no permissions, locked user.config) is logged and
    /// swallowed so it can never crash the app from an ordinary user action.
    /// </summary>
    /// <remarks>
    /// The sync loop is isolated per-panel via <see cref="SyncAllWindowsToModels"/> (Phase 7 / Block C,
    /// M1 fix) rather than wrapped together with the serialize+save below in one try/catch: a single
    /// panel throwing while syncing must never abort the write for every other panel, and must never
    /// silently skip the write entirely on every subsequent save (including the final flush on app
    /// exit, since the unsynced model would otherwise remain in <see cref="_childWindows"/> forever).
    /// </remarks>
    private void PersistWindowsState()
    {
        try
        {
            SyncAllWindowsToModels();

            var states = GetAllChildWindows().ToList();
            var json = JsonSerializer.Serialize(states);

            // Cheap no-op guard: skip the disk write entirely (Settings.Save() rewrites the whole
            // user.config) when nothing actually changed since the last write.
            if (string.Equals(json, _lastSavedJson, StringComparison.Ordinal)) return;

            BackupPendingCorruptStateIfAny();

            Properties.Settings.Default.ChildWindowsState = json;
            Properties.Settings.Default.Save();
            _lastSavedJson = json;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save window state: {ex.Message}");
        }
    }

    /// <summary>
    /// If <see cref="LoadWindowsState"/> flagged the just-loaded state as incomplete (corrupt JSON, or
    /// one or more panels skipped), backs up the original raw payload to
    /// <c>%LocalAppData%\DesktopOrganizer\ChildWindowsState.corrupt-&lt;timestamp&gt;.json</c> before
    /// it's about to be overwritten by the first save of the (now smaller) recovered panel set —
    /// otherwise the user's full layout is lost the moment the debounced/flushed save lands. Runs at
    /// most once per incomplete load; failures here are non-fatal (best-effort safety net, not a
    /// feature the app depends on).
    /// </summary>
    private void BackupPendingCorruptStateIfAny()
    {
        var raw = _pendingCorruptStateBackup;
        if (raw is null) return;
        _pendingCorruptStateBackup = null;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopOrganizer");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"ChildWindowsState.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, raw);
            Console.Error.WriteLine($"Backed up pre-recovery window state to '{path}' before it would have been overwritten.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to back up corrupt window state: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the <see cref="IDebounceTimer"/> to use for <see cref="_saveScheduler"/>, evaluated on
    /// every call (not cached beyond the lazily-created instance) so that a headless/test host — or a
    /// late-shutdown window where <see cref="Application.Current"/> has already gone
    /// <see langword="null"/> — degrades to an immediate synchronous save instead of silently losing
    /// the request. See <see cref="SaveScheduler"/> remarks.
    /// </summary>
    private IDebounceTimer? ResolveDebounceTimer()
    {
        if (Application.Current is not { } app) return null;
        return _debounceTimer ??= new DispatcherDebounceTimer(app.Dispatcher);
    }

    // MainWindow is a singleton in DI — always returns the same instance
    private MainWindow GetOrCreateMainWindow()
        => _serviceProvider.GetRequiredService<MainWindow>();

    /// <summary>
    /// Ensures the panel is placed on a valid monitor:
    /// <list type="bullet">
    /// <item>If <see cref="ChildWindowModel.MonitorDeviceId"/> matches an existing monitor, keep the
    /// position but clamp it inside that monitor's bounds.</item>
    /// <item>If the stored monitor no longer exists, move the panel onto the monitor that currently
    /// contains its position, else the primary monitor.</item>
    /// <item>If no monitor id is set yet (new panel), assign the monitor under the panel's position.</item>
    /// </list>
    /// All coordinates are WPF DIPs, matching <see cref="MonitorInfo"/>.
    /// </summary>
    private void ResolveMonitorPlacement(ChildWindowModel model)
    {
        var pos = new System.Windows.Point(model.Position.X, model.Position.Y);

        var target = !string.IsNullOrEmpty(model.MonitorDeviceId)
            ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
            : null;

        // Stored monitor gone (or never set) → resolve by current position, else primary.
        target ??= _monitorService.FindMonitorAt(pos) ?? _monitorService.GetPrimaryMonitor();

        model.MonitorDeviceId = target.DeviceName;
        ClampIntoMonitor(model, target);
    }

    /// <summary>
    /// Keeps the panel's top-left within the monitor's work area so it never lands off-screen.
    /// </summary>
    /// <remarks>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can
    /// unit test this pure model+monitor clamping logic directly — <see cref="OnMonitorsChanged"/>,
    /// which drives it in production, also touches real WPF <c>ChildWindow</c> instances held in
    /// <see cref="_childWindows"/> and isn't practically unit-testable end-to-end (see remarks there).
    /// </remarks>
    internal static void ClampIntoMonitor(ChildWindowModel model, MonitorInfo monitor)
    {
        var area = monitor.WorkArea;
        var width = Math.Max(0, model.Size.Width);
        var height = Math.Max(0, model.Size.Height);

        // If the panel already sits fully within the monitor, leave it untouched. Must check the
        // whole Position+Size rect, not just the top-left point — otherwise a panel whose top-left
        // remains inside a shrunk work area (e.g. a taskbar appearing and eating the bottom edge)
        // would be considered "inside" while its bottom/right edge is actually off-screen.
        var inside = model.Position.X >= area.Left && model.Position.Y >= area.Top
                     && model.Position.X + width <= area.Right && model.Position.Y + height <= area.Bottom;
        if (inside) return;

        var maxX = Math.Max(area.Left, area.Right - width);
        var maxY = Math.Max(area.Top, area.Bottom - height);
        var x = Math.Min(Math.Max(model.Position.X, area.Left), maxX);
        var y = Math.Min(Math.Max(model.Position.Y, area.Top), maxY);
        model.Position = new Point((int)x, (int)y);
    }

    /// <summary>
    /// Re-evaluates placement for every panel when the monitor configuration changes (monitor
    /// added/removed, or the resolution/work area of an existing monitor changed — e.g.
    /// <c>WM_DISPLAYCHANGE</c>/<c>WM_SETTINGCHANGE</c> relayed via <see cref="IMonitorService"/>).
    /// Panels whose monitor disappeared are moved onto the primary monitor. Panels whose monitor is
    /// still present are re-clamped into its (possibly shrunk) current work area, so a panel that
    /// ends up off-screen after a resolution/scale change is pulled back in.
    /// </summary>
    /// <remarks>
    /// Not practically unit-testable end-to-end: <see cref="_childWindows"/> only gets populated via
    /// <see cref="CreateChildWindow"/>, which resolves a real WPF <c>ChildWindow</c> through DI and
    /// (when visible) calls <c>Show()</c>/creates a native window handle — the model+monitor re-clamp
    /// math itself is covered directly via the internal <see cref="ClampIntoMonitor"/>; the
    /// <c>window.Left</c>/<c>window.Top</c> sync at the end of this loop is a trivial pass-through and
    /// is manual verify (Win10/Win11).
    /// </remarks>
    private void OnMonitorsChanged()
    {
        if (_childWindows.Count == 0) return;

        var primary = _monitorService.GetPrimaryMonitor();

        foreach (var (window, model) in _childWindows.Values)
        {
            var monitor = !string.IsNullOrEmpty(model.MonitorDeviceId)
                ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
                : null;

            if (monitor is null)
            {
                // Stored monitor is gone → fall back to primary.
                model.MonitorDeviceId = primary.DeviceName;
                monitor = primary;
            }

            // Re-clamp against the (possibly resized) current work area. ClampIntoMonitor early-outs
            // when the panel is already fully inside, so this is a cheap no-op in the common case.
            ClampIntoMonitor(model, monitor);

            window.Left = model.Position.X;
            window.Top = model.Position.Y;
        }

        SaveWindowsState();
    }

    private void ShowWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        MainWindowVisibilityChanged(true);
    }

    private void HideWindow(MainWindow mainWindow)
    {
        mainWindow.Hide();
        MainWindowVisibilityChanged(false);
    }

    private void MainWindowVisibilityChanged(bool visibility)
        => OnMainWindowVisibilityChanged?.Invoke(this, visibility);
}
