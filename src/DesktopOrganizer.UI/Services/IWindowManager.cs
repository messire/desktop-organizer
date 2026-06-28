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
    /// Triggered when a child window is modified.
    /// </summary>
    event EventHandler<ChildWindowModel> OnChildWindowModified;

    /// <summary>
    /// Shows or activates the main application window.
    /// </summary>
    void StartMainWindow();

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
    void ApplyPanelVisibility(Models.ChildWindowModel model);
}
