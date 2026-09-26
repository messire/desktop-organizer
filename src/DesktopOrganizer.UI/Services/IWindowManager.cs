using System.Windows;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages the application's windows.
/// </summary>
public interface IWindowManager
{
    /// <summary>
    /// Triggered when the visibility of the main window changes.
    /// </summary>
    event EventHandler<bool> OnMainWindowVisibilityChanged;

    /// <summary>
    /// Triggered when a child window is added.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowAdded;

    /// <summary>
    /// Triggered when a child window is removed.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowRemoved;

    /// <summary>
    /// Toggles the main application window: shows/activates it if currently hidden, hides it if
    /// currently visible. Used by the tray icon's double-click gesture.
    /// </summary>
    void StartMainWindow();

    /// <summary>
    /// Shows and activates the main application window unconditionally — never hides it. If the
    /// window is already visible (e.g. minimized or behind other windows), it is restored to its
    /// normal window state and brought to the foreground. Used by the tray icon context menu's
    /// "Settings" item.
    /// </summary>
    void ShowMainWindow();

    /// <summary>
    /// Creates a new child window.
    /// </summary>
    /// <param name="model">The model for the child window.</param>
    /// <returns>The created child window.</returns>
    ChildWindow CreateChildWindow(ChildWindowModel model);

    /// <summary>
    /// Removes a child window without releasing any app-managed item copies it referenced.
    /// Equivalent to <see cref="RemoveChildWindow(Guid, bool)"/> with <c>releaseManagedItems: false</c>
    /// — the safe default for callers that are merely tearing the panel down to recreate it
    /// (e.g. layout snapshot restore), not permanently deleting user data.
    /// </summary>
    /// <param name="windowId">The ID of the window to remove.</param>
    void RemoveChildWindow(Guid windowId);

    /// <summary>
    /// Removes a child window, optionally releasing any app-managed item copies it referenced.
    /// </summary>
    /// <param name="windowId">The ID of the window to remove.</param>
    /// <param name="releaseManagedItems">
    /// <see langword="true"/> if the panel is being permanently deleted by the user (or its owning
    /// page is being permanently deleted) and its app-managed item copies should be released;
    /// <see langword="false"/> if the panel is only being torn down to be immediately recreated
    /// (e.g. layout snapshot restore) and its managed item copies must be preserved.
    /// </param>
    void RemoveChildWindow(Guid windowId, bool releaseManagedItems);

    /// <summary>
    /// Removes a child window, optionally releasing any app-managed item copies it referenced, and
    /// optionally deferring the resulting persistence write.
    /// </summary>
    /// <param name="windowId">The ID of the window to remove.</param>
    /// <param name="releaseManagedItems">See <see cref="RemoveChildWindow(Guid, bool)"/>.</param>
    /// <param name="deferSave">
    /// <see langword="false"/> to persist synchronously right away, matching
    /// <see cref="RemoveChildWindow(Guid, bool)"/>. <see langword="true"/> for bulk-removal loops (e.g.
    /// layout snapshot restore, page deletion), where the request is only debounced via
    /// <see cref="SaveWindowsState"/> — the caller MUST follow the loop with a single
    /// <see cref="SaveWindowsStateNow"/> call once every removal is done, or the change can be lost to
    /// a debounce window that never elapses.
    /// </param>
    void RemoveChildWindow(Guid windowId, bool releaseManagedItems, bool deferSave);

    /// <summary>
    /// Gets all child windows.
    /// </summary>
    /// <returns>A collection of all child windows.</returns>
    IEnumerable<ChildWindowModel> GetAllChildWindows();

    /// <summary>
    /// Requests that the state of all windows be persisted. For "frequent" call sites (move, resize,
    /// roll-up, view/filter changes, file removal) this only <em>schedules</em> a debounced write —
    /// see <see cref="SaveWindowsStateNow"/> for the synchronous variant required by structural
    /// changes and application shutdown. Never throws: persistence failures are logged, not
    /// propagated, so an ordinary user action can never crash the app because of a disk/config error.
    /// </summary>
    void SaveWindowsState();

    /// <summary>
    /// Persists the state of all windows synchronously, right now, cancelling any pending debounced
    /// write from <see cref="SaveWindowsState"/> first. Must be used by every "structural" call site
    /// (panel create/delete, layout snapshot restore, application exit) so the corresponding change is
    /// never lost to a debounce window that never gets to elapse (e.g. the process exits first). Like
    /// <see cref="SaveWindowsState"/>, never throws — persistence failures are logged only.
    /// </summary>
    void SaveWindowsStateNow();

    /// <summary>
    /// Syncs every live window into its model without writing anything to disk. Isolated per-panel
    /// (see <c>WindowManager.SyncEachIsolated</c>) so one panel failing to sync never blocks the rest.
    /// Callers that need the in-memory models to reflect the latest on-screen/file state right before
    /// reading them — without paying for (or waiting on) a full <see cref="SaveWindowsStateNow"/> write
    /// — should call this first. See <c>docs/PLANS/phase-7-performance-hardening.md</c> (Block C, M3):
    /// <c>LayoutSnapshotService.Capture</c> must not serialize stale <c>FilePaths</c> just because the
    /// regular save is debounced up to 500ms.
    /// </summary>
    void SyncAllWindowsToModels();

    /// <summary>
    /// Loads the state of all windows. A saved panel that fails to (de)serialize or construct is
    /// logged and skipped rather than aborting the whole load — see
    /// <c>docs/PLANS/phase-7-performance-hardening.md</c> (Block C, per-item isolation).
    /// </summary>
    void LoadWindowsState();

    /// <summary>
    /// Applies the <see cref="Models.ChildWindowModel.IsVisible"/> flag of <paramref name="model"/>
    /// to the actual <see cref="Views.ChildWindow"/> (shows or hides it).
    /// </summary>
    /// <param name="model">The panel model whose <see cref="Models.ChildWindowModel.IsVisible"/> flag is applied.</param>
    void ApplyPanelVisibility(Models.ChildWindowModel model);

    /// <summary>
    /// Fills <paramref name="buffer"/> with the current on-screen rectangles of every live, visible
    /// child panel except <paramref name="excludingId"/> — the "obstacles" a drag gesture must not be
    /// allowed to overlap (see <c>docs/PLANS/panel-anti-overlap.md</c> §3.3).
    /// </summary>
    /// <remarks>
    /// Deliberately reads live <see cref="Views.ChildWindow"/> geometry (<c>Window.Left</c>/<c>Top</c>/
    /// <c>ActualWidth</c>/<c>ActualHeight</c>), never <see cref="Models.ChildWindowModel.Position"/>/
    /// <see cref="Models.ChildWindowModel.Size"/>: the model's <c>Size</c> intentionally keeps a rolled-up
    /// panel's <em>expanded</em> height (see <c>ChildWindow.OnRenderSizeChanged</c>), which would make a
    /// collapsed panel block far more space than it visually occupies. A hidden panel (different page,
    /// or otherwise not currently shown) contributes no obstacle — it blocks nothing the user can see.
    /// <para>
    /// The buffer is owned by the caller and reused across gestures; this method always starts by
    /// calling <see cref="ICollection{T}.Clear"/> on it and never allocates a new list itself. Intended
    /// to be called once per drag gesture (e.g. on mouse-down), not on every mouse-move.
    /// </para>
    /// </remarks>
    /// <param name="excludingId">The <see cref="Models.ChildWindowModel.Id"/> of the panel currently
    /// being dragged — excluded from its own obstacle set — or <see langword="null"/> to exclude nothing
    /// (n-1 fix-cycle 1, docs/PLANS/workspace-grid-03-04-integration.md §2, code-review: replaces the
    /// previous <c>Guid.Empty</c>-as-sentinel convention used when placing a NOT-YET-created panel, which
    /// has no id of its own to pass).</param>
    /// <param name="buffer">The list to clear and repopulate with obstacle rectangles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    void CollectOtherPanelBounds(Guid? excludingId, IList<Rect> buffer);

    /// <summary>
    /// Runs the explicit "Align to workspace grid" command (ADR-028 «Явная команда», P03-W4) over a
    /// single panel when <paramref name="panelId"/> is supplied, or over every panel on the CURRENT page
    /// when it is <see langword="null"/>: each panel switches to <see cref="Models.PanelGeometryMode.GridAligned"/>,
    /// its position quantises to its monitor's work-area origin and its size rounds UP to the nearest
    /// whole span, with the same anti-overlap wall every drag frame uses applied last — so a panel may
    /// stop short of its ideal slot rather than displace a neighbour.
    /// </summary>
    /// <remarks>
    /// Panels are processed top-to-bottom, then left-to-right, and each already-processed panel becomes
    /// an obstacle for the next, so the outcome is deterministic for a given layout. Persists once at the
    /// end (not per panel). There is deliberately no bulk inverse: un-aligning is per-panel, through the
    /// panel's own context menu (OQ-3 = B).
    /// <para>
    /// <b>M-2 fix-cycle 1 (docs/PLANS/workspace-grid-03-04-integration.md §2.5).</b> The <see langword="null"/>
    /// ("align all") form only ever targets the CURRENT page. Every OTHER page's panels are always hidden
    /// (<c>CollectOtherPanelBounds</c> skips non-<c>IsVisible</c> panels), so a batch spanning several
    /// inactive pages saw no obstacles at all between panels on the SAME inactive page and let them
    /// collide into a single slot — scoping to the current page is what keeps "who is an obstacle for
    /// whom" honest.
    /// </para>
    /// </remarks>
    /// <param name="panelId">The single panel to align, or <see langword="null"/> for every panel on the current page.</param>
    void AlignPanelsToWorkspaceGrid(Guid? panelId);

    /// <summary>
    /// Assigns a brand-new <see cref="Models.PanelGeometryMode.GridAligned"/> panel the first free slot
    /// on its target monitor's workspace grid — left-to-right, then top-to-bottom, keeping the standard
    /// gap from every existing panel (P03-L3, baseline M8: new panels used to be created at <c>(0,0)</c>
    /// and stack exactly on top of each other). Call BEFORE <see cref="CreateChildWindow"/>.
    /// </summary>
    /// <remarks>
    /// A no-op for <see cref="Models.PanelGeometryMode.LegacyFreeform"/> panels (their creation behavior
    /// is unchanged by ADR-028) and when the work area has no free slot left — in which case the caller's
    /// own position stands and the usual placement/clamping applies.
    /// </remarks>
    /// <param name="model">The not-yet-created panel, with its size already assigned.</param>
    void PlaceNewPanelOnFreeSlot(Models.ChildWindowModel model);

    /// <summary>
    /// ADR-011 Amendment (2026-09-24): re-aligns every open panel's <c>Position</c>/<c>Size</c> onto the
    /// CURRENT <c>IAppSettings.GridSize</c> lattice, in place, right after the setting itself changes —
    /// so a panel positioned/sized under a PREVIOUS <c>GridSize</c> (e.g. placed at <c>32</c>, then the
    /// setting changed to <c>48</c>) does not keep "living on a different grid" than panels created
    /// after the change and than every subsequent drag (which snaps in the NEW <c>GridSize</c>'s steps).
    /// A no-op when <c>GridSize &lt;= 1</c> (no grid) or there are no open panels. Persists once at the
    /// end (not per panel), only if at least one panel actually changed.
    /// </summary>
    /// <remarks>
    /// A <see cref="Models.PanelGeometryMode.GridAligned"/> panel is only touched when the current
    /// <c>GridSize</c> happens to be a whole multiple of <c>WorkspaceGridMetrics.UnitSize</c> — see
    /// <c>WindowManager.NormalizeGridAlignedToGridSize</c>'s remarks for why an incommensurate
    /// <c>GridSize</c> is deliberately left alone for that mode.
    /// </remarks>
    void NormalizePanelsToGrid();

    /// <summary>
    /// Moves <paramref name="panel"/> to a different page (Panels section's "Move to page ▾",
    /// docs/prompts/settings-window-redesign.md D-10): sets <see cref="Models.ChildWindowModel.PageId"/>,
    /// applies the resulting visibility (visible only when <paramref name="pageId"/> is the current
    /// page — mirrors <c>IPageManager.SwitchTo</c>'s own show/hide rule), and persists synchronously
    /// (a structural change, like every other panel create/delete/move call site). A no-op — the panel is
    /// left exactly where it was — for a blank/whitespace-only <paramref name="pageId"/>, for a
    /// <paramref name="pageId"/> equal to <paramref name="panel"/>'s current page, or for a
    /// <paramref name="pageId"/> that isn't one of <c>IPageManager.Pages</c>'s existing pages (fix-cycle
    /// finding 10: a stale/typo'd destination must never silently strand the panel on an unlisted page).
    /// </summary>
    /// <param name="panel">The panel to move.</param>
    /// <param name="pageId">The destination page identifier.</param>
    void MovePanelToPage(Models.ChildWindowModel panel, string pageId);
}
