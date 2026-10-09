using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.State;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Point = System.Drawing.Point;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Handles window management in the application.
/// </summary>
public class WindowManager : IWindowManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMonitorService _monitorService;
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IApplicationStateService _applicationStateService;
    private readonly IAppSettings _appSettings;
    private readonly IDesktopScaleProvider _desktopScale;
    private readonly IAppLog? _appLog;
    private readonly Dictionary<Guid, (ChildWindow Window, ChildWindowModel Model)> _childWindows = [];

    // Lazily resolved to break the circular dependency WindowManager ↔ PageManagerService.
    private IPageManager? _pageManager;

    /// <summary>
    /// M-C fix (code review, panel clone to page, backlog 1.x wave 3 review): the full panel-state batch
    /// <see cref="LoadWindowsState"/> is currently (re)constructing panels from, set only for the duration
    /// of that method's own <see cref="CreateEachIsolated"/> loop — see
    /// <see cref="GetManagedPathsClaimedByOtherPanels"/>'s remarks for why. <see langword="null"/> at every
    /// other time (a live "Copy to page" clone, an ordinary panel edit, …), where <see cref="_childWindows"/>
    /// alone is already the complete picture.
    /// </summary>
    private IReadOnlyList<ChildWindowModel>? _loadingStates;

    // Lazily resolved (ADR-045): only ClonePanelToPage's title resolution needs it, so — like
    // _pageManager above — it is never added as a constructor parameter every test/DI call site would
    // otherwise have to supply.
    private ILocalizationService? _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowManager"/> class.
    /// </summary>
    /// <param name="serviceProvider">DI provider used to resolve windows.</param>
    /// <param name="monitorService">Monitor enumeration / change-tracking service.</param>
    /// <param name="itemStorage">App-managed storage used to release orphaned managed copies on permanent panel deletion.</param>
    /// <param name="applicationStateService">
    /// Owns the versioned JSON application-state document (ADR-024 C6) — <see cref="LoadWindowsState"/>
    /// reads the already-loaded, already-migrated panel list from it instead of parsing
    /// <c>Properties.Settings.Default.ChildWindowsState</c> directly, and
    /// <see cref="SaveWindowsState"/>/<see cref="SaveWindowsStateNow"/> are thin delegates onto its
    /// single, shared <c>SaveScheduler</c> (ADR-024 D-12) instead of owning one here.
    /// </param>
    /// <param name="appSettings">
    /// ADR-011 Amendment (2026-09-24): supplies the current <see cref="IAppSettings.GridSize"/> for the
    /// restore-time/settings-change grid-size normalization (<see cref="NormalizeLegacyFreeformToGridSize"/>/
    /// <see cref="NormalizeGridAlignedToGridSize"/>/<see cref="NormalizePanelsToGrid"/>).
    /// </param>
    /// <param name="desktopScale">
    /// The per-monitor DPI snapshot provider (release-hardening F, ADR-037) — <see cref="CollectOtherPanelBounds"/>
    /// reads live panel geometry through it, and <see cref="RecoverDesktopLayer"/> refreshes it once every
    /// surviving panel has been re-embedded/rebuilt.
    /// </param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public WindowManager(
        IServiceProvider serviceProvider,
        IMonitorService monitorService,
        IPanelItemStorageService itemStorage,
        IApplicationStateService applicationStateService,
        IAppSettings appSettings,
        IDesktopScaleProvider desktopScale,
        IAppLog? appLog = null)
    {
        _serviceProvider = serviceProvider;
        _monitorService = monitorService;
        _itemStorage = itemStorage;
        _applicationStateService = applicationStateService;
        _appSettings = appSettings;
        _desktopScale = desktopScale;
        _appLog = appLog;
        _monitorService.MonitorsChanged += OnMonitorsChanged;
    }

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    private IPageManager PageManager => _pageManager ??= _serviceProvider.GetRequiredService<IPageManager>();

    private ILocalizationService Localization => _localization ??= _serviceProvider.GetRequiredService<ILocalizationService>();

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowAdded;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowRemoved;

    /// <inheritdoc />
    public event EventHandler<ChildWindowModel>? OnChildWindowPageChanged;

    /// <inheritdoc />
    public void ShowMainWindow()
        => ShowWindow(GetOrCreateMainWindow());

    /// <inheritdoc />
    public ChildWindow CreateChildWindow(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var window = CreateChildWindowCore(model);
        OnChildWindowAdded?.Invoke(this, model);
        return window;
    }

    /// <inheritdoc />
    public DesktopLayerRecoveryResult RecoverDesktopLayer()
    {
        int embedded = 0, topLevel = 0, recreated = 0, skipped = 0, failed = 0;

        // Snapshot: rebuilding a panel overwrites its dictionary entry mid-loop.
        foreach (var (id, entry) in _childWindows.ToList())
        {
            try
            {
                var window = entry.Window;
                var outcome = window.RecoverAfterShellRestart();

                if (outcome == PanelShellRecoveryOutcome.Destroyed)
                {
                    window = RebuildDestroyedPanel(entry);
                    recreated++;

                    // The rebuilt window embedded itself on Show() (OnSourceInitialized); asking again
                    // reports where it actually ended up, and is a no-op when it already is embedded.
                    outcome = window.RecoverAfterShellRestart();
                }

                switch (outcome)
                {
                    case PanelShellRecoveryOutcome.Embedded:
                        embedded++;
                        break;
                    case PanelShellRecoveryOutcome.TopLevel:
                        topLevel++;
                        break;
                    case PanelShellRecoveryOutcome.NotCreated:
                        skipped++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;

                // Opaque id only — never the panel title (privacy invariant).
                Log.Error(
                    "WindowManager",
                    "ShellRecoveryPanelFailed",
                    ex,
                    new KeyValuePair<string, object?>("panelId", id));
            }
        }

        // release-hardening F (ADR-037, plan §3.2 triggers): re-embedding/rebuilding every surviving panel
        // above can change the desktop layer mode (e.g. Explorer restarted into a WorkerW-less shell) or
        // the root window's DPI — refresh the snapshot once, after all of them, rather than per-panel.
        _desktopScale.Refresh();

        return new DesktopLayerRecoveryResult(embedded, topLevel, recreated, skipped, failed);
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
    /// <remarks>
    /// Not practically unit-testable: constructing a real <see cref="ChildWindow"/> requires the full
    /// DI graph (see <see cref="CreateChildWindow"/>) and a live dispatcher, mirroring
    /// <see cref="OnMonitorsChanged"/>'s remarks. The filtering rules themselves
    /// (exclude self, exclude hidden, skip degenerate geometry) are a straight-line pass-through over
    /// <see cref="Window"/> properties with no branching worth isolating — see
    /// <c>docs/PLANS/panel-anti-overlap.md</c> §3.3/§6 item 4. Manual verify (Win10/Win11).
    /// </remarks>
    public void CollectOtherPanelBounds(Guid? excludingId, IList<Rect> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Clear();

        var scale = _desktopScale.Current;

        foreach (var (id, entry) in _childWindows)
        {
            if (excludingId is { } exclude && id == exclude) continue;

            var window = entry.Window;

            // Different page / otherwise not currently shown → blocks nothing the user can see.
            if (!window.IsVisible) continue;

            // Live geometry only — never the model's Position/Size (see the interface XML-doc for why
            // a rolled-up panel's model Size is unusable here). Height is the one exception: while a
            // roll-up/roll-down toggle is mid-animation (ChildWindow.ApplyRollUp, 150ms HeightProperty
            // animation), window.ActualHeight is a moving in-between value, not the panel's real
            // resting height — freezing whatever it happens to read as an obstacle for an entire
            // concurrent drag gesture on another panel would block/allow movement based on a height
            // that never actually persists (finalization MAJOR M-2). Model.IsRolledUp already flips to
            // its target value synchronously, before the animation starts, so it reflects the panel's
            // settled height even mid-animation; fall back to live geometry only when the panel isn't
            // rolled up.
            var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            var height = entry.Model.IsRolledUp
                ? ChildWindow.TitleBarHeight
                : window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            if (double.IsNaN(window.Left) || double.IsNaN(window.Top) || double.IsNaN(width) || double.IsNaN(height))
                continue;
            if (width <= 0 || height <= 0) continue; // not yet measured / degenerate

            // release-hardening F (ADR-037): obstacles must be desktop-DIP (the space every other caller
            // of this buffer — WorkspaceGridPlacement/PanelCollision/SnapGuideCalculator — already reasons
            // in), converted through the adapter rather than a bare Window.Left/Top read. Identity on any
            // uniform snapshot.
            buffer.Add(PanelWindowCoordinates.ToDesktopBounds(window, new Rect(window.Left, window.Top, width, height), scale));
        }
    }

    /// <inheritdoc />
    public void AlignPanelsToWorkspaceGrid(Guid? panelId)
    {
        if (_childWindows.Count == 0) return;

        var monitors = new List<(string DeviceName, Rect Bounds, Rect WorkArea)>();
        foreach (var monitor in _monitorService.GetMonitors())
            monitors.Add((monitor.DeviceName, monitor.Bounds, monitor.WorkArea));

        if (monitors.Count == 0) return;

        // M-2 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2.5): "Align all" (panelId is
        // null) is scoped to the CURRENT page only. Before this fix, targets spanned every page, but
        // CollectOtherPanelBounds below only ever sees VISIBLE (i.e. current-page) panels as obstacles —
        // a batch that included several INACTIVE pages let their panels collide straight into the same
        // slot, seeing none of each other, because none of them registered as an obstacle for the others.
        // A single-panel "Align to workspace grid" (from a panel's own context menu, panelId != null) is
        // unaffected — it is only ever invoked on an already-visible panel.
        var currentPage = PageManager.CurrentPage;

        // Deterministic order (top-to-bottom, then left-to-right): the panels are processed as a
        // sequence in which each already-aligned panel becomes an obstacle for the next one, so an
        // order that depended on dictionary enumeration would make "who got the contested slot"
        // effectively random between runs.
        var targets = _childWindows.Values
            .Where(entry => panelId is null ? entry.Model.PageId == currentPage : entry.Model.Id == panelId)
            .OrderBy(entry => entry.Model.Position.Y)
            .ThenBy(entry => entry.Model.Position.X)
            .ToList();

        var obstacles = new List<Rect>();
        var aligned = 0;

        foreach (var (window, model) in targets)
        {
            // Re-collected per panel (not hoisted): every panel aligned so far has just moved, and the
            // next one must see it where it actually is now.
            CollectOtherPanelBounds(model.Id, obstacles);

            if (!WorkspaceGridPlacement.TryAlign(model, window.GetExpandedBoundsWindowSpace(), monitors, obstacles, out var geometry))
                continue;

            window.ApplyWorkspaceGridGeometry(geometry.Bounds, geometry.Span, geometry.MonitorDeviceId);
            aligned++;
        }

        // One synchronous write for the whole batch rather than N (same "flush once after the loop"
        // shape as the bulk-removal path above).
        if (aligned > 0) SaveWindowsStateNow();
    }

    /// <inheritdoc />
    public void PlaceNewPanelOnFreeSlot(ChildWindowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var monitor = _monitorService.FindMonitorAt(new System.Windows.Point(model.Position.X, model.Position.Y))
                      ?? _monitorService.GetPrimaryMonitor();

        var size = new System.Windows.Size(model.Size.Width, model.Size.Height);
        var state = PanelGridStateResolver.Resolve(
            model,
            new Rect(new System.Windows.Point(model.Position.X, model.Position.Y), size),
            [(monitor.DeviceName, monitor.Bounds, monitor.WorkArea)],
            model.IsRolledUp);

        // LegacyFreeform keeps its existing behavior exactly (a new panel lands wherever the caller put
        // it, historically (0,0) for the "Add panel" action) — P03 only fixes the grid-aligned case.
        if (!state.IsGridAligned) return;

        var slot = WorkspaceGridPlacement.FindFreeSlot(state.WorkArea, size, state.UnitSize, state.Gap, CollectAllPanelBounds());
        if (slot is not { } position) return;

        // m-1 fix-cycle 1: Math.Round, not a truncating (int) cast — see WriteModelGeometry's remarks.
        model.Position = new Point((int)Math.Round(position.X), (int)Math.Round(position.Y));
        model.MonitorDeviceId = monitor.DeviceName;
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
    /// <item><c>PageManagerService.RemovePage</c> — the user explicitly deletes an entire page. It
    /// recycles the managed items of all the page's panels in one batch itself (ADR-035), then removes
    /// the panels with <c>releaseManagedItems: false</c> (their items are already in the Recycle Bin).</item>
    /// <item><c>LayoutSnapshotService.Restore</c> — removes every current panel only so it can
    /// immediately recreate the panel set from a snapshot's JSON, which references the very same
    /// managed paths. NOT a permanent deletion → <c>releaseManagedItems: false</c> (the default),
    /// otherwise restoring a snapshot would delete the managed files the recreated panels point to.
    /// </item>
    /// </list>
    /// This overload always persists synchronously (<c>deferSave: false</c>) — see
    /// <see cref="RemoveChildWindow(Guid, bool, bool)"/> for the bulk-removal-loop variant.
    /// </remarks>
    public PanelRemovalResult RemoveChildWindow(Guid windowId, bool releaseManagedItems)
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
    /// <para>
    /// ADR-035 (fail visibly): with <paramref name="releaseManagedItems"/> the managed items go to the
    /// Recycle Bin in one batch. If any of them is still on disk afterwards, the panel is <b>kept</b>:
    /// only the items that did reach the Recycle Bin are taken out of it (a debounced save follows,
    /// like any ordinary item removal), and <see cref="PanelRemovalResult.Blocked"/> is returned.
    /// </para>
    /// </remarks>
    public PanelRemovalResult RemoveChildWindow(Guid windowId, bool releaseManagedItems, bool deferSave)
    {
        if (!_childWindows.TryGetValue(windowId, out var entry)) return PanelRemovalResult.Removed;

        // Sync the model with any pending UI-side changes before inspecting FilePaths, then move any
        // app-managed copies to the Recycle Bin so they don't linger orphaned in PanelItems storage —
        // but only when this is a genuine permanent deletion (see remarks above).
        entry.Window.SyncToModel();
        var outcome = ReleaseManagedItemsIfRequested(entry.Model, _itemStorage, releaseManagedItems);

        if (!outcome.IsComplete)
        {
            RemoveItemsFromPanel(entry.Window, outcome.Recycled);
            SaveWindowsState();
            return PanelRemovalResult.Blocked(outcome.Remaining);
        }

        OnChildWindowRemoved?.Invoke(this, entry.Model);

        entry.Window.Close();
        _childWindows.Remove(windowId);

        // Structural change (panel permanently gone or being recreated by a snapshot restore) — write
        // synchronously so it can never be lost to a debounce window that never gets to elapse, unless
        // the caller is driving a bulk removal loop and will flush once at the end itself.
        if (deferSave)
            SaveWindowsState();
        else
            SaveWindowsStateNow();

        return PanelRemovalResult.Removed;
    }

    /// <inheritdoc />
    public void RemoveItemsFromPanels(IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) return;

        foreach (var entry in _childWindows.Values)
            RemoveItemsFromPanel(entry.Window, paths);

        SaveWindowsState();
    }

    /// <inheritdoc />
    public IEnumerable<ChildWindowModel> GetAllChildWindows()
        => _childWindows.Values.Select(entry => entry.Model);

    /// <inheritdoc />
    public void SaveWindowsState() => _applicationStateService.RequestSave();

    /// <inheritdoc />
    public void SaveWindowsStateNow() => _applicationStateService.SaveNow();

    /// <inheritdoc />
    public void SyncAllWindowsToModels() => SyncEachIsolated(_childWindows.Values, e => e.Window.SyncToModel());

    /// <inheritdoc />
    /// <remarks>
    /// Reads <see cref="IApplicationStateService.Panels"/> — the already-loaded, already-migrated
    /// panel list from the single <c>ApplicationStateService.Load()</c> call in <c>Program.Main</c>
    /// (ADR-024 D-11) — rather than parsing JSON off <c>Properties.Settings</c> itself. JSON validity,
    /// corruption recovery and quarantine are entirely the store's concern now (ADR-024 D-6/D-7); the
    /// per-item isolation below only ever guards against a well-formed panel failing to <em>construct</em>
    /// (e.g. <c>ResolveMonitorPlacement</c>/DI throwing), which is a WindowManager-level concern the
    /// store cannot see.
    /// </remarks>
    public void LoadWindowsState()
    {
        var states = _applicationStateService.Panels;
        if (states.Count == 0)
        {
            CreateFirstRunPanelIfApplicable();
            return;
        }

        // M1 fix (P08 fix cycle 1, 2026-09-22): resolve stale original->managed remaps across ALL
        // panels up front, before any of them is constructed — see ResolveStalePanelPaths' remarks for
        // why this must run here rather than inside each panel's own ChildWindowViewModel.LoadFromModel.
        ResolveStalePanelPaths(states, _itemStorage);

        // M-C fix (code review, panel clone to page, backlog 1.x wave 3 review): see _loadingStates' own
        // remarks and GetManagedPathsClaimedByOtherPanels'. Cleared in `finally` so a panel that fails to
        // construct (CreateEachIsolated's own per-item isolation) never leaves this set permanently.
        _loadingStates = states;
        try
        {
            var skipped = CreateEachIsolated(states, state => CreateChildWindow(state));
            if (skipped > 0)
            {
                Log.Warn(
                    "WindowManager",
                    "CorruptSavedPanelsSkipped",
                    new KeyValuePair<string, object?>("skipped", skipped),
                    new KeyValuePair<string, object?>("total", states.Count));

                // ADR-024 finalization C-1: a skipped panel means whatever gets persisted next reflects
                // fewer panels than the document ApplicationStateService.Load() actually returned. Without
                // this, the first Save() after this load (any user action — a single drag) would silently
                // overwrite the on-disk primary with the reduced set, and the next Save() after that would
                // overwrite .bak too, permanently losing the skipped panel(s) with nothing but a
                // Console.Error line as a trace. ReportPartialLoad preserves the still-complete on-disk
                // document in quarantine before that first overwrite can happen.
                _applicationStateService.ReportPartialLoad("PartialPanelLoad");
            }
        }
        finally
        {
            _loadingStates = null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not practically unit-testable end-to-end for the same reason as <see cref="OnMonitorsChanged"/>:
    /// <see cref="_childWindows"/> only gets populated via <see cref="CreateChildWindow"/>, which resolves
    /// a real WPF <see cref="ChildWindow"/> through DI. The per-panel math itself
    /// (<see cref="NormalizeLegacyFreeformToGridSize"/>/<see cref="NormalizeGridAlignedToGridSize"/>) is
    /// covered directly; this loop's own wiring is manual verify (Win10/Win11).
    /// </remarks>
    public void NormalizePanelsToGrid()
    {
        if (_childWindows.Count == 0) return;

        var gridSize = _appSettings.GridSize;
        if (gridSize <= 1) return;

        var anyChanged = false;

        foreach (var (window, model) in _childWindows.Values)
        {
            // L-6 fix (code review): shares ResolveMonitorForPosition's lookup instead of a third inline
            // copy of the same "stored device id, else the monitor under the current position, else
            // primary" resolution — see that method's own remarks.
            var monitor = ResolveMonitorForPosition(model);

            if (ResolveGridAlignedBounds(model, monitor) is { } bounds)
            {
                var normalized = NormalizeGridAlignedToGridSize(model, bounds, monitor, gridSize);
                if (normalized == bounds) continue;

                window.ApplyWorkspaceGridGeometry(normalized, span: null, monitorDeviceId: null);
                anyChanged = true;
                continue;
            }

            if (!NormalizeLegacyFreeformToGridSize(model, monitor, gridSize)) continue;

            anyChanged = true;

            // release-hardening F (ADR-037): through the panel's own geometry writer/adapter, not a bare
            // Window.Left/Top/Width/Height assignment — ApplyWorkspaceGridGeometry already applies
            // position-only while the panel is rolled up (see its remarks), matching this method's own
            // pre-F "skip Width/Height while rolled up" shape exactly.
            window.ApplyWorkspaceGridGeometry(
                new Rect(model.Position.X, model.Position.Y, model.Size.Width, model.Size.Height),
                span: null,
                monitorDeviceId: null);
        }

        if (anyChanged) SaveWindowsStateNow();
    }

    /// <inheritdoc />
    public void MovePanelToPage(ChildWindowModel panel, string pageId)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (string.IsNullOrWhiteSpace(pageId) || panel.PageId == pageId) return;

        // Fix-cycle finding 10: reject an unknown destination instead of silently assigning PageId to a
        // page IPageManager doesn't know about — the panel would still show in PanelsOnCurrentPage's
        // "Move to page" menu targeting real pages, yet become permanently invisible/unreachable (no page
        // switcher entry ever equals its new PageId again).
        if (!PageManager.Pages.Contains(pageId)) return;

        panel.PageId = pageId;
        panel.IsVisible = pageId == PageManager.CurrentPage;
        ApplyPanelVisibility(panel);
        SaveWindowsStateNow();

        // M-D fix (code review, panel clone to page, backlog 1.x wave 3 review): a panel moved via its
        // OWN header context menu (ChildWindowViewModel.MovePanelToPageCommand) never goes through
        // MainWindowViewModel at all, so that ViewModel's Panels/PanelsOnCurrentPage — built once from
        // GetAllChildWindows() and otherwise only ever refreshed by OnChildWindowAdded/Removed/PageChanged
        // — kept showing the panel under its stale page until some UNRELATED event happened to trigger a
        // refresh. This event lets it react to a page reassignment specifically, regardless of which UI
        // surface triggered it.
        OnChildWindowPageChanged?.Invoke(this, panel);
    }

    /// <inheritdoc />
    public PanelCloneResult ClonePanelToPage(ChildWindowModel source, string pageId)
    {
        ArgumentNullException.ThrowIfNull(source);

        var unknownPage = new PanelCloneResult(PanelCloneOutcome.UnknownPage, null, 0);
        if (string.IsNullOrWhiteSpace(pageId) || !PageManager.Pages.Contains(pageId)) return unknownPage;

        // Sync only the source panel (SyncEachIsolated for a single window, same discipline MovePanelToPage's
        // caller — RemovePanel — already applies before inspecting FilePaths) so the clone starts from the
        // FilePaths/geometry actually on screen, not a stale in-memory snapshot.
        if (_childWindows.TryGetValue(source.Id, out var sourceEntry))
            SyncEachIsolated([sourceEntry], e => e.Window.SyncToModel());

        // H-1 fix (code review, panel clone to page): the title is resolved BEFORE any managed item is
        // copied. It is pure in-memory work that cannot itself leave an orphaned copy behind if it throws,
        // so resolving it first keeps the try/catch below focused solely on the steps that follow a
        // successful copy — exactly the steps that DO need a rollback.
        var titlesOnTargetPage = _childWindows.Values
            .Where(entry => entry.Model.PageId == pageId)
            .Select(entry => entry.Model.Title)
            .ToList();
        var title = PanelCloneTitles.Resolve(source.Title, titlesOnTargetPage, Localization);

        var itemsPlan = BuildClonedFilePaths(source.FilePaths, _itemStorage);
        if (!itemsPlan.Success) return new PanelCloneResult(itemsPlan.FailureOutcome, null, 0);

        Guid? clonedWindowId = null;
        ChildWindowModel? clone = null;
        var addedRaised = false;

        // NEW-3 fix (final review, panel clone to page, backlog 1.x): flips once SaveWindowsStateNow()
        // below has actually run — tells the catch block whether the on-disk state still needs undoing.
        // Without this, a subscriber to OnChildWindowAdded that throws (after that save already
        // succeeded) left the clone AND its managed-item copies committed to disk, even though the catch
        // below fully unwinds the clone from memory (_childWindows, DiscardCopies, Window.Close()) —
        // "removed everywhere except the one place that survives a restart" is worse than never having
        // cleaned up in memory at all.
        var persisted = false;
        try
        {
            clone = PanelCloneBuilder.Build(source, pageId, title, itemsPlan.FilePaths);
            clone.IsVisible = pageId == PageManager.CurrentPage;
            clonedWindowId = clone.Id;

            // Only a SAME-page clone needs its own spot — a different-page clone keeps the source's exact
            // position/monitor verbatim (ADR-045 §8.3), same as every OTHER copied geometry field.
            if (pageId == source.PageId)
                PanelCloneGeometry.Offset(clone, _appSettings.GridSize, ResolveMonitorForPosition(clone));

            // M-B fix (code review, panel clone to page, backlog 1.x wave 3 review): registers and shows
            // the window via CreateChildWindowCore directly — NOT the public CreateChildWindow, which
            // would raise OnChildWindowAdded immediately. See the remarks on addedRaised below for why
            // that event must wait.
            CreateChildWindowCore(clone);

            // L-4 fix (code review): SkippedMissingItems is deliberately never surfaced to the user (see
            // MainWindowViewModel.CopyPanelToPage's own remarks) — this is the one place it is actually
            // recorded, opaque panel id only (privacy invariant), never a title.
            Log.Info(
                "WindowManager",
                "PanelCloned",
                new KeyValuePair<string, object?>("panelId", source.Id),
                new KeyValuePair<string, object?>("skippedMissingItems", itemsPlan.SkippedMissingItems));

            // Structural change (a brand-new panel), like every other panel create/delete/move call site —
            // persisted synchronously so it can never be lost to a debounce window that never gets to elapse.
            SaveWindowsStateNow();
            persisted = true;

            // M-B fix (code review, panel clone to page, backlog 1.x wave 3 review): OnChildWindowAdded is
            // raised as the LAST step inside this try — strictly after the window is registered, logged
            // AND persisted — so a throw from any of those earlier steps can never leave
            // MainWindowViewModel.Panels (or any other subscriber) holding a "ghost" entry for a clone this
            // method is about to unwind in the catch below. addedRaised only flips once the event has
            // actually started firing, which tells the catch below whether a matching
            // OnChildWindowRemoved is owed.
            addedRaised = true;
            OnChildWindowAdded?.Invoke(this, clone);

            return new PanelCloneResult(PanelCloneOutcome.Cloned, clone, itemsPlan.SkippedMissingItems);
        }
        catch (Exception ex)
        {
            // M-A fix (code review, panel clone to page, backlog 1.x wave 3 review): the managed-item
            // copies made by BuildClonedFilePaths above are discarded FIRST and unconditionally — never
            // gated on whatever happens to the failed window below, which touches WPF/native resources and
            // can itself throw. Under the previous ordering, an exception from Window.Close() skipped
            // DiscardCopies entirely and left the fresh managed copies orphaned on disk.
            _itemStorage.DiscardCopies(itemsPlan.CreatedCopies);

            // H-1 fix (code review, panel clone to page): if CreateChildWindowCore got far enough to
            // register the panel before failing (e.g. Show() itself threw), it must be unregistered
            // DIRECTLY — never through RemoveChildWindow(releaseManagedItems: true), which would move the
            // very copies just discarded above to the Recycle Bin instead, and would also raise
            // OnChildWindowRemoved unconditionally, even for a panel OnChildWindowAdded was never raised
            // for.
            if (clonedWindowId is { } id && _childWindows.TryGetValue(id, out var failedEntry))
            {
                _childWindows.Remove(id);

                // M-A fix: isolated in its own try/catch and logged — a native Close() failure must never
                // prevent this catch from finishing its own cleanup (the DiscardCopies call above already
                // ran regardless).
                try
                {
                    failedEntry.Window.Close();
                }
                catch (Exception closeEx)
                {
                    Log.Error(
                        "WindowManager",
                        "PanelCloneCloseFailed",
                        closeEx,
                        new KeyValuePair<string, object?>("panelId", source.Id));
                }
            }

            // NEW-3 fix (final review, panel clone to page, backlog 1.x): the earlier SaveWindowsStateNow()
            // above already committed the clone (with its now-DISCARDED managed-item copies) to disk — a
            // second synchronous save re-persists the fully-unwound in-memory state (clone removed from
            // _childWindows, its window closed) so the on-disk file matches what every OTHER subscriber
            // now sees, instead of resurrecting the clone (pointing at copies that no longer exist) on the
            // next load. Isolated in its own try/catch and logged, same discipline as the Window.Close()
            // cleanup above — a save failure here must never prevent this catch from finishing, and this
            // clone-create failure is reported to the caller regardless either way.
            if (persisted)
            {
                try
                {
                    SaveWindowsStateNow();
                }
                catch (Exception saveEx)
                {
                    Log.Error(
                        "WindowManager",
                        "PanelCloneRollbackSaveFailed",
                        saveEx,
                        new KeyValuePair<string, object?>("panelId", source.Id));
                }
            }

            // M-B fix: only raised when OnChildWindowAdded actually started firing above — never for a
            // clone that failed before ever being announced to any subscriber. M-5 fix (review 6.1): raised
            // AFTER the rollback save above and isolated in its own try/catch — a throwing subscriber here
            // (we are already inside a catch) would otherwise escape this method and skip both the on-disk
            // rollback and the failure log/result below.
            if (addedRaised && clone is not null)
            {
                try
                {
                    OnChildWindowRemoved?.Invoke(this, clone);
                }
                catch (Exception removedEx)
                {
                    Log.Error(
                        "WindowManager",
                        "PanelCloneRemovedHandlerFailed",
                        removedEx,
                        new KeyValuePair<string, object?>("panelId", source.Id));
                }
            }

            // Opaque source panel id only — never a panel title (privacy invariant).
            Log.Error(
                "WindowManager",
                "PanelCloneCreateFailed",
                ex,
                new KeyValuePair<string, object?>("panelId", source.Id));

            return new PanelCloneResult(PanelCloneOutcome.CopyFailed, null, 0);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetManagedPathsClaimedByOtherPanels(Guid excludingPanelId)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entry) in _childWindows)
        {
            if (id == excludingPanelId) continue;
            CollectManagedPaths(entry.Model, claimed);
        }

        // M-C fix (code review, panel clone to page, backlog 1.x wave 3 review): during LoadWindowsState's
        // own CreateEachIsolated loop, _childWindows above only contains the panels constructed SO FAR — a
        // panel later in the same batch (not yet registered) would otherwise look "unclaimed" to an
        // EARLIER panel's own ChildWindowViewModel.ResolveManagedReplacements self-heal, even though the
        // later panel's own FilePaths already reference that exact same managed path once IT, too, finishes
        // loading — letting two panels in the same startup batch heal onto the same managed item.
        // _loadingStates (set only for the duration of LoadWindowsState) closes that gap; it is
        // <see langword="null"/> at every other call time, where _childWindows alone is already complete.
        if (_loadingStates is not null)
        {
            foreach (var state in _loadingStates)
            {
                if (state.Id == excludingPanelId) continue;
                if (_childWindows.ContainsKey(state.Id)) continue; // already covered above
                CollectManagedPaths(state, claimed);
            }
        }

        return claimed;
    }

    /// <summary>
    /// The "duplicate every item" half of <see cref="ClonePanelToPage"/> (ADR-045): copies every MANAGED
    /// path in <paramref name="sourceFilePaths"/> into its own brand-new per-item storage directory
    /// (<see cref="IPanelItemStorageService.CopyManaged"/>); a non-managed path (an external file, or a
    /// shortcut the user left where it was) is carried over unchanged — it belongs to the user, not this
    /// app, so there is nothing to duplicate; a managed path whose file is already gone from disk is left
    /// out of the clone entirely (<see cref="PanelCloneItemsPlan.SkippedMissingItems"/>) rather than
    /// copying a reference to nothing that two panels would then both "own". Any other copy failure rolls
    /// back every managed copy already made in THIS call (<see cref="IPanelItemStorageService.DiscardCopies"/>)
    /// and reports the failing outcome — the clone is either complete, or none of it happened.
    /// </summary>
    /// <remarks>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can unit
    /// test this pure model+storage logic directly, without needing a real WPF <see cref="ChildWindow"/> —
    /// mirrors <see cref="ReleaseManagedItems"/>.
    /// </remarks>
    /// <param name="sourceFilePaths">The source panel's <see cref="ChildWindowModel.FilePaths"/> (already synced to the live panel).</param>
    /// <param name="storage">Duplicates app-managed item copies.</param>
    /// <returns>The plan: either the clone's own file path list, or which outcome to fail with.</returns>
    internal static PanelCloneItemsPlan BuildClonedFilePaths(IReadOnlyList<string> sourceFilePaths, IPanelItemStorageService storage)
    {
        var clonedPaths = new List<string>(sourceFilePaths.Count);
        var createdCopies = new List<string>();
        var skippedMissingItems = 0;

        foreach (var path in sourceFilePaths)
        {
            if (!storage.IsManaged(path))
            {
                clonedPaths.Add(path);
                continue;
            }

            var copy = storage.CopyManaged(path);
            if (copy.Outcome == ManagedCopyOutcome.Copied && copy.CopyPath is not null)
            {
                clonedPaths.Add(copy.CopyPath);
                createdCopies.Add(copy.CopyPath);
                continue;
            }

            if (copy.Outcome == ManagedCopyOutcome.SourceMissing)
            {
                skippedMissingItems++;
                continue;
            }

            storage.DiscardCopies(createdCopies);
            var outcome = copy.Outcome switch
            {
                ManagedCopyOutcome.TooLarge => PanelCloneOutcome.TooLarge,
                ManagedCopyOutcome.InsufficientSpace => PanelCloneOutcome.InsufficientSpace,
                _ => PanelCloneOutcome.CopyFailed,
            };
            return PanelCloneItemsPlan.Failed(outcome);
        }

        return PanelCloneItemsPlan.Ok(clonedPaths, skippedMissingItems, createdCopies);
    }

    /// <summary>
    /// Moves every app-managed item copy referenced by <paramref name="model"/>'s
    /// <see cref="ChildWindowModel.FilePaths"/> to the Recycle Bin in a single batch (ADR-035). Folder
    /// Portal panels have no persisted <see cref="ChildWindowModel.FilePaths"/> pointing into managed
    /// storage, so <see cref="IPanelItemStorageService.IsManaged"/> naturally makes this a no-op for them
    /// (the storage service is not even called when there's nothing managed).
    /// </summary>
    /// <remarks>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can
    /// unit test this pure model+storage logic directly, without needing a real WPF <c>ChildWindow</c>
    /// (which <see cref="RemoveChildWindow(Guid, bool)"/> otherwise requires via DI).
    /// </remarks>
    /// <param name="model">The panel model whose managed <see cref="ChildWindowModel.FilePaths"/> are recycled.</param>
    /// <param name="storage">Recycles the app-managed item copies.</param>
    /// <returns>What was recycled and what is still on disk; an empty outcome when nothing was managed.</returns>
    internal static RecycleOutcome ReleaseManagedItems(ChildWindowModel model, IPanelItemStorageService storage)
    {
        var managed = model.FilePaths.Where(storage.IsManaged).ToList();
        return managed.Count == 0 ? default : storage.ReleaseToRecycleBin(managed);
    }

    /// <summary>
    /// Gates <see cref="ReleaseManagedItems"/> behind the <c>releaseManagedItems</c> decision made by
    /// <see cref="RemoveChildWindow(Guid, bool)"/>'s callers. Extracted as its own internal member —
    /// rather than an inline <see langword="if"/> in <see cref="RemoveChildWindow(Guid, bool)"/> —
    /// solely so <c>DesktopOrganizer.Tests</c> can directly assert that the flag controls the release
    /// call, without needing a real WPF <c>ChildWindow</c>.
    /// </summary>
    /// <param name="model">The panel model whose managed items may be recycled.</param>
    /// <param name="storage">Recycles the app-managed item copies.</param>
    /// <param name="releaseManagedItems">Whether the caller actually wants the release to happen.</param>
    /// <returns>The recycle outcome; an empty (complete) outcome when <paramref name="releaseManagedItems"/> is <see langword="false"/>.</returns>
    internal static RecycleOutcome ReleaseManagedItemsIfRequested(ChildWindowModel model, IPanelItemStorageService storage, bool releaseManagedItems)
        => releaseManagedItems ? ReleaseManagedItems(model, storage) : default;

    /// <summary>
    /// The decision <see cref="CreateFirstRunPanelIfApplicable"/> gates itself on: exactly one panel
    /// should be seeded when, and only when, there are no panels to restore AND the document load that
    /// produced that empty list is <see cref="StateLoadStatus.FirstRun"/> — no JSON store existed yet and
    /// legacy <c>user.config</c> was empty/default (see that member's remarks). Every other status that
    /// can also report zero panels — <see cref="StateLoadStatus.Loaded"/> (the user deliberately deleted
    /// every panel from an existing document), <see cref="StateLoadStatus.Quarantined"/>/
    /// <see cref="StateLoadStatus.RecoveredFromBackup"/> (a corrupt/recovered document, not "nothing
    /// configured yet"), <see cref="StateLoadStatus.ImportedFromLegacy"/> (a legacy install, handled by
    /// its own import path), and <see cref="StateLoadStatus.NewerSchema"/> (a read-only session against a
    /// newer on-disk schema this build must never write to) — must all return <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Extracted as its own internal static member so this decision is directly unit-testable without a
    /// real WPF <c>ChildWindow</c>/DI graph — mirrors <see cref="CreateEachIsolated"/>/<see cref="SyncEachIsolated"/>.
    /// </remarks>
    /// <param name="existingPanelCount">How many panels the just-completed load actually returned.</param>
    /// <param name="loadStatus">Which rung of the recovery ladder produced that load.</param>
    internal static bool ShouldCreateFirstRunPanel(int existingPanelCount, StateLoadStatus loadStatus)
        => existingPanelCount == 0 && loadStatus == StateLoadStatus.FirstRun;

    /// <summary>
    /// Cross-panel-aware counterpart to <c>ChildWindowViewModel.LoadFromModel</c>'s own per-panel
    /// stale-path self-heal (ADR-009 Amendment). Resolves a panel's stale
    /// <see cref="ChildWindowModel.FilePaths"/> entry to its managed copy only when exactly one LIVE
    /// manifest candidate exists for it AND that candidate is not already directly referenced by ANY
    /// panel's current file list — this panel's own, or a different one's (M1 fix, P08 fix cycle 1,
    /// 2026-09-22).
    /// </summary>
    /// <remarks>
    /// Must run here, across every panel at once, BEFORE any panel is constructed: determining "is
    /// this candidate already owned by another panel" requires seeing every panel's
    /// <see cref="ChildWindowModel.FilePaths"/> up front. A single panel's own
    /// <c>ChildWindowViewModel.LoadFromModel</c> — called one panel at a time from
    /// <see cref="CreateEachIsolated"/> — has no visibility into sibling panels not yet loaded, so it
    /// can only ever guard against an ambiguous <i>manifest</i> (delegated to
    /// <see cref="IPanelItemStorageService.TryFindManagedPath"/>, which itself now refuses to pick an
    /// arbitrary candidate — see that method's remarks), never a candidate that's unambiguous by
    /// manifest but already claimed by a sibling panel's own directly-referenced path.
    /// <para>
    /// Internal (rather than <see langword="private"/>) solely so <c>DesktopOrganizer.Tests</c> can
    /// unit test this pure model+storage cross-referencing logic directly, mirroring
    /// <see cref="CreateEachIsolated"/>/<see cref="SyncEachIsolated"/>.
    /// </para>
    /// <para>
    /// Folder Portal panels are skipped entirely: their <see cref="ChildWindowModel.FilePaths"/> is
    /// never persisted/consulted (a portal always mirrors a live folder listing instead — see
    /// <see cref="ChildWindowModel.SourceFolderPath"/>), so remapping it here would be both pointless
    /// and immediately overwritten.
    /// </para>
    /// </remarks>
    /// <param name="states">Every saved panel state about to be (re)constructed.</param>
    /// <param name="storage">Provides read-only manifest lookups — see <see cref="IPanelItemStorageService.FindManagedCandidates"/>.</param>
    /// <remarks>
    /// MINOR-2 fix (P08 fix cycle 2, 2026-09-22, ADR-009 Amendment 2 tie-break rule): when two or more
    /// DISTINCT stale entries (whether in the same panel or different panels) each resolve to the SAME
    /// single unclaimed candidate, none of them may claim it. The original single-pass implementation
    /// awarded it to whichever entry happened to be processed first — an unexplained, iteration-order-
    /// dependent "coin flip" that contradicts this codebase's explicit-refusal-over-guessing principle,
    /// already applied to <see cref="RestoreOutcome.OriginalPathUnknown"/> and to
    /// <see cref="IPanelItemStorageService.TryFindManagedPath"/>'s own ambiguous-manifest refusal. This
    /// method now runs two passes: the first only DISCOVERS each stale entry's single unclaimed
    /// candidate (without mutating anything) and tallies how many distinct entries resolved to each
    /// candidate; the second applies the remap only for candidates with a tally of exactly one. Every
    /// contested entry is simply left stale — same as an ambiguous manifest or an already-claimed
    /// candidate — which degrades to a visible gray placeholder or generic-by-extension icon
    /// (<c>IconProvider</c>'s missing-path fallback, UX P1), never a silently wrong one.
    /// </remarks>
    internal static void ResolveStalePanelPaths(IEnumerable<ChildWindowModel> states, IPanelItemStorageService storage)
    {
        var panelList = states as IReadOnlyList<ChildWindowModel> ?? states.ToList();

        // Every managed path already directly present (as-is, no remap needed) anywhere across every
        // panel is "claimed" — it must never also be handed to a *different* stale entry, whether in
        // the very same panel (dedup: a stale duplicate alongside the real managed path) or a
        // different one (ownership: a sibling panel already references it).
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in panelList)
        {
            if (!string.IsNullOrWhiteSpace(panel.SourceFolderPath)) continue;

            foreach (var path in panel.FilePaths)
            {
                if (!string.IsNullOrWhiteSpace(path) && storage.IsManaged(path))
                    claimed.Add(path);
            }
        }

        // First pass: discover, per stale entry, its single unclaimed candidate (if unambiguous) — no
        // mutation yet — and tally how many DISTINCT stale entries resolved to that same candidate.
        var pendingRemaps = new List<(ChildWindowModel Panel, int Index, string Candidate)>();
        var candidateContestCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var panel in panelList)
        {
            if (!string.IsNullOrWhiteSpace(panel.SourceFolderPath)) continue;

            for (var i = 0; i < panel.FilePaths.Count; i++)
            {
                var path = panel.FilePaths[i];
                if (string.IsNullOrWhiteSpace(path)) continue;

                // B-6 (ADR-046 7c): manifest lookup FIRST — FindManagedCandidates is a purely in-memory
                // manifest scan (its own File.Exists/Directory.Exists calls only ever touch LOCAL managed
                // candidates under %LOCALAPPDATA%, never `path` itself) — and only THEN check whether the
                // original `path` still exists. The remap condition itself is unchanged (original missing
                // AND exactly one unclaimed candidate — an AND of two checks, whose result cannot depend on
                // evaluation order), but at startup, with every saved panel's paths checked before any
                // window is even created, a `Z:\`/UNC entry with no managed candidate at all no longer pays
                // for a network round-trip up front for every such panel.
                var candidates = storage.FindManagedCandidates(path)
                    .Where(candidate => !claimed.Contains(candidate))
                    .ToList();

                if (candidates.Count != 1) continue; // none, ambiguous, or every candidate already claimed
                if (File.Exists(path) || Directory.Exists(path)) continue; // original still valid — nothing to remap

                var candidate = candidates[0];
                pendingRemaps.Add((panel, i, candidate));
                candidateContestCount[candidate] = candidateContestCount.GetValueOrDefault(candidate) + 1;
            }
        }

        // Second pass: apply only the remaps whose candidate wasn't also independently resolved by a
        // different stale entry above.
        foreach (var (panel, index, candidate) in pendingRemaps)
        {
            if (candidateContestCount[candidate] != 1) continue; // contested — every claimant stays stale

            panel.FilePaths[index] = candidate;
            claimed.Add(candidate);
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

                // state.Title is deliberately never logged (privacy invariant) — only the panel's
                // opaque id.
                AppLog.Current.Error(
                    "WindowManager",
                    "CorruptSavedPanelSkipped",
                    ex,
                    new KeyValuePair<string, object?>("panelId", state.Id));
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
                // entry.Model.Title is deliberately never logged (privacy invariant) — only the
                // panel's opaque id.
                AppLog.Current.Error(
                    "WindowManager",
                    "PanelSyncFailed",
                    ex,
                    new KeyValuePair<string, object?>("panelId", entry.Model.Id));
            }
        }
    }

    /// <summary>
    /// The <see cref="PanelGeometryMode.GridAligned"/> half of <see cref="ResolveMonitorPlacement"/> and
    /// <see cref="OnMonitorsChanged"/> (P03-W1/W3): re-derives the panel's bounds from its spans against
    /// <paramref name="monitor"/>'s CURRENT work area, or returns <see langword="null"/> when this panel
    /// is not (or cannot currently be treated as) grid-aligned and the caller must fall back to the
    /// unchanged <see cref="ClampIntoMonitor"/> path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately routed through <see cref="PanelGridStateResolver.Resolve"/> with a ONE-ELEMENT monitor
    /// list: that resolver is the single place <see cref="ChildWindowModel.GeometryMode"/> may be read
    /// (Инвариант В1) and the single place a <see cref="PanelGeometryMode.GridAligned"/> panel with
    /// missing spans lazily acquires them. Passing only <paramref name="monitor"/> keeps this method's own
    /// "which monitor" decision authoritative — the resolver's monitor-resolution order (device id →
    /// center → first) cannot second-guess a caller that has already applied the "stored monitor gone →
    /// primary" rule (M4, unchanged by ADR-028).
    /// </para>
    /// <para>
    /// The result is idempotent by construction (<c>SpansToBounds</c> is a pure function of the span, and
    /// <c>SnapPosition</c> + the grid-aware <c>SafeClamp</c> are both idempotent), which matters because
    /// <c>WM_SETTINGCHANGE</c> raises <c>MonitorsChanged</c> for something as ordinary as a theme change:
    /// re-running this on an unchanged configuration must move nothing (R-9).
    /// </para>
    /// </remarks>
    /// <param name="model">The panel to place.</param>
    /// <param name="monitor">The monitor the caller has already decided this panel belongs to.</param>
    /// <returns>The panel's bounds in <c>Window.Left</c>/<c>Top</c> space, or <see langword="null"/> for the legacy path.</returns>
    internal static Rect? ResolveGridAlignedBounds(ChildWindowModel model, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(monitor);

        var persisted = new Rect(
            model.Position.X,
            model.Position.Y,
            Math.Max(0, model.Size.Width),
            Math.Max(0, model.Size.Height));

        var state = PanelGridStateResolver.Resolve(
            model,
            persisted,
            [(monitor.DeviceName, monitor.Bounds, monitor.WorkArea)],
            model.IsRolledUp);

        if (!state.IsGridAligned) return null;
        if (model.WidthSpanUnits is not { } widthUnits || model.HeightSpanUnits is not { } heightUnits) return null;

        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(widthUnits, heightUnits), state.UnitSize, state.Gap);
        var position = WorkspaceLayoutPolicy.SnapPosition(persisted.TopLeft, state.Origin, state.UnitSize);

        return WorkspaceLayoutPolicy.SafeClamp(new Rect(position, size), state.WorkArea, state.UnitSize, state.Gap);
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
    /// <param name="model">The panel model whose <see cref="ChildWindowModel.Position"/> is clamped in place.</param>
    /// <param name="monitor">The monitor whose work area the panel must stay within.</param>
    internal static void ClampIntoMonitor(ChildWindowModel model, MonitorInfo monitor)
    {
        var area = monitor.WorkArea;

        // H1: a monitor the desktop-scale snapshot doesn't know about surfaces as Rect.Empty (X/Y =
        // +Infinity, ADR-037 MonitorService.GetMonitors). Clamping against that would round to
        // int.MinValue below and PERSIST it into model.Position — leave the panel untouched instead.
        if (area.IsEmpty || !IsFiniteRect(area)) return;

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
        model.Position = new Point((int)Math.Round(x), (int)Math.Round(y)); // m-1 fix-cycle 1: see WriteModelGeometry's remarks.
    }

    /// <summary>
    /// ADR-011 Amendment (2026-09-24): re-aligns a <see cref="PanelGeometryMode.LegacyFreeform"/> panel's
    /// persisted <c>Position</c>/<c>Size</c> onto the CURRENT <paramref name="gridSize"/> lattice (global
    /// origin <c>(0,0)</c>, same as <see cref="GridSnap.SnapAxis"/>) when they have drifted off it — the
    /// user-reported scenario this closes: a panel dropped at <c>(64,64)</c>/<c>64x64</c> while
    /// <c>GridSize=32</c> stays a multiple of 32 forever unless something re-quantises it; once
    /// <c>GridSize</c> changes to <c>48</c>, dragging that panel (which snaps in 48-DIP steps, see
    /// <c>Behaviors.PanelDragMoveBehavior</c>) never lands it back on a clean 48-multiple, because a
    /// LegacyFreeform drag only magnetically snaps to the absolute lattice within
    /// <c>GridSnapDistance</c> (see <see cref="GridSnap.SnapAxis"/>); a persisted off-lattice
    /// position/size is never re-quantised unless something normalises it. Called once at restore
    /// (<see cref="ResolveMonitorPlacement"/>) and once whenever the grid changes underneath an already
    /// open panel (<see cref="OnMonitorsChanged"/>'s LegacyFreeform branch, <see cref="NormalizePanelsToGrid"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="ChildWindowModel.Size"/> always holds the panel's EXPANDED extent regardless of roll-up
    /// state (<c>ChildWindow.OnRenderSizeChanged</c> skips its write while
    /// <see cref="ChildWindowModel.IsRolledUp"/>) — so mutating it here is always safe, whether or not the
    /// panel is currently rolled up. A caller that also drives a LIVE <c>ChildWindow</c> must still gate
    /// its own <c>Window.Width</c>/<c>Height</c> write on <c>!model.IsRolledUp</c> (see
    /// <see cref="OnMonitorsChanged"/>) — this method itself never touches a <c>Window</c>.
    /// </remarks>
    /// <param name="model">The panel to normalize.</param>
    /// <param name="monitor">The monitor the panel is (or is about to be) placed on — supplies the work-area clamp.</param>
    /// <param name="gridSize">The current <see cref="IAppSettings.GridSize"/>. Values <c>&lt;= 1</c> are a no-op.</param>
    /// <returns><see langword="true"/> if <see cref="ChildWindowModel.Position"/> or <see cref="ChildWindowModel.Size"/> changed.</returns>
    internal static bool NormalizeLegacyFreeformToGridSize(ChildWindowModel model, MonitorInfo monitor, int gridSize)
    {
        if (gridSize <= 1) return false;

        var minSize = LegacyFreeformMinSize(model);
        var current = new Rect(
            model.Position.X,
            model.Position.Y,
            Math.Max(0, model.Size.Width),
            Math.Max(0, model.Size.Height));

        var normalized = GridSizeNormalizer.Normalize(current, gridSize, minSize, monitor.WorkArea);

        var newPosition = new Point((int)Math.Round(normalized.X), (int)Math.Round(normalized.Y));
        var newSize = new System.Drawing.Size((int)Math.Round(normalized.Width), (int)Math.Round(normalized.Height));

        if (newPosition == model.Position && newSize == model.Size) return false;

        model.Position = newPosition;
        model.Size = newSize;
        return true;
    }

    /// <summary>
    /// ADR-011 Amendment (2026-09-24): when the legacy <paramref name="gridSize"/> lattice happens to be
    /// commensurate with the workspace grid's own unit (<paramref name="gridSize"/> is a whole multiple of
    /// <see cref="WorkspaceGridMetrics.UnitSize"/>, e.g. <c>48</c>/<c>32</c> against <c>U=8</c>), also
    /// re-aligns an ALREADY workspace-grid-resolved <see cref="PanelGeometryMode.GridAligned"/> panel's
    /// <paramref name="bounds"/> onto that coarser lattice — every <paramref name="gridSize"/> multiple is
    /// then also a <c>U</c> multiple, so the panel never leaves the workspace grid it is locked to. A no-op
    /// whenever <paramref name="gridSize"/> is not a whole multiple of <c>U</c> (or <c>&lt;= 1</c>): forcing
    /// the panel onto an incommensurate lattice would pull it OFF the workspace grid it belongs to (ADR-028
    /// "две сетки намеренно несоизмеримы" — the two grids are deliberately incommensurable in general).
    /// </summary>
    /// <param name="model">The panel — <see cref="ChildWindowModel.WidthSpanUnits"/>/<see cref="ChildWindowModel.HeightSpanUnits"/> are updated in place when the size changes.</param>
    /// <param name="bounds">The panel's already workspace-grid-resolved bounds (<see cref="ResolveGridAlignedBounds"/>'s result), in <c>Window.Left</c>/<c>Top</c> space.</param>
    /// <param name="monitor">The panel's monitor — supplies the work-area clamp, translated into the same coordinate space as <paramref name="bounds"/>.</param>
    /// <param name="gridSize">The current <see cref="IAppSettings.GridSize"/>.</param>
    /// <returns><paramref name="bounds"/> unchanged, or the re-aligned bounds.</returns>
    internal static Rect NormalizeGridAlignedToGridSize(ChildWindowModel model, Rect bounds, MonitorInfo monitor, int gridSize)
    {
        if (gridSize <= 1) return bounds;
        if (gridSize % (int)WorkspaceGridMetrics.UnitSize != 0) return bounds;

        var minSpan = model.IsRolledUp ? WorkspaceGridMetrics.MinSpanCollapsed : WorkspaceGridMetrics.MinSpanExpanded;
        var minSize = WorkspaceLayoutPolicy.SpansToBounds(minSpan, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);
        var workArea = DesktopCoordinateSpace.ToWindowSpace(monitor.WorkArea);

        var normalized = GridSizeNormalizer.Normalize(bounds, gridSize, minSize, workArea);
        if (normalized == bounds) return bounds;

        var span = WorkspaceLayoutPolicy.RoundBoundsToSpans(normalized.Size, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);
        model.WidthSpanUnits = span.WidthUnits;
        model.HeightSpanUnits = span.HeightUnits;

        return normalized;
    }

    /// <summary>
    /// M-3 fix-cycle 1: whether re-applying <paramref name="bounds"/> onto a panel currently at
    /// <paramref name="currentWindowSpace"/> would be a genuine visual change, within the same 0.5 DIP
    /// rounding budget <see cref="WorkspaceLayoutPolicy"/>'s own idempotency guarantees are built against
    /// (e.g. its <c>SpanQuantizationToleranceDip</c>). Extracted as its own internal static member so
    /// <see cref="OnMonitorsChanged"/>'s "did anything actually change" decision is unit-testable without
    /// a live WPF <see cref="Window"/> — see that method's own "not practically unit-testable end-to-end"
    /// remarks for why the surrounding loop itself stays manual verify (Win10/Win11).
    /// </summary>
    /// <param name="currentWindowSpace">The panel's current <c>Window.Left/Top/Width/Height</c>, in window space.</param>
    /// <param name="bounds">The bounds <see cref="ResolveGridAlignedBounds"/> just computed.</param>
    /// <param name="isRolledUp">
    /// Whether the panel is currently rolled up. A rolled-up panel's live <c>Window.Height</c> is the
    /// collapsed header while <paramref name="bounds"/> carries the EXPANDED span height, and
    /// <c>ApplyWorkspaceGridGeometry</c> only re-applies the POSITION in that state — so only X/Y are
    /// compared (P03 fix-cycle 1 final verification, MAJOR-1: comparing heights made the gate never fire
    /// for rolled-up panels, re-saving on every theme/locale <c>WM_SETTINGCHANGE</c>).
    /// </param>
    /// <param name="tolerance">The DIP tolerance, per component. Defaults to <c>0.5</c>.</param>
    internal static bool GridAlignedBoundsUnchanged(Rect currentWindowSpace, Rect bounds, bool isRolledUp = false, double tolerance = 0.5)
    {
        var positionUnchanged = Math.Abs(currentWindowSpace.X - bounds.X) <= tolerance
                                && Math.Abs(currentWindowSpace.Y - bounds.Y) <= tolerance;
        if (isRolledUp) return positionUnchanged;

        return positionUnchanged
               && Math.Abs(currentWindowSpace.Width - bounds.Width) <= tolerance
               && Math.Abs(currentWindowSpace.Height - bounds.Height) <= tolerance;
    }

    /// <summary>
    /// True when every edge of <paramref name="rect"/> is a finite number — guards <see cref="ClampIntoMonitor"/>
    /// against a work area whose bounds are <see cref="double.PositiveInfinity"/>/<see cref="double.NegativeInfinity"/>
    /// (as produced by <c>Rect.Empty</c>) without relying on <c>Rect.IsEmpty</c> alone.
    /// </summary>
    private static bool IsFiniteRect(Rect rect)
        => double.IsFinite(rect.Left) && double.IsFinite(rect.Top) && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    /// <summary>
    /// The same width/height floor <c>ResizeConstraintsFactory.BuildConstraints</c> computes for a
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> panel's live resize gesture: <c>MinWidth</c> is
    /// icon-grid-derived only in <see cref="PanelViewMode.Grid"/>, <c>MinHeight</c> is always the flat
    /// <see cref="GridMetrics.MinPanelSize"/> floor in both view modes. Kept in agreement with that
    /// factory on purpose — a normalized panel's floor must match the floor its own resize gesture would
    /// enforce, or the very next manual resize could immediately violate what this method just normalized to.
    /// </summary>
    private static Size LegacyFreeformMinSize(ChildWindowModel model)
    {
        var minWidth = model.ViewMode == PanelViewMode.Grid
            ? GridMetrics.MinPanelWidth(model.IconSize, model.IconLayoutPolicy, SystemParameters.VerticalScrollBarWidth)
            : GridMetrics.MinPanelSize;

        return new Size(minWidth, GridMetrics.MinPanelSize);
    }

    /// <summary>
    /// Mirrors <paramref name="bounds"/> onto <paramref name="model"/> using the same
    /// <c>double → int</c> rounding <see cref="ClampIntoMonitor"/> and
    /// <c>Behaviors.PanelGeometryWriter</c> already use, so a panel's persisted geometry never depends on
    /// which of the three wrote it last.
    /// </summary>
    /// <remarks>
    /// m-1 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2, code-review): all three writers
    /// now round via <see cref="Math.Round(double)"/> rather than a truncating <c>(int)</c> cast — a plain
    /// cast truncates TOWARD ZERO (asymmetric: <c>-0.6</c> becomes <c>0</c>, not <c>-1</c>), unlike
    /// <see cref="Models.LassoPanelGeometry"/>'s own <c>Math.Round</c>-based conversion (F-7), which this
    /// method previously disagreed with for negative DIP coordinates (a monitor left of/above the primary).
    /// </remarks>
    private static void WriteModelGeometry(ChildWindowModel model, Rect bounds)
    {
        model.Position = new Point((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
        model.Size = new System.Drawing.Size((int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
    }

    /// <summary>
    /// Takes <paramref name="paths"/> out of one live panel: out of its ViewModel (so the items vanish
    /// from the UI) and then out of its model via <see cref="ChildWindow.SyncToModel"/>. Folder Portal
    /// panels are left alone — they mirror a live folder and never hold managed items.
    /// </summary>
    private static void RemoveItemsFromPanel(ChildWindow window, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;

        if (window.DataContext is ChildWindowViewModel { IsPortal: false } viewModel)
        {
            foreach (var path in paths) viewModel.RemoveFileByPath(path);
        }

        window.SyncToModel();
    }

    /// <summary>
    /// Every live panel's rectangle — <see cref="CollectOtherPanelBounds"/> with no panel excluded
    /// (a not-yet-created panel has no id in <see cref="_childWindows"/> to exclude).
    /// </summary>
    private List<Rect> CollectAllPanelBounds()
    {
        var bounds = new List<Rect>();
        CollectOtherPanelBounds(excludingId: null, bounds);
        return bounds;
    }

    /// <summary>
    /// Adds every app-managed path <paramref name="model"/> directly references, as-is, to
    /// <paramref name="claimed"/> — the shared "claimed" rule <see cref="GetManagedPathsClaimedByOtherPanels"/>
    /// and <see cref="ResolveStalePanelPaths"/> both apply. A Folder Portal panel never persists/consults
    /// <see cref="ChildWindowModel.FilePaths"/> (see <see cref="ResolveStalePanelPaths"/>'s remarks), so
    /// nothing of theirs can ever be "claimed".
    /// </summary>
    /// <param name="model">The panel whose <see cref="ChildWindowModel.FilePaths"/> are inspected.</param>
    /// <param name="claimed">The case-insensitive set to add every managed path to.</param>
    private void CollectManagedPaths(ChildWindowModel model, HashSet<string> claimed)
    {
        if (!string.IsNullOrWhiteSpace(model.SourceFolderPath)) return;

        foreach (var path in model.FilePaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && _itemStorage.IsManaged(path))
                claimed.Add(path);
        }
    }

    /// <summary>
    /// Seeds a brand-new install with exactly one empty panel (README.md's "On first run it creates one
    /// empty panel" — Install step 3, both language sections), so a user who just installed the app sees
    /// something rather than an entirely blank desktop. Only ever fires on a genuine first run — see
    /// <see cref="ShouldCreateFirstRunPanel"/> — never when the user has simply deleted every panel
    /// (that document already existed and loaded as
    /// <see cref="StateLoadStatus.Loaded"/> with an empty <c>panels</c> array), and never on the
    /// read-only <see cref="StateLoadStatus.NewerSchema"/> rung or after a quarantine/backup-recovery
    /// load (<see cref="StateLoadStatus.Quarantined"/>/<see cref="StateLoadStatus.RecoveredFromBackup"/>),
    /// where the real panels are either unreadable by this build or already being recovered — inventing a
    /// new one on top would be actively misleading.
    /// </summary>
    /// <remarks>
    /// Goes through the exact same creation path as the "Add panel" toolbar/tray action
    /// (<c>MainWindowViewModel.AddPanel</c>): <see cref="PanelDefaults.CreateNew"/> for the localized
    /// default title/page/appearance defaults, <see cref="PanelDefaults.ReconcileGridAlignedSpans"/> for
    /// a self-consistent size, then <see cref="PlaceNewPanelOnFreeSlot"/> for a snapped, non-overlapping
    /// position on the primary monitor's work area (there are no other panels yet, so this always finds
    /// the top-left-most slot) — no bespoke geometry or persistence logic. The resulting panel is
    /// persisted through the ordinary debounced <see cref="SaveWindowsState"/> path, exactly like any
    /// other structural change made during <see cref="LoadWindowsState"/> (e.g.
    /// <see cref="ResolveMonitorPlacement"/>'s own grid-size normalization save above it) — the debounce
    /// timer only fires once the dispatcher starts pumping, by which point
    /// <see cref="IApplicationStateService.ConsumersReady"/> is already <see langword="true"/>
    /// (<c>App.StartApp</c> calls <c>MarkConsumersReady()</c> synchronously, still inside the same
    /// Startup handler that called <see cref="LoadWindowsState"/>), so nothing extra is needed to make
    /// this land on disk.
    /// </remarks>
    private void CreateFirstRunPanelIfApplicable()
    {
        if (!ShouldCreateFirstRunPanel(_applicationStateService.Panels.Count, _applicationStateService.LastLoadStatus))
            return;

        var model = PanelDefaults.CreateNew(_appSettings, PageManager.CurrentPage);
        PanelDefaults.ReconcileGridAlignedSpans(model);
        PlaceNewPanelOnFreeSlot(model);
        CreateChildWindow(model);

        // Structural change, but not urgent enough to warrant a synchronous flush during startup — same
        // debounced path every other load-time structural adjustment in this class already uses.
        SaveWindowsState();
    }

    /// <summary>
    /// Builds, registers and (when on the active page) shows a panel window for <paramref name="model"/>
    /// — everything <see cref="CreateChildWindow"/> does except raising <see cref="OnChildWindowAdded"/>,
    /// which a shell-restart rebuild (<see cref="RebuildDestroyedPanel"/>) must not raise: the panel is
    /// not new, only its native window is.
    /// </summary>
    private ChildWindow CreateChildWindowCore(ChildWindowModel model)
    {
        // Assign the current page if the model does not already have a non-default page.
        if (string.IsNullOrWhiteSpace(model.PageId))
            model.PageId = "default";

        ResolveMonitorPlacement(model);

        var window = _serviceProvider.GetRequiredService<ChildWindow>();
        window.Initialize(model);

        // ADR-034: lets shell-restart recovery tell "this panel's HWND died with Explorer" from "it
        // was never created" — must be attached before Show() so SourceInitialized is observed.
        window.AttachShellRecoveryTracking();

        // ADR-034 amendment 2026-10-09 (ADR-audit WP-D): keeps the panel in the desktop band while it is a
        // top-level window (Windows 11 24H2+, no WorkerW) — a no-op for WorkerW-embedded panels. Also before
        // Show(), so the very first show is already filtered.
        window.AttachDesktopZOrderKeeper();

        _childWindows[model.Id] = (window, model);

        // Only show the panel when its page is the active page.
        var isOnActivePage = model.PageId == PageManager.CurrentPage;
        if (model.IsVisible && isOnActivePage)
        {
            window.Show();
        }

        return window;
    }

    /// <summary>
    /// Rebuilds a panel whose native window was destroyed by the shell (ADR-034 — a <c>WorkerW</c>
    /// child dies together with its dead Explorer parent). The model is the single source of truth, so
    /// the panel comes back exactly as it was: same model instance, same position/size/page/roll-up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT <see cref="RemoveChildWindow(Guid, bool, bool)"/>: that path treats the panel as
    /// deleted (raises <see cref="OnChildWindowRemoved"/>, may recycle managed items, writes state). A
    /// window destroyed from outside is not a user deletion, so none of that may happen here.
    /// </para>
    /// <para>
    /// The dictionary entry is never removed first: <see cref="CreateChildWindowCore"/> overwrites it
    /// only once the new window exists, so if the rebuild throws the model stays registered — it keeps
    /// being persisted and the next recovery attempt tries again — instead of silently dropping the
    /// panel from the next save ("all my panels are gone").
    /// </para>
    /// </remarks>
    private ChildWindow RebuildDestroyedPanel((ChildWindow Window, ChildWindowModel Model) entry)
    {
        try
        {
            // Releases what WPF's own close path would have (model/VM subscriptions, VM disposal) and
            // syncs the live file list into the model first. Idempotent across attempts.
            entry.Window.TearDownAfterExternalDestroy();
        }
        catch (Exception ex)
        {
            Log.Error(
                "WindowManager",
                "ShellRecoveryTearDownFailed",
                ex,
                new KeyValuePair<string, object?>("panelId", entry.Model.Id));
        }

        return CreateChildWindowCore(entry.Model);
    }

    // MainWindow is a singleton in DI — always returns the same instance
    private MainWindow GetOrCreateMainWindow()
        => _serviceProvider.GetRequiredService<MainWindow>();

    /// <summary>
    /// The "stored device id, else the monitor under the current position, else primary" monitor lookup
    /// shared by <see cref="ClonePanelToPage"/> (via <see cref="PanelCloneGeometry.Offset"/>) and
    /// <see cref="NormalizePanelsToGrid"/> (L-6 fix, code review — the latter used to keep its own inline
    /// copy of this exact same logic). <see cref="ResolveMonitorPlacement"/> keeps its own separate copy:
    /// unlike these two read-only lookups, it also WRITES <see cref="ChildWindowModel.MonitorDeviceId"/> as
    /// part of resolving where a panel restores to, which the callers of this method must not be coupled to.
    /// </summary>
    /// <param name="model">The panel whose monitor is resolved.</param>
    private MonitorInfo ResolveMonitorForPosition(ChildWindowModel model)
    {
        var target = !string.IsNullOrEmpty(model.MonitorDeviceId)
            ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
            : null;

        return target
            ?? _monitorService.FindMonitorAt(new System.Windows.Point(model.Position.X, model.Position.Y))
            ?? _monitorService.GetPrimaryMonitor();
    }

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
    /// <remarks>
    /// <b>ADR-011 Amendment (2026-09-24).</b> D3-4's original guarantee ("loading must not move a
    /// LegacyFreeform panel by a single DIP") is DELIBERATELY weakened here, by explicit user request, when
    /// <c>IAppSettings.GridSize &gt; 1</c>: after the untouched <see cref="ClampIntoMonitor"/> call below, a
    /// panel whose persisted bounds have drifted off the CURRENT <c>GridSize</c> lattice (e.g. it was placed
    /// while <c>GridSize=32</c> and the setting was later changed to <c>48</c>) is re-aligned onto that
    /// lattice via <see cref="NormalizeLegacyFreeformToGridSize"/> — see that method's remarks. At the
    /// project's <c>GridSize</c> default (<c>1</c>, "no grid") this normalization is a pure no-op and D3-4
    /// holds exactly as before; it only ever moves a panel when the user has an active, non-default grid
    /// AND the panel is genuinely off it.
    /// </remarks>
    private void ResolveMonitorPlacement(ChildWindowModel model)
    {
        var pos = new System.Windows.Point(model.Position.X, model.Position.Y);

        var target = !string.IsNullOrEmpty(model.MonitorDeviceId)
            ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
            : null;

        // Stored monitor gone (or never set) → resolve by current position, else primary.
        target ??= _monitorService.FindMonitorAt(pos) ?? _monitorService.GetPrimaryMonitor();

        model.MonitorDeviceId = target.DeviceName;

        // P03-W1 (docs/PLANS/workspace-grid-03-04-integration.md §2.4): a GridAligned panel's position
        // and size are re-derived from its persisted spans against the CURRENT origin of the monitor it
        // was already assigned to above — it is never relocated to a different monitor here, and the
        // legacy ClampIntoMonitor path below is left completely untouched for every other panel (D3-4:
        // loading must not move a LegacyFreeform panel by a single DIP) EXCEPT for the ADR-011 Amendment
        // grid-size normalization documented on this method — see the remarks above.
        if (ResolveGridAlignedBounds(model, target) is { } bounds)
        {
            var normalized = NormalizeGridAlignedToGridSize(model, bounds, target, _appSettings.GridSize);
            WriteModelGeometry(model, normalized);
            if (normalized != bounds) SaveWindowsState();
            return;
        }

        ClampIntoMonitor(model, target);

        if (NormalizeLegacyFreeformToGridSize(model, target, _appSettings.GridSize))
            SaveWindowsState();
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

        // M-3 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2.4): WM_SETTINGCHANGE raises
        // this for events that never actually move a single panel (a theme or locale change, for
        // instance — MonitorService.cs's own remarks). Track whether ANYTHING below actually changed so
        // an all-quiet firing persists nothing at all, instead of unconditionally requesting a save (and,
        // for a GridAligned panel, unconditionally re-writing Width/Height through
        // ApplyWorkspaceGridGeometry) every single time.
        var anyChanged = false;
        var scale = _desktopScale.Current;

        foreach (var (window, model) in _childWindows.Values)
        {
            var monitor = !string.IsNullOrEmpty(model.MonitorDeviceId)
                ? _monitorService.FindByDeviceName(model.MonitorDeviceId)
                : null;

            if (monitor is null)
            {
                // Stored monitor is gone → fall back to primary. A genuine persisted-state change
                // (MonitorDeviceId), independent of whether the geometry below also moves.
                model.MonitorDeviceId = primary.DeviceName;
                monitor = primary;
                anyChanged = true;
            }

            // P03-W3: a GridAligned panel stays on the SAME monitor and in the SAME logical slot — its
            // bounds are re-derived from its spans against that monitor's new origin/work area (a taskbar
            // appearing, a resolution change), which is the whole architectural point of storing spans
            // rather than absolute pixels (ADR-028 «Origin для системы B»). LegacyFreeform keeps the
            // untouched ClampIntoMonitor path below.
            if (ResolveGridAlignedBounds(model, monitor) is { } bounds)
            {
                // M-3: skip the write (and the resulting model-dirty/save) entirely when the panel is
                // already, within a sub-pixel-rounding tolerance, exactly where ApplyWorkspaceGridGeometry
                // would put it — the overwhelmingly common case for a WM_SETTINGCHANGE that changed
                // nothing about this panel's monitor. release-hardening F (ADR-037): read through the
                // adapter — desktop-DIP, the same space `bounds` is already in.
                var current = PanelWindowCoordinates.ToDesktopBounds(window, new Rect(window.Left, window.Top, window.Width, window.Height), scale);
                if (GridAlignedBoundsUnchanged(current, bounds, model.IsRolledUp)) continue;

                // Through the panel's own single geometry writer, so Window.Left/Top/Width/Height and the
                // model are written as one guarded act and the synchronous OnLocationChanged/
                // OnRenderSizeChanged callbacks do not write them a second time (plan §1.5).
                window.ApplyWorkspaceGridGeometry(bounds, span: null, monitorDeviceId: null);
                anyChanged = true;
                continue;
            }

            // Re-clamp against the (possibly resized) current work area. ClampIntoMonitor early-outs
            // when the panel is already fully inside, so this is a cheap no-op in the common case.
            var before = model.Position;
            ClampIntoMonitor(model, monitor);
            if (model.Position != before) anyChanged = true;

            // ADR-011 Amendment (2026-09-24): also re-aligns the panel onto the CURRENT GridSize lattice
            // if it drifted off it (see ResolveMonitorPlacement's remarks for the scenario). model.Size
            // always holds the panel's EXPANDED extent regardless of roll-up state (ChildWindow.
            // OnRenderSizeChanged's own documented contract), so it is always safe to normalize — but the
            // live window's OWN Width/Height must stay untouched while rolled up (its live Height is the
            // COLLAPSED header, not model.Size.Height; writing model.Size.Height into it here would
            // visibly, wrongly expand a rolled-up panel).
            if (NormalizeLegacyFreeformToGridSize(model, monitor, _appSettings.GridSize))
                anyChanged = true;

            // release-hardening F (ADR-037): through the geometry writer/adapter — matches
            // NormalizePanelsToGrid's identical fix; ApplyWorkspaceGridGeometry already skips the
            // Width/Height write while rolled up, same as this method's own pre-F guard, and re-writing
            // an unchanged size is idempotent.
            window.ApplyWorkspaceGridGeometry(
                new Rect(model.Position.X, model.Position.Y, model.Size.Width, model.Size.Height),
                span: null,
                monitorDeviceId: null);
        }

        if (anyChanged) SaveWindowsState();
    }

    private void ShowWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }
}

/// <summary>
/// The outcome of <see cref="WindowManager.BuildClonedFilePaths"/> (ADR-045): either every item copied
/// successfully (<see cref="Success"/>, with <see cref="FilePaths"/> ready to hand to
/// <c>PanelCloneBuilder.Build</c>), or a copy failed and every managed copy already made for this attempt
/// was rolled back (<see cref="FailureOutcome"/>).
/// </summary>
/// <remarks>
/// <c>default(PanelCloneItemsPlan)</c> reports success with an empty, non-<see langword="null"/>
/// <see cref="FilePaths"/> — a loose test double returning the default value never causes a
/// <see cref="NullReferenceException"/> in a caller that only checks <see cref="Success"/> first.
/// </remarks>
internal readonly record struct PanelCloneItemsPlan
{
    private PanelCloneItemsPlan(bool success, List<string>? filePaths, int skippedMissingItems, IReadOnlyList<string>? createdCopies, PanelCloneOutcome failureOutcome)
    {
        Success = success;
        FilePaths = filePaths ?? [];
        SkippedMissingItems = skippedMissingItems;
        CreatedCopies = createdCopies ?? [];
        FailureOutcome = failureOutcome;
    }

    /// <summary>Gets a value indicating whether every item copied successfully.</summary>
    public bool Success { get; }

    /// <summary>The clone's own file path list, ready for <c>PanelCloneBuilder.Build</c>. Empty when <see cref="Success"/> is <see langword="false"/>.</summary>
    public List<string> FilePaths { get; }

    /// <summary>How many managed items were left out because their file was already gone. Always <c>0</c> when <see cref="Success"/> is <see langword="false"/>.</summary>
    public int SkippedMissingItems { get; }

    /// <summary>
    /// H-1 fix (code review, panel clone to page): every managed copy <c>BuildClonedFilePaths</c> itself
    /// created for THIS attempt (a subset of <see cref="FilePaths"/> — excludes non-managed paths carried
    /// over as-is) — so <see cref="WindowManager.ClonePanelToPage"/> can still roll them back via
    /// <see cref="IPanelItemStorageService.DiscardCopies"/> if something AFTER this plan was built (title
    /// resolution already happened earlier, geometry, <c>CreateChildWindow</c>) throws. Always empty when
    /// <see cref="Success"/> is <see langword="false"/> — <c>BuildClonedFilePaths</c> already discards its
    /// own copies itself before reporting a failure.
    /// </summary>
    public IReadOnlyList<string> CreatedCopies { get; }

    /// <summary>Which <see cref="PanelCloneOutcome"/> <see cref="WindowManager.ClonePanelToPage"/> should fail with. Meaningless when <see cref="Success"/> is <see langword="true"/>.</summary>
    public PanelCloneOutcome FailureOutcome { get; }

    /// <summary>Creates a successful plan.</summary>
    /// <param name="filePaths">The clone's own file path list.</param>
    /// <param name="skippedMissingItems">See <see cref="SkippedMissingItems"/>.</param>
    /// <param name="createdCopies">See <see cref="CreatedCopies"/>.</param>
    public static PanelCloneItemsPlan Ok(List<string> filePaths, int skippedMissingItems, IReadOnlyList<string> createdCopies)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(createdCopies);
        return new PanelCloneItemsPlan(true, filePaths, skippedMissingItems, createdCopies, default);
    }

    /// <summary>Creates a failed plan.</summary>
    /// <param name="outcome">See <see cref="FailureOutcome"/>.</param>
    public static PanelCloneItemsPlan Failed(PanelCloneOutcome outcome) => new(false, null, 0, null, outcome);
}
