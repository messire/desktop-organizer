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
    /// Triggered when a child window is added.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowAdded;

    /// <summary>
    /// Triggered when a child window is removed.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowRemoved;

    /// <summary>
    /// M-D fix (code review, panel clone to page, backlog 1.x wave 3 review): triggered whenever
    /// <see cref="MovePanelToPage"/> actually reassigns a panel's page — regardless of which UI surface
    /// invoked it (a panel's own header context menu, via <c>ChildWindowViewModel.MovePanelToPageCommand</c>,
    /// never goes through <c>MainWindowViewModel</c> at all). Lets any subscriber whose own panel list is
    /// keyed by page (<c>MainWindowViewModel.PanelsOnCurrentPage</c>) refresh itself without depending on
    /// some unrelated event happening to fire first.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowPageChanged;

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
    /// Removes a child window, optionally moving any app-managed item copies it referenced to the
    /// Recycle Bin (ADR-035).
    /// </summary>
    /// <param name="windowId">The ID of the window to remove.</param>
    /// <param name="releaseManagedItems">
    /// <see langword="true"/> if the panel is being permanently deleted by the user (or its owning
    /// page is being permanently deleted) and its app-managed item copies should be moved to the
    /// Recycle Bin; <see langword="false"/> if the panel is only being torn down to be immediately
    /// recreated (e.g. layout snapshot restore) and its managed item copies must be preserved.
    /// </param>
    /// <returns>
    /// <see cref="PanelRemovalResult.Removed"/>, or <see cref="PanelRemovalResult.Blocked"/> when some
    /// managed items could not be recycled — the panel is then kept (only the items that did reach the
    /// Recycle Bin are taken out of it) and the caller is expected to tell the user.
    /// </returns>
    PanelRemovalResult RemoveChildWindow(Guid windowId, bool releaseManagedItems);

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
    /// <returns>
    /// <see cref="PanelRemovalResult.Removed"/>, or <see cref="PanelRemovalResult.Blocked"/> when the panel
    /// was kept because some managed items could not be recycled (details as for the two-argument overload).
    /// </returns>
    PanelRemovalResult RemoveChildWindow(Guid windowId, bool releaseManagedItems, bool deferSave);

    /// <summary>
    /// Takes <paramref name="paths"/> out of every live panel that lists them (ViewModel and model) and
    /// requests a debounced save. Used after a partially successful Recycle Bin operation (ADR-035) —
    /// e.g. a blocked page deletion — so items that already are in the Recycle Bin don't linger as
    /// broken entries in the panels that are being kept.
    /// </summary>
    /// <param name="paths">Item paths to remove (case-insensitive).</param>
    void RemoveItemsFromPanels(IReadOnlyCollection<string> paths);

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

    /// <summary>
    /// Creates a fully independent copy of <paramref name="source"/> on <paramref name="pageId"/> — which
    /// may equal <paramref name="source"/>'s own page (Panels section's "Copy to page ▾", ADR-045 «Panel
    /// clone to page»). Every saved field is duplicated (through <c>PanelStateMapper</c>, the same
    /// serialization contract every other panel uses), so the clone and the source share nothing mutable:
    /// changing or deleting either afterwards never affects the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transactional: either the clone is created complete, or nothing happens at all. Every app-managed
    /// item the source references is duplicated into its own brand-new per-item storage directory
    /// (<c>IPanelItemStorageService.CopyManaged</c>) — never a shared managed path between the two panels.
    /// A managed item whose file is already gone from disk is left out of the clone entirely (counted in
    /// <see cref="PanelCloneResult.SkippedMissingItems"/>) rather than copying a reference to
    /// nothing that two panels would then both "own". A non-managed reference (an external file, or a
    /// shortcut the user left where it was) is duplicated as the SAME path in both panels — it belongs to
    /// the user, not this app, so there is nothing to copy. Any copy failure discards every managed copy
    /// already made for this attempt (<c>IPanelItemStorageService.DiscardCopies</c>) before returning — the
    /// source is never touched.
    /// </para>
    /// <para>
    /// A Folder Portal clone (<see cref="Models.ChildWindowModel.SourceFolderPath"/> set) copies only the
    /// folder BINDING, exactly like every other appearance/behavior field — never the folder's contents; the
    /// clone gets its own live <c>FileSystemWatcher</c> on the same folder once created.
    /// </para>
    /// <para>
    /// The clone's title follows Explorer's own collision convention against every panel already on
    /// <paramref name="pageId"/> ("Notes" → "Notes — copy" → "Notes — copy (2)", localized). Its geometry is
    /// the source's own position/size verbatim when <paramref name="pageId"/> differs from the source's
    /// page; on the SAME page it is offset by one grid step and clamped into its monitor's work area instead,
    /// so it never sits exactly on top of the source.
    /// </para>
    /// </remarks>
    /// <param name="source">The panel to clone. Never mutated.</param>
    /// <param name="pageId">The clone's destination page — may be <paramref name="source"/>'s own current page.</param>
    /// <returns>What happened; see <see cref="PanelCloneOutcome"/>.</returns>
    PanelCloneResult ClonePanelToPage(Models.ChildWindowModel source, string pageId);

    /// <summary>
    /// M-1 fix (code review, panel clone to page): every app-managed path (see
    /// <c>IPanelItemStorageService.IsManaged</c>) currently listed, as-is, in ANY live panel's
    /// <see cref="Models.ChildWindowModel.FilePaths"/> OTHER than <paramref name="excludingPanelId"/> — the
    /// same "claimed" concept <c>LoadWindowsState</c>'s own up-front <c>ResolveStalePanelPaths</c> pass
    /// already applies once, at startup. Exposed so a live panel's ONGOING per-item stale-path self-heal
    /// (<c>ChildWindowViewModel.ResolveManagedReplacements</c>, which runs on every model load/reload —
    /// not just at startup) can stay claim-aware too: after <see cref="ClonePanelToPage"/> gives a second
    /// panel its own live copy of a managed item, a DIFFERENT (e.g. stale) panel's entry for the pre-clone
    /// original path must never be healed onto that copy — it belongs to the panel that was just cloned
    /// into, not to whichever other panel happens to still reference the same original path.
    /// </summary>
    /// <param name="excludingPanelId">The panel whose own <see cref="Models.ChildWindowModel.FilePaths"/> are excluded from the result.</param>
    /// <returns>Every OTHER live panel's directly-referenced managed paths (case-insensitive).</returns>
    IReadOnlySet<string> GetManagedPathsClaimedByOtherPanels(Guid excludingPanelId);

    /// <summary>
    /// One attempt at putting every panel back on the desktop after Explorer was restarted (ADR-034).
    /// A panel whose window survived is re-embedded into the current <c>WorkerW</c> (or, when there
    /// is none, re-styled and pushed back to the bottom of the Z-order as a top-level window); a panel
    /// whose window was destroyed together with the dead shell is rebuilt from its
    /// <see cref="Models.ChildWindowModel"/>, the single source of truth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent — the caller retries it within a bounded window while Explorer is still starting.
    /// Rebuilding a panel neither raises <see cref="OnChildWindowRemoved"/>/<see cref="OnChildWindowAdded"/>
    /// (the model, and therefore every view of it, is unchanged), nor releases managed items, nor
    /// writes state: recovery must never change what is on disk.
    /// </para>
    /// <para>Each panel is isolated in its own <c>try</c>/<c>catch</c>; never throws. UI thread only.</para>
    /// </remarks>
    /// <returns>Counts of what happened to the panels during this attempt.</returns>
    DesktopLayerRecoveryResult RecoverDesktopLayer();
}
